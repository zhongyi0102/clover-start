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

        /// <summary>来源：开始菜单 / 注册表 / 磁盘。用于排序与图标选择。</summary>
        public string Source;

        /// <summary>扩展名（小写，含点）。文件才有。</summary>
        public string Ext;
    }

    /// <summary>
    /// 系统级搜索（参考 GlassBoard 的两级设计）。
    ///
    /// **为什么要两级**：
    ///   · 用户期望"输入即出结果"，但遍历磁盘不可能瞬间完成
    ///   · 所以先给「已知的、廉价的」结果（开始菜单 + 注册表），
    ///     再在后台慢慢补磁盘文件，结果**流式追加**
    ///   · 用户感知是"立刻有结果，然后越来越多"
    ///
    /// 两级：
    ///   Instant → 开始菜单快捷方式 + 注册表 App Paths + 注册表 Uninstall
    ///             目标 < 20 ms（实测这台机器约 8-15 ms）
    ///   Deep    → 桌面/文档/图片/音乐/视频/下载 + 其他盘
    ///             后台线程，分批回调，可取消
    ///
    /// 防卡死的四个硬约束（都来自实测教训）：
    ///   · 总结果上限 120（再多用户也翻不到）
    ///   · 单根目录上限 80（避免一个巨大目录吃掉全部配额）
    ///   · 递归深度 5（够覆盖正常文件组织）
    ///   · CancellationToken：用户继续输入时立刻中断旧搜索，
    ///     否则多次输入会累积成多个并发遍历，把磁盘打满
    /// </summary>
    static class SystemSearch
    {
        /// <summary>总结果上限。</summary>
        const int MaxTotal = 120;

        /// <summary>单个根目录上限。</summary>
        const int MaxPerRoot = 80;

        /// <summary>递归深度上限。</summary>
        const int MaxDepth = 5;

        /// <summary>
        /// 深度搜索的**时间预算**（毫秒）。
        ///
        /// 为什么必须有它：结果数上限挡不住慢搜索。
        /// 实测搜"报告"时整盘只有 9 个匹配，永远达不到 MaxPerRoot=80，
        /// 于是遍历了 D: 和 E: 的全部目录（合计 300+ GB），耗时 **8545 ms**。
        ///
        /// 用户的心理预期是"输入后马上有反应"，超过 1 秒就会觉得卡。
        /// 所以按**时间**截断，而不是按结果数 —— 时间到了就停，
        /// 已经找到的照样交给界面（部分结果比没有好）。
        /// </summary>
        const int DeepBudgetMs = 900;

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

        /// <summary>
        /// 磁盘搜索结果里**明确排除**的扩展名。
        ///
        /// 为什么必须排除：实测搜 "chrome" 时，Deep 找到 60 条，
        /// 其中 58 条是噪音 ——
        ///   · `chrome_100_percent.pak`（Chromium 的资源包，几十个程序各带一份）
        ///   · `chrome_elf.dll`（同名 DLL）
        ///   · `chromehtml.dll`、`host-manifest-chrome.json`、`xl_ext_chrome.crx`
        /// 用户搜 "chrome" 想要的是浏览器，不是这些内部文件。
        ///
        /// 原则：磁盘搜索只返回**用户可能想打开的东西**（程序、文档、媒体、压缩包），
        /// 不返回程序内部资源。
        /// </summary>
        static readonly HashSet<string> JunkExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 程序内部资源
            ".dll", ".pak", ".bin", ".dat", ".json", ".xml", ".ini", ".cfg", ".conf",
            ".reg", ".backup", ".bak", ".tmp", ".log", ".db", ".sqlite", ".pyc",
            ".crx", ".pdb", ".lib", ".obj", ".res", ".manifest", ".lock", ".cache",
            ".so", ".dylib", ".a", ".h", ".hpp", ".c", ".cpp", ".cs", ".java",
            ".js", ".map", ".css", ".scss", ".less", ".ts", ".jsx", ".tsx",
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".webp",
            ".woff", ".woff2", ".ttf", ".otf", ".eot"
        };

        static void TryAdd(List<SearchResult> list, Dictionary<string, bool> seen,
                           string name, string path, bool isFolder, string[] words, string source)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) return;
                if (!Matches(name, path, words)) return;

                // 磁盘来源才做扩展名过滤；开始菜单/注册表的结果都是用户可见的程序
                string ext = isFolder ? "" : Path.GetExtension(path).ToLowerInvariant();
                if (source == "磁盘" && !isFolder && JunkExt.Contains(ext)) return;

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
        //  Deep：磁盘文件（后台流式）
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 深度搜索。**必须在后台线程调用**（会遍历磁盘）。
        ///
        /// 分批回调：每找到一批就交给 batch，让界面能边搜边显示。
        /// ct 被取消时立刻停止 —— 用户继续打字时旧搜索必须让路，
        /// 否则多次输入会累积成多个并发遍历把磁盘打满。
        /// </summary>
        public static void Deep(string query, Action<List<SearchResult>> batch, CancellationToken ct)
        {
            try
            {
                string[] words = SplitWords(query);
                if (words.Length == 0) return;

                Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                List<SearchResult> pending = new List<SearchResult>();
                int total = 0;
                int lastFlush = Environment.TickCount;
                int started = Environment.TickCount;

                foreach (string root in DeepRoots())
                {
                    if (ct.IsCancellationRequested || total >= MaxTotal) break;
                    if (Environment.TickCount - started > DeepBudgetMs) break;
                    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

                    int rootCount = 0;
                    Stack<KeyValuePair<string, int>> stack = new Stack<KeyValuePair<string, int>>();
                    stack.Push(new KeyValuePair<string, int>(root, 0));

                    while (stack.Count > 0)
                    {
                        if (ct.IsCancellationRequested || total >= MaxTotal || rootCount >= MaxPerRoot || Environment.TickCount - started > DeepBudgetMs) break;

                        KeyValuePair<string, int> cur = stack.Pop();
                        string dir = cur.Key;
                        int depth = cur.Value;
                        if (depth > MaxDepth) continue;

                        try
                        {
                            // 子目录
                            foreach (string sub in Directory.EnumerateDirectories(dir))
                            {
                                if (ct.IsCancellationRequested || total >= MaxTotal || rootCount >= MaxPerRoot || Environment.TickCount - started > DeepBudgetMs) break;
                                try
                                {
                                    // 跳过程序/系统内部目录：那里全是 .pak/.dll 之类，
                                    // 遍历它们只会拖慢搜索、制造噪音（实测占 58/60 条）
                                    if (ShouldSkipDir(sub)) continue;

                                    string nm = Path.GetFileName(sub);
                                    if (Matches(nm, sub, words))
                                    {
                                        int before = pending.Count;
                                        TryAdd(pending, seen, nm, sub, true, words, "磁盘");
                                        if (pending.Count > before) { total++; rootCount++; }
                                    }
                                    stack.Push(new KeyValuePair<string, int>(sub, depth + 1));
                                }
                                catch { }
                            }

                            // 文件
                            foreach (string f in Directory.EnumerateFiles(dir))
                            {
                                if (ct.IsCancellationRequested || total >= MaxTotal || rootCount >= MaxPerRoot || Environment.TickCount - started > DeepBudgetMs) break;
                                try
                                {
                                    string nm = Path.GetFileNameWithoutExtension(f);
                                    int before = pending.Count;
                                    TryAdd(pending, seen, nm, f, false, words, "磁盘");
                                    if (pending.Count > before) { total++; rootCount++; }
                                }
                                catch { }
                            }
                        }
                        catch { }

                        // 攒够一批或超过 120ms 就交给界面，避免"搜完了才一起显示"
                        int now = Environment.TickCount;
                        if (pending.Count > 0 && (pending.Count >= 12 || now - lastFlush > 120))
                        {
                            lastFlush = now;
                            try { if (batch != null) batch(pending); } catch { }
                            pending = new List<SearchResult>();
                        }
                    }
                }

                // 最后一批
                if (pending.Count > 0 && !ct.IsCancellationRequested)
                {
                    try { if (batch != null) batch(pending); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("深度搜索失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 深度搜索的根目录。
        ///
        /// 只扫**用户自己的目录**：桌面、文档、图片、音乐、视频、下载。
        /// 不扫 C:\Windows、Program Files 这类系统目录 ——
        /// 那里面的东西用户几乎不会"按名字找文件"，扫了只是浪费时间。
        /// </summary>
        static IEnumerable<string> DeepRoots()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            yield return GetDownloads();

            // 其他固定磁盘的根（D:\ E:\ ...）。
            //
            // 注意：这里不能用 try/catch 包 yield return —— C# 不允许
            // （CS1626）。所以先把盘符收集到列表里，再逐个 yield。
            List<string> drives = new List<string>();
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    string root = d.RootDirectory.FullName;
                    // C 盘太杂（系统 + 程序 + 用户数据混在一起），跳过
                    if (root.StartsWith("C:", StringComparison.OrdinalIgnoreCase)) continue;
                    drives.Add(root);
                }
            }
            catch { }

            foreach (string d in drives) yield return d;
        }

        /// <summary>
        /// 递归时**跳过**的目录名（小写比较）。
        ///
        /// 为什么需要：实测 Deep 搜 "chrome" 时，命中的绝大部分来自
        /// `Program Files\...\chrome_100_percent.pak` 这类程序内部资源。
        /// 那些目录用户根本不会"按名字找文件"，遍历它们纯属浪费
        /// 时间和制造噪音。
        ///
        /// 注意只跳过**目录名**，不跳过用户自己建的普通文件夹 ——
        /// 名单里都是明确的"程序/系统内部"目录。
        /// </summary>
        static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "program files", "program files (x86)", "programdata", "windows",
            "appdata", "node_modules", ".git", ".vs", ".vscode", ".idea",
            "bin", "obj", "packages", "cache", "temp", "tmp", "logs",
            "dist", "build", "target", "debug", "release", "lib", "libs",
            "resources", "locales", "plugins", "extensions", "cef",
            "$recycle.bin", "system volume information", "recovery",
            // 各类缓存目录：实测 E:\Caches 下有几十个 Chrome/VS Code 的
            // 缓存快照目录，名字里带 "chrome" 但完全是噪音
            "caches", "cache", ".cache", "cacheddata", "app_cache_staged_removals",
            "temp", "tmp", "logs", "crashpad", "gpucache", "code cache",
            "service worker", "blob_storage", "local storage", "session storage"
        };

        static bool ShouldSkipDir(string dir)
        {
            try
            {
                string name = Path.GetFileName(dir.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) return false;
                if (name.StartsWith(".")) return true;
                return SkipDirs.Contains(name);
            }
            catch { return true; }
        }

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
