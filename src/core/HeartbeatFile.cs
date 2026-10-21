using System;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 心跳文件的**读写**部分（属于 core 层，零 WPF 依赖）。
    ///
    /// 心跳的用途：看门狗判断主进程界面线程是否卡死。
    /// 界面线程每 2 秒把当前时间写进 config\heartbeat；
    /// 看门狗读它，超过 2 个周期没更新就认为卡死。
    ///
    /// 为什么拆开：定时器（DispatcherTimer）属于 UI 层，
    /// 而看门狗（core 层）只需要读这个文件。拆开后 core 不再依赖 UI。
    /// 写入的实际发起由 UI 层的 HeartbeatWriter 负责。
    /// </summary>
    static class HeartbeatFile
    {
        /// <summary>心跳间隔。看门狗按 2 倍间隔判超时。</summary>
        public const int IntervalMs = 2000;

        public static string PathOf(string baseDir)
        {
            return Path.Combine(baseDir, "config", "heartbeat");
        }

        /// <summary>写入一次心跳（UTC ticks）。失败不抛异常。</summary>
        public static void Write(string baseDir)
        {
            try
            {
                string p = PathOf(baseDir);
                string dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // UTC ticks：看门狗直接比较数值，不受时区/格式影响
                File.WriteAllText(p, DateTime.UtcNow.Ticks.ToString(),
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
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
