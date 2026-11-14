using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

namespace SP2Chinese.Setup
{
    internal static class Program
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDPIAware();

        [DllImport("shcore.dll", SetLastError = true)]
        private static extern int SetProcessDpiAwareness(int value);

        private static void EnableDpiAwareness()
        {
            try { if (SetProcessDpiAwareness(2) == 0) return; } catch { }   // PER_MONITOR
            try { SetProcessDPIAware(); } catch { }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            EnableDpiAwareness();

            // 自测模式：不显示界面，直接跑一遍安装核心逻辑。
            // 用于 CI / 打包后自动验证「exe 里的 payload 与安装代码真的能用」，
            // 而不只是「能编译、能开窗」。
            if (args.Length >= 2 && args[0] == "--selftest")
                return SelfTest.Run(args[1]);

            // 自替换：由「新 exe」启动，等旧进程退出后覆盖旧 exe 并重启。
            // args: --replace-self <旧exe路径> <旧PID> <新版本号>
            if (args.Length >= 4 && args[0] == "--replace-self")
                return Updater.RunReplaceSelf(args[1], args[2], args[3],
                    Path.Combine(Path.GetTempPath(), "sp2-selfupdate-error.txt"));

            // 回滚窗口预览（开发验证用）：列出指定游戏目录的历史备份并弹出选择框
            if (args.Length >= 2 && args[0] == "--show-history")
                return ShowHistoryPreview(args[1]);

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                ShowFatal(e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowFatal(e.ExceptionObject as Exception);

            try
            {
                Manifest manifest = LoadManifest();
                Payload payload = Payload.Open();
                app.Run(new SetupWindow(payload, manifest));
                return 0;
            }
            catch (Exception ex)
            {
                ShowFatal(ex);
                return 1;
            }
        }

        /// <summary>开发用：直接弹出历史版本选择窗口，验证布局与列表内容。</summary>
        private static int ShowHistoryPreview(string gameDir)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var items = Installer.ListHistory(gameDir);
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "sp2-history-dir.txt"),
                "gameDir=" + gameDir + "\r\nitems=" + items.Count); } catch { }
            Console.WriteLine("[show-history] " + items.Count + " backups");

            var win = new HistoryWindow(items);
            // 把列表数据写成文本，证明绑定源本身正确（RenderTargetBitmap 对
            // ListBox 虚拟化项的抓取有已知局限，文字可能不出现在位图里）
            try
            {
                var lines = new System.Text.StringBuilder();
                foreach (var it in items) lines.AppendLine(it.Display + "   |   " + it.Name);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "sp2-history-data.txt"),
                    lines.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch { }
            win.ContentRendered += (s, e) =>
            {
                try
                {
                    // 必须等布局完成 + 强制刷新一次，否则 ListBox 的虚拟化项
                    // 还没生成，RenderTargetBitmap 只能抓到空的选中条。
                    win.UpdateLayout();
                    win.Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                        new Action(() =>
                        {
                            try
                            {
                                win.UpdateLayout();
                                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                                    (int)win.ActualWidth, (int)win.ActualHeight, 96, 96,
                                    System.Windows.Media.PixelFormats.Pbgra32);
                                rtb.Render(win);
                                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                                string outPath = Path.Combine(Path.GetTempPath(), "sp2-history-preview.png");
                                using (var fs = File.Create(outPath)) enc.Save(fs);
                            }
                            catch (Exception ex) { Console.WriteLine("[show-history] render failed: " + ex.Message); }
                            // 留 4 秒供 UIAutomation / 人工核对，之后自动退出
                            var timer = new System.Windows.Threading.DispatcherTimer
                            {
                                Interval = TimeSpan.FromSeconds(4)
                            };
                            timer.Tick += (ts, te) => { timer.Stop(); app.Shutdown(); };
                            timer.Start();
                        }));
                }
                catch (Exception ex) { Console.WriteLine("[show-history] layout failed: " + ex.Message); app.Shutdown(); }
            };
            app.Run(win);
            return 0;
        }

        private static Manifest LoadManifest()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string n in asm.GetManifestResourceNames())
            {
                if (!n.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
                using (Stream s = asm.GetManifestResourceStream(n))
                using (var reader = new StreamReader(s, System.Text.Encoding.UTF8, true))
                    return Manifest.Parse(reader.ReadToEnd());
            }
            throw new InvalidOperationException("安装包不完整：缺少内嵌 manifest.json");
        }

        private static void ShowFatal(Exception ex)
        {
            string msg = ex == null ? "未知错误" : ex.Message;
            MessageBox.Show(
                "安装程序遇到问题：\n\n" + msg +
                "\n\n请截图发给 QQ 群 1128992783，或到 GitHub 提 Issue。",
                "SimplePlanes 2 简体中文汉化",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
