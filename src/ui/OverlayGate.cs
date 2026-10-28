using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 悬浮按钮（开始 / 四叶草 / 迷你任务栏）的全屏显示策略：
    /// 全屏视频或游戏时全部隐藏，鼠标移到按钮所在位置时临时显示，移开再隐藏。
    /// 全屏检测和光标位置统一在这里轮询，避免各窗口自己判断导致显示/隐藏互相打架。
    /// </summary>
    static class OverlayGate
    {
        /// <summary>鼠标离按钮边缘多少 DIP 之内算“悬停”。</summary>
        const double HoverMargin = 10;

        class Entry
        {
            public Window Win;
            public Func<Rect> Area;     // 按钮区域（DIP），用于判断悬停
            public Func<bool> CanShow;  // 是否允许显示（例如迷你任务栏为空时不显示）
            public Func<bool> Enabled;  // 为 false 时本窗口不参与管理（例如正在打开开始菜单）
        }

        static readonly List<Entry> entries = new List<Entry>();
        static DispatcherTimer timer;
        static int currentMs;
        static bool lastFullscreen;

        /// <summary>返回 true 时临时视为“非全屏”，保证面板打开期间按钮不会消失。</summary>
        public static Func<bool> HoldVisible;

        /// <summary>全屏检测总开关（settings.fullscreenDetect）。关掉后按钮始终显示。</summary>
        public static bool Enabled = true;

        /// <summary>悬停唤出开关（settings.hoverReveal）。关掉后全屏时按钮直接隐藏、不随鼠标浮现。</summary>
        public static bool HoverReveal = true;

        public static bool Fullscreen { get; private set; }

        public static void Register(Window win, Func<Rect> area, Func<bool> canShow, Func<bool> enabled)
        {
            if (win == null) return;
            entries.Add(new Entry { Win = win, Area = area, CanShow = canShow, Enabled = enabled });
        }

        public static void Start()
        {
            if (timer != null) return;
            timer = new DispatcherTimer(DispatcherPriority.Normal);
            timer.Interval = TimeSpan.FromMilliseconds(700);
            timer.Tick += delegate { Refresh(); };   // 走 Refresh 以复用关停检查
            timer.Start();
        }

        /// <summary>
        /// 停止轮询并清空登记表（退出时调用）。
        ///
        /// 必须停：定时器在程序关停时还会跳，而那时窗口已经关闭，
        /// 对已关闭的 Window 调 Show() 会抛 InvalidOperationException
        /// —— 表现成一条 ERROR，还会被记成崩溃计数。
        /// </summary>
        public static void Stop()
        {
            try
            {
                if (timer != null) { timer.Stop(); timer = null; }
                entries.Clear();
            }
            catch { }
        }

        /// <summary>立即重新计算一次（切换任务栏模式、窗口增删后调用）。</summary>
        public static void Refresh()
        {
            if (ErrorLog.ShuttingDown) return;
            Tick();
        }

        static void Tick()
        {
            bool fs;
            // 全屏检测被关掉时，永远按“非全屏”处理，按钮常显
            if (!Enabled) fs = false;
            else
            {
                try { fs = FullscreenWatch.IsFullscreen(); } catch { fs = false; }
            }
            try
            {
                if (fs && HoldVisible != null && HoldVisible()) fs = false;
            }
            catch { }
            Fullscreen = fs;

            if (fs != lastFullscreen)
            {
                lastFullscreen = fs;
                Ui.Log("fullscreen=" + fs);
            }

            double cx = -100000;
            double cy = -100000;
            double scale = 1;
            try { scale = TaskbarGeometry.ScaleFactor(); } catch { }
            try
            {
                Native.POINT p;
                if (Native.GetCursorPos(out p))
                {
                    cx = p.X / scale;
                    cy = p.Y / scale;
                }
            }
            catch { }

            foreach (Entry e in entries)
            {
                bool enabled = true;
                try { if (e.Enabled != null) enabled = e.Enabled(); } catch { }
                if (!enabled) continue;

                bool allowed = true;
                try { if (e.CanShow != null) allowed = e.CanShow(); } catch { }
                if (!allowed)
                {
                    Apply(e.Win, false);
                    continue;
                }

                bool show = true;
                if (fs)
                {
                    if (!HoverReveal)
                    {
                        // 悬停唤出关闭：全屏时直接隐藏，不跟鼠标
                        show = false;
                    }
                    else
                    {
                        Rect r = Rect.Empty;
                        try { if (e.Area != null) r = e.Area(); } catch { }
                        if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) show = false;
                        else
                        {
                            show = cx >= r.Left - HoverMargin && cx <= r.Right + HoverMargin
                                && cy >= r.Top - HoverMargin && cy <= r.Bottom + HoverMargin;
                        }
                    }
                }
                Apply(e.Win, show);
            }

            // 全屏 + 悬停唤出时高频轮询鼠标（要跟手），其余情况低频即可
            int wantMs = (fs && HoverReveal) ? 150 : 700;
            if (wantMs != currentMs && timer != null)
            {
                currentMs = wantMs;
                timer.Interval = TimeSpan.FromMilliseconds(wantMs);
            }
        }

        static void Apply(Window w, bool show)
        {
            if (w == null) return;

            // 窗口被关掉后不能再动 Visibility/Show —— 会抛 InvalidOperationException。
            // 用 try/catch 兜住而不是只判断 IsVisible：IsVisible 在"已 Close 但尚未
            // 从 Dispatcher 摘除"的窗口上仍可能为 false，随后调 Show() 照样抛。
            try
            {
                if (!show)
                {
                    if (w.IsVisible) w.Hide();
                    return;
                }
                if (!w.IsVisible) w.Show();
            }
            catch (InvalidOperationException)
            {
                // 窗口已关闭：静默跳过，下一轮 Tick 会发现它不再需要管理
                return;
            }
            catch { return; }

            try
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                if (h != IntPtr.Zero)
                {
                    // 抬到最前，但不抢焦点（全屏里不会把游戏顶出去）
                    Native.SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010);
                }
            }
            catch { }
        }
    }
}
