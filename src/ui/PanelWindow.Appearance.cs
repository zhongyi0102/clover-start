using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Clover
{
    partial class PanelWindow
    {
        WallpaperAppearance.Scheme automaticAppearance;
        Border automaticSurface;
        DispatcherTimer appearanceTimer;
        string appearanceSource = "";
        string wallpaperIdentity = "";
        bool wasAutomaticAppearance;
        DateTime lastAppearanceSample;
        byte[] lastAnalyzedBackground;

        public void RecalculateAutomaticAppearance()
        {
            automaticAppearance = null;
            lastAnalyzedBackground = null;
            glassBackdrop.InvalidateSource();
            ApplyGlassSetting();
        }

        int EffectiveGlassOpacity
        { get { return settings == null ? 0 : settings.AutoAppearance ? 90 : settings.GlassOpacity; } }

        public string AutomaticAppearanceDescription
        { get { return settings != null && settings.AutoAppearance ?
            (automaticAppearance == null ? "正在适配背景…" : automaticAppearance.Description) : "手动调整 · 原参数已保留"; } }

        void InitializeAutomaticAppearance(Grid host, Grid content)
        {
            // The old darkness overlay was above the content; keep all backdrop
            // adjustment layers below content, so they cannot dim foreground text.
            host.Children.Remove(darkOverlay);
            host.Children.Insert(host.Children.IndexOf(content), darkOverlay);
            automaticSurface = new Border { IsHitTestVisible = false, Visibility = Visibility.Collapsed,
                CornerRadius = new CornerRadius(Ui.R.Lg), Tag = "automaticAppearanceSurface" };
            host.Children.Insert(host.Children.IndexOf(content), automaticSurface);
            glassBackdrop.OnBackgroundSample = ApplyAutomaticSample;
            glassBackdrop.OnBackgroundUnavailable = delegate { ApplyAutomaticSample(null); };
            appearanceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            appearanceTimer.Tick += delegate
            {
                if (panelClosed || !IsVisible || settings == null || !settings.AutoAppearance) return;
                if (videoBackdrop != null && videoBackdrop.InUse)
                { ApplyAutomaticSample(videoBackdrop.SampleFrame()); return; }
                // File identity changes invalidate capture once; never re-capture just
                // because Windows emitted multiple automatic-accent/theme broadcasts.
                string identity = CurrentWallpaperIdentity();
                if (identity != wallpaperIdentity)
                { wallpaperIdentity = identity; glassBackdrop.InvalidateSource(); glassBackdrop.RefreshSoon(); }
            };
            IsVisibleChanged += delegate
            {
                if (IsVisible && settings != null && settings.AutoAppearance) appearanceTimer.Start();
                else appearanceTimer.Stop();
            };
            Closed += delegate
            {
                appearanceTimer.Stop();
                glassBackdrop.Stop();
                Ui.SetAutomaticPalette(null);
            };
        }

        string CurrentWallpaperIdentity()
        {
            try
            {
                string path = "";
                if (glassBackdrop.UseWeImage) path = glassBackdrop.WeImagePath;
                else if (glassBackdrop.UseCustomImage) path = BackgroundStore.Find(baseDir);
                else if (glassBackdrop.FollowWallpaper)
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                        if (key != null) path = Convert.ToString(key.GetValue("WallPaper")) + "|" +
                            Convert.ToString(key.GetValue("WallpaperStyle"));
                string file = (path ?? "").Split('|')[0];
                return path + "|" + (File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks.ToString() : "missing");
            }
            catch { return "unavailable"; }
        }

        void ApplyAutomaticSample(byte[] pixels)
        {
            if (settings == null || !settings.AutoAppearance) return;
            bool dynamic = videoBackdrop != null && videoBackdrop.InUse;
            if (!dynamic && pixels != null && object.ReferenceEquals(pixels, lastAnalyzedBackground)) return;
            if (dynamic && pixels != null && DateTime.UtcNow - lastAppearanceSample < TimeSpan.FromSeconds(2)) return;
            lastAppearanceSample = DateTime.UtcNow;
            if (dynamic && pixels == null && automaticAppearance != null) return;
            // A fallback surface is not history: first valid image must be allowed
            // to immediately replace it, without a long opacity-release tail.
            WallpaperAppearance.Scheme previous = lastAnalyzedBackground == null ? null : automaticAppearance;
            automaticAppearance = WallpaperAppearance.Analyze(pixels, 64, 40, previous, dynamic);
            lastAnalyzedBackground = pixels;
            Ui.SetAutomaticPalette(automaticAppearance);
            RefreshAppearanceTileOpacity();
            glassBackdrop.BlurRadius = automaticAppearance.Blur;
            glassBackdrop.Brightness = 0;
            glassBackdrop.TintStrength = 0; // semantic surface, no saturated full-panel tint
            glassBackdrop.ContrastGuardEnabled = false; // surface protects every page/state
            if (automaticSurface != null)
            {
                Color color = Color.FromRgb((byte)(automaticAppearance.Surface >> 16),
                    (byte)(automaticAppearance.Surface >> 8), (byte)automaticAppearance.Surface);
                double alpha = pixels == null && !dynamic ? 1 : automaticAppearance.SurfaceAlpha;
                automaticSurface.Background = new SolidColorBrush(Color.FromArgb(
                    (byte)Math.Ceiling(alpha * 255), color.R, color.G, color.B));
                automaticSurface.Visibility = Visibility.Visible;
            }
            if (rootBorder != null) rootBorder.Background = Ui.Bg;
            ApplyShadow();
        }

        void ConfigureAutomaticAppearance()
        {
            bool enabled = settings != null && settings.AutoAppearance;
            glassBackdrop.AutomaticAppearance = enabled;
            if (!enabled)
            {
                if (automaticSurface != null) automaticSurface.Visibility = Visibility.Collapsed;
                if (appearanceTimer != null) appearanceTimer.Stop();
                if (wasAutomaticAppearance)
                {
                    glassBackdrop.InvalidateSource();
                    Ui.SetAutomaticPalette(null);
                    RefreshAppearanceTileOpacity();
                    Ui.ApplyTheme(Ui.ParseTheme(settings.Theme));
                    byte r, g, b;
                    Ui.SetAccentOverride(Settings.TryParseHexColor(settings.GlassAccent, out r, out g, out b)
                        ? (Color?)Color.FromRgb(r, g, b) : null);
                    ApplyShadow();
                }
                wasAutomaticAppearance = false;
                automaticAppearance = null;
                lastAnalyzedBackground = null;
                return;
            }
            string source = settings.GlassMode + "|" + settings.WeWallpaperId + "|" +
                settings.WeWallpaperFile + "|" + (BackgroundStore.Find(baseDir) ?? "");
            if (!wasAutomaticAppearance || source != appearanceSource)
            {
                automaticAppearance = null;
                lastAnalyzedBackground = null;
                appearanceSource = source;
                glassBackdrop.InvalidateSource();
            }
            wasAutomaticAppearance = true;
            wallpaperIdentity = CurrentWallpaperIdentity();
            glassBackdrop.TintStrength = 0;
            glassBackdrop.Brightness = 0;
            glassBackdrop.ContrastGuardEnabled = false;
            glassBackdrop.BlurRadius = automaticAppearance == null ? 6 : automaticAppearance.Blur;
            if (automaticAppearance == null) ApplyAutomaticSample(null);
            if (appearanceTimer != null && IsVisible) appearanceTimer.Start();
        }

        void RefreshAppearanceTileOpacity()
        {
            int count = Math.Min(renderedApps.Count, renderedTiles.Count);
            for (int i = 0; i < count; i++) UpdateTilePresentation(renderedTiles[i], renderedApps[i]);
        }
    }
}
