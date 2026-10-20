using System;
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

        [STAThread]
        static void Main(string[] args)
        {
            bool rescan = false;
            bool show = false;
            bool forceSafeMode = false;
            bool restoreOnly = false;
            bool resetAll = false;
            bool resetKeepConfig = false;
            bool guard = false;
            int guardPid = 0;
            string guardBaseDir = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Compare(a, "--rescan", true) == 0) rescan = true;
                else if (string.Compare(a, "--show", true) == 0) show = true;
                else if (string.Compare(a, "--safe-mode", true) == 0) forceSafeMode = true;
                else if (string.Compare(a, "--restore-system", true) == 0) restoreOnly = true;
                else if (string.Compare(a, "--reset-all", true) == 0) resetAll = true;
                else if (string.Compare(a, "--keep-config", true) == 0) resetKeepConfig = true;
                else if (string.Compare(a, "--guard", true) == 0)
                {
                    guard = true;
                    if (i + 1 < args.Length) int.TryParse(args[i + 1], out guardPid);
                    if (i + 2 < args.Length) guardBaseDir = args[i + 2];
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

            // ---------- 看门狗模式：无界面，只等主进程退出后还原系统状态 ----------
            if (guard)
            {
                Guard.Run(guardPid, string.IsNullOrEmpty(guardBaseDir) ? baseDir : guardBaseDir);
                return;
            }

            // ---------- 一键回滚所有系统改动然后退出 ----------
            if (resetAll)
            {
                ErrorLog.Init(baseDir);
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
                // 已经有实例在运行：只通知它打开面板，然后自己退出。
                try
                {
                    using (EventWaitHandle ev = EventWaitHandle.OpenExisting(ShowEventName))
                    {
                        ev.Set();
                    }
                }
                catch { }
                GC.KeepAlive(instanceMutex);
                return;
            }

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
                    // 安全模式下强制关掉所有会动系统状态的功能
                    settings.MiniTaskbar = false;
                    settings.TaskbarHiding = false;
                    settings.HideTaskbar = false;
                    settings.DesktopAutoClean = false;
                    settings.FullscreenDetect = false;
                    settings.HoverReveal = false;
                    TaskbarVisibility.RestoreSystemState("进入安全模式");
                    ErrorLog.Warn("进入安全模式：不隐藏任务栏、不启用迷你任务栏、不移动桌面快捷方式、不做全屏检测。"
                        + "（2 分钟内连续异常退出 " + 3 + " 次触发，或用 --safe-mode 强制）");
                }

                // ---------- 4) 拉起看门狗（安全模式下也拉，它只负责还原） ----------
                Guard.Spawn(baseDir);

                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ErrorLog.Bind(app);

                // 所有退出路径统一走 RestoreSystemState + 清除运行标记
                app.Exit += delegate { OnExit(baseDir); };
                app.SessionEnding += delegate
                {
                    TaskbarVisibility.RestoreSystemState("会话结束");
                    CrashGuard.EndRun("会话结束");
                };

                EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

                TriggerWindow trigger = new TriggerWindow(baseDir, settings);
                PanelWindow panel = new PanelWindow(baseDir, trigger, settings);
                trigger.AttachPanel(panel);

                AppWatcher watcher = new AppWatcher(baseDir, panel, settings);
                panel.AttachWatcher(watcher);
                MiniTaskbarWindow miniBar = new MiniTaskbarWindow();
                miniBar.Enabled = settings.MiniTaskbar;
                panel.AttachMiniBar(miniBar);

                // 全屏视频/游戏时隐藏悬浮按钮，鼠标移到按钮位置再临时显示
                OverlayGate.HoldVisible = delegate { return panel.IsVisible; };
                OverlayGate.Enabled = settings.FullscreenDetect;
                OverlayGate.HoverReveal = settings.HoverReveal;
                OverlayGate.Register(trigger,
                    delegate { return new Rect(trigger.Left, trigger.Top, trigger.Width, trigger.Height); },
                    delegate { return true; }, null);
                OverlayGate.Register(trigger.StartButton,
                    delegate { return TaskbarGeometry.StartButtonRect(); },
                    delegate { return trigger.HideTaskbar; },
                    delegate { return !trigger.StartButton.GatePaused; });
                OverlayGate.Register(miniBar,
                    delegate { return TaskbarGeometry.MiniBarRect(miniBar.Count); },
                    delegate { return miniBar.Count > 0; }, null);
                OverlayGate.Start();

                app.Startup += delegate
                {
                    watcher.Start();
                    ResourceWatch.Start();
                    ResourceWatch.Check();
                    // 心跳：界面线程每 2 秒写一次 config\heartbeat 的时间戳。
                    // 看门狗在主进程没有可检测窗口时，靠它判断是否卡死。
                    Heartbeat.Start(baseDir);
                    if (safeMode) ShowSafeModeTip();
                };

                // 稳定运行 5 分钟后清除崩溃计数
                StartStableTimer();

                Thread waiter = new Thread(delegate()
                {
                    while (true)
                    {
                        try
                        {
                            showEvent.WaitOne();
                            app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate
                            {
                                panel.ShowPanel();
                            }));
                        }
                        catch { }
                    }
                });
                waiter.IsBackground = true;
                waiter.Start();

                trigger.Show();
                if (show) panel.ShowPanel();

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
                        "四叶草启动失败：" + ex.Message + Environment.NewLine +
                        "版本 " + BuildInfo.Version + Environment.NewLine +
                        "详细信息已写入 config\\error.log",
                        "四叶草");
                }
                catch { }
            }
            finally
            {
                // 最后一道保险：无论怎么退出都还原系统状态
                OnExit(baseDir);
                GC.KeepAlive(instanceMutex);
            }
        }

        /// <summary>统一的退出清理：停监听、还原系统状态、清除运行标记。</summary>
        static void OnExit(string baseDir)
        {
            try
            {
                ResourceWatch.Stop();
                Heartbeat.Stop();
                Heartbeat.Cleanup(baseDir);
                TaskbarVisibility.RestoreSystemState("程序退出");
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
                    "四叶草已进入【安全模式】。\n\n"
                    + "原因：2 分钟内连续异常退出 3 次，或使用了 --safe-mode。\n\n"
                    + "安全模式下已自动关闭：\n"
                    + "  · 隐藏系统任务栏\n"
                    + "  · 迷你任务栏\n"
                    + "  · 自动移走桌面快捷方式\n"
                    + "  · 全屏检测与悬停唤出\n\n"
                    + "面板和按钮仍可正常使用。稳定运行 5 分钟后会自动解除。\n"
                    + "详细日志见 config\\error.log",
                    "四叶草 · 安全模式");
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
