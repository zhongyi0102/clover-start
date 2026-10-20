using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Clover
{
    class AppItem
    {
        public string Name;
        public string Path;
        public string Category;
        public int Usage;
        public long LastUsed;
    }

    class Catalog
    {
        public List<string> Order = new List<string>();
        public List<AppItem> Apps = new List<AppItem>();

        public static readonly string[] DefaultOrder = new string[] { "办公", "开发", "网络通讯", "影音娱乐", "系统工具", "其他" };

        public static Catalog Load(string baseDir)
        {
            Catalog c = new Catalog();
            string cfg = Path.Combine(baseDir, "config");
            ReadInto(c, Path.Combine(cfg, "apps.json"));
            ReadInto(c, Path.Combine(cfg, "manual-apps.json"));

            foreach (string d in DefaultOrder) EnsureOrder(c, d);
            foreach (AppItem a in c.Apps) EnsureOrder(c, a.Category);
            return c;
        }

        static void EnsureOrder(Catalog c, string cat)
        {
            if (string.IsNullOrEmpty(cat)) return;
            if (!c.Order.Contains(cat)) c.Order.Add(cat);
        }


        static void ReadInto(Catalog c, string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                string json = JsonIO.Read(path);
                if (string.IsNullOrEmpty(json)) return;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> data = ser.Deserialize<Dictionary<string, object>>(json);

                object arr;
                if (data.TryGetValue("apps", out arr) && arr != null)
                {
                    foreach (object o in (System.Collections.IEnumerable)arr)
                    {
                        Dictionary<string, object> m = o as Dictionary<string, object>;
                        if (m == null) continue;
                        AppItem a = new AppItem();
                        a.Name = GetStr(m, "name");
                        a.Path = GetStr(m, "path");
                        a.Category = GetStr(m, "category");
                        if (string.IsNullOrEmpty(a.Name) || string.IsNullOrEmpty(a.Path)) continue;
                        if (!File.Exists(a.Path)) continue;
                        if (c.ContainsPath(a.Path)) continue;
                        if (string.IsNullOrEmpty(a.Category)) a.Category = GuessCategory(a.Name);
                        if (c.ContainsName(a.Name)) continue;
                        c.Apps.Add(a);
                    }
                }
            }
            catch { }
        }

        static string GetStr(Dictionary<string, object> m, string key)
        {
            object v;
            if (m.TryGetValue(key, out v) && v != null)
                return Convert.ToString(v);
            return "";
        }

        public bool ContainsName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (AppItem a in Apps)
            {
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public bool ContainsPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (AppItem a in Apps)
            {
                if (string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static List<AppItem> LoadManual(string baseDir)
        {
            List<AppItem> list = new List<AppItem>();
            string path = Path.Combine(baseDir, "config", "manual-apps.json");
            if (!File.Exists(path)) return list;
            try
            {
                string json = JsonIO.Read(path);
                if (string.IsNullOrEmpty(json)) return list;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> data = ser.Deserialize<Dictionary<string, object>>(json);
                object arr;
                if (data.TryGetValue("apps", out arr) && arr != null)
                {
                    foreach (object o in (System.Collections.IEnumerable)arr)
                    {
                        Dictionary<string, object> m = o as Dictionary<string, object>;
                        if (m == null) continue;
                        AppItem a = new AppItem();
                        a.Name = GetStr(m, "name");
                        a.Path = GetStr(m, "path");
                        a.Category = GetStr(m, "category");
                        if (!string.IsNullOrEmpty(a.Name) && !string.IsNullOrEmpty(a.Path))
                            list.Add(a);
                    }
                }
            }
            catch { }
            return list;
        }

        public static void SaveManual(string baseDir, List<AppItem> list)
        {
            try
            {
                Dictionary<string, object> root = new Dictionary<string, object>();
                List<object> arr = new List<object>();
                foreach (AppItem a in list)
                {
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["name"] = a.Name;
                    m["path"] = a.Path;
                    m["category"] = a.Category;
                    arr.Add(m);
                }
                root["apps"] = arr;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                string json = ser.Serialize(root);
                JsonIO.WriteAtomic(Path.Combine(baseDir, "config", "manual-apps.json"), json);
            }
            catch { }
        }

        public static string GuessCategory(string name)
        {
            string n = name.ToLowerInvariant();
            if (Regex.IsMatch(n, "steam|epic|wegame|game|游戏|原神|minecraft|league")) return "影音娱乐";
            if (Regex.IsMatch(n, "chrome|edge|firefox|brave|browser|浏览器|telegram|discord|wechat|weixin|qq|微信|抖音|钉钉|迅雷|网盘|clash|v2ray|迅游")) return "网络通讯";
            if (Regex.IsMatch(n, "word|excel|powerpoint|wps|office|notion|obsidian|onenote|飞书|foxit|adobe.*(acrobat|reader)|pdf")) return "办公";
            if (Regex.IsMatch(n, "code|studio|python|git|node|sql|java|docker|postman|pycharm|idea|anaconda|jupyter|vscode|visual")) return "开发";
            if (Regex.IsMatch(n, "player|vlc|potplayer|music|音乐|vid|video|bilibili|哔哩|剪映|obs|audacity|格式工厂")) return "影音娱乐";
            if (Regex.IsMatch(n, "7-zip|winrar|bandizip|压缩|power|sharex|snip|截图|ditto|defender|清理|disk|分区")) return "系统工具";
            return "其他";
        }

        public void HideCategories(List<string> hidden)
        {
            if (hidden == null || hidden.Count == 0) return;
            List<AppItem> keep = new List<AppItem>();
            foreach (AppItem a in Apps)
            {
                if (!hidden.Contains(a.Category)) keep.Add(a);
            }
            Apps = keep;
            List<string> order = new List<string>();
            foreach (string cat in Order)
            {
                if (!hidden.Contains(cat)) order.Add(cat);
            }
            Order = order;
        }

        public void ApplyUsage(UsageStore usage)
        {
            foreach (AppItem a in Apps)
            {
                a.Usage = usage.GetCount(a.Path);
                a.LastUsed = usage.GetLast(a.Path);
            }
        }

        static int CompareApps(AppItem a, AppItem b)
        {
            int c = b.Usage.CompareTo(a.Usage);
            if (c != 0) return c;
            c = b.LastUsed.CompareTo(a.LastUsed);
            if (c != 0) return c;
            return string.Compare(a.Name, b.Name, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), System.Globalization.CompareOptions.None);
        }

        public List<AppItem> ByCategory(string cat)
        {
            List<AppItem> list = new List<AppItem>();
            foreach (AppItem a in Apps)
            {
                if (string.IsNullOrEmpty(cat) || a.Category == cat)
                    list.Add(a);
            }
            list.Sort(CompareApps);
            return list;
        }

        public List<string> ActiveCategories()
        {
            List<string> list = new List<string>();
            foreach (string cat in Order)
            {
                if (ByCategory(cat).Count > 0)
                    list.Add(cat);
            }
            return list;
        }
    }
}
