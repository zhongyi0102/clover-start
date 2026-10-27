using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Clover
{
    class AppItem
    {
        public string Name;
        public string Path;
        public string Category;
        public int Usage;
        public long LastUsed;
    }

    class Catalog
    {
        public List<string> Order = new List<string>();
        public List<AppItem> Apps = new List<AppItem>();

        public static readonly string[] DefaultOrder = new string[] { "办公", "开发", "网络通讯", "影音娱乐", "系统工具", "工具/命令行", "其他" };

        /// <summary>
        /// 「工具/命令行」分类名。
        /// 命令行工具、驱动、Module Docs、Reset 工具、帮助文档都收在这里，
        /// 避免它们混在「开发」「网络通讯」里干扰查找。
        /// </summary>
        public const string ToolCategory = "工具/命令行";

        public static Catalog Load(string baseDir)
        {
            Catalog c = new Catalog();
            string cfg = Path.Combine(baseDir, "config");

            // 先加载分类规则表（config\category-rules.tsv）。
            // 必须在任何 GuessCategory 调用之前完成，否则会用到上一次的规则。
            // 带修改时间检查，文件没改就跳过重复解析。
            CategoryRules.Init(baseDir);

            ReadInto(c, Path.Combine(cfg, "apps.json"));
            ReadInto(c, Path.Combine(cfg, "manual-apps.json"));

            foreach (string d in DefaultOrder) EnsureOrder(c, d);

            // 用户手工指定的分类覆盖优先级最高：
            // 本表 > 开始菜单目录规则 > 关键词规则。
            // 放在这里，每次加载/重新扫描都会重新应用，所以「重新扫描后保持」。
            try
            {
                CategoryOverrides ov = CategoryOverrides.Load(baseDir);
                ov.Apply(c.Apps);
            }
            catch { }

            foreach (AppItem a in c.Apps) EnsureOrder(c, a.Category);
            return c;
        }

        static void EnsureOrder(Catalog c, string cat)
        {
            if (string.IsNullOrEmpty(cat)) return;
            if (!c.Order.Contains(cat)) c.Order.Add(cat);
        }


        static void ReadInto(Catalog c, string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                string json = JsonIO.Read(path);
                if (string.IsNullOrEmpty(json)) return;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> data = ser.Deserialize<Dictionary<string, object>>(json);

                object arr;
                if (data.TryGetValue("apps", out arr) && arr != null)
                {
                    foreach (object o in (System.Collections.IEnumerable)arr)
                    {
                        Dictionary<string, object> m = o as Dictionary<string, object>;
                        if (m == null) continue;
                        AppItem a = new AppItem();
                        a.Name = GetStr(m, "name");
                        a.Path = GetStr(m, "path");
                        a.Category = GetStr(m, "category");
                        if (string.IsNullOrEmpty(a.Name) || string.IsNullOrEmpty(a.Path)) continue;
                        if (!File.Exists(a.Path)) continue;
                        if (c.ContainsPath(a.Path)) continue;
                        if (string.IsNullOrEmpty(a.Category)) a.Category = GuessCategory(a.Name);
                        if (c.ContainsName(a.Name)) continue;
                        c.Apps.Add(a);
                    }
                }
            }
            catch { }
        }

        static string GetStr(Dictionary<string, object> m, string key)
        {
            object v;
            if (m.TryGetValue(key, out v) && v != null)
                return Convert.ToString(v);
            return "";
        }

        public bool ContainsName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (AppItem a in Apps)
            {
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public bool ContainsPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (AppItem a in Apps)
            {
                if (string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static List<AppItem> LoadManual(string baseDir)
        {
            List<AppItem> list = new List<AppItem>();
            string path = Path.Combine(baseDir, "config", "manual-apps.json");
            if (!File.Exists(path)) return list;
            try
            {
                string json = JsonIO.Read(path);
                if (string.IsNullOrEmpty(json)) return list;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> data = ser.Deserialize<Dictionary<string, object>>(json);
                object arr;
                if (data.TryGetValue("apps", out arr) && arr != null)
                {
                    foreach (object o in (System.Collections.IEnumerable)arr)
                    {
                        Dictionary<string, object> m = o as Dictionary<string, object>;
                        if (m == null) continue;
                        AppItem a = new AppItem();
                        a.Name = GetStr(m, "name");
                        a.Path = GetStr(m, "path");
                        a.Category = GetStr(m, "category");
                        if (!string.IsNullOrEmpty(a.Name) && !string.IsNullOrEmpty(a.Path))
                            list.Add(a);
                    }
                }
            }
            catch { }
            return list;
        }

        public static void SaveManual(string baseDir, List<AppItem> list)
        {
            try
            {
                Dictionary<string, object> root = new Dictionary<string, object>();
                List<object> arr = new List<object>();
                foreach (AppItem a in list)
                {
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["name"] = a.Name;
                    m["path"] = a.Path;
                    m["category"] = a.Category;
                    arr.Add(m);
                }
                root["apps"] = arr;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                string json = ser.Serialize(root);
                JsonIO.WriteAtomic(Path.Combine(baseDir, "config", "manual-apps.json"), json);
            }
            catch { }
        }

        /// <summary>
        /// 按名称判定分类。
        ///
        /// 三个层次，从高到低：
        ///   1. 用户手工覆盖（category-overrides.json，在 Catalog.Load 里应用）
        ///   2. **外部规则表**（config\category-rules.tsv）—— 命中即返回
        ///   3. 内置规则（下面的正则与逐个列举）
        ///
        /// 之所以把规则表放在内置规则**之前**：表是用户/贡献者维护的，
        /// 应当能覆盖程序内置的判断；而内置规则作为兜底，保证不编辑文件时
        /// 行为不退化。
        /// </summary>
        public static string GuessCategory(string name)
        {
            if (string.IsNullOrEmpty(name)) return "其他";

            // ---------- 2. 外部规则表 ----------
            string fromTable = CategoryRules.Guess(name);
            if (fromTable != null) return fromTable;

            // ---------- 3. 内置规则 ----------
            // 用 NameMatch.Fold 而不是裸 ToLowerInvariant：它额外做 FormKC，
            // 把全角字符折叠成半角（`Ｗｏｒｄ` → `word`），同时保留标点，
            // 因此下面这些正则（含 `7-zip`、`adobe.*(acrobat|reader)`）照旧可用。
            string n = NameMatch.Fold(name);

            // ---------- 工具/命令行（放在最前，避免被「开发」「系统工具」抢走）----------
            // 命令行工具 / 驱动 / Module Docs / Reset 工具 / 帮助文档 都归这里。
            // 这些平时用不到，混在开发或网络通讯里只会干扰查找。
            if (IsToolLike(n)) return ToolCategory;

            // ---------- Windows 自带管理工具 ----------
            // 这些名字没有共同后缀，只能逐个列举。
            // 注意：必须在「开发」等规则之前判断，但它们也不该进「工具/命令行」
            // （那是给命令行窗口和文档用的），所以单独走一条。
            if (IsSystemTool(n)) return "系统工具";

            // ---------- 其余按原有的优先级 ----------
            if (Regex.IsMatch(n, "steam|epic|wegame|game|游戏|原神|minecraft|league")) return "影音娱乐";
            if (Regex.IsMatch(n, "chrome|edge|firefox|brave|browser|浏览器|telegram|discord|wechat|weixin|qq|微信|抖音|钉钉|迅雷|网盘|clash|v2ray|迅游")) return "网络通讯";
            if (Regex.IsMatch(n, "word|excel|powerpoint|wps|office|notion|obsidian|onenote|飞书|foxit|adobe.*(acrobat|reader)|pdf")) return "办公";
            // 打印机 / 扫描仪 / 传真类硬件工具：不是办公套件，归系统工具
            if (Regex.IsMatch(n, @"\bhp\b|canon|epson|brother|打印机|扫描仪|传真")) return "系统工具";
            if (Regex.IsMatch(n, "code|studio|python|git|node|sql|java|docker|postman|pycharm|idea|anaconda|jupyter|vscode|visual|mongo")) return "开发";
            if (Regex.IsMatch(n, "player|vlc|potplayer|music|音乐|vid|video|bilibili|哔哩|剪映|obs|audacity|格式工厂")) return "影音娱乐";
            if (Regex.IsMatch(n, "7-zip|winrar|bandizip|压缩|power|sharex|snip|截图|ditto|defender|清理|disk|分区")) return "系统工具";

            // ---------- 最小补充：早期版本能正确分类、当前关键词覆盖不到的 15 个 ----------
            // 这些应用在更早的规则里分对了，当前关键词没有对应项。
            // 若不补，改成「每次重判」后它们会掉进「其他」。
            // 只补这一批，不再扩大关键词表——后续靠「移到分类」让用户自己纠正。
            if (Regex.IsMatch(n, "dbeaver|ollama|\\bwsl\\b|wsl settings|harness|\\bdsh\\b|deepseek")) return "开发";
            if (Regex.IsMatch(n, "karing|localsend|向日葵|直播伴侣")) return "网络通讯";
            if (Regex.IsMatch(n, "mineradio|faststone|动漫|概念版")) return "影音娱乐";
            if (Regex.IsMatch(n, "图吧|工具箱|sticky notes|便笺")) return "系统工具";

            return "其他";
        }

        /// <summary>
        /// 判断是否属于 Windows 自带的系统管理/辅助工具。传入已转小写的名称。
        ///
        /// 这些是开始菜单「Windows 管理工具」「辅助功能」等目录里的东西，
        /// 名字没有共同后缀，只能逐个列举；漏掉的会掉进「其他」。
        /// </summary>
        public static bool IsSystemTool(string lowerName)
        {
            if (string.IsNullOrEmpty(lowerName)) return false;
            string n = lowerName;

            string[] keywords = new string[]
            {
                // 管理工具
                "file explorer", "task manager", "control panel", "event viewer",
                "registry editor", "task scheduler", "system configuration",
                "system information", "resource monitor", "performance monitor",
                "component services", "computer management", "services",
                "memory diagnostics", "steps recorder", "recoverydrive", "recovery drive",
                "iscsi initiator", "odbc data sources", "remote desktop connection",
                "windows defender firewall", "disk cleanup", "dfrgui", "defragment",
                // 辅助功能
                "magnify", "narrator", "on-screen keyboard", "voiceaccess",
                "livecaptions", "live captions", "character map",
                // 常见运行/工具
                "run", "pe viewer", "system informer", "托盘", "工具箱",
            };

            foreach (string k in keywords)
            {
                if (n == k) return true;                 // 完全相等（如 "run"、"services"）
                if (k.Length >= 4 && n.Contains(k)) return true;  // 包含（避免过短词误伤）
            }
            return false;
        }

        /// <summary>
        /// 判断是否属于「工具/命令行」分类。传入已转小写的名称。
        ///
        /// 覆盖：命令行窗口、驱动、Python Module Docs、Reset 工具、帮助/文档。
        /// 顺序上这些必须在「开发」等规则之前判断，否则会被抢走。
        ///
        /// 注意：词匹配用「包含」而不是严格词边界，因为像 ChromeDriver 这种
        /// 连写的名称里 driver 前面是字母，用 \b 会漏掉。
        /// </summary>
        public static bool IsToolLike(string lowerName)
        {
            if (string.IsNullOrEmpty(lowerName)) return false;
            string n = lowerName;

            // 命令行窗口 / 终端
            if (Regex.IsMatch(n, "(^|\\W)(prompt|command line|console|terminal|bash|shell|repl|cmd|powershell|命令提示符)(\\W|$)"))
                return true;

            // 驱动（ChromeDriver、Driver Booster、xxx Driver 等）
            if (n.Contains("driver")) return true;

            // Python Module Docs 之类
            if (n.Contains("module docs") || n.Contains("module doc")) return true;

            // Reset xxx 工具
            if (n.StartsWith("reset ") || n.StartsWith("reset-")) return true;

            // 帮助 / 文档（单独归一类，不混进业务分类）
            if (Regex.IsMatch(n, "(^|\\W)(guide|manual|help|readme|faq|documentation|docs|about|release notes)(\\W|$)"))
                return true;
            if (n.Contains("帮助") || n.Contains("关于") || n.Contains("说明")) return true;

            return false;
        }

        public void HideCategories(List<string> hidden)
        {
            if (hidden == null || hidden.Count == 0) return;
            List<AppItem> keep = new List<AppItem>();
            foreach (AppItem a in Apps)
            {
                if (!hidden.Contains(a.Category)) keep.Add(a);
            }
            Apps = keep;
            List<string> order = new List<string>();
            foreach (string cat in Order)
            {
                if (!hidden.Contains(cat)) order.Add(cat);
            }
            Order = order;
        }

        public void ApplyUsage(UsageStore usage)
        {
            foreach (AppItem a in Apps)
            {
                a.Usage = usage.GetCount(a.Path);
                a.LastUsed = usage.GetLast(a.Path);
            }
        }

        static int CompareApps(AppItem a, AppItem b)
        {
            int c = b.Usage.CompareTo(a.Usage);
            if (c != 0) return c;
            c = b.LastUsed.CompareTo(a.LastUsed);
            if (c != 0) return c;
            return string.Compare(a.Name, b.Name, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), System.Globalization.CompareOptions.None);
        }

        public List<AppItem> ByCategory(string cat)
        {
            List<AppItem> list = new List<AppItem>();
            foreach (AppItem a in Apps)
            {
                if (string.IsNullOrEmpty(cat) || a.Category == cat)
                    list.Add(a);
            }
            list.Sort(CompareApps);
            return list;
        }

        public List<string> ActiveCategories()
        {
            List<string> list = new List<string>();
            foreach (string cat in Order)
            {
                if (ByCategory(cat).Count > 0)
                    list.Add(cat);
            }
            return list;
        }
    }
}
