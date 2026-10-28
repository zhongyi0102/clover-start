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

                // 必须先加载分类规则表。
                // 这里是 --rescan（无界面）的入口，它直接调 GuessCategory 而不经过
                // Catalog.Load；漏了这一步会让规则表在重扫时完全不生效。
                CategoryRules.Init(baseDir);

                List<string> order = new List<string>();

                // 只从旧文件里读 order（分类顺序），不再读 category。
                // 分类每次按当前规则重判，见下方 GuessCategory 调用处的说明。
                // 上一轮由 PortableScanner 补进来的便携软件条目，先捡出来。
                //
                // 为什么必须保留：本方法是 apps.json 的**唯一写入者**，但它只扫开始菜单，
                // 一个便携软件都不认识。若不保留，每轮扫描都会把便携条目整批删掉，
                // 只能靠随后的 PortableScanner.Merge 再补回来 ——
                // 而 Merge 一旦超时中止（60 秒上限 / 8000 目录上限）就**不写文件**，
                // 那几条会悄无声息地从面板里消失，直到某次完整扫描成功。
                // 实测过：扫前 119 个（含 7 个便携），只跑 Scan 后剩 112 个，便携归零。
                List<Dictionary<string, object>> portableKept = new List<Dictionary<string, object>>();

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
                            foreach (string s in JsonIO.AsStringArray(arr)) order.Add(s);
                        }

                        object oldApps;
                        if (d != null && d.TryGetValue("apps", out oldApps) && oldApps != null)
                        {
                            foreach (object o in JsonIO.AsArray(oldApps))
                            {
                                Dictionary<string, object> m = o as Dictionary<string, object>;
                                if (m == null) continue;
                                if (IsPortableEntry(m)) portableKept.Add(m);
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

                // 把上一轮的便携条目并回来。
                //
                // 分类按当前规则重判（与开始菜单条目一致）—— 这样改进了分类规则表之后，
                // 便携条目的分类也会跟着更新，而不是永远停在首次识别时的结果。
                // 用 Catalog.GuessCategory(name) 而不是 GuessCategory(name, folder)：
                // 前者正是 PortableScanner.Merge 识别它们时用的那个，保持一致。
                foreach (Dictionary<string, object> m in portableKept)
                {
                    string name = Convert.ToString(m.ContainsKey("name") ? m["name"] : null);
                    string path = Convert.ToString(m.ContainsKey("path") ? m["path"] : null);
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) continue;

                    // 路径或名字已被这轮开始菜单扫描收录：不要重复（开始菜单的优先）
                    if (seenPaths.Contains(path)) continue;
                    if (seenNames.Contains(name.ToLowerInvariant())) continue;

                    // 目标文件已不在了（用户删了那个便携软件）：自然淘汰，不保留死条目
                    try { if (!File.Exists(path)) continue; } catch { continue; }

                    m["category"] = Catalog.GuessCategory(name);
                    apps.Add(m);
                    seenPaths.Add(path);
                    seenNames.Add(name.ToLowerInvariant());
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
                Log.Info("内置 C# 扫描完成：" + apps.Count + " 个应用");
                return true;
            }
            catch (Exception ex)
            {
                Log.Hook(ex, "内置 C# 扫描");
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
                Log.Hook(ex, "清理失效应用");
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
                foreach (object o in JsonIO.AsArray(arr))
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
                Log.Info("清理死链接：移除 " + (total - keep.Count) + " 个失效应用");
                return ser.Serialize(d);
            }
            catch { return null; }
        }

        /// <summary>
        /// 这个条目是不是便携软件识别出来的。
        ///
        /// 判据是 folder 标记（PortableScanner 写入 PortableScanner.FolderMarker），
        /// 不是"路径是否在某个目录下"——因为用户可以用 portable-roots.json
        /// 把扫描根指到任意盘符，路径特征不可靠。
        /// </summary>
        static bool IsPortableEntry(Dictionary<string, object> m)
        {
            if (m == null) return false;
            object f;
            if (!m.TryGetValue("folder", out f) || f == null) return false;
            return string.Equals(Convert.ToString(f), PortableScanner.FolderMarker, StringComparison.Ordinal);
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
            if (Catalog.IsToolLike(NameMatch.Fold(name)))
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
