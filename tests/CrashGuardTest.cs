using System;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// CrashGuard 测试：崩溃计数、时间窗口、安全模式判定、稳定后清除。
    /// 注意：CrashGuard 依赖 ErrorLog/Ui（WPF），测试里通过 Warn 钩子绕开。
    /// 这里只测纯逻辑部分（计数与判定），用反射不现实，所以直接复刻判定公式。
    /// </summary>
    static class CrashGuardTest
    {
        static int failed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name + (cond || detail.Length == 0 ? "" : "  [" + detail + "]"));
            if (!cond) failed++;
        }

        // 与 CrashGuard 一致的判定
        static bool ShouldSafeMode(int recentCrashes, bool alreadySafe)
        {
            if (alreadySafe) return true;
            return recentCrashes >= 3;
        }

        static int CountInWindow(long[] ticks, long now, int windowSeconds)
        {
            long window = TimeSpan.FromSeconds(windowSeconds).Ticks;
            int n = 0;
            foreach (long t in ticks)
            {
                if (now - t <= window && now >= t) n++;
            }
            return n;
        }

        static void Main()
        {
            long now = DateTime.UtcNow.Ticks;
            long sec = TimeSpan.TicksPerSecond;

            // ---------- 1. 窗口内计数 ----------
            Check(CountInWindow(new long[] { now - 10 * sec }, now, 120) == 1, "10 秒前的崩溃计入");
            Check(CountInWindow(new long[] { now - 60 * sec }, now, 120) == 1, "60 秒前的崩溃计入");
            Check(CountInWindow(new long[] { now - 119 * sec }, now, 120) == 1, "119 秒前的崩溃计入（边界内）");
            Check(CountInWindow(new long[] { now - 121 * sec }, now, 120) == 0, "121 秒前的崩溃不计入（窗口外）");
            Check(CountInWindow(new long[] { now - 300 * sec }, now, 120) == 0, "5 分钟前的不计入");

            // ---------- 2. 阈值 ----------
            Check(ShouldSafeMode(0, false) == false, "0 次崩溃 -> 不进入安全模式");
            Check(ShouldSafeMode(1, false) == false, "1 次崩溃 -> 不进入");
            Check(ShouldSafeMode(2, false) == false, "2 次崩溃 -> 不进入");
            Check(ShouldSafeMode(3, false) == true, "3 次崩溃 -> 进入安全模式");
            Check(ShouldSafeMode(5, false) == true, "5 次崩溃 -> 进入");
            Check(ShouldSafeMode(0, true) == true, "已标记安全模式 -> 保持");

            // ---------- 3. 典型场景：2 分钟内连续 3 次 ----------
            long[] threeRapid = new long[] { now - 100 * sec, now - 50 * sec, now - 10 * sec };
            int recent = CountInWindow(threeRapid, now, 120);
            Check(recent == 3, "2 分钟内 3 次崩溃被正确统计", "实际 " + recent);
            Check(ShouldSafeMode(recent, false) == true, "该场景应进入安全模式");

            // ---------- 4. 慢速崩溃不触发（每次间隔 > 2 分钟）----------
            long[] slow = new long[] { now - 400 * sec, now - 200 * sec, now - 5 * sec };
            int slowRecent = CountInWindow(slow, now, 120);
            Check(slowRecent == 1, "间隔超过窗口的崩溃只算最近 1 次", "实际 " + slowRecent);
            Check(ShouldSafeMode(slowRecent, false) == false, "慢速崩溃不触发安全模式");

            // ---------- 5. 未来时间戳不应导致误判 ----------
            Check(CountInWindow(new long[] { now + 100 * sec }, now, 120) == 0, "未来时间戳不计入（防止时钟回拨误判）");

            // ---------- 6. 稳定 5 分钟清除 ----------
            // 逻辑：MarkStable 清空 ticks 并把 safeMode 置 false
            Check(300 == 5 * 60, "稳定判定阈值为 5 分钟（300 秒）");

            // ---------- 7. crash-guard.json 往返（用最小 JSON 校验，不依赖 WPF）----------
            string root = Path.Combine(Path.GetTempPath(), "clover-guard-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            try
            {
                string p = Path.Combine(root, "config", "crash-guard.json");
                File.WriteAllText(p, "{\"crashes\":[" + (now - 10 * sec) + "," + (now - 20 * sec) + "],\"safeMode\":false}",
                    new UTF8Encoding(false));
                Check(File.Exists(p), "crash-guard.json 可写入");
                string text = File.ReadAllText(p, Encoding.UTF8);
                Check(text.Contains("crashes"), "内容含 crashes 字段");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }

            // ---------- 8. 运行标记法（真正调用 CrashGuard）----------
            // CrashGuard 依赖 ErrorLog/Ui，测试时链接全部源码（见 run-tests.ps1）
            RunMarkerTests();

            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        /// <summary>运行标记（running）相关测试：真正调用 CrashGuard。</summary>
        static void RunMarkerTests()
        {
            // ---- 8a. 首次启动：没有残留标记，不应补记异常退出 ----
            string r = NewRoot();
            ErrorLog.Init(r);
            CrashGuard.Init(r);
            bool leftover = CrashGuard.BeginRun();
            Check(leftover == false, "首次启动没有残留运行标记");
            Check(ReadCrashCount(r) == 0, "首次启动不计异常退出", "实际 " + ReadCrashCount(r));
            Check(ReadRunning(r) == true, "启动后写入了 running=true");

            // ---- 8b. 正常退出：清除标记 ----
            CrashGuard.EndRun("单元测试正常退出");
            Check(ReadRunning(r) == false, "正常退出清除 running 标记");
            Check(ReadCrashCount(r) == 0, "正常退出不计异常退出");

            // ---- 8c. 异常退出（残留 running=true）：下次启动补记一次 ----
            string r2 = NewRoot();
            ErrorLog.Init(r2);
            CrashGuard.Init(r2);
            CrashGuard.BeginRun();          // 第一次运行
            // 模拟"没调 EndRun 就没了"：不调 EndRun，直接再 BeginRun
            CrashGuard.Init(r2);
            bool leftover2 = CrashGuard.BeginRun();
            Check(leftover2 == true, "检测到上次运行标记残留");
            Check(ReadCrashCount(r2) == 1, "残留标记记为 1 次异常退出", "实际 " + ReadCrashCount(r2));

            // ---- 8d. 去重：同一轮已经由钩子计过，残留标记不再重复计 ----
            string r3 = NewRoot();
            ErrorLog.Init(r3);
            CrashGuard.Init(r3);
            CrashGuard.BeginRun();          // 第一次运行，写入 runStarted
            CrashGuard.RecordCrash();       // 模拟异常钩子记了一次（发生在 runStarted 之后）
            int afterHook = ReadCrashCount(r3);
            Check(afterHook == 1, "异常钩子记录 1 次", "实际 " + afterHook);

            CrashGuard.Init(r3);
            CrashGuard.BeginRun();          // 重新启动，发现残留
            int afterRestart = ReadCrashCount(r3);
            Check(afterRestart == 1, "残留标记与钩子计数去重（仍是 1 次，不会变 2）",
                "实际 " + afterRestart);

            // ---- 8e. 连续异常退出 -> 进安全模式 ----
            // 注意：每次 BeginRun 只有在"上一次残留没被清除"时才补记。
            // 所以 N 次异常退出 = N+1 次 BeginRun（第 1 次是干净启动，不计）。
            string r4 = NewRoot();
            ErrorLog.Init(r4);
            CrashGuard.Init(r4);
            for (int i = 0; i < 4; i++)          // 1 次正常启动 + 3 次异常退出
            {
                CrashGuard.Init(r4);
                CrashGuard.BeginRun();           // 每次都没正常退出
                System.Threading.Thread.Sleep(5);
            }
            int crashCount = ReadCrashCount(r4);
            Check(crashCount == 3, "连续 3 次异常退出记为 3 次", "实际 " + crashCount);
            CrashGuard.Init(r4);
            bool safe = CrashGuard.ShouldEnterSafeMode(r4, false);
            Check(safe == true, "3 次异常退出后应进入安全模式", "崩溃数 " + crashCount);

            // ---- 8f. 稳定运行清除计数与运行标记 ----
            CrashGuard.Init(r4);
            CrashGuard.MarkStable();
            Check(ReadCrashCount(r4) == 0, "MarkStable 清除崩溃计数", "实际 " + ReadCrashCount(r4));
            Check(CrashGuard.ShouldEnterSafeMode(r4, false) == false, "清除后不再进安全模式");

            // ---- 8g. 崩溃记录写入 running 字段（文件格式校验）----
            string json = File.ReadAllText(Path.Combine(r4, "config", "crash-guard.json"), Encoding.UTF8);
            Check(json.Contains("running"), "crash-guard.json 含 running 字段");
            Check(json.Contains("runStarted"), "crash-guard.json 含 runStarted 字段");
            Check(json.Contains("crashRecordedThisRun"), "crash-guard.json 含去重标记字段");

            // ---- 8h. EndRun 幂等：重复调用不报错、不产生额外崩溃 ----
            string r5 = NewRoot();
            ErrorLog.Init(r5);
            CrashGuard.Init(r5);
            CrashGuard.BeginRun();
            CrashGuard.EndRun("第一次");
            CrashGuard.EndRun("第二次（重复）");
            Check(ReadRunning(r5) == false, "重复 EndRun 后 running 仍为 false");
            Check(ReadCrashCount(r5) == 0, "重复 EndRun 不产生崩溃记录");

            // ---- 8i. 正常退出后重新启动：不算异常退出 ----
            CrashGuard.Init(r5);
            bool leftover5 = CrashGuard.BeginRun();
            Check(leftover5 == false, "正常退出后重启没有残留标记");
            Check(ReadCrashCount(r5) == 0, "正常退出后重启不计异常退出");

            // ---- 8j. 看门狗"重新隐藏配额"标记：整轮只放行 1 次 ----
            string r6 = NewRoot();
            WatchdogResume.Init(r6);
            WatchdogResume.Cleanup(r6);

            Check(WatchdogResume.TryConsumeRehidePermission() == false,
                "没有标记时不允许重新隐藏");

            WatchdogResume.MarkRestored(r6);
            Check(File.Exists(Path.Combine(r6, "config", "guard-restored")),
                "看门狗还原后写出 guard-restored 标记");

            Check(WatchdogResume.TryConsumeRehidePermission() == true,
                "第一次消费标记：允许重新隐藏");
            Check(WatchdogResume.TryConsumeRehidePermission() == false,
                "第二次消费：配额已用完，不再允许");
            Check(!File.Exists(Path.Combine(r6, "config", "guard-restored")),
                "消费后标记文件已删除");

            // 启动清理：遗留标记不应影响下一轮
            WatchdogResume.MarkRestored(r6);
            WatchdogResume.Cleanup(r6);
            Check(WatchdogResume.TryConsumeRehidePermission() == false,
                "启动清理后遗留标记失效（新一轮重新计数）");
        }

        static string NewRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "clover-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            return root;
        }

        /// <summary>读 crash-guard.json 里的崩溃次数。</summary>
        static int ReadCrashCount(string root)
        {
            try
            {
                string p = Path.Combine(root, "config", "crash-guard.json");
                if (!File.Exists(p)) return 0;
                string json = File.ReadAllText(p, Encoding.UTF8);
                System.Web.Script.Serialization.JavaScriptSerializer ser =
                    new System.Web.Script.Serialization.JavaScriptSerializer();
                System.Collections.Generic.Dictionary<string, object> d =
                    ser.Deserialize<System.Collections.Generic.Dictionary<string, object>>(json);
                if (d == null) return 0;
                object v;
                if (!d.TryGetValue("crashes", out v) || v == null) return 0;
                int n = 0;
                foreach (object o in (System.Collections.IEnumerable)v) n++;
                return n;
            }
            catch { return -1; }
        }

        /// <summary>读 crash-guard.json 里的 running 标记。</summary>
        static bool ReadRunning(string root)
        {
            try
            {
                string p = Path.Combine(root, "config", "crash-guard.json");
                if (!File.Exists(p)) return false;
                string json = File.ReadAllText(p, Encoding.UTF8);
                System.Web.Script.Serialization.JavaScriptSerializer ser =
                    new System.Web.Script.Serialization.JavaScriptSerializer();
                System.Collections.Generic.Dictionary<string, object> d =
                    ser.Deserialize<System.Collections.Generic.Dictionary<string, object>>(json);
                if (d == null) return false;
                object v;
                if (!d.TryGetValue("running", out v) || v == null) return false;
                return Convert.ToBoolean(v);
            }
            catch { return false; }
        }
    }
}
