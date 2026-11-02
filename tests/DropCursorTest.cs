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
    }
}
