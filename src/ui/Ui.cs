using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Clover
{
    static class Ui
    {
        /// <summary>主题模式。System = 跟随 Windows 的浅色/深色设置。</summary>
        public enum ThemeMode { Light, Dark, System }

        // ---------- 颜色刷子 ----------
        //
        // 这里**故意不 Freeze**：全项目 126 处调用点持有的是这些刷子的
        // **对象引用**，所以切换主题时只要改 brush.Color，整棵界面立刻变色 ——
        // 既不用重建控件树，也不用去改那 15 个文件里的调用点。
        //
        // 代价与约束：
        //   · 未冻结的刷子比冻结的略慢（100+ 磁贴下可忽略）
        //   · 未冻结的 Freezable **只能在创建它的线程上改**，即界面线程。
        //     本程序所有刷子使用都在界面线程（面板与三个悬浮窗都由 WPF 调度器创建，
        //     AppWatcher 的后台线程只调 Ui.Log，不碰刷子），所以这条天然成立。
        public static readonly SolidColorBrush Bg = new SolidColorBrush();
        public static readonly SolidColorBrush Line = new SolidColorBrush();
        public static readonly SolidColorBrush Fg = new SolidColorBrush();
        public static readonly SolidColorBrush Sub = new SolidColorBrush();
        public static readonly SolidColorBrush Hover = new SolidColorBrush();
        /// <summary>
        /// 按下态（比 Hover 再深一档）。
        ///
        /// 补全"静止 / 悬停 / 按下"三态：此前磁贴只有悬停和选中，
        /// 鼠标按下去到松开之间**没有任何视觉反馈** —— 点一个启动稍慢的应用时，
        /// 用户不知道是"没点中"还是"正在启动"。
        /// </summary>
        public static readonly SolidColorBrush Pressed = new SolidColorBrush();
        public static readonly SolidColorBrush SearchBg = new SolidColorBrush();
        public static readonly SolidColorBrush Accent = new SolidColorBrush();
        public static readonly SolidColorBrush AccentSoft = new SolidColorBrush();
        public static readonly SolidColorBrush AccentText = new SolidColorBrush();

        public static readonly FontFamily YaHei = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
        public static readonly FontFamily Mdl = new FontFamily("Segoe MDL2 Assets");

        /// <summary>当前已应用的主题（System 会被解析成 Light 或 Dark）。</summary>
        public static ThemeMode AppliedTheme = ThemeMode.Light;

        /// <summary>主题切换后的回调（各窗口用它重画临时控件上的颜色）。</summary>
        public static Action ThemeChanged = delegate { };

        /// <summary>一套配色。</summary>
        class Palette
        {
            public uint Bg, Line, Fg, Sub, Hover, Pressed, SearchBg, Accent, AccentSoft, AccentText;
        }

        // 浅色（原来的样子，保持不变）
        static readonly Palette Light = new Palette
        {
            Bg = 0xFBFAF8,
            Line = 0xE2DFDA,
            Fg = 0x202020,
            Sub = 0x787672,
            Hover = 0xF3F1ED,
            Pressed = 0xE8E5DF,
            SearchBg = 0xF2F0EC,
            Accent = 0x388C48,
            AccentSoft = 0xE8F3E9,
            AccentText = 0x1E692D
        };

        // 深色。
        //
        // 注意 Accent 系在深色下必须**变亮**：同一个 #388C48 放在深底上会显脏、
        // 对比度不足；AccentText 反向变亮（它是在 AccentSoft 底上的文字）。
        static readonly Palette Dark = new Palette
        {
            Bg = 0x1E1F22,
            Line = 0x35383D,
            Fg = 0xE9E9EA,
            Sub = 0x9A9AA0,
            Hover = 0x2A2C31,
            Pressed = 0x33363C,
            SearchBg = 0x26282C,
            Accent = 0x54B863,
            AccentSoft = 0x24352A,
            AccentText = 0x8FD89B
        };

        static Color FromRgb24(uint rgb)
        {
            return Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        }

        static void Fill(Palette p)
        {
            Bg.Color = FromRgb24(p.Bg);
            Line.Color = FromRgb24(p.Line);
            Fg.Color = FromRgb24(p.Fg);
            Sub.Color = FromRgb24(p.Sub);
            Hover.Color = FromRgb24(p.Hover);
            Pressed.Color = FromRgb24(p.Pressed);
            SearchBg.Color = FromRgb24(p.SearchBg);
            Accent.Color = FromRgb24(p.Accent);
            AccentSoft.Color = FromRgb24(p.AccentSoft);
            AccentText.Color = FromRgb24(p.AccentText);
        }

        /// <summary>
        /// 应用主题。System 会读注册表决定用浅色还是深色。
        /// 幂等：同一主题重复调用只是再写一遍颜色。
        /// </summary>
        public static void ApplyTheme(ThemeMode mode)
        {
            ThemeMode effective = mode;
            if (mode == ThemeMode.System) effective = SystemPrefersDark() ? ThemeMode.Dark : ThemeMode.Light;

            AppliedTheme = effective;
            Fill(effective == ThemeMode.Dark ? Dark : Light);

            try { ThemeChanged(); }
            catch { }
        }

        /// <summary>
        /// 读 Windows 的「应用模式」是浅色还是深色。
        /// AppsUseLightTheme: 0 = 深色，1 = 浅色；键不存在时按浅色处理。
        /// </summary>
        public static bool SystemPrefersDark()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k == null) return false;
                    object v = k.GetValue("AppsUseLightTheme");
                    if (v == null) return false;
                    return Convert.ToInt32(v) == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把设置里的字符串转成主题模式（非法值按「跟随系统」）。</summary>
        public static ThemeMode ParseTheme(string s)
        {
            if (string.Equals(s, "light", StringComparison.OrdinalIgnoreCase)) return ThemeMode.Light;
            if (string.Equals(s, "dark", StringComparison.OrdinalIgnoreCase)) return ThemeMode.Dark;
            return ThemeMode.System;
        }

        /// <summary>主题模式转成存进 settings.json 的字符串。</summary>
        public static string ThemeToString(ThemeMode m)
        {
            switch (m)
            {
                case ThemeMode.Light: return "light";
                case ThemeMode.Dark: return "dark";
                default: return "system";
            }
        }

        /// <summary>给菜单显示用的中文名。</summary>
        public static string ThemeDisplayName(ThemeMode m)
        {
            switch (m)
            {
                case ThemeMode.Light: return "浅色";
                case ThemeMode.Dark: return "深色";
                default: return "跟随系统";
            }
        }

        public static TextBlock T(string text, double size, Brush color)
        {
            return T(text, size, color, FontWeights.Normal);
        }

        public static TextBlock T(string text, double size, Brush color, FontWeight weight)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontFamily = YaHei;
            t.FontSize = size;
            t.Foreground = color;
            t.FontWeight = weight;
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }

        public static DropShadowEffect Shadow(double blur, double opacity, double depth)
        {
            DropShadowEffect e = new DropShadowEffect();
            e.BlurRadius = blur;
            e.ShadowDepth = depth;
            e.Opacity = opacity;
            e.Color = Color.FromRgb(0, 0, 0);
            return e;
        }

        /// <summary>
        /// 按当前主题给面板投影。
        ///
        /// 深色下要**加深且拉近**：黑投影落在 #1E1F22 的底上本来就几乎看不见，
        /// 沿用浅色的 0.20 会让面板边界糊在背景里。
        /// </summary>
        public static DropShadowEffect PanelShadow()
        {
            return AppliedTheme == ThemeMode.Dark
                ? Shadow(34, 0.55, 4)    // 深色：更浓、更贴近（投影在暗背景上靠浓度而非距离表现）
                : Shadow(30, 0.20, 6);   // 浅色：保持原来的观感
        }

        // ════════════════════════════════════════════════════════════
        //  设计令牌（design tokens）
        //
        //  原先数值散落在各文件里：圆角有 0/1/2/6/7/8/10/12/16/17 共 **10 种**，
        //  间距混了 1/2/6/10/18 这些不成倍数的值。看起来"每处都合理"，
        //  合起来就没有体系 —— 这是最典型的"AI 拼出来"的痕迹。
        //  这里收敛成有限几档，改的时候只改这里。
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 圆角三档。
        ///
        /// 按元素**层级**选，不按大小选：控件 → 卡片 → 容器，越大越圆。
        /// 统一圆角会显得像盖章盖出来的（每个元素都一个样）。
        /// </summary>
        public static class R
        {
            /// <summary>小：按钮、磁贴、列表项、输入框。</summary>
            public const double Sm = 8;
            /// <summary>中：对话框、卡片。</summary>
            public const double Md = 12;
            /// <summary>大：面板主体、大容器。</summary>
            public const double Lg = 16;
            /// <summary>胶囊：搜索框这种完全圆头（高的一半）。</summary>
            public const double Pill = 999;
        }

        /// <summary>
        /// 间距阶梯，**全是 4 的倍数**。
        ///
        /// 4px 基准是屏幕像素与视觉节奏的公约数：既能表达细腻层次，
        /// 又保证任意两档之和不出现奇数（奇数间距在高 DPI 下会半像素糊边）。
        /// 原来的 1/6/10/18 会让相邻元素的边界落在半像素上。
        /// </summary>
        public static class S
        {
            /// <summary>4 — 紧贴（图标与文字之间）。</summary>
            public const double Xs = 4;
            /// <summary>8 — 相关元素之间（磁贴间距）。</summary>
            public const double Sm = 8;
            /// <summary>12 — 同组元素之间。</summary>
            public const double Md = 12;
            /// <summary>16 — 组与组之间（面板内边距）。</summary>
            public const double Lg = 16;
            /// <summary>20 — 区域与区域之间。</summary>
            public const double Xl = 20;
        }

        // ──────────── 缓动 ────────────
        //
        // 此前全项目 **0 处缓动**，所有动画都是 WPF 默认的线性插值。
        // 线性动画的问题是两端速度不变：入场结尾"急停"像被掐断，
        // 出场开头"猛冲"像被踢了一脚。
        //
        // 只用 CubicEase 这一个家族：
        //   · EaseOut → 入场（快起慢收，落得稳）
        //   · EaseIn  → 出场（慢起快收，走得干脆）
        //   · EaseInOut → 需要"对称感"的位移
        //
        // ⚠ 明确不用 BackEase / ElasticEase / BounceEase：
        //   那是"回弹/弹性"效果，在桌面工具类界面里会显得轻浮、花哨，
        //   而且会让高频操作（反复开关面板）产生晕眩感。

        static readonly IEasingFunction EaseOut = FreezeEase(EasingMode.EaseOut);
        static readonly IEasingFunction EaseIn = FreezeEase(EasingMode.EaseIn);
        static readonly IEasingFunction EaseInOut = FreezeEase(EasingMode.EaseInOut);

        static IEasingFunction FreezeEase(EasingMode mode)
        {
            CubicEase e = new CubicEase();
            e.EasingMode = mode;
            e.Freeze();   // 冻结后可跨控件/跨次复用，避免反复 new
            return e;
        }

        /// <summary>
        /// 入场缓动（快起慢收）。动画对象会被冻结，可安全跨控件共享。
        ///
        /// 注意：**冻结的动画不能再改属性**（`From`、`To`）。
        /// 所以这里只在"起点固定"的场合用（如悬停放大固定从 1.0 起）。
        /// 起点每次不同的动画（如面板透明度从当前值起）用 <see cref="EnterAnim"/>。
        /// </summary>
        public static DoubleAnimation Enter(double to, double ms, double? from = null)
        {
            DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            a.EasingFunction = EaseOut;
            if (from.HasValue) a.From = from.Value;
            a.Freeze();
            return a;
        }

        /// <summary>出场缓动（慢起快收），冻结版。同上：起点固定时用。</summary>
        public static DoubleAnimation Exit(double to, double ms, double? from = null)
        {
            DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            a.EasingFunction = EaseIn;
            if (from.HasValue) a.From = from.Value;
            a.Freeze();
            return a;
        }

        /// <summary>对称缓动（两端都柔），冻结版。用于位移类。</summary>
        public static DoubleAnimation Shift(double to, double ms, double? from = null)
        {
            DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            a.EasingFunction = EaseInOut;
            if (from.HasValue) a.From = from.Value;
            a.Freeze();
            return a;
        }

        // ── 可复用的未冻结动画（起点每回合都要改）──
        //
        // 面板的透明度/位移动画起点每次都不同（当前值 → 目标值），
        // 所以必须是**未冻结**的对象，由调用方在复用前重设 From。
        // 仍然复用同一个对象实例（而不是每次 new），避免反复开关面板
        // 造成托管堆增长 —— 这个坑项目里踩过一次。

        /// <summary>建一个可复用的入场动画（起点留空，调用方每次设 From）。</summary>
        public static DoubleAnimation ReusableEnter(double to, double ms)
        {
            DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            a.EasingFunction = EaseOut;
            return a;
        }

        /// <summary>建一个可复用的出场动画（起点留空）。</summary>
        public static DoubleAnimation ReusableExit(double to, double ms)
        {
            DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            a.EasingFunction = EaseIn;
            return a;
        }

        public static void Log(string msg)
        {
            try
            {
                string p = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clover.log");
                System.IO.File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>只输出到 clover.log，同时把异常细节写进 error.log。</summary>
        public static void LogError(Exception ex, string where)
        {
            Log(where + ": " + (ex == null ? "(null)" : ex.Message));
            ErrorLog.Hook(ex, where);
        }

        /// <summary>读取图片；失败（文件被删、损坏、权限不足）时返回 null，不抛异常拖垮窗口构造。</summary>
        public static BitmapImage LoadImage(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
                BitmapImage b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.UriSource = new Uri(path, UriKind.Absolute);
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("读取图片失败: " + path + " -> " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 加载四叶草图标。单文件 exe 场景下没有外部 res\ 目录，
        /// 所以优先读 <paramref name="externalPath"/>，读不到就用**嵌入资源**兜底。
        /// 两条路都失败时返回 null（调用方显示首字占位，不崩）。
        /// </summary>
        public static BitmapImage LoadCloverIcon(string externalPath)
        {
            BitmapImage img = LoadImage(externalPath);
            if (img != null) return img;

            // 兜底：编译时嵌入的程序集资源（见 build.ps1 的 /resource 参数）
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (System.IO.Stream s = asm.GetManifestResourceStream("CloverStart.clover.png"))
                {
                    if (s == null)
                    {
                        // 资源名可能带目录前缀，模糊匹配一次
                        foreach (string name in asm.GetManifestResourceNames())
                        {
                            if (name.EndsWith("clover.png", StringComparison.OrdinalIgnoreCase))
                            {
                                using (System.IO.Stream s2 = asm.GetManifestResourceStream(name))
                                {
                                    if (s2 != null) return DecodeStream(s2);
                                }
                            }
                        }
                        return null;
                    }
                    return DecodeStream(s);
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("读取嵌入图标失败: " + ex.Message);
                return null;
            }
        }

        static BitmapImage DecodeStream(System.IO.Stream stream)
        {
            BitmapImage b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.StreamSource = stream;
            b.EndInit();
            b.Freeze();
            return b;
        }
    }
}
