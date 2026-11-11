using System;
using System.IO;

namespace Clover
{
    static class DesktopOrganizeRulesTest
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
            string path = Path.Combine(Path.GetTempPath(), "clover-rules-" + tag + "-"
                + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        static void Clean(string root)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }

        static void Main()
        {
            TestDefaultsAndValidation();
            TestRoundTrip();
            TestBackupFallback();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static void TestDefaultsAndValidation()
        {
            DesktopOrganizeRule rule = new DesktopOrganizeRule();
            Check(rule.Enabled, "规则默认启用");
            Check(rule.Action == DesktopRuleAction.Move, "规则默认动作是移动");
            Check(rule.NamePattern == "*", "规则默认匹配所有名称");
            Check(DesktopOrganizeRules.Validate(rule) == "请输入规则名称。",
                "空规则名有明确错误");

            rule.Name = "图片";
            Check(DesktopOrganizeRules.Validate(rule) == "请选择目标桌面。",
                "移动规则必须有目标桌面");
            rule.Action = DesktopRuleAction.Ignore;
            Check(DesktopOrganizeRules.Validate(rule) == null,
                "忽略规则不要求目标");

            rule.ModifiedAfter = "2026/11/10";
            Check(DesktopOrganizeRules.Validate(rule) != null,
                "非法日期不会被静默清空");
            rule.ModifiedAfter = "";

            rule.Action = DesktopRuleAction.Copy;
            rule.TargetKind = DesktopRuleTargetKind.Folder;
            rule.Target = @"C:\Temp";
            rule.MinSizeBytes = 200;
            rule.MaxSizeBytes = 100;
            Check(DesktopOrganizeRules.Validate(rule) == "最小大小不能大于最大大小。",
                "大小范围倒置被拒绝");
        }

        static void TestRoundTrip()
        {
            string root = Root("roundtrip");
            try
            {
                DesktopOrganizeRules store = DesktopOrganizeRules.Load(root);
                DesktopOrganizeRule first = new DesktopOrganizeRule();
                first.Name = "图片移动";
                first.NamePattern = "IMG_*;截图*";
                first.Extensions = "jpg;png";
                first.MinSizeBytes = 10;
                first.MaxSizeBytes = 9999;
                first.ModifiedAfter = "2026-01-02";
                first.ModifiedBefore = "2026-11-10";
                first.Action = DesktopRuleAction.Move;
                first.TargetKind = DesktopRuleTargetKind.Desk;
                first.Target = "桌面文件";
                store.Items.Add(first);

                DesktopOrganizeRule second = new DesktopOrganizeRule();
                second.Name = "临时文件忽略";
                second.Enabled = false;
                second.Extensions = "tmp";
                second.Action = DesktopRuleAction.Ignore;
                store.Items.Add(second);

                string error;
                Check(store.Save(out error), "规则保存成功", error);
                Check(File.Exists(DesktopOrganizeRules.PathOf(root)), "规则文件已创建");

                DesktopOrganizeRules loaded = DesktopOrganizeRules.Load(root);
                Check(loaded.Items.Count == 2, "规则数量往返", loaded.Items.Count.ToString());
                Check(loaded.Items[0].Name == "图片移动", "规则顺序和名称往返");
                Check(loaded.Items[0].Action == DesktopRuleAction.Move, "移动动作往返");
                Check(loaded.Items[0].TargetKind == DesktopRuleTargetKind.Desk, "目标类型往返");
                Check(loaded.Items[0].ModifiedAfter == "2026-01-02", "日期按稳定格式往返");
                Check(!loaded.Items[1].Enabled, "禁用状态往返");
                Check(loaded.Items[1].Action == DesktopRuleAction.Ignore, "忽略动作往返");
                Check(!string.IsNullOrEmpty(loaded.Items[0].Id)
                    && !string.IsNullOrEmpty(loaded.Items[1].Id)
                    && loaded.Items[0].Id != loaded.Items[1].Id,
                    "保存时补齐唯一规则 ID");
            }
            finally { Clean(root); }
        }

        static void TestBackupFallback()
        {
            string root = Root("fallback");
            try
            {
                DesktopOrganizeRules store = DesktopOrganizeRules.Load(root);
                DesktopOrganizeRule rule = new DesktopOrganizeRule();
                rule.Name = "保留规则";
                rule.Action = DesktopRuleAction.Ignore;
                store.Items.Add(rule);
                Check(store.Save(), "首次保存供回退使用");

                // 第二次原子写会把第一版放进 .bak。
                store.Items[0].Name = "主文件新版";
                Check(store.Save(), "第二次保存生成 .bak");

                string path = DesktopOrganizeRules.PathOf(root);
                Check(File.Exists(path + ".bak"), "存在备份文件");
                File.WriteAllText(path, "{ broken", System.Text.Encoding.UTF8);

                DesktopOrganizeRules fallback = DesktopOrganizeRules.Load(root);
                Check(fallback.Items.Count == 1, "主文件损坏时从备份恢复");
                Check(fallback.Items[0].Name == "保留规则", "恢复的是上一版完整规则",
                    fallback.Items.Count == 0 ? "空" : fallback.Items[0].Name);

                File.WriteAllText(path + ".bak", "[broken", System.Text.Encoding.UTF8);
                DesktopOrganizeRules empty = DesktopOrganizeRules.Load(root);
                Check(empty.Items.Count == 0, "主文件与备份都损坏时安全回退为空");
            }
            finally { Clean(root); }
        }
    }
}
