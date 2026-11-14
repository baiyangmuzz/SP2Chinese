using System;
using System.IO;
using SP2Chinese.Setup;

/// <summary>
/// 复现并验证「旧安装包 + 机器上已有更新词库」这个场景。
///
/// 这是真实用户会遇到的：用旧版 exe 重装，但游戏内已经把词库热更新到新版。
/// 正确行为是「保留较新的本地词库」，而不是拿旧包的 manifest 哈希去校验它
/// 然后报「内容不符」并回滚。
///
/// 之前的 bug 正是这个：keepDict 从备份捞回新词库，VerifyWritten 却按旧包
/// manifest 里的旧哈希校验，必然失败。
/// </summary>
internal static class KeepDictTest
{
    private static int _failed;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length < 3)
        {
            Console.WriteLine("用法: KeepDictTest <旧payload.zip> <旧manifest> <新词库文件>");
            return 2;
        }

        string oldZip = args[0], oldManifest = args[1], newDict = args[2];
        Manifest m = Manifest.Load(oldManifest);
        Payload p = Payload.OpenFromFile(oldZip);

        Console.WriteLine("=== 场景：旧包 + 机器上已有更新词库 ===");
        Console.WriteLine("旧包声明: build " + m.DictBuild + ", dict " + m.DictSha256.Substring(0, 12));
        string newDictSha = Payload.Sha256OfFile(newDict);
        Console.WriteLine("本地新词库: " + newDictSha.Substring(0, 12) + " (" + new FileInfo(newDict).Length + " 字节)");
        Console.WriteLine();

        string game = Path.Combine(Path.GetTempPath(), "sp2keep-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Path.Combine(game, "SimplePlanes 2_Data"));
        File.WriteAllText(Path.Combine(game, "SimplePlanes 2.exe"), "stub");

        try
        {
            // 1) 先模拟「已装了更新词库」：写插件目录 + 更大的 dict.build
            //    关键：必须造 BepInEx/core/BepInEx.dll，否则 Installer 会判定为
            //    「没装 BepInEx」而走全新安装，keepDict 分支根本不会触发。
            string pluginDir = Path.Combine(game, "BepInEx/plugins/SP2Chinese");
            string coreDir = Path.Combine(game, "BepInEx/core");
            Directory.CreateDirectory(pluginDir);
            Directory.CreateDirectory(coreDir);
            File.WriteAllText(Path.Combine(coreDir, "BepInEx.dll"), "EXISTING-BEPINEX");
            File.Copy(newDict, Path.Combine(pluginDir, "dictionary_zh_cn.tsv"), true);
            File.WriteAllText(Path.Combine(pluginDir, "dict.build"),
                (m.DictBuild + 100000).ToString() + "\n");
            File.WriteAllText(Path.Combine(pluginDir, "SP2Chinese.dll"), "OLD-DLL-PLACEHOLDER");

            Console.WriteLine("-- 用旧包安装（应保留较新词库，不报校验失败）--");
            var inst = new Installer(p, m, (x, s) => { }, s => Console.WriteLine("   " + s));
            Installer.Outcome o = inst.Install(game);

            Check("安装成功", o.Success, o.Message);
            Check("标记了保留本地词库", o.KeptLocalDict);

            string dict = Path.Combine(pluginDir, "dictionary_zh_cn.tsv");
            Check("词库仍然存在", File.Exists(dict));
            if (File.Exists(dict))
            {
                string sha = Payload.Sha256OfFile(dict);
                Check("保留的是较新的本地词库", sha == newDictSha, sha.Substring(0, 12));
            }

            string bp = Path.Combine(pluginDir, "dict.build");
            if (File.Exists(bp))
            {
                string txt = File.ReadAllText(bp).Trim();
                Check("dict.build 跟随本地较大值", txt == (m.DictBuild + 100000).ToString(), txt);
            }

            // DLL 应该被换成本包内的（这是升级安装器时该做的）
            string dll = Path.Combine(pluginDir, "SP2Chinese.dll");
            Check("插件 DLL 已更新", File.Exists(dll) && new FileInfo(dll).Length > "OLD-DLL-PLACEHOLDER".Length);
        }
        finally
        {
            try { Directory.Delete(game, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "RESULT: ALL PASSED" : "RESULT: " + _failed + " FAILED");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = null)
    {
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + name +
                          (string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")"));
        if (!ok) _failed++;
    }
}
