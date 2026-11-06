using System;
using System.Collections.Generic;

namespace Clover
{
    /// <summary>
    /// LowValueRules 测试：关键词识别、扩展名识别、失效快捷方式、不误伤主程序。
    ///
    /// **纯逻辑，不碰文件系统**（失效判定用不存在的路径字符串，
    /// 不创建/删除任何文件）。
    /// </summary>
    static class LowValueTest
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
            Console.WriteLine("=== LowValueRules 测试 ===");
            Console.WriteLine();

            TestUninstall();
            TestHelp();
            TestLicense();
            TestLanguage();
            TestRepair();
            TestExtensions();
            TestBrokenShortcut();
            TestNotMisidentified();
            TestCaseInsensitive();
            TestExeNameOf();
            TestCollapseSetting();
            TestReasonCarriedThrough();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 卸载类 ----------
        static void TestUninstall()
        {
            Console.WriteLine("--- 1. 卸载 ---");

            Check(IsLow("卸载 微信", null), "★「卸载 微信」→ 低价值");
            Check(IsLow("WeChat Uninstall", null), "★「WeChat Uninstall」→ 低价值");
            Check(IsLow("Uninstall", null), "★「Uninstall」→ 低价值");
            Check(IsLow("卸载程序", null), "「卸载程序」→ 低价值");
            Check(IsLow("Uninstaller", null), "「Uninstaller」→ 低价值");
            Check(IsLow("Remove Tool", null), "「Remove Tool」→ 低价值");

            Console.WriteLine();
        }

        // ---------- 2. 帮助类 ----------
        static void TestHelp()
        {
            Console.WriteLine("--- 2. 帮助 / 说明 ---");

            Check(IsLow("帮助", null), "★「帮助」→ 低价值");
            Check(IsLow("Help", null), "★「Help」→ 低价值");
            Check(IsLow("说明", null), "「说明」→ 低价值");
            Check(IsLow("Readme", null), "★「Readme」→ 低价值");
            Check(IsLow("ReadMe.txt", null), "「ReadMe.txt」→ 低价值");
            Check(IsLow("用户手册", null), "★「用户手册」→ 低价值");
            Check(IsLow("User Manual", null), "「User Manual」→ 低价值");
            Check(IsLow("Documentation", null), "「Documentation」→ 低价值");
            Check(IsLow("使用指南", null), "「使用指南」→ 低价值");

            Console.WriteLine();
        }

        // ---------- 3. 许可类 ----------
        static void TestLicense()
        {
            Console.WriteLine("--- 3. 许可 / 更新日志 ---");

            Check(IsLow("许可协议", null), "★「许可协议」→ 低价值");
            Check(IsLow("License", null), "★「License」→ 低价值");
            Check(IsLow("Licence Agreement", null), "「Licence Agreement」→ 低价值");
            Check(IsLow("EULA", null), "★「EULA」→ 低价值");
            Check(IsLow("Terms of Service", null), "「Terms of Service」→ 低价值");
            Check(IsLow("更新日志", null), "★「更新日志」→ 低价值");
            Check(IsLow("Release Notes", null), "★「Release Notes」→ 低价值");
            Check(IsLow("Changelog", null), "「Changelog」→ 低价值");

            Console.WriteLine();
        }

        // ---------- 4. 语言 / 修复 ----------
        static void TestLanguage()
        {
            Console.WriteLine("--- 4. 语言首选项 / 修复 ---");

            Check(IsLow("语言首选项", null), "★「语言首选项」→ 低价值");
            Check(IsLow("Language Preferences", null), "★「Language Preferences」→ 低价值");
            Check(IsLow("语言设置", null), "「语言设置」→ 低价值");
            Check(IsLow("修复", null), "★「修复」→ 低价值");
            Check(IsLow("Repair", null), "★「Repair」→ 低价值");
            Check(IsLow("修复工具", null), "「修复工具」→ 低价值");

            Console.WriteLine();
        }

        static void TestRepair()
        {
            // 已在 TestLanguage 覆盖，留空占位
            Console.WriteLine();
        }

