using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>左下角的迷你任务栏：显示通过“显示在任务栏”加入的窗口按钮。</summary>
    class MiniTaskbarWindow : Window
    {
        StackPanel stack;
        List<IntPtr> items = new List<IntPtr>();
        IntPtr hwnd;

        /// <summary>刷新定时器（存成字段，退出时要停）。</summary>
        DispatcherTimer refreshTimer;

        /// <summary>功能开关。为 false 时整条迷你任务栏不显示、也不接收新窗口。</summary>
        public bool Enabled = true;

        static readonly Brush HoverBg = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255));
        static readonly Brush IdleBg = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));

        public MiniTaskbarWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            // Topmost 只用于防止被普通窗口遮挡，**不再靠它压住系统任务栏** ——
            // 位置由 TaskbarGeometry.MiniBarRect 贴到任务栏上沿算出来。
            Topmost = true;
            Title = "任务栏";
            Height = 48;
            Width = 0;
            WindowStartupLocation = WindowStartupLocation.Manual;
            // 初始位置交给 Refresh() 算，不写死（分辨率/DPI/任务栏位置都会变）
            Left = 0;
            Top = 0;

            stack = new StackPanel();
            stack.Orientation = Orientation.Horizontal;
            Content = stack;

            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(1500);
            t.Tick += delegate
            {
                if (ErrorLog.ShuttingDown) return;

                // 分辨率 / DPI / 多显示器 / 任务栏位置变化时重算
                if (GeometryChanged()) Refresh();
            };
            t.Start();
            refreshTimer = t;   // 存成字段，退出时要停（否则关停期间还会跳）
            TaskbarGeometry.LayoutChanged += OnTaskbarLayoutChanged;
            Closed += delegate { TaskbarGeometry.LayoutChanged -= OnTaskbarLayoutChanged; };
        }

        /// <summary>
        /// 停止刷新（退出时调用）。
        ///
        /// 原先是构造函数里的局部变量，**没有引用可以停它** ——
        /// 程序关停后它每 1.5 秒还会跳一次并碰已关闭的窗口，
        /// 抛出的异常会被记成崩溃计数。
        /// </summary>
        public void StopRefresh()
        {
            try { if (refreshTimer != null) { refreshTimer.Stop(); refreshTimer = null; } }
            catch { }
        }

        public void Add(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            if (!Enabled)
            {
                Ui.Log("迷你任务栏已停用，忽略加入请求");
                return;
            }
            if (!items.Contains(hwnd)) items.Add(hwnd);
            Refresh();
            OverlayGate.Refresh();
        }

        /// <summary>清空所有按钮（关闭该功能时调用）。</summary>
        public void Clear()
        {
            items.Clear();
            Refresh();
        }

        public void Remove(IntPtr hwnd)
        {
            items.Remove(hwnd);
            Refresh();
            OverlayGate.Refresh();
        }

        /// <summary>条上的按钮数量（0 时整条不显示）。功能停用时恒为 0。</summary>
        public int Count { get { return Enabled ? items.Count : 0; } }

        /// <summary>
        /// 上次算位置时的几何快照，用于判断"环境变了没"。
        ///
        /// 变化来源：分辨率、DPI 缩放、任务栏贴边方向/厚度、任务栏可见性、多显示器。
        /// 只在这些值真的变了才重算，避免每 1.5 秒都重排一遍。
        /// </summary>
        string lastGeomKey = "";

        /// <summary>
        /// 几何环境是否变化（分辨率 / DPI / 任务栏位置与可见性）。
        /// </summary>
        bool GeometryChanged()
        {
            try
            {
                string key = BuildGeomKey();
                if (key == lastGeomKey) return false;
                lastGeomKey = key;
                return true;
            }
            catch { return false; }
        }

        /// <summary>把影响位置的因素拼成一个可比较的字符串。</summary>
        string BuildGeomKey()
        {
            try
            {
                TaskbarGeometry.Snapshot s = TaskbarGeometry.Current;
                return s.Generation + "|" + s.Edge + "|" + s.Dpi + "|"
                    + RectKey(s.TaskbarPx) + "|" + RectKey(s.MonitorPx) + "|"
                    + RectKey(s.WorkPx) + "|" + s.TaskbarVisible + "|" + s.AutoHide;
            }
            catch { return ""; }
        }

        static string RectKey(Native.RECT r)
        {
            return r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom;
        }

        void Refresh()
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (!TaskList.IsWindowAlive(items[i])) items.RemoveAt(i);
            }

            stack.Children.Clear();
            foreach (IntPtr h in items) stack.Children.Add(MakeButton(h));

            TaskbarGeometry.Refresh();
            ApplyTaskbarLayout();
            // 显示/隐藏（含全屏时隐藏 + 悬停显示）由 OverlayGate 统一处理；
            // 功能停用时 Count 视为 0，OverlayGate 会直接隐藏整条
        }

        void ApplyTaskbarLayout()
        {
            TaskbarGeometry.Snapshot snapshot = TaskbarGeometry.Current;
            stack.Orientation = (snapshot.Edge == TaskbarGeometry.Edge.Left
                || snapshot.Edge == TaskbarGeometry.Edge.Right)
                ? Orientation.Vertical : Orientation.Horizontal;
            Native.RECT bar = TaskbarGeometry.MiniBarRectPx(items.Count);
            TaskbarGeometry.PositionWindow(this, hwnd, bar, true);
        }

        void OnTaskbarLayoutChanged()
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(OnTaskbarLayoutChanged));
                    return;
                }
                ApplyTaskbarLayout();
            }
            catch { }
        }

        Border MakeButton(IntPtr h)
        {
            Border b = new Border();
            b.Width = 44;
            b.Height = 48;
            b.CornerRadius = new CornerRadius(Ui.R.Sm);
            b.Background = IdleBg;
            b.Cursor = Cursors.Hand;

            TaskWindow w = TaskList.GetWindow(h);
            if (w != null && w.Icon != null)
            {
                Image img = new Image();
                img.Source = w.Icon;
                img.Width = 26;
                img.Height = 26;
                img.HorizontalAlignment = HorizontalAlignment.Center;
                img.VerticalAlignment = VerticalAlignment.Center;
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                b.Child = img;
            }
            if (w != null) b.ToolTip = w.Title;

            b.MouseEnter += delegate { b.Background = HoverBg; };
            b.MouseLeave += delegate { b.Background = IdleBg; };
            b.MouseLeftButtonUp += delegate { TaskList.Activate(h); };
            b.MouseRightButtonUp += delegate
            {
                ContextMenu cm = new ContextMenu();

                MenuItem close = new MenuItem();
                close.Header = "关闭窗口";
                close.Click += delegate { TaskList.CloseWindow(h); };
                cm.Items.Add(close);

                MenuItem rm = new MenuItem();
                rm.Header = "从任务栏移除";
                rm.Click += delegate { Remove(h); };
                cm.Items.Add(rm);

                cm.Placement = PlacementMode.MousePoint;
                cm.IsOpen = true;
            };
            return b;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            hwnd = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(hwnd, -20, Native.GetWindowLong(hwnd, -20) | 0x80);
            Refresh();
        }
    }
}
