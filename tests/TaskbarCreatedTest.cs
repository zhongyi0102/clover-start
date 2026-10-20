using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Clover
{
    /// <summary>
    /// TaskbarCreated 消息测试：验证我们注册的消息号与系统广播一致，
    /// 并且能真实收到 explorer 重启后发出的广播。
    ///
    /// 做法：起一个子进程（--listener）注册 TaskbarCreated 消息并建窗口，
    /// 父进程调 BroadcastSystemMessage / PostMessage 模拟广播，
    /// 子进程收到后在标准输出打印标记，父进程断言收到。
    ///
    /// 注意：真正的 explorer 重启会重建任务栏，风险较高且会影响当前桌面，
    /// 所以这里验证「消息注册 + 接收 + 处理」这条链路，
    /// 而不去真的杀 explorer（那会让用户桌面闪一下）。
    /// </summary>
    static class TaskbarCreatedTest
    {
        static int failed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name + (cond || detail.Length == 0 ? "" : "  [" + detail + "]"));
            if (!cond) failed++;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint RegisterWindowMessage(string name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

        [DllImport("user32.dll")]
        static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll")]
        static extern bool GetMessage(out MSG m, IntPtr h, uint min, uint max);

        [DllImport("user32.dll")]
        static extern bool TranslateMessage(ref MSG m);

        [DllImport("user32.dll")]
        static extern IntPtr DispatchMessage(ref MSG m);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr h, int cmd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string cls, string name);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
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

        delegate IntPtr WndProcDelegate(IntPtr h, uint msg, IntPtr w, IntPtr l);
        static WndProcDelegate keepAlive;
        static uint taskbarCreatedMsg;
        static bool gotMessage;
        static int listenerPid;

        static IntPtr ListenerProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            if (taskbarCreatedMsg != 0 && msg == taskbarCreatedMsg)
            {
                gotMessage = true;
                // 用文件做跨进程信号（标准输出在 UseShellExecute=false 下不稳）
                try
                {
                    string flag = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), "clover-taskbarcreated-" + listenerPid + ".flag");
                    System.IO.File.WriteAllText(flag, "1");
                }
                catch { }
            }
            return DefWindowProc(h, msg, w, l);
        }

        static int RunListener(int pid)
        {
            listenerPid = pid;
            taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");

            WNDCLASSEX wc = new WNDCLASSEX();
            wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
            keepAlive = ListenerProc;
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(keepAlive);
            wc.lpszClassName = "CloverTBCListener";
            RegisterClassEx(ref wc);

            IntPtr hwnd = CreateWindowEx(0, "CloverTBCListener", "CloverTBCListener", 0,
                0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return 2;

            // 注意：不 ShowWindow —— 这是个隐藏窗口，不干扰用户桌面
            MSG m;
            while (GetMessage(out m, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref m);
                DispatchMessage(ref m);
            }
            return 0;
        }

        static void Main(string[] args)
        {
            if (args.Length > 1 && args[0] == "--listener")
            {
                int pid = 0;
                int.TryParse(args[1], out pid);
                Thread guard = new Thread(delegate() { Thread.Sleep(30000); Environment.Exit(0); });
                guard.IsBackground = true;
                guard.Start();
                Environment.Exit(RunListener(pid));
                return;
            }

            // ---------- 1. 消息号注册：两个独立调用应得到同一个值 ----------
            uint m1 = RegisterWindowMessage("TaskbarCreated");
            uint m2 = RegisterWindowMessage("TaskbarCreated");
            Check(m1 != 0, "TaskbarCreated 注册成功", "msg=" + m1);
            Check(m1 == m2, "重复注册返回同一个消息号（系统保证唯一）");

            // 与代码里 Native.RegisterWindowMessage 的结果一致
            uint m3 = Native.RegisterWindowMessage("TaskbarCreated");
            Check(m1 == m3, "Native.RegisterWindowMessage 与 P/Invoke 一致", m1 + " vs " + m3);

            // 不同消息名应得到不同消息号
            uint other = RegisterWindowMessage("CloverSomethingElse");
            Check(other != m1, "不同消息名得到不同消息号");

            // ---------- 2. 子进程监听，父进程投递 ----------
            string flag = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "clover-taskbarcreated-" + Process.GetCurrentProcess().Id + ".flag");
            try { System.IO.File.Delete(flag); } catch { }

            Process listener = null;
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                ProcessStartInfo psi = new ProcessStartInfo(exe, "--listener " + Process.GetCurrentProcess().Id);
                psi.UseShellExecute = false;
                listener = Process.Start(psi);

                // 等窗口建好
                IntPtr hwnd = IntPtr.Zero;
                for (int i = 0; i < 50; i++)
                {
                    Thread.Sleep(100);
                    hwnd = FindWindow("CloverTBCListener", null);
                    if (hwnd != IntPtr.Zero) break;
                }
                Check(hwnd != IntPtr.Zero, "监听子进程创建了隐藏窗口");

                if (hwnd != IntPtr.Zero)
                {
                    // 投递 TaskbarCreated 广播（等价于 explorer 重启后系统发的消息）
                    PostMessage(hwnd, m1, IntPtr.Zero, IntPtr.Zero);

                    bool got = false;
                    for (int i = 0; i < 30; i++)
                    {
                        Thread.Sleep(100);
                        if (System.IO.File.Exists(flag)) { got = true; break; }
                    }
                    Check(got, "监听方收到了 TaskbarCreated 消息");
                }
            }
            finally
            {
                if (listener != null)
                {
                    try { listener.Kill(); } catch { }
                    try { listener.WaitForExit(3000); } catch { }
                    try { listener.Dispose(); } catch { }
                }
                try { System.IO.File.Delete(flag); } catch { }
            }

            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
