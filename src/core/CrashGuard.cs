using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// 崩溃循环保护。
    ///
    /// 规则：2 分钟内连续异常退出 3 次 → 下次启动进入安全模式。
    /// 安全模式下不隐藏任务栏、不启用迷你任务栏、不移动桌面快捷方式、不做全屏检测，
    /// 只显示面板和按钮，让用户还能把它关掉 / 修好。
    ///
    /// 稳定运行 5 分钟后清除计数（由 Program 调 MarkStable）。
    /// 计数存在 config\crash-guard.json。
    /// </summary>
    static class CrashGuard
    {
        /// <summary>判定为“连续崩溃”的时间窗口。</summary>
        const int WindowSeconds = 120;

        /// <summary>窗口内达到这个次数就进安全模式。</summary>
        const int CrashThreshold = 3;

        /// <summary>稳定运行多久算“没事了”，可以清计数。</summary>
        public const int StableSeconds = 300;

        class Data
        {
            public List<long> Ticks = new List<long>();  // 最近几次异常退出时间（UTC ticks）
            public bool SafeMode;                       // 已进入安全模式
            public bool Running;                        // 运行标记：true 表示上次没正常退出
            public long RunStarted;                     // 本次运行的开始时间（UTC ticks）
            /// <summary>
            /// 本轮运行是否已经由异常钩子记过一次崩溃。
            /// 用独立布尔字段而不是比较时间戳——时间戳比较会在下一次运行时误判。
            /// </summary>
            public bool CrashRecordedThisRun;
        }

        static readonly object gate = new object();
        static string path;

        public static void Init(string baseDir)
        {
            try { path = Path.Combine(baseDir, "config", "crash-guard.json"); } catch { }
        }

        static Data LoadRaw()
        {
            Data d = new Data();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return d;
                string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return d;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> m = ser.Deserialize<Dictionary<string, object>>(json);
                if (m == null) return d;

                object v;
                if (m.TryGetValue("crashes", out v) && v != null)
                {
                    foreach (object o in JsonIO.AsArray(v))
                    {
                        try { d.Ticks.Add(Convert.ToInt64(o)); } catch { }
                    }
                }
                if (m.TryGetValue("safeMode", out v) && v != null) d.SafeMode = Convert.ToBoolean(v);
                if (m.TryGetValue("running", out v) && v != null) d.Running = Convert.ToBoolean(v);
                if (m.TryGetValue("crashRecordedThisRun", out v) && v != null) d.CrashRecordedThisRun = Convert.ToBoolean(v);
                if (m.TryGetValue("runStarted", out v) && v != null)
                {
                    try { d.RunStarted = Convert.ToInt64(v); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取 crash-guard.json 失败（按空处理）: " + ex.Message);
            }
            return d;
        }

        static void SaveRaw(Data d)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                Dictionary<string, object> m = new Dictionary<string, object>();
                m["crashes"] = d.Ticks;
                m["safeMode"] = d.SafeMode;
                m["running"] = d.Running;
                m["runStarted"] = d.RunStarted;
                m["crashRecordedThisRun"] = d.CrashRecordedThisRun;
                m["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(m);

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                Log.Warn("写 crash-guard.json 失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 启动时调用（**在 ShouldEnterSafeMode 之前**）：处理上次的运行标记。
        ///
        /// 规则：
        ///   - 如果上次 running=true 且没被清除 → 记一次异常退出
        ///   - 本轮已经通过异常钩子计过数（同一 runStarted）→ 不重复计（去重）
        ///   - 然后把 running 置 true、记录本次 runStarted，开始新一轮
        ///
        /// 返回：本次是否因残留标记补记了一次异常退出。
        /// </summary>
        public static bool BeginRun()
        {
            try
            {
                lock (gate)
                {
                    Data d = LoadRaw();
                    bool leftover = d.Running;
                    long now = DateTime.UtcNow.Ticks;

                    if (leftover)
                    {
                        // 去重：如果上一轮运行期间异常钩子已经记过一次崩溃，
                        // 这次残留标记就不再重复计数。用独立布尔标记，不用时间戳比较
                        // （时间戳会在每次 BeginRun 更新 RunStarted 后失效）。
                        if (!d.CrashRecordedThisRun)
                        {
                            d.Ticks = TrimWindow(d.Ticks, now);
                            d.Ticks.Add(now);
                            if (d.Ticks.Count >= CrashThreshold) d.SafeMode = true;
                            Log.Warn("检测到上次未正常退出（运行标记残留），已记为一次异常退出");
                        }
                        else
                        {
                            Log.Info("上次运行标记残留，但本轮崩溃已由异常钩子记录，跳过重复计数");
                        }
                    }

                    // 开始新一轮：清掉"本轮已记录"标记，写入运行标记
                    d.Running = true;
                    d.RunStarted = now;
                    d.CrashRecordedThisRun = false;
                    SaveRaw(d);
                    return leftover;
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("处理运行标记失败: " + ex.Message); } catch { }
                return false;
            }
        }

        /// <summary>
        /// 正常退出时调用：清除运行标记，表示这次是干净退出。
        /// 用户点退出、会话结束、Application.Exit 都会走到这里。
        /// </summary>
        public static void EndRun(string reason)
        {
            try
            {
                lock (gate)
                {
                    Data d = LoadRaw();
                    if (!d.Running && d.RunStarted == 0) return;
                    d.Running = false;
                    d.RunStarted = 0;
                    d.CrashRecordedThisRun = false;
                    SaveRaw(d);
                    Log.Info("清除运行标记（" + reason + "）");
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("清除运行标记失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>只保留时间窗口内的记录，避免文件无限增长。</summary>
        static List<long> TrimWindow(List<long> src, long now)
        {
            long window = TimeSpan.FromSeconds(WindowSeconds).Ticks;
            List<long> keep = new List<long>();
            foreach (long t in src)
            {
                if (now - t <= window && now >= t) keep.Add(t);
            }
            return keep;
        }

        /// <summary>
        /// 启动时调用（在 BeginRun 之后）：判断是否应该进安全模式。
        /// 这里只读计数，**不**新增记录。
        /// </summary>
        public static bool ShouldEnterSafeMode(string baseDir, bool forceSafeMode)
        {
            if (forceSafeMode) return true;

            lock (gate)
            {
                Data d = LoadRaw();
                long now = DateTime.UtcNow.Ticks;
                long window = TimeSpan.FromSeconds(WindowSeconds).Ticks;

                // 只统计窗口内的崩溃
                int recent = 0;
                foreach (long t in d.Ticks)
                {
                    if (now - t <= window && now >= t) recent++;
                }

                if (d.SafeMode) return true;
                if (recent >= CrashThreshold) return true;
                return false;
            }
        }

        /// <summary>
        /// 记录一次“异常退出”。在未处理异常钩子里调用。
        /// 同时写盘，保证进程立刻被杀也能留下记录。
        /// </summary>
        public static void RecordCrash()
        {
            try
            {
                lock (gate)
                {
                    Data d = LoadRaw();
                    long now = DateTime.UtcNow.Ticks;

                    List<long> keep = TrimWindow(d.Ticks, now);
                    keep.Add(now);
                    d.Ticks = keep;

                    // 标记"本轮已经由钩子记过"，供下次 BeginRun 去重
                    d.CrashRecordedThisRun = true;

                    if (keep.Count >= CrashThreshold) d.SafeMode = true;
                    SaveRaw(d);
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("记录崩溃次数失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>稳定运行够久后清除计数（Program 在 5 分钟后调用）。</summary>
        public static void MarkStable()
        {
            try
            {
                lock (gate)
                {
                    Data d = LoadRaw();
                    if (d.Ticks.Count == 0 && !d.SafeMode) return;
                    d.Ticks.Clear();
                    d.SafeMode = false;
                    SaveRaw(d);
                    Log.Info("稳定运行 " + StableSeconds + " 秒，已清除崩溃计数");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("清除崩溃计数失败: " + ex.Message);
            }
        }

        /// <summary>手动退出安全模式（用户在安全模式面板里点“恢复正常模式”）。</summary>
        public static void ClearSafeMode()
        {
            try
            {
                lock (gate)
                {
                    Data d = LoadRaw();
                    d.Ticks.Clear();
                    d.SafeMode = false;
                    SaveRaw(d);
                }
            }
            catch { }
        }
    }
}
