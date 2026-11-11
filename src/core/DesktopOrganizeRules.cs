using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>智能整理支持的安全动作。刻意不提供删除、覆盖或外部命令。</summary>
    public enum DesktopRuleAction
    {
        Move = 0,
        Copy,
        Ignore
    }

    /// <summary>移动/复制目标的种类。</summary>
    public enum DesktopRuleTargetKind
    {
        Desk = 0,
        Folder
    }

    /// <summary>
    /// 一条桌面整理规则。Items 中的顺序就是优先级：越靠前越先采用。
    /// 日期使用 yyyy-MM-dd，避免配置文件受系统区域格式影响。
    /// </summary>
    public class DesktopOrganizeRule
    {
        public string Id = "";
        public string Name = "";
        public bool Enabled = true;
        public string NamePattern = "*";
        public string Extensions = "";
        public long MinSizeBytes = -1;
        public long MaxSizeBytes = -1;
        public string ModifiedAfter = "";
        public string ModifiedBefore = "";
        public DesktopRuleAction Action = DesktopRuleAction.Move;
        public DesktopRuleTargetKind TargetKind = DesktopRuleTargetKind.Desk;
        public string Target = "";

        public DesktopOrganizeRule Clone()
        {
            return new DesktopOrganizeRule
            {
                Id = Id ?? "",
                Name = Name ?? "",
                Enabled = Enabled,
                NamePattern = NamePattern ?? "",
                Extensions = Extensions ?? "",
                MinSizeBytes = MinSizeBytes,
                MaxSizeBytes = MaxSizeBytes,
                ModifiedAfter = ModifiedAfter ?? "",
                ModifiedBefore = ModifiedBefore ?? "",
                Action = Action,
                TargetKind = TargetKind,
                Target = Target ?? ""
            };
        }
    }

    /// <summary>
    /// config\desktop-organize-rules.json 的读写入口。
    ///
    /// 配置与通用设置分开保存：规则可独立备份/分享，损坏时也不会影响程序启动。
    /// JsonIO 负责原子写入和 .bak 回退；这里再负责字段形状与枚举值校验。
    /// </summary>
    public class DesktopOrganizeRules
    {
        public const int MaxRules = 100;

        public List<DesktopOrganizeRule> Items = new List<DesktopOrganizeRule>();

        string path;

        public static string PathOf(string baseDir)
        {
            try { return Path.Combine(baseDir, "config", "desktop-organize-rules.json"); }
            catch { return ""; }
        }

        public static DesktopOrganizeRules Load(string baseDir)
        {
            DesktopOrganizeRules store = new DesktopOrganizeRules();
            store.path = PathOf(baseDir);

            try
            {
                string json = JsonIO.Read(store.path);
                if (string.IsNullOrWhiteSpace(json)) return store;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> root =
                    ser.Deserialize<Dictionary<string, object>>(json);
                if (root == null) return store;

                object rawItems;
                if (!root.TryGetValue("items", out rawItems)) return store;

                HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (object raw in JsonIO.AsArray(rawItems))
                {
                    if (store.Items.Count >= MaxRules) break;
                    Dictionary<string, object> d = raw as Dictionary<string, object>;
                    if (d == null) continue;

                    DesktopOrganizeRule rule = ParseRule(d);
                    if (rule == null) continue;

                    if (string.IsNullOrEmpty(rule.Id) || ids.Contains(rule.Id))
                        rule.Id = Guid.NewGuid().ToString("N");
                    ids.Add(rule.Id);
                    store.Items.Add(rule);
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("读取桌面整理规则失败，已使用空规则：" + ex.Message); }
                catch { }
                store.Items.Clear();
            }

            return store;
        }

        public bool Save(out string error)
        {
            error = "";
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    error = "规则配置路径不可用";
                    return false;
                }

                Dictionary<string, object> root = new Dictionary<string, object>();
                root["version"] = 1;
                List<object> arr = new List<object>();

                HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DesktopOrganizeRule source in Items)
                {
                    if (source == null || arr.Count >= MaxRules) continue;
                    DesktopOrganizeRule rule = Sanitize(source);
                    if (string.IsNullOrEmpty(rule.Id) || ids.Contains(rule.Id))
                        rule.Id = Guid.NewGuid().ToString("N");
                    ids.Add(rule.Id);
                    source.Id = rule.Id;

                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["id"] = rule.Id;
                    d["name"] = rule.Name;
                    d["enabled"] = rule.Enabled;
                    d["namePattern"] = rule.NamePattern;
                    d["extensions"] = rule.Extensions;
                    d["minSizeBytes"] = rule.MinSizeBytes;
                    d["maxSizeBytes"] = rule.MaxSizeBytes;
                    d["modifiedAfter"] = rule.ModifiedAfter;
                    d["modifiedBefore"] = rule.ModifiedBefore;
                    d["action"] = ActionKey(rule.Action);
                    d["targetKind"] = TargetKindKey(rule.TargetKind);
                    d["target"] = rule.Target;
                    arr.Add(d);
                }
                root["items"] = arr;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                JsonIO.WriteResult written = JsonIO.WriteAtomic(path, ser.Serialize(root));
                if (!written.Success)
                {
                    error = string.IsNullOrEmpty(written.ErrorMessage)
                        ? "规则配置写入失败" : written.ErrorMessage;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { Log.Warn("保存桌面整理规则失败：" + ex.Message); } catch { }
                return false;
            }
        }

        public bool Save()
        {
            string ignored;
            return Save(out ignored);
        }

        static DesktopOrganizeRule ParseRule(Dictionary<string, object> d)
        {
            try
            {
                DesktopOrganizeRule r = new DesktopOrganizeRule();
                r.Id = Str(d, "id");
                r.Name = Str(d, "name");
                r.Enabled = Bool(d, "enabled", true);
                r.NamePattern = Str(d, "namePattern");
                r.Extensions = Str(d, "extensions");
                r.MinSizeBytes = Long(d, "minSizeBytes", -1);
                r.MaxSizeBytes = Long(d, "maxSizeBytes", -1);
                r.ModifiedAfter = Str(d, "modifiedAfter");
                r.ModifiedBefore = Str(d, "modifiedBefore");
                r.Action = ParseAction(Value(d, "action"));
                r.TargetKind = ParseTargetKind(Value(d, "targetKind"));
                r.Target = Str(d, "target");
                return Sanitize(r);
            }
            catch { return null; }
        }

        public static DesktopOrganizeRule Sanitize(DesktopOrganizeRule source)
        {
            DesktopOrganizeRule r = source == null
                ? new DesktopOrganizeRule() : source.Clone();

            r.Id = Trim(r.Id, 64);
            r.Name = Trim(r.Name, 60);
            r.NamePattern = Trim(r.NamePattern, 240);
            if (string.IsNullOrEmpty(r.NamePattern)) r.NamePattern = "*";
            r.Extensions = Trim(r.Extensions, 240);
            if (r.MinSizeBytes < -1) r.MinSizeBytes = -1;
            if (r.MaxSizeBytes < -1) r.MaxSizeBytes = -1;
            r.ModifiedAfter = NormalizeDate(r.ModifiedAfter);
            r.ModifiedBefore = NormalizeDate(r.ModifiedBefore);
            if (!Enum.IsDefined(typeof(DesktopRuleAction), r.Action))
                r.Action = DesktopRuleAction.Move;
            if (!Enum.IsDefined(typeof(DesktopRuleTargetKind), r.TargetKind))
                r.TargetKind = DesktopRuleTargetKind.Desk;
            r.Target = Trim(r.Target, 1024);
            return r;
        }

        public static string Validate(DesktopOrganizeRule source)
        {
            string rawAfter = source == null ? "" : (source.ModifiedAfter ?? "").Trim();
            string rawBefore = source == null ? "" : (source.ModifiedBefore ?? "").Trim();
            DateTime after, before;
            if (!string.IsNullOrEmpty(rawAfter) && !TryDate(rawAfter, out after))
                return "修改日期起点无效，请使用 yyyy-MM-dd。";
            if (!string.IsNullOrEmpty(rawBefore) && !TryDate(rawBefore, out before))
                return "修改日期终点无效，请使用 yyyy-MM-dd。";
            if (!string.IsNullOrEmpty(rawAfter) && !string.IsNullOrEmpty(rawBefore)
                && TryDate(rawAfter, out after) && TryDate(rawBefore, out before)
                && after.Date > before.Date)
                return "修改日期起点不能晚于终点。";

            DesktopOrganizeRule r = Sanitize(source);
            if (string.IsNullOrEmpty(r.Name)) return "请输入规则名称。";
            if (r.MinSizeBytes >= 0 && r.MaxSizeBytes >= 0
                && r.MinSizeBytes > r.MaxSizeBytes)
                return "最小大小不能大于最大大小。";

            if (r.Action != DesktopRuleAction.Ignore
                && string.IsNullOrWhiteSpace(r.Target))
                return r.TargetKind == DesktopRuleTargetKind.Desk
                    ? "请选择目标桌面。" : "请选择目标文件夹。";
            return null;
        }

        public static string ActionText(DesktopRuleAction action)
        {
            if (action == DesktopRuleAction.Copy) return "复制";
            if (action == DesktopRuleAction.Ignore) return "忽略";
            return "移动";
        }

        static string ActionKey(DesktopRuleAction action)
        {
            if (action == DesktopRuleAction.Copy) return "copy";
            if (action == DesktopRuleAction.Ignore) return "ignore";
            return "move";
        }

        static string TargetKindKey(DesktopRuleTargetKind kind)
        {
            return kind == DesktopRuleTargetKind.Folder ? "folder" : "desk";
        }

        static DesktopRuleAction ParseAction(object raw)
        {
            string s = Convert.ToString(raw) ?? "";
            int n;
            if (int.TryParse(s, out n) && Enum.IsDefined(typeof(DesktopRuleAction), n))
                return (DesktopRuleAction)n;
            if (string.Equals(s, "copy", StringComparison.OrdinalIgnoreCase))
                return DesktopRuleAction.Copy;
            if (string.Equals(s, "ignore", StringComparison.OrdinalIgnoreCase))
                return DesktopRuleAction.Ignore;
            return DesktopRuleAction.Move;
        }

        static DesktopRuleTargetKind ParseTargetKind(object raw)
        {
            string s = Convert.ToString(raw) ?? "";
            int n;
            if (int.TryParse(s, out n) && Enum.IsDefined(typeof(DesktopRuleTargetKind), n))
                return (DesktopRuleTargetKind)n;
            return string.Equals(s, "folder", StringComparison.OrdinalIgnoreCase)
                ? DesktopRuleTargetKind.Folder : DesktopRuleTargetKind.Desk;
        }

        static string NormalizeDate(string raw)
        {
            DateTime value;
            return TryDate(raw, out value) ? value.ToString("yyyy-MM-dd") : "";
        }

        internal static bool TryDate(string raw, out DateTime value)
        {
            return DateTime.TryParseExact((raw ?? "").Trim(), "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out value);
        }

        static object Value(Dictionary<string, object> d, string key)
        {
            object value;
            return d != null && d.TryGetValue(key, out value) ? value : null;
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object value = Value(d, key);
            return value == null ? "" : Convert.ToString(value);
        }

        static bool Bool(Dictionary<string, object> d, string key, bool fallback)
        {
            object value = Value(d, key);
            if (value == null) return fallback;
            bool parsed;
            return bool.TryParse(Convert.ToString(value), out parsed) ? parsed : fallback;
        }

        static long Long(Dictionary<string, object> d, string key, long fallback)
        {
            object value = Value(d, key);
            if (value == null) return fallback;
            long parsed;
            return long.TryParse(Convert.ToString(value), out parsed) ? parsed : fallback;
        }

        static string Trim(string value, int max)
        {
            string s = (value ?? "").Trim();
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
