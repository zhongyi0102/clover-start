using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 全程序唯一的任务栏布局服务。
    /// 一份快照同时保存物理像素与逻辑坐标；一次 UIAutomation 探测同时取得
    /// 开始按钮和应用按钮组，避免多个窗口并发访问 Explorer。
    /// </summary>
    static class TaskbarGeometry
    {
        public enum Edge { Bottom, Top, Left, Right }

        public sealed class Snapshot
        {
            public long Generation;
            public Edge Edge;
            public Native.RECT TaskbarPx;
            public Native.RECT MonitorPx;
            public Native.RECT WorkPx;
            public Native.RECT StartCellPx;
            public Native.RECT CloverPx;
            public int Dpi = 96;
            public bool StartLocated;
            public bool TaskButtonsLocated;
            public bool TaskbarVisible;
            public bool AutoHide;
            public string Source = "fallback";
            public DateTime CalibratedAt = DateTime.MinValue;

            public double Scale { get { return Dpi > 0 ? Dpi / 96.0 : 1.0; } }
            public Rect TaskbarDip { get { return ToLocalDip(TaskbarPx, Scale); } }
            public Rect MonitorDip { get { return ToLocalDip(MonitorPx, Scale); } }
            public Rect WorkDip { get { return ToLocalDip(WorkPx, Scale); } }
            public Rect StartCellDip { get { return ToLocalDip(StartCellPx, Scale); } }
            public Rect CloverDip { get { return ToLocalDip(CloverPx, Scale); } }

            public Snapshot Clone() { return (Snapshot)MemberwiseClone(); }
        }

        sealed class ProbeResult
        {
            public Rect Tray;
            public Rect Start;
            public bool HasStart;
            public double HorizontalTaskEnd = double.MinValue;
            public double VerticalTaskEnd = double.MinValue;
            public bool HasTaskButtons;
        }

        public const int ProbeRetryLimit = 2;
        public const int ProbeIntervalMs = 5000;
        static readonly TaskbarProbeCoordinator probe =
            new TaskbarProbeCoordinator(ProbeRetryLimit);
        static readonly object sync = new object();
        static Snapshot current = BuildEmergencySnapshot();
        static DateTime lastProbeStarted = DateTime.MinValue;
        static DispatcherTimer retryTimer;
        static string lastUiState;

        public static event Action LayoutChanged;
        public static Action GeometryRefined;

        public static Snapshot Current
        {
            get { lock (sync) return current.Clone(); }
        }

        // 兼容既有调用点；所有值都来自同一快照。
        public static Edge CurrentEdge { get { return Current.Edge; } }
        public static Rect StartCell { get { return Current.StartCellDip; } }
        public static string LastSource { get { return Current.Source; } }
        public static bool PreciseLocated { get { return HasPreciseCalibration(Current); } }
        public static DateTime LastCalibrated { get { return Current.CalibratedAt; } }
        public static double Thickness
        {
            get
            {
                Snapshot s = Current;
                return (s.Edge == Edge.Left || s.Edge == Edge.Right)
                    ? Width(s.TaskbarPx) / s.Scale : Height(s.TaskbarPx) / s.Scale;
            }
        }

        public static double ScaleFactor() { return Current.Scale; }

        /// <summary>诊断界面使用的稳定中文名称；兼容旧版来源键。</summary>
        public static string SourceDisplayName(string source)
        {
            switch (source ?? "")
            {
                case "uia/task-buttons": return "UIAutomation 应用按钮组";
                case "uia/start": return "UIAutomation 开始按钮";
                case "appbar": return "AppBar 原生接口";
                case "trayWindow": return "任务栏窗口矩形";
                case "workArea": return "工作区推断（旧版）";
                case "fallback": return "主屏底部回退估算（降级）";
                default: return string.IsNullOrEmpty(source) ? "未知" : source;
            }
        }

        internal static bool HasPreciseCalibration(Snapshot snapshot)
        {
            return snapshot != null && (snapshot.StartLocated || snapshot.TaskButtonsLocated);
        }

        public static void Refresh() { Refresh(false); }

        /// <summary>刷新廉价原生几何，并按节流规则请求一次统一 UIA 校准。</summary>
        public static void Refresh(bool forceProbe)
        {
            try
            {
                Snapshot previous = Current;
                Snapshot native = BuildNativeSnapshot();

                // 新一代精确结果返回前保留旧精确坐标，避免 Shell 事件风暴期间跳回估算位。
                if ((previous.StartLocated || previous.TaskButtonsLocated)
                    && SameMonitor(previous.MonitorPx, native.MonitorPx)
                    && previous.Edge == native.Edge)
                {
                    native.StartCellPx = previous.StartCellPx;
                    native.CloverPx = RebuildClover(previous.CloverPx, native);
                    native.StartLocated = previous.StartLocated;
                    native.TaskButtonsLocated = previous.TaskButtonsLocated;
                    native.CalibratedAt = previous.CalibratedAt;
                    if (native.Source != "fallback") native.Source = previous.Source;
                }

                Commit(native, false);
                ReportUiState(native);
                RequestProbe(forceProbe);
            }
            catch (Exception ex) { Ui.LogError(ex, "计算任务栏几何"); }
        }

        public static void NotifyLayoutChanged() { Refresh(true); }

        /// <summary>Explorer 重建后旧 HWND/坐标均无效，立即回到安全原生值。</summary>
        public static void Reset()
        {
            lock (sync)
            {
                probe.Reset();
                lastProbeStarted = DateTime.MinValue;
                current = BuildNativeSnapshot();
            }
            try { if (retryTimer != null) retryTimer.Stop(); } catch { }
            lastUiState = null;
            RaiseChanged(false);
        }

        public static void ResetUiStateLog() { lastUiState = null; }

        static void RequestProbe(bool force)
        {
            if (!force && (DateTime.Now - lastProbeStarted).TotalMilliseconds < ProbeIntervalMs)
                return;

            lock (sync) probe.NotifyChange();
            StartPendingProbe();
        }

        static void StartPendingProbe()
        {
            long generation;
            lock (sync)
            {
                if (!probe.TryStart(out generation)) return;
                lastProbeStarted = DateTime.Now;
            }

            SafeCall.RunWithDeadlineQuiet<ProbeResult>(ProbeTaskbar,
                SafeCall.DefaultTimeoutMs, delegate(bool completed, ProbeResult found)
            {
                Dispatcher dispatcher = null;
                try { dispatcher = Application.Current == null ? null : Application.Current.Dispatcher; }
                catch { }

                Action finish = delegate { CompleteProbe(generation, completed ? found : null); };
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    try { dispatcher.BeginInvoke(finish); }
                    catch { CompleteProbe(generation, null); }
                }
                else finish();
            });
        }

        static ProbeResult ProbeTaskbar()
        {
            AutomationElement tray = AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
            if (tray == null) return null;

            Rect trayRect = tray.Current.BoundingRectangle;
            if (!Valid(trayRect)) return null;

            ProbeResult result = new ProbeResult();
            result.Tray = trayRect;

            AutomationElement start = tray.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "开始"));
            if (start == null)
                start = tray.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "Start"));
            if (start != null)
            {
                Rect r = start.Current.BoundingRectangle;
                if (Valid(r)) { result.Start = r; result.HasStart = true; }
            }

            AutomationElementCollection buttons = tray.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ClassNameProperty,
                    "Taskbar.TaskListButtonAutomationPeer"));
            foreach (AutomationElement button in buttons)
            {
                try
                {
                    Rect r = button.Current.BoundingRectangle;
                    if (!Valid(r)) continue;
                    result.HorizontalTaskEnd = Math.Max(result.HorizontalTaskEnd, r.Right);
                    result.VerticalTaskEnd = Math.Max(result.VerticalTaskEnd, r.Bottom);
                    result.HasTaskButtons = true;
                }
                catch { }
            }
            return result;
        }

        static void CompleteProbe(long generation, ProbeResult found)
        {
            bool valid = found != null && Valid(found.Tray)
                && (found.HasStart || found.HasTaskButtons);
            TaskbarProbeCoordinator.Completion completion;
            lock (sync) completion = probe.Complete(generation, valid);

            if (completion.Apply)
            {
                Snapshot next = Current;
                next.Generation = generation;
                Native.RECT tray = FromRect(found.Tray);
                if (Width(tray) > 0 && Height(tray) > 0) next.TaskbarPx = tray;
                RefreshMonitorAndDpi(next);
                next.Edge = InferEdge(next.TaskbarPx, next.MonitorPx);
                next.WorkPx = EffectiveWorkArea(next);

                if (found.HasStart) next.StartCellPx = FromRect(found.Start);
                next.StartLocated = found.HasStart;
                next.TaskButtonsLocated = found.HasTaskButtons;
                next.CalibratedAt = DateTime.Now;
                next.Source = found.HasTaskButtons ? "uia/task-buttons" : "uia/start";
                double taskEnd = double.NaN;
                if (found.HasTaskButtons)
                    taskEnd = (next.Edge == Edge.Left || next.Edge == Edge.Right)
                        ? found.VerticalTaskEnd : found.HorizontalTaskEnd;
                next.CloverPx = BuildCloverRect(next, taskEnd);
                Commit(next, true);
                lastUiState = "ok/" + next.Source;
                Ui.Log("任务栏布局定位: " + lastUiState + "，DPI " + next.Dpi
                    + "，代次 " + generation);
            }

            if (completion.Rerun)
            {
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.Background, new Action(StartPendingProbe));
            }
            else if (completion.Retry) ScheduleRetry(completion.RetryAttempt);
        }

        static void ScheduleRetry(int attempt)
        {
            if (ErrorLog.ShuttingDown || attempt <= 0 || attempt > ProbeRetryLimit) return;
            if (retryTimer == null)
            {
                retryTimer = new DispatcherTimer(DispatcherPriority.Background);
                retryTimer.Tick += delegate
                {
                    retryTimer.Stop();
                    if (!ErrorLog.ShuttingDown) StartPendingProbe();
                };
            }
            retryTimer.Stop();
            retryTimer.Interval = TimeSpan.FromMilliseconds(attempt == 1 ? 1000 : 3000);
            retryTimer.Start();
        }

        static void Commit(Snapshot next, bool refined)
        {
            bool changed;
            lock (sync)
            {
                changed = !Equivalent(current, next);
                current = next.Clone();
            }
            if (changed || refined) RaiseChanged(refined);
        }

        static bool Equivalent(Snapshot a, Snapshot b)
        {
            if (a == null || b == null) return false;
            return a.Edge == b.Edge && a.Dpi == b.Dpi
                && SameRect(a.TaskbarPx, b.TaskbarPx)
                && SameRect(a.MonitorPx, b.MonitorPx)
                && SameRect(a.WorkPx, b.WorkPx)
                && SameRect(a.StartCellPx, b.StartCellPx)
                && SameRect(a.CloverPx, b.CloverPx)
                && a.StartLocated == b.StartLocated
                && a.TaskButtonsLocated == b.TaskButtonsLocated
                && a.TaskbarVisible == b.TaskbarVisible
                && a.AutoHide == b.AutoHide
                && string.Equals(a.Source, b.Source, StringComparison.Ordinal);
        }

        static bool SameRect(Native.RECT a, Native.RECT b)
        {
            return a.Left == b.Left && a.Top == b.Top
                && a.Right == b.Right && a.Bottom == b.Bottom;
        }

        static void RaiseChanged(bool refined)
        {
            try { Action a = LayoutChanged; if (a != null) a(); } catch { }
            if (!refined) return;
            try { Action a = GeometryRefined; if (a != null) a(); } catch { }
        }

        static Snapshot BuildNativeSnapshot()
        {
            Snapshot s = new Snapshot();
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            Native.RECT taskbar;
            uint appbarEdge;
            bool fromAppbar = Native.GetTaskbarPos(out appbarEdge, out taskbar);
            if (!fromAppbar && tray != IntPtr.Zero)
                Native.GetWindowRectPhysical(tray, out taskbar);

            if (Width(taskbar) <= 0 || Height(taskbar) <= 0)
                return BuildEmergencySnapshot();

            s.TaskbarPx = taskbar;
            s.TaskbarVisible = tray != IntPtr.Zero && Native.IsWindowVisible(tray);
            s.AutoHide = Native.TaskbarAutoHide();
            s.Source = fromAppbar ? "appbar" : "trayWindow";
            RefreshMonitorAndDpi(s);
            s.Edge = fromAppbar && appbarEdge <= 3
                ? EdgeFromAppbar(appbarEdge) : InferEdge(taskbar, s.MonitorPx);
            s.WorkPx = EffectiveWorkArea(s);
            s.StartCellPx = EstimateStartCell(s);
            s.CloverPx = BuildCloverRect(s, double.NaN);
            return s;
        }

        static Snapshot BuildEmergencySnapshot()
        {
            Snapshot s = new Snapshot();
            int dpi = 96;
            try { uint d = Native.GetDpiForSystem(); if (d > 0) dpi = (int)d; } catch { }
            double scale = dpi / 96.0;
            int sw = Math.Max(1, (int)Math.Round(SystemParameters.PrimaryScreenWidth * scale));
            int sh = Math.Max(1, (int)Math.Round(SystemParameters.PrimaryScreenHeight * scale));
            int thick = Math.Max(1, (int)Math.Round(48 * scale));
            s.Dpi = dpi;
            s.Edge = Edge.Bottom;
            s.MonitorPx = R(0, 0, sw, sh);
            s.WorkPx = R(0, 0, sw, Math.Max(1, sh - thick));
            s.TaskbarPx = R(0, Math.Max(0, sh - thick), sw, sh);
            s.StartCellPx = EstimateStartCell(s);
            s.CloverPx = BuildCloverRect(s, double.NaN);
            s.Source = "fallback";
            return s;
        }

        static void RefreshMonitorAndDpi(Snapshot s)
        {
            Native.MONITORINFO info;
            if (Native.TryGetMonitorInfoForRect(s.TaskbarPx, out info))
            {
                s.MonitorPx = info.rcMonitor;
                s.WorkPx = info.rcWork;
            }
            else { s.MonitorPx = s.TaskbarPx; s.WorkPx = s.TaskbarPx; }

            int dpi = 0;
            try
            {
                IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
                if (tray != IntPtr.Zero) dpi = Native.GetDpiForWindow(tray);
            }
            catch { }
            if (dpi <= 0) { try { dpi = (int)Native.GetDpiForSystem(); } catch { } }
            s.Dpi = dpi > 0 ? dpi : 96;
        }

        static Native.RECT EffectiveWorkArea(Snapshot s)
        {
            if (!s.TaskbarVisible || s.AutoHide) return s.MonitorPx;
            Native.RECT w = s.MonitorPx;
            switch (s.Edge)
            {
                case Edge.Top: w.Top = Math.Max(w.Top, s.TaskbarPx.Bottom); break;
                case Edge.Left: w.Left = Math.Max(w.Left, s.TaskbarPx.Right); break;
                case Edge.Right: w.Right = Math.Min(w.Right, s.TaskbarPx.Left); break;
                default: w.Bottom = Math.Min(w.Bottom, s.TaskbarPx.Top); break;
            }
            return w;
        }

        static Native.RECT EstimateStartCell(Snapshot s)
        {
            int along = Math.Max(1, Px(56, s.Scale));
            if (s.Edge == Edge.Top || s.Edge == Edge.Bottom)
                return R(s.TaskbarPx.Left, s.TaskbarPx.Top,
                    Math.Min(s.TaskbarPx.Right, s.TaskbarPx.Left + along), s.TaskbarPx.Bottom);
            return R(s.TaskbarPx.Left, s.TaskbarPx.Top, s.TaskbarPx.Right,
                Math.Min(s.TaskbarPx.Bottom, s.TaskbarPx.Top + along));
        }

        static Native.RECT BuildCloverRect(Snapshot s, double taskEnd)
        {
            int w = Math.Max(1, Px(44, s.Scale));
            int h = Math.Max(1, Px(48, s.Scale));
            int gap = Math.Max(1, Px(4, s.Scale));
            int x, y;
            if (s.Edge == Edge.Left || s.Edge == Edge.Right)
            {
                x = s.TaskbarPx.Left + (Width(s.TaskbarPx) - w) / 2;
                y = IsFinite(taskEnd) ? (int)Math.Round(taskEnd + gap) : s.StartCellPx.Bottom + gap;
                y = Clamp(y, s.TaskbarPx.Top, s.TaskbarPx.Bottom - h);
            }
            else
            {
                y = s.TaskbarPx.Top + (Height(s.TaskbarPx) - h) / 2;
                x = IsFinite(taskEnd) ? (int)Math.Round(taskEnd + gap) : s.StartCellPx.Right + gap;
                x = Clamp(x, s.TaskbarPx.Left, s.TaskbarPx.Right - w);
            }
            return R(x, y, x + w, y + h);
        }

        static Native.RECT RebuildClover(Native.RECT previous, Snapshot native)
        {
            int w = Math.Max(1, Px(44, native.Scale));
            int h = Math.Max(1, Px(48, native.Scale));
            if (native.Edge == Edge.Left || native.Edge == Edge.Right)
            {
                int x = native.TaskbarPx.Left + (Width(native.TaskbarPx) - w) / 2;
                int y = Clamp(previous.Top, native.TaskbarPx.Top, native.TaskbarPx.Bottom - h);
                return R(x, y, x + w, y + h);
            }
            else
            {
                int x = Clamp(previous.Left, native.TaskbarPx.Left, native.TaskbarPx.Right - w);
                int y = native.TaskbarPx.Top + (Height(native.TaskbarPx) - h) / 2;
                return R(x, y, x + w, y + h);
            }
        }

        public static Rect StartButtonRect() { Snapshot s = Current; return ToLocalDip(StartButtonRectPx(), s.Scale); }

        public static Native.RECT StartButtonRectPx()
        {
            Snapshot s = Current;
            int w = Px(44, s.Scale), h = Px(48, s.Scale);
            int x = s.StartCellPx.Left + (Width(s.StartCellPx) - w) / 2;
            int y = s.StartCellPx.Top + (Height(s.StartCellPx) - h) / 2;
            return R(x, y, x + w, y + h);
        }

        public static Rect CloverRect() { Snapshot s = Current; return ToLocalDip(s.CloverPx, s.Scale); }
        public static Native.RECT CloverRectPx() { return Current.CloverPx; }

        public static bool TryGetButtonLeft(out double leftDip)
        {
            Snapshot s = Current;
            leftDip = s.CloverPx.Left / s.Scale;
            return s.TaskButtonsLocated;
        }

        public static bool TryGetButtonLeftPx(out int leftPx)
        {
            Snapshot s = Current;
            leftPx = s.CloverPx.Left;
            return s.TaskButtonsLocated;
        }

        public static bool MiniBarAnchor(out double edgeDip)
        {
            Snapshot s = Current;
            edgeDip = (s.Edge == Edge.Top ? s.WorkPx.Top : s.WorkPx.Bottom) / s.Scale;
            return true;
        }

        public static Rect MiniBarRect(int count)
        {
            Snapshot s = Current;
            return ToLocalDip(BuildMiniBarRect(s, count), s.Scale);
        }

        public static Native.RECT MiniBarRectPx(int count) { return BuildMiniBarRect(Current, count); }

        static Native.RECT BuildMiniBarRect(Snapshot s, int count)
        {
            int gap = Px(4, s.Scale), itemW = Px(44, s.Scale), itemH = Px(48, s.Scale);
            int n = Math.Max(0, count);
            if (s.Edge == Edge.Left || s.Edge == Edge.Right)
            {
                int height = itemH * n;
                int x = s.CloverPx.Left;
                int y = Clamp(s.CloverPx.Bottom + gap, s.MonitorPx.Top, s.MonitorPx.Bottom - height);
                return R(x, y, x + itemW, y + height);
            }

            int width = itemW * n;
            int bx = Clamp(s.CloverPx.Right + gap, s.MonitorPx.Left, s.MonitorPx.Right - width);
            int by = s.Edge == Edge.Top ? s.WorkPx.Top : s.WorkPx.Bottom - itemH;
            return R(bx, by, bx + width, by + itemH);
        }

        /// <summary>纯几何测试入口：不读取真实任务栏、不发 UIAutomation。</summary>
        internal static Snapshot BuildSnapshotForTest(Native.RECT taskbar, Native.RECT monitor,
            int dpi, Edge edge, Native.RECT startCell, double taskEnd,
            bool taskbarVisible, bool autoHide)
        {
            Snapshot s = new Snapshot();
            s.TaskbarPx = taskbar;
            s.MonitorPx = monitor;
            s.Dpi = dpi > 0 ? dpi : 96;
            s.Edge = edge;
            s.TaskbarVisible = taskbarVisible;
            s.AutoHide = autoHide;
            s.WorkPx = EffectiveWorkArea(s);
            s.StartCellPx = Width(startCell) > 0 && Height(startCell) > 0
                ? startCell : EstimateStartCell(s);
            s.CloverPx = BuildCloverRect(s, taskEnd);
            return s;
        }

        internal static Native.RECT MiniBarRectForTest(Snapshot snapshot, int count)
        {
            return BuildMiniBarRect(snapshot, count);
        }

        /// <summary>按物理像素定位任务栏关联窗口，不让主屏 DPI 虚拟化坐标。</summary>
        public static void PositionWindow(Window window, IntPtr hwnd, Native.RECT rect, bool resize)
        {
            if (window == null || Width(rect) <= 0 || Height(rect) <= 0) return;
            if (hwnd != IntPtr.Zero)
            {
                uint flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE;
                if (!resize) flags |= Native.SWP_NOSIZE;
                Native.SetWindowPosPhysical(hwnd, IntPtr.Zero, rect.Left, rect.Top,
                    Width(rect), Height(rect), flags);
                return;
            }

            double systemScale = SystemScale();
            window.Left = rect.Left / systemScale;
            window.Top = rect.Top / systemScale;
            if (resize)
            {
                window.Width = Width(rect) / systemScale;
                window.Height = Height(rect) / systemScale;
            }
        }

        public static Native.RECT ActualWindowRectPx(Window window)
        {
            Native.RECT r = new Native.RECT();
            try
            {
                if (window == null) return r;
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero) Native.GetWindowRectPhysical(hwnd, out r);
            }
            catch { }
            return r;
        }

        public static double SystemScale()
        {
            try { uint dpi = Native.GetDpiForSystem(); if (dpi > 0) return dpi / 96.0; }
            catch { }
            return 1;
        }

        static void ReportUiState(Snapshot s)
        {
            bool degraded = s.Source == "fallback";
            string state = degraded ? "degraded/fallback" : "ok/" + s.Source;
            if (state == lastUiState) return;
            lastUiState = state;
            if (degraded)
                ErrorLog.Warn("任务栏定位降级：找不到有效任务栏矩形，暂用主屏底部 48px；稍后自动重试。");
            else
                Ui.Log("任务栏定位: " + state
                    + (s.StartLocated || s.TaskButtonsLocated ? "" : "（等待统一精确校准）"));
        }

        static Edge EdgeFromAppbar(uint edge)
        {
            switch (edge)
            {
                case 0: return Edge.Left;
                case 1: return Edge.Top;
                case 2: return Edge.Right;
                default: return Edge.Bottom;
            }
        }

        static Edge InferEdge(Native.RECT bar, Native.RECT monitor)
        {
            // 横向任务栏通常铺满整块屏幕宽度，因此会同时贴住左、右以及上/下
            // 三条边。若简单在四个距离里取最小值，底部任务栏的 dl/dr/db 都是 0，
            // 就会被分支顺序误判成 Left。先按长宽确定方向，再只比较对应的两条边。
            if (Width(bar) >= Height(bar))
            {
                int dt = Math.Abs(bar.Top - monitor.Top);
                int db = Math.Abs(bar.Bottom - monitor.Bottom);
                return dt <= db ? Edge.Top : Edge.Bottom;
            }

            int dl = Math.Abs(bar.Left - monitor.Left);
            int dr = Math.Abs(bar.Right - monitor.Right);
            return dl <= dr ? Edge.Left : Edge.Right;
        }

        /// <summary>测试入口：验证铺满整边的任务栏不会因多边距离并列而误判。</summary>
        internal static Edge InferEdgeForTest(Native.RECT bar, Native.RECT monitor)
        {
            return InferEdge(bar, monitor);
        }

        static bool SameMonitor(Native.RECT a, Native.RECT b)
        {
            return SameRect(a, b);
        }

        static bool Valid(Rect r)
        {
            return !r.IsEmpty && IsFinite(r.X) && IsFinite(r.Y)
                && IsFinite(r.Width) && IsFinite(r.Height) && r.Width > 0 && r.Height > 0;
        }

        static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v) && v != double.MinValue;
        }

        static Native.RECT FromRect(Rect r)
        {
            return R((int)Math.Round(r.Left), (int)Math.Round(r.Top),
                (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));
        }

        static Rect ToLocalDip(Native.RECT r, double scale)
        {
            if (scale <= 0) scale = 1;
            return new Rect(r.Left / scale, r.Top / scale,
                Math.Max(0, Width(r) / scale), Math.Max(0, Height(r) / scale));
        }

        static Native.RECT R(int left, int top, int right, int bottom)
        {
            return new Native.RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        }

        static int Width(Native.RECT r) { return r.Right - r.Left; }
        static int Height(Native.RECT r) { return r.Bottom - r.Top; }
        static int Px(double dip, double scale) { return (int)Math.Round(dip * (scale > 0 ? scale : 1)); }
        static int Clamp(int value, int min, int max)
        {
            if (max < min) max = min;
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
