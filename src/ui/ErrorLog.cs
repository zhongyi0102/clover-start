using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 崩溃/异常日志：写到 config\error.log，并弹一句提示，避免“闪退”后无从排查。
    /// 每条记录独立一行，格式：[yyyy-MM-dd HH:mm:ss] LEVEL 内容。
    /// 超过 1 MB 自动轮转成 error.log.1（只保留一份旧档）。
    /// 连续出现的完全相同内容不重复写，而是在下一次写入时合并成“×N 次”。
    /// </summary>
    static class ErrorLog
    {
        /// <summary>单份日志上限，超过就轮转。</summary>
        const long MaxBytes = 1024 * 1024;

        static readonly object gate = new object();
        static string logPath;
        static string lastMessage;
        static int repeatCount;      // 上一条消息已连续出现的总次数（含第一次）

        /// <summary>
        /// 正在关停。
        ///
        /// 关停期间各定时器可能还在跳（它们的 Stop 是异步的），
        /// 对已关闭的窗口调 Show/Hide 会抛 InvalidOperationException。
        /// 那属于**关停噪音**，不是真崩溃：
        ///   · 不该写 ERROR 刷屏
        ///   · 更不该走 CrashGuard.RecordCrash() —— 否则每次干净退出都记一次，
        ///     连退 3 次就误判成"连续崩溃"而进入安全模式。
        /// 之前 2026-10-20 和 10-28 各出现过一次这个 ERROR，就是这条路径。
        /// </summary>
        public static volatile bool ShuttingDown = false;

        /// <summary>同一条消息连续出现这么多次时，即使内容没变化也补一条计数汇总。</summary>
        const int RepeatTrace = 100;

        public static void Init(string baseDir)
        {
            try { logPath = Path.Combine(baseDir, "config", "error.log"); } catch { }

            // 1) 非界面线程未处理异常：进程即将终止，必须立刻还原系统状态
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Hook(e.ExceptionObject as Exception, "AppDomain.UnhandledException");
                try { CrashGuard.RecordCrash(); } catch { }
                TaskbarVisibility.RestoreSystemState("未处理异常");
            };

            // 2) 未观察的 Task 异常：不一定终止进程，但说明有代码在吞异常
            TaskScheduler.UnobservedTaskException += delegate(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
            {
                Hook(e.Exception, "TaskScheduler.UnobservedTaskException");
                try { e.SetObserved(); } catch { }
            };

            // 3) 正常关闭（含被任务管理器结束）时也恢复一次
            AppDomain.CurrentDomain.ProcessExit += delegate
            {
                TaskbarVisibility.RestoreSystemState("进程退出");
            };
        }

        public static void Bind(Application app)
        {
            app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
            {
                // 关停期间的异常：只记一行 INFO 说明，不弹窗、不记崩溃。
                // 见 ShuttingDown 的说明 —— 那类异常来自还在跳的定时器，
                // 把它算成崩溃会让"干净退出"累积计数，最终误入安全模式。
                if (ShuttingDown)
                {
                    Write("INFO", "关停期间忽略异常（计时器收尾）: "
                        + (e.Exception == null ? "?" : e.Exception.GetType().Name + ": " + e.Exception.Message));
                    e.Handled = true;
                    return;
                }

                Hook(e.Exception, "DispatcherUnhandledException");
                try { CrashGuard.RecordCrash(); } catch { }
                // 界面线程异常：先把系统状态放回来，再决定是否继续
                TaskbarVisibility.RestoreSystemState("界面线程未处理异常");
                e.Handled = true; // 不让程序直接退出
                try
                {
                    MessageBox.Show("四叶草出错了，详细信息已写入 config\\error.log，程序会继续运行。", "四叶草");
                }
                catch { }
            };
        }

        public static void Hook(Exception ex, string where)
        {
            StringBuilder text = new StringBuilder();
            text.Append(where).Append(": ");
            if (ex != null)
            {
                text.Append(ex.GetType().FullName).Append(": ").Append(ex.Message).Append(Environment.NewLine);
                text.Append(ex.StackTrace);
                Exception inner = ex.InnerException;
                int depth = 0;
                while (inner != null && depth < 5)
                {
                    text.Append(Environment.NewLine)
                        .Append("  --- inner: ").Append(inner.GetType().FullName).Append(": ").Append(inner.Message)
                        .Append(Environment.NewLine)
                        .Append(inner.StackTrace);
                    inner = inner.InnerException;
                    depth++;
                }
            }
            else
            {
                text.Append("(no exception object)");
            }
            Write("ERROR", text.ToString());
        }

        /// <summary>普通信息（默认级别）。</summary>
        public static void Log(string message)
        {
            Write("INFO", message);
        }

        /// <summary>不致命但值得注意的问题（例如某个图标读不出来）。</summary>
        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        /// <summary>明确的错误。</summary>
        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        static void Write(string level, string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            try
            {
                lock (gate)
                {
                    if (string.IsNullOrEmpty(logPath)) return;

                    // 连续相同内容：首次已落盘，之后只累计次数，不再逐条刷屏
                    if (string.Equals(message, lastMessage, StringComparison.Ordinal))
                    {
                        repeatCount++;
                        if (repeatCount % RepeatTrace == 0)
                        {
                            AppendLocked("INFO", "（上一条已连续出现 " + repeatCount + " 次）" + Brief(message));
                        }
                        return;
                    }
                    FlushRepeatLocked();

                    lastMessage = message;
                    repeatCount = 1;
                    AppendLocked(level, message);
                }
            }
            catch { }
        }

        /// <summary>把“上一条重复 N 次”写进日志（调用方必须已持有 gate）。</summary>
        static void FlushRepeatLocked()
        {
            if (repeatCount > 1 && lastMessage != null)
            {
                int total = repeatCount;
                repeatCount = 1;
                AppendLocked("INFO", "（上一条重复，共 " + total + " 次）" + Brief(lastMessage));
            }
        }

        static string Brief(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            string s = message.Replace(Environment.NewLine, " ").Replace("\n", " ").Replace("\r", " ");
            return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
        }

        static void AppendLocked(string level, string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                RotateIfNeededLocked();

                StringBuilder sb = new StringBuilder();
                sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ")
                  .Append(level).Append(' ')
                  .Append(message)
                  .Append(Environment.NewLine);

                File.AppendAllText(logPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>超过上限就轮转：error.log -> error.log.1（旧的 .1 直接丢弃）。</summary>
        static void RotateIfNeededLocked()
        {
            try
            {
                if (!File.Exists(logPath)) return;
                FileInfo fi = new FileInfo(logPath);
                if (fi.Length < MaxBytes) return;

                string old = logPath + ".1";
                try { if (File.Exists(old)) File.Delete(old); } catch { }
                try { File.Move(logPath, old); }
                catch
                {
                    // 移动失败（被占用等）就退化为清空，至少不让文件无限增长
                    try { File.WriteAllText(logPath, "", new UTF8Encoding(false)); } catch { }
                }
                lastMessage = null;
                repeatCount = 0;
            }
            catch { }
        }
    }
}
