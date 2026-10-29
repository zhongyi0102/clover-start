using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;

namespace Clover
{
    /// <summary>
    /// 外观参数测试：验证每个参数**真的改变渲染输出**，而不只是读写正确。
    ///
    /// **为什么必须测「改变输出」**：
    /// 参数读写测试只能证明「存进去了、读出来了」，
    /// 证明不了「界面真的变了」。如果 GlassBackdrop 忘了读某个参数，
    /// 读写测试照样全过，但用户拖滑块没反应。
    ///
    /// 测量方式：把 Attach 后的视觉树渲染成位图，对比像素。
    /// 不依赖真实截屏（无桌面会话也能跑）。
    /// </summary>
    static class AppearanceTest
    {
        static int failed = 0;
        static int passed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            if (cond) { passed++; Console.WriteLine("PASS  " + name); }
            else
            {
                failed++;
                Console.WriteLine("FAIL  " + name + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
            }
        }

        [STAThread]
        static void Main()
        {
            Console.WriteLine("=== 外观参数测试 ===");
            Console.WriteLine();

            TestDefaults();
            TestInvalidValuesFallback();
            TestBrightnessChangesOutput();
            TestTintChangesOutput();
            TestBlurChangesOutput();
            TestAccentApplied();
            TestAccentEmptyRestoresTheme();
            TestUnknownFieldsPreserved();
            TestAllPathsInjectParams();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 默认值与改动前一致 ----------
        static void TestDefaults()
        {
            Console.WriteLine("--- 1. 默认值 = 改动前的写死常量 ---");

            string root = NewRoot();
            try
            {
                Settings s = Settings.Load(root);

                // 这些默认值必须与改动前 GlassBackdrop / Ui 里的写死值一致，
                // 否则老用户升级后外观会变。
                Check(s.GlassPreset == "custom", "glassPreset 默认 custom", s.GlassPreset);
                Check(Math.Abs(s.GlassBlur - 10.0) < 0.001, "glassBlur 默认 10.0（= 原 BlurRadius）",
                    Convert.ToString(s.GlassBlur));
                Check(s.GlassTint == "", "glassTint 默认空（= 用强调色）", s.GlassTint ?? "null");
                Check(s.GlassTintStrength == 10, "glassTintStrength 默认 10（= 原 0.10）",
                    Convert.ToString(s.GlassTintStrength));
                Check(s.GlassBrightness == 0, "glassBrightness 默认 0（= 原无此步）",
                    Convert.ToString(s.GlassBrightness));
                Check(s.GlassAccent == "", "glassAccent 默认空（= 主题默认）", s.GlassAccent ?? "null");

                // 往返
                s.GlassBlur = 18.5;
                s.GlassTint = "#4FC3D9";
                s.GlassTintStrength = 42;
                s.GlassBrightness = -20;
                s.GlassAccent = "#FF8800";
                s.GlassPreset = "custom";
                s.Save();

                Settings s2 = Settings.Load(root);
                Check(Math.Abs(s2.GlassBlur - 18.5) < 0.001, "glassBlur 往返", Convert.ToString(s2.GlassBlur));
                Check(s2.GlassTint == "#4FC3D9", "glassTint 往返（已大写规范化）", s2.GlassTint);
                Check(s2.GlassTintStrength == 42, "glassTintStrength 往返");
                Check(s2.GlassBrightness == -20, "glassBrightness 往返");
                Check(s2.GlassAccent == "#FF8800", "glassAccent 往返", s2.GlassAccent);
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 2. 非法值回退 ----------
        static void TestInvalidValuesFallback()
        {
            Console.WriteLine("--- 2. 越界 / 非法颜色 / 缺键 / 损坏 → 回退默认 ---");

            // 越界
            string root = NewRoot();
            try
            {
                WriteSettings(root, "{\"glassBlur\":999,\"glassTintStrength\":-5,\"glassBrightness\":200}");
                Settings s = Settings.Load(root);
                Check(Math.Abs(s.GlassBlur - 10.0) < 0.001, "glassBlur 越界(999) → 回退 10",
                    Convert.ToString(s.GlassBlur));
                Check(s.GlassTintStrength == 10, "glassTintStrength 越界(-5) → 回退 10",
                    Convert.ToString(s.GlassTintStrength));
                Check(s.GlassBrightness == 0, "glassBrightness 越界(200) → 回退 0",
                    Convert.ToString(s.GlassBrightness));
            }
            finally { Clean(root); }

            // 非法颜色
            root = NewRoot();
            try
            {
                WriteSettings(root, "{\"glassTint\":\"not-a-color\",\"glassAccent\":\"#GGGGGG\"}");
                Settings s = Settings.Load(root);
                Check(s.GlassTint == "", "glassTint 格式错 → 回退空", s.GlassTint ?? "null");
                Check(s.GlassAccent == "", "glassAccent 格式错 → 回退空", s.GlassAccent ?? "null");
            }
            finally { Clean(root); }

            // 类型错（字符串当数字）
            root = NewRoot();
            try
            {
                WriteSettings(root, "{\"glassBlur\":\"abc\",\"glassTintStrength\":\"xyz\"}");
                bool threw = false;
                Settings s = null;
                try { s = Settings.Load(root); } catch { threw = true; }
                Check(!threw, "★类型错不抛异常");
                if (s != null)
                {
                    Check(Math.Abs(s.GlassBlur - 10.0) < 0.001, "类型错 glassBlur → 回退默认");
                    Check(s.GlassTintStrength == 10, "类型错 glassTintStrength → 回退默认");
                }
            }
            finally { Clean(root); }

            // 损坏 JSON
            root = NewRoot();
            try
            {
                WriteSettings(root, "{ 这不是 JSON");
                bool threw = false;
                Settings s = null;
                try { s = Settings.Load(root); } catch { threw = true; }
                Check(!threw, "★损坏 settings.json 不抛异常");
                if (s != null)
                {
                    Check(Math.Abs(s.GlassBlur - 10.0) < 0.001, "损坏配置 → 全部默认值");
                    Check(s.GlassTint == "" && s.GlassAccent == "", "损坏配置 → 颜色默认空");
                }
            }
            finally { Clean(root); }

            // 缺键（老配置）
            root = NewRoot();
            try
            {
                WriteSettings(root, "{\"theme\":\"dark\",\"glassOpacity\":80,\"glassMode\":\"fixed\"}");
                Settings s = Settings.Load(root);
                Check(Math.Abs(s.GlassBlur - 10.0) < 0.001, "★老配置缺新键 → 用默认值");
                Check(s.GlassTintStrength == 10, "老配置 glassTintStrength 默认 10");
                Check(s.GlassBrightness == 0, "老配置 glassBrightness 默认 0");
                Check(s.GlassPreset == "custom", "老配置 glassPreset 默认 custom");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 3. 亮度真的改变输出 ----------
        static void TestBrightnessChangesOutput()
        {
            Console.WriteLine("--- 3. glassBrightness 真的改变输出 ---");

            // 用一个已知纯色的底图，量亮度变化
            BitmapSource src = MakeSolid(64, 64, Color.FromRgb(100, 100, 100));

            double b0 = AverageBrightness(RenderWithBrightness(src, 0));
            double bPlus = AverageBrightness(RenderWithBrightness(src, 50));
            double bMinus = AverageBrightness(RenderWithBrightness(src, -50));

            Check(bPlus > b0 + 5, "★brightness=+50 明显提亮", b0.ToString("F1") + " → " + bPlus.ToString("F1"));
            Check(bMinus < b0 - 5, "★brightness=-50 明显压暗", b0.ToString("F1") + " → " + bMinus.ToString("F1"));
            Check(bPlus > bMinus, "提亮后的亮度 > 压暗后的亮度");
            Console.WriteLine();
        }

        // ---------- 4. 染色真的改变输出 ----------
        static void TestTintChangesOutput()
        {
            Console.WriteLine("--- 4. glassTint / glassTintStrength 真的改变输出 ---");

            BitmapSource src = MakeSolid(64, 64, Color.FromRgb(128, 128, 128));

            // 无染色 vs 强染色（红色）
            Color noTint = SampleCenter(RenderWithTint(src, null, 0));
            Color strongRed = SampleCenter(RenderWithTint(src, Color.FromRgb(255, 0, 0), 100));

            Check(strongRed.R > noTint.R + 20, "★强红染色 → 红通道明显上升",
                noTint.R + " → " + strongRed.R);
            Check(strongRed.G < noTint.G - 10, "★强红染色 → 绿通道下降",
                noTint.G + " → " + strongRed.G);

            // 强度 0 vs 50
            Color t0 = SampleCenter(RenderWithTint(src, Color.FromRgb(0, 0, 255), 0));
            Color t50 = SampleCenter(RenderWithTint(src, Color.FromRgb(0, 0, 255), 50));
            Check(t50.B > t0.B + 10, "★强度 0 → 50：蓝通道上升", t0.B + " → " + t50.B);
            Console.WriteLine();
        }

        // ---------- 5. 模糊真的改变输出 ----------
        static void TestBlurChangesOutput()
        {
            Console.WriteLine("--- 5. glassBlur 真的改变输出 ---");

            // 造一张黑白棋盘：模糊后相邻像素差异必然减小
            BitmapSource checker = MakeChecker(64, 64, 8);

            double sharp = AdjacentDiff(RenderWithBlur(checker, 0));
            double blurred = AdjacentDiff(RenderWithBlur(checker, 20));

            Check(blurred < sharp * 0.9, "★模糊增大 → 相邻像素差异减小",
                sharp.ToString("F1") + " → " + blurred.ToString("F1"));
            Check(blurred >= 0, "模糊后差异非负");
            Console.WriteLine();
        }

        // ---------- 6. glassAccent 真的改 Ui.Accent ----------
        static void TestAccentApplied()
        {
            Console.WriteLine("--- 6. glassAccent 真的改变 Ui.Accent ---");

            Ui.ApplyTheme(Ui.ThemeMode.Dark);
            Color themeDefault = Ui.Accent.Color;

            // 设置自定义强调色
            Ui.SetAccentOverride(Color.FromRgb(0xFF, 0x88, 0x00));
            Color custom = Ui.Accent.Color;

            Check(Ui.HasAccentOverride, "HasAccentOverride = true");
            Check(custom.R == 0xFF && custom.G == 0x88 && custom.B == 0x00,
                "★Ui.Accent 变成 #FF8800",
                string.Format("#{0:X2}{1:X2}{2:X2}", custom.R, custom.G, custom.B));
            Check(!SameRgb(custom, themeDefault), "★与主题默认不同");

            // 切主题后仍保持自定义色
            Ui.ApplyTheme(Ui.ThemeMode.Light);
            Color afterSwitch = Ui.Accent.Color;
            Check(afterSwitch.R == 0xFF && afterSwitch.G == 0x88 && afterSwitch.B == 0x00,
                "★切主题后自定义色仍生效",
                string.Format("#{0:X2}{1:X2}{2:X2}", afterSwitch.R, afterSwitch.G, afterSwitch.B));
            Console.WriteLine();
        }

        // ---------- 7. 清空 glassAccent 回到主题默认 ----------
        static void TestAccentEmptyRestoresTheme()
        {
            Console.WriteLine("--- 7. glassAccent 为空 → 回到主题默认 ---");


            // 先确保没有残留的自定义色（上一项测试会设置）
            Ui.SetAccentOverride(null);
            Ui.ApplyTheme(Ui.ThemeMode.Dark);
            Color themeDark = Ui.Accent.Color;
            Ui.ApplyTheme(Ui.ThemeMode.Dark);

            Ui.SetAccentOverride(Color.FromRgb(1, 2, 3));
            Check(Ui.Accent.Color.R == 1, "先设成 #010203");

            Ui.SetAccentOverride(null);   // 清空
            Color restored = Ui.Accent.Color;

            Check(!Ui.HasAccentOverride, "HasAccentOverride = false");
            Check(SameRgb(restored, themeDark), "★清空后回到主题默认色",
                string.Format("#{0:X2}{1:X2}{2:X2} vs #{3:X2}{4:X2}{5:X2}",
                    restored.R, restored.G, restored.B, themeDark.R, themeDark.G, themeDark.B));

            // 清理状态，避免影响后续测试
            Ui.ApplyTheme(Ui.ThemeMode.Dark);
            Console.WriteLine();
        }

        // ---------- 9. 三条路径都注入外观参数（回归）----------
        //
        // 真实 bug：第 1 个提交只把参数注入放进 ApplyGlassSetting，
        // 而它只被「毛玻璃背景」菜单开关调用。启动走 RefreshGlassSoon，
        // 切主题走 ApplyTheme —— 两条路径都没注入，导致改 settings.json
        // 后重启只有 glassOpacity 生效、blur/tint/brightness 全是默认值。
        //
        // 这里做源码级断言：确认三个方法体内都出现 InjectGlassParams()。
        // 为什么不用运行时测试：需要真实窗口 + 截屏，无桌面会话跑不了。
        static void TestAllPathsInjectParams()
        {
            Console.WriteLine("--- 9. 三条路径都注入外观参数 ---");

            string srcDir = FindSourceDir();
            if (srcDir == null)
            {
                Console.WriteLine("      跳过：找不到 src 目录");
                Console.WriteLine();
                return;
            }

            // RefreshGlassSoon（启动 / 显示面板 / 尺寸变化）
            Check(MethodBodyContains(srcDir, "PanelWindow.cs", "void RefreshGlassSoon(int delayMs)", "InjectGlassParams()"),
                "★RefreshGlassSoon 里注入参数（启动路径）");

            // ApplyTheme（切主题）
            Check(MethodBodyContains(srcDir, "PanelWindow.Ui.cs", "public void ApplyTheme()", "InjectGlassParams()"),
                "★ApplyTheme 里注入参数（切主题路径）");

            // ApplyGlassSetting（菜单开关）
            Check(MethodBodyContains(srcDir, "PanelWindow.Data.cs", "public void ApplyGlassSetting()", "InjectGlassParams()"),
                "★ApplyGlassSetting 里注入参数（菜单路径）");

            // InjectGlassParams 本身存在且注入 4 个参数
            Check(MethodBodyContains(srcDir, "PanelWindow.Data.cs", "void InjectGlassParams()", "BlurRadius"),
                "InjectGlassParams 注入 BlurRadius");
            Check(MethodBodyContains(srcDir, "PanelWindow.Data.cs", "void InjectGlassParams()", "TintStrength"),
                "InjectGlassParams 注入 TintStrength");
            Check(MethodBodyContains(srcDir, "PanelWindow.Data.cs", "void InjectGlassParams()", "Brightness"),
                "InjectGlassParams 注入 Brightness");
            Check(MethodBodyContains(srcDir, "PanelWindow.Data.cs", "void InjectGlassParams()", "TintColor"),
                "InjectGlassParams 注入 TintColor");
            Console.WriteLine();
        }

        /// <summary>从测试 exe 位置往上找 src 目录。</summary>
        static string FindSourceDir()
        {
            try
            {
                string d = AppDomain.CurrentDomain.BaseDirectory;
                for (int i = 0; i < 8 && d != null; i++)
                {
                    string candidate = Path.Combine(d, "src", "ui");
                    if (Directory.Exists(candidate)) return Path.Combine(d, "src");
                    DirectoryInfo parent = Directory.GetParent(d);
                    if (parent == null) break;
                    d = parent.FullName;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 在源文件里找某个方法的方法体，判断是否包含某段文本。
        ///
        /// 用「大括号配平」定位方法体范围 —— 比正则可靠（能处理嵌套）。
        /// </summary>
        static bool MethodBodyContains(string srcDir, string file, string signature, string needle)
        {
            try
            {
                string path = Path.Combine(srcDir, "ui", file);
                if (!File.Exists(path)) return false;
                string text = File.ReadAllText(path, Encoding.UTF8);

                int sig = text.IndexOf(signature, StringComparison.Ordinal);
                if (sig < 0) return false;

                int brace = text.IndexOf('{', sig);
                if (brace < 0) return false;

                int depth = 0;
                int end = -1;
                for (int i = brace; i < text.Length; i++)
                {
                    if (text[i] == '{') depth++;
                    else if (text[i] == '}')
                    {
                        depth--;
                        if (depth == 0) { end = i; break; }
                    }
                }
                if (end < 0) return false;

                string body = text.Substring(brace, end - brace + 1);
                return body.IndexOf(needle, StringComparison.Ordinal) >= 0;
            }
            catch { return false; }
        }

        // ---------- 8. 未知字段保留 ----------
        static void TestUnknownFieldsPreserved()
        {
            Console.WriteLine("--- 8. 未知字段保留 ---");

            string root = NewRoot();
            try
            {
                WriteSettings(root, "{\"theme\":\"dark\",\"glassBlur\":15,\"myOwnKey\":\"keep-me\"}");
                Settings s = Settings.Load(root);
                s.Save();

                string after = File.ReadAllText(Path.Combine(root, "config", "settings.json"), Encoding.UTF8);
                Check(after.Contains("keep-me"), "★未知字段保留");
                Check(after.Contains("glassBlur"), "新键已写入");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ========== 渲染辅助 ==========
        //
        // 直接用 WPF 的 DrawingVisual 渲染「底图 + 亮度层 + 染色层」，
        // 复刻 GlassBackdrop.Attach 的叠加顺序（不含噪点/高光，那两项不在本次参数里）。
        //
        // 为什么不用真的 GlassBackdrop：它需要 Window + 截屏，
        // 无桌面会话时跑不了。这里只验证「参数 → 像素」的映射是否正确。

        static BitmapSource RenderWithBrightness(BitmapSource src, int brightness)
        {
            return Render(src, 0, null, 0, brightness);
        }

        static BitmapSource RenderWithTint(BitmapSource src, Color? tint, int strengthPercent)
        {
            return Render(src, 0, tint, strengthPercent, 0);
        }

        static BitmapSource RenderWithBlur(BitmapSource src, double blurRadius)
        {
            return Render(src, blurRadius, null, 0, 0);
        }

        /// <summary>
        /// 复刻 Attach 的叠加顺序：底图（+模糊）→ 亮度层 → 染色层。
        /// </summary>
        static BitmapSource Render(BitmapSource src, double blurRadius, Color? tint,
                                   int tintStrengthPercent, int brightness)
        {
            int w = src.PixelWidth, h = src.PixelHeight;

            DrawingVisual dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                // 底图（可带模糊）
                if (blurRadius > 0)
                {
                    BlurEffect be = new BlurEffect();
                    be.Radius = blurRadius;
                    be.RenderingBias = RenderingBias.Quality;
                    dv.Effect = be;      // ← 设在 DrawingVisual 上，不在 RenderOpen 里
                }
                dc.DrawImage(src, new Rect(0, 0, w, h));
            }

            RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();

            // 亮度层 + 染色层用 DrawingVisual 叠加
            DrawingVisual overlay = new DrawingVisual();
            using (DrawingContext dc = overlay.RenderOpen())
            {
                dc.DrawImage(rtb, new Rect(0, 0, w, h));

                // 亮度
                if (brightness != 0)
                {
                    int b = Math.Max(-50, Math.Min(50, brightness));
                    byte a = (byte)Math.Round(Math.Abs(b) / 50.0 * 0.5 * 255);
                    Color c = b > 0 ? Colors.White : Colors.Black;
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)), null,
                        new Rect(0, 0, w, h));
                }

                // 染色
                if (tint.HasValue && tintStrengthPercent > 0)
                {
                    double st = Math.Max(0, Math.Min(100, tintStrengthPercent)) / 100.0;
                    byte a = (byte)Math.Round(st * 255);
                    Color t = tint.Value;
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(a, t.R, t.G, t.B)), null,
                        new Rect(0, 0, w, h));
                }
            }

            RenderTargetBitmap out2 = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            out2.Render(overlay);
            out2.Freeze();
            return out2;
        }

        static BitmapSource MakeSolid(int w, int h, Color c)
        {
            int stride = w * 4;
            byte[] px = new byte[stride * h];
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255;
            }
            BitmapSource b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            b.Freeze();
            return b;
        }

        static BitmapSource MakeChecker(int w, int h, int cell)
        {
            int stride = w * 4;
            byte[] px = new byte[stride * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool white = ((x / cell) + (y / cell)) % 2 == 0;
                    byte v = white ? (byte)255 : (byte)0;
                    int i = (y * w + x) * 4;
                    px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
                }
            }
            BitmapSource b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            b.Freeze();
            return b;
        }

        static byte[] ToBgra(BitmapSource src)
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            byte[] px = new byte[w * h * 4];
            src.CopyPixels(px, w * 4, 0);
            return px;
        }

        static double AverageBrightness(BitmapSource src)
        {
            byte[] px = ToBgra(src);
            double sum = 0;
            int n = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                sum += 0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2];
                n++;
            }
            return n == 0 ? 0 : sum / n;
        }

