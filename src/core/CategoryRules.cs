using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 分类规则表（属于 core 层，零 WPF 依赖）。
    ///
    /// 解决的问题：分类规则原来是**硬编码在 C# 里的正则**。每遇到一个判错的
    /// （HP 打印机、ChromeDriver、非 Steam 启动的游戏…）都要改代码、重新编译、
    /// 重新发版。规则表把这件事变成**改一个文本文件**。
    ///
    /// 文件：config\category-rules.tsv（UTF-8，可带 BOM）
    /// 格式：`分类 <TAB> 匹配方式 <TAB> 别名1|别名2|...`
    ///   - `#` 开头为注释；空行忽略
    ///   - 匹配方式见 MatchMode
    ///   - 别名用 `|` 分隔；同一行可写多个别名
    ///
    /// 优先级：**手工覆盖（category-overrides.json）> 本规则表 > 内置兜底规则**
    ///
    /// 未命中时返回 null，由调用方继续走内置逻辑——这样**不编辑文件时行为
    /// 等于原有行为 + 新增条目**，不会因为引入规则表而让老用户分类退化。
    ///
    /// 文件缺失时：把内置默认表写出一份到 config，方便用户在此基础上改。
    /// 文件损坏时：回退到内置默认表，不阻断启动。
    /// </summary>
    class CategoryRules
    {
        /// <summary>匹配方式。</summary>
        public enum MatchMode
        {
            /// <summary>按词匹配（默认）。ASCII 别名要求词边界，中文别名按子串。</summary>
            Word,
            /// <summary>归一化后完全相等。适合短缩写，避免误判。</summary>
            Exact,
            /// <summary>归一化后包含即可。最宽松，容易误判，慎用。</summary>
            Substring,
            /// <summary>归一化后以别名开头。</summary>
            Prefix
        }

        /// <summary>一条规则。</summary>
        class Rule
        {
            public string Category;
            public MatchMode Mode;
            public string Alias;      // 已归一化
            public string AliasRaw;   // 原始写法，用于报错
            public int Line;          // 来源行号
        }

        static readonly object gate = new object();
        static List<Rule> rules = new List<Rule>();
        static string loadedPath;
        static long loadedStamp = -1;   // 文件修改时间戳，用于热加载
        static bool loadedOnce;

        /// <summary>警告输出钩子（由 UI 层注入，见 Log）。</summary>
        public static Action<string> Warn = delegate { };

        public static string PathOf(string baseDir)
        {
            return Path.Combine(baseDir, "config", "category-rules.tsv");
        }

        public static int RuleCount
        {
            get { lock (gate) { return rules.Count; } }
        }

        /// <summary>
        /// 加载（或重新加载）规则表。带修改时间检查，文件没变则跳过重复解析。
        ///
        /// 由 Catalog.Load 在每次加载清单时调用，所以用户改完 TSV 后
        /// 「重新扫描应用」即可生效，不需要重启程序。
        /// </summary>
        public static void Init(string baseDir)
        {
            if (string.IsNullOrEmpty(baseDir)) return;
            try
            {
                string path = PathOf(baseDir);
                long stamp = -1;
                try
                {
                    if (File.Exists(path))
                        stamp = File.GetLastWriteTimeUtc(path).Ticks;
                }
                catch { }

                lock (gate)
                {
                    if (loadedOnce && stamp == loadedStamp && string.Equals(path, loadedPath, StringComparison.OrdinalIgnoreCase))
                        return;

                    List<Rule> parsed = null;

                    if (stamp >= 0)
                        parsed = ParseFile(path);

                    if (parsed == null)
                    {
                        // 文件不存在 → 写出内置默认表；解析失败 → 用内置表（不覆盖用户的坏文件）
                        if (stamp < 0)
                        {
                            WriteDefaultTable(path);
                        }
                        else
                        {
                            Warn("分类规则表解析失败，本次回退到内置默认规则: " + path);
                        }
                        parsed = ParseLines(DefaultTableText(), "内置默认表");
                    }

                    rules = parsed;
                    loadedPath = path;
                    loadedStamp = stamp;
                    loadedOnce = true;
                }
            }
            catch (Exception ex)
            {
                Warn("加载分类规则表失败，沿用上一次的规则: " + ex.Message);
            }
        }

        /// <summary>
        /// 按规则表判定分类。未命中返回 null（由调用方继续走内置逻辑）。
        /// </summary>
        public static string Guess(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            string text = NameMatch.Normalize(name);
            if (text.Length == 0) return null;
            string canon = NameMatch.Canonical(name);

            List<Rule> snapshot;
            lock (gate)
            {
                if (rules.Count == 0) return null;
                snapshot = rules;
            }

            // 长别名优先：更具体的名字应当赢过更宽泛的
            // （例如 `visual studio code` 不该被 `visual studio` 抢走）。
            // 规则表按别名长度从长到短排过序，这里顺序遍历即可。
            foreach (Rule r in snapshot)
            {
                bool hit;
                switch (r.Mode)
                {
                    case MatchMode.Exact:
                        hit = NameMatch.Exact(canon, r.Alias);
                        break;
                    case MatchMode.Substring:
                        hit = NameMatch.Contains(text, r.Alias);
                        break;
                    case MatchMode.Prefix:
                        hit = NameMatch.Prefix(text, r.Alias);
                        break;
                    default:
                        hit = NameMatch.Word(text, r.Alias);
                        break;
                }
                if (hit) return r.Category;
            }
            return null;
        }

        // ---------------- 解析 ----------------

        static List<Rule> ParseFile(string path)
        {
            try
            {
                string text = ReadText(path);
                if (text == null) return null;
                return ParseLines(text, path);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>读文件（自动跳过 BOM）。</summary>
        static string ReadText(string path)
        {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            return text;
        }

        /// <summary>
        /// 解析 TSV。返回 null 表示「解析失败」（调用方回退内置表）；
        /// 返回空列表表示「文件有效但没有可用规则」（视为用户有意清空，尊重它）。
        /// </summary>
        static List<Rule> ParseLines(string text, string source)
        {
            if (text == null) return null;

            List<Rule> list = new List<Rule>();
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int badLines = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line[0] == '#') continue;

                // 允许用制表符或连续空格分隔（用户可能从别处粘过来）
                string[] parts = line.Split(new char[] { '\t' }, 3);
                if (parts.Length < 3)
                {
                    string[] bySpace = line.Split(new char[] { ' ', '　' }, 3, StringSplitOptions.RemoveEmptyEntries);
                    if (bySpace.Length >= 3 && bySpace[1].Length <= 8 && bySpace[0].Length <= 12)
                        parts = bySpace;
                    else
                    {
                        badLines++;
                        continue;
                    }
                }

                string category = parts[0].Trim();
                string modeText = parts[1].Trim().ToLowerInvariant();
                string aliases = parts[2];

                if (category.Length == 0) { badLines++; continue; }

                MatchMode mode;
                if (!TryParseMode(modeText, out mode))
                {
                    badLines++;
                    continue;
                }

                foreach (string raw in aliases.Split('|'))
                {
                    string alias = raw.Trim();
                    if (alias.Length == 0) continue;

                    string normalized = (mode == MatchMode.Exact)
                        ? NameMatch.Canonical(alias)
                        : NameMatch.Normalize(alias);

                    if (normalized.Length == 0) continue;

                    Rule r = new Rule();
                    r.Category = category;
                    r.Mode = mode;
                    r.Alias = normalized;
                    r.AliasRaw = alias;
                    r.Line = i + 1;
                    list.Add(r);
                }
            }

            if (badLines > 0)
                Warn(string.Format("分类规则表有 {0} 行格式不对，已跳过（{1}）", badLines, source));

            // 长别名优先，保证更具体的规则先命中
            list.Sort(delegate(Rule a, Rule b)
            {
                int c = b.Alias.Length.CompareTo(a.Alias.Length);
                if (c != 0) return c;
                // 长度相同时 exact 优先于 word，避免短缩写被宽松模式抢走
                int ma = a.Mode == MatchMode.Exact ? 0 : 1;
                int mb = b.Mode == MatchMode.Exact ? 0 : 1;
                return ma.CompareTo(mb);
            });

            return list;
        }

        static bool TryParseMode(string text, out MatchMode mode)
        {
            switch (text)
            {
                case "word":
                case "词":
                case "":          // 省略方式时默认 word
                    mode = MatchMode.Word;
                    return true;
                case "exact":
                case "精确":
                    mode = MatchMode.Exact;
                    return true;
                case "sub":
                case "substring":
                case "包含":
                    mode = MatchMode.Substring;
                    return true;
                case "prefix":
                case "前缀":
                    mode = MatchMode.Prefix;
                    return true;
                default:
                    mode = MatchMode.Word;
                    return false;
            }
        }

        // ---------------- 默认表 ----------------

        /// <summary>
        /// 内置默认表。首次运行会写出到 config\category-rules.tsv。
        ///
        /// 这里只放**内置正则覆盖不到的**条目（主要是游戏与开发工具别名）。
        /// 内置正则能判的仍由 Catalog 处理，避免两处规则重复维护。
        /// </summary>
        public static string DefaultTableText()
        {
            return DefaultTable;
        }

        static void WriteDefaultTable(string path)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(path)) return;   // 不覆盖用户已有的文件

                // 直接落盘即可：这是「把内置表导出一份给用户改」，
                // 不是覆盖用户数据，所以不走 JsonIO 的备份流程。
                File.WriteAllText(path, DefaultTable, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Warn("写出默认分类规则表失败（不影响使用，将直接用内置规则）: " + ex.Message);
            }
        }

        /// <summary>
        /// 默认规则表内容。
        ///
        /// 说明：别名用 `word` 模式（带 ASCII 词边界），所以 `gta` 不会命中
        /// `gta5tool` 之类的无关名字；短缩写用 `exact`。
        /// </summary>
        const string DefaultTable = @"# 四叶草启动面板 - 分类规则表
