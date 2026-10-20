using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Clover
{
    /// <summary>判断当前是否处于“全屏应用”状态（视频/游戏），用于隐藏悬浮按钮。</summary>
    static class FullscreenWatch
    {
        [DllImport("shell32.dll")]
        static extern int SHQueryUserNotificationState(out int state);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public Native.RECT rcMonitor;
            public Native.RECT rcWork;
            public uint dwFlags;
        }

        const uint MONITOR_DEFAULTTONEAREST = 2;
        const uint MONITORINFOF_PRIMARY = 1;

        [DllImport("user32.dll")]
        static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        static extern int GetWindowLong32(IntPtr hWnd, int index);

        /// <summary>
        /// 全屏判定：
        /// 1) 系统通知状态（全屏游戏/D3D/演示模式）；
        /// 2) 前台窗口铺满**它所在的那块显示器**，且没有标题栏（真正的全屏/无边框窗口）。
        /// 桌面（Progman/WorkerW）和普通最大化窗口不算全屏——否则隐藏任务栏后
        /// 任何最大化的窗口都会把按钮误判成“全屏中”。
        /// 多显示器：按窗口所在显示器判断，副屏全屏也能正确识别。
        /// </summary>
        public static bool IsFullscreen()
        {
            try
            {
                int state;
                if (SHQueryUserNotificationState(out state) == 0)
                {
                    // 2 = 忙碌(全屏应用), 3 = D3D 全屏, 4 = 演示模式
                    if (state == 2 || state == 3 || state == 4) return true;
                }
            }
            catch { }

            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return false;
                if (h == GetDesktopWindow() || h == GetShellWindow()) return false;

                string cls = ClassOf(h);
                if (cls == "Progman" || cls == "WorkerW" ||
                    cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;

                int pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == Process.GetCurrentProcess().Id) return false;

                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) return false;

                // 用窗口所在显示器判断，而不是主屏尺寸：
                // 原来用 GetSystemMetrics(0/1)（主屏），副屏上全屏的窗口会被判成非全屏。
                Native.RECT mon;
                if (!TryGetMonitorRect(h, out mon))
                {
                    // 拿不到显示器信息时退回主屏尺寸（至少不比以前更差）
                    int sw0 = GetSystemMetrics(0);
                    int sh0 = GetSystemMetrics(1);
                    if (sw0 <= 0 || sh0 <= 0) return false;
                    mon = new Native.RECT { Left = 0, Top = 0, Right = sw0, Bottom = sh0 };
                }

                int monW = mon.Right - mon.Left;
                int monH = mon.Bottom - mon.Top;
                if (monW <= 0 || monH <= 0) return false;

                // 必须完全盖住它所在的那块屏幕（连任务栏区域都盖住）
                if (!(r.Left <= mon.Left && r.Top <= mon.Top && r.Right >= mon.Right && r.Bottom >= mon.Bottom)) return false;

                // 带标题栏的是普通最大化窗口，不是全屏
                int style = GetWindowLong32(h, -16);
                if ((style & 0x00C00000) != 0) return false; // WS_CAPTION

                return true;
            }
            catch { }
            return false;
        }

        /// <summary>取窗口所在显示器的完整矩形（含任务栏区域）。</summary>
        static bool TryGetMonitorRect(IntPtr hwnd, out Native.RECT rect)
        {
            rect = new Native.RECT();
            try
            {
                IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (mon == IntPtr.Zero) return false;
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (!GetMonitorInfo(mon, ref mi)) return false;
                rect = mi.rcMonitor;
                return true;
            }
            catch
            {
                return false;
            }
        }

        static string ClassOf(IntPtr h)
        {
            try
            {
                StringBuilder sb = new StringBuilder(256);
                GetClassName(h, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }
    }
}
