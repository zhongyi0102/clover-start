using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clover
{
    /// <summary>面板界面搭建：整体骨架 + 通用小控件（图标按钮、字形）。</summary>
    partial class PanelWindow
    {
        void BuildUi()
        {
            Border root = new Border();
            root.CornerRadius = new CornerRadius(16);
            root.Background = Ui.Bg;
            root.BorderBrush = Ui.Line;
            root.BorderThickness = new Thickness(1);
            root.Margin = new Thickness(16);
            // 投影交给 ApplyShadow()：深色主题下要加深，否则黑投影落在深底上几乎看不见。
            // 存进 rootBorder 供切主题时重设（见 ApplyTheme）。
            rootBorder = root;
            ApplyShadow();

            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions[0].Height = new GridLength(64);
            grid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
            grid.RowDefinitions[2].Height = new GridLength(32);

            // ---------------- header ----------------
            Grid header = new Grid();
            header.Margin = new Thickness(18, 0, 14, 0);
            header.Background = Brushes.Transparent;
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions[0].Width = GridLength.Auto;
            header.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            header.ColumnDefinitions[2].Width = GridLength.Auto;
            header.ColumnDefinitions[3].Width = GridLength.Auto;

            Image logo = new Image();
            logo.Source = Ui.LoadCloverIcon(Path.Combine(baseDir, "res", "clover.png"));
            logo.Width = 28;
            logo.Height = 28;
            logo.VerticalAlignment = VerticalAlignment.Center;
            logo.Margin = new Thickness(0, 0, 12, 0);
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            Grid.SetColumn(logo, 0);
            header.Children.Add(logo);

            TextBlock title = Ui.T("四叶草", 16, Ui.Fg, FontWeights.SemiBold);
            title.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(title, 1);
            header.Children.Add(title);

            searchBorder = new Border();
            searchBorder.Width = 300;
            searchBorder.Height = 34;
            searchBorder.CornerRadius = new CornerRadius(17);
            searchBorder.Background = Ui.SearchBg;
            searchBorder.BorderBrush = Ui.Line;
            searchBorder.BorderThickness = new Thickness(1);
            searchBorder.VerticalAlignment = VerticalAlignment.Center;
            searchBorder.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(searchBorder, 2);

            Grid sg = new Grid();
            sg.ColumnDefinitions.Add(new ColumnDefinition());
            sg.ColumnDefinitions.Add(new ColumnDefinition());
            sg.ColumnDefinitions[0].Width = new GridLength(34);
            sg.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);

            TextBlock sic = new TextBlock();
            sic.Text = "\uE721";
            sic.FontFamily = Ui.Mdl;
            sic.FontSize = 12;
            sic.Foreground = Ui.Sub;
            sic.HorizontalAlignment = HorizontalAlignment.Center;
            sic.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(sic, 0);
            sg.Children.Add(sic);

            Grid fg = new Grid();
            Grid.SetColumn(fg, 1);
            searchHint = new TextBlock();
            searchHint.Text = "搜索应用（支持拼音 / 首字母）…";
            searchHint.FontFamily = Ui.YaHei;
            searchHint.FontSize = 12.5;
            searchHint.Foreground = Ui.Sub;
            searchHint.VerticalAlignment = VerticalAlignment.Center;
            searchHint.Margin = new Thickness(2, 0, 0, 0);
            searchHint.IsHitTestVisible = false;

            searchBox = new TextBox();
            searchBox.Background = Brushes.Transparent;
            searchBox.BorderThickness = new Thickness(0);
            searchBox.FontFamily = Ui.YaHei;
            searchBox.FontSize = 12.5;
            searchBox.Foreground = Ui.Fg;
            searchBox.VerticalContentAlignment = VerticalAlignment.Center;
            searchBox.Padding = new Thickness(0, 0, 8, 0);
            searchBox.TextChanged += delegate { OnSearchChanged(); };

            fg.Children.Add(searchHint);
            fg.Children.Add(searchBox);
            sg.Children.Add(fg);
            searchBorder.Child = sg;
            header.Children.Add(searchBorder);

            StackPanel winButtons = new StackPanel();
            winButtons.Orientation = Orientation.Horizontal;
            winButtons.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(winButtons, 3);
            header.Children.Add(winButtons);

            minBtn = MakeHeaderButton("\uE921", delegate { HidePanel(); });
            winButtons.Children.Add(minBtn);

            maxBtn = MakeHeaderButton("\uE922", delegate { ToggleExpand(); });
            winButtons.Children.Add(maxBtn);

            closeBtn = MakeHeaderButton("\uE711", delegate { HidePanel(); });
            winButtons.Children.Add(closeBtn);

            header.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                DependencyObject d = e.OriginalSource as DependencyObject;
                if (IsDescendantOf(d, searchBorder) || IsDescendantOf(d, closeBtn) || IsDescendantOf(d, minBtn) || IsDescendantOf(d, maxBtn)) return;
                try { DragMove(); } catch { }
            };

            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            // ---------------- body ----------------
            Grid body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition());
            body.ColumnDefinitions.Add(new ColumnDefinition());
            body.ColumnDefinitions.Add(new ColumnDefinition());
            body.ColumnDefinitions[0].Width = new GridLength(160);
            body.ColumnDefinitions[1].Width = new GridLength(1);
            body.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(body, 1);
            // 折叠动画改的是这一列的宽度（存成字段），见 ToggleNavCollapsed
            navColumn = body.ColumnDefinitions[0];

            // 注意：这里用字段 navGrid，不再声明局部同名变量
            navGrid = new Grid();
            navGrid.RowDefinitions.Add(new RowDefinition());
            navGrid.RowDefinitions.Add(new RowDefinition());
            navGrid.RowDefinitions.Add(new RowDefinition());
            navGrid.RowDefinitions[0].Height = GridLength.Auto;
            navGrid.RowDefinitions[1].Height = new GridLength(1);
            navGrid.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);

            // 折叠切换按钮：贴在侧栏上沿，点击收起/展开整条侧栏。
            // 放在 navGrid 的第 0 行里（和分类列表同一行），所以折叠时它仍在。
            navToggle = MakeNavToggle();
            Grid.SetRow(navToggle, 0);
            navGrid.Children.Add(navToggle);

            navScroll = new ScrollViewer();
            navScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            navScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            navScroll.Margin = new Thickness(10, 30, 4, 2);
            navStack = new StackPanel();
            navScroll.Content = navStack;
            Grid.SetRow(navScroll, 0);
            navGrid.Children.Add(navScroll);

            Border navSep = new Border();
            navSep.Height = 1;
            navSep.Background = Ui.Line;
            navSep.Margin = new Thickness(10, 2, 10, 2);
            Grid.SetRow(navSep, 1);
            navGrid.Children.Add(navSep);

            taskGrid = new Grid();
            taskGrid.RowDefinitions.Add(new RowDefinition());
            taskGrid.RowDefinitions.Add(new RowDefinition());
            taskGrid.RowDefinitions[0].Height = GridLength.Auto;
            taskGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
            TextBlock taskHeader = Ui.T("任务栏", 10.5, Ui.Sub);
            taskHeader.Margin = new Thickness(12, 2, 0, 2);
            Grid.SetRow(taskHeader, 0);
            taskGrid.Children.Add(taskHeader);
            ScrollViewer taskScroll = new ScrollViewer();
            taskScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            taskScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            taskScroll.Margin = new Thickness(6, 0, 4, 2);
            taskStack = new StackPanel();
            taskScroll.Content = taskStack;
            Grid.SetRow(taskScroll, 1);
            taskGrid.Children.Add(taskScroll);
            Grid.SetRow(taskGrid, 2);
            navGrid.Children.Add(taskGrid);

            Grid.SetColumn(navGrid, 0);
            body.Children.Add(navGrid);

            Border sep = new Border();
            sep.Width = 1;
            sep.Background = Ui.Line;
            sep.Margin = new Thickness(0, 6, 0, 6);
            Grid.SetColumn(sep, 1);
            body.Children.Add(sep);

            // 磁贴区滚动容器（存成字段，记住滚动位置要用）
            contentScroll = new ScrollViewer();
            contentScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            contentScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            contentScroll.Padding = new Thickness(10, 4, 8, 6);
            tilePanel = new WrapPanel();
            tilePanel.Orientation = Orientation.Horizontal;
            contentScroll.Content = tilePanel;
            Grid.SetColumn(contentScroll, 2);
            body.Children.Add(contentScroll);

            grid.Children.Add(body);

            // ---------------- footer ----------------
            Grid footer = new Grid();
            footer.Margin = new Thickness(20, 0, 18, 0);
            TextBlock hint = Ui.T("Esc 关闭 · 点击面板外自动收起 · 右键四叶草按钮可设置", 11, Ui.Sub);
            hint.HorizontalAlignment = HorizontalAlignment.Left;
            footer.Children.Add(hint);
            footerCount = Ui.T("", 11, Ui.Sub);
            footerCount.HorizontalAlignment = HorizontalAlignment.Right;
            footer.Children.Add(footerCount);
            Grid.SetRow(footer, 2);
            grid.Children.Add(footer);

            root.Child = grid;
            Content = root;

            // 弹出/收起动画用的位移变换，在这里就接好（而不是等第一次 ShowPanel）。
            // 好处：窗口一构造完就是可断言的状态，测试能直接检查是否接线。
            showShift = new TranslateTransform(0, 0);
            root.RenderTransform = showShift;

            // 滚动位置变化时记下来（按分类分别记），供下次切回该分类时恢复
            contentScroll.ScrollChanged += delegate(object s, ScrollChangedEventArgs e)
            {
                try
                {
                    if (suppressScrollRecord) return;
                    if (e.VerticalChange == 0 && e.ExtentHeightChange == 0) return;
                    scrollOffsets[current] = contentScroll.VerticalOffset;
                }
                catch { }
            };

            RebuildNav();
            RenderApps();

            // 恢复上次的侧栏收起状态（必须在 RebuildNav 之后：
            // 那一步会把分类项建出来，收起逻辑要作用于已存在的控件）
            ApplyNavCollapsedFromSettings();

            StartTaskTimer();
        }

        /// <summary>按当前主题重设面板投影（切主题时调用）。</summary>
        void ApplyShadow()
        {
            try
            {
                if (rootBorder != null) rootBorder.Effect = Ui.PanelShadow();
            }
            catch { }
        }

        /// <summary>
        /// 主题变了以后由 TriggerWindow 调用。
        ///
        /// 面板上绝大多数颜色走 Ui 刷子（共享对象），改 .Color 就自动重绘了；
        /// 这里只需要补那些**构建时定死**的部分：投影效果。
        /// </summary>
        public void ApplyTheme()
        {
            try
            {
                ApplyShadow();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "面板应用主题");
            }
        }

        TextBlock MakeGlyph(string glyph)
        {
            TextBlock t = new TextBlock();
            t.Text = glyph;
            t.FontFamily = Ui.Mdl;
            t.FontSize = 12;
            t.Foreground = Ui.Sub;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }

        Border MakeHeaderButton(string glyph, Action action)
        {
            Border b = new Border();
            b.Width = 32;
            b.Height = 32;
            b.CornerRadius = new CornerRadius(8);
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Cursor = Cursors.Hand;
            b.Background = Brushes.Transparent;
            b.Margin = new Thickness(2, 0, 0, 0);
            b.Child = MakeGlyph(glyph);
            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += delegate { if (action != null) action(); };
            return b;
        }

        /// <summary>
        /// 侧栏顶部的收起/展开按钮。
        ///
        /// 为什么单独做一个而不是靠双击分隔线：按钮更可发现，且状态一目了然
        /// （展开时显示 ◀、收起时显示 ▶）。
        /// </summary>
        Border MakeNavToggle()
        {
            Border b = new Border();
            b.Width = 22;
            b.Height = 22;
            b.CornerRadius = new CornerRadius(6);
            b.HorizontalAlignment = HorizontalAlignment.Right;
            b.VerticalAlignment = VerticalAlignment.Top;
            b.Margin = new Thickness(0, 4, 6, 0);
            b.Cursor = Cursors.Hand;
            b.Background = Brushes.Transparent;
            b.ToolTip = "收起 / 展开分类栏";
            b.Child = MakeGlyph("\uE76B");   // ChevronLeft
            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += delegate { ToggleNavCollapsed(); };
            return b;
        }

        /// <summary>侧栏展开宽度 / 收起宽度。</summary>
        public const double NavWidthExpanded = 160;
        public const double NavWidthCollapsed = 44;

        /// <summary>侧栏折叠按钮（验收测试用来检查它真的落在侧栏区域内）。</summary>
        public Border NavToggle { get { return navToggle; } }
        /// <summary>当前是否已收起。</summary>
        public bool NavIsCollapsed { get { return navCollapsed; } }
        /// <summary>侧栏列宽（-1 表示尚未建立）。</summary>
        public double NavColumnValue
        {
            get { return navColumn == null ? -1 : navColumn.Width.Value; }
        }
        /// <summary>供测试直接触发折叠（绕开鼠标点击）。</summary>
        public void ToggleNavNow() { ToggleNavCollapsed(); }

        /// <summary>
        /// 收起或展开侧栏。
        ///
        /// 只动列宽度（ColumnDefinition.Width 的 GridLengthAnimation 用 DoubleAnimation
        /// 做不到，所以用固定值直接切换 + 让 Grid 自己布局）。不用 Visibility 切换，
        /// 那会把整棵内容丢掉、展开时要重建，反而更慢也更容易出状态问题。
        /// </summary>
        void ToggleNavCollapsed()
        {
            try
            {
                navCollapsed = !navCollapsed;
                if (navColumn != null)
                {
                    navColumn.Width = new GridLength(
                        navCollapsed ? NavWidthCollapsed : NavWidthExpanded);
                }

                // 收起时把分类列表和任务栏整体藏起来（保留容器，避免重建）
                if (navScroll != null) navScroll.Visibility =
                    navCollapsed ? Visibility.Collapsed : Visibility.Visible;
                if (taskGrid != null) taskGrid.Visibility =
                    navCollapsed ? Visibility.Collapsed : Visibility.Visible;

                // 切换箭头方向
                if (navToggle != null)
                {
                    navToggle.Child = MakeGlyph(navCollapsed ? "\uE76C" : "\uE76B");
                }

                // 记住这个偏好：你在 dev-run 里反复重启时，不该每次都手动收起一遍
                if (settings != null)
                {
                    settings.NavCollapsed = navCollapsed;
                    settings.Save();
                }

                // 收起时磁贴区变宽，重排一次让图标重新流动
                if (tilePanel != null) tilePanel.UpdateLayout();

                Ui.Log("侧栏 " + (navCollapsed ? "收起" : "展开"));
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "切换侧栏");
            }
        }

        /// <summary>
        /// 按设置应用侧栏的收起状态（构造时调一次，恢复上次的样子）。
        ///
        /// 与 ToggleNavCollapsed 分开：那个是"切换"（会写设置），
        /// 这个是"应用已知状态"（只读不写），混在一起会导致构造时白写一次盘。
        /// </summary>
        void ApplyNavCollapsedFromSettings()
        {
            try
            {
                if (settings == null || !settings.NavCollapsed) return;
                navCollapsed = true;

                if (navColumn != null) navColumn.Width = new GridLength(NavWidthCollapsed);
                if (navScroll != null) navScroll.Visibility = Visibility.Collapsed;
                if (taskGrid != null) taskGrid.Visibility = Visibility.Collapsed;
                if (navToggle != null) navToggle.Child = MakeGlyph("\uE76C");

                Ui.Log("侧栏按上次设置收起");
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "恢复侧栏收起状态");
            }
        }

        static bool IsDescendantOf(DependencyObject d, DependencyObject parent)
        {
            while (d != null)
            {
                if (d == parent) return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }
    }
}
