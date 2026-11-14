using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Clover
{
    partial class PanelWindow
    {
        bool tileReflowQueued;
        Border tileReflowAnchor;
        string tileReflowCategory;
        double tileReflowRelativeY, tileReflowOffset = -1;

        // Called before the tiles are rearranged. Keep the top visible application's
        // position instead of retaining a pixel offset that now points at another row.
        void PreserveTileReflowAnchor()
        {
            if (tileReflowQueued || panelClosed || !IsVisible || tilePanel == null
                || tilePanel.Visibility != Visibility.Visible || contentScroll.VerticalOffset <= 0
                || tileRenderPending) return;
            Border anchor = null;
            double relativeY = 0;
            if (tileReflowAnchor != null && tileReflowCategory == current
                && renderedTiles.Contains(tileReflowAnchor)
                && Math.Abs(contentScroll.VerticalOffset - tileReflowOffset) < 0.5)
            {
                // 连续缩放沿用同一个阅读项目，不因它落到新行的中间而换成行首项目。
                anchor = tileReflowAnchor;
                relativeY = tileReflowRelativeY;
            }
            else foreach (Border tile in renderedTiles)
            {
                if (!tile.IsVisible || tile.ActualHeight <= 0) continue;
                double y = tile.TranslatePoint(new Point(0, 0), tilePanel).Y - contentScroll.VerticalOffset;
                if (y + tile.ActualHeight <= 0) continue;
                anchor = tile; relativeY = y; break;
            }
            if (anchor == null) return;
            StopSmoothScroll(false);
            CancelTileDrag(); // Drag geometry belongs to the old layout.
            tileReflowQueued = true;
            string category = current;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                tileReflowQueued = false;
                if (panelClosed || category != current || !renderedTiles.Contains(anchor)) return;
                double y = anchor.TranslatePoint(new Point(0, 0), tilePanel).Y;
                suppressScrollRecord = true;
                try
                {
                    contentScroll.ScrollToVerticalOffset(Math.Max(0, y - relativeY));
                    contentScroll.UpdateLayout();
                }
                finally { suppressScrollRecord = false; }
                scrollOffsets[current] = contentScroll.VerticalOffset;
                tileReflowAnchor = anchor;
                tileReflowCategory = current;
                tileReflowRelativeY = relativeY;
                tileReflowOffset = contentScroll.VerticalOffset;
                ScheduleViewportIconRefresh();
                if (automaticTextProtection != null) automaticTextProtection.Refresh();
            }));
        }
    }
}
