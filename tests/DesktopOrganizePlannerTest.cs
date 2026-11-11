using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    static class DesktopOrganizePlannerTest
    {
        static int passed;
        static int failed;

        static void Check(bool condition, string name, string detail = "")
        {
            if (condition) { passed++; Console.WriteLine("PASS  " + name); }
            else
            {
                failed++;
                Console.WriteLine("FAIL  " + name
                    + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
            }
        }

        static string Root(string tag)
        {
            string path = Path.Combine(Path.GetTempPath(), "clover-plan-" + tag + "-"
                + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        static void Clean(string root)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }

        static DesktopOrganizeRule Rule(string name, string pattern, string exts,
            DesktopRuleAction action, string target)
        {
            return new DesktopOrganizeRule
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                NamePattern = pattern,
                Extensions = exts,
                Action = action,
                TargetKind = DesktopRuleTargetKind.Folder,
                Target = target
            };
        }

        static void Main()
        {
            TestWildcard();
            TestConditions();
            TestPriorityAndTargets();
            TestMixedPlan();
            TestScanTopLevelOnly();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static void TestWildcard()
        {
            Check(DesktopOrganizePlanner.Wildcard("Screenshot 12.PNG", "screen*?.png"),
                "通配符支持 * / ? 且忽略大小写");
            Check(DesktopOrganizePlanner.Wildcard("报告.docx", "报?.docx"),
                "通配符支持中文");
            Check(!DesktopOrganizePlanner.Wildcard("abc.txt", "a?d*"),
                "通配符不误命中");

            string root = Root("pattern-space");
            try
            {
                string file = Path.Combine(root, "项目 报告.pdf");
                File.WriteAllText(file, "x");
                DesktopOrganizeRule rule = Rule("带空格", "项目 *.pdf", "pdf",
                    DesktopRuleAction.Ignore, "");
                Check(DesktopOrganizePlanner.Matches(rule, file),
                    "文件名模式中的空格不会被拆开");
            }
            finally { Clean(root); }
        }

        static void TestConditions()
        {
            string root = Root("conditions");
            try
            {
                string file = Path.Combine(root, "IMG_2026.JPG");
                File.WriteAllBytes(file, new byte[128]);
                File.SetLastWriteTime(file, new DateTime(2026, 6, 15, 9, 0, 0));

                DesktopOrganizeRule rule = Rule("图片", "img_*", "png; jpg",
                    DesktopRuleAction.Ignore, "");
                rule.MinSizeBytes = 100;
                rule.MaxSizeBytes = 200;
                rule.ModifiedAfter = "2026-06-01";
                rule.ModifiedBefore = "2026-06-30";
                Check(DesktopOrganizePlanner.Matches(rule, file),
                    "名称/扩展名/大小/日期同时命中");

                rule.MinSizeBytes = 129;
                Check(!DesktopOrganizePlanner.Matches(rule, file), "小于最小大小不命中");
                rule.MinSizeBytes = -1;
                rule.MaxSizeBytes = 127;
                Check(!DesktopOrganizePlanner.Matches(rule, file), "大于最大大小不命中");
                rule.MaxSizeBytes = -1;
                rule.ModifiedAfter = "2026-06-16";
                Check(!DesktopOrganizePlanner.Matches(rule, file), "早于修改日期起点不命中");

                string folder = Path.Combine(root, "IMG_folder");
                Directory.CreateDirectory(folder);
                rule.ModifiedAfter = "";
                rule.ModifiedBefore = "";
                rule.Extensions = "jpg";
                Check(!DesktopOrganizePlanner.Matches(rule, folder), "带扩展名条件时文件夹不命中");
                rule.Extensions = "";
                Check(DesktopOrganizePlanner.Matches(rule, folder), "无扩展名和大小条件时文件夹可命中");
            }
            finally { Clean(root); }
        }

        static void TestPriorityAndTargets()
        {
            string root = Root("priority");
            try
            {
                string source = Path.Combine(root, "desktop");
                string firstTarget = Path.Combine(root, "first");
                string secondTarget = Path.Combine(root, "second");
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(firstTarget);
                Directory.CreateDirectory(secondTarget);
                string file = Path.Combine(source, "photo.jpg");
                File.WriteAllText(file, "new");
                File.WriteAllText(Path.Combine(firstTarget, "photo.jpg"), "old");

                List<DesktopOrganizeRule> rules = new List<DesktopOrganizeRule>
                {
                    Rule("第一条", "*", "jpg", DesktopRuleAction.Move, firstTarget),
                    Rule("第二条", "photo*", "jpg", DesktopRuleAction.Copy, secondTarget)
                };
                DesktopOrganizePlan plan = DesktopOrganizePlanner.Build(
                    new string[] { file }, rules, null);
                DesktopOrganizePlanItem item = plan.Items[0];
                Check(item.MatchedRules.Count == 2, "预览显示全部命中规则");
                Check(item.RuleName == "第一条" && item.Action == DesktopRuleAction.Move,
                    "多规则命中采用顺序最前一条");
                Check(Path.GetFileName(item.TargetPath) == "photo (1).jpg",
                    "已有同名目标在预览阶段自动改名", item.TargetPath);
                Check(File.ReadAllText(Path.Combine(firstTarget, "photo.jpg")) == "old",
                    "生成计划不写文件");
            }
            finally { Clean(root); }
        }

        static void TestMixedPlan()
        {
            string root = Root("mixed");
            try
            {
                string source = Path.Combine(root, "desktop");
                string target = Path.Combine(root, "target");
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(target);
                string move = Path.Combine(source, "move.txt");
                string copy = Path.Combine(source, "copy.pdf");
                string ignore = Path.Combine(source, "ignore.tmp");
                string unmatched = Path.Combine(source, "other.bin");
                string protectedFile = Path.Combine(source, "setup.exe");
                File.WriteAllText(move, "m");
                File.WriteAllText(copy, "c");
                File.WriteAllText(ignore, "i");
                File.WriteAllText(unmatched, "u");
                File.WriteAllText(protectedFile, "p");

                List<DesktopOrganizeRule> rules = new List<DesktopOrganizeRule>
                {
                    Rule("移动文本", "*", "txt", DesktopRuleAction.Move, target),
                    Rule("复制 PDF", "*", "pdf", DesktopRuleAction.Copy, target),
                    Rule("忽略临时", "*", "tmp", DesktopRuleAction.Ignore, ""),
                    Rule("应用保护", "*", "exe", DesktopRuleAction.Move, target)
                };
                DesktopOrganizePlan plan = DesktopOrganizePlanner.Build(
                    new string[] { move, copy, ignore, unmatched, protectedFile }, rules, null);

                Check(plan.Items[0].State == DesktopPlanState.Ready
                    && plan.Items[0].Selected, "移动项默认勾选");
                Check(plan.Items[1].State == DesktopPlanState.Ready
                    && plan.Items[1].Action == DesktopRuleAction.Copy, "复制项可执行");
                Check(plan.Items[2].State == DesktopPlanState.Ignored
                    && !plan.Items[2].Selected, "忽略项明确且不勾选");
                Check(plan.Items[3].State == DesktopPlanState.Unmatched,
                    "未匹配项明确列出");
                Check(plan.Items[4].State == DesktopPlanState.Ready,
                    "显式规则可以整理应用类文件");
                Check(plan.ReadyCount == 3 && plan.IgnoredCount == 1,
                    "计划汇总数量正确");
            }
            finally { Clean(root); }
        }

        static void TestScanTopLevelOnly()
        {
            string root = Root("scan");
            try
            {
                string desktop = Path.Combine(root, "desktop");
                string folder = Path.Combine(desktop, "folder");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(desktop, "top.txt"), "t");
                File.WriteAllText(Path.Combine(folder, "inner.txt"), "i");

                DesktopOrganizeScan scan = DesktopOrganizePlanner.Scan(
                    new string[] { desktop, desktop });
                Check(scan.Paths.Count == 2, "桌面目录去重且只扫描直接子项",
                    scan.Paths.Count.ToString());
                Check(!scan.Paths.Contains(Path.Combine(folder, "inner.txt")),
                    "不会递归修改文件夹内部结构");
            }
            finally { Clean(root); }
        }
    }
}
