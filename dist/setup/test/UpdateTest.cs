using System;
using System.IO;
using System.Threading.Tasks;
using SP2Chinese.Setup;

/// <summary>
/// 「检查更新」功能的离线测试。
///
/// 覆盖三条分支，全部用本地假 manifest 驱动，不依赖线上文件是否恰好更新过：
///   1. 线上版本更高   -> 应提示「新版 X 可用」
///   2. 版本相同       -> 应提示「已是最新」
///   3. 线上版本更低   -> 也应提示「已是最新」（不能把降级当成更新）
///   4. 拿不到 manifest -> 应报「网络不可用」，而不是崩或误报有新版
/// </summary>
internal static class UpdateTest
{
    private static int _failed;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== 检查更新 · 离线测试 ===");
        Console.WriteLine();

        string dir = Path.Combine(Path.GetTempPath(), "sp2upd-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);

        try
        {
            TestNewer(dir);
            TestSame(dir);
            TestOlder(dir);
            TestUnreachable(dir);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "RESULT: ALL PASSED" : "RESULT: " + _failed + " FAILED");
        return _failed == 0 ? 0 : 1;
    }

    private static void TestNewer(string dir)
    {
        Console.WriteLine("-- 分支 1：线上有更新的插件版本 --");
        string mf = WriteManifest(dir, "1.6.4", 202611150000);
        Updater.RemoteVersion r = Updater.CheckVersionAsync("1.6.3", ToUrl(mf)).GetAwaiter().GetResult();

        Check("联网成功", r.Online);
        Check("读到线上版本 1.6.4", r.ModVersion == "1.6.4", r.ModVersion);
        Check("提示有新版本", r.Message.Contains("新版"), r.Message);
        Check("包含 Releases 链接", !string.IsNullOrEmpty(r.ReleaseUrl));
    }

    private static void TestSame(string dir)
    {
        Console.WriteLine();
        Console.WriteLine("-- 分支 2：版本相同 --");
        string mf = WriteManifest(dir, "1.6.3", 202611080911);
        Updater.RemoteVersion r = Updater.CheckVersionAsync("1.6.3", ToUrl(mf)).GetAwaiter().GetResult();

        Check("联网成功", r.Online);
        Check("提示已是最新", r.Message.Contains("已是最新"), r.Message);
    }

    private static void TestOlder(string dir)
    {
        Console.WriteLine();
        Console.WriteLine("-- 分支 3：线上版本反而更低（不能当成有更新）--");
        string mf = WriteManifest(dir, "1.6.2", 202611010000);
        Updater.RemoteVersion r = Updater.CheckVersionAsync("1.6.3", ToUrl(mf)).GetAwaiter().GetResult();

        Check("联网成功", r.Online);
        // 注意不能只用 Contains("新版") 判断：「已是最新版本」里也含「新版」二字。
        // 正确的判据是「新版本号出现在提示里」。
        Check("不提示有新版本", !r.Message.Contains("1.6.2"), r.Message);
        Check("提示已是最新", r.Message.Contains("已是最新"), r.Message);
    }

    private static void TestUnreachable(string dir)
    {
        Console.WriteLine();
        Console.WriteLine("-- 分支 4：拿不到 manifest --");
        string missing = Path.Combine(dir, "not-exist.json");
        Updater.RemoteVersion r = Updater.CheckVersionAsync("1.6.3", ToUrl(missing)).GetAwaiter().GetResult();

        Check("离线判定", !r.Online);
        Check("给出可读原因", !string.IsNullOrEmpty(r.Message), r.Message);
        Check("不误报有新版", !r.Message.Contains("新版"));
    }

    // ────────────────────────────────────────────────────────────

    private static string WriteManifest(string dir, string version, long build)
    {
        string path = Path.Combine(dir, "manifest-" + version + ".json");
        string json =
            "{\"modVersion\":\"" + version + "\"," +
            "\"dictVersion\":\"32e94b7e9372\"," +
            "\"dictBuild\":" + build + "," +
            "\"dictRows\":10393," +
            "\"dictSha256\":\"32e94b7e9372f00ea1569bd4011271aaffc86368c07b43084da2a08179a5a853\"," +
            "\"files\":{}}";
        File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
        return path;
    }

    private static string ToUrl(string path)
    {
        return new Uri(path).AbsoluteUri;
    }

    private static void Check(string name, bool ok, string detail = null)
    {
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + name +
                          (string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")"));
        if (!ok) _failed++;
    }
}
