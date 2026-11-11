using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 搜索合并测试：目录搜索（MatchCatalog）+ 系统索引（Instant）的
    /// 去重、统一智能排序、上限、拼音保留。
    ///
    /// **为什么不用真实 Catalog**：不同机器装的软件不同，断言具体应用会失败。
    /// 这里直接构造 SearchResult 列表，调用 core 的 SearchRanking.Merge；
    /// SearchResultList 的行内来源说明与异步接线另用源码级断言保护。
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
            TestUnifiedOrdering();
            TestCap();
            TestInlineSourceDetail();
            TestRealQueries();
            TestSmartRanking();
            TestSourceLevelSync();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 8. 智能排序（直接测生产逻辑） ----------
        static void TestSmartRanking()
        {
            Console.WriteLine("--- 8. 智能排序（匹配质量 + 用户习惯）---");

            List<SearchResult> list = new List<SearchResult> {
                Make("Visual Studio Code", @"C:\Apps\vscode.exe", "已安装"),
                Make("Code", @"C:\Apps\code.exe", "注册表"),
            };
            list[0].Usage = 999;
            SearchRanking.Sort(list, "code");
            Check(list[0].Name == "Code",
                "★原名精确匹配优先于高频子串命中", list[0].Name);

            list = new List<SearchResult> {
                Make("Alpha One", @"C:\Apps\a1.exe", "已安装"),
                Make("Alpha Two", @"C:\Apps\a2.exe", "已安装"),
            };
            list[1].Pinned = true;
            SearchRanking.Sort(list, "alpha");
            Check(list[0].Name == "Alpha Two", "★同质量时手动固定优先", list[0].Name);

            list[0].Pinned = false;
            list[0].Usage = 1;
            list[1].Usage = 20;
            SearchRanking.Sort(list, "alpha");
            Check(list[0].Usage == 20, "★同质量时使用次数高的优先",
                Convert.ToString(list[0].Usage));

            list[0].Usage = list[1].Usage = 3;
            list[0].LastUsed = 10;
            list[1].LastUsed = 99;
            SearchRanking.Sort(list, "alpha");
            Check(list[0].LastUsed == 99, "★次数相同时最近启动的优先",
                Convert.ToString(list[0].LastUsed));

            List<SearchResult> pinyin = new List<SearchResult> {
                Make("微信读书", @"C:\Apps\weread.exe", "已安装"),
                Make("微信", @"C:\Apps\wechat.exe", "已安装"),
            };
            SearchRanking.Sort(pinyin, "wx");
            Check(pinyin[0].Name == "微信", "★拼音首字母精确命中优先", pinyin[0].Name);

            Check(SearchRanking.Matches("Visual Studio Code", "visual code"),
                "★多词查询使用 AND 语义");
            Check(!SearchRanking.Matches("Visual Studio", "visual code"),
                "★缺少任一查询词不命中");

            List<SearchResult> many = new List<SearchResult>();
            for (int i = 0; i < 80; i++)
                many.Add(Make("Tool code helper " + i, @"C:\Apps\h" + i + ".exe", "注册表"));
            many.Add(Make("Code", @"C:\Apps\exact.exe", "注册表"));
            List<SearchResult> ranked = SearchRanking.Merge(
                new List<SearchResult>(), many, "code", 10);
            Check(ranked.Count == 10 && ranked[0].Name == "Code",
                "★先全量排名再截断，索引末尾的精确结果不会丢", ranked[0].Name);

            List<SearchResult> main = new List<SearchResult> {
                Make("Chrome", @"C:\Start\Chrome.lnk", "已安装")
            };
            List<SearchResult> extra = new List<SearchResult> {
                Make("Chrome", @"D:\Chrome\chrome.exe", "注册表")
            };
            ranked = SearchRanking.Merge(main, extra, "chrome", 60);
            Check(ranked.Count == 1 && ranked[0].Source == "已安装",
                "★统一排名仍保留目录主结果做同名去重");
            Console.WriteLine();
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

        // ---------- 4. 两路统一排序 ----------
        static void TestUnifiedOrdering()
        {
            Console.WriteLine("--- 4. 两路合并后按相关性统一排序 ---");

            List<SearchResult> main = new List<SearchResult> {
                Make("Visual Studio Code", @"C:\m1.exe", "已安装"),
                Make("Code Helper", @"C:\m2.exe", "已安装"),
            };
            main[0].Usage = 999;
            List<SearchResult> extra = new List<SearchResult> {
                Make("Code", @"C:\e1.exe", "注册表"),
                Make("Encode Tool", @"C:\e2.exe", "注册表"),
            };

            List<SearchResult> merged = Merge(main, extra, "code", 60);
            Check(merged.Count == 4, "共 4 条", Convert.ToString(merged.Count));
            Check(merged[0].Name == "Code" && merged[0].Source == "注册表",
                "★注册表精确结果可排在高频目录子串前", merged[0].Name);
            Check(merged[merged.Count - 1].Name == "Encode Tool",
                "普通内部子串低于前缀与词首命中", merged[merged.Count - 1].Name);
            Console.WriteLine();
        }

        // ---------- 5. 结果上限 ----------
        static void TestCap()
        {
            Console.WriteLine("--- 5. 结果上限 ---");

            List<SearchResult> main = new List<SearchResult>();
            for (int i = 0; i < 50; i++)
            {
                SearchResult r = Make("Tool Main " + i, @"C:\m" + i + ".exe", "已安装");
                r.Usage = i;
                main.Add(r);
            }

            List<SearchResult> extra = new List<SearchResult>();
            for (int i = 0; i < 50; i++)
            {
                SearchResult r = Make("Tool Extra " + i, @"C:\e" + i + ".exe", "注册表");
                r.Usage = 100 + i;
                extra.Add(r);
            }

            List<SearchResult> merged = Merge(main, extra, "tool", 60);
            Check(merged.Count == 60, "★总条数 = 60（MaxDisplay）", Convert.ToString(merged.Count));
            Check(merged[0].Name == "Tool Extra 49",
                "★先统一排名再截断", merged[0].Name);

            int extraCount = 0;
            foreach (SearchResult r in merged) if (r.Name.StartsWith("Tool Extra")) extraCount++;
            Check(extraCount == 50, "★高相关补充结果不会被主来源预留名额挤掉",
                Convert.ToString(extraCount));

            // 上限小于主结果数
            merged = Merge(main, extra, "tool", 30);
            Check(merged.Count == 30, "上限 30 时只取 30 条", Convert.ToString(merged.Count));
            Console.WriteLine();
        }

        // ---------- 6. 来源改为行内次要信息 ----------
        static void TestInlineSourceDetail()
        {
            Console.WriteLine("--- 6. 来源作为行内次要信息 ---");
            string root = FindRepoRoot();
            if (root == null)
            {
                Console.WriteLine("      跳过：找不到仓库根");
                Console.WriteLine();
                return;
            }

            string text = File.ReadAllText(Path.Combine(root, "src", "ui", "SearchResultList.cs"),
                Encoding.UTF8);
            Check(text.IndexOf("ResultDetail(r)", StringComparison.Ordinal) >= 0,
                "★每一行渲染来源详情");
            Check(text.IndexOf("r.Source + \" · \" + path", StringComparison.Ordinal) >= 0,
                "★来源与路径在次要文字中组合显示");
            Check(text.IndexOf("MakeSourceHeader", StringComparison.Ordinal) < 0,
                "★不再用来源大标题切断统一排名");
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
                List<SearchResult> merged = Merge(main, extra, q, 60);

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

        // ---------- 9. 源码级同步断言 ----------
        static void TestSourceLevelSync()
        {
            Console.WriteLine("--- 9. 源码级：实现与测试保持同步 ---");

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
            Check(text.IndexOf("ThreadPool.QueueUserWorkItem", StringComparison.Ordinal) >= 0,
                "★系统补充搜索在后台执行");
            Check(text.IndexOf("AppIcons.GetAsync", StringComparison.Ordinal) >= 0,
                "★搜索结果图标异步补齐");
            Check(text.IndexOf("ownGeneration != generation", StringComparison.Ordinal) >= 0,
                "★过期搜索结果受 generation 保护");
            Check(text.IndexOf("SearchRanking.Merge", StringComparison.Ordinal) >= 0,
                "★两路结果走统一智能排名");
            Check(text.IndexOf("按来源分组", StringComparison.Ordinal) < 0,
                "★实现注释不再描述旧的来源分组界面");
            Check(text.IndexOf("ResultOpened", StringComparison.Ordinal) >= 0,
                "★从搜索启动也会记录使用历史");
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
        /// 直接调用生产合并逻辑；空查询用于只验证去重、不改变来源兜底顺序的用例。
        /// </summary>
        static List<SearchResult> Merge(List<SearchResult> main, List<SearchResult> extra, int cap)
        {
            return SearchRanking.Merge(main, extra, "", cap);
        }

        static List<SearchResult> Merge(List<SearchResult> main, List<SearchResult> extra,
            string query, int cap)
        {
            return SearchRanking.Merge(main, extra, query, cap);
        }

        static int CountCatalogMatches(Catalog cat, string q)
        {
            int n = 0;
            foreach (AppItem a in cat.Apps)
            {
                if (a == null || string.IsNullOrEmpty(a.Path)) continue;
                if (SearchRanking.Matches(a.Name, q)) n++;
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
                if (!SearchRanking.Matches(a.Name, q)) continue;
                SearchResult r = Make(a.Name, a.Path, "已安装");
                r.Usage = a.Usage;
                r.LastUsed = a.LastUsed;
                list.Add(r);
            }
            SearchRanking.Sort(list, q);
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
