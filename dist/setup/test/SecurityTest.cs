using System;
using System.IO;
using System.IO.Compression;
using SP2Chinese.Setup;

/// <summary>
/// 安全防护测试：路径穿越 / 磁盘检查 / 单实例。
///
/// 构造一个含恶意路径的 payload，确认安装器拒绝写入游戏目录之外。
/// 这是真实威胁：SHA256 只证明「包没被改动」，不证明「路径安全」。
/// </summary>
internal static class SecurityTest
{
    private static int _failed;

    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string tmp = Path.Combine(Path.GetTempPath(), "sp2sec-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmp);

        try
        {
            Console.WriteLine("=== 1. 恶意 payload 必须被拒绝 ===");

            // 造一个含 ../ 逃逸路径的 zip
            string evilZip = Path.Combine(tmp, "evil.zip");
            using (var fs = File.Create(evilZip))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteEntry(z, "BepInEx/plugins/SP2Chinese/SP2Chinese.dll", "MZ-fake");
                WriteEntry(z, "BepInEx/plugins/SP2Chinese/dictionary_zh_cn.tsv", "k\tv\n");
                // 关键：试图写到游戏目录之外
                WriteEntry(z, "../../escaped.txt", "should never be written");
            }

            string game = Path.Combine(tmp, "game");
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, "SimplePlanes 2.exe"), "stub");

            // 用恶意 zip 当 payload；manifest 只列安全文件，所以这里直接测 WriteTo 路径
            var payload = Payload.OpenFromFile(evilZip);
            bool rejected = false;
            string msg = "";
            try
            {
                // SafeCombine 是 private，通过 Installer.Install 走完整流程验证
                var m = Manifest.Parse(MakeManifestJson());
                var inst = new Installer(payload, m, (x, s) => { }, s => { });
                var outcome = inst.Install(game);
                rejected = !outcome.Success;
                msg = outcome.Message;
            }
            catch (Exception ex)
            {
                rejected = true;
                msg = ex.Message;
            }

            Check("含 ../ 的包被拒绝", rejected, msg);

            // 确认没有文件逃逸到游戏目录外
            string escaped = Path.Combine(tmp, "escaped.txt");
            Check("没有文件被写到游戏目录外", !File.Exists(escaped));

            Console.WriteLine();
            Console.WriteLine("=== 2. 正常包仍然能装 ===");
            string okZip = Path.Combine(tmp, "ok.zip");
            using (var fs = File.Create(okZip))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteEntry(z, "BepInEx/plugins/SP2Chinese/SP2Chinese.dll", "MZ-fake");
                WriteEntry(z, "BepInEx/plugins/SP2Chinese/dictionary_zh_cn.tsv", "k\tv\n");
            }
            var p2 = Payload.OpenFromFile(okZip);
            var m2 = Manifest.Parse(MakeManifestJson());
            var inst2 = new Installer(p2, m2, (x, s) => { }, s => { });
            var o2 = inst2.Install(game);
            Check("正常包安装成功", o2.Success, o2.Message);
            Check("插件目录已创建",
                Directory.Exists(Path.Combine(game, "BepInEx", "plugins", "SP2Chinese")));

            Console.WriteLine();
            Console.WriteLine("=== 3. 磁盘检查方法存在（无法在测试中真的塞满盘）===");
            var mi = typeof(Installer).GetMethod("EnsureFreeSpace",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Check("EnsureFreeSpace 已实现", mi != null);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "RESULT: ALL PASSED" : "RESULT: " + _failed + " FAILED");
        return _failed == 0 ? 0 : 1;
    }

    private static void WriteEntry(ZipArchive z, string name, string content)
    {
        var e = z.CreateEntry(name);
        using (var w = new StreamWriter(e.Open(), new System.Text.UTF8Encoding(false)))
            w.Write(content);
    }

    private static string MakeManifestJson()
    {
        return @"{
  ""modVersion"": ""1.7.0"",
  ""dictVersion"": ""aaaaaaaaaaaa"",
  ""dictBuild"": 202611140000,
  ""dictRows"": 1,
  ""dictSha256"": ""aaaa"",
  ""files"": {
    ""BepInEx/plugins/SP2Chinese/SP2Chinese.dll"": { ""sha256"": ""b4dfa38ae2092c22ca85b26cf3dd4d54b50682562e2ce3e617b4bac2c8cd9dd4"", ""size"": 7 },
    ""BepInEx/plugins/SP2Chinese/dictionary_zh_cn.tsv"": { ""sha256"": ""44164c6583de4f96a1f8d0906f7444e315fb15d5ef23b472285e5754e726f744"", ""size"": 4 }
  }
}";
    }

    private static void Check(string name, bool ok, string detail = null)
    {
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + name +
                          (string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")"));
        if (!ok) _failed++;
    }
}
