using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// 识别“没有开始菜单/桌面快捷方式”的软件（Green/便携版，例如 geek.exe 这种直接解压就用的）。
    /// 做法：扫描常见软件目录（以及 %ProgramFiles%、%LOCALAPPDATA%\Programs），
    /// 每个含主程序的文件夹挑出一个主 exe 作为应用，追加进 apps.json。
    /// 可选的根目录配置在 config\portable-roots.json：{"roots":["D:\\Software\\Portable"]}。
    /// </summary>
    static class PortableScanner
    {
        /// <summary>
        /// 便携条目的 folder 标记。
        /// 提成常量是因为 NativeScanner 要靠它把上一轮的便携条目认回来
        /// （否则每轮扫描都会把它们整批抹掉），两处字面量必须一致。
        /// </summary>
        public const string FolderMarker = "便携识别";

        const int MaxDepth = 5;
        const int MaxDirs = 8000;
        const long MinExeSize = 200 * 1024;
        const int MaxApps = 200;

        /// <summary>单次扫描最多处理多少个文件条目（防止超大目录树拖慢系统）。</summary>
        const int MaxFiles = 200000;

        /// <summary>单次扫描总超时（毫秒），超时就中止并写日志。</summary>
        const int ScanTimeoutMs = 60000;

        /// <summary>本次扫描的开始时间与已处理文件数，用于限流。</summary>
        static DateTime scanStart;
        static int fileCount;
        static bool aborted;

        class Cand
        {
            public string Name;
            public string Path;
        }

        // exe 文件名里出现这些词就不是应用本体（卸载器、安装器、更新器、崩溃上报……）
        static readonly string[] JunkNameParts = new string[]
        {
            "unins", "uninst", "setup", "install", "update", "upgrade", "patch",
            "crash", "report", "helper", "vcredist", "redist", "dxsetup",
            "telemetry", "updater", "卸载", "安装", "更新", "升级", "反馈"
        };

        // 描述里出现这些词说明是安装/卸载程序（注意：不能用 install/uninstall，
        // 否则像 "Geek Uninstaller" 这种正牌应用会被误杀）
        static readonly string[] JunkDescParts = new string[]
        {
            "安装程序", "安装向导", "卸载程序", "卸载向导", "setup",
            "crash reporter", "安装包"
        };

        static readonly string[] SkipDirs = new string[]
        {
            "$recycle.bin", "system volume information", "windows", "windowsapps",
            "installer", "installers", "backup", "备份", "cache", "temp", "tmp",
            "node_modules", "driverstore", "drivers", "servicing", "packages", "logs",
            "logs2", "日志"
        };

        /// <summary>把识别到的便携/无快捷方式软件补进 apps.json，返回新增数量。</summary>
        public static int Merge(string baseDir)
        {
            scanStart = DateTime.Now;
            fileCount = 0;
            aborted = false;
            try
            {
                string appsPath = Path.Combine(baseDir, "config", "apps.json");
                string text = JsonIO.Read(appsPath);
                if (string.IsNullOrEmpty(text)) return 0;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> root = ser.Deserialize<Dictionary<string, object>>(text);
                if (root == null) return 0;

                List<object> apps = new List<object>();
                object arrObj;
                if (root.TryGetValue("apps", out arrObj) && arrObj != null)
                {
                    foreach (object o in JsonIO.AsArray(arrObj)) apps.Add(o);
                }
                List<string> order = new List<string>();
                if (root.TryGetValue("order", out arrObj) && arrObj != null)
                {
                    foreach (string s in JsonIO.AsStringArray(arrObj)) order.Add(s);
                }
                if (order.Count == 0)
                {
                    foreach (string c in Catalog.DefaultOrder) order.Add(c);
                }

                HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (object o in apps)
                {
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m == null) continue;
                    paths.Add(Str(m, "path"));
                    names.Add(Str(m, "name"));
                }

                List<string> hidden = LoadHidden(baseDir);

                List<string> normNames = new List<string>();
                foreach (object o in apps)
                {
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m == null) continue;
                    string n = Norm(Str(m, "name"));
                    if (n.Length > 0) normNames.Add(n);
                }

                List<Cand> found = new List<Cand>();
                int visited = 0;
                foreach (string r in Roots(baseDir)) Walk(r, 1, found, ref visited);

                int added = 0;
                foreach (Cand c in found)
                {
                    if (added >= MaxApps) break;
                    if (string.IsNullOrEmpty(c.Path) || string.IsNullOrEmpty(c.Name)) continue;
                    if (!paths.Add(c.Path)) continue;

                    string name = c.Name.Trim();
                    if (names.Contains(name)) continue;
                    if (NameDup(name, normNames)) continue;
                    names.Add(name);
                    normNames.Add(Norm(name));

                    string cat = Catalog.GuessCategory(name);
                    if (hidden.Contains(cat)) cat = "其他";

                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["name"] = name;
                    m["path"] = c.Path;
                    m["category"] = cat;
                    m["folder"] = FolderMarker;
                    apps.Add(m);
                    added++;
                }

                if (added == 0) return 0;

                apps.Sort(delegate(object x, object y)
                {
                    Dictionary<string, object> a = x as Dictionary<string, object>;
                    Dictionary<string, object> b = y as Dictionary<string, object>;
                    if (a == null || b == null) return 0;
                    int ia = order.IndexOf(Str(a, "category"));
                    int ib = order.IndexOf(Str(b, "category"));
                    if (ia < 0) ia = order.Count;
                    if (ib < 0) ib = order.Count;
                    if (ia != ib) return ia.CompareTo(ib);
                    return string.Compare(Str(a, "name"), Str(b, "name"),
                        System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
                        System.Globalization.CompareOptions.None);
                });

                root["apps"] = apps;
                root["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                JsonIO.WriteAtomic(appsPath, ser.Serialize(root));
                Log.Info("便携软件识别：新增 " + added + " 个（扫描目录 " + visited + " 个，候选 " + found.Count + " 个）");
                return added;
            }
            catch (Exception ex)
            {
                Log.Hook(ex, "便携软件识别");
                return 0;
            }
        }

        static void Walk(string dir, int depth, List<Cand> found, ref int visited)
        {
            if (depth > MaxDepth || visited >= MaxDirs) return;
            if (ShouldAbort()) return;
            visited++;
            try
            {
                if (SkipDir(dir)) return;

                string main = PickMainExe(dir);
                if (main != null)
                {
                    found.Add(new Cand { Name = DisplayName(main), Path = main });
                    return; // 这个文件夹就是一个软件，不再往下钻
                }

                string[] subs;
                try { subs = Directory.GetDirectories(dir); } catch { return; }
                foreach (string s in subs)
                {
                    if (ShouldAbort()) return;
                    Walk(s, depth + 1, found, ref visited);
                }
            }
            catch { }
        }

        /// <summary>
        /// 扫描限流：超过总文件数上限或总超时就中止本次扫描（已找到的结果仍然保留）。
        /// 防止目录异常庞大时长时间占用磁盘 IO 拖慢系统。
        /// </summary>
        static bool ShouldAbort()
        {
            if (aborted) return true;

            if (fileCount >= MaxFiles)
            {
                aborted = true;
                Log.Warn("便携软件扫描中止：文件数超过上限 " + MaxFiles + "，本次只处理已扫到的部分");
                return true;
            }

            if ((DateTime.Now - scanStart).TotalMilliseconds > ScanTimeoutMs)
            {
                aborted = true;
                Log.Warn("便携软件扫描中止：超过 " + (ScanTimeoutMs / 1000) + " 秒超时限制，本次只处理已扫到的部分");
                return true;
            }
            return false;
        }

        /// <summary>挑出这个文件夹里的主程序；容器目录（多个候选又看不出主次）返回 null 继续往下找。</summary>
        static string PickMainExe(string dir)
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "*.exe"); } catch { return null; }

            fileCount += files.Length;
            if (ShouldAbort()) return null;

            List<string> cands = new List<string>();
            foreach (string f in files)
            {
                try
                {
                    FileInfo fi = new FileInfo(f);
                    if (fi.Length < MinExeSize) continue;
                    if (IsJunk(fi.Name, fi.FullName)) continue;
                    cands.Add(f);
                }
                catch { }
            }
            if (cands.Count == 0) return null;
            if (cands.Count == 1) return cands[0];

            // 多个候选：优先与目录同名的（允许带版本号后缀）
            string dn = Norm(Path.GetFileName(dir));
            foreach (string c in cands)
            {
                string en = Norm(Path.GetFileNameWithoutExtension(c));
                if (en.Length == 0) continue;
                if (dn.StartsWith(en, StringComparison.OrdinalIgnoreCase) ||
                    en.StartsWith(dn, StringComparison.OrdinalIgnoreCase)) return c;
            }

            // 最大的那个通常才是本体（例如 xxx.exe + 小工具.exe）
            string best = null;
            long bestSize = 0;
            foreach (string c in cands)
            {
                try
                {
                    long len = new FileInfo(c).Length;
                    if (len > bestSize) { bestSize = len; best = c; }
                }
                catch { }
            }
            return best;
        }

        static string DisplayName(string exePath)
        {
            string desc = "";
            try { desc = Convert.ToString(FileVersionInfo.GetVersionInfo(exePath).FileDescription); } catch { }
            if (!string.IsNullOrEmpty(desc) && desc.Trim().Length > 1 && !Matches(desc, JunkDescParts)) return desc.Trim();

            string baseName = Path.GetFileNameWithoutExtension(exePath);
            if (!Matches(baseName, JunkNameParts)) return baseName;

            try
            {
                string parent = Path.GetFileName(Path.GetDirectoryName(exePath));
                if (!string.IsNullOrEmpty(parent)) return parent;
            }
            catch { }
            return baseName;
        }

        static string Str(Dictionary<string, object> m, string key)
        {
            object v;
            if (m != null && m.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return "";
        }

        static bool IsJunk(string fileName, string fullPath)
        {
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            if (Matches(baseName, JunkNameParts)) return true;

            string desc = "";
            try { desc = Convert.ToString(FileVersionInfo.GetVersionInfo(fullPath).FileDescription); } catch { }
            if (!string.IsNullOrEmpty(desc) && Matches(desc, JunkDescParts)) return true;
            return false;
        }

        static bool Matches(string text, string[] parts)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.ToLowerInvariant();
            foreach (string p in parts)
            {
                if (t.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>名称去重：归一化后相同，或一方包含另一方（PotPlayer / PotPlayer 64 bit）。</summary>
        static bool NameDup(string name, List<string> normNames)
        {
            string n = Norm(name);
            if (n.Length < 3) return false;
            foreach (string e in normNames)
            {
                if (e.Length < 3) continue;
                if (n == e) return true;
                if (n.Length >= 4 && e.Length >= 4 && (n.IndexOf(e, StringComparison.Ordinal) >= 0 || e.IndexOf(n, StringComparison.Ordinal) >= 0))
                    return true;
            }
            return false;
        }

        static bool SkipDir(string dir)
        {
            try
            {
                string name = Path.GetFileName(dir.TrimEnd('\\', '/')).ToLowerInvariant();
                if (name.Length == 0) return false;
                if (name.StartsWith(".")) return true;
                foreach (string s in SkipDirs)
                {
                    if (name == s || name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                return false;
            }
            catch { return true; }
        }

        static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        static List<string> LoadHidden(string baseDir)
        {
            List<string> list = new List<string>();
            try
            {
                string path = Path.Combine(baseDir, "config", "settings.json");
                if (!File.Exists(path)) return list;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(JsonIO.Read(path));
                object arr;
                if (d != null && d.TryGetValue("hiddenCategories", out arr) && arr != null)
                {
                    foreach (string s in JsonIO.AsStringArray(arr)) list.Add(s);
                }
            }
            catch { }
            return list;
        }

        /// <summary>扫描根目录：优先 config\portable-roots.json，否则自动推断常见软件目录。</summary>
        static List<string> Roots(string baseDir)
        {
            List<string> list = new List<string>();
            string cfgPath = Path.Combine(baseDir, "config", "portable-roots.json");
            if (File.Exists(cfgPath))
            {
                try
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    ser.MaxJsonLength = int.MaxValue;
                    Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(JsonIO.Read(cfgPath));
                    object arr;
                    if (d != null && d.TryGetValue("roots", out arr) && arr != null)
                    {
                        foreach (object o in JsonIO.AsArray(arr))
                        {
                            string p = Convert.ToString(o);
                            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) list.Add(p);
                        }
                    }
                }
                catch { }
            }

            if (list.Count == 0)
            {
                // 只扫“软件收藏”类目录：Program Files 里的程序基本都有开始菜单快捷方式，
                // 扫它只会带进一堆服务/OEM 组件，所以默认不扫（需要时可写进 portable-roots.json）。
                string[] names = new string[] { "Software", "Portable", "Programs", "Apps", "Tools", "绿色软件", "软件", "应用" };
                try
                {
                    foreach (DriveInfo d in DriveInfo.GetDrives())
                    {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                        foreach (string n in names) Add(list, Path.Combine(d.RootDirectory.FullName, n));
                    }
                }
                catch { }
            }

            // 去掉被其它根包含的根，避免重复扫描
            List<string> result = new List<string>();
            foreach (string p in list)
            {
                bool nested = false;
                foreach (string q in list)
                {
                    if (string.Equals(p, q, StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.StartsWith(q.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) { nested = true; break; }
                }
                if (!nested) result.Add(p);
            }
            return result;
        }

        static void Add(List<string> list, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (!Directory.Exists(path)) return;
                foreach (string p in list)
                {
                    if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) return;
                }
                list.Add(path);
            }
            catch { }
        }
    }
}
