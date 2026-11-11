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

        /// <summary>来源：已安装 / 开始菜单 / 注册表。作为行内说明与最终同分项。</summary>
        public string Source;

        /// <summary>扩展名（小写，含点）。文件才有。</summary>
        public string Ext;

        /// <summary>真实启动次数；系统补充结果由界面按路径补齐。</summary>
        public int Usage;

        /// <summary>最近一次真实启动的 UTC ticks。</summary>
        public long LastUsed;

        /// <summary>是否被用户手动固定到「常用」。</summary>
        public bool Pinned;
    }

    /// <summary>
    /// 系统级搜索：开始菜单快捷方式 + 注册表（App Paths / Uninstall）。
    ///
    /// 热索引查询目标 &lt; 20 ms；首次索引构建由启动预热或后台搜索线程承担。
    ///
    /// 只搜「可启动的程序」：用户打开启动面板通常是想启动程序，不是找文件。
    /// </summary>
    static class SystemSearch
    {
        /// <summary>Instant 阶段的结果上限（开始菜单 + 注册表）。</summary>
        const int MaxInstant = 150;

        static readonly object indexGate = new object();
        static List<SearchResult> index;
        static int indexGeneration;
        static bool indexBuilding;
        static int buildingGeneration;
        static Func<List<SearchResult>> indexBuilder = delegate { return BuildIndex(); };

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
            return SearchIndex(query, GetOrBuildIndex());
        }

        /// <summary>
        /// 在内存索引中匹配。索引首次构建后，后续每次按键不再递归开始菜单或遍历注册表。
        /// </summary>
        static List<SearchResult> SearchIndex(string query, List<SearchResult> source)
        {
            List<SearchResult> list = new List<SearchResult>();
            try
            {
                string[] words = SplitWords(query);
                if (words.Length == 0) return list;

                Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

                if (source != null)
                {
                    foreach (SearchResult r in source)
                    {
                        if (r == null) continue;
                        TryAdd(list, seen, r.Name, r.Path, r.IsFolder, words, r.Source);
                    }
                }

                SearchRanking.Sort(list, query);
                if (list.Count > MaxInstant)
                    list.RemoveRange(MaxInstant, list.Count - MaxInstant);
            }
            catch (Exception ex)
            {
                Log.Warn("即时搜索失败: " + ex.Message);
            }
            return list;
        }

        static List<SearchResult> GetOrBuildIndex()
        {
            while (true)
            {
                int ownGeneration;
                lock (indexGate)
                {
                    if (index != null) return index;
                    if (indexBuilding)
                    {
                        // 等待当前单飞构建。超时宁可本次返回空补充结果，也不并发
                        // 遍历开始菜单/注册表；下一次输入仍可继续命中正在构建的任务。
                        if (!Monitor.Wait(indexGate, 5000)) return new List<SearchResult>();
                        continue;
                    }
                    indexBuilding = true;
                    buildingGeneration = indexGeneration;
                    ownGeneration = buildingGeneration;
                }

                List<SearchResult> built = null;
                bool succeeded = false;
                try
                {
                    Func<List<SearchResult>> builder;
                    lock (indexGate) { builder = indexBuilder; }
                    built = builder == null ? BuildIndex() : builder();
                    if (built == null) built = new List<SearchResult>();
                    succeeded = true;
                }
                catch (Exception ex)
                {
                    Log.Warn("系统搜索索引构建失败: " + ex.Message);
                }

                bool invalidated;
                lock (indexGate)
                {
                    // 只有自己认领的 generation 能收尾。旧 worker 绝不能清掉
                    // 失效后由别的调用者启动的新构建状态。
                    if (indexBuilding && buildingGeneration == ownGeneration)
                    {
                        if (succeeded && ownGeneration == indexGeneration && index == null)
                            index = built;
                        invalidated = ownGeneration != indexGeneration;
                        indexBuilding = false;
                        Monitor.PulseAll(indexGate);
                    }
                    else
                    {
                        invalidated = false;
                    }
                    if (index != null) return index;
                }

                // 构建期间输入失效：当前查询继续认领最新 generation；真正失败则
                // 返回空结果，避免异常环境下无休止重试。
                if (!invalidated || !succeeded) return new List<SearchResult>();
            }
        }

        static List<SearchResult> BuildIndex()
        {
            List<SearchResult> list = new List<SearchResult>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in StartMenuDirs()) CollectShortcuts(dir, list, seen, 0);
            try { CollectAppPaths(Registry.LocalMachine, list, seen); } catch { }
            try { CollectAppPaths(Registry.CurrentUser, list, seen); } catch { }
            try { CollectUninstall(Registry.LocalMachine, list, seen); } catch { }
            try { CollectUninstall(Registry.CurrentUser, list, seen); } catch { }
            return list;
        }

        /// <summary>启动后后台预热搜索索引，不阻塞四叶草首帧。</summary>
        public static void WarmAsync()
        {
            int ownGeneration;
            lock (indexGate)
            {
                if (index != null || indexBuilding) return;
                indexBuilding = true;
                buildingGeneration = indexGeneration;
                ownGeneration = buildingGeneration;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<SearchResult> built = null;
                bool succeeded = false;
                try
                {
                    Func<List<SearchResult>> builder;
                    lock (indexGate) { builder = indexBuilder; }
                    built = builder == null ? BuildIndex() : builder();
                    if (built == null) built = new List<SearchResult>();
                    succeeded = true;
                }
                catch (Exception ex) { Log.Warn("系统搜索索引预热失败: " + ex.Message); }
                finally
                {
                    bool retryLatest = false;
                    lock (indexGate)
                    {
                        if (indexBuilding && buildingGeneration == ownGeneration)
                        {
                            if (succeeded && ownGeneration == indexGeneration && index == null)
                                index = built;
                            retryLatest = ownGeneration != indexGeneration;
                            indexBuilding = false;
                            Monitor.PulseAll(indexGate);
                        }
                    }
                    // 预热期间开始菜单变化时，旧结果丢弃后自动为最新一代补跑；
                    // 若已有查询线程先认领，WarmAsync 会看到 single-flight 并直接返回。
                    if (retryLatest) WarmAsync();
                }
                if (succeeded)
                    Log.Diagnostic("系统搜索索引已预热：" + built.Count + " 条");
            });
        }

        /// <summary>开始菜单或完整扫描变化后失效；下一次搜索/预热会重建。</summary>
        public static void Invalidate()
        {
            lock (indexGate)
            {
                index = null;
                unchecked { indexGeneration++; }
            }
        }

        internal static int IndexedCount
        {
            get { lock (indexGate) { return index == null ? 0 : index.Count; } }
        }

        /// <summary>测试接缝：注入可阻塞构建器，覆盖失效与 single-flight 竞态。</summary>
        internal static void SetIndexBuilderForTest(Func<List<SearchResult>> builder)
        {
            lock (indexGate)
            {
                if (indexBuilding) throw new InvalidOperationException("索引正在构建");
                indexBuilder = builder ?? delegate { return BuildIndex(); };
                index = null;
                unchecked { indexGeneration++; }
            }
        }

        static IEnumerable<string> StartMenuDirs()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        }

        static void CollectShortcuts(string dir, List<SearchResult> list,
                                     Dictionary<string, bool> seen, int depth)
        {
            if (depth > 3) return;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".lnk" || ext == ".url" || ext == ".appref-ms")
                    {
                        AddIndexed(list, seen, Path.GetFileNameWithoutExtension(file), file,
                            false, "开始菜单");
                    }
                }
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    CollectShortcuts(sub, list, seen, depth + 1);
                }
            }
            catch { }
        }

        static void CollectAppPaths(RegistryKey root, List<SearchResult> list,
                                    Dictionary<string, bool> seen)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"))
                {
                    if (key == null) return;
                    foreach (string name in key.GetSubKeyNames())
                    {
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
                                    AddIndexed(list, seen, Path.GetFileNameWithoutExtension(name), path,
                                        false, "注册表");
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        static void CollectUninstall(RegistryKey root, List<SearchResult> list,
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
                            try
                            {
                                using (RegistryKey sub = key.OpenSubKey(name))
                                {
                                    if (sub == null) continue;
                                    string display = sub.GetValue("DisplayName") as string;
                                    if (string.IsNullOrWhiteSpace(display)) continue;
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
                                        AddIndexed(list, seen, display, exe, false, "注册表");
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }

        static void AddIndexed(List<SearchResult> list, Dictionary<string, bool> seen,
            string name, string path, bool isFolder, string source)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) return;
                if (seen.ContainsKey(path)) return;
                seen[path] = true;
                list.Add(new SearchResult
                {
                    Name = name,
                    Path = path,
                    IsFolder = isFolder,
                    Source = source,
                    Ext = isFolder ? "" : Path.GetExtension(path).ToLowerInvariant(),
                });
            }
            catch { }
        }

    }
}
