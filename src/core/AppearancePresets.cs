using System;
using System.Collections.Generic;

namespace Clover
{
    /// <summary>
    /// 毛玻璃外观预设（集中定义，不散落在界面代码里）。
    ///
    /// **设计约束（来自实际渲染能力，不是拍脑袋）**
    ///
    /// 可调维度只有 6 个（settings.json 里那 6 个键）：
    ///   glassOpacity / glassBlur / glassTint / glassTintStrength /
    ///   glassBrightness / glassAccent
    ///
    /// **不可调**（写死在 GlassBackdrop）：
    ///   · GrainStrength = 0.025 —— 噪点层 opacity，最大影响 2.5%（约 6/255）
    ///   · HighlightStrength = 0.30 —— 1px 内边缘高光的 alpha
    ///
    /// 所以「磨砂玻璃」不能靠噪点做（那项不可调），只能靠**加大 blur**
    /// 来近似；「液态玻璃」也不能做出真正的折射，只能靠**低 opacity + 低 blur**
    /// 做出"清透"感。README 与测试都如实说明这个限制，不虚假宣称。
    ///
    /// **每个预设必须在至少 2 个维度上与相邻预设明显不同**，
    /// 否则用户看不出区别（用户明确要求"不要仅改名称却效果相同"）。
    /// </summary>
    static class AppearancePresets
    {
        /// <summary>预设名：自定义（用户手动调的参数）。</summary>
        public const string Custom = "custom";

        /// <summary>一个预设的完整参数组。</summary>
        public class Preset
        {
            /// <summary>内部名（存进 settings.glassPreset，不随界面语言变）。</summary>
            public string Key;

            /// <summary>界面显示名。</summary>
            public string DisplayName;

            /// <summary>一句话说明它的视觉特征（界面上做 tooltip）。</summary>
            public string Description;

            public int Opacity;            // glassOpacity      0-100
            public double Blur;            // glassBlur         0-30
            public string Tint;            // glassTint         "" = 用强调色
            public int TintStrength;       // glassTintStrength 0-100
            public int Brightness;         // glassBrightness   -50~50
            public string Accent;          // glassAccent       "" = 主题默认

            /// <summary>
            /// 主题处理：
            ///   "keep"  = 不改用户当前主题（默认，最安全）
            ///   "dark"  = 切到深色
            ///   "light" = 切到浅色
            ///
            /// **只有用户明确点「深色玻璃」「浅色玻璃」时才改主题** ——
            /// 这两个预设的名字本身就暗示了主题，改了符合预期。
            /// 其他预设一律 keep，避免用户点一下外观就把全局主题改掉。
            /// </summary>
            public string Theme;

            /// <summary>是不是「自定义」占位（没有固定参数）。</summary>
            public bool IsCustom;
        }

        /// <summary>
        /// 全部预设（顺序即界面顺序）。
        ///
        /// 「经典毛玻璃」的参数**精确等于**第 1 个提交的默认值，
        /// 所以老用户升级后选它 = 回到改动前的观感。
        /// </summary>
        static readonly Preset[] All = new Preset[]
        {
            new Preset
            {
                Key = Custom,
                DisplayName = "自定义",
                Description = "使用你当前手动调整的参数",
                IsCustom = true,
                Theme = "keep"
            },
            new Preset
            {
                Key = "classic",
                DisplayName = "经典毛玻璃",
                Description = "柔和、通用的半透明模糊（与改动前的默认观感一致）",
                Opacity = 80, Blur = 10.0, Tint = "", TintStrength = 10,
                Brightness = 0, Accent = "", Theme = "keep"
            },
            new Preset
            {
                Key = "frosted",
                DisplayName = "磨砂玻璃",
                Description = "模糊明显、发白发雾，像磨砂玻璃（噪点强度固定，靠 blur + 白染色近似）",
                Opacity = 78, Blur = 25.0, Tint = "#DCE4E8", TintStrength = 22,
                Brightness = 6, Accent = "", Theme = "keep"
            },
            new Preset
            {
                Key = "dark",
                DisplayName = "深色玻璃",
                Description = "深青色调、厚重，适合暗色界面",
                Opacity = 92, Blur = 12.0, Tint = "#172A30", TintStrength = 45,
                Brightness = -8, Accent = "#4FC3D9", Theme = "dark"
            },
            new Preset
            {
                Key = "light",
                DisplayName = "浅色玻璃",
                Description = "通透亮白，适合浅色界面",
                Opacity = 65, Blur = 8.0, Tint = "#FFFFFF", TintStrength = 30,
                Brightness = 18, Accent = "#388C48", Theme = "light"
            },
            new Preset
            {
                Key = "liquid",
                DisplayName = "液态玻璃",
                Description = "极通透、几乎不模糊，像水滴（真正的折射效果 WPF 做不了，这是近似）",
                // 液态感靠「很透 + 很清 + 略暗」三者叠加：
                //   · opacity 45（很透）
                //   · blur 3（几乎不模糊，边界清楚）
                //   · 冷色淡染色（像水滴的折射色偏）
                //   · brightness -12（压暗，与 classic 的 0 拉开距离）
                Opacity = 45, Blur = 3.0, Tint = "#7FA8B8", TintStrength = 26,
                Brightness = -18, Accent = "", Theme = "keep"
            }
        };

        /// <summary>全部预设（含 custom）。</summary>
        public static Preset[] AllPresets { get { return All; } }

        /// <summary>非 custom 的预设数量。</summary>
        public static int BuiltinCount
        {
            get
            {
                int n = 0;
                foreach (Preset p in All) if (!p.IsCustom) n++;
                return n;
            }
        }

