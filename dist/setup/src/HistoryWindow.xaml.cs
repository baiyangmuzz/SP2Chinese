using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SP2Chinese.Setup
{
    /// <summary>历史版本选择窗口。返回用户选中的备份名，取消返回 null。</summary>
    public partial class HistoryWindow : Window
    {
        internal string SelectedName { get; private set; }

        // 手动拖拽状态（与主窗口一致：DragMove 在无边框+透明窗口上不可靠）
        private bool _dragging;
        private Point _dragStartScreen;
        private Point _windowStart;

        internal HistoryWindow(IList<Installer.HistoryItem> items)
        {
            InitializeComponent();

            if (items.Count == 0)
            {
                HistoryList.Visibility = Visibility.Collapsed;
                EmptyHint.Visibility = Visibility.Visible;
                OkButton.IsEnabled = false;
            }
            else
            {
                HistoryList.ItemsSource = items;
                HistoryList.SelectedIndex = 0;   // 默认选最新的那份
            }

            OkButton.Click += (s, e) =>
            {
                var sel = HistoryList.SelectedItem as Installer.HistoryItem;
                if (sel == null) return;
                SelectedName = sel.Name;
                DialogResult = true;
                Close();
            };
            CancelButton.Click += (s, e) => { DialogResult = false; Close(); };

            // 无边框窗口拖动：自己捕获鼠标改 Left/Top
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
        }

        private static bool IsInteractive(DependencyObject d)
        {
            while (d != null)
            {
                if (d is ButtonBase || d is ScrollBar || d is ScrollViewer || d is TextBox) return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (IsInteractive(e.OriginalSource as DependencyObject)) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            _dragging = true;
            _dragStartScreen = PointToScreen(e.GetPosition(this));
            _windowStart = new Point(Left, Top);
            CaptureMouse();
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Point now = PointToScreen(e.GetPosition(this));
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Left = _windowStart.X + (now.X - _dragStartScreen.X) / dpi.DpiScaleX;
            Top = _windowStart.Y + (now.Y - _dragStartScreen.Y) / dpi.DpiScaleY;
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
        }
    }
}
