using System;
using System.Windows;

namespace Clover
{
    /// <summary>
    /// 对话框窗口的收尾处理：不占任务栏、不提供最小化按钮。
    ///
    /// ── 要解决的两个问题 ──
    ///
    /// 项目的对话框都是 `ShowInTaskbar = false`（一闪而过的配置窗口，
    /// 不该占任务栏）。但 `ResizeMode = CanResize` 会让系统标题栏带上
    /// 最小化按钮 —— 两者矛盾：
    ///
    ///   最小化后任务栏没有图标 → 窗口再也找不回来 → 用户以为程序挂了
    ///
    /// ── 为什么不给 ShowInTaskbar = true ──
    ///
    /// 试过，能用，但这些是配置对话框，不该在任务栏堆图标。
    /// 去掉最小化按钮更干净：既然没法恢复，就不该给这个入口。
    ///
    /// ── 为什么需要 Win32 ──
    ///
    /// WPF 的 `ResizeMode` 只有四档：
    ///   NoResize / CanMinimize / CanResize / CanResizeWithGrip
    /// **没有"可调大小但没有最小化"**这一档。
    /// `CanMinimize` 反过来又去掉了最大化。
    /// 所以只能改 Win32 的窗口样式位（去掉 WS_MINIMIZEBOX）。
    ///
    /// ── 顺带：最小化时解禁 Owner ──
    ///
    /// 即使去掉了最小化按钮，用户仍可能用 Win+↓ 或任务栏右键最小化。
    /// 这些窗口是 `ShowDialog()` 模态，最小化后 Owner 会被系统禁用，
    /// 主面板点不动 —— 所以最小化时解禁、恢复时禁回去。
    ///
    /// 两条保护都留着：按钮去掉是"不给入口"，解禁是"万一还是最小化了"。
    /// </summary>
    static class DialogChrome
    {
        /// <summary>
        /// 把系统标题栏换成项目自绘的标题栏。
        ///
        /// ── 为什么必须自绘 ──
        ///
        /// 实测：`WS_MINIMIZEBOX` 样式位**确实清掉了**
        /// （日志：before=0x06CF0000 hasMin=True → after=0x06CD0000 hasMin=False），
        /// 但标题栏的「—」按钮**还在画面上**。
        ///
        /// 试过的手段都无效：
        ///   · SetWindowPos + SWP_FRAMECHANGED
        ///   · SendMessage(WM_NCPAINT)
        ///   · InvalidateRect
        ///   · SourceInitialized / Loaded 两个时机各改一次
        ///
        /// 说明这个按钮不是靠 WS_MINIMIZEBOX 位画的 —— 系统按窗口当前
        /// 状态（可调整大小 + 有标题栏）重建了非客户区。
        ///
        /// 唯一可靠的办法：**不用系统标题栏**。设 WindowStyle=None 后
        /// 系统一个按钮都不画，标题栏完全由 WPF 内容区自己画。
        ///
        /// 这也是主面板的做法（PanelWindow 就是 WindowStyle=None +
        /// 自绘 min/max/close 三个按钮），风格统一。
        /// </summary>
        /// <param name="dlg">目标窗口</param>
        /// <param name="content">窗口内容（标题栏会插在它上面）</param>
        public static void UseCustomTitleBar(Window dlg, UIElement content)
        {
            try
            {
                if (dlg == null) return;

                dlg.WindowStyle = WindowStyle.None;
                dlg.ResizeMode = ResizeMode.CanResize;   // 保留边框拖拽缩放

                // 用 WindowChrome 保留系统的边缘缩放热区 —— 只关掉标题栏，
                // 不牺牲"拖边框改大小"。
                System.Windows.Shell.WindowChrome chrome =
                    new System.Windows.Shell.WindowChrome();
                chrome.CaptionHeight = 0;                 // 没有系统标题栏
                chrome.ResizeBorderThickness = new Thickness(4);
                chrome.GlassFrameThickness = new Thickness(0);
                chrome.UseAeroCaptionButtons = false;     // ★ 关键：不要系统按钮
                System.Windows.Shell.WindowChrome.SetWindowChrome(dlg, chrome);

                if (content != null)
                    dlg.Content = BuildWithTitleBar(dlg, content);
            }
            catch { }
        }

