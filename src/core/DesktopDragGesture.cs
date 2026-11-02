using System;

namespace Clover
{
    /// <summary>
    /// 虚拟桌面的拖动手势判定（纯逻辑、无 WPF 依赖）。
    ///
    /// 面板内允许自由摆放，因此“移动超过几像素”本身不能直接启动系统拖放；
    /// 用户把指针拖到内容画布的退出带时，才从内部摆放无缝切到 OLE 拖放。
    /// </summary>
    public static class DesktopDragGesture
    {
        /// <summary>内部虚拟桌面拖出使用的数据格式标记。</summary>
        public const string InternalDataFormat = "CloverStart.VirtualDesktopDrag";

        /// <summary>
        /// 离开图标格子「半格」的判定比例：0.5 = 半格。
        /// 调大更不容易误触拖出，调小拖出更灵敏。
        /// </summary>
        public const double LeaveCellRatio = 0.5;

        /// <summary>
        /// 按住多久后仍未松手就认定想拖出（秒）。
        /// 调大更保守，调小更灵敏。
        /// </summary>
        public const double HoldSeconds = 0.3;

        /// <summary>
        /// 指针是否已离开「按下时的格子」超过半格。
        ///
        /// 用**按下时的原始格子**判断，不用图标当前位置 ——
        /// 图标会跟着指针走，用当前位置永远判不出来（这就是旧 bug）。
        /// </summary>
        public static bool HasLeftOriginCell(
            double startX, double startY,
            double currentX, double currentY,
            double cellWidth, double cellHeight,
            double ratio)
        {
            try
            {
                if (cellWidth <= 0 || cellHeight <= 0) return false;
                if (double.IsNaN(startX) || double.IsNaN(currentX)) return false;

                double limitX = cellWidth * Math.Max(0.05, ratio);
                double limitY = cellHeight * Math.Max(0.05, ratio);

                return Math.Abs(currentX - startX) > limitX
                    || Math.Abs(currentY - startY) > limitY;
            }
            catch { return false; }
        }

        /// <summary>按下至今是否已超过 HoldSeconds。</summary>
        public static bool HasHeldLongEnough(DateTime pressTime, DateTime now, double seconds)
        {
            try
            {
                if (seconds <= 0) return false;
                return (now - pressTime).TotalSeconds >= seconds;
            }
            catch { return false; }
        }

        /// <summary>
        /// 是否应从面板内自由拖动切换到系统拖放。
        /// </summary>
        public static bool ShouldStartSystemDrag(
            double startX, double startY,
            double currentX, double currentY,
            double canvasWidth, double canvasHeight,
            double minHorizontalDistance, double minVerticalDistance,
            double edgeBand)
        {
            try
            {
                if (canvasWidth <= 0 || canvasHeight <= 0) return false;
                if (double.IsNaN(startX) || double.IsNaN(startY)
                    || double.IsNaN(currentX) || double.IsNaN(currentY)) return false;

                double dx = Math.Abs(currentX - startX);
                double dy = Math.Abs(currentY - startY);
                if (dx < Math.Max(0, minHorizontalDistance)
                    && dy < Math.Max(0, minVerticalDistance)) return false;

                double band = Math.Max(0, edgeBand);
                return currentX <= band
                    || currentY <= band
                    || currentX >= canvasWidth - band
                    || currentY >= canvasHeight - band;
            }
            catch { return false; }
        }
    }
}
