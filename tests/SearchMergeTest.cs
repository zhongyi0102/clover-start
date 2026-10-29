using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 搜索合并测试：主搜索（MatchCatalog）+ 补充搜索（Instant）的
    /// 去重、排序、上限、拼音保留。
    ///
    /// **为什么不用真实 Catalog**：不同机器装的软件不同，断言具体应用会失败。
    /// 这里直接构造 SearchResult 列表，验证 AppendBatch 的合并逻辑。
    ///
    /// **但 SearchResultList 依赖 WPF（StackPanel）**，所以本测试用
    /// 「复刻合并逻辑」的方式 —— 与 SearchResultList.AppendBatch 保持同步。
    /// 一旦实现改动，这里也要跟着改（源码级断言会提醒）。
    /// </summary>
    static class SearchMergeTest
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
            Console.WriteLine("=== 搜索合并测试（主 + 补充）===");
            Console.WriteLine();

            TestDedupSamePath();
            TestDedupSameNameDiffPath();
            TestDedupEmptyName();
            TestOrderPreserved();
            TestCap();
            TestSourceHeader();
            TestRealQueries();
            TestSourceLevelSync();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 同路径去重 ----------
        static void TestDedupSamePath()
        {
            Console.WriteLine("--- 1. 同路径去重 ---");

            List<SearchResult> main = new List<SearchResult> {
                Make("Chrome", @"C:\a\chrome.exe", "已安装"),
            };
            List<SearchResult> extra = new List<SearchResult> {
                Make("Chrome", @"C:\a\chrome.exe", "开始菜单"),   // 同路径
            };

            List<SearchResult> merged = Merge(main, extra, 60);
            Check(merged.Count == 1, "★同路径 → 只保留一条", Convert.ToString(merged.Count));
            Check(merged[0].Source == "已安装", "保留主搜索结果（来源=已安装）", merged[0].Source);
            Console.WriteLine();
        }

        // ---------- 2. 同名不同路径去重（核心改进）----------
        static void TestDedupSameNameDiffPath()
        {
            Console.WriteLine("--- 2. 同名不同路径去重（本次核心改进）---");

            // 微信：.lnk vs .exe
            List<SearchResult> main = new List<SearchResult> {
                Make("微信", @"C:\StartMenu\微信.lnk", "已安装"),
            };
            List<SearchResult> extra = new List<SearchResult> {
                Make("微信", @"D:\Tencent\Weixin\Weixin.exe", "注册表"),
            };
            List<SearchResult> merged = Merge(main, extra, 60);
            Check(merged.Count == 1, "★微信：.lnk 与 .exe 只保留一条", Convert.ToString(merged.Count));
            Check(merged.Count > 0 && merged[0].Path.IndexOf(".lnk") >= 0, "保留主结果的 .lnk 路径");

            // Steam：.lnk vs uninstall.exe
            main = new List<SearchResult> { Make("Steam", @"C:\StartMenu\Steam.lnk", "已安装") };
            extra = new List<SearchResult> { Make("Steam", @"D:\steam\uninstall.exe", "注册表") };
            merged = Merge(main, extra, 60);
            Check(merged.Count == 1, "★Steam：.lnk 与 uninstall.exe 只保留一条", Convert.ToString(merged.Count));

            // QQ：.lnk vs .exe
            main = new List<SearchResult> { Make("QQ", @"C:\StartMenu\QQ.lnk", "已安装") };
            extra = new List<SearchResult> { Make("QQ", @"D:\QQNT\QQ.exe", "注册表") };
            merged = Merge(main, extra, 60);
            Check(merged.Count == 1, "★QQ：.lnk 与 .exe 只保留一条", Convert.ToString(merged.Count));

            // 大小写差异
            main = new List<SearchResult> { Make("Chrome", @"C:\a\chrome.exe", "已安装") };
            extra = new List<SearchResult> { Make("chrome", @"D:\b\chrome.exe", "注册表") };
            merged = Merge(main, extra, 60);
            Check(merged.Count == 1, "★大小写不同但同名 → 去重", Convert.ToString(merged.Count));

            // 全角/半角：保守键**不合并**（可接受，不误伤更重要）
            main = new List<SearchResult> { Make("ＱＱ", @"C:\a\qq.lnk", "已安装") };
            extra = new List<SearchResult> { Make("QQ", @"D:\b\qq.exe", "注册表") };
            merged = Merge(main, extra, 60);
            Check(merged.Count == 2, "全角ＱＱ vs 半角QQ → 不合并（保守键的已知取舍）",
                Convert.ToString(merged.Count));

            // 不同应用不应被误合并
            main = new List<SearchResult> { Make("Notepad", @"C:\a\notepad.exe", "已安装") };
            extra = new List<SearchResult> { Make("Notepad++", @"D:\b\npp.exe", "注册表") };
            merged = Merge(main, extra, 60);
            Check(merged.Count == 2, "★不同名（Notepad vs Notepad++）→ 不合并", Convert.ToString(merged.Count));
            Console.WriteLine();
        }

        // ---------- 3. 空名不参与名字去重 ----------
        static void TestDedupEmptyName()
        {
            Console.WriteLine("--- 3. 空名 / 纯符号名不参与名字去重 ---");

            List<SearchResult> main = new List<SearchResult> {
                Make("", @"C:\a\one.exe", "已安装"),
                Make("", @"C:\a\two.exe", "已安装"),
            };
            List<SearchResult> merged = Merge(main, new List<SearchResult>(), 60);
            Check(merged.Count == 2, "★空名的两条不合并（路径不同）", Convert.ToString(merged.Count));

            // 纯符号名（归一化后为空）
            main = new List<SearchResult> {
                Make("...", @"C:\a\x.exe", "已安装"),
                Make("---", @"C:\a\y.exe", "已安装"),
            };
            merged = Merge(main, new List<SearchResult>(), 60);
            Check(merged.Count == 2, "★纯符号名不合并", Convert.ToString(merged.Count));
            Console.WriteLine();
        }

        // ---------- 4. 排序：主结果在前 ----------
        static void TestOrderPreserved()
        {
            Console.WriteLine("--- 4. 补充结果追加在主结果之后 ---");

            List<SearchResult> main = new List<SearchResult> {
                Make("A-Main", @"C:\m1.exe", "已安装"),
                Make("B-Main", @"C:\m2.exe", "已安装"),
            };
            List<SearchResult> extra = new List<SearchResult> {
                Make("C-Extra", @"C:\e1.exe", "注册表"),
                Make("D-Extra", @"C:\e2.exe", "注册表"),
            };

            List<SearchResult> merged = Merge(main, extra, 60);
            Check(merged.Count == 4, "共 4 条", Convert.ToString(merged.Count));
            Check(merged[0].Name == "A-Main" && merged[1].Name == "B-Main",
                "★主结果占据前两位", merged[0].Name + "," + merged[1].Name);
            Check(merged[2].Name == "C-Extra" && merged[3].Name == "D-Extra",
                "★补充结果追加在后", merged[2].Name + "," + merged[3].Name);

            // 主结果内部顺序不变
            Check(merged[0].Name == "A-Main", "主结果内部顺序未被重排", merged[0].Name);
            Console.WriteLine();
        }

        // ---------- 5. 结果上限 ----------
        static void TestCap()
        {
            Console.WriteLine("--- 5. 结果上限 ---");

            List<SearchResult> main = new List<SearchResult>();
            for (int i = 0; i < 50; i++) main.Add(Make("Main" + i, @"C:\m" + i + ".exe", "已安装"));

            List<SearchResult> extra = new List<SearchResult>();
            for (int i = 0; i < 50; i++) extra.Add(Make("Extra" + i, @"C:\e" + i + ".exe", "注册表"));

            List<SearchResult> merged = Merge(main, extra, 60);
            Check(merged.Count == 60, "★总条数 = 60（MaxDisplay）", Convert.ToString(merged.Count));

            // 主结果优先：前 50 条应全是 Main
            int mainCount = 0;
            foreach (SearchResult r in merged) if (r.Name.StartsWith("Main")) mainCount++;
            Check(mainCount == 50, "★主结果 50 条全部保留", Convert.ToString(mainCount));

            // 补充只取前 10
            int extraCount = 0;
            foreach (SearchResult r in merged) if (r.Name.StartsWith("Extra")) extraCount++;
            Check(extraCount == 10, "★补充结果被截到 10 条", Convert.ToString(extraCount));

            // 上限小于主结果数
            merged = Merge(main, extra, 30);
            Check(merged.Count == 30, "上限 30 时只取 30 条", Convert.ToString(merged.Count));
            Console.WriteLine();
        }

        // ---------- 6. 分组标题 ----------
        static void TestSourceHeader()
        {
            Console.WriteLine("--- 6. 来源分组标题 ---");

            // 同一来源只插一次标题
            HashSet<string> shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> headers = new List<string>();
            List<SearchResult> batch = new List<SearchResult> {
                Make("A", @"C:\a.exe", "已安装"),
                Make("B", @"C:\b.exe", "已安装"),
                Make("C", @"C:\c.exe", "已安装"),
            };
            foreach (SearchResult r in batch)
            {
                if (!shown.Add(r.Path)) continue;
                string nn = NameMatch.Normalize(r.Name ?? "");
                if (nn.Length > 0 && !shown.Add("N:" + nn)) continue;
                string gk = "G:" + r.Source;
                if (shown.Add(gk)) headers.Add(r.Source);
            }
            Check(headers.Count == 1, "★同来源只插一次标题", Convert.ToString(headers.Count));
            Check(headers.Count > 0 && headers[0] == "已安装", "标题内容正确");

            // 不同来源各插一次
            shown.Clear();
            headers.Clear();
            batch = new List<SearchResult> {
                Make("A", @"C:\a.exe", "已安装"),
                Make("B", @"C:\b.exe", "开始菜单"),
                Make("C", @"C:\c.exe", "注册表"),
            };
            foreach (SearchResult r in batch)
            {
                if (!shown.Add(r.Path)) continue;
                string nn = NameMatch.Normalize(r.Name ?? "");
                if (nn.Length > 0 && !shown.Add("N:" + nn)) continue;
                string gk = "G:" + r.Source;
                if (shown.Add(gk)) headers.Add(r.Source);
            }
            Check(headers.Count == 3, "★三个来源各插一次标题", Convert.ToString(headers.Count));
            Console.WriteLine();
        }

        // ---------- 7. 真实查询回归（用真实 Catalog）----------
        static void TestRealQueries()
        {
            Console.WriteLine("--- 7. 真实查询回归 ---");

            string root = FindRepoRoot();
            Catalog cat = null;
            if (root != null)
            {
                try { cat = Catalog.Load(root); } catch { }
            }

            if (cat == null || cat.Apps.Count == 0)
            {
                Console.WriteLine("      跳过：无法加载 Catalog（" + (root ?? "找不到仓库根") + "）");
                Console.WriteLine();
                return;
            }

            Console.WriteLine("      Catalog.Apps = " + cat.Apps.Count);

            // 7a. 拼音查询必须有结果（MatchCatalog 独有能力）
            foreach (string q in new string[] { "wx", "weixin" })
            {
                int mc = CountCatalogMatches(cat, q);
                Check(mc > 0, "★拼音查询「" + q + "」有命中（MatchCatalog 独有）",
                    Convert.ToString(mc));
            }

            // 7b. 注册表独有查询必须能搜到（Instant 补充能力）
            foreach (string q in new string[] { "notepad", "7z" })
            {
                List<SearchResult> inst = SystemSearch.Instant(q);
                bool has = inst != null && inst.Count > 0;
                // 可能 Catalog 也有，那就无所谓；只要合并后非空
                int mc = CountCatalogMatches(cat, q);
                Check(mc > 0 || has, "★查询「" + q + "」合并后有结果",
                    "主 " + mc + " 补 " + (inst == null ? 0 : inst.Count));
            }

            // 7c. 13 个回归查询：合并后不出现同名重复
            string[] queries = { "微信","wx","weixin","notepad","7z","cmd","code",
                                 "python","office","steam","qq","a","e" };
            foreach (string q in queries)
            {
                List<SearchResult> main = MatchCatalogSim(cat, q);
                List<SearchResult> extra = SystemSearch.Instant(q);
                List<SearchResult> merged = Merge(main, extra, 60);

                // 同名重复检查
                Dictionary<string, int> cnt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (SearchResult r in merged)
                {
                    string k = NameMatch.Normalize(r.Name ?? "");
                    if (k.Length == 0) continue;
                    if (cnt.ContainsKey(k)) cnt[k]++; else cnt[k] = 1;
                }
                int dup = 0;
                foreach (KeyValuePair<string, int> kv in cnt) if (kv.Value > 1) dup++;

                Check(dup == 0, "★「" + q + "」合并后无同名重复",
                    "共 " + merged.Count + " 条，重复 " + dup);

                // 主结果不被挤掉
                if (main.Count > 0 && main.Count <= 60)
                {
                    int mainKept = 0;
                    foreach (SearchResult r in merged)
                        if (r.Source == "已安装") mainKept++;
                    Check(mainKept > 0 || main.Count == 0,
                        "「" + q + "」主结果未被完全挤掉",
                        "主 " + main.Count + " → 保留 " + mainKept);
                }
            }
            Console.WriteLine();
        }

        // ---------- 8. 源码级同步断言 ----------
        static void TestSourceLevelSync()
        {
            Console.WriteLine("--- 8. 源码级：实现与测试保持同步 ---");

            string root = FindRepoRoot();
            if (root == null)
            {
                Console.WriteLine("      跳过：找不到仓库根");
                Console.WriteLine();
                return;
            }

            string path = Path.Combine(root, "src", "ui", "SearchResultList.cs");
            if (!File.Exists(path))
            {
                Console.WriteLine("      跳过：找不到 SearchResultList.cs");
                Console.WriteLine();
                return;
            }

            string text = File.ReadAllText(path, Encoding.UTF8);

            Check(text.IndexOf("shown.Add(r.Path)", StringComparison.Ordinal) >= 0,
                "★实现里仍有路径去重键");
            Check(text.IndexOf("Trim().ToLowerInvariant()", StringComparison.Ordinal) >= 0,
                "★实现里用 Trim().ToLowerInvariant() 做名字键（保守键）");
            Check(text.IndexOf("\"N:\"", StringComparison.Ordinal) >= 0,
                "★名字键有 N: 前缀（避免与路径键冲突）");
            Check(text.IndexOf("MaxDisplay", StringComparison.Ordinal) >= 0,
                "结果上限仍在");
            Check(text.IndexOf("SystemSearch.Instant", StringComparison.Ordinal) >= 0,
                "★Instant 补充搜索仍在（未被删除）");
            Check(text.IndexOf("MatchCatalog", StringComparison.Ordinal) >= 0,
                "★MatchCatalog 主搜索仍在");
            Console.WriteLine();
        }

        // ========== 辅助 ==========

        static SearchResult Make(string name, string path, string source)
        {
            SearchResult r = new SearchResult();
            r.Name = name;
            r.Path = path;
            r.Source = source;
            r.IsFolder = false;
            try { r.Ext = Path.GetExtension(path).ToLowerInvariant(); } catch { }
            return r;
        }

        /// <summary>
        /// 复刻 SearchResultList.AppendBatch 的合并逻辑。
        ///
        /// **必须与实现保持同步**：实现改了这里也要改，
        /// TestSourceLevelSync 会提醒。
        /// </summary>
        static List<SearchResult> Merge(List<SearchResult> main, List<SearchResult> extra, int cap)
        {
            List<SearchResult> result = new List<SearchResult>();
            HashSet<string> shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Append(result, shown, main, cap);
            Append(result, shown, extra, cap);
            return result;
        }

        static void Append(List<SearchResult> result, HashSet<string> shown,
            List<SearchResult> batch, int cap)
        {
            if (batch == null) return;
            foreach (SearchResult r in batch)
            {
                if (r == null) continue;
                if (result.Count >= cap) return;
                if (!shown.Add(r.Path)) continue;

                string normName = (r.Name ?? "").Trim().ToLowerInvariant();
                if (normName.Length > 0 && !shown.Add("N:" + normName)) continue;

                result.Add(r);
            }
        }

        static int CountCatalogMatches(Catalog cat, string q)
        {
            int n = 0;
            foreach (AppItem a in cat.Apps)
            {
                if (a == null || string.IsNullOrEmpty(a.Path)) continue;
                if (Pinyin.Matches(a.Name, q)) n++;
            }
            return n;
        }

        /// <summary>复刻 MatchCatalog（含排序）。</summary>
        static List<SearchResult> MatchCatalogSim(Catalog cat, string q)
        {
            List<SearchResult> list = new List<SearchResult>();
            foreach (AppItem a in cat.Apps)
            {
                if (a == null || string.IsNullOrEmpty(a.Path)) continue;
                if (!Pinyin.Matches(a.Name, q)) continue;
                list.Add(Make(a.Name, a.Path, "已安装"));
            }
            list.Sort(delegate(SearchResult x, SearchResult y)
            {
                int lx = x.Name == null ? 0 : x.Name.Length;
                int ly = y.Name == null ? 0 : y.Name.Length;
                if (lx != ly) return lx.CompareTo(ly);
                return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        static string FindRepoRoot()
        {
            try
            {
                string d = AppDomain.CurrentDomain.BaseDirectory;
                for (int i = 0; i < 8 && d != null; i++)
                {
                    if (Directory.Exists(Path.Combine(d, "src", "ui"))
                        && Directory.Exists(Path.Combine(d, "config")))
                        return d;
                    DirectoryInfo parent = Directory.GetParent(d);
                    if (parent == null) break;
                    d = parent.FullName;
                }
            }
            catch { }
            return null;
        }
    }
}
