using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>面板左侧的“任务栏”区域：列出当前打开的窗口，支持切换/右键菜单/定时刷新。</summary>
    partial class PanelWindow
    {
        string taskSig = "";
        DateTime lastTaskEnum = DateTime.MinValue;

        /// <summary>
        /// 枚举窗口的最小间隔。ShowPanel 每次都会调这里，而枚举会为每个窗口
        /// 创建对象并取图标——即便有签名去重，枚举本身的开销也很大。
        /// 面板刚打开时不必每次都全量枚举。
        /// </summary>
        const int TaskEnumMinIntervalMs = 800;

        /// <summary>启动任务栏列表的定时刷新；重复调用只会复用同一个定时器。</summary>
        void StartTaskTimer()
        {
            if (taskTimer != null)
            {
                if (!taskTimer.IsEnabled) taskTimer.Start();
                return;
            }
            taskTimer = new DispatcherTimer();
            taskTimer.Interval = TimeSpan.FromMilliseconds(1500);
            taskTimer.Tick += delegate
            {
                try { if (IsVisible) RefreshTaskList(); }
                catch (Exception ex) { Ui.LogError(ex, "刷新任务栏列表"); }
            };
            taskTimer.Start();
        }

        void RefreshTaskList()
        {
            if (taskStack == null) return;

            // 距上次枚举太近就直接跳过，避免 ShowPanel 与定时器叠加导致重复全量枚举
            if ((DateTime.Now - lastTaskEnum).TotalMilliseconds < TaskEnumMinIntervalMs) return;
            lastTaskEnum = DateTime.Now;

            List<TaskWindow> wins = TaskList.GetWindows(Process.GetCurrentProcess().Id);
            StringBuilder sb = new StringBuilder();
            sb.Append(TaskList.GetActiveWindow()).Append('|');
            foreach (TaskWindow tw in wins) sb.Append(tw.Hwnd).Append(':').Append(tw.Title).Append('|');
            string sig = sb.ToString();
            if (sig == taskSig) return;
            taskSig = sig;
            taskStack.Children.Clear();
            if (wins.Count == 0)
            {
                TextBlock none = Ui.T("没有打开的窗口", 10.5, Ui.Sub);
                none.Margin = new Thickness(6, 4, 0, 0);
                taskStack.Children.Add(none);
                return;
            }
            foreach (TaskWindow w in wins)
                taskStack.Children.Add(MakeTaskRow(w));
        }

        Border MakeTaskRow(TaskWindow w)
        {
            Border b = new Border();
            b.Height = 30;
            b.CornerRadius = new CornerRadius(6);
            b.Margin = new Thickness(0, 1, 0, 1);
            b.Background = TaskList.IsActive(w.Hwnd) ? Ui.AccentSoft : Brushes.Transparent;
            b.Cursor = Cursors.Hand;
            b.ToolTip = w.Title;

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = new GridLength(22);
            g.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);

            if (w.Icon != null)
            {
                Image img = new Image();
                img.Source = w.Icon;
                img.Width = 16;
                img.Height = 16;
                img.Stretch = Stretch.Uniform;
                img.HorizontalAlignment = HorizontalAlignment.Center;
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                Grid.SetColumn(img, 0);
                g.Children.Add(img);
            }

            TextBlock t = new TextBlock();
            t.Text = w.Title;
            t.FontFamily = Ui.YaHei;
            t.FontSize = 10.5;
            t.Foreground = Ui.Fg;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(2, 0, 6, 0);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);

            b.Child = g;
            b.MouseEnter += delegate { if (!TaskList.IsActive(w.Hwnd)) b.Background = Ui.Hover; };
            b.MouseLeave += delegate { if (!TaskList.IsActive(w.Hwnd)) b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += delegate { TaskList.Activate(w.Hwnd); HidePanel(); };
            b.MouseRightButtonUp += delegate { ShowTaskMenu(w); };
            return b;
        }

        void ShowTaskMenu(TaskWindow w)
        {
            ContextMenu cm = new ContextMenu();

            MenuItem pin = new MenuItem();
            pin.Header = "显示在任务栏";
            pin.Click += delegate { if (miniBar != null) miniBar.Add(w.Hwnd); };
            cm.Items.Add(pin);

            taskMenuOpen = true;
            cm.Closed += delegate { taskMenuOpen = false; };
            cm.Placement = PlacementMode.MousePoint;
            cm.IsOpen = true;
        }
    }
}
