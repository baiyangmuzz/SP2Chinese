using System;
using System.Collections.Generic;
using System.IO;
using SP2Chinese.Setup;

/// <summary>
/// 回滚功能测试。
///
/// 验证：
///   1. ListHistory 能列出所有类型的备份，按时间倒序，过滤损坏的
///   2. 回滚后插件目录内容 == 选中备份的内容
///   3. 回滚会把「回滚前」的当前版本先备份（可逆）
///   4. 选中的历史备份本身仍然保留（可以继续回滚）
///   5. 非法备份名（目录穿越）必须被拒绝
/// </summary>
internal static class RollbackTest
{
    private static int _failed;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length < 2)
        {
            Console.WriteLine("用法: RollbackTest <payload.zip> <manifest.json>");
            return 2;
        }

        Manifest m = Manifest.Load(args[1]);
        Payload p = Payload.OpenFromFile(args[0]);
        var inst = new Installer(p, m, (x, s) => { }, s => { });

        string game = Path.Combine(Path.GetTempPath(), "sp2rb-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Path.Combine(game, "SimplePlanes 2_Data"));
        File.WriteAllText(Path.Combine(game, "SimplePlanes 2.exe"), "stub");

        try
        {
            // 构造三份历史备份，内容各不相同，模拟「装过三个不同版本」
            string root = Path.Combine(game, "BepInEx", "_mod_backups");
            Directory.CreateDirectory(root);
            MakeBackup(root, "SP2Chinese-20261101-120000", "v1", "DICT-V1", 101376);
            MakeBackup(root, "SP2Chinese-20261102-120000", "v2", "DICT-V2", 109568);
            MakeBackup(root, "SP2Chinese-uninstalled-20261103-120000", "v3", "DICT-V3", 111111);
            // 一份损坏的（缺 DLL），不应出现在列表里
            Directory.CreateDirectory(Path.Combine(root, "SP2Chinese-20261104-999999"));

            Console.WriteLine("=== 场景 1：列出历史 ===");
            List<Installer.HistoryItem> hist = Installer.ListHistory(game);
            Check("列出 3 份有效备份", hist.Count == 3, "实际 " + hist.Count);
            Check("过滤掉损坏备份", !hist.Exists(h => h.Name.Contains("999999")));
            Check("按时间倒序", hist.Count >= 2 && hist[0].Time >= hist[1].Time,
                  hist.Count > 0 ? hist[0].Name : "");
            Check("识别卸载备份", hist.Exists(h => h.Kind == "卸载备份"));
            Check("DisplayName 有内容", hist.TrueForAll(h => h.Display.Length > 0));

            foreach (var h in hist) Console.WriteLine("   " + h.Display);

            // 当前「已安装」状态
            string pluginDir = Path.Combine(game, "BepInEx", "plugins", "SP2Chinese");
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(Path.Combine(pluginDir, "SP2Chinese.dll"), "CURRENT-DLL");
            File.WriteAllText(Path.Combine(pluginDir, "dictionary_zh_cn.tsv"), "CURRENT-DICT");
            File.WriteAllText(Path.Combine(pluginDir, "dict.build"), "999\n");

            Console.WriteLine();
            Console.WriteLine("=== 场景 2：回滚到 v2 安装备份 ===");
            string target = "SP2Chinese-20261102-120000";
            Installer.Outcome o = inst.RollbackTo(game, target);
            Check("回滚成功", o.Success, o.Message);
            Check("词库已换成目标版本", ReadText(pluginDir, "dictionary_zh_cn.tsv") == "DICT-V2",
                  ReadText(pluginDir, "dictionary_zh_cn.tsv"));
            Check("DLL 已换成目标版本", ReadText(pluginDir, "SP2Chinese.dll") == "V2-DLL",
                  ReadText(pluginDir, "SP2Chinese.dll"));
            Check("目标备份本身仍保留", Directory.Exists(Path.Combine(root, target)));
            Check("回滚前状态已备份", HasPreRollbackBackup(root));
            Check("诊断日志不跟着回滚", !File.Exists(Path.Combine(pluginDir, "untranslated.txt")));

            Console.WriteLine();
            Console.WriteLine("=== 场景 3：回滚到卸载备份 ===");
            Installer.Outcome o2 = inst.RollbackTo(game, "SP2Chinese-uninstalled-20261103-120000");
            Check("卸载备份也能回滚", o2.Success, o2.Message);
            Check("词库是 v3", ReadText(pluginDir, "dictionary_zh_cn.tsv") == "DICT-V3");

            Console.WriteLine();
            Console.WriteLine("=== 场景 4：非法备份名必须拒绝 ===");
            Installer.Outcome o3 = inst.RollbackTo(game, "..\\..\\evil");
            Check("目录穿越被拒绝", !o3.Success, o3.Message);
            Installer.Outcome o4 = inst.RollbackTo(game, "not-exist");
            Check("不存在的备份被拒绝", !o4.Success, o4.Message);
        }
        finally
        {
            try { Directory.Delete(game, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "RESULT: ALL PASSED" : "RESULT: " + _failed + " FAILED");
        return _failed == 0 ? 0 : 1;
    }

    private static void MakeBackup(string root, string name, string ver, string dict, int dllSize)
    {
        string d = Path.Combine(root, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "SP2Chinese.dll"), ver.ToUpper() + "-DLL");
        File.WriteAllText(Path.Combine(d, "dictionary_zh_cn.tsv"), dict);
        File.WriteAllText(Path.Combine(d, "dict.build"), ver.Replace("v", "") + "000\n");
        File.WriteAllText(Path.Combine(d, "loaded-build.txt"), ver.Replace("v", "") + "\t2026-01-01\n");
        File.WriteAllText(Path.Combine(d, "untranslated.txt"), "should not be restored");
        System.Threading.Thread.Sleep(30);   // 保证时间戳有区分
    }

    private static string ReadText(string dir, string name)
    {
        string p = Path.Combine(dir, name);
        return File.Exists(p) ? File.ReadAllText(p) : "<missing>";
    }

    private static bool HasPreRollbackBackup(string root)
    {
        if (!Directory.Exists(root)) return false;
        foreach (string d in Directory.GetDirectories(root))
            if (Path.GetFileName(d).Contains("-pre-rollback-")) return true;
        return false;
    }

    private static void Check(string name, bool ok, string detail = null)
    {
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + name +
                          (string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")"));
        if (!ok) _failed++;
    }
}
