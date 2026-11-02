using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// 旧桌面迁移冲突的一次性提示状态。
    ///
    /// 同一组冲突路径只弹一次；路径变化后会生成新签名，再提示一次。
    /// </summary>
    public class DeskMigrationNotice
    {
        string path;
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public DeskMigrationNotice(string baseDir)
        {
            try
            {
                if (!string.IsNullOrEmpty(baseDir))
                    path = Path.Combine(baseDir, "config", "desk-migration-notice.json");
            }
            catch { path = null; }
        }

        public void Load()
        {
            seen.Clear();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                string json = JsonIO.Read(path);
                if (string.IsNullOrEmpty(json)) return;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return;
                object raw;
                if (!d.TryGetValue("seen", out raw)) return;
                foreach (string s in JsonIO.AsStringArray(raw))
                    if (!string.IsNullOrEmpty(s)) seen.Add(s);
            }
            catch { seen.Clear(); }
        }

        /// <summary>该签名是否应该提示；首次返回 true 并持久化。</summary>
        public bool ShouldShow(string signature)
        {
            try
            {
                if (string.IsNullOrEmpty(signature)) return false;
                if (seen.Contains(signature)) return false;
                seen.Add(signature);
                Save();
                return true;
            }
            catch { return false; }
        }

        void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["seen"] = new List<string>(seen).ToArray();
                JavaScriptSerializer ser = new JavaScriptSerializer();
                JsonIO.WriteAtomic(path, ser.Serialize(d));
            }
            catch { }
        }
    }
}
