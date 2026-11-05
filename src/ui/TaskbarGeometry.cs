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

        /// <summary>
        /// 几何值从"估算"变成"精确"时的回调。
        ///
        /// 面板用它重新定位：首次 PositionPanel 只能用估算的 StartCell，
        /// 而 UIA 探测是异步的、完成时面板已经显示出来了。
        /// 不重新定位就会差几十上百像素（实测差 99px）。
        /// </summary>
        public static Action GeometryRefined;
        public static double Thickness = 48;
        public static Rect StartCell = Rect.Empty;

        /// <summary>最近一次几何来源：workArea / trayWindow / fallback。供诊断显示。</summary>
        public static string LastSource = "";

        /// <summary>
        /// 是否已用 UIAutomation 精确校准过开始按钮位置。
        /// false 表示 StartCell 只是估算值（按钮可能偏几个像素），供诊断显示。
        /// </summary>
        public static bool PreciseLocated;

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
                LastSource = source;

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
                ReportUiState(source);
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

                SafeCall.RunWithDeadline<Rect?>(delegate
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
                        PreciseLocated = true;
                        // 记为“已用 uia 精确定位”，Refresh 再报 workArea 时就不会重复刷
                        lastUiState = "ok/uia";
                        Ui.Log("开始按钮定位: ok/uia");

                        // ★ 通知外面：几何值从"估算"变成"精确"了。
                        //
                        // 为什么必须通知：面板的 PositionPanel() 用 CloverRect()
                        // 算位置，而首次调用时 StartCell 还是估算值 (0, sh-48)，
                        // 于是面板被放到 x=54（估算位置）而不是真实按钮旁边 (x=411)。
                        // 实测：按钮在 411，面板跑到 554 —— 差 99px。
                        try
                        {
                            Action cb = GeometryRefined;
                            if (cb != null) cb();
                        }
                        catch { }
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

        /// <summary>
        /// 只在状态变化时写一条日志，避免每 2 秒刷屏。
        ///
        /// 原先签名是 ReportUiState(fail, source)，但**所有调用点都传 fail=null**，
        /// 于是 `else` 分支（写"定位失败"WARN）永远不会执行：
        /// 退到估算位置（workArea / trayWindow / fallback）时记的是 `ok/workArea`，
        /// 日志里看不出"其实没精确定位"。现在按来源判断：
        /// fallback = 推断失败，trayWindow = 有任务栏但没精确校准。
        /// </summary>
        static void ReportUiState(string source)
        {
            bool degraded = string.Equals(source, "fallback", StringComparison.Ordinal);

            string state = degraded ? ("degraded/" + source) : ("ok/" + source);
            if (string.Equals(state, lastUiState, StringComparison.Ordinal)) return;
            lastUiState = state;

            if (degraded)
            {
                ErrorLog.Warn("开始按钮定位降级：任务栏几何推断失败，回退到底部 48（来源 " + source + "）"
                    + "。按钮位置可能不准，这通常是任务栏刚重建或第三方任务栏工具导致的，稍后会自动重试。");
            }
            else
            {
                Ui.Log("开始按钮定位: " + state + (PreciseLocated ? "" : "（尚未精确校准，用估算值）"));
            }
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

        /// <summary>
        /// 迷你任务栏的贴边基准线（设备无关单位）。
        ///
        /// ── 为什么不能写死坐标 ──
        ///
        /// 之前是 `Top = 1152`（= 屏幕高 1200 − 任务栏 48），分辨率、DPI、
        /// 任务栏位置一变就全错。这里按三种情况实时算：
        ///
        ///   1. **系统任务栏可见** → 迷你条底边贴任务栏上沿
        ///      （用 SHAppBarMessage(ABM_GETTASKBARPOS) 取任务栏真实矩形）
        ///   2. **任务栏被本程序隐藏** → 迷你条底边贴工作区最底部
        ///      （此时 WorkArea 等于全屏，直接用屏幕底边）
        ///   3. **任务栏自动隐藏** → 同 2，贴屏幕底部
        ///
        /// 返回值是该贴边方向上的「可用区下边界」：底部任务栏时是任务栏上沿，
        /// 顶部任务栏时是任务栏下沿。
        /// </summary>
        /// <param name="bottomEdgeY">底部贴边的 Y 坐标（迷你条应把底边放在这里）</param>
        /// <returns>成功算出返回 true；失败时调用方用屏幕底部兜底</returns>
        public static bool MiniBarAnchor(out double bottomEdgeY)
        {
            bottomEdgeY = 0;
            try
            {
                double s = ScaleFactor();
                if (s <= 0) s = 1;

                double screenH = SystemParameters.PrimaryScreenHeight;

                // ---- 情况 2/3：任务栏被隐藏 或 自动隐藏 → 贴屏幕底 ----
                bool taskbarHidden = !TaskbarVisibleForGeometry();
                bool autoHide = Native.TaskbarAutoHide();

                if (taskbarHidden || autoHide)
                {
                    bottomEdgeY = screenH;
                    return true;
                }

                // ---- 情况 1：任务栏可见 → 取它的真实矩形 ----
                uint edge;
                Native.RECT rc;
                if (Native.GetTaskbarPos(out edge, out rc))
                {
                    double top = rc.Top / s;
                    double bottom = rc.Bottom / s;
                    double left = rc.Left / s;
                    double right = rc.Right / s;

                    // 只有「贴底部」的任务栏才把迷你条放它上面；
                    // 其它边（上/左/右）由 MiniBarRect 走另一套排布
                    if (edge == 3)
                    {
                        bottomEdgeY = top;   // 任务栏上沿
                        return true;
                    }

                    if (edge == 1)
                    {
                        // 顶部任务栏：可用区从任务栏下沿开始
                        bottomEdgeY = bottom;
                        return true;
                    }

                    // 左/右任务栏：竖直方向不受影响，贴屏幕底
                    bottomEdgeY = screenH;
                    return true;
                }

                // ---- 兜底：用 WorkArea 推 ----
                Rect wa = SystemParameters.WorkArea;
                bottomEdgeY = (wa.Bottom > 0 && wa.Bottom < screenH - 1) ? wa.Bottom : screenH;
                return true;
            }
            catch
            {
                try { bottomEdgeY = SystemParameters.PrimaryScreenHeight; } catch { }
                return false;
            }
        }

        /// <summary>
        /// 任务栏当前是否真的可见（用于几何判断）。
        ///
        /// 不能只看 `settings.HideTaskbar`：任务栏可能被别的程序隐藏，
        /// 或者正在自动隐藏。这里直接问窗口本身。
        /// </summary>
        static bool TaskbarVisibleForGeometry()
        {
            try
            {
                IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero) return false;
                return Native.IsWindowVisible(tray);
            }
            catch { return true; }
        }

        /// <summary>
        /// 迷你任务栏位置。**不再写死坐标**，按任务栏状态自适应。
        ///
        /// ── 三种排布 ──
        ///
        ///   · **底部任务栏**：紧挨四叶草右侧，底边贴任务栏上沿
        ///   · **顶部任务栏**：紧挨四叶草右侧，顶边贴任务栏下沿
        ///   · **左/右任务栏**：竖排，紧挨四叶草下方，底边贴屏幕底
        ///
        /// 任务栏被隐藏时（本程序隐藏或自动隐藏），一律按「贴屏幕底部」处理。
        /// </summary>
        public static Rect MiniBarRect(int count)
        {
            double barH = 48;
            double w = 44 * Math.Max(0, count);
            Rect c = CloverRect();

            double anchor;
            MiniBarAnchor(out anchor);

            switch (CurrentEdge)
            {
                case Edge.Top:
                {
                    // 顶部任务栏：顶边贴任务栏下沿
                    return new Rect(c.Right + 4, anchor, w, barH);
                }
                case Edge.Left:
                case Edge.Right:
                {
                    // 竖任务栏：迷你条在四叶草下方，底边贴可用区底部
                    double top = anchor - barH;
                    return new Rect(c.X, top, w, barH);
                }
                default:
                {
                    // 底部任务栏（或已隐藏）：底边贴锚点
                    double top = anchor - barH;
                    return new Rect(c.Right + 4, top, w, barH);
                }
            }
        }
    }
}
