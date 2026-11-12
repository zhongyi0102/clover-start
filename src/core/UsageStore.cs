using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Clover
{
    class UsageEntry
    {
        public int Count;
        public long LastTicks;
        public List<long> RecentTicks = new List<long>();
    }

    /// <summary>记录每个应用（按快捷方式/程序路径）的启动次数与最近使用时间，用于排序。</summary>
    class UsageStore
    {
        string path;
        Dictionary<string, UsageEntry> map = new Dictionary<string, UsageEntry>(StringComparer.OrdinalIgnoreCase);

        public static UsageStore Load(string baseDir)
        {
            UsageStore s = new UsageStore();
            s.path = Path.Combine(baseDir, "config", "usage.json");
            try
            {
                if (!File.Exists(s.path)) return s;
                string json = JsonIO.Read(s.path);
                if (string.IsNullOrEmpty(json)) return s;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                object apps;
                if (d.TryGetValue("apps", out apps) && apps != null)
                {
                    foreach (KeyValuePair<string, object> kv in (Dictionary<string, object>)apps)
                    {
                        Dictionary<string, object> m = kv.Value as Dictionary<string, object>;
                        if (m == null) continue;
                        UsageEntry e = new UsageEntry();
                        object v;
                        if (m.TryGetValue("count", out v) && v != null) e.Count = Convert.ToInt32(v);
                        if (m.TryGetValue("last", out v) && v != null) e.LastTicks = Convert.ToInt64(v);
                        if (m.TryGetValue("recent", out v) && v is System.Collections.IEnumerable)
                        {
                            foreach (object tick in (System.Collections.IEnumerable)v)
                                if (tick != null) e.RecentTicks.Add(Convert.ToInt64(tick));
                        }
                        // 老记录只有累计次数与最后启动时间，不能把历史次数当成近期频率。
                        else if (e.LastTicks > 0) e.RecentTicks.Add(e.LastTicks);
                        s.map[kv.Key] = e;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Record(string appPath)
        {
            Record(appPath, DateTime.UtcNow.Ticks);
        }

        internal void Record(string appPath, long nowTicks)
        {
            if (string.IsNullOrEmpty(appPath)) return;
            UsageEntry e;
            if (!map.TryGetValue(appPath, out e))
            {
                e = new UsageEntry();
                map[appPath] = e;
            }
            e.Count++;
            e.LastTicks = nowTicks;
            e.RecentTicks.RemoveAll(t => t <= nowTicks - RecentWindowTicks || t > nowTicks);
            e.RecentTicks.Add(nowTicks);
            Save();
        }

        internal const long RecentWindowTicks = 3 * TimeSpan.TicksPerDay;

        public int GetRecentCount(string appPath, long nowTicks)
        {
            UsageEntry e;
            if (string.IsNullOrEmpty(appPath) || !map.TryGetValue(appPath, out e)) return 0;
            int count = 0;
            foreach (long tick in e.RecentTicks)
                if (tick > nowTicks - RecentWindowTicks && tick <= nowTicks) count++;
            return count;
        }

        public int GetRecentDays(string appPath, long nowTicks)
        {
            UsageEntry e;
            if (string.IsNullOrEmpty(appPath) || !map.TryGetValue(appPath, out e)) return 0;
            HashSet<long> days = new HashSet<long>();
            foreach (long tick in e.RecentTicks)
                if (tick > nowTicks - RecentWindowTicks && tick <= nowTicks)
                    days.Add((nowTicks - tick) / TimeSpan.TicksPerDay);
            return days.Count;
        }

        public void Remove(string appPath)
        {
            if (string.IsNullOrEmpty(appPath)) return;
            if (map.Remove(appPath)) Save();
        }

        public int GetCount(string appPath)
        {
            UsageEntry e;
            if (!string.IsNullOrEmpty(appPath) && map.TryGetValue(appPath, out e)) return e.Count;
            return 0;
        }

        public long GetLast(string appPath)
        {
            UsageEntry e;
            if (!string.IsNullOrEmpty(appPath) && map.TryGetValue(appPath, out e)) return e.LastTicks;
            return 0;
        }

        void Save()
        {
            try
            {
                Dictionary<string, object> apps = new Dictionary<string, object>();
                long nowTicks = DateTime.UtcNow.Ticks;
                foreach (KeyValuePair<string, UsageEntry> kv in map)
                {
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["count"] = kv.Value.Count;
                    m["last"] = kv.Value.LastTicks;
                    m["recent"] = kv.Value.RecentTicks.FindAll(t => t > nowTicks - RecentWindowTicks && t <= nowTicks);
                    apps[kv.Key] = m;
                }
                Dictionary<string, object> root = new Dictionary<string, object>();
                root["apps"] = apps;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                JsonIO.WriteAtomic(path, ser.Serialize(root));
            }
            catch { }
        }
    }
}
