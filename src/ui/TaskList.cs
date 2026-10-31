using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clover
{
    class TaskWindow
    {
        public IntPtr Hwnd;
        public string Title;
        public ImageSource Icon;

        /// <summary>该窗口所属进程 ID（用于按应用路径匹配窗口）。</summary>
        public int Pid;
    }

    /// <summary>枚举当前可见的顶层窗口，供面板内的“任务栏”使用。</summary>
    static class TaskList
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const uint GW_OWNER = 4;
        const uint WM_GETICON = 0x007F;
        const int ICON_SMALL2 = 2;
        const int ICON_SMALL = 0;
        const int GCLP_HICONSM = -34;
        const int GCLP_HICON = -14;
        const int DWMWA_CLOAKED = 14;

        delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")]
        static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetClassLongPtr(IntPtr hWnd, int index);
        [DllImport("user32.dll")]
        static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("dwmapi.dll")]
        static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        [DllImport("user32.dll")]
        static extern bool IsWindow(IntPtr hWnd);

        public static IntPtr GetActiveWindow()
        {
            return GetForegroundWindow();
        }

        public static bool IsWindowAlive(IntPtr hWnd)
        {
            return hWnd != IntPtr.Zero && IsWindow(hWnd) && IsWindowVisible(hWnd);
        }

        public static TaskWindow GetWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return null;
            try
            {
                TaskWindow w = new TaskWindow();
                w.Hwnd = hWnd;
                StringBuilder sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, sb.Capacity);
                w.Title = sb.ToString();
                w.Icon = GetIcon(hWnd);
                return w;
            }
            catch { return null; }
        }

        /// <summary>
        /// 找出某个应用路径对应的**运行中窗口**。
        ///
        /// ── 匹配方式（三级，从严到宽）──
        ///
        ///   1. **exe 路径完全一致** —— 最可靠。取窗口进程的 MainModule 路径
        ///      与应用的 exe 路径比较（大小写不敏感、统一分隔符）。
        ///   2. **exe 文件名一致** —— 路径取不到时（权限不足读 MainModule）
        ///      退一步用进程名比较，如 `chrome.exe`。
        ///   3. **标题包含应用名** —— 前两级都失败时的兜底，可能误判，
        ///      所以只在**唯一命中**时才采用。
        ///
        /// ── 为什么不用 RunningApps ──
        ///
        /// `RunningApps` 只回答"在不在运行"，不返回窗口句柄。
        /// 这里需要具体的 HWND 才能钉到迷你任务栏上。
        ///
        /// ── 性能 ──
        ///
        /// 只在右键菜单**打开时**调一次，不在渲染循环里，可以接受。
        /// </summary>
        /// <param name="appPath">应用的 .lnk 或 .exe 路径</param>
        /// <param name="appName">应用显示名（第 3 级兜底用）</param>
        public static List<TaskWindow> GetWindowsOfApp(string appPath, string appName)
        {
            List<TaskWindow> result = new List<TaskWindow>();
            try
            {
                if (string.IsNullOrWhiteSpace(appPath)) return result;

                // 解析 .lnk 得到真实 exe 路径
                string exePath = appPath;
                if (appPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    exePath = ResolveLnkTarget(appPath);
                    if (string.IsNullOrEmpty(exePath)) exePath = appPath;
                }

                string exeFull = NormalizePath(exePath);
                string exeName = "";
                try { exeName = Path.GetFileName(exePath) ?? ""; }
                catch { }

                if (exeName.Length == 0 && exeFull.Length > 0)
                {
                    try { exeName = Path.GetFileName(exeFull); } catch { }
                }

                List<TaskWindow> all = GetWindows(Process.GetCurrentProcess().Id);
                if (all.Count == 0) return result;

                // ---- 1 + 2 级：按 exe 路径 / 文件名 ----
                List<TaskWindow> byPath = new List<TaskWindow>();
                List<TaskWindow> byName = new List<TaskWindow>();

                foreach (TaskWindow w in all)
                {
                    string wPath = null;
                    try
                    {
                        Process p = Process.GetProcessById(w.Pid);
                        if (p != null && p.MainModule != null)
                            wPath = p.MainModule.FileName;
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(wPath))
                    {
                        string wp = NormalizePath(wPath);
                        if (exeFull.Length > 0 && wp == exeFull) { byPath.Add(w); continue; }

                        if (exeName.Length > 0
                            && string.Equals(Path.GetFileName(wp), exeName, StringComparison.OrdinalIgnoreCase))
                        {
                            byName.Add(w);
                            continue;
                        }
                    }
                    else if (exeName.Length > 0)
                    {
                        // MainModule 读不到 → 用进程名兜底（不带 .exe 比较）
                        try
                        {
                            Process p = Process.GetProcessById(w.Pid);
                            string pname = p.ProcessName;
                            if (!string.IsNullOrEmpty(pname)
                                && string.Equals(pname + ".exe", exeName, StringComparison.OrdinalIgnoreCase))
                                byName.Add(w);
                        }
                        catch { }
                    }
                }

                if (byPath.Count > 0) return byPath;
                if (byName.Count > 0) return byName;

                // ---- 3 级：标题包含应用名（只在唯一命中时采用）----
                if (!string.IsNullOrWhiteSpace(appName))
                {
                    List<TaskWindow> byTitle = new List<TaskWindow>();
                    foreach (TaskWindow w in all)
                    {
                        if (string.IsNullOrEmpty(w.Title)) continue;
                        if (w.Title.IndexOf(appName, StringComparison.OrdinalIgnoreCase) >= 0)
                            byTitle.Add(w);
                    }
                    if (byTitle.Count == 1) return byTitle;
                }

                return result;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "查找应用窗口");
                return result;
            }
        }

        /// <summary>路径归一化：统一分隔符 + 小写，用于比较。</summary>
        static string NormalizePath(string p)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(p)) return "";
                return p.Trim().Replace('/', '\\').ToLowerInvariant();
            }
            catch { return ""; }
        }

        /// <summary>解析 .lnk 的目标（WScript.Shell）。失败返回 null。</summary>
        static string ResolveLnkTarget(string lnk)
        {
            object shell = null;
            object sc = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;

                shell = Activator.CreateInstance(t);
                if (shell == null) return null;

                sc = t.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell,
                    new object[] { lnk });
                if (sc == null) return null;

                object tp = sc.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.GetProperty, null, sc, null);
                string s = tp as string;
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            catch { return null; }
            finally
            {
                try { if (sc != null && System.Runtime.InteropServices.Marshal.IsComObject(sc)) System.Runtime.InteropServices.Marshal.ReleaseComObject(sc); } catch { }
                try { if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell)) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); } catch { }
            }
        }

        public static bool IsActive(IntPtr hWnd)
        {
            return GetForegroundWindow() == hWnd;
        }

        public static List<TaskWindow> GetWindows(int ownPid)
        {
            PruneIconCache();
            List<TaskWindow> list = new List<TaskWindow>();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                try
                {
                    if (!IsWindowVisible(h)) return true;
                    if (GetWindowTextLength(h) == 0) return true;
                    if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                    if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
                    int cloaked;
                    if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true;
                    int pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (pid == ownPid) return true;

                    StringBuilder sb = new StringBuilder(512);
                    GetWindowText(h, sb, sb.Capacity);
                    string title = sb.ToString();
                    if (string.IsNullOrEmpty(title)) return true;

                    TaskWindow w = new TaskWindow();
                    w.Hwnd = h;
                    w.Title = title;
                    w.Icon = GetIconCached(h, pid);
                    w.Pid = pid;
                    list.Add(w);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public static void Minimize(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            ShowWindow(hWnd, 6); // SW_MINIMIZE
        }

        public static void CloseWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            PostMessage(hWnd, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
        }

        public static void Activate(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            if (IsIconic(hWnd)) ShowWindow(hWnd, 9); // SW_RESTORE
            SetForegroundWindow(hWnd);
        }

        /// <summary>
        /// 窗口图标缓存：key = 窗口句柄，value = 图标 + 该窗口的进程ID。
        /// 每次 RefreshTaskList 都会枚举所有窗口，如果每次都重新 CreateBitmapSourceFromHIcon，
        /// 位图会持续累积（实测反复开关面板 30 次涨 100+ MB）。
        /// 这里按句柄缓存，进程变化（句柄复用）时自动失效。
        /// </summary>
        static readonly System.Collections.Generic.Dictionary<IntPtr, IconEntry> iconCache =
            new System.Collections.Generic.Dictionary<IntPtr, IconEntry>();

        class IconEntry
        {
            public ImageSource Icon;
            public int Pid;
        }

        static ImageSource GetIconCached(IntPtr hWnd, int pid)
        {
            try
            {
                IconEntry e;
                if (iconCache.TryGetValue(hWnd, out e) && e != null && e.Pid == pid)
                    return e.Icon;

                ImageSource icon = GetIcon(hWnd);
                iconCache[hWnd] = new IconEntry { Icon = icon, Pid = pid };
                return icon;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>清空窗口图标缓存（窗口大量变化或退出时调用）。</summary>
        public static void ClearIconCache()
        {
            lock (iconCache) { iconCache.Clear(); }
        }

        /// <summary>清掉已销毁窗口的缓存条目，避免句柄复用导致图标错乱。</summary>
        static void PruneIconCache()
        {
            try
            {
                List<IntPtr> dead = null;
                foreach (IntPtr h in iconCache.Keys)
                {
                    if (h == IntPtr.Zero || !IsWindow(h))
                    {
                        if (dead == null) dead = new List<IntPtr>();
                        dead.Add(h);
                    }
                }
                if (dead != null)
                {
                    lock (iconCache)
                    {
                        foreach (IntPtr h in dead) iconCache.Remove(h);
                    }
                }
            }
            catch { }
        }

        static ImageSource GetIcon(IntPtr hWnd)
        {
            IntPtr hIcon = SendMessage(hWnd, WM_GETICON, new IntPtr(ICON_SMALL2), IntPtr.Zero);
            if (hIcon == IntPtr.Zero) hIcon = SendMessage(hWnd, WM_GETICON, new IntPtr(ICON_SMALL), IntPtr.Zero);
            if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hWnd, GCLP_HICONSM);
            if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hWnd, GCLP_HICON);
            if (hIcon == IntPtr.Zero) return null;
            try
            {
                ImageSource img = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                img.Freeze();
                return img;
            }
            catch { return null; }
        }
    }
}
