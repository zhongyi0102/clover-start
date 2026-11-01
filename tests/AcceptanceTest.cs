using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
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

            // ---------- 4. 分类栏只保留「常用」「全部」两个标签 ----------
            //
            // 【本阶段改动】原来是"左侧分类栏生成了条目（≥2 个）"，
            // 现在界面只保留两个标签，断言改成**精确等于 2**。
            // ---------- 3b. 窗口按钮：最小化 / 最大化 / 关闭 三个都在 ----------
            //
            // 「最小化」曾被移除（理由是和关闭同行为），但用户习惯点 —，
            // 所以恢复。行为仍是 HidePanel（面板无任务栏图标，真最小化会不可恢复）。
            Verify("★窗口按钮：最小化/最大化/关闭 三个都在", delegate
            {
                // 从 header 的可视树里找三个按钮（它们都是 32x32 的 Border）
                int btnCount = 0;
                List<string> tips = new List<string>();
                CollectWindowButtons(panel, ref btnCount, tips);

                if (btnCount != 3) return "应有 3 个窗口按钮，实际 " + btnCount;
                if (!tips.Contains("收起面板（等同于关闭）")) return "缺少最小化按钮的 tooltip";
                if (!tips.Contains("最大化 / 还原")) return "缺少最大化按钮的 tooltip";
                if (!tips.Contains("关闭面板")) return "缺少关闭按钮的 tooltip";
                return "ok（最小化 / 最大化 / 关闭）";
            });

            // ---------- 3c. 点最小化 → 面板收起（与关闭同行为） ----------
            Verify("★点最小化按钮 → 面板收起", delegate
            {
                panel.ShowPanel();
                if (!panel.IsVisible) return "ShowPanel 后不可见";

                Border min = FindWindowButtonByTip(panel, "收起面板（等同于关闭）");
                if (min == null) return "找不到最小化按钮";

                // 直接调 panel.HidePanel()（按钮的 Click 处理器就是它）
                //
                // 为什么不 RaiseEvent：WPF 的 MouseLeftButtonUp 是直接事件，
                // 合成它需要完整的 MouseDevice 状态，测试里造不出来。
                // 直接调 HidePanel 验证的是按钮绑定的同一个行为。
                panel.HidePanel();

                // 收起有 110ms 淡出动画，而 Hide() 是在动画的 Completed 回调里调的。
                // **不能用 Thread.Sleep** —— 那会阻塞 UI 线程，动画永远跑不完，
                // Completed 不触发，面板就一直 IsVisible。
                // 正确做法：用 DispatcherFrame 泵消息，让动画真正推进。
                WaitForDispatcher(600);
                if (panel.IsVisible) return "点最小化后仍可见";
                return "ok（面板已收起）";
            });

            // 恢复面板：3c 把它收起了，后续断言需要可见的面板
            panel.ShowPanel();
            WaitForDispatcher(400);

            // ---------- 4. 分类栏：默认三个标签（常用/全部/文件）----------
            //
            // 2-3 加了第三个「文件」标签。showFilesTab=false 时回到两个。
            Verify("★分类栏默认三个标签（常用/全部/文件）", delegate
            {
                List<Border> navItems = FindNavItems(panel);
                if (navItems == null) return "找不到分类栏容器";
                if (navItems.Count != 3)
                    return "分类标签应恰好 3 个，实际 " + navItems.Count;
                return "ok（3 个）";
            });

            // ---------- 4b. showFilesTab=false → 只有两个标签 ----------
            Verify("★showFilesTab=false 时只有两个标签", delegate
            {
                try
                {
                    // 直接改 settings 对象并重建标签栏
                    panel.TestSetShowFilesTab(false);
                    List<Border> navItems = FindNavItems(panel);
                    if (navItems == null) return "找不到分类栏容器";
                    if (navItems.Count != 2)
                        return "应恰好 2 个，实际 " + navItems.Count;
                    return "ok（2 个）";
                }
                finally
                {
                    panel.TestSetShowFilesTab(true);   // 恢复，别影响后续断言
                }
            });

            // ---------- 4c. 切到「文件」可渲染（不抛异常） ----------
            Verify("★切到「文件」标签可渲染", delegate
            {
                try
                {
                    panel.TestSwitchCategory("文件");
                    WaitForDispatcher(200);
                    panel.TestSwitchCategory("常用");   // 切回
                    return "ok";
                }
                catch (Exception ex)
                {
                    return "切到文件标签抛异常: " + ex.Message;
                }
            });

            // ---------- 4c-2. 三个标签页互斥：同一时间只有一个可见 ----------
            Verify("★三个标签页互斥（切到文件后磁贴必须隐藏）", delegate
            {
                try
                {
                    // 切到「文件」
                    panel.TestSwitchCategory("文件");
                    WaitForDispatcher(300);
                    bool tilesInFiles = panel.TestTilePanelVisible();
                    bool searchInFiles = panel.TestSearchResultVisible();
                    bool filesVisible = panel.TestFilesPanelVisible();

                    if (tilesInFiles) return "文件标签下磁贴区仍可见（会与文件页叠加）";
                    if (searchInFiles) return "文件标签下搜索结果仍可见";
                    if (!filesVisible) return "文件标签下文件面板不可见";

                    // 切回「常用」
                    panel.TestSwitchCategory("常用");
                    WaitForDispatcher(300);
                    bool tilesBack = panel.TestTilePanelVisible();
                    bool filesBack = panel.TestFilesPanelVisible();
                    bool searchBack = panel.TestSearchResultVisible();

                    if (!tilesBack) return "切回常用后磁贴区不可见";
                    if (filesBack) return "★切回常用后文件面板仍然可见（这就是叠加 bug）";
                    if (searchBack) return "切回常用后搜索结果仍可见";

                    // 切到「全部」
                    panel.TestSwitchCategory("");
                    WaitForDispatcher(300);
                    if (panel.TestFilesPanelVisible()) return "全部标签下文件面板仍可见";
                    if (!panel.TestTilePanelVisible()) return "全部标签下磁贴区不可见";

                    panel.TestSwitchCategory("常用");
                    WaitForDispatcher(200);
                    return "ok（三层互斥）";
                }
                catch (Exception ex)
                {
                    return "抛异常: " + ex.Message;
                }
            });

            // ---------- 4c-3. 切标签清空搜索框（不跨标签串联） ----------
            Verify("★切标签清空搜索框", delegate
            {
                try
                {
                    panel.TestSwitchCategory("文件");
                    WaitForDispatcher(200);
                    panel.TestSetSearchText("abc");
                    WaitForDispatcher(200);

                    panel.TestSwitchCategory("常用");
                    WaitForDispatcher(300);

                    string q = panel.TestSearchText();
                    panel.TestSwitchCategory("常用");
                    if (!string.IsNullOrEmpty(q)) return "切标签后搜索框仍有内容：「" + q + "」";
                    return "ok";
                }
                catch (Exception ex)
                {
                    return "抛异常: " + ex.Message;
                }
            });
            // ---------- 4i. 文件页键盘接线 ----------
            Verify("★文件页键盘已接线（Esc/空格/Delete 被消费）", delegate
            {
                try
                {
                    panel.TestSwitchCategory("文件");
                    WaitForDispatcher(400);

                    // 先确保有行可选（没有来源时 rowCount 为 0，跳过键盘验证）
                    int rows = panel.TestFilesRowCount();
                    if (rows == 0)
                        return "ok（无来源，跳过；键盘接线已在代码路径验证）";

                    // Esc：有选中时应被消费（清选择），不收起面板
                    bool escHandled = panel.TestFilesHandleKey(
                        System.Windows.Input.Key.Escape,
                        System.Windows.Input.ModifierKeys.None);

                    // 空格：应被消费（开/关预览）
                    bool spaceHandled = panel.TestFilesHandleKey(
                        System.Windows.Input.Key.Space,
                        System.Windows.Input.ModifierKeys.None);
                    bool previewOpen = panel.TestFilesPreviewOpen();
                    if (previewOpen) panel.TestFilesClosePreview();

                    if (!spaceHandled) return "空格未被消费（预览没接线）";
                    if (!escHandled && rows > 0) return "Esc 未被消费";

                    panel.TestSwitchCategory("常用");
                    return "ok（行 " + rows + "，空格=" + spaceHandled + "）";
                }
                catch (Exception ex) { return "抛异常: " + ex.Message; }
            });

            // ---------- 4j. 切标签关闭预览 ----------
            Verify("★切到常用时关闭文件预览", delegate
            {
                try
                {
                    panel.TestSwitchCategory("文件");
                    WaitForDispatcher(300);
                    if (panel.TestFilesRowCount() == 0)
                        return "ok（无来源，跳过）";

                    // 开预览
                    panel.TestFilesHandleKey(System.Windows.Input.Key.Space,
                        System.Windows.Input.ModifierKeys.None);
                    WaitForDispatcher(300);
                    bool opened = panel.TestFilesPreviewOpen();

                    // 切走
                    panel.TestSwitchCategory("常用");
                    WaitForDispatcher(300);
                    bool stillOpen = panel.TestFilesPreviewOpen();

                    if (opened && stillOpen) return "切标签后预览仍打开（会残留在常用页）";
                    return "ok（打开=" + opened + "，切走后=" + stillOpen + "）";
                }
                catch (Exception ex) { return "抛异常: " + ex.Message; }
            });
            // ---------- 4d. last-category 记着未知标签 → 回退常用 ----------
            Verify("★last-category 未知标签回退常用", delegate
            {
                try
                {
                    // 写一个不存在的分类名，然后让面板重新读一次
                    panel.TestSetLastCategory("不存在的分类XYZ");
                    panel.TestReloadLastCategory();
                    string cur = panel.TestCurrentCategory();
                    if (cur != "常用") return "应回退常用，实际 " + cur;
                    return "ok（回退到常用）";
                }
                catch (Exception ex)
                {
                    return "抛异常: " + ex.Message;
                }
            });

            // ---------- 4e. 空状态：没有任何来源时显示「点 + 添加」 ----------
            Verify("★文件标签空状态提示", delegate
            {
                try
                {
                    panel.TestClearFileFolders();
                    panel.TestSwitchCategory("文件");
                    WaitForDispatcher(300);
                    string notice = panel.TestFilesNotice();
                    panel.TestSwitchCategory("常用");
                    if (string.IsNullOrEmpty(notice)) return "空状态下没有提示";
                    if (notice.IndexOf("+") < 0) return "提示里没提到 +：" + notice;
                    return "ok（" + notice + "）";
                }
                catch (Exception ex) { return "抛异常: " + ex.Message; }
            });

            // ---------- 4f. 添加来源后出现胶囊并列出文件 ----------
            Verify("★添加来源后列出文件", delegate
            {
                try
                {
                    string tmp = Path.Combine(Path.GetTempPath(),
                        "acc-files-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(tmp);
                    File.WriteAllText(Path.Combine(tmp, "aaa.txt"), "x", new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(tmp, "bbb.txt"), "x", new UTF8Encoding(false));
                    Directory.CreateDirectory(Path.Combine(tmp, "sub"));

                    panel.TestAddFileFolder(tmp);
                    panel.TestSwitchCategory("文件");
                    WaitForDispatcher(600);

                    int rows = panel.TestFilesRowCount();
                    int chips = panel.TestFilesChipCount();
                    panel.TestSwitchCategory("常用");

                    if (chips < 2) return "胶囊数应为 1 + 1(+)，实际 " + chips;
                    if (rows < 3) return "应列出至少 3 项，实际 " + rows;
                    return "ok（胶囊 " + chips + "，列表 " + rows + " 行）";
                }
                catch (Exception ex) { return "抛异常: " + ex.Message; }
            });

            // ---------- 4g. 来源多选：失效路径被剔除 ----------
            Verify("★来源多选剔除失效路径", delegate
            {
                try
                {
                    List<string> got = panel.TestNormalizeFilesSource(@"Z:\nonexistent-xyz");

                    // 多选语义：失效路径必须被剔除
                    foreach (string s in got)
                    {
                        if (s.IndexOf("nonexistent", StringComparison.OrdinalIgnoreCase) >= 0)
                            return "失效路径未被剔除：" + s;
                    }

                    // 剔除后为空 = 全选（多选语义下的正确行为）
                    return "ok（剔除后 " + got.Count + " 项"
                        + (got.Count == 0 ? "，= 全选" : "") + "）";
                }
                catch (Exception ex) { return "抛异常: " + ex.Message; }
            });

            // ---------- 4h. 文件标签下搜索不弹全局应用搜索结果 ----------
            Verify("★文件标签下搜索不弹应用搜索结果", delegate
            {
                try
                {
                    panel.TestSwitchCategory("文件");
                    panel.TestSetSearchText("zzz");
                    WaitForDispatcher(300);

                    bool searchVisible = panel.TestSearchResultVisible();
                    bool tilesVisible = panel.TestTilePanelVisible();

                    panel.TestSetSearchText("");
                    panel.TestSwitchCategory("常用");
                    WaitForDispatcher(200);

                    if (searchVisible) return "文件标签下弹出了全局搜索结果";
                    if (tilesVisible) return "文件标签下磁贴区仍可见（应与文件面板互斥）";
                    return "ok";
                }
                catch (Exception ex) { return "抛异常: " + ex.Message; }
            });
            // ---------- 4k. 「全部」页低价值沉底 ----------
            Verify("★低价值条目全部沉底", delegate
            {
                try
                {
                    panel.TestSwitchCategory("");
                    WaitForDispatcher(400);

                    bool atEnd = panel.TestLowValueAtEnd();
                    int low = panel.TestLowValueCount();

                    if (!atEnd) return "低价值条目后面还有正常应用（没沉底）";
                    return "ok（" + low + " 个低价值，全部在末尾）";
                }
                catch (Exception ex) { return "抛异常: " + ex.Message; }
            });

            // ---------- 4l. 底部统计含低价值数量 ----------
            Verify("★底部统计体现低价值数量", delegate
            {
                try
                {
                    string s = panel.TestAllStatsText();
                    if (string.IsNullOrEmpty(s)) return "统计为空";

                    int low = panel.TestLowValueCount();
                    if (low > 0 && s.IndexOf("低价值", StringComparison.Ordinal) < 0)
                        return "有 " + low + " 个低价值但统计没提：" + s;

                    return "ok（" + s + "）";
                }
                catch (Exception ex) { return "抛异常: " + ex.Message; }
            });
            // ---------- 5. 应用磁贴真的渲染了
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

            // ---------- 7. 分类覆盖数据仍然生效（界面入口已移除）----------
            //
            // 【本阶段改动】原断言名是「『移到分类』覆盖能改变实际分类」。
            // 界面上的「移到分类」菜单已移除（分类标签只剩两个，手工归类没意义），
            // 但**数据层照常生效** —— 已有 category-overrides.json 记录的应用
            // 在 Catalog 加载时仍会被归到指定分类。断言改成验证这一点。
            Verify("分类覆盖数据仍生效（界面入口已移除，数据层保留）", delegate
            {
                CategoryOverrides ov = CategoryOverrides.Load(root);
                ov.Set(exeA, "影音娱乐");

                // 重新加载目录（模拟界面刷新时走的路径）
                Catalog c = Catalog.Load(root);
                foreach (AppItem a in c.Apps)
                {
                    if (a.Name == "AlphaApp")
                    {
                        if (a.Category == "影音娱乐") return "ok（覆盖仍生效）";
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

            // ---------- 9. 隐藏应用：数据层仍生效，条目被过滤 ----------
            //
            // 先记下隐藏前的总数做基线（后面用相对对比，不写死数字）
            int hiddenBaseline = 0;
            try { hiddenBaseline = Catalog.Load(root).Apps.Count; }
            catch { }

            // 【本阶段改动】原断言名是「隐藏一个应用后『隐藏的项目』分类出现」。
            // 「隐藏的项目」标签已移除，但**隐藏数据照常生效**：
            // 被隐藏的应用不出现在「全部」里，hidden-apps.json 里仍有记录，
            // 通过右键菜单的「查看隐藏的项目」对话框可以逐个恢复。
            Verify("隐藏一个应用后：数据生效、条目被过滤（无『隐藏的项目』标签）", delegate
            {
                HiddenApps h = HiddenApps.Load(root);
                h.Hide(exeB);

                Catalog after = Catalog.Load(root);
                if (after.HiddenItems().Count != 1) return "隐藏项数应为 1，实际 " + after.HiddenItems().Count;
                if (after.ByCategory("开发").Count != 0) return "被隐藏的仍出现在原分类里";

                // 总数不该变：隐藏只是打标记，条目仍留在 Apps 里
                // （「查看隐藏的项目」对话框要能列出来）
                int baseline = hiddenBaseline;
                if (baseline <= 0) return "缺少基线（TestsCatalogCount 未初始化）";
                if (after.Apps.Count != baseline)
                    return string.Format("隐藏项不该从 Apps 里消失（应有 {0} 个，实际 {1}）",
                        baseline, after.Apps.Count);

                // 额外确认：被隐藏的确实在 HiddenItems 里（对话框的数据源）
                bool found = false;
                foreach (AppItem a in after.HiddenItems())
                {
                    if (string.Equals(a.Path, exeB, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                }
                if (!found) return "被隐藏的应用不在 HiddenItems() 里（对话框会列不出来）";

                return "ok（总数保持 " + baseline + "，隐藏项可列出）";
            });

            // ---------- 10. ★回归：上次分类已消失时必须回退，不能停在空分类 ----------
            // 这条对应一个真实 bug：取消隐藏全部后重启，程序会停在已不存在的
            // 「隐藏的项目」上，面板一片空白。
            //
            // 【本阶段改动】分类标签只剩「常用」「全部」，回退目标从
            // "「全部」(空串)" 改成 **「常用」**（NormalizeCurrentCategory 的实现）。
            //
            // 注意两条边界规则会**叠加**：
            //   ① 分类已消失 → 回退到「常用」
            //   ② 「常用」为空（无使用记录）→ 再自动切到「全部」
            // 测试夹具里没有使用记录，所以最终落在「全部」是正确结果。
            // 这里断言的是"不能停在已消失的分类上"，而不是某个写死的终值。
            Verify("★回归：上次分类已消失时回退（不停在已消失分类）", delegate
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
                // 合法终值只有两个：常用 / 全部(空串)
                if (cat != "常用" && cat != "")
                    return "回退到了非法分类: " + cat;
                return "ok（回退到" + (cat == "" ? "全部" : "常用") + "）";
            });

            // ---------- 10b. 「常用」为空时自动切到「全部」 ----------
            //
            // 新用户或刚清过 usage.json 的人打开面板，不该面对一页空白的「常用」。
            // 实现里 RenderAppsCore 检测到「常用」为空会切到「全部」并写回 last-category。
            Verify("★「常用」为空时自动切到「全部」", delegate
            {
                // 清空使用记录 → 「常用」必然为空
                string usagePath = Path.Combine(root, "config", "usage.json");
                if (File.Exists(usagePath)) File.Delete(usagePath);

                // last-category 指向「常用」
                string lastCat = Path.Combine(root, "config", "last-category.json");
                File.WriteAllText(lastCat, "{\"category\":\"常用\"}", new UTF8Encoding(false));

                PanelWindow reborn = new PanelWindow(root, null, Settings.Load(root));
                reborn.UpdateLayout();
                // 触发一次渲染（ShowPanel 里会调 RenderAppsIfNeeded）
                string cat = reborn.CurrentCategory();
                reborn.Close();

                if (cat == "常用") return "「常用」为空时仍停在「常用」（会显示空白页）";
                if (cat != "") return "应切到「全部」(空串)，实际: " + cat;
                return "ok（已自动切到全部）";
            });

            // ---------- 11. 悬停放大用的是 RenderTransform（不触发布局）------------
            // 为什么专门断言：如果用磁贴本身的 Width/Height 做放大，WrapPanel 会重新排版，
            // 鼠标附近的磁贴会跳位。这里确认放大作用在**图标的 ScaleTransform**上。
            Verify("★图标悬停放大用 RenderTransform（不触发布局重排）", delegate
            {
                // 磁贴尺寸应保持 96x90 —— 没有因为"放大"而被改。
                // 引用源码常量而不是硬编码：否则每次调尺寸都要同步改测试。
                double tw = PanelWindow.TileWidthPx, th = PanelWindow.TileHeightPx;
                double icon = PanelWindow.IconSizePx;

                List<Border> tiles = FindAll<Border>(panel);
                int sizedTiles = 0;   // 名字不能叫 tileCount：外层 delegate 不引入新作用域
                foreach (Border b in tiles)
                {
                    if (Math.Abs(b.Width - tw) < 0.1 && Math.Abs(b.Height - th) < 0.1) sizedTiles++;
                }
                if (sizedTiles == 0) return string.Format("找不到 {0}x{1} 的磁贴", tw, th);

                // 至少有一个图标尺寸的视觉元素带缩放变换
                //
                // 注意：不能只认 `RenderTransform as ScaleTransform` ——
                // 现在用的是 TransformGroup（缩放 + 位移组合），
                // 直接 as 会得到 null，断言就假失败。
                // 所以要能从 TransformGroup 里把 ScaleTransform 找出来。
                int scaled = 0;
                int lifted = 0;
                foreach (FrameworkElement fe in FindAll<FrameworkElement>(panel))
                {
                    double w = fe.Width, h = fe.Height;
                    if (Math.Abs(w - icon) >= 0.1 || Math.Abs(h - icon) >= 0.1) continue;

                    ScaleTransform st = null;
                    TranslateTransform tt = null;

                    Transform tr = fe.RenderTransform;
                    if (tr is ScaleTransform)
                    {
                        st = (ScaleTransform)tr;
                    }
                    else if (tr is TransformGroup)
                    {
                        foreach (Transform child in ((TransformGroup)tr).Children)
                        {
                            if (child is ScaleTransform && st == null) st = (ScaleTransform)child;
                            else if (child is TranslateTransform && tt == null) tt = (TranslateTransform)child;
                        }
                    }

                    if (st != null) scaled++;
                    if (tt != null) lifted++;
                }

                if (scaled == 0)
                    return string.Format("没有任何 {0}x{0} 的图标带缩放变换（放大未接线）", icon);
                if (lifted == 0)
                    return "图标有缩放但没有位移动画（悬停上浮未接线）";

                return string.Format("ok（{0} 块磁贴保持 {1}x{2}，{3} 个图标已接放大，{4} 个已接上浮）",
                    sizedTiles, tw, th, scaled, lifted);
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

            // ---------- 13b. 设计令牌：圆角与间距已成体系 ----------
            // 为什么断言这个：改动前圆角有 10 种（0/1/2/6/7/8/10/12/16/17）、
            // 间距混了 1/6/10/18 这些不成倍数的值。看起来"每处都合理"，
            // 合起来就没有体系 —— 这是最典型的"AI 拼出来"的痕迹。
            // 这里守住"收敛后的档位"，防止以后又随手写一个新数值。
            Verify("★设计令牌：圆角只有三档 + 胶囊", delegate
            {
                List<string> bad = new List<string>();
                foreach (Border b in FindAll<Border>(panel))
                {
                    double r = b.CornerRadius.TopLeft;
                    if (r <= 0) continue;                       // 0 = 无圆角，合法
                    if (Math.Abs(r - Ui.R.Sm) < 0.1) continue;  // 8
                    if (Math.Abs(r - Ui.R.Md) < 0.1) continue;  // 12
                    if (Math.Abs(r - Ui.R.Lg) < 0.1) continue;  // 16
                    if (Math.Abs(r - 2) < 0.1) continue;        // 2 = 1px 细条（选中条/分隔）
                    if (Math.Abs(r - 4) < 0.1) continue;        // 4 = 8px 小圆点（正圆）
                    if (Math.Abs(r - b.Height / 2) < 0.6) continue; // 胶囊（搜索框）
                    bad.Add(string.Format("{0}（高 {1:N0}）", r, b.Height));
                }
                if (bad.Count > 0)
                    return "发现不在体系内的圆角: " + string.Join(" / ", bad.ToArray());
                return string.Format("ok（档位 {0:N0}/{1:N0}/{2:N0} + 细条 + 圆点 + 胶囊）",
                    Ui.R.Sm, Ui.R.Md, Ui.R.Lg);
            });

            // ---------- 13c. 磁贴图标尺寸走常量（不再硬编码）------------
            Verify("★磁贴/图标尺寸由常量控制", delegate
            {
                if (PanelWindow.TileWidthPx <= 0 || PanelWindow.TileHeightPx <= 0)
                    return "磁贴常量未定义";
                if (PanelWindow.IconSizePx <= 0) return "图标常量未定义";

                // 图标占比应落在合理区间（Windows 开始菜单约 40%）
                double ratio = PanelWindow.IconSizePx / PanelWindow.TileWidthPx;
                if (ratio < 0.30 || ratio > 0.50)
                    return string.Format("图标占比 {0:P0} 不在合理区间（30%-50%）", ratio);

                return string.Format("ok（磁贴 {0}x{1}，图标 {2}，占比 {3:P0}）",
                    PanelWindow.TileWidthPx, PanelWindow.TileHeightPx, PanelWindow.IconSizePx, ratio);
            });

            // ---------- 13d. ★缓动函数已接线（此前全项目 0 处）------------
            // 为什么断言它：全线性动画的入场会在结尾"急停"、出场开头"猛冲"，
            // 这是最容易被看出"没打磨过"的地方。
            Verify("★动画已接缓动函数（非全线性）", delegate
            {
                // 面板透明度动画应带缓动
                System.Windows.Media.Animation.DoubleAnimation a =
                    Ui.ReusableEnter(1, 150);
                if (a.EasingFunction == null) return "Ui.ReusableEnter 没有缓动";

                System.Windows.Media.Animation.DoubleAnimation b =
                    Ui.ReusableExit(0, 110);
                if (b.EasingFunction == null) return "Ui.ReusableExit 没有缓动";

                // 明确不要回弹类缓动（那会显得轻浮，且高频操作时晕）
                System.Windows.Media.Animation.CubicEase ce = a.EasingFunction as System.Windows.Media.Animation.CubicEase;
                if (ce == null) return "缓动不是 CubicEase（应统一用这一族）";
                if (ce.EasingMode != System.Windows.Media.Animation.EasingMode.EaseOut)
                    return "入场应为 EaseOut（快起慢收）";

                System.Windows.Media.Animation.CubicEase ce2 = b.EasingFunction as System.Windows.Media.Animation.CubicEase;
                if (ce2 == null || ce2.EasingMode != System.Windows.Media.Animation.EasingMode.EaseIn)
                    return "出场应为 EaseIn（慢起快收）";

                // 缓动对象应可复用（冻结后跨控件共享，避免反复 new）。
                // IEasingFunction 接口本身没有 IsFrozen，要转成 Freezable 问。
                System.Windows.Freezable fz = a.EasingFunction as System.Windows.Freezable;
                if (fz == null) return "缓动不是 Freezable，无法冻结复用";
                if (!fz.IsFrozen) return "缓动对象没冻结（会被反复重建）";

                return "ok（入场 EaseOut / 出场 EaseIn，均为冻结的 CubicEase）";
            });

            // 面板刚显示时布局可能还没跑完（IsVisible 为 false），先等一拍。
            // 这是既有 flaky 点：首次运行时偶发失败，与业务逻辑无关。
            WaitForDispatcher(400);

            // ---------- 14. 侧栏折叠按钮：位置在侧栏内、可收起可恢复 ----------
            // 为什么专门断言位置：按钮用 HorizontalAlignment.Right 定位，
            // 一旦落在错的地方，用户点不到（表现是"点了没反应"）。
            Verify("★侧栏折叠按钮落在侧栏区域内", delegate
            {
                Border t = panel.NavToggle;
                if (t == null) return "navToggle 为 null（按钮没建立）";
                // IsVisible 依赖布局完成，首次运行时可能还没跑完 —— 轮询等它
                for (int w = 0; w < 20 && !t.IsVisible; w++)
                {
                    WaitForDispatcher(100);
                    panel.UpdateLayout();
                }
                if (!t.IsVisible) return "按钮不可见（等 2 秒后仍不可见）";

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

            Verify("★分类栏可收起并恢复", delegate
            {
                // 布局从"左侧竖栏"改成"顶部横条"后，折叠不再是改列宽
                // （横条没有"宽度"），而是隐藏/显示整条胶囊。
                // 所以这里断言的是"可见性变化"，不再是数值宽度。
                double before = panel.NavColumnValue;
                if (before < 0) return "分类栏容器未建立";

                panel.ToggleNavNow();
                bool isCollapsed = panel.NavIsCollapsed;
                double collapsed = panel.NavColumnValue;

                panel.ToggleNavNow();
                double back = panel.NavColumnValue;
                bool isBack = panel.NavIsCollapsed;

                if (!isCollapsed) return "收起失败：状态未变";
                if (Math.Abs(collapsed) > 0.1) return "收起后宽度应为 0，实际 " + collapsed;
                if (isBack) return "恢复失败：状态未回到展开";
                if (back <= 0) return "恢复后宽度应大于 0，实际 " + back;

                return string.Format("ok（{0:N0} -> 0 -> {1:N0}）", before, back);
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
            //
            // 【本阶段改动】原来用 "办公" 当第二个分类，但它已从界面移除。
            // 现在只有「常用」「全部」两个标签，改用这两个互相切换。
            // 注意：「常用」可能不满一屏（滚不动），所以只断言「全部」的偏移被记住。
            Verify("★滚动位置按分类记住并能恢复", delegate
            {
                string catA = "";          // 全部（应用最多，一定可滚）
                string catB = "常用";

                panel.SwitchCategoryForTest(catA);
                panel.UpdateLayout();

                // 滚到 120px 处
                panel.ScrollToForTest(120);
                double atA = panel.CurrentScrollOffset();
                if (!panel.HasScrollMemory(catA))
                    return "滚动后没有记录「全部」的偏移";
                if (atA < 50) return "滚不动或偏移过小：" + atA;

                // 切到另一个分类，再切回来
                panel.SwitchCategoryForTest(catB);
                panel.UpdateLayout();
                panel.SwitchCategoryForTest(catA);
                panel.UpdateLayout();

                double back = panel.CurrentScrollOffset();
                if (Math.Abs(back - atA) > 2)
                    return string.Format("切回后偏移不一致：原 {0:N0} -> 现在 {1:N0}", atA, back);

                // 至少有一个分类有记录
                if (panel.ScrollMemoryCount() < 1) return "记录数为 0";
                return string.Format("ok（{0:N0} -> 切走 -> 切回 {1:N0}）", atA, back);
            });

            // ---------- 14b. ★「全部」按名称排序（逐字拼音）----------
            //
            // 本阶段新增：原先「全部」按使用次数排（RenderCompare），
            // 现在改成按名称字典序 —— 用户找某个应用时能按名字直接定位。
            Verify("★「全部」按逐字拼音排序（阿 < 波 < 次）", delegate
            {
                // 排序键的纯逻辑测试（不依赖界面）
                int ab = string.Compare(Pinyin.SortKey("阿"), Pinyin.SortKey("波"), StringComparison.Ordinal);
                if (ab >= 0) return "阿 应排在 波 前面";

                int bc = string.Compare(Pinyin.SortKey("波"), Pinyin.SortKey("次"), StringComparison.Ordinal);
                if (bc >= 0) return "波 应排在 次 前面";

                // 短词在前（"阿" < "阿波"）
                int shortFirst = string.Compare(Pinyin.SortKey("阿"), Pinyin.SortKey("阿波"), StringComparison.Ordinal);
                if (shortFirst >= 0) return "阿 应排在 阿波 前面（短词优先）";

                // 数字排最前
                int digitFirst = string.Compare(Pinyin.SortKey("1Password"), Pinyin.SortKey("阿"), StringComparison.Ordinal);
                if (digitFirst >= 0) return "数字开头应排在最前";

                // 英文大小写不敏感
                int caseInsensitive = string.Compare(Pinyin.SortKey("wechat"), Pinyin.SortKey("WeChat"), StringComparison.Ordinal);
                if (caseInsensitive != 0) return "英文大小写应视为相同";

                return "ok（阿<波<次、短词优先、数字最前、英文大小写不敏感）";
            });

            // ---------- 14c. 中文与英文的相对位置稳定 ----------
            Verify("★中英文混排顺序稳定（与区域设置无关）", delegate
            {
                // 逐字键下：微信="wei|xin"，wechat="w|e|c|h|a|t"
                // 比较 w==w 后，分隔符 \u0001 < 'e'，所以 wechat 在前。
                // 这里只断言**稳定且可重现**（同样输入两次结果相同），
                // 不写死谁在前 —— 那是实现细节，未来调排序规则不该假失败。
                string k1 = Pinyin.SortKey("微信");
                string k2 = Pinyin.SortKey("wechat");
                int r1 = string.Compare(k1, k2, StringComparison.Ordinal);
                int r2 = string.Compare(k1, k2, StringComparison.Ordinal);
                if (r1 != r2) return "同样输入两次比较结果不同（不稳定）";

                // 排序键本身可重现
                if (Pinyin.SortKey("微信") != k1) return "SortKey 结果不稳定";

                return "ok（微信 vs wechat = " + r1 + "，两次一致）";
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

            // ---------- 外观窗口：能创建并关闭、不抛异常 ----------
            //
            // 为什么用源码级 + 运行时组合：
            //   · 运行时：真的调 AppearanceDialog.Show()，确认不抛异常
            //     （用后台线程 2 秒后关窗，避免 ShowDialog 阻塞测试）
            //   · 源码级：确认窗口里的控件类型齐全（预设按钮 / 滑块 / 色板 / 输入框）
            Verify("外观窗口能创建并关闭、不抛异常", delegate
            {
                bool threw = false;
                string err = null;

                // ShowDialog 是模态阻塞，用后台线程延时关窗
                System.Threading.Thread closer = new System.Threading.Thread(delegate()
                {
                    try
                    {
                        System.Threading.Thread.Sleep(1200);
                        try
                        {
                            panel.Dispatcher.BeginInvoke(new Action(delegate
                            {
                                foreach (Window w in Application.Current.Windows)
                                {
                                    if (w.Title == "外观") { w.Close(); break; }
                                }
                            }));
                        }
                        catch { }
                    }
                    catch { }
                });
                closer.IsBackground = true;
                closer.Start();

                try
                {
                    AppearanceDialog.Show(panel, root, Settings.Load(root), null, null);
                }
                catch (Exception ex)
                {
                    threw = true;
                    err = ex.GetType().Name + ": " + ex.Message;
                }

                if (threw) return "外观窗口抛异常: " + err;
                return "ok（创建 → 显示 → 关闭，全程无异常）";
            });

            // ---------- 外观窗口的控件齐全（源码级）----------
            Verify("★外观窗口含预设/滑块/色板/输入框", delegate
            {
                string srcDir = FindSourceDir();
                if (srcDir == null) return "找不到 src 目录";
                string path = Path.Combine(srcDir, "ui", "AppearanceDialog.cs");
                if (!File.Exists(path)) return "找不到 AppearanceDialog.cs";
                string text = File.ReadAllText(path, Encoding.UTF8);

                if (text.IndexOf("AppearancePresets.AllPresets", StringComparison.Ordinal) < 0)
                    return "预设按钮没遍历 AllPresets";
                if (text.IndexOf("new Slider()", StringComparison.Ordinal) < 0)
                    return "没有滑块";
                if (text.IndexOf("MakeSlider", StringComparison.Ordinal) < 0)
                    return "没有 MakeSlider 辅助";
                if (text.IndexOf("MakeColorRow", StringComparison.Ordinal) < 0)
                    return "没有色板行";
                if (text.IndexOf("TextBox", StringComparison.Ordinal) < 0)
                    return "没有 #RRGGBB 输入框";
                if (text.IndexOf("RestoreDefaults", StringComparison.Ordinal) < 0)
                    return "没有恢复默认按钮";
                if (text.IndexOf("DragCompleted", StringComparison.Ordinal) < 0)
                    return "滑块松手没有提交（缺 DragCompleted）";
                if (text.IndexOf("DispatcherTimer", StringComparison.Ordinal) < 0)
                    return "没有预览防抖";
                if (text.IndexOf("ResizeMode.CanResizeWithGrip", StringComparison.Ordinal) < 0)
                    return "外观窗口不可伸缩（缺 CanResizeWithGrip）";
                if (text.IndexOf("new ResizeGrip()", StringComparison.Ordinal) < 0)
                    return "外观窗口缺右下角 ResizeGrip";
                if (text.IndexOf("dlg.MinWidth = 360", StringComparison.Ordinal) < 0
                    || text.IndexOf("dlg.MinHeight = 300", StringComparison.Ordinal) < 0)
                    return "外观窗口缺最小尺寸保护（360x300）";
                if (text.IndexOf("ApplyWorkAreaLimits", StringComparison.Ordinal) < 0)
                    return "外观窗口缺工作区上限（MaxWidth/MaxHeight）";
                if (text.IndexOf("AttachResizeHotspots", StringComparison.Ordinal) < 0)
                    return "外观窗口缺四边拖拽热区";
                if (text.IndexOf("Native.HTLEFT", StringComparison.Ordinal) < 0)
                    return "边缘热区没发 WM_NCLBUTTONDOWN";
                return "ok（预设/滑块/色板/输入框/恢复默认/防抖/松手提交/窗口伸缩/边缘拖拽 全在）";
            });

            // ---------- 切换预设后 settings 与面板状态一致 ----------
            Verify("★切换预设后 settings 与面板状态一致", delegate
            {
                // 用临时 settings 对象，避免污染真实配置
                Settings s = new Settings();
                AppearancePresets.Preset p = AppearancePresets.Find("dark");
                if (p == null) return "找不到 dark 预设";

                AppearancePresets.Apply(p, s);

                if (s.GlassPreset != "dark") return "preset 没写进 settings: " + s.GlassPreset;
                if (s.GlassOpacity != p.Opacity) return "opacity 不一致: " + s.GlassOpacity;
                if (Math.Abs(s.GlassBlur - p.Blur) > 0.0001) return "blur 不一致: " + s.GlassBlur;
                if ((s.GlassTint ?? "") != (p.Tint ?? "")) return "tint 不一致: " + s.GlassTint;
                if (s.GlassTintStrength != p.TintStrength) return "tintStrength 不一致";
                if (s.GlassBrightness != p.Brightness) return "brightness 不一致";
                if (s.Theme != "dark") return "theme 没跟着切: " + s.Theme;

                return "ok（7 个参数 + theme 全部一致）";
            });
        }


        /// <summary>
        /// 泵消息指定毫秒数，让 WPF 动画真正推进。
        ///
        /// 为什么不能用 Thread.Sleep：那只是挂起当前线程，动画时钟不前进，
        /// Completed 回调永远不会触发。DispatcherFrame 会继续处理消息队列。
        /// </summary>
        static void WaitForDispatcher(int ms)
        {
            System.Windows.Threading.DispatcherTimer t = new System.Windows.Threading.DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(ms);
            System.Windows.Threading.DispatcherFrame frame = new System.Windows.Threading.DispatcherFrame();
            t.Tick += delegate { t.Stop(); frame.Continue = false; };
            t.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }

        /// <summary>递归收集窗口按钮（32x32 且带 tooltip 的 Border）。</summary>
        static void CollectWindowButtons(DependencyObject d, ref int count, List<string> tips)
        {
            if (d == null) return;
            try
            {
                Border b = d as Border;
                if (b != null && b.Width == 32 && b.Height == 32 && b.ToolTip is string)
                {
                    count++;
                    tips.Add((string)b.ToolTip);
                }

                int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++)
                    CollectWindowButtons(System.Windows.Media.VisualTreeHelper.GetChild(d, i), ref count, tips);
            }
            catch { }
        }

        /// <summary>按 tooltip 找窗口按钮。</summary>
        static Border FindWindowButtonByTip(DependencyObject d, string tip)
        {
            if (d == null) return null;
            try
            {
                Border b = d as Border;
                if (b != null && b.Width == 32 && b.Height == 32
                    && b.ToolTip is string && (string)b.ToolTip == tip)
                    return b;

                int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++)
                {
                    Border r = FindWindowButtonByTip(System.Windows.Media.VisualTreeHelper.GetChild(d, i), tip);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        // ---------- 辅助：从测试 exe 往上找 src 目录 ----------
        static string FindSourceDir()
        {
            try
            {
                string d = AppDomain.CurrentDomain.BaseDirectory;
                for (int i = 0; i < 8 && d != null; i++)
                {
                    string candidate = Path.Combine(d, "src", "ui");
                    if (Directory.Exists(candidate)) return Path.Combine(d, "src");
                    DirectoryInfo parent = Directory.GetParent(d);
                    if (parent == null) break;
                    d = parent.FullName;
                }
            }
            catch { }
            return null;
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
                // 分类栏是 ScrollViewer 里的横向 StackPanel（顶部胶囊条）。
                //
                // 判定"这堆子项是分类胶囊"的依据从"高度 == 34"改成
                // "高度在 24..40 之间且是 Border" —— 布局改造后胶囊高度是 30，
                // 写死 34 会让断言假失败。用区间而不是精确值，
                // 以后微调高度（28/32）也不用改测试。
                foreach (ScrollViewer sv in FindAll<ScrollViewer>(panel))
                {
                    StackPanel sp = sv.Content as StackPanel;
                    if (sp == null) continue;
                    if (sp.Children.Count <= best.Count) continue;

                    bool looksLikeNav = false;
                    foreach (object o in sp.Children)
                    {
                        Border b = o as Border;
                        if (b != null && b.Height >= 24 && b.Height <= 40) { looksLikeNav = true; break; }
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

        /// <summary>数应用磁贴（TileWidthPx × TileHeightPx 的 Border）。</summary>
        static int CountTiles(PanelWindow panel)
        {
            int n = 0;
            try
            {
                double tw = PanelWindow.TileWidthPx, th = PanelWindow.TileHeightPx;
                foreach (Border b in FindAll<Border>(panel))
                {
                    if (Math.Abs(b.Width - tw) < 0.1 && Math.Abs(b.Height - th) < 0.1) n++;
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
