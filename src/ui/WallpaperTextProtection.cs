using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>Local, non-interactive text backings above the image/video but below UI.
    /// Geometry follows visible content, including scroll clipping, without wrapping controls.</summary>
    sealed class WallpaperTextProtection : FrameworkElement
    {
        readonly FrameworkElement content;
        byte[] pixels;
        WallpaperAppearance.Scheme scheme;
        bool dynamic, queued;
        DrawingGroup drawing;
        string signature = "";
        internal int RegionCount { get; private set; }
        internal double ProtectedArea { get; private set; }

        sealed class Region
        {
            public Rect Bounds;
            public uint Surface;
            public double Alpha;
            public DependencyObject Row;
        }

        public WallpaperTextProtection(FrameworkElement contentRoot)
        {
            content = contentRoot;
            IsHitTestVisible = false; Focusable = false;
            Loaded += delegate { content.LayoutUpdated += OnLayoutUpdated; Refresh(); };
            Unloaded += delegate { content.LayoutUpdated -= OnLayoutUpdated; };
        }

        public void SetBackground(byte[] sample, WallpaperAppearance.Scheme palette, bool isDynamic)
        { pixels = sample; scheme = palette; dynamic = isDynamic; Refresh(); }

        void OnLayoutUpdated(object sender, EventArgs args) { Refresh(); }

        public void Refresh()
        {
            if (queued || !IsVisible) return;
            queued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            { queued = false; if (IsVisible) Rebuild(); }));
        }

        internal void Rebuild()
        {
            if (scheme == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            List<Region> regions = new List<Region>();
            Collect(content, new Rect(0, 0, ActualWidth, ActualHeight), regions);
            MergeRowRegions(regions);
            System.Text.StringBuilder key = new System.Text.StringBuilder();
            foreach (var region in regions)
                key.Append(region.Bounds.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append('|').Append(region.Surface).Append('|').Append(region.Alpha.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            string next = key.ToString();
            if (next == signature && drawing != null) return;
            signature = next; RegionCount = regions.Count; ProtectedArea = 0;
            DrawingGroup group = new DrawingGroup();
            using (DrawingContext dc = group.Open())
                foreach (var region in regions)
                {
                    Rect rect = region.Bounds; uint surface = region.Surface;
                    SolidColorBrush brush = new SolidColorBrush(Color.FromArgb((byte)Math.Ceiling(region.Alpha * 255),
                        (byte)(surface >> 16), (byte)(surface >> 8), (byte)surface));
                    brush.Freeze(); dc.DrawRoundedRectangle(brush, null, rect, 3, 3);
                    ProtectedArea += rect.Width * rect.Height;
                }
            group.Freeze(); drawing = group; InvalidateVisual();
        }

        static void MergeRowRegions(List<Region> regions)
        {
            // 只合并同一父容器、同一行且间隔很小的文字；不能跨图标、控件或列表行铺底。
            // 先按父容器分桶，长文件列表只比较每行自己的几个标签。
            Dictionary<DependencyObject, List<Region>> byRow = new Dictionary<DependencyObject, List<Region>>();
            List<List<Region>> rows = new List<List<Region>>();
            foreach (Region region in regions)
            {
                List<Region> group;
                if (region.Row == null)
                {
                    rows.Add(new List<Region> { region });
                    continue;
                }
                if (!byRow.TryGetValue(region.Row, out group))
                {
                    group = new List<Region>(); byRow[region.Row] = group; rows.Add(group);
                }
                group.Add(region);
            }
            regions.Clear();
            foreach (List<Region> group in rows)
            {
                for (int i = 0; i < group.Count; i++)
                {
                    Region row = group[i];
                    bool merged;
                    do
                    {
                        merged = false;
                        for (int j = i + 1; j < group.Count; j++)
                        {
                            Region next = group[j];
                            if (row.Surface != next.Surface || Math.Abs(row.Bounds.Top - next.Bounds.Top) > 2
                                || Math.Abs(row.Bounds.Bottom - next.Bounds.Bottom) > 2) continue;
                            double gap = Math.Max(next.Bounds.Left - row.Bounds.Right,
                                row.Bounds.Left - next.Bounds.Right);
                            if (gap > Ui.S.Sm) continue;
                            Rect union = Rect.Union(row.Bounds, next.Bounds);
                            if (union.Width * union.Height > (row.Bounds.Width * row.Bounds.Height
                                + next.Bounds.Width * next.Bounds.Height) * 1.18) continue;
                            row.Bounds = union;
                            row.Alpha = Math.Max(row.Alpha, next.Alpha);
                            group.RemoveAt(j); merged = true; break;
                        }
                    } while (merged);
                }
                regions.AddRange(group);
            }
        }

        void Collect(DependencyObject node, Rect clip, List<Region> regions)
        {
            FrameworkElement element = node as FrameworkElement;
            if (element != null)
            {
                if (!element.IsVisible || element.Opacity <= 0 || element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
                Rect bounds;
                try { bounds = element.TransformToVisual(this).TransformBounds(new Rect(element.RenderSize)); }
                catch { return; }
                if (element.ClipToBounds || element is ScrollContentPresenter)
                { clip.Intersect(bounds); if (clip.IsEmpty) return; }
                TextBlock label = element as TextBlock;
                TextBox input = element as TextBox;
                SolidColorBrush foreground = label != null ? label.Foreground as SolidColorBrush : input != null ? input.Foreground as SolidColorBrush : null;
                // Icon fonts are not text; preserve application and glyph colors unchanged.
                bool isText = label != null && !string.IsNullOrWhiteSpace(label.Text) && label.FontFamily != Ui.Mdl;
                if ((isText || input != null) && foreground != null)
                {
                    if (label != null && !double.IsNaN(label.DesiredSize.Width))
                    {
                        double textWidth = Math.Min(bounds.Width, label.DesiredSize.Width);
                        if (label.TextAlignment == TextAlignment.Center) bounds.X += (bounds.Width - textWidth) / 2;
                        else if (label.TextAlignment == TextAlignment.Right) bounds.X += bounds.Width - textWidth;
                        bounds.Width = textWidth;
                    }
                    Rect visible = bounds; visible.Intersect(clip);
                    if (!visible.IsEmpty && visible.Width > 0 && visible.Height > 0)
                    {
                        bounds.Inflate(2, 1); bounds.Intersect(clip);
                        uint fg = (uint)(foreground.Color.R << 16 | foreground.Color.G << 8 | foreground.Color.B);
                        // Search, menu and selection already have semantic opaque
                        // surfaces. Do not put an unrelated backing beneath them.
                        if (HasReadableSurface(element, fg)) return;
                        uint surface = scheme.Surface;
                        if (WallpaperAppearance.Contrast(fg, surface) < 5.2)
                            surface = WallpaperAppearance.Contrast(fg, 0) > WallpaperAppearance.Contrast(fg, 0xFFFFFF) ? 0u : 0xFFFFFFu;
                        int left = (int)Math.Floor(bounds.Left / ActualWidth * 64) - 1;
                        int top = (int)Math.Floor(bounds.Top / ActualHeight * 40) - 1;
                        int right = (int)Math.Ceiling(bounds.Right / ActualWidth * 64) + 1;
                        int bottom = (int)Math.Ceiling(bounds.Bottom / ActualHeight * 40) + 1;
                        double alpha = WallpaperAppearance.TextProtectionAlpha(pixels, 64, 40,
                            left, top, right, bottom, fg, surface, scheme.Surface, scheme.SurfaceAlpha, dynamic);
                        // 相邻标签采用统一强度档位；向上取整，不牺牲最低文字对比度。
                        alpha = Math.Min(1, Math.Ceiling(alpha * 20) / 20);
                        if (alpha > 0) regions.Add(new Region { Bounds = bounds, Surface = surface, Alpha = alpha,
                            Row = label == null ? null : VisualTreeHelper.GetParent(element) });
                    }
                    // TextBox template children would duplicate its protection.
                    return;
                }
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Collect(VisualTreeHelper.GetChild(node, i), clip, regions);
        }

        bool HasReadableSurface(FrameworkElement element, uint foreground)
        {
            DependencyObject node = element;
            while (node != null)
            {
                Border border = node as Border;
                Panel panel = node as Panel;
                Control control = node as Control;
                SolidColorBrush brush = (border != null ? border.Background : panel != null ? panel.Background :
                    control != null ? control.Background : null) as SolidColorBrush;
                if (brush != null && brush.Color.A == 255)
                {
                    uint color = (uint)(brush.Color.R << 16 | brush.Color.G << 8 | brush.Color.B);
                    return WallpaperAppearance.Contrast(foreground, color) >= 4.5;
                }
                if (node == content) break;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        protected override void OnRender(DrawingContext dc)
        { base.OnRender(dc); if (drawing != null) dc.DrawDrawing(drawing); }
    }
}
