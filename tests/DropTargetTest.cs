using System;

namespace Clover
{
    /// <summary>
    /// 拖出落点判定测试：类名 → 落点类型 → 是否该移动源文件。
    ///
    /// 纯函数，不需要真实窗口。
    /// </summary>
    static class DropTargetTest
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
            Console.WriteLine("=== 拖出落点判定测试 ===");
            Console.WriteLine();

            try
            {
                TestDesktop();
                TestExplorer();
                TestOther();
                TestUnknown();
                TestShouldMove();
            }
            catch (Exception ex)
            {
                Check(false, "测试不抛异常", ex.GetType().Name + ": " + ex.Message);
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static void TestDesktop()
        {
            Console.WriteLine("--- 1. 桌面 ---");

            Check(DropTarget.ClassifyByClassName("Progman") == DropTarget.Kind.Desktop,
                "★Progman → 桌面");
            Check(DropTarget.ClassifyByClassName("WorkerW") == DropTarget.Kind.Desktop,
                "★WorkerW → 桌面");
            Check(DropTarget.ClassifyByClassName("SysListView32") == DropTarget.Kind.Desktop,
                "★SysListView32 → 桌面");
            Check(DropTarget.ClassifyByClassName("progman") == DropTarget.Kind.Desktop,
                "★小写也认");
            Check(DropTarget.ClassifyByClassName("WORKERW") == DropTarget.Kind.Desktop,
                "★大写也认");

            Console.WriteLine();
        }

        static void TestExplorer()
        {
            Console.WriteLine("--- 2. 资源管理器 ---");

            Check(DropTarget.ClassifyByClassName("CabinetWClass") == DropTarget.Kind.Explorer,
                "★CabinetWClass → 资源管理器");
            Check(DropTarget.ClassifyByClassName("ExploreWClass") == DropTarget.Kind.Explorer,
                "★ExploreWClass → 资源管理器");
            Check(DropTarget.ClassifyByClassName("cabinetwclass") == DropTarget.Kind.Explorer,
                "★小写也认");

            Console.WriteLine();
        }

        static void TestOther()
        {
            Console.WriteLine("--- 3. 其他程序 ---");

            Check(DropTarget.ClassifyByClassName("TXGuiFoundation") == DropTarget.Kind.Other,
                "★QQ → Other");
            Check(DropTarget.ClassifyByClassName("WeChatMainWndForPC") == DropTarget.Kind.Other,
                "★微信 → Other");
            Check(DropTarget.ClassifyByClassName("Chrome_WidgetWin_1") == DropTarget.Kind.Other,
                "★Chrome → Other");
            Check(DropTarget.ClassifyByClassName("Notepad") == DropTarget.Kind.Other,
                "★记事本 → Other");

            Console.WriteLine();
        }

        static void TestUnknown()
        {
            Console.WriteLine("--- 4. 认不出 ---");

            Check(DropTarget.ClassifyByClassName(null) == DropTarget.Kind.Unknown,
                "★null → Unknown");
            Check(DropTarget.ClassifyByClassName("") == DropTarget.Kind.Unknown,
                "★空串 → Unknown");

            Console.WriteLine();
        }

        static void TestShouldMove()
        {
            Console.WriteLine("--- 5. 是否该移动源文件 ---");

            Check(DropTarget.ShouldMove(DropTarget.Kind.Desktop),
                "★桌面 → 要移动");
            Check(DropTarget.ShouldMove(DropTarget.Kind.Explorer),
                "★资源管理器 → 要移动");
            Check(!DropTarget.ShouldMove(DropTarget.Kind.Other),
                "★其他程序 → 不移动（只发送）");
            Check(!DropTarget.ShouldMove(DropTarget.Kind.Unknown),
                "★Unknown → 不移动（取消）");

            Console.WriteLine();
        }
    }
}
