using System;

namespace Clover
{
    /// <summary>
    /// 「这次拖入能不能被当前页接收」的纯逻辑判定。
    ///
    /// ── 为什么需要它 ──
    ///
    /// 拖放期间光标显示什么（允许 / 禁止），取决于三件事：
    ///
    ///   1. 拖的是不是外部文件（内部拖动是磁贴排序，不该被当文件处理）
    ///   2. 当前页是不是「接收文件」的页（常用/全部/搜索页都不接收）
    ///
    /// 这个判断原本散在 `OnPanelDragEnter` / `OnPanelDragOver` / `OnPanelDrop`
    /// 里，靠几个 if 拼出来。改一处就得同时改三处，而且**没法单测** ——
    /// 只能靠人手拖一遍看光标。
    ///
    /// 现在收成一个纯函数，UI 层只负责把结果翻译成 WPF 的 `DragDropEffects`。
    ///
    /// ── 为什么不用 WPF 的枚举 ──
    ///
    /// core 层零 WPF 依赖（build.ps1 会强制校验）。所以这里定义自己的
    /// `Effect` 枚举，由 UI 层映射过去 —— 映射是一行 switch，有测试覆盖。
    /// </summary>
    public static class DropCursor
    {
        /// <summary>拖放光标效果（与 WPF 的 DragDropEffects 一一对应）。</summary>
        public enum Effect
        {
            /// <summary>禁止放下（光标显示 🚫）。</summary>
            None = 0,

            /// <summary>可以放下（复制）。</summary>
            Copy,
        }

        /// <summary>
        /// 「文件」标签拖出时写入的内部拖动标记。
        ///
        /// ── 为什么不复用 DesktopDragGesture.InternalDataFormat ──
        ///
        /// 那个标记被 `PanelWindow.OnPanelDragLeave` 用来触发
        /// `StartDragWatch()` —— 一个 80ms 轮询，松手后若鼠标在面板外
        /// 就 `HidePanel()`。它服务于「桌面页拖图标到资源管理器」这个场景
        /// （拖出去后面板该收起）。
        ///
        /// 但「文件」页拖出到资源管理器是**复制**，用户拖完往往还要回来继续
        /// 操作面板。复用那个标记会让面板在拖完后消失，是行为倒退。
        ///
        /// 所以单独一个格式：只用于「光标判定」，不参与拖出后的收起逻辑。
        /// </summary>
        public const string InternalFilesFormat = "CloverStart.FilesTabDrag";

        /// <summary>
        /// 算出拖入时该显示的光标效果。
        /// </summary>
        /// <param name="isInternalDrag">
        /// 是不是内部拖动（磁贴排序、图标拖动、「文件」页拖出）。
        /// **内部拖动一律返回 None**：它不是外部文件拖入，不该显示"可放"光标。
        /// </param>
        /// <param name="hasFiles">拖的东西里有没有文件（FileDrop 格式）</param>
        /// <param name="pageAccepts">
        /// 当前页会不会处理文件落下。
        /// false = 「常用」「全部」「搜索结果」这些页 —— 它们收到文件也不做事。
        /// </param>
        public static Effect Resolve(bool isInternalDrag, bool hasFiles, bool pageAccepts)
        {
            // 内部拖动：不是外部文件拖入，不给"可放"光标
            if (isInternalDrag) return Effect.None;

            // 没带文件：不是我们能处理的拖放
            if (!hasFiles) return Effect.None;

            // 页不接收：光标显示禁止，比"亮了高亮却什么都不发生"诚实
            if (!pageAccepts) return Effect.None;

            return Effect.Copy;
        }

        /// <summary>
        /// 这个 DataObject 是不是本程序自己发起的拖动。
        ///
        /// 两个来源都算：
        ///   · 桌面页拖出图标（`DesktopDragGesture.InternalDataFormat`）
        ///   · 「文件」页拖出文件（<see cref="InternalFilesFormat"/>）
        ///
        /// **外部程序不可能写入这两个私有格式**，所以外部拖入必然返回 false。
        /// 这是唯一的可靠判据 —— 不能看有没有 FileDrop，因为内部拖出
        /// 同样会带 FileDrop（`DesktopView.BeginDragOut` 就设了）。
        /// </summary>
        public static bool IsInternalDrag(bool isDesktopDrag, bool isFilesDrag)
        {
            return isDesktopDrag || isFilesDrag;
        }

        /// <summary>
        /// 这一页会不会处理文件落下。
        ///
        /// 依据 `OnPanelDrop` 的分支：
        ///   · 「文件」标签 → 加来源（接收）
        ///   · 虚拟桌面标签（内置「桌面文件」+ 自定义标签）→ 搬文件进去（接收）
        ///   · 其余（常用 / 全部 / 搜索结果）→ 直接 return（不接收）
        ///
        /// **抽成纯函数的原因**：这个判断原来写在 `OnPanelDrop` 里，
        /// 而 DragEnter/DragOver 需要提前知道同一个答案才能设光标。
        /// 两处各写一遍迟早分叉。
        /// </summary>
        /// <param name="currentPage">当前标签名</param>
        /// <param name="isDeskTab">该标签是不是虚拟桌面（由调用方按 nav-tabs 判定）</param>
        public static bool PageAcceptsDrop(string currentPage, bool isDeskTab)
        {
            try
            {
                if (isDeskTab) return true;

                // 「文件」标签的内部名。这里写字面量而不是引用
                // PanelWindow.FilesCategory（那是 ui 层）——
                // 值必须与它一致，测试里有断言钉住。
                if (string.Equals(currentPage, "文件", StringComparison.Ordinal)) return true;

                return false;
            }
            catch { return false; }
        }
    }
}
