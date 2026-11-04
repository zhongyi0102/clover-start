using System;

namespace Clover
{
    /// <summary>
    /// 虚拟桌面拖动手势测试。
    ///
    /// 精确覆盖回归：图标视觉跟随指针移动后，若仍用“指针是否离开移动后的格子”
    /// 判定拖出，格子会追着指针，系统拖放几乎永远启动不了。
    /// </summary>
    static class DesktopDragGestureTest
    {
        static int passed;
        static int failed;

        static void Check(bool cond, string name, string detail = "")
        {
            if (cond) { passed++; Console.WriteLine("PASS  " + name); }
            else
            {
                failed++;
                Console.WriteLine("FAIL  " + name
                    + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
            }
        }

        static void Main()
        {
            Console.WriteLine("=== 虚拟桌面拖动手势测试 ===");
            Console.WriteLine();

            // 画布内移动：只做自由摆放，不启动系统拖放
            Check(!DesktopDragGesture.ShouldStartSystemDrag(
                    140, 140, 300, 300, 1000, 700, 4, 4, 8),
                "★画布内部移动保持自由摆放");

            // 未超过系统最小拖动距离：即使靠近边缘也不能误触发
            Check(!DesktopDragGesture.ShouldStartSystemDrag(
                    7, 100, 5, 101, 1000, 700, 4, 4, 8),
                "★未过系统阈值不触发");

            // 左右上下四条退出带
            Check(DesktopDragGesture.ShouldStartSystemDrag(
                    140, 140, 7, 200, 1000, 700, 4, 4, 8),
                "★到左边缘立即切系统拖放");
            Check(DesktopDragGesture.ShouldStartSystemDrag(
                    140, 140, 993, 200, 1000, 700, 4, 4, 8),
                "★到右边缘立即切系统拖放");
            Check(DesktopDragGesture.ShouldStartSystemDrag(
                    140, 140, 200, 7, 1000, 700, 4, 4, 8),
                "★到上边缘立即切系统拖放");
            Check(DesktopDragGesture.ShouldStartSystemDrag(
                    140, 140, 200, 693, 1000, 700, 4, 4, 8),
                "★到下边缘立即切系统拖放");

            // 指针已经越过画布，也必须触发
            Check(DesktopDragGesture.ShouldStartSystemDrag(
                    140, 140, 1010, 200, 1000, 700, 4, 4, 8),
                "★越过画布仍触发");

            // 本次 bug 的最小复现：
            // 原图标 x=100，按下点相对图标偏移 30；指针走到 995 时，
            // 图标也平移到 965，所以旧算法仍判指针在 [965,1061] 内。
            double pointerX = 995;
            double translatedCellX = 965;
            bool oldOutsideCell = pointerX < translatedCellX
                || pointerX > translatedCellX + 96;
            Check(!oldOutsideCell,
                "★复现旧算法：移动后的格子追着指针，判定仍在格子内");
            Check(DesktopDragGesture.ShouldStartSystemDrag(
                    130, 140, pointerX, 200, 1000, 700, 4, 4, 8),
                "★新算法不看移动后的格子，到画布边缘就触发");

            // 坏尺寸必须闭合失败
            Check(!DesktopDragGesture.ShouldStartSystemDrag(
                    10, 10, 20, 20, 0, 700, 4, 4, 8),
                "★无效画布尺寸不触发");

            Console.WriteLine();
            Console.WriteLine("--- B 方案：离开格子半格 / 按住超时 ---");

            // 格子 96x96，半格 = 48
            Check(!DesktopDragGesture.HasLeftOriginCell(
                    100, 100, 140, 100, 96, 96, DesktopDragGesture.LeaveCellRatio),
                "★格内移动 40px 不触发");
            Check(DesktopDragGesture.HasLeftOriginCell(
                    100, 100, 152, 100, 96, 96, DesktopDragGesture.LeaveCellRatio),
                "★离开半格（52px）触发");
            Check(DesktopDragGesture.HasLeftOriginCell(
                    100, 100, 100, 152, 96, 96, DesktopDragGesture.LeaveCellRatio),
                "★纵向离开半格也触发");
            Check(!DesktopDragGesture.HasLeftOriginCell(
                    100, 100, 100, 100, 0, 0, DesktopDragGesture.LeaveCellRatio),
                "★无效格子尺寸不触发");

            DateTime press = new DateTime(2026, 11, 2, 9, 0, 0);
            Check(!DesktopDragGesture.HasHeldLongEnough(press, press.AddMilliseconds(200),
                    DesktopDragGesture.HoldSeconds),
                "★按住 0.2 秒不触发");
            Check(DesktopDragGesture.HasHeldLongEnough(press, press.AddMilliseconds(350),
                    DesktopDragGesture.HoldSeconds),
                "★按住 0.35 秒触发");
            Check(!DesktopDragGesture.HasHeldLongEnough(press, press.AddSeconds(5), 0),
                "★阈值为 0 时不触发");

            Console.WriteLine();
            Console.WriteLine("--- 新判据：指针离开面板窗口才转系统拖放 ---");

            // 窗口矩形 (100,100)-(900,700)
            // 面板内移动：任意位置都不该转系统拖放（这是修「拖不动」的核心）
            Check(!DesktopDragGesture.HasLeftWindow(500, 400, 100, 100, 900, 700),
                "★窗口正中间 → 不转");
            Check(!DesktopDragGesture.HasLeftWindow(101, 101, 100, 100, 900, 700),
                "★贴着左上角但在窗口内 → 不转");
            Check(!DesktopDragGesture.HasLeftWindow(899, 699, 100, 100, 900, 700),
                "★贴着右下角但在窗口内 → 不转");
            Check(!DesktopDragGesture.HasLeftWindow(100, 400, 100, 100, 900, 700),
                "★正好在左边界上 → 不转（边界算内）");
            Check(!DesktopDragGesture.HasLeftWindow(900, 400, 100, 100, 900, 700),
                "★正好在右边界上 → 不转");

            // 真的离开窗口才转
            Check(DesktopDragGesture.HasLeftWindow(99, 400, 100, 100, 900, 700),
                "★左移出窗口 → 转系统拖放");
            Check(DesktopDragGesture.HasLeftWindow(901, 400, 100, 100, 900, 700),
                "★右移出窗口 → 转系统拖放");
            Check(DesktopDragGesture.HasLeftWindow(500, 99, 100, 100, 900, 700),
                "★上移出窗口 → 转系统拖放");
            Check(DesktopDragGesture.HasLeftWindow(500, 701, 100, 100, 900, 700),
                "★下移出窗口 → 转系统拖放");

            // 边界与异常输入
            Check(!DesktopDragGesture.HasLeftWindow(500, 400, 900, 700, 100, 100),
                "★窗口矩形反了（未布局）→ 不转");
            Check(!DesktopDragGesture.HasLeftWindow(500, 400, 100, 100, 100, 700),
                "★零宽窗口 → 不转");
            Check(!DesktopDragGesture.HasLeftWindow(double.NaN, 400, 100, 100, 900, 700),
                "★NaN 坐标 → 不转");
            Check(!DesktopDragGesture.HasLeftWindow(500, 400, double.NaN, 100, 900, 700),
                "★NaN 窗口坐标 → 不转");

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
