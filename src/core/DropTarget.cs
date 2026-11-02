using System;

namespace Clover
{
    /// <summary>
    /// 判断拖出后鼠标停在哪个程序上，据此决定文件要不要真的移走。
    ///
    /// ── 为什么需要这个 ──
    ///
    /// `DoDragDrop` 的返回值**不可靠**：目标程序接受 FileDrop 时，
    /// 无论它实际是复制还是移动，返回值都可能是 Copy。
    /// 所以不能靠返回值判断，必须自己看落点窗口是谁。
    ///
    /// ── 判定规则 ──
    ///
    ///   · 桌面        Progman / WorkerW / SysListView32(桌面图标层)
    ///                → 只核对系统是否已经移动；程序不补删源文件
    ///   · 资源管理器  CabinetWClass / ExploreWClass
    ///                → 同上
    ///   · 其他程序    QQ / 微信 / 浏览器…
    ///                → 只当"发送文件"，源文件必须保留，图标也不移除
    ///
    /// ── 窗口类名为什么用前缀匹配 ──
    ///
    /// QQ 的窗口类名在不同版本里是 `TXGuiFoundation` / `TXGuiFoundation2`…
    /// 微信是 `WeChatMainWndForPC`；精确匹配会漏。
    /// </summary>
    public static class DropTarget
    {
        /// <summary>落点类型。</summary>
        public enum Kind
        {
            /// <summary>认不出来（拖拽取消、或无窗口）。</summary>
            Unknown = 0,
            /// <summary>桌面（Progman / WorkerW）。</summary>
            Desktop,
            /// <summary>资源管理器窗口。</summary>
            Explorer,
            /// <summary>其他程序（QQ、微信等）。</summary>
            Other
        }

        /// <summary>
        /// 按窗口类名判定落点类型。
        ///
        /// **这是纯函数，可单元测试。**
        /// </summary>
        public static Kind ClassifyByClassName(string cls)
        {
            try
            {
                if (string.IsNullOrEmpty(cls)) return Kind.Unknown;

                // ---- 桌面 ----
                // Progman  = 桌面本身
                // WorkerW  = 壁纸层（Windows 8+ 桌面图标常挂在这）
                // SysListView32 = 桌面图标列表（点中某个图标时命中的是它）
                if (Eq(cls, "Progman")) return Kind.Desktop;
                if (Eq(cls, "WorkerW")) return Kind.Desktop;
                if (Eq(cls, "SysListView32")) return Kind.Desktop;

                // ---- 资源管理器 ----
                if (Eq(cls, "CabinetWClass")) return Kind.Explorer;
                if (Eq(cls, "ExploreWClass")) return Kind.Explorer;

                // ---- 其他 ----
                return Kind.Other;
            }
            catch { return Kind.Unknown; }
        }

        static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 这个落点是否需要"确保文件真的被移走"。
        ///
        /// 桌面和资源管理器属于"用户想把文件挪到那"，
        /// 其他程序只是"发送"，源文件必须留着。
        /// </summary>
        public static bool ShouldMove(Kind k)
        {
            return k == Kind.Desktop || k == Kind.Explorer;
        }

        /// <summary>当前光标下的顶层窗口句柄（失败返回零）。</summary>
        public static IntPtr RootWindowAtCursor()
        {
            try
            {
                Native.POINT p;
                if (!Native.GetCursorPos(out p)) return IntPtr.Zero;
                IntPtr h = Native.WindowFromPoint(p.X, p.Y);
                if (h == IntPtr.Zero) return IntPtr.Zero;
                IntPtr root = Native.GetAncestor(h, Native.GA_ROOT);
                return root == IntPtr.Zero ? h : root;
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>
        /// 一次拿到「顶层窗口句柄 + 落点类型」。
        ///
        /// ── 为什么要合并 ──
        ///
        /// 原来调用方是这样写的：
        /// <code>
        /// IntPtr hwnd = DropTarget.RootWindowAtCursor();   // GetCursorPos + WindowFromPoint + GetAncestor
        /// Kind kind   = DropTarget.KindAtCursor();          // 再一次 GetCursorPos + WindowFromPoint + 类名
        /// </code>
        /// 两次 `GetCursorPos` + 两次 `WindowFromPoint` + 多次 `ClassNameOf`，
        /// 而这些调用发生在 `DoDragDrop` **刚返回**的那一刻 —— 正是用户
        /// 松手后最敏感的时刻。合并成一次，省掉一半 P/Invoke 往返。
        /// </summary>
        public static Kind ResolveAtCursor(out IntPtr rootHwnd)
        {
            rootHwnd = IntPtr.Zero;
            try
            {
                Native.POINT p;
                if (!Native.GetCursorPos(out p)) return Kind.Unknown;

                IntPtr h = Native.WindowFromPoint(p.X, p.Y);
                if (h == IntPtr.Zero) return Kind.Unknown;

                // 顶层句柄（后面解析资源管理器目录要用）
                IntPtr root = IntPtr.Zero;
                try { root = Native.GetAncestor(h, Native.GA_ROOT); } catch { }
                if (root == IntPtr.Zero) root = h;
                rootHwnd = root;

                // 先看命中的窗口本身
                Kind k = ClassifyByClassName(Native.ClassNameOf(h));
                if (k != Kind.Other) return k;

                // 再向上找顶层
                if (root != h)
                {
                    Kind rk = ClassifyByClassName(Native.ClassNameOf(root));
                    if (rk != Kind.Other) return rk;
                }

                // 兜底：顶层是 Progman/WorkerW 也算桌面
                try
                {
                    string rc = Native.ClassNameOf(root);
                    if (Eq(rc, "Progman") || Eq(rc, "WorkerW")) return Kind.Desktop;
                }
                catch { }

                return Kind.Other;
            }
            catch { return Kind.Unknown; }
        }
    }
}
