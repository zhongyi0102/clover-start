using System;
using System.Windows;

namespace Clover
{
    /// <summary>
    /// 让模态子窗口最小化时，把 Owner 解禁 —— 否则用户以为程序卡死。
    ///
    /// ── 问题 ──
    ///
    /// WPF 的 `ShowDialog()` 是模态：子窗口显示期间系统会
    /// `EnableWindow(owner, false)`，Owner 完全点不动。这是模态的定义，
    /// 本身没错。
    ///
    /// 但子窗口可以最小化。最小化后：
    ///   · 子窗口看不见了
    ///   · Owner 仍被禁用
    ///   · 用户点主面板没反应，以为整个程序挂了
    ///
    /// ── 修法 ──
    ///
    /// 子窗口 `StateChanged`：
    ///   · `WindowState == Minimized` → 解禁 Owner
    ///   · 其它（Normal / Maximized）  → 重新禁用 Owner
    ///
    /// 这不破坏模态语义 —— 子窗口**可见时**它仍然是模态的，
    /// 只有最小化（用户主动把它挪走）时才放开 Owner。
    ///
    /// ── 为什么用 Win32 而不是 WPF 属性 ──
    ///
    /// 模态禁用是 Win32 层 `EnableWindow` 做的。改 `owner.IsEnabled`
    /// 是另一层，改不动它。见 `Native.SetWindowEnabled`。
    ///
    /// ── 关闭时为什么要强制禁回去 ──
    ///
    /// 如果用户在"最小化"状态下直接关掉子窗口（任务栏右键 → 关闭），
    /// 我们没机会在 StateChanged 里恢复。`ShowDialog()` 返回后模态
    /// 循环自己会把 Owner 解开，但**顺序不保证**。
    /// 所以在 `Closed` 里显式解禁一次，保证 Owner 一定可用。
    /// </summary>
    static class ModalOwnerGuard
    {
        /// <summary>
        /// 给一个模态窗口挂上"最小化时解禁 Owner"的行为。
        /// 在 `ShowDialog()` 之前调用。
        /// </summary>
        public static void Attach(Window dlg)
        {
            try
            {
                if (dlg == null) return;

                Window owner = dlg.Owner;
                if (owner == null) return;   // 没有 Owner 就没有模态禁用，不用管

                dlg.StateChanged += delegate
                {
                    try
                    {
                        IntPtr hwnd = OwnerHandle(owner);
                        if (hwnd == IntPtr.Zero) return;

                        bool minimized = dlg.WindowState == WindowState.Minimized;

                        // 最小化 → 解禁 Owner；恢复 → 重新禁用（保持模态）
                        Native.SetWindowEnabled(hwnd, minimized);
                    }
                    catch { }
                };

                // 关闭时兜底：无论之前是什么状态，Owner 都必须可用
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
