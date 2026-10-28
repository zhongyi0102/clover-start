using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>面板左侧的“任务栏”区域：列出当前打开的窗口，支持切换/右键菜单/定时刷新。</summary>
    partial class PanelWindow
    {
        string taskSig = "";
        DateTime lastTaskEnum = DateTime.MinValue;

        /// <summary>
        /// 当前前台窗口句柄的缓存。
        /// 只在 RefreshTaskList 里取一次；鼠标事件（MouseEnter/MouseLeave）只读它，
        /// 不在输入回调里调 GetForegroundWindow()。
        /// </summary>
        IntPtr cachedActive = IntPtr.Zero;

        /// <summary>
        /// 枚举窗口的最小间隔。ShowPanel 每次都会调这里，而枚举会为每个窗口
        /// 创建对象并取图标——即便有签名去重，枚举本身的开销也很大。
        /// 面板刚打开时不必每次都全量枚举。
        /// </summary>
        const int TaskEnumMinIntervalMs = 800;

        /// <summary>启动任务栏列表的定时刷新；重复调用只会复用同一个定时器。</summary>
        void StartTaskTimer()
        {
            if (taskTimer != null)
            {
                if (!taskTimer.IsEnabled) taskTimer.Start();
                return;
            }
            taskTimer = new DispatcherTimer();
            taskTimer.Interval = TimeSpan.FromMilliseconds(1500);
            taskTimer.Tick += delegate
            {
                // 关停期间不再刷新：枚举窗口会为每个窗口取图标，
                // 而那时窗口正在被销毁，既白费力气也可能碰到半销毁状态
                if (ErrorLog.ShuttingDown) return;
                try { if (IsVisible) RefreshTaskList(); }
                catch (Exception ex) { Ui.LogError(ex, "刷新任务栏列表"); }
            };
            taskTimer.Start();
        }

        /// <summary>
        /// 只刷新前台窗口句柄缓存，不做全量枚举。
        ///
        /// 用途：面板刚打开（ShowPanel）或窗口被激活时调用，保证鼠标还没划过来之前
        /// 缓存就是新值。这样 MouseEnter/MouseLeave 依然只读缓存（输入回调里无系统调用），
        /// 又不会出现「刚切到某窗口、立刻划过它却高亮不对」的延迟。
        ///
        /// 与 RefreshTaskList 的区别：这里**不受 TaskEnumMinIntervalMs 限制**——
        /// 取一个前台句柄是极轻量的（一次 GetForegroundWindow），
        /// 被限流跳过反而会留下旧值。
        /// </summary>
        void RefreshActiveCache()
        {
            try
            {
                IntPtr active = TaskList.GetActiveWindow();
                if (active == cachedActive) return;
                cachedActive = active;
                // 句柄变了：就地改各行底色，不必等下一次全量刷新，也不重建整行。
                ApplyActiveHighlight();
            }
            catch { }
        }

        /// <summary>按缓存的 cachedActive，重新给任务栏各行上底色（不重建控件）。</summary>
        void ApplyActiveHighlight()
        {
            if (taskStack == null) return;
            try
            {
                foreach (object o in taskStack.Children)
                {
                    Border b = o as Border;
                    if (b == null || b.Tag == null) continue;
                    IntPtr hwnd = (IntPtr)b.Tag;
                    b.Background = IsActiveRow(hwnd, cachedActive) ? Ui.AccentSoft : Brushes.Transparent;
                }
            }
            catch { }
        }

        /// <summary>
        /// 判断任务栏某一行是否该显示「当前前台」底色。
        ///
        /// 抽成静态纯函数是为了可测：面板的视觉状态由它决定，
        /// 但它不碰任何控件或系统调用，所以能在没有窗口的环境里断言。
        /// </summary>
        public static bool IsActiveRow(IntPtr rowHwnd, IntPtr cachedActive)
        {
            return rowHwnd != IntPtr.Zero && rowHwnd == cachedActive;
        }

        void RefreshTaskList()
        {
            if (taskStack == null) return;

            // 距上次枚举太近就直接跳过，避免 ShowPanel 与定时器叠加导致重复全量枚举
            if ((DateTime.Now - lastTaskEnum).TotalMilliseconds < TaskEnumMinIntervalMs) return;
            lastTaskEnum = DateTime.Now;

            List<TaskWindow> wins = TaskList.GetWindows(Process.GetCurrentProcess().Id);

            // 前台窗口句柄只在这次刷新时取一次，缓存起来。
            // 鼠标事件（MouseEnter/MouseLeave）不能再各自调 GetForegroundWindow()——
            // 那会变成"输入回调里查询系统状态"，鼠标划过列表时每项每次进出都发一次系统调用。
            cachedActive = TaskList.GetActiveWindow();

            StringBuilder sb = new StringBuilder();
            sb.Append(cachedActive).Append('|');
            foreach (TaskWindow tw in wins) sb.Append(tw.Hwnd).Append(':').Append(tw.Title).Append('|');
            string sig = sb.ToString();
            if (sig == taskSig) return;
            taskSig = sig;
            taskStack.Children.Clear();
            if (wins.Count == 0)
            {
                TextBlock none = Ui.T("没有打开的窗口", 10.5, Ui.Sub);
                none.Margin = new Thickness(6, 4, 0, 0);
                taskStack.Children.Add(none);
                return;
            }
            foreach (TaskWindow w in wins)
                taskStack.Children.Add(MakeTaskRow(w));
        }

        Border MakeTaskRow(TaskWindow w)
        {
            Border b = new Border();
            b.Height = 30;
            b.CornerRadius = new CornerRadius(6);
            b.Margin = new Thickness(0, 1, 0, 1);
            // 用缓存的句柄判断，不在构造时调 GetForegroundWindow
            b.Background = IsActiveRow(w.Hwnd, cachedActive) ? Ui.AccentSoft : Brushes.Transparent;
            // 记住这行对应哪个窗口，供 RefreshActiveCache 就地改底色（无需重建整行）
            b.Tag = w.Hwnd;
            b.Cursor = Cursors.Hand;
            b.ToolTip = w.Title;

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = new GridLength(22);
            g.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);

            if (w.Icon != null)
            {
                Image img = new Image();
                img.Source = w.Icon;
                img.Width = 16;
                img.Height = 16;
                img.Stretch = Stretch.Uniform;
                img.HorizontalAlignment = HorizontalAlignment.Center;
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                Grid.SetColumn(img, 0);
                g.Children.Add(img);
            }

            TextBlock t = new TextBlock();
            t.Text = w.Title;
            t.FontFamily = Ui.YaHei;
            t.FontSize = 10.5;
            t.Foreground = Ui.Fg;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(2, 0, 6, 0);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);

            b.Child = g;
            // 鼠标事件只读缓存的 cachedActive，不再各自调 GetForegroundWindow()。
            // 这样"输入回调"里没有任何系统调用。
            b.MouseEnter += delegate { if (cachedActive != w.Hwnd) b.Background = Ui.Hover; };
            b.MouseLeave += delegate { if (cachedActive != w.Hwnd) b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += delegate { TaskList.Activate(w.Hwnd); HidePanel(); };
            b.MouseRightButtonUp += delegate { ShowTaskMenu(w); };
            return b;
        }

        void ShowTaskMenu(TaskWindow w)
        {
            ContextMenu cm = new ContextMenu();

            // ---- 窗口基本操作 ----
            // 原先这里只有「显示在任务栏」，而迷你任务栏的菜单有「关闭窗口」——
            // 同一个窗口在两处右键，能做的事却不一样，这是能力不对等。
            // TaskList 早已备好 Minimize / CloseWindow，接上即可。
            MenuItem act = new MenuItem();
            act.Header = "切换到该窗口";
            act.Click += delegate { TaskList.Activate(w.Hwnd); HidePanel(); };
            cm.Items.Add(act);

            MenuItem min = new MenuItem();
            min.Header = "最小化";
            min.Click += delegate { TaskList.Minimize(w.Hwnd); };
            cm.Items.Add(min);

            MenuItem close = new MenuItem();
            close.Header = "关闭窗口";
            close.ToolTip = "向窗口发送关闭请求（等同于点它的 × 按钮）";
            close.Click += delegate { TaskList.CloseWindow(w.Hwnd); };
            cm.Items.Add(close);

            cm.Items.Add(new Separator());

            MenuItem pin = new MenuItem();
            pin.Header = "显示在任务栏";
            pin.Click += delegate { if (miniBar != null) miniBar.Add(w.Hwnd); };
            cm.Items.Add(pin);

            taskMenuOpen = true;
            cm.Closed += delegate { taskMenuOpen = false; };
            cm.Placement = PlacementMode.MousePoint;
            cm.IsOpen = true;
        }
    }
}
