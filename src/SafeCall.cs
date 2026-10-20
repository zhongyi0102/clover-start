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
    ///   - 界面线程上**不要**用 TryRun 等结果；用 RunAsync，结果回到界面线程再处理
    ///   - TryRun 只用于真正的后台线程（没有消息泵依赖的调用）
    /// </summary>
    static class SafeCall
    {
        /// <summary>默认超时（毫秒）。</summary>
        public const int DefaultTimeoutMs = 2000;

        /// <summary>
        /// 在后台线程执行 func 并把结果回调到调用方指定的调度器（通常是界面线程）。
        /// 界面线程不阻塞：发起后立刻返回。
        /// timeoutMs 只用于判断“要不要丢弃这次结果”，不会阻塞任何线程。
        /// </summary>
        public static void RunAsync<T>(Func<T> func, int timeoutMs, Action<bool, T> onDone)
        {
            if (func == null || onDone == null) return;
            if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;

            DateTime started = DateTime.Now;
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
                    ErrorLog.Warn("后台调用失败: " + ex.GetType().Name + ": " + ex.Message);
                    ok = false;
                }

                double ms = (DateTime.Now - started).TotalMilliseconds;
                if (ok && ms > timeoutMs)
                {
                    Ui.Log("后台调用耗时 " + (int)ms + "ms 超过阈值 " + timeoutMs + "ms，结果仍采用");
                }

                try { onDone(ok, result); }
                catch (Exception ex) { ErrorLog.Warn("后台调用回调失败: " + ex.Message); }
            });
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
                Ui.Log("后台调用超时(" + timeoutMs + "ms)，走回退值");
                return false;
            }
            if (error != null)
            {
                ErrorLog.Warn("后台调用失败: " + error.GetType().Name + ": " + error.Message);
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
