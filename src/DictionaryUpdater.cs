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
        private const string ManifestUrl =
            "https://raw.githubusercontent.com/baiyangmuzz/SP2Chinese/main/manifest.json";

        /// <summary>已下载并校验通过的临时字典路径；主线程消费后置空。</summary>
        internal static volatile string PendingPath;

        internal static volatile string LastStatus;

        private static int _started;

        internal static void Kick(string localDictPath, string localVersion)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            Task.Run(() => Run(localDictPath, localVersion));
        }

        private static void Run(string localDictPath, string localVersion)
        {
            try
            {
                var json = Fetch(ManifestUrl);
                if (string.IsNullOrEmpty(json))
                {
                    LastStatus = "manifest 获取失败，使用本地词库";
                    return;
                }

                var manifest = MiniJson.Parse(json);
                if (manifest == null)
                {
                    LastStatus = "manifest 解析失败";
                    return;
                }

                // 插件本身变了 -> 提示下载完整包，不动字典（避免新旧逻辑配新字典出新问题）
                var remoteMod = Str(manifest, "modVersion");
                if (!string.IsNullOrEmpty(remoteMod) && remoteMod != localVersion)
                {
                    LastStatus = "有新版本插件 " + remoteMod + "（当前 " + localVersion +
                                 "），请到 QQ 群 1128992783 或 Releases 下载完整包";
                    return;
                }

                var remoteDictId = Str(manifest, "dictVersion");
                var localDictId = Sha256OfFile(localDictPath);
                if (localDictId != null && localDictId.Length > 12) localDictId = localDictId.Substring(0, 12);

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

                var body = FetchBytes(url);
                if (body == null)
                {
                    LastStatus = "词库下载失败，继续用本地版本";
                    return;
                }

                var expect = Str(manifest, "dictSha256");
                if (string.IsNullOrEmpty(expect))
                {
                    var dictMeta = Sub(manifest, "files");
                    var entry = Sub(dictMeta, "BepInEx/plugins/SP2Chinese/dictionary_zh_cn.tsv");
                    expect = Str(entry, "sha256");
                }
                var actual = Sha256OfBytes(body);
                if (!string.IsNullOrEmpty(expect) &&
                    !string.Equals(expect, actual, StringComparison.OrdinalIgnoreCase))
                {
                    LastStatus = "词库哈希不符（期望 " + Short(expect) + "，实际 " + Short(actual) + "），已丢弃";
                    return;
                }

                var tmp = localDictPath + ".download";
                File.WriteAllBytes(tmp, body);
                PendingPath = tmp;
                LastStatus = "已下载新词库 " + remoteDictId + "（" + (body.Length / 1024) + " KB），等待热重载";
            }
            catch (Exception e)
            {
                LastStatus = "更新检查异常：" + e.GetType().Name;
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
            if (!node.TryGet(key, out v)) return null;
            return v as string;
        }

        private static string Fetch(string url)
        {
            try
            {
                using (var http = NewClient())
                {
                    return http.GetStringAsync(url).GetAwaiter().GetResult();
                }
            }
            catch
            {
                return null;
            }
        }

        private static byte[] FetchBytes(string url)
        {
            try
            {
                using (var http = NewClient())
                {
                    return http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                }
            }
            catch
            {
                return null;
            }
        }

        private static HttpClient NewClient()
        {
            var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            // GitHub raw 需要 UA，否则可能被拒
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SP2Chinese-Updater");
            return http;
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
            var v = ParseValue(text, ref i);
            return v as MiniJson;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;

            switch (s[i])
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't': i += 4; return true;
                case 'f': i += 5; return false;
                case 'n': i += 4; return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static MiniJson ParseObject(string s, ref int i)
        {
            var obj = new MiniJson();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return obj; }

            while (i < s.Length)
            {
                var key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                var val = ParseValue(s, ref i);
                if (key != null) obj._map[key] = val;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
                break;
            }
            return obj;
        }

        private static System.Collections.Generic.List<object> ParseArray(string s, ref int i)
        {
            var list = new System.Collections.Generic.List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }

            while (i < s.Length)
            {
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
                break;
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
            double d;
            if (double.TryParse(s.Substring(start, i - start),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out d))
                return d;
            return null;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }
    }
}
