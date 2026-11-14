using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Clover
{
    static class Program
    {
        const string MutexName = "CloverStart_SingleInstance_7f3a9c";
        const string ShowEventName = "CloverStart_ShowPanel_7f3a9c";
        const string ExitEventName = "CloverStart_Exit_7f3a9c";

        [STAThread]
        static void Main(string[] args)
        {
            // Embedded full license keeps single-file distribution compliant.
            if (args != null && Array.Exists(args, delegate(string a) { return a == "--licenses"; }))
            {
                System.Reflection.Assembly assembly = typeof(Program).Assembly;
                using (Stream stream = assembly.GetManifestResourceStream("CloverStart.MaterialColors.License.txt"))
                using (StreamReader reader = stream == null ? null : new StreamReader(stream))
                    if (reader != null) MessageBox.Show(reader.ReadToEnd(), AppBrand.Text("四叶草 · Material Color Utilities · Apache-2.0"));
                return;
            }
            Stopwatch startupWatch = Stopwatch.StartNew();
            bool rescan = false;
            bool show = false;
            bool exitRequest = false;
            bool forceSafeMode = false;
            bool restoreOnly = false;
            bool resetAll = false;
            bool resetKeepConfig = false;
            bool enableAutostart = false;
            bool disableAutostart = false;
            bool guard = false;
            int guardPid = 0;
            string guardBaseDir = null;
            string guardStopEvent = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Compare(a, "--rescan", true) == 0) rescan = true;
                else if (string.Compare(a, "--show", true) == 0) show = true;
                else if (string.Compare(a, "--exit", true) == 0) exitRequest = true;
                else if (string.Compare(a, "--safe-mode", true) == 0) forceSafeMode = true;
                else if (string.Compare(a, "--restore-system", true) == 0) restoreOnly = true;
                else if (string.Compare(a, "--reset-all", true) == 0) resetAll = true;
                else if (string.Compare(a, "--keep-config", true) == 0) resetKeepConfig = true;
                else if (string.Compare(a, "--enable-autostart", true) == 0) enableAutostart = true;
                else if (string.Compare(a, "--disable-autostart", true) == 0) disableAutostart = true;
                else if (string.Compare(a, "--guard", true) == 0)
                {
                    guard = true;
                    if (i + 1 < args.Length) int.TryParse(args[i + 1], out guardPid);
                    if (i + 2 < args.Length) guardBaseDir = args[i + 2];
                    if (i + 3 < args.Length) guardStopEvent = args[i + 3];
                }
            }

            // ---------- 程序根目录 ----------
            // 两种分发形态都要支持：
            //   A. 免安装包：exe 在 <根>\app\ 下，根目录放 config\ 和 res\  → 取上一级
            //   B. 单文件 exe：exe 直接放在任意目录，config\ 和 res\ 与它同级 → 取 exe 所在目录
            // 判定依据：exe 所在目录名是 "app" 就用上一级，否则用当前目录。
            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string baseDir;
            if (string.Equals(Path.GetFileName(exeDir), "app", StringComparison.OrdinalIgnoreCase))
            {
                baseDir = Path.GetFullPath(Path.Combine(exeDir, ".."));
            }
            else
            {
                baseDir = exeDir;
            }

            // ---------- 日志接线（必须在任何 core 层调用之前）----------
            // core 层只依赖 Log 抽象，真实实现（error.log / clover.log）在这里注入。
            // 这样 core 层保持零 WPF 依赖，可以单独编译成 DLL。
            Log.Wire(
                delegate(string msg) { ErrorLog.Log(msg); },
                delegate(string msg) { ErrorLog.Warn(msg); },
                delegate(Exception ex, string where) { ErrorLog.Hook(ex, where); },
                delegate(string dir) { ErrorLog.Init(dir); },
                delegate(string msg) { ErrorLog.Diagnostic(msg); });
            Settings.Warn = delegate(string msg) { ErrorLog.Warn(msg); };
            CategoryOverrides.Warn = delegate(string msg) { ErrorLog.Warn(msg); };
            JsonIO.Warn = delegate(string msg) { ErrorLog.Warn(msg); };

            // ---------- 只切换开机自启然后退出（安装/维护脚本用） ----------
            // 放在单实例锁之前：程序已经运行时，维护命令也必须能完成，不能被
            // “通知现有实例显示面板”的分支吃掉。
            if (enableAutostart || disableAutostart)
            {
                ErrorLog.Init(baseDir);
                Autostart.Result result = enableAutostart
                    ? Autostart.Enable(Autostart.ExecutablePath(baseDir))
                    : Autostart.Disable();
                if (result.Success) ErrorLog.Log(result.Message);
                else ErrorLog.Warn(result.Message);
                Environment.ExitCode = result.Success ? 0 : 1;
                return;
            }

            // ---------- 看门狗模式：无界面，只等主进程退出后还原系统状态 ----------
            if (guard)
            {
                Guard.Run(guardPid, string.IsNullOrEmpty(guardBaseDir) ? baseDir : guardBaseDir,
                    guardStopEvent);
                return;
            }

            // ---------- 一键回滚所有系统改动然后退出 ----------
            if (resetAll)
            {
                Log.Init(baseDir);
                TaskbarVisibility.Init(baseDir);
                SystemState.Init(baseDir);
                int failed = 1;
                try
                {
                    failed = ResetAll.Run(baseDir, resetKeepConfig);
                }
                catch (Exception ex)
                {
                    ErrorLog.Hook(ex, "一键回滚");
                    try { Console.Error.WriteLine("一键回滚失败: " + ex.Message); } catch { }
                }
                Environment.Exit(failed == 0 ? 0 : 1);
                return;
            }

            // ---------- 只还原系统状态然后退出（紧急恢复用） ----------
            if (restoreOnly)
            {
                ErrorLog.Init(baseDir);
                TaskbarVisibility.Init(baseDir);
                SystemState.Init(baseDir);
                TaskbarVisibility.RecoverLeftover(baseDir, "--restore-system");
                TaskbarVisibility.RestoreSystemState("--restore-system");
                ErrorLog.Log(BuildInfo.Describe + " 已还原系统状态（--restore-system）");
                return;
            }

            // ---------- 只扫描 ----------
            if (rescan)
            {
                ErrorLog.Init(baseDir);
                IconCache.Init(baseDir);
                ErrorLog.Log(BuildInfo.Describe + " 手动扫描 (--rescan)");
                AppScan.Run(baseDir);
                return;
            }

            bool created;
            Mutex instanceMutex = new Mutex(true, MutexName, out created);
            if (!created)
            {
                // 已经有实例在运行：只发信号给它，然后自己退出。
                //
                // --exit 走优雅退出（触发正常的 OnExit 清理），
                // 这样 dev-run.ps1 重启程序时不会累积假的"异常退出"计数
                // ——强杀 3 次就会误触发安全模式。
                try
                {
                    using (EventWaitHandle ev = EventWaitHandle.OpenExisting(
                        exitRequest ? ExitEventName : ShowEventName))
                    {
                        ev.Set();
                    }
                }
                catch { }
                GC.KeepAlive(instanceMutex);
                return;
            }

            // 没有实例在跑、但用户要的是"退出"：无事可做
            if (exitRequest)
            {
                GC.KeepAlive(instanceMutex);
                return;
            }

            AppWatcher watcher = null;
            MiniTaskbarWindow miniBar = null;
            try
            {
                ErrorLog.Init(baseDir);
                IconCache.Init(baseDir);
                SystemState.Init(baseDir);
                TaskbarVisibility.Init(baseDir);
                CrashGuard.Init(baseDir);
                WatchdogResume.Init(baseDir);
                Settings.Warn = delegate(string msg) { ErrorLog.Warn(msg); };

                ErrorLog.Log(BuildInfo.Describe + " 启动，目录 " + baseDir);

                // ---------- 1) 先还原上次残留的系统状态（在任何窗口创建之前） ----------
                TaskbarVisibility.RecoverLeftover(baseDir, "启动");

                // 清理上次遗留的"看门狗已还原"标记：新一次运行重新计数
                WatchdogResume.Cleanup(baseDir);

                // ---------- 2) 运行标记：处理上次是否正常退出 ----------
                // 必须在 ShouldEnterSafeMode 之前，因为残留标记会补记一次异常退出。
                // 注意：看门狗拉起的还原不算异常退出（那条路径不经过这里）。
                CrashGuard.BeginRun();

                // ---------- 3) 崩溃循环保护 ----------
                Settings settings = Settings.Load(baseDir);
                bool safeMode = forceSafeMode || settings.SafeMode || CrashGuard.ShouldEnterSafeMode(baseDir, forceSafeMode);
                if (safeMode)
                {
                    // 安全模式下强制关闭所有会动系统状态的功能。
                    //
                    // **不再改写 settings 的字段**（原来是 settings.MiniTaskbar = false 等）。
                    // 原因：Save() 会写全部字段，而安全模式期间用户只要动任意一个设置，
                    // 那几项降级就会被永久固化进 settings.json —— 其中
                    // fullscreenDetect / hoverReveal 没有界面开关，一旦固化就永久失效。
                    // 现在改为只置运行时门控，用户的偏好原封不动留在文件里，
                    // 消费方读 Effective* 系列即可（见 Settings.cs 的说明）。
                    Settings.SafeModeActive = true;

                    TaskbarVisibility.RestoreSystemState("进入安全模式");
                    ErrorLog.Warn("进入安全模式：不隐藏任务栏、不启用迷你任务栏、不移动桌面快捷方式、不做全屏检测。"
                        + "（2 分钟内连续异常退出 " + 3 + " 次触发，或用 --safe-mode 强制）"
                        + "。你的设置没被改动，退出安全模式后即恢复。");
                }

                // ---------- 4) 应用主题（必须在任何窗口创建之前）----------
                // 颜色刷子是全局对象，窗口构造时就把它们挂到控件上了；
                // 这里先定好颜色，后续所有窗口一建出来就是对的。
                Ui.ApplyTheme(Ui.ParseTheme(settings.Theme));

                // 自定义强调色（settings.glassAccent）：空 = 用主题默认
                byte ar, ag, ab;
                if (!string.IsNullOrEmpty(settings.GlassAccent)
                    && Settings.TryParseHexColor(settings.GlassAccent, out ar, out ag, out ab))
                {
                    Ui.SetAccentOverride(System.Windows.Media.Color.FromRgb(ar, ag, ab));
                }
                ErrorLog.Log("主题: " + Ui.ThemeDisplayName(Ui.ParseTheme(settings.Theme))
                    + "（实际应用 " + Ui.ThemeDisplayName(Ui.AppliedTheme) + "）");

                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ErrorLog.Bind(app);

                // 所有退出路径统一走 RestoreSystemState + 清除运行标记
                app.Exit += delegate { OnExit(baseDir, watcher, miniBar); };
                app.SessionEnding += delegate
                {
                    TaskbarVisibility.RestoreSystemState("会话结束");
                    CrashGuard.EndRun("会话结束");
                };

                EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                EventWaitHandle exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);

                TriggerWindow trigger = new TriggerWindow(baseDir, settings);
                PanelWindow panel = null;
                watcher = new AppWatcher(baseDir, null, settings);

                Func<MiniTaskbarWindow> ensureMiniBar = delegate
                {
                    if (miniBar != null) return miniBar;

                    MiniTaskbarWindow createdBar = new MiniTaskbarWindow();
                    createdBar.Enabled = settings.EffectiveMiniTaskbar;
                    miniBar = createdBar;
                    if (panel != null) panel.AttachMiniBar(createdBar);

                    OverlayGate.Register(createdBar,
                        delegate { return TaskbarGeometry.MiniBarRectPx(createdBar.Count); },
                        delegate { return createdBar.Count > 0; }, null);
                    return createdBar;
                };

                // 开机关键路径只创建四叶草按钮。完整主面板（目录合并、虚拟桌面、
                // 背景与磁贴树）以及当前为空的迷你任务栏，都在第一次使用时再建。
                Func<PanelWindow> ensurePanel = delegate
                {
                    if (panel != null) return panel;

                    Stopwatch panelWatch = Stopwatch.StartNew();
                    PanelWindow createdPanel = new PanelWindow(baseDir, trigger, settings);

                    createdPanel.AttachMiniBarFactory(ensureMiniBar);
                    createdPanel.AttachWatcher(watcher);

                    panel = createdPanel;
                    watcher.AttachPanel(createdPanel);
                    trigger.AttachPanel(createdPanel);

                    panelWatch.Stop();
                    ErrorLog.Log("主面板首次加载完成，用时 " + panelWatch.ElapsedMilliseconds + "ms");
                    return createdPanel;
                };
                trigger.AttachPanelFactory(ensurePanel);

                // 全屏视频/游戏时隐藏悬浮按钮，鼠标移到按钮位置再临时显示
                OverlayGate.HoldVisible = delegate { return panel != null && panel.IsVisible; };
                OverlayGate.Enabled = settings.EffectiveFullscreenDetect;
                OverlayGate.HoverReveal = settings.EffectiveHoverReveal;
                OverlayGate.Register(trigger,
                    delegate { return trigger.ActualWindowRectPx(); },
                    delegate { return true; }, null);
                OverlayGate.Start();

                app.Startup += delegate
                {
                    // 先让四叶草按钮完成首帧，再启动监听与自检。
                    // 这些功能仍在启动后立即生效，只是不再阻塞可见反馈。
                    app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                    {
                        watcher.Start();
                        // 旧版使用启动文件夹快捷方式，会被 Explorer 分批延迟。
                        // 首帧后在后台升级为零延迟的用户登录计划任务；新任务
                        // 注册和回读校验成功前绝不删除旧快捷方式。
                        ThreadPool.QueueUserWorkItem(delegate
                        {
                            Autostart.Result migration = Autostart.MigrateLegacy(
                                Autostart.ExecutablePath(baseDir));
                            if (!migration.Success || migration.UsingLegacyFallback)
                                ErrorLog.Warn(migration.Message);
                            else if (migration.Changed)
                                ErrorLog.Log(migration.Message);
                        });
                        // 不提前创建任何 WPF 面板对象；只在首帧后预读纯数据目录。
                        CatalogPreloader.WarmAsync(baseDir);
                        SystemSearch.WarmAsync();
                        ResourceWatch.Start();
                        ResourceWatch.Check();
                        if (safeMode) ShowSafeModeTip();
                    }));
                };

                // 稳定运行 5 分钟后清除崩溃计数
                StartStableTimer();

                Thread waiter = new Thread(delegate()
                {
                    WaitHandle[] events = new WaitHandle[] { showEvent, exitEvent };
                    while (true)
                    {
                        try
                        {
                            // WaitAny 返回下标：0 = 显示面板，1 = 请求退出
                            int which = WaitHandle.WaitAny(events);
                            if (which == 1)
                            {
                                // 优雅退出：走 Shutdown 才会触发 app.Exit -> OnExit 清理，
                                // 清除运行标记，下次启动不算异常退出。
                                app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate
                                {
                                    Ui.Log("收到 --exit 请求，正常退出");
                                    Application.Current.Shutdown();
                                }));
                                return;
                            }
                            app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate
                            {
                                trigger.ShowPanel();
                            }));
                        }
                        catch { }
                    }
                });
                waiter.IsBackground = true;
                waiter.Start();

                trigger.Show();
                startupWatch.Stop();
                ErrorLog.Log(AppBrand.Text("启动就绪：四叶草按钮已显示，用时 ")
                    + startupWatch.ElapsedMilliseconds + "ms；主面板将在首次打开时加载");
                if (show) trigger.ShowPanel();

                app.Run();
            }
            catch (Exception ex)
            {
                ErrorLog.Hook(ex, "启动");
                CrashGuard.RecordCrash();
                TaskbarVisibility.RestoreSystemState("启动失败");
                try
                {
                    MessageBox.Show(
                        AppBrand.Text("四叶草启动失败：") + ex.Message + Environment.NewLine +
                        "版本 " + BuildInfo.Version + Environment.NewLine +
                        "详细信息已写入 config\\error.log",
                        AppBrand.Text("四叶草"));
                }
                catch { }
            }
            finally
            {
                // 最后一道保险：无论怎么退出都还原系统状态
                OnExit(baseDir, watcher, miniBar);
                GC.KeepAlive(instanceMutex);
            }
        }

        /// <summary>统一的退出清理：停监听、还原系统状态、清除运行标记。</summary>
        static void OnExit(string baseDir, AppWatcher watcher, MiniTaskbarWindow miniBar)
        {
            try
            {
                // 第一件事：标记正在关停。
                // 之后各定时器的回调会自查这个标记并直接返回 ——
                // 否则它们会继续碰已关闭的窗口，抛出的异常会被记成崩溃计数，
                // 连退 3 次就误判成"连续崩溃"进入安全模式（干净退出不该这样）。
                ErrorLog.ShuttingDown = true;

                OverlayGate.Stop();
                if (watcher != null) watcher.Stop();
                if (miniBar != null) miniBar.StopRefresh();
                ResourceWatch.Stop();
                // 先恢复系统任务栏，再协作式停看门狗；默认模式两者都只是快速空操作。
                TaskbarVisibility.RestoreSystemState("程序退出");
                Guard.Stop();
                Heartbeat.Stop();
                Heartbeat.Cleanup(baseDir);
                // 清除运行标记：表示这次是干净退出，下次启动不算异常退出
                CrashGuard.EndRun("正常退出");
            }
            catch { }
        }

        /// <summary>安全模式提示：弹一次，告知已降级运行。</summary>
        static void ShowSafeModeTip()
        {
            try
            {
                MessageBox.Show(
                    AppBrand.Text("四叶草已进入【安全模式】。\n\n")
                    + "原因：2 分钟内连续异常退出 3 次，或使用了 --safe-mode。\n\n"
                    + "安全模式下已自动关闭：\n"
                    + "  · 隐藏系统任务栏\n"
                    + "  · 迷你任务栏\n"
                    + "  · 自动移走桌面快捷方式\n"
                    + "  · 全屏检测与悬停唤出\n\n"
                    + "面板和按钮仍可正常使用。稳定运行 5 分钟后会自动解除。\n"
                    + "详细日志见 config\\error.log",
                    AppBrand.Text("四叶草 · 安全模式"));
            }
            catch { }
        }

        /// <summary>稳定运行 5 分钟后清除崩溃计数。</summary>
        static void StartStableTimer()
        {
            try
            {
                DispatcherTimer t = new DispatcherTimer();
                t.Interval = TimeSpan.FromSeconds(CrashGuard.StableSeconds);
                t.Tick += delegate
                {
                    t.Stop();
                    CrashGuard.MarkStable();
                };
                t.Start();
            }
            catch { }
        }
    }
}
