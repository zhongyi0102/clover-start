using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Clover
{
    /// <summary>
    /// 「查看隐藏的项目」对话框：列出被隐藏的应用，逐个恢复。
    ///
    /// 为什么需要这个：分类标签只剩「常用」「全部」后，没有「隐藏的项目」页了。
    /// 如果只留「取消隐藏全部」，用户想恢复某一个（比如误隐藏了一个常用软件）
    /// 就得先全部恢复、再重新隐藏其余的 —— 很别扭。
    ///
    /// 形态：自绘 WPF 窗口（与 TextPromptDialog 同风格），不引入 WinForms。
    /// 每行：应用名 + 「恢复」按钮。点恢复立刻从 hidden-apps.json 移除并刷新列表。
    /// </summary>
    static class HiddenItemsDialog
    {
        /// <summary>
        /// 显示对话框。
        /// </summary>
        /// <param name="owner">父窗口（用于居中）</param>
        /// <param name="hidden">当前隐藏的应用（调用方传入快照）</param>
        /// <param name="onRestore">
        /// 点「恢复」时回调，参数是应用路径。返回 true 表示成功。
        /// 由调用方负责从 hidden-apps.json 移除并刷新面板。
        /// </param>
        public static void Show(Window owner, List<AppItem> hidden, Func<string, bool> onRestore)
        {
            try
            {
                Window dlg = new Window();
                dlg.Title = "隐藏的项目";
                dlg.Width = 420;
                dlg.Height = 460;
                dlg.WindowStyle = WindowStyle.None;
                dlg.AllowsTransparency = true;
                dlg.Background = Brushes.Transparent;
                dlg.ResizeMode = ResizeMode.NoResize;
                dlg.ShowInTaskbar = false;
                dlg.Owner = owner;
                dlg.WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen;

                Border root = new Border();
                root.CornerRadius = new CornerRadius(Ui.R.Lg);
                root.Background = Ui.Bg;
                root.BorderBrush = Ui.Line;
                root.BorderThickness = new Thickness(1);
                root.Effect = Ui.PanelShadow();
                root.Margin = new Thickness(16);

                Grid layout = new Grid();
                layout.RowDefinitions.Add(new RowDefinition());   // 标题
                layout.RowDefinitions.Add(new RowDefinition());   // 列表
                layout.RowDefinitions.Add(new RowDefinition());   // 底部按钮
                layout.RowDefinitions[0].Height = GridLength.Auto;
                layout.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
                layout.RowDefinitions[2].Height = GridLength.Auto;

                // ---- 标题 ----
                TextBlock title = Ui.T("隐藏的项目", 14, Ui.Fg, FontWeights.SemiBold);
                title.Margin = new Thickness(Ui.S.Xl, Ui.S.Lg, Ui.S.Xl, Ui.S.Sm);
                Grid.SetRow(title, 0);
                layout.Children.Add(title);

                // ---- 列表 ----
                ScrollViewer scroll = new ScrollViewer();
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                scroll.Margin = new Thickness(Ui.S.Lg, 0, Ui.S.Lg, Ui.S.Sm);

                StackPanel list = new StackPanel();
                scroll.Content = list;
                Grid.SetRow(scroll, 1);
                layout.Children.Add(scroll);

                // ---- 底部 ----
                StackPanel footer = new StackPanel();
                footer.Orientation = Orientation.Horizontal;
                footer.HorizontalAlignment = HorizontalAlignment.Right;
                footer.Margin = new Thickness(Ui.S.Lg, 0, Ui.S.Lg, Ui.S.Lg);

                Border closeBtn = MakeChip("关闭", true, delegate { dlg.Close(); });
                footer.Children.Add(closeBtn);
                Grid.SetRow(footer, 2);
                layout.Children.Add(footer);

                // ---- 填充列表 ----
                Action rebuild = null;
                rebuild = delegate
                {
                    list.Children.Clear();

                    if (hidden == null || hidden.Count == 0)
                    {
                        TextBlock none = Ui.T("没有被隐藏的项目", 12.5, Ui.Sub);
                        none.Margin = new Thickness(Ui.S.Sm, Ui.S.Md, 0, 0);
                        list.Children.Add(none);
                        return;
                    }

                    // 从后往前遍历，因为恢复时会从 hidden 里移除元素
                    List<AppItem> snapshot = new List<AppItem>(hidden);
                    foreach (AppItem a in snapshot)
                    {
                        if (a == null) continue;
                        AppItem item = a;

                        Border row = new Border();
                        row.CornerRadius = new CornerRadius(Ui.R.Sm);
                        row.Margin = new Thickness(0, 1, 0, 1);
                        row.Padding = new Thickness(Ui.S.Sm, 5, Ui.S.Sm, 5);
                        row.Background = Brushes.Transparent;

                        Grid g = new Grid();
                        g.ColumnDefinitions.Add(new ColumnDefinition());
                        g.ColumnDefinitions.Add(new ColumnDefinition());
                        g.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                        g.ColumnDefinitions[1].Width = GridLength.Auto;

                        TextBlock name = Ui.T(item.Name, 12.5, Ui.Fg);
                        name.TextTrimming = TextTrimming.CharacterEllipsis;
                        name.VerticalAlignment = VerticalAlignment.Center;
                        name.ToolTip = item.Path;
                        Grid.SetColumn(name, 0);
                        g.Children.Add(name);

                        Border restore = MakeChip("恢复", false, delegate
                        {
                            try
                            {
                                bool ok = onRestore != null && onRestore(item.Path);
                                if (ok)
                                {
                                    hidden.Remove(item);
                                    rebuild();
                                }
                            }
                            catch { }
                        });
                        restore.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
                        Grid.SetColumn(restore, 1);
                        g.Children.Add(restore);

                        row.Child = g;
                        row.MouseEnter += delegate { row.Background = Ui.Hover; };
                        row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
                        list.Children.Add(row);
                    }
                };

                rebuild();

                root.Child = layout;
                dlg.Content = root;

                // Esc 关闭
                dlg.PreviewKeyDown += delegate(object s, KeyEventArgs e)
                {
                    if (e.Key == Key.Escape) { dlg.Close(); e.Handled = true; }
                };

                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "显示隐藏项对话框");
            }
        }

        /// <summary>对话框里的小胶囊按钮。</summary>
        static Border MakeChip(string label, bool primary, Action onClick)
        {
            Border b = new Border();
            b.Height = 28;
            b.CornerRadius = new CornerRadius(b.Height / 2);
            b.Padding = new Thickness(14, 0, 14, 0);
            b.Cursor = Cursors.Hand;
            b.Background = primary ? Ui.Accent : Ui.SearchBg;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(primary ? 0 : 1);

            TextBlock t = Ui.T(label, 11.5, primary ? Brushes.White : Ui.Fg,
                primary ? FontWeights.SemiBold : FontWeights.Normal);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            b.MouseEnter += delegate { b.Opacity = 0.85; };
            b.MouseLeave += delegate { b.Opacity = 1.0; };
            b.MouseLeftButtonUp += delegate { if (onClick != null) onClick(); };
            return b;
        }
    }
}
