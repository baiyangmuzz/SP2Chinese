using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SP2Chinese
{
    /// <summary>
    /// 词库自动更新。
    ///
    /// 小更新（只改词库）：从 GitHub 拉 latest 的 dictionary_zh_cn.tsv，SHA256 校验通过后
    /// 交给主线程替换并热重载 —— 玩家不用重装、不用重启。
    /// 大更新（插件代码变了）：只在日志里提示，让玩家去群里/Releases 下完整包。
    ///
    /// 所有网络失败都静默降级到本地词库。这个类绝不能因为断网而影响游戏启动。
    /// </summary>
    internal static class DictionaryUpdater
    {
        private static readonly string[] ManifestUrls =
        {
            "https://raw.githubusercontent.com/baiyangmuzz/SP2Chinese/main/manifest.json",
            "https://fastly.jsdelivr.net/gh/baiyangmuzz/SP2Chinese@main/manifest.json",
            "https://cdn.jsdelivr.net/gh/baiyangmuzz/SP2Chinese@main/manifest.json",
        };

        /// <summary>
        /// 已下载并校验通过的词库。用 volatile 引用把「字节 + 构建号 + 期望哈希」作为一个
        /// 不可变整体交给主线程，后台线程不再落盘 .download —— 避免「校验内存字节、却从
        /// 可预测文件复制安装」的 TOCTOU 窗口，主线程安装前还能再复验一次哈希。
        /// 主线程消费后应置 null（见 Plugin.ApplyDownloadedDictionary）。
        /// </summary>
        internal sealed class Download
        {
            internal readonly byte[] Bytes;
            internal readonly string Build;
            internal readonly string Sha256;
            internal readonly string VersionId;
            internal Download(byte[] bytes, string build, string sha256, string versionId)
            {
                Bytes = bytes; Build = build; Sha256 = sha256; VersionId = versionId;
            }
        }

        internal static volatile Download Pending;

        internal static volatile string LastStatus;

        private static int _started;

        // 允许的最大下载体积（当前词典约 795 KB；给未来增长留足余量，同时挡住超大文件 DoS）。
        private const int MaxDictionaryBytes = 8 * 1024 * 1024;

        internal static void Kick(string localDictPath, string localVersion)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            Task.Run(() => Run(localDictPath, localVersion));
        }

        /// <summary>
        /// 网络类失败后由主线程调用，重置一次性闸门再试一次。成功拿到新词库后不再重试。
        /// 保持单生产者：只有 _started 被原子地清零，后台 Run 才会再跑一个。
        /// </summary>
        internal static void Retry(string localDictPath, string localVersion)
        {
            if (Pending != null) return;                 // 已有下载待消费，不必再检查
            if (Interlocked.CompareExchange(ref _started, 0, 1) != 1) return;
            LastStatus = null;
            Task.Run(() => Run(localDictPath, localVersion));
        }

        private static void Run(string localDictPath, string localVersion)
        {
            try
            {
                var json = FetchManifest();
                if (string.IsNullOrEmpty(json))
                {
                    LastStatus = "manifest 获取失败，使用本地词库";
                    return;
                }

                MiniJson manifest;
                try { manifest = MiniJson.Parse(json); }
                catch (Exception e)
                {
                    LastStatus = "manifest 解析失败：" + e.Message;
                    return;
                }
                if (manifest == null)
                {
                    LastStatus = "manifest 解析失败";
                    return;
                }

                // 远程插件比本地新才提示下载完整包，不动字典（避免新旧逻辑配新字典出新问题）。
                // 只比字符串不等会把“本地开发版比线上新”误报成有新版本。
                var remoteMod = Str(manifest, "modVersion");
                if (!string.IsNullOrEmpty(remoteMod) && IsNewer(remoteMod, localVersion))
                {
                    LastStatus = "有新版本插件 " + remoteMod + "（当前 " + localVersion +
                                 "），请到 QQ 群 1128992783 或 Releases 下载完整包";
                    return;
                }

                var remoteDictId = Str(manifest, "dictVersion");
                var localDictId = Sha256OfFile(localDictPath);
                if (localDictId != null && localDictId.Length > 12) localDictId = localDictId.Substring(0, 12);

                // 词库构建序号：本地不旧于线上就不下载。
                // 只比哈希会把"本地已修好、线上还是旧版"的情况误判成需要更新，反而覆盖掉修复。
                var remoteBuild = Long(manifest, "dictBuild");
                var localBuild = ReadLocalBuild(localDictPath);
                if (localBuild > 0)
                {
                    // 远程没写版本号（老 manifest）或版本不高于本地，都保留本地：
                    // 本地带版本号说明是比线上更新的构建，覆盖只会把修复打回去。
                    if (remoteBuild == 0 || remoteBuild <= localBuild)
                    {
                        LastStatus = "词库已是本地较新版本（build " + localBuild + "），不覆盖";
                        return;
                    }
                }

                if (string.IsNullOrEmpty(remoteDictId) || remoteDictId == localDictId)
                {
                    LastStatus = "词库已是最新（" + remoteDictId + "）";
                    return;
                }

                var url = DictUrl(manifest, "dictionary");
                if (string.IsNullOrEmpty(url))
                {
                    LastStatus = "manifest 里没有字典地址";
                    return;
                }
                // 下载地址完全由 manifest 指定：限制 scheme 与 host，避免被改成 http://
                // 或指向任意第三方主机（配合无签名 manifest 的供应链风险）。
                if (!IsAllowedDictionaryUrl(url))
                {
                    LastStatus = "字典地址不在允许列表，已拒绝下载";
                    return;
                }

                // 期望哈希必须在下载前就确定，且缺失时直接拒绝（fail-closed）。
                // 旧实现 `if (!string.IsNullOrEmpty(expect) && ...)` 在哈希缺失时会
                // 跳过校验，损坏或被篡改的下载会被直接接受。
                var expect = Str(manifest, "dictSha256");
                if (string.IsNullOrEmpty(expect))
                {
                    var dictMeta = Sub(manifest, "files");
                    var entry = Sub(dictMeta, "BepInEx/plugins/SP2Chinese/dictionary_zh_cn.tsv");
                    expect = Str(entry, "sha256");
                }
                if (string.IsNullOrEmpty(expect))
                {
                    LastStatus = "manifest 缺少字典哈希，拒绝安装未校验的词库";
                    return;
                }

                var body = FetchBytesLimited(url);
                if (body == null)
                {
                    var mirror = MirrorUrl(url);
                    if (mirror != null && IsAllowedDictionaryUrl(mirror)) body = FetchBytesLimited(mirror);
                }
                if (body == null)
                {
                    LastStatus = "词库下载失败或超过大小上限，继续用本地版本";
                    return;
                }

                var actual = Sha256OfBytes(body);
                if (!string.Equals(expect, actual, StringComparison.OrdinalIgnoreCase))
                {
                    LastStatus = "词库哈希不符（期望 " + Short(expect) + "，实际 " + Short(actual) + "），已丢弃";
                    return;
                }

                var build = remoteBuild > 0
                    ? remoteBuild.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : null;
                // 单个 volatile 引用整体发布：主线程拿到的永远是「已校验的这一份字节 +
                // 与之对应的构建号」，不存在两个字段先后写入的竞态。
                Pending = new Download(body, build, actual, remoteDictId);
                LastStatus = "已下载新词库 " + remoteDictId + "（" + (body.Length / 1024) + " KB），等待热重载";
            }
            catch (Exception e)
            {
                LastStatus = "更新检查异常：" + e.GetType().Name + "：" + e.Message;
            }
        }

        private static string Short(string s)
        {
            return string.IsNullOrEmpty(s) || s.Length <= 8 ? s : s.Substring(0, 8);
        }

        private static string DictUrl(MiniJson manifest, string key)
        {
            var update = Sub(manifest, "update");
            return Str(update, key);
        }

        private static MiniJson Sub(MiniJson node, string key)
        {
            if (node == null) return null;
            object v;
            return node.TryGet(key, out v) ? v as MiniJson : null;
        }

        private static string Str(MiniJson node, string key)
        {
            if (node == null) return null;
            object v;
            if (!node.TryGet(key, out v) || v == null) return null;
            // Manifest numeric fields (dictBuild, dictRows, files.*.size) are parsed as
            // long, not string. Using `as string` here silently returned null for all of
            // them, which made Long()/dictBuild read 0 and disabled in-game dictionary
            // auto-update entirely. Read the boxed value in a culture-invariant way.
            var s = v as string;
            if (s != null) return s;
            if (v is long) return ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (v is double) return ((double)v).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (v is bool) return (bool)v ? "true" : "false";
            return null;
        }

        private static string FetchManifest()
        {
            for (var i = 0; i < ManifestUrls.Length; i++)
            {
                var json = Fetch(ManifestUrls[i], 6);
                if (!string.IsNullOrEmpty(json)) return json;
            }
            return null;
        }

        private static string Fetch(string url, int timeoutSeconds)
        {
            try
            {
                using (var http = NewClient(timeoutSeconds))
                {
                    return http.GetStringAsync(url).GetAwaiter().GetResult();
                }
            }
            catch
            {
                return null;
            }
        }

        private static byte[] FetchBytes(string url, int timeoutSeconds = 20)
        {
            try
            {
                using (var http = NewClient(timeoutSeconds))
                using (var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                                         .GetAwaiter().GetResult())
                {
                    resp.EnsureSuccessStatusCode();
                    // Content-Length 能在读完响应体之前就拒绝明显超大的文件。
                    var declared = resp.Content.Headers.ContentLength;
                    if (declared.HasValue && declared.Value > MaxDictionaryBytes) return null;
                    using (var stream = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var ms = new MemoryStream())
                    {
                        var buf = new byte[64 * 1024];
                        int n;
                        while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (ms.Length + n > MaxDictionaryBytes) return null;
                            ms.Write(buf, 0, n);
                        }
                        return ms.ToArray();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static byte[] FetchBytesLimited(string url)
        {
            return FetchBytes(url);
        }

        /// <summary>只接受 https 且属于项目自身分发域名的字典地址。</summary>
        private static bool IsAllowedDictionaryUrl(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps) return false;
            var host = u.Host.ToLowerInvariant();
            // raw.githubusercontent.com 与 jsDelivr 的若干节点；镜像 URL 由 MirrorUrl() 生成。
            return host == "raw.githubusercontent.com" ||
                   host == "github.com" ||
                   host.EndsWith(".jsdelivr.net", StringComparison.Ordinal) ||
                   host == "cdn.jsdelivr.net" ||
                   host == "fastly.jsdelivr.net";
        }

        private static HttpClient NewClient(int timeoutSeconds)
        {
            var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            // GitHub raw 需要 UA，否则可能被拒
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SP2Chinese-Updater");
            return http;
        }

        private static bool IsNewer(string remote, string local)
        {
            try { return new Version(remote) > new Version(local); }
            catch { return false; }
        }

        /// <summary>把 GitHub 下载地址换成 jsDelivr 镜像，raw 被墙时还有一条路。
        /// 同时兼容 manifest 里的两种历史写法：
        ///   https://github.com/<repo>/raw/main/<file>              （302 跳到 raw.githubusercontent）
        ///   https://raw.githubusercontent.com/<repo>/main/<file>  （当前主地址）
        /// </summary>
        private static string MirrorUrl(string url)
        {
            const string marker = "/SP2Chinese/";
            var i = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;

            var rest = url.Substring(i + marker.Length);
            // github.com/<repo>/raw/main/<file> 里的 "raw/" 前缀
            if (rest.StartsWith("raw/main/", StringComparison.OrdinalIgnoreCase))
                rest = rest.Substring("raw/main/".Length);
            else if (rest.StartsWith("main/", StringComparison.OrdinalIgnoreCase))
                rest = rest.Substring("main/".Length);
            else
                return null;

            return "https://fastly.jsdelivr.net/gh/baiyangmuzz/SP2Chinese@main/" + rest;
        }


        private static long ReadLocalBuild(string localDictPath)
        {
            try
            {
                var p = Path.Combine(Path.GetDirectoryName(localDictPath), "dict.build");
                if (!File.Exists(p)) return 0;
                long v;
                return long.TryParse(File.ReadAllText(p).Trim(), out v) ? v : 0;
            }
            catch { return 0; }
        }

        private static long Long(MiniJson node, string key)
        {
            var s = Str(node, key);
            long v;
            return long.TryParse(s, out v) ? v : 0;
        }

        private static string Sha256OfFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(path))
                    return Hex(sha.ComputeHash(fs));
            }
            catch
            {
                return null;
            }
        }

        private static string Sha256OfBytes(byte[] data)
        {
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(data));
        }

        private static string Hex(byte[] hash)
        {
            var sb = new StringBuilder(hash.Length * 2);
            for (var i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>
    /// 极简 JSON 读取器 —— 只为解析 manifest.json，不引第三方库。
    /// 支持对象 / 数组 / 字符串 / 数字 / true / false / null。
    /// </summary>
    internal sealed class MiniJson
    {
        private readonly System.Collections.Generic.Dictionary<string, object> _map =
            new System.Collections.Generic.Dictionary<string, object>(StringComparer.Ordinal);

        internal bool TryGet(string key, out object value)
        {
            return _map.TryGetValue(key, out value);
        }

        internal static MiniJson Parse(string text)
        {
            var i = 0;
            var v = ParseValue(text, ref i, 0);
            SkipWs(text, ref i);
            if (i != text.Length) throw new FormatException("trailing data after JSON at " + i);
            return v as MiniJson;
        }

        private const int MaxDepth = 32;

        private static object ParseValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("manifest JSON nesting too deep");
            SkipWs(s, ref i);
            if (i >= s.Length) return null;

            switch (s[i])
            {
                case '{': return ParseObject(s, ref i, depth + 1);
                case '[': return ParseArray(s, ref i, depth + 1);
                case '"': return ParseString(s, ref i);
                // Validate the whole literal instead of blindly skipping 4/5 characters:
                // a tampered manifest could otherwise hide a trailing payload behind "truex".
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length ||
                string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw new FormatException("invalid JSON literal at " + i);
            i += literal.Length;
        }

        private static MiniJson ParseObject(string s, ref int i, int depth)
        {
            var obj = new MiniJson();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return obj; }

            while (i < s.Length)
            {
                var key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' at " + i);
                i++;
                var val = ParseValue(s, ref i, depth);
                if (key != null) obj._map[key] = val;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
                throw new FormatException("expected ',' or '}' at " + i);
            }
            return obj;
        }

        private static System.Collections.Generic.List<object> ParseArray(string s, ref int i, int depth)
        {
            var list = new System.Collections.Generic.List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }

            while (i < s.Length)
            {
                list.Add(ParseValue(s, ref i, depth));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
                throw new FormatException("expected ',' or ']' at " + i);
            }
            return list;
        }

        private static string ParseString(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '"') return null;
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                var c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                var e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 <= s.Length)
                        {
                            var hex = s.Substring(i, 4);
                            int code;
                            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                             null, out code))
                                sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        private static object ParseNumber(string s, ref int i)
        {
            var start = i;
            while (i < s.Length && ("+-0123456789.eE".IndexOf(s[i]) >= 0)) i++;
            var token = s.Substring(start, i - start);
            if (token.Length == 0) throw new FormatException("expected number at " + start);
            // Integers come back as long, not double: manifest fields such as dictBuild /
            // dictRows / files.*.size are integers, and the Str()/Long() helpers below read
            // them through Convert.ToString. Keeping them integral also avoids the precision
            // loss a double would introduce for a 12-digit yyyyMMddHHmm build number.
            long l;
            if (long.TryParse(token, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out l))
                return l;
            double d;
            if (double.TryParse(token, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out d))
                return d;
            throw new FormatException("invalid number '" + token + "' at " + start);
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }
    }
}
