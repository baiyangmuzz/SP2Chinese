using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace SP2Chinese.Setup
{
    /// <summary>
    /// exe 内置自测：在临时目录里模拟一个游戏目录，跑完整安装 → 校验 → 卸载。
    ///
    /// 为什么要有它：单看「能编译、能开窗」不足以证明发布包可用 ——
    /// 真正会出问题的是 payload 与 manifest 对不上、dict.build 换行符不对、
    /// 已经有 BepInEx 时覆盖了别人的文件。这些只有真跑一遍才知道。
    ///
    /// 用法：SP2Chinese-Setup.exe --selftest &lt;输出日志路径&gt;
    /// 退出码 0 表示全部通过。
    /// </summary>
    internal static class SelfTest
    {
        private static int _failed;
        private static readonly List<string> Out = new List<string>();

        internal static int Run(string logPath)
        {
            try
            {
                Line("=== SP2Chinese 安装器自测 ===");
                Line("exe: " + Assembly.GetExecutingAssembly().Location);

                Manifest manifest = LoadManifest();
                Payload payload = Payload.Open();
                Line(string.Format("manifest: v{0}, {1} 条, build {2}, {3} 个文件",
                    manifest.ModVersion, manifest.DictRows, manifest.DictBuild, manifest.Files.Count));

                TestFreshInstall(payload, manifest);
                TestExistingBepInEx(payload, manifest);
                TestUninstall(payload, manifest);
                TestTampered(payload, manifest);

                Line("");
                Line(_failed == 0 ? "RESULT: ALL PASSED" : "RESULT: " + _failed + " FAILED");
            }
            catch (Exception ex)
            {
                _failed++;
                Line("!!! 自测异常中断：" + ex);
            }

            try { File.WriteAllLines(logPath, Out.ToArray(), new System.Text.UTF8Encoding(false)); } catch { }
            return _failed == 0 ? 0 : 1;
        }

        private static void TestFreshInstall(Payload payload, Manifest manifest)
        {
            Line("");
            Line("-- 场景 1：全新安装 --");
            string game = NewFakeGame();
            try
            {
                var inst = new Installer(payload, manifest, (p, s) => { }, s => Line("   " + s));
                Installer.Outcome o = inst.Install(game);

                Check("安装成功", o.Success, o.Message);
                if (!o.Success) return;

                Check("词典存在", File.Exists(Path.Combine(game, "BepInEx/plugins/SP2Chinese/dictionary_zh_cn.tsv")));
                Check("插件 DLL 存在", File.Exists(Path.Combine(game, "BepInEx/plugins/SP2Chinese/SP2Chinese.dll")));
                Check("BepInEx core 就位", File.Exists(Path.Combine(game, "BepInEx/core/BepInEx.dll")));
                Check("winhttp 就位", File.Exists(Path.Combine(game, "winhttp.dll")));

                string dict = Path.Combine(game, "BepInEx/plugins/SP2Chinese/dictionary_zh_cn.tsv");
                if (File.Exists(dict))
                {
                    string sha = Payload.Sha256OfFile(dict);
                    Check("词典哈希匹配", string.Equals(sha, manifest.DictSha256, StringComparison.OrdinalIgnoreCase));
                }

                string bp = Path.Combine(game, "BepInEx/plugins/SP2Chinese/dict.build");
                if (File.Exists(bp))
                    Check("dict.build 与 manifest 一致",
                        File.ReadAllText(bp).Trim() == manifest.DictBuild.ToString());
            }
            finally { SafeDelete(game); }
        }

        private static void TestExistingBepInEx(Payload payload, Manifest manifest)
        {
            Line("");
            Line("-- 场景 2：已装 BepInEx（不能覆盖别人的）--");
            string game = NewFakeGame();
            try
            {
                string core = Path.Combine(game, "BepInEx/core");
                Directory.CreateDirectory(core);
                File.WriteAllText(Path.Combine(core, "BepInEx.dll"), "EXISTING-BEPINEX-DO-NOT-TOUCH");
                File.WriteAllText(Path.Combine(core, "OtherMod.dll"), "keep-me");

                var inst = new Installer(payload, manifest, (p, s) => { }, s => { });
                Installer.Outcome o = inst.Install(game);
                Check("安装成功", o.Success, o.Message);
                if (!o.Success) return;

                Check("未覆盖已有 BepInEx",
                    File.ReadAllText(Path.Combine(core, "BepInEx.dll")) == "EXISTING-BEPINEX-DO-NOT-TOUCH");
                Check("别的模组保留", File.Exists(Path.Combine(core, "OtherMod.dll")));
                Check("汉化仍装上", File.Exists(Path.Combine(game, "BepInEx/plugins/SP2Chinese/SP2Chinese.dll")));
            }
            finally { SafeDelete(game); }
        }

        private static void TestUninstall(Payload payload, Manifest manifest)
        {
            Line("");
            Line("-- 场景 3：卸载 --");
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

                bool hasBackup = false;
                string backups = Path.Combine(game, "BepInEx/_mod_backups");
                if (Directory.Exists(backups))
                    foreach (string d in Directory.GetDirectories(backups))
                        if (Path.GetFileName(d).Contains("uninstalled")) hasBackup = true;
                Check("卸载备份存在", hasBackup);
            }
            finally { SafeDelete(game); }
        }

        private static void TestTampered(Payload payload, Manifest manifest)
        {
            Line("");
            Line("-- 场景 4：坏包必须被拒绝 --");
            string game = NewFakeGame();
            try
            {
                // 构造一个条目不匹配的 payload：拿真 payload 的条目表，但让哈希校验失败
                // （不改动真实资源，直接用一个空 Payload 模拟「文件对不上」）
                var badPayload = Payload.Open();
                // 篡改期望哈希：把 manifest 里某条目的哈希改掉，等价于包被改坏
                var badManifest = Manifest.Parse(
                    "{\"modVersion\":\"x\",\"dictBuild\":1,\"dictRows\":1,\"dictSha256\":\"deadbeef\"," +
                    "\"files\":{\"BepInEx/plugins/SP2Chinese/SP2Chinese.dll\":{\"sha256\":\"" +
                    new string('0', 64) + "\",\"size\":1}}}");

                var inst = new Installer(badPayload, badManifest, (p, s) => { }, s => { });
                Installer.Outcome o = inst.Install(game);

                Check("坏包被拒绝", !o.Success);
                Check("未写入插件目录", !Directory.Exists(Path.Combine(game, "BepInEx/plugins/SP2Chinese")));
                Check("未写入 core", !File.Exists(Path.Combine(game, "BepInEx/core/BepInEx.dll")));
            }
            finally { SafeDelete(game); }
        }

        // ────────────────────────────────────────────────────────────

        private static string NewFakeGame()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "sp2selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(root, "SimplePlanes 2_Data"));
            File.WriteAllText(Path.Combine(root, "SimplePlanes 2.exe"), "stub");
            return root;
        }

        private static Manifest LoadManifest()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string n in asm.GetManifestResourceNames())
                if (n.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                    using (Stream s = asm.GetManifestResourceStream(n))
                    using (var r = new StreamReader(s, System.Text.Encoding.UTF8, true))
                        return Manifest.Parse(r.ReadToEnd());
            throw new InvalidOperationException("缺少内嵌 manifest");
        }

        private static void Check(string name, bool ok, string detail = null)
        {
            Line("  [" + (ok ? "PASS" : "FAIL") + "] " + name + (string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")"));
            if (!ok) _failed++;
        }

        private static void Line(string s) { Out.Add(s); }

        private static void SafeDelete(string dir)
        {
            for (int i = 0; i < 3; i++)
            {
                try { Directory.Delete(dir, true); return; }
                catch { System.Threading.Thread.Sleep(100); }
            }
        }
    }
}
