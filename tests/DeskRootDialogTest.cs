using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 「更改存放目录」对话框的文案与默认项。
    ///
    /// ── 为什么单独测这两点 ──
    ///
    /// 对话框要 ShowDialog 才能走完整流程，无人值守跑不了。
    /// 但需求里有两件事是**可断言**的，而且一旦被改坏用户就会骂：
    ///   1. 默认选项必须是「搬过去」
    ///   2. 弹窗里必须写明会搬多少个文件
    ///
    /// 所以把这两块抽成纯函数（`DefaultChoice` / `BuildCountText`），
    /// 在这里钉死。对话框外壳（按钮、布局）不做断言 —— 那属于肉眼验收。
    /// </summary>
    static class DeskRootDialogTest
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

        [STAThread]
        static void Main()
        {
            Console.WriteLine("=== 更改存放目录 对话框测试 ===");
            Console.WriteLine();

            try
            {
                TestDefault();
                TestCountText();
            }
            catch (Exception ex)
            {
                Check(false, "测试不抛异常", ex.GetType().Name + ": " + ex.Message);
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 默认项 ----------
        static void TestDefault()
        {
            Console.WriteLine("--- 1. 默认选项 ---");

            Check(DeskRootDialog.DefaultChoice == DeskRootDialog.Choice.Migrate,
                "★默认是「搬过去」", DeskRootDialog.DefaultChoice.ToString());

            // 默认项不能是 Cancel：用户在设置里主动点了「更改…」并选好目录，
            // 默认取消等于把意图丢掉。
            Check(DeskRootDialog.DefaultChoice != DeskRootDialog.Choice.Cancel,
                "★默认不是取消");

            // 枚举值本身也别被改乱（Cancel = 0 是「什么都不做」的安全默认）
            Check((int)DeskRootDialog.Choice.Cancel == 0, "★Cancel 仍是 0");
            Check(Enum.IsDefined(typeof(DeskRootDialog.Choice), DeskRootDialog.DefaultChoice),
                "★默认值在枚举范围内");

            Console.WriteLine();
        }

        // ---------- 2. 数量文案 ----------
        static void TestCountText()
        {
            Console.WriteLine("--- 2. 数量文案 ---");

            DeskRootChange.Plan p = new DeskRootChange.Plan();
            p.Desks.Add("工作");
            p.Desks.Add("娱乐");
            p.Desks.Add("学习");
            p.Files = 128;
            p.Entries = 30;

            string t = DeskRootDialog.BuildCountText(p);
            Check(t.Contains("3 个桌面"), "★写出桌面数", t);
            Check(t.Contains("128 个文件"), "★写出文件数", t);

            // 空桌面：不能显示「共 0 个文件」了事，要说清都是空文件夹
            DeskRootChange.Plan empty = new DeskRootChange.Plan();
            empty.Desks.Add("空桌面");
            string te = DeskRootDialog.BuildCountText(empty);
            Check(te.Contains("1 个桌面"), "空桌面也报桌面数", te);
            Check(te.Contains("空文件夹"), "★0 个文件时说明是空文件夹", te);

            // 一个桌面都没有
            DeskRootChange.Plan none = new DeskRootChange.Plan();
            string tn = DeskRootDialog.BuildCountText(none);
            Check(tn.Length > 0, "★没有桌面时也有说明");
            Check(!tn.Contains("0 个桌面"), "不写「0 个桌面」这种废话", tn);

            // null 不炸
            Check(DeskRootDialog.BuildCountText(null) == "", "null → 空串，不抛");

            // 大数字（千级）也要正常
            DeskRootChange.Plan big = new DeskRootChange.Plan();
            for (int i = 0; i < 10; i++) big.Desks.Add("d" + i);
            big.Files = 12345;
            string tb = DeskRootDialog.BuildCountText(big);
            Check(tb.Contains("10 个桌面") && tb.Contains("12345 个文件"), "★千级数量", tb);

            Console.WriteLine();
        }
    }
}
