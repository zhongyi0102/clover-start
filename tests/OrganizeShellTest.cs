using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Clover
{
    /// <summary>
    /// 「整理与隐藏」窗口外壳完整性回归测试。
    ///
    /// ── 为什么需要它 ──
    ///
    /// 曾经出过这个 bug：`DesktopCleanPage.BuildUi()` 末尾有一行
    /// `dlg.Content = sv;`，把宿主窗口的 Content 从外壳 Grid
    /// （标签栏 + pageHost）换成了自己的 ScrollViewer ——
    /// 结果一点「桌面快捷方式」标签，**整个窗口内容全没了**，
    /// 而且不抛任何异常、日志里也没有痕迹。
    ///
    /// 这类错误靠肉眼只能发现"点了没反应"，很难定位。用断言钉死：
    /// 无论怎么切标签，自绘标题栏下面的页面外壳 Grid 都必须保留，
    /// pageHost 必须始终挂在视觉树上。
    ///
    /// ── 测什么 ──
    ///
    ///   · 两种入口（Desktop / Hidden）分别构造
    ///   · 每个入口下：初始状态、切到另一标签、来回 5 次
    ///   · 每次都检查：标题栏外壳完整、pageHost 在树上、页面有内容
    ///
    /// ── 不测什么 ──
    ///
    /// 不显示窗口（ShowDialog 会阻塞），只构造 + 调 SwitchTab。
    /// 不碰桌面清理和隐藏/恢复的底层逻辑。
    /// </summary>
    static class OrganizeShellTest
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
            Console.WriteLine("=== 整理与隐藏 外壳完整性测试 ===");
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

                // 两种入口分别测
                TestEntry(root, cfg, OrganizeDialog.Tab.Desktop, "Desktop");
                TestEntry(root, cfg, OrganizeDialog.Tab.Hidden, "Hidden");
            }
            catch (Exception ex)
            {
                Check(false, "测试不抛异常", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { app.Shutdown(); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ========== 单个入口的完整流程 ==========

        static void TestEntry(string root, Settings cfg, OrganizeDialog.Tab initial, string label)
        {
            Console.WriteLine("--- 入口：" + label + " ---");

            OrganizeDialog dlg = null;
            try
            {
                dlg = new OrganizeDialog(null, root, cfg, null, null, null, initial);
            }
            catch (Exception ex)
            {
                Check(false, label + "：构造不抛异常", ex.Message);
                Console.WriteLine();
                return;
            }

            // ---- 初始状态 ----
            Check(dlg.TestCurrentTab == initial,
                label + "：初始标签 = " + label,
                dlg.TestCurrentTab.ToString());

            Check(dlg.TestShellIntact(),
                "★" + label + "：构造后外壳 Grid 完整");

            Check(dlg.TestPageHostInTree(),
                "★" + label + "：构造后 pageHost 在视觉树上");

            Check(dlg.TestPageHasContent(),
                label + "：构造后页面有内容");

            List<string> captionActions = dlg.TestCaptionActions();
            Check(captionActions.Count == 2,
                "★" + label + "：标题栏只有两个动作",
                string.Join(" / ", captionActions.ToArray()));
            bool hasMaximizeAction = captionActions.Contains("最大化窗口")
                || captionActions.Contains("还原窗口");
            Check(hasMaximizeAction && captionActions.Contains("关闭窗口"),
                "★" + label + "：标题栏保留最大化/还原与关闭",
                string.Join(" / ", captionActions.ToArray()));
            Check(!captionActions.Contains("最小化窗口"),
                "★★" + label + "：标题栏没有最小化按钮");

            // ---- 切到另一个标签 ----
            OrganizeDialog.Tab other = (initial == OrganizeDialog.Tab.Desktop)
                ? OrganizeDialog.Tab.Hidden
                : OrganizeDialog.Tab.Desktop;

            dlg.SwitchTab(other);
            Check(dlg.TestShellIntact(),
                "★" + label + "：切到 " + other + " 后外壳完整");
            Check(dlg.TestPageHostInTree(),
                "★" + label + "：切到 " + other + " 后 pageHost 在树上");
            Check(dlg.TestPageHasContent(),
                label + "：切到 " + other + " 后有内容");

            // ---- 切回初始标签（这是原 bug 的触发点）----
            dlg.SwitchTab(initial);
            Check(dlg.TestShellIntact(),
                "★★" + label + "：切回 " + initial + " 后外壳**仍**完整（原 bug 触发点）");
            Check(dlg.TestPageHostInTree(),
                "★★" + label + "：切回 " + initial + " 后 pageHost 仍在树上");
            Check(dlg.TestPageHasContent(),
                label + "：切回 " + initial + " 后有内容");

            // ---- 来回 5 次 ----
            bool allOk = true;
            for (int i = 0; i < 5; i++)
            {
                dlg.SwitchTab(OrganizeDialog.Tab.Hidden);
                if (!dlg.TestShellIntact() || !dlg.TestPageHostInTree()) { allOk = false; break; }

                dlg.SwitchTab(OrganizeDialog.Tab.Desktop);
                if (!dlg.TestShellIntact() || !dlg.TestPageHostInTree()) { allOk = false; break; }
            }
            Check(allOk, "★★" + label + "：来回切 5 次后外壳仍完整");

            // 最终确认一次详细状态
            Check(dlg.TestShellIntact(), label + "：5 次后最终外壳检查");
            Check(dlg.TestPageHostInTree(), label + "：5 次后 pageHost 检查");

            // 打印当前 dlg.Content 类型，便于人工核对
            try
            {
                Window w = dlg.TestWindow;
                string t = w == null || w.Content == null ? "(null)" : w.Content.GetType().Name;
                Console.WriteLine("  dlg.Content = " + t
                    + "（应为标题栏 Border；若是 ScrollViewer 说明又被页面覆盖了）");
            }
            catch { }

            Console.WriteLine();
        }

        // ========== 辅助 ==========

        static string FindRepoRoot()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                for (int i = 0; i < 8 && dir != null; i++)
                {
                    if (Directory.Exists(Path.Combine(dir, "config"))
                        && File.Exists(Path.Combine(dir, "build.ps1")))
                        return dir;
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return null;
        }
    }
}