        /// <summary>
        /// 把原内容包进「自绘标题栏 + 内容」的 Grid。
        ///
        /// 标题栏只放一个关闭按钮 —— 没有最小化（这正是本次要的），
        /// 也不放最大化（配置对话框不需要全屏）。
        /// </summary>
        static UIElement BuildWithTitleBar(Window dlg, UIElement content)
        {
            // WindowStyle=None 后窗口没有系统背景，必须自己给一层，
            // 否则内容区直接透出桌面。
            System.Windows.Controls.Border outer = new System.Windows.Controls.Border();
            outer.Background = Ui.Bg;
            outer.BorderBrush = Ui.Line;
            outer.BorderThickness = new Thickness(1);

            System.Windows.Controls.Grid root = new System.Windows.Controls.Grid();
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
            { Height = System.Windows.GridLength.Auto });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
            { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

            // ---- 标题栏 ----
            System.Windows.Controls.Border bar = new System.Windows.Controls.Border();
            bar.Height = 36;
            bar.Background = System.Windows.Media.Brushes.Transparent;
            bar.MouseLeftButtonDown += delegate
            {
                // 拖标题栏移动窗口（WindowStyle=None 后系统不再自动处理）
                try { dlg.DragMove(); } catch { }
            };

            System.Windows.Controls.Grid barGrid = new System.Windows.Controls.Grid();
            barGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            barGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
            { Width = System.Windows.GridLength.Auto });

            System.Windows.Controls.TextBlock title = new System.Windows.Controls.TextBlock();
            title.Text = dlg.Title ?? "";
            title.FontFamily = Ui.YaHei;
            title.FontSize = 12.5;
            title.Foreground = Ui.Sub;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = new Thickness(Ui.S.Lg, 0, 0, 0);
            System.Windows.Controls.Grid.SetColumn(title, 0);
            barGrid.Children.Add(title);

            // 关闭按钮（只有一个，没有最小化/最大化）
            System.Windows.Controls.Border close =
                new System.Windows.Controls.Border();
            close.Width = 32;
            close.Height = 28;
            close.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
            close.CornerRadius = new CornerRadius(Ui.R.Sm);
            close.Background = System.Windows.Media.Brushes.Transparent;
            close.Cursor = System.Windows.Input.Cursors.Hand;
            close.ToolTip = "关闭";

            System.Windows.Controls.TextBlock glyph =
                new System.Windows.Controls.TextBlock();
            glyph.Text = "\uE711";                    // Segoe MDL2 Close
            glyph.FontFamily = Ui.Mdl;
            glyph.FontSize = 11;
            glyph.Foreground = Ui.Sub;
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            close.Child = glyph;

            close.MouseEnter += delegate { close.Background = Ui.Hover; };
            close.MouseLeave += delegate { close.Background = System.Windows.Media.Brushes.Transparent; };
            close.MouseLeftButtonUp += delegate { try { dlg.Close(); } catch { } };

            System.Windows.Controls.Grid.SetColumn(close, 1);
            barGrid.Children.Add(close);

            bar.Child = barGrid;
            System.Windows.Controls.Grid.SetRow(bar, 0);
            root.Children.Add(bar);

            // ---- 原内容 ----
            System.Windows.Controls.Grid.SetRow(content, 1);
            root.Children.Add(content);

            outer.Child = root;
            return outer;
        }

