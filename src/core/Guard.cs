using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Clover
{
    /// <summary>
    /// 看门狗（--guard 模式）。
    ///
    /// 做两件事：
    ///   1. 等主进程退出 → 若 system-state.json 还有未还原标记，就还原系统状态
    ///   2. 每秒检查主进程是否**卡死** → 连续 10 秒无响应且状态未还原，就还原任务栏
    ///
    /// 卡死判定（优先用系统接口，最后才退回心跳文件）：
    ///   a. 主进程有主窗口 → IsHungAppWindow（系统自己的"无响应"判定）
    ///   b. 没有可检测窗口 → 读 config\heartbeat 的时间戳，超过 2 个心跳周期没更新即视为卡死
    ///
    /// 约束（按要求）：
    ///   - 无界面、无常驻窗口
    ///   - 轮询 1 秒，只读一个小文件，开销可忽略
    ///   - 只还原任务栏，**绝不杀主进程**
    ///   - 同一次运行里最多自动重新隐藏 1 次（避免来回抖动）
    ///   - 任何异常都不外抛
    /// </summary>
    static class Guard
    {
        /// <summary>轮询主进程的间隔。</summary>
        const int PollMs = 1000;

        /// <summary>连续判定无响应多久算卡死。</summary>
        const int HungSeconds = 10;

        /// <summary>心跳超过这个时长没更新就算卡死（2 个心跳周期 + 余量）。</summary>
        const int HeartbeatTimeoutMs = HeartbeatFile.IntervalMs * 2 + 1000;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool IsHungAppWindow(IntPtr hWnd);

        /// <summary>
        /// 以看门狗身份运行。参数：--guard &lt;主进程PID&gt; &lt;baseDir&gt;
        /// </summary>
        public static void Run(int parentPid, string baseDir)
        {
            try
            {
                Log.Init(baseDir);
                TaskbarVisibility.Init(baseDir);
                SystemState.Init(baseDir);

                long lastHeartbeatTicks = 0;
                int hungSeconds = 0;
                bool restoredForHung = false;

                Process parent = null;
                try { parent = Process.GetProcessById(parentPid); }
                catch { parent = null; }

                if (parent != null)
                {
                    try
                    {
                        while (!parent.HasExited)
                        {
                            Thread.Sleep(PollMs);

                            // ---------- 卡死检测 ----------
                            if (parent.HasExited) break;

                            bool hung;
                            try { hung = IsParentHung(parent, baseDir, ref lastHeartbeatTicks); }
                            catch { hung = false; }

                            if (hung)
                            {
                                hungSeconds++;
                                if (hungSeconds >= HungSeconds && !restoredForHung)
                                {
                                    // 确认卡死：只有存在未还原状态时才动手
                                    SystemState st = SystemState.Load(baseDir);
                                    if (st != null && st.Dirty)
                                    {
                                        restoredForHung = true;
                                        Log.Warn("看门狗：主进程连续 " + HungSeconds
                                            + " 秒无响应（疑似卡死），已把系统任务栏还原。"
                                            + "（不会结束主进程）");
                                        TaskbarVisibility.RestoreSystemState("看门狗·卡死检测");
                                        // 留下标记：主进程恢复后最多允许自动重新隐藏 1 次
                                        WatchdogResume.MarkRestored(baseDir);
                                    }
                                }
                            }
                            else if (restoredForHung)
                            {
                                // 主进程恢复响应了：交给主进程自己决定要不要再隐藏
                                // （主进程侧用 WatchdogResume 限制为最多 1 次）
                                Log.Info("看门狗：主进程已恢复响应（本次运行最多允许自动重新隐藏 1 次）");
                                restoredForHung = false;
                                hungSeconds = 0;
                            }
                            else
                            {
                                hungSeconds = 0;
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        try { parent.Dispose(); } catch { }
                    }
                }

                // ---------- 主进程已退出 ----------
                SystemState leftover = SystemState.Load(baseDir);
                if (leftover != null && leftover.Dirty)
                {
                    Log.Warn("看门狗：主进程异常退出，检测到未还原的系统状态（"
                        + leftover.Since + "），正在还原");
                    TaskbarVisibility.RecoverLeftover(baseDir, "看门狗");
                }

                // 清理心跳文件，避免下次启动误判
                HeartbeatFile.Cleanup(baseDir);
            }
            catch (Exception ex)
            {
                // 看门狗自己绝不能抛到外面，否则会变成错误弹窗
                try { Log.Warn("看门狗异常: " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// 判断主进程是否卡死。优先用 IsHungAppWindow，没有窗口时退回心跳文件。
        /// </summary>
        static bool IsParentHung(Process parent, string baseDir, ref long lastHeartbeatTicks)
        {
            // a. 有主窗口 → 用系统的"无响应"判定
            try
            {
                IntPtr hwnd = parent.MainWindowHandle;
                if (hwnd != IntPtr.Zero) return IsHungAppWindow(hwnd);
            }
            catch { }

            // b. 没有可检测窗口 → 用心跳
            long ticks = HeartbeatFile.ReadTicks(baseDir);
            if (ticks <= 0) return false; // 读不到就不判断，避免误杀

            if (lastHeartbeatTicks == 0)
            {
                lastHeartbeatTicks = ticks;
                return false;
            }

            if (ticks != lastHeartbeatTicks)
            {
                lastHeartbeatTicks = ticks;
                return false; // 心跳在更新，说明界面线程还活着
            }

            // 心跳没变：看它停在多久之前
            long ageMs = (long)(DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalMilliseconds;
            return ageMs > HeartbeatTimeoutMs;
        }

        /// <summary>
        /// 主进程启动时调用：拉起一个 --guard 子进程。
        /// 失败不影响主功能（只是少了最后一道保险），写一条日志即可。
        /// </summary>
        public static void Spawn(string baseDir)
        {
            try
            {
                string exe = Path.Combine(baseDir, "app", "CloverStart.exe");
                if (!File.Exists(exe)) return;

                ProcessStartInfo psi = new ProcessStartInfo(exe);
                psi.Arguments = "--guard " + Process.GetCurrentProcess().Id + " \"" + baseDir + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
                Log.Info("看门狗已启动");
            }
            catch (Exception ex)
            {
                Log.Warn("启动看门狗失败（不影响主功能）: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 看门狗与主进程之间的"重新隐藏许可"。
    ///
    /// 场景：主进程卡死 → 看门狗还原任务栏 → 主进程恢复响应 → 用户仍勾选"隐藏任务栏"
    ///      → 主进程想再隐藏。为了不来回抖动，同一次运行只允许自动重新隐藏 1 次。
    ///
    /// 跨进程传递：看门狗还原后写一个标记文件 config\guard-restored；
    /// 主进程侧在"自动应用隐藏"前读它，用完删掉，保证整轮运行只放行 1 次。
    /// </summary>
    static class WatchdogResume
    {
        static string markerPath;

        public static void Init(string baseDir)
        {
            try { markerPath = Path.Combine(baseDir, "config", "guard-restored"); } catch { }
        }

        /// <summary>看门狗还原后调用：留下"我已还原过"的标记，供主进程读取。</summary>
        public static void MarkRestored(string baseDir)
        {
            try
            {
                string p = Path.Combine(baseDir, "config", "guard-restored");
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                try { Log.Warn("写 guard-restored 标记失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// 主进程在"自动重新应用隐藏"前调用。
        /// 返回 true 表示这次允许隐藏（且是看门狗还原后的第一次）；标记用完即删。
        /// </summary>
        public static bool TryConsumeRehidePermission()
        {
            try
            {
                if (string.IsNullOrEmpty(markerPath) || !File.Exists(markerPath)) return false;
                File.Delete(markerPath);
                Log.Info("检测到看门狗曾还原任务栏，允许本次自动重新隐藏（整轮仅 1 次）");
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>启动时清理上次遗留的标记。</summary>
        public static void Cleanup(string baseDir)
        {
            try
            {
                string p = Path.Combine(baseDir, "config", "guard-restored");
                if (File.Exists(p)) File.Delete(p);
            }
            catch { }
        }
    }
}
