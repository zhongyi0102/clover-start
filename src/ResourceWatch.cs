using System;
using System.Diagnostics;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 资源自检：每 10 分钟记录一次内存与句柄数，超阈值时写 WARN 并清理缓存。
    ///
    /// 阈值：内存 400 MB / 句柄 3000。
    /// 清理只动“可以安全丢弃”的缓存，不会动用户数据：
    ///   1) 应用图标内存缓存（最安全，代价只是重新解码）
    ///   2) 窗口列表图标缓存
    ///
    /// 实测结论（重要）：WPF 透明窗口（AllowsTransparency=true）在反复 Show/Hide 时
    /// 会把工作集推高到约 200 MB 后**收敛不再增长**，那是渲染表面预热，不是泄漏。
    /// 预热后继续开关 40 次只涨 3.7 MB（单次 0.09 MB）。所以 400 MB 阈值正常不会触发。
    /// </summary>
    static class ResourceWatch
    {
        const int IntervalMs = 10 * 60 * 1000;
        const long MemoryWarnBytes = 400L * 1024 * 1024;
        const int HandleWarn = 3000;

        static DispatcherTimer timer;
        static bool warned;
        static long peakBytes;

        public static void Start()
        {
            if (timer != null) return;
            timer = new DispatcherTimer(DispatcherPriority.Background);
            timer.Interval = TimeSpan.FromMilliseconds(IntervalMs);
            timer.Tick += delegate { Check(); };
            timer.Start();
        }

        public static void Stop()
        {
            try { if (timer != null) timer.Stop(); } catch { }
        }

        /// <summary>立即检查一次（启动后也会调一次，便于建立基线）。</summary>
        public static void Check()
        {
            try
            {
                using (Process p = Process.GetCurrentProcess())
                {
                    long mem = p.WorkingSet64;
                    int handles = p.HandleCount;
                    double memMb = mem / 1024.0 / 1024.0;

                    if (mem > peakBytes) peakBytes = mem;

                    bool overMem = mem > MemoryWarnBytes;
                    bool overHandle = handles > HandleWarn;

                    if (overMem || overHandle)
                    {
                        // 只在“第一次越线”时写 WARN，避免每 10 分钟刷屏
                        if (!warned)
                        {
                            warned = true;
                            ErrorLog.Warn("资源占用偏高：内存 " + memMb.ToString("0.0") + " MB，句柄 " + handles
                                + " 个（阈值 " + (MemoryWarnBytes / 1024 / 1024) + " MB / " + HandleWarn + " 个）");
                        }
                        // 逐级释放可丢弃的缓存
                        try { Native.ClearIconCache(); } catch { }
                        try { TaskList.ClearIconCache(); } catch { }
                        // 主动回收一次，给 WPF 归还渲染表面的机会
                        try { GC.Collect(2, GCCollectionMode.Forced, false, false); } catch { }
                        Ui.Log("资源自检：已清理图标缓存并强制回收");
                    }
                    else
                    {
                        warned = false;
                        Ui.Log("资源自检：内存 " + memMb.ToString("0.0") + " MB（峰值 "
                            + (peakBytes / 1024.0 / 1024.0).ToString("0.0") + " MB），句柄 " + handles + " 个");
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("资源自检失败: " + ex.Message);
            }
        }
    }
}
