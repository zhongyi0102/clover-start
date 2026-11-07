using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 「常用 / 全部」页磁贴点击路径判定测试。
    ///
    /// **本次改动**：磁贴从「单击启动」改成「双击启动」。
    ///   旧实现 `MouseLeftButtonUp` 里只要不是拖动就无条件 `Launch(a)`，
    ///   于是用户想拖动排序时，手指一按程序就开了，排序根本做不成。
    ///
    /// **怎么测**：
    ///   1. 穷举 `TileClickAction.Resolve` 的 24 种组合（拖动 × 连击数）
    ///   2. 静态检查生产磁贴真的调了这个判定、且不再无条件 Launch
    ///
    /// **纯逻辑，不启动 WPF、不启动任何程序。**
    /// </summary>
    static class TileClickActionTest
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
            Console.WriteLine("=== 磁贴点击路径判定测试（双击启动）===");
            Console.WriteLine();

            try
            {
                TestDoubleClickLaunches();
                TestSingleClickSelectsOnly();
                TestDragNeverLaunches();
                TestOtherClicks();
                TestMatrix();
                TestProductionWiring();
            }
            catch (Exception ex)
            {
                Check(false, "测试不抛异常", ex.GetType().Name + ": " + ex.Message);
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 双击启动（本次改动的核心）----------
        static void TestDoubleClickLaunches()
        {
            Console.WriteLine("--- 1. 双击启动 ---");

            Check(TileClickAction.Resolve(false, 2) == TileClickAction.Action.Launch,
                "★没拖动 + 双击 → 启动");

            Check(TileClickAction.Resolve(false, 2) != TileClickAction.Action.Select,
                "★双击不再只选中（否则双击等于没反应）");

            Console.WriteLine();
        }

        // ---------- 2. 单击只选中 ----------
        static void TestSingleClickSelectsOnly()
        {
            Console.WriteLine("--- 2. 单击只选中，不启动 ---");

            Check(TileClickAction.Resolve(false, 1) == TileClickAction.Action.Select,
                "★单击 → 只选中（旧实现这里是 Launch）");

            Check(TileClickAction.Resolve(false, 1) != TileClickAction.Action.Launch,
                "★单击绝不启动（拖动排序不再被误触）");

            // 选中态必须留在按下时做，否则单击完键盘方向键没有起点。
            // 这条约束在 UI 侧（SelectTile 在 MouseLeftButtonDown 里），
            // 由 TestProductionWiring 静态断言。
            Console.WriteLine();
        }

        // ---------- 3. 拖动优先于点击 ----------
        static void TestDragNeverLaunches()
        {
            Console.WriteLine("--- 3. 拖动过就不启动 ---");

            Check(TileClickAction.Resolve(true, 1) == TileClickAction.Action.Drop,
                "★拖动 + 单击计数 → 提交排序");

            // 关键回归：拖得慢时 WPF 会把两次按下算进系统双击间隔，
            // 松手报 ClickCount=2。此时若先判点击就会"拖完顺手启动"。
            Check(TileClickAction.Resolve(true, 2) == TileClickAction.Action.Drop,
                "★拖动 + ClickCount=2 → 仍是排序，不启动（关键回归）");

            Check(TileClickAction.Resolve(true, 3) == TileClickAction.Action.Drop,
                "★拖动 + 三击 → 仍是排序");

            Console.WriteLine();
        }

        // ---------- 4. 三击 / 异常值 ----------
        static void TestOtherClicks()
        {
            Console.WriteLine("--- 4. 三击与异常 ClickCount ---");

            Check(TileClickAction.Resolve(false, 3) == TileClickAction.Action.Select,
                "★三击不启动（避免手抖开出两个窗口）");

            Check(TileClickAction.Resolve(false, 0) == TileClickAction.Action.Select,
                "ClickCount=0 → 只选中");
            Check(TileClickAction.Resolve(false, -1) == TileClickAction.Action.Select,
                "ClickCount=-1 → 只选中");
            Check(TileClickAction.Resolve(false, 99) == TileClickAction.Action.Select,
                "ClickCount=99 → 只选中");

            Console.WriteLine();
        }

        // ---------- 5. 全矩阵 ----------
        static void TestMatrix()
        {
            Console.WriteLine("--- 5. 24 种组合矩阵 ---");

            bool ok = true;
            int launches = 0;
            string firstBad = "";

            for (int d = 0; d < 2; d++)
            {
                for (int c = 0; c < 12; c++)
                {
                    bool dragged = d == 1;
                    TileClickAction.Action act = TileClickAction.Resolve(dragged, c);

                    TileClickAction.Action expect;
                    if (dragged) expect = TileClickAction.Action.Drop;
                    else if (c == 2) expect = TileClickAction.Action.Launch;
                    else expect = TileClickAction.Action.Select;

                    if (act != expect)
                    {
                        ok = false;
                        if (firstBad.Length == 0)
                            firstBad = "dragged=" + dragged + " click=" + c
                                + " got=" + act + " want=" + expect;
                    }
                    if (act == TileClickAction.Action.Launch) launches++;
                }
            }

            Check(ok, "★24 种组合全部符合预期", firstBad);
            // 12 种不拖动 × 只有 click=2 启动 → 恰好 1 种
            Check(launches == 1, "★24 种组合里只有「不拖动 + 双击」会启动",
                "启动组合数=" + launches);

            Console.WriteLine();
        }

        // ---------- 6. 生产代码真的接上了 ----------
        static void TestProductionWiring()
        {
            Console.WriteLine("--- 6. 生产磁贴接线 ---");

            string src = ReadSource();
            if (src == null)
            {
                Console.WriteLine("SKIP  找不到 src/ui/PanelWindow.Nav.cs，跳过静态检查");
                Console.WriteLine();
                return;
            }

            int start = src.IndexOf("Border MakeTile(AppItem a)");
            int end = start < 0 ? -1 : src.IndexOf("void BeginDragVisual", start);
            string body = start >= 0 && end > start ? src.Substring(start, end - start) : "";

            Check(body.Length > 0, "找到 MakeTile 函数体");

            Check(body.Contains("TileClickAction.Resolve(tileDrag, downClickCount)"),
                "★MakeTile 松手时调用 TileClickAction.Resolve");

            Check(body.Contains("TileClickAction.Action.Launch")
                && body.Contains("Launch(a)"),
                "★双击分支调用 Launch(a)");

            Check(body.Contains("TileClickAction.Action.Drop")
                && body.Contains("DropDragGhost(a, target)"),
                "★拖动分支仍走 DropDragGhost（排序没被改坏）");

            Check(body.Contains("downClickCount = e.ClickCount"),
                "★按下时记录 ClickCount 供松手判定");

            Check(body.Contains("SelectTile(renderedApps.IndexOf(a), false)"),
                "★选中仍在按下时做（单击后键盘方向键有起点）");

            Check(body.Contains("双击启动"),
                "★ToolTip 写明「双击启动」（否则单击没反应像坏了）");

            // 旧实现是 else { Launch(a); } —— 只要不是拖动就启动。
            // 这条断言防止有人把「无条件启动」改回来。
            Check(!body.Contains("else\n                {\n                    Launch(a);"),
                "★不再有「非拖动就启动」的无条件分支");

            Console.WriteLine();
        }

        static string ReadSource()
        {
            // 先看环境变量（测试产物可能在 temp，不在仓库树里），再看可执行文件向上找
            List<string> starts = new List<string>();
            try
            {
                string env = Environment.GetEnvironmentVariable("CLOVER_REPO_ROOT");
                if (!string.IsNullOrEmpty(env)) starts.Add(env);
            }
            catch { }
            try { starts.Add(Directory.GetCurrentDirectory()); } catch { }
            try { starts.Add(AppDomain.CurrentDomain.BaseDirectory); } catch { }

            foreach (string start in starts)
            {
                try
                {
                    string d = start;
                    for (int i = 0; i < 8 && d != null; i++)
                    {
                        string p = Path.Combine(d, "src", "ui", "PanelWindow.Nav.cs");
                        if (File.Exists(p)) return File.ReadAllText(p);
                        DirectoryInfo parent = Directory.GetParent(d);
                        if (parent == null) break;
                        d = parent.FullName;
                    }
                }
                catch { }
            }
            return null;
        }
    }
}
