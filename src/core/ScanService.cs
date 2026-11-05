using System;
using System.Collections.Generic;
using System.Threading;

namespace Clover
{
    /// <summary>
    /// 扫描调度器：把耗时的扫描（开始菜单遍历 + 便携软件目录递归）放到后台线程，
    /// 界面线程只负责在扫描完成后刷新界面。
    /// 同一时刻只允许一个扫描在跑；运行中收到的新触发会合并成一次补跑，
    /// 既避免多个线程同时写 apps.json，也不会漏掉扫描期间发生的变化。
    /// </summary>
    static class ScanService
    {
        sealed class Batch
        {
            public string BaseDir;
            public string Reason;
            public readonly List<Action<bool>> Callbacks = new List<Action<bool>>();
        }

        static int running;
        static readonly object gate = new object();
        static DateTime lastDone = DateTime.MinValue;
        static Batch pending;

        // 通过字段隔离真实扫描，方便并发回归测试注入可控工作；生产默认永远走 AppScan。
        static Func<string, bool> scanRunner = delegate(string dir) { return AppScan.Run(dir); };

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
        public static bool RunAsync(string baseDir, string reason, Action<bool> onDone)
        {
            if (string.IsNullOrEmpty(baseDir)) return false;

            Batch first = null;
            bool queuedBehindCurrent = false;
            lock (gate)
            {
                if (running != 0)
                {
                    queuedBehindCurrent = true;
                    if (pending == null)
                    {
                        pending = NewBatch(baseDir, reason, onDone);
                    }
                    else
                    {
                        // 多次文件系统事件只需要补扫一次；原因合并供诊断，回调全部保留。
                        pending.BaseDir = baseDir;
                        pending.Reason = MergeReason(pending.Reason, reason);
                        if (onDone != null) pending.Callbacks.Add(onDone);
                    }
                }
                else
                {
                    running = 1;
                    first = NewBatch(baseDir, reason, onDone);
                }
            }

            if (queuedBehindCurrent)
            {
                Log.Info("扫描已在运行，已合并为完成后的补跑: " + (reason ?? "未注明"));
                return true;
            }

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                RunLoop(first);
            });
            return true;
        }

        static Batch NewBatch(string baseDir, string reason, Action<bool> onDone)
        {
            Batch b = new Batch();
            b.BaseDir = baseDir;
            b.Reason = string.IsNullOrWhiteSpace(reason) ? "未注明" : reason.Trim();
            if (onDone != null) b.Callbacks.Add(onDone);
            return b;
        }

        static string MergeReason(string current, string next)
        {
            string n = string.IsNullOrWhiteSpace(next) ? "未注明" : next.Trim();
            if (string.IsNullOrEmpty(current)) return n;
            if (current.IndexOf(n, StringComparison.Ordinal) >= 0) return current;
            string merged = current + " + " + n;
            return merged.Length <= 240 ? merged : merged.Substring(0, 237) + "…";
        }

        static void RunLoop(Batch batch)
        {
            while (batch != null)
            {
                bool ok = false;
                try
                {
                    DateTime started = DateTime.Now;
                    ok = scanRunner(batch.BaseDir);
                    double ms = (DateTime.Now - started).TotalMilliseconds;
                    Log.Info("后台扫描完成(" + batch.Reason + ") ok=" + ok + " 用时 " + (int)ms + "ms");
                    lock (gate) { lastDone = DateTime.Now; }
                }
                catch (Exception ex)
                {
                    Log.Hook(ex, "后台扫描(" + batch.Reason + ")");
                }

                foreach (Action<bool> callback in batch.Callbacks.ToArray())
                {
                    if (callback == null) continue;
                    try { callback(ok); }
                    catch (Exception ex) { Log.Hook(ex, "扫描完成回调"); }
                }

                lock (gate)
                {
                    batch = pending;
                    pending = null;
                    if (batch == null) Interlocked.Exchange(ref running, 0);
                }
            }
        }

        // ---------- 测试接缝（与测试编进同一程序集，不进入公开 API） ----------

        internal static void SetRunnerForTest(Func<string, bool> runner)
        {
            lock (gate)
            {
                if (running != 0) throw new InvalidOperationException("扫描运行中，不能替换 runner");
                scanRunner = runner ?? delegate(string dir) { return AppScan.Run(dir); };
                pending = null;
                lastDone = DateTime.MinValue;
            }
        }

        internal static void ResetForTest()
        {
            SetRunnerForTest(null);
        }
    }
}
