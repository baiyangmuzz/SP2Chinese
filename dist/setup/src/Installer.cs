using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SP2Chinese.Setup
{
    /// <summary>
    /// 安装/卸载核心。
    ///
    /// 关键约束（都是上一版 PowerShell 脚本踩过坑总结出来的）：
    ///   1. 覆盖写盘之前先验证 payload 自身完整 —— 坏包不能直接把旧版搬走
    ///   2. 校验的期望值只能来自随包 manifest，绝不能用「源文件自身哈希」自证
    ///   3. 旧版先移到 BepInEx\_mod_backups（不能留在 plugins 下，否则会被 BepInEx 二次加载）
    ///   4. 写盘失败要能回滚，不能留下「旧版搬走了、新版只拷一半」
    /// </summary>
    internal sealed class Installer
    {
        internal const string PluginDirName = "SP2Chinese";
        internal const string DictRel = "BepInEx/plugins/SP2Chinese/dictionary_zh_cn.tsv";
        internal const string BuildRel = "BepInEx/plugins/SP2Chinese/dict.build";
        internal const string DllRel = "BepInEx/plugins/SP2Chinese/SP2Chinese.dll";

        /// <summary>manifest 里不列、但需要随包复制的文件（不写入游戏目录语义不同，单独处理）。</summary>
        private static readonly string[] ExtraFiles = { "BepInEx/config/BepInEx.cfg" };

        internal sealed class Outcome
        {
            public bool Success;
            public string Message = "";
            public string InstalledDir = "";
            public string BackupName = "";
            public bool KeptLocalDict;
            /// <summary>没装过 / 已经是最新 —— 不是错误，界面上不该标红。</summary>
            public bool NothingToDo;
            /// <summary>部分非致命问题（例如已有 BepInEx 就跳过了 core）。</summary>
            public readonly List<string> Notes = new List<string>();
        }

        private readonly Payload _payload;
        private readonly Manifest _manifest;
        private readonly Action<int, string> _progress;
        private readonly Action<string> _log;

        internal Installer(Payload payload, Manifest manifest,
                           Action<int, string> progress, Action<string> log)
        {
            _payload = payload;
            _manifest = manifest;
            _progress = progress ?? ((p, s) => { });
            _log = log ?? (s => { });
        }

        // ────────────────────────────────────────────────────────────
        // 安装
        // ────────────────────────────────────────────────────────────

        internal Outcome Install(string gameDir)
        {
            var outcome = new Outcome { InstalledDir = gameDir };
            string pluginDir = Path.Combine(gameDir, "BepInEx", "plugins", PluginDirName);

            try
            {
                _progress(5, "检查安装包完整性…");
                VerifyPayloadIntegrity();

                bool hasBepInEx = File.Exists(Path.Combine(gameDir, "BepInEx", "core", "BepInEx.dll"));
                long pkgBuild = _manifest.DictBuild;
                long installedBuild = ReadBuildFile(pluginDir);
                bool hasDict = File.Exists(Path.Combine(pluginDir, "dictionary_zh_cn.tsv"));

                // 机器上已装的词库比包内新（游戏内热更新过）就保留，不覆盖
                bool keepDict = hasBepInEx && hasDict && installedBuild > pkgBuild;
                outcome.KeptLocalDict = keepDict;
                if (keepDict)
                    _log(string.Format("已装词库 build {0} 比包内 {1} 新，保留", installedBuild, pkgBuild));

                _progress(20, "备份旧版本…");
                string backupPath = BackupExisting(pluginDir, outcome);

                _progress(35, "写入文件…");
                var written = new List<string>();
                try
                {
                    if (!hasBepInEx)
                        WriteBepInExCore(gameDir, written);
                    else
                        outcome.Notes.Add("检测到已安装 BepInEx，沿用现有版本（不改动 core）");

                    WritePlugin(gameDir, written, keepDict, backupPath, pluginDir);

                    _progress(75, "校验写入结果…");
                    VerifyWritten(gameDir, written);
                }
                catch
                {
                    _log("写入失败，正在回滚…");
                    Rollback(pluginDir, backupPath, written);
                    throw;
                }

                PruneHistory(gameDir);

                _progress(95, "收尾…");
                outcome.Success = true;
                outcome.Message = "安装完成";
                if (!string.IsNullOrEmpty(backupPath)) outcome.BackupName = Path.GetFileName(backupPath);
                _progress(100, "完成");
                return outcome;
            }
            catch (Exception ex)
            {
                outcome.Success = false;
                outcome.Message = ex.Message;
                return outcome;
            }
        }

        /// <summary>覆盖之前先按 manifest 校验整个 payload，坏包在这里就拦下，游戏目录完全不动。</summary>
        private void VerifyPayloadIntegrity()
        {
            var bad = new List<string>();
            int i = 0, total = _manifest.Files.Count;
            foreach (var kv in _manifest.Files)
            {
                i++;
                if (i % 4 == 0) _progress(5 + (int)(10.0 * i / Math.Max(1, total)), "校验 " + i + "/" + total);

                if (!_payload.Contains(kv.Key)) { bad.Add(kv.Key + "（包内缺失）"); continue; }
                string expect = kv.Value.Sha256;
                if (string.IsNullOrEmpty(expect)) { bad.Add(kv.Key + "（清单缺哈希）"); continue; }

                string actual = Payload.Sha256OfBytes(_payload.Read(kv.Key));
                if (!string.Equals(actual, expect, StringComparison.OrdinalIgnoreCase))
                    bad.Add(kv.Key + "（内容不符）");
            }
            if (bad.Count > 0)
                throw new InvalidDataException("安装包已损坏，请重新下载：" + string.Join("；", bad.Take(5)));
        }

        private string BackupExisting(string pluginDir, Outcome outcome)
        {
            if (!Directory.Exists(pluginDir)) return null;
            if (!Directory.EnumerateFileSystemEntries(pluginDir).Any()) return null;

            string backupRoot = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(pluginDir)), "_mod_backups");
            Directory.CreateDirectory(backupRoot);
            string dest = UniqueDir(backupRoot,
                "SP2Chinese-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.Move(pluginDir, dest);
            _log("旧版已备份到 BepInEx\\_mod_backups\\" + Path.GetFileName(dest));
            return dest;
        }

        private void WriteBepInExCore(string gameDir, List<string> written)
        {
            string[] roots =
            {
                "winhttp.dll", "doorstop_config.ini", ".doorstop_version",
                "BepInEx/core/", "BepInEx/config/"
            };

            foreach (var kv in _manifest.Files)
            {
                bool wanted = false;
                foreach (string r in roots)
                {
                    if (r.EndsWith("/") ? kv.Key.StartsWith(r, StringComparison.OrdinalIgnoreCase)
                                        : string.Equals(kv.Key, r, StringComparison.OrdinalIgnoreCase))
                    { wanted = true; break; }
                }
                if (!wanted) continue;

                string dest = Path.Combine(gameDir, kv.Key.Replace('/', '\\'));
                _payload.WriteTo(kv.Key, dest);
                written.Add(dest);
            }
            _log("BepInEx 运行环境已就位");
        }

        private void WritePlugin(string gameDir, List<string> written, bool keepDict,
                                 string backupPath, string pluginDir)
        {
            // dict.build 不在循环里处理：它的值取决于「是否保留本地词库」，
            // 需要在末尾按单一规则写一次，避免先写包内值再覆盖的重复逻辑。
            string[] pluginFiles = { DllRel, DictRel };

            foreach (string rel in pluginFiles)
            {
                string dest = Path.Combine(gameDir, rel.Replace('/', '\\'));

                // 只保留「词库」相关文件。DLL 始终用包内的：
                // 玩家拿旧 exe 重装时，本来就是为了换上新的安装器/插件本体。
                if (keepDict && backupPath != null
                    && !rel.EndsWith("SP2Chinese.dll", StringComparison.OrdinalIgnoreCase))
                {
                    // 从备份里把较新的本地词库捞回来
                    string fromBackup = Path.Combine(backupPath, Path.GetFileName(rel));
                    if (File.Exists(fromBackup))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        if (File.Exists(dest)) { try { File.SetAttributes(dest, FileAttributes.Normal); } catch { } }
                        File.Copy(fromBackup, dest, true);
                        // 记录「这个文件按它自己的来源校验」，而不是按包内 manifest。
                        // 否则「保留本地较新词库」必然被判成内容不符：
                        // 包里声明的是旧哈希，实际写的是本地那份新词库。
                        _keptFromBackup[dest] = Payload.Sha256OfFile(fromBackup);
                        written.Add(dest);
                        continue;
                    }
                }

                if (!_payload.Contains(rel))
                    throw new FileNotFoundException("payload 缺少 " + rel);
                _payload.WriteTo(rel, dest);
                written.Add(dest);
            }

            // ── dict.build ──
            // 保留本地词库时沿用备份里那个更大的 build；否则用包内值。
            // 两种写法都带行尾 LF（manifest 里的哈希是对 "xxx\n" 算的）。
            string buildPath = Path.Combine(gameDir, BuildRel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(buildPath));

            string buildText = _manifest.DictBuild + "\n";
            if (keepDict && backupPath != null)
            {
                string bakBuild = Path.Combine(backupPath, "dict.build");
                if (File.Exists(bakBuild))
                {
                    string local = File.ReadAllText(bakBuild).Trim();
                    if (local.Length > 0) buildText = local + "\n";
                }
            }
            File.WriteAllText(buildPath, buildText);
            written.Add(buildPath);

            // 若这个值是本地来的（不是包内 manifest 声明的），按自身来源校验
            if (keepDict && buildText.Trim() != _manifest.DictBuild.ToString())
                _keptFromBackup[buildPath] = Payload.Sha256OfBytes(
                    System.Text.Encoding.UTF8.GetBytes(buildText));
        }

        /// <summary>保留自备份的文件 -> 它自己的哈希，供 VerifyWritten 按来源校验。</summary>
        private readonly Dictionary<string, string> _keptFromBackup =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>写完后逐个核对哈希。期望值来自 manifest，不用源文件自证。</summary>
        private void VerifyWritten(string gameDir, List<string> written)
        {
            var bad = new List<string>();
            foreach (string dest in written)
            {
                if (!File.Exists(dest)) { bad.Add(Path.GetFileName(dest) + "（缺失）"); continue; }

                // 从备份捞回来的文件按它自己的来源校验（见 WritePlugin 里的说明）
                string ownHash;
                if (_keptFromBackup.TryGetValue(dest, out ownHash))
                {
                    string got = Payload.Sha256OfFile(dest);
                    if (!string.Equals(got, ownHash, StringComparison.OrdinalIgnoreCase))
                        bad.Add(Path.GetFileName(dest) + "（复制损坏）");
                    continue;
                }

                string rel = MakeRelative(gameDir, dest).Replace('\\', '/');
                Manifest.FileHash expect;
                if (!_manifest.Files.TryGetValue(rel, out expect)) continue;   // 非清单文件

                string actual = Payload.Sha256OfFile(dest);
                if (!string.Equals(actual, expect.Sha256, StringComparison.OrdinalIgnoreCase))
                    bad.Add(Path.GetFileName(dest) + "（内容不符）");
            }
            if (bad.Count > 0)
                throw new InvalidDataException("写入后校验失败：" + string.Join("；", bad));
            _log("哈希校验通过（" + written.Count + " 个文件）");
        }

        private void Rollback(string pluginDir, string backupPath, List<string> written)
        {
            try
            {
                foreach (string f in written)
                    try { if (File.Exists(f)) File.Delete(f); } catch { }

                if (backupPath != null && Directory.Exists(backupPath))
                {
                    if (Directory.Exists(pluginDir))
                        try { Directory.Delete(pluginDir, true); } catch { }
                    Directory.Move(backupPath, pluginDir);
                    _log("已恢复安装前的版本");
                }
            }
            catch (Exception ex)
            {
                _log("回滚失败：" + ex.Message + "，请到 BepInEx\\_mod_backups 手动恢复");
            }
        }

        // ────────────────────────────────────────────────────────────
        // 回滚 / 历史
        // ────────────────────────────────────────────────────────────

        internal sealed class HistoryItem
        {
            // 必须是属性而非公开字段：WPF 的 {Binding} 只解析 CLR 属性，
            // 绑到公开字段会静默失败，列表里只剩空的选中条（玩家看到的蓝条无字）。
            // 每个引用类型属性都要给初值：Version 曾因缺初始化为 null，
            // 在 ListHistory 里 item.Version.Length 直接抛 NRE，导致整个列表返回空。
            public string Name { get; set; } = "";
            public string FullPath { get; set; } = "";
            public DateTime Time { get; set; }
            public string Version { get; set; } = "";
            public long Build { get; set; }
            public string Kind { get; set; } = "";
            public string Display { get; set; } = "";
        }

        /// <summary>
        /// 列出可回滚的历史备份。
        ///
        /// 安装备份、卸载备份、回滚前备份都能回滚 —— 它们内容一样，都是完整插件目录。
        /// 卸载备份尤其有用：玩家卸载后想反悔，可以直接从那里恢复。
        /// 按时间倒序（新的在前），并排除明显损坏的（缺 SP2Chinese.dll）。
        /// </summary>
        internal static List<HistoryItem> ListHistory(string gameDir)
        {
            var result = new List<HistoryItem>();
            try
            {
                string root = Path.Combine(gameDir, "BepInEx", "_mod_backups");
                if (!Directory.Exists(root)) return result;

                foreach (DirectoryInfo d in new DirectoryInfo(root).GetDirectories("SP2Chinese-*"))
                {
                    if (!File.Exists(Path.Combine(d.FullName, "SP2Chinese.dll"))) continue;

                    var item = new HistoryItem
                    {
                        Name = d.Name,
                        FullPath = d.FullName,
                        Time = d.LastWriteTime,
                        Build = ReadBuildFile(d.FullName)
                    };

                    if (d.Name.Contains("-uninstalled-")) item.Kind = "卸载备份";
                    else if (d.Name.Contains("-pre-rollback-")) item.Kind = "回滚前备份";
                    else item.Kind = "安装备份";

                    // 版本信息优先从 loaded-build.txt 第一列读
                    string lb = Path.Combine(d.FullName, "loaded-build.txt");
                    if (File.Exists(lb))
                    {
                        try
                        {
                            string first = File.ReadAllText(lb).Split('\t')[0].Trim();
                            if (first.Length > 0) item.Version = "v" + first;
                        }
                        catch { }
                    }
                    if (item.Version.Length == 0)
                        item.Version = item.Build > 0 ? "build " + item.Build : "版本未知";

                    item.Display = string.Format("{0:yyyy-MM-dd HH:mm}   {1}   {2}",
                        item.Time, item.Version, item.Kind);
                    result.Add(item);
                }
                result.Sort((a, b) => b.Time.CompareTo(a.Time));
            }
            catch (Exception ex)
            {
                // 不再静默吞掉：ListHistory 返回空时界面会显示「没有历史备份」，
                // 用户无从判断是「真的没有」还是「读取异常」。把原因暴露给诊断层。
                try
                {
                    File.WriteAllText(
                        Path.Combine(Path.GetTempPath(), "sp2-listhistory-error.txt"),
                        "ListHistory failed: " + ex);
                }
                catch { }
            }
            return result;
        }

        /// <summary>
        /// 回滚到指定备份。
        ///
        /// 关键：回滚本身不能变成不可逆操作 —— 先把当前插件目录挪到
        /// "-pre-rollback-" 备份，再把选中的历史备份复制回来。
        /// 用复制而不是移动，这样选中的那份历史备份仍然留着，可以再次回滚。
        /// </summary>
        internal Outcome RollbackTo(string gameDir, string backupName)
        {
            var outcome = new Outcome { InstalledDir = gameDir };
            string pluginDir = Path.Combine(gameDir, "BepInEx", "plugins", PluginDirName);
            string backupRoot = Path.Combine(gameDir, "BepInEx", "_mod_backups");
            string source = Path.Combine(backupRoot, backupName);

            try
            {
                // 防目录穿越：backupName 必须是 _mod_backups 下的直接子目录名
                if (string.IsNullOrEmpty(backupName) || backupName.Contains("\\") || backupName.Contains("/")
                    || !Directory.Exists(source))
                {
                    outcome.Success = false;
                    outcome.Message = "选中的备份不存在";
                    return outcome;
                }
                if (!File.Exists(Path.Combine(source, "SP2Chinese.dll")))
                {
                    outcome.Success = false;
                    outcome.Message = "选中的备份不完整（缺少 SP2Chinese.dll）";
                    return outcome;
                }

                _progress(25, "备份当前版本…");
                Directory.CreateDirectory(backupRoot);
                if (Directory.Exists(pluginDir))
                {
                    string pre = UniqueDir(backupRoot,
                        "SP2Chinese-pre-rollback-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    Directory.Move(pluginDir, pre);
                    _log("当前版本已备份到 " + Path.GetFileName(pre));
                }

                _progress(60, "恢复选中的版本…");
                CopyDirectory(source, pluginDir);
                _progress(90, "整理备份…");
                PruneHistory(gameDir);

                // 校验恢复结果
                if (!File.Exists(Path.Combine(pluginDir, "SP2Chinese.dll"))
                    || !File.Exists(Path.Combine(pluginDir, "dictionary_zh_cn.tsv")))
                    throw new IOException("回滚后文件不完整");

                outcome.Success = true;
                outcome.Message = "已回滚到 " + backupName;
                _log("已回滚到 " + backupName + "（原版本仍保留在历史备份里）");
                _progress(100, "完成");
                return outcome;
            }
            catch (Exception ex)
            {
                outcome.Success = false;
                outcome.Message = ex.Message;
                return outcome;
            }
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string file in Directory.GetFiles(src))
            {
                string name = Path.GetFileName(file);
                // 诊断日志不跟着回滚（那是当时那次运行产生的，不是版本内容）
                if (name == "untranslated.txt" || name == "font_missing.txt") continue;
                string dest = Path.Combine(dst, name);
                if (File.Exists(dest)) { try { File.SetAttributes(dest, FileAttributes.Normal); } catch { } }
                File.Copy(file, dest, true);
            }
            foreach (string dir in Directory.GetDirectories(src))
                CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }

        // ────────────────────────────────────────────────────────────
        // 卸载 / 历史清理
        // ────────────────────────────────────────────────────────────

        internal Outcome Uninstall(string gameDir)
        {
            var outcome = new Outcome { InstalledDir = gameDir };
            string pluginDir = Path.Combine(gameDir, "BepInEx", "plugins", PluginDirName);
            try
            {
                if (!Directory.Exists(pluginDir))
                {
                    // 「本来就没装」是正常状态，不是失败
                    outcome.Success = true;
                    outcome.NothingToDo = true;
                    outcome.Message = "没有安装汉化，无需卸载";
                    return outcome;
                }

                _progress(30, "移动插件目录…");
                string backupRoot = Path.Combine(gameDir, "BepInEx", "_mod_backups");
                Directory.CreateDirectory(backupRoot);
                string dest = UniqueDir(backupRoot,
                    "SP2Chinese-uninstalled-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.Move(pluginDir, dest);

                // 卸载也清理一次：反复「卸载 → 重装」会不断堆积备份
                PruneHistory(gameDir);

                outcome.Success = true;
                outcome.Message = "已卸载（移入 BepInEx\\_mod_backups，没有删除）";
                _log("插件目录已移到 " + Path.GetFileName(dest));
                _progress(100, "完成");
                return outcome;
            }
            catch (Exception ex)
            {
                outcome.Success = false;
                outcome.Message = ex.Message;
                return outcome;
            }
        }

        /// <summary>
        /// 清理历史备份。
        ///
        /// 分三类处理，不能一刀切：
        ///   · 安装备份（SP2Chinese-xxx）—— 留最近 3 份，用于「回滚到上一版」
        ///   · 卸载备份（-uninstalled-）—— 用户主动卸载留下的，留最近 3 份
        ///     卸载 → 重装 → 再卸载是常见操作，不清理会一直堆积（每份约 900 KB）
        ///   · 回滚前备份（-pre-rollback-）—— 只留最近 2 份
        ///
        /// 都不能删光：清理只是控量，最后几份要留给用户手动找回。
        /// </summary>
        private void PruneHistory(string gameDir)
        {
            try
            {
                string root = Path.Combine(gameDir, "BepInEx", "_mod_backups");
                if (!Directory.Exists(root)) return;

                var all = new DirectoryInfo(root).GetDirectories("SP2Chinese-*")
                    .OrderByDescending(d => d.LastWriteTime)
                    .ToList();

                PruneKind(all, "-uninstalled-", 3);
                PruneKind(all, "-pre-rollback-", 2);

                // 其余（普通安装备份）留 3 份
                var installs = all.Where(d =>
                    !d.Name.Contains("-uninstalled-") && !d.Name.Contains("-pre-rollback-"))
                    .ToList();
                foreach (var d in installs.Skip(3))
                    try { d.Delete(true); } catch { }
            }
            catch { }
        }

        private static void PruneKind(List<DirectoryInfo> all, string marker, int keep)
        {
            var list = all.Where(d => d.Name.Contains(marker)).ToList();
            foreach (var d in list.Skip(keep))
                try { d.Delete(true); } catch { }
        }

        /// <summary>
        /// 生成不冲突的备份目录名。
        ///
        /// 备份名用「秒级时间戳」，同一秒内连续操作（自动化测试、快速装卸载）
        /// 会撞名，Directory.Move 抛「当文件已存在时，无法创建该文件」。
        /// 这里在冲突时追加 -2、-3……直到不重名。
        /// </summary>
        private static string UniqueDir(string root, string baseName)
        {
            string path = Path.Combine(root, baseName);
            if (!Directory.Exists(path) && !File.Exists(path)) return path;

            for (int i = 2; i < 1000; i++)
            {
                path = Path.Combine(root, baseName + "-" + i);
                if (!Directory.Exists(path) && !File.Exists(path)) return path;
            }
            // 极端情况：用完整时间戳兜底
            return Path.Combine(root, baseName + "-" + DateTime.Now.ToString("fffffff"));
        }

        internal static long ReadBuildFile(string pluginDir)
        {
            try
            {
                string p = Path.Combine(pluginDir, "dict.build");
                if (!File.Exists(p)) return 0;
                long v;
                return long.TryParse(File.ReadAllText(p).Trim(), out v) ? v : 0;
            }
            catch { return 0; }
        }

        private static string MakeRelative(string baseDir, string full)
        {
            string b = baseDir.TrimEnd('\\') + "\\";
            return full.StartsWith(b, StringComparison.OrdinalIgnoreCase) ? full.Substring(b.Length) : full;
        }
    }
}
