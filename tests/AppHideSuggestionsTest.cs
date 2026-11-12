using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    static class AppHideSuggestionsTest
    {
        static int passed, failed;
        static void Check(bool ok, string name)
        {
            if (ok) passed++; else failed++;
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
        }
        static AppItem App(string name, string path = null)
        {
            return new AppItem { Name = name, Path = path ?? ("C:\\apps\\" + name + ".lnk"), Category = "开发" };
        }
        static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "CloverHideRules-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                foreach (string name in new[] { "Python 3.13 (64-bit)", "Python 3.11 (64-bit)", "python 3.13.1 (32-bit)",
                    "Python Launcher", "Node.js", "Java 8 Update 491 (64-bit)", "Java SE Development" })
                    Check(AppHideSuggestions.Explain(App(name)).DefaultHide, "无启动记录运行入口可默认选择：" + name);
                foreach (string name in new[] { "IDLE (Python 3.11 64-bit)", "IDLE (Python 3.13 64-bit)",
                    "IDLE", "Anaconda Navigator", "Anaconda Navigator (anaconda3)", "Application Verifier (WOW)",
                    "Application Verifier (X64)", "Sample Desktop Apps", "Tools for UWP Apps", "Windows App Cert Kit" })
                {
                    AppHideSuggestion s = AppHideSuggestions.Explain(App(name));
                    Check(s != null && s.DefaultHide && s.Explanation.Length > 10, "未使用配套入口默认隐藏：" + name);
                }
                foreach (string name in new[] { "Spyder", "Jupyter Notebook", "Visual Studio Code", "Python 项目管理器",
                    "Python 3.13 Tutorial", "Python Tools", "Anaconda Prompt", "Miniconda", "7-Zip File Manager", "Geek Uninstaller",
                    "ego (lite)", "Deno", "Git Bash", "Postman", "My Java App", "Node.js Tools", "Windows Software", "新应用" })
                    Check(AppHideSuggestions.Explain(App(name)) == null, "未知或主动工具不按家族误伤：" + name);
                Check(AppHideSuggestions.Explain(null) == null && AppHideSuggestions.Explain(new AppItem { Name = "Python" }) == null,
                    "空条目与空路径跳过");
                Settings settings = Settings.Load(root);
                HiddenApps hidden = HiddenApps.Load(root);
                AppItem python = App("Python 3.13 (64-bit)"), idle = App("IDLE (Python 3.13 64-bit)"), nav = App("Anaconda Navigator");
                List<AppItem> apps = new List<AppItem> { python, idle, nav, App("Spyder") };
                Check(AppHideSuggestions.Build(apps, settings, hidden).Count == 3 && hidden.Count == 0 && !python.Hidden,
                    "扫描只产生三项建议，不改显示或隐藏存储");
                Check(!File.Exists(HiddenApps.PathOf(root)), "预览不创建隐藏文件");
                python.Usage = 1;
                Check(!AppHideSuggestions.Explain(python).DefaultHide, "有启动记录默认暂不处理");
                settings.ActiveAppRoles.Add(python.Path.ToUpperInvariant().Replace('\\', '/'));
                settings.ActiveAppRoles.Add(idle.Path);
                hidden.ShowWithException(nav.Path);
                Check(AppHideSuggestions.Build(apps, settings, hidden).Count == 0, "主动角色、保留例外优先且路径规范化");
                settings.ActiveAppRoles.Clear(); settings.PinnedCommon.Clear(); hidden.RemoveException(nav.Path);
                AppItem custom = App("自定义服务界面"); apps.Add(custom);
                settings.AuxiliaryAppRoles.Add(custom.Path);
                Check(AppHideSuggestions.Build(apps, settings, hidden).Count == 4,
                    "手动辅助配套提供可选建议，不按未知名称自动隐藏");
                settings.ActiveAppRoles.Add(custom.Path);
                Check(AppHideSuggestions.Build(apps, settings, hidden).Count == 3, "角色冲突主动优先");
                Check(AppHideSuggestions.Apply(apps, settings, hidden, null) == 0, "取消或无选择不执行");
                settings.ActiveAppRoles.Add(python.Path);
                Check(AppHideSuggestions.Apply(apps, settings, hidden, new[] { new AppHideChoice { Path = python.Path, Action = AppHideAction.Hide } }) == 0,
                    "预览后变为主动使用的入口不被过期确认隐藏");
                settings.ActiveAppRoles.Remove(python.Path);
                int n = AppHideSuggestions.Apply(apps, settings, hidden, new[] {
                    new AppHideChoice { Path = python.Path, Action = AppHideAction.Hide },
                    new AppHideChoice { Path = idle.Path, Action = AppHideAction.Keep },
                    new AppHideChoice { Path = nav.Path, Action = AppHideAction.Skip },
                    new AppHideChoice { Path = apps[3].Path, Action = AppHideAction.Hide },
                    new AppHideChoice { Path = "C:\\missing.exe", Action = AppHideAction.Hide } });
                Check(n == 1 && python.Hidden && !idle.Hidden && !nav.Hidden && !apps[3].Hidden, "仅隐藏已确认且仍匹配的具体入口");
                Check(hidden.IsException(idle.Path) && !hidden.IsException(nav.Path), "保留持久化，暂不处理不记例外");
                Check(!python.LowValue && python.Category == "开发" && settings.AuxiliaryAppRoles.Count == 1, "隐藏不更改低价值或应用分类");
                HiddenApps reread = HiddenApps.Load(root);
                HideReason reason = reread.ReasonOf(python.Path);
                Check(reason.Type == HideReason.Kind.Suggested && reason.GroupKey.StartsWith("suggested:")
                    && reason.DisplayText == "确认建议隐藏：运行环境入口" && reason.RetainOnRestore, "建议原因单独分组且往返保存");
                Check(HideReason.FromDict(reason.ToDict()).Type == HideReason.Kind.Suggested, "新原因序列化可往返");
                Check(HideReason.Manual().DisplayText == "手动隐藏" && HideReason.Batch("系统工具").DisplayText == "批量隐藏：系统工具",
                    "旧原因兼容不变");
                reread.ShowWithException(python.Path); python.Hidden = false;
                Check(AppHideSuggestions.Build(apps, settings, reread).Count == 1, "恢复建议隐藏后不再次建议同一入口");
                Check(AppHideSuggestions.Apply(apps, settings, reread, new[] {
                    new AppHideChoice { Path = nav.Path, Action = AppHideAction.Hide },
                    new AppHideChoice { Path = nav.Path.ToUpperInvariant(), Action = AppHideAction.Keep } }) == 0
                    && reread.IsException(nav.Path), "重复输入冲突保留优先");
                AppItem newApp = App("新 Python 工具"); apps.Add(newApp);
                Check(!newApp.Hidden && AppHideSuggestions.Build(apps, settings, reread).Count == 0, "新应用不自动隐藏或降级");

                HiddenApps autoHidden = HiddenApps.Load(Path.Combine(root, "auto"));
                List<AppItem> autoApps = new List<AppItem> { App("IDLE (Python 3.11 64-bit)"), App("IDLE (Python 3.13 64-bit)"),
                    App("Anaconda Navigator"), App("Spyder"), App("Jupyter Notebook"), App("Visual Studio Code"), App("新应用") };
                Settings autoSettings = Settings.Load(Path.Combine(root, "auto"));
                Check(AppHideSuggestions.ApplyAutomatic(autoApps, autoSettings, autoHidden) == 3,
                    "截图两个 IDLE 和 Navigator 自动隐藏，无需逐项确认");
                Check(autoApps[0].Hidden && autoApps[1].Hidden && autoApps[2].Hidden && !autoApps[3].Hidden
                    && !autoApps[4].Hidden && !autoApps[5].Hidden && !autoApps[6].Hidden, "主工具与未知新应用继续显示");
                Check(AppHideSuggestions.ApplyAutomatic(autoApps, autoSettings, autoHidden) == 0, "自动隐藏重复运行幂等");
                HideReason autoReason = HiddenApps.Load(Path.Combine(root, "auto")).ReasonOf(autoApps[0].Path);
                Check(autoReason.Type == HideReason.Kind.Automatic && autoReason.DisplayText == "自动隐藏：可选配套界面"
                    && autoReason.RetainOnRestore && HideReason.FromDict(autoReason.ToDict()).Type == HideReason.Kind.Automatic,
                    "自动原因独立保存、分组与往返，恢复记住保留");
                autoHidden.ShowWithException(autoApps[0].Path); autoApps[0].Hidden = false;
                Check(AppHideSuggestions.ApplyAutomatic(autoApps, autoSettings, autoHidden) == 0 && !autoApps[0].Hidden,
                    "恢复后扫描不再次隐藏");
                List<AppItem> protectedApps = new List<AppItem> { App("Python 3.13 (64-bit)"), App("Node.js"), App("Application Verifier (X64)") };
                autoSettings.ActiveAppRoles.Add(protectedApps[0].Path);
                autoSettings.ActiveAppRoles.Add(protectedApps[1].Path);
                protectedApps[2].Usage = 1;
                Check(AppHideSuggestions.ApplyAutomatic(protectedApps, autoSettings, autoHidden) == 0,
                    "主动使用与启动记录优先于自动规则");
                foreach (string name in new[] { "Python 3.13 Module Docs (64-bit)", "x64 Native Tools", "x64_x86 Cross Tools",
                    "SDK Configuration", "Debuggable Package Manager", "Reset Spyder Settings" })
                    Check(AppHideSuggestions.Explain(App(name)).DefaultHide, "识别明确文档或开发配套：" + name);
            }
            catch (Exception ex) { Check(false, "异常：" + ex); }
            finally { try { Directory.Delete(root, true); } catch { } }
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            return failed == 0 ? 0 : 1;
        }
    }
}
