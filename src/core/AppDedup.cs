using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 去重：把「开始菜单 / 注册表 / 商店」三个来源的条目合并成一份不重复的应用列表。
    ///
    /// **核心原则：按稳定标识匹配，不靠显示名。**
    ///
    /// 匹配优先级（从高到低）：
    ///   1. **归一化路径**（exe 路径 / Shell 路径）—— 最可靠
    ///      同一路径必然同一程序（去引号、展开、小写）
    ///   2. **AUMID**（商店应用）—— 唯一标识
    ///   3. **归一化名称 + 发布者** —— 只在拿不到路径时兜底
    ///      为什么不只用名称：同名不同程序很常见
    ///      （`QQ` 和 `QQ` 安装版/商店版、`Ditto` 与某软件的 `Ditto` 组件）
    ///
    /// **跨来源合并规则**（用户明确要求）：
    ///   · 商店应用之间、商店与 exe 应用之间**不跨来源合并**
    ///     （同名的商店版和安装版是两个不同的软件，各留一条）
    ///   · 开始菜单 / 注册表 / 便携 之间**合并**（它们描述的是同一个安装）
    ///
    /// 合并时保留哪个：
    ///   · 启动路径：优先"能启动的"；同级时优先开始菜单（它带快捷方式，用户认这个）
    ///   · 名称：优先开始菜单（更接近用户看到的）
    ///   · 版本/发布者/安装日期/安装目录：从注册表补（开始菜单没有这些信息）
    ///
    /// **确定性**：同样的输入每次得到同样的结果。
    ///   · 不依赖 Dictionary 枚举顺序（用有序列表 + 稳定排序）
    ///   · 最终按 名称拼音键 + 路径 排序
    /// </summary>
    static class AppDedup
    {
        /// <summary>去重统计。</summary>
        public class Stats
        {
            public int StartMenuCount;        // 开始菜单原始条目
            public int RegistryCount;         // 注册表可启动条目
            public int RegistryTotal;         // 注册表全部条目（含不可启动）
            public int StoreCount;            // 商店原始条目

            public int LnkResolved;           // 实际解析的 lnk 数（COM 调用）
            public int LnkCacheHits;          // 命中缓存的 lnk 数
            public int LnkFailed;             // 解析失败的 lnk 数

            public int MergedByPath;          // 因（真实）路径相同被合并
            public int MergedByNamePublisher; // 因名称+发布者被合并
            public int MergedByNameOnly;      // 因名称相同被合并（双方无发布者）
            public int KeptStoreSeparate;     // 商店应用保持独立
            public int SkippedStoreDuplicate; // 非真商店 AppsFolder 条目，Catalog 已有对应条目
            public int SkippedSameSourceDuplicate; // 开始菜单内同目标的非主快捷方式被丢弃
            public int DroppedNoName;         // 无名称被丢弃
            public int DroppedUnlaunchable;   // 不可启动被丢弃

            public int FinalCount;            // 最终应用数

            /// <summary>被合并的组（前 N 组，用于人工核对是否误合并）。</summary>
            public List<string> MergeSamples = new List<string>();

            /// <summary>没能用路径匹配、退回名称匹配的条目（供核对）。</summary>
            public List<string> PathUnresolvedSamples = new List<string>();

            public string Report()
            {
                return "去重: 开始菜单 " + StartMenuCount
                     + " + 注册表可启动 " + RegistryCount + "（总 " + RegistryTotal + "）"
                     + " + 商店 " + StoreCount
                     + " → 最终 " + FinalCount
                     + "\n      路径合并 " + MergedByPath
                     + " | 名称+发布者合并 " + MergedByNamePublisher
                     + " | 名称兜底合并 " + MergedByNameOnly
                     + " | 商店独立 " + KeptStoreSeparate
                     + " | 非商店AppsFolder重复 " + SkippedStoreDuplicate
                     + " | 同源重复快捷方式 " + SkippedSameSourceDuplicate
                     + " | 丢弃无名称 " + DroppedNoName
                     + " | 丢弃不可启动 " + DroppedUnlaunchable
                     + "\n      lnk 解析: 实际 " + LnkResolved + " 次 / 缓存命中 " + LnkCacheHits
                     + " 次 / 失败 " + LnkFailed + " 次";
            }

            public string SampleReport(int max)
            {
                if (MergeSamples.Count == 0) return "（无合并）";
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                int n = Math.Min(max, MergeSamples.Count);
                for (int i = 0; i < n; i++) sb.AppendLine("    " + MergeSamples[i]);
                if (MergeSamples.Count > n)
                    sb.AppendLine("    …还有 " + (MergeSamples.Count - n) + " 组");
                return sb.ToString();
            }

            public string UnresolvedReport(int max)
            {
                if (PathUnresolvedSamples.Count == 0) return "（全部解析成功）";
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                int n = Math.Min(max, PathUnresolvedSamples.Count);
                for (int i = 0; i < n; i++) sb.AppendLine("    " + PathUnresolvedSamples[i]);
                if (PathUnresolvedSamples.Count > n)
                    sb.AppendLine("    …还有 " + (PathUnresolvedSamples.Count - n) + " 条");
                return sb.ToString();
            }
        }

        /// <summary>合并时的来源优先级（数字小的优先）。</summary>
        static int SourceRank(string source)
        {
            switch (source)
            {
                case AppPaths.SourceStartMenu: return 0;   // 快捷方式，用户认这个
                case AppPaths.SourcePortable: return 1;
                case AppPaths.SourceRegistry: return 2;    // 注册表信息多但没有快捷方式
                case AppPaths.SourceStore: return 3;       // 商店独立，不参与跨来源合并
                default: return 9;
            }
        }

        /// <summary>
        /// 合并三个来源。返回去重后的列表（已排序）。
        /// </summary>
        /// <param name="startMenu">开始菜单 + 便携（来自 apps.json）</param>
        /// <param name="registry">注册表条目（含不可启动的）</param>
        /// <param name="store">商店应用</param>
        public static List<AppItem> Merge(
            List<AppItem> startMenu,
            List<RegistryScanner.Entry> registry,
            List<StoreAppScanner.Entry> store,
            Stats stats)
        {
            if (stats == null) stats = new Stats();

            List<AppItem> result = new List<AppItem>();

            // 路径索引：归一化路径 → 结果列表里的下标
            Dictionary<string, int> byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // 名称+发布者索引：归一化名称|归一化发布者 → 下标
            Dictionary<string, int> byNamePublisher = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // 名称索引：归一化名称 → 下标列表（一个名称可能对应多个不同程序）
            Dictionary<string, List<int>> byName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            // ---------- 0. 开始菜单内「同目标的重复快捷方式」预过滤 ----------
            //
            // ── 实测 bug（用户截图）──
            //
            // 某安装器在开始菜单建了两份快捷方式：
            //   · Agent Orchestrator.lnk  → ...\agent-orchestrator.exe
            //   · Orchestrator.inc.lnk    → 同一个 exe
            // 名称不同、lnk 路径不同，但目标是**同一个 exe** —— 界面上
            // 同一个程序出现两条（「全部」「常用」页都有）。
            //
            // ── 为什么不直接「同目标就合并」──
            //
            // DedupTest 第 12 条固化过既有结论：Anaconda Prompt /
            // Command Prompt / Node.js command prompt 三个 lnk 都指向
            // cmd.exe，但它们是**不同用途的入口**，必须全部保留。
            // 无脑按目标合并会把它们吃掉两个。
            //
            // ── 「主快捷方式」规则（本修复的判定）──
            //
            //   · 主快捷方式：条目名与目标 exe 文件名**归一化后一致**
            //     （Agent Orchestrator ↔ agent-orchestrator.exe → 一致）
            //   · 同目标组里**存在主快捷方式**时，只保留主快捷方式，
            //     其余视为安装器误建的重复入口，丢弃
            //   · 同目标组里**没有任何**主快捷方式（cmd.exe 三入口：
            //     名称都与 cmd 无关）→ 全部保留，行为与以前一致
            //
            // 这样 cmd.exe 场景不受影响，而 Orchestrator 场景正确收敛为 1 条。
            if (startMenu != null && startMenu.Count > 1)
            {
                startMenu = FilterSameSourceDuplicates(startMenu, stats);
            }

            // ---------- 1. 开始菜单（含便携）作为基底 ----------
            if (startMenu != null)
            {
                foreach (AppItem a in startMenu)
                {
                    try
                    {
                        if (a == null || string.IsNullOrWhiteSpace(a.Name))
                        {
                            stats.DroppedNoName++;
                            continue;
                        }
                        stats.StartMenuCount++;

                        // 开始菜单来源的标记（老数据可能没有 Source）
                        if (string.IsNullOrEmpty(a.Source))
                        {
                            a.Source = AppPaths.IsShellPath(a.Path)
                                ? AppPaths.SourceStore
                                : AppPaths.SourceStartMenu;
                        }

                        int idx = result.Count;
                        result.Add(a);
                        Index(byPath, byNamePublisher, byName, a, idx, stats);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("去重: 处理开始菜单条目失败 " + (a == null ? "(null)" : a.Name) + ": " + ex.Message);
                    }
                }
            }

            // ---------- 2. 注册表：合并进已有条目，或新增 ----------
            if (registry != null)
            {
                foreach (RegistryScanner.Entry r in registry)
                {
                    try
                    {
                        if (r == null) continue;
                        stats.RegistryTotal++;

                        // 不可启动的不进界面列表（但数据留在 registry-apps.json）
                        if (!r.Launchable || string.IsNullOrEmpty(r.LaunchPath))
                        {
                            stats.DroppedUnlaunchable++;
                            continue;
                        }
                        if (string.IsNullOrWhiteSpace(r.DisplayName))
                        {
                            stats.DroppedNoName++;
                            continue;
                        }
                        stats.RegistryCount++;

                        // 注册表给的就是真实 exe 路径，直接归一化
                        string normPath = AppPaths.Normalize(r.LaunchPath);

                        // ---- 匹配 1：真实路径相同 → 合并 ----
                        int hit;
                        if (!string.IsNullOrEmpty(normPath) && byPath.TryGetValue(normPath, out hit))
                        {
                            MergeInto(result[hit], r, stats, "路径");
                            stats.MergedByPath++;
                            continue;
                        }

                        // ---- 匹配 2：名称 + 发布者相同 → 合并 ----
                        string key = NameKey(r.DisplayName, r.Publisher);
                        if (key != null && byNamePublisher.TryGetValue(key, out hit))
                        {
                            MergeInto(result[hit], r, stats, "名称+发布者");
                            stats.MergedByNamePublisher++;
                            continue;
                        }

                        // ---- 匹配 3：名称兜底（条件严格，宁可少合并）----
                        //
                        // 用户要求："名称相同不等于应用相同，宁可少合并，不要误删。"
                        //
                        // 只有**同时**满足下面全部条件才合并：
                        //   ① 注册表条目没有 Publisher
                        //   ② 候选条目也没有 Publisher
                        //   ③ 归一化名称完全相同
                        //   ④ 候选来源不是商店（商店按设计独立）
                        //   ⑤ 两条的 exe 文件名相同（进一步佐证是同一程序）
                        //
                        // 第 ⑤ 条是关键：`Clash V-Ninja` 的开始菜单版指向
                        // clash-ninja.exe，注册表版也指向 clash-ninja.exe → 合并；
                        // 但两个不同目录下的同名 `Helper.exe` 如果文件名相同、
                        // 目录不同，仍然会被合并 —— 这是可接受的，
                        // 因为同名+同 exe 名+同无发布者，三条证据都指向同一程序。
                        if (string.IsNullOrWhiteSpace(r.Publisher))
                        {
                            List<int> sameName;
                            if (byName.TryGetValue(NameMatch.Normalize(r.DisplayName), out sameName))
                            {
                                bool merged = false;
                                foreach (int si in sameName)
                                {
                                    AppItem cand = result[si];
                                    if (cand == null) continue;

                                    // ① ② 双方都无发布者
                                    if (!string.IsNullOrWhiteSpace(cand.Publisher)) continue;
                                    // ④ 商店独立
                                    if (cand.Source == AppPaths.SourceStore) continue;
                                    // ⑤ exe 文件名相同
                                    if (!SameExeName(cand.Path, r.LaunchPath)) continue;

                                    MergeInto(cand, r, stats, "名称+同exe名");
                                    stats.MergedByNameOnly++;
                                    merged = true;
                                    break;
                                }
                                if (merged) continue;
                            }
                        }

                        // ---- 新增 ----
                        AppItem item = new AppItem();
                        item.Name = r.DisplayName;
                        item.Path = r.LaunchPath;
                        item.Source = AppPaths.SourceRegistry;
                        item.Version = r.Version ?? "";
                        item.Publisher = r.Publisher ?? "";
                        item.InstallDate = r.InstallDate ?? "";
                        item.InstallLocation = r.InstallLocation ?? "";
                        item.Launchable = true;

                        int ni = result.Count;
                        result.Add(item);
                        Index(byPath, byNamePublisher, byName, item, ni, stats);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("去重: 处理注册表条目失败 " + (r == null ? "(null)" : r.DisplayName) + ": " + ex.Message);
                    }
                }
            }

            // ---------- 3. shell:AppsFolder 条目 ----------
            //
            // **分两类处理，分类与去重分开**（用户明确要求）：
            //
            // A. 真商店应用（IsRealStore，AUMID 形如 <PackageFamilyName>!<AppId>）
            //    → 收录，Source = Store（界面加 ·商店 后缀）
            //    → 不跨来源合并：同名的商店版和安装版是两个不同的软件
            //
            // B. 非真商店（AUMID 不是包族名格式，如 Chrome / Microsoft.Office.EXCEL.EXE.15）
            //    → 它们其实是传统软件在 AppsFolder 里的入口，开始菜单/注册表多半已有
            //    → 按「名称归一化」或「目标路径」匹配已有条目：匹配上就跳过（避免重复）
            //    → 匹配不上才收录，Source = AppsFolder（界面**不加** ·商店 后缀）
            if (store != null)
            {
                foreach (StoreAppScanner.Entry s in store)
                {
                    try
                    {
                        if (s == null || string.IsNullOrWhiteSpace(s.DisplayName)) continue;
                        if (string.IsNullOrWhiteSpace(s.Aumid)) continue;

                        // ---- A. 真商店 ----
                        if (s.IsRealStore)
                        {
                            stats.StoreCount++;

                            AppItem item = new AppItem();
                            item.Name = s.DisplayName;
                            item.Path = s.LaunchPath;
                            item.Aumid = s.Aumid;
                            item.Source = AppPaths.SourceStore;
                            item.Launchable = true;

                            // 同 AUMID 已在结果里（apps.json 里已有该商店应用）→ 保留那条
                            string normPath = AppPaths.Normalize(item.Path);
                            int existing;
                            if (byPath.TryGetValue(normPath, out existing))
                            {
                                AppItem cand = result[existing];
                                if (cand != null && string.IsNullOrEmpty(cand.Aumid))
                                    cand.Aumid = item.Aumid;
                                stats.KeptStoreSeparate++;
                                continue;
                            }

                            int ni = result.Count;
                            result.Add(item);
                            byPath[normPath] = ni;
                            continue;
                        }

                        // ---- B. 非真商店：先查是否已有对应条目 ----
                        stats.StoreCount++;
                        if (HasExistingEntry(result, byName, s.DisplayName))
                        {
                            stats.SkippedStoreDuplicate++;
                            continue;
                        }

                        AppItem legacy = new AppItem();
                        legacy.Name = s.DisplayName;
                        legacy.Path = s.LaunchPath;
                        legacy.Aumid = s.Aumid;
                        legacy.Source = AppPaths.SourceAppsFolder;   // 不加 ·商店
                        legacy.Launchable = true;

                        int li = result.Count;
                        result.Add(legacy);
                        byPath[AppPaths.Normalize(legacy.Path)] = li;

                        string ln = NameMatch.Normalize(legacy.Name);
                        if (!string.IsNullOrEmpty(ln))
                        {
                            List<int> lst;
                            if (!byName.TryGetValue(ln, out lst))
                            {
                                lst = new List<int>();
                                byName[ln] = lst;
                            }
                            lst.Add(li);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("去重: 处理 AppsFolder 条目失败 " + (s == null ? "(null)" : s.DisplayName) + ": " + ex.Message);
                    }
                }
            }

            // ---------- 4. 稳定排序 ----------
            //
            // 按 名称拼音键 → 路径 排序。不依赖 Dictionary 枚举顺序，
            // 同样输入每次结果完全一致（用户要求"去重结果确定性"）。
            result.Sort(delegate(AppItem a, AppItem b)
            {
                if (a == null) return b == null ? 0 : 1;
                if (b == null) return -1;
                int c = string.Compare(Pinyin.SortKey(a.Name), Pinyin.SortKey(b.Name), StringComparison.Ordinal);
                if (c != 0) return c;
                return string.Compare(a.Path ?? "", b.Path ?? "", StringComparison.OrdinalIgnoreCase);
            });

            // ---------- 5. 回填 lnk 解析统计 ----------
            //
            // ShortcutResolver 是全局的，这里把它的计数读进本次统计，
            // 方便报告"实际解析了多少次 / 命中缓存多少次"。
            try
            {
                stats.LnkResolved = ShortcutResolver.RealResolves;
                stats.LnkCacheHits = ShortcutResolver.CacheHits;
                stats.LnkFailed = stats.PathUnresolvedSamples.Count;
            }
            catch { }

            stats.FinalCount = result.Count;
            return result;
        }

        /// <summary>
        /// 开始菜单内「同目标重复快捷方式」过滤。
        ///
        /// 分组键：解析出的目标 exe 归一化路径（解析失败 / shell 路径不参与，
        /// 保持原样）。同组内：
        ///   · 找「主快捷方式」（名称与 exe 名归一化后一致）；
        ///   · 恰好有主快捷方式 → 保留它、丢弃其余；
        ///   · 有多个主快捷方式 → 保留第一个（稳定：按列表原顺序），
        ///     丢弃其余（此时其余的名称也都与 exe 一致，属于纯重名重复）；
        ///   · 没有主快捷方式 → 全部保留（cmd.exe 多入口场景）。
        /// </summary>
        static List<AppItem> FilterSameSourceDuplicates(List<AppItem> startMenu, Stats stats)
        {
            List<AppItem> kept = new List<AppItem>(startMenu.Count);
            try
            {
                // 组键 → 该组全部条目的下标（按原顺序）
                Dictionary<string, List<int>> groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < startMenu.Count; i++)
                {
                    AppItem a = startMenu[i];
                    if (a == null) continue;
                    // 商店/shell 条目不参与（它们的去重在后面的 AppsFolder 段做）
                    if (a.Path != null && AppPaths.IsShellPath(a.Path)) continue;

                    string key = ShortcutResolver.ResolveDedupKey(a.Path);
                    if (string.IsNullOrEmpty(key)) continue;

                    List<int> g;
                    if (!groups.TryGetValue(key, out g))
                    {
                        g = new List<int>();
                        groups[key] = g;
                    }
                    g.Add(i);
                }

                // 需要丢弃的下标集合
                HashSet<int> drop = new HashSet<int>();

                foreach (KeyValuePair<string, List<int>> kv in groups)
                {
                    List<int> g = kv.Value;
                    if (g == null || g.Count < 2) continue;

                    // 组内找主快捷方式：名称与 exe 名归一化后一致
                    string exeNorm = ExeNormOfKey(kv.Key);
                    int primary = -1;
                    if (!string.IsNullOrEmpty(exeNorm))
                    {
                        foreach (int i in g)
                        {
                            AppItem a = startMenu[i];
                            if (a == null || string.IsNullOrWhiteSpace(a.Name)) continue;
                            if (NameMatch.Canonical(a.Name) == exeNorm) { primary = i; break; }
                        }
                    }

                    if (primary < 0) continue;   // 没有主快捷方式 → 全部保留

                    foreach (int i in g)
                    {
                        if (i != primary) drop.Add(i);
                    }
                }

                if (drop.Count == 0) return startMenu;

                for (int i = 0; i < startMenu.Count; i++)
                {
                    if (drop.Contains(i)) continue;
                    AppItem a = startMenu[i];
                    if (a == null) continue;
                    kept.Add(a);
                }

                if (stats != null)
                {
                    stats.SkippedSameSourceDuplicate = drop.Count;
                    foreach (int i in drop)
                    {
                        if (stats.MergeSamples.Count < 10)
                        {
                            AppItem a = startMenu[i];
                            // 找它所在组的保留项，方便人工核对
                            string key = (a != null && a.Path != null && !AppPaths.IsShellPath(a.Path))
                                ? ShortcutResolver.ResolveDedupKey(a.Path) : null;
                            int primary = -1;
                            List<int> grp;
                            if (!string.IsNullOrEmpty(key) && groups.TryGetValue(key, out grp))
                            {
                                foreach (int j in grp)
                                {
                                    if (!drop.Contains(j)) { primary = j; break; }
                                }
                            }
                            stats.MergeSamples.Add(string.Format("[同源重复] 丢弃「{0}」  (保留「{1}」，同一目标)",
                                Trim(a == null ? "" : a.Name, 28),
                                primary >= 0 ? Trim(startMenu[primary].Name, 28) : "?"));
                        }
                    }
                }

                Log.Info("去重: 开始菜单内同目标重复快捷方式丢弃 " + drop.Count + " 条");
                return kept;
            }
            catch (Exception ex)
            {
                // 过滤失败绝不能影响正常合并 —— 返回原列表
                Log.Warn("去重: 同源重复过滤失败（已跳过）: " + ex.Message);
                return startMenu;
            }
        }

        /// <summary>
        /// 从去重键（归一化目标路径）取 exe 文件名的归一化形式。
        ///
        /// 键是 AppPaths.Normalize 的产物（小写、展开路径），
        /// 这里取文件名、去扩展名，再用 NameMatch.Canonical 归一化，
        /// 与条目名比（Agent Orchestrator ↔ c:\...\agent-orchestrator.exe → agentorchestrator）。
        /// </summary>
        static string ExeNormOfKey(string dedupKey)
        {
            try
            {
                if (string.IsNullOrEmpty(dedupKey)) return null;
                string fn = System.IO.Path.GetFileName(dedupKey);
                if (string.IsNullOrEmpty(fn)) return null;
                return NameMatch.Canonical(System.IO.Path.GetFileNameWithoutExtension(fn));
            }
            catch { return null; }
        }

            /// <summary>
            /// 结果里是否已有「同一个软件」的条目（用于过滤非真商店的 AppsFolder 入口）。
            ///
            /// 只按**归一化名称**匹配，不按目标路径：
            ///   · 非真商店条目的 Path 是 shell:AppsFolder\Chrome，没有文件目标可解析
            ///   · 名称匹配足够可靠（Chrome ↔ Google Chrome 这种差异靠 Contains 兜住）
            ///
            /// 为什么不用「目标路径」：shell 路径无法解析出 exe，
            /// 强行解析会误伤（把 Chrome 的 AppsFolder 入口当成独立软件）。
            /// </summary>
            static bool HasExistingEntry(List<AppItem> result, Dictionary<string, List<int>> byName, string displayName)
            {
                try
                {
                    string n = NameMatch.Normalize(displayName);
                    if (string.IsNullOrEmpty(n)) return false;

                    // 精确名称命中
                    List<int> same;
                    if (byName.TryGetValue(n, out same) && same.Count > 0) return true;

                    // 包含关系兜底（Google Chrome vs Chrome）
                    foreach (AppItem a in result)
                    {
                        if (a == null) continue;
                        if (AppPaths.IsShellPath(a.Path)) continue;   // 只和真实文件条目比
                        string an = NameMatch.Normalize(a.Name);
                        if (string.IsNullOrEmpty(an)) continue;
                        if (an == n) return true;
                        if (n.Length >= 4 && an.Length >= 4 &&
                            (an.IndexOf(n, StringComparison.Ordinal) >= 0 ||
                             n.IndexOf(an, StringComparison.Ordinal) >= 0))
                            return true;
                    }
                    return false;
                }
                catch { return false; }
            }

        /// <summary>
        /// 把条目登记进三个索引。
        ///
        /// **路径索引用的是「去重键」而不是原始 Path**：
        ///   · .lnk → 解析出的真实 exe 路径（这样才能和注册表的 exe 路径匹配）
        ///   · .exe / shell 路径 → 归一化后原样
        ///   · 解析失败 → 不登记路径索引（该条目只能靠名称匹配）
        ///
        /// 注意：**不修改 a.Path**。原始路径保持原样，避免破坏启动行为
        /// （用户明确要求"不要为了去重修改原始 AppItem 的启动路径"）。
        /// </summary>
        static void Index(Dictionary<string, int> byPath,
                          Dictionary<string, int> byNamePublisher,
                          Dictionary<string, List<int>> byName,
                          AppItem a, int idx, Stats stats)
        {
            try
            {
                if (a == null) return;

                // ---- 路径索引（用去重键）----
                string key = ShortcutResolver.ResolveDedupKey(a.Path);
                if (!string.IsNullOrEmpty(key))
                {
                    if (!byPath.ContainsKey(key)) byPath[key] = idx;
                }
                else if (!string.IsNullOrEmpty(a.Path)
                         && a.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    // lnk 解析失败：记录样本，方便核对
                    if (stats != null && stats.PathUnresolvedSamples.Count < 10)
                        stats.PathUnresolvedSamples.Add(a.Name + "  (" + a.Path + ")");
                }

                // ---- 名称+发布者索引 ----
                string nk = NameKey(a.Name, a.Publisher);
                if (nk != null && !byNamePublisher.ContainsKey(nk))
                    byNamePublisher[nk] = idx;

                // ---- 名称索引 ----
                string n = NameMatch.Normalize(a.Name);
                if (!string.IsNullOrEmpty(n))
                {
                    List<int> list;
                    if (!byName.TryGetValue(n, out list))
                    {
                        list = new List<int>();
                        byName[n] = list;
                    }
                    list.Add(idx);
                }
            }
            catch { }
        }

        /// <summary>
        /// 名称+发布者组合键。任一为空时返回 null（不参与该级匹配）。
        /// </summary>
        static string NameKey(string name, string publisher)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return null;
                if (string.IsNullOrWhiteSpace(publisher)) return null;
                return NameMatch.Normalize(name) + "|" + NameMatch.Normalize(publisher);
            }
            catch { return null; }
        }

        /// <summary>
        /// 把注册表条目的补充信息合并进已有条目。
        ///
        /// 只补**空的**字段，不覆盖已有值 —— 开始菜单那条更接近用户认知。
        /// 启动路径也只在原条目不可启动时才替换。
        /// </summary>
        static void MergeInto(AppItem target, RegistryScanner.Entry r, Stats stats, string reason)
        {
            try
            {
                if (target == null || r == null) return;

                bool filled = false;

                if (string.IsNullOrEmpty(target.Version) && !string.IsNullOrEmpty(r.Version))
                { target.Version = r.Version; filled = true; }
                if (string.IsNullOrEmpty(target.Publisher) && !string.IsNullOrEmpty(r.Publisher))
                { target.Publisher = r.Publisher; filled = true; }
                if (string.IsNullOrEmpty(target.InstallDate) && !string.IsNullOrEmpty(r.InstallDate))
                { target.InstallDate = r.InstallDate; filled = true; }
                if (string.IsNullOrEmpty(target.InstallLocation) && !string.IsNullOrEmpty(r.InstallLocation))
                { target.InstallLocation = r.InstallLocation; filled = true; }

                // 启动路径：原条目不可启动时才用注册表的
                if (!target.Launchable && !string.IsNullOrEmpty(r.LaunchPath))
                {
                    target.Path = r.LaunchPath;
                    target.Launchable = true;
                    filled = true;
                }

                // 记录样本（前 10 组），供人工核对是否误合并
                if (stats.MergeSamples.Count < 10)
                {
                    stats.MergeSamples.Add(string.Format("[{0}] {1}  (开始菜单「{2}」← 注册表「{3}」{4})",
                        reason, Trim(target.Name, 28), Trim(target.Name, 20), Trim(r.DisplayName, 20),
                        filled ? " +补充信息" : " 无新信息"));
                }
            }
            catch { }
        }

        /// <summary>
        /// 判断两个路径的 exe 文件名是否相同（忽略大小写）。
        ///
        /// 用于名称兜底合并的第五道校验：同名 + 同 exe 名 + 双方无发布者，
        /// 三条证据都指向同一程序才合并。
        ///
        /// .lnk 会先解析出目标再比 —— 否则 `7zFM.exe` 与 `7-Zip File Manager.lnk`
        /// 永远比不上（一个是 lnk 名，一个是 exe 名）。
        /// </summary>
        static bool SameExeName(string pathA, string pathB)
        {
            try
            {
                string a = ExeNameOf(pathA);
                string b = ExeNameOf(pathB);
                if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static string ExeNameOf(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return null;
                string p = path.Trim().Trim('"');

                if (p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string target = ShortcutResolver.Resolve(p);
                    if (string.IsNullOrEmpty(target)) return null;
                    p = target;
                }
                if (AppPaths.IsShellPath(p)) return null;
                return Path.GetFileName(p);
            }
            catch { return null; }
        }

        static string Trim(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }
    }
}
