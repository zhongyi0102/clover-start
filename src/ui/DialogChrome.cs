using System;
using System.Windows;

namespace Clover
{
    /// <summary>
    /// 对话框窗口的收尾处理：不占任务栏、不提供最小化按钮。
    ///
    /// ── 要解决的两个问题 ──
    ///
    /// 项目的对话框都是 `ShowInTaskbar = false`（一闪而过的配置窗口，
    /// 不该占任务栏）。但 `ResizeMode = CanResize` 会让系统标题栏带上
    /// 最小化按钮 —— 两者矛盾：
    ///
    ///   最小化后任务栏没有图标 → 窗口再也找不回来 → 用户以为程序挂了
    ///
    /// ── 为什么不给 ShowInTaskbar = true ──
    ///
    /// 试过，能用，但这些是配置对话框，不该在任务栏堆图标。
    /// 去掉最小化按钮更干净：既然没法恢复，就不该给这个入口。
    ///
    /// ── 为什么需要 Win32 ──
    ///
    /// WPF 的 `ResizeMode` 只有四档：
    ///   NoResize / CanMinimize / CanResize / CanResizeWithGrip
    /// **没有"可调大小但没有最小化"**这一档。
    /// `CanMinimize` 反过来又去掉了最大化。
    /// 所以只能改 Win32 的窗口样式位（去掉 WS_MINIMIZEBOX）。
    ///
    /// ── 顺带：最小化时解禁 Owner ──
    ///
    /// 即使去掉了最小化按钮，用户仍可能用 Win+↓ 或任务栏右键最小化。
    /// 这些窗口是 `ShowDialog()` 模态，最小化后 Owner 会被系统禁用，
    /// 主面板点不动 —— 所以最小化时解禁、恢复时禁回去。
    ///
    /// 两条保护都留着：按钮去掉是"不给入口"，解禁是"万一还是最小化了"。
    /// </summary>
    static class DialogChrome
    {
        /// <summary>
        /// 在 `ShowDialog()` 之前调用。
        ///
        /// 做两件事：
        ///   1. 窗口句柄建立后去掉最小化按钮
        ///   2. 万一被最小化（Win+↓ 等），解禁 Owner
        /// </summary>
        public static void Attach(Window dlg)
        {
            try
            {
                if (dlg == null) return;

                // ---- 1. 去掉最小化按钮 ----
                //
                // 必须在句柄建立之后。**两个时机都做**：
                //   · SourceInitialized —— 最早，但此时窗口还没被系统画出来，
                //     有些情况下样式改动会被后续的 CreateWindow 覆盖
                //   · Loaded —— 窗口已经画完，再改一次并强制重画标题栏
                //
                // 实测只做 SourceInitialized 时按钮有时还留着，所以补 Loaded。
                dlg.SourceInitialized += delegate
                {
                    try
                    {
                        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
                        Native.RemoveMinimizeBox(hwnd);
                    }
                    catch { }
                };

                dlg.Loaded += delegate
                {
                    try
                    {
                        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
                        Native.RemoveMinimizeBox(hwnd);
                    }
                    catch { }
                };

                // ---- 2. 拦截最小化命令（保险）----
                //
                // 即使样式位没生效、按钮还留着，点它也**不会真的最小化**。
                // 双保险：样式去掉是"看不见按钮"，这里是"点了也没用"。
                //
                // 同时覆盖 Win+↓ 和任务栏右键 → 最小化。
                dlg.SourceInitialized += delegate
                {
                    try
                    {
                        System.Windows.Interop.HwndSource src =
                            System.Windows.Interop.HwndSource.FromHwnd(
                                new System.Windows.Interop.WindowInteropHelper(dlg).Handle);
                        if (src == null) return;

                        src.AddHook(delegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
                            ref bool handled)
                        {
                            try
                            {
                                const int WM_SYSCOMMAND = 0x0112;
                                const int SC_MINIMIZE = 0xF020;

                                if (msg != WM_SYSCOMMAND) return IntPtr.Zero;

                                int cmd = wParam.ToInt32() & 0xFFF0;
                                if (cmd == SC_MINIMIZE)
                                {
                                    // 吞掉最小化命令：窗口保持原状
                                    handled = true;
                                    return IntPtr.Zero;
                                }
                            }
                            catch { }
                            return IntPtr.Zero;
                        });
                    }
                    catch { }
                };

                // ---- 3. 万一还是被最小化，解禁 Owner ----
                Window owner = dlg.Owner;
                if (owner != null)
                {
                    dlg.StateChanged += delegate
                    {
                        try
                        {
                            IntPtr hwnd = OwnerHandle(owner);
                            if (hwnd == IntPtr.Zero) return;

                            bool minimized = dlg.WindowState == WindowState.Minimized;
                            Native.SetWindowEnabled(hwnd, minimized);
                        }
                        catch { }
                    };

                    // 关闭时兜底：无论之前什么状态，Owner 都必须可用
                    dlg.Closed += delegate
                    {
                        try
                        {
                            IntPtr hwnd = OwnerHandle(owner);
                            if (hwnd != IntPtr.Zero) Native.SetWindowEnabled(hwnd, true);
                        }
                        catch { }
                    };
                }
            }
            catch { }
        }

        static IntPtr OwnerHandle(Window owner)
        {
            try
            {
                if (owner == null) return IntPtr.Zero;
                return new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            }
            catch { return IntPtr.Zero; }
        }
    }
}
