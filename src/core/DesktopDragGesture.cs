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
        /// 是否应从面板内自由拖动切换到系统拖放（**旧版判据，已不用于拖动流程**）。
        ///
        /// 判据是「指针到了画布边缘带」。这个方案的问题是：
        /// 画布边缘仍然在面板内，用户想挪图标到角落就会被系统拖放接管。
        /// 现在拖动流程改用 <see cref="HasLeftWindow"/>（指针真的离开面板窗口）。
        ///
        /// 保留这个函数是因为它有测试覆盖，且将来若要做「拖到画布边缘
        /// 也允许拖出」还能用上。
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

        /// <summary>
        /// 指针是否已经离开面板窗口的矩形范围。
        ///
        /// ── 为什么用「离开窗口」而不是「离开原格子」──
        ///
        /// 旧实现（B 方案）是「离开按下时的格子超过半格」就转系统拖放，
        /// 结果面板内根本没法自由摆放：想挪一个图标到旁边，一动就被
        /// 系统拖放接管了。
        ///
        /// 改成「指针真的离开面板窗口」才转系统拖放：
        ///   · 面板内拖动 → 自由摆放（图标跟着指针走，松手停在新位置）
        ///   · 拖到面板外 → 转系统拖放（拖到桌面 / 资源管理器）
        ///
        /// 这与 Windows 桌面本身的行为一致：桌面上拖动图标是自由摆放，
        /// 拖到另一个窗口才变成文件拖放。
        /// </summary>
        /// <param name="pointerX">指针的屏幕坐标 X</param>
        /// <param name="pointerY">指针的屏幕坐标 Y</param>
        /// <param name="left">面板窗口左边（屏幕坐标）</param>
        /// <param name="top">面板窗口上边（屏幕坐标）</param>
        /// <param name="right">面板窗口右边（屏幕坐标）</param>
        /// <param name="bottom">面板窗口下边（屏幕坐标）</param>
        public static bool HasLeftWindow(
            double pointerX, double pointerY,
            double left, double top, double right, double bottom)
        {
            try
            {
                if (double.IsNaN(pointerX) || double.IsNaN(pointerY)) return false;
                if (double.IsNaN(left) || double.IsNaN(top)) return false;
                if (double.IsNaN(right) || double.IsNaN(bottom)) return false;
                if (right <= left || bottom <= top) return false;   // 窗口还没布局好

                return pointerX < left || pointerX > right
                    || pointerY < top || pointerY > bottom;
            }
            catch { return false; }
        }
    }
}
