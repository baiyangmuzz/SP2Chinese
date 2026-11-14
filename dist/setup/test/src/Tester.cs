using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SP2Chinese.Setup;

/// <summary>
/// 安装逻辑的离线测试驱动。
///
/// exe 是 GUI，不方便自动化；这里直接调用同样的 Installer / Payload / Manifest，
/// 在临时目录里模拟一个游戏目录，验证：
///   1. 正常安装能写出正确文件且哈希匹配
///   2. 篡改 payload 后 fail-closed（不写坏文件）
///   3. 已有 BepInEx 时不动 core
///   4. 卸载能把插件目录移走
/// </summary>
internal static class Tester
{
    private static int _failed;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length < 1)
        {
            Console.WriteLine("用法: sparketest <payload.zip> [manifest.json]");
            return 2;
        }

        string zipPath = args[0];
        string manifestPath = args.Length > 1 ? args[1] : null;

        // 用临时构建的 payload 替换程序集资源不现实，这里改用文件系统版 Payload
        // 通过反射注入：直接测 Installer 的核心流程。
        Console.WriteLine("== 载入资源 ==");
        Manifest manifest = LoadManifest(manifestPath);
        Console.WriteLine("  manifest: v{0}, {1} 条, build {2}", manifest.ModVersion, manifest.DictRows, manifest.DictBuild);
        Console.WriteLine("  files: {0} 个条目", manifest.Files.Count);

        Payload payload = LoadPayloadFromFile(zipPath);
        Console.WriteLine();

        Console.WriteLine("== 场景 1：全新安装 ==");
        TestFreshInstall(payload, manifest);

        Console.WriteLine();
        Console.WriteLine("== 场景 2：已有 BepInEx ==");
        TestExistingBepInEx(payload, manifest);

        Console.WriteLine();
        Console.WriteLine("== 场景 3：卸载 ==");
        TestUninstall(payload, manifest);

        Console.WriteLine();
        Console.WriteLine("== 场景 4：坏包应被拒绝（fail-closed）==");
        TestTamperedPayload(manifest, zipPath);

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "RESULT: ALL PASSED" : "RESULT: " + _failed + " FAILED");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 篡改 payload 里一个字节，安装必须整单拒绝，且游戏目录一个文件都不能写。
    /// 这是最重要的一条：宁可装不上，也不能把坏文件铺进玩家游戏目录。
    /// </summary>
    private static void TestTamperedPayload(Manifest manifest, string originalZip)
    {
        string tmpZip = Path.Combine(Path.GetTempPath(), "tampered-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
        try
        {
            // 复制并篡改其中一个条目
            using (var src = new System.IO.Compression.ZipArchive(File.OpenRead(originalZip), System.IO.Compression.ZipArchiveMode.Read))
            using (var dst = new System.IO.Compression.ZipArchive(File.Create(tmpZip), System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var e in src.Entries)
                {
                    var ne = dst.CreateEntry(e.FullName);
                    using (var si = e.Open())
                    using (var di = ne.Open())
                    {
                        var buf = new MemoryStream();
                        si.CopyTo(buf);
                        byte[] data = buf.ToArray();
                        // 只改 SP2Chinese.dll 的一个字节
                        if (e.FullName.EndsWith("SP2Chinese.dll", StringComparison.OrdinalIgnoreCase) && data.Length > 200)
                            data[150] ^= 0xFF;
                        di.Write(data, 0, data.Length);
                    }
                }
            }

            Payload bad = Payload.OpenFromFile(tmpZip);
            string game = NewFakeGame();
            try
            {
                var log = new List<string>();
                var inst = new Installer(bad, manifest, (p, s) => { }, s => log.Add(s));
                Installer.Outcome o = inst.Install(game);

                Check("坏包安装被拒绝", !o.Success, o.Message);
                Check("未写入插件目录", !Directory.Exists(Path.Combine(game, "BepInEx/plugins/SP2Chinese")));
                Check("未写入 BepInEx core", !File.Exists(Path.Combine(game, "BepInEx/core/BepInEx.dll")));
            }
            finally { SafeDelete(game); }
        }
        finally { try { File.Delete(tmpZip); } catch { } }
    }

    private static void TestFreshInstall(Payload payload, Manifest manifest)
    {
        string game = NewFakeGame();
        try
        {
            var log = new List<string>();
            var inst = new Installer(payload, manifest, (p, s) => { }, s => log.Add(s));
            Installer.Outcome o = inst.Install(game);

            Check("安装成功", o.Success, o.Message);
            if (!o.Success) { DumpLog(log); return; }

            Check("词典存在", File.Exists(Path.Combine(game, "BepInEx/plugins/SP2Chinese/dictionary_zh_cn.tsv")));
            Check("DLL 存在", File.Exists(Path.Combine(game, "BepInEx/plugins/SP2Chinese/SP2Chinese.dll")));
            Check("dict.build 存在", File.Exists(Path.Combine(game, "BepInEx/plugins/SP2Chinese/dict.build")));
            Check("BepInEx core 就位", File.Exists(Path.Combine(game, "BepInEx/core/BepInEx.dll")));
            Check("winhttp 就位", File.Exists(Path.Combine(game, "winhttp.dll")));

            // 词典哈希必须等于 manifest 声明
            string dict = Path.Combine(game, "BepInEx/plugins/SP2Chinese/dictionary_zh_cn.tsv");
            if (File.Exists(dict))
            {
                string actual = Payload.Sha256OfFile(dict);
                Check("词典哈希匹配", string.Equals(actual, manifest.DictSha256, StringComparison.OrdinalIgnoreCase),
                      actual.Substring(0, 12) + " vs " + manifest.DictSha256.Substring(0, 12));
            }

            // dict.build 内容应等于 manifest.dictBuild
            string bp = Path.Combine(game, "BepInEx/plugins/SP2Chinese/dict.build");
            if (File.Exists(bp))
                Check("dict.build 一致", File.ReadAllText(bp).Trim() == manifest.DictBuild.ToString(),
                      File.ReadAllText(bp).Trim());
        }
        finally { SafeDelete(game); }
    }

    private static void TestExistingBepInEx(Payload payload, Manifest manifest)
    {
        string game = NewFakeGame();
        try
        {
            // 预置一个「别的模组」用的 BepInEx
            string core = Path.Combine(game, "BepInEx/core");
            Directory.CreateDirectory(core);
            File.WriteAllText(Path.Combine(core, "BepInEx.dll"), "EXISTING-BEPINEX-DO-NOT-TOUCH");
            File.WriteAllText(Path.Combine(core, "OtherMod.dll"), "keep-me");

            var log = new List<string>();
            var inst = new Installer(payload, manifest, (p, s) => { }, s => log.Add(s));
            Installer.Outcome o = inst.Install(game);

            Check("安装成功", o.Success, o.Message);
            if (!o.Success) { DumpLog(log); return; }

            string bep = Path.Combine(core, "BepInEx.dll");
            Check("未覆盖已有 BepInEx", File.ReadAllText(bep) == "EXISTING-BEPINEX-DO-NOT-TOUCH",
                  File.ReadAllText(bep).Length > 40 ? "被覆盖了!" : File.ReadAllText(bep));
            Check("别的模组保留", File.Exists(Path.Combine(core, "OtherMod.dll")));
            Check("汉化仍装上", File.Exists(Path.Combine(game, "BepInEx/plugins/SP2Chinese/SP2Chinese.dll")));
        }
        finally { SafeDelete(game); }
    }

    private static void TestUninstall(Payload payload, Manifest manifest)
    {
        string game = NewFakeGame();
        try
        {
            var inst = new Installer(payload, manifest, (p, s) => { }, s => { });
            inst.Install(game);

            string pluginDir = Path.Combine(game, "BepInEx/plugins/SP2Chinese");
            Check("卸载前插件存在", Directory.Exists(pluginDir));

            Installer.Outcome o = inst.Uninstall(game);
            Check("卸载成功", o.Success, o.Message);
            Check("插件目录已移走", !Directory.Exists(pluginDir));
            Check("BepInEx 本体保留", File.Exists(Path.Combine(game, "BepInEx/core/BepInEx.dll")));

            string backups = Path.Combine(game, "BepInEx/_mod_backups");
            bool hasUninstall = false;
            if (Directory.Exists(backups))
                foreach (string d in Directory.GetDirectories(backups))
                    if (Path.GetFileName(d).Contains("uninstalled")) hasUninstall = true;
            Check("卸载备份存在", hasUninstall);
        }
        finally { SafeDelete(game); }
    }

    // ────────────────────────────────────────────────────────────

    private static string NewFakeGame()
    {
        string root = Path.Combine(Path.GetTempPath(), "sp2setuptest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Path.Combine(root, "SimplePlanes 2_Data"));
        File.WriteAllText(Path.Combine(root, "SimplePlanes 2.exe"), "stub");
        return root;
    }

    private static Manifest LoadManifest(string path)
    {
        if (path != null && File.Exists(path)) return Manifest.Load(path);
        // 从 exe 资源里读
        Assembly asm = Assembly.GetExecutingAssembly();
        foreach (string n in asm.GetManifestResourceNames())
            if (n.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                using (var s = asm.GetManifestResourceStream(n))
                using (var r = new StreamReader(s, System.Text.Encoding.UTF8, true))
                    return Manifest.Parse(r.ReadToEnd());
        throw new InvalidOperationException("找不到 manifest");
    }

    private static Payload LoadPayloadFromFile(string zipPath)
    {
        // Payload.Open() 读的是程序集资源；测试时把 zip 路径暴露成同样的接口
        return Payload.OpenFromFile(zipPath);
    }

    private static void Check(string name, bool ok, string detail = null)
    {
        Console.WriteLine("  [{0}] {1}{2}", ok ? "PASS" : "FAIL", name,
            string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")");
        if (!ok) _failed++;
    }

    private static void DumpLog(List<string> log)
    {
        Console.WriteLine("  --- 安装日志 ---");
        foreach (string l in log) Console.WriteLine("    " + l);
    }

    private static void SafeDelete(string dir)
    {
        for (int i = 0; i < 3; i++)
        {
            try { Directory.Delete(dir, true); return; }
            catch { System.Threading.Thread.Sleep(100); }
        }
    }
}