        /// <summary>
        /// 在 `ShowDialog()` 之前调用。
        ///
        /// 做两件事：
        ///   1. 窗口句柄建立后去掉最小化按钮
        ///   2. 万一被最小化（Win+↓ 等），解禁 Owner
        /// </summary>
        public static void Attach(Window dlg)
        {
            try
            {
                if (dlg == null) return;

                // 诊断：确认 Attach 被调用
                Diag("Attach 进入, title=" + dlg.Title);

                // ---- 1. 去掉最小化按钮 ----
                //
                // 必须在句柄建立之后。**两个时机都做**：
                //   · SourceInitialized —— 最早，但此时窗口还没被系统画出来，
                //     有些情况下样式改动会被后续的 CreateWindow 覆盖
                //   · Loaded —— 窗口已经画完，再改一次并强制重画标题栏
                //
                // 实测只做 SourceInitialized 时按钮有时还留着，所以补 Loaded。
                dlg.SourceInitialized += delegate
                {
                    try
                    {
                        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
                        Diag("SourceInitialized hwnd=" + hwnd);
                        Native.RemoveMinimizeBox(hwnd);
                    }
                    catch (Exception ex) { Diag("SourceInitialized 异常: " + ex.Message); }
                };

                dlg.Loaded += delegate
                {
                    try
                    {
                        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
                        Diag("Loaded hwnd=" + hwnd);
                        Native.RemoveMinimizeBox(hwnd);
                    }
                    catch (Exception ex) { Diag("Loaded 异常: " + ex.Message); }
                };

                // ---- 2. 拦截最小化命令（保险）----
                //
                // 即使样式位没生效、按钮还留着，点它也**不会真的最小化**。
                // 双保险：样式去掉是"看不见按钮"，这里是"点了也没用"。
                //
                // 同时覆盖 Win+↓ 和任务栏右键 → 最小化。
                dlg.SourceInitialized += delegate
                {
                    try
                    {
                        System.Windows.Interop.HwndSource src =
                            System.Windows.Interop.HwndSource.FromHwnd(
                                new System.Windows.Interop.WindowInteropHelper(dlg).Handle);
                        if (src == null) return;

                        src.AddHook(delegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
                            ref bool handled)
                        {
                            try
                            {
                                const int WM_SYSCOMMAND = 0x0112;
                                const int SC_MINIMIZE = 0xF020;

                                if (msg != WM_SYSCOMMAND) return IntPtr.Zero;

                                int cmd = wParam.ToInt32() & 0xFFF0;
                                if (cmd == SC_MINIMIZE)
                                {
                                    // 吞掉最小化命令：窗口保持原状
                                    handled = true;
                                    return IntPtr.Zero;
                                }
                            }
                            catch { }
                            return IntPtr.Zero;
                        });
                    }
                    catch { }
                };

                // ---- 3. 万一还是被最小化，解禁 Owner ----
                Window owner = dlg.Owner;
                if (owner != null)
                {
                    dlg.StateChanged += delegate
                    {
                        try
                        {
                            IntPtr hwnd = OwnerHandle(owner);
                            if (hwnd == IntPtr.Zero) return;

                            bool minimized = dlg.WindowState == WindowState.Minimized;
                            Native.SetWindowEnabled(hwnd, minimized);
                        }
                        catch { }
                    };

                    // 关闭时兜底：无论之前什么状态，Owner 都必须可用
                    dlg.Closed += delegate
                    {
                        try
                        {
                            IntPtr hwnd = OwnerHandle(owner);
                            if (hwnd != IntPtr.Zero) Native.SetWindowEnabled(hwnd, true);
                        }
                        catch { }
                    };
                }
            }
            catch { }
        }

        static IntPtr OwnerHandle(Window owner)
        {
            try
            {
                if (owner == null) return IntPtr.Zero;
                return new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>
        /// 直接写日志文件，不依赖 Log 管道（core 层的 Log 要 Program 接线，
        /// 排查阶段用最直接的方式确认代码有没有跑到）。
        /// </summary>
        static void Diag(string msg)
        {
            try
            {
                string p = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "dialog-chrome.log");
                System.IO.File.AppendAllText(p,
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg
                    + Environment.NewLine);
            }
            catch { }
        }
    }
}