        /// <summary>
        /// 按名字找预设。**大小写不敏感**；找不到返回 null（调用方回退 custom）。
        /// </summary>
        public static Preset Find(string key)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(key)) return null;
                string k = key.Trim();
                foreach (Preset p in All)
                {
                    if (string.Equals(p.Key, k, StringComparison.OrdinalIgnoreCase)) return p;
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 按名字找预设；找不到返回 custom 预设（永远不返回 null）。
        /// </summary>
        public static Preset FindOrCustom(string key)
        {
            Preset p = Find(key);
            if (p != null) return p;
            return Find(Custom) ?? All[0];
        }

        /// <summary>预设名是否合法（custom 或任一内置预设）。</summary>
        public static bool IsValidKey(string key)
        {
            return Find(key) != null;
        }

        /// <summary>
        /// 把一个预设的**参数**写进 Settings（不含 Theme —— 那由调用方决定）。
        ///
        /// custom 预设没有固定参数，调用它什么也不做（用户当前参数保持不变）。
        /// </summary>
        public static void ApplyParams(Preset p, Settings s)
        {
            try
            {
                if (p == null || s == null) return;
                if (p.IsCustom) return;   // 自定义：不改参数

                s.GlassPreset = p.Key;
                s.GlassOpacity = p.Opacity;
                s.GlassBlur = p.Blur;
                s.GlassTint = p.Tint ?? "";
                s.GlassTintStrength = p.TintStrength;
                s.GlassBrightness = p.Brightness;
                s.GlassAccent = p.Accent ?? "";
            }
            catch { }
        }

        /// <summary>
        /// 应用预设（参数 + 主题）。
        ///
        /// 主题规则：
        ///   · Theme == "keep" → **不动** s.Theme（保留用户当前全局主题）
        ///   · "dark" / "light" → 设置 s.Theme
        ///
        /// 只有「深色玻璃」「浅色玻璃」会改主题 —— 它们的名字本身就暗示了主题。
        /// 其他预设一律 keep，避免"点一下外观就把全局主题改掉"。
        /// </summary>
        public static void Apply(Preset p, Settings s)
        {
            try
            {
                if (p == null || s == null) return;
                if (p.IsCustom) { s.GlassPreset = Custom; return; }

                ApplyParams(p, s);

                if (string.Equals(p.Theme, "dark", StringComparison.OrdinalIgnoreCase))
                    s.Theme = "dark";
                else if (string.Equals(p.Theme, "light", StringComparison.OrdinalIgnoreCase))
                    s.Theme = "light";
                // "keep" 或其他值 → 不改
            }
            catch { }
        }

        /// <summary>
        /// 用户手动改了某个参数后调用：把 glassPreset 切成 custom。
        ///
        /// 为什么需要：预设名必须与实际参数一致。
        /// 用户选了「磨砂玻璃」又拖了 blur 滑块，那就不再是磨砂玻璃了。
        ///
        /// **不动任何参数值** —— 只改预设名，保证"保留修改后的效果"。
        /// </summary>
        public static void MarkCustom(Settings s)
        {
            try
            {
                if (s == null) return;
                if (!string.Equals(s.GlassPreset, Custom, StringComparison.OrdinalIgnoreCase))
                    s.GlassPreset = Custom;
            }
            catch { }
        }

        /// <summary>
        /// 当前参数是否与某个预设**完全一致**（用于"恢复预设默认值"和诊断）。
        ///
        /// 只比较参数，不比较 Theme（主题是独立的全局设置）。
        /// </summary>
        public static bool Matches(Preset p, Settings s)
        {
            try
            {
                if (p == null || s == null) return false;
                if (p.IsCustom) return false;   // custom 不参与"完全一致"判断

                return s.GlassOpacity == p.Opacity
                    && Math.Abs(s.GlassBlur - p.Blur) < 0.0001
                    && string.Equals(s.GlassTint ?? "", p.Tint ?? "", StringComparison.OrdinalIgnoreCase)
                    && s.GlassTintStrength == p.TintStrength
                    && s.GlassBrightness == p.Brightness
                    && string.Equals(s.GlassAccent ?? "", p.Accent ?? "", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// 找出与当前参数完全一致的预设名（没有则返回 null）。
        /// </summary>
        public static string DetectKey(Settings s)
        {
            try
            {
                foreach (Preset p in All)
                {
                    if (p.IsCustom) continue;
                    if (Matches(p, s)) return p.Key;
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 恢复当前预设的默认参数。
        ///
        /// 若当前是 custom（或预设名非法），回退到「经典毛玻璃」
        /// —— 它是改动前的默认观感，是最合理的"恢复默认"目标。
        ///
        /// **不动 Theme**：恢复外观参数不该顺带改全局主题。
        /// </summary>
        public static Preset RestoreDefaults(Settings s)
        {
            try
            {
                if (s == null) return null;

                Preset p = Find(s.GlassPreset);
                if (p == null || p.IsCustom)
                    p = Find("classic");

                if (p != null && !p.IsCustom)
                {
                    // 只写参数，不改 Theme
                    s.GlassPreset = p.Key;
                    s.GlassOpacity = p.Opacity;
                    s.GlassBlur = p.Blur;
                    s.GlassTint = p.Tint ?? "";
                    s.GlassTintStrength = p.TintStrength;
                    s.GlassBrightness = p.Brightness;
                    s.GlassAccent = p.Accent ?? "";
                }
                return p;
            }
            catch { return null; }
        }
    }
}