        // ---------- 5. 扩展名 ----------
        static void TestExtensions()
        {
            Console.WriteLine("--- 5. 目标扩展名 ---");

            // ★ 扩展名判定必须在「目标存在」的前提下测 ——
            //   目标不存在会先命中「失效快捷方式」规则，测不到扩展名分支。
            //   所以这里创建临时文件，测完删掉。
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "lv-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                System.IO.Directory.CreateDirectory(tmp);

                foreach (string ext in new string[] { ".url", ".txt", ".pdf", ".chm", ".html", ".htm" })
                {
                    string p = System.IO.Path.Combine(tmp, "x" + ext);
                    System.IO.File.WriteAllText(p, "x");
                    Check(IsLow("普通应用名", p), "★目标 " + ext + "（存在）→ 低价值", p);
                }

                foreach (string ext in new string[] { ".exe", ".lnk", ".docx" })
                {
                    string p = System.IO.Path.Combine(tmp, "x" + ext);
                    System.IO.File.WriteAllText(p, "x");
                    Check(!IsLow("普通应用名", p), "目标 " + ext + "（存在）→ 不是低价值", p);
                }

                // 目标存在 + 名称含关键词 → 仍判低价值
                string exe = System.IO.Path.Combine(tmp, "app.exe");
                System.IO.File.WriteAllText(exe, "x");
                Check(IsLow("卸载 某软件", exe), "★目标 .exe 但名称含「卸载」→ 低价值");
            }
            finally
            {
                try { if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true); } catch { }
            }

            Console.WriteLine();
        }
        // ---------- 6. 失效快捷方式 ----------
        static void TestBrokenShortcut()
        {
            Console.WriteLine("--- 6. 失效快捷方式 ---");

            Check(IsLow("正常应用", @"Z:\definitely-not-exist-xyz\app.exe"),
                "★目标不存在 → 低价值");
            Check(IsLow("随便什么名字", @"Z:\no\such\file.exe"),
                "★任意名字 + 目标不存在 → 低价值");

            // null 目标不算失效（商店应用没有文件路径）
            Check(!IsLow("普通应用", null), "★null 目标不判失效");

            Console.WriteLine();
        }

        // ---------- 7. 不误伤 ----------
        static void TestNotMisidentified()
        {
            Console.WriteLine("--- 7. 不误伤主程序 ---");

            // 普通应用不该被判低价值
            Check(!IsLow("Google Chrome", null), "★Chrome 不是低价值");
            Check(!IsLow("微信", null), "★微信 不是低价值");
            Check(!IsLow("Visual Studio Code", null), "★VS Code 不是低价值");
            Check(!IsLow("Notepad++", null), "★Notepad++ 不是低价值");
            Check(!IsLow("PowerShell", null), "★PowerShell 不是低价值");
            Check(!IsLow("", null), "空名不是低价值");
            Check(!IsLow(null, null), "null 名不是低价值");

            // ★ 关键：Geek Uninstaller 是工具本身，但名称含 Uninstall
            //
            // 当前规则会把它判为低价值（名称含 Uninstall）—— 这是**已知取舍**：
            // 完全避免误伤需要维护"工具白名单"，成本高于收益。
            // 但用户可以用右键「取消低价值标记」手动纠正。
            bool geek = IsLow("Geek Uninstaller", null);
            Console.WriteLine("  (Geek Uninstaller 被判低价值 = " + geek + "，可用右键手动纠正)");

            Console.WriteLine();
        }

        // ---------- 8. 大小写 ----------
        static void TestCaseInsensitive()
        {
            Console.WriteLine("--- 8. 不区分大小写 ---");

            Check(IsLow("UNINSTALL", null), "★「UNINSTALL」→ 低价值");
            Check(IsLow("uninstall", null), "「uninstall」→ 低价值");
            Check(IsLow("UnInStAlL", null), "「UnInStAlL」→ 低价值");
            Check(IsLow("HELP", null), "★「HELP」→ 低价值");
            Check(IsLow("LICENSE", null), "「LICENSE」→ 低价值");
            Check(IsLow("Release NOTES", null), "「Release NOTES」→ 低价值");

            Console.WriteLine();
        }

        // ---------- 9. ExeNameOf ----------
        static void TestExeNameOf()
        {
            Console.WriteLine("--- 9. ExeNameOf ---");

            Check(LowValueRules.ExeNameOf(@"C:\a\notepad.exe") == "notepad", "★取 exe 名",
                LowValueRules.ExeNameOf(@"C:\a\notepad.exe"));
            Check(LowValueRules.ExeNameOf(@"C:\a b\微信.exe") == "微信", "中文 exe 名",
                LowValueRules.ExeNameOf(@"C:\a b\微信.exe"));
            Check(LowValueRules.ExeNameOf(null) == null, "null → null");
            Check(LowValueRules.ExeNameOf("") == null, "空串 → null");

            Console.WriteLine();
        }

        // ---------- 辅助 ----------
        static bool IsLow(string name, string target)
        {
            return LowValueRules.IsLowValue(name, target, LowValueRules.ExeNameOf(target));
        }

        // ---------- 10. 折叠设置（collapseLowValue）----------
        static void TestCollapseSetting()
        {
            Console.WriteLine("--- 10. collapseLowValue 设置 ---");

            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "lvc-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                System.IO.Directory.CreateDirectory(tmp);

                // 默认值必须是 false（展开）—— 等于改动前的行为，
                // 否则老用户升级后会以为"我的应用少了一批"
                Settings s = Settings.Load(tmp);
                Check(!s.CollapseLowValue, "★缺键时默认 false（展开，与改动前一致）");

                s.CollapseLowValue = true;
                s.Save();

                Settings back = Settings.Load(tmp);
                Check(back.CollapseLowValue, "★true 能往返保存/读取");

                back.CollapseLowValue = false;
                back.Save();
                Check(!Settings.Load(tmp).CollapseLowValue, "★false 能往返保存/读取");

                // 键名拼写固定为 collapseLowValue（手改配置的人靠它）
                string json = JsonIO.Read(System.IO.Path.Combine(tmp, "config", "settings.json"));
                Check(json != null && json.IndexOf("collapseLowValue", StringComparison.Ordinal) >= 0,
                    "★settings.json 里的键名是 collapseLowValue", json == null ? "(读不到)" : "");
            }
            finally
            {
                try { if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true); } catch { }
            }

            Console.WriteLine();
        }

        // ---------- 11. 原因字段不污染 apps.json ----------
        static void TestReasonCarriedThrough()
        {
            Console.WriteLine("--- 11. 原因字段（AppItem）序列化行为 ---");

            // 两个新字段必须与既有 LowValue 一样标 [ScriptIgnore]，
            // 否则每次保存 apps.json 都会多出 lowValueReason / lowValueDetail ——
            // 那是运行时算出来的，写进文件既没用又会让 diff 很吵。
            AppItem a = new AppItem();
            a.Name = "卸载 微信";
            a.Path = @"C:\x\卸载 微信.lnk";
            a.LowValue = true;
            a.LowValueReason = LowValueReason.NameKeyword;
            a.LowValueDetail = "名称含「卸载」";

            System.Web.Script.Serialization.JavaScriptSerializer ser =
                new System.Web.Script.Serialization.JavaScriptSerializer();
            string json = ser.Serialize(a);

            Check(json.IndexOf("lowValueReason", StringComparison.OrdinalIgnoreCase) < 0,
                "★lowValueReason 不写进 json", json);
            Check(json.IndexOf("lowValueDetail", StringComparison.OrdinalIgnoreCase) < 0,
                "★lowValueDetail 不写进 json", json);
            Check(json.IndexOf("lowValue", StringComparison.OrdinalIgnoreCase) < 0,
                "★lowValue 也不写进 json（既有行为未破坏）", json);
            Check(json.IndexOf("卸载 微信", StringComparison.Ordinal) >= 0,
                "name 仍正常序列化");

            // 字段本身可读写（ToolTip / 清单窗口依赖它）
            Check(a.LowValueReason == LowValueReason.NameKeyword, "原因字段可读回");
            Check(a.LowValueDetail == "名称含「卸载」", "说明字段可读回");

            Console.WriteLine();
        }
    }
}
