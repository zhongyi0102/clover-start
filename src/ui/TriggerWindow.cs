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
            // 功能被关掉时强制视为“不隐藏”，避免配置文件里留着旧的 true 却无法取消。
            // 用 EffectiveHideTaskbar：安全模式下自动为 false。
            this.hideTaskbar = this.settings.EffectiveHideTaskbar;
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
            root.CornerRadius = new CornerRadius(Ui.R.Sm);
            root.Background = IdleBg;
            root.Cursor = Cursors.Hand;

            Image img = new Image();
            img.Source = Ui.LoadCloverIcon(Path.Combine(baseDir, "res", "clover.png"));
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
            follow.Tick += delegate { if (!ErrorLog.ShuttingDown) Reposition(); };
            follow.Start();
            followTimer = follow;   // 存成字段，退出时要停

            // 窗口关闭时把两个常驻定时器停掉，避免关停期间继续跳
            Closed += delegate
            {
                try { if (followTimer != null) { followTimer.Stop(); followTimer = null; } } catch { }
                try { if (topTimer != null) { topTimer.Stop(); topTimer = null; } } catch { }
            };
        }

        DispatcherTimer followTimer;
        DispatcherTimer topTimer;

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
                if (ErrorLog.ShuttingDown) return;   // 关停期间不再碰窗口
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
            topTimer = top;
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
                    // 任务栏是全新的窗口，上次那条"定位正常"的日志状态已经过时。
                    // 清掉它，好让下一次 Refresh 重新报一条真实结果，
                    // 否则 explorer 重启后若定位降级，日志会一直沉默。
                    TaskbarGeometry.ResetUiStateLog();
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
            // 安全模式下禁用：隐藏任务栏是"程序挂了用户就没任务栏"的最高风险操作，
            // 正是安全模式要挡住的事。菜单项置灰 + 提示原因，比点了没反应清楚。
            taskbarItem.IsEnabled = settings.EffectiveTaskbarHiding;
            if (Settings.SafeModeActive)
            {
                taskbarItem.ToolTip = "安全模式下已禁用（避免程序异常时系统任务栏回不来）。"
                    + "退出安全模式后即可使用。";
            }
            taskbarItem.Click += delegate
            {
                // 双保险：IsEnabled 已挡住，但菜单可能在打开后被 SafeMode 变化影响
                if (!settings.EffectiveTaskbarHiding) return;

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

            // ---- 悬浮按钮显示策略 ----
            // fullscreenDetect / hoverReveal 原先只能手改 settings.json，
            // 界面上没有开关 —— 这两个设置恰好也是被安全模式降级的项，
            // 一旦被固化就永久失效、还查不出原因（现在安全模式已不再改它们）。
            cm.Items.Add(new Separator());

            MenuItem fullscreenItem = new MenuItem();
            fullscreenItem.Header = "全屏时隐藏悬浮按钮";
            fullscreenItem.IsCheckable = true;
            // 显示的是**用户的偏好**（原字段），不是 Effective 值 ——
            // 安全模式下功能被临时停用，但菜单该如实反映"你设的是什么"，
            // 否则用户会以为自己的设置丢了。
            fullscreenItem.IsChecked = settings.FullscreenDetect;
            fullscreenItem.ToolTip = "看视频、玩游戏（全屏）时自动隐藏四叶草按钮，不挡画面";
            fullscreenItem.Click += delegate
            {
                settings.FullscreenDetect = !settings.FullscreenDetect;
                settings.Save();
                OverlayGate.Enabled = settings.EffectiveFullscreenDetect;
                OverlayGate.Refresh();
                Ui.Log("全屏检测 -> " + settings.FullscreenDetect);
            };
            cm.Items.Add(fullscreenItem);

            MenuItem hoverItem = new MenuItem();
            hoverItem.Header = "全屏时鼠标移过去临时显示";
            hoverItem.IsCheckable = true;
            hoverItem.IsChecked = settings.HoverReveal;
            hoverItem.ToolTip = "全屏时把鼠标移到按钮位置，按钮临时浮现，移开再隐藏。"
                + "关掉后全屏时按钮直接隐藏、不随鼠标出现";
            hoverItem.Click += delegate
            {
                settings.HoverReveal = !settings.HoverReveal;
                settings.Save();
                OverlayGate.HoverReveal = settings.EffectiveHoverReveal;
                OverlayGate.Refresh();
                Ui.Log("悬停唤出 -> " + settings.HoverReveal);
            };
            cm.Items.Add(hoverItem);

            cm.Items.Add(new Separator());

            // ---- 隐藏的项目管理 ----
            MenuItem hideTools = new MenuItem();
            hideTools.Header = "隐藏用不到的";
            if (panel == null)
            {
                hideTools.IsEnabled = false;
            }
            else
            {
                // 一键藏掉整类（你从没用过「系统工具」和「工具/命令行」）
                foreach (string cat in new string[] { "系统工具", Catalog.ToolCategory })
                {
                    string c = cat;
                    MenuItem one = new MenuItem();
                    one.Header = "隐藏全部「" + c + "」";
                    one.Click += delegate
                    {
                        int n = panel.HideCategory(c);
                        if (n > 0) panel.ShowPanel();
                    };
                    hideTools.Items.Add(one);
                }

                hideTools.Items.Add(new Separator());

                MenuItem showHidden = new MenuItem();
                showHidden.Header = "查看隐藏的项目";
                showHidden.IsEnabled = panel.HiddenCount() > 0;
                showHidden.Click += delegate { panel.ShowHiddenItems(); };
                hideTools.Items.Add(showHidden);

                MenuItem unhideAll = new MenuItem();
                unhideAll.Header = "取消隐藏全部";
                unhideAll.IsEnabled = panel.HiddenCount() > 0;
                unhideAll.Click += delegate
                {
                    int n = panel.UnhideAll();
                    if (n > 0) panel.ShowPanel();
                };
                hideTools.Items.Add(unhideAll);
            }
            cm.Items.Add(hideTools);

            cm.Items.Add(new Separator());

            // ---- 主题 ----
            // 三选一，当前项打勾。切换后立刻生效（颜色刷子是全局的，
            // 改 .Color 整棵界面就变色，不用重建窗口）。
            MenuItem themeItem = new MenuItem();
            themeItem.Header = "主题";
            Ui.ThemeMode currentTheme = Ui.ParseTheme(settings.Theme);
            foreach (Ui.ThemeMode m in new Ui.ThemeMode[]
                { Ui.ThemeMode.System, Ui.ThemeMode.Light, Ui.ThemeMode.Dark })
            {
                Ui.ThemeMode mm = m;
                MenuItem one = new MenuItem();
                one.Header = Ui.ThemeDisplayName(mm);
                one.IsCheckable = true;
                one.IsChecked = (mm == currentTheme);
                one.Click += delegate { ApplyThemeAndSave(mm); };
                themeItem.Items.Add(one);
            }
            cm.Items.Add(themeItem);

            cm.Items.Add(new Separator());

            // ---- 诊断 ----
            // 定位状态原先只写进日志，界面上看不到。定位降级时按钮会偏几像素，
            // 用户只会觉得"按钮位置怪怪的"却查不出原因，所以给一个可见入口。
            MenuItem diag = new MenuItem();
            diag.Header = "诊断";
            diag.ToolTip = "查看任务栏定位、看门狗、崩溃计数等运行状态";

            MenuItem loc = new MenuItem();
            loc.Header = "任务栏定位";
            loc.Click += delegate { ShowLocationStatus(); };
            diag.Items.Add(loc);

            MenuItem health = new MenuItem();
            health.Header = "运行状态";
            health.Click += delegate { ShowHealthStatus(); };
            diag.Items.Add(health);

            cm.Items.Add(diag);

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

            // ---- 一键回滚（放最后，且要二次确认）----
            // ResetAll 设计成**独立进程**跑：它会杀掉看门狗，且要求自己不是被回滚的对象。
            // 所以这里不直接调 ResetAll.Run —— 那是自杀式操作（第 5 步会遍历同名进程）。
            // 改为拉起一个带 --reset-all 的子进程；该分支在互斥锁之前，
            // 所以主实例还在跑也不冲突。
            cm.Items.Add(new Separator());

            MenuItem rollback = new MenuItem();
            rollback.Header = "一键回滚…";
            rollback.ToolTip = "撤销本程序对系统做过的所有改动：任务栏可见性、开机自启、"
                + "看门狗、崩溃保护状态。你的设置和应用清单会保留";
            rollback.Click += delegate { PromptRollback(); };
            cm.Items.Add(rollback);

            cm.Placement = PlacementMode.Bottom;
            cm.PlacementTarget = root;
            cm.IsOpen = true;
        }

        /// <summary>
        /// 一键回滚的确认与执行。
        ///
        /// 为什么放在菜单最底部并要二次确认：这是个"撤销所有系统改动"的重操作，
        /// 误点代价高（任务栏会被放回来、开机自启被删掉）。原先只有命令行能触发，
        /// 界面上没有任何入口，反而容易让人以为没这个功能。
        /// </summary>
        void PromptRollback()
        {
            try
            {
                bool yes = TextPromptDialog.Confirm(this,
                    "一键回滚",
                    "将撤销本程序对系统做过的所有改动：\r\n"
                    + "  · 恢复系统任务栏（可见性与透明样式）\r\n"
                    + "  · 移除开机自启快捷方式\r\n"
                    + "  · 停止看门狗进程\r\n"
                    + "  · 清除崩溃保护状态（退出安全模式）\r\n\r\n"
                    + "你的设置、应用清单、使用记录、快捷键备份都会**保留**。\r\n\r\n"
                    + "回滚完成后本程序会退出。继续吗？",
                    "确认回滚");

                if (!yes) return;

                string exe = Path.Combine(baseDir, "app", "CloverStart.exe");
                if (!File.Exists(exe))
                {
                    MessageBox.Show("找不到 " + exe + "\r\n无法执行回滚。", "四叶草");
                    return;
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--reset-all --keep-config",
                    UseShellExecute = false
                });

                Ui.Log("用户从菜单触发一键回滚（--reset-all --keep-config）");

                MessageBox.Show(
                    "回滚已启动，本程序即将退出。\r\n\r\n"
                    + "回滚结果可在 config\\error.log 里查看（搜索「一键回滚」）。",
                    "四叶草");

                // 让子进程先跑起来，再退出自己（否则它可能被本进程的退出清理干扰）
                TaskbarVisibility.RestoreSystemState("一键回滚（用户触发）");
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "一键回滚");
                MessageBox.Show("启动回滚失败：" + ex.Message, "四叶草");
            }
        }

        /// <summary>
        /// 显示任务栏定位状态。
        ///
        /// 为什么要给这个入口：定位降级（推断不出任务栏几何、或 UIAutomation
        /// 没能精确校准）时按钮会偏几个像素，用户只觉得"位置怪怪的"，
        /// 但原因只写在日志里。这里把来源和是否精确校准直接摆出来。
        /// </summary>
        void ShowLocationStatus()
        {
            try
            {
                TaskbarGeometry.Refresh();

                string edgeName;
                switch (TaskbarGeometry.CurrentEdge)
                {
                    case TaskbarGeometry.Edge.Top: edgeName = "顶部"; break;
                    case TaskbarGeometry.Edge.Left: edgeName = "左侧"; break;
                    case TaskbarGeometry.Edge.Right: edgeName = "右侧"; break;
                    default: edgeName = "底部"; break;
                }

                string sourceName = TaskbarGeometry.LastSource;
                if (sourceName == "workArea") sourceName = "工作区推断";
                else if (sourceName == "trayWindow") sourceName = "任务栏窗口矩形";
                else sourceName = "回退估算（降级）";

                Rect s = TaskbarGeometry.StartCell;
                Rect c = TaskbarGeometry.CloverRect();

                string msg =
                    "任务栏位置：" + edgeName + "，厚度 " + Math.Round(TaskbarGeometry.Thickness) + " px\r\n"
                    + "几何来源：" + sourceName + "\r\n"
                    + "精确校准：" + (TaskbarGeometry.PreciseLocated ? "已校准（UIAutomation）" : "未校准，用估算值")
                    + "\r\n"
                    + "DPI 缩放：" + TaskbarGeometry.ScaleFactor().ToString("0.##") + "\r\n"
                    + "\r\n"
                    + "开始按钮格：" + Fmt(s) + "\r\n"
                    + "四叶草按钮：" + Fmt(c) + "\r\n"
                    + "\r\n";

                if (TaskbarGeometry.LastSource == "fallback")
                {
                    msg += "⚠ 几何推断失败，已回退到底部 48px。\r\n"
                        + "按钮位置可能不准。常见原因：任务栏刚重建、或被第三方任务栏工具接管。\r\n"
                        + "程序会自动重试，通常几秒后恢复。";
                }
                else if (!TaskbarGeometry.PreciseLocated)
                {
                    msg += "说明：尚未取得原生开始按钮的精确矩形，当前是估算位置。\r\n"
                        + "按钮仍可正常点击，只是可能横向偏几像素。";
                }
                else
                {
                    msg += "定位正常。";
                }

                MessageBox.Show(msg, "任务栏定位 · 四叶草");
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取定位状态失败：" + ex.Message, "四叶草");
            }
        }

        /// <summary>
        /// 显示运行状态（崩溃计数、安全模式、内存句柄），并提供退出安全模式。
        ///
        /// 「退出安全模式」原先只有 `--reset-all` 命令行能触发：
        /// CrashGuard.ClearSafeMode() 的注释写着"用户在安全模式面板里点恢复正常模式"，
        /// 但那个界面入口一直没做。进了安全模式就只能手改 JSON 或走命令行。
        /// </summary>
        void ShowHealthStatus()
        {
            try
            {
                string msg = "";
                bool safeInFile = false;

                // 崩溃计数 / 安全模式
                try
                {
                    string p = Path.Combine(baseDir, "config", "crash-guard.json");
                    if (File.Exists(p))
                    {
                        JavaScriptSerializer ser = new JavaScriptSerializer();
                        Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(
                            File.ReadAllText(p, System.Text.Encoding.UTF8));
                        if (d != null)
                        {
                            object v;
                            int crashes = 0;
                            if (d.TryGetValue("crashes", out v) && v != null)
                            {
                                foreach (object o in JsonIO.AsArray(v))
                                    crashes++;
                            }
                            safeInFile = d.TryGetValue("safeMode", out v) && v != null && Convert.ToBoolean(v);

                            msg += "异常退出记录（2 分钟内累计到 3 次会进安全模式）：\r\n"
                                + "  文件记录 " + crashes + " 次\r\n\r\n";
                        }
                    }
                    else
                    {
                        msg += "异常退出记录：无（crash-guard.json 不存在）\r\n\r\n";
                    }
                }
                catch (Exception ex)
                {
                    msg += "读崩溃记录失败：" + ex.Message + "\r\n\r\n";
                }

                // 运行时真实状态（比只看文件准：--safe-mode 强制时文件里可能没标记）
                bool safeNow = Settings.SafeModeActive;
                msg += "安全模式：" + (safeNow ? "**本次运行处于安全模式**" : "否") + "\r\n";
                if (safeNow)
                {
                    msg += "  已临时停用：隐藏任务栏、迷你任务栏、自动移走桌面快捷方式、全屏检测\r\n"
                        + "  你的设置**没有被改动**，退出安全模式后即刻恢复\r\n";
                }
                else if (safeInFile)
                {
                    msg += "  （文件里有标记，但本次运行未进入；下次启动会重新判定）\r\n";
                }
                msg += "\r\n";

                // 内存 / 句柄
                try
                {
                    System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
                    msg += "当前进程：\r\n"
                        + "  内存 " + Math.Round(self.WorkingSet64 / 1024.0 / 1024.0) + " MB（阈值 400 MB 时自动清缓存）\r\n"
                        + "  句柄 " + self.HandleCount + " 个（阈值 3000 个）\r\n"
                        + "  启动于 " + self.StartTime.ToString("MM-dd HH:mm") + "\r\n\r\n";
                }
                catch { }

                // 看门狗
                try
                {
                    string hb = Path.Combine(baseDir, "config", "heartbeat");
                    if (File.Exists(hb))
                    {
                        DateTime t = File.GetLastWriteTime(hb);
                        double age = (DateTime.Now - t).TotalSeconds;
                        msg += "看门狗心跳：" + t.ToString("HH:mm:ss") + "（" + Math.Round(age) + " 秒前）\r\n"
                            + "  " + (age < 30 ? "正常" : "⚠ 心跳偏旧，看门狗可能重启过程序") + "\r\n\r\n";
                    }
                    else
                    {
                        msg += "看门狗心跳：文件不存在（看门狗可能未启用）\r\n\r\n";
                    }
                }
                catch { }

                // 隐藏 / 分类
                try
                {
                    if (panel != null)
                    {
                        msg += "应用：隐藏 " + panel.HiddenCount() + " 个，当前分类「"
                            + panel.CurrentCategory() + "」";
                    }
                }
                catch { }

                // ---- 退出安全模式 ----
                // 只是清 crash-guard.json 里的标记；本次运行已经生效的功能降级要重启才恢复，
                // 所以提示里说清楚。用户设置本身从未被改动（见 Settings.SafeModeActive 的说明）。
                if (Settings.SafeModeActive || safeInFile)
                {
                    msg += "\r\n\r\n是否现在退出安全模式？\r\n"
                        + "（会清掉崩溃计数与安全模式标记。由于相关功能在本次启动时已停用，\r\n"
                        + "　需要重启程序才会完全恢复。）";

                    bool yes = TextPromptDialog.Confirm(this, "退出安全模式", msg, "退出安全模式");
                    if (yes)
                    {
                        CrashGuard.ClearSafeMode();
                        Settings.SafeModeActive = false;
                        Ui.Log("用户手动退出安全模式");
                        MessageBox.Show(
                            "已退出安全模式。\r\n\r\n"
                            + "重启程序后，隐藏任务栏 / 迷你任务栏 / 全屏检测等功能会按你的设置恢复。\r\n"
                            + "（你的设置文件从未被安全模式改动过）",
                            "四叶草");
                    }
                    return;
                }

                MessageBox.Show(msg.TrimEnd(), "运行状态 · 四叶草");
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取运行状态失败：" + ex.Message, "四叶草");
            }
        }

        static string Fmt(Rect r)
        {
            if (r.IsEmpty) return "(空)";
            return "(" + Math.Round(r.X) + ", " + Math.Round(r.Y) + ")  "
                + Math.Round(r.Width) + "×" + Math.Round(r.Height);
        }

        void ApplyTaskbarVisibility(bool hide)
        {
            TaskbarVisibility.Apply(hide);
        }

        /// <summary>
        /// 切换主题并保存。
        ///
        /// 为什么不需要重建窗口：颜色刷子是**共享对象**，改 .Color 后
        /// 所有引用它们的控件会自动重绘。只有那些把颜色**写死**在
        /// 构造函数里的地方（如悬浮窗的背景、投影）需要显式重画，
        /// 由 Ui.ThemeChanged 回调通知。
        /// 菜单项自己的勾选状态由下次打开菜单时重建，不用手动同步。
        /// </summary>
        void ApplyThemeAndSave(Ui.ThemeMode mode)
        {
            try
            {
                settings.Theme = Ui.ThemeToString(mode);
                settings.Save();
                Ui.ApplyTheme(mode);
                Ui.Log("主题 -> " + Ui.ThemeDisplayName(mode)
                    + "（实际 " + Ui.ThemeDisplayName(Ui.AppliedTheme) + "）");

                // 让三个悬浮窗和面板重画写死的颜色
                try { RefreshWindowsForTheme(); } catch { }
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("切换主题失败: " + ex.Message);
            }
        }

        /// <summary>主题变了以后，通知各窗口重画那些不能自动跟随的颜色。</summary>
        void RefreshWindowsForTheme()
        {
            // 面板：颜色全走 Ui 刷子会**自动跟随**，但 rootBorder 的投影是
            // 构建时创建的固定黑色效果，深色底上会几乎看不见 —— 需要重设。
            try { if (panel != null) panel.ApplyTheme(); } catch { }

            // 三个悬浮窗**不用动**：它们的 HoverBg/IdleBg 是半透明白色浮层
            // （alpha 46 / 1），本意就是叠在任务栏上，深浅主题都成立
            // （Windows 自己的任务栏悬停高亮也是浅色浮层）。
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