#
# 用途：不改代码就能调整应用的自动分类。改完保存，右键四叶草 → 重新扫描应用即可生效。
#
# 格式：  分类 <TAB> 匹配方式 <TAB> 别名1|别名2|别名3
#   - 以 # 开头的是注释，空行忽略
#   - 别名之间用 | 分隔，一行可以写多个
#   - 匹配方式：
#       word   按词匹配（默认）。英文别名要求词边界，所以 chrome 不会命中 chromedriver；
#              中文别名按子串，所以 微信 能命中 微信电脑版
#       exact  归一化后完全相等。适合 gta、wps 这种短缩写，避免误判
#       sub    包含即可，最宽松，容易误判，慎用
#       prefix 以别名开头
#   - 优先级：右键「移到分类」的手工设置 > 本表 > 程序内置规则
#   - 本表命中后会直接采用，不再走内置规则；没命中才用内置规则
#
# 注意：归一化会折叠全角字符并忽略大小写，所以不必在意大小写。

# ============ 影音娱乐（游戏，内置规则只认 Steam 系，这里补独立的）============

影音娱乐	word	黑神话|黑神话悟空|black myth|wukong
影音娱乐	word	艾尔登法环|elden ring
影音娱乐	word	赛博朋克|cyberpunk
影音娱乐	word	荒野大镖客|red dead
影音娱乐	exact	gta|gta5|gta6|gtaiv
影音娱乐	word	侠盗猎车手|grand theft auto
影音娱乐	word	上古卷轴|elder scrolls|skyrim
影音娱乐	word	辐射|fallout
影音娱乐	word	星空|starfield
影音娱乐	word	巫师|witcher
影音娱乐	word	博德之门|baldur
影音娱乐	word	神界原罪|divinity
影音娱乐	word	霍格沃茨|hogwarts
影音娱乐	word	怪物猎人|monster hunter
影音娱乐	word	只狼|sekiro
影音娱乐	word	黑暗之魂|dark souls
影音娱乐	word	仁王|nioh
影音娱乐	word	死亡搁浅|death stranding
影音娱乐	word	最终幻想|final fantasy
影音娱乐	word	尼尔机械纪元|nier
影音娱乐	word	生化危机|resident evil
影音娱乐	word	寂静岭|silent hill
影音娱乐	word	古墓丽影|tomb raider
影音娱乐	word	刺客信条|assassin
影音娱乐	word	孤岛惊魂|far cry
影音娱乐	word	看门狗2|看门狗3|watch dogs
影音娱乐	word	地平线零之曙光|horizon zero
影音娱乐	word	战神4|战神5|god of war
影音娱乐	word	对马岛|ghost of tsushima
影音娱乐	word	最后生还者|the last of us
影音娱乐	word	匹诺曹|lies of p
影音娱乐	word	剑星|stellar blade
影音娱乐	word	天国拯救|kingdom come
影音娱乐	word	地铁离去|metro exodus
影音娱乐	word	消逝的光芒|dying light
影音娱乐	word	无主之地|borderlands
影音娱乐	word	质量效应|mass effect
影音娱乐	word	我的世界|minecraft
影音娱乐	word	幻兽帕鲁|palworld
影音娱乐	word	双人成行|it takes two
影音娱乐	word	恐鬼症|phasmophobia
影音娱乐	word	森林之子|sons of the forest
影音娱乐	word	幻塔|tower of fantasy
影音娱乐	word	鸣潮|wuthering waves
影音娱乐	word	崩坏|honkai
影音娱乐	word	绝区零|zenless
影音娱乐	word	永劫无间|naraka
影音娱乐	word	剑网|jx3
影音娱乐	word	英雄联盟|league of legends
影音娱乐	exact	lol
影音娱乐	word	穿越火线|crossfire
影音娱乐	word	地下城与勇士|dungeon fighter
影音娱乐	word	逆水寒|justice online
影音娱乐	word	原神|genshin
影音娱乐	word	阴阳师|onmyoji
影音娱乐	word	第五人格|identity v
影音娱乐	word	蛋仔派对|egg party
影音娱乐	word	光遇|sky children

