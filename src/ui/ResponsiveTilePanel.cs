using System;
using System.Windows;
using System.Windows.Controls;

namespace Clover
{
    /// <summary>Fixed-size application tiles, evenly spaced columns and full-width section rows.</summary>
    sealed class ResponsiveTilePanel : WrapPanel
    {
        internal Action BeforeReflow;
        double measuredWidth;
        const double CellWidth = PanelWindow.TileWidthPx + 4;

        static bool IsTile(UIElement child)
        {
            FrameworkElement element = child as FrameworkElement;
            return element != null && element.Width == PanelWindow.TileWidthPx
                && element.Height == PanelWindow.TileHeightPx;
        }

        protected override Size MeasureOverride(Size available)
        {
            double width = double.IsInfinity(available.Width) ? CellWidth : Math.Max(CellWidth, available.Width);
            if (measuredWidth > 0 && Math.Abs(width - measuredWidth) > 0.5 && BeforeReflow != null)
                BeforeReflow();
            measuredWidth = width;
            int columns = Math.Max(1, (int)Math.Floor(width / CellWidth));
            int column = 0;
            double height = 0, rowHeight = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child.Visibility == Visibility.Collapsed) continue;
                bool tile = IsTile(child);
                if (!tile && column > 0)
                { height += rowHeight; column = 0; rowHeight = 0; }
                child.Measure(new Size(tile ? CellWidth : width, double.PositiveInfinity));
                if (!tile) { height += child.DesiredSize.Height; continue; }
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                if (++column == columns)
                { height += rowHeight; column = 0; rowHeight = 0; }
            }
            return new Size(width, height + rowHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int columns = Math.Max(1, (int)Math.Floor(finalSize.Width / CellWidth));
            double pitch = finalSize.Width / columns;
            int column = 0;
            double y = 0, rowHeight = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child.Visibility == Visibility.Collapsed) continue;
                if (!IsTile(child))
                {
                    if (column > 0) { y += rowHeight; column = 0; rowHeight = 0; }
                    child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
                    y += child.DesiredSize.Height;
                    continue;
                }
                child.Arrange(new Rect(column * pitch + (pitch - CellWidth) / 2, y,
                    CellWidth, child.DesiredSize.Height));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                if (++column == columns) { y += rowHeight; column = 0; rowHeight = 0; }
            }
            return finalSize;
        }
    }
}