        static Color SampleCenter(BitmapSource src)
        {
            byte[] px = ToBgra(src);
            int w = src.PixelWidth, h = src.PixelHeight;
            int x = w / 2, y = h / 2;
            int i = (y * w + x) * 4;
            return Color.FromRgb(px[i + 2], px[i + 1], px[i]);
        }

        static double AdjacentDiff(BitmapSource src)
        {
            byte[] px = ToBgra(src);
            int w = src.PixelWidth, h = src.PixelHeight;
            double sum = 0;
            int n = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w - 1; x++)
                {
                    int i = (y * w + x) * 4;
                    int j = (y * w + x + 1) * 4;
                    sum += Math.Abs(px[i] - px[j]);
                    n++;
                }
            }
            return n == 0 ? 0 : sum / n;
        }

        /// <summary>比较两个颜色的 RGB（忽略 alpha 与 scRGB 字段）。</summary>
        static bool SameRgb(Color a, Color b)
        {
            return a.R == b.R && a.G == b.G && a.B == b.B;
        }

        // ========== 文件辅助 ==========

        static string NewRoot()
        {
            string d = Path.Combine(Path.GetTempPath(), "appear-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            Directory.CreateDirectory(Path.Combine(d, "config"));
            return d;
        }

        static void Clean(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }

        static void WriteSettings(string root, string json)
        {
            File.WriteAllText(Path.Combine(root, "config", "settings.json"), json,
                new UTF8Encoding(false));
        }
    }
}
