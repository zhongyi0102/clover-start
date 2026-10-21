using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// 用户手工指定的「应用 → 分类」覆盖表。
    ///
    /// 为什么要它：分类靠关键词枚举，永远会有判错的（HP 打印机、各类专有名词）。
    /// 与其不断往规则表里堆词，不如让用户用右键菜单「移到分类」自己纠正一次，
    /// 结果记在这里，重新扫描后依然保持。
    ///
    /// 优先级：**本表 > 开始菜单目录规则 > 关键词规则**。
    ///
    /// 存储：config\category-overrides.json
    ///   {"version":1,"overrides":{"C:\\path\\app.exe":"开发"}}
    ///   key 用应用路径（唯一且稳定），不区分大小写。
    ///
    /// 文件损坏 / 读不出时**回退为空表**（等于全部走默认规则），不阻断启动。
    /// </summary>
    class CategoryOverrides
    {
        const int CurrentVersion = 1;

        static readonly object gate = new object();
        readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string path;

        /// <summary>警告输出钩子（默认不输出，避免强依赖 ErrorLog/WPF）。</summary>
        public static Action<string> Warn = delegate { };

        public int Count
        {
            get { lock (gate) { return map.Count; } }
        }

        public static string PathOf(string baseDir)
        {
            return Path.Combine(baseDir, "config", "category-overrides.json");
        }

        /// <summary>读取覆盖表；文件不存在或损坏时返回空表。</summary>
        public static CategoryOverrides Load(string baseDir)
        {
            CategoryOverrides o = new CategoryOverrides();
            o.path = PathOf(baseDir);
            try
            {
                string json = JsonIO.Read(o.path);
                if (string.IsNullOrEmpty(json)) return o;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return o;

                object ov;
                if (!d.TryGetValue("overrides", out ov) || ov == null) return o;

                Dictionary<string, object> m = ov as Dictionary<string, object>;
                if (m == null) return o;

                foreach (KeyValuePair<string, object> kv in m)
                {
                    string cat = Convert.ToString(kv.Value);
                    if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(cat)) continue;
                    o.map[kv.Key] = cat;
                }
            }
            catch (Exception ex)
            {
                // 损坏时回退为空表：全部走默认规则，不让启动失败
                Warn("读取 category-overrides.json 失败，已回退到默认分类规则: " + ex.Message);
            }
            return o;
        }

        /// <summary>取某个应用的覆盖分类；没有覆盖时返回 null。</summary>
        public string Get(string appPath)
        {
            if (string.IsNullOrEmpty(appPath)) return null;
            lock (gate)
            {
                string cat;
                return map.TryGetValue(appPath, out cat) ? cat : null;
            }
        }

        /// <summary>
        /// 给某个应用指定分类并落盘。返回 true 表示有变化。
        /// 传空分类等同 Remove。
        /// </summary>
        public bool Set(string appPath, string category)
        {
            if (string.IsNullOrEmpty(appPath)) return false;

            if (string.IsNullOrEmpty(category))
                return Remove(appPath);

            lock (gate)
            {
                string old;
                if (map.TryGetValue(appPath, out old) &&
                    string.Equals(old, category, StringComparison.Ordinal))
                {
                    return false; // 没变化，不写盘
                }
                map[appPath] = category;
                SaveLocked();
                return true;
            }
        }

        /// <summary>删除某个应用的覆盖（回到默认规则）。</summary>
        public bool Remove(string appPath)
        {
            if (string.IsNullOrEmpty(appPath)) return false;
            lock (gate)
            {
                if (!map.Remove(appPath)) return false;
                SaveLocked();
                return true;
            }
        }

        /// <summary>把一批「应用 → 分类」应用到目录清单上（就地修改 Category）。</summary>
        public int Apply(IEnumerable<AppItem> apps)
        {
            if (apps == null) return 0;
            int n = 0;
            foreach (AppItem a in apps)
            {
                if (a == null) continue;
                string cat = Get(a.Path);
                if (cat == null) continue;
                if (!string.Equals(a.Category, cat, StringComparison.Ordinal)) n++;
                a.Category = cat;
            }
            return n;
        }

        /// <summary>导出为字典（供扫描/保存流程使用）。</summary>
        public Dictionary<string, string> Snapshot()
        {
            lock (gate)
            {
                return new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>原子写盘（复用 JsonIO：临时文件 + 替换 + .bak + 每日备份）。</summary>
        void SaveLocked()
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;

                Dictionary<string, object> d = new Dictionary<string, object>();
                d["version"] = CurrentVersion;
                d["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                Dictionary<string, object> ov = new Dictionary<string, object>();
                foreach (KeyValuePair<string, string> kv in map) ov[kv.Key] = kv.Value;
                d["overrides"] = ov;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                JsonIO.WriteAtomic(path, ser.Serialize(d));
            }
            catch (Exception ex)
            {
                Warn("保存 category-overrides.json 失败: " + ex.Message);
            }
        }
    }
}
