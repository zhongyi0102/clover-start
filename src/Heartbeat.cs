using System;
using System.IO;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 心跳文件：界面线程每 2 秒把当前时间写进 config\heartbeat。
    ///
    /// 用途：看门狗判断主进程是否卡死。优先用 IsHungAppWindow（主窗口）；
    /// 主进程没有可检测窗口时（例如窗口全被隐藏），退回读这个文件的时间戳。
    ///
    /// 约束：
    ///   - 只在界面线程的定时器里跑，界面线程卡住时心跳自然就停了——这正是要检测的信号
    ///   - 写一个很小的文本文件，失败只记一次日志，不重试
    /// </summary>
    static class Heartbeat
    {
        /// <summary>心跳间隔。看门狗按 2 倍间隔判超时。</summary>
        public const int IntervalMs = 2000;

        static DispatcherTimer timer;
        static string path;
        static bool writeWarned;

        public static string PathOf(string baseDir)
        {
            return Path.Combine(baseDir, "config", "heartbeat");
        }

        public static void Start(string baseDir)
        {
            try
            {
                if (timer != null) return;
                path = PathOf(baseDir);

                timer = new DispatcherTimer(DispatcherPriority.Background);
                timer.Interval = TimeSpan.FromMilliseconds(IntervalMs);
                timer.Tick += delegate { Beat(); };
                timer.Start();
                Beat(); // 立刻写一次，避免刚启动时没有时间戳
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("启动心跳失败: " + ex.Message);
            }
        }

        public static void Stop()
        {
            try { if (timer != null) timer.Stop(); } catch { }
        }

        static void Beat()
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // UTC ticks：看门狗直接比较数值，不受时区/格式影响
                File.WriteAllText(path, DateTime.UtcNow.Ticks.ToString(),
                    new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                if (!writeWarned)
                {
                    writeWarned = true;
                    ErrorLog.Warn("写心跳失败（后续不再重复提示）: " + ex.Message);
                }
            }
        }

        /// <summary>删除心跳文件（正常退出时调用，避免下次误判）。</summary>
        public static void Cleanup(string baseDir)
        {
            try
            {
                string p = PathOf(baseDir);
                if (File.Exists(p)) File.Delete(p);
            }
            catch { }
        }

        /// <summary>
        /// 读心跳时间戳（UTC ticks）。读不到返回 0。
        /// 看门狗用这个判断主进程界面线程是否还活着。
        /// </summary>
        public static long ReadTicks(string baseDir)
        {
            try
            {
                string p = PathOf(baseDir);
                if (!File.Exists(p)) return 0;
                string text = File.ReadAllText(p, System.Text.Encoding.UTF8).Trim();
                long ticks;
                if (long.TryParse(text, out ticks)) return ticks;
                return 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
