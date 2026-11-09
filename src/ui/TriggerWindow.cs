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
        Func<PanelWindow> panelFactory;
        bool panelLoading;
        string baseDir;
        string exePath;
        string startupLnk;
        Button root;
        Border panelIndicator;
        IntPtr hwnd;
        HwndSource hwndSource;
        bool shellHookRegistered;
        double lastLeft = double.NaN;
        double lastTop = double.NaN;
        bool hideTaskbar;
        string settingsPath;
        Settings settings;
        StartButtonWindow startWin;

        /// <summary>TaskbarCreated 消息号（explorer 重启后广播）。</summary>
        static uint taskbarCreatedMsg;
        /// <summary>Shell Hook 消息号（顶层窗口创建/销毁）。</summary>
        static uint shellHookMsg;
        static readonly Brush HoverBg = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255));
        static readonly Brush IdleBg = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));

        /// <summary>事件丢失时的低频兜底；正常定位由 Shell Hook 驱动。</summary>
        internal const int TaskbarFallbackIntervalMs = 30000;
        /// <summary>合并窗口密集创建/销毁事件，等任务栏动画和布局稳定后只校准一次。</summary>
        internal const int ShellLayoutDebounceMs = 350;
        /// <summary>UIAutomation 瞬时失败时只做两次一次性重试，不恢复高频常驻轮询。</summary>
        internal const int LayoutProbeRetryLimit = 2;

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

            root = new Button();
            root.Template = MakeTaskbarButtonTemplate();
            root.Background = IdleBg;
            root.BorderBrush = Brushes.Transparent;
            root.BorderThickness = new Thickness(0);
            root.Padding = new Thickness(0);
            root.Cursor = Cursors.Hand;
            root.Focusable = true;
            KeyboardNavigation.SetIsTabStop(root, true);
            root.FocusVisualStyle = null;

            Image img = new Image();
            img.Source = Ui.LoadCloverIcon(Path.Combine(baseDir, "res", "clover.png"));
            img.Width = 26;
            img.Height = 26;
            img.Stretch = Stretch.Uniform;
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            img.HorizontalAlignment = HorizontalAlignment.Center;
            img.VerticalAlignment = VerticalAlignment.Center;

            Grid iconLayer = new Grid();
            iconLayer.Children.Add(img);

            // 面板打开时只加一条克制的 2px 强调色指示，不改变任务栏按钮尺寸与图标。
            // 颜色沿用项目令牌，圆头用 Pill，深浅主题和自定义强调色会自动同步。
            panelIndicator = new Border();
            panelIndicator.Width = 20;
            panelIndicator.Height = 2;
            panelIndicator.CornerRadius = new CornerRadius(Ui.R.Pill);
            panelIndicator.Background = Ui.Accent;
            panelIndicator.HorizontalAlignment = HorizontalAlignment.Center;
            panelIndicator.VerticalAlignment = VerticalAlignment.Bottom;
            panelIndicator.Margin = new Thickness(0, 0, 0, Ui.S.Xs);
            panelIndicator.IsHitTestVisible = false;
            panelIndicator.Visibility = Visibility.Collapsed;
            iconLayer.Children.Add(panelIndicator);
            root.Content = iconLayer;
            UpdatePanelState();

            root.MouseEnter += delegate { ApplyRootVisualState(); };
            root.MouseLeave += delegate { ApplyRootVisualState(); };
            root.GotKeyboardFocus += delegate { ApplyRootVisualState(); };
            root.LostKeyboardFocus += delegate { ApplyRootVisualState(); };
            // 原生 Button.Click 同时覆盖鼠标、Enter、Space 与 UIAutomation InvokePattern。
            root.Click += delegate { TogglePanel(); };
            root.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; ShowMenu(); };

            Content = root;
            TaskbarGeometry.LayoutChanged += OnTaskbarLayoutChanged;
            Closed += delegate
            {
                if (hwnd != IntPtr.Zero) Native.UnregisterHotKey(hwnd, 1);
                TaskbarGeometry.LayoutChanged -= OnTaskbarLayoutChanged;
            };

            // Shell Hook 负责及时跟随任务栏图标变化；定时器只在事件丢失时低频兜底。
            DispatcherTimer follow = new DispatcherTimer();
            follow.Interval = TimeSpan.FromMilliseconds(TaskbarFallbackIntervalMs);
            follow.Tick += delegate { if (!ErrorLog.ShuttingDown) Reposition(); };
            follow.Start();
            followTimer = follow;   // 存成字段，退出时要停

            layoutEventTimer = new DispatcherTimer(DispatcherPriority.Background);
            layoutEventTimer.Interval = TimeSpan.FromMilliseconds(ShellLayoutDebounceMs);
            layoutEventTimer.Tick += delegate
            {
                layoutEventTimer.Stop();
                if (ErrorLog.ShuttingDown) return;
                lastLeft = double.NaN;
                lastTop = double.NaN;
                Reposition(true);
            };

            // 窗口关闭时停掉任务栏监听，避免关停期间继续碰已关闭的窗口。
            Closed += delegate
            {
                try { if (followTimer != null) { followTimer.Stop(); followTimer = null; } } catch { }
                try { if (layoutEventTimer != null) { layoutEventTimer.Stop(); layoutEventTimer = null; } } catch { }
                try { if (panel != null) panel.IsVisibleChanged -= PanelVisibilityChanged; } catch { }
                try { if (shellHookRegistered && hwnd != IntPtr.Zero) Native.DeregisterShellHookWindow(hwnd); } catch { }
                try { if (hwndSource != null) hwndSource.RemoveHook(OnHook); } catch { }
                shellHookRegistered = false;
                hwndSource = null;
                OverlayGate.Maintenance = null;
                try { if (startWin != null) startWin.Close(); } catch { }
            };
        }

        DispatcherTimer followTimer;
        DispatcherTimer layoutEventTimer;

        public void AttachPanel(PanelWindow p)
        {
            try { if (panel != null) panel.IsVisibleChanged -= PanelVisibilityChanged; } catch { }
            panel = p;
            try { if (panel != null) panel.IsVisibleChanged += PanelVisibilityChanged; } catch { }
            UpdatePanelState();
        }

        void PanelVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            UpdatePanelState();
        }

        /// <summary>
        /// 注册主面板工厂。开机只显示 44x48 的四叶草按钮；完整面板、目录合并、
        /// 背景和磁贴树留到用户第一次打开时再创建。
        /// </summary>
        public void AttachPanelFactory(Func<PanelWindow> factory)
        {
            panelFactory = factory;
        }

        /// <summary>已创建的“开始”浮动按钮；读取不会隐式构造。</summary>
        public StartButtonWindow StartButton { get { return startWin; } }

        public bool HideTaskbar { get { return hideTaskbar; } }

        StartButtonWindow EnsureStartButton()
        {
            if (startWin != null) return startWin;

            startWin = new StartButtonWindow();
            OverlayGate.Register(startWin,
                delegate { return TaskbarGeometry.StartButtonRectPx(); },
                delegate { return hideTaskbar; },
                delegate { return startWin != null && !startWin.GatePaused; });
            return startWin;
        }

        internal bool TestHasStartButton()
        {
            return startWin != null;
        }

        void Reposition()
        {
            Reposition(false);
        }

        /// <summary>
        /// 重算按钮位置。UIAutomation、任务栏矩形、显示器与 DPI 都由
        /// TaskbarGeometry 的统一快照提供；本窗口只消费物理像素结果。
        /// </summary>
        void Reposition(bool forceLayoutProbe)
        {
            try
            {
                TaskbarGeometry.Refresh(forceLayoutProbe);
                Native.RECT rect = TaskbarGeometry.CloverRectPx();
                ApplyPhysicalRect(rect);
                if (startWin != null) startWin.ApplyTaskbarPosition();
            }
            catch { }
        }

        void OnTaskbarLayoutChanged()
        {
            if (ErrorLog.ShuttingDown) return;
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(OnTaskbarLayoutChanged));
                    return;
                }
                Native.RECT rect = TaskbarGeometry.CloverRectPx();
                ApplyPhysicalRect(rect);
                if (startWin != null) startWin.ApplyTaskbarPosition();
            }
            catch { }
        }

        void ApplyPhysicalRect(Native.RECT rect)
        {
            if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return;
            if (!double.IsNaN(lastLeft) && !double.IsNaN(lastTop)
                && Math.Abs(rect.Left - lastLeft) <= 0.5
                && Math.Abs(rect.Top - lastTop) <= 0.5) return;

            TaskbarGeometry.PositionWindow(this, hwnd, rect, true);
            lastLeft = rect.Left;
            lastTop = rect.Top;
        }

        /// <summary>兼容旧面板调用；返回值实际是四叶草左缘。</summary>
        public static bool TryGetButtonRight(out double rightDip)
        {
            return TaskbarGeometry.TryGetButtonLeft(out rightDip);
        }

        public Native.RECT ActualWindowRectPx()
        {
            Native.RECT r = TaskbarGeometry.ActualWindowRectPx(this);
            return r.Right > r.Left && r.Bottom > r.Top ? r : TaskbarGeometry.CloverRectPx();
        }

        /// <summary>四叶草实际窗口矩形（供现有诊断文字使用）。</summary>
        public Rect ActualWindowRect()
        {
            Native.RECT r = ActualWindowRectPx();
            double scale = TaskbarGeometry.Current.Scale;
            return new Rect(r.Left / scale, r.Top / scale,
                (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale);
        }

        /// <summary>当前四叶草位置所采用的数据来源，供诊断页显示。</summary>
        public string PositionSource
        {
            get
            {
                TaskbarGeometry.Snapshot s = TaskbarGeometry.Current;
                if (hideTaskbar) return "统一任务栏快照（隐藏模式）";
                if (s.TaskButtonsLocated) return "统一任务栏快照（UIAutomation 应用按钮组）";
                if (s.StartLocated) return "统一任务栏快照（UIAutomation 开始按钮）";
                return "统一任务栏快照（" + TaskbarGeometry.SourceDisplayName(s.Source) + "）";
            }
        }

        public static DateTime LastLayoutCalibrated { get { return TaskbarGeometry.LastCalibrated; } }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            hwnd = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(hwnd, -20, Native.GetWindowLong(hwnd, -20) | 0x80);
            ApplyPhysicalRect(TaskbarGeometry.CloverRectPx());
            hwndSource = HwndSource.FromHwnd(hwnd);
            if (hwndSource != null) hwndSource.AddHook(OnHook);
            Native.RegisterHotKey(hwnd, 1, 0x0002 | 0x0001, 0x20);

            // 注册 explorer 重启广播消息（任务栏重建后重新应用隐藏状态）
            try { taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated"); }
            catch { taskbarCreatedMsg = 0; }

            // 顶层窗口创建/销毁会改变任务栏应用按钮组。Shell Hook 负责即时通知，
            // 350ms 防抖后才跑一次 UIA；30 秒定时器只是兜底。
            try { shellHookMsg = Native.RegisterWindowMessage("SHELLHOOK"); }
            catch { shellHookMsg = 0; }
            RegisterShellHook(false);
            QueueTaskbarLayoutRefresh();

            // 置顶与可见性统一由 OverlayGate 的 700ms Tick 管理；这里仅复用它处理
            // 看门狗恢复标记，不再额外创建 1.2 秒置顶定时器。
            OverlayGate.Maintenance = delegate
            {
                MaintainTaskbarSafety();
            };
            if (hideTaskbar)
            {
                // 先把安全网启动，再动系统任务栏；主进程异常退出时才有恢复保障。
                Heartbeat.Start(baseDir);
                Guard.SetRequired(baseDir, true);
                StartButtonWindow start = EnsureStartButton();
                if (start != null) start.Show();
            }
            ApplyTaskbarVisibility(hideTaskbar);
        }

        void RegisterShellHook(bool force)
        {
            try
            {
                if (hwnd == IntPtr.Zero || shellHookMsg == 0) return;
                if (force && shellHookRegistered)
                {
                    try { Native.DeregisterShellHookWindow(hwnd); } catch { }
                    shellHookRegistered = false;
                }
                if (!shellHookRegistered)
                    shellHookRegistered = Native.RegisterShellHookWindow(hwnd);
            }
            catch { shellHookRegistered = false; }
        }

        /// <summary>HSHELL_WINDOWCREATED / HSHELL_WINDOWDESTROYED 会改变任务栏应用按钮组。</summary>
        internal static bool IsTaskbarLayoutShellEvent(int code)
        {
            return code == 1 || code == 2;
        }

        void QueueTaskbarLayoutRefresh()
        {
            try
            {
                if (layoutEventTimer == null) return;
                layoutEventTimer.Stop();
                layoutEventTimer.Start();
            }
            catch { }
        }

        void MaintainTaskbarSafety()
        {
            if (ErrorLog.ShuttingDown || !hideTaskbar) return;
            if (!WatchdogResume.TryConsumeRehidePermission()) return;

            try
            {
                TaskbarVisibility.Apply(true);
                lastLeft = double.NaN;
                lastTop = double.NaN;
                Reposition();
                Ui.Log("卡死恢复：已按用户设置重新隐藏任务栏（本次运行仅此 1 次）");
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("卡死恢复后重新隐藏失败: " + ex.Message);
            }
        }

        IntPtr OnHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312)
            {
                TogglePanel();
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == shellHookMsg && shellHookMsg != 0
                && IsTaskbarLayoutShellEvent(unchecked((int)wParam.ToInt64())))
            {
                QueueTaskbarLayoutRefresh();
                return IntPtr.Zero;
            }

            // 显示器布局、DPI 或任务栏设置改变时同样重新校准。事件合并后执行，
            // 避免系统切换缩放时一串消息触发重复 UIA。
            if (msg == 0x007E || msg == 0x02E0 || msg == 0x001A) // WM_DISPLAYCHANGE / WM_DPICHANGED / WM_SETTINGCHANGE
            {
                QueueTaskbarLayoutRefresh();
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
                    TaskbarGeometry.Reset();
                    RegisterShellHook(true);
                    ApplyTaskbarVisibility(hideTaskbar);
                    lastLeft = double.NaN;
                    lastTop = double.NaN;
                    Reposition(true);
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
            if (panel != null && panel.IsVisible) panel.HidePanel();
            else ShowPanel();
        }

        /// <summary>显示主面板；第一次调用时先异步排到首帧之后构造。</summary>
        public void ShowPanel()
        {
            WithPanel(delegate(PanelWindow p) { p.ShowPanel(); });
        }

        /// <summary>
        /// 在主面板可用后执行动作。用 Background 优先级排队，让“正在加载”光标先画出来，
        /// 避免首次构造 UI 树时按钮看起来像没有响应。
        /// </summary>
        void WithPanel(Action<PanelWindow> action)
        {
            if (panel != null)
            {
                if (action != null) action(panel);
                return;
            }
            if (panelLoading) return;

            panelLoading = true;
            SetPanelLoading(true);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                try
                {
                    PanelWindow ready = EnsurePanel();
                    if (ready != null && action != null) action(ready);
                }
                finally
                {
                    panelLoading = false;
                    SetPanelLoading(false);
                }
            }));
        }

        PanelWindow EnsurePanel()
        {
            if (panel != null) return panel;
            if (panelFactory == null) return null;
            try
            {
                PanelWindow created = panelFactory();
                if (created != null) AttachPanel(created);
            }
            catch (Exception ex)
            {
                ErrorLog.Hook(ex, "首次加载主面板");
                try
                {
                    MessageBox.Show(this,
                        "主面板加载失败：" + ex.Message + "\r\n详细信息已写入 config\\error.log。",
                        "四叶草", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
            }
            return panel;
        }

        void SetPanelLoading(bool loading)
        {
            try
            {
                if (root == null) return;
                root.Cursor = loading ? Cursors.Wait : Cursors.Hand;
                root.Opacity = loading ? 0.62 : 1;
                UpdatePanelState();
            }
            catch { }
        }

        static ControlTemplate MakeTaskbarButtonTemplate()
        {
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty,
                new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty,
                new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty,
                new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(Ui.R.Sm));

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentProperty,
                new TemplateBindingExtension(ContentControl.ContentProperty));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            border.AppendChild(presenter);

            ControlTemplate template = new ControlTemplate(typeof(Button));
            template.VisualTree = border;
            return template;
        }

        /// <summary>
        /// 键盘焦点必须清晰可见。高对比度模式使用系统 HighlightBrush；普通主题复用
        /// Accent，不引入第二套颜色。状态仍同时写入名称和 Tooltip，不只靠颜色。
        /// </summary>
        void ApplyRootVisualState()
        {
            ApplyRootVisualState(root != null && root.IsKeyboardFocused);
        }

        void ApplyRootVisualState(bool focused)
        {
            if (root == null) return;
            root.Background = focused
                ? (SystemParameters.HighContrast ? SystemColors.HighlightBrush : Ui.AccentSoft)
                : (root.IsMouseOver ? HoverBg : IdleBg);
            root.BorderBrush = focused
                ? (SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : Ui.Accent)
                : Brushes.Transparent;
            root.BorderThickness = focused ? new Thickness(2) : new Thickness(0);
        }

        /// <summary>同步任务栏按钮的打开指示、Tooltip 与辅助功能名称。</summary>
        void UpdatePanelState()
        {
            try
            {
                if (root == null) return;

                bool open = panel != null && panel.IsVisible;
                if (panelIndicator != null)
                    panelIndicator.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

                string name;
                string tip;
                if (panelLoading)
                {
                    name = "正在加载四叶草面板";
                    tip = "正在加载主面板…";
                }
                else if (open)
                {
                    name = "收起四叶草面板";
                    tip = "收起四叶草面板";
                }
                else
                {
                    name = "打开四叶草面板";
                    tip = "打开四叶草面板";
                }

                AutomationProperties.SetName(root, name);
                AutomationProperties.SetHelpText(root, "左键" + (open ? "收起" : "打开")
                    + "主面板，右键打开菜单");
                AutomationProperties.SetName(this, name);
                AutomationProperties.SetHelpText(this, "左键" + (open ? "收起" : "打开")
                    + "主面板，右键打开菜单");
                root.ToolTip = tip;
            }
            catch { }
        }

        internal Border TestPanelIndicator { get { return panelIndicator; } }
        internal Button TestRootButton { get { return root; } }
        internal string TestAccessibleName { get { return root == null ? "" : AutomationProperties.GetName(root); } }
        internal void TestApplyFocusVisual(bool focused) { ApplyRootVisualState(focused); }

        /// <summary>上次构建的菜单（仅测试用；生产代码不读它）。</summary>
        internal ContextMenu lastMenu;

        void ShowMenu()
        {
            ContextMenu cm = new ContextMenu();

            // ================================================================
            //  菜单结构（一级，**无级联子菜单**）
            //
            //    重新扫描应用
            //    ─────────
            //    外观…
            //    整理与隐藏…
            //    ─────────
            //    ☑ 开机自启
            //    ─────────
            //    [Shift] 诊断信息…
            //    [Shift] 一键回滚…
            //    设置…
            //    退出
            //
            // 为什么只剩这些：
            //   · 只剩动作（扫描/退出）和「开机自启」一个开关
            //   · 其余配置全收进「设置…」—— 之前有 4 个级联子菜单，
            //     找一个开关要点三层，菜单还越来越长
            //   · 诊断/回滚是"出问题才用"，按住 Shift 才显示
            //   · 打开面板不再占菜单项 —— 托盘图标左键单击就是它
            // ================================================================

            MenuItem rescan = new MenuItem();
            rescan.Header = "重新扫描应用";
            rescan.Click += delegate { WithPanel(delegate(PanelWindow p) { p.Rescan(); }); };
            cm.Items.Add(rescan);

            cm.Items.Add(new Separator());

            // ---- 外观… ----
            MenuItem appearanceItem = new MenuItem();
            appearanceItem.Header = "外观…";
            appearanceItem.ToolTip = "主题、毛玻璃预设、浓度、模糊、染色、亮度、强调色";
            appearanceItem.IsEnabled = GlassBackdrop.Supported;
            if (!GlassBackdrop.Supported)
            {
                appearanceItem.ToolTip = "当前为软件渲染（无硬件加速），毛玻璃会自动拖慢界面，已禁用。";
            }
            appearanceItem.Click += delegate
            {
                    try { AppearanceDialog.Show(this, baseDir, settings, panel, null); }
                catch (Exception ex) { ErrorLog.Warn("打开外观窗口失败: " + ex.Message); }
            };
            cm.Items.Add(appearanceItem);

            // ---- 整理与隐藏… ----
            MenuItem organizeItem = new MenuItem();
            organizeItem.Header = "整理与隐藏…";
            organizeItem.ToolTip = "标签一：桌面保持无快捷方式；标签二：已隐藏的应用";
            organizeItem.Click += delegate
            {
                WithPanel(delegate(PanelWindow ready)
                {
                    try
                    {
                        OrganizeDialog dlg = new OrganizeDialog(
                            this, baseDir, settings, ready.Watcher, ready,
                            delegate { ready.RefreshCatalogQuiet(); },
                            OrganizeDialog.Tab.Desktop);
                        dlg.Show();
                    }
                    catch (Exception ex) { ErrorLog.Warn("打开整理与隐藏窗口失败: " + ex.Message); }
                });
            };
            cm.Items.Add(organizeItem);

            cm.Items.Add(new Separator());

            // ---- 开机自启（勾选开关）----
            MenuItem auto = new MenuItem();
            auto.Header = "开机自启";
            auto.IsCheckable = true;
            auto.IsChecked = File.Exists(startupLnk);
            auto.Click += delegate { ToggleAutostart(); };
            cm.Items.Add(auto);

            cm.Items.Add(new Separator());

            // ---- Shift 隐藏项：诊断 / 回滚 ----
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (shift)
            {
                MenuItem diagItem = new MenuItem();
                diagItem.Header = "诊断信息…";
                diagItem.ToolTip = "任务栏定位、运行状态、崩溃计数";
                diagItem.Click += delegate { OpenSettings(SettingsWindow.Section.Maintenance); };
                cm.Items.Add(diagItem);

                MenuItem rollbackItem = new MenuItem();
                rollbackItem.Header = "一键回滚…";
                rollbackItem.ToolTip = "撤销本程序对系统做过的所有改动";
                rollbackItem.Click += delegate { PromptRollback(); };
                cm.Items.Add(rollbackItem);

                cm.Items.Add(new Separator());
            }

            // ---- 设置… ----
            MenuItem settingsItem = new MenuItem();
            settingsItem.Header = "设置…";
            settingsItem.ToolTip = "常规 / 任务栏 / 维护";
            settingsItem.Click += delegate { OpenSettings(SettingsWindow.Section.General); };
            cm.Items.Add(settingsItem);

            // ---- 退出 ----
            MenuItem exit = new MenuItem();
            exit.Header = "退出";
            exit.Click += delegate
            {
                TaskbarVisibility.RestoreSystemState("用户退出");
                Application.Current.Shutdown();
            };
            cm.Items.Add(exit);

            cm.Placement = PlacementMode.Bottom;
            cm.PlacementTarget = root;
            cm.IsOpen = true;

            // 供 MenuStructureTest 反射读取（生产代码不用它）
            lastMenu = cm;
        }

        // ========== 供 SettingsWindow 调用的公开入口 ==========
        //
        // 设置窗口和右键菜单共用同一批开关，状态必须实时同步。
        // 把底层逻辑集中在这里，两边都调同一份实现，避免两处各写一遍。

        /// <summary>切换开机自启（供设置窗口调用）。</summary>
        public void ToggleAutostartPublic()
        {
            ToggleAutostart();
        }

        /// <summary>设置「隐藏系统任务栏」（供设置窗口调用）。</summary>
        public void SetHideTaskbarPublic(bool on)
        {
            try
            {
                if (!settings.EffectiveTaskbarHiding && on) return;
                if (on)
                {
                    // 先启用心跳和看门狗，再修改系统状态。若随后隐藏任务栏时主进程出错，
                    // 子进程已经有能力恢复，而不是留下不可见任务栏。
                    Heartbeat.Start(baseDir);
                    Guard.SetRequired(baseDir, true);
                    hideTaskbar = true;
                    settings.HideTaskbar = true;
                    settings.Save();
                    TaskbarVisibility.Apply(true);

                    StartButtonWindow start = EnsureStartButton();
                    if (start != null) start.Show();
                }
                else
                {
                    // 先恢复系统任务栏，再停安全网。顺序反过来会产生一个没有看门狗保护、
                    // 任务栏仍不可见的窗口期。
                    TaskbarVisibility.Apply(false);
                    hideTaskbar = false;
                    settings.HideTaskbar = false;
                    settings.Save();
                    if (startWin != null) startWin.Hide();

                    Guard.SetRequired(baseDir, false);
                    Heartbeat.Stop();
                    Heartbeat.Cleanup(baseDir);
                }

                lastLeft = double.NaN;
                lastTop = double.NaN;
                Reposition();
                OverlayGate.Refresh();
                Ui.Log("隐藏系统任务栏 -> " + on);
            }
            catch (Exception ex) { ErrorLog.Warn("设置隐藏系统任务栏失败: " + ex.Message); }
        }

        /// <summary>一键回滚的确认与执行（供设置窗口调用）。</summary>
        public void PromptRollbackPublic(Window owner)
        {
            try
            {
                PromptRollback();
            }
            catch (Exception ex) { ErrorLog.Warn("一键回滚失败: " + ex.Message); }
        }

        /// <summary>打开设置窗口（单例，可定位到指定区块）。</summary>
        void OpenSettings(SettingsWindow.Section section)
        {
            WithPanel(delegate(PanelWindow ready)
            {
                try { SettingsWindow.Open(this, settings, ready, baseDir, section); }
                catch (Exception ex) { ErrorLog.Warn("打开设置窗口失败: " + ex.Message); }
            });
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

                TaskbarGeometry.Snapshot snapshot = TaskbarGeometry.Current;
                string sourceName = TaskbarGeometry.SourceDisplayName(snapshot.Source);
                string calibration = snapshot.TaskButtonsLocated
                    ? "应用按钮组已校准（UIAutomation）"
                    : (snapshot.StartLocated
                        ? "开始按钮已校准（UIAutomation）"
                        : "未校准，使用原生几何估算");

                Rect s = TaskbarGeometry.StartCell;
                Rect c = ActualWindowRect();
                string calibrated = LastLayoutCalibrated == DateTime.MinValue
                    ? "尚未成功"
                    : LastLayoutCalibrated.ToString("yyyy-MM-dd HH:mm:ss");

                string msg =
                    "任务栏位置：" + edgeName + "，厚度 " + Math.Round(TaskbarGeometry.Thickness) + " px\r\n"
                    + "几何来源：" + sourceName + "\r\n"
                    + "精确校准：" + calibration
                    + "\r\n"
                    + "DPI 缩放：" + TaskbarGeometry.ScaleFactor().ToString("0.##") + "\r\n"
                    + "\r\n"
                    + "开始按钮格：" + Fmt(s) + "\r\n"
                    + "四叶草实际窗口：" + Fmt(c) + "\r\n"
                    + "按钮定位来源：" + PositionSource + "\r\n"
                    + "最后按钮组校准：" + calibrated + "\r\n"
                    + "\r\n";

                if (snapshot.Source == "fallback")
                {
                    msg += "⚠ 几何推断失败，已回退到底部 48px。\r\n"
                        + "按钮位置可能不准。常见原因：任务栏刚重建、或被第三方任务栏工具接管。\r\n"
                        + "程序会自动重试，通常几秒后恢复。";
                }
                else if (!TaskbarGeometry.HasPreciseCalibration(snapshot))
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
                    if (!hideTaskbar)
                    {
                        msg += "看门狗：按需待机（仅隐藏系统任务栏时运行）\r\n\r\n";
                    }
                    else
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
                            msg += "看门狗心跳：尚未建立（隐藏模式应自动重试）\r\n\r\n";
                        }
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
