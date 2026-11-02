using System;
using System.Collections.Generic;

namespace Clover
{
    /// <summary>
    /// 拒绝反馈文案测试：单原因 / 多原因排序 / 全部被拒 / 无被拒返回 null /
    /// 扩展名超 3 种 / 复数与零值边界。
    ///
    /// **纯函数测试，不碰磁盘、不碰 UI。**
    /// </summary>
    static class DropSummaryTest
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
            Console.WriteLine("=== 拒绝反馈文案测试 ===");
            Console.WriteLine();

            try
            {
                TestNoRejected();
                TestSingleReason();
                TestMultiReasonOrder();
                TestAllRejected();
                TestExtsTruncation();
                TestEdgeCases();
                TestDeterminism();
            }
            catch (Exception ex)
            {
                Check(false, "测试不抛异常", ex.GetType().Name + ": " + ex.Message);
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 构造工具 ----------

        static DropSpec.Rejected R(DropSpec.RejectReason reason, string ext)
        {
            DropSpec.Rejected r = new DropSpec.Rejected();
            r.Reason = reason;
            r.Ext = ext ?? "";
            r.Name = "file" + (ext ?? "");
            r.Path = @"C:\x\file" + (ext ?? "");
            return r;
        }

        static List<DropSpec.Rejected> List(params DropSpec.Rejected[] items)
        {
            return new List<DropSpec.Rejected>(items);
        }

        // ---------- 1. 无被拒 → null ----------
        static void TestNoRejected()
        {
            Console.WriteLine("--- 1. 无被拒项 → null ---");

            Check(DropSpec.BuildSummary(3, null) == null, "★null → null");
            Check(DropSpec.BuildSummary(3, new List<DropSpec.Rejected>()) == null,
                "★空列表 → null");
            Check(DropSpec.BuildSummary(0, null) == null, "★0 接受 + null → null");
            Check(DropSpec.BuildSummary(3, List(new DropSpec.Rejected[] { null })) == null,
                "★只含 null 元素 → null");

            Console.WriteLine();
        }

        // ---------- 2. 单原因 ----------
        static void TestSingleReason()
        {
            Console.WriteLine("--- 2. 单原因 ---");

            string s = DropSpec.BuildSummary(2, List(
                R(DropSpec.RejectReason.AppFile, ".exe"),
                R(DropSpec.RejectReason.AppFile, ".lnk"),
                R(DropSpec.RejectReason.AppFile, ".exe")));

            Check(s == "已放入 2 个，跳过 3 个应用类文件（.exe/.lnk）",
                "★单原因完整文案", s);

            // 单原因且只有一个扩展名
            string s2 = DropSpec.BuildSummary(1, List(
                R(DropSpec.RejectReason.AppFile, ".msi")));
            Check(s2 == "已放入 1 个，跳过 1 个应用类文件（.msi）", "★单个被拒", s2);

            // 其它原因也要走同一模板
            string s3 = DropSpec.BuildSummary(5, List(
                R(DropSpec.RejectReason.TooLarge, ".png")));
            Check(s3 == "已放入 5 个，跳过 1 个超出大小上限（.png）", "★超大小原因", s3);

            string s4 = DropSpec.BuildSummary(0, List(
                R(DropSpec.RejectReason.UnsupportedType, ".xyz")));
            Check(s4 == "没有放入任何文件，跳过 1 个不支持的类型（.xyz）",
                "★0 接受走另一前缀", s4);

            Console.WriteLine();
        }

        // ---------- 3. 多原因排序 ----------
        static void TestMultiReasonOrder()
        {
            Console.WriteLine("--- 3. 多原因排序（数量从多到少）---");

            // 应用类 3 个、不支持 1 个 → 应用类在前
            string s = DropSpec.BuildSummary(2, List(
                R(DropSpec.RejectReason.UnsupportedType, ".xyz"),
                R(DropSpec.RejectReason.AppFile, ".exe"),
                R(DropSpec.RejectReason.AppFile, ".lnk"),
                R(DropSpec.RejectReason.AppFile, ".msi")));

            Check(s == "已放入 2 个，跳过 4 个：应用类文件 3、不支持的类型 1",
                "★多的在前", s);

            // 反过来：不支持 2 个、应用类 1 个 → 不支持在前
            string s2 = DropSpec.BuildSummary(1, List(
                R(DropSpec.RejectReason.AppFile, ".exe"),
                R(DropSpec.RejectReason.UnsupportedType, ".xyz"),
                R(DropSpec.RejectReason.UnsupportedType, ".abc")));

            Check(s2 == "已放入 1 个，跳过 3 个：不支持的类型 2、应用类文件 1",
                "★顺序跟着数量走", s2);

            // 三种原因
            string s3 = DropSpec.BuildSummary(0, List(
                R(DropSpec.RejectReason.AppFile, ".exe"),
                R(DropSpec.RejectReason.AppFile, ".lnk"),
                R(DropSpec.RejectReason.AppFile, ".bat"),
                R(DropSpec.RejectReason.TooLarge, ".bin"),
                R(DropSpec.RejectReason.TooLarge, ".iso"),
                R(DropSpec.RejectReason.UnsupportedType, ".xyz")));

            Check(s3 == "没有放入任何文件，跳过 6 个：应用类文件 3、超出大小上限 2、不支持的类型 1",
                "★三种原因降序", s3);

            // 多原因时不列扩展名（避免一行装不下）
            Check(s3.IndexOf("（.", StringComparison.Ordinal) < 0,
                "★多原因不列扩展名", s3);

            Console.WriteLine();
        }

        // ---------- 4. 全部被拒 ----------
        static void TestAllRejected()
        {
            Console.WriteLine("--- 4. 全部被拒 ---");

            string s = DropSpec.BuildSummary(0, List(
                R(DropSpec.RejectReason.AppFile, ".exe"),
                R(DropSpec.RejectReason.AppFile, ".lnk"),
                R(DropSpec.RejectReason.AppFile, ".exe")));

            Check(s == "没有放入任何文件，跳过 3 个应用类文件（.exe/.lnk）",
                "★全拒文案", s);
            Check(s.IndexOf("已放入", StringComparison.Ordinal) < 0,
                "★不出现「已放入」", s);

            // 全拒 + 多原因
            string s2 = DropSpec.BuildSummary(0, List(
                R(DropSpec.RejectReason.AppFile, ".exe"),
                R(DropSpec.RejectReason.NotDirectory, "")));
            Check(s2 == "没有放入任何文件，跳过 2 个：应用类文件 1、不是文件夹 1",
                "★全拒 + 多原因", s2);

            Console.WriteLine();
        }

        // ---------- 5. 扩展名超 3 种 ----------
        static void TestExtsTruncation()
        {
            Console.WriteLine("--- 5. 扩展名超过 3 种 ---");

            string s = DropSpec.BuildSummary(1, List(
                R(DropSpec.RejectReason.AppFile, ".exe"),
                R(DropSpec.RejectReason.AppFile, ".lnk"),
                R(DropSpec.RejectReason.AppFile, ".msi"),
                R(DropSpec.RejectReason.AppFile, ".bat"),
                R(DropSpec.RejectReason.AppFile, ".cmd")));

            Check(s == "已放入 1 个，跳过 5 个应用类文件（.bat/.cmd/.exe 等）",
                "★超 3 种写「等」", s);

            // 正好 3 种：不写「等」
            string s3 = DropSpec.BuildSummary(1, List(
                R(DropSpec.RejectReason.AppFile, ".exe"),
                R(DropSpec.RejectReason.AppFile, ".lnk"),
                R(DropSpec.RejectReason.AppFile, ".msi")));
            Check(s3 == "已放入 1 个，跳过 3 个应用类文件（.exe/.lnk/.msi）",
                "★正好 3 种不加「等」", s3);
            Check(s3.IndexOf("等", StringComparison.Ordinal) < 0, "3 种时没有「等」字");

            // 4 种：列 3 个 + 等
            string s4 = DropSpec.BuildSummary(1, List(
                R(DropSpec.RejectReason.AppFile, ".exe"),
                R(DropSpec.RejectReason.AppFile, ".lnk"),
                R(DropSpec.RejectReason.AppFile, ".msi"),
                R(DropSpec.RejectReason.AppFile, ".bat")));
            Check(s4.IndexOf(" 等）", StringComparison.Ordinal) > 0, "★4 种时出现「等」", s4);

            // FormatExts 单独测
            Check(DropSpec.FormatExts(null) == "", "null → 空串");
            Check(DropSpec.FormatExts(new List<DropSpec.Rejected>()) == "", "空列表 → 空串");
            Check(DropSpec.FormatExts(List(R(DropSpec.RejectReason.AppFile, ""))) == "",
                "★无扩展名 → 空串（不输出空括号）");
            Check(DropSpec.MaxExtsShown == 3, "★上限常量是 3");

            Console.WriteLine();
        }

        // ---------- 6. 边界 ----------
        static void TestEdgeCases()
        {
            Console.WriteLine("--- 6. 复数 / 零值边界 ---");

            // 1 个也用「个」而不是「些」——中文没有单复数变化，但要确认措辞稳定
            string s1 = DropSpec.BuildSummary(1, List(
                R(DropSpec.RejectReason.AppFile, ".exe")));
            Check(s1 == "已放入 1 个，跳过 1 个应用类文件（.exe）", "★1 个的说法", s1);

            // 负数接受数被钳到 0（防御，正常不会出现）
            string s2 = DropSpec.BuildSummary(-5, List(
                R(DropSpec.RejectReason.AppFile, ".exe")));
            Check(s2.StartsWith("没有放入任何文件", StringComparison.Ordinal),
                "★负数接受数按 0 处理", s2);

            // 大数字
            string s3 = DropSpec.BuildSummary(999, List(
                R(DropSpec.RejectReason.AppFile, ".exe")));
            Check(s3 == "已放入 999 个，跳过 1 个应用类文件（.exe）", "★大数字", s3);

            // 空扩展名的被拒项：文案里不出现空括号
            string s4 = DropSpec.BuildSummary(0, List(
                R(DropSpec.RejectReason.NotDirectory, "")));
            Check(s4 == "没有放入任何文件，跳过 1 个不是文件夹", "★无扩展名不出空括号", s4);

            // 每个原因单独跑一遍，确认都能拼出完整句子
            foreach (DropSpec.RejectReason reason in Enum.GetValues(typeof(DropSpec.RejectReason)))
            {
                string one = DropSpec.BuildSummary(1, List(R(reason, ".x")));
                Check(!string.IsNullOrEmpty(one)
                    && one.IndexOf(DropSpec.ReasonText(reason), StringComparison.Ordinal) >= 0,
                    "原因 " + reason + " 能拼出句子", one);
            }

            Console.WriteLine();
        }

        // ---------- 7. 确定性 ----------
        static void TestDeterminism()
        {
            Console.WriteLine("--- 7. 同数量原因的顺序是确定的 ---");

            // 应用类 2、不支持 2 —— 数量相同，靠枚举值排序（AppFile=1 < UnsupportedType=0？）
            // 实际枚举：UnsupportedType=0, AppFile=1, TooMany=2, TooLarge=3, NotDirectory=4
            // 所以同数量时 UnsupportedType 在前
            string a = DropSpec.BuildSummary(0, List(
                R(DropSpec.RejectReason.AppFile, ".exe"),
                R(DropSpec.RejectReason.AppFile, ".lnk"),
                R(DropSpec.RejectReason.UnsupportedType, ".xyz"),
                R(DropSpec.RejectReason.UnsupportedType, ".abc")));

            string b = DropSpec.BuildSummary(0, List(
                R(DropSpec.RejectReason.UnsupportedType, ".xyz"),
                R(DropSpec.RejectReason.AppFile, ".exe"),
                R(DropSpec.RejectReason.UnsupportedType, ".abc"),
                R(DropSpec.RejectReason.AppFile, ".lnk")));

            Check(a == b, "★输入顺序不影响输出", a + "  vs  " + b);
            Check(a.IndexOf("不支持的类型 2、应用类文件 2", StringComparison.Ordinal) > 0,
                "★同数量按枚举值升序", a);

            // GroupByReason 直接测
            List<KeyValuePair<DropSpec.RejectReason, int>> g = DropSpec.GroupByReason(List(
                R(DropSpec.RejectReason.TooMany, ".a"),
                R(DropSpec.RejectReason.AppFile, ".b"),
                R(DropSpec.RejectReason.AppFile, ".c")));

            Check(g.Count == 2, "两组");
            Check(g[0].Key == DropSpec.RejectReason.AppFile && g[0].Value == 2,
                "★第一组是应用类 2");
            Check(g[1].Key == DropSpec.RejectReason.TooMany && g[1].Value == 1,
                "第二组是超数量 1");

            Console.WriteLine();
        }
    }
}
