using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SP2Chinese.Setup
{
    /// <summary>
    /// 安装时顺带检查有没有更新的词库。
    ///
    /// 直接登录器里做这件事的意义：玩家装一次就拿到最新词库，不用先进游戏等热更新。
    /// 网络不通时静默跳过，绝不阻塞安装。
    /// 实现用 HttpClient（不再 depend 系统 curl.exe），并强制 TLS 校验。
    /// </summary>
    internal static class Updater
    {
        private static readonly string[] ManifestUrls =
        {
            "https://raw.githubusercontent.com/baiyangmuzz/SP2Chinese/main/manifest.json",
            "https://fastly.jsdelivr.net/gh/baiyangmuzz/SP2Chinese@main/manifest.json",
            "https://cdn.jsdelivr.net/gh/baiyangmuzz/SP2Chinese@main/manifest.json"
        };

        private const int MaxBytes = 8 * 1024 * 1024;

        internal sealed class Result
        {
            public bool HasUpdate;
            public byte[] Dictionary;
            public string Sha256 = "";
            public long Build;
            public string Message = "";
        }

        /// <summary>线上版本信息，用于提示「有新版本安装包」。</summary>
        internal sealed class RemoteVersion
        {
            public bool Online;              // 是否成功取到 manifest
            public string ModVersion = "";
            public string ReleaseUrl = "";
            public string Message = "";
        }

        /// <summary>
        /// 只查版本号，不下载任何东西。
        /// 插件本体（DLL）改了必须换整个安装包，所以这里只提示，不自动替换。
        ///
        /// overrideManifestUrl 仅供测试：指向本地假 manifest 就能验证
        /// 「有新版本 / 已是最新 / 网络失败」三种分支，不必真去改线上文件。
        /// </summary>
        internal static async Task<RemoteVersion> CheckVersionAsync(string localVersion, string overrideManifestUrl = null)
        {
            var r = new RemoteVersion();
            try
            {
                string json = overrideManifestUrl != null
                    ? await FetchTextFromUrlAsync(overrideManifestUrl).ConfigureAwait(false)
                    : await FetchTextAsync(ManifestUrls).ConfigureAwait(false);

                if (string.IsNullOrEmpty(json)) { r.Message = "网络不可用"; return r; }

                Manifest remote = Manifest.Parse(json);
                r.Online = true;
                r.ModVersion = remote.ModVersion;
                r.ReleaseUrl = "https://github.com/baiyangmuzz/SP2Chinese/releases/latest";

                if (CompareVersions(remote.ModVersion, localVersion) > 0)
                    r.Message = "新版 " + remote.ModVersion + " 可用（当前 " + localVersion + "）";
                else
                    r.Message = "已是最新版本（" + localVersion + "）";
                return r;
            }
            catch (Exception ex)
            {
                r.Message = "版本检查失败：" + ex.Message;
                return r;
            }
        }

        /// <summary>测试用：从任意 URL 取文本（支持 file:// 便于离线验证）。</summary>
        private static async Task<string> FetchTextFromUrlAsync(string url)
        {
            if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                string path = new Uri(url).LocalPath;
                return File.Exists(path) ? File.ReadAllText(path, System.Text.Encoding.UTF8) : null;
            }
            using (HttpClient http = NewClient())
            {
                string text = await http.GetStringAsync(url).ConfigureAwait(false);
                return (text ?? "").TrimEnd().EndsWith("}") ? text : null;
            }
        }

        /// <summary>语义化版本比较：a &gt; b 返回正数。解析失败按相等处理，避免误报。</summary>
        private static int CompareVersions(string a, string b)
        {
            Version va, vb;
            if (!Version.TryParse(Normalize(a), out va)) return 0;
            if (!Version.TryParse(Normalize(b), out vb)) return 0;
            return va.CompareTo(vb);
        }

        /// <summary>把 "1.6.3" 补成可解析的 "1.6.3.0"。</summary>
        private static string Normalize(string v)
        {
            if (string.IsNullOrEmpty(v)) return "0.0.0.0";
            int dots = 0;
            foreach (char c in v) if (c == '.') dots++;
            while (dots < 3) { v += ".0"; dots++; }
            return v;
        }

        // ════════════════════════════════════════════════════════════
        // 安装器自更新
        //
        // 米哈游启动器能自动更新，靠的是「下载新程序 → 退出 → 替换 → 重启」。
        // Windows 不允许覆盖正在运行的 exe，所以替换必须由一个第三方进程
        // 在本进程退出后完成 —— 标准做法是写一个 cmd 脚本：
        //   等待旧进程退出 → 覆盖 → 删除自身 → 启动新 exe
        //
        // 下载源用 GitHub Releases 的 latest 直链（浏览器会 302 到实际文件）。
        // 必须校验哈希：下载的是即将执行的程序，比词库更敏感。
        // ════════════════════════════════════════════════════════════

        private const string ReleaseApi =
            "https://api.github.com/repos/baiyangmuzz/SP2Chinese/releases/latest";

        /// <summary>
        /// 查「最新安装器版本 + 可校验的下载地址」。
        ///
        /// api.github.com 在国内经常被墙，所以按可靠性排多个来源：
        ///   1. api.github.com —— 最规范，能拿到 digest/size
        ///   2. manifest.json 的 setup 字段 —— 走 raw.githubusercontent / jsDelivr，
        ///      国内可达，且带 sha256，可以安全自动下载
        ///   3. github.com/releases/latest 的重定向 —— 只能拿到版本号，无哈希，
        ///      仅提示用户手动下载（fail-closed：没有哈希绝不自动替换）
        /// </summary>
        internal static async Task<NewInstaller> CheckInstallerUpdateAsync(
            string localVersion, string overrideManifestUrl = null)
        {
            // 1) 官方 API：信息最全，能自动更新
            var viaApi = await CheckInstallerViaApi(localVersion).ConfigureAwait(false);
            if (viaApi.Available || viaApi.Online) return viaApi;

            // 2) manifest 的 setup 段：API 被墙时可达，且带 sha256，同样能自动更新
            var viaManifest = await CheckInstallerViaManifest(localVersion, overrideManifestUrl)
                .ConfigureAwait(false);
            if (viaManifest.Available || viaManifest.Online) return viaManifest;

            // 3) github.com 重定向：只能拿到版本号，无哈希 → 只提示手动下载
            var viaHtml = await CheckInstallerViaHtml(localVersion).ConfigureAwait(false);
            if (viaHtml != null) return viaHtml;

            // 全部失败：把信息量最大的说明带回去
            if (!string.IsNullOrEmpty(viaManifest.Message)) return viaManifest;
            if (string.IsNullOrEmpty(viaApi.Message)) viaApi.Message = "所有更新源都不可达";
            return viaApi;
        }

        /// <summary>
        /// 来源 3：github.com 的 /releases/latest 重定向。
        /// 这里拿不到附件哈希，所以即使发现新版也标 Available=false，
        /// 只把版本号告诉用户，让他自己去页面下载 —— 没有哈希绝不自动替换。
        /// </summary>
        private static async Task<NewInstaller> CheckInstallerViaHtml(string localVersion)
        {
            try
            {
                using (HttpClient http = NewClient(AllowRedirect: false))
                using (var resp = await http.GetAsync(
                    "https://github.com/baiyangmuzz/SP2Chinese/releases/latest",
                    HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    if (resp.StatusCode != HttpStatusCode.Found &&
                        resp.StatusCode != HttpStatusCode.MovedPermanently) return null;

                    string loc = resp.Headers.Location?.ToString() ?? "";
                    var m = System.Text.RegularExpressions.Regex.Match(
                        loc, @"releases/tag/v?(\d+\.\d+\.\d+)");
                    if (!m.Success) return null;

                    string ver = m.Groups[1].Value;
                    if (CompareVersions(ver, localVersion) > 0)
                    {
                        return new NewInstaller
                        {
                            Online = true,
                            Version = ver,
                            Message = "新版 " + ver + " 可用（API 不可达，请到 Releases 页面手动下载）",
                            DownloadUrl = "https://github.com/baiyangmuzz/SP2Chinese/releases/latest"
                        };
                    }
                    return new NewInstaller
                    {
                        Online = true,
                        Version = ver,
                        Message = "安装器已是最新（" + localVersion + "）"
                    };
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 来源 2：manifest.json 的 setup 段。
        ///
        /// 为什么放在 API 之后、HTML 之前：api.github.com 被墙时这里仍然可达
        /// （raw.githubusercontent / jsDelivr），而且带 sha256 —— 有了哈希就能
        /// 安全地自动下载并替换，不必让用户自己去浏览器翻 Releases。
        /// </summary>
        private static async Task<NewInstaller> CheckInstallerViaManifest(
            string localVersion, string overrideManifestUrl = null)
        {
            var r = new NewInstaller();
            try
            {
                string json = overrideManifestUrl != null
                    ? await FetchTextFromUrlAsync(overrideManifestUrl).ConfigureAwait(false)
                    : await FetchTextAsync(ManifestUrls).ConfigureAwait(false);
                if (string.IsNullOrEmpty(json)) { r.Message = "manifest 不可达"; return r; }

                Manifest remote = Manifest.Parse(json);
                r.Online = true;

                if (string.IsNullOrEmpty(remote.SetupVersion))
                {
                    // 清单可达但还没写 setup 段（发布端尚未带上安装器信息）。
                    // 标 Online=false 让调用方继续走 HTML 兜底，而不是停在这里。
                    r.Online = false;
                    r.Message = "manifest 无 setup 段（发布端未写入安装器信息）";
                    return r;
                }
                r.Version = remote.SetupVersion;

                if (CompareVersions(remote.SetupVersion, localVersion) <= 0)
                {
                    r.Message = "安装器已是最新（" + localVersion + "）";
                    return r;
                }

                // 没有哈希或地址就不允许自动更新（fail-closed）
                if (string.IsNullOrEmpty(remote.SetupSha256) || string.IsNullOrEmpty(remote.SetupUrl))
                {
                    r.Online = false;
                    r.Message = "新版 " + remote.SetupVersion + " 可用，但缺哈希/地址，无法自动更新";
                    return r;
                }
                if (!IsAllowed(remote.SetupUrl))
                {
                    r.Online = false;
                    r.Message = "新版 " + remote.SetupVersion + " 的下载地址不在允许列表";
                    return r;
                }

                r.Available = true;
                r.DownloadUrl = remote.SetupUrl;
                r.Sha256 = remote.SetupSha256;
                r.Size = remote.SetupSize;
                r.Message = "安装器新版 " + remote.SetupVersion + " 可用";
                return r;
            }
            catch (Exception ex)
            {
                r.Message = "manifest 检查失败：" + ex.Message;
                return r;
            }
        }

        /// <summary>来源 1：api.github.com 的结构化数据。</summary>
        private static async Task<NewInstaller> CheckInstallerViaApi(string localVersion)
        {
            var r = new NewInstaller();
            try
            {
                string json = await FetchTextAsync(new[] { ReleaseApi + "?per_page=1" })
                    .ConfigureAwait(false);
                if (string.IsNullOrEmpty(json)) { r.Message = "api.github.com 不可达"; return r; }

                r.Online = true;
                MiniJson release = MiniJson.Parse(json) as MiniJson;
                if (release == null) { r.Message = "Releases 返回格式异常"; return r; }

                object tagObj;
                if (!release.TryGet("tag_name", out tagObj)) { r.Message = "Releases 无 tag"; return r; }
                string tag = Convert.ToString(tagObj);
                r.Version = tag.TrimStart('v', 'V');

                if (CompareVersions(r.Version, localVersion) <= 0)
                {
                    r.Message = "安装器已是最新（" + localVersion + "）";
                    return r;
                }

                MiniJson assets = SubArray(release, "assets");
                if (assets == null) { r.Message = "Releases 无附件"; return r; }
                foreach (var kv in assets.Items)
                {
                    MiniJson a = kv.Value as MiniJson;
                    if (a == null) continue;
                    string name = (Str2(a, "name") ?? "").ToLowerInvariant();
                    if (!name.StartsWith("sp2chinese-setup-") || !name.EndsWith(".exe")) continue;

                    r.Available = true;
                    r.DownloadUrl = Str2(a, "browser_download_url");
                    r.Size = Long2(a, "size");
                    string digest = Str2(a, "digest") ?? "";
                    if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        r.Sha256 = digest.Substring(7);
                    break;
                }
                if (!r.Available) { r.Message = "新版本 " + r.Version + " 存在但没有安装器附件"; return r; }

                r.Message = "安装器新版 " + r.Version + " 可用";
                return r;
            }
            catch (Exception ex)
            {
                r.Message = "检查安装器更新失败：" + ex.Message;
                return r;
            }
        }

        internal sealed class NewInstaller
        {
            public bool Available;
            public bool Online;              // 至少连上了一个来源（哪怕拿不到附件明细）
            public string Version = "";
            public string DownloadUrl = "";
            public long Size;
            public string Sha256 = "";      // GitHub API 的 digest 字段（sha256:xxx）
            public string Message = "";
        }

        /// <summary>查 Releases 上有没有比本地新的安装器 exe。</summary>
        // （旧版 CheckInstallerUpdateAsync 已由上面的 API+HTML 双来源版本取代）


        /// <summary>下载新版安装器到临时目录并校验哈希。失败返回 null。</summary>
        internal static async Task<string> DownloadInstallerAsync(NewInstaller info)
        {
            try
            {
                byte[] body = await FetchBytesAsync(info.DownloadUrl).ConfigureAwait(false);
                if (body == null || body.Length == 0) return null;
                if (info.Size > 0 && body.Length != info.Size) return null;
                if (!string.IsNullOrEmpty(info.Sha256)
                    && !string.Equals(Payload.Sha256OfBytes(body), info.Sha256, StringComparison.OrdinalIgnoreCase))
                    return null;

                string path = Path.Combine(Path.GetTempPath(),
                    "SP2Chinese-Setup-" + info.Version + ".exe");
                File.WriteAllBytes(path, body);
                return path;
            }
            catch { return null; }
        }

        private static MiniJson SubArray(MiniJson node, string key)
        {
            object v;
            if (node == null || !node.TryGet(key, out v)) return null;
            var arr = v as List<object>;
            if (arr == null) return null;
            var wrapper = new MiniJson();
            for (int i = 0; i < arr.Count; i++) wrapper.Set("#" + i, arr[i]);
            return wrapper;
        }

        private static string Str2(MiniJson node, string key)
        {
            object v;
            if (node == null || !node.TryGet(key, out v) || v == null) return null;
            return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static long Long2(MiniJson node, string key)
        {
            return long.TryParse(Str2(node, key) ?? "", out long l) ? l : 0;
        }

        /// <summary>
        /// 下载完成后的自替换流程（v2，不再用 cmd 脚本）。
        ///
        /// 旧方案的三个失败点：
        ///   1. cmd 由提权进程启动，但 UAC 下子进程不一定继承提权，copy 写
        ///      Program Files 会静默失败；
        ///   2. 脚本按 ANSI 编码写，中文路径乱码；
        ///   3. tasklist|find 按 PID 子串匹配不可靠。
        ///
        /// 新方案：启动「新 exe 自己」并传 --replace-self 参数，由新进程完成：
        ///   等旧进程退出 → 覆盖 → 用新身份重启。全程 C#，权限继承明确
        ///   （新进程由本进程启动，继承管理员令牌），没有脚本编码问题。
        /// </summary>
        internal static void ScheduleSelfReplace(string newExePath, string newVersion)
        {
            string current = Process.GetCurrentProcess().MainModule.FileName;

            var psi = new ProcessStartInfo
            {
                FileName = newExePath,
                Arguments = "--replace-self \"" + current + "\" "
                          + Process.GetCurrentProcess().Id + " \"" + newVersion + "\"",
                UseShellExecute = true          // 走 shell，继承当前提权令牌
            };
            Process.Start(psi);
        }

        /// <summary>
        /// --replace-self 模式：等旧进程退出后，用它自己覆盖旧 exe，再以正常方式启动。
        ///
        /// 覆盖后还会把文件名里的版本号改成新版本：原地覆盖只换内容不换名字，
        /// 否则会出现「文件名写着 v1.6.2、实际已经是 v1.6.3」的困惑。
        /// </summary>
        internal static int RunReplaceSelf(string oldExePath, string oldPidStr,
                                           string newVersion, string logPath)
        {
            try
            {
                if (!int.TryParse(oldPidStr, out int oldPid)) return 1;

                // 等旧进程完全退出（最多 15 秒）
                for (int i = 0; i < 60; i++)
                {
                    try { Process.GetProcessById(oldPid); Thread.Sleep(250); }
                    catch (ArgumentException) { break; }   // 进程已不存在
                }

                // 覆盖：先删旧（退出后允许），再把新文件移过去
                if (File.Exists(oldExePath))
                {
                    try { File.SetAttributes(oldExePath, FileAttributes.Normal); } catch { }
                    File.Delete(oldExePath);
                }
                File.Move(SelfExePath, oldExePath);

                string finalPath = RenameWithVersion(oldExePath, newVersion);

                // 以正常方式启动新版本（不带 --replace-self）
                Process.Start(new ProcessStartInfo
                {
                    FileName = finalPath,
                    UseShellExecute = true
                });
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(logPath, "replace-self failed: " + ex); } catch { }
                return 1;
            }
        }

        /// <summary>
        /// 把文件名里的 vX.Y.Z 换成新版本，例如
        ///   SP2Chinese-Setup-v1.6.2-OLD-TEST.exe → SP2Chinese-Setup-v1.6.3-OLD-TEST.exe
        /// 文件名里没有版本号时原样返回。
        /// </summary>
        private static string RenameWithVersion(string exePath, string version)
        {
            try
            {
                if (string.IsNullOrEmpty(version)) return exePath;
                string dir = Path.GetDirectoryName(exePath) ?? "";
                string name = Path.GetFileNameWithoutExtension(exePath);
                string ext = Path.GetExtension(exePath);

                var m = System.Text.RegularExpressions.Regex.Match(name, @"v\d+(?:\.\d+)+");
                if (!m.Success) return exePath;

                string renamed = name.Substring(0, m.Index) + "v" + version
                               + name.Substring(m.Index + m.Length);
                string target = Path.Combine(dir, renamed + ext);
                if (string.Equals(target, exePath, StringComparison.OrdinalIgnoreCase)) return exePath;

                if (File.Exists(target)) { try { File.Delete(target); } catch { } }
                File.Move(exePath, target);
                return target;
            }
            catch { return exePath; }
        }

        /// <summary>当前正在运行的 exe 路径（--replace-self 模式下 = 新下载的那个）。</summary>
        internal static string SelfExePath
        {
            get { return Process.GetCurrentProcess().MainModule.FileName; }
        }

        /// <summary>检查并下载比本地更新的词库；没有更新或网络失败时 HasUpdate=false。</summary>
        internal static async Task<Result> CheckAsync(long localBuild, string localSha)
        {
            var outcome = new Result();
            try
            {
                string json = await FetchTextAsync(ManifestUrls).ConfigureAwait(false);
                if (string.IsNullOrEmpty(json)) { outcome.Message = "网络不可用，跳过词库更新"; return outcome; }

                Manifest remote = Manifest.Parse(json);
                if (remote.DictBuild > 0 && remote.DictBuild <= localBuild)
                {
                    outcome.Message = "词库已是本地较新版本";
                    return outcome;
                }
                if (!string.IsNullOrEmpty(remote.DictVersion)
                    && string.Equals(remote.DictVersion, localSha, StringComparison.OrdinalIgnoreCase))
                {
                    outcome.Message = "词库已是最新";
                    return outcome;
                }
                if (string.IsNullOrEmpty(remote.DictSha256))
                {
                    outcome.Message = "线上清单缺少哈希，拒绝未校验的词库";
                    return outcome;
                }
                if (!IsAllowed(remote.UpdateDictionary))
                {
                    outcome.Message = "词库地址不在允许列表，已拒绝";
                    return outcome;
                }

                byte[] body = await FetchBytesAsync(remote.UpdateDictionary).ConfigureAwait(false);
                if (body == null)
                {
                    outcome.Message = "词库下载失败";
                    return outcome;
                }

                string actual = Payload.Sha256OfBytes(body);
                if (!string.Equals(actual, remote.DictSha256, StringComparison.OrdinalIgnoreCase))
                {
                    outcome.Message = "词库哈希不符，已丢弃";
                    return outcome;
                }

                outcome.HasUpdate = true;
                outcome.Dictionary = body;
                outcome.Sha256 = actual;
                outcome.Build = remote.DictBuild;
                outcome.Message = "已获取新词库（" + (body.Length / 1024) + " KB）";
                return outcome;
            }
            catch (Exception ex)
            {
                outcome.Message = "更新检查失败：" + ex.Message;
                return outcome;
            }
        }

        private static bool IsAllowed(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps) return false;
            string host = u.Host.ToLowerInvariant();
            return host == "raw.githubusercontent.com" || host == "github.com"
                || host.EndsWith(".jsdelivr.net", StringComparison.Ordinal);
        }

        private static HttpClient NewClient(bool AllowRedirect = true)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = AllowRedirect
            };
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SP2Chinese-Setup");
            return http;
        }

        private static async Task<string> FetchTextAsync(IEnumerable<string> urls)
        {
            foreach (string url in urls)
            {
                try
                {
                    using (HttpClient http = NewClient())
                    {
                        string text = await http.GetStringAsync(url).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(text) && text.TrimEnd().EndsWith("}")
                            && text.Contains("\"dictSha256\""))
                            return text;
                    }
                }
                catch { /* 换下一个源 */ }
            }
            return null;
        }

        private static async Task<byte[]> FetchBytesAsync(string url)
        {
            try
            {
                using (HttpClient http = NewClient())
                using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    long? declared = resp.Content.Headers.ContentLength;
                    if (declared.HasValue && declared.Value > MaxBytes) return null;

                    using (Stream s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var ms = new MemoryStream())
                    {
                        var buf = new byte[64 * 1024];
                        int n;
                        while ((n = await s.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                        {
                            if (ms.Length + n > MaxBytes) return null;
                            ms.Write(buf, 0, n);
                        }
                        return ms.ToArray();
                    }
                }
            }
            catch { return null; }
        }
    }
}
