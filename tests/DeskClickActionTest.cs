using System;

namespace Clover
{
    /// <summary>
    /// 「桌面文件」页鼠标按下路径判定测试。
    ///
    /// 覆盖本次修复的两件事：
    ///   1. 双击打开**此前从未实现** —— 断言双击确实返回 Open
    ///   2. 修饰键不误触 —— Ctrl/Shift + 双击不该打开文件
    ///
    /// **纯逻辑，不碰 WPF、不打开文件。**
    /// </summary>
    static class DeskClickActionTest
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
            Console.WriteLine("=== 桌面点击路径判定测试 ===");
            Console.WriteLine();

            try
            {
                TestDoubleClick();
                TestModifiers();
                TestBlank();
                TestOtherClicks();
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

        // ---------- 1. 双击打开（本次修复的核心）----------
        static void TestDoubleClick()
        {
            Console.WriteLine("--- 1. 双击打开 ---");

            Check(DeskClickAction.Resolve(true, 2, false, false) == DeskClickAction.Action.Open,
                "★图标 + 双击 + 无修饰键 → 打开");

            // 双击 html 就是用户报的场景
            Check(DeskClickAction.Resolve(true, 2, false, false) != DeskClickAction.Action.Drag,
                "★双击不再落到拖动路径（旧实现的症状）");

            // 三击（ClickCount=3）不打开 —— 避免手抖连点三次打开两次
            Check(DeskClickAction.Resolve(true, 3, false, false) == DeskClickAction.Action.Drag,
                "★三击不打开");

            Console.WriteLine();
        }

        // ---------- 2. 修饰键不误触 ----------
        static void TestModifiers()
        {
            Console.WriteLine("--- 2. 修饰键不误触打开 ---");

            // Ctrl+双击：用户想加选，不该打开文件
            Check(DeskClickAction.Resolve(true, 2, false, true) == DeskClickAction.Action.Drag,
                "★Ctrl+双击 → 拖动路径（不打开）");

            // Shift+双击：用户想范围选中
            Check(DeskClickAction.Resolve(true, 2, true, false) == DeskClickAction.Action.Drag,
                "★Shift+双击 → 拖动路径（不打开）");

            // 两个都按
            Check(DeskClickAction.Resolve(true, 2, true, true) == DeskClickAction.Action.Drag,
                "Ctrl+Shift+双击 → 拖动路径");

            Console.WriteLine();
        }

        // ---------- 3. 空白处 ----------
        static void TestBlank()
        {
            Console.WriteLine("--- 3. 空白处永远框选 ---");

            Check(DeskClickAction.Resolve(false, 1, false, false) == DeskClickAction.Action.BandSelect,
                "★空白单击 → 框选");
            Check(DeskClickAction.Resolve(false, 2, false, false) == DeskClickAction.Action.BandSelect,
                "★空白双击 → 仍是框选（不该打开什么）");
            Check(DeskClickAction.Resolve(false, 3, false, false) == DeskClickAction.Action.BandSelect,
                "空白三击 → 仍是框选");
            Check(DeskClickAction.Resolve(false, 2, true, true) == DeskClickAction.Action.BandSelect,
                "空白 + 修饰键 → 仍是框选");

            Console.WriteLine();
        }

        // ---------- 4. 其它点击次数 ----------
        static void TestOtherClicks()
        {
            Console.WriteLine("--- 4. 单击 / 异常 ClickCount ---");

            Check(DeskClickAction.Resolve(true, 1, false, false) == DeskClickAction.Action.Drag,
                "★单击 → 拖动路径（选中 + 准备拖）");

            // ClickCount 异常值（理论上不会出现，防御）
            Check(DeskClickAction.Resolve(true, 0, false, false) == DeskClickAction.Action.Drag,
                "ClickCount=0 → 拖动路径");
            Check(DeskClickAction.Resolve(true, -1, false, false) == DeskClickAction.Action.Drag,
                "ClickCount=-1 → 拖动路径");
            Check(DeskClickAction.Resolve(true, 99, false, false) == DeskClickAction.Action.Drag,
                "ClickCount=99 → 拖动路径");

            Console.WriteLine();
        }

        // ---------- 5. 完整矩阵 ----------
        static void TestMatrix()
        {
            Console.WriteLine("--- 5. 完整矩阵（2×3×2×2 = 24 种）---");

            int openCount = 0, bandCount = 0, dragCount = 0;

            foreach (bool hit in new bool[] { false, true })
            foreach (int cc in new int[] { 1, 2, 3 })
            foreach (bool shift in new bool[] { false, true })
            foreach (bool ctrl in new bool[] { false, true })
            {
                DeskClickAction.Action a = DeskClickAction.Resolve(hit, cc, shift, ctrl);

                // 期望：空白 → BandSelect；图标 + 双击 + 无修饰 → Open；其余 → Drag
                DeskClickAction.Action expect;
                if (!hit) expect = DeskClickAction.Action.BandSelect;
                else if (cc == 2 && !shift && !ctrl) expect = DeskClickAction.Action.Open;
                else expect = DeskClickAction.Action.Drag;

                Check(a == expect,
                    string.Format("命中={0} ClickCount={1} Shift={2} Ctrl={3} → {4}",
                        hit, cc, shift, ctrl, a));

                if (a == DeskClickAction.Action.Open) openCount++;
                else if (a == DeskClickAction.Action.BandSelect) bandCount++;
                else dragCount++;
            }

            // 12 种空白 + 12 种图标；图标里只有 1 种（双击无修饰）是 Open
            Check(openCount == 1, "★24 种里只有 1 种是打开", Convert.ToString(openCount));
            Check(bandCount == 12, "★12 种空白都是框选", Convert.ToString(bandCount));
            Check(dragCount == 11, "★其余 11 种是拖动路径", Convert.ToString(dragCount));

            Console.WriteLine();
        }
    }
}
