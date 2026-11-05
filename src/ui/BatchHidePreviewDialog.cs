using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Clover
{
    /// <summary>
    /// 批量隐藏预览：「将隐藏 N 个应用」，可展开看清单，确认后才执行。
    ///
    /// ── 为什么需要预览 ──
    ///
    /// 「隐藏全部「系统工具」」一次可能带走 45 个条目。以前是直接执行，
    /// 用户看到面板突然少一大片才知道发生了什么，只能去「已隐藏」里一个个找回。
    /// 先给清单，用户能当场判断"这批里有没有我需要的"。
    ///
    /// ── 例外名单 ──
    ///
    /// 已在 `batchExceptions` 里的应用**不计入、不显示** —— 用户明确表示过
    /// "这个我要留着"，再把它列进"将隐藏"清单里只会让人困惑。
    /// </summary>
    class BatchHidePreviewDialog
    {
        readonly Window dlg;
        bool confirmed;

        /// <summary>用户是否点了「隐藏」。</summary>
        public bool Confirmed { get { return confirmed; } }

        /// <summary>
        /// 构造并显示。
        /// </summary>
        /// <param name="owner">父窗口</param>
        /// <param name="category">分类名（显示用）</param>
        /// <param name="targets">将被隐藏的应用（调用方已排除例外名单）</param>
        public BatchHidePreviewDialog(Window owner, string category, List<AppItem> targets)
        {
            dlg = new Window();
            dlg.Title = "四叶草 · 批量隐藏";
            dlg.Width = 460;
            dlg.Height = 520;
            dlg.WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen;
            dlg.Owner = owner;
            dlg.Background = Ui.Bg;
            dlg.ResizeMode = ResizeMode.CanResize;
            // 不占任务栏（这是配置对话框，一闪而过）。
            // 配合 DialogChrome.Attach 去掉最小化按钮 ——
            // 否则最小化后任务栏没图标，窗口再也找不回来。
            dlg.ShowInTaskbar = false;
            dlg.MinWidth = 380;
            dlg.MinHeight = 320;

            BuildUi(category ?? "", targets ?? new List<AppItem>());
        }

        void BuildUi(string category, List<AppItem> targets)
        {
            Grid layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition());   // 标题
            layout.RowDefinitions.Add(new RowDefinition());   // 清单
            layout.RowDefinitions.Add(new RowDefinition());   // 按钮
            layout.RowDefinitions[0].Height = GridLength.Auto;
            layout.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
            layout.RowDefinitions[2].Height = GridLength.Auto;

            // ---- 标题 ----
            StackPanel head = new StackPanel();
            head.Orientation = Orientation.Vertical;
            head.Margin = new Thickness(Ui.S.Xl, Ui.S.Lg, Ui.S.Xl, Ui.S.Sm);

            string titleText = targets.Count == 0
                ? "没有可隐藏的应用"
                : ("将隐藏 " + targets.Count + " 个应用");

            TextBlock title = Ui.T(titleText, 14, Ui.Fg);
            title.FontWeight = FontWeights.SemiBold;
            head.Children.Add(title);

            if (!string.IsNullOrWhiteSpace(category))
            {
                TextBlock sub = Ui.T("分类：" + category, 11, Ui.Sub);
                sub.Margin = new Thickness(0, 4, 0, 0);
                head.Children.Add(sub);
            }

            if (targets.Count > 0)
            {
                TextBlock hint = Ui.T(
                    "已在「例外名单」里的应用不会出现在下面，也不会被隐藏。",
                    10.5, Ui.Sub);
                hint.Margin = new Thickness(0, 6, 0, 0);
                hint.TextWrapping = TextWrapping.Wrap;
                head.Children.Add(hint);
            }

            Grid.SetRow(head, 0);
            layout.Children.Add(head);

            // ---- 清单（可折叠）----
            StackPanel body = new StackPanel();
            body.Orientation = Orientation.Vertical;
            body.Margin = new Thickness(Ui.S.Xl, 0, Ui.S.Xl, 0);

            if (targets.Count > 0)
            {
                // 折叠开关
                Border toggle = new Border();
                toggle.Cursor = Cursors.Hand;
                toggle.Padding = new Thickness(0, 4, 0, 6);
                toggle.Background = Brushes.Transparent;

                TextBlock toggleText = Ui.T("\uE70D  展开清单", 11, Ui.Accent);
                toggle.Child = toggleText;
                body.Children.Add(toggle);

                // 清单容器
                ScrollViewer sv = new ScrollViewer();
                sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                sv.MaxHeight = 280;
                sv.Visibility = Visibility.Collapsed;

                StackPanel names = new StackPanel();
                names.Orientation = Orientation.Vertical;

                // 按名称排序，方便扫读
                List<AppItem> sorted = new List<AppItem>(targets);
                sorted.Sort(delegate(AppItem x, AppItem y)
                {
                    try
                    {
                        return string.Compare(
                            Pinyin.SortKey(x.Name), Pinyin.SortKey(y.Name), StringComparison.Ordinal);
                    }
                    catch { return 0; }
                });

                foreach (AppItem a in sorted)
                {
                    if (a == null) continue;
                    TextBlock t = Ui.T("· " + (a.Name ?? ""), 11, Ui.Fg);
                    t.TextTrimming = TextTrimming.CharacterEllipsis;
                    t.Margin = new Thickness(4, 1, 4, 1);
                    t.ToolTip = a.Path;
                    names.Children.Add(t);
                }

                sv.Content = names;
                body.Children.Add(sv);

                bool expanded = false;
                toggle.MouseLeftButtonUp += delegate
                {
                    try
                    {
                        expanded = !expanded;
                        sv.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                        toggleText.Text = (expanded ? "\uE76C" : "\uE70D")
                            + (expanded ? "  收起清单" : "  展开清单");
                    }
                    catch (Exception ex) { Ui.LogError(ex, "折叠批量隐藏清单"); }
                };
            }

            Grid.SetRow(body, 1);
            layout.Children.Add(body);

            // ---- 按钮 ----
            StackPanel footer = new StackPanel();
            footer.Orientation = Orientation.Horizontal;
            footer.HorizontalAlignment = HorizontalAlignment.Right;
            footer.Margin = new Thickness(Ui.S.Xl, Ui.S.Sm, Ui.S.Xl, Ui.S.Lg);

            // 「取消」放前面（视觉上靠左），但**默认焦点**在它身上
            Border cancel = Chip("取消", false, delegate { dlg.Close(); });
            cancel.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
            footer.Children.Add(cancel);

            Border ok = Chip("隐藏", true, delegate
            {
                confirmed = true;
                dlg.Close();
            });
            ok.IsEnabled = targets.Count > 0;
            if (targets.Count == 0) ok.Opacity = 0.4;
            footer.Children.Add(ok);

            Grid.SetRow(footer, 2);
            layout.Children.Add(footer);

            dlg.Content = layout;

            // Esc = 取消
            dlg.PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { dlg.Close(); e.Handled = true; }
                else if (e.Key == Key.Enter)
                {
                    // ★ 回车不默认执行 —— 默认焦点在「取消」，这里也不给 Enter 删除
                    e.Handled = true;
                }
            };

            // 默认焦点：取消按钮（阻止 Enter 误触）
            dlg.Loaded += delegate
            {
                try { cancel.Focus(); } catch { }
            };
            cancel.Focusable = true;
            cancel.Focus();
        }

        static Border Chip(string label, bool primary, Action onClick)
        {
            Border b = new Border();
            b.Height = 30;
            b.CornerRadius = new CornerRadius(b.Height / 2);
            b.Padding = new Thickness(16, 0, 16, 0);
            b.Cursor = Cursors.Hand;
            b.Background = primary ? Ui.Accent : Ui.SearchBg;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(primary ? 0 : 1);
            b.Focusable = true;

            TextBlock t = Ui.T(label, 11.5, primary ? Brushes.White : Ui.Fg,
                primary ? FontWeights.SemiBold : FontWeights.Normal);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            b.MouseEnter += delegate { b.Opacity = 0.85; };
            b.MouseLeave += delegate { b.Opacity = 1.0; };
            b.MouseLeftButtonUp += delegate
            {
                try { if (onClick != null) onClick(); }
                catch (Exception ex) { Ui.LogError(ex, "批量隐藏预览按钮"); }
            };
            return b;
        }

        /// <summary>显示（模态）。返回用户是否确认。</summary>
        public bool ShowDialog()
        {
            try
            {
                DialogChrome.Attach(dlg);
                dlg.ShowDialog();
                return confirmed;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "显示批量隐藏预览");
                return false;
            }
        }
    }
}
