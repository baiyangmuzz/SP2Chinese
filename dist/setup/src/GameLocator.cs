using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SP2Chinese.Setup
{
    /// <summary>
    /// 找到 SimplePlanes 2 的安装目录。
    ///
    /// 顺序：Steam 注册表 -> 所有 Steam 库（libraryfolders.vdf）-> 各盘常见路径。
    /// 全部失败则交给调用方弹文件夹选择框，再不行让玩家手输。
    /// 判定标准是同时存在 SimplePlanes 2.exe 与 SimplePlanes 2_Data 目录，
    /// 只认一个的话容易误判成 Data 子目录或 common 父目录。
    /// </summary>
    internal static class GameLocator
    {
        private const string FolderName = "SimplePlanes 2";

        internal static bool IsGameDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try
            {
                return File.Exists(Path.Combine(dir, "SimplePlanes 2.exe"))
                    && Directory.Exists(Path.Combine(dir, "SimplePlanes 2_Data"));
            }
            catch { return false; }
        }

        /// <summary>返回找到的游戏目录，找不到返回 null。</summary>
        internal static string Find()
        {
            foreach (string lib in GetSteamLibraries())
            {
                string candidate = Path.Combine(lib, "steamapps", "common", FolderName);
                if (IsGameDir(candidate)) return candidate;
            }

            foreach (DriveInfo drive in SafeDrives())
            {
                foreach (string rel in new[]
                {
                    @"steamapps\common\" + FolderName,
                    @"SteamLibrary\steamapps\common\" + FolderName,
                    @"Steam\steamapps\common\" + FolderName,
                    @"Games\" + FolderName,
                    FolderName
                })
                {
                    string candidate;
                    try { candidate = Path.Combine(drive.RootDirectory.FullName, rel); }
                    catch { continue; }
                    if (IsGameDir(candidate)) return candidate;
                }
            }
            return null;
        }

        private static IEnumerable<DriveInfo> SafeDrives()
        {
            DriveInfo[] all;
            try { all = DriveInfo.GetDrives(); }
            catch { yield break; }

            foreach (DriveInfo d in all)
            {
                bool usable;
                try { usable = d.DriveType == DriveType.Fixed && d.IsReady; }
                catch { usable = false; }
                if (usable) yield return d;
            }
        }

        /// <summary>Steam 库列表：先读注册表里的 SteamPath，再解析每个库的 libraryfolders.vdf。</summary>
        internal static List<string> GetSteamLibraries()
        {
            var libs = new List<string>();
            string steamRoot = ReadSteamPathFromRegistry();
            if (!string.IsNullOrEmpty(steamRoot)) AddLibrary(libs, steamRoot);

            // 常见安装位置兜底（注册表被清理过时仍能找到）
            foreach (string guess in new[]
            {
                @"C:\Program Files (x86)\Steam",
                @"C:\Program Files\Steam",
                @"D:\Steam", @"D:\steam", @"E:\Steam", @"E:\steam"
            })
            {
                if (Directory.Exists(guess)) AddLibrary(libs, guess);
            }

            // 展开 libraryfolders.vdf 里登记的所有库
            var expanded = new List<string>(libs);
            foreach (string steam in libs.ToArray())
            {
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (string p in ParseLibraryFolders(vdf))
                    if (!ContainsIgnoreCase(expanded, p)) expanded.Add(p);
            }
            return expanded;
        }

        private static void AddLibrary(List<string> libs, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                path = path.TrimEnd('\\', '/');
                if (!ContainsIgnoreCase(libs, path)) libs.Add(path);
            }
            catch { }
        }

        /// <summary>.NET Framework 的 List&lt;string&gt; 没有带比较器的 Contains，这里手写一个。</summary>
        private static bool ContainsIgnoreCase(List<string> list, string value)
        {
            foreach (string s in list)
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ReadSteamPathFromRegistry()
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (string sub in new[]
                {
                    @"SOFTWARE\Valve\Steam",
                    @"SOFTWARE\WOW6432Node\Valve\Steam"
                })
                {
                    try
                    {
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                        using (RegistryKey k = baseKey.OpenSubKey(sub))
                        {
                            string p = k == null ? null : k.GetValue("SteamPath") as string;
                            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p.Replace('/', '\\');
                        }
                    }
                    catch { }

                    try
                    {
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                        using (RegistryKey k = baseKey.OpenSubKey(sub))
                        {
                            string p = k == null ? null : k.GetValue("InstallPath") as string;
                            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p.Replace('/', '\\');
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>从 vdf 里抓所有 "path" "X:\\..." 的值（正则足够，不值得写完整 vdf 解析器）。</summary>
        private static IEnumerable<string> ParseLibraryFolders(string vdf)
        {
            string text;
            try { text = File.ReadAllText(vdf, Encoding.UTF8); }
            catch { yield break; }

            foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            {
                string p = m.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(p)) yield return p.TrimEnd('\\');
            }
        }
    }
}
