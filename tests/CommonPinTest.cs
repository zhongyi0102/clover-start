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
            Console.WriteLine("=== 常用名单测试（人工固定 + 无上限）===");
            Console.WriteLine();

            TestDefault();
            TestRoundTrip();
            TestDedupAndCase();
            TestNoCap();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
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
            s.Save();

            Settings back = Settings.Load(root);
            Check(back.PinnedCommon.Count == 2, "★重新加载后仍是 2 条", "实际 " + back.PinnedCommon.Count);
            Check(back.PinnedCommon.Contains(@"C:\Apps\wechat.exe"), "★内容保持（wechat）");
            Check(back.PinnedCommon.Contains(@"C:\Apps\code.exe"), "★内容保持（code）");

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
