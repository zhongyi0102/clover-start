using System;
using System.Runtime.InteropServices;
using System.Text;

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

        /// <summary>GWL_STYLE：窗口样式位（WS_* 系列）。</summary>
        public const int GWL_STYLE = -16;

        /// <summary>WS_MINIMIZEBOX：标题栏的最小化按钮。</summary>
        public const int WS_MINIMIZEBOX = 0x00020000;

        // SetWindowPos 的 uFlags（只列本文件用到的）
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_FRAMECHANGED = 0x0020;

        /// <summary>
        /// 去掉标题栏的最小化按钮。
        ///
        /// ── 为什么需要它 ──
        ///
        /// 项目的对话框都是 `ShowInTaskbar = false`（不占任务栏），
        /// 但 `ResizeMode = CanResize` 会让系统标题栏带上最小化按钮。
        /// 两者矛盾：最小化后任务栏没有图标，窗口再也找不回来。
        ///
        /// WPF 的 `ResizeMode` 只有三档（NoResize / CanMinimize /
        /// CanResize / CanResizeWithGrip），**没有"可调大小但没有最小化"**
        /// 这一档。所以只能改 Win32 窗口样式位。
        ///
        /// ── 什么时候调 ──
        ///
        /// 必须在窗口句柄建立之后（`SourceInitialized` 或 `Loaded`）。
        /// 在构造函数里调 `WindowInteropHelper.Handle` 会拿到 0。
        /// </summary>
        public static void RemoveMinimizeBox(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;

                int style = GetWindowLong(hwnd, GWL_STYLE);
                if ((style & WS_MINIMIZEBOX) == 0) return;   // 已经没有，不用改

                SetWindowLong(hwnd, GWL_STYLE, style & ~WS_MINIMIZEBOX);

                // 样式改动后要重画标题栏，否则按钮还在视觉上留着。
                // SetWindowPos 与 SWP_* 常量在文件下方已有声明（第 129 行附近）。
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        // ========== 无边框窗口的边缘拖拽缩放 ==========

        /// <summary>左键按在非客户区（标题栏/边框）上，由系统接管拖动或缩放。</summary>
        public const int WM_NCLBUTTONDOWN = 0x00A1;

        public const int HTLEFT = 10;
        public const int HTRIGHT = 11;
        public const int HTTOP = 12;
        public const int HTTOPLEFT = 13;
        public const int HTTOPRIGHT = 14;
        public const int HTBOTTOM = 15;
        public const int HTBOTTOMLEFT = 16;
        public const int HTBOTTOMRIGHT = 17;

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

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

        // ---- 按键状态（拖放检测用）----
        // GetAsyncKeyState 读物理按键状态，不受窗口焦点影响。
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        /// <summary>鼠标左键虚拟键码。</summary>
        public const int VK_LBUTTON = 0x01;

        /// <summary>左键当前是否被按住（物理状态）。</summary>
        public static bool IsLeftButtonDown()
        {
            try { return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0; }
            catch { return false; }
        }

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(int x, int y);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        /// <summary>GA_ROOT = 2，取顶层窗口。</summary>
        public const uint GA_ROOT = 2;

        /// <summary>取窗口类名（失败返回空串）。</summary>
        public static string ClassNameOf(IntPtr hWnd)
        {
            try
            {
                if (hWnd == IntPtr.Zero) return "";
                StringBuilder sb = new StringBuilder(256);
                int n = GetClassName(hWnd, sb, sb.Capacity);
                return n > 0 ? sb.ToString() : "";
            }
            catch { return ""; }
        }

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
        /// 取系统任务栏的真实位置矩形（物理像素）。
        ///
        /// `ABM_GETTASKBARPOS` 比 `SystemParameters.WorkArea` 可靠：
        ///   · WorkArea 只反映"被任务栏占掉的工作区"，任务栏自动隐藏时
        ///     WorkArea 等于全屏，就推不出任务栏在哪条边、多厚
        ///   · 任务栏被本程序隐藏后，WorkArea 也会变成全屏
        /// 而 ABM_GETTASKBARPOS 直接返回任务栏窗口自己的矩形与贴边方向，
        /// 不受隐藏状态影响。
        /// </summary>
        /// <param name="edge">贴哪条边：0=左 1=上 2=右 3=下</param>
        /// <param name="rc">任务栏矩形（物理像素）</param>
        /// <returns>取到返回 true</returns>
        public static bool GetTaskbarPos(out uint edge, out RECT rc)
        {
            edge = 3;
            rc = new RECT();
            try
            {
                APPBARDATA d = new APPBARDATA();
                d.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
                IntPtr r = SHAppBarMessage(0x05, ref d); // ABM_GETTASKBARPOS
                if (r == IntPtr.Zero) return false;

                edge = d.uEdge;
                rc = d.rc;
                return rc.Right > rc.Left && rc.Bottom > rc.Top;
            }
            catch { return false; }
        }

        /// <summary>任务栏是否处于自动隐藏状态（ABS_AUTOHIDE = 0x1）。</summary>
        public static bool TaskbarAutoHide()
        {
            try { return (GetTaskbarState() & 0x1) != 0; }
            catch { return false; }
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

        // ========== 模态窗口的 Owner 解禁 ==========

        [DllImport("user32.dll")]
        public static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

        [DllImport("user32.dll")]
        public static extern bool IsWindowEnabled(IntPtr hWnd);

        /// <summary>
        /// 启用/禁用窗口（传句柄，不传 WPF 的 Window —— core 层零 WPF 依赖）。
        ///
        /// ── 为什么需要它 ──
        ///
        /// WPF 的 `ShowDialog()` 是**模态**：子窗口显示期间，系统会
        /// `EnableWindow(owner, false)` 把 Owner 禁掉。这是模态的定义。
        ///
        /// 但子窗口可以最小化。最小化后：
        ///   · 子窗口看不见了
        ///   · Owner 仍被禁用（点不动）
        ///   · 用户感觉整个程序卡死
        ///
        /// 所以子窗口最小化时把 Owner 解禁，恢复时再禁回去。
        /// 这不破坏模态语义 —— 子窗口可见时它仍然是模态的。
        ///
        /// ── 为什么不用 WPF 的 owner.IsEnabled ──
        ///
        /// WPF 的 `IsEnabled` 和 Win32 的 `EnableWindow` 是两层：
        /// 模态禁用是 Win32 层做的，改 WPF 属性改不动它。
        /// </summary>
        public static void SetWindowEnabled(IntPtr hwnd, bool enabled)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;

                // 只在状态真的需要变时才调，避免无谓的 Win32 往返
                bool now = IsWindowEnabled(hwnd);
                if (now == enabled) return;

                EnableWindow(hwnd, enabled);
            }
            catch { }
        }
    }
}
