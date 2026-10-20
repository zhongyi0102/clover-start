using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// 纯 C# 扫描开始菜单（不依赖 PowerShell）：PowerShell 被杀毒软件/策略拦截时的兜底。
    /// 保留已有分类，只对新应用做猜测分类；顺便过滤掉目标已不存在的死链接。
    /// </summary>
    static class NativeScanner
    {
        static readonly string[] ExcludeNameParts = new string[]
        {
            "卸载", "Uninstall", "Help$", "帮助", "ReadMe", "FAQ", "Documentation",
            "Dokumentation", "Manuals", "User's Guide", "Release Notes", "Homepage",
            "Visit ", "关于", "About", "检查更新", "Check for Update", "配置 Java",
            "新机手册", "发送至", "产品改进", "向导", "Install Additional",
            "打开日志目录", "SnoreToast", "Administrative Tools"
        };

        public static bool Scan(string baseDir)
        {
            try
            {
                string cfg = Path.Combine(baseDir, "config");
                string appsPath = Path.Combine(cfg, "apps.json");

                Dictionary<string, string> oldCats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                List<string> order = new List<string>();

                string oldText = JsonIO.Read(appsPath);
                if (!string.IsNullOrEmpty(oldText))
                {
                    try
                    {
                        JavaScriptSerializer ser = new JavaScriptSerializer();
                        ser.MaxJsonLength = int.MaxValue;
                        Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(oldText);
                        object arr;
                        if (d != null && d.TryGetValue("order", out arr) && arr != null)
                        {
                            foreach (object o in (System.Collections.IEnumerable)arr) order.Add(Convert.ToString(o));
                        }
                        if (d != null && d.TryGetValue("apps", out arr) && arr != null)
                        {
                            foreach (object o in (System.Collections.IEnumerable)arr)
                            {
                                Dictionary<string, object> m = o as Dictionary<string, object>;
                                if (m == null) continue;
                                object pv;
                                if (!m.TryGetValue("path", out pv)) continue;
                                string p = Convert.ToString(pv);
                                if (string.IsNullOrEmpty(p)) continue;
                                string c = "";
                                object cv;
                                if (m.TryGetValue("category", out cv)) c = Convert.ToString(cv);
                                if (!oldCats.ContainsKey(p)) oldCats[p] = c;
                            }
                        }
                    }
                    catch { }
                }

                if (order.Count == 0)
                {
                    foreach (string c in Catalog.DefaultOrder) order.Add(c);
                }

                List<Dictionary<string, object>> apps = new List<Dictionary<string, object>>();
                HashSet<string> seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                string[] dirs = new string[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "Start Menu", "Programs")
                };

                foreach (string dir in dirs)
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    string[] files;
                    try { files = Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories); }
                    catch { continue; }

                    foreach (string file in files)
                    {
                        try
                        {
                            string name = Path.GetFileNameWithoutExtension(file);
                            if (IsExcluded(name)) continue;
                            if (!seenPaths.Add(file)) continue;
                            if (!seenNames.Add(name.ToLowerInvariant())) continue;
                            if (!IsLiveLnk(file)) continue;

                            string folder = "";
                            try
                            {
                                string d = Path.GetDirectoryName(file);
                                if (d != null && d.Length > dir.Length) folder = d.Substring(dir.Length).TrimStart('\\');
                            }
                            catch { }

                            string cat;
                            if (!oldCats.TryGetValue(file, out cat) || string.IsNullOrEmpty(cat))
                            {
                                cat = GuessCategory(name, folder);
                            }

                            Dictionary<string, object> m = new Dictionary<string, object>();
                            m["name"] = name;
                            m["path"] = file;
                            m["category"] = cat;
                            m["folder"] = folder;
                            apps.Add(m);
                        }
                        catch { }
                    }
                }

                apps.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
                {
                    int ia = order.IndexOf(Convert.ToString(a["category"]));
                    int ib = order.IndexOf(Convert.ToString(b["category"]));
                    if (ia < 0) ia = order.Count;
                    if (ib < 0) ib = order.Count;
                    if (ia != ib) return ia.CompareTo(ib);
                    return string.Compare(
                        Convert.ToString(a["name"]),
                        Convert.ToString(b["name"]),
                        System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
                        System.Globalization.CompareOptions.None);
                });

                Dictionary<string, object> root = new Dictionary<string, object>();
                root["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                root["order"] = order;
                root["apps"] = apps;

                JavaScriptSerializer ser2 = new JavaScriptSerializer();
                ser2.MaxJsonLength = int.MaxValue;
                JsonIO.WriteAtomic(appsPath, ser2.Serialize(root));
                ErrorLog.Log("内置 C# 扫描完成：" + apps.Count + " 个应用");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog.Hook(ex, "内置 C# 扫描");
                return false;
            }
        }

        /// <summary>删除 apps.json / manual-apps.json 里目标已不存在的条目（死链接）。</summary>
        public static void FilterDeadLinks(string baseDir)
        {
            try
            {
                string cfg = Path.Combine(baseDir, "config");
                string appsPath = Path.Combine(cfg, "apps.json");
                string manualPath = Path.Combine(cfg, "manual-apps.json");

                string appsText = JsonIO.Read(appsPath);
                if (!string.IsNullOrEmpty(appsText))
                {
                    string filtered = FilterJson(appsText);
                    if (filtered != null) JsonIO.WriteAtomic(appsPath, filtered);
                }

                string manualText = JsonIO.Read(manualPath);
                if (!string.IsNullOrEmpty(manualText))
                {
                    string filtered = FilterJson(manualText);
                    if (filtered != null) JsonIO.WriteAtomic(manualPath, filtered);
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Hook(ex, "清理失效应用");
            }
        }

        static string FilterJson(string text)
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(text);
                if (d == null) return null;
                object arr;
                if (!d.TryGetValue("apps", out arr) || arr == null) return null;

                List<object> keep = new List<object>();
                int total = 0;
                foreach (object o in (System.Collections.IEnumerable)arr)
                {
                    total++;
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m == null) { keep.Add(o); continue; }
                    object pv;
                    if (!m.TryGetValue("path", out pv)) { keep.Add(o); continue; }
                    string p = Convert.ToString(pv);
                    if (string.IsNullOrEmpty(p)) { keep.Add(o); continue; }

                    bool alive;
                    if (p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) alive = IsLiveLnk(p);
                    else alive = File.Exists(p);
                    if (alive) keep.Add(o);
                }
                if (keep.Count == total) return null;

                d["apps"] = keep;
                ErrorLog.Log("清理死链接：移除 " + (total - keep.Count) + " 个失效应用");
                return ser.Serialize(d);
            }
            catch { return null; }
        }

        static bool IsExcluded(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            foreach (string p in ExcludeNameParts)
            {
                string pat = p.EndsWith("$") ? p.Substring(0, p.Length - 1) : p;
                if (pat == "Help" && name.EndsWith("Help", StringComparison.OrdinalIgnoreCase)) return true;
                if (name.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        static string GuessCategory(string name, string folder)
        {
            string f = (folder == null ? "" : folder).ToLowerInvariant();
            if (f.Contains("office") || f.Contains("wps") || f.Contains("hp")) return "办公";
            if (f.Contains("visual studio") || f.Contains("jetbrains") || f.Contains("python") ||
                f.Contains("anaconda") || f.Contains("git") || f.Contains("node") || f.Contains("mysql") ||
                f.Contains("mongo") || f.Contains("xshell") || f.Contains("xftp") || f.Contains("sql"))
                return "开发";
            if (f.Contains("steam")) return "影音娱乐";
            if (f.Contains("potplayer") || f.Contains("mineradio") || f.Contains("剪映")) return "影音娱乐";
            if (f.Contains("微信") || f.Contains("腾讯") || f.Contains("迅雷") || f.Contains("百度") || f.Contains("karing"))
                return "网络通讯";
            return Catalog.GuessCategory(name);
        }

        static bool IsLiveLnk(string lnk)
        {
            try
            {
                object sc = CreateShortcut(lnk);
                if (sc == null) return true;
                object tp = sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null);
                string s = tp as string;
                if (string.IsNullOrEmpty(s)) return true;
                return File.Exists(s);
            }
            catch { return true; }
        }

        static object CreateShortcut(string lnk)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                object shell = Activator.CreateInstance(t);
                return t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            }
            catch { return null; }
        }
    }
}
