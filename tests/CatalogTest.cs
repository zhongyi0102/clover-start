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
            Eq(Catalog.GuessCategory("Git Bash"), "开发", "Git -> 开发");

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
            Check(Catalog.DefaultOrder.Length == 6, "DefaultOrder 有 6 个分类", "实际 " + Catalog.DefaultOrder.Length);
            Check(Array.IndexOf(Catalog.DefaultOrder, "办公") >= 0, "DefaultOrder 含「办公」");
            Check(Array.IndexOf(Catalog.DefaultOrder, "其他") >= 0, "DefaultOrder 含「其他」");

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
