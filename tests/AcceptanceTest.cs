using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 最小验收测试：**真实创建 PanelWindow**，检查界面真的画出来了，
    /// 并把结果写成 JSON 证据报告。
    ///
    /// 与单元测试的区别：
    ///   - 单元测试断言"逻辑对不对"（函数返回值）
    ///   - 验收测试断言"界面真的存在"（控件树、数量、可见性）
    ///
    /// 借鉴 GlassDesk 的两个做法（只借鉴形态，不复制代码）：
    ///   1. 输出 JSON 证据报告，而不是只喊"通过"
    ///   2. **拒绝使用用户真实的 config 目录**，强制在临时目录里跑
    ///
    /// 运行： AcceptanceTest.exe [--report 输出路径]
    /// 退出码：0 = 全部通过，1 = 有失败，2 = 拒绝运行（指向了真实目录）
    /// </summary>
    static class AcceptanceTest
    {
        static int failed;
        static int passed;
        static readonly List<object> evidence = new List<object>();

        /// <summary>一条验收证据。</summary>
        class Item
        {
            public string name;
            public bool ok;
            public string detail;
        }

        static void Verify(string name, Func<string> check)
        {
            Item it = new Item { name = name };
            try
            {
                string detail = check();
                // 约定：返回 null/空 或 以 "ok" 开头 都算通过；
                // 其他内容一律当作失败原因（便于携带实测数据）。
                bool ok = string.IsNullOrEmpty(detail) || detail.StartsWith("ok");
                it.ok = ok;
                it.detail = detail ?? "";
                if (ok) passed++; else failed++;
            }
            catch (Exception ex)
            {
                it.ok = false;
                it.detail = ex.GetType().Name + ": " + ex.Message;
                failed++;
            }
            evidence.Add(it);
            Console.WriteLine((it.ok ? "PASS  " : "FAIL  ") + name +
                (it.ok || it.detail.Length == 0 ? "" : "  [" + it.detail + "]"));
        }

        [STAThread]
        static int Main(string[] args)
        {
            string reportPath = Argument(args, "--report");
            string dataDir = Argument(args, "--data-dir");

            // ---------- 安全闸：绝不使用用户真实的 config 目录 ----------
            string realBase = FindRealBaseDir();
            string root = dataDir != null
                ? Path.GetFullPath(dataDir)
                : Path.Combine(Path.GetTempPath(), "CloverAcceptance-" + Guid.NewGuid().ToString("n"));

            if (realBase != null)
            {
                // 目标目录等于真实目录、或落在它里面，一律拒绝
                if (root.Equals(realBase, StringComparison.OrdinalIgnoreCase) ||
                    root.StartsWith(realBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine(
                        "拒绝在用户真实数据目录里运行验收测试：" + root + "\r\n" +
                        "请用 --data-dir 指定一个临时目录，或不要传该参数（默认走临时目录）。");
                    return 2;
                }
            }

            Directory.CreateDirectory(Path.Combine(root, "config"));
            Directory.CreateDirectory(Path.Combine(root, "res"));

            Console.WriteLine("验收测试数据目录: " + root);
            Console.WriteLine("（用户真实目录受保护，已确认不在其中）");
            Console.WriteLine();

            // 需要 WPF Application 实例（窗口依赖资源、调度器）
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            try
            {
                RunAcceptance(root, reportPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("验收测试异常: " + ex);
                failed++;
            }
            finally
            {
                try { app.Shutdown(); } catch { }
            }

            WriteReport(reportPath, root);

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " 项，失败 " + failed + " 项");
            if (reportPath != null) Console.WriteLine("证据报告: " + reportPath);

            return failed == 0 ? 0 : 1;
        }

        static void RunAcceptance(string root, string reportPath)
        {
            // ---------- 1. 在临时目录里造最小数据 ----------
            string exeA = Path.Combine(root, "alpha.exe");
            string exeB = Path.Combine(root, "beta.exe");
            string exeC = Path.Combine(root, "gamma.exe");
            File.WriteAllText(exeA, "x", new UTF8Encoding(false));
            File.WriteAllText(exeB, "x", new UTF8Encoding(false));
            File.WriteAllText(exeC, "x", new UTF8Encoding(false));

            // 三个应用，分属「办公」「开发」「其他」
            WriteManualApps(root, new string[][]
            {
                new string[] { "AlphaApp", exeA, "办公" },
                new string[] { "BetaApp",  exeB, "开发" },
                new string[] { "GammaApp", exeC, "其他" },
            });

            // ---------- 2. 真实创建 PanelWindow ----------
            PanelWindow panel = null;

            Verify("能真实创建 PanelWindow 窗口", delegate
            {
                Settings settings = Settings.Load(root);
                panel = new PanelWindow(root, null, settings);   // trigger 传 null（已做空值保护）
                return panel != null ? "ok" : "构造返回 null";
            });

            if (panel == null)
            {
                // 后面都依赖它，直接短路
                evidence.Add(new Item { name = "（后续检查跳过）", ok = false, detail = "面板未创建成功" });
                failed++;
                return;
            }

            // WPF 的窗口在 Show 之前，可视化树不完整（子元素尚未应用到 VisualTree），
            // 所以要先真的显示出来，才能验证"界面确实画出来了"。
            // 这也是验收测试区别于单元测试的地方：它需要一个真实的窗口生命周期。
            Verify("能显示面板并完成布局", delegate
            {
                panel.Show();
                // 让布局跑完
                panel.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Loaded);
                panel.UpdateLayout();
                return panel.IsVisible ? "ok" : "Show 后 IsVisible 仍为 false";
            });

            // ---------- 3. 界面证据：控件树真的建起来了 ----------
            Verify("窗口标题正确", delegate
            {
                return panel.Title == "四叶草面板" ? "ok" : "实际标题: " + panel.Title;
            });

            Verify("窗口有内容（Content 非空）", delegate
            {
                if (panel.Content == null) return "Content 为 null";
                return "ok";
            });

            // 递归数控件（确认真画出了控件树，不是空壳）
            int visualCount = 0;
            Verify("控件树已真实构建（可递归遍历）", delegate
            {
                Border rootBorder = panel.Content as Border;
                if (rootBorder == null) return "根不是 Border";
                visualCount = CountVisuals(rootBorder);
                if (visualCount < 20) return "控件数偏少: " + visualCount;
                return "ok（共 " + visualCount + " 个可视元素）";
            });

            // ---------- 4. 分类侧栏真的生成了条目 ----------
            Verify("左侧分类栏生成了条目", delegate
            {
                List<Border> navItems = FindNavItems(panel);
                if (navItems == null) return "找不到分类栏容器";
                // 至少有「常用」「全部」两类
                if (navItems.Count < 2) return "分类条目过少: " + navItems.Count;
                return "ok（" + navItems.Count + " 个分类条目）";
            });

            // ---------- 5. 应用磁贴真的渲染了 ----------
            int tileCount = 0;
            Verify("应用磁贴已渲染", delegate
            {
                tileCount = CountTiles(panel);
                if (tileCount < 3) return "磁贴数少于预期: " + tileCount;
                return "ok（" + tileCount + " 个磁贴）";
            });

            // ---------- 6. 搜索框存在且可输入 ----------
            Verify("搜索框存在且可输入", delegate
            {
                TextBox box = FindDescendant<TextBox>(panel);
                if (box == null) return "找不到 TextBox";
                box.Text = "alp";      // 真输入
                string after = box.Text;
                box.Text = "";
                return after == "alp" ? "ok" : "输入未生效: " + after;
            });

            // ---------- 7. 分类覆盖在真实界面里生效 ----------
            Verify("「移到分类」覆盖能改变实际分类", delegate
            {
                CategoryOverrides ov = CategoryOverrides.Load(root);
                ov.Set(exeA, "影音娱乐");

                // 重新加载目录（模拟界面刷新时走的路径）
                Catalog c = Catalog.Load(root);
                foreach (AppItem a in c.Apps)
                {
                    if (a.Name == "AlphaApp")
                    {
                        if (a.Category == "影音娱乐") return "ok";
                        return "覆盖未生效，实际: " + a.Category;
                    }
                }
                return "找不到 AlphaApp";
            });

            // ---------- 8. 覆盖表文件真的落在临时目录里 ----------
            Verify("category-overrides.json 写在临时目录（未污染真实目录）", delegate
            {
                string p = CategoryOverrides.PathOf(root);
                if (!File.Exists(p)) return "文件不存在: " + p;
                return "ok（" + p + "）";
            });

            // ---------- 9. 面板显示/隐藏是真的能跑 ----------
            Verify("能显示面板（Show 不抛异常）", delegate
            {
                panel.Show();
                bool visible = panel.IsVisible;
                return visible ? "ok" : "调用 Show 后 IsVisible 仍为 false";
            });

            Verify("能隐藏面板（Hide 不抛异常）", delegate
            {
                panel.Hide();
                return panel.IsVisible ? "调用 Hide 后仍可见" : "ok";
            });
        }

        // ---------- 辅助：递归数可视元素 ----------
        static int CountVisuals(DependencyObject d)
        {
            if (d == null) return 0;
            int n = 1;
            try
            {
                int c = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < c; i++)
                    n += CountVisuals(VisualTreeHelper.GetChild(d, i));
            }
            catch { }
            return n;
        }

        /// <summary>找左侧分类栏（StackPanel 里装着一堆 Border）。</summary>
        static List<Border> FindNavItems(PanelWindow panel)
        {
            List<Border> best = new List<Border>();
            try
            {
                // 分类栏是 Margin=(10,4,4,2) 的 ScrollViewer 里的 StackPanel
                foreach (ScrollViewer sv in FindAll<ScrollViewer>(panel))
                {
                    StackPanel sp = sv.Content as StackPanel;
                    if (sp == null) continue;
                    if (sp.Children.Count <= best.Count) continue;
                    // 里面的子项是 Border 且高度 34（分类条目样式）
                    bool looksLikeNav = false;
                    foreach (object o in sp.Children)
                    {
                        Border b = o as Border;
                        if (b != null && Math.Abs(b.Height - 34) < 0.1) { looksLikeNav = true; break; }
                    }
                    if (!looksLikeNav) continue;

                    List<Border> items = new List<Border>();
                    foreach (object o in sp.Children)
                    {
                        Border b = o as Border;
                        if (b != null) items.Add(b);
                    }
                    best = items;
                }
            }
            catch { }
            return best;
        }

        /// <summary>数应用磁贴（96×90 的 Border）。</summary>
        static int CountTiles(PanelWindow panel)
        {
            int n = 0;
            try
            {
                foreach (Border b in FindAll<Border>(panel))
                {
                    if (Math.Abs(b.Width - 96) < 0.1 && Math.Abs(b.Height - 90) < 0.1) n++;
                }
            }
            catch { }
            return n;
        }

        static T FindDescendant<T>(DependencyObject d) where T : DependencyObject
        {
            List<T> all = FindAll<T>(d);
            return all.Count > 0 ? all[0] : null;
        }

        static List<T> FindAll<T>(DependencyObject d) where T : DependencyObject
        {
            List<T> list = new List<T>();
            try
            {
                if (d == null) return list;
                T t = d as T;
                if (t != null) list.Add(t);
                int c = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < c; i++)
                    list.AddRange(FindAll<T>(VisualTreeHelper.GetChild(d, i)));
            }
            catch { }
            return list;
        }

        // ---------- 辅助：找到用户真实的程序根目录（用于安全闸） ----------
        static string FindRealBaseDir()
        {
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                // 测试 exe 在 tests\bin 下，真实程序在 clover-start 下
                // 这里只做"拒绝保护"用，取不到就返回 null（不阻断）
                DirectoryInfo di = new DirectoryInfo(exeDir);
                for (int i = 0; i < 6 && di != null; i++)
                {
                    if (di.Name.Equals("clover-start", StringComparison.OrdinalIgnoreCase))
                        return di.FullName;
                    di = di.Parent;
                }
            }
            catch { }
            return null;
        }

        static void WriteManualApps(string root, string[][] rows)
        {
            List<object> arr = new List<object>();
            foreach (string[] r in rows)
            {
                Dictionary<string, object> m = new Dictionary<string, object>();
                m["name"] = r[0];
                m["path"] = r[1];
                m["category"] = r[2];
                arr.Add(m);
            }
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["apps"] = arr;
            JavaScriptSerializer ser = new JavaScriptSerializer();
            File.WriteAllText(Path.Combine(root, "config", "manual-apps.json"),
                ser.Serialize(d), new UTF8Encoding(false));
        }

        static string Argument(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        static void WriteReport(string reportPath, string dataDir)
        {
            if (string.IsNullOrEmpty(reportPath)) return;
            try
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["suite"] = "AcceptanceTest";
                d["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                d["passed"] = passed;
                d["failed"] = failed;
                d["ok"] = failed == 0;
                d["dataDir"] = dataDir;
                d["note"] = "运行在临时目录，未接触用户真实 config";

                List<object> items = new List<object>();
                foreach (object o in evidence)
                {
                    Item it = o as Item;
                    if (it == null) continue;
                    Dictionary<string, object> e = new Dictionary<string, object>();
                    e["name"] = it.name;
                    e["ok"] = it.ok;
                    e["detail"] = it.detail;
                    items.Add(e);
                }
                d["evidence"] = items;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(d);

                string dir = Path.GetDirectoryName(Path.GetFullPath(reportPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(reportPath, json, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("写证据报告失败: " + ex.Message);
            }
        }
    }
}
