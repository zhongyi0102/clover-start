using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Clover
{
    /// <summary>
    /// 全屏检测集成测试。
    ///
    /// 做法：起一个子进程（本 exe 的 --host 模式）创建**真正的无边框全屏窗口**，
    /// 把它设为前台，然后父进程调 FullscreenWatch.IsFullscreen() 断言返回 true；
    /// 再关掉子进程，断言恢复成 false。
    ///
    /// 为什么必须用子进程：FullscreenWatch 会主动排除「自己进程的窗口」
    /// （pid == 当前进程 → 不算全屏），所以同进程建窗口测不出来。
    ///
    /// 编译：
    ///   csc /target:exe /r:System.dll /r:System.Core.dll /r:WindowsBase.dll ...
    ///       src\FullscreenWatch.cs src\Native.cs tests\FullscreenTest.cs
    /// </summary>
    static class FullscreenTest
    {
        const string HostTitle = "CloverFullscreenTestHost";
        static int failed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name + (cond || detail.Length == 0 ? "" : "  [" + detail + "]"));
            if (!cond) failed++;
        }

        // ---------------- 子进程：创建全屏窗口并保持 ----------------
        const int WS_POPUP = unchecked((int)0x80000000);
        const int WS_VISIBLE = 0x10000000;

        delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")]
        static extern bool GetMessage(out MSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")]
        static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")]
        static extern IntPtr DispatchMessage(ref MSG m);
        [DllImport("user32.dll")]
        static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string cls, string name);

        static IntPtr HostProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            return DefWindowProc(h, msg, w, l);
        }

        /// <summary>子进程入口：建一个铺满屏幕的无边框窗口，然后一直跑消息循环。</summary>
        static int RunHost()
        {
            WNDCLASSEX wc = new WNDCLASSEX();
            wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
            wc.lpfnWndProc = HostProc;
            wc.lpszClassName = "CloverFSHost";
            RegisterClassEx(ref wc);

            int sw = GetSystemMetrics(0);
            int sh = GetSystemMetrics(1);

            IntPtr hwnd = CreateWindowEx(0, "CloverFSHost", HostTitle, WS_POPUP | WS_VISIBLE,
                0, 0, sw, sh, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return 2;

            ShowWindow(hwnd, 5);
            SetForegroundWindow(hwnd);

            MSG m;
            while (GetMessage(out m, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref m);
                DispatchMessage(ref m);
            }
            return 0;
        }

        // ---------------- 父进程：测试 ----------------
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--host")
            {
                // 子进程：建全屏窗口并保持。设一个最长存活时间，避免测试异常时留孤儿进程。
                Thread guard = new Thread(delegate()
                {
                    Thread.Sleep(30000);
                    Environment.Exit(0);
                });
                guard.IsBackground = true;
                guard.Start();

                Environment.Exit(RunHost());
                return;
            }

            // ---------- 1. 没有全屏窗口时：不应误判 ----------
            bool before = FullscreenWatch.IsFullscreen();
            Check(before == false, "无全屏窗口时 IsFullscreen() = false", "实际 " + before);

            // ---------- 2. 起子进程建全屏窗口 ----------
            Process host = null;
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                ProcessStartInfo psi = new ProcessStartInfo(exe, "--host");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = false;
                host = Process.Start(psi);

                // 等窗口出现（按类名找，标题在不同系统语言/主题下可能被改写）
                IntPtr hwnd = IntPtr.Zero;
                for (int i = 0; i < 50; i++)
                {
                    Thread.Sleep(100);
                    hwnd = FindWindow("CloverFSHost", null);
                    if (hwnd != IntPtr.Zero) break;
                }
                Check(hwnd != IntPtr.Zero, "子进程创建了全屏窗口");
                if (hwnd == IntPtr.Zero)
                {
                    Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
                    Environment.Exit(failed == 0 ? 0 : 1);
                }

                // 把全屏窗口抢到前台。SetForegroundWindow 对非前台进程可能被系统拒绝，
                // 而且测试机器上常有别的窗口（Chrome/QQ/微信）在抢焦点，所以这里重试几次
                // 并校验 GetForegroundWindow 确实是它，避免偶发失败被误判成功能坏了。
                bool foreground = false;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    SetForegroundWindow(hwnd);
                    Thread.Sleep(300);
                    if (GetForegroundWindow() == hwnd) { foreground = true; break; }
                }
                Check(foreground, "全屏窗口成功成为前台窗口（若失败是环境干扰，非功能问题）");

                Thread.Sleep(400);

                // ---------- 3. 全屏窗口在前台：应判定为全屏 ----------
                bool during = FullscreenWatch.IsFullscreen();
                Check(during == true, "无边框全屏窗口在前台时 IsFullscreen() = true", "实际 " + during);
            }
            finally
            {
                if (host != null)
                {
                    try { host.Kill(); } catch { }
                    try { host.WaitForExit(3000); } catch { }
                    try { host.Dispose(); } catch { }
                }
            }

            Thread.Sleep(800);

            // ---------- 4. 关掉全屏窗口后：应恢复 ----------
            bool after = FullscreenWatch.IsFullscreen();
            Check(after == false, "全屏窗口关闭后 IsFullscreen() = false", "实际 " + after);

            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
