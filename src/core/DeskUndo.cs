using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// 「收纳桌面 / 放回桌面」的撤销历史。
    ///
    /// ── 为什么单独存 ──
    ///
    /// 上次删文件的教训：操作只记了文件名，没记原路径，出了事无从追溯。
    /// 这里每条记录都强制带「原路径 → 新路径 → 时间 → 动作」。
    ///
    /// ── 只保留最近 5 批 ──
    ///
    /// 每批是一次按钮操作（可能含多个文件）。5 批足以覆盖误操作，
    /// 又不会让配置无限膨胀。
    /// </summary>
    public class DeskUndo
    {
        /// <summary>最多保留几批。</summary>
        public const int MaxBatches = 5;

        /// <summary>一条文件移动记录。</summary>
        public class Entry
        {
            public string From = "";     // 原路径
            public string To = "";       // 新路径
            public string Time = "";     // 时间
        }

        /// <summary>一批操作。</summary>
        public class Batch
        {
            public string Action = "";   // "收纳桌面" / "放回桌面"
            public string Time = "";
            public List<Entry> Entries = new List<Entry>();
        }

        public List<Batch> Batches = new List<Batch>();

        string path;

        public DeskUndo(string baseDir)
        {
            try
            {
                if (!string.IsNullOrEmpty(baseDir))
                    path = Path.Combine(baseDir, "config", "desk-undo.json");
            }
            catch { path = null; }
        }

        // ========== 读 ==========

        public void Load()
        {
            Batches = new List<Batch>();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

                string json = JsonIO.Read(path);
                if (string.IsNullOrEmpty(json)) return;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return;

                object raw;
                if (!d.TryGetValue("batches", out raw) || raw == null) return;

                foreach (object one in JsonIO.AsArray(raw))
                {
                    Dictionary<string, object> b = one as Dictionary<string, object>;
                    if (b == null) continue;

                    Batch batch = new Batch();
                    batch.Action = Str(b, "action");
                    batch.Time = Str(b, "time");

                    object ev;
                    if (b.TryGetValue("entries", out ev) && ev != null)
                    {
                        foreach (object e in JsonIO.AsArray(ev))
                        {
                            Dictionary<string, object> em = e as Dictionary<string, object>;
                            if (em == null) continue;

                            Entry entry = new Entry();
                            entry.From = Str(em, "from");
                            entry.To = Str(em, "to");
                            entry.Time = Str(em, "time");

                            if (string.IsNullOrEmpty(entry.From) || string.IsNullOrEmpty(entry.To))
                                continue;

                            batch.Entries.Add(entry);
                        }
                    }

                    if (batch.Entries.Count == 0) continue;
                    Batches.Add(batch);
                    if (Batches.Count >= MaxBatches) break;
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("读取 desk-undo.json 失败，按空处理: " + ex.Message); } catch { }
                Batches = new List<Batch>();
            }
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            try
            {
                object v;
                if (d == null || !d.TryGetValue(key, out v) || v == null) return "";
                return Convert.ToString(v) ?? "";
            }
            catch { return ""; }
        }

        // ========== 写 ==========

        public void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;

                List<Dictionary<string, object>> arr = new List<Dictionary<string, object>>();
                foreach (Batch b in Batches)
                {
                    if (b == null) continue;

                    List<Dictionary<string, object>> entries = new List<Dictionary<string, object>>();
                    foreach (Entry e in b.Entries)
                    {
                        if (e == null) continue;
                        if (string.IsNullOrEmpty(e.From) || string.IsNullOrEmpty(e.To)) continue;

                        Dictionary<string, object> one = new Dictionary<string, object>();
                        one["from"] = e.From;
                        one["to"] = e.To;
                        one["time"] = e.Time ?? "";
                        entries.Add(one);
                    }
                    if (entries.Count == 0) continue;

                    Dictionary<string, object> body = new Dictionary<string, object>();
                    body["action"] = b.Action ?? "";
                    body["time"] = b.Time ?? "";
                    body["entries"] = entries.ToArray();
                    arr.Add(body);
                }

                Dictionary<string, object> root = new Dictionary<string, object>();
                root["batches"] = arr.ToArray();

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                JsonIO.WriteAtomic(path, ser.Serialize(root));
            }
            catch (Exception ex)
            {
                try { Log.Warn("保存 desk-undo.json 失败: " + ex.Message); } catch { }
            }
        }

        // ========== 记录 / 撤销 ==========

        /// <summary>记一批操作（自动裁剪到最近 MaxBatches 批）。</summary>
        public void Push(string action, List<Entry> entries)
        {
            try
            {
                if (entries == null || entries.Count == 0) return;

                Batch b = new Batch();
                b.Action = action ?? "";
                b.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                b.Entries.AddRange(entries);

                Batches.Insert(0, b);          // 最新的在前
                while (Batches.Count > MaxBatches) Batches.RemoveAt(Batches.Count - 1);

                Save();
            }
            catch (Exception ex)
            {
                try { Log.Warn("记录撤销批次失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>最近一批（没有则 null）。</summary>
        public Batch Latest
        {
            get { try { return Batches.Count > 0 ? Batches[0] : null; } catch { return null; } }
        }

        /// <summary>撤销结果。</summary>
        public class UndoResult
        {
            public int Restored;
            public List<string> Failed = new List<string>();
            public List<string> Reasons = new List<string>();
            public string Action = "";
            public bool AllOk { get { return Failed.Count == 0; } }
        }

        /// <summary>
        /// 撤销最近一批：把每个 To 移回 From。
        ///
        /// 安全约束：原路径已被占用时**不覆盖**，记为失败；
        /// 源文件不存在时也记为失败（可能已被用户手动移走）。
        /// </summary>
        public UndoResult UndoLatest()
        {
            UndoResult r = new UndoResult();
            try
            {
                Batch b = Latest;
                if (b == null) return r;

                r.Action = b.Action;

                List<Entry> remaining = new List<Entry>();
                foreach (Entry e in b.Entries)
                {
                    try
                    {
                        if (!File.Exists(e.To) && !Directory.Exists(e.To))
                        {
                            r.Failed.Add(Path.GetFileName(e.To));
                            r.Reasons.Add("找不到要撤销的文件");
                            remaining.Add(e);
                            continue;
                        }

                        if (File.Exists(e.From) || Directory.Exists(e.From))
                        {
                            r.Failed.Add(Path.GetFileName(e.To));
                            r.Reasons.Add("原位置已被占用，未覆盖");
                            remaining.Add(e);
                            continue;
                        }

                        VerifiedFileMove.Result moved = VerifiedFileMove.Move(e.To, e.From);
                        if (!moved.Ok)
                        {
                            r.Failed.Add(Path.GetFileName(e.To));
                            r.Reasons.Add(moved.Message);
                            remaining.Add(e);
                            continue;
                        }

                        r.Restored++;
                        try { Log.Info("撤销: " + e.To + " → " + e.From); } catch { }
                    }
                    catch (Exception ex)
                    {
                        r.Failed.Add(Path.GetFileName(e.To));
                        r.Reasons.Add(ex.Message);
                        remaining.Add(e);
                    }
                }

                // 成功项不再保留；只留下失败项供下次重试。
                if (remaining.Count == 0)
                    Batches.RemoveAt(0);
                else
                    b.Entries = remaining;
                Save();
            }
            catch (Exception ex)
            {
                r.Failed.Add("(整体)");
                r.Reasons.Add(ex.Message);
            }
            return r;
        }
    }
}
