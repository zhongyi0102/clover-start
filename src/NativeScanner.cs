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
            "Dokumentation", "Manuals", "User’s Guide", "User's Guide", "Release Notes", "Homepage",
            "Visit ", "关于", "About", "检查更新", "Check for Update", "配置 Java",
            "新机手册", "发送至", "产品改进", "向导", "Install Additional",
            "打开日志目录", "SnoreToast", "Administrative Tools",
            // 自我引用：不该把启动面板自己列进面板
            "四叶草启动器", "CloverStart"
        };

        public static bool Scan(string baseDir)
        {
            try
            {
                string cfg = Path.Combine(baseDir, "config");
                string appsPath = Path.Combine(cfg, "apps.json");

                List<string> order = new List<string>();

                // 只从旧文件里读 order（分类顺序），不再读 category。
                // 分类每次按当前规则重判，见下方 GuessCategory 调用处的说明。
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

                            // 每次都按当前规则重新判定分类。
                            //
                            // 以前这里会保留 apps.json 里的旧分类（oldCats），本意是
                            // "别覆盖用户改过的"，但实际上用户手工调整的条目都在
                            // manual-apps.json 里，这里扫描的是开始菜单自动发现的，
                            // 保留旧值只会让规则改进永远不生效。
                            string cat = GuessCategory(name, folder);

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

            // 工具/命令行优先：命令行、驱动、Module Docs、Reset、帮助文档
            // 放在最前，否则会被下面的「开发」等目录规则抢走。
            if (Catalog.IsToolLike(name == null ? "" : name.ToLowerInvariant()))
                return Catalog.ToolCategory;

            // 注意：这里曾经用 f.Contains("hp") 把 HP 打印机的 4 个工具判成「办公」，
            // 但 HP 是打印机制造商，不是办公套件。已移除，改由名称规则归「系统工具」。
            if (f.Contains("office") || f.Contains("wps")) return "办公";
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
