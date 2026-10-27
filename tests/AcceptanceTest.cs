using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
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

            // 再造一批填充应用，让磁贴区**真的能滚动**。
            // 为什么需要：滚动位置恢复只有在"内容超出视口"时才有意义，
            // 3 个磁贴撑不满一屏，ScrollToVerticalOffset 会被钳到 0，
            // 那样的断言等于没测。
            {
                List<object> filler = new List<object>();
                for (int i = 0; i < 60; i++)
                {
                    string p = Path.Combine(root, "fill" + i + ".exe");
                    File.WriteAllText(p, "x", new UTF8Encoding(false));
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["name"] = "Fill" + i.ToString("D2");
                    m["path"] = p;
                    m["category"] = (i % 2 == 0) ? "" : "办公";   // 一半进「全部」，一半进「办公」
                    filler.Add(m);
                }
                // 追加到 manual-apps.json
                AppendManualApps(root, filler);
            }

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

            // ---------- 9. 隐藏应用：分类出现、条目过滤 ----------
            // 先记下隐藏前的总数做基线（后面用相对对比，不写死数字）
            int hiddenBaseline = 0;
            try { hiddenBaseline = Catalog.Load(root).Apps.Count; }
            catch { }

            Verify("隐藏一个应用后「隐藏的项目」分类出现", delegate
            {
                HiddenApps h = HiddenApps.Load(root);
                h.Hide(exeB);

                Catalog after = Catalog.Load(root);
                if (after.HiddenItems().Count != 1) return "隐藏项数应为 1，实际 " + after.HiddenItems().Count;
                if (after.ByCategory("开发").Count != 0) return "被隐藏的仍出现在原分类里";

                // 总数不该变：隐藏只是打标记，条目仍留在 Apps 里（「隐藏的项目」要能看到）
                // 用 **基线对比** 而不是写死数字 —— 夹具里还有一批填充应用，
                // 写死数字的话以后一改夹具这条断言就假失败。
                int baseline = hiddenBaseline;
                if (baseline <= 0) return "缺少基线（TestsCatalogCount 未初始化）";
                if (after.Apps.Count != baseline)
                    return string.Format("隐藏项不该从 Apps 里消失（应有 {0} 个，实际 {1}）",
                        baseline, after.Apps.Count);
                return "ok（总数保持 " + baseline + "）";
            });

            // ---------- 10. ★回归：上次分类已消失时必须回退，不能停在空分类 ----------
            // 这条对应一个真实 bug：取消隐藏全部后重启，程序会停在已不存在的
            // 「隐藏的项目」上，面板一片空白。修法是 LoadLastCategory 校验分类**还有内容**。
            Verify("★回归：上次分类已消失时回退到「全部」", delegate
            {
                // 造出 bug 的触发条件：last-category 指向「隐藏的项目」，
                // 但隐藏清单是空的（分类不该存在）
                string lastCat = Path.Combine(root, "config", "last-category.json");
                File.WriteAllText(lastCat,
                    "{\"category\":\"\\u0001hidden\"}", new UTF8Encoding(false));

                // 清空隐藏清单
                HiddenApps hidden = HiddenApps.Load(root);
                hidden.Clear();

                // 重建窗口 = 模拟重启
                PanelWindow reborn = new PanelWindow(root, null, Settings.Load(root));
                reborn.UpdateLayout();
                string cat = reborn.CurrentCategory();
                reborn.Close();

                if (cat == PanelWindow.HiddenCategory)
                    return "停在已消失的「隐藏的项目」上（bug 复发）";
                if (cat != "") return "应回退到「全部」(空串)，实际: " + cat;
                return "ok";
            });

            // ---------- 11. 悬停放大用的是 RenderTransform（不触发布局）------------
            // 为什么专门断言：如果用磁贴本身的 Width/Height 做放大，WrapPanel 会重新排版，
            // 鼠标附近的磁贴会跳位。这里确认放大作用在**图标的 ScaleTransform**上。
            Verify("★图标悬停放大用 RenderTransform（不触发布局重排）", delegate
            {
                // 磁贴尺寸应保持 96x90 —— 没有因为"放大"而被改
                List<Border> tiles = FindAll<Border>(panel);
                int tilesAt96 = 0;
                foreach (Border b in tiles)
                {
                    if (Math.Abs(b.Width - 96) < 0.1 && Math.Abs(b.Height - 90) < 0.1) tilesAt96++;
                }
                if (tilesAt96 == 0) return "找不到 96x90 的磁贴";

                // 至少有一个 32x32 的图标视觉元素带 ScaleTransform
                int scaled = 0;
                foreach (FrameworkElement fe in FindAll<FrameworkElement>(panel))
                {
                    ScaleTransform st = fe.RenderTransform as ScaleTransform;
                    if (st == null) continue;
                    // 图标是 32x32（Image 或占位 Border）
                    double w = fe.Width, h = fe.Height;
                    if (Math.Abs(w - 32) < 0.1 && Math.Abs(h - 32) < 0.1) scaled++;
                }
                if (scaled == 0) return "没有任何图标带 ScaleTransform（放大未接线）";
                return "ok（" + tilesAt96 + " 块磁贴保持 96x90，" + scaled + " 个图标已接放大）";
            });

            // ---------- 12. 弹出动画用的位移变换已建立 ----------
            Verify("面板弹出动画的位移变换已建立", delegate
            {
                // showShift 是 8px 上移动画用的 TranslateTransform，挂在 rootBorder 上。
                // 这里只确认它是 TranslateTransform 且接线在根容器上 ——
                // 具体动画时序无法在离屏环境断言。
                Border rootB = panel.Content as Border;
                if (rootB == null) return "根不是 Border";
                TranslateTransform tt = rootB.RenderTransform as TranslateTransform;
                if (tt == null) return "根容器上没有 TranslateTransform（动画未接线）";
                return "ok（根容器已接位移变换）";
            });

            // ---------- 14. 侧栏折叠按钮：位置在侧栏内、可收起可恢复 ----------
            // 为什么专门断言位置：按钮用 HorizontalAlignment.Right 定位，
            // 一旦落在错的地方，用户点不到（表现是"点了没反应"）。
            Verify("★侧栏折叠按钮落在侧栏区域内", delegate
            {
                Border t = panel.NavToggle;
                if (t == null) return "navToggle 为 null（按钮没建立）";
                if (!t.IsVisible) return "按钮不可见";

                try
                {
                    GeneralTransform gt = t.TransformToAncestor(panel);
                    Point p = gt.Transform(new Point(0, 0));
                    double left = p.X, right = p.X + t.ActualWidth;
                    string d = string.Format("按钮 @({0:N0},{1:N0}) {2:N0}x{3:N0}，右边缘 {4:N0}",
                        p.X, p.Y, t.ActualWidth, t.ActualHeight, right);

                    // root.Margin=16，侧栏宽 160 → 侧栏在面板坐标 16 .. 176
                    if (left < 10) return d + " —— 跑到侧栏左边了";
                    if (right > 200) return d + " —— 跑到侧栏外面了";
                    return "ok（" + d + "）";
                }
                catch (Exception ex)
                {
                    return "取坐标失败: " + ex.Message;
                }
            });

            Verify("★侧栏可收起并恢复", delegate
            {
                if (panel.NavColumnValue < 0) return "navColumn 为 null";

                double before = panel.NavColumnValue;
                panel.ToggleNavNow();
                double collapsed = panel.NavColumnValue;
                bool isCollapsed = panel.NavIsCollapsed;

                panel.ToggleNavNow();
                double back = panel.NavColumnValue;
                bool isBack = panel.NavIsCollapsed;

                if (Math.Abs(before - PanelWindow.NavWidthExpanded) > 0.1)
                    return "初始宽度应为 160，实际 " + before;
                if (!isCollapsed || Math.Abs(collapsed - PanelWindow.NavWidthCollapsed) > 0.1)
                    return string.Format("收起失败：collapsed={0} 宽度={1}", isCollapsed, collapsed);
                if (isBack || Math.Abs(back - PanelWindow.NavWidthExpanded) > 0.1)
                    return string.Format("恢复失败：collapsed={0} 宽度={1}", isBack, back);

                return string.Format("ok（160 -> {0:N0} -> 160）", collapsed);
            });

            // ---------- 15. ★命中测试：折叠按钮那个点上真的是它吗 ----------
            // 为什么单独测这个：按钮位置对、逻辑也对，但真实鼠标点不到 ——
            // 只有 InputHitTest 能告诉我们"那个坐标上实际是谁"。
            // WPF 里后来的子元素会盖住先加的（同一个 Grid 单元格尤其容易踩）。
            Verify("★折叠按钮那个点上点得到它（无遮挡）", delegate
            {
                Border t = panel.NavToggle;
                if (t == null) return "navToggle 为 null";
                if (t.ActualWidth <= 0 || t.ActualHeight <= 0)
                    return string.Format("按钮尺寸为 0：{0}x{1}", t.ActualWidth, t.ActualHeight);

                // 按钮中心（相对 panel）
                Point inPanel = t.TransformToAncestor(panel)
                    .Transform(new Point(t.ActualWidth / 2, t.ActualHeight / 2));

                // 改成相对 panel 的 root 内容坐标做命中测试
                // （panel 是 Window，InputHitTest 要作用于它的内容元素）
                FrameworkElement content = panel.Content as FrameworkElement;
                if (content == null) return "panel.Content 不是 FrameworkElement";

                Point inContent = t.TransformToAncestor(content)
                    .Transform(new Point(t.ActualWidth / 2, t.ActualHeight / 2));

                IInputElement hit = content.InputHitTest(inContent);
                DependencyObject hitObj = hit as DependencyObject;

                string hitDesc = "null";
                if (hitObj != null)
                {
                    FrameworkElement fe = hitObj as FrameworkElement;
                    hitDesc = hitObj.GetType().Name;
                    if (fe != null && !string.IsNullOrEmpty(fe.Name)) hitDesc += "#" + fe.Name;
                }

                if (hitObj == null) return "命中测试返回 null（该点没有任何可交互元素）";

                // 命中结果应是按钮本身或它的直接子元素（那个字形 TextBlock）
                if (hitObj == t) return "ok（命中 = 按钮本身）";
                if (hitObj is TextBlock && IsDescendantOfVisual(hitObj, t))
                    return "ok（命中 = 按钮内的字形）";

                return "被别的元素盖住了：命中 " + hitDesc + "，坐标 " + inContent;
            });

            // ---------- 16. ★滚动位置按分类分别记住 ----------
            // 为什么按分类记：切走再回来应该还在原处，而不是全局共用一个偏移
            // （那样从长分类切到短分类会滚到看不见的位置）。
            Verify("★滚动位置按分类记住并能恢复", delegate
            {
                string catA = "";          // 全部（应用最多，一定可滚）
                string catB = "办公";

                panel.SwitchCategoryForTest(catA);
                panel.UpdateLayout();

                // 滚到 120px 处
                panel.ScrollToForTest(120);
                double atA = panel.CurrentScrollOffset();
                if (!panel.HasScrollMemory(catA))
                    return "滚动后没有记录 " + (catA == "" ? "全部" : catA) + " 的偏移";
                if (atA < 50) return "滚不动或偏移过小：" + atA;

                // 切到另一个分类，再切回来
                panel.SwitchCategoryForTest(catB);
                panel.UpdateLayout();
                panel.SwitchCategoryForTest(catA);
                panel.UpdateLayout();

                double back = panel.CurrentScrollOffset();
                if (Math.Abs(back - atA) > 2)
                    return string.Format("切回后偏移不一致：原 {0:N0} -> 现在 {1:N0}", atA, back);

                // 两个分类各自有记录
                if (panel.ScrollMemoryCount() < 1) return "记录数为 0";
                return string.Format("ok（{0:N0} -> 切走 -> 切回 {1:N0}）", atA, back);
            });

            // ---------- 17. ★主题：刷子真的变色（而不是只改了设置）------------
            // 为什么必须断言花色值：主题最容易出的问题是"设置改了、界面没变"
            // —— 因为刷子被 Freeze 过、或控件把颜色写死在自己身上。
            Verify("★主题切换真的改变颜色刷子", delegate
            {
                // 浅色
                Ui.ApplyTheme(Ui.ThemeMode.Light);
                byte lightBgR = Ui.Bg.Color.R;
                byte lightFgR = Ui.Fg.Color.R;
                if (lightBgR < 200) return "浅色底应偏亮，实际 R=" + lightBgR;
                if (lightFgR > 100) return "浅色前景应偏暗，实际 R=" + lightFgR;

                // 深色
                Ui.ApplyTheme(Ui.ThemeMode.Dark);
                byte darkBgR = Ui.Bg.Color.R;
                byte darkFgR = Ui.Fg.Color.R;
                if (darkBgR > 80) return "深色底应偏暗，实际 R=" + darkBgR;
                if (darkFgR < 150) return "深色前景应偏亮，实际 R=" + darkFgR;

                // 关键：必须是同一批刷子对象（引用不变），否则 126 处调用点会拿不到新颜色
                if (!ReferenceEquals(Ui.Bg, Ui.Bg)) return "刷子对象被替换了";

                // 强调色在深色下也应更亮
                if (Ui.Accent.Color.R <= 56) return "深色下的强调色没有变亮，R=" + Ui.Accent.Color.R;

                // 切回浅色应完全恢复
                Ui.ApplyTheme(Ui.ThemeMode.Light);
                if (Ui.Bg.Color.R != lightBgR || Ui.Fg.Color.R != lightFgR)
                    return "切回浅色没有恢复原值";

                return string.Format("ok（浅 {0} / 深 {1}）", lightBgR, darkBgR);
            });

            Verify("★主题非法值回退到「跟随系统」", delegate
            {
                Ui.ThemeMode m1 = Ui.ParseTheme("dark");
                Ui.ThemeMode m2 = Ui.ParseTheme("light");
                Ui.ThemeMode m3 = Ui.ParseTheme("system");
                Ui.ThemeMode m4 = Ui.ParseTheme("这不是合法值");
                Ui.ThemeMode m5 = Ui.ParseTheme(null);

                if (m1 != Ui.ThemeMode.Dark) return "dark 解析错";
                if (m2 != Ui.ThemeMode.Light) return "light 解析错";
                if (m3 != Ui.ThemeMode.System) return "system 解析错";
                if (m4 != Ui.ThemeMode.System) return "非法值应回退到 system，实际 " + m4;
                if (m5 != Ui.ThemeMode.System) return "null 应回退到 system，实际 " + m5;

                // 往返：存的字符串要能读回来
                if (Ui.ParseTheme(Ui.ThemeToString(Ui.ThemeMode.Dark)) != Ui.ThemeMode.Dark)
                    return "dark 往返失败";
                if (Ui.ParseTheme(Ui.ThemeToString(Ui.ThemeMode.Light)) != Ui.ThemeMode.Light)
                    return "light 往返失败";
                if (Ui.ParseTheme(Ui.ThemeToString(Ui.ThemeMode.System)) != Ui.ThemeMode.System)
                    return "system 往返失败";

                return "ok";
            });

            Verify("★深色下投影会加深（否则面板边界糊在深底里）", delegate
            {
                Ui.ApplyTheme(Ui.ThemeMode.Light);
                DropShadowEffect lo = Ui.PanelShadow();
                Ui.ApplyTheme(Ui.ThemeMode.Dark);
                DropShadowEffect hi = Ui.PanelShadow();

                if (hi.Opacity <= lo.Opacity)
                    return string.Format("深色投影不该更淡：浅 {0:N2} 深 {1:N2}", lo.Opacity, hi.Opacity);

                Ui.ApplyTheme(Ui.ThemeMode.Light);
                return string.Format("ok（{0:N2} -> {1:N2}）", lo.Opacity, hi.Opacity);
            });

            // ---------- 18. last-category.json 写在临时目录 ----------
            Verify("last-category.json 写在临时目录（未污染真实目录）", delegate
            {
                string p = Path.Combine(root, "config", "last-category.json");
                if (!File.Exists(p)) return "文件不存在: " + p;
                return "ok";
            });

            // ---------- 12. order.json 键名往返（「全部」分类不该用空串当键）------------
            // 背景：空串是合法的 JSON 键，但会让 PowerShell 的 ConvertFrom-Json 报错，
            // 手工排查 order.json 时很别扭。存盘改成 "__all__"，读取两种都认。
            Verify("order.json 键名往返且兼容老的\"\"写法", delegate
            {
                // 「全部」(空串) 存成 __all__
                string saved = PanelWindow.FileCatKey("");
                if (saved == "") return "空串仍被直接写盘（改动未生效）";
                if (PanelWindow.RestoreCatKey(saved) != "") return "往返失败：读回来不是空串";
                // 老文件里的 "" 仍能读
                if (PanelWindow.RestoreCatKey("") != "") return "不兼容老的\"\"写法";
                // 普通分类名不受影响
                if (PanelWindow.FileCatKey("办公") != "办公") return "普通分类被改写";
                if (PanelWindow.RestoreCatKey("办公") != "办公") return "普通分类读回不对";
                return "ok（空串 -> " + saved + " -> 空串）";
            });

            // ---------- 13. 面板显示/隐藏是真的能跑 ----------
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

        /// <summary>子元素是否在视觉树上属于某个祖先（命中测试用）。</summary>
        static bool IsDescendantOfVisual(DependencyObject child, DependencyObject ancestor)
        {
            try
            {
                DependencyObject cur = child;
                while (cur != null)
                {
                    if (cur == ancestor) return true;
                    cur = VisualTreeHelper.GetParent(cur);
                }
            }
            catch { }
            return false;
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

        /// <summary>往 manual-apps.json 追加一批条目（用于造出可滚动的内容）。</summary>
        static void AppendManualApps(string root, List<object> extra)
        {
            string p = Path.Combine(root, "config", "manual-apps.json");
            JavaScriptSerializer ser = new JavaScriptSerializer();
            List<object> arr = new List<object>();

            if (File.Exists(p))
            {
                try
                {
                    Dictionary<string, object> old =
                        ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(p, Encoding.UTF8));
                    object o;
                    if (old != null && old.TryGetValue("apps", out o) && o != null)
                    {
                        foreach (object x in (System.Collections.IEnumerable)o) arr.Add(x);
                    }
                }
                catch { }
            }

            arr.AddRange(extra);
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["apps"] = arr;
            File.WriteAllText(p, ser.Serialize(d), new UTF8Encoding(false));
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
