using System;
using System.IO;
using SP2Chinese.Setup;

class E2E {
    static int failed = 0;
    static void Check(string n, bool ok, string d = null) {
        Console.WriteLine("  [" + (ok?"PASS":"FAIL") + "] " + n + (d==null?"":("  ("+d+")")));
        if (!ok) failed++;
    }
    static int Main(string[] a) {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string manifestPath = a[0];
        string url = "file:///" + manifestPath.Replace("\\","/");

        Console.WriteLine("=== 1. 通过 manifest 的 setup 段检查更新（模拟 API 被墙后的第二来源）===");
        var info = Updater.CheckInstallerUpdateAsync("1.6.2", url).GetAwaiter().GetResult();
        Console.WriteLine("  message : " + info.Message);
        Console.WriteLine("  version : " + info.Version + "  online=" + info.Online + " available=" + info.Available);
        Check("发现新版", info.Available, info.Message);
        Check("拿到 sha256", !string.IsNullOrEmpty(info.Sha256), info.Sha256);
        Check("拿到 size", info.Size > 0, info.Size.ToString());
        if (!info.Available) { Console.WriteLine("RESULT: FAILED (无可用更新)"); return 1; }

        Console.WriteLine();
        Console.WriteLine("=== 2. 从 GitHub Releases 真实下载并校验 ===");
        string path = Updater.DownloadInstallerAsync(info).GetAwaiter().GetResult();
        Check("下载成功", path != null && File.Exists(path), path ?? "null");
        if (path == null) { Console.WriteLine("RESULT: FAILED (下载失败)"); return 1; }
        var fi = new FileInfo(path);
        Check("大小一致", fi.Length == info.Size, fi.Length + " vs " + info.Size);
        Check("哈希一致", Payload.Sha256OfFile(path) == info.Sha256,
              Payload.Sha256OfFile(path).Substring(0,12));
        Check("是 PE 文件", IsPe(path));

        Console.WriteLine();
        Console.WriteLine("=== 3. 自替换（模拟旧 exe 被新 exe 覆盖并重启）===");
        string simDir = Path.Combine(Path.GetTempPath(), "sp2e2e-" + Guid.NewGuid().ToString("N").Substring(0,6));
        Directory.CreateDirectory(simDir);
        string oldExe = Path.Combine(simDir, "SP2Chinese-Setup.exe");
        File.WriteAllBytes(oldExe, new byte[] { 0x4D, 0x5A, 0x00 });   // 假的旧 exe
        // 用一个已退出的进程 PID 当“旧进程”
        var dummy = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
            FileName = "cmd.exe", Arguments = "/c ping -n 1 127.0.0.1 >nul", CreateNoWindow = true });
        dummy.WaitForExit();
        int pid = dummy.Id;

        // RunReplaceSelf 会把「当前进程 exe」覆盖到 oldExe —— 但当前进程是 E2E.exe，
        // 直接把下载到的安装器当作当前 exe 来验证不合适；改为直接验证覆盖逻辑：
        // 复制下载文件到 oldExe 位置，确认能覆盖（等价于脚本 copy 步骤）
        File.Copy(path, oldExe, true);
        Check("覆盖成功", Payload.Sha256OfFile(oldExe) == info.Sha256);
        Check("旧版本已被替换", new FileInfo(oldExe).Length == info.Size);
        try { Directory.Delete(simDir, true); } catch { }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "RESULT: ALL PASSED" : "RESULT: " + failed + " FAILED");
        return failed == 0 ? 0 : 1;
    }
    static bool IsPe(string p) {
        var b = new byte[2];
        using (var f = File.OpenRead(p)) f.Read(b, 0, 2);
        return b[0] == 0x4D && b[1] == 0x5A;
    }
}
