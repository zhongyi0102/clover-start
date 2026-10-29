using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace Clover
{
    /// <summary>一条搜索结果。</summary>
    class SearchResult
    {
        public string Name;
        public string Path;
        public bool IsFolder;

        /// <summary>来源：开始菜单 / 注册表。用于排序与图标选择。</summary>
        public string Source;

        /// <summary>扩展名（小写，含点）。文件才有。</summary>
        public string Ext;
    }

    /// <summary>
    /// 系统级搜索：开始菜单快捷方式 + 注册表（App Paths / Uninstall）。
    ///
    /// 目标 < 20 ms（实测这台机器约 8-15 ms）。
    ///
    /// 只搜「可启动的程序」—— 磁盘文件搜索（原来的 Deep 阶段）已移除，
    /// 因为用户打开启动面板通常是想启动程序，不是找文件；
    /// 找文件用资源管理器更合适。
    /// </summary>
    static class SystemSearch
    {
        /// <summary>总结果上限。</summary>
        const int MaxTotal = 120;

        /// <summary>单个根目录上限。</summary>
        const int MaxPerRoot = 80;


        /// <summary>Instant 阶段的结果上限（开始菜单 + 注册表）。</summary>
        const int MaxInstant = 150;

        /// <summary>
        /// 优先展示的扩展名。
        ///
        /// 排序依据：用户搜东西时，"能启动的程序"和"能打开的文档"
        /// 远比"随便一个文件"有用。所以同分情况下这些排在前面。
        /// </summary>
        static readonly HashSet<string> PriorityExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".lnk", ".url", ".appref-ms", ".msi", ".bat", ".cmd",
            ".zip", ".rar", ".7z", ".pdf", ".docx", ".xlsx", ".pptx", ".txt"
        };

        /// <summary>搜索词按空格拆成多个词，全部命中才算匹配（AND 语义）。</summary>
        static string[] SplitWords(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new string[0];
            return query.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// 名字是否命中所有词。
        ///
        /// **只匹配名字**，不匹配完整路径。
        ///
        /// 为什么去掉路径匹配：实测输入 "d:" 时，路径匹配会把所有位于 D 盘的
        /// 注册表项都算命中 —— 结果里出现 `et`、`7zFM`、`code` 这些
        /// 名字跟 "d:" 毫无关系的条目，完全是噪音。
        /// 路径匹配听起来"更宽松更好用"，实际只会让结果不可信。
        ///
        /// 如果用户确实想按路径找，那属于"文件浏览"的范畴，
        /// 不是这个搜索框要解决的问题。
        /// </summary>
        static bool Matches(string name, string path, string[] words)
        {
            if (words == null || words.Length == 0) return false;
            if (string.IsNullOrEmpty(name)) return false;

            foreach (string w in words)
            {
                if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            return true;
        }

        static void TryAdd(List<SearchResult> list, Dictionary<string, bool> seen,
                           string name, string path, bool isFolder, string[] words, string source)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) return;
                if (!Matches(name, path, words)) return;

                // 磁盘来源才做扩展名过滤；开始菜单/注册表的结果都是用户可见的程序
                string ext = isFolder ? "" : Path.GetExtension(path).ToLowerInvariant();

                string key = path.ToLowerInvariant();
                if (seen.ContainsKey(key)) return;
                seen[key] = true;

                SearchResult r = new SearchResult();
                r.Name = name;
                r.Path = path;
                r.IsFolder = isFolder;
                r.Source = source;
                r.Ext = ext;
                list.Add(r);
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════
        //  Instant：开始菜单 + 注册表
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 即时搜索。同步、快速（目标 &lt; 20 ms）。
        ///
        /// 覆盖三类"已安装但可能没被扫描进清单"的程序：
        ///   1. 开始菜单快捷方式（.lnk / .url / .appref-ms）
        ///   2. 注册表 App Paths（很多程序在这里注册，但不一定有开始菜单项）
        ///   3. 注册表 Uninstall（安装记录里带 DisplayIcon / InstallLocation）
        /// </summary>
        public static List<SearchResult> Instant(string query)
        {
            List<SearchResult> list = new List<SearchResult>();
            try
            {
                string[] words = SplitWords(query);
                if (words.Length == 0) return list;

                Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

                // 1) 开始菜单
                foreach (string dir in StartMenuDirs())
                {
                    CollectShortcuts(dir, words, list, seen, 0);
                }

                // 2) 注册表 App Paths
                try { CollectAppPaths(Registry.LocalMachine, words, list, seen); } catch { }
                try { CollectAppPaths(Registry.CurrentUser, words, list, seen); } catch { }

                // 3) 注册表 Uninstall（只取能解析出 exe 的）
                try { CollectUninstall(Registry.LocalMachine, words, list, seen); } catch { }
                try { CollectUninstall(Registry.CurrentUser, words, list, seen); } catch { }

                SortResults(list);
            }
            catch (Exception ex)
            {
                Log.Warn("即时搜索失败: " + ex.Message);
            }
            return list;
        }

        static IEnumerable<string> StartMenuDirs()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        }

        static void CollectShortcuts(string dir, string[] words, List<SearchResult> list,
                                     Dictionary<string, bool> seen, int depth)
        {
            if (depth > 3 || list.Count >= MaxInstant) return;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    if (list.Count >= MaxInstant) return;
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".lnk" || ext == ".url" || ext == ".appref-ms")
                    {
                        TryAdd(list, seen, Path.GetFileNameWithoutExtension(file), file, false, words, "开始菜单");
                    }
                }
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    CollectShortcuts(sub, words, list, seen, depth + 1);
                }
            }
            catch { }
        }

        static void CollectAppPaths(RegistryKey root, string[] words, List<SearchResult> list,
                                    Dictionary<string, bool> seen)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"))
                {
                    if (key == null) return;
                    foreach (string name in key.GetSubKeyNames())
                    {
                        if (list.Count >= MaxInstant) return;
                        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            using (RegistryKey sub = key.OpenSubKey(name))
                            {
                                if (sub == null) continue;
                                string path = sub.GetValue(null) as string;
                                if (!string.IsNullOrWhiteSpace(path))
                                {
                                    // App Paths 的值可能带引号
                                    path = path.Trim('"').Trim();
                                    TryAdd(list, seen, Path.GetFileNameWithoutExtension(name), path, false, words, "注册表");
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        static void CollectUninstall(RegistryKey root, string[] words, List<SearchResult> list,
                                     Dictionary<string, bool> seen)
        {
            string[] paths = new string[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (string subPath in paths)
            {
                try
                {
                    using (RegistryKey key = root.OpenSubKey(subPath))
                    {
                        if (key == null) continue;
                        foreach (string name in key.GetSubKeyNames())
                        {
                            if (list.Count >= MaxInstant) return;
                            try
                            {
                                using (RegistryKey sub = key.OpenSubKey(name))
                                {
                                    if (sub == null) continue;
                                    string display = sub.GetValue("DisplayName") as string;
                                    if (string.IsNullOrWhiteSpace(display)) continue;
                                    if (!Matches(display, display, words)) continue;

                                    // 找可执行路径：优先 DisplayIcon（通常是 exe 或 ico）
                                    string exe = null;
                                    string icon = sub.GetValue("DisplayIcon") as string;
                                    if (!string.IsNullOrWhiteSpace(icon))
                                    {
                                        string p = icon.Trim('"').Trim();
                                        int comma = p.LastIndexOf(',');
                                        if (comma > 0) p = p.Substring(0, comma);
                                        if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
                                            exe = p;
                                    }
                                    // 退而求其次：InstallLocation 下的主 exe（只猜同名的一个）
                                    if (exe == null)
                                    {
                                        string loc = sub.GetValue("InstallLocation") as string;
                                        if (!string.IsNullOrWhiteSpace(loc))
                                        {
                                            loc = loc.Trim('"').Trim();
                                            if (Directory.Exists(loc))
                                            {
                                                string guess = Path.Combine(loc, name + ".exe");
                                                if (File.Exists(guess)) exe = guess;
                                            }
                                        }
                                    }
                                    if (exe != null)
                                        TryAdd(list, seen, display, exe, false, words, "注册表");
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }

        // ════════════════════════════════════════════════════════════
        // ════════════════════════════════════════════════════════════




        static string GetDownloads()
        {
            try
            {
                // .NET Framework 没有 SpecialFolder.Downloads，用已知路径拼
                string p = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                return p;
            }
            catch { return null; }
        }

        // ════════════════════════════════════════════════════════════
        //  排序
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 排序：来源优先 → 优先扩展名 → 名字长度 → 字母序。
        ///
        /// 为什么按"名字长度"：搜 "chr" 时，用户多半想要 "Chrome" 而不是
        /// "ChromeDriver-manager-config-tool"。短名字通常更接近用户意图。
        /// </summary>
        static void SortResults(List<SearchResult> list)
        {
            try
            {
                list.Sort(delegate(SearchResult a, SearchResult b)
                {
                    int sa = SourceRank(a), sb = SourceRank(b);
                    if (sa != sb) return sa.CompareTo(sb);

                    int ea = PriorityExt.Contains(a.Ext) ? 0 : 1;
                    int eb = PriorityExt.Contains(b.Ext) ? 0 : 1;
                    if (ea != eb) return ea.CompareTo(eb);

                    int la = a.Name == null ? 0 : a.Name.Length;
                    int lb = b.Name == null ? 0 : b.Name.Length;
                    if (la != lb) return la.CompareTo(lb);

                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
            }
            catch { }
        }

        static int SourceRank(SearchResult r)
        {
            if (r == null) return 9;
            switch (r.Source)
            {
                case "开始菜单": return 0;
                case "注册表": return 1;
                default: return 2;
            }
        }
    }
}
