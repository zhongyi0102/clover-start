using System;
using System.Windows;
using System.Windows.Automation;

namespace Clover
{
    /// <summary>
    /// 任务栏几何：从 SystemParameters.WorkArea 判断任务栏在哪条边、多厚，
    /// 并通过 UIAutomation 自动定位原生“开始”按钮的位置（DIP 坐标）。
    /// </summary>
    static class TaskbarGeometry
    {
        public enum Edge { Bottom, Top, Left, Right }

        public static Edge CurrentEdge = Edge.Bottom;
        public static double Thickness = 48;
        public static Rect StartCell = Rect.Empty;

        public static double ScaleFactor()
        {
            try
            {
                uint dpi = Native.GetDpiForSystem();
                if (dpi > 0) return dpi / 96.0;
            }
            catch { }
            return 1.0;
        }

        public static void Refresh()
        {
            try
            {
                double sw = SystemParameters.PrimaryScreenWidth;
                double sh = SystemParameters.PrimaryScreenHeight;
                Rect wa = SystemParameters.WorkArea;

                Edge e = Edge.Bottom;
                double thickness = 48;
                string source = "workArea";

                if (wa.Bottom < sh - 1) { e = Edge.Bottom; thickness = sh - wa.Bottom; }
                else if (wa.Top > 1) { e = Edge.Top; thickness = wa.Top; }
                else if (wa.Left > 1) { e = Edge.Left; thickness = wa.Left; }
                else if (wa.Right < sw - 1) { e = Edge.Right; thickness = sw - wa.Right; }
                else
                {
                    // 工作区等于全屏（任务栏自动隐藏/被隐藏）：用任务栏窗口矩形推断
                    source = "trayWindow";
                    IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
                    Native.RECT r;
                    if (tray != IntPtr.Zero && Native.GetWindowRect(tray, out r))
                    {
                        double s = ScaleFactor();
                        double top = r.Top / s;
                        double bottom = r.Bottom / s;
                        double left = r.Left / s;
                        double right = r.Right / s;
                        double w = right - left;
                        double h = bottom - top;
                        if (h > 0 && h <= 80 && top >= sh - 80) { e = Edge.Bottom; thickness = h; }
                        else if (h > 0 && h <= 80 && bottom <= 80) { e = Edge.Top; thickness = h; }
                        else if (w > 0 && w <= 80 && right <= 80) { e = Edge.Left; thickness = w; }
                        else if (w > 0 && w <= 80 && left >= sw - 80) { e = Edge.Right; thickness = w; }
                        else
                        {
                            // 矩形不合理：保持默认底部估算，并记一条，方便排查任务栏布局变化
                            source = "fallback";
                            Ui.Log("任务栏几何推断失败：矩形 " + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom
                                + " 屏幕 " + sw + "x" + sh + "，回退到底部 48");
                        }
                    }
                    else
                    {
                        source = "fallback";
                        Ui.Log("找不到 Shell_TrayWnd，回退到底部 48");
                    }
                }

                if (thickness < 1) thickness = 48;
                CurrentEdge = e;
                Thickness = thickness;

                switch (e)
                {
                    case Edge.Top:
                        StartCell = new Rect(0, 0, 56, thickness);
                        break;
                    case Edge.Left:
                        StartCell = new Rect(0, 0, thickness, 56);
                        break;
                    case Edge.Right:
                        StartCell = new Rect(sw - thickness, 0, thickness, 56);
                        break;
                    default:
                        StartCell = new Rect(0, sh - thickness, 56, thickness);
                        break;
                }

                // 先用估算值（上面已经算好），再异步尝试用 UIAutomation 精确校正。
                // 关键：界面线程绝不能同步等 UIAutomation——FindFirst 要往目标窗口发消息，
                // 界面线程被占住会导致消息泵停转，反而必然超时。所以这里只发起，不等待。
                ReportUiState(null, source);
                StartUiProbe();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "计算任务栏几何");
            }
        }

