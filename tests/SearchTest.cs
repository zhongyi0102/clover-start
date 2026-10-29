using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Clover
{
    /// <summary>
    /// 系统级搜索回归测试（`SystemSearch.Instant`）。
    ///
    /// **为什么需要它**：清理磁盘搜索逻辑时，`Instant` 是唯一保留的路径，
    /// 必须有测试锁住它的行为，否则以后改动（或误删某个私有方法）不会被发现。
    ///
    /// **不依赖真实开始菜单内容**：不同机器装的软件不同，断言具体应用名会失败。
    /// 只验证「结构性质」：
    ///   · 不抛异常
    ///   · 返回非 null
    ///   · 结果上限生效
    ///   · 排序稳定（同分不随机）
    ///   · 空查询返回空
    ///   · 结果里没有目录（只搜可启动的程序）
    /// </summary>
    static class SearchTest
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
            Console.WriteLine("=== 系统级搜索测试（Instant）===");
            Console.WriteLine();

            TestEmptyQuery();
            TestNoThrow();
            TestResultShape();
            TestCap();
            TestDeterministic();
            TestNoDeepRemnants();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 空查询 ----------
        static void TestEmptyQuery()
        {
            Console.WriteLine("--- 1. 空查询 ---");

            Check(Count(SystemSearch.Instant(null)) == 0, "null → 空结果");
            Check(Count(SystemSearch.Instant("")) == 0, "空串 → 空结果");
            Check(Count(SystemSearch.Instant("   ")) == 0, "空白 → 空结果");
            Check(Count(SystemSearch.Instant("\t\t")) == 0, "制表符 → 空结果");
            Console.WriteLine();
        }

        // ---------- 2. 不抛异常 ----------
        static void TestNoThrow()
        {
            Console.WriteLine("--- 2. 任意查询不抛异常 ---");

            string[] queries = {
                "chrome", "微信", "wx", "weixin", "notepad", "cmd",
                "a", "zz", "!!!", "\\\\", "..", "con", "a b c",
                new string('x', 200), "中文English混合", "%TEMP%", "*.exe"
            };

            bool threw = false;
            string bad = null;
            foreach (string q in queries)
            {
                try { SystemSearch.Instant(q); }
                catch (Exception ex) { threw = true; bad = q + " → " + ex.GetType().Name; break; }
            }
            Check(!threw, "★16 种查询全部不抛异常", bad ?? "");
            Console.WriteLine();
        }

        // ---------- 3. 结果结构 ----------
        static void TestResultShape()
        {
            Console.WriteLine("--- 3. 结果结构正确 ---");

            List<SearchResult> r = SystemSearch.Instant("a");
            Check(r != null, "返回非 null");

            bool allNamed = true, allSourced = true, noFolder = true, noEmptyName = true;
            foreach (SearchResult x in r)
            {
                if (x.Name == null) allNamed = false;
                if (string.IsNullOrEmpty(x.Source)) allSourced = false;
                if (x.IsFolder) noFolder = false;
                if (string.IsNullOrWhiteSpace(x.Name)) noEmptyName = false;
            }

            Check(allNamed, "所有结果 Name 非 null");
            Check(noEmptyName, "所有结果 Name 非空白");
            Check(allSourced, "所有结果 Source 非空", "共 " + r.Count + " 条");
            Check(noFolder, "★不返回目录（只搜可启动的程序）", "共 " + r.Count + " 条");

            // Source 只能是这两种
            HashSet<string> validSources = new HashSet<string> { "开始菜单", "注册表" };
            bool okSource = true;
            foreach (SearchResult x in r)
                if (!validSources.Contains(x.Source)) { okSource = false; break; }
            Check(okSource, "★Source 只有「开始菜单」/「注册表」两类");
            Console.WriteLine();
        }

        // ---------- 4. 上限生效 ----------
        static void TestCap()
        {
            Console.WriteLine("--- 4. 结果上限生效 ---");

            // "a" 通常命中很多条目；上限 150
            List<SearchResult> r = SystemSearch.Instant("a");
            Check(r.Count <= 150, "★结果数 ≤ 150（MaxInstant）", Convert.ToString(r.Count));

            // 单字符查询不应返回几千条
            List<SearchResult> e = SystemSearch.Instant("e");
            Check(e.Count <= 150, "单字符 e 结果数 ≤ 150", Convert.ToString(e.Count));
            Console.WriteLine();
        }

        // ---------- 5. 确定性 ----------
        static void TestDeterministic()
        {
            Console.WriteLine("--- 5. 同一查询结果稳定（排序不随机）---");

            List<SearchResult> a = SystemSearch.Instant("a");
            List<SearchResult> b = SystemSearch.Instant("a");

            Check(a.Count == b.Count, "两次调用结果数一致",
                a.Count + " vs " + b.Count);

            bool sameOrder = a.Count == b.Count;
            if (sameOrder)
            {
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i].Name != b[i].Name || a[i].Path != b[i].Path)
                    {
                        sameOrder = false;
                        break;
                    }
                }
            }
            Check(sameOrder, "★两次调用顺序完全一致（排序稳定）");
            Console.WriteLine();
        }

        // ---------- 6. 磁盘搜索确实不存在 ----------
        static void TestNoDeepRemnants()
        {
            Console.WriteLine("--- 6. 磁盘搜索逻辑确实已移除 ---");

            // 反射检查：SystemSearch 不应再有 Deep / DeepRoots 等公开成员
            Type t = typeof(SystemSearch);
            string[] gone = { "Deep", "DeepRoots", "ShouldSkipDir", "JunkExt", "SkipDirs", "MaxDepth", "DeepBudgetMs" };
            foreach (string name in gone)
            {
                System.Reflection.MethodInfo m = t.GetMethod(name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Static);
                System.Reflection.FieldInfo f = t.GetField(name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Static);
                Check(m == null && f == null, "★SystemSearch." + name + " 已移除");
            }

            // Instant 必须还在
            System.Reflection.MethodInfo instant = t.GetMethod("Instant",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Check(instant != null, "★SystemSearch.Instant 仍存在");
            Check(instant != null && instant.ReturnType == typeof(List<SearchResult>),
                "Instant 返回 List<SearchResult>");

            // 源码级：确认 GetDownloads / MaxTotal / MaxPerRoot 孤儿已删
            string srcDir = FindSourceDir();
            if (srcDir != null)
            {
                string path = System.IO.Path.Combine(srcDir, "core", "SystemSearch.cs");
                if (System.IO.File.Exists(path))
                {
                    string text = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);
                    Check(text.IndexOf("GetDownloads", StringComparison.Ordinal) < 0,
                        "★GetDownloads 孤儿方法已删除");
                    Check(text.IndexOf("MaxTotal", StringComparison.Ordinal) < 0,
                        "★MaxTotal 孤儿常量已删除");
                    Check(text.IndexOf("MaxPerRoot", StringComparison.Ordinal) < 0,
                        "★MaxPerRoot 孤儿常量已删除");
                    Check(text.IndexOf("磁盘文件搜索", StringComparison.Ordinal) < 0,
                        "★注释里的「磁盘文件搜索」已清理");
                }
            }

            // SearchResultList 的 AddStatusLine 应已删除
            if (srcDir != null)
            {
                string path2 = System.IO.Path.Combine(srcDir, "ui", "SearchResultList.cs");
                if (System.IO.File.Exists(path2))
                {
                    string text2 = System.IO.File.ReadAllText(path2, System.Text.Encoding.UTF8);
                    Check(text2.IndexOf("AddStatusLine", StringComparison.Ordinal) < 0,
                        "★SearchResultList.AddStatusLine 孤儿方法已删除");
                    Check(text2.IndexOf("磁盘文件搜索已移除", StringComparison.Ordinal) < 0,
                        "★SearchResultList 的过时注释已清理");
                }
            }
            Console.WriteLine();
        }

        // ---------- 辅助 ----------

        static int Count(List<SearchResult> r) { return r == null ? 0 : r.Count; }

        static string FindSourceDir()
        {
            try
            {
                string d = AppDomain.CurrentDomain.BaseDirectory;
                for (int i = 0; i < 8 && d != null; i++)
                {
                    string candidate = System.IO.Path.Combine(d, "src", "ui");
                    if (System.IO.Directory.Exists(candidate)) return System.IO.Path.Combine(d, "src");
                    System.IO.DirectoryInfo parent = System.IO.Directory.GetParent(d);
                    if (parent == null) break;
                    d = parent.FullName;
                }
            }
            catch { }
            return null;
        }
    }
}
