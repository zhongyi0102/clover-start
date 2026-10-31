using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Clover
{
    /// <summary>
    /// 右键菜单结构测试。
    ///
    /// **为什么需要它**：菜单是纯代码构建的（`TriggerWindow.ShowMenu`），
    /// 没有 XAML 可以静态检查。改了分组很容易漏项或错位，
    /// 而这种错误只有用户点到才发现。
    ///
    /// **怎么测**：真建一个 `TriggerWindow`，用反射调 `ShowMenu()`，
    /// 然后从 `ContextMenu` 里读顶层项和子菜单结构。
    ///
    /// **不测什么**：不点菜单项（那会真的隐藏任务栏、改开机自启）。
    /// 只验证结构：数量、顺序、文字、子菜单归属、分隔线。
    /// </summary>
    static class MenuStructureTest
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

        [STAThread]
        static void Main()
        {
            Console.WriteLine("=== 右键菜单结构测试 ===");
            Console.WriteLine();

            string root = FindRepoRoot();
            if (root == null)
            {
                Console.WriteLine("  跳过：找不到仓库根（需要 config 目录）");
                Environment.Exit(0);
            }

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            try
            {
                Settings cfg = Settings.Load(root);
                TriggerWindow tw = new TriggerWindow(root, cfg);
                ContextMenu cm = BuildMenu(tw);

                if (cm == null)
                {
                    Check(false, "能构建菜单", "ShowMenu 未产生 ContextMenu");
                }
                else
                {
                    TestTopLevelCount(cm);
                    TestTopLevelOrder(cm);
                    TestSeparators(cm);
                    TestAppearanceDirect(cm);
                    TestSubmenuContents(cm);
                    TestNoDuplicates(cm);
                    TestStateBindings(cm);
                }
            }
            catch (Exception ex)
            {
                Check(false, "构建菜单不抛异常", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { app.Shutdown(); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 顶层功能项恰好 8 个 ----------
        static void TestTopLevelCount(ContextMenu cm)
        {
            Console.WriteLine("--- 1. 顶层功能项恰好 8 个 ---");

            int funcs = 0, seps = 0;
            foreach (object o in cm.Items)
            {
                if (o is Separator) seps++;
                else if (o is MenuItem) funcs++;
            }

        Check(funcs == 9, "★顶层功能项 = 9（新增「桌面快捷方式…」）", "实际 " + funcs);
            Check(seps == 3, "分隔线 = 3", "实际 " + seps);
            Console.WriteLine();
        }

        // ---------- 2. 顺序 ----------
        static void TestTopLevelOrder(ContextMenu cm)
        {
            Console.WriteLine("--- 2. 顶层顺序 ---");

            List<string> names = new List<string>();
            foreach (object o in cm.Items)
            {
                MenuItem mi = o as MenuItem;
                if (mi != null) names.Add(mi.Header as string ?? "");
            }

        string[] expect = { "打开面板", "重新扫描应用", "外观…", "桌面快捷方式…", "主题", "任务栏", "设置", "维护与诊断", "退出" };
            Check(names.Count == expect.Length, "顶层项数一致", names.Count + " vs " + expect.Length);

            for (int i = 0; i < Math.Min(names.Count, expect.Length); i++)
            {
                Check(names[i] == expect[i], "第 " + (i + 1) + " 项 = " + expect[i],
                    "实际 " + names[i]);
            }
            Console.WriteLine();
        }

        // ---------- 3. 分隔线位置 ----------
        static void TestSeparators(ContextMenu cm)
        {
            Console.WriteLine("--- 3. 分隔线位置 ---");

            List<string> seq = new List<string>();
            foreach (object o in cm.Items)
                seq.Add(o is Separator ? "---" : ((MenuItem)o).Header as string ?? "");

            Check(seq.Count > 0 && seq[0] != "---", "★开头不是分隔线",
                seq.Count > 0 ? seq[0] : "(空)");
            Check(seq.Count > 0 && seq[seq.Count - 1] != "---", "★结尾不是分隔线",
                seq.Count > 0 ? seq[seq.Count - 1] : "(空)");

            bool noConsecutive = true;
            for (int i = 1; i < seq.Count; i++)
                if (seq[i] == "---" && seq[i - 1] == "---") { noConsecutive = false; break; }
            Check(noConsecutive, "★没有连续分隔线");

            // 期望位置：第 3、6、10 个元素是分隔线
            List<int> sepPos = new List<int>();
            for (int i = 0; i < seq.Count; i++) if (seq[i] == "---") sepPos.Add(i);
            Check(sepPos.Count == 3, "分隔线 3 条", "实际 " + sepPos.Count);
            if (sepPos.Count == 3)
            {
                Check(sepPos[0] == 2, "第 1 条分隔线在「重新扫描」之后", "索引 " + sepPos[0]);
        Check(sepPos[1] == 6, "第 2 条分隔线在「主题」之后", "索引 " + sepPos[1]);
        Check(sepPos[2] == 10, "第 3 条分隔线在「维护与诊断」之后", "索引 " + sepPos[2]);
            }
            Console.WriteLine();
        }

        // ---------- 4. 外观直达 ----------
        static void TestAppearanceDirect(ContextMenu cm)
        {
            Console.WriteLine("--- 4. 外观直达顶层 ---");

            MenuItem found = null;
            foreach (object o in cm.Items)
            {
                MenuItem mi = o as MenuItem;
                if (mi != null && (mi.Header as string) == "外观…") { found = mi; break; }
            }

            Check(found != null, "★顶层有「外观…」");
            if (found != null)
            {
                Check(found.Items.Count == 0, "「外观…」不是子菜单（无子项）",
                    Convert.ToString(found.Items.Count));
                Check(found.ToolTip != null && (found.ToolTip as string).Length > 0,
                    "「外观…」有 tooltip");
            }
            Console.WriteLine();
        }

        // ---------- 5. 子菜单内容 ----------
        static void TestSubmenuContents(ContextMenu cm)
        {
            Console.WriteLine("--- 5. 子菜单内容 ---");

            // 主题 ▸ 三项
            MenuItem theme = FindTop(cm, "主题");
            Check(theme != null, "有「主题」子菜单");
            if (theme != null)
            {
                List<string> items = HeadersOf(theme);
                Check(items.Count == 3, "主题有 3 项", Convert.ToString(items.Count));
                Check(items.Contains("跟随系统"), "含「跟随系统」");
                Check(items.Contains("浅色"), "含「浅色」");
                Check(items.Contains("深色"), "含「深色」");
            }

            // 任务栏 ▸ 三项
            MenuItem taskbar = FindTop(cm, "任务栏");
            Check(taskbar != null, "有「任务栏」子菜单");
            if (taskbar != null)
            {
                List<string> items = HeadersOf(taskbar);
                Check(items.Count == 3, "任务栏有 3 项", Convert.ToString(items.Count));
                Check(items.Contains("启用迷你任务栏"), "含「启用迷你任务栏」");
                Check(items.Contains("隐藏系统任务栏"), "含「隐藏系统任务栏」");
                Check(items.Contains("允许隐藏系统任务栏"), "★含「允许隐藏系统任务栏」（总开关，不是重复项）");
            }

            // 设置 ▸ 四项
            MenuItem settings = FindTop(cm, "设置");
            Check(settings != null, "有「设置」子菜单");
            if (settings != null)
            {
                List<string> items = HeadersOf(settings);
                Check(items.Count == 3, "★设置有 3 项", "实际 " + items.Count + "：" + string.Join(", ", items.ToArray()));
                Check(items.Contains("全屏时隐藏悬浮按钮"), "含「全屏时隐藏悬浮按钮」");
                Check(items.Contains("全屏时鼠标移过去临时显示"), "含「全屏时鼠标移过去临时显示」");
                Check(items.Contains("开机自启"), "含「开机自启」");
            }

            // 维护与诊断 ▸
            MenuItem maintain = FindTop(cm, "维护与诊断");
            Check(maintain != null, "有「维护与诊断」子菜单");
            if (maintain != null)
            {
                List<string> items = HeadersOf(maintain);
                Check(items.Contains("隐藏用不到的"), "含「隐藏用不到的」");
                Check(items.Contains("诊断"), "含「诊断」");
                Check(items.Contains("一键回滚…"), "含「一键回滚…」");

                // 「隐藏用不到的」的子项依赖 panel 实例：
                //   · panel != null → 建 3 类子项（隐藏全部X / 查看 / 取消）
                //   · panel == null → IsEnabled = false，不建子项
                // 本测试没建 PanelWindow（需要完整 config + 扫描），
                // 所以验证「panel 为 null 时正确禁用」这个分支。
                MenuItem hideTools = FindChild(maintain, "隐藏用不到的");
                Check(hideTools != null, "「隐藏用不到的」仍在");
                Check(hideTools != null && !hideTools.IsEnabled,
                    "★panel 为 null 时「隐藏用不到的」被禁用",
                    hideTools == null ? "找不到" : Convert.ToString(hideTools.IsEnabled));
                // 诊断 保持二级结构
                MenuItem diag = FindChild(maintain, "诊断");
                Check(diag != null, "「诊断」仍在");
                if (diag != null)
                {
                    List<string> sub = HeadersOf(diag);
                    Check(sub.Contains("任务栏定位"), "★「任务栏定位」保留");
                    Check(sub.Contains("运行状态"), "★「运行状态」保留");
                }
            }
            Console.WriteLine();
        }

        // ---------- 6. 无重复项 ----------
        static void TestNoDuplicates(ContextMenu cm)
        {
            Console.WriteLine("--- 6. 无重复项 ---");

            HashSet<string> top = new HashSet<string>();
            bool dupTop = false;
            foreach (object o in cm.Items)
            {
                MenuItem mi = o as MenuItem;
                if (mi == null) continue;
                string h = mi.Header as string ?? "";
                if (!top.Add(h)) dupTop = true;
            }
            Check(!dupTop, "★顶层无同名项");

            // 子菜单内也无重复
            foreach (string name in new string[] { "主题", "任务栏", "设置", "维护与诊断" })
            {
                MenuItem mi = FindTop(cm, name);
                if (mi == null) continue;
                HashSet<string> seen = new HashSet<string>();
                bool dup = false;
                foreach (object o in mi.Items)
                {
                    MenuItem c = o as MenuItem;
                    if (c == null) continue;
                    string h = c.Header as string ?? "";
                    if (!seen.Add(h)) dup = true;
                }
                Check(!dup, "「" + name + "」子菜单无重复项");
            }
            Console.WriteLine();
        }

        // ---------- 7. 状态绑定 ----------
        static void TestStateBindings(ContextMenu cm)
        {
            Console.WriteLine("--- 7. 勾选状态绑定 ---");

            // 任务栏三项都应该是 IsCheckable
            MenuItem taskbar = FindTop(cm, "任务栏");
            if (taskbar != null)
            {
                foreach (string h in new string[] { "启用迷你任务栏", "隐藏系统任务栏", "允许隐藏系统任务栏" })
                {
                    MenuItem mi = FindChild(taskbar, h);
                    Check(mi != null && mi.IsCheckable, "「" + h + "」可勾选",
                        mi == null ? "找不到" : Convert.ToString(mi.IsCheckable));
                }
            }

            // 设置里三项可勾选（自动清理/全屏/悬停）+ 开机自启
            MenuItem settings = FindTop(cm, "设置");
            if (settings != null)
            {
                foreach (string h in new string[] { "全屏时隐藏悬浮按钮",
                                            "全屏时鼠标移过去临时显示", "开机自启" })
                {
                    MenuItem mi = FindChild(settings, h);
                    Check(mi != null && mi.IsCheckable, "「" + h + "」可勾选",
                        mi == null ? "找不到" : Convert.ToString(mi.IsCheckable));
                }
            }

            // 主题三项可勾选，且恰好一项被勾
            MenuItem theme = FindTop(cm, "主题");
            if (theme != null)
            {
                int checkedCount = 0;
                foreach (object o in theme.Items)
                {
                    MenuItem c = o as MenuItem;
                    if (c == null) continue;
                    if (c.IsChecked) checkedCount++;
                }
                Check(checkedCount == 1, "★主题恰好一项被勾选", "实际 " + checkedCount);
            }
            Console.WriteLine();
        }

        // ========== 辅助 ==========

        /// <summary>反射调 TriggerWindow.ShowMenu()，拿到它建的 ContextMenu。</summary>
        /// <summary>
        /// 反射调 TriggerWindow.ShowMenu()，然后读它的 internal lastMenu 字段。
        ///
        /// 为什么加 lastMenu：ShowMenu 是 void 且不返回 cm，ContextMenu 也没挂到
        /// 任何可视树属性上，测试拿不到实例。加一个 internal 字段是最小改动
        /// （生产代码不读它，不影响行为）。
        /// </summary>
        static ContextMenu BuildMenu(TriggerWindow tw)
        {
            try
            {
                MethodInfo mi = typeof(TriggerWindow).GetMethod("ShowMenu",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (mi == null) return null;

                // 窗口放到屏幕外，避免菜单真的弹出来打扰
                tw.Left = -30000;
                tw.Top = -30000;
                mi.Invoke(tw, null);

                // 立刻关掉（ShowMenu 里 cm.IsOpen = true）
                FieldInfo f = typeof(TriggerWindow).GetField("lastMenu",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                ContextMenu cm = f == null ? null : f.GetValue(tw) as ContextMenu;
                if (cm != null)
                {
                    try { cm.IsOpen = false; } catch { }
                }
                return cm;
            }
            catch (Exception ex)
            {
                Console.WriteLine("      反射调用失败: " + ex.Message);
                return null;
            }
        }

        static MenuItem FindTop(ContextMenu cm, string header)
        {
            foreach (object o in cm.Items)
            {
                MenuItem mi = o as MenuItem;
                if (mi != null && (mi.Header as string) == header) return mi;
            }
            return null;
        }

        static MenuItem FindChild(MenuItem parent, string header)
        {
            foreach (object o in parent.Items)
            {
                MenuItem mi = o as MenuItem;
                if (mi != null && (mi.Header as string) == header) return mi;
            }
            return null;
        }

        static List<string> HeadersOf(MenuItem mi)
        {
            List<string> list = new List<string>();
            foreach (object o in mi.Items)
            {
                MenuItem c = o as MenuItem;
                if (c != null) list.Add(c.Header as string ?? "");
            }
            return list;
        }

        static string FindRepoRoot()
        {
            try
            {
                string d = AppDomain.CurrentDomain.BaseDirectory;
                for (int i = 0; i < 8 && d != null; i++)
                {
                    if (System.IO.Directory.Exists(System.IO.Path.Combine(d, "src", "ui"))
                        && System.IO.Directory.Exists(System.IO.Path.Combine(d, "config")))
                        return d;
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
