using System;

namespace Clover
{
    /// <summary>
    /// 拖放光标判定测试（提交 2）。
    ///
    /// 覆盖两件事：
    ///   1. `DropCursor.Resolve` 的三输入组合（内部拖动 / 无文件 / 页不接收 / 正常）
    ///   2. `DropCursor.PageAcceptsDrop` 与 `OnPanelDrop` 的实际分支一致
    ///
    /// **纯逻辑，不碰 WPF、不碰磁盘。**
    /// </summary>
    static class DropCursorTest
    {
        static int failed = 0;
        static int passed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            if (cond) { passed++; Console.WriteLine("PASS  " + name); }
            else
            {
                failed++;
                Console.WriteLine("FAIL  " + name + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
            }
        }

        static void Main()
        {
            Console.WriteLine("=== 拖放光标判定测试 ===");
            Console.WriteLine();

            try
            {
                TestResolve();
                TestInternalDrag();
                TestPageAccepts();
                TestMatrix();
                TestInternalFormats();
                TestFilesTabDragBack();
            }
            catch (Exception ex)
            {
                Check(false, "测试不抛异常", ex.GetType().Name + ": " + ex.Message);
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 基本组合 ----------
        static void TestResolve()
        {
            Console.WriteLine("--- 1. Resolve 基本组合 ---");

            // 外部文件 + 接收页 → Copy
            Check(DropCursor.Resolve(false, true, true) == DropCursor.Effect.Copy,
                "★外部文件 + 接收页 → Copy");

            // 外部文件 + 不接收页 → None（本轮改动的核心）
            Check(DropCursor.Resolve(false, true, false) == DropCursor.Effect.None,
                "★外部文件 + 不接收页 → None");

            // 没带文件 → None
            Check(DropCursor.Resolve(false, false, true) == DropCursor.Effect.None,
                "★无文件 + 接收页 → None");
            Check(DropCursor.Resolve(false, false, false) == DropCursor.Effect.None,
                "无文件 + 不接收页 → None");

            Console.WriteLine();
        }

        // ---------- 2. 内部拖动：Effects 行为必须不变 ----------
        static void TestInternalDrag()
        {
            Console.WriteLine("--- 2. 内部拖动一律 None（行为不变）---");

            // 内部拖动不管别的输入是什么，都是 None。
            // 这是「磁贴拖拽排序」的路径：内部拖动从来不带 FileDrop，
            // 也不该显示"可放"光标。
            Check(DropCursor.Resolve(true, true, true) == DropCursor.Effect.None,
                "★内部拖动 + 有文件 + 接收页 → None");
            Check(DropCursor.Resolve(true, true, false) == DropCursor.Effect.None,
                "★内部拖动 + 有文件 + 不接收页 → None");
            Check(DropCursor.Resolve(true, false, true) == DropCursor.Effect.None,
                "★内部拖动 + 无文件 + 接收页 → None");
            Check(DropCursor.Resolve(true, false, false) == DropCursor.Effect.None,
                "内部拖动 + 无文件 + 不接收页 → None");

            Console.WriteLine();
        }

        // ---------- 3. 页面判定与 OnPanelDrop 分支一致 ----------
        static void TestPageAccepts()
        {
            Console.WriteLine("--- 3. PageAcceptsDrop 与 OnPanelDrop 分支一致 ---");

            // 「文件」标签接收（加来源）
            Check(DropCursor.PageAcceptsDrop("文件", false), "★「文件」标签接收");

            // 虚拟桌面标签接收（搬文件进去）—— 内置和自定义都算
            Check(DropCursor.PageAcceptsDrop("桌面文件", true), "★「桌面文件」接收");
            Check(DropCursor.PageAcceptsDrop("我的桌面", true), "★自定义桌面标签接收");

            // 常用 / 全部 / 搜索结果不接收
            Check(!DropCursor.PageAcceptsDrop("常用", false), "★「常用」不接收");
            Check(!DropCursor.PageAcceptsDrop("", false), "★「全部」（空串）不接收");
            Check(!DropCursor.PageAcceptsDrop("随便什么", false), "★未知标签不接收");

            // null / 边界
            Check(!DropCursor.PageAcceptsDrop(null, false), "null 页名不接收");
            Check(DropCursor.PageAcceptsDrop(null, true), "★但 isDeskTab=true 时仍接收");

            // isDeskTab 优先于页名：即使页名是「常用」，标成桌面标签也接收
            // （正常不会这样，但防御性断言：调用方传错时以 isDeskTab 为准）
            Check(DropCursor.PageAcceptsDrop("常用", true), "isDeskTab 优先");

            Console.WriteLine();
        }

        // ---------- 4. 完整矩阵 ----------
        static void TestMatrix()
        {
            Console.WriteLine("--- 4. 三输入完整矩阵（2×2×2 = 8 种）---");

            int copyCount = 0, noneCount = 0;

            foreach (bool internalDrag in new bool[] { false, true })
            foreach (bool hasFiles in new bool[] { false, true })
            foreach (bool accepts in new bool[] { false, true })
            {
                DropCursor.Effect e = DropCursor.Resolve(internalDrag, hasFiles, accepts);

                // 只有「非内部 + 有文件 + 接收」这一种才该是 Copy
                bool expectCopy = !internalDrag && hasFiles && accepts;
                bool actualCopy = e == DropCursor.Effect.Copy;

                Check(expectCopy == actualCopy,
                    string.Format("内部={0} 有文件={1} 接收={2} → {3}",
                        internalDrag, hasFiles, accepts, e));

                if (actualCopy) copyCount++; else noneCount++;
            }

            Check(copyCount == 1, "★8 种里只有 1 种是 Copy", Convert.ToString(copyCount));
            Check(noneCount == 7, "★其余 7 种都是 None", Convert.ToString(noneCount));

            Console.WriteLine();
        }

        // ---------- 5. 两个内部格式互不干扰 ----------
        static void TestInternalFormats()
        {
            Console.WriteLine("--- 5. 两个内部格式 ---");

            // 「文件」页的格式与桌面页的格式必须是不同的字符串，
            // 否则 OnPanelDragLeave 的 StartDragWatch 会被误触发。
            Check(DropCursor.InternalFilesFormat != DesktopDragGesture.InternalDataFormat,
                "★两个内部格式不同",
                DropCursor.InternalFilesFormat + " vs " + DesktopDragGesture.InternalDataFormat);

            // 都是私有命名空间前缀（外部程序不可能撞上）
            Check(DropCursor.InternalFilesFormat.StartsWith("CloverStart.", StringComparison.Ordinal),
                "★文件页格式用私有前缀", DropCursor.InternalFilesFormat);
            Check(DesktopDragGesture.InternalDataFormat.StartsWith("CloverStart.", StringComparison.Ordinal),
                "★桌面页格式用私有前缀", DesktopDragGesture.InternalDataFormat);

            // IsInternalDrag：两个来源任一为 true 就算内部
            Check(DropCursor.IsInternalDrag(true, false), "★桌面页拖动 → 内部");
            Check(DropCursor.IsInternalDrag(false, true), "★文件页拖动 → 内部");
            Check(DropCursor.IsInternalDrag(true, true), "两个都是 → 内部");
            Check(!DropCursor.IsInternalDrag(false, false), "★外部拖入 → 不是内部");

            Console.WriteLine();
        }

        // ---------- 6. 文件页拖回自身（本轮修复的场景）----------
        static void TestFilesTabDragBack()
        {
            Console.WriteLine("--- 6. 文件页拖回面板自身 ---");

            // 场景：在「文件」页拖出一个文件，拖回面板自身范围。
            // 拖动源带 FileDrop + InternalFilesFormat。
            bool isDesktopDrag = false;
            bool isFilesDrag = true;
            bool hasFiles = true;                       // 内部拖出**确实**带 FileDrop
            bool accepts = DropCursor.PageAcceptsDrop("文件", false);   // 文件页接收

            bool internalDrag = DropCursor.IsInternalDrag(isDesktopDrag, isFilesDrag);
            DropCursor.Effect e = DropCursor.Resolve(internalDrag, hasFiles, accepts);

            Check(internalDrag, "★被判为内部拖动");
            Check(hasFiles, "★确实带 FileDrop（证明不能靠它判断）");
            Check(accepts, "★文件页本身是接收页");
            Check(e == DropCursor.Effect.None,
                "★但内部拖动 → 光标禁止（不被当外部拖入）", e.ToString());

            // 对照：外部拖入同一个页面 → Copy
            DropCursor.Effect ext = DropCursor.Resolve(false, true, accepts);
            Check(ext == DropCursor.Effect.Copy,
                "★外部拖入同一页仍是 Copy（修复没影响正常路径）", ext.ToString());

            // 对照：桌面页拖出图标拖回面板自身 → 也是 None
            bool deskInternal = DropCursor.IsInternalDrag(true, false);
            Check(DropCursor.Resolve(deskInternal, true, true) == DropCursor.Effect.None,
                "★桌面页拖回自身 → None");

            // 对照：拖到资源管理器 —— 那是外部程序接收，不经过这里的判定。
            // 这里只断言本程序的判定不会把内部拖出变成 Copy。
            Check(DropCursor.Resolve(true, true, true) != DropCursor.Effect.Copy,
                "★内部拖出在任何页都不是 Copy");

            Console.WriteLine();
        }
    }
}
