using System;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 心跳的**定时器部分**（属于 UI 层）。
    ///
    /// 界面线程每 2 秒写一次 config\heartbeat；界面线程卡住时心跳自然就停了——
    /// 这正是看门狗要检测的信号。
    ///
    /// 实际的文件读写下沉到 core 层的 HeartbeatFile（零 WPF），
    /// 这样看门狗（core）不需要依赖 UI。
    /// </summary>
    static class Heartbeat
    {
        /// <summary>心跳间隔（供 core 层查超时用）。</summary>
        public const int IntervalMs = HeartbeatFile.IntervalMs;

        static DispatcherTimer timer;
        static string baseDir;
        static bool writeWarned;

        public static void Start(string dir)
        {
            try
            {
                if (timer != null) return;
                baseDir = dir;

                timer = new DispatcherTimer(DispatcherPriority.Background);
                timer.Interval = TimeSpan.FromMilliseconds(IntervalMs);
                timer.Tick += delegate { Beat(); };
                timer.Start();
                Beat(); // 立刻写一次，避免刚启动时没有时间戳
            }
            catch (Exception ex)
            {
                Log.Warn("启动心跳失败: " + ex.Message);
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
                if (string.IsNullOrEmpty(baseDir)) return;
                HeartbeatFile.Write(baseDir);
            }
            catch (Exception ex)
            {
                if (!writeWarned)
                {
                    writeWarned = true;
                    Log.Warn("写心跳失败（后续不再重复提示）: " + ex.Message);
                }
            }
        }

        /// <summary>删除心跳文件（正常退出时调用，避免下次误判）。</summary>
        public static void Cleanup(string dir)
        {
            HeartbeatFile.Cleanup(dir);
        }

        /// <summary>读心跳时间戳（UTC ticks）。读不到返回 0。</summary>
        public static long ReadTicks(string dir)
        {
            return HeartbeatFile.ReadTicks(dir);
        }
    }
}
