using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Clover
{
    /// <summary>
    /// 「低价值清单」窗口：列出被判为低价值的应用**及原因**，支持批量取消标记。
    ///
    /// ── 为什么需要它 ──
    ///
    /// 低价值判定靠关键词（卸载 / 帮助 / 许可 / 语言 / 修复……），必然有误判。
    /// 纠正手段本来只有磁贴右键「取消低价值标记」，一次只能处理一个 ——
    /// 9 个误判就要点 9 次菜单、在 113 个磁贴里逐个找。
    /// 这个窗口把"批量纠正"变成一次操作：勾选 → 一个按钮。
    ///
    /// ── 为什么把原因列出来 ──
    ///
    /// 只给名字的话，用户得逐个回忆"它为什么变淡"。
    /// 原因（「名称含"卸载"」/「目标不存在」）直接决定要不要取消标记：
    /// 失效快捷方式往往真的该沉底，误判的产品名才需要纠正。
    ///
    /// ── 样式 ──
    ///
    /// 与 BatchHidePreviewDialog 保持一致（同一套 Ui 令牌、同样的
    /// DialogChrome 外壳、默认焦点在「关闭」以防误触）。
    /// </summary>
    class LowValueListDialog
    {
        readonly Window dlg;
        readonly List<AppItem> targets;
        readonly List<AppItem> selected = new List<AppItem>();
        readonly Dictionary<AppItem, CheckBox> boxes = new Dictionary<AppItem, CheckBox>();

        Border okBtn;
        TextBlock okLabel;
        TextBlock summary;

        /// <summary>用户确认要取消标记的应用（只有点了「取消低价值标记」才有内容）。</summary>
        public List<AppItem> SelectedItems { get { return selected; } }

        public LowValueListDialog(Window owner, List<AppItem> lowValueApps)
        {
            targets = new List<AppItem>();
            if (lowValueApps != null)
            {
                foreach (AppItem a in lowValueApps)
                    if (a != null) targets.Add(a);
            }

            // 按名称排序，方便扫读（与批量隐藏窗口同一套规则）
            targets.Sort(delegate(AppItem x, AppItem y)
            {
                try
                {
                    return string.Compare(
                        Pinyin.SortKey(x.Name), Pinyin.SortKey(y.Name), StringComparison.Ordinal);
                }
                catch { return 0; }
            });

            dlg = new Window();
            dlg.Title = "四叶草 · 低价值清单";
            dlg.Width = 520;
            dlg.Height = 540;
            dlg.WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen;
            dlg.Owner = owner;
            dlg.Background = Ui.Bg;
            dlg.ResizeMode = ResizeMode.CanResize;
            // 不占任务栏（配置对话框）。配合 DialogChrome.Attach 去掉最小化按钮。
            dlg.ShowInTaskbar = false;
            dlg.MinWidth = 420;
            dlg.MinHeight = 340;

            BuildUi();
        }

        void BuildUi()
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
                ? "没有低价值应用"
                : ("共 " + targets.Count + " 个低价值应用");

            TextBlock title = Ui.T(titleText, 14, Ui.Fg);
            title.FontWeight = FontWeights.SemiBold;
            head.Children.Add(title);

            TextBlock hint = Ui.T(
                targets.Count == 0
                    ? "「全部」页里没有条目被判为低价值。"
                    : "勾选被误判的应用，点下面的按钮即可恢复正常亮度与排序。",
                10.5, Ui.Sub);
            hint.Margin = new Thickness(0, 6, 0, 0);
            hint.TextWrapping = TextWrapping.Wrap;
            head.Children.Add(hint);

            summary = Ui.T("", 10.5, Ui.Sub);
            summary.Margin = new Thickness(0, 6, 0, 0);
            head.Children.Add(summary);

            Grid.SetRow(head, 0);
            layout.Children.Add(head);

            // ---- 清单 ----
            StackPanel body = new StackPanel();
            body.Orientation = Orientation.Vertical;
            body.Margin = new Thickness(Ui.S.Xl, 0, Ui.S.Xl, 0);

            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

            StackPanel rows = new StackPanel();
            rows.Orientation = Orientation.Vertical;

            foreach (AppItem a in targets)
            {
                rows.Children.Add(MakeRow(a));
            }

            sv.Content = rows;
            body.Children.Add(sv);

            Grid.SetRow(body, 1);
            layout.Children.Add(body);

            // ---- 按钮 ----
            StackPanel footer = new StackPanel();
            footer.Orientation = Orientation.Horizontal;
            footer.HorizontalAlignment = HorizontalAlignment.Right;
            footer.Margin = new Thickness(Ui.S.Xl, Ui.S.Sm, Ui.S.Xl, Ui.S.Lg);

            // 「关闭」放前面（视觉靠左），默认焦点在它身上 —— 防 Enter 误触
            Border cancel = Chip("关闭", false, delegate { dlg.Close(); });
            cancel.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
            footer.Children.Add(cancel);

            okBtn = Chip("取消低价值标记（选中 0 项）", true, delegate
            {
                CollectSelected();
                dlg.Close();
            });
            okLabel = okBtn.Child as TextBlock;
            SetOkEnabled(targets.Count > 0);
            footer.Children.Add(okBtn);

            Grid.SetRow(footer, 2);
            layout.Children.Add(footer);

            dlg.Content = layout;

            UpdateSummary();

            // Esc = 关闭；Enter 不默认执行（默认焦点在「关闭」）
            dlg.PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { dlg.Close(); e.Handled = true; }
                else if (e.Key == Key.Enter) { e.Handled = true; }
            };

            dlg.Loaded += delegate { try { cancel.Focus(); } catch { } };
            cancel.Focusable = true;
            cancel.Focus();
        }

        /// <summary>一行：复选框 + 应用名 + 原因。</summary>
        Border MakeRow(AppItem a)
        {
            Border row = new Border();
            row.CornerRadius = new CornerRadius(Ui.R.Sm);
            row.Padding = new Thickness(Ui.S.Xs, Ui.S.Xs, Ui.S.Xs, Ui.S.Xs);
            row.Margin = new Thickness(0, 1, 0, 1);
            row.Background = Brushes.Transparent;

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = GridLength.Auto;   // 复选框
            g.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);

            CheckBox cb = new CheckBox();
            cb.VerticalAlignment = VerticalAlignment.Center;
            cb.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
            cb.Cursor = Cursors.Hand;
            cb.Checked += delegate { UpdateSummary(); };
            cb.Unchecked += delegate { UpdateSummary(); };
            boxes[a] = cb;
            g.Children.Add(cb);

            StackPanel texts = new StackPanel();
            texts.Orientation = Orientation.Vertical;
            texts.VerticalAlignment = VerticalAlignment.Center;

            TextBlock name = Ui.T(DisplayNameOf(a), 11.5, Ui.Fg);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.ToolTip = a.Path;
            texts.Children.Add(name);

            string detail = a.LowValueDetail;
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = a.LowValueReason == LowValueReason.ManualMarked
                    ? "已手动标记"
                    : "被规则判为低价值";
            }
            TextBlock why = Ui.T(detail, 10.5, Ui.Sub);
            why.TextTrimming = TextTrimming.CharacterEllipsis;
            why.Margin = new Thickness(0, 1, 0, 0);
            texts.Children.Add(why);

            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);

            row.Child = g;

            // 点整行也能切换勾选（不用精确点到那个小方框）
            row.MouseLeftButtonUp += delegate
            {
                try { cb.IsChecked = !(cb.IsChecked == true); }
                catch (Exception ex) { Ui.LogError(ex, "低价值清单勾选"); }
            };
            row.MouseEnter += delegate { row.Background = Ui.TileHover; };
            row.MouseLeave += delegate { row.Background = Brushes.Transparent; };

            return row;
        }

        /// <summary>与磁贴一致的显示名（商店应用带 ·商店 后缀）。</summary>
        static string DisplayNameOf(AppItem a)
        {
            try
            {
                if (a == null) return "";
                string name = a.Name ?? "";
                if (a.Source == AppPaths.SourceStore && !string.IsNullOrEmpty(name))
                    return name + " ·商店";
                return name;
            }
            catch { return a == null ? "" : (a.Name ?? ""); }
        }

        int CountChecked()
        {
            int n = 0;
            foreach (KeyValuePair<AppItem, CheckBox> kv in boxes)
            {
                if (kv.Value != null && kv.Value.IsChecked == true) n++;
            }
            return n;
        }

        void CollectSelected()
        {
            selected.Clear();
            foreach (KeyValuePair<AppItem, CheckBox> kv in boxes)
            {
                if (kv.Value != null && kv.Value.IsChecked == true) selected.Add(kv.Key);
            }
        }

        void UpdateSummary()
        {
            try
            {
                int n = CountChecked();
                if (summary != null)
                {
                    summary.Text = n == 0
                        ? "尚未勾选"
                        : ("已勾选 " + n + " 项，点右下按钮取消它们的低价值标记");
                }
                if (okLabel != null)
                    okLabel.Text = "取消低价值标记（选中 " + n + " 项）";

                SetOkEnabled(targets.Count > 0 && n > 0);
            }
            catch { }
        }

        void SetOkEnabled(bool on)
        {
            try
            {
                if (okBtn == null) return;
                okBtn.IsEnabled = on;
                okBtn.Opacity = on ? 1.0 : 0.4;
            }
            catch { }
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
                catch (Exception ex) { Ui.LogError(ex, "低价值清单按钮"); }
            };
            return b;
        }

        /// <summary>显示（模态）。返回用户是否确认取消标记。</summary>
        public bool ShowDialog()
        {
            try
            {
                DialogChrome.Attach(dlg);
                dlg.ShowDialog();
                return selected.Count > 0;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "显示低价值清单");
                return false;
            }
        }
    }
}
