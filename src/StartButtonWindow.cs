using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 任务栏隐藏时显示的“开始”按钮：点一下打开/收起原生开始菜单。
    ///
    /// 做法：把任务栏临时改成“全透明但仍可点击”，再向原生开始按钮位置发送一次点击，
    /// 让系统自己弹出开始菜单，然后立刻把任务栏恢复成隐藏 + 正常样式。
    ///
    /// 加固点（本轮）：
    ///   - 不再用 Thread.Sleep 阻塞界面线程，改成 DispatcherTimer 分步推进
    ///   - 不再移动用户鼠标（SetCursorPos + mouse_event 会劫持用户操作），
    ///     改成向目标点投递 WM_LBUTTONDOWN/UP（PostMessage），用户鼠标不受影响
    ///   - 全透明状态通过 TaskbarVisibility 记录到 system-state.json，
    ///     即使中途崩溃也能在下次启动还原
    ///   - 任一步失败都会在 finally 里恢复任务栏样式与可见性
    /// </summary>
    class StartButtonWindow : Window
    {
        Border root;
        IntPtr hwnd;

        /// <summary>正在打开原生开始菜单期间，让 OverlayGate 暂停管理显示状态。</summary>
        public bool GatePaused;

        /// <summary>正在执行点击流程，防止连点。</summary>
        bool busy;

        static readonly Brush HoverBg = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255));
        static readonly Brush IdleBg = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
        static readonly Brush LogoBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212));

        // 流程分步参数（毫秒）
        const int StepPrepare = 140;   // 让任务栏完成透明化
        const int StepClick = 70;      // 点击前后间隔
        const int StepFinish = 350;    // 等开始菜单弹出
        const int SW_SHOW = 5;

        public StartButtonWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Title = "开始";
            Width = 44;
            Height = 48;
            WindowStartupLocation = WindowStartupLocation.Manual;
            TaskbarGeometry.Refresh();
            Rect sb = TaskbarGeometry.StartButtonRect();
            Left = sb.X;
            Top = sb.Y;

            root = new Border();
            root.CornerRadius = new CornerRadius(6);
            root.Background = IdleBg;
            root.Cursor = Cursors.Hand;

            Grid logo = new Grid();
            logo.Width = 18;
            logo.Height = 18;
            logo.HorizontalAlignment = HorizontalAlignment.Center;
            logo.VerticalAlignment = VerticalAlignment.Center;
            for (int r = 0; r < 2; r++)
            {
                for (int c = 0; c < 2; c++)
                {
                    Border sq = new Border();
                    sq.Width = 8;
                    sq.Height = 8;
                    sq.CornerRadius = new CornerRadius(1);
                    sq.Background = LogoBrush;
                    sq.HorizontalAlignment = HorizontalAlignment.Left;
                    sq.VerticalAlignment = VerticalAlignment.Top;
                    sq.Margin = new Thickness(c * 10, r * 10, 0, 0);
                    logo.Children.Add(sq);
                }
            }
            root.Child = logo;

            root.MouseEnter += delegate { root.Background = HoverBg; };
            root.MouseLeave += delegate { root.Background = IdleBg; };
            root.MouseLeftButtonUp += delegate { OpenStartMenu(); };

            Content = root;
        }

        void OpenStartMenu()
        {
            if (busy) return;
            busy = true;
            GatePaused = true;

            int originalEx = 0;
            bool layered = false;
            bool wasVisible = false;

            try
            {
                IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero)
                {
                    EndOpen(ref originalEx, ref layered, ref wasVisible, false);
                    return;
                }
                wasVisible = Native.IsWindowVisible(tray);

                // 让任务栏参与但完全透明；状态会落盘到 system-state.json
                if (!wasVisible) Native.ShowWindow(tray, SW_SHOW);
                layered = TaskbarVisibility.MakeTaskbarLayered(out originalEx);

                // 暂时隐藏自己，避免挡住原生开始按钮
                this.Visibility = Visibility.Hidden;

                // 分步推进，不阻塞界面线程
                DispatcherTimer step = new DispatcherTimer();
                step.Interval = TimeSpan.FromMilliseconds(StepPrepare);
                step.Tick += delegate
                {
                    step.Stop();
                    try
                    {
                        ClickNativeStart();
                    }
                    catch (Exception ex)
                    {
                        ErrorLog.Warn("点击原生开始按钮失败: " + ex.Message);
                    }

                    DispatcherTimer finish = new DispatcherTimer();
                    finish.Interval = TimeSpan.FromMilliseconds(StepFinish);
                    finish.Tick += delegate
                    {
                        finish.Stop();
                        EndOpen(ref originalEx, ref layered, ref wasVisible, true);
                    };
                    finish.Start();
                };
                step.Start();
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("打开开始菜单失败: " + ex.Message);
                EndOpen(ref originalEx, ref layered, ref wasVisible, false);
            }
        }

        /// <summary>结束流程：恢复任务栏可见性与 EX 样式，解除 GatePaused。</summary>
        void EndOpen(ref int originalEx, ref bool layered, ref bool wasVisible, bool hideAgain)
        {
            try
            {
                if (layered) TaskbarVisibility.RestoreTaskbarLayered(originalEx);

                // 开始菜单弹出后任务栏本应重新隐藏（除非用户本来就让它显示）
                if (hideAgain && !wasVisible)
                {
                    IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
                    if (tray != IntPtr.Zero) Native.ShowWindow(tray, 0);
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("恢复任务栏状态失败: " + ex.Message);
            }
            finally
            {
                busy = false;
                GatePaused = false;
                OverlayGate.Refresh();
            }
        }

        /// <summary>
        /// 向原生开始按钮的位置投递一次点击。
        /// 用 PostMessage 而不是 SetCursorPos + mouse_event：
        /// 后者会真的移动用户鼠标，正在操作时会被打断。
        /// </summary>
        static void ClickNativeStart()
        {
            try
            {
                TaskbarGeometry.Refresh();
                double scl = TaskbarGeometry.ScaleFactor();
                int cx = (int)Math.Round((TaskbarGeometry.StartCell.X + TaskbarGeometry.StartCell.Width / 2) * scl);
                int cy = (int)Math.Round((TaskbarGeometry.StartCell.Y + TaskbarGeometry.StartCell.Height / 2) * scl);

                IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero) return;

                // 先找到该坐标下的窗口（通常是任务栏上的开始按钮子窗口）
                IntPtr target = Native.WindowFromPoint(cx, cy);
                if (target == IntPtr.Zero) target = tray;

                IntPtr lParam = (IntPtr)((cy << 16) | (cx & 0xFFFF));
                Native.PostMessage(target, 0x0201, (IntPtr)1, lParam); // WM_LBUTTONDOWN
                Thread.Sleep(StepClick);
                Native.PostMessage(target, 0x0202, IntPtr.Zero, lParam); // WM_LBUTTONUP

                // 兜底：某些 Windows 版本只认真实鼠标事件，再补一次极短的真实点击
                // （不移动光标：mouse_event 使用当前光标位置，这里只在光标恰好在按钮上时才补）
                Native.POINT p;
                if (Native.GetCursorPos(out p) && Math.Abs(p.X - cx) <= 2 && Math.Abs(p.Y - cy) <= 2)
                {
                    Native.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
                    Native.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("投递开始按钮点击失败: " + ex.Message);
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            hwnd = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(hwnd, -20, Native.GetWindowLong(hwnd, -20) | 0x80);
        }
    }
}
