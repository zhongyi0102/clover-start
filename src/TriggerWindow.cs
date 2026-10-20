using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 任务栏里的独立按钮：44x48，跟随任务栏图标组末尾，样式与原生任务栏按钮一致。
    /// </summary>
    class TriggerWindow : Window
    {
        PanelWindow panel;
        string baseDir;
        string exePath;
        string startupLnk;
        Border root;
        IntPtr hwnd;
        double lastLeft = double.NaN;
        double lastTop = double.NaN;
        bool hideTaskbar;
        string settingsPath;
        Settings settings;
        StartButtonWindow startWin;

        /// <summary>TaskbarCreated 消息号（explorer 重启后广播）。</summary>
        static uint taskbarCreatedMsg;
        static readonly Brush HoverBg = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255));
        static readonly Brush IdleBg = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));

        public TriggerWindow(string baseDir, Settings settings)
        {
            this.baseDir = baseDir;
            this.settings = settings ?? Settings.Load(baseDir);
            this.exePath = Path.Combine(baseDir, "app", "CloverStart.exe");
            this.startupLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "四叶草启动器.lnk");
            this.settingsPath = Path.Combine(baseDir, "config", "settings.json");
            // 功能被关掉时强制视为“不隐藏”，避免配置文件里留着旧的 true 却无法取消
            this.hideTaskbar = this.settings.TaskbarHiding && this.settings.HideTaskbar;
            this.startWin = new StartButtonWindow();
            this.startWin.Show();
            if (!hideTaskbar) this.startWin.Hide();

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Title = "四叶草";
            Width = 44;
            Height = 48;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Reposition();

            root = new Border();
            root.CornerRadius = new CornerRadius(6);
            root.Background = IdleBg;
            root.Cursor = Cursors.Hand;

            Image img = new Image();
            img.Source = Ui.LoadImage(Path.Combine(baseDir, "res", "clover.png"));
            img.Width = 26;
            img.Height = 26;
            img.Stretch = Stretch.Uniform;
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            img.HorizontalAlignment = HorizontalAlignment.Center;
            img.VerticalAlignment = VerticalAlignment.Center;
            root.Child = img;

            root.MouseEnter += delegate { root.Background = HoverBg; };
            root.MouseLeave += delegate { root.Background = IdleBg; };
            root.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; Ui.Log("trigger click"); TogglePanel(); };
            root.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; ShowMenu(); };

            Content = root;
            Closed += delegate { if (hwnd != IntPtr.Zero) Native.UnregisterHotKey(hwnd, 1); };

            // 任务栏图标变化时（新开/关闭窗口）跟随到图标组末尾
            DispatcherTimer follow = new DispatcherTimer();
            follow.Interval = TimeSpan.FromSeconds(2);
            follow.Tick += delegate { Reposition(); };
            follow.Start();
        }

        public void AttachPanel(PanelWindow p)
        {
            panel = p;

        }

        /// <summary>“开始”浮动按钮（任务栏隐藏模式下与四叶草并排）。</summary>
        public StartButtonWindow StartButton { get { return startWin; } }

        public bool HideTaskbar { get { return hideTaskbar; } }

        void Reposition()
        {
            try
            {
                TaskbarGeometry.Refresh();
                if (hideTaskbar)
                {
                    Rect cl = TaskbarGeometry.CloverRect();
                    Left = cl.X;
                    Top = cl.Y;
                    if (startWin != null)
                    {
                        Rect sb = TaskbarGeometry.StartButtonRect();
                        startWin.Left = sb.X;
                        startWin.Top = sb.Y;
                    }
                    return;
                }
                if (TaskbarGeometry.CurrentEdge != TaskbarGeometry.Edge.Bottom)
                {
                    Rect cl2 = TaskbarGeometry.CloverRect();
                    Left = cl2.X;
                    Top = cl2.Y;
                    return;
                }
                // 用上次异步探测到的布局值（没有就先用几何估算），并顺手发起下一次探测。
                // 界面线程不等待 UIAutomation。
                RequestTaskbarLayout();

                Rect tr;
                double right;
                if (hasCachedLayout)
                {
                    tr = cachedTrayRect;
                    right = cachedButtonRight;
                }
                else
                {
                    // 还没有探测结果：先用几何估算位置，探测回来后会自动 Reposition 校正
                    Rect fallback = TaskbarGeometry.CloverRect();
                    Left = fallback.X;
                    Top = fallback.Y;
                    return;
                }

                double scale = 1.0;
                try
                {
                    uint dpi = Native.GetDpiForSystem();
                    if (dpi > 0) scale = dpi / 96.0;
                }
                catch { }

                double left = right / scale;
                double top = tr.Top / scale;
                double maxLeft = SystemParameters.PrimaryScreenWidth - Width - 8;
                if (left > maxLeft) left = maxLeft;

                if (double.IsNaN(lastLeft) || Math.Abs(left - lastLeft) > 0.5)
                {
                    Left = left;
                    lastLeft = left;
                }
                if (double.IsNaN(lastTop) || Math.Abs(top - lastTop) > 0.5)
                {
                    Top = top;
                    lastTop = top;
                }
            }
            catch { }
        }

        /// <summary>
        /// 用 UIAutomation 取任务栏矩形与应用按钮组右边界。
        /// **异步**：界面线程不等待（FindFirst 需要消息泵，同步等必然超时）。
        /// 结果回来后用缓存的布局值重新定位。
        /// </summary>
        static bool uiLayoutProbing;
        static DateTime lastLayoutProbe = DateTime.MinValue;
        static Rect cachedTrayRect = Rect.Empty;
        static double cachedButtonRight;
        static bool hasCachedLayout;

        /// <summary>探测间隔下限：任务栏布局不会频繁变化，没必要高频查 UIAutomation。</summary>
        const int LayoutProbeIntervalMs = 5000;

        void RequestTaskbarLayout()
        {
            if (uiLayoutProbing) return;
            if ((DateTime.Now - lastLayoutProbe).TotalMilliseconds < LayoutProbeIntervalMs) return;
            uiLayoutProbing = true;
            lastLayoutProbe = DateTime.Now;

            SafeCall.RunAsync<bool>(delegate
            {
                AutomationElement tray = AutomationElement.RootElement.FindFirst(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
                if (tray == null) return false;

                Rect r = tray.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width <= 0) return false;

                double maxRight = 0;
                AutomationElementCollection buttons = tray.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer"));
                foreach (AutomationElement b in buttons)
                {
                    Rect br = b.Current.BoundingRectangle;
                    if (br.IsEmpty || br.Width <= 0) continue;
                    if (br.Right > maxRight) maxRight = br.Right;
                }

                cachedTrayRect = r;
                cachedButtonRight = maxRight > 0 ? maxRight + 4 : r.Left + 56 + 4;
                hasCachedLayout = true;
                return true;
            }, SafeCall.DefaultTimeoutMs, delegate(bool ok, bool _)
            {
                uiLayoutProbing = false;
                // 关键：这里只“应用位置”，绝不再调 Reposition()。
                // 否则 Reposition -> RequestTaskbarLayout -> 回调 Reposition 会形成
                // 持续递归，导致每 2 秒产生一轮 UIAutomation 调用和闭包分配（实测 14 MB/分钟内存泄漏）。
                if (ok && hasCachedLayout) ApplyCachedLayout();
            });
        }

        /// <summary>把缓存的布局应用到窗口位置，不触发新的探测。</summary>
        /// <summary>
        /// 把缓存的布局应用到窗口位置，不触发新的探测。
        /// 注意：本方法由 SafeCall 的后台回调调用，必须在界面线程执行
        /// （WPF 的 Left/Top 只能在创建窗口的线程上改）。
        /// </summary>
        void ApplyCachedLayout()
        {
            try
            {
                // 跨线程访问 WPF 属性会抛 InvalidOperationException，
                // 这里统一切回界面线程执行。
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(ApplyCachedLayout));
                    return;
                }

                if (!hasCachedLayout) return;
                if (hideTaskbar) return;
                if (TaskbarGeometry.CurrentEdge != TaskbarGeometry.Edge.Bottom) return;

                double scale = 1.0;
                try
                {
                    uint dpi = Native.GetDpiForSystem();
                    if (dpi > 0) scale = dpi / 96.0;
                }
                catch { }

                double left = cachedButtonRight / scale;
                double top = cachedTrayRect.Top / scale;
                double maxLeft = SystemParameters.PrimaryScreenWidth - Width - 8;
                if (left > maxLeft) left = maxLeft;

                if (double.IsNaN(lastLeft) || Math.Abs(left - lastLeft) > 0.5)
                {
                    Left = left;
                    lastLeft = left;
                }
                if (double.IsNaN(lastTop) || Math.Abs(top - lastTop) > 0.5)
                {
                    Top = top;
                    lastTop = top;
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("应用任务栏布局失败: " + ex.Message);
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            hwnd = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(hwnd, -20, Native.GetWindowLong(hwnd, -20) | 0x80);
            HwndSource.FromHwnd(hwnd).AddHook(OnHook);
            Native.RegisterHotKey(hwnd, 1, 0x0002 | 0x0001, 0x20);

            // 注册 explorer 重启广播消息（任务栏重建后重新应用隐藏状态）
            try { taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated"); }
            catch { taskbarCreatedMsg = 0; }

            // 定期把自己抬回任务栏之上（任务栏是 topmost，用户点击它后可能压住本按钮）。
            // 显示/隐藏（含全屏时隐藏 + 悬停显示）统一由 OverlayGate 负责。
            DispatcherTimer top = new DispatcherTimer();
            top.Interval = TimeSpan.FromMilliseconds(1200);
            top.Tick += delegate
            {
                // 卡死恢复：看门狗曾因主进程卡死还原过任务栏（留下 guard-restored 标记）。
                // 如果用户仍勾选"隐藏系统任务栏"，自动重新隐藏一次——配额由标记文件保证整轮仅 1 次。
                if (hideTaskbar && WatchdogResume.TryConsumeRehidePermission())
                {
                    try
                    {
                        TaskbarVisibility.Apply(true);
                        lastLeft = double.NaN;
                        lastTop = double.NaN;
                        Reposition();
                        OverlayGate.Refresh();
                        Ui.Log("卡死恢复：已按用户设置重新隐藏任务栏（本次运行仅此 1 次）");
                    }
                    catch (Exception ex)
                    {
                        ErrorLog.Warn("卡死恢复后重新隐藏失败: " + ex.Message);
                    }
                }

                if (Visibility != Visibility.Visible) return;
                if (hwnd != IntPtr.Zero)
                    Native.SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010);
            };
            top.Start();
            ApplyTaskbarVisibility(hideTaskbar);
        }

        IntPtr OnHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312)
            {
                TogglePanel();
                handled = true;
                return IntPtr.Zero;
            }

            // explorer 重启后任务栏窗口是新建的：原来隐藏的状态会丢，按钮定位也会失效。
            // 这里收到 TaskbarCreated 就重新应用当前设置并重新定位，不报错、不进入崩溃循环。
            if (msg == taskbarCreatedMsg && taskbarCreatedMsg != 0)
            {
                Ui.Log("收到 TaskbarCreated（资源管理器重启），重新应用任务栏状态");
                try
                {
                    ApplyTaskbarVisibility(hideTaskbar);
                    lastLeft = double.NaN;
                    lastTop = double.NaN;
                    Reposition();
                    OverlayGate.Refresh();
                }
                catch (Exception ex)
                {
                    ErrorLog.Warn("处理 TaskbarCreated 失败: " + ex.Message);
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void TogglePanel()
        {
            Ui.Log("TogglePanel, panel=null? " + (panel == null) + " visible=" + (panel != null && panel.IsVisible));
            if (panel == null) return;
            if (panel.IsVisible) panel.HidePanel();
            else panel.ShowPanel();
        }

        void ShowMenu()
        {
            ContextMenu cm = new ContextMenu();

            MenuItem open = new MenuItem();
            open.Header = "打开面板";
            open.Click += delegate { TogglePanel(); };
            cm.Items.Add(open);

            MenuItem rescan = new MenuItem();
            rescan.Header = "重新扫描应用";
            rescan.Click += delegate { if (panel != null) panel.Rescan(); };
            cm.Items.Add(rescan);

            MenuItem clean = new MenuItem();
            clean.Header = "立即清理桌面快捷方式";
            clean.Click += delegate { if (panel != null) panel.CleanDesktopFromMenu(); };
            cm.Items.Add(clean);

            MenuItem restore = new MenuItem();
            restore.Header = "还原桌面快捷方式";
            restore.ToolTip = "把 desktop-backup 里的快捷方式放回桌面（重名自动加后缀，不覆盖）";
            restore.Click += delegate
            {
                if (panel == null) return;
                int n = panel.RestoreDesktopFromMenu();
                MessageBox.Show(
                    n > 0
                        ? "已把 " + n + " 个快捷方式放回桌面。\r\n重名的会加上「-还原N」后缀，没有覆盖任何文件。"
                        : "没有可还原的快捷方式。\r\n（desktop-backup 里没有 .lnk 文件）",
                    "四叶草");
            };
            cm.Items.Add(restore);

            // ---- 开关区 ----
            cm.Items.Add(new Separator());

            MenuItem miniItem = new MenuItem();
            miniItem.Header = "启用迷你任务栏";
            miniItem.IsCheckable = true;
            miniItem.IsChecked = settings.MiniTaskbar;
            miniItem.ToolTip = "左下角显示“显示在任务栏”加入的窗口按钮；关闭后该功能整体停用";
            miniItem.Click += delegate
            {
                settings.MiniTaskbar = !settings.MiniTaskbar;
                settings.Save();
                if (panel != null) panel.ApplyMiniTaskbarSetting();
                Ui.Log("迷你任务栏开关 -> " + settings.MiniTaskbar);
            };
            cm.Items.Add(miniItem);

            MenuItem autoCleanItem = new MenuItem();
            autoCleanItem.Header = "自动移走桌面快捷方式";
            autoCleanItem.IsCheckable = true;
            autoCleanItem.IsChecked = settings.DesktopAutoClean;
            autoCleanItem.ToolTip = "桌面上的 .lnk 自动移到 desktop-backup；关闭后不再自动移动";
            autoCleanItem.Click += delegate
            {
                bool target = !settings.DesktopAutoClean;

                // 首次“勾选”时弹一次说明，让用户明确知道会发生什么。
                // 取消则不勾选，也不记录“已提示”（下次勾选还会再问一次）。
                if (target && !settings.DesktopAutoCleanNoticeShown)
                {
                    System.Windows.MessageBoxResult r = MessageBox.Show(
                        "开启后，桌面上的快捷方式（.lnk）会被自动移到：\r\n"
                        + Path.Combine(baseDir, "desktop-backup") + "\r\n\r\n"
                        + "· 只是移动，不会删除\r\n"
                        + "· 随时可以用右键菜单「还原桌面快捷方式」放回桌面\r\n"
                        + "· 这个说明只显示一次\r\n\r\n"
                        + "确定开启吗？",
                        "四叶草 · 自动移走桌面快捷方式",
                        MessageBoxButton.OKCancel, MessageBoxImage.Information);

                    if (r != System.Windows.MessageBoxResult.OK)
                    {
                        // 用户取消：保持不勾选，菜单项状态回滚
                        autoCleanItem.IsChecked = false;
                        Ui.Log("用户取消开启自动移走桌面快捷方式");
                        return;
                    }
                    settings.DesktopAutoCleanNoticeShown = true;
                }

                settings.DesktopAutoClean = target;
                settings.Save();
                if (panel != null) panel.ApplyDesktopCleanSetting();
                Ui.Log("桌面快捷方式自动移走开关 -> " + settings.DesktopAutoClean);
            };
            cm.Items.Add(autoCleanItem);

            MenuItem taskbarHidingItem = new MenuItem();
            taskbarHidingItem.Header = "允许隐藏系统任务栏";
            taskbarHidingItem.IsCheckable = true;
            taskbarHidingItem.IsChecked = settings.TaskbarHiding;
            taskbarHidingItem.ToolTip = "关闭后强制恢复任务栏，并禁用下面的“隐藏系统任务栏”";
            taskbarHidingItem.Click += delegate
            {
                settings.TaskbarHiding = !settings.TaskbarHiding;
                if (!settings.TaskbarHiding)
                {
                    // 关掉这个功能时，必须真的把任务栏放出来
                    hideTaskbar = false;
                    settings.HideTaskbar = false;
                    TaskbarVisibility.Apply(false);
                    startWin.Hide();
                }
                settings.Save();
                lastLeft = double.NaN;
                lastTop = double.NaN;
                Reposition();
                OverlayGate.Refresh();
                Ui.Log("允许隐藏系统任务栏 -> " + settings.TaskbarHiding);
            };
            cm.Items.Add(taskbarHidingItem);

            MenuItem taskbarItem = new MenuItem();
            taskbarItem.Header = "隐藏系统任务栏";
            taskbarItem.IsCheckable = true;
            taskbarItem.IsChecked = hideTaskbar;
            taskbarItem.IsEnabled = settings.TaskbarHiding;
            taskbarItem.Click += delegate
            {
                hideTaskbar = !hideTaskbar;
                settings.HideTaskbar = hideTaskbar;
                settings.Save();
                TaskbarVisibility.Apply(hideTaskbar);
                if (hideTaskbar) startWin.Show();
                else startWin.Hide();
                lastLeft = double.NaN;
                lastTop = double.NaN;
                Reposition();
                OverlayGate.Refresh();
            };
            cm.Items.Add(taskbarItem);

            cm.Items.Add(new Separator());

            MenuItem auto = new MenuItem();
            auto.Header = "开机自启";
            auto.IsCheckable = true;
            auto.IsChecked = File.Exists(startupLnk);
            auto.Click += delegate { ToggleAutostart(); };
            cm.Items.Add(auto);

            cm.Items.Add(new Separator());

            MenuItem exit = new MenuItem();
            exit.Header = "退出";
            exit.Click += delegate
            {
                // 退出前把任务栏放回来，否则用户会面对没有任务栏的桌面
                TaskbarVisibility.RestoreSystemState("用户退出");
                Application.Current.Shutdown();
            };
            cm.Items.Add(exit);

            cm.Placement = PlacementMode.Bottom;
            cm.PlacementTarget = root;
            cm.IsOpen = true;
        }

        void ApplyTaskbarVisibility(bool hide)
        {
            TaskbarVisibility.Apply(hide);
        }

        void ToggleAutostart()
        {
            try
            {
                if (File.Exists(startupLnk))
                {
                    File.Delete(startupLnk);
                    return;
                }
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { startupLnk });
                Type scType = sc.GetType();
                scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exePath });
                scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(exePath) });
                scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { exePath + ",0" });
                scType.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "四叶草启动面板" });
                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置开机自启失败: " + ex.Message, "四叶草");
            }
        }
    }
}
