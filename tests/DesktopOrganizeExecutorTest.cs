using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    static class DesktopOrganizeExecutorTest
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
            string path = Path.Combine(Path.GetTempPath(), "clover-exec-" + tag + "-"
                + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        static void Clean(string root)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }

        static DesktopOrganizeRule Rule(string name, string ext,
            DesktopRuleAction action, string target)
        {
            return new DesktopOrganizeRule
            {
                Id = Guid.NewGuid().ToString("N"), Name = name, NamePattern = "*",
                Extensions = ext, Action = action,
                TargetKind = DesktopRuleTargetKind.Folder, Target = target
            };
        }

        static void Main()
        {
            TestMoveCopyIgnoreAndDeselection();
            TestTargetRaceNeverOverwrites();
            TestSourceChangeNeedsNewPreview();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static void TestMoveCopyIgnoreAndDeselection()
        {
            string root = Root("mixed");
            try
            {
                string src = Path.Combine(root, "desktop");
                string dst = Path.Combine(root, "target");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dst);
                string move = Path.Combine(src, "move.txt");
                string copy = Path.Combine(src, "copy.pdf");
                string ignore = Path.Combine(src, "ignore.tmp");
                string deselected = Path.Combine(src, "later.log");
                File.WriteAllText(move, "move");
                File.WriteAllText(copy, "copy");
                File.WriteAllText(ignore, "ignore");
                File.WriteAllText(deselected, "later");

                List<DesktopOrganizeRule> rules = new List<DesktopOrganizeRule>
                {
                    Rule("移动", "txt", DesktopRuleAction.Move, dst),
                    Rule("复制", "pdf", DesktopRuleAction.Copy, dst),
                    Rule("忽略", "tmp", DesktopRuleAction.Ignore, ""),
                    Rule("暂不执行", "log", DesktopRuleAction.Move, dst)
                };
                DesktopOrganizePlan plan = DesktopOrganizePlanner.Build(
                    new string[] { move, copy, ignore, deselected }, rules, null);
                plan.Items[3].Selected = false;

                DesktopOrganizeExecution done = DesktopOrganizeExecutor.Execute(plan.Items);
                Check(done.Succeeded == 2 && done.Failed == 0, "只执行勾选的移动/复制项");
                Check(!File.Exists(move) && File.Exists(Path.Combine(dst, "move.txt")),
                    "移动成功后源消失、目标存在");
                Check(File.Exists(copy) && File.Exists(Path.Combine(dst, "copy.pdf")),
                    "复制成功后源与目标都存在");
                Check(File.Exists(ignore) && !File.Exists(Path.Combine(dst, "ignore.tmp")),
                    "忽略项不做文件写入");
                Check(File.Exists(deselected) && !File.Exists(Path.Combine(dst, "later.log")),
                    "预览取消勾选项不执行");
            }
            finally { Clean(root); }
        }

        static void TestTargetRaceNeverOverwrites()
        {
            string root = Root("race");
            try
            {
                string srcDir = Path.Combine(root, "desktop");
                string dst = Path.Combine(root, "target");
                Directory.CreateDirectory(srcDir);
                Directory.CreateDirectory(dst);
                string src = Path.Combine(srcDir, "same.txt");
                File.WriteAllText(src, "source");

                DesktopOrganizePlan plan = DesktopOrganizePlanner.Build(
                    new string[] { src },
                    new List<DesktopOrganizeRule> { Rule("移动", "txt", DesktopRuleAction.Move, dst) },
                    null);
                File.WriteAllText(plan.Items[0].TargetPath, "competitor");

                DesktopOrganizeExecution done = DesktopOrganizeExecutor.Execute(plan.Items);
                Check(done.Succeeded == 0 && done.Failed == 1, "预览后目标被占用则失败");
                Check(File.Exists(src) && File.ReadAllText(src) == "source",
                    "目标竞争时源文件完整保留");
                Check(File.ReadAllText(plan.Items[0].TargetPath) == "competitor",
                    "绝不覆盖竞争者文件");
                Check(done.Items[0].Message.Contains("重新预览"),
                    "失败原因提示重新预览", done.Items[0].Message);
            }
            finally { Clean(root); }
        }

        static void TestSourceChangeNeedsNewPreview()
        {
            string root = Root("source-change");
            try
            {
                string srcDir = Path.Combine(root, "desktop");
                string dst = Path.Combine(root, "target");
                Directory.CreateDirectory(srcDir);
                Directory.CreateDirectory(dst);
                string src = Path.Combine(srcDir, "change.txt");
                File.WriteAllText(src, "old");

                DesktopOrganizePlan plan = DesktopOrganizePlanner.Build(
                    new string[] { src },
                    new List<DesktopOrganizeRule> { Rule("移动", "txt", DesktopRuleAction.Move, dst) },
                    null);
                File.WriteAllText(src, "new content is longer");

                DesktopOrganizeExecution done = DesktopOrganizeExecutor.Execute(plan.Items);
                Check(done.Succeeded == 0 && done.Failed == 1, "源文件改变后拒绝执行");
                Check(File.Exists(src) && File.ReadAllText(src).StartsWith("new"),
                    "源变化失败时保留最新源文件");
                Check(!File.Exists(plan.Items[0].TargetPath), "源变化时不发布目标");
            }
            finally { Clean(root); }
        }
    }
}
