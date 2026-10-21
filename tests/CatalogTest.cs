using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// Catalog 与 JsonIO 的最小测试。用 csc 直接编译成 console exe：
    ///   csc /target:exe /r:System.dll src\Catalog.cs src\JsonIO.cs src\UsageStore.cs tests\CatalogTest.cs
    /// Catalog/UsageStore 依赖 System.Web.Script.Serialization，需额外 /r:System.Web.Extensions.dll。
    /// </summary>
    static class CatalogTest
    {
        static int failed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name + (cond || detail.Length == 0 ? "" : "  [" + detail + "]"));
            if (!cond) failed++;
        }

        static void Eq(string actual, string expected, string name)
        {
            Check(actual == expected, name, "实际=" + actual + " 期望=" + expected);
        }

        static void Main()
        {
            // ---------- GuessCategory：分类规则 ----------
            Eq(Catalog.GuessCategory("Steam"), "影音娱乐", "Steam -> 影音娱乐");
            Eq(Catalog.GuessCategory("Minecraft Launcher"), "影音娱乐", "Minecraft -> 影音娱乐");
            Eq(Catalog.GuessCategory("原神"), "影音娱乐", "原神 -> 影音娱乐");

            Eq(Catalog.GuessCategory("Google Chrome"), "网络通讯", "Chrome -> 网络通讯");
            Eq(Catalog.GuessCategory("微信"), "网络通讯", "微信 -> 网络通讯");
            Eq(Catalog.GuessCategory("Telegram Desktop"), "网络通讯", "Telegram -> 网络通讯");
            Eq(Catalog.GuessCategory("Clash for Windows"), "网络通讯", "Clash -> 网络通讯");

            Eq(Catalog.GuessCategory("Microsoft Word"), "办公", "Word -> 办公");
            Eq(Catalog.GuessCategory("WPS Office"), "办公", "WPS -> 办公");
            Eq(Catalog.GuessCategory("Adobe Acrobat Reader"), "办公", "Acrobat -> 办公");
            Eq(Catalog.GuessCategory("飞书"), "办公", "飞书 -> 办公");

            Eq(Catalog.GuessCategory("Visual Studio Code"), "开发", "VS Code -> 开发");
            Eq(Catalog.GuessCategory("PyCharm"), "开发", "PyCharm -> 开发");
            Eq(Catalog.GuessCategory("Docker Desktop"), "开发", "Docker -> 开发");
            // 注意：Git 本身算「开发」（它是版本控制工具，不是命令行窗口）；
            // 而 Git Bash / Git CMD 是命令行窗口，算「工具/命令行」。
            Eq(Catalog.GuessCategory("Git"), "开发", "Git -> 开发（工具规则不误伤）");
            Eq(Catalog.GuessCategory("Git Bash"), Catalog.ToolCategory, "Git Bash -> 工具/命令行");
            Eq(Catalog.GuessCategory("Git CMD"), Catalog.ToolCategory, "Git CMD -> 工具/命令行");

            Eq(Catalog.GuessCategory("VLC media player"), "影音娱乐", "VLC -> 影音娱乐");
            Eq(Catalog.GuessCategory("剪映专业版"), "影音娱乐", "剪映 -> 影音娱乐");
            Eq(Catalog.GuessCategory("哔哩哔哩"), "影音娱乐", "哔哩 -> 影音娱乐");

            Eq(Catalog.GuessCategory("7-Zip File Manager"), "系统工具", "7-Zip -> 系统工具");
            Eq(Catalog.GuessCategory("WinRAR"), "系统工具", "WinRAR -> 系统工具");
            Eq(Catalog.GuessCategory("ShareX"), "系统工具", "ShareX -> 系统工具");

            Eq(Catalog.GuessCategory("大肥鱼桌宠"), "其他", "未知应用 -> 其他");
            Eq(Catalog.GuessCategory("Some Random App"), "其他", "随机英文 -> 其他");

            // 大小写不敏感
            Eq(Catalog.GuessCategory("CHROME"), "网络通讯", "大写 CHROME -> 网络通讯");
            Eq(Catalog.GuessCategory("Steam"), "影音娱乐", "首字母大写 Steam -> 影音娱乐");

            // 优先级：命中第一条规则即返回（Steam 同时含 game 语义，但先判影音娱乐）
            Eq(Catalog.GuessCategory("Steam Game"), "影音娱乐", "Steam Game 命中影音娱乐");
            // "Visual Studio" 命中开发，即使后面还有别的关键词
            Eq(Catalog.GuessCategory("Visual Studio Code"), "开发", "Visual Studio Code 优先开发");

            // 空值/边界
            Eq(Catalog.GuessCategory(""), "其他", "空字符串 -> 其他");

            // ---------- DefaultOrder ----------
            Check(Catalog.DefaultOrder.Length == 7, "DefaultOrder 有 7 个分类", "实际 " + Catalog.DefaultOrder.Length);
            Check(Array.IndexOf(Catalog.DefaultOrder, "办公") >= 0, "DefaultOrder 含「办公」");
            Check(Array.IndexOf(Catalog.DefaultOrder, "其他") >= 0, "DefaultOrder 含「其他」");
            Check(Array.IndexOf(Catalog.DefaultOrder, Catalog.ToolCategory) >= 0,
                "DefaultOrder 含「" + Catalog.ToolCategory + "」");

            // ---------- 工具/命令行 分类 ----------
            Eq(Catalog.GuessCategory("Command Prompt"), Catalog.ToolCategory, "命令行 -> 工具/命令行");
            Eq(Catalog.GuessCategory("Windows PowerShell"), Catalog.ToolCategory, "PowerShell -> 工具/命令行");
            Eq(Catalog.GuessCategory("ChromeDriver"), Catalog.ToolCategory, "驱动 -> 工具/命令行");
            Eq(Catalog.GuessCategory("Python 3.11 Module Docs (64-bit)"), Catalog.ToolCategory, "Module Docs -> 工具/命令行");
            Eq(Catalog.GuessCategory("Reset Spyder Settings"), Catalog.ToolCategory, "Reset 工具 -> 工具/命令行");
            Eq(Catalog.GuessCategory("Pandoc User’s Guide"), Catalog.ToolCategory, "帮助文档 -> 工具/命令行");
            Eq(Catalog.GuessCategory("Git Bash"), Catalog.ToolCategory, "Git Bash -> 工具/命令行（不是开发）");
            Eq(Catalog.GuessCategory("Node.js command prompt"), Catalog.ToolCategory, "命令提示符 -> 工具/命令行");

            // ---------- Windows 系统工具 ----------
            Eq(Catalog.GuessCategory("File Explorer"), "系统工具", "文件资源管理器 -> 系统工具");
            Eq(Catalog.GuessCategory("Task Manager"), "系统工具", "任务管理器 -> 系统工具");
            Eq(Catalog.GuessCategory("Registry Editor"), "系统工具", "注册表编辑器 -> 系统工具");
            Eq(Catalog.GuessCategory("Event Viewer"), "系统工具", "事件查看器 -> 系统工具");
            Eq(Catalog.GuessCategory("Narrator"), "系统工具", "讲述人 -> 系统工具");

            // ---------- HP 不再是办公（A 项修复）----------
            Eq(Catalog.GuessCategory("HP Scan"), "系统工具", "HP 扫描 -> 系统工具（不再误判办公）");
            Eq(Catalog.GuessCategory("HP 打印机状态和警报"), "系统工具", "HP 打印机 -> 系统工具");
            Eq(Catalog.GuessCategory("Canon IJ Scan Utility"), "系统工具", "佳能扫描 -> 系统工具");

            // ---------- 补漏的 15 个应用（B 项修复）----------
            Eq(Catalog.GuessCategory("DBeaver"), "开发", "DBeaver -> 开发");
            Eq(Catalog.GuessCategory("Ollama"), "开发", "Ollama -> 开发");
            Eq(Catalog.GuessCategory("WSL"), "开发", "WSL -> 开发");
            Eq(Catalog.GuessCategory("Karing"), "网络通讯", "Karing -> 网络通讯");
            Eq(Catalog.GuessCategory("LocalSend"), "网络通讯", "LocalSend -> 网络通讯");
            Eq(Catalog.GuessCategory("向日葵远程控制"), "网络通讯", "向日葵 -> 网络通讯");
            Eq(Catalog.GuessCategory("Mineradio"), "影音娱乐", "Mineradio -> 影音娱乐");
            Eq(Catalog.GuessCategory("FastStone Image Viewer"), "影音娱乐", "FastStone -> 影音娱乐");
            Eq(Catalog.GuessCategory("图吧工具箱"), "系统工具", "图吧工具箱 -> 系统工具");
            Eq(Catalog.GuessCategory("Sticky Notes (new)"), "系统工具", "便笺 -> 系统工具");
            Eq(Catalog.GuessCategory("MongoDB Shell CLI REPL Package"), Catalog.ToolCategory,
                "MongoDB Shell -> 工具/命令行（mongo 规则不再漏）");

            // ---------- 工具规则不该误伤正常应用 ----------
            Eq(Catalog.GuessCategory("Visual Studio Code"), "开发", "VS Code 不会被工具规则抢走");
            Eq(Catalog.GuessCategory("GitHub Desktop"), "开发", "GitHub Desktop 不会被工具规则抢走");
            Eq(Catalog.GuessCategory("Windows Terminal"), Catalog.ToolCategory, "Windows Terminal -> 工具/命令行");

            // ---------- HideCategories ----------
            Catalog c = new Catalog();
            c.Order = new List<string>(new string[] { "办公", "开发", "系统工具", "其他" });
            c.Apps = new List<AppItem>();
            c.Apps.Add(new AppItem { Name = "Word", Path = "a", Category = "办公" });
            c.Apps.Add(new AppItem { Name = "Code", Path = "b", Category = "开发" });
            c.Apps.Add(new AppItem { Name = "7zip", Path = "c", Category = "系统工具" });
            c.HideCategories(new List<string>(new string[] { "系统工具" }));
            Check(c.Apps.Count == 2, "隐藏分类后应用被过滤", "剩余 " + c.Apps.Count);
            Check(!c.Order.Contains("系统工具"), "隐藏分类后 Order 中移除该分类");
            bool anyHidden = false;
            foreach (AppItem a in c.Apps) if (a.Category == "系统工具") anyHidden = true;
            Check(!anyHidden, "隐藏分类的应用不再出现");

            // 隐藏 null / 空列表不应崩溃
            try
            {
                c.HideCategories(null);
                c.HideCategories(new List<string>());
                Check(true, "HideCategories(null/空) 不崩溃");
            }
            catch (Exception ex)
            {
                Check(false, "HideCategories(null/空) 不崩溃", ex.Message);
            }

            // ---------- ContainsName / ContainsPath ----------
            Catalog d = new Catalog();
            d.Apps = new List<AppItem>();
            d.Apps.Add(new AppItem { Name = "Chrome", Path = @"C:\a\chrome.exe", Category = "网络通讯" });
            Check(d.ContainsName("chrome"), "ContainsName 大小写不敏感");
            Check(!d.ContainsName("Firefox"), "ContainsName 不存在的返回 false");
            Check(d.ContainsPath(@"C:\A\CHROME.EXE"), "ContainsPath 大小写不敏感");
            Check(!d.ContainsName(null), "ContainsName(null) 返回 false");
            Check(!d.ContainsPath(""), "ContainsPath(空) 返回 false");

            // ---------- JsonIO 往返 ----------
            string root = Path.Combine(Path.GetTempPath(), "clover-catalog-test-" + Guid.NewGuid().ToString("N"));
            string cfg = Path.Combine(root, "config");
            Directory.CreateDirectory(cfg);
            try
            {
                string p = Path.Combine(cfg, "t.json");
                string json = "{\"apps\":[{\"name\":\"测试\",\"path\":\"C:\\\\x.exe\",\"category\":\"其他\"}]}";
                JsonIO.WriteAtomic(p, json);
                string back = JsonIO.Read(p);
                Check(back == json, "JsonIO 写入后可原样读回", back ?? "null");

                // 文件被写坏时回退 .bak
                JsonIO.WriteAtomic(p, json); // 第二次，生成 .bak
                File.WriteAllText(p, "");
                string fallback = JsonIO.Read(p);
                Check(fallback != null && fallback.Contains("测试"), ".bak 回退有效", fallback ?? "null");

                // 中文与特殊字符往返
                string p2 = Path.Combine(cfg, "u.json");
                string j2 = "{\"name\":\"微信\",\"desc\":\"中文<>&\\\"引号\"}";
                JsonIO.WriteAtomic(p2, j2);
                Check(JsonIO.Read(p2) == j2, "中文/特殊字符往返一致");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }

            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
