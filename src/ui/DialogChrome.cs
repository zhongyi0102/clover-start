using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

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
        /// 为可调整大小的长期配置窗口使用项目标题栏。
        ///
        /// 这些窗口不占任务栏，因此标题栏只提供「关闭」：不显示最小化的
        /// 灰色占位，也不提供没有必要的最大化/还原入口。窗口仍可拖动并从
        /// 四边调整大小。
        /// </summary>
        public static void UseCloseOnlyTitleBar(Window dlg, UIElement content)
        {
            try
            {
                if (dlg == null) return;

                // 旧版位置记录可能保存了最大化状态；只保留关闭按钮后不能让
                // 窗口以无还原入口的状态打开。
                if (dlg.WindowState == WindowState.Maximized)
                    dlg.WindowState = WindowState.Normal;

                dlg.WindowStyle = WindowStyle.None;

                WindowChrome chrome = new WindowChrome();
                chrome.CaptionHeight = 0;
                chrome.ResizeBorderThickness = new Thickness(6);
                chrome.GlassFrameThickness = new Thickness(0);
                chrome.CornerRadius = new CornerRadius(0);
                chrome.UseAeroCaptionButtons = false;
                WindowChrome.SetWindowChrome(dlg, chrome);

                AttachCloseOnlySystemBehavior(dlg);

                if (content != null) dlg.Content = BuildCloseOnlyShell(dlg, content);
            }
            catch { }
        }

        static UIElement BuildCloseOnlyShell(Window dlg, UIElement content)
        {
            Border outer = new Border();
            outer.Background = Ui.Bg;
            outer.BorderBrush = Ui.Line;
            outer.BorderThickness = new Thickness(1);

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition
            { Height = new GridLength(1, GridUnitType.Star) });

            Border bar = new Border();
            bar.Height = 36;
            bar.Background = Ui.Bg;
            bar.BorderBrush = Ui.Line;
            bar.BorderThickness = new Thickness(0, 0, 0, 1);

            Grid barGrid = new Grid();
            barGrid.ColumnDefinitions.Add(new ColumnDefinition());
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock title = new TextBlock();
            title.Text = dlg.Title ?? "";
            title.FontFamily = Ui.YaHei;
            title.FontSize = 12.5;
            title.Foreground = Ui.Fg;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = new Thickness(Ui.S.Lg, 0, 0, 0);
            Grid.SetColumn(title, 0);
            barGrid.Children.Add(title);

            StackPanel actions = new StackPanel();
            actions.Orientation = Orientation.Horizontal;
            actions.VerticalAlignment = VerticalAlignment.Stretch;

            Button close = MakeCaptionButton("\uE711", "关闭", delegate
            {
                try { SystemCommands.CloseWindow(dlg); }
                catch { try { dlg.Close(); } catch { } }
            });
            actions.Children.Add(close);

            Grid.SetColumn(actions, 1);
            barGrid.Children.Add(actions);
            bar.Child = barGrid;

            bar.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (IsDescendantOf(e.OriginalSource as DependencyObject, actions)) return;

                // 双击标题栏不再切换最大化；消费第二次按下，避免误触拖动。
                if (e.ClickCount > 1)
                {
                    e.Handled = true;
                    return;
                }

                try { dlg.DragMove(); }
                catch { }
                e.Handled = true;
            };

            Grid.SetRow(bar, 0);
            root.Children.Add(bar);

            Grid.SetRow(content, 1);
            root.Children.Add(content);

            outer.Child = root;
            return outer;
        }

        /// <summary>
        /// 自绘按钮之外再从系统层禁用最小化/最大化，覆盖快捷键、系统菜单和
        /// Aero Snap；窗口边框缩放能力由 WS_THICKFRAME / WindowChrome 保留。
        /// </summary>
        static void AttachCloseOnlySystemBehavior(Window dlg)
        {
            try
            {
                dlg.SourceInitialized += delegate
                {
                    try
                    {
                        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
                        Native.RemoveMinimizeAndMaximizeBoxes(hwnd);

                        System.Windows.Interop.HwndSource src =
                            System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                        if (src != null) src.AddHook(BlockMinimizeAndMaximize);
                    }
                    catch { }
                };

                // 某些主题会在窗口首次布局时重写样式，再清一次作为兜底。
                dlg.Loaded += delegate
                {
                    try
                    {
                        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
                        Native.RemoveMinimizeAndMaximizeBoxes(hwnd);
                    }
                    catch { }
                };
            }
            catch { }
        }

        static IntPtr BlockMinimizeAndMaximize(IntPtr hwnd, int msg, IntPtr wParam,
            IntPtr lParam, ref bool handled)
        {
            try
            {
                const int WM_SYSCOMMAND = 0x0112;
                const int SC_MINIMIZE = 0xF020;
                const int SC_MAXIMIZE = 0xF030;

                if (msg != WM_SYSCOMMAND) return IntPtr.Zero;

                int cmd = wParam.ToInt32() & 0xFFF0;
                if (cmd == SC_MINIMIZE || cmd == SC_MAXIMIZE)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        static Button MakeCaptionButton(string glyph, string name, Action onClick)
        {
            Button button = new Button();
            button.Width = 46;
            button.Height = 34;
            button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            button.Padding = new Thickness(0);
            button.Cursor = Cursors.Hand;
            button.Template = MakeCaptionButtonTemplate();
            button.ToolTip = name;
            AutomationProperties.SetName(button, name + "窗口");
            button.Content = MakeCaptionGlyph(glyph);
            button.Click += delegate { if (onClick != null) onClick(); };
            button.MouseEnter += delegate { if (!button.IsKeyboardFocused) button.Background = Ui.Hover; };
            button.MouseLeave += delegate { if (!button.IsKeyboardFocused) button.Background = Brushes.Transparent; };
            button.GotKeyboardFocus += delegate { button.Background = Ui.AccentSoft; };
            button.LostKeyboardFocus += delegate { button.Background = Brushes.Transparent; };
            return button;
        }

        static ControlTemplate MakeCaptionButtonTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(Ui.R.Sm));
            border.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.TemplatedParent)
                });

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }

        static TextBlock MakeCaptionGlyph(string glyph)
        {
            TextBlock text = new TextBlock();
            text.Text = glyph;
            text.FontFamily = Ui.Mdl;
            text.FontSize = 11;
            text.Foreground = Ui.Fg;
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;
            return text;
        }

        static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
        {
            try
            {
                DependencyObject current = child;
                while (current != null)
                {
                    if (current == ancestor) return true;
                    current = VisualTreeHelper.GetParent(current);
                }
            }
            catch { }
            return false;
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
                        Native.RemoveMinimizeBox(hwnd);
                    }
                    catch { }
                };

                dlg.Loaded += delegate
                {
                    try
                    {
                        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
                        Native.RemoveMinimizeBox(hwnd);

                        // ★ Loaded 时样式位已经是 hasMin=False，但按钮还画在
                        //   画面上 —— 说明系统是在 Loaded **之后**才最终画出
                        //   非客户区的。用 Dispatcher 排到渲染之后再来一次。
                        dlg.Dispatcher.BeginInvoke(
                            new Action(delegate
                            {
                                try
                                {
                                    Native.RemoveMinimizeBox(hwnd);
                                    // 再排一轮：有些主题/DPI 下要两帧
                                    dlg.Dispatcher.BeginInvoke(
                                        new Action(delegate
                                        {
                                            try { Native.RemoveMinimizeBox(hwnd); }
                                            catch { }
                                        }),
                                        System.Windows.Threading.DispatcherPriority.ContextIdle);
                                }
                                catch { }
                            }),
                            System.Windows.Threading.DispatcherPriority.Loaded);
                    }
                    catch { }
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
    }
}
