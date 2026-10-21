using System;

namespace Clover
{
    /// <summary>
    /// 任务栏行高亮判定的测试。
    ///
    /// 背景：为了不在「输入回调」里查系统状态（鼠标划过时每次进出都发一次
    /// GetForegroundWindow），MouseEnter/MouseLeave 改成只读缓存的句柄。
    /// 代价是高亮可能读到旧值。补救办法是在 ShowPanel / Activated 时主动刷新缓存。
    ///
    /// 这里覆盖那个判定的纯逻辑部分（PanelWindow.IsActiveRow），
    /// 以及缓存刷新「只在变化时才改界面」的语义。
    /// </summary>
    static class ActiveRowTest
    {
        static int failed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name +
                (cond || detail.Length == 0 ? "" : "  [" + detail + "]"));
            if (!cond) failed++;
        }

        static IntPtr H(int v)
        {
            return new IntPtr(v);
        }

        static void Main()
        {
            // ---------- 基本判定 ----------
            IntPtr a = H(1000);
            IntPtr b = H(2000);

            Check(PanelWindow.IsActiveRow(a, a), "同一句柄 -> 高亮");
            Check(!PanelWindow.IsActiveRow(a, b), "不同句柄 -> 不高亮");
            Check(!PanelWindow.IsActiveRow(b, a), "顺序无关：反过来也不高亮");

            // ---------- 边界：句柄为 0 ----------
            // 取不到前台窗口时 GetActiveWindow 返回 0；
            // 此时不该把任何一行点亮（修复前用 == 比较，0 会匹配到 Tag==0 的行）。
            Check(!PanelWindow.IsActiveRow(IntPtr.Zero, IntPtr.Zero),
                "前台句柄为 0 时不高亮任何行");
            Check(!PanelWindow.IsActiveRow(a, IntPtr.Zero),
                "前台为 0、行有句柄 -> 不高亮");
            Check(!PanelWindow.IsActiveRow(IntPtr.Zero, a),
                "行为 0、前台有句柄 -> 不高亮");

            // ---------- 缓存语义：句柄没变就不该动界面 ----------
            // RefreshActiveCache 的做法是「变了才 ApplyActiveHighlight」。
            // 这里验证判定本身在重复调用下是稳定的（幂等）。
            bool first = PanelWindow.IsActiveRow(a, a);
            bool second = PanelWindow.IsActiveRow(a, a);
            Check(first == second, "重复判定结果稳定（幂等）");

            // 模拟一次切换：前台从 a 变到 b，两行的判定应同时翻转
            IntPtr cached = a;
            bool rowA_before = PanelWindow.IsActiveRow(a, cached);
            bool rowB_before = PanelWindow.IsActiveRow(b, cached);
            cached = b; // 缓存刷新
            bool rowA_after = PanelWindow.IsActiveRow(a, cached);
            bool rowB_after = PanelWindow.IsActiveRow(b, cached);

            Check(rowA_before && !rowA_after, "切换后原前台行不再高亮");
            Check(!rowB_before && rowB_after, "切换后新前台行高亮");
            Check(rowA_after == false && rowB_after == true,
                "切换后恰好一行高亮（不会两行都亮或都不亮）");

            // ---------- 多点验证：只有一行会亮 ----------
            IntPtr[] rows = new IntPtr[] { H(1), H(2), H(3), H(4), H(5) };
            for (int target = 0; target < rows.Length; target++)
            {
                int lit = 0;
                for (int i = 0; i < rows.Length; i++)
                {
                    if (PanelWindow.IsActiveRow(rows[i], rows[target])) lit++;
                }
                if (lit != 1)
                {
                    Check(false, "第 " + (target + 1) + " 个为目标时恰好一行高亮", "实际 " + lit + " 行");
                    break;
                }
                if (target == rows.Length - 1)
                    Check(true, "5 个窗口轮流做前台，每次都恰好一行高亮");
            }

            // ---------- 负数/极大句柄（IntPtr 全范围不出错）----------
            Check(PanelWindow.IsActiveRow(H(-1), H(-1)), "负数句柄也能正确比较");
            Check(!PanelWindow.IsActiveRow(H(-1), H(1)), "负数与正数不同");
            // .NET Framework 4.8 的 IntPtr 没有 MaxValue，用 long 最大值构造
            IntPtr huge = new IntPtr(long.MaxValue);
            Check(PanelWindow.IsActiveRow(huge, huge), "极大句柄不出错");

            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
