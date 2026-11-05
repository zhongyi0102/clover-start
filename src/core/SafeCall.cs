using System;
using System.Threading;

namespace Clover
{
    /// <summary>
    /// 给 UIAutomation / Shell / COM 这类可能长时间阻塞的调用加超时。
    ///
    /// 重要（踩过的坑）：UIAutomation 的 FindFirst 内部会向目标窗口发消息并等回复，
    /// 如果调用方是**界面线程**且在这里同步 Wait，界面线程被占住 → 消息泵停转 →
    /// UIAutomation 永远等不到回复 → 必然超时，反而比不设超时更糟（每轮卡 2 秒）。
    ///
    /// 所以约定：
    ///   - 界面线程上**不要**用 TryRun 等结果；按语义选择 RunInBackground 或
    ///     RunWithDeadline，结果回来后由调用方切回界面线程
    ///   - TryRun 只用于真正的后台线程（没有消息泵依赖的调用）
    /// </summary>
    static class SafeCall
    {
        /// <summary>默认超时（毫秒）。</summary>
        public const int DefaultTimeoutMs = 2000;

        /// <summary>
        /// 纯后台执行：不设截止时间，完成或失败后恰好回调一次。
        /// 回调发生在后台线程；需要操作 WPF 时由调用方切回 Dispatcher。
        /// </summary>
        public static void RunInBackground<T>(Func<T> func, Action<bool, T> onDone)
        {
            if (func == null || onDone == null) return;
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                T result = default(T);
                bool ok = false;
                try
                {
                    result = func();
                    ok = true;
                }
                catch (Exception ex)
                {
                    Log.Warn("后台调用失败: " + ex.GetType().Name + ": " + ex.Message);
                    ok = false;
                }

                try { onDone(ok, result); }
                catch (Exception ex) { Log.Warn("后台调用回调失败: " + ex.Message); }
            });
        }

        /// <summary>
        /// 带截止时间的异步执行。界面线程不会阻塞：
        ///   · 截止时间内完成 → 回调真实结果；
        ///   · 超时 → 立刻回调 false/默认值；底层调用继续自然结束，但迟到结果被丢弃。
        /// 无论哪条路径，onDone 都恰好调用一次。
        /// </summary>
        public static void RunWithDeadline<T>(Func<T> func, int timeoutMs, Action<bool, T> onDone)
        {
            if (func == null || onDone == null) return;
            if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;

            int completed = 0;
            DateTime started = DateTime.Now;
            Timer timer = null;

            timer = new Timer(delegate(object state)
            {
                if (Interlocked.CompareExchange(ref completed, 1, 0) != 0) return;
                Log.Info("后台调用超时(" + timeoutMs + "ms)，迟到结果将被丢弃");
                try { onDone(false, default(T)); }
                catch (Exception ex) { Log.Warn("后台调用超时回调失败: " + ex.Message); }
                try { timer.Dispose(); } catch { }
            }, null, timeoutMs, Timeout.Infinite);

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                T result = default(T);
                bool ok = false;
                try
                {
                    result = func();
                    ok = true;
                }
                catch (Exception ex)
                {
                    Log.Warn("后台调用失败: " + ex.GetType().Name + ": " + ex.Message);
                }

                double ms = (DateTime.Now - started).TotalMilliseconds;
                if (Interlocked.CompareExchange(ref completed, 1, 0) != 0)
                {
                    Log.Info("后台调用在 " + (int)ms + "ms 后返回，结果已按截止时间丢弃");
                    return;
                }

                try { timer.Dispose(); } catch { }
                try { onDone(ok, result); }
                catch (Exception ex) { Log.Warn("后台调用回调失败: " + ex.Message); }
            });
        }

        /// <summary>兼容旧调用；新代码请显式选择 RunInBackground 或 RunWithDeadline。</summary>
        public static void RunAsync<T>(Func<T> func, int timeoutMs, Action<bool, T> onDone)
        {
            RunWithDeadline(func, timeoutMs, onDone);
        }

        /// <summary>
        /// 在后台线程执行 func，最多等 timeoutMs。
        /// **只能在后台线程调用**（例如看门狗、扫描线程）。
        /// </summary>
        public static bool TryRun<T>(Func<T> func, int timeoutMs, out T result)
        {
            result = default(T);
            if (func == null) return false;
            if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;

            T local = default(T);
            Exception error = null;
            ManualResetEvent done = new ManualResetEvent(false);

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try { local = func(); }
                catch (Exception ex) { error = ex; }
                finally
                {
                    try { done.Set(); } catch { }
                }
            });

            bool finished = false;
            try { finished = done.WaitOne(timeoutMs); }
            catch { finished = false; }
            finally { try { done.Close(); } catch { } }

            if (!finished)
            {
                Log.Info("后台调用超时(" + timeoutMs + "ms)，走回退值");
                return false;
            }
            if (error != null)
            {
                Log.Warn("后台调用失败: " + error.GetType().Name + ": " + error.Message);
                return false;
            }

            result = local;
            return true;
        }

        /// <summary>无返回值的版本。</summary>
        public static bool TryRun(Action action, int timeoutMs)
        {
            bool ignored;
            return TryRun<bool>(delegate
            {
                action();
                return true;
            }, timeoutMs, out ignored);
        }
    }
}
