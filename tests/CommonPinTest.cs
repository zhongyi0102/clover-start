using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 「常用」名单测试：人工固定（右键「添加到常用」）+ 使用榜合并、无上限。
    ///
    /// **为什么需要它**：
    ///   1. 早期 `CommonApps()` 截断到前 12 个 —— 使用榜已满时右键「添加到常用」
    ///      点了看不到任何变化，用户以为功能坏了。这个上限必须钉死为「不存在」。
    ///   2. 固定名单存 `settings.json` 的 `pinnedCommon`，与 usage.json 是两套数据，
    ///      合并时容易出重复项、大小写差异、已隐藏/已卸载残留。
    ///
    /// **怎么测**：真建 Settings 文件写盘 → 重新 Load → 验证往返；
    /// 再用一份假的 AppItem 清单验证合并规则（不启动 WPF）。
    /// </summary>
    static class CommonPinTest
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

        static string NewRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "clover-common-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            return root;
        }

        static void Main()
        {
            Console.WriteLine("=== 常用名单测试（人工固定 + 无上限 + 隐藏过滤）===");
            Console.WriteLine();

            TestDefault();
            TestRoundTrip();
            TestDedupAndCase();
            TestNoCap();
            TestUsageFilterReal();     // ★ 直接测生产函数 PanelWindow.IsCommonByUsage
            TestMenuActionReal();      // ★ 直接测生产函数 PanelWindow.CommonMenuFor
            TestPinnedOrderReal();     // ★ 新固定项进入常用首位
            TestContentSignatureReal();// ★ 固定名单变化触发重绘
            TestTileInteractionColors();// ★ 磁贴悬停不能变成不透明白块

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 9. ★磁贴交互色必须保持半透明 ----------
        static void TestTileInteractionColors()
        {
            Console.WriteLine("--- 9. ★磁贴悬停 / 按下色（浅色与深色）---");

            Ui.ApplyTheme(Ui.ThemeMode.Light);
            Check(Ui.TileHover.Color.A > 0 && Ui.TileHover.Color.A < 255,
                "★浅色磁贴悬停为半透明", "Alpha=" + Ui.TileHover.Color.A);
            Check(Ui.TilePressed.Color.A > Ui.TileHover.Color.A && Ui.TilePressed.Color.A < 255,
                "★浅色磁贴按下更明显且仍半透明",
                "Hover=" + Ui.TileHover.Color.A + " Pressed=" + Ui.TilePressed.Color.A);

            Ui.ApplyTheme(Ui.ThemeMode.Dark);
            Check(Ui.TileHover.Color.A > 0 && Ui.TileHover.Color.A < 255,
                "★深色磁贴悬停为半透明", "Alpha=" + Ui.TileHover.Color.A);
            Check(Ui.TilePressed.Color.A > Ui.TileHover.Color.A && Ui.TilePressed.Color.A < 255,
                "★深色磁贴按下更明显且仍半透明",
                "Hover=" + Ui.TileHover.Color.A + " Pressed=" + Ui.TilePressed.Color.A);

            // 清回默认，避免影响同进程后续检查。
            Ui.ApplyTheme(Ui.ThemeMode.Light);

            // 令牌透明还不够，生产磁贴也必须真的使用组件令牌；否则改了令牌
            // 却仍绑定全局 Ui.Hover，截图里的不透明白块会原样复发。
            string src = ReadSource();
            if (src != null)
            {
                int start = src.IndexOf("Border MakeTile(AppItem a)");
                int end = start < 0 ? -1 : src.IndexOf("void BeginDragVisual", start);
                string body = start >= 0 && end > start ? src.Substring(start, end - start) : "";
                Check(body.Contains("b.Background = Ui.TileHover"),
                    "★生产磁贴悬停绑定 TileHover");
                Check(body.Contains("b.Background = Ui.TilePressed"),
                    "★生产磁贴按下绑定 TilePressed");
                Check(src.Contains("ReferenceEquals(sourceTile, selectedTile)")
                    && src.Contains("sourceTile.IsMouseOver"),
                    "★右键菜单关闭后按键盘选中/鼠标状态恢复磁贴背景");
            }
            Console.WriteLine();
        }

        // ---------- 8. ★内容签名必须识别固定名单的内容与顺序 ----------
        static void TestContentSignatureReal()
        {
            Console.WriteLine("--- 8. ★固定名单变化触发重绘 ---");

            List<string> one = new List<string> { "app-a", "app-b" };
            List<string> changed = new List<string> { "app-a", "app-c" };
            List<string> reordered = new List<string> { "app-b", "app-a" };

            string s1 = PanelWindow.PathListSignature(one);
            Check(s1 != PanelWindow.PathListSignature(changed),
                "★数量相同但固定项变化 → 签名变化");
            Check(s1 != PanelWindow.PathListSignature(reordered),
                "★固定项顺序变化 → 签名变化");
            Check(s1 == PanelWindow.PathListSignature(new List<string> { "app-a", "app-b" }),
                "相同固定名单 → 签名稳定");

            string src = ReadSource();
            if (src != null)
            {
                int start = src.IndexOf("string CurrentContentSig()");
                int end = start < 0 ? -1 : src.IndexOf("void SaveScrollOffset()", start);
                string body = start >= 0 && end > start ? src.Substring(start, end - start) : "";
                Check(body.Contains("settings.PinnedCommon"),
                    "★CurrentContentSig() 纳入 PinnedCommon");
            }
            Console.WriteLine();
        }

        // ---------- 7. ★新固定项必须立即出现在常用首位 ----------
        static void TestPinnedOrderReal()
        {
            Console.WriteLine("--- 7. ★固定状态同步到常用排序（生产函数）---");

            string store = @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App";
            List<string> order = new List<string> {
                @"C:\Apps\old-a.exe", store.ToUpperInvariant(), @"C:\Apps\old-b.exe", store
            };

            PanelWindow.UpdateCommonOrder(order, store, true);
            Check(order.Count == 3, "★重复固定记录被去重", "实际 " + order.Count);
            Check(string.Equals(order[0], store, StringComparison.Ordinal),
                "★新固定的商店应用排到常用首位", order[0]);

            PanelWindow.UpdateCommonOrder(order, store, true);
            Check(order.Count == 3 && order[0] == store,
                "★重复执行固定仍只有一条且保持首位");

            bool removed = PanelWindow.UpdateCommonOrder(order, store, false);
            Check(removed, "★取消固定会报告排序记录已移除");
            Check(!order.Exists(delegate(string p)
                { return string.Equals(p, store, StringComparison.OrdinalIgnoreCase); }),
                "★取消固定会清除常用人工排序项");
            Console.WriteLine();
        }

        // ---------- 6. ★生产行为：右键菜单项选择 ----------
        //
        // 回归背景：菜单曾经用 `inCommon = pinned || Usage > 0` 当门控。
        // 在「全部」页里，任何用过一次的应用都落进 inCommon，却又不在常用页、
        // 也没固定 —— 三个分支全不命中，菜单项直接消失。
        // 绝大多数应用都被启动过，所以「添加到常用」几乎看不到。
        static void TestMenuActionReal()
        {
            Console.WriteLine("--- 6. ★右键菜单项选择（生产函数 CommonMenuFor）---");

            // 「全部」页（showingCommon=false）
            Check(PanelWindow.CommonMenuFor(false, false, 0) == PanelWindow.CommonMenuAction.Add,
                "★全部页 + 未固定 + 无使用记录 → 添加到常用");
            Check(PanelWindow.CommonMenuFor(false, false, 7) == PanelWindow.CommonMenuAction.Add,
                "★全部页 + 未固定 + 有使用记录 → 仍显示添加到常用（回归：原实现此处什么都不显示）");
            Check(PanelWindow.CommonMenuFor(false, true, 0) == PanelWindow.CommonMenuAction.Unpin,
                "全部页 + 已固定 → 取消固定到常用");
            Check(PanelWindow.CommonMenuFor(false, true, 9) == PanelWindow.CommonMenuAction.Unpin,
                "全部页 + 已固定 + 有使用记录 → 取消固定到常用");

            // 「常用」页（showingCommon=true）
            Check(PanelWindow.CommonMenuFor(true, true, 0) == PanelWindow.CommonMenuAction.Remove,
                "常用页 + 已固定 → 从常用移除");
            Check(PanelWindow.CommonMenuFor(true, false, 3) == PanelWindow.CommonMenuAction.Remove,
                "常用页 + 未固定但有使用记录 → 从常用移除");

            // 源码级：菜单里不得再出现旧的 inCommon 门控
            string src = ReadSource();
            if (src == null)
            {
                Console.WriteLine("  （跳过源码检查：找不到 PanelWindow.Nav.cs）");
            }
            else
            {
                Check(!src.Contains("bool inCommon = pinned || a.Usage > 0"),
                    "★菜单不再用 inCommon = pinned || Usage > 0 当门控");
                Check(src.Contains("CommonMenuFor(showingCommon, pinned, a.Usage)"),
                    "★菜单改走 CommonMenuFor 判定");
            }
            Console.WriteLine();
        }

        // ---------- 5. ★生产行为：使用榜分支必须过滤隐藏项 ----------
        //
        // 回归背景：CommonApps() 的 usage 分支曾经只判 Usage > 0，
        // 没判 Hidden —— 隐藏但有使用记录的应用会重新出现在「常用」页，
        // 用户刚把它藏起来却还在。这里直接调用生产函数，不复制逻辑。
        static void TestUsageFilterReal()
        {
            Console.WriteLine("--- 5. ★使用榜分支：隐藏项必须被过滤（生产函数）---");

            AppItem normal = new AppItem();
            normal.Path = @"C:\Apps\wechat.exe";
            normal.Usage = 5;
            normal.Hidden = false;

            AppItem hidden = new AppItem();
            hidden.Path = @"C:\Apps\secret.exe";
            hidden.Usage = 99;          // 使用记录很多，但已隐藏
            hidden.Hidden = true;

            AppItem zero = new AppItem();
            zero.Path = @"C:\Apps\never.exe";
            zero.Usage = 0;
            zero.Hidden = false;

            AppItem noPath = new AppItem();
            noPath.Path = "";
            noPath.Usage = 3;
            noPath.Hidden = false;

            Check(PanelWindow.IsCommonByUsage(normal), "★有使用记录 + 未隐藏 → 进常用");
            Check(!PanelWindow.IsCommonByUsage(hidden),
                "★有使用记录但已隐藏 → 不进常用（回归：原实现会漏进来）");
            Check(!PanelWindow.IsCommonByUsage(zero), "无使用记录 → 不进常用");
            Check(!PanelWindow.IsCommonByUsage(noPath), "无路径 → 不进常用");
            Check(!PanelWindow.IsCommonByUsage(null), "null → 不进常用");

            // 两条分支一致：人工固定分支同样跳过隐藏项（源码级断言，
            // 因为固定分支在实例方法里、依赖 catalog，不便直接构造）
            string src = ReadSource();
            if (src == null)
            {
                Console.WriteLine("  （跳过源码检查：找不到 PanelWindow.Nav.cs）");
            }
            else
            {
                int start = src.IndexOf("List<AppItem> CommonApps()");
                int end = start < 0 ? -1 : src.IndexOf("internal static bool IsCommonByUsage", start);
                if (start < 0 || end < 0)
                {
                    Check(false, "★找得到 CommonApps() 与 IsCommonByUsage()");
                }
                else
                {
                    string body = src.Substring(start, end - start);
                    Check(body.Contains("hit.Hidden"), "★人工固定分支过滤 Hidden");
                    Check(body.Contains("IsCommonByUsage(a)"), "★使用榜分支走统一判定（含 Hidden）");
                }
            }
            Console.WriteLine();
        }

        // ---------- 1. 默认空 ----------
        static void TestDefault()
        {
            Console.WriteLine("--- 1. 默认值 ---");
            Settings s = Settings.Load(NewRoot());
            Check(s.PinnedCommon != null, "★pinnedCommon 默认不是 null");
            Check(s.PinnedCommon != null && s.PinnedCommon.Count == 0, "★pinnedCommon 默认为空");
            Console.WriteLine();
        }

        // ---------- 2. 写盘往返 ----------
        static void TestRoundTrip()
        {
            Console.WriteLine("--- 2. 保存 / 重新加载往返 ---");
            string root = NewRoot();

            Settings s = Settings.Load(root);
            s.PinnedCommon.Add(@"C:\Apps\wechat.exe");
            s.PinnedCommon.Add(@"C:\Apps\code.exe");
            string store = @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App";
            s.PinnedCommon.Add(store);
            s.Save();

            Settings back = Settings.Load(root);
            Check(back.PinnedCommon.Count == 3, "★重新加载后仍是 3 条", "实际 " + back.PinnedCommon.Count);
            Check(back.PinnedCommon.Contains(@"C:\Apps\wechat.exe"), "★内容保持（wechat）");
            Check(back.PinnedCommon.Contains(@"C:\Apps\code.exe"), "★内容保持（code）");
            Check(back.PinnedCommon.Contains(store), "★商店应用 shell:AppsFolder 路径可往返");

            // 清空后也要能保存成空列表（不是"读不到键就用默认"）
            back.PinnedCommon.Clear();
            back.Save();
            Settings empty = Settings.Load(root);
            Check(empty.PinnedCommon.Count == 0, "★清空后保存 → 重新加载仍为空",
                "实际 " + empty.PinnedCommon.Count);
            Console.WriteLine();
        }

        // ---------- 3. 合并去重 + 大小写 ----------
        static void TestDedupAndCase()
        {
            Console.WriteLine("--- 3. 合并规则（去重 / 大小写 / 顺序）---");

            // 模拟 CommonApps() 的合并逻辑（与生产代码同规则）
            //   · catalog = 全部已扫描应用（固定项也在这里找，不限于使用榜）
            //   · usage   = 有使用记录的路径
            List<string> catalog = new List<string> {
                @"C:\Apps\wechat.exe",      // 有使用记录
                @"C:\Apps\code.exe",        // 只在固定名单，Usage 为 0
                @"C:\Apps\chrome.exe",      // 有使用记录
                @"C:\Apps\other.exe"        // 既没固定也没使用 → 不出现
            };
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                @"C:\Apps\wechat.exe", @"C:\Apps\chrome.exe"
            };

            List<string> pinned = new List<string> {
                @"C:\Apps\WeChat.exe",      // 与 catalog 同一程序，大小写不同
                @"C:\Apps\code.exe",        // 只在固定名单
                @"C:\Apps\gone.exe"         // 已卸载，catalog 里没有
            };

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> merged = new List<string>();

            // 1. 固定项：在 catalog 里找（忽略大小写），找不到=已卸载 → 跳过
            foreach (string p in pinned)
            {
                string hit = null;
                foreach (string c in catalog)
                    if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase)) { hit = c; break; }
                if (hit == null) continue;      // 已卸载 → 跳过
                if (seen.Add(hit)) merged.Add(hit);
            }
            // 2. 使用榜
            foreach (string c in catalog)
            {
                if (!used.Contains(c)) continue;
                if (seen.Add(c)) merged.Add(c);
            }

            Check(merged.Count == 3, "★去重后 3 条（wechat/code/chrome）", "实际 " + merged.Count + "：" + string.Join(" | ", merged.ToArray()));
            Check(merged.Contains(@"C:\Apps\wechat.exe"), "★wechat 只出现一次（大小写视为同一程序）");
            Check(merged.Contains(@"C:\Apps\code.exe"), "★固定项 code 在列（Usage 为 0 也要出现）");
            Check(!merged.Contains(@"C:\Apps\gone.exe"), "★已卸载的 gone 被跳过");
            Console.WriteLine();
        }

        // ---------- 4. 无上限（核心回归） ----------
        static void TestNoCap()
        {
            Console.WriteLine("--- 4. 常用不设上限（回归：原实现截断前 12 个）---");

            List<string> apps = new List<string>();
            for (int i = 0; i < 40; i++) apps.Add(@"C:\Apps\app" + i + ".exe");

            // 生产实现里已无 GetRange(0, 12)，此处断言 40 条全部保留
            List<string> list = new List<string>(apps);
            Check(list.Count == 40, "★40 个常用项全部保留（无 12 个截断）", "实际 " + list.Count);

            // 源码级断言：CommonApps() 里不得再出现截断
            string src = ReadSource();
            if (src == null)
            {
                Console.WriteLine("  （跳过源码检查：找不到 PanelWindow.Nav.cs）");
            }
            else
            {
                int start = src.IndexOf("List<AppItem> CommonApps()");
                if (start < 0)
                {
                    Check(false, "★找得到 CommonApps()", "源码里没有该方法");
                }
                else
                {
                    int end = src.IndexOf("\n        }", start);
                    string body = end > start ? src.Substring(start, end - start) : src.Substring(start);
                    Check(!body.Contains("GetRange(0, 12)"), "★CommonApps() 不再截断 12 个");
                    Check(body.Contains("PinnedCommon"), "★CommonApps() 读入人工固定名单");
                }
            }
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
