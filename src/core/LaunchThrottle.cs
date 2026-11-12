using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Clover
{
    /// <summary>
    /// 应用启动请求的短时去重。
    ///
    /// 调用方先用 IsBlocked 查询；只有真正发出启动请求后才调用 MarkSent。
    /// 因此 Process.Start 失败不会进入冷却期，用户可以立即重试。
    /// </summary>
    sealed class LaunchThrottle
    {
        readonly long windowTicks;
        readonly Dictionary<string, long> lastSent =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public LaunchThrottle(int windowMilliseconds)
        {
            if (windowMilliseconds < 0) windowMilliseconds = 0;
            windowTicks = (long)(Stopwatch.Frequency * (windowMilliseconds / 1000.0));
        }

        public bool IsBlocked(string key, long nowTimestamp)
        {
            if (string.IsNullOrWhiteSpace(key) || windowTicks <= 0) return false;

            long sentAt;
            if (!lastSent.TryGetValue(key, out sentAt)) return false;

            long elapsed = nowTimestamp - sentAt;
            return elapsed >= 0 && elapsed < windowTicks;
        }

        public void MarkSent(string key, long nowTimestamp)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            lastSent[key] = nowTimestamp;
        }
    }
}
