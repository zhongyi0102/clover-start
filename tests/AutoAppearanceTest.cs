using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clover
{
    static class AutoAppearanceTest
    {
        static int passed, failed;
        static void Check(bool value, string name)
        { if (value) { passed++; Console.WriteLine("PASS  " + name); } else { failed++; Console.WriteLine("FAIL  " + name); } }

        [STAThread]
        static void Main()
        {
            TestMath(); TestSchemes(); TestPersistence(); TestRealLayers(); TestPanelAndDialog(); TestWiring();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static byte[] Solid(byte r, byte g, byte b)
        {
            byte[] pixels = new byte[64 * 40 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255; }
            return pixels;
        }
        static void TestMath()
        {
            Check(Math.Abs(MaterialColorMath.YFromLstar(50) - 18.41865) < 0.0001, "Material L*=50 reference Y");
            for (int i = 0; i <= 255; i++) Check(MaterialColorMath.Delinearized(MaterialColorMath.Linearized(i)) == i, "sRGB roundtrip " + i);
            Check(Math.Abs(WallpaperAppearance.Contrast(0, 0xFFFFFF) - 21) < 0.001, "black/white ratio");
            Check(WallpaperAppearance.Analyze(null, int.MaxValue, int.MaxValue, null, false) != null, "overflow-safe fallback");
        }
        static void TestSchemes()
        {
            WallpaperAppearance.Scheme dark = WallpaperAppearance.Analyze(Solid(0, 0, 0), 64, 40, null, false);
            WallpaperAppearance.Scheme light = WallpaperAppearance.Analyze(Solid(255, 255, 255), 64, 40, null, false);
            Check(dark.Dark && dark.Neutral, "black wallpaper: dark neutral");
            Check(!light.Dark && light.Neutral, "white wallpaper: light neutral");
            Check(WallpaperAppearance.Analyze(Solid(128, 128, 128), 64, 40, null, false).Neutral, "gray never invents hue");
            byte[][] fixtures = { Solid(0, 0, 0), Solid(255, 255, 255), Solid(128, 128, 128), Solid(20, 110, 190), Solid(180, 45, 60) };
            byte[] checker = Solid(255, 255, 255);
            for (int i = 0; i < checker.Length; i += 8) checker[i] = checker[i + 1] = checker[i + 2] = 0;
            Check(WallpaperAppearance.Analyze(checker, 64, 40, null, false).Blur > light.Blur, "busy wallpaper gets more blur");
            foreach (byte[] fixture in fixtures)
                foreach (bool dynamic in new bool[] { false, true })
                {
                    WallpaperAppearance.Scheme s = WallpaperAppearance.Analyze(fixture, 64, 40, null, dynamic);
                    Check(s.Blur >= 3 && s.Blur <= 14 && s.SurfaceAlpha >= 0 && s.SurfaceAlpha <= 1, "bounded runtime values");
                    Check(IndependentContrast(s.Accent, 0xFFFFFF) >= 4.5, "white on accent readable");
                    Check(IndependentContrast(s.Foreground, s.AccentSoft) >= 4.5, "selected foreground readable");
                    Check(IndependentContrast(s.Secondary, s.AccentSoft) >= 4.5, "selected secondary readable");
                    Check(IndependentContrast(s.AccentText, s.Surface) >= 4.5, "footer accent readable");
                    Check(IndependentContrast(s.Secondary, WallpaperAppearance.Mix(s.Surface, s.Foreground, 0.10)) >= 4.5,
                        "pressed secondary readable");
                    Check(IndependentContrast(s.Secondary, s.Surface) >= 4.5, "secondary on surface readable");
                    uint background = ((uint)fixture[2] << 16) | ((uint)fixture[1] << 8) | fixture[0];
                    Check(IndependentContrast(s.Secondary, WallpaperAppearance.Mix(background, s.Surface, s.SurfaceAlpha)) >= 4.5,
                        "actual composited secondary contrast");
                    if (dynamic)
                    {
                        Check(IndependentContrast(s.Secondary, WallpaperAppearance.Mix(0, s.Surface, s.SurfaceAlpha)) >= 4.5 &&
                            IndependentContrast(s.Secondary, WallpaperAppearance.Mix(0xFFFFFF, s.Surface, s.SurfaceAlpha)) >= 4.5,
                            "video safe even between opposite frames");
                        Check(WallpaperAppearance.Analyze(Solid(255, 255, 255), 64, 40, s, true).Dark == s.Dark,
                            "video does not invert theme per frame");
                    }
                }
            WallpaperAppearance.Scheme colored = WallpaperAppearance.Analyze(Solid(20, 110, 190), 64, 40, null, false);
            Check(!colored.Neutral, "blue wallpaper extracts an accent");
            Check(colored.Seed == WallpaperAppearance.Analyze(Solid(21, 111, 191), 64, 40, colored, false).Seed, "tiny changes keep accent stable");
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) WallpaperAppearance.Analyze(checker, 64, 40, null, false);
            Check(sw.ElapsedMilliseconds < 3000, "bounded 64x40 analysis performance");
        }
        static double IndependentContrast(uint a, uint b)
        {
            double x = IndependentLuminance(a), y = IndependentLuminance(b);
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }
        static double IndependentLuminance(uint rgb)
        { return 0.2126 * Channel((rgb >> 16) & 255) + 0.7152 * Channel((rgb >> 8) & 255) + 0.0722 * Channel(rgb & 255); }
        static double Channel(uint c)
        { double v = c / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }

        static void TestPersistence()
        {
            string root = Path.Combine(Path.GetTempPath(), "clover-auto-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Settings s = Settings.Load(root);
                Check(s.AutoAppearance, "new and legacy settings default automatic");
                s.GlassOpacity = 27; s.GlassBlur = 19; s.GlassAccent = "#884411"; s.Theme = "dark";
                s.Save();
                WallpaperAppearance.Analyze(Solid(0, 0, 0), 64, 40, null, false);
                s.AutoAppearance = false; s.Save();
                Settings loaded = Settings.Load(root);
                Check(!loaded.AutoAppearance && loaded.GlassOpacity == 27 && loaded.GlassBlur == 19 &&
                    loaded.GlassAccent == "#884411" && loaded.Theme == "dark", "manual settings survive automatic mode/save/reload");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        static void TestRealLayers()
        {
            Window window = new Window { Width = 400, Height = 300, ShowInTaskbar = false };
            Grid host = new Grid(); window.Content = host;
            Border content = new Border { Background = Brushes.Transparent };
            host.Children.Add(content);
            Border surface = new Border { IsHitTestVisible = false };
            host.Children.Insert(0, surface);
            GlassBackdrop glass = new GlassBackdrop(window, host, "");
            glass.AutomaticAppearance = true;
            glass.OnBackgroundSample = delegate(byte[] pixels)
            {
                WallpaperAppearance.Scheme s = WallpaperAppearance.Analyze(pixels, 64, 40, null, false);
                Ui.SetAutomaticPalette(s);
                surface.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Ceiling(s.SurfaceAlpha * 255),
                    (byte)(s.Surface >> 16), (byte)(s.Surface >> 8), (byte)s.Surface));
                glass.BlurRadius = s.Blur; glass.TintStrength = 0; glass.Brightness = 0;
            };
            glass.ContrastGuardEnabled = false;
            try
            {
                host.Measure(new Size(400, 300)); host.Arrange(new Rect(0, 0, 400, 300));
                MethodInfo attach = typeof(GlassBackdrop).GetMethod("Attach", BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (byte v in new byte[] { 0, 128, 255 })
                {
                    BitmapSource source = BitmapSource.Create(64, 40, 96, 96, PixelFormats.Bgra32, null, Solid(v, v, v), 64 * 4);
                    attach.Invoke(glass, new object[] { source });
                    host.UpdateLayout(); host.Measure(new Size(400, 300)); host.Arrange(new Rect(0, 0, 400, 300));
                    RenderTargetBitmap render = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);
                    render.Render(host); byte[] px = new byte[400 * 300 * 4]; render.CopyPixels(px, 400 * 4, 0);
                    int i = (150 * 400 + 200) * 4;
                    uint rgb = ((uint)px[i + 2] << 16) | ((uint)px[i + 1] << 8) | px[i];
                    uint fg = ((uint)Ui.Sub.Color.R << 16) | ((uint)Ui.Sub.Color.G << 8) | Ui.Sub.Color.B;
                    Check(IndependentContrast(fg, rgb) >= 4.5, "real WPF backdrop/overlay contrast " + v);
                    Console.WriteLine("      actual foreground=" + fg.ToString("X6") + " background=" + rgb.ToString("X6") +
                        " ratio=" + IndependentContrast(fg, rgb).ToString("F2"));
                    Check(object.ReferenceEquals(host.Children[host.Children.Count - 1], content), "content stays above backdrop");
                }
            }
            finally { glass.Clear(); glass.Stop(); window.Close(); Ui.SetAutomaticPalette(null); Ui.ApplyTheme(Ui.ThemeMode.Light); }
        }
        static void TestWiring()
        {
            string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
            string ui = File.ReadAllText(Path.Combine(root, "src", "ui", "PanelWindow.Appearance.cs"));
            Check(!ui.Contains("settings.Save(") && !ui.Contains("settings.Theme ="), "automatic runtime does not overwrite persisted settings");
            Check(ui.Contains("TimeSpan.FromSeconds(3)") && ui.Contains("appearanceTimer.Stop()"), "sampling throttled and disposed");
            Check(!ui.Contains("Reload(") && !ui.Contains("RenderTiles("), "appearance does not reload lists");
            string dialog = File.ReadAllText(Path.Combine(root, "src", "ui", "AppearanceDialog.cs"));
            Check(dialog.Contains("Content = \"自动适配壁纸\"") && dialog.Contains("Header = \"手动调整\""), "simple automatic/manual UI");
        }

        static void Pump()
        { Dispatcher.CurrentDispatcher.Invoke(new Action(delegate { }), DispatcherPriority.ApplicationIdle); }
        static T Field<T>(object target, string name)
        { return (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
        static void Call(object target, string name, params object[] args)
        { target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, args); }
        static void SavePng(FrameworkElement visual, string name)
        {
            visual.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
                (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual); PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
            using (FileStream stream = File.Create(path)) encoder.Save(stream);
            Console.WriteLine("Screenshot: " + path);
        }
        static void TestPanelAndDialog()
        {
            string root = Path.Combine(Path.GetTempPath(), "clover-auto-panel-" + Guid.NewGuid().ToString("N"));
            PanelWindow panel = null;
            try
            {
                if (Application.Current == null) new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Settings s = Settings.Load(root); s.GlassMode = "fixed"; s.GlassOpacity = 29;
                Directory.CreateDirectory(Path.Combine(root, "res"));
                byte[] wallpaper = Solid(30, 100, 150);
                for (int y = 0; y < 40; y++) for (int x = 0; x < 64; x++)
                { int i = (y * 64 + x) * 4; wallpaper[i] = (byte)(70 + x * 2); wallpaper[i + 1] = (byte)(60 + y * 3); }
                PngBitmapEncoder backgroundEncoder = new PngBitmapEncoder();
                backgroundEncoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(64, 40, 96, 96, PixelFormats.Bgra32, null, wallpaper, 64 * 4)));
                using (FileStream file = File.Create(Path.Combine(root, "res", "background-cyangray.png"))) backgroundEncoder.Save(file);
                string application = typeof(AutoAppearanceTest).Assembly.Location;
                Directory.CreateDirectory(Path.Combine(root, "config"));
                File.WriteAllText(Path.Combine(root, "config", "manual-apps.json"),
                    "{\"apps\":[{\"name\":\"测试文档应用\",\"path\":\"" + application.Replace("\\", "\\\\") + "\"}]}");
                s.GlassBlur = 17; s.GlassTintStrength = 12; s.GlassBrightness = -8; s.GlassDarkness = 23;
                s.GlassAccent = "#884411"; s.Theme = "light";
                panel = new PanelWindow(root, null, s) { Width = 860, Height = 550 };
                panel.Show(); Pump(); panel.ApplyGlassSetting(); Pump();
                Call(panel, "ApplyAutomaticSample", Solid(20, 110, 190));
                Border surface = Field<Border>(panel, "automaticSurface");
                Check(surface.Visibility == Visibility.Visible && !surface.IsHitTestVisible, "actual panel has nonblocking automatic surface");
                Border lowTile = (Border)typeof(PanelWindow).GetMethod("MakeTile", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(panel, new object[] { new AppItem { Name = "低价值测试", Path = application, LowValue = true }, 0 });
                Check(lowTile.Opacity == 1, "automatic low-value labels are not faded with icon");
                Check(s.GlassOpacity == 29 && s.GlassBlur == 17 && s.GlassBrightness == -8 && s.GlassDarkness == 23,
                    "actual application leaves manual parameters unchanged");
                TextBox search = Field<TextBox>(panel, "searchBox"); search.Text = "测试状态"; Pump();
                object content = Field<ScrollViewer>(panel, "contentScroll").Content;
                Call(panel, "ApplyAutomaticSample", Solid(180, 45, 60)); Pump();
                Check(search.Text == "测试状态" && object.ReferenceEquals(content, Field<ScrollViewer>(panel, "contentScroll").Content),
                    "wallpaper update keeps search and content identity");
                s.AutoAppearance = false; panel.ApplyGlassSetting(); Pump();
                Check(surface.Visibility == Visibility.Collapsed && Ui.AppliedTheme == Ui.ThemeMode.Light && Ui.Accent.Color.R == 0x88,
                    "switching to manual restores theme/accent and removes auto layer");
                GlassBackdrop backdrop = Field<GlassBackdrop>(panel, "glassBackdrop");
                Check(backdrop.BlurRadius == 17 && backdrop.Brightness == -8 && Math.Abs(backdrop.TintStrength - 0.12) < 0.001,
                    "switching to manual restores injected renderer parameters");
                s.AutoAppearance = true; panel.ApplyGlassSetting();
                Call(backdrop, "Attach", BitmapSource.Create(64, 40, 96, 96, PixelFormats.Bgra32, null, wallpaper, 64 * 4));
                search.Text = ""; Pump(); SavePng((FrameworkElement)panel.Content, "auto-appearance-panel.png");
                bool inspected = false;
                DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                timer.Tick += delegate
                {
                    foreach (Window dialog in Application.Current.Windows)
                    {
                        if (dialog.Title != "外观") continue;
                        timer.Stop();
                        CheckBox checkbox = Find<CheckBox>((DependencyObject)dialog.Content);
                        Expander expander = Find<Expander>((DependencyObject)dialog.Content);
                        Check(checkbox != null && checkbox.IsChecked == true && expander != null && !expander.IsExpanded && !expander.IsEnabled,
                            "actual dialog opens in compact automatic mode");
                        SavePng((FrameworkElement)dialog.Content, "auto-appearance-dialog.png");
                        inspected = true; dialog.Close(); break;
                    }
                };
                timer.Start(); AppearanceDialog.Show(panel, root, s, panel, null); timer.Stop();
                Check(inspected, "actual appearance dialog inspected and closed");
            }
            catch (Exception ex) { Check(false, "actual panel/dialog: " + ex.ToString()); }
            finally
            {
                if (panel != null) panel.Close();
                Ui.SetAutomaticPalette(null); Ui.SetAccentOverride(null); Ui.ApplyTheme(Ui.ThemeMode.Light);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
        static T Find<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent is T) return (T)parent;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            { T found = Find<T>(VisualTreeHelper.GetChild(parent, i)); if (found != null) return found; }
            return null;
        }
    }
}
