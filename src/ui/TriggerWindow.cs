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
            // 左键单击 = 打开/收起面板（「打开面板」菜单项已移除，这里是唯一入口）
            root.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; TogglePanel(); };
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

        /// <summary>
        /// 四叶草按钮的真实右边缘（DIP），来自任务栏图标组的 UIA 探测。
        ///
        /// 为什么需要对外暴露：面板的 PositionPanel() 原来用
        /// TaskbarGeometry.CloverRect() 算位置，那套基于"开始按钮格"，
        /// 算出来 x=59；而四叶草按钮实际贴在**任务栏图标组末尾**（x=411）。
        /// 两套定位机制不一致，导致面板和按钮错位 350px。
        ///
        /// 返回 false 表示探测还没完成，调用方应先用估算值。
        /// </summary>
        public static bool TryGetButtonRight(out double rightDip)
        {
            rightDip = 0;
            if (!hasCachedLayout) return false;
            if (cachedButtonRight <= 0) return false;
            double scale = 1.0;
            try
            {
                uint dpi = Native.GetDpiForSystem();
                if (dpi > 0) scale = dpi / 96.0;
            }
            catch { }
            rightDip = cachedButtonRight / scale;
            return true;
        }

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
            rescan.Click += delegate { if (panel != null) panel.Rescan(); };
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
                try
                {
                    OrganizeDialog dlg = new OrganizeDialog(
                        this, baseDir, settings, panel == null ? null : panel.Watcher, panel,
                        delegate { if (panel != null) panel.RefreshCatalogQuiet(); },
                        OrganizeDialog.Tab.Desktop);
                    dlg.Show();
                }
                catch (Exception ex) { ErrorLog.Warn("打开整理与隐藏窗口失败: " + ex.Message); }
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

                hideTaskbar = on;
                settings.HideTaskbar = on;
                settings.Save();

                TaskbarVisibility.Apply(on);
                if (on) startWin.Show();
                else startWin.Hide();

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
            try
            {
                SettingsWindow.Open(this, settings, panel, baseDir, section);
            }
            catch (Exception ex) { ErrorLog.Warn("打开设置窗口失败: " + ex.Message); }
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
