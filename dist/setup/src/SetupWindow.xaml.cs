using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SP2Chinese.Setup
{
    /// <summary>
    /// 主窗口（WPF）。
    ///
    /// 相比 WinForms 版的关键差异：
    ///   · 无边框窗口 + 整窗圆角 + 柔和阴影（现代桌面的通行做法）
    ///   · 矢量渲染，任意缩放下文字都清晰
    ///   · 进度条与状态条带动画，不是瞬间跳变
    ///   · 单色系三层背景（窗口 / 卡片 / 日志），有明确层级感
    /// </summary>
    public partial class SetupWindow : Window
    {
        private readonly Payload _payload;
        private readonly Manifest _manifest;

        private string _gameDir;
        private bool _busy;
        private double _progress;

        internal SetupWindow(Payload payload, Manifest manifest)
        {
            _payload = payload;
            _manifest = manifest;
            InitializeComponent();

            // 两行信息要分清：
            //   · v1.6.3 是这个 exe 自身的版本，词库更新不会替换安装器/DLL，永远不变
            //   · build 是「当前 exe 打包时带的词库版本」，装完可能已被换成更新的
            // 所以更新成功后要刷新这一行，并明确标出「已更新」，否则用户会以为没生效。
            RefreshSubtitle(null);
            DictInfoRun.Text = _manifest.DictRows + " 条 · Build " + _manifest.DictBuild;

            InstallButton.Click += async (s, e) => await DoInstallAsync();
            UpdateButton.Click += async (s, e) => await DoUpdateCheckAsync();
            RollbackButton.Click += (s, e) => DoRollback();
            UninstallButton.Click += async (s, e) => await DoUninstallAsync();
            SelfUpdateButton.Click += async (s, e) => await DoSelfUpdateAsync();
            CloseButton.Click += (s, e) => { if (!_busy) Close(); };

            // 无边框窗口：Header 拖动 + 双击最大化由 XAML 的
            // MouseLeftButtonDown="HeaderGrid_OnMouseLeftButtonDown" 注册。
            // 这里不能再 +=（会一次点击触发两次 DragMove，第二次鼠标已松开会抛异常）。

            // 无边框窗口：整个 Header 区域都可拖动（日志区滚动不能抢拖动，
            // 所以不用整窗 MouseLeftButtonDown，只认 Header 内的空白区）。
            // 简化实现：标题文字与徽标所在的 Grid 命中测试即可。
        }

        /// <summary>
        /// 刷新顶部副标题。
        ///
        /// installedBuild 为 null 时显示「这个 exe 自带」的词库版本；
        /// 传入实际值说明词库已被更新，需要如实显示并标注，避免用户看到旧 build
        /// 以为更新没生效（旧版 exe 更新词库后顶部仍显示 202611011750 的 bug）。
        /// </summary>
        private void RefreshSubtitle(long? installedBuild)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => RefreshSubtitle(installedBuild)));
                return;
            }

            long builtIn = _manifest.DictBuild;
            if (!installedBuild.HasValue || installedBuild.Value == builtIn)
            {
                DictInfoRun.Text = _manifest.DictRows + " 条 · Build " + builtIn;
            }
            else
            {
                DictInfoRun.Text = _manifest.DictRows + " 条 · Build " + installedBuild.Value
                    + "（已更新，原 " + builtIn + "）";
            }
        }

        protected override async void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            AppendLog("欢迎使用 SimplePlanes 2 简体中文汉化安装程序");
            await LocateGameDirAsync();
        }

        // ────────────────────────────────────────────────────────────
        // 找游戏目录
        // ────────────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task LocateGameDirAsync()
        {
            SetBusy(true, "正在查找游戏目录…", "Muted");
            string found = await System.Threading.Tasks.Task.Run(() => GameLocator.Find());
            SetBusy(false);

            if (found != null) { ApplyGameDir(found); return; }

            SetStatus("没能自动找到游戏目录", "Warn");
            AppendLog("没能自动找到游戏目录，请手动选择。");
            ChooseGameDirManually();
        }

        private void ApplyGameDir(string dir)
        {
            _gameDir = dir;
            SetStatus("已找到游戏目录，可以开始安装", "Ok");
            PathText.Text = dir;
            AppendLog("游戏目录：" + dir);
            InstallButton.IsEnabled = true;
        }

        /// <summary>
        /// 用 Win32 SHBrowseForFolder 选目录（不依赖 WinForms，避免多引一个 UI 框架）。
        /// 取消或失败则让玩家走「手动输入」路径。
        /// </summary>
        private void ChooseGameDirManually()
        {
            string picked = PickFolder("请选择 SimplePlanes 2 的安装文件夹（里面应有 SimplePlanes 2.exe）");

            if (picked != null)
            {
                if (GameLocator.IsGameDir(picked)) { ApplyGameDir(picked); return; }
                string fixedPath = TryFix(picked);
                if (fixedPath != null) { ApplyGameDir(fixedPath); return; }
                MessageBox.Show(this,
                    "这个文件夹里没有 SimplePlanes 2.exe。\n\n请选择包含它的一层，不是 SimplePlanes 2_Data，也不是 Steam 的 common 文件夹。",
                    "目录不对", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                SetStatus("未选择游戏目录", "Warn");
                AppendLog("已取消选择。可以点「开始安装」重新选择。");
            }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool SHGetPathFromIDList(IntPtr pidl, System.Text.StringBuilder pszPath);

        [System.Runtime.InteropServices.DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct BROWSEINFO
        {
            public IntPtr hwndOwner;
            public IntPtr pidlRoot;
            public IntPtr pszDisplayName;
            public string lpszTitle;
            public uint ulFlags;
            public IntPtr lpfn;
            public IntPtr lParam;
            public int iImage;
        }

        private string PickFolder(string title)
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var bi = new BROWSEINFO
            {
                hwndOwner = hwnd,
                lpszTitle = title,
                ulFlags = 0x00000001 | 0x00000040   // BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE
            };

            IntPtr pidl = SHBrowseForFolder(ref bi);
            if (pidl == IntPtr.Zero) return null;

            try
            {
                var sb = new System.Text.StringBuilder(260);
                if (SHGetPathFromIDList(pidl, sb)) return sb.ToString();
                return null;
            }
            finally { CoTaskMemFree(pidl); }
        }

        private static string TryFix(string path)
        {
            try
            {
                string parent = Path.GetDirectoryName(path.TrimEnd('\\'));
                if (!string.IsNullOrEmpty(parent) && GameLocator.IsGameDir(parent)) return parent;
                foreach (string child in Directory.GetDirectories(path))
                    if (GameLocator.IsGameDir(child)) return child;
            }
            catch { }
            return null;
        }

        // ────────────────────────────────────────────────────────────
        // 安装 / 卸载
        // ────────────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task DoInstallAsync()
        {
            if (_busy) return;
            if (string.IsNullOrEmpty(_gameDir) || !GameLocator.IsGameDir(_gameDir))
            {
                ChooseGameDirManually();
                if (string.IsNullOrEmpty(_gameDir)) return;
            }

            if (IsGameRunning())
            {
                var r = MessageBox.Show(this,
                    "游戏正在运行，安装会覆盖正在使用的文件。\n\n请先在游戏里保存进度并完全退出。\n\n要现在强制关闭游戏吗？",
                    "游戏正在运行", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes && !KillGame()) { AppendLog("关闭游戏失败，请手动退出后重试。"); return; }
            }

            SetBusy(true, "正在安装…", "Accent");
            ResetProgress();
            AppendLog("");
            AppendLog("开始安装：" + _gameDir);

            var installer = new Installer(_payload, _manifest,
                (p, s) => Dispatcher.BeginInvoke(new Action(() => { SetProgress(p); if (s != null) StatusText.Text = s; })),
                s => Dispatcher.BeginInvoke(new Action(() => AppendLog(s))));

            string gameDir = _gameDir;
            // 安装本身在后台线程跑（有磁盘 IO），但回调统一由 Installer 的
            // progress/log 委托转到 UI 线程，不会跨线程碰控件。
            Installer.Outcome outcome = await System.Threading.Tasks.Task.Run(
                () => installer.Install(gameDir));

            foreach (string note in outcome.Notes) AppendLog(note);

            // 词库更新后如实刷新顶部版本号；同时读取安装后游戏目录里真实的
            // dict.build（keepDict 保留本地较新词库时它比包内值大，也要显示出来）
            long? effectiveBuild = null;
            if (outcome.Success)
            {
                long? updated = await ApplyNewerDictAsync(gameDir);
                effectiveBuild = updated ?? Installer.ReadBuildFile(
                    Path.Combine(gameDir, "BepInEx", "plugins", "SP2Chinese"));
            }
            RefreshSubtitle(effectiveBuild);

            SetBusy(false);

            if (outcome.Success)
            {
                SetProgress(100);
                SetStatus("安装完成，可以启动游戏了", "Ok");
                AppendLog("");
                AppendLog("=== 安装完成 ===");
                if (!string.IsNullOrEmpty(outcome.BackupName))
                    AppendLog("旧版备份：BepInEx\\_mod_backups\\" + outcome.BackupName);
                InstallButton.Content = "重新安装";

                // Star 提示只在「真的装好了」之后出现一次，别在打开时就打扰
                ShowStarHint();
            }
            else
            {
                SetStatus("安装未完成", "Err");
                AppendLog("!!! " + outcome.Message);
                MessageBox.Show(this,
                    "安装没有完成：\n\n" + outcome.Message +
                    "\n\n游戏目录已保持安装前状态。如果反复失败，请把日志发给 QQ 群 1128992783。",
                    "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 安装成功后顺带取一次最新词库。
        ///
        /// 注意整个方法在 UI 线程上跑：网络请求本身是 await 的（不阻塞界面），
        /// 而它内部的 AppendLog 就不会与安装阶段的日志回调交错。
        /// </summary>
        /// <returns>更新后的词库 build；没有更新返回 null。</returns>
        private async System.Threading.Tasks.Task<long?> ApplyNewerDictAsync(string gameDir)
        {
            try
            {
                string dictPath = Path.Combine(gameDir, Installer.DictRel.Replace('/', '\\'));
                string pluginDir = Path.GetDirectoryName(dictPath);
                long localBuild = Installer.ReadBuildFile(pluginDir);
                string localSha = File.Exists(dictPath)
                    ? Payload.Sha256OfFile(dictPath).Substring(0, 12) : "";

                Updater.Result up = await Updater.CheckAsync(localBuild, localSha);
                if (!string.IsNullOrEmpty(up.Message)) AppendLog(up.Message);
                if (!up.HasUpdate) return null;

                string tmp = dictPath + ".incoming";
                File.WriteAllBytes(tmp, up.Dictionary);
                if (File.Exists(dictPath)) { try { File.SetAttributes(dictPath, FileAttributes.Normal); } catch { } }
                File.Delete(dictPath);
                File.Move(tmp, dictPath);
                File.WriteAllText(Path.Combine(pluginDir, "dict.build"), up.Build + "\n");
                AppendLog("已顺带更新到最新词库（build " + up.Build + "）");
                AppendLog("注意：词库已更新，但安装器/插件本体仍是 v" + _manifest.ModVersion
                        + "，如需新功能请到 Releases 下载新安装包。");
                return up.Build > 0 ? up.Build : (long?)null;
            }
            catch (Exception ex)
            {
                AppendLog("词库更新跳过：" + ex.Message);
                return null;
            }
        }

        private async System.Threading.Tasks.Task DoUninstallAsync()
        {
            if (_busy) return;
            if (string.IsNullOrEmpty(_gameDir) || !GameLocator.IsGameDir(_gameDir))
            {
                ChooseGameDirManually();
                if (string.IsNullOrEmpty(_gameDir)) return;
            }

            var confirm = MessageBox.Show(this,
                "确定要卸载汉化吗？\n\n插件目录会被移到 BepInEx\\_mod_backups，不会删除文件。\nBepInEx 本体和其它模组不受影响。",
                "卸载汉化", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            SetBusy(true, "正在卸载…", "Warn");
            ResetProgress();

            string gameDir = _gameDir;
            var installer = new Installer(_payload, _manifest,
                (p, s) => Dispatcher.BeginInvoke(new Action(() => SetProgress(p))),
                s => Dispatcher.BeginInvoke(new Action(() => AppendLog(s))));

            Installer.Outcome outcome = await System.Threading.Tasks.Task.Run(() => installer.Uninstall(gameDir));
            SetBusy(false);

            if (outcome.NothingToDo)
            {
                // 本来就没装：中性提示，不该显示成「失败」
                SetProgress(100);
                SetStatus("没有安装汉化，无需卸载", "Muted");
                AppendLog(outcome.Message);
            }
            else if (outcome.Success)
            {
                SetProgress(100);
                SetStatus("已卸载，插件目录保留在 _mod_backups", "Ok");
                AppendLog(outcome.Message);
            }
            else
            {
                SetStatus("卸载未完成", "Err");
                AppendLog("!!! " + outcome.Message);
            }
        }

        /// <summary>
        /// 回滚：列出历史备份 → 让玩家选一份 → 恢复。
        ///
        /// 交互分两步是为了避免误操作：选择窗口里明确显示时间、版本、备份类型，
        /// 确认后才动文件。回滚本身会把当前版本先备份，随时能再滚回来。
        /// </summary>
        private void DoRollback()
        {
            if (_busy) return;
            if (string.IsNullOrEmpty(_gameDir) || !GameLocator.IsGameDir(_gameDir))
            {
                ChooseGameDirManually();
                if (string.IsNullOrEmpty(_gameDir)) return;
            }

            string gameDir = _gameDir;
            var history = Installer.ListHistory(gameDir);

            var dlg = new HistoryWindow(history) { Owner = this };
            bool? picked = dlg.ShowDialog();
            if (picked != true || string.IsNullOrEmpty(dlg.SelectedName)) return;

            string chosen = dlg.SelectedName;
            var confirm = MessageBox.Show(this,
                "确定回滚到以下版本吗？\n\n" + chosen +
                "\n\n当前版本会先自动备份到 _mod_backups，随时可以再滚回来。",
                "确认回滚", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            SetBusy(true, "正在回滚…", "Warn");
            ResetProgress();
            AppendLog("");
            AppendLog("回滚到：" + chosen);

            var installer = new Installer(_payload, _manifest,
                (p, s) => Dispatcher.BeginInvoke(new Action(() => SetProgress(p))),
                s => Dispatcher.BeginInvoke(new Action(() => AppendLog(s))));

            Installer.Outcome outcome = null;
            System.Threading.Tasks.Task.Run(() =>
            {
                outcome = installer.RollbackTo(gameDir, chosen);
            }).ContinueWith(t =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    SetBusy(false);
                    if (outcome != null && outcome.Success)
                    {
                        SetProgress(100);
                        SetStatus("已回滚到 " + chosen, "Ok");
                        AppendLog("=== 回滚完成 ===");
                        // 回滚后词库版本可能变了，如实刷新顶部
                        long real = Installer.ReadBuildFile(
                            Path.Combine(gameDir, "BepInEx", "plugins", "SP2Chinese"));
                        RefreshSubtitle(real > 0 ? real : (long?)null);
                    }
                    else
                    {
                        SetStatus("回滚失败", "Err");
                        AppendLog("!!! " + (outcome == null ? "未知错误" : outcome.Message));
                    }
                }));
            });
        }

        /// <summary>
        /// 检查更新。
        ///
        /// 三类检查：插件本体 / 词库 / 安装器自身。
        /// 收尾必须显式设置状态与进度 —— 否则「已是最新」这条路径会把界面
        /// 停在「正在检查更新…」，用户以为卡死了。
        /// </summary>
        private async System.Threading.Tasks.Task DoUpdateCheckAsync()
        {
            if (_busy) return;

            SetBusy(true, "正在检查更新…", "Accent");
            ResetProgress();
            AppendLog("");
            AppendLog("正在检查更新…");

            string localVersion = _manifest.ModVersion;
            long localBuild = _manifest.DictBuild;
            string localSha = _manifest.DictVersion;

            Updater.RemoteVersion ver = null;
            Updater.Result dict = null;
            Updater.NewInstaller instUpd = null;

            try
            {
                SetProgress(15);
                ver = await Updater.CheckVersionAsync(localVersion);
                SetProgress(40);
                dict = await Updater.CheckAsync(localBuild, localSha);
                SetProgress(65);
                instUpd = await Updater.CheckInstallerUpdateAsync(localVersion);
                SetProgress(85);
            }
            catch (Exception ex)
            {
                SetStatus("检查更新失败", "Err");
                AppendLog("!!! " + ex.Message);
                SetBusy(false);
                return;
            }

            bool dictUpdate = false;
            bool pluginUpdate = false;

            // —— 插件本体 ——
            if (ver != null && ver.Online)
            {
                if (ver.Message.StartsWith("新版"))
                {
                    pluginUpdate = true;
                    AppendLog("插件：" + ver.Message + "（词库安装后已自动带上，无需单独操作）");
                }
                else
                {
                    AppendLog("插件：已是 v" + localVersion + "（最新）");
                }
            }
            else
            {
                AppendLog("插件：无法连接更新服务器（" + (ver == null ? "未知原因" : ver.Message) + "）");
            }

            // —— 词库 ——
            if (dict != null)
            {
                AppendLog("词库：" + dict.Message);
                if (dict.HasUpdate)
                {
                    dictUpdate = true;
                    AppendLog("      词库有更新，点「开始安装」会自动换成新词库（不会重装 BepInEx）");
                }
            }

            // —— 安装器自更新 ——
            string finalStatus = null;
            string finalTone = "Warn";

            if (instUpd != null && instUpd.Available)
            {
                _installerUpdate = instUpd;
                SelfUpdateText.Text = "安装器有新版本 v" + instUpd.Version
                    + "（当前 v" + localVersion + "，"
                    + (instUpd.Size / 1024.0 / 1024.0).ToString("F2") + " MB）";
                SelfUpdateBar.Visibility = Visibility.Visible;
                AppendLog("安装器：" + instUpd.Message);
                AppendLog("      点「立即更新」会下载并自动替换重启。");
                finalStatus = "安装器有新版本 v" + instUpd.Version + "，点「立即更新」";
            }
            else if (instUpd != null && instUpd.Online
                     && !string.IsNullOrEmpty(instUpd.Version)
                     && CompareVersionStrings(instUpd.Version, localVersion) > 0)
            {
                finalStatus = "安装器有新版本 v" + instUpd.Version + "（需手动下载）";
                AppendLog("安装器：" + instUpd.Message);
                AppendLog("      " + instUpd.DownloadUrl);
            }
            else
            {
                if (instUpd != null && !string.IsNullOrEmpty(instUpd.Message))
                    AppendLog("安装器：" + instUpd.Message);
            }

            // —— 收尾：一定要给状态和进度一个明确终值 ——
            SetProgress(100);
            if (finalStatus != null)
            {
                SetStatus(finalStatus, finalTone);
            }
            else if (dictUpdate)
            {
                SetStatus("发现新词库（build " + dict.Build + "），点「开始安装」即可更新", "Warn");
            }
            else if (pluginUpdate)
            {
                SetStatus("发现新版本 v" + (ver != null ? ver.ModVersion : "") + "，点「开始安装」即可更新", "Warn");
            }
            else
            {
                SetStatus("已是最新版本（v" + localVersion + "）", "Ok");
            }

            SetBusy(false);
        }

        private static int CompareVersionStrings(string a, string b)
        {
            Version va, vb;
            if (!Version.TryParse(a, out va)) return 0;
            if (!Version.TryParse(b, out vb)) return 0;
            return va.CompareTo(vb);
        }

        /// <summary>
        /// 「立即更新」：下载新版安装器 → 校验 → 写替换脚本 → 退出。
        /// 脚本会在本进程退出后完成覆盖并重启新版。
        /// </summary>
        private async System.Threading.Tasks.Task DoSelfUpdateAsync()
        {
            if (_busy || _installerUpdate == null || !_installerUpdate.Available) return;

            SetBusy(true, "正在下载新版安装器…", "Accent");
            ResetProgress();
            AppendLog("");
            AppendLog("下载新版安装器 v" + _installerUpdate.Version + " …");

            string path = await Updater.DownloadInstallerAsync(_installerUpdate);
            if (path == null)
            {
                SetBusy(false);
                SetStatus("下载失败，请稍后重试或到 Releases 手动下载", "Err");
                AppendLog("!!! 下载失败：" + _installerUpdate.DownloadUrl);
                return;
            }

            SetProgress(60);
            AppendLog("已下载并校验通过：" + path);
            AppendLog("即将退出并由新版本接管（自动替换、自动重启）……");

            SetProgress(100);
            Updater.ScheduleSelfReplace(path, _installerUpdate.Version);
            // 给新进程一点启动时间，然后退出本进程；新进程会等我们退出后完成覆盖
            await System.Threading.Tasks.Task.Delay(800);
            Application.Current.Shutdown();
        }

        // ────────────────────────────────────────────────────────────
        // 视觉状态
        // ────────────────────────────────────────────────────────────

        private void SetStatus(string text, string tone)
        {
            // 所有 UI 写入都要求 UI 线程；后台回调可能直接调到这里
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SetStatus(text, tone)));
                return;
            }

            StatusText.Text = text;
            Color c;
            switch (tone)
            {
                case "Ok": c = (Color)ColorConverter.ConvertFromString("#10B981"); break;
                case "Warn": c = (Color)ColorConverter.ConvertFromString("#E6B44E"); break;
                case "Err": c = (Color)ColorConverter.ConvertFromString("#EF4444"); break;
                case "Accent": c = (Color)ColorConverter.ConvertFromString("#3B82F6"); break;
                default: c = (Color)ColorConverter.ConvertFromString("#A1A1AA"); break;
            }
            StatusText.Foreground = new SolidColorBrush(c);
            StatusDot.Fill = new SolidColorBrush(c);
        }

        /// <summary>
        /// 进度条宽度按比例设置。
        ///
        /// 之前这里有个 bug：安装完成后进度条停在 100%，紧接着点卸载时它还是满的，
        /// 看起来像「卸载没开始」。所以每次新操作开始前必须显式 ResetProgress()。
        /// </summary>
        private void SetProgress(int percent)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SetProgress(percent)));
                return;
            }

            _progress = Math.Max(0, Math.Min(100, percent));

            double total = ProgressTrack.ActualWidth;
            if (total <= 0) total = 620;   // 布局尚未完成时的兜底
            double target = _progress / 100.0 * total;

            double from = ProgressFill.Width;
            if (double.IsNaN(from) || from < 0) from = 0;

            var anim = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            anim.Completed += (s, e) =>
            {
                try
                {
                    ProgressFill.BeginAnimation(WidthProperty, null);
                    ProgressFill.Width = target;
                }
                catch { }
            };
            ProgressFill.BeginAnimation(WidthProperty, anim);
        }

        /// <summary>新操作开始前把进度条清零（带动画，避免突然跳回）。</summary>
        private void ResetProgress()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ResetProgress));
                return;
            }
            SetProgress(0);
        }

        private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 窗口尺寸变化后按当前百分比重算宽度，否则进度条会按旧轨道宽度缩放
            SetProgress((int)_progress);
        }

        private void AppendLog(string line)
        {
            // 后台线程会经由 Installer 的回调打日志，必须切回 UI 线程
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => AppendLog(line)));
                return;
            }

            string text = LogText.Text ?? "";
            if (string.IsNullOrEmpty(line))
            {
                LogText.Text = text + Environment.NewLine;
            }
            else
            {
                // 日志过长会拖慢渲染，只保留最后约 300 行
                if (text.Length > 20000)
                {
                    int cut = text.IndexOf('\n', text.Length - 8000);
                    if (cut > 0) text = text.Substring(cut + 1);
                }
                LogText.Text = text.Length == 0 ? line : text + Environment.NewLine + line;
            }
            LogScroll.ScrollToEnd();
        }

        private void SetBusy(bool busy, string statusIfBusy = null, string tone = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SetBusy(busy, statusIfBusy, tone)));
                return;
            }

            _busy = busy;
            InstallButton.IsEnabled = !busy;
            UpdateButton.IsEnabled = !busy;
            RollbackButton.IsEnabled = !busy;
            UninstallButton.IsEnabled = !busy;
            CloseButton.IsEnabled = !busy;
            Cursor = busy ? Cursors.AppStarting : Cursors.Arrow;
            // 只控制可用性，不改按钮文字：
            // 安装完成后按钮是「重新安装」，若这里改回「开始安装」会误导用户。
            if (busy && statusIfBusy != null) SetStatus(statusIfBusy, tone ?? "Muted");
        }

        private const string QqGroup = "1128992783";
        private const string RepoUrl = "https://github.com/baiyangmuzz/SP2Chinese";

        private Updater.NewInstaller _installerUpdate;   // 检查到的安装器新版本
        private bool _maximized;

        /// <summary>
        /// 窗口拖动（手动实现，不用 DragMove）。
        ///
        /// DragMove 依赖系统模态移动循环，在无边框 + AllowsTransparency 窗口上
        /// 表现不稳定；改成自己捕获鼠标、实时改 Left/Top，行为可预测且可自动化验证。
        /// 挂在根容器上，所以要排除按钮 / 滚动区 / 链接，避免误触。
        /// </summary>
        private bool _dragging;
        private Point _dragStartScreen;
        private Point _windowStart;

        private void RootGrid_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsInteractiveSource(e.OriginalSource as DependencyObject)) return;

            if (e.ClickCount == 2) { ToggleMaximize(); return; }
            if (e.LeftButton != MouseButtonState.Pressed || _busy) return;

            _dragging = true;
            _dragStartScreen = PointToScreen(e.GetPosition(this));
            _windowStart = new Point(Left, Top);
            RootGrid.CaptureMouse();
            e.Handled = true;
        }

        private void RootGrid_OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Point now = PointToScreen(e.GetPosition(this));
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Left = _windowStart.X + (now.X - _dragStartScreen.X) / dpi.DpiScaleX;
            Top = _windowStart.Y + (now.Y - _dragStartScreen.Y) / dpi.DpiScaleY;
        }

        private void RootGrid_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            RootGrid.ReleaseMouseCapture();
        }

        /// <summary>点击源是否落在应自行处理鼠标的控件内。</summary>
        private static bool IsInteractiveSource(DependencyObject d)
        {
            while (d != null)
            {
                if (d is ButtonBase || d is ScrollBar || d is TextBox) return true;
                if (d is ScrollViewer) return true;   // 日志区 / 历史列表
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        /// <summary>双击切换最大化。</summary>
        private void ToggleMaximize()
        {
            if (_maximized)
            {
                WindowState = WindowState.Normal;
                _maximized = false;
            }
            else
            {
                MaxWidth = double.PositiveInfinity;
                MaxHeight = double.PositiveInfinity;
                WindowState = WindowState.Maximized;
                _maximized = true;
            }
        }

        /// <summary>安装成功后显示 Star 提示（只此一处，避免打扰）。</summary>
        private void ShowStarHint()
        {
            if (StarHint != null) StarHint.Visibility = Visibility.Visible;
        }

        private void RepoLink_OnClick(object sender, MouseButtonEventArgs e) { OpenRepo(); }
        private void QqLink_OnClick(object sender, MouseButtonEventArgs e) { CopyQqGroup(); }

        /// <summary>复制群号到剪贴板，给一个轻量反馈而不是弹框打断。</summary>
        private void CopyQqGroup()
        {
            try
            {
                Clipboard.SetText(QqGroup);
                SetStatus("已复制 QQ 群号：" + QqGroup + "（免费，谨防收费诈骗）", "Ok");
            }
            catch (Exception ex)
            {
                // 剪贴板偶尔被其它进程占用
                SetStatus("复制失败，群号：" + QqGroup, "Warn");
                AppendLog("复制剪贴板失败：" + ex.Message);
            }
        }

        private void OpenRepo()
        {
            try { Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true }); }
            catch (Exception ex) { AppendLog("打开浏览器失败：" + ex.Message); }
        }

        private bool IsGameRunning()
        {
            try { return Process.GetProcessesByName("SimplePlanes 2").Length > 0; }
            catch { return false; }
        }

        private bool KillGame()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("SimplePlanes 2"))
                    try { p.Kill(); p.WaitForExit(5000); } catch { }
                System.Threading.Thread.Sleep(1500);
                return !IsGameRunning();
            }
            catch { return false; }
        }
    }
}
