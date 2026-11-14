using System;
using System.IO;
using SP2Chinese.Setup;

class Cycle {
    static void Main(string[] a) {
        string zip = a[0], mf = a[1];
        Manifest m = Manifest.Load(mf);
        Payload p = Payload.OpenFromFile(zip);

        string game = Path.Combine(Path.GetTempPath(), "sp2cycle-" + Guid.NewGuid().ToString("N").Substring(0,8));
        Directory.CreateDirectory(Path.Combine(game, "SimplePlanes 2_Data"));
        File.WriteAllText(Path.Combine(game, "SimplePlanes 2.exe"), "stub");

        var inst = new Installer(p, m, (x,s)=>{}, s=>{});
        int fail = 0;

        for (int i = 1; i <= 5; i++) {
            var r1 = inst.Install(game);
            if (!r1.Success) { Console.WriteLine("第" + i + "轮安装失败: " + r1.Message); fail++; break; }
            bool installed = File.Exists(Path.Combine(game, "BepInEx/plugins/SP2Chinese/SP2Chinese.dll"));

            var r2 = inst.Uninstall(game);
            if (!r2.Success) { Console.WriteLine("第" + i + "轮卸载失败: " + r2.Message); fail++; break; }
            bool removed = !Directory.Exists(Path.Combine(game, "BepInEx/plugins/SP2Chinese"));

            string backups = Path.Combine(game, "BepInEx/_mod_backups");
            int inst1 = 0, unin = 0;
            if (Directory.Exists(backups))
                foreach (var d in Directory.GetDirectories(backups)) {
                    string n = Path.GetFileName(d);
                    if (n.Contains("-uninstalled-")) unin++;
                    else if (!n.Contains("-pre-rollback-")) inst1++;
                }
            long bytes = 0;
            if (Directory.Exists(backups))
                foreach (var f in Directory.GetFiles(backups, "*", SearchOption.AllDirectories))
                    bytes += new FileInfo(f).Length;

            Console.WriteLine(string.Format("第{0}轮: 安装={1} 卸载={2} 安装备份={3} 卸载备份={4} 占用={5:N0} KB",
                i, installed, removed, inst1, unin, bytes/1024));

            if (!installed || !removed) { fail++; break; }
        }

        // 验证重装内容正确（不依赖卸载备份）
        var r = inst.Install(game);
        string dict = Path.Combine(game, "BepInEx/plugins/SP2Chinese/dictionary_zh_cn.tsv");
        bool ok = r.Success && File.Exists(dict) && Payload.Sha256OfFile(dict) == m.DictSha256;
        Console.WriteLine("重装内容正确: " + ok);
        if (!ok) fail++;

        try { Directory.Delete(game, true); } catch {}
        Console.WriteLine(fail == 0 ? "RESULT: PASS" : "RESULT: FAIL");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
