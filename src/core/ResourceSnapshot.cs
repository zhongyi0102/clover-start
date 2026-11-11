using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Clover
{
    /// <summary>进程资源快照，供运行时自检与独立压力测试共用。</summary>
    sealed class ResourceSnapshot
    {
        public DateTime AtUtc;
        public long WorkingSetBytes;
        public long PrivateBytes;
        public long ManagedBytes;
        public int Handles;
        public int Threads;
        public int GdiObjects;
        public int UserObjects;

        [DllImport("user32.dll")]
        static extern int GetGuiResources(IntPtr process, int flags);

        public static ResourceSnapshot Capture()
        {
            ResourceSnapshot s = new ResourceSnapshot();
            s.AtUtc = DateTime.UtcNow;
            try
            {
                using (Process p = Process.GetCurrentProcess())
                {
                    p.Refresh();
                    s.WorkingSetBytes = p.WorkingSet64;
                    s.PrivateBytes = p.PrivateMemorySize64;
                    s.Handles = p.HandleCount;
                    s.Threads = p.Threads.Count;
                    try { s.GdiObjects = GetGuiResources(p.Handle, 0); } catch { }
                    try { s.UserObjects = GetGuiResources(p.Handle, 1); } catch { }
                }
                s.ManagedBytes = GC.GetTotalMemory(false);
            }
            catch { }
            return s;
        }

        public static bool IsConverging(ResourceSnapshot warm, ResourceSnapshot end,
            int measuredCycles, out string reason)
        {
            reason = "";
            if (warm == null || end == null || measuredCycles <= 0)
            {
                reason = "缺少有效快照";
                return false;
            }
            long privateGrowth = end.PrivateBytes - warm.PrivateBytes;
            long managedGrowth = end.ManagedBytes - warm.ManagedBytes;
            int handleGrowth = end.Handles - warm.Handles;
            int gdiGrowth = end.GdiObjects - warm.GdiObjects;
            int userGrowth = end.UserObjects - warm.UserObjects;

            // 先预热再观察后半段；阈值既能容纳 WPF/DWM 缓存波动，也能抓到
            // 每次循环稳定增加对象/句柄的线性泄漏。
            bool ok = privateGrowth <= 48L * 1024 * 1024
                && managedGrowth <= 16L * 1024 * 1024
                // 固定波动预算能容纳 WPF 一次性缓存；不能按“每轮一个”放宽，
                // 否则恰好会放过最典型的线性句柄/GDI/USER 泄漏。
                && handleGrowth <= Math.Max(40, measuredCycles / 3)
                && gdiGrowth <= Math.Max(30, measuredCycles / 4)
                && userGrowth <= Math.Max(30, measuredCycles / 4);
            reason = "private=" + ToMb(privateGrowth) + "MB, managed=" + ToMb(managedGrowth)
                + "MB, handles=" + handleGrowth + ", gdi=" + gdiGrowth + ", user=" + userGrowth;
            return ok;
        }

        static string ToMb(long bytes)
        {
            return (bytes / 1024.0 / 1024.0).ToString("0.0");
        }
    }
}
