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
    /// 和「隐藏分类」的区别：隐藏分类一次藏掉一整类（如整个「系统工具」），
    /// 隐藏应用是精确到单个条目。两者可以叠加。
    ///
    /// 存储：config\hidden-apps.json
    ///   {"version":1,"paths":["C:\\...\\xxx.lnk",...]}
    /// 用路径做键（唯一且稳定），不区分大小写。
    ///
    /// 文件损坏 / 读不出时**回退为空**（等于什么都没隐藏），不阻断启动。
    /// </summary>
    class HiddenApps
    {
        const int CurrentVersion = 1;

        static readonly object gate = new object();
        readonly HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string path;

        public static Action<string> Warn = delegate { };

        public int Count
        {
            get { lock (gate) { return paths.Count; } }
        }

        public static string PathOf(string baseDir)
        {
            return Path.Combine(baseDir, "config", "hidden-apps.json");
        }

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

                object arr;
                if (!d.TryGetValue("paths", out arr) || arr == null) return h;

                foreach (object o in (System.Collections.IEnumerable)arr)
                {
                    string p = Convert.ToString(o);
                    if (!string.IsNullOrEmpty(p)) h.paths.Add(p);
                }
            }
            catch (Exception ex)
            {
                Warn("读取 hidden-apps.json 失败，已回退为空（未隐藏任何应用）: " + ex.Message);
            }
            return h;
        }

        public bool IsHidden(string appPath)
        {
            if (string.IsNullOrEmpty(appPath)) return false;
            lock (gate) { return paths.Contains(appPath); }
        }

        /// <summary>隐藏一个应用。返回 true 表示有变化。</summary>
        public bool Hide(string appPath)
        {
            if (string.IsNullOrEmpty(appPath)) return false;
            lock (gate)
            {
                if (!paths.Add(appPath)) return false;
                SaveLocked();
                return true;
            }
        }

        /// <summary>取消隐藏。</summary>
        public bool Show(string appPath)
        {
            if (string.IsNullOrEmpty(appPath)) return false;
            lock (gate)
            {
                if (!paths.Remove(appPath)) return false;
                SaveLocked();
                return true;
            }
        }

        /// <summary>全部取消隐藏。</summary>
        public int Clear()
        {
            lock (gate)
            {
                int n = paths.Count;
                if (n == 0) return 0;
                paths.Clear();
                SaveLocked();
                return n;
            }
        }

        /// <summary>
        /// 反过来：把清单里**当前可见**的路径收集起来。
        /// 用于「隐藏的项目」视图（要显示被藏起来的那些，需要重建清单）。
        /// </summary>
        public List<string> Snapshot()
        {
            lock (gate) { return new List<string>(paths); }
        }

        void SaveLocked()
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;

                Dictionary<string, object> d = new Dictionary<string, object>();
                d["version"] = CurrentVersion;
                d["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                List<string> list = new List<string>(paths);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                d["paths"] = list;

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
