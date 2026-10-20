using System;
using System.Threading;

namespace Clover
{
    /// <summary>
    /// 扫描调度器：把耗时的扫描（开始菜单遍历 + 便携软件目录递归）放到后台线程，
    /// 界面线程只负责在扫描完成后刷新界面。
    /// 同一时刻只允许一个扫描在跑，重复触发会被忽略（避免多个线程同时写 apps.json）。
    /// </summary>
    static class ScanService
    {
        static int running;
        static readonly object gate = new object();
        static DateTime lastDone = DateTime.MinValue;

        /// <summary>是否有扫描正在进行。</summary>
        public static bool Busy
        {
            get { return Interlocked.CompareExchange(ref running, 0, 0) != 0; }
        }

        /// <summary>上一次扫描完成的时间（用于日志/诊断）。</summary>
        public static DateTime LastDone
        {
            get { lock (gate) { return lastDone; } }
        }

        /// <summary>
        /// 后台扫描。onDone 会在扫描结束（成功或失败）后于后台线程调用，
        /// 调用方需要自己用 Dispatcher 回到界面线程。
        /// </summary>
        public static void RunAsync(string baseDir, string reason, Action<bool> onDone)
        {
            if (string.IsNullOrEmpty(baseDir)) return;
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            {
                Ui.Log("扫描已在运行，忽略本次触发: " + reason);
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                bool ok = false;
                try
                {
                    DateTime started = DateTime.Now;
                    ok = AppScan.Run(baseDir);
                    double ms = (DateTime.Now - started).TotalMilliseconds;
                    Ui.Log("后台扫描完成(" + reason + ") ok=" + ok + " 用时 " + (int)ms + "ms");
                    lock (gate) { lastDone = DateTime.Now; }
                }
                catch (Exception ex)
                {
                    ErrorLog.Hook(ex, "后台扫描(" + reason + ")");
                }
                finally
                {
                    Interlocked.Exchange(ref running, 0);
                    if (onDone != null)
                    {
                        try { onDone(ok); }
                        catch (Exception ex) { ErrorLog.Hook(ex, "扫描完成回调"); }
                    }
                }
            });
        }
    }
}
