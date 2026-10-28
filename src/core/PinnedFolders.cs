using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>一个被收藏的文件夹（或文件）。</summary>
    class PinnedItem
    {
        public string Name;
        /// <summary>完整路径。可能是文件夹，也可能是文件。</summary>
        public string Path;
        /// <summary>true = 文件夹，false = 文件。</summary>
        public bool IsFolder;
        /// <summary>加入时间（UTC ticks），用于默认排序。</summary>
        public long AddedAt;
    }

    /// <summary>
    /// 收藏的文件夹/文件（config\pinned-folders.json）。
    ///
    /// **为什么单独一套而不是塞进 AppItem**：
    ///   · AppItem 由扫描生成，重新扫描会覆盖；收藏是用户手动指定的，必须独立持久化
    ///   · AppItem 没有"是不是文件夹"的概念，磁贴的启动逻辑（Launch）只认 exe/lnk
    ///   · 混进 Apps 会让分类统计、搜索、隐藏等功能都要跟着改，波及面大
    ///
    /// 存储格式：
    ///   { "version":1, "items":[ {"name":"项目","path":"E:\\proj","isFolder":true,"addedAt":638...} ] }
    ///
    /// 健壮性：文件缺失/损坏 → 返回空列表（不阻断启动）；
    /// 路径已不存在 → 保留记录但标记为失效（由界面层决定怎么显示），
    /// 不自动删除 —— 可能是移动了硬盘，用户还想找回来。
    /// </summary>
    static class PinnedFolders
    {
        const int CurrentVersion = 1;

        static readonly object gate = new object();

        public static Action<string> Warn = delegate { };

        public static string PathOf(string baseDir)
        {
            return Path.Combine(baseDir, "config", "pinned-folders.json");
        }

        /// <summary>读取全部收藏（按加入时间升序）。</summary>
        public static List<PinnedItem> Load(string baseDir)
        {
            List<PinnedItem> list = new List<PinnedItem>();
            lock (gate)
            {
                try
                {
                    string p = PathOf(baseDir);
                    string json = JsonIO.Read(p);
                    if (string.IsNullOrEmpty(json)) return list;

                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    ser.MaxJsonLength = int.MaxValue;
                    Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                    if (d == null) return list;

                    object arr;
                    if (!d.TryGetValue("items", out arr) || arr == null) return list;

                    foreach (object o in JsonIO.AsArray(arr))
                    {
                        Dictionary<string, object> m = o as Dictionary<string, object>;
                        if (m == null) continue;

                        string path = Str(m, "path");
                        if (string.IsNullOrEmpty(path)) continue;

                        PinnedItem it = new PinnedItem();
                        it.Path = path;
                        string name = Str(m, "name");
                        it.Name = string.IsNullOrEmpty(name) ? Path.GetFileName(path.TrimEnd('\\')) : name;
                        it.IsFolder = Bool(m, "isFolder", true);
                        it.AddedAt = Long(m, "addedAt", 0);
                        list.Add(it);
                    }

                    // 按加入时间升序（老的在前）
                    list.Sort(delegate(PinnedItem a, PinnedItem b)
                    {
                        if (a.AddedAt == b.AddedAt) return 0;
                        return a.AddedAt < b.AddedAt ? -1 : 1;
                    });
                }
                catch (Exception ex)
                {
                    Warn("读取 pinned-folders.json 失败，已回退到空列表: " + ex.Message);
                }
            }
            return list;
        }

        /// <summary>
        /// 收藏一个路径。已存在则返回 false（不重复）。
        ///
        /// 路径不存在也允许收藏：用户可能是先建了记录，
        /// 或者目标在网络盘上暂时未连接。
        /// </summary>
        public static bool Add(string baseDir, string path, string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;

                string full;
                try { full = Path.GetFullPath(path); }
                catch { full = path; }

                List<PinnedItem> list = Load(baseDir);
                foreach (PinnedItem it in list)
                {
                    if (string.Equals(it.Path, full, StringComparison.OrdinalIgnoreCase)) return false;
                }

                bool isFolder = false;
                try { isFolder = Directory.Exists(full); } catch { }
                if (!isFolder)
                {
                    // 不是目录 → 当文件处理；但要求文件确实存在，
                    // 否则说明用户手输了一个无效路径
                    try { if (!File.Exists(full)) return false; } catch { return false; }
                }

                PinnedItem n = new PinnedItem();
                n.Path = full;
                n.Name = string.IsNullOrWhiteSpace(name)
                    ? (isFolder ? new DirectoryInfo(full).Name : Path.GetFileNameWithoutExtension(full))
                    : name.Trim();
                n.IsFolder = isFolder;
                n.AddedAt = DateTime.UtcNow.Ticks;
                list.Add(n);

                return Save(baseDir, list);
            }
            catch (Exception ex)
            {
                Warn("收藏失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>取消收藏（按路径）。</summary>
        public static bool Remove(string baseDir, string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                List<PinnedItem> list = Load(baseDir);
                int removed = list.RemoveAll(delegate(PinnedItem it)
                {
                    return string.Equals(it.Path, path, StringComparison.OrdinalIgnoreCase);
                });
                if (removed == 0) return false;
                return Save(baseDir, list);
            }
            catch { return false; }
        }

        /// <summary>重命名收藏项（只改显示名，不改磁盘上的名字）。</summary>
        public static bool Rename(string baseDir, string path, string newName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                List<PinnedItem> list = Load(baseDir);
                bool hit = false;
                foreach (PinnedItem it in list)
                {
                    if (string.Equals(it.Path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        it.Name = string.IsNullOrWhiteSpace(newName)
                            ? (it.IsFolder ? new DirectoryInfo(it.Path).Name : Path.GetFileNameWithoutExtension(it.Path))
                            : newName.Trim();
                        hit = true;
                        break;
                    }
                }
                if (!hit) return false;
                return Save(baseDir, list);
            }
            catch { return false; }
        }

        /// <summary>清空全部收藏。</summary>
        public static bool Clear(string baseDir)
        {
            try { return Save(baseDir, new List<PinnedItem>()); }
            catch { return false; }
        }

        static bool Save(string baseDir, List<PinnedItem> list)
        {
            try
            {
                List<object> arr = new List<object>();
                foreach (PinnedItem it in list)
                {
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["name"] = it.Name;
                    m["path"] = it.Path;
                    m["isFolder"] = it.IsFolder;
                    m["addedAt"] = it.AddedAt;
                    arr.Add(m);
                }

                Dictionary<string, object> d = new Dictionary<string, object>();
                d["version"] = CurrentVersion;
                d["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                d["items"] = arr;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                JsonIO.WriteAtomic(PathOf(baseDir), ser.Serialize(d));
                return true;
            }
            catch (Exception ex)
            {
                Warn("保存 pinned-folders.json 失败: " + ex.Message);
                return false;
            }
        }

        static string Str(Dictionary<string, object> m, string key)
        {
            object v;
            return (m.TryGetValue(key, out v) && v != null) ? Convert.ToString(v) : "";
        }

        static bool Bool(Dictionary<string, object> m, string key, bool def)
        {
            object v;
            if (!m.TryGetValue(key, out v) || v == null) return def;
            try { return Convert.ToBoolean(v); } catch { return def; }
        }

        static long Long(Dictionary<string, object> m, string key, long def)
        {
            object v;
            if (!m.TryGetValue(key, out v) || v == null) return def;
            try { return Convert.ToInt64(v); } catch { return def; }
        }
    }
}
