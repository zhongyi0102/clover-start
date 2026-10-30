using System;
using System.Runtime.InteropServices;

namespace Clover
{
    /// <summary>
    /// P/Invoke 声明与 Win32 结构体。
    ///
    /// 本文件属于 **core 层**：纯 P/Invoke，零 WPF 依赖，可单独编译成 DLL。
    /// 需要 WPF 的部分（图标 → ImageSource）已拆到 ui\AppIcons.cs。
    /// </summary>
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

        /// <summary>
        /// 窗口所在显示器的 DPI（Win10 1607+）。
        ///
        /// 比 GetDpiForSystem 准：多显示器混合 DPI 时，每个窗口在**自己那块屏**
        /// 上的 DPI 可能不同，用系统 DPI 算截屏区域会错位。
        /// 旧系统上此函数不存在（DllNotFoundException），调用方要兜住。
        /// </summary>
        [DllImport("user32.dll")]
        public static extern int GetDpiForWindow(IntPtr hWnd);

        /// <summary>
        /// 设置/查询系统参数（用它换桌面壁纸）。
        ///
        /// SPIF_UPDATEINIFILE = 0x01（写入用户配置，重启后仍生效）
        /// SPIF_SENDCHANGE    = 0x02（广播 WM_SETTINGCHANGE，让其他程序立刻刷新）
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SystemParametersInfo(
            uint uiAction, uint uiParam, string pvParam, uint fWinIni);

        public const uint SPIF_UPDATEINIFILE = 0x01;
        public const uint SPIF_SENDCHANGE = 0x02;


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

        public static int GetTaskbarState()
        {
            APPBARDATA d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            return SHAppBarMessage(0x04, ref d).ToInt32(); // ABM_GETSTATE
        }
        /// <summary>
        /// 显示系统的「文件属性」对话框（与资源管理器右键 → 属性一致）。
        ///
        /// 为什么用 SHObjectProperties 而不是 ShellExecuteEx(verb=properties)：
        ///   · SHObjectProperties 是专为此设计的 API，直接给 hwnd 让对话框
        ///     挂在面板上（否则会飘到桌面左上角）
        ///   · ShellExecuteEx 需要 SEE_MASK_INVOKEIDLIST + lpVerb，
        ///     而且返回值要手动释放
        ///
        /// 只读：只显示信息，不修改任何文件。
        /// </summary>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType,
            string pszObjectName, string pszPropertyPage);

        /// <summary>SHOP_FILEPATH：pszObjectName 是完整路径。</summary>
        const uint SHOP_FILEPATH = 0x2;

        /// <summary>显示属性对话框。失败时静默（不影响主流程）。</summary>
        public static void ShowFileProperties(string path, IntPtr ownerHwnd)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                SHObjectProperties(ownerHwnd, SHOP_FILEPATH, path, null);
            }
            catch { }
        }
    }
}
