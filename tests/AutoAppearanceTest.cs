using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
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
            TestMath(); TestSchemes(); TestPersistence(); TestRealLayers(); TestPanelAndDialog(); TestWiring(); TestCover(); TestTextGeometry(); TestVideoFrame();
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
                    Check(s.Blur >= 0 && s.Blur <= 2 && s.SurfaceAlpha >= 0 && s.SurfaceAlpha <= 0.08, "image detail retained with bounded light treatment");
                    Check(IndependentContrast(s.Accent, 0xFFFFFF) >= 4.5, "white on accent readable");
                    Check(IndependentContrast(s.Foreground, s.AccentSoft) >= 4.5, "selected foreground readable");
                    Check(IndependentContrast(s.Secondary, s.AccentSoft) >= 4.5, "selected secondary readable");
                    Check(IndependentContrast(s.AccentText, s.Surface) >= 4.5, "footer accent readable");
                    Check(IndependentContrast(s.Secondary, WallpaperAppearance.Mix(s.Surface, s.Foreground, 0.10)) >= 4.5,
                        "pressed secondary readable");
                    Check(IndependentContrast(s.Secondary, s.Surface) >= 4.5, "secondary on surface readable");
                    uint background = ((uint)fixture[2] << 16) | ((uint)fixture[1] << 8) | fixture[0];
                    double local = WallpaperAppearance.TextProtectionAlpha(fixture, 64, 40, 0, 0, 64, 40,
                        s.Secondary, s.Surface, s.Surface, s.SurfaceAlpha, dynamic);
                    uint baseColor = WallpaperAppearance.Mix(background, s.Surface, s.SurfaceAlpha);
                    Check(IndependentContrast(s.Secondary, WallpaperAppearance.Mix(baseColor, s.Surface, local)) >= 4.5,
                        "local backing makes actual composited secondary readable");
                    if (dynamic)
                    {
                        Check(IndependentContrast(s.Secondary, WallpaperAppearance.Mix(0, s.Surface, local)) >= 4.5 &&
                            IndependentContrast(s.Secondary, WallpaperAppearance.Mix(0xFFFFFF, s.Surface, local)) >= 4.5,
                            "video text safe between opposite frames without full-panel veil");
                        Check(WallpaperAppearance.Analyze(Solid(255, 255, 255), 64, 40, s, true).Dark == s.Dark,
                            "video does not invert theme per frame");
                    }
                }
            WallpaperAppearance.Scheme colored = WallpaperAppearance.Analyze(Solid(20, 110, 190), 64, 40, null, false);
            Check(!colored.Neutral, "blue wallpaper extracts an accent");
            Check(colored.Seed == WallpaperAppearance.Analyze(Solid(21, 111, 191), 64, 40, colored, false).Seed, "tiny changes keep accent stable");
            WallpaperAppearance.Scheme moving = WallpaperAppearance.Analyze(Solid(180, 45, 60), 64, 40, colored, true);
            Check(moving.Dark == colored.Dark && moving.Seed != colored.Seed && moving.Seed != 0xB42D3C,
                "dynamic palette moves gradually without immediate hue jump");
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
            Grid content = new Grid();
            TextBlock label = Ui.T("文字保护", 12, Ui.Sub);
            label.Width = 100; label.Height = 24; label.HorizontalAlignment = HorizontalAlignment.Left;
            label.VerticalAlignment = VerticalAlignment.Top; label.Margin = new Thickness(20, 20, 0, 0);
            content.Children.Add(label);
            WallpaperTextProtection protection = new WallpaperTextProtection(content);
            host.Children.Add(content);
            host.Children.Insert(0, protection);
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
                protection.SetBackground(pixels, s, true);
            };
            glass.ContrastGuardEnabled = false;
            try
            {
                window.Left = -30000; window.Top = -30000; window.Show(); Pump();
                host.Measure(new Size(400, 300)); host.Arrange(new Rect(0, 0, 400, 300));
                MethodInfo attach = typeof(GlassBackdrop).GetMethod("Attach", BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (byte v in new byte[] { 0, 128, 255 })
                {
                    BitmapSource source = BitmapSource.Create(64, 40, 96, 96, PixelFormats.Bgra32, null, Solid(v, v, v), 64 * 4);
                    attach.Invoke(glass, new object[] { source });
                    host.UpdateLayout(); host.Measure(new Size(400, 300)); host.Arrange(new Rect(0, 0, 400, 300));
                    protection.Rebuild();
                    host.UpdateLayout(); Pump();
                    RenderTargetBitmap render = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);
                    render.Render(host); byte[] px = new byte[400 * 300 * 4]; render.CopyPixels(px, 400 * 4, 0);
                    int i = (41 * 400 + 115) * 4;
                    uint rgb = ((uint)px[i + 2] << 16) | ((uint)px[i + 1] << 8) | px[i];
                    uint fg = ((uint)Ui.Sub.Color.R << 16) | ((uint)Ui.Sub.Color.G << 8) | Ui.Sub.Color.B;
                    Check(IndependentContrast(fg, rgb) >= 4.5, "real WPF local backing contrast " + v);
                    Console.WriteLine("      actual foreground=" + fg.ToString("X6") + " background=" + rgb.ToString("X6") +
                        " ratio=" + IndependentContrast(fg, rgb).ToString("F2"));
                    Check(object.ReferenceEquals(host.Children[host.Children.Count - 1], content), "content stays above backdrop");
                    Check(protection.RegionCount == 1 && protection.ProtectedArea < 400 * 300 * 0.03,
                        "only text bounds protected; empty space untouched");
                    int empty = (150 * 400 + 200) * 4;
                    Check(Math.Abs(px[empty + 2] - v) < 12, "empty wallpaper keeps original brightness " + v);
                }
            }
            finally { glass.Clear(); glass.Stop(); window.Close(); Ui.SetAutomaticPalette(null); Ui.ApplyTheme(Ui.ThemeMode.Light); }
        }

        static void TestCover()
        {
            foreach (int[] source in new int[][] { new[] { 2880, 5120 }, new[] { 5120, 2880 }, new[] { 1, 1 } })
                foreach (int[] panel in new int[][] { new[] { 912, 632 }, new[] { 632, 912 }, new[] { 1920, 1080 } })
                {
                    Int32Rect crop = GlassBackdrop.CalculateCoverCrop(source[0], source[1], panel[0], panel[1]);
                    Check(crop.Width > 0 && crop.Height > 0 && crop.X >= 0 && crop.Y >= 0 &&
                        crop.X + crop.Width <= source[0] && crop.Y + crop.Height <= source[1], "cover crop stays within source");
                    Check(Math.Abs(crop.X * 2 + crop.Width - source[0]) <= 1 &&
                        Math.Abs(crop.Y * 2 + crop.Height - source[1]) <= 1, "cover uses centered generic anchor");
                    if (source[0] > 1)
                        Check(Math.Abs((double)crop.Width / crop.Height - (double)panel[0] / panel[1]) < 0.002,
                            "portrait and landscape retain non-integer aspect");
                }
        }

        static void TestTextGeometry()
        {
            Window window = new Window { Width = 300, Height = 240, ShowInTaskbar = false, Left = -30000, Top = -30000 };
            Grid host = new Grid(), content = new Grid();
            WallpaperTextProtection protection = new WallpaperTextProtection(content);
            host.Children.Add(protection); host.Children.Add(content); window.Content = host;
            ScrollViewer scroll = new ScrollViewer { Height = 80, Width = 200, VerticalAlignment = VerticalAlignment.Top,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
            StackPanel list = new StackPanel();
            for (int i = 0; i < 30; i++) list.Children.Add(new TextBlock { Text = "文件来源 " + i, Height = 24, Foreground = Ui.Fg });
            scroll.Content = list; content.Children.Add(scroll);
            try
            {
                window.Show(); Pump();
                WallpaperAppearance.Scheme s = WallpaperAppearance.Analyze(Solid(20, 100, 160), 64, 40, null, true);
                Ui.SetAutomaticPalette(s); protection.SetBackground(Solid(20, 100, 160), s, true); Pump(); protection.Rebuild();
                Check(protection.RegionCount >= 3 && protection.RegionCount <= 4, "scroll clips offscreen text protection");
                object same = scroll.Content; scroll.ScrollToVerticalOffset(240); Pump(); protection.Rebuild();
                Console.WriteLine("      scroll regions=" + protection.RegionCount + " offset=" + scroll.VerticalOffset);
                Check(protection.RegionCount >= 3 && protection.RegionCount <= 4 && object.ReferenceEquals(same, scroll.Content),
                    "scroll repositions protection without replacing file content");
                content.Children.Clear();
                Border card = new Border { Background = Ui.SearchBg, Width = 140, Height = 30,
                    Child = new TextBlock { Text = "搜索框", Foreground = Ui.Fg } };
                content.Children.Add(card); Pump(); protection.Rebuild();
                Check(protection.RegionCount == 0, "readable opaque control needs no extra text backing");
                content.Children.Clear();
                Grid inlineRow = new Grid { Height = 24, VerticalAlignment = VerticalAlignment.Top };
                inlineRow.Children.Add(new TextBlock { Text = "来源一", Width = 60, Height = 24, Foreground = Ui.Fg,
                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10, 0, 0, 0) });
                inlineRow.Children.Add(new TextBlock { Text = "来源二", Width = 60, Height = 24, Foreground = Ui.Fg,
                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(76, 0, 0, 0) });
                content.Children.Add(inlineRow); Pump(); protection.Rebuild();
                Check(protection.RegionCount == 1 && protection.ProtectedArea < 140 * 28,
                    "adjacent same-row labels share a bounded backing without rebuilding controls");
                ((TextBlock)inlineRow.Children[1]).Margin = new Thickness(170, 0, 0, 0);
                Pump(); protection.Rebuild();
                Check(protection.RegionCount == 2, "separated text preserves unprotected space between labels");
            }
            finally { window.Close(); Ui.SetAutomaticPalette(null); Ui.ApplyTheme(Ui.ThemeMode.Light); }
        }

        static void TestVideoFrame()
        {
            // Optional local synthetic fixture, never Workshop content. CI remains
            // independent of a third-party encoder or installed video codecs.
            string fixture = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appearance-video-fixture.mp4");
            if (!File.Exists(fixture)) { Console.WriteLine("SKIP  local synthetic video fixture unavailable"); return; }
            Window window = new Window { Width = 200, Height = 300, Left = -30000, Top = -30000, ShowInTaskbar = false };
            Grid host = new Grid(); window.Content = host;
            VideoBackdrop video = new VideoBackdrop(window, host);
            try
            {
                window.Show(); video.SetSource(fixture); video.ApplyPolicy(true, false, false);
                byte[] sample = null; Stopwatch sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 6000)
                {
                    Pump(); sample = video.SampleFrame();
                    if (sample != null && sample[(20 * 64 + 32) * 4 + 1] > 20) break;
                    System.Threading.Thread.Sleep(20);
                }
                Check(sample != null, "actual decoded video supplies background-only frame");
                if (sample != null)
                {
                    int i = (20 * 64 + 32) * 4;
                    Console.WriteLine("      video center=" + sample[i + 2] + "," + sample[i + 1] + "," + sample[i] + " alpha=" + sample[i + 3]);
                    MediaElement media = Field<MediaElement>(video, "media");
                    Console.WriteLine("      video layout=" + media.RenderSize + " host=" + host.RenderSize + " offset=" + media.TranslatePoint(new Point(), host));
                    Check(sample[i + 1] > sample[i + 2] * 2 && sample[i + 3] > 240,
                        "actual portrait viewport samples centered green video region");
                }
                video.ApplyPolicy(false, false, false); Check(video.IsPaused, "video hidden policy pauses existing player");
            }
            finally { video.Stop(); video.Unload(); window.Close(); }
        }
        static void TestWiring()
        {
            string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
            string ui = File.ReadAllText(Path.Combine(root, "src", "ui", "PanelWindow.Appearance.cs"));
            Check(!ui.Contains("settings.Save(") && !ui.Contains("settings.Theme ="), "automatic runtime does not overwrite persisted settings");
            Check(ui.Contains("TimeSpan.FromSeconds(3)") && ui.Contains("appearanceTimer.Stop()"), "sampling throttled and disposed");
            Check(!ui.Contains("Reload(") && !ui.Contains("RenderTiles("), "appearance does not reload lists");
            string dialog = File.ReadAllText(Path.Combine(root, "src", "ui", "AppearanceDialog.cs"));
            Check(dialog.Contains("Content = \"自动适配壁纸\"") && dialog.Contains("manualSettings.Click"), "explicit automatic/manual mode action");
            Check(!dialog.Contains("chip.MouseLeftButtonUp") && !dialog.Contains("b.MouseLeftButtonUp") && dialog.Contains("MakeAppearanceButtonTemplate"),
                "all appearance actions use native buttons rather than mouse-up-only borders");
            Check(dialog.Contains("dlg.ShowDialog(fileOwner)"), "image picker owned by appearance window");
            Check(dialog.Contains("settings.GlassMode == \"custom\"") && dialog.Contains("更换壁纸…"),
                "active source can be replaced and WE has explicit replacement entry");
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
                string importError;
                BackgroundStore.Import(root, Path.Combine(root, "res", "background-cyangray.png"), out importError);
                string application = typeof(AutoAppearanceTest).Assembly.Location;
                Directory.CreateDirectory(Path.Combine(root, "config"));
                File.WriteAllText(Path.Combine(root, "config", "window-positions.json"),
                    "{\"appearance\":{\"left\":100,\"top\":60,\"width\":520,\"height\":730,\"maximized\":false}}");
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
                        try
                        {
                            DependencyObject visual = (DependencyObject)dialog.Content;
                            CheckBox checkbox = Find<CheckBox>(visual);
                            Button manual = FindButton(visual, "手动调整…");
                            Check(checkbox != null && checkbox.IsChecked == true && manual != null && manual.IsEnabled,
                                "actual automatic dialog has usable manual entry");
                            Check(Find<Expander>(visual) == null && dialog.ActualHeight < 460,
                                "obsolete tall window memory replaced by content height without disabled expander");
                            Check(dialog.ResizeMode == ResizeMode.CanResize && Find<ResizeGrip>(visual) == null,
                                "dialog remains resizable without duplicate grip");
                            SavePng((FrameworkElement)dialog.Content, "auto-appearance-dialog.png");
                            double automaticHeight = dialog.ActualHeight;
                            InvokeButton(manual); Pump();
                            Check(!s.AutoAppearance && checkbox.IsChecked == false && dialog.ActualHeight > automaticHeight + 100,
                                "manual button changes mode and grows window to parameters");
                            Check(s.GlassOpacity == 29 && s.GlassBlur == 17 && s.GlassBrightness == -8 && s.GlassDarkness == 23,
                                "manual entry preserves existing values");
                            SavePng((FrameworkElement)dialog.Content, "manual-appearance-dialog.png");
                            Button color = FindNamedButton(visual, "染色 #172A30");
                            InvokeButton(color); Pump();
                            Check(s.GlassTint == "#172A30", "native color swatch invoke applies selected value");
                            TextBox colorInput = FindNamed<TextBox>(visual, "染色十六进制颜色");
                            Check(colorInput.Text == "#172A30", "color swatch keeps hex input synchronized");
                            colorInput.Text = "not-a-color";
                            colorInput.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent)); Pump();
                            Check(colorInput.Text == "not-a-color" && s.GlassTint == "#172A30",
                                "invalid color stays editable and does not overwrite settings");
                            colorInput.Text = "";
                            colorInput.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent)); Pump();
                            Check(s.GlassTint == "", "empty color restores default through native input");
                            InvokeButton(FindButton(visual, "返回自动适配")); Pump();
                            Check(s.AutoAppearance && checkbox.IsChecked == true && dialog.ActualHeight < 460,
                                "return-to-auto synchronizes checkbox and compacts window");
                            InvokeButton(FindButton(visual, "重新适配")); Pump();
                            Check(s.GlassBlur == 17 && s.GlassOpacity == 29, "recalculate preserves manual settings");
                            InvokeButton(FindButton(visual, "自定义图片")); Pump();
                            Button custom = FindButton(visual, "自定义图片");
                            Check(s.GlassMode == "custom" && AutomationProperties.GetItemStatus(custom) == "已选择"
                                && FindButton(visual, "更换图片…") != null, "cached custom source switches with selection and replacement feedback");
                            SavePng((FrameworkElement)dialog.Content, "custom-appearance-dialog.png");
                            InvokeButton(FindButton(visual, "跟随桌面")); Pump();
                            Check(s.GlassMode == "screen" && !FindButton(visual, "更换图片…").IsVisible,
                                "screen source hides irrelevant replacement action");
                            dialog.Width = 360; dialog.MaxHeight = 340; Pump();
                            InvokeButton(FindButton(visual, "手动调整…")); Pump();
                            ScrollViewer dialogScroll = Find<ScrollViewer>(visual);
                            Check(dialog.ActualHeight <= 340 && dialogScroll.ScrollableHeight > 0 && FindButton(visual, "完成").IsVisible,
                                "small working area scrolls parameters while footer remains accessible");
                            InvokeButton(FindButton(visual, "返回自动适配")); Pump();
                            inspected = true;
                        }
                        catch (Exception ex) { Check(false, "dialog interaction: " + ex.ToString()); }
                        finally { dialog.Close(); }
                        break;
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
        static Button FindButton(DependencyObject parent, string label)
        {
            Button button = parent as Button;
            if (button != null && (button.Content as string) == label) return button;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            { Button found = FindButton(VisualTreeHelper.GetChild(parent, i), label); if (found != null) return found; }
            return null;
        }
        static T FindNamed<T>(DependencyObject parent, string name) where T : DependencyObject
        {
            if (parent is T && AutomationProperties.GetName(parent) == name) return (T)parent;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            { T found = FindNamed<T>(VisualTreeHelper.GetChild(parent, i), name); if (found != null) return found; }
            return null;
        }
        static Button FindNamedButton(DependencyObject parent, string name) { return FindNamed<Button>(parent, name); }
        static void InvokeButton(Button button)
        {
            if (button == null) throw new InvalidOperationException("Missing native button");
            IInvokeProvider invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
            invoke.Invoke(); Pump();
        }
    }
}
