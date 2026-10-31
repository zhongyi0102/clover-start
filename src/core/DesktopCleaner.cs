using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Clover
{
    /// <summary>
    /// 桌面保持无快捷方式：监听两个桌面目录，把新出现的 .lnk/.url 移到回收站。
    ///
    /// ── 只处理 .lnk 和 .url ──
    ///
    /// 文件、文件夹、文档、图片一律不碰。
    ///
    /// ── 保留名单优先 ──
    ///
    /// 名单里的快捷方式永远跳过（见 DesktopKeep）。
    ///
    /// ── 一次性的撤销豁免 ──
    ///
    /// 「撤销」把文件从回收站还原时，还原动作会触发 Created 事件，
    /// 会被再次移除 —— 所以还原前先登记一个 5 分钟的豁免（见 GrantExemption）。
    ///
    /// ── 公共桌面 ──
    ///
    /// 需要管理员权限。被拒绝时**不自动提权、不弹 UAC**，只记入待处理列表，
    /// 等用户在设置里主动点「点此处理」。
    /// </summary>
    class DesktopCleaner
    {
        /// <summary>事件延迟：等安装程序写完文件。</summary>
        const int DelayMs = 2500;

        /// <summary>文件被占用时的重试次数。</summary>
        const int MaxRetries = 3;

        /// <summary>重试间隔。</summary>
        const int RetryDelayMs = 500;

        /// <summary>同一文件的事件去重窗口。</summary>
        const int DedupeWindowMs = 3000;

        /// <summary>「撤销」豁免的有效期。</summary>
        const int ExemptionMs = 5 * 60 * 1000;

        /// <summary>已移除记录上限。</summary>
        const int MaxRemovedRecords = 200;

        readonly Settings settings;
        readonly string baseDir;

        readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();

        /// <summary>去重：路径 → 上次处理时间戳（TickCount）。</summary>
        readonly Dictionary<string, int> recent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>一次性豁免：路径 → 过期时间戳。</summary>
        readonly Dictionary<string, int> exemptions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>延迟处理定时器（纯 .NET，不用 WPF Dispatcher —— core 层不能引 WPF）。</summary>
        System.Threading.Timer pending;

        /// <summary>待处理的路径（等延迟到期后统一处理）。</summary>
        readonly HashSet<string> queue = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>移除后的回调（用于弹提示）。参数：成功的条目列表。</summary>
        public Action<List<Dictionary<string, object>>> OnRemoved;

        public DesktopCleaner(string baseDir, Settings settings)
        {
            this.baseDir = baseDir;
            this.settings = settings;
        }

        // ========== 生命周期 ==========

        /// <summary>开始监听两个桌面目录。</summary>
        public void Start()
        {
            try
            {
                Stop();   // 幂等

                List<string> dirs = DesktopKeep.DesktopDirs();
                foreach (string dir in dirs)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                        SetupWatcher(dir);
                    }
                    catch (Exception ex)
                    {
                        // 无权限的目录直接跳过，不报错
                        try { Log.Warn("桌面监听跳过: " + dir + " -> " + ex.Message); } catch { }
                    }
                }

                if (pending == null)
                {
                    // 一次性 Timer：每次入队后 Change() 重置到期时间
                    pending = new System.Threading.Timer(OnPendingTick, null,
                        System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("启动桌面监听失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// 让保留名单跟随用户的**手动删除**失效。
        ///
        /// ── 为什么需要 ──
        ///
        /// 用户自己把保留名单里的快捷方式移入回收站（或永久删除）后，
        /// 名单里的 key 就成了幽灵条目。不清理的话：
        ///   · 设置界面里一堆对不上任何文件的行
        ///   · 以后重装同一软件、桌面又出现同名快捷方式时，
        ///     会被这个幽灵 key 意外保护（用户早就删了，不该再保护）
        ///
        /// ── 调用时机 ──
        ///
        ///   · 启动补扫时（SweepOnce 开头）
        ///   · 开关打开时（OnEnabled）
        ///   · 用户在设置里点「清理失效项」（手动）
        ///
        /// 判定由 DesktopKeep.Prune 完成 —— 它只在能成功枚举桌面时才清理，
        /// 避免权限问题导致误删整个名单。
        /// </summary>
        public int PruneKeepList()
        {
            try
            {
                if (settings == null || settings.DesktopKeepTargets == null) return 0;

                int n = DesktopKeep.Prune(settings.DesktopKeepTargets);
                if (n > 0)
                {
                    try { Log.Info("保留名单：跟随手动删除清理了 " + n + " 条"); } catch { }
                    SaveSettings();
                }
                return n;
            }
            catch (Exception ex)
            {
                try { Log.Warn("清理保留名单失败: " + ex.Message); } catch { }
                return 0;
            }
        }

        /// <summary>停止监听并释放 watcher。</summary>
        public void Stop()
        {
            try { if (pending != null) pending.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch { }

            foreach (FileSystemWatcher w in watchers)
            {
                try
                {
                    w.EnableRaisingEvents = false;
                    w.Dispose();
                }
                catch { }
            }
            watchers.Clear();
            queue.Clear();
        }

        void SetupWatcher(string dir)
        {
            FileSystemWatcher w = new FileSystemWatcher();
            w.Path = dir;
            w.IncludeSubdirectories = false;

            // ★ 不能用 "*.lnk" —— 一个 watcher 只能一个 filter，.url 会漏。
            //   收全部再自己过滤扩展名。
            w.Filter = "*.*";
            w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime
                           | NotifyFilters.LastWrite | NotifyFilters.DirectoryName;
            w.InternalBufferSize = 64 * 1024;

            w.Created += delegate(object s, FileSystemEventArgs e) { Enqueue(e.FullPath); };
            w.Renamed += delegate(object s, RenamedEventArgs e) { Enqueue(e.FullPath); };

            // 缓冲区溢出时主动补扫一次（否则会漏掉）
            w.Error += delegate
            {
                try { SweepOnce("监听缓冲区溢出"); } catch { }
            };

            w.EnableRaisingEvents = true;
            watchers.Add(w);
        }

        // ========== 事件入队 ==========

        void Enqueue(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                if (!IsTargetExt(path)) return;                 // 只放行 .lnk / .url
                if (IsExempt(path)) return;                     // 撤销豁免期内不处理

                int now = Environment.TickCount;

                // 去重：同一文件短时间内多次事件只处理一次
                int last;
                if (recent.TryGetValue(path, out last))
                {
                    if (unchecked(now - last) < DedupeWindowMs) return;
                }
                recent[path] = now;

                // 清掉过期记录，避免字典无限增长
                if (recent.Count > 512) PruneRecent(now);

                lock (queue) { queue.Add(path); }

                // 延迟 2.5 秒统一处理（合并同一批事件）
                if (pending != null)
                {
                    // 重置到期时间：最后一条事件之后 DelayMs 才真正处理
                    try { pending.Change(DelayMs, System.Threading.Timeout.Infinite); } catch { }
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("桌面事件入队失败: " + ex.Message); } catch { }
            }
        }

        void PruneRecent(int now)
        {
            try
            {
                List<string> dead = new List<string>();
                foreach (KeyValuePair<string, int> kv in recent)
                    if (unchecked(now - kv.Value) > 60000) dead.Add(kv.Key);
                foreach (string k in dead) recent.Remove(k);
            }
            catch { }
        }

        void OnPendingTick(object state)
        {
            try
            {

                List<string> batch;
                lock (queue)
                {
                    batch = new List<string>(queue);
                    queue.Clear();
                }
                if (batch.Count == 0) return;

                ProcessBatch(batch);
            }
            catch (Exception ex)
            {
                try { Log.Warn("处理桌面批次失败: " + ex.Message); } catch { }
            }
        }

        // ========== 过滤与豁免 ==========

        /// <summary>只处理 .lnk / .url。</summary>
        public static bool IsTargetExt(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
                return ext == ".lnk" || ext == ".url";
            }
            catch { return false; }
        }

        /// <summary>
        /// 登记一次性豁免（「撤销」还原前调用）。
        ///
        /// 还原动作会触发 Created 事件，不豁免的话会被立刻再次移除。
        /// 豁免 5 分钟后自动失效 —— 只管这一次。
        /// </summary>
        public void GrantExemption(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                exemptions[path] = Environment.TickCount;
            }
            catch { }
        }

        bool IsExempt(string path)
        {
            try
            {
                int t;
                if (!exemptions.TryGetValue(path, out t)) return false;

                int now = Environment.TickCount;
                if (unchecked(now - t) > ExemptionMs)
                {
                    exemptions.Remove(path);   // 过期，清掉
                    return false;
                }
                return true;
            }
            catch { return false; }
        }

        // ========== 处理 ==========

        void ProcessBatch(List<string> batch)
        {
            List<Dictionary<string, object>> removedOk = new List<Dictionary<string, object>>();

            foreach (string path in batch)
            {
                try
                {
                    if (!ProcessOne(path, removedOk)) continue;
                }
                catch (Exception ex)
                {
                    try { Log.Warn("处理桌面快捷方式失败: " + path + " -> " + ex.Message); } catch { }
                }
            }

            if (removedOk.Count > 0)
            {
                SaveSettings();
                NotifyShell();

                try
                {
                    if (OnRemoved != null) OnRemoved(removedOk);
                }
                catch (Exception ex)
                {
                    try { Log.Warn("桌面移除回调失败: " + ex.Message); } catch { }
                }
            }
        }

        /// <summary>处理单个文件。返回是否真的移除了。</summary>
        bool ProcessOne(string path, List<Dictionary<string, object>> removedOk)
        {
            try
            {
                if (!File.Exists(path)) return false;
                if (!IsTargetExt(path)) return false;
                if (IsExempt(path)) return false;

                // ★ 保留名单：永远跳过
                if (DesktopKeep.IsKept(settings.DesktopKeepTargets, path)) return false;

                // 等文件写完（被占用时重试）
                if (!WaitUnlocked(path)) return false;

                string target = DesktopKeep.ResolveTarget(path);
                bool isPublic = DesktopKeep.IsPublicDesktop(path);

                // ---- 移到回收站 ----
                FileOps.OpResult r = FileOps.Delete(
                    new List<string> { path }, null, IntPtr.Zero);

                if (!r.Ok)
                {
                    if (isPublic)
                    {
                        // 公共桌面被拒：不自动提权、不弹 UAC，记为待处理
                        AddPending(path, target);
                        SaveSettings();
                    }
                    else
                    {
                        try { Log.Warn("移除桌面快捷方式被拒: " + path + " -> " + r.Message); } catch { }
                    }
                    return false;
                }

                // ---- 记录 ----
                Dictionary<string, object> rec = new Dictionary<string, object>();
                rec["name"] = SafeFileName(path);
                rec["lnkPath"] = path;
                rec["targetPath"] = target ?? "";
                rec["removedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                rec["fromPublic"] = isPublic;

                settings.DesktopRemoved.Add(rec);
                TrimRemoved();
                removedOk.Add(rec);

                return true;
            }
            catch (Exception ex)
            {
                try { Log.Warn("移除桌面快捷方式异常: " + path + " -> " + ex.Message); } catch { }
                return false;
            }
        }

        /// <summary>
        /// 等文件不再被占用（安装程序可能还在写）。
        /// 返回 false = 重试用尽仍被占用，本次跳过。
        /// </summary>
        bool WaitUnlocked(string path)
        {
            for (int i = 0; i < MaxRetries; i++)
            {
                try
                {
                    using (FileStream fs = new FileStream(path, FileMode.Open,
                        FileAccess.Read, FileShare.None))
                    {
                        return true;   // 能独占打开 = 写完了
                    }
                }
                catch (FileNotFoundException) { return false; }
                catch (DirectoryNotFoundException) { return false; }
                catch
                {
                    // 被占用 → 等一下再试
                    if (i < MaxRetries - 1)
                    {
                        try { System.Threading.Thread.Sleep(RetryDelayMs); } catch { }
                    }
                }
            }
            return false;
        }

        void AddPending(string path, string target)
        {
            try
            {
                // 已有同路径的待处理项 → 不重复加
                foreach (object o in settings.DesktopPendingAdmin)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string p = d.ContainsKey("lnkPath") ? Convert.ToString(d["lnkPath"]) : null;
                    if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) return;
                }

                Dictionary<string, object> rec = new Dictionary<string, object>();
                rec["name"] = SafeFileName(path);
                rec["lnkPath"] = path;
                rec["targetPath"] = target ?? "";
                rec["removedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                rec["fromPublic"] = true;
                settings.DesktopPendingAdmin.Add(rec);
            }
            catch { }
        }

        void TrimRemoved()
        {
            try
            {
                while (settings.DesktopRemoved.Count > MaxRemovedRecords)
                    settings.DesktopRemoved.RemoveAt(0);   // 丢最旧
            }
            catch { }
        }

        // ========== 启动补扫 ==========

        /// <summary>
        /// 启动时补扫。**范围严格限定**，避免把用户早放好的东西清掉。
        ///
        /// 判定规则（按需求 4.2 + 4.5 的补充）：
        ///   · 开关关闭 → 不扫
        ///   · 子选项勾选（DesktopCleanExisting=true）→ 只处理创建时间晚于
        ///     `DesktopEnabledAt` 的（首次开启时「清理现有」已处理过旧的）
        ///   · 子选项未勾选 → 同样只处理晚于 `DesktopEnabledAt` 的
        ///     （现有的一律视为用户的）
        ///
        /// 两种情况都再叠加 `DesktopLastScan`：只处理晚于上次扫描的，
        /// 即"应用没运行期间新增的"。
        ///
        /// 最终取 `max(DesktopEnabledAt, DesktopLastScan)` 作为时间下限。
        /// </summary>
        public void SweepOnce(string reason)
        {
            try
            {
                if (settings == null || !settings.DesktopCleanEnabled) return;

                // 先让保留名单跟随手动删除失效（用户自己删掉的条目）
                PruneKeepList();

                DateTime since = ComputeSweepSince();

                List<string> candidates = new List<string>();
                foreach (string dir in DesktopKeep.DesktopDirs())
                {
                    try
                    {
                        if (!Directory.Exists(dir)) continue;

                        string[] files;
                        try { files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly); }
                        catch { continue; }   // 无权限目录直接跳过

                        foreach (string f in files)
                        {
                            try
                            {
                                if (!IsTargetExt(f)) continue;
                                if (DesktopKeep.IsKept(settings.DesktopKeepTargets, f)) continue;

                                // 只处理「新增」的：创建时间晚于下限
                                DateTime created;
                                try { created = File.GetCreationTime(f); }
                                catch { continue; }

                                if (created <= since) continue;   // 早就存在 → 不碰

                                candidates.Add(f);
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        try { Log.Warn("补扫桌面目录失败: " + dir + " -> " + ex.Message); } catch { }
                    }
                }

                if (candidates.Count > 0)
                {
                    try { Log.Info("桌面补扫(" + reason + ")：处理 " + candidates.Count + " 个新增快捷方式"); }
                    catch { }
                    ProcessBatch(candidates);
                }

                // 记录本次扫描时间
                settings.DesktopLastScan = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                SaveSettings();
            }
            catch (Exception ex)
            {
                try { Log.Warn("桌面补扫失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// 算出补扫的时间下限：取 `DesktopEnabledAt` 与 `DesktopLastScan` 的较大者。
        /// 两者都解析不出 → 用「现在」（等于不处理任何现有文件，最保守）。
        /// </summary>
        DateTime ComputeSweepSince()
        {
            DateTime a = ParseTime(settings.DesktopEnabledAt);
            DateTime b = ParseTime(settings.DesktopLastScan);

            if (a == DateTime.MinValue && b == DateTime.MinValue)
                return DateTime.Now;    // 都没记录 → 不碰任何现有文件

            if (a == DateTime.MinValue) return b;
            if (b == DateTime.MinValue) return a;
            return a > b ? a : b;
        }

        static DateTime ParseTime(string s)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
                DateTime t;
                if (DateTime.TryParse(s, out t)) return t;
                return DateTime.MinValue;
            }
            catch { return DateTime.MinValue; }
        }

        // ========== 开关切换 ==========

        /// <summary>
        /// 总开关从关 → 开时调用。
        ///
        /// 记录 `DesktopEnabledAt`，并按子选项决定是否立即清理现有的。
        /// </summary>
        public void OnEnabled()
        {
            try
            {
                settings.DesktopCleanEnabled = true;
                settings.DesktopEnabledAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                // 开启前先清理失效名单（用户自己删掉的条目）
                PruneKeepList();

                if (settings.DesktopCleanExisting)
                {
                    // 「同时清理现有的」→ 全部处理（不受创建时间限制）
                    CleanAllExisting();
                }

                settings.DesktopLastScan = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                SaveSettings();
                Start();
            }
            catch (Exception ex)
            {
                try { Log.Warn("开启桌面清理失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>总开关关闭：停止监听，释放 watcher。</summary>
        public void OnDisabled()
        {
            try
            {
                settings.DesktopCleanEnabled = false;
                Stop();
                SaveSettings();
            }
            catch (Exception ex)
            {
                try { Log.Warn("关闭桌面清理失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>清理桌面上**全部**现有快捷方式（忽略创建时间）。</summary>
        void CleanAllExisting()
        {
            try
            {
                List<string> all = new List<string>();
                foreach (string dir in DesktopKeep.DesktopDirs())
                {
                    try
                    {
                        if (!Directory.Exists(dir)) continue;
                        string[] files;
                        try { files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly); }
                        catch { continue; }

                        foreach (string f in files)
                        {
                            try
                            {
                                if (!IsTargetExt(f)) continue;
                                if (DesktopKeep.IsKept(settings.DesktopKeepTargets, f)) continue;
                                all.Add(f);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                if (all.Count > 0)
                {
                    try { Log.Info("桌面清理（现有）：处理 " + all.Count + " 个快捷方式"); } catch { }
                    ProcessBatch(all);
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("清理现有桌面快捷方式失败: " + ex.Message); } catch { }
            }
        }

        // ========== 公共桌面待处理 ==========

        /// <summary>
        /// 处理待处理的公共桌面快捷方式（用户在设置里点了「点此处理」之后调用）。
        ///
        /// 提权删除是**永久删除**，调用方必须已经明确提示过用户。
        /// </summary>
        public int ProcessPendingAdmin(out int failed)
        {
            failed = 0;
            int ok = 0;
            try
            {
                List<object> pend = new List<object>(settings.DesktopPendingAdmin);
                List<object> stillFail = new List<object>();

                foreach (object o in pend)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;

                    string path = d.ContainsKey("lnkPath") ? Convert.ToString(d["lnkPath"]) : null;
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    string err;
                    if (Elevation.DeleteAsAdmin(path, out err))
                    {
                        ok++;

                        // 记入已移除
                        d["removedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        settings.DesktopRemoved.Add(d);
                    }
                    else
                    {
                        failed++;
                        stillFail.Add(o);
                    }
                }

                settings.DesktopPendingAdmin = stillFail;
                TrimRemoved();
                SaveSettings();

                if (ok > 0) NotifyShell();
            }
            catch (Exception ex)
            {
                try { Log.Warn("提权处理待办失败: " + ex.Message); } catch { }
            }
            return ok;
        }

        // ========== 通知资源管理器 ==========

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

        const int SHCNE_ASSOCCHANGED = 0x08000000;
        const int SHCNE_UPDATEDIR = 0x00001000;
        const uint SHCNF_PATHW = 0x0005;
        const uint SHCNF_FLUSH = 0x1000;

        /// <summary>通知资源管理器刷新桌面。</summary>
        void NotifyShell()
        {
            try
            {
                foreach (string dir in DesktopKeep.DesktopDirs())
                {
                    try
                    {
                        SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSH, dir, IntPtr.Zero);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ========== 辅助 ==========

        void SaveSettings()
        {
            try { if (settings != null) settings.Save(); }
            catch (Exception ex)
            {
                try { Log.Warn("保存桌面清理设置失败: " + ex.Message); } catch { }
            }
        }

        static string SafeFileName(string path)
        {
            try { return Path.GetFileName(path); }
            catch { return path ?? ""; }
        }
    }
}
