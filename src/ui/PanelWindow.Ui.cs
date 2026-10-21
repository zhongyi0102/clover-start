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
            root.Effect = Ui.Shadow(30, 0.20, 6);

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

            Grid navGrid = new Grid();
            navGrid.RowDefinitions.Add(new RowDefinition());
            navGrid.RowDefinitions.Add(new RowDefinition());
            navGrid.RowDefinitions.Add(new RowDefinition());
            navGrid.RowDefinitions[0].Height = GridLength.Auto;
            navGrid.RowDefinitions[1].Height = new GridLength(1);
            navGrid.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);

            ScrollViewer navScroll = new ScrollViewer();
            navScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            navScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            navScroll.Margin = new Thickness(10, 4, 4, 2);
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

            Grid taskGrid = new Grid();
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

            ScrollViewer contentScroll = new ScrollViewer();
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
            rootBorder = root;

            RebuildNav();
            RenderApps();

            StartTaskTimer();
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