        static bool uiProbeRunning;
        static DateTime lastProbe = DateTime.MinValue;
        /// <summary>
        /// 异步探测原生开始按钮的真实矩形。结果回来后在界面线程更新 StartCell。
        /// 同一时刻只允许一个探测在跑；两次探测之间至少间隔 5 秒（任务栏布局不会频繁变）。
        /// </summary>
        static void StartUiProbe()
        {
            try
            {
                if (uiProbeRunning) return;
                if ((DateTime.Now - lastProbe).TotalMilliseconds < 5000) return;
                uiProbeRunning = true;
                lastProbe = DateTime.Now;

                SafeCall.RunAsync<Rect?>(delegate
                {
                    AutomationElement tray = AutomationElement.RootElement.FindFirst(
                        TreeScope.Children,
                        new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
                    if (tray == null) return null;

                    AutomationElement btn = tray.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.NameProperty, "开始"));
                    if (btn == null)
                    {
                        btn = tray.FindFirst(
                            TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.NameProperty, "Start"));
                    }
                    if (btn == null) return null;

                    Rect r = btn.Current.BoundingRectangle;
                    if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return null;
                    return (Rect?)r;
                }, SafeCall.DefaultTimeoutMs, delegate(bool ok, Rect? found)
                {
                    uiProbeRunning = false;
                    try
                    {
                        if (!ok) return;                                   // 探测失败：保留估算值，不刷日志
                        if (found == null || !found.HasValue) return;      // 找不到按钮：同上

                        Rect r = found.Value;
                        double s = ScaleFactor();
                        Rect next = new Rect(r.X / s, r.Y / s, r.Width / s, r.Height / s);
                        if (next.IsEmpty || next.Width <= 0) return;

                        // 位置没明显变化就不动，避免抖动和无谓刷新
                        bool changed = Math.Abs(next.X - StartCell.X) > 1 || Math.Abs(next.Y - StartCell.Y) > 1
                            || Math.Abs(next.Width - StartCell.Width) > 1 || Math.Abs(next.Height - StartCell.Height) > 1;
                        if (!changed) return;

                        StartCell = next;
                        // 记为“已用 uia 精确定位”，Refresh 再报 workArea 时就不会重复刷
                        lastUiState = "ok/uia";
                        Ui.Log("开始按钮定位: ok/uia");
                    }
                    catch (Exception ex)
                    {
                        ErrorLog.Warn("应用开始按钮位置失败: " + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                uiProbeRunning = false;
                Ui.LogError(ex, "启动开始按钮探测");
            }
        }

        static string lastUiState;

        /// <summary>只在状态变化时写一条日志，避免每 2 秒刷屏。</summary>
        static void ReportUiState(string fail, string source)
        {
            string state = fail == null ? ("ok/" + source) : ("fallback/" + source + "/" + fail);
            if (string.Equals(state, lastUiState, StringComparison.Ordinal)) return;
            lastUiState = state;
            if (fail == null) Ui.Log("开始按钮定位: " + state);
            else ErrorLog.Warn("开始按钮定位失败，使用估算位置：" + fail + "（来源 " + source + "）");
        }

        /// <summary>让下一次 Refresh 重新写一条定位日志（例如任务栏重建后）。</summary>
        public static void ResetUiStateLog()
        {
            lastUiState = null;
        }

        /// <summary>“开始”浮动按钮的位置（在开始按钮格子里居中，44x48）。</summary>
        public static Rect StartButtonRect()
        {
            double w = 44;
            double h = 48;
            double x = StartCell.X + (StartCell.Width - w) / 2;
            double y = StartCell.Y + (StartCell.Height - h) / 2;
            return new Rect(x, y, w, h);
        }

        /// <summary>四叶草按钮：沿任务栏方向紧挨“开始”按钮。</summary>
        public static Rect CloverRect()
        {
            Rect s = StartButtonRect();
            switch (CurrentEdge)
            {
                case Edge.Left:
                case Edge.Right:
                    return new Rect(StartCell.X, s.Bottom + 4, s.Width, s.Height);
                default:
                    return new Rect(s.Right + 4, StartCell.Y, s.Width, s.Height);
            }
        }

        /// <summary>迷你任务栏：紧挨四叶草后面。</summary>
        public static Rect MiniBarRect(int count)
        {
            Rect c = CloverRect();
            double w = 44 * Math.Max(0, count);
            switch (CurrentEdge)
            {
                case Edge.Left:
                case Edge.Right:
                    return new Rect(c.X, c.Bottom + 4, w, 48);
                default:
                    return new Rect(c.Right + 4, c.Y, w, 48);
            }
        }
    }
}
