using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
            public uint Bg, Line, Fg, Sub, Hover, SearchBg, Accent, AccentSoft, AccentText;
        }

        // 浅色（原来的样子，保持不变）
        static readonly Palette Light = new Palette
        {
            Bg = 0xFBFAF8,
            Line = 0xE2DFDA,
            Fg = 0x202020,
            Sub = 0x787672,
            Hover = 0xF3F1ED,
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
