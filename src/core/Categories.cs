using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// 分类的顺序与显示名（属于 core 层，零 WPF 依赖）。
    ///
    /// 存储：config\categories.json
    ///   {
    ///     "version": 1,
    ///     "order":   ["办公", "开发", "网络通讯", "影音娱乐", "系统工具", "工具/命令行", "其他"],
    ///     "renames": { "影音娱乐": "游戏" }
    ///   }
    ///
    /// **设计要点：重命名只改"显示名"，内部名不动。**
    /// 因为分类名被至少四处按名字引用：
    ///   · apps.json 里每个应用的 category
    ///   · category-rules.tsv 里的规则行
    ///   · category-overrides.json 的值
    ///   · order.json 的顶层键
    /// 若把内部名一起改掉，这四处全要跟着迁移，而且**下次重扫会被写回旧名**。
    /// 所以：内部名永远不变，只在**界面显示**时查这张改名表。
    ///
    /// 文件缺失 / 损坏时回退为空（= 用内置默认顺序、不改名），不阻断启动。
    /// 顺序里出现未知分类名（用户在文件里手写错了）会被忽略，不报错。
    /// </summary>
    class Categories
    {
        const int CurrentVersion = 1;

        static readonly object gate = new object();

        /// <summary>自定义顺序（内部名）。空表示用内置默认顺序。</summary>
        readonly List<string> order = new List<string>();

        /// <summary>内部名 -> 显示名。</summary>
        readonly Dictionary<string, string> renames = new Dictionary<string, string>(StringComparer.Ordinal);

        string path;

        public static Action<string> Warn = delegate { };

        public static string PathOf(string baseDir)
        {
            return Path.Combine(baseDir, "config", "categories.json");
        }

        public int RenameCount
        {
            get { lock (gate) { return renames.Count; } }
        }

        public int OrderCount
        {
            get { lock (gate) { return order.Count; } }
        }

        public static Categories Load(string baseDir)
        {
            Categories c = new Categories();
            c.path = PathOf(baseDir);
            try
            {
                string json = JsonIO.Read(c.path);
                if (string.IsNullOrEmpty(json)) return c;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return c;

                object v;

                // 顺序。
                // 用 JsonIO.AsStringArray 而不是直接 foreach IEnumerable：
                // 后者在文件写成字符串时（"order":"办公"）会遍历**字符**，
                // 把每个汉字当成一个分类名。
                if (d.TryGetValue("order", out v) && v != null)
                {
                    foreach (string s in JsonIO.AsStringArray(v))
                    {
                        if (!c.order.Contains(s)) c.order.Add(s);
                    }
                }

                // 改名表
                if (d.TryGetValue("renames", out v) && v != null)
                {
                    Dictionary<string, object> m = v as Dictionary<string, object>;
                    if (m != null)
                    {
                        foreach (KeyValuePair<string, object> kv in m)
                        {
                            string display = Convert.ToString(kv.Value);
                            if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(display)) continue;
                            // 改成正自己没意义，跳过
                            if (string.Equals(kv.Key, display, StringComparison.Ordinal)) continue;
                            c.renames[kv.Key] = display;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Warn("读取 categories.json 失败，已回退到默认分类顺序与名称: " + ex.Message);
            }
            return c;
        }

        /// <summary>
        /// 把自定义顺序应用到分类列表上（就地重排）。
        ///
        /// 只调整**已存在**的分类相对次序：表里没提到的分类保持原有相对位置排在后面。
        /// 这样即使文件是旧的（比如新增了内置分类），也不会把新分类弄丢。
        /// </summary>
        public void ApplyOrder(List<string> cats)
        {
            if (cats == null || cats.Count == 0) return;

            List<string> saved;
            lock (gate)
            {
                if (order.Count == 0) return;
                saved = new List<string>(order);
            }

            List<string> result = new List<string>();
            // 先按保存的顺序放（只放当前确实存在的）
            foreach (string s in saved)
            {
                foreach (string c in cats)
                {
                    if (string.Equals(c, s, StringComparison.Ordinal) && !result.Contains(c))
                    {
                        result.Add(c);
                        break;
                    }
                }
            }
            // 表里没提到的，按原有相对顺序接在后面
            foreach (string c in cats)
            {
                if (!result.Contains(c)) result.Add(c);
            }

            cats.Clear();
            cats.AddRange(result);
        }

        /// <summary>取显示名。没有改名时返回原名。</summary>
        public string DisplayName(string internalName)
        {
            if (string.IsNullOrEmpty(internalName)) return internalName;
            lock (gate)
            {
                string d;
                return renames.TryGetValue(internalName, out d) ? d : internalName;
            }
        }

        /// <summary>是否被改过名。</summary>
        public bool IsRenamed(string internalName)
        {
            if (string.IsNullOrEmpty(internalName)) return false;
            lock (gate) { return renames.ContainsKey(internalName); }
        }

        /// <summary>
        /// 改名。display 传空表示恢复原名。返回 true 表示有变化。
        /// </summary>
        public bool Rename(string internalName, string display)
        {
            if (string.IsNullOrEmpty(internalName)) return false;
            lock (gate)
            {
                if (string.IsNullOrEmpty(display) ||
                    string.Equals(internalName, display, StringComparison.Ordinal))
                {
                    if (!renames.Remove(internalName)) return false;
                    SaveLocked();
                    return true;
                }
                string old;
                if (renames.TryGetValue(internalName, out old) && string.Equals(old, display, StringComparison.Ordinal))
                    return false;
                renames[internalName] = display;
                SaveLocked();
                return true;
            }
        }

        /// <summary>
        /// 保存分类顺序（按给定的内部名列表）。返回 true 表示有变化。
        /// </summary>
        public bool SaveOrder(List<string> cats)
        {
            if (cats == null) return false;
            lock (gate)
            {
                if (order.Count == cats.Count)
                {
                    bool same = true;
                    for (int i = 0; i < cats.Count; i++)
                    {
                        if (!string.Equals(order[i], cats[i], StringComparison.Ordinal)) { same = false; break; }
                    }
                    if (same) return false;
                }
                order.Clear();
                foreach (string c in cats)
                {
                    if (!string.IsNullOrEmpty(c) && !order.Contains(c)) order.Add(c);
                }
                SaveLocked();
                return true;
            }
        }

        /// <summary>把某个分类在顺序里上移或下移一位。返回 true 表示有变化。</summary>
        public bool MoveCategory(List<string> cats, string cat, int delta)
        {
            if (cats == null || string.IsNullOrEmpty(cat) || delta == 0) return false;
            int i = cats.IndexOf(cat);
            if (i < 0) return false;
            int j = i + delta;
            if (j < 0 || j >= cats.Count) return false;

            string tmp = cats[i];
            cats[i] = cats[j];
            cats[j] = tmp;
            return SaveOrder(cats);
        }

        /// <summary>清空所有自定义（顺序与改名都回到默认）。返回清掉的项数。</summary>
        public int Clear()
        {
            lock (gate)
            {
                int n = order.Count + renames.Count;
                if (n == 0) return 0;
                order.Clear();
                renames.Clear();
                SaveLocked();
                return n;
            }
        }

        void SaveLocked()
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;

                Dictionary<string, object> d = new Dictionary<string, object>();
                d["version"] = CurrentVersion;
                d["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                d["order"] = new List<string>(order);

                Dictionary<string, object> r = new Dictionary<string, object>();
                foreach (KeyValuePair<string, string> kv in renames) r[kv.Key] = kv.Value;
                d["renames"] = r;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                JsonIO.WriteAtomic(path, ser.Serialize(d));
            }
            catch (Exception ex)
            {
                Warn("保存 categories.json 失败: " + ex.Message);
            }
        }
    }
}