# ============ 开发（Mod 管理器与调试注入，内置规则判不出来）============

开发	word	mod organizer|modorganizer
开发	exact	mo2|vortex
开发	word	nexus mod manager|nexusmods
开发	word	wemod|wrye bash
开发	word	smapi|stardewmodding
开发	word	skse
开发	word	f4se
开发	word	mod engine|modengine
开发	word	fluffy mod manager|fluffymanager
开发	word	hedgemodmanager
开发	word	r2modman|thunderstore
开发	word	curseforge
开发	word	bakkesmod
开发	word	reshade
开发	word	bepinex
开发	word	melonloader
开发	word	dnspy|ilspy
开发	word	cheat engine|cheatengine
开发	word	ida pro|ida64
开发	word	ghidra|x64dbg|x32dbg
开发	word	process hacker|procexp
开发	word	winmerge|beyond compare
开发	word	sourcetree|gitkraken|fork
开发	word	wireshark|fiddler|charles
开发	word	jprofiler|yourkit
开发	word	dbeaver|datagrip|navicat
开发	word	redis insight|another redis
开发	word	mongodb compass
开发	word	ollama|lm studio|lmstudio
开发	word	wsl
开发	word	docker desktop
开发	word	vscode|visual studio

# ============ 网络通讯（下载器、远程、代理、社交，内置规则没覆盖的）============

