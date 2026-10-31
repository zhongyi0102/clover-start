using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// config\settings.json 的统一读写入口。
    /// 之前 TriggerWindow 自己读 hideTaskbar、PanelWindow 自己读 hiddenCategories，
    /// 各写各的容易互相覆盖；现在集中在这里，保存时会保留未知字段。
    /// </summary>
    class Settings
    {
        /// <summary>隐藏系统任务栏（当前状态）。默认 false。</summary>
        public bool HideTaskbar = false;

        /// <summary>是否启用“迷你任务栏”功能（左下角显示窗口按钮）。默认 true。</summary>
        public bool MiniTaskbar = true;

        /// <summary>是否允许“隐藏系统任务栏”这个功能。默认 true。</summary>
        public bool TaskbarHiding = true;

        // 注：`desktopAutoClean` / `desktopAutoCleanNoticeShown` 字段已移除
        // （桌面快捷方式自动移走功能下线）。老 settings.json 里若还有这两个键，
        // 会作为**未知字段原样保留**（Settings.Save 的保留逻辑），但不再有任何代码读它们。

        /// <summary>是否启用悬停唤出（全屏时鼠标移到按钮位置临时显示）。默认 true。</summary>
        public bool HoverReveal = true;

        /// <summary>是否启用全屏检测（全屏时自动隐藏悬浮按钮）。默认 true。</summary>
        public bool FullscreenDetect = true;

        /// <summary>强制安全模式：不隐藏任务栏、不启用迷你任务栏、不移动桌面快捷方式、不做全屏钩子。</summary>
        public bool SafeMode = false;

        // 注：`hiddenCategories` 字段已移除（分类系统下线）。
        // 老 settings.json 里若还有这个键，会作为**未知字段原样保留**
        // （Settings.Save 的保留逻辑），迁移代码直接读原始 JSON。
        /// <summary>
        /// 侧栏是否已收起。默认 false（展开）。
        ///
        /// 放进 settings.json 而不是单独一个文件：它和 miniTaskbar 一样是
        /// "用户对界面的一个持久偏好"，没必要为它多一个配置文件。
        /// </summary>
        public bool NavCollapsed = false;

        /// <summary>
        /// 主题：light / dark / system。默认 system（跟随 Windows）。
        ///
        /// 存字符串而不是枚举，避免 core 层依赖界面层的类型；
        /// 非法值统一按 system 处理（见 Ui.ParseTheme）。
        /// </summary>
        public string Theme = "system";

        /// <summary>
        /// 毛玻璃背景浓度（0-100）。0 = 关闭。
        ///
        /// 默认 0（关闭）：这个效果要截桌面背景，代价比纯色背景高
        /// （每次位置/主题变化要截一次屏，约 5-15 ms），
        /// 而且会让文字对比度依赖壁纸颜色。默认关、想要的人自己开。
        /// </summary>
        public int GlassOpacity = 0;

        /// <summary>
        /// 毛玻璃背景模式。
        ///
        ///   "fixed"  = 固定图 res\background-cyangray.png（默认，观感稳定）
        ///   "screen" = 跟随桌面壁纸（截屏 + 模糊）
        ///
        /// 为什么默认 fixed：截屏模式下壁纸上的文字/图案会透过来干扰阅读
        /// （实测壁纸上的"我们买了50箱泡面"透过 77% 不透明度的面板仍可辨）。
        /// fixed 则完全可控。想要"透出真实桌面"的观感可以切成 screen。
        /// </summary>
        public string GlassMode = "fixed";

        // ---------- 外观参数（第 1 个提交新增）----------
        //
        // 默认值必须与「加这些键之前」的写死常量**完全一致**，
        // 否则老用户升级后外观会变（用户明确要求「像素级不变」）。
        //
        // 写死常量的来源（GlassBackdrop.cs / Ui.cs）：
        //   BlurRadius        = 10.0
        //   TintStrength      = 0.10   （= 10%）
        //   GrainStrength     = 0.025  （= 2.5%，不在本次 6 个键里）
        //   HighlightStrength = 0.30   （= 30%，不在本次 6 个键里）
        //   Accent（深色）     = #4FC3D9 / 浅色 = #388C48
        //   Tint 色            = 取 Accent 同色（GlassBackdrop 里用 Ui.Accent.Color）

        /// <summary>
        /// 预设名。
        ///
        /// "custom" = 手动调过参数（默认）。
        /// 其他值见 AppearancePresets（第 2 个提交）。
        /// 未知值一律按 "custom" 处理，不报错。
        /// </summary>
        public string GlassPreset = "custom";

        /// <summary>
        /// 模糊半径（WPF BlurEffect.Radius，作用在**已缩小 1/4** 的图上）。
        ///
        /// 注意：视觉模糊 ≈ Radius × Downscale(4)，所以 10 实际看起来像 40px。
        /// 默认 10.0 = 改动前的写死值。
        /// 范围 0-30（0 = 不模糊，30 = 非常糊）。
        /// </summary>
        public double GlassBlur = 10.0;

        /// <summary>
        /// 染色色（#RRGGBB）。空 = 用当前强调色（改动前的行为）。
        ///
        /// 改动前 GlassBackdrop 直接用 Ui.Accent.Color 当染色色，
        /// 所以「空」这个默认值精确复刻了旧行为。
        /// </summary>
        public string GlassTint = "";

        /// <summary>
        /// 染色强度 0-100（%）。默认 10 = 改动前的 TintStrength 0.10。
        /// </summary>
        public int GlassTintStrength = 10;

        /// <summary>
        /// 亮度偏移 -50 ~ +50。默认 0 = 改动前没有这一步（不变）。
        ///
        /// 正值提亮、负值压暗，作用在模糊后的底图上。
        /// </summary>
        public int GlassBrightness = 0;

        /// <summary>
        /// 自定义强调色（#RRGGBB）。空 = 用主题默认（改动前的行为）。
        ///
        /// 这个键**真的会被应用**：Ui.ApplyTheme 里调 Ui.SetAccent。
        /// </summary>
        public string GlassAccent = "";

        /// <summary>
        /// 文字清晰保底：背景太亮/太暗导致文字读不清时，自动在**文字区域**叠一层
        /// 极淡的压暗（深色主题）或提亮（浅色主题），让对比度达到 WCAG。
        ///
        /// **只影响渲染结果，不写入任何配置**。默认开。
        /// 关掉后完全回到「不补偿」的渲染（补偿层被移除，不残留蒙层）。
        /// </summary>
        public bool TextContrastGuard = true;

        // ---------- 「文件」标签（2-3 新增）----------

        /// <summary>是否显示第三个标签「文件」。关掉后只有「常用」「全部」。</summary>
        public bool ShowFilesTab = true;

        /// <summary>
        /// **已废弃，仅用于一次性迁移**。旧版单选来源。
        /// 首次读取时若 filesSources 为空且此值非空，把它当作唯一选中项；之后不再写。
        /// </summary>
        public string FilesSource = "";

        /// <summary>
        /// 「文件」列表排序，格式 `列:方向`（如 `name:asc` / `modified:desc` / `size:desc`）。
        /// 兼容旧值：只有列名（如 `modified`）时按降序兜底。非法值回退 `modified:desc`。
        public string FilesSort = "modified:desc";

        /// <summary>
        /// 「文件」标签多选来源（FileFolders 里若干 path）。
        /// 空列表 = 没选 = 显示所有来源的文件。
        /// </summary>
        public List<string> FilesSources = new List<string>();



        /// <summary>
        /// 本次运行是否处于安全模式。
        ///
        /// **只在运行时生效，不写回 settings.json。**
        ///
        /// 为什么不直接改各个开关的值：`Save()` 会写**全部字段**，
        /// 而安全模式原本是在内存里把 6 个开关改成 false。于是安全模式期间
        /// 用户只要动任意一个设置（换主题、开关别的功能），
        /// 那 6 项降级就会被**永久固化进 settings.json**。
        /// 其中 `fullscreenDetect` / `hoverReveal` 没有界面开关，
        /// 一旦被固化就永久失效，只能手改 JSON。
        ///
        /// 现在改成：用户的偏好原封不动留在文件里，安全模式只让
        /// `Effective*` 系列返回 false，用户一退出安全模式立刻恢复原样。
        /// </summary>
        public static bool SafeModeActive = false;

        // ---- 安全模式感知的有效值 ----
        // 所有**消费方**（窗口、悬浮条、监听器）都应读这些，而不是直接读字段，
        // 否则安全模式挡不住。菜单显示则读原字段（见 TriggerWindow 的说明）。

        public bool EffectiveMiniTaskbar
        {
            get { return MiniTaskbar && !SafeModeActive; }
        }

        public bool EffectiveTaskbarHiding
        {
            get { return TaskbarHiding && !SafeModeActive; }
        }

        /// <summary>实际是否该隐藏任务栏（总开关 + 当前状态 + 安全模式）。</summary>
        public bool EffectiveHideTaskbar
        {
            get { return TaskbarHiding && HideTaskbar && !SafeModeActive; }
        }

        public bool EffectiveFullscreenDetect
        {
            get { return FullscreenDetect && !SafeModeActive; }
        }

        public bool EffectiveHoverReveal
        {
            get { return HoverReveal && !SafeModeActive; }
        }

        string path;

        /// <summary>
        /// 警告输出钩子。默认接到 ErrorLog（WPF 程序里由 Program 设置），
        /// 测试环境可以不设，避免 Settings 强依赖 WPF 的 ErrorLog。
        /// </summary>
        public static Action<string> Warn = delegate { };

        public static Settings Load(string baseDir)
        {
            Settings s = new Settings();
            s.path = Path.Combine(baseDir, "config", "settings.json");
            try
            {
                string json = JsonIO.Read(s.path);
                if (string.IsNullOrEmpty(json)) return s;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return s;

                object v;
                if (d.TryGetValue("hideTaskbar", out v) && v != null) s.HideTaskbar = Convert.ToBoolean(v);
                if (d.TryGetValue("miniTaskbar", out v) && v != null) s.MiniTaskbar = Convert.ToBoolean(v);
                if (d.TryGetValue("taskbarHiding", out v) && v != null) s.TaskbarHiding = Convert.ToBoolean(v);
                if (d.TryGetValue("hoverReveal", out v) && v != null) s.HoverReveal = Convert.ToBoolean(v);
                if (d.TryGetValue("fullscreenDetect", out v) && v != null) s.FullscreenDetect = Convert.ToBoolean(v);
                if (d.TryGetValue("safeMode", out v) && v != null) s.SafeMode = Convert.ToBoolean(v);
                if (d.TryGetValue("navCollapsed", out v) && v != null) s.NavCollapsed = Convert.ToBoolean(v);
                if (d.TryGetValue("theme", out v) && v != null) s.Theme = Convert.ToString(v);
                if (d.TryGetValue("glassOpacity", out v) && v != null) s.GlassOpacity = Convert.ToInt32(v);
                if (d.TryGetValue("glassMode", out v) && v != null) s.GlassMode = Convert.ToString(v);
                // ---- 外观参数（越界/格式错 → 回退默认 + 写日志，不崩溃）----
                // glassPreset 用白名单校验：未知值（旧配置 / 手改错）回退 "custom"
                // 白名单从 AppearancePresets 动态取，避免两处维护
                s.GlassPreset = ReadPresetName(d, "glassPreset", s.GlassPreset);
                s.GlassBlur = ReadDoubleClamped(d, "glassBlur", s.GlassBlur, 0.0, 30.0);
                s.GlassTint = ReadColor(d, "glassTint", s.GlassTint);
                s.GlassTintStrength = ReadIntClamped(d, "glassTintStrength", s.GlassTintStrength, 0, 100);
                s.GlassBrightness = ReadIntClamped(d, "glassBrightness", s.GlassBrightness, -50, 50);
                s.GlassAccent = ReadColor(d, "glassAccent", s.GlassAccent);
                // ---- 「文件」标签（缺键用默认值，非法值回退）----
                if (d.TryGetValue("showFilesTab", out v) && v != null)
                {
                    try { s.ShowFilesTab = Convert.ToBoolean(v); }
                    catch { Log.Warn("settings.showFilesTab 不是布尔值，已用默认 true"); }
                }
                // ---- 来源多选 ----
                s.FilesSource = ReadString(d, "filesSource", s.FilesSource, null);
                s.FilesSources = ReadStringList(d, "filesSources");

                // 一次性迁移：filesSources 为空、旧 filesSource 有值 → 当作唯一选中项
                // 迁移后不再写 filesSource（Save 时固定写空串）
                if (s.FilesSources.Count == 0 && !string.IsNullOrEmpty(s.FilesSource))
                {
                    s.FilesSources.Add(s.FilesSource);
                    Log.Warn("settings: 已把旧 filesSource 迁移到 filesSources");
                }
                s.FilesSort = ReadFilesSort(d, "filesSort", s.FilesSort);
                // 文字清晰保底：缺键时用默认 true（老配置也享受保底）
                if (d.TryGetValue("textContrastGuard", out v) && v != null)
                {
                    try { s.TextContrastGuard = Convert.ToBoolean(v); }
                    catch { Log.Warn("settings.textContrastGuard 不是布尔值，已用默认 true"); }
                }
            }
            catch (Exception ex)
            {
                Warn("读取 settings.json 失败，使用默认设置: " + ex.Message);
            }
            return s;
        }

        /// <summary>写回 settings.json；保留文件里已有的其它字段，不覆盖别人写的内容。</summary>
        public void Save()
        {
            try
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                // 先读回现有内容，保留未知字段
                try
                {
                    string oldJson = JsonIO.Read(path);
                    if (!string.IsNullOrEmpty(oldJson))
                    {
                        JavaScriptSerializer ser0 = new JavaScriptSerializer();
                        Dictionary<string, object> old = ser0.Deserialize<Dictionary<string, object>>(oldJson);
                        if (old != null) d = old;
                    }
                }
                catch { }

                d["hideTaskbar"] = HideTaskbar;
                d["miniTaskbar"] = MiniTaskbar;
                d["taskbarHiding"] = TaskbarHiding;
                d["hoverReveal"] = HoverReveal;
                d["fullscreenDetect"] = FullscreenDetect;
                d["safeMode"] = SafeMode;
                d["navCollapsed"] = NavCollapsed;
                d["theme"] = Theme;
                d["glassOpacity"] = GlassOpacity;
                d["glassMode"] = GlassMode;
                d["glassPreset"] = GlassPreset;
                d["glassBlur"] = GlassBlur;
                d["glassTint"] = GlassTint;
                d["glassTintStrength"] = GlassTintStrength;
                d["glassBrightness"] = GlassBrightness;
                d["glassAccent"] = GlassAccent;
                d["textContrastGuard"] = TextContrastGuard;
                d["showFilesTab"] = ShowFilesTab;
                d["filesSource"] = "";   // 已废弃，迁移后固定写空
                d["filesSources"] = FilesSources;
                d["filesSort"] = FilesSort;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                JsonIO.WriteAtomic(path, ser.Serialize(d));
            }
            catch (Exception ex)
            {
                Warn("保存 settings.json 失败: " + ex.Message);
            }
        }

        // ---------- 带校验的读取辅助（外观参数用）----------
        //
        // 共同原则：任何非法值都回退默认 + 写 error.log，绝不抛异常。
        // 用户明确要求「越界/颜色格式错 → 回退默认并写 error.log，不得崩溃」。

        static string ReadString(Dictionary<string, object> d, string key, string def, string[] allowed)
        {
            try
            {
                object v;
                if (!d.TryGetValue(key, out v) || v == null) return def;
                string s = Convert.ToString(v);
                if (string.IsNullOrWhiteSpace(s)) return def;
                s = s.Trim();
                if (allowed != null)
                {
                    bool ok = false;
                    foreach (string a in allowed)
                    {
                        if (string.Equals(a, s, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
                    }
                    if (!ok)
                    {
                        Log.Warn("settings." + key + " 值非法（" + s + "），已回退默认 " + def);
                        return def;
                    }
                }
                return s;
            }
            catch (Exception ex)
            {
                Log.Warn("settings." + key + " 读取失败（" + ex.Message + "），已回退默认 " + def);
                return def;
            }
        }

        static double ReadDoubleClamped(Dictionary<string, object> d, string key, double def, double min, double max)
        {
            try
            {
                object v;
                if (!d.TryGetValue(key, out v) || v == null) return def;
                double x = Convert.ToDouble(v);
                if (double.IsNaN(x) || double.IsInfinity(x))
                {
                    Log.Warn("settings." + key + " 不是有限数，已回退默认 " + def);
                    return def;
                }
                if (x < min || x > max)
                {
                    Log.Warn("settings." + key + " = " + x + " 越界 [" + min + ", " + max + "]，已回退默认 " + def);
                    return def;
                }
                return x;
            }
            catch (Exception ex)
            {
                Log.Warn("settings." + key + " 读取失败（" + ex.Message + "），已回退默认 " + def);
                return def;
            }
        }

        static int ReadIntClamped(Dictionary<string, object> d, string key, int def, int min, int max)
        {
            try
            {
                object v;
                if (!d.TryGetValue(key, out v) || v == null) return def;
                int x = Convert.ToInt32(v);
                if (x < min || x > max)
                {
                    Log.Warn("settings." + key + " = " + x + " 越界 [" + min + ", " + max + "]，已回退默认 " + def);
                    return def;
                }
                return x;
            }
            catch (Exception ex)
            {
                Log.Warn("settings." + key + " 读取失败（" + ex.Message + "），已回退默认 " + def);
                return def;
            }
        }

        /// <summary>
        /// 读 #RRGGBB 颜色。空串是合法值（表示"用默认"），格式错则回退 def。
        /// </summary>
        static string ReadColor(Dictionary<string, object> d, string key, string def)
        {
            try
            {
                object v;
                if (!d.TryGetValue(key, out v) || v == null) return def;
                string s = Convert.ToString(v);
                if (s == null) return def;
                s = s.Trim();
                if (s.Length == 0) return "";   // 空 = 用默认，合法

                if (!IsHexColor(s))
                {
                    Log.Warn("settings." + key + " 不是合法颜色（" + s + "），已回退默认 " + (def.Length == 0 ? "空" : def));
                    return def;
                }
                return "#" + s.TrimStart('#').ToUpperInvariant();
            }
            catch (Exception ex)
            {
                Log.Warn("settings." + key + " 读取失败（" + ex.Message + "），已回退默认");
                return def;
            }
        }

        /// <summary>
        /// 读预设名：必须是 custom 或任一内置预设，否则回退 def 并写日志。
        ///
        /// 为什么单独一个方法：白名单来自 AppearancePresets（集中定义），
        /// 不在这里硬编码一份，避免两处不同步。
        /// </summary>
        /// <summary>
        /// 读 filesSort，格式 `列:方向`：
        ///   · `name:asc` / `modified:desc` / `size:desc`
        ///   · 兼容旧值：只有列名（`modified`）→ 按降序兜底
        ///   · 非法值 → 回退 def（`modified:desc`）
        /// 只接受 modified / name / size 三列（type 已随排序按钮移除，但仍兼容旧值）。
        /// </summary>
        static string ReadFilesSort(Dictionary<string, object> d, string key, string def)
        {
            try
            {
                object v;
                if (!d.TryGetValue(key, out v) || v == null) return def;
                string s = Convert.ToString(v);
                if (string.IsNullOrWhiteSpace(s)) return def;
                s = s.Trim().ToLowerInvariant();

                string col = s;
                string dir = "desc";
                int colon = s.IndexOf(':');
                if (colon > 0)
                {
                    col = s.Substring(0, colon).Trim();
                    string dpart = s.Substring(colon + 1).Trim();
                    if (dpart == "asc" || dpart == "desc") dir = dpart;
                    else
                    {
                        Log.Warn("settings." + key + " 方向非法（" + dpart + "），已用 desc");
                    }
                }

                if (col == "modified" || col == "name" || col == "size")
                    return col + ":" + dir;

                // type 列已移除，旧配置回退到 modified:desc
                if (col == "type")
                {
                    Log.Warn("settings." + key + " 的 type 列已移除，回退 modified:desc");
                    return "modified:desc";
                }

                Log.Warn("settings." + key + " 值非法（" + s + "），已回退 " + def);
                return def;
            }
            catch (Exception ex)
            {
                Log.Warn("settings." + key + " 读取失败（" + ex.Message + "），已回退 " + def);
                return def;
            }
        }

        /// <summary>读字符串数组（缺键/类型错 → 空列表，不抛）。</summary>
        static List<string> ReadStringList(Dictionary<string, object> d, string key)
        {
            List<string> list = new List<string>();
            try
            {
                object v;
                if (!d.TryGetValue(key, out v) || v == null) return list;
                foreach (object o in JsonIO.AsArray(v))
                {
                    if (o == null) continue;
                    string s = Convert.ToString(o);
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                }
            }
            catch (Exception ex)
            {
                Log.Warn("settings." + key + " 读取失败（" + ex.Message + "），已用空列表");
            }
            return list;
        }

        static string ReadPresetName(Dictionary<string, object> d, string key, string def)
        {
            try
            {
                object v;
                if (!d.TryGetValue(key, out v) || v == null) return def;
                string s = Convert.ToString(v);
                if (string.IsNullOrWhiteSpace(s)) return def;
                s = s.Trim();

                if (AppearancePresets.IsValidKey(s))
                    return AppearancePresets.Find(s).Key;   // 规范化成内部名（小写）

                Log.Warn("settings." + key + " 不是已知预设（" + s + "），已回退 " + def);
                return def;
            }
            catch (Exception ex)
            {
                Log.Warn("settings." + key + " 读取失败（" + ex.Message + "），已回退 " + def);
                return def;
            }
        }

        /// <summary>是不是 #RRGGBB / RRGGBB 格式（大小写不敏感）。</summary>
        public static bool IsHexColor(string s)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(s)) return false;
                string h = s.Trim().TrimStart('#');
                if (h.Length != 6) return false;
                foreach (char c in h)
                {
                    if (!Uri.IsHexDigit(c)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>把 #RRGGBB 解析成 (r,g,b)。格式错返回 false。</summary>
        public static bool TryParseHexColor(string s, out byte r, out byte g, out byte b)
        {
            r = 0; g = 0; b = 0;
            try
            {
                if (!IsHexColor(s)) return false;
                string h = s.Trim().TrimStart('#');
                r = Convert.ToByte(h.Substring(0, 2), 16);
                g = Convert.ToByte(h.Substring(2, 2), 16);
                b = Convert.ToByte(h.Substring(4, 2), 16);
                return true;
            }
            catch { return false; }
        }
    }
}
