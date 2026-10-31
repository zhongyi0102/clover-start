using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// 被隐藏的应用（属于 core 层，零 WPF 依赖）。
    ///
    /// 用途：把不想在面板里看到的条目彻底藏起来——它比「移到分类」更彻底：
    ///   移到分类 = 归错类了，换个位置
    ///   隐藏      = 完全不想看到（卸载残留、重复项、用不到的系统工具）
    ///
    /// 存储：config\hidden-apps.json
    ///
    /// <code>
    /// {
    ///   "version": 2,
    ///   "updated": "2026-10-31 18:00:00",
    ///   "paths":   ["C:\\...\\xxx.lnk", ...],          // 原始路径（保持大小写，便于展示）
    ///   "reasons": {                                    // key = 规范化路径（小写、统一 \）
    ///     "c:\\...\\xxx.lnk": { "kind": "manual" },
    ///     "c:\\...\\yyy.lnk": { "kind": "batch", "category": "系统工具" }
    ///   },
    ///   "batchExceptions": ["c:\\...\\zzz.lnk"]         // 从批量隐藏里单独恢复的
    /// }
    /// </code>
    ///
    /// ── 兼容性 ──
    ///
    ///   · v1（只有 paths）→ 全部按「手动隐藏」处理
    ///   · `reasons` 的 key 用**规范化路径**（小写、统一 `\`），查的时候必须同样规范化，
    ///     否则 `C:\A\B` 和 `c:\a\b` 对不上
    ///   · `paths` 里存**原始路径**，保持大小写以便界面展示
    ///   · 写回时**保留未知字段**，避免以后升级丢数据
    ///
    /// 文件损坏 / 读不出时**回退为空**（等于什么都没隐藏），不阻断启动。
    /// </summary>
    class HiddenApps
    {
        const int CurrentVersion = 2;

        static readonly object gate = new object();

        /// <summary>隐藏的应用原始路径（保持大小写）。</summary>
        readonly HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>规范化路径 → 隐藏原因。</summary>
        readonly Dictionary<string, HideReason> reasons =
            new Dictionary<string, HideReason>(StringComparer.OrdinalIgnoreCase);

        /// <summary>批量隐藏的例外名单（规范化路径）。</summary>
        readonly HashSet<string> batchExceptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>读进来但本版本不认识的顶层字段 —— 写回时原样带上。</summary>
        readonly Dictionary<string, object> unknown = new Dictionary<string, object>();

        string path;

        public static Action<string> Warn = delegate { };

        public int Count
        {
            get { lock (gate) { return paths.Count; } }
        }

        /// <summary>例外名单数量。</summary>
        public int ExceptionCount
        {
            get { lock (gate) { return batchExceptions.Count; } }
        }

        public static string PathOf(string baseDir)
        {
            return Path.Combine(baseDir, "config", "hidden-apps.json");
        }

        // ========== 路径规范化 ==========

        /// <summary>
        /// 规范化路径：统一 `\`、小写、去尾部斜杠。
        ///
        /// **reasons 和 batchExceptions 的 key 都用它**，
        /// 读取时也必须用它去查，否则大小写/分隔符差异会导致对不上。
        /// </summary>
        public static string Normalize(string p)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(p)) return "";
                string s = p.Trim().Replace('/', '\\');
                while (s.Length > 3 && s.EndsWith("\\", StringComparison.Ordinal))
                    s = s.Substring(0, s.Length - 1);
                return s.ToLowerInvariant();
            }
            catch { return ""; }
        }

        // ========== 读取 ==========

        /// <summary>读取隐藏清单；文件不存在或损坏时返回空。</summary>
        public static HiddenApps Load(string baseDir)
        {
            HiddenApps h = new HiddenApps();
            h.path = PathOf(baseDir);
            try
            {
                string json = JsonIO.Read(h.path);
                if (string.IsNullOrEmpty(json)) return h;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return h;

                // ---- paths（原始路径）----
                object arr;
                if (d.TryGetValue("paths", out arr) && arr != null)
                {
                    foreach (object o in JsonIO.AsArray(arr))
                    {
                        string p = Convert.ToString(o);
                        if (!string.IsNullOrEmpty(p)) h.paths.Add(p);
                    }
                }

                // ---- reasons（规范化路径 → 原因）----
                object rs;
                if (d.TryGetValue("reasons", out rs) && rs != null)
                {
                    Dictionary<string, object> rd = rs as Dictionary<string, object>;
                    if (rd != null)
                    {
                        foreach (KeyValuePair<string, object> kv in rd)
                        {
                            string k = Normalize(kv.Key);
                            if (string.IsNullOrEmpty(k)) continue;

                            Dictionary<string, object> item = kv.Value as Dictionary<string, object>;
                            h.reasons[k] = HideReason.FromDict(item);
                        }
                    }
                }

                // ---- batchExceptions ----
                object ex;
                if (d.TryGetValue("batchExceptions", out ex) && ex != null)
                {
                    foreach (object o in JsonIO.AsArray(ex))
                    {
                        string k = Normalize(Convert.ToString(o));
                        if (!string.IsNullOrEmpty(k)) h.batchExceptions.Add(k);
                    }
                }

                // ---- 保留未知字段 ----
                foreach (KeyValuePair<string, object> kv in d)
                {
                    string key = kv.Key ?? "";
                    if (key == "version" || key == "updated" || key == "paths"
                        || key == "reasons" || key == "batchExceptions") continue;
                    h.unknown[key] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                Warn("读取 hidden-apps.json 失败，已回退为空（未隐藏任何应用）: " + ex.Message);
            }
            return h;
        }

        // ========== 查询 ==========

        public bool IsHidden(string appPath)
        {
            if (string.IsNullOrEmpty(appPath)) return false;
            lock (gate) { return paths.Contains(appPath); }
        }

        /// <summary>取某应用的隐藏原因（没记录 → 按手动隐藏）。</summary>
        public HideReason ReasonOf(string appPath)
        {
            try
            {
                if (string.IsNullOrEmpty(appPath)) return HideReason.Manual();
                string k = Normalize(appPath);
                lock (gate)
                {
                    HideReason r;
                    if (reasons.TryGetValue(k, out r) && r != null) return r;
                }
                return HideReason.Manual();
            }
            catch { return HideReason.Manual(); }
        }

        /// <summary>某个路径是否在批量隐藏例外名单里。</summary>
        public bool IsException(string appPath)
        {
            try
            {
                string k = Normalize(appPath);
                if (string.IsNullOrEmpty(k)) return false;
                lock (gate) { return batchExceptions.Contains(k); }
            }
            catch { return false; }
        }

        /// <summary>例外名单快照（规范化路径）。</summary>
        public List<string> ExceptionSnapshot()
        {
            lock (gate) { return new List<string>(batchExceptions); }
        }

        // ========== 修改 ==========

        /// <summary>隐藏一个应用（默认为手动隐藏）。返回 true 表示有变化。</summary>
        public bool Hide(string appPath)
        {
            return Hide(appPath, HideReason.Manual());
        }

        /// <summary>隐藏一个应用并记录原因。</summary>
        public bool Hide(string appPath, HideReason reason)
        {
            if (string.IsNullOrEmpty(appPath)) return false;
            lock (gate)
            {
                bool changed = paths.Add(appPath);

                string k = Normalize(appPath);
                if (!string.IsNullOrEmpty(k))
                {
                    // 原因总是覆盖（批量隐藏可能把手动隐藏的项收编进来）
                    HideReason r = reason ?? HideReason.Manual();
                    HideReason old;
                    if (!reasons.TryGetValue(k, out old) || old == null
                        || old.Type != r.Type
                        || !string.Equals(old.Category, r.Category, StringComparison.Ordinal))
                    {
                        reasons[k] = r;
                        changed = true;
                    }
                }

                if (changed) SaveLocked();
                return changed;
            }
        }

        /// <summary>取消隐藏。</summary>
        public bool Show(string appPath)
        {
            if (string.IsNullOrEmpty(appPath)) return false;
            lock (gate)
            {
                bool changed = paths.Remove(appPath);

                string k = Normalize(appPath);
                if (!string.IsNullOrEmpty(k) && reasons.Remove(k)) changed = true;

                if (changed) SaveLocked();
                return changed;
            }
        }

        /// <summary>
        /// 恢复一个**批量隐藏**的项，同时写入例外名单。
        ///
        /// 调用方负责判断该项是否确实是批量隐藏的 —— 只有那种情况才写例外，
        /// 手动隐藏的项恢复时不写（否则例外名单会被无关项塞满）。
        /// </summary>
        public bool ShowWithException(string appPath)
        {
            if (string.IsNullOrEmpty(appPath)) return false;
            lock (gate)
            {
                bool changed = paths.Remove(appPath);

                string k = Normalize(appPath);
                if (!string.IsNullOrEmpty(k))
                {
                    if (reasons.Remove(k)) changed = true;
                    if (batchExceptions.Add(k)) changed = true;
                }

                if (changed) SaveLocked();
                return changed;
            }
        }

        /// <summary>从例外名单移除（之后批量隐藏会重新包含它）。</summary>
        public bool RemoveException(string appPath)
        {
            try
            {
                string k = Normalize(appPath);
                if (string.IsNullOrEmpty(k)) return false;
                lock (gate)
                {
                    if (!batchExceptions.Remove(k)) return false;
                    SaveLocked();
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>全部取消隐藏（同时清空原因与例外名单）。</summary>
        public int Clear()
        {
            lock (gate)
            {
                int n = paths.Count;
                if (n == 0 && reasons.Count == 0 && batchExceptions.Count == 0) return 0;

                paths.Clear();
                reasons.Clear();
                batchExceptions.Clear();
                SaveLocked();
                return n;
            }
        }

        /// <summary>隐藏清单快照（原始路径）。</summary>
        public List<string> Snapshot()
        {
            lock (gate) { return new List<string>(paths); }
        }

        // ========== 写入 ==========

        void SaveLocked()
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;

                Dictionary<string, object> d = new Dictionary<string, object>();

                // 先放未知字段（保留未来版本写的键）
                foreach (KeyValuePair<string, object> kv in unknown)
                    d[kv.Key] = kv.Value;

                d["version"] = CurrentVersion;
                d["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                List<string> list = new List<string>(paths);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                d["paths"] = list;

                // reasons：规范化路径 → 原因
                Dictionary<string, object> rd = new Dictionary<string, object>();
                foreach (KeyValuePair<string, HideReason> kv in reasons)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    rd[kv.Key] = kv.Value == null ? HideReason.Manual().ToDict() : kv.Value.ToDict();
                }
                d["reasons"] = rd;

                // 例外名单
                List<string> ex = new List<string>(batchExceptions);
                ex.Sort(StringComparer.OrdinalIgnoreCase);
                d["batchExceptions"] = ex;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                JsonIO.WriteAtomic(path, ser.Serialize(d));
            }
            catch (Exception ex)
            {
                Warn("保存 hidden-apps.json 失败: " + ex.Message);
            }
        }
    }
}
