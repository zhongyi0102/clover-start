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
        /// <summary>
        /// 面板外框色（比 Line 更淡）。
        ///
        /// 分两个层级：外框只负责"勾出面板边界"，内部（侧栏分隔线、磁贴边框）
        /// 用 Line。同一个颜色用在两处会显得边界过重 —— 玻璃质感的做法是
        /// 外框最淡、内部稍实。
        /// </summary>
        public static readonly SolidColorBrush Edge = new SolidColorBrush();
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

        /// <summary>
        /// 一套配色。
        ///
        /// 颜色用 **ARGB**（32 位），不再只有 RGB —— 因为"玻璃感"的关键
        /// 恰恰在于边框和分隔线要**半透明**，而不是实色。
        /// 参考 GlassBoard 的做法：边框用 alpha 0x2A / 0x24 的发丝线，
        /// 让下层颜色透一点上来，边界才不会像"贴了张纸"。
        /// </summary>
        class Palette
        {
            public uint Bg, Line, Fg, Sub, Hover, Pressed, SearchBg, Accent, AccentSoft, AccentText;
            /// <summary>面板外框（比内部分隔线更淡，只用来勾出边界）。</summary>
            public uint Edge;
        }

        // 浅色
        //
        // Line 从实色 #E2DFDA 改为半透明蓝灰：
        // 实色边框在任何底色上都是"硬线"，而发丝线会随底层明暗自然融合。
        // alpha 的实测校准：0x2A(16%) 叠在 #FBFAF8 上差值只有 53，
        // 肉眼几乎不可分（用户反馈"看不出来"）—— 提到 0x55(33%) 才有
        // "能看见但仍然柔和"的边界。
        // Edge 稍淡（alpha 26%），只负责勾边。
        static readonly Palette Light = new Palette
        {
            Bg = 0xFFFBFAF8,
            Line = 0x557687A2,
            Edge = 0x428A93A8,
            Fg = 0xFF202020,
            Sub = 0xFF787672,
            Hover = 0xFFF3F1ED,
            Pressed = 0xFFE8E5DF,
            SearchBg = 0xFFF2F0EC,
            Accent = 0xFF388C48,
            AccentSoft = 0xFFE8F3E9,
            AccentText = 0xFF1E692D
        };

        // 深色 —— 目标样式：深青玻璃（参考用户给的启动台截图）
        //
        // 色相从"中性灰"改为"青蓝"：
        //   #172A30 是那张截图里毛玻璃区域的主色调（占比 15.7%），
        //   比纯灰更耐看，且与壁纸的冷色系更协调。
        //
        // ★ Bg 的 alpha 是**实测调出来的**，两头都试过：
        //   · 0xC4(77%) → 壁纸上的文字（"我们买了50箱泡面"）透过来干扰阅读，
        //     实测视觉评估判为"中等干扰"
        //   · 0xFF(100%) → 毛玻璃完全看不见，退化成纯色板
        //   0xE6(90%) 是平衡点：玻璃的"透"还在（能感知到背后有明暗），
        //   但壁纸细节（文字、图案）被压到看不清，不干扰内容。
        //
        // Line 用冷白 alpha 0x3C(24%) —— 深底上的"微光"，再低就看不见。
        // Accent 系在深色下必须变亮：同一个绿色放深底上会显脏、对比度不足。
        static readonly Palette Dark = new Palette
        {
            Bg = 0xE6172A30,        // 深青黑，90% 不透明
            Line = 0x3CD5E3FF,      // 冷白发丝线
            Edge = 0x30D5E3FF,      // 外框更淡
            Fg = 0xFFF0F4F5,          // 近白（深底上要够亮）
            Sub = 0xFFA8B6BC,         // 青灰（比原先亮一档，小字要可读）
            Hover = 0x38FFFFFF,     // 白 22%（玻璃上的悬停是高光，不是加深）
            Pressed = 0x50FFFFFF,   // 白 31%
            SearchBg = 0x2EFFFFFF,  // 白 18%（搜索框比周围略亮）
            Accent = 0xFF4FC3D9,      // 青蓝强调色（与主色调同族，比绿更协调）
            AccentSoft = 0x332E8FA3,
            AccentText = 0xFF8FDCE8
        };

        /// <summary>32 位 ARGB（含 alpha）。</summary>
        static Color FromArgb32(uint argb)
        {
            return Color.FromArgb(
                (byte)((argb >> 24) & 0xFF),
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF));
        }

        /// <summary>24 位 RGB（alpha 固定 255）。</summary>
        static Color FromRgb24(uint rgb)
        {
            return Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        }

        static void Fill(Palette p)
        {
            // 全部用 ARGB —— Bg / Hover / Pressed / SearchBg 在深色下
            // 也需要半透明（玻璃感），浅色下它们的 alpha 是 FF，行为不变。
            Bg.Color = FromArgb32(p.Bg);
            Line.Color = FromArgb32(p.Line);
            Edge.Color = FromArgb32(p.Edge);
            Fg.Color = FromArgb32(p.Fg);
            Sub.Color = FromArgb32(p.Sub);
            Hover.Color = FromArgb32(p.Hover);
            Pressed.Color = FromArgb32(p.Pressed);
            SearchBg.Color = FromArgb32(p.SearchBg);
            Accent.Color = FromArgb32(p.Accent);
            AccentSoft.Color = FromArgb32(p.AccentSoft);
            AccentText.Color = FromArgb32(p.AccentText);
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
        /// 面板投影——**近距**那层。
        ///
        /// 双层投影（ambient + direct）：近距负责"接触"，远距负责"悬浮"。
        ///
        /// 不透明度经验值（实测校准过）：
        ///   · < 0.10 摊开后每像素不到 2%，肉眼不可见 —— 第一版用了 0.16，
        ///     结果"看不出来"（用户反馈）。0.28 起，浅色下才能看到投影贴着面板边。
        ///   · 深色可以更浓，因为暗底上投影本来就更难察觉。
        /// </summary>
        public static DropShadowEffect PanelShadowNear()
        {
            return AppliedTheme == ThemeMode.Dark
                ? Shadow(10, 0.55, 3)    // 深色：短而实
                : Shadow(12, 0.28, 3);   // 浅色
        }

        /// <summary>
        /// 面板投影——**远距**那层（环境光）。
        ///
        /// 大模糊会把不透明度摊到很大范围，每像素实际只占很小比例，
        /// 所以这层的标称值必须比看起来想要的效果**大得多**：
        /// 0.14 的远距投影实测几乎不存在，0.32 才有"浮起来"的感觉。
        /// </summary>
        public static DropShadowEffect PanelShadowFar()
        {
            return AppliedTheme == ThemeMode.Dark
                ? Shadow(44, 0.55, 12)   // 深色：靠浓度表现
                : Shadow(40, 0.32, 12);  // 浅色
        }

        /// <summary>
        /// 旧接口：单层投影。保留给弹窗等轻量场景（它们不需要双层）。
        /// </summary>
        public static DropShadowEffect PanelShadow()
        {
            return AppliedTheme == ThemeMode.Dark
                ? Shadow(34, 0.55, 4)
                : Shadow(30, 0.20, 6);
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