网络通讯	word	qbittorrent|utorrent|transmission
网络通讯	word	bitcomet|free download manager
网络通讯	word	idm|internet download manager
网络通讯	word	motrix|aria2
网络通讯	word	todesk|anydesk|teamviewer|rustdesk
网络通讯	word	向日葵|sunlogin
网络通讯	word	localsend|snapdrop|kde connect
网络通讯	word	slack|zoom|tencent meeting|腾讯会议
网络通讯	word	钉钉|dingtalk

# ============ 办公 ============

办公	word	typora|marktext|joplin
办公	word	xmind|mindmanager|processon
办公	word	visio|drawio|diagrams
办公	word	acrobat|foxit|pdf
办公	word	onenote|notion|evernote|印象笔记
办公	word	obsidian|logseq
办公	word	飞书|feishu|lark

# ============ 系统工具 ============

系统工具	word	geek uninstaller|geekuninstaller
系统工具	word	revo uninstaller
系统工具	word	ccleaner|bleachbit
系统工具	word	wise care|advanced systemcare
系统工具	word	crystaldiskinfo|crystaldiskmark
系统工具	word	cpuz|gpuz|hwinfo|aida64
系统工具	word	rufus|ventoy|ultraiso
系统工具	word	diskgenius|分区助手|傲梅
系统工具	word	treesize|wiztree|spacesniffer
系统工具	word	everything|listary|utools
系统工具	word	translucenttb|startallback|start11
系统工具	word	wallpaper engine
系统工具	word	autoruns|process monitor|procmon
系统工具	word	directx repair|directx修复
系统工具	word	图吧工具箱
";
    }
}
