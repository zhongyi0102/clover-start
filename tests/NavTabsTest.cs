using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 自定义标签清单测试：新建 / 重命名 / 删除 / 上限 / 内置名保护 / 持久化 / 容错。
    ///
    /// **全部用临时目录，不碰真实 config。**
    /// </summary>
    static class NavTabsTest
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
            Console.WriteLine("=== 自定义标签清单测试 ===");
            Console.WriteLine();

            try
            {
                TestNameSanitize();
                TestReserved();
                TestAddRemove();
                TestRename();
                TestLimits();
                TestPersistence();
                TestCorruptFile();
            }
            catch (Exception ex)
            {
                Check(false, "测试不抛异常", ex.GetType().Name + ": " + ex.Message);
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static string NewRoot()
        {
            string d = Path.Combine(Path.GetTempPath(), "navtabs-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(d, "config"));
            return d;
        }

        static void Clean(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }

        // ---------- 1. 名称清洗 ----------
        static void TestNameSanitize()
        {
            Console.WriteLine("--- 1. 名称清洗 ---");

            Check(NavTabs.SanitizeName("  游戏  ") == "游戏", "★去首尾空白", NavTabs.SanitizeName("  游戏  "));
            Check(NavTabs.SanitizeName("游戏   中心") == "游戏 中心", "★内部连续空白压成一个",
                NavTabs.SanitizeName("游戏   中心"));
            Check(NavTabs.SanitizeName("") == "", "空串 → 空");
            Check(NavTabs.SanitizeName(null) == "", "null → 空");
            Check(NavTabs.SanitizeName("   ") == "", "全空白 → 空");

            string longName = new string('A', 30);
            Check(NavTabs.SanitizeName(longName).Length == NavTabs.MaxNameLength,
                "★超长截断到上限 " + NavTabs.MaxNameLength,
                Convert.ToString(NavTabs.SanitizeName(longName).Length));

            Check(NavTabs.SanitizeName("开发") == "开发", "正常名不变");

            Console.WriteLine();
        }

        // ---------- 2. 内置名保护 ----------
        static void TestReserved()
        {
            Console.WriteLine("--- 2. 内置名保护 ---");

            Check(NavTabs.IsReserved("常用"), "常用 是内置");
            Check(NavTabs.IsReserved("全部"), "全部 是内置");
            Check(NavTabs.IsReserved("文件"), "文件 是内置");
            Check(!NavTabs.IsReserved("游戏"), "游戏 不是内置");
            Check(!NavTabs.IsReserved(""), "空串不是内置");
            Check(!NavTabs.IsReserved(null), "null 不是内置");
            Check(NavTabs.IsReserved("常用"), "内置判断");

            Console.WriteLine();
        }

        // ---------- 3. 新建 / 删除 ----------
        static void TestAddRemove()
        {
            Console.WriteLine("--- 3. 新建 / 删除 ---");

            string root = NewRoot();
            try
            {
                NavTabs t = new NavTabs(root);
                t.Load();
                Check(t.Items.Count == 0, "初始为空", Convert.ToString(t.Items.Count));

                string e1 = t.Add("游戏");
                Check(e1 == null, "★新建成功", e1 ?? "");
                Check(t.Items.Count == 1, "数量 1", Convert.ToString(t.Items.Count));
                Check(t.Items[0].Name == "游戏", "名字正确", t.Items[0].Name);

                // 重名
                string e2 = t.Add("游戏");
                Check(e2 != null, "★重名被拒绝", e2 ?? "");
                Check(t.Items.Count == 1, "重名后数量不变");

                // 大小写不敏感的重名
                string e3 = t.Add("GAME");
                Check(e3 == null, "GAME 新建成功");
                string e4 = t.Add("game");
                Check(e4 != null, "★大小写不同也算重名", e4 ?? "");

                // 内置名
                string e5 = t.Add("常用");
                Check(e5 != null, "★内置名被拒绝", e5 ?? "");
                string e6 = t.Add("文件");
                Check(e6 != null, "★文件 被拒绝", e6 ?? "");

                // 空名
                string e7 = t.Add("   ");
                Check(e7 != null, "★空名被拒绝", e7 ?? "");

                // 删除
                string e8 = t.Remove("游戏");
                Check(e8 == null, "★删除成功", e8 ?? "");
                Check(t.Items.Count == 1, "删除后数量 1", Convert.ToString(t.Items.Count));
                Check(t.FindIndex("游戏") < 0, "找不到已删除的");

                // 删除不存在的
                string e9 = t.Remove("不存在");
                Check(e9 != null, "删除不存在的被拒绝", e9 ?? "");

                // 删除内置
                string e10 = t.Remove("常用");
                Check(e10 != null, "★内置标签不能删除", e10 ?? "");

                Console.WriteLine();
            }
            finally { Clean(root); }
        }

        // ---------- 4. 重命名 ----------
        static void TestRename()
        {
            Console.WriteLine("--- 4. 重命名 ---");

            string root = NewRoot();
            try
            {
                NavTabs t = new NavTabs(root);
                t.Load();
                t.Add("游戏");
                t.Add("开发");

                string e1 = t.Rename("游戏", "娱乐");
                Check(e1 == null, "★重命名成功", e1 ?? "");
                Check(t.FindIndex("娱乐") >= 0, "新名字找得到");
                Check(t.FindIndex("游戏") < 0, "旧名字找不到");

                // 改成已存在的名字
                string e2 = t.Rename("娱乐", "开发");
                Check(e2 != null, "★重名被拒绝", e2 ?? "");

                // 改成内置名
                string e3 = t.Rename("娱乐", "常用");
                Check(e3 != null, "★改成内置名被拒绝", e3 ?? "");

                // 改成空
                string e4 = t.Rename("娱乐", "  ");
                Check(e4 != null, "改成空被拒绝", e4 ?? "");

                // 改名不存在
                string e5 = t.Rename("不存在", "x");
                Check(e5 != null, "改名不存在的被拒绝", e5 ?? "");

                // 改成自己（允许，不算重名）
                string e6 = t.Rename("开发", "开发");
                Check(e6 == null, "★改成自己允许", e6 ?? "");

                Console.WriteLine();
            }
            finally { Clean(root); }
        }

        // ---------- 5. 上限 ----------
        static void TestLimits()
        {
            Console.WriteLine("--- 5. 数量上限 ---");

            string root = NewRoot();
            try
            {
                NavTabs t = new NavTabs(root);
                t.Load();

                for (int i = 0; i < NavTabs.MaxTabs; i++)
                {
                    string e = t.Add("标签" + i);
                    if (e != null) { Check(false, "第 " + i + " 个应成功", e); break; }
                }
                Check(t.Items.Count == NavTabs.MaxTabs, "★达到上限 " + NavTabs.MaxTabs,
                    Convert.ToString(t.Items.Count));

                string over = t.Add("溢出");
                Check(over != null, "★超上限被拒绝", over ?? "");
                Check(t.Items.Count == NavTabs.MaxTabs, "超限后数量不变");

                Console.WriteLine();
            }
            finally { Clean(root); }
        }

        // ---------- 6. 持久化 ----------
        static void TestPersistence()
        {
            Console.WriteLine("--- 6. 持久化 ---");

            string root = NewRoot();
            try
            {
                NavTabs t = new NavTabs(root);
                t.Load();
                t.Add("游戏");
                t.Add("开发");
                t.Add("影音");

                string file = Path.Combine(root, "config", "nav-tabs.json");
                Check(File.Exists(file), "★文件已写出", file);

                // 重新加载
                NavTabs t2 = new NavTabs(root);
                t2.Load();
                Check(t2.Items.Count == 3, "★重载数量 3", Convert.ToString(t2.Items.Count));
                Check(t2.Items[0].Name == "游戏", "顺序 0 正确", t2.Items[0].Name);
                Check(t2.Items[1].Name == "开发", "顺序 1 正确", t2.Items[1].Name);
                Check(t2.Items[2].Name == "影音", "顺序 2 正确", t2.Items[2].Name);

                // 删除后持久化
                t2.Remove("开发");
                NavTabs t3 = new NavTabs(root);
                t3.Load();
                Check(t3.Items.Count == 2, "★删除已持久化", Convert.ToString(t3.Items.Count));
                Check(t3.FindIndex("开发") < 0, "开发 已不在");

                // 重命名后持久化
                t3.Rename("影音", "媒体");
                NavTabs t4 = new NavTabs(root);
                t4.Load();
                Check(t4.FindIndex("媒体") >= 0, "★重命名已持久化");

                Console.WriteLine();
            }
            finally { Clean(root); }
        }

        // ---------- 7. 容错 ----------
        static void TestCorruptFile()
        {
            Console.WriteLine("--- 7. 容错 ---");

            string root = NewRoot();
            try
            {
                string file = Path.Combine(root, "config", "nav-tabs.json");

                // 坏 JSON
                File.WriteAllText(file, "{ 这不是合法 JSON", new UTF8Encoding(false));
                NavTabs t = new NavTabs(root);
                t.Load();
                Check(t.Items.Count == 0, "★坏 JSON → 空清单，不抛异常",
                    Convert.ToString(t.Items.Count));

                // 缺 tabs 字段
                File.WriteAllText(file, "{\"other\":1}", new UTF8Encoding(false));
                t.Load();
                Check(t.Items.Count == 0, "缺 tabs → 空清单");

                // tabs 不是数组
                File.WriteAllText(file, "{\"tabs\":\"abc\"}", new UTF8Encoding(false));
                t.Load();
                Check(t.Items.Count == 0, "tabs 非数组 → 空清单");

                // 元素缺 name
                File.WriteAllText(file, "{\"tabs\":[{\"x\":1},{\"name\":\"有效\"}]}", new UTF8Encoding(false));
                t.Load();
                Check(t.Items.Count == 1, "★跳过缺 name 的元素", Convert.ToString(t.Items.Count));
                Check(t.Items[0].Name == "有效", "保留有效项");

                // 文件里含内置名 → 跳过
                File.WriteAllText(file, "{\"tabs\":[{\"name\":\"常用\"},{\"name\":\"自定义\"}]}",
                    new UTF8Encoding(false));
                t.Load();
                Check(t.Items.Count == 1, "★内置名被过滤掉", Convert.ToString(t.Items.Count));
                Check(t.Items[0].Name == "自定义", "保留自定义");

                // 文件里含重复名 → 去重
                File.WriteAllText(file, "{\"tabs\":[{\"name\":\"A\"},{\"name\":\"a\"}]}",
                    new UTF8Encoding(false));
                t.Load();
                Check(t.Items.Count == 1, "★重复名去重（大小写不敏感）", Convert.ToString(t.Items.Count));

                // 文件不存在
                File.Delete(file);
                t.Load();
                Check(t.Items.Count == 0, "文件不存在 → 空清单");

                Console.WriteLine();
            }
            finally { Clean(root); }
        }
    }
}
