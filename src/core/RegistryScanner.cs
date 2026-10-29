using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace Clover
{
    /// <summary>
    /// 注册表 Uninstall 扫描：把「已安装但开始菜单里没有快捷方式」的软件找出来。
    ///
    /// **为什么需要这个来源**：
    /// 开始菜单只有用户能看见的快捷方式。很多软件（尤其开发工具、驱动配套程序、
    /// 绿色安装器）装完不建快捷方式，但它们确实"装了"。
    /// 用户打开启动面板找"那个软件"时，它应该在「全部」里。
    ///
    /// 读三处：
    ///   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall            （64 位机器上的 64 位程序）
    ///   HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall（32 位程序）
    ///   HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall            （只对当前用户安装的）
    ///
    /// 过滤规则（都有明确理由，见各个 if 的注释）：
    ///   · 没有 DisplayName                    → 不是给人看的条目
    ///   · SystemComponent = 1                 → 系统组件，用户不会想"启动"它
    ///   · 有 ParentKeyName                    → 更新补丁（隶属于某个主程序）
    ///   · DisplayName 以 "KB" 开头            → Windows 更新
    ///
    /// 可启动路径解析（按顺序尝试，全部失败则 Launchable=false）：
    ///   1. DisplayIcon（去掉引号、",0" 这类图标序号）
    ///   2. InstallLocation 下与 DisplayName 最匹配的 exe
    ///   3. App Paths 注册表
    /// 拿到 exe 后用 PortableScanner.IsJunk 排除卸载器/安装器/更新器。
    ///
    /// 输出：config\registry-apps.json（独立文件，不碰 apps.json）
    /// </summary>
    static class RegistryScanner
    {
        /// <summary>一条注册表条目的扫描结果（含统计用信息）。</summary>
        public class Entry
        {
            public string DisplayName;
            public string Version;
            public string Publisher;
            public string InstallDate;
            public string InstallLocation;
            public string DisplayIcon;
            public string UninstallString;

            /// <summary>解析出的可启动 exe（null = 解析失败）。</summary>
            public string LaunchPath;

            /// <summary>能否启动（界面只显示 true 的）。</summary>
            public bool Launchable;

            /// <summary>不能启动的原因（统计用）。</summary>
            public string SkipReason;
        }

        /// <summary>扫描统计（写日志/报告用）。</summary>
        public class Stats
        {
            public int ReadTotal;                 // 读到的原始条目数
            public int SkippedNoDisplayName;      // 无 DisplayName
            public int SkippedSystemComponent;    // SystemComponent=1
            public int SkippedParentKey;          // 有 ParentKeyName（补丁）
            public int SkippedKbUpdate;           // DisplayName 以 KB 开头
            public int SkippedNoLaunchPath;       // 解析不出可启动路径
            public int SkippedJunkExe;            // 解析出的 exe 是卸载器/安装器
            public int Launchable;                // 最终可启动条数
            public int DuplicateInRegistry;       // 注册表内部重复（同 exe）

            public int Filtered
            {
                get
                {
                    return SkippedNoDisplayName + SkippedSystemComponent
                         + SkippedParentKey + SkippedKbUpdate + SkippedJunkExe;
                }
            }

            public string Report()
            {
                return "注册表: 读到 " + ReadTotal + " 条"
                     + " | 过滤 " + Filtered + " 条"
                     + "（无名称 " + SkippedNoDisplayName
                     + " / 系统组件 " + SkippedSystemComponent
                     + " / 补丁 " + SkippedParentKey
                     + " / KB更新 " + SkippedKbUpdate
                     + " / 卸载器 " + SkippedJunkExe + "）"
                     + " | 无启动路径 " + SkippedNoLaunchPath
                     + " | 注册表内重复 " + DuplicateInRegistry
                     + " | 可启动 " + Launchable;
            }
        }

        /// <summary>Uninstall 注册表的三个位置。</summary>
        static readonly string[] UninstallPaths = new string[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"   // HKCU 用同一路径
        };

        /// <summary>
        /// 扫描注册表，返回**全部**条目（含不可启动的），并填充 stats。
        ///
        /// 调用方决定怎么用：可启动的接界面，不可启动的留着以后做「已安装清单」。
        /// 必须在后台线程调用（读注册表 + 探测文件存在性，约 200-800ms）。
        /// </summary>
        public static List<Entry> Scan(Stats stats)
        {
            List<Entry> result = new List<Entry>();
            if (stats == null) stats = new Stats();

            HashSet<string> seenExe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenKey = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ---- HKLM 64 位 ----
            ScanHive(Registry.LocalMachine, UninstallPaths[0], result, stats, seenExe, seenKey);
            // ---- HKLM 32 位（WOW6432Node）----
            ScanHive(Registry.LocalMachine, UninstallPaths[1], result, stats, seenExe, seenKey);
            // ---- HKCU ----
            ScanHive(Registry.CurrentUser, UninstallPaths[2], result, stats, seenExe, seenKey);

            Log.Info(stats.Report());
            return result;
        }

        static void ScanHive(RegistryKey root, string subPath, List<Entry> result, Stats stats,
                             HashSet<string> seenExe, HashSet<string> seenKey)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(subPath))
                {
                    if (key == null) return;

                    foreach (string subName in key.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey sub = key.OpenSubKey(subName))
                            {
                                if (sub == null) continue;
                                stats.ReadTotal++;

                                // 同一个子键可能被两个 hive 都读到（HKLM 64/32 有交集时）
                                string keyId = root.Name + "\\" + subPath + "\\" + subName;
                                if (!seenKey.Add(keyId)) continue;

                                Entry e = ParseEntry(sub, subName, stats);
                                if (e == null) continue;

                                // 注册表内部按 exe 去重（同一程序在 64/32 位各注册一次很常见）
                                if (e.Launchable && !string.IsNullOrEmpty(e.LaunchPath))
                                {
                                    if (!seenExe.Add(e.LaunchPath))
                                    {
                                        stats.DuplicateInRegistry++;
                                        continue;
                                    }
                                }

                                result.Add(e);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("读注册表条目失败 " + subName + ": " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 某个 hive 读不到（权限/不存在）不影响其它两处
                Log.Warn("读注册表位置失败 " + subPath + ": " + ex.Message);
            }
        }

        /// <summary>解析单个注册表子键。返回 null 表示被过滤掉（stats 里已计数）。</summary>
        static Entry ParseEntry(RegistryKey sub, string subName, Stats stats)
        {
            try
            {
                string displayName = GetStr(sub, "DisplayName");

                // ---- 过滤 1：没有 DisplayName ----
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    stats.SkippedNoDisplayName++;
                    return null;
                }
                displayName = displayName.Trim();

                // ---- 过滤 2：SystemComponent = 1（系统组件）----
                object sc = sub.GetValue("SystemComponent");
                if (sc != null)
                {
                    try { if (Convert.ToInt32(sc) == 1) { stats.SkippedSystemComponent++; return null; } }
                    catch { }
                }

                // ---- 过滤 3：有 ParentKeyName（更新补丁，隶属于主程序）----
                if (sub.GetValue("ParentKeyName") != null)
                {
                    stats.SkippedParentKey++;
                    return null;
                }

                // ---- 过滤 4：DisplayName 以 "KB" 开头（Windows 更新）----
                if (displayName.StartsWith("KB", StringComparison.OrdinalIgnoreCase)
                    && displayName.Length > 2 && char.IsDigit(displayName[2]))
                {
                    stats.SkippedKbUpdate++;
                    return null;
                }

                Entry e = new Entry();
                e.DisplayName = displayName;
                e.Version = GetStr(sub, "DisplayVersion");
                e.Publisher = GetStr(sub, "Publisher");
                e.InstallDate = GetStr(sub, "InstallDate");
                e.InstallLocation = GetStr(sub, "InstallLocation");
                e.DisplayIcon = GetStr(sub, "DisplayIcon");
                e.UninstallString = GetStr(sub, "UninstallString");

                // ---- 解析可启动路径 ----
                string exe = ResolveLaunchPath(e);

                if (string.IsNullOrEmpty(exe))
                {
                    // 运行库、驱动、补丁这类没有可启动程序
                    e.Launchable = false;
                    e.SkipReason = "无可启动路径";
                    stats.SkippedNoLaunchPath++;
                    return e;   // 仍然返回（写进 json，标记 Launchable=false）
                }

                // ---- 过滤 5：解析出的 exe 是卸载器/安装器/更新器 ----
                // 复用 PortableScanner 的规则，不另写一套
                if (PortableScanner.IsJunk(Path.GetFileName(exe), exe))
                {
                    e.Launchable = false;
                    e.SkipReason = "exe 是卸载器/安装器";
                    stats.SkippedJunkExe++;
                    return e;
                }

                e.LaunchPath = exe;
                e.Launchable = true;
                stats.Launchable++;
                return e;
            }
            catch (Exception ex)
            {
                Log.Warn("解析注册表条目失败 " + subName + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 按顺序尝试三种方式解析可启动 exe。全部失败返回 null。
        /// </summary>
        static string ResolveLaunchPath(Entry e)
        {
            // ---- 方式 1：DisplayIcon ----
            string fromIcon = CleanIconPath(e.DisplayIcon);
            if (fromIcon != null && File.Exists(fromIcon)) return fromIcon;

            // ---- 方式 2：InstallLocation 下与名称最匹配的 exe ----
            string fromLoc = FindExeInInstallLocation(e.InstallLocation, e.DisplayName);
            if (fromLoc != null) return fromLoc;

            // ---- 方式 3：App Paths 注册表 ----
            string fromAppPaths = FindInAppPaths(e.DisplayName);
            if (fromAppPaths != null) return fromAppPaths;

            return null;
        }

        /// <summary>
        /// 清理 DisplayIcon 的值：去掉引号、去掉 ",0" 这类图标序号。
        ///
        /// 实测格式五花八门：
        ///   "C:\Program Files\App\app.exe",0
        ///   C:\Program Files\App\app.exe
        ///   C:\Program Files\App\app.exe,0
        ///   C:\Program Files\App\icon.ico      ← 不是 exe，不能用
        /// </summary>
        static string CleanIconPath(string raw)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(raw)) return null;
                string s = raw.Trim().Trim('"');

                // 去掉末尾的 ,N（图标序号）
                int comma = s.LastIndexOf(',');
                if (comma > 0)
                {
                    string tail = s.Substring(comma + 1).Trim();
                    bool allDigits = tail.Length > 0;
                    foreach (char c in tail) { if (!char.IsDigit(c)) { allDigits = false; break; } }
                    if (allDigits) s = s.Substring(0, comma).Trim().Trim('"');
                }

                // 只接受 .exe（.ico/.dll 不能启动）
                if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
                return s;
            }
            catch { return null; }
        }

        /// <summary>
        /// 在 InstallLocation 下找与 DisplayName 最匹配的 exe。
        ///
        /// 只扫一层（不递归）：递归会在大型安装目录里跑几百毫秒，
        /// 而主程序几乎总在根目录或一层的子目录里。
        /// </summary>
        static string FindExeInInstallLocation(string location, string displayName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(location)) return null;
                string dir = location.Trim().Trim('"');
                if (!Directory.Exists(dir)) return null;

                // 候选：根目录 + 一层子目录
                List<string> candidates = new List<string>();
                try
                {
                    foreach (string f in Directory.EnumerateFiles(dir, "*.exe"))
                    {
                        candidates.Add(f);
                        if (candidates.Count >= 40) break;   // 上限：避免巨型目录
                    }
                }
                catch { }

                if (candidates.Count == 0)
                {
                    try
                    {
                        foreach (string sub in Directory.EnumerateDirectories(dir))
                        {
                            try
                            {
                                foreach (string f in Directory.EnumerateFiles(sub, "*.exe"))
                                {
                                    candidates.Add(f);
                                    if (candidates.Count >= 60) break;
                                }
                            }
                            catch { }
                            if (candidates.Count >= 60) break;
                        }
                    }
                    catch { }
                }

                if (candidates.Count == 0) return null;

                // 打分：文件名与 DisplayName 的归一化相似度 + 排除卸载器
                string normDisplay = NameMatch.Normalize(displayName);
                string best = null;
                int bestScore = -1;

                foreach (string f in candidates)
                {
                    try
                    {
                        string fileName = Path.GetFileName(f);
                        if (PortableScanner.IsJunk(fileName, f)) continue;

                        string normFile = NameMatch.Normalize(Path.GetFileNameWithoutExtension(f));
                        int score = Similarity(normDisplay, normFile);
                        if (score > bestScore) { bestScore = score; best = f; }
                    }
                    catch { }
                }

                // 分数太低说明只是碰巧同目录，不如不猜
                if (best == null || bestScore < 1) return null;
                return best;
            }
            catch { return null; }
        }

        /// <summary>
        /// 两个归一化名称的相似度：包含关系得分最高，其次是共同前缀。
        /// 返回 0 表示不相关。
        /// </summary>
        static int Similarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
            if (a == b) return 100;
            if (a.IndexOf(b, StringComparison.Ordinal) >= 0) return 80;
            if (b.IndexOf(a, StringComparison.Ordinal) >= 0) return 70;

            // 共同前缀长度（至少 3 个字符才算）
            int n = Math.Min(a.Length, b.Length);
            int common = 0;
            for (int i = 0; i < n; i++)
            {
                if (a[i] != b[i]) break;
                common++;
            }
            return common >= 3 ? common : 0;
        }

        /// <summary>
        /// 在 App Paths 注册表里找（很多程序在这里注册了 exe，但没有开始菜单项）。
        /// </summary>
        static string FindInAppPaths(string displayName)
        {
            try
            {
                string norm = NameMatch.Normalize(displayName);
                if (string.IsNullOrEmpty(norm)) return null;

                foreach (RegistryKey root in new RegistryKey[] { Registry.LocalMachine, Registry.CurrentUser })
                {
                    try
                    {
                        using (RegistryKey key = root.OpenSubKey(
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"))
                        {
                            if (key == null) continue;
                            foreach (string name in key.GetSubKeyNames())
                            {
                                try
                                {
                                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                                    string normName = NameMatch.Normalize(
                                        Path.GetFileNameWithoutExtension(name));
                                    if (normName != norm) continue;

                                    using (RegistryKey sub = key.OpenSubKey(name))
                                    {
                                        if (sub == null) continue;
                                        string path = sub.GetValue(null) as string;
                                        if (string.IsNullOrWhiteSpace(path)) continue;
                                        path = path.Trim().Trim('"').Trim();
                                        if (File.Exists(path)) return path;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        static string GetStr(RegistryKey k, string name)
        {
            try
            {
                object v = k.GetValue(name);
                if (v == null) return "";
                return Convert.ToString(v).Trim();
            }
            catch { return ""; }
        }

        /// <summary>输出文件路径。</summary>
        public static string PathOf(string baseDir)
        {
            return Path.Combine(baseDir, "config", "registry-apps.json");
        }

        /// <summary>
        /// 把扫描结果写成 registry-apps.json（独立文件，不碰 apps.json）。
        ///
        /// 不可启动的条目也写进去（Launchable=false），方便以后做「已安装清单」。
        /// </summary>
        public static bool Save(string baseDir, List<Entry> entries, Stats stats)
        {
            try
            {
                List<object> arr = new List<object>();
                foreach (Entry e in entries)
                {
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["name"] = e.DisplayName;
                    m["launchPath"] = e.LaunchPath ?? "";
                    m["launchable"] = e.Launchable;
                    m["skipReason"] = e.SkipReason ?? "";
                    m["version"] = e.Version ?? "";
                    m["publisher"] = e.Publisher ?? "";
                    m["installDate"] = e.InstallDate ?? "";
                    m["installLocation"] = e.InstallLocation ?? "";
                    arr.Add(m);
                }

                Dictionary<string, object> root = new Dictionary<string, object>();
                root["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                root["stats"] = stats == null ? "" : stats.Report();
                root["apps"] = arr;

                System.Web.Script.Serialization.JavaScriptSerializer ser =
                    new System.Web.Script.Serialization.JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                JsonIO.WriteAtomic(PathOf(baseDir), ser.Serialize(root));
                return true;
            }
            catch (Exception ex)
            {
                Log.Hook(ex, "写 registry-apps.json");
                return false;
            }
        }
    }
}
