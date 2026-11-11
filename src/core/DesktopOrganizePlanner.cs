using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    public enum DesktopPlanState
    {
        Ready = 0,
        Ignored,
        Unmatched,
        InvalidTarget,
        Missing
    }

    /// <summary>一次桌面扫描。只列顶层项目，不进入文件夹修改内部结构。</summary>
    public class DesktopOrganizeScan
    {
        public List<string> Paths = new List<string>();
        public List<string> Skipped = new List<string>();
    }

    /// <summary>预览表中的一行，也是执行器的不可变输入快照。</summary>
    public class DesktopOrganizePlanItem
    {
        public bool Selected;
        public string Source = "";
        public string SourceLabel = "";
        public string Name = "";
        public bool IsDirectory;
        public long Size = -1;
        public long LastWriteTicks;
        public DesktopPlanState State;
        public DesktopRuleAction Action;
        public string RuleId = "";
        public string RuleName = "";
        public List<string> MatchedRules = new List<string>();
        public string TargetDirectory = "";
        public string TargetPath = "";
        public string Reason = "";

        public bool Executable
        {
            get
            {
                return State == DesktopPlanState.Ready
                    && (Action == DesktopRuleAction.Move || Action == DesktopRuleAction.Copy);
            }
        }
    }

    public class DesktopOrganizePlan
    {
        public List<DesktopOrganizePlanItem> Items = new List<DesktopOrganizePlanItem>();

        public int ReadyCount
        {
            get
            {
                int n = 0;
                foreach (DesktopOrganizePlanItem item in Items)
                    if (item != null && item.Executable) n++;
                return n;
            }
        }

        public int IgnoredCount
        {
            get
            {
                int n = 0;
                foreach (DesktopOrganizePlanItem item in Items)
                    if (item != null && item.State == DesktopPlanState.Ignored) n++;
                return n;
            }
        }
    }

    /// <summary>规则匹配、目标解析与安全预览；本类不写文件。</summary>
    public static class DesktopOrganizePlanner
    {
        /// <summary>
        /// 枚举给定桌面目录的直接子项。隐藏、系统、重解析点会明确记入 Skipped。
        /// </summary>
        public static DesktopOrganizeScan Scan(IEnumerable<string> desktopDirs)
        {
            DesktopOrganizeScan result = new DesktopOrganizeScan();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (desktopDirs == null) return result;
                foreach (string rawDir in desktopDirs)
                {
                    string dir;
                    try { dir = Path.GetFullPath(rawDir ?? "").TrimEnd('\\'); }
                    catch { continue; }
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || !seen.Add(dir))
                        continue;

                    string[] entries;
                    try { entries = Directory.GetFileSystemEntries(dir); }
                    catch (Exception ex)
                    {
                        result.Skipped.Add(dir + "（无法读取：" + ex.Message + "）");
                        continue;
                    }

                    foreach (string path in entries)
                    {
                        try
                        {
                            FileAttributes attr = File.GetAttributes(path);
                            if ((attr & (FileAttributes.Hidden | FileAttributes.System
                                | FileAttributes.ReparsePoint)) != 0)
                            {
                                result.Skipped.Add(Path.GetFileName(path));
                                continue;
                            }
                            result.Paths.Add(Path.GetFullPath(path));
                        }
                        catch
                        {
                            result.Skipped.Add(Path.GetFileName(path));
                        }
                    }
                }

                result.Paths.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 生成逐项预览。desks 是「桌面显示名 → 实际目录」。
        /// 多条规则命中时 MatchedRules 会全部保留，但采用顺序最前的一条。
        /// </summary>
        public static DesktopOrganizePlan Build(IEnumerable<string> sources,
            IList<DesktopOrganizeRule> rules, IDictionary<string, string> desks)
        {
            DesktopOrganizePlan plan = new DesktopOrganizePlan();
            HashSet<string> reservedTargets =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (sources == null) return plan;
                foreach (string raw in sources)
                {
                    DesktopOrganizePlanItem item = Inspect(raw);
                    plan.Items.Add(item);
                    if (item.State == DesktopPlanState.Missing) continue;

                    List<DesktopOrganizeRule> matches = new List<DesktopOrganizeRule>();
                    if (rules != null)
                    {
                        foreach (DesktopOrganizeRule candidate in rules)
                        {
                            if (candidate == null || !candidate.Enabled) continue;
                            if (Matches(candidate, item.Source)) matches.Add(candidate);
                        }
                    }

                    foreach (DesktopOrganizeRule match in matches)
                        item.MatchedRules.Add(match.Name ?? "");

                    if (matches.Count == 0)
                    {
                        item.State = DesktopPlanState.Unmatched;
                        item.Reason = "没有命中启用的规则";
                        continue;
                    }

                    DesktopOrganizeRule rule = DesktopOrganizeRules.Sanitize(matches[0]);
                    item.RuleId = rule.Id;
                    item.RuleName = rule.Name;
                    item.Action = rule.Action;

                    if (rule.Action == DesktopRuleAction.Ignore)
                    {
                        item.State = DesktopPlanState.Ignored;
                        item.Reason = "规则设为忽略，不会改动文件";
                        continue;
                    }

                    string targetDir;
                    string targetError;
                    if (!TryResolveTarget(rule, desks, item.Source, out targetDir, out targetError))
                    {
                        item.State = DesktopPlanState.InvalidTarget;
                        item.Reason = targetError;
                        continue;
                    }

                    item.TargetDirectory = targetDir;
                    item.TargetPath = MakeUniqueTarget(targetDir, item.Name,
                        item.IsDirectory, reservedTargets);
                    reservedTargets.Add(Normalize(item.TargetPath));
                    item.State = DesktopPlanState.Ready;
                    item.Selected = true;
                    if (matches.Count > 1)
                        item.Reason = "共命中 " + matches.Count + " 条规则，按顺序采用「"
                            + rule.Name + "」";
                }
            }
            catch { }
            return plan;
        }

        public static bool Matches(DesktopOrganizeRule sourceRule, string rawPath)
        {
            try
            {
                if (sourceRule == null || !sourceRule.Enabled || string.IsNullOrWhiteSpace(rawPath))
                    return false;
                string path = Path.GetFullPath(rawPath);
                bool isDir = Directory.Exists(path);
                if (!isDir && !File.Exists(path)) return false;

                DesktopOrganizeRule rule = DesktopOrganizeRules.Sanitize(sourceRule);
                string name = Path.GetFileName(path);
                if (!MatchesAnyPattern(name, rule.NamePattern)) return false;

                List<string> exts = SplitTokens(rule.Extensions);
                if (exts.Count > 0)
                {
                    if (isDir) return false;
                    string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
                    bool extHit = false;
                    foreach (string raw in exts)
                    {
                        string token = raw.Trim().ToLowerInvariant();
                        if (token == "*") { extHit = true; break; }
                        if (!token.StartsWith(".")) token = "." + token;
                        if (string.Equals(token, ext, StringComparison.OrdinalIgnoreCase))
                        { extHit = true; break; }
                    }
                    if (!extHit) return false;
                }

                if (rule.MinSizeBytes >= 0 || rule.MaxSizeBytes >= 0)
                {
                    if (isDir) return false;
                    long size = new FileInfo(path).Length;
                    if (rule.MinSizeBytes >= 0 && size < rule.MinSizeBytes) return false;
                    if (rule.MaxSizeBytes >= 0 && size > rule.MaxSizeBytes) return false;
                }

                DateTime modified = isDir
                    ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
                DateTime boundary;
                if (!string.IsNullOrEmpty(rule.ModifiedAfter)
                    && DesktopOrganizeRules.TryDate(rule.ModifiedAfter, out boundary)
                    && modified < boundary.Date) return false;
                if (!string.IsNullOrEmpty(rule.ModifiedBefore)
                    && DesktopOrganizeRules.TryDate(rule.ModifiedBefore, out boundary)
                    && modified >= boundary.Date.AddDays(1)) return false;

                return true;
            }
            catch { return false; }
        }

        public static string ConditionSummary(DesktopOrganizeRule source)
        {
            try
            {
                DesktopOrganizeRule r = DesktopOrganizeRules.Sanitize(source);
                List<string> parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(r.NamePattern) && r.NamePattern != "*")
                    parts.Add("名称 " + r.NamePattern);
                if (!string.IsNullOrWhiteSpace(r.Extensions))
                    parts.Add("扩展名 " + r.Extensions);
                if (r.MinSizeBytes >= 0 || r.MaxSizeBytes >= 0)
                {
                    string min = r.MinSizeBytes < 0 ? "不限" : FormatBytes(r.MinSizeBytes);
                    string max = r.MaxSizeBytes < 0 ? "不限" : FormatBytes(r.MaxSizeBytes);
                    parts.Add("大小 " + min + "–" + max);
                }
                if (!string.IsNullOrEmpty(r.ModifiedAfter)
                    || !string.IsNullOrEmpty(r.ModifiedBefore))
                    parts.Add("修改日期 " + (string.IsNullOrEmpty(r.ModifiedAfter) ? "不限" : r.ModifiedAfter)
                        + "–" + (string.IsNullOrEmpty(r.ModifiedBefore) ? "不限" : r.ModifiedBefore));
                return parts.Count == 0 ? "所有顶层项目" : string.Join("；", parts.ToArray());
            }
            catch { return ""; }
        }

        static DesktopOrganizePlanItem Inspect(string raw)
        {
            DesktopOrganizePlanItem item = new DesktopOrganizePlanItem();
            try
            {
                item.Source = Path.GetFullPath(raw ?? "");
                item.Name = Path.GetFileName(item.Source);
                item.IsDirectory = Directory.Exists(item.Source);
                if (!item.IsDirectory && !File.Exists(item.Source))
                {
                    item.State = DesktopPlanState.Missing;
                    item.Reason = "源项目不存在";
                    return item;
                }

                if (item.IsDirectory)
                {
                    item.Size = -1;
                    item.LastWriteTicks = Directory.GetLastWriteTimeUtc(item.Source).Ticks;
                }
                else
                {
                    FileInfo f = new FileInfo(item.Source);
                    item.Size = f.Length;
                    item.LastWriteTicks = f.LastWriteTimeUtc.Ticks;
                }
                item.SourceLabel = DesktopSourceLabel(item.Source);
                item.State = DesktopPlanState.Unmatched;
            }
            catch
            {
                item.State = DesktopPlanState.Missing;
                item.Reason = "无法读取源项目";
            }
            return item;
        }

        static string DesktopSourceLabel(string path)
        {
            try
            {
                string parent = Path.GetDirectoryName(path);
                string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                if (!string.IsNullOrEmpty(common)
                    && string.Equals(parent.TrimEnd('\\'), common.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase)) return "公共桌面";
            }
            catch { }
            return "用户桌面";
        }

        static bool TryResolveTarget(DesktopOrganizeRule rule,
            IDictionary<string, string> desks, string source,
            out string targetDir, out string error)
        {
            targetDir = "";
            error = "";
            try
            {
                if (rule.TargetKind == DesktopRuleTargetKind.Desk)
                {
                    if (desks == null || !TryGetIgnoreCase(desks, rule.Target, out targetDir))
                    {
                        error = "目标桌面不存在，请编辑规则";
                        return false;
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(rule.Target))
                    {
                        error = "未选择目标文件夹";
                        return false;
                    }
                    if (IsNetworkPath(rule.Target))
                    {
                        error = "为避免网络中断，本阶段只支持本地文件夹";
                        return false;
                    }
                    targetDir = Path.GetFullPath(rule.Target);
                }

                if (!Path.IsPathRooted(targetDir) || !Directory.Exists(targetDir))
                {
                    error = "目标文件夹不存在或不可用";
                    return false;
                }
                targetDir = Path.GetFullPath(targetDir).TrimEnd('\\');

                string src = Path.GetFullPath(source).TrimEnd('\\');
                string parent = (Path.GetDirectoryName(src) ?? "").TrimEnd('\\');
                if (string.Equals(parent, targetDir, StringComparison.OrdinalIgnoreCase))
                {
                    error = "目标仍是源项目所在的桌面";
                    return false;
                }
                if (Directory.Exists(src)
                    && targetDir.StartsWith(src + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = "目标不能位于源文件夹内部";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "目标不可用：" + ex.Message;
                return false;
            }
        }

        static bool TryGetIgnoreCase(IDictionary<string, string> map, string key,
            out string value)
        {
            value = null;
            if (map.TryGetValue(key ?? "", out value)) return true;
            foreach (KeyValuePair<string, string> kv in map)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                { value = kv.Value; return true; }
            }
            return false;
        }

        static string MakeUniqueTarget(string dir, string name, bool isDirectory,
            HashSet<string> reserved)
        {
            string candidate = Path.Combine(dir, name);
            if (!ExistsOrReserved(candidate, reserved)) return candidate;

            string baseName = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
            string ext = isDirectory ? "" : Path.GetExtension(name);
            for (int i = 1; i <= 9999; i++)
            {
                candidate = Path.Combine(dir, baseName + " (" + i + ")" + ext);
                if (!ExistsOrReserved(candidate, reserved)) return candidate;
            }
            return Path.Combine(dir, baseName + " (" + Guid.NewGuid().ToString("N") + ")" + ext);
        }

        static bool ExistsOrReserved(string path, HashSet<string> reserved)
        {
            try
            {
                return File.Exists(path) || Directory.Exists(path)
                    || (reserved != null && reserved.Contains(Normalize(path)));
            }
            catch { return true; }
        }

        static string Normalize(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant(); }
            catch { return (path ?? "").ToLowerInvariant(); }
        }

        static bool MatchesAnyPattern(string value, string rawPatterns)
        {
            List<string> patterns = SplitPatterns(rawPatterns);
            if (patterns.Count == 0) patterns.Add("*");
            foreach (string pattern in patterns)
                if (Wildcard(value ?? "", pattern)) return true;
            return false;
        }

        /// <summary>
        /// 文件名模式只用分号/逗号/换行分隔，不能按空格拆：
        /// “项目 *.pdf”里的空格是文件名的一部分。
        /// </summary>
        static List<string> SplitPatterns(string raw)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return result;
            foreach (string token in raw.Split(new char[] { ';', ',', '，', '；', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                string value = token.Trim();
                if (value.Length > 0) result.Add(value);
            }
            return result;
        }

        /// <summary>不依赖正则的 * / ? 通配符匹配，忽略大小写。</summary>
        public static bool Wildcard(string value, string pattern)
        {
            value = value ?? "";
            pattern = string.IsNullOrEmpty(pattern) ? "*" : pattern;
            int vi = 0, pi = 0, star = -1, mark = -1;
            while (vi < value.Length)
            {
                if (pi < pattern.Length
                    && (pattern[pi] == '?'
                        || char.ToUpperInvariant(pattern[pi]) == char.ToUpperInvariant(value[vi])))
                { vi++; pi++; continue; }
                if (pi < pattern.Length && pattern[pi] == '*')
                { star = pi++; mark = vi; continue; }
                if (star >= 0)
                { pi = star + 1; vi = ++mark; continue; }
                return false;
            }
            while (pi < pattern.Length && pattern[pi] == '*') pi++;
            return pi == pattern.Length;
        }

        static List<string> SplitTokens(string raw)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return result;
            foreach (string token in raw.Split(new char[] { ';', ',', '，', '；', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                string value = token.Trim();
                if (value.Length > 0) result.Add(value);
            }
            return result;
        }

        static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.#") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.#") + " MB";
            return (mb / 1024.0).ToString("0.##") + " GB";
        }

        static bool IsNetworkPath(string raw)
        {
            try
            {
                string path = (raw ?? "").Trim().Trim('"');
                if (path.StartsWith("\\\\")) return true;
                if (path.Length >= 2 && path[1] == ':')
                {
                    DriveInfo drive = new DriveInfo(path.Substring(0, 2) + "\\");
                    return drive.DriveType == DriveType.Network;
                }
            }
            catch { }
            return false;
        }
    }

    public class DesktopOrganizeExecutionItem
    {
        public DesktopOrganizePlanItem PlanItem;
        public bool Ok;
        public string Message = "";
        public string Target = "";
    }

    public class DesktopOrganizeExecution
    {
        public List<DesktopOrganizeExecutionItem> Items =
            new List<DesktopOrganizeExecutionItem>();
        public int Succeeded;
        public int Failed;
    }

    /// <summary>严格按预览里的目标逐项执行，不临时换路径，也绝不覆盖。</summary>
    public static class DesktopOrganizeExecutor
    {
        public static DesktopOrganizeExecution Execute(
            IEnumerable<DesktopOrganizePlanItem> planItems)
        {
            DesktopOrganizeExecution result = new DesktopOrganizeExecution();
            if (planItems == null) return result;

            foreach (DesktopOrganizePlanItem item in planItems)
            {
                if (item == null || !item.Selected || !item.Executable) continue;
                DesktopOrganizeExecutionItem one = new DesktopOrganizeExecutionItem();
                one.PlanItem = item;
                one.Target = item.TargetPath ?? "";
                result.Items.Add(one);

                try
                {
                    string changedReason;
                    if (!SnapshotStillMatches(item, out changedReason))
                    {
                        one.Message = changedReason;
                        result.Failed++;
                        continue;
                    }

                    // 预览后的同名竞争不重新改名：那会让实际落点偏离用户确认的表格。
                    if (File.Exists(item.TargetPath) || Directory.Exists(item.TargetPath))
                    {
                        one.Message = "预览后目标已被占用，请重新预览";
                        result.Failed++;
                        continue;
                    }

                    VerifiedFileMove.Result moved = item.Action == DesktopRuleAction.Copy
                        ? VerifiedFileMove.Copy(item.Source, item.TargetPath)
                        : VerifiedFileMove.Move(item.Source, item.TargetPath);
                    one.Ok = moved.Ok;
                    one.Target = moved.Target;
                    one.Message = moved.Message ?? "";
                    if (moved.Ok)
                    {
                        result.Succeeded++;
                        try
                        {
                            Log.Info("智能整理" + DesktopOrganizeRules.ActionText(item.Action)
                                + ": " + item.Source + " → " + moved.Target
                                + "（规则：" + item.RuleName + "）");
                        }
                        catch { }
                    }
                    else result.Failed++;
                }
                catch (Exception ex)
                {
                    one.Message = ex.Message;
                    result.Failed++;
                }
            }
            return result;
        }

        static bool SnapshotStillMatches(DesktopOrganizePlanItem item, out string reason)
        {
            reason = "";
            try
            {
                if (item.IsDirectory)
                {
                    if (!Directory.Exists(item.Source))
                    { reason = "源文件夹已不存在"; return false; }
                    if (Directory.GetLastWriteTimeUtc(item.Source).Ticks != item.LastWriteTicks)
                    { reason = "源文件夹在预览后发生变化，请重新预览"; return false; }
                    return true;
                }

                if (!File.Exists(item.Source))
                { reason = "源文件已不存在"; return false; }
                FileInfo f = new FileInfo(item.Source);
                if (f.Length != item.Size || f.LastWriteTimeUtc.Ticks != item.LastWriteTicks)
                { reason = "源文件在预览后发生变化，请重新预览"; return false; }
                return true;
            }
            catch (Exception ex)
            {
                reason = "无法重新核对源项目：" + ex.Message;
                return false;
            }
        }
    }
}
