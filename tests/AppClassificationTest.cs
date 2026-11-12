using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Clover
{
    static class AppClassificationTest
    {
        static int passed, failed;
        static void Check(bool ok, string name) { if (ok) passed++; else failed++; Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name); }
        static AppItem App(string root, string name, string file)
        { string path = Path.Combine(root, file); File.WriteAllText(path, "fixture"); return new AppItem { Name = name, Path = path }; }
        static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "CloverClasses-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                foreach (string name in new[] { "7-Zip File Manager", "Geek Uninstaller", "ego (lite)", "Douzy", "DSH Tauri", "Spyder", "Jupyter Notebook", "OBS Studio", "大肥鱼桌宠", "隔空传送", "新应用" })
                    Check(AppClassification.Guess(name, @"C:\apps\program.exe") == AppRole.User, "主应用不按名字误伤：" + name);
                foreach (string name in new[] { "Python 3.13 (64-bit)", "Node.js", "Java SE Development Kit 8 Update 241 (64-bit)", "Apple Mobile Device Support", "MySQL Router 8.0", "AO-SemanticRouter", "SnoreToast", "Application Verifier (WOW)", "Windows App Cert Kit", "Microsoft Visual C++ v14 Redistributable (x64)", "x64 Native Tools Command Prompt for VS 2022" })
                    Check(AppClassification.Guess(name, @"C:\apps\program.exe") == AppRole.Computer, "存量明确后台归电脑类：" + name);
                foreach (string name in new[] { "IDLE (Python 3.13 64-bit)", "Anaconda Navigator", "Deno", "Pandoc 3.10", "yt-dlp", "FFmpeg", "GitHub CLI", "RipGrep MSVC", "Rustup: the Rust toolchain installer", "WSL", "Ubuntu", "终端", "Xagent", "配置工具", "Office 语言首选项", "AMD Software", "HP Scan", "kuaixcrlespc-64", "Task Manager" })
                    Check(AppClassification.Guess(name, @"C:\apps\program.exe") == AppRole.LowValue, "存量工具入口归低价值：" + name);

                AppItem user = App(root, "Spyder", "spyder.exe"), computer = App(root, "Python 3.13 (64-bit)", "python.exe"),
                    low = App(root, "Deno", "deno.exe"), hiddenUser = App(root, "Snipaste", "snipaste.exe"), manualComputer = App(root, "Node.js", "node.exe");
                List<AppItem> apps = new List<AppItem> { user, computer, low, hiddenUser, manualComputer };
                Settings settings = Settings.Load(root); HiddenApps hidden = HiddenApps.Load(root);
                hidden.Hide(computer.Path, HideReason.Automatic("运行环境入口"));
                hidden.Hide(hiddenUser.Path); hidden.Hide(manualComputer.Path);
                AppClassification classes = new AppClassification(root);
                classes.SeedExisting("GitHub CLI", Path.Combine(root, "gh.exe"));
                classes.Apply(apps, settings, hidden);
                Check(classes.RoleOf(Path.Combine(root, "gh.exe")) == AppRole.LowValue, "系统搜索存量入口参与首次重新分类");
                Check(user.Role == AppRole.User && low.Role == AppRole.LowValue && computer.Role == AppRole.Computer, "首次重分全部存量");
                Check(!computer.Hidden && !hidden.IsHidden(computer.Path), "自动隐藏电脑入口迁移为可搜索分类");
                Check(hiddenUser.Hidden && manualComputer.Hidden, "手动隐藏不迁移为搜索可见");
                Check(!PanelFreeVisible(computer) && !PanelFreeVisible(manualComputer), "电脑类不平铺、隐藏仍完全隐藏");
                string before = File.ReadAllText(Path.Combine(root, "config", "app-classification.json"));
                classes.Apply(apps, settings, hidden);
                Check(before == File.ReadAllText(Path.Combine(root, "config", "app-classification.json")), "重复应用不重写分类");
                AppItem newlyInstalled = App(root, "Python 3.14 (64-bit)", "new-python.exe"); apps.Add(newlyInstalled);
                classes.Apply(apps, settings, hidden);
                Check(newlyInstalled.Role == AppRole.User && !newlyInstalled.LowValue && !newlyInstalled.Hidden, "新安装即使匹配运行库规则也默认用户类");
                new AppClassification(root).Apply(apps, Settings.Load(root), HiddenApps.Load(root));
                Check(newlyInstalled.Role == AppRole.User && computer.Role == AppRole.Computer, "重启扫描保留新应用与旧分类");
                settings.ActiveAppRoles.Add(computer.Path.ToUpperInvariant().Replace('\\', '/'));
                classes.Apply(apps, settings, hidden);
                Check(computer.Role == AppRole.User, "手动用户类优先且路径规范化");
                settings.LowValueMarked.Add(user.Path); classes.Apply(apps, settings, hidden);
                Check(user.Role == AppRole.LowValue && user.LowValue, "手动低价值生效");
                settings.LowValueUnmarked.Add(user.Path); classes.Apply(apps, settings, hidden);
                Check(user.Role == AppRole.User && !user.LowValue, "取消低价值优先");
                classes.Set(low, AppRole.Computer); new AppClassification(root).Apply(apps, Settings.Load(root), hidden);
                Check(low.Role == AppRole.Computer && !low.Hidden, "显式电脑分类落盘且不是 Hidden");
                Check(AppClassification.IsNonAppEntry("Tesseract-OCR", @"C:\tools\tesseract-uninstall.exe"), "错指卸载入口被排除");
                Check(AppClassification.IsNonAppEntry("Python Manuals", @"C:\tools\manual.html"), "文档不是应用");
                Check(!AppClassification.IsNonAppEntry("Geek Uninstaller", @"C:\tools\geek.exe"), "卸载管理主应用保留");
                Check(!AppClassification.IsNonAppEntry("Visual Studio Installer", @"C:\tools\setup.exe"), "已安装维护界面可归低价值");
                Check(AppClassification.IsNonAppEntry("Anaconda3", @"D:\Anaconda\Uninstall-Anaconda3.exe"), "搜索补充的卸载目标排除");
                Check(AppClassification.IsNonAppEntry("运行库安装包", @"C:\ProgramData\Package Cache\id\setup.exe"), "缓存安装包不是已安装应用入口");
                Check(AppClassification.Guess("WindowsPackageManagerServer", @"C:\apps\WindowsPackageManagerServer.exe") == AppRole.Computer,
                    "系统搜索补充的包管理后台归电脑类");
                Check(AppClassification.Guess("Ollama version 0.30.5", @"C:\apps\ollama.exe") == AppRole.LowValue,
                    "同软件命令行入口与主界面分别分类");
                string manualFile = Path.Combine(root, "config", "manual-apps.json");
                File.WriteAllText(manualFile, "{\"future\":7,\"apps\":[]}");
                Check(Catalog.RegisterManual(root, user) && Catalog.LoadManual(root).Count == 1, "搜索登记入口持久保存");
                Check(Catalog.RegisterManual(root, user) && Catalog.LoadManual(root).Count == 1, "重复登记不重复增加");
                Check(File.ReadAllText(manualFile).Contains("\"future\":7"), "登记入口保留未知字段");
                File.WriteAllText(manualFile, "broken");
                Check(!Catalog.RegisterManual(root, low) && File.ReadAllText(manualFile) == "broken", "损坏手工清单不覆盖");
                SearchResult userResult = new SearchResult { Name = "Python 管理界面", Path = "u", RoleRank = 0 };
                SearchResult lowResult = new SearchResult { Name = "Python Shell", Path = "l", RoleRank = 1, Pinned = true, Usage = 500 };
                SearchResult computerResult = new SearchResult { Name = "Python", Path = "c", RoleRank = 2, Pinned = true, Usage = 999 };
                List<SearchResult> results = SearchRanking.Merge(new List<SearchResult> { computerResult, lowResult, userResult }, null, "Python", 2);
                Check(results.Count == 2 && results[0] == userResult && results[1] == lowResult, "分类优先于精确匹配/固定/使用次数/截断");
                Check(SearchRanking.Merge(new List<SearchResult> { new SearchResult { Name = "输入法", Path = "settings", RoleRank = 1 } },
                    new List<SearchResult> { new SearchResult { Name = "输入法", Path = "server", RoleRank = 2 } }, "输入法", 10).Count == 2,
                    "同名设置界面与服务不跨类别去重");
                string serialized = new JavaScriptSerializer().Serialize(newlyInstalled);
                Check(!serialized.Contains("Role") && !serialized.Contains("ExcludedEntry"), "运行字段不污染扫描清单");
                File.WriteAllText(Path.Combine(root, "config", "app-classification.json"), "broken");
                new AppClassification(root).Apply(apps, Settings.Load(root), hidden);
                Check(File.ReadAllText(Path.Combine(root, "config", "app-classification.json")) == "broken", "分类坏文件不覆盖");
            }
            catch (Exception ex) { Check(false, "异常：" + ex); }
            finally { try { Directory.Delete(root, true); } catch { } }
            Console.WriteLine("通过 " + passed + " / 失败 " + failed); return failed == 0 ? 0 : 1;
        }
        static bool PanelFreeVisible(AppItem app) { return !app.Hidden && !app.ExcludedEntry && app.Role != AppRole.Computer; }
    }
}
