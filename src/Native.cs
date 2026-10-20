using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clover
{
    static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);

        [DllImport("user32.dll")]
        public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(int x, int y);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string name);

        public struct POINT
        {
            public int X;
            public int Y;
        }



        [DllImport("shell32.dll")]
        static extern IntPtr SHAppBarMessage(int message, ref APPBARDATA data);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        // 0x0A=ABM_SETSTATE, 1=ABS_AUTOHIDE, 2=ABS_ALWAYSONTOP
        public static void SetTaskbarAutoHide(bool autoHide)
        {
            APPBARDATA d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            d.lParam = new IntPtr(autoHide ? 1 : 2);
            SHAppBarMessage(0x0A, ref d);
        }

        public static int GetTaskbarState()
        {
            APPBARDATA d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            return SHAppBarMessage(0x04, ref d).ToInt32(); // ABM_GETSTATE
        }



        static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 取应用图标。现在走 IconCache（内存 + 磁盘 PNG 缓存，key 含文件修改时间），
        /// 进程内仍保留一层字典，避免同一路径反复算 key。
        /// </summary>
        public static ImageSource GetAppIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            ImageSource cached;
            if (Cache.TryGetValue(path, out cached))
                return cached;

            ImageSource result = IconCache.Get(path);
            Cache[path] = result;
            return result;
        }

        /// <summary>清掉进程内缓存（重新扫描后调用，让改了图标的程序重新取图）。</summary>
        public static void ClearIconCache()
        {
            Cache.Clear();
        }
    }
}
