using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 「整理与隐藏」窗口的标签二：已隐藏的应用。
    ///
    /// ── 相对旧版的改动 ──
    ///
    ///   · 每项显示**应用图标**（后台取，先占位字形）
    ///   · 名称下方小字显示**隐藏原因**（手动隐藏 / 批量隐藏：xxx）
    ///   · 悬停显示**目标路径**
    ///   · 顶部**搜索框**（拼音 + 首字母，复用 Pinyin.Matches）
    ///   · 按隐藏原因**分组**，组标题带数量、可折叠，手动隐藏的组在最上面
    ///   · **多选** + 底部「恢复所选」；每组标题旁「恢复本组」；「全部恢复」需确认
    ///   · 恢复后**弹窗不关闭**，该行淡出，选中落到下一项，焦点留在列表
    ///   · 底部「例外名单(N)」折叠区
    ///   · 键盘：↑↓ 选择，Enter 恢复当前项，Esc 先清搜索
    ///
    /// ── 数据 ──
    ///
    /// 从 PanelWindow 拿隐藏应用快照 + HiddenApps 的原因信息。
    /// 恢复走 PanelWindow.SetAppHidden（内部写 hidden-apps.json + 刷新）。
    /// </summary>
    class HiddenAppsPage
    {
        readonly Window dlg;
        readonly PanelWindow panel;
        readonly Settings settings;
        readonly Action onChanged;

        TextBox searchBox;
        TextBlock searchHint;
        ScrollViewer scroll;
        StackPanel listHost;
        TextBlock statusText;
        StackPanel exceptionHost;
        Border exceptionHeader;

        /// <summary>当前显示的行（筛选后，按显示顺序）。键盘导航靠它。</summary>
        readonly List<HiddenRow> rows = new List<HiddenRow>();

        /// <summary>选中的路径集合。</summary>
        readonly HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>分组折叠状态：组 key → 是否折叠。</summary>
        readonly Dictionary<string, bool> collapsed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>例外名单折叠状态。</summary>
        bool exceptionCollapsed = true;

        /// <summary>搜索抑制标志（程序改文本时不触发过滤）。</summary>
        bool suppress;

        /// <summary>页面内容。</summary>
        public FrameworkElement Content { get; private set; }

        /// <summary>一行数据。</summary>
        class HiddenRow
        {
            public AppItem Item;
            public HideReason Reason;
            public Border Element;
            public Image IconHost;
            public string PathKey;
        }

        public HiddenAppsPage(Window host, PanelWindow panel, Settings settings, Action onChanged)
        {
            this.dlg = host;
            this.panel = panel;
            this.settings = settings;
            this.onChanged = onChanged;

            BuildUi();
        }

        // ========== 构建 ==========

        void BuildUi()
        {
            Grid layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition());   // 搜索
            layout.RowDefinitions.Add(new RowDefinition());   // 列表
            layout.RowDefinitions.Add(new RowDefinition());   // 例外名单
            layout.RowDefinitions.Add(new RowDefinition());   // 底部按钮
            layout.RowDefinitions[0].Height = GridLength.Auto;
            layout.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
            layout.RowDefinitions[2].Height = GridLength.Auto;
            layout.RowDefinitions[3].Height = GridLength.Auto;

            // ---- 搜索框 ----
            Border searchWrap = new Border();
            searchWrap.CornerRadius = new CornerRadius(Ui.R.Sm);
            searchWrap.Background = Ui.SearchBg;
            searchWrap.BorderBrush = Ui.Line;
            searchWrap.BorderThickness = new Thickness(1);
            searchWrap.Margin = new Thickness(Ui.S.Xl, 0, Ui.S.Xl, Ui.S.Sm);
            searchWrap.Height = 32;

            Grid sg = new Grid();
            searchBox = new TextBox();
            searchBox.Background = Brushes.Transparent;
            searchBox.BorderThickness = new Thickness(0);
            searchBox.Foreground = Ui.Fg;
            searchBox.CaretBrush = Ui.Fg;
            searchBox.FontSize = 12;
            searchBox.Padding = new Thickness(10, 0, 10, 0);
            searchBox.VerticalContentAlignment = VerticalAlignment.Center;
            searchBox.TextChanged += delegate
            {
                try
                {
                    if (suppress) return;
                    if (searchHint != null)
                        searchHint.Visibility = searchBox.Text.Length == 0
                            ? Visibility.Visible : Visibility.Collapsed;
                    Rebuild();
                }
                catch (Exception ex) { Ui.LogError(ex, "隐藏项搜索"); }
            };
            sg.Children.Add(searchBox);

            searchHint = Ui.T("搜索（支持拼音 / 首字母）", 12, Ui.Sub);
            searchHint.Margin = new Thickness(12, 0, 0, 0);
            searchHint.VerticalAlignment = VerticalAlignment.Center;
            searchHint.IsHitTestVisible = false;
            sg.Children.Add(searchHint);

            searchWrap.Child = sg;
            Grid.SetRow(searchWrap, 0);
            layout.Children.Add(searchWrap);

            // ---- 列表 ----
            scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.Margin = new Thickness(Ui.S.Xl, 0, Ui.S.Xl, 0);
            scroll.Focusable = true;

            listHost = new StackPanel();
            listHost.Orientation = Orientation.Vertical;
            scroll.Content = listHost;
            Grid.SetRow(scroll, 1);
            layout.Children.Add(scroll);

            // ---- 例外名单 ----
            StackPanel exWrap = new StackPanel();
            exWrap.Orientation = Orientation.Vertical;
            exWrap.Margin = new Thickness(Ui.S.Xl, Ui.S.Sm, Ui.S.Xl, 0);

            exceptionHeader = new Border();
            exceptionHeader.Cursor = Cursors.Hand;
            exceptionHeader.Padding = new Thickness(0, 4, 0, 4);
            exceptionHeader.Background = Brushes.Transparent;

            TextBlock exTitle = Ui.T("例外名单 (0)", 11, Ui.Sub);
            exTitle.FontWeight = FontWeights.SemiBold;
            exceptionHeader.Child = exTitle;
            exceptionHeader.MouseLeftButtonUp += delegate
            {
                try
                {
                    exceptionCollapsed = !exceptionCollapsed;
                    RefreshExceptions();
                }
                catch (Exception ex) { Ui.LogError(ex, "折叠例外名单"); }
            };
            exWrap.Children.Add(exceptionHeader);

            exceptionHost = new StackPanel();
            exceptionHost.Orientation = Orientation.Vertical;
            exceptionHost.Margin = new Thickness(0, 2, 0, 0);
            exWrap.Children.Add(exceptionHost);

            Grid.SetRow(exWrap, 2);
            layout.Children.Add(exWrap);

            // ---- 底部 ----
            Grid footer = new Grid();
            footer.Margin = new Thickness(Ui.S.Xl, Ui.S.Sm, Ui.S.Xl, Ui.S.Lg);
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            footer.ColumnDefinitions[1].Width = GridLength.Auto;

            statusText = Ui.T("", 11, Ui.Sub);
            statusText.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(statusText, 0);
            footer.Children.Add(statusText);

            StackPanel btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            btns.HorizontalAlignment = HorizontalAlignment.Right;

            Border restoreSel = Chip("恢复所选", true, delegate { RestoreSelected(); });
            btns.Children.Add(restoreSel);

            Border restoreAll = Chip("全部恢复", false, delegate { RestoreAll(); });
            restoreAll.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
            btns.Children.Add(restoreAll);

            // 「隐藏用不到的」：原右键菜单的级联子菜单迁移至此，做成一个按钮
            Border hideTools = Chip("隐藏用不到的…", false, delegate { ShowHideTools(); });
            hideTools.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
            btns.Children.Add(hideTools);

            Grid.SetColumn(btns, 1);
            footer.Children.Add(btns);

            Grid.SetRow(footer, 3);
            layout.Children.Add(footer);

            Content = layout;

            // 键盘：↑↓ 选择，Enter 恢复当前项
            layout.PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                try
                {
                    if (e.Key == Key.Down) { MoveSelection(1); e.Handled = true; }
                    else if (e.Key == Key.Up) { MoveSelection(-1); e.Handled = true; }
                    else if (e.Key == Key.Enter) { RestoreCurrent(); e.Handled = true; }
                }
                catch (Exception ex) { Ui.LogError(ex, "隐藏项键盘"); }
            };
            layout.Focusable = true;
        }

        // ========== 显示 ==========

        /// <summary>外壳切到本标签时调用。</summary>
        public void OnShown()
        {
            try
            {
                Rebuild();
                // 焦点给列表（不是搜索框 —— 用户可能直接用键盘导航）
                if (scroll != null) scroll.Focus();
            }
            catch (Exception ex) { Ui.LogError(ex, "显示已隐藏应用"); }
        }

        /// <summary>
        /// Esc 处理：搜索框有内容 → 清空并返回 true（不关窗口）。
        /// 返回 false 表示交给外壳关闭窗口。
        /// </summary>
        public bool HandleEscape()
        {
            try
            {
                if (searchBox != null && searchBox.Text.Length > 0)
                {
                    suppress = true;
                    try { searchBox.Text = ""; }
                    finally { suppress = false; }

                    if (searchHint != null) searchHint.Visibility = Visibility.Visible;
                    Rebuild();
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        // ========== 重建列表 ==========

        void Rebuild()
        {
            try
            {
                if (listHost == null) return;

                listHost.Children.Clear();
                rows.Clear();

                List<AppItem> hidden = panel == null ? null : panel.HiddenSnapshot();
                if (hidden == null || hidden.Count == 0)
                {
                    TextBlock none = Ui.T("没有被隐藏的应用", 12.5, Ui.Sub);
                    none.Margin = new Thickness(Ui.S.Sm, Ui.S.Md, 0, 0);
                    listHost.Children.Add(none);
                    UpdateStatus(0, 0);
                    RefreshExceptions();
                    return;
                }

                string query = searchBox == null ? "" : (searchBox.Text ?? "").Trim();

                // ---- 1. 按原因分组 ----
                Dictionary<string, List<HiddenRow>> groups =
                    new Dictionary<string, List<HiddenRow>>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, HideReason> groupReason =
                    new Dictionary<string, HideReason>(StringComparer.OrdinalIgnoreCase);

                int totalMatched = 0;

                foreach (AppItem a in hidden)
                {
                    if (a == null) continue;

                    // 搜索过滤（拼音 + 首字母）
                    if (query.Length > 0)
                    {
                        bool hit = false;
                        try { hit = Pinyin.Matches(a.Name ?? "", query); }
                        catch { hit = false; }
                        if (!hit) continue;
                    }

                    HideReason r = panel.ReasonOf(a.Path);

                    HiddenRow row = new HiddenRow();
                    row.Item = a;
                    row.Reason = r;
                    row.PathKey = HiddenApps.Normalize(a.Path);

                    string gk = r.GroupKey;
                    List<HiddenRow> bucket;
                    if (!groups.TryGetValue(gk, out bucket))
                    {
                        bucket = new List<HiddenRow>();
                        groups[gk] = bucket;
                        groupReason[gk] = r;
                    }
                    bucket.Add(row);
                    totalMatched++;
                }

                // ---- 2. 排序：手动隐藏的组在最上面 ----
                List<string> order = new List<string>(groups.Keys);
                order.Sort(delegate(string x, string y)
                {
                    HideReason rx = groupReason[x];
                    HideReason ry = groupReason[y];
                    if (rx.GroupOrder != ry.GroupOrder) return rx.GroupOrder - ry.GroupOrder;

                    // 同类按组标题字典序
                    int c = string.Compare(rx.GroupTitle, ry.GroupTitle, StringComparison.Ordinal);
                    if (c != 0) return c;
                    return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
                });

                // ---- 3. 渲染 ----
                foreach (string gk in order)
                {
                    List<HiddenRow> bucket = groups[gk];
                    if (bucket.Count == 0) continue;

                    HideReason gr = groupReason[gk];
                    bool isCollapsed = false;
                    collapsed.TryGetValue(gk, out isCollapsed);

                    listHost.Children.Add(MakeGroupHeader(gk, gr, bucket.Count, isCollapsed));

                    if (isCollapsed) continue;

                    foreach (HiddenRow row in bucket)
                    {
                        Border el = MakeRow(row);
                        if (el != null)
                        {
                            row.Element = el;
                            listHost.Children.Add(el);
                            rows.Add(row);
                        }
                    }
                }

                UpdateStatus(totalMatched, hidden.Count);
                RefreshExceptions();
                UpdateSelectionVisuals();
            }
            catch (Exception ex) { Ui.LogError(ex, "重建已隐藏应用列表"); }
        }

        // ========== 组标题 ==========

        Border MakeGroupHeader(string gk, HideReason reason, int count, bool isCollapsed)
        {
            Border b = new Border();
            b.Padding = new Thickness(0, 8, 0, 4);
            b.Background = Brushes.Transparent;

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            g.ColumnDefinitions[1].Width = GridLength.Auto;

            // 折叠箭头 + 标题 + 数量
            StackPanel left = new StackPanel();
            left.Orientation = Orientation.Horizontal;

            TextBlock arrow = Ui.T(isCollapsed ? "\uE76C" : "\uE70D", 9, Ui.Sub);
            arrow.FontFamily = Ui.Mdl;
            arrow.VerticalAlignment = VerticalAlignment.Center;
            arrow.Margin = new Thickness(0, 0, 6, 0);
            left.Children.Add(arrow);

            TextBlock t = Ui.T(reason.GroupTitle + "  (" + count + ")", 11.5, Ui.Sub);
            t.FontWeight = FontWeights.SemiBold;
            t.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(t);

            Grid.SetColumn(left, 0);
            g.Children.Add(left);

            // 「恢复本组」
            Border restoreGroup = Chip("恢复本组", false, delegate { RestoreGroup(gk); });
            restoreGroup.Height = 22;
            restoreGroup.Padding = new Thickness(10, 0, 10, 0);
            Grid.SetColumn(restoreGroup, 1);
            g.Children.Add(restoreGroup);

            b.Child = g;
            b.Cursor = Cursors.Hand;
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                try
                {
                    // 点「恢复本组」按钮时不折叠（按钮自己会 Handled）
                    if (e.Handled) return;
                    bool c;
                    collapsed.TryGetValue(gk, out c);
                    collapsed[gk] = !c;
                    Rebuild();
                }
                catch (Exception ex) { Ui.LogError(ex, "折叠分组"); }
            };

            return b;
        }

        // ========== 行 ==========

        Border MakeRow(HiddenRow row)
        {
            try
            {
                AppItem a = row.Item;

                Border b = new Border();
                b.CornerRadius = new CornerRadius(Ui.R.Sm);
                b.Margin = new Thickness(0, 1, 0, 1);
                b.Padding = new Thickness(Ui.S.Sm, 6, Ui.S.Sm, 6);
                b.Background = Brushes.Transparent;
                b.Cursor = Cursors.Hand;
                b.ToolTip = a.Path;   // 悬停显示目标路径

                Grid g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition());   // 图标
                g.ColumnDefinitions.Add(new ColumnDefinition());   // 名称 + 原因
                g.ColumnDefinitions.Add(new ColumnDefinition());   // 恢复
                g.ColumnDefinitions[0].Width = GridLength.Auto;
                g.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                g.ColumnDefinitions[2].Width = GridLength.Auto;

                // 图标（先占位字形，后台取到再替换）
                Border iconWrap = new Border();
                iconWrap.Width = 28;
                iconWrap.Height = 28;
                iconWrap.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
                iconWrap.VerticalAlignment = VerticalAlignment.Center;

                Grid iconGrid = new Grid();

                TextBlock glyph = Ui.T(GlyphOf(a), 13, Ui.Sub);
                glyph.FontFamily = Ui.Mdl;
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                glyph.VerticalAlignment = VerticalAlignment.Center;
                iconGrid.Children.Add(glyph);

                Image img = new Image();
                img.Width = 24;
                img.Height = 24;
                img.Stretch = Stretch.Uniform;
                img.Visibility = Visibility.Collapsed;
                iconGrid.Children.Add(img);

                iconWrap.Child = iconGrid;
                Grid.SetColumn(iconWrap, 0);
                g.Children.Add(iconWrap);

                row.IconHost = img;
                LoadIconAsync(a, img, glyph);

                // 名称 + 原因
                StackPanel texts = new StackPanel();
                texts.Orientation = Orientation.Vertical;
                texts.VerticalAlignment = VerticalAlignment.Center;
                texts.Margin = new Thickness(0, 0, Ui.S.Sm, 0);

                TextBlock name = Ui.T(a.Name ?? "", 12, Ui.Fg);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                texts.Children.Add(name);

                TextBlock why = Ui.T(row.Reason.DisplayText, 10, Ui.Sub);
                why.TextTrimming = TextTrimming.CharacterEllipsis;
                texts.Children.Add(why);

                Grid.SetColumn(texts, 1);
                g.Children.Add(texts);

                // 恢复按钮
                AppItem captured = a;
                Border restore = Chip("恢复", false, delegate { RestoreOne(captured); });
                restore.Height = 24;
                restore.Padding = new Thickness(10, 0, 10, 0);
                Grid.SetColumn(restore, 2);
                g.Children.Add(restore);

                b.Child = g;

                b.MouseLeftButtonUp += delegate
                {
                    try
                    {
                        ToggleSelect(row.PathKey);
                        UpdateSelectionVisuals();
                    }
                    catch (Exception ex) { Ui.LogError(ex, "选择隐藏项"); }
                };

                return b;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "创建隐藏项行");
                return null;
            }
        }

        static string GlyphOf(AppItem a)
        {
            try
            {
                if (a == null) return "\uE7C3";
                string src = a.Source ?? "";
                if (string.Equals(src, "Store", StringComparison.OrdinalIgnoreCase)) return "\uE719";
                return "\uE7C3";   // 通用应用字形
            }
            catch { return "\uE7C3"; }
        }

        /// <summary>
        /// 后台取图标，取到后在界面线程替换占位字形。
        /// 不阻塞弹窗打开。
        /// </summary>
        void LoadIconAsync(AppItem a, Image target, TextBlock glyph)
        {
            try
            {
                if (a == null || string.IsNullOrEmpty(a.Path) || target == null) return;

                string path = a.Path;
                System.Threading.Thread t = new System.Threading.Thread(delegate()
                {
                    ImageSource src = null;
                    try { src = AppIcons.Get(path); }
                    catch { src = null; }

                    if (src == null) return;

                    try
                    {
                        Application.Current.Dispatcher.BeginInvoke((Action)delegate
                        {
                            try
                            {
                                target.Source = src;
                                target.Visibility = Visibility.Visible;
                                if (glyph != null) glyph.Visibility = Visibility.Collapsed;
                            }
                            catch { }
                        });
                    }
                    catch { }
                });
                t.IsBackground = true;
                t.Start();
            }
            catch { }
        }

        // ========== 选择 ==========

        void ToggleSelect(string pathKey)
        {
            try
            {
                if (string.IsNullOrEmpty(pathKey)) return;
                if (!selected.Remove(pathKey)) selected.Add(pathKey);
            }
            catch { }
        }

        void UpdateSelectionVisuals()
        {
            try
            {
                foreach (HiddenRow row in rows)
                {
                    if (row == null || row.Element == null) continue;
                    bool on = selected.Contains(row.PathKey);
                    row.Element.Background = on ? Ui.Hover : Brushes.Transparent;
                }
            }
            catch { }
        }

        /// <summary>当前焦点行在 rows 里的下标（没有选中 → -1）。</summary>
        int CurrentIndex()
        {
            try
            {
                for (int i = 0; i < rows.Count; i++)
                    if (rows[i] != null && selected.Contains(rows[i].PathKey)) return i;
                return -1;
            }
            catch { return -1; }
        }

        void MoveSelection(int delta)
        {
            try
            {
                if (rows.Count == 0) return;

                int cur = CurrentIndex();
                int next = cur < 0 ? 0 : Math.Max(0, Math.Min(rows.Count - 1, cur + delta));

                selected.Clear();
                if (rows[next] != null)
                {
                    selected.Add(rows[next].PathKey);
                    if (rows[next].Element != null) rows[next].Element.BringIntoView();
                }
                UpdateSelectionVisuals();
            }
            catch (Exception ex) { Ui.LogError(ex, "移动隐藏项选中"); }
        }

        // ========== 恢复 ==========

        void RestoreOne(AppItem a)
        {
            try
            {
                if (a == null || panel == null) return;

                int idx = -1;
                for (int i = 0; i < rows.Count; i++)
                    if (rows[i] != null && rows[i].Item == a) { idx = i; break; }

                // 该行淡出
                Border el = idx >= 0 ? rows[idx].Element : null;
                if (el != null)
                {
                    if (Ui.ReducedMotion)
                    {
                        el.BeginAnimation(UIElement.OpacityProperty, null);
                        el.Opacity = 0;
                    }
                    else
                    {
                        DoubleAnimation fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120));
                        el.BeginAnimation(UIElement.OpacityProperty, fade);
                    }
                }

                bool ok = panel.RestoreHidden(a);
                if (!ok) return;

                // 选中落到下一项
                string nextKey = null;
                if (idx >= 0)
                {
                    if (idx + 1 < rows.Count) nextKey = rows[idx + 1].PathKey;
                    else if (idx - 1 >= 0) nextKey = rows[idx - 1].PathKey;
                }

                Rebuild();

                selected.Clear();
                if (!string.IsNullOrEmpty(nextKey))
                {
                    foreach (HiddenRow r in rows)
                    {
                        if (r != null && string.Equals(r.PathKey, nextKey, StringComparison.OrdinalIgnoreCase))
                        {
                            selected.Add(r.PathKey);
                            break;
                        }
                    }
                }
                UpdateSelectionVisuals();

                // 焦点留在列表，不跳搜索框
                if (scroll != null) scroll.Focus();
            }
            catch (Exception ex) { Ui.LogError(ex, "恢复隐藏项"); }
        }

        void RestoreCurrent()
        {
            try
            {
                int idx = CurrentIndex();
                if (idx < 0 || idx >= rows.Count) return;
                RestoreOne(rows[idx].Item);
            }
            catch (Exception ex) { Ui.LogError(ex, "恢复当前隐藏项"); }
        }

        void RestoreSelected()
        {
            try
            {
                if (selected.Count == 0 || panel == null) return;

                List<AppItem> targets = new List<AppItem>();
                foreach (HiddenRow r in rows)
                {
                    if (r == null || r.Item == null) continue;
                    if (selected.Contains(r.PathKey)) targets.Add(r.Item);
                }
                if (targets.Count == 0) return;

                foreach (AppItem a in targets) panel.RestoreHidden(a);

                selected.Clear();
                Rebuild();
                if (scroll != null) scroll.Focus();
            }
            catch (Exception ex) { Ui.LogError(ex, "恢复所选隐藏项"); }
        }

        void RestoreGroup(string groupKey)
        {
            try
            {
                if (panel == null) return;

                List<AppItem> targets = new List<AppItem>();
                foreach (HiddenRow r in rows)
                {
                    if (r == null || r.Item == null) continue;
                    if (string.Equals(r.Reason.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase))
                        targets.Add(r.Item);
                }
                if (targets.Count == 0) return;

                foreach (AppItem a in targets) panel.RestoreHidden(a);

                selected.Clear();
                Rebuild();
                if (scroll != null) scroll.Focus();
            }
            catch (Exception ex) { Ui.LogError(ex, "恢复本组隐藏项"); }
        }

        void RestoreAll()
        {
            try
            {
                if (panel == null) return;

                int n = panel.HiddenCount();
                if (n == 0) return;

                // ★ 确认框，默认焦点在「取消」
                MessageBoxResult r = MessageBox.Show(dlg,
                    "将恢复全部 " + n + " 个被隐藏的应用。\r\n\r\n"
                    + "（批量隐藏的例外名单会保留，下次批量隐藏仍会跳过它们）",
                    "四叶草 · 全部恢复",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question,
                    MessageBoxResult.Cancel);

                if (r != MessageBoxResult.OK) return;

                panel.RestoreAllHidden();

                selected.Clear();
                Rebuild();
                if (scroll != null) scroll.Focus();
            }
            catch (Exception ex) { Ui.LogError(ex, "恢复全部隐藏项"); }
        }

        // ========== 状态与例外名单 ==========

        void UpdateStatus(int matched, int total)
        {
            try
            {
                if (statusText == null) return;

                if (matched == total)
                    statusText.Text = "共 " + total + " 个已隐藏" + (selected.Count > 0 ? "，已选 " + selected.Count + " 个" : "");
                else
                    statusText.Text = "匹配 " + matched + " / 共 " + total + " 个已隐藏"
                        + (selected.Count > 0 ? "，已选 " + selected.Count + " 个" : "");
            }
            catch { }
        }

        void RefreshExceptions()
        {
            try
            {
                if (exceptionHeader == null || exceptionHost == null || panel == null) return;

                List<string> ex = panel.BatchExceptionSnapshot();
                int n = ex == null ? 0 : ex.Count;

                TextBlock title = exceptionHeader.Child as TextBlock;
                if (title != null)
                    title.Text = (exceptionCollapsed ? "\uE76C" : "\uE70D") + "  例外名单 (" + n + ")";

                exceptionHost.Children.Clear();

                if (exceptionCollapsed || n == 0)
                {
                    exceptionHost.Visibility = Visibility.Collapsed;
                    return;
                }

                exceptionHost.Visibility = Visibility.Visible;

                foreach (string key in ex)
                {
                    Grid g = new Grid();
                    g.Margin = new Thickness(0, 1, 0, 1);
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    g.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                    g.ColumnDefinitions[1].Width = GridLength.Auto;

                    TextBlock t = Ui.T(key, 10, Ui.Sub);
                    t.TextTrimming = TextTrimming.CharacterEllipsis;
                    t.ToolTip = key;
                    t.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(t, 0);
                    g.Children.Add(t);

                    string captured = key;
                    Border del = Chip("取消", false, delegate
                    {
                        try
                        {
                            if (panel != null) panel.RemoveBatchException(captured);
                            RefreshExceptions();
                        }
                        catch (Exception ex2) { Ui.LogError(ex2, "取消例外"); }
                    });
                    del.Height = 20;
                    del.Padding = new Thickness(8, 0, 8, 0);
                    Grid.SetColumn(del, 1);
                    g.Children.Add(del);

                    exceptionHost.Children.Add(g);
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "刷新例外名单"); }
        }

        /// <summary>
        /// 「隐藏用不到的」：原右键菜单的级联子菜单，现在是一个按钮 + 弹出选择。
        ///
        /// 内容与原菜单一致：
        ///   · 隐藏全部「系统工具」
        ///   · 隐藏全部「工具/命令行」
        ///   · 取消隐藏全部
        /// </summary>
        void ShowHideTools()
        {
            try
            {
                if (panel == null) return;

                ContextMenu cm = new ContextMenu();

                foreach (string cat in new string[] { "系统工具", Catalog.ToolCategory })
                {
                    string c = cat;
                    MenuItem one = new MenuItem();
                    one.Header = "隐藏全部「" + c + "」";
                    one.Click += delegate
                    {
                        try
                        {
                            int n = panel.HideCategory(c);
                            if (n > 0) Rebuild();
                        }
                        catch (Exception ex) { Ui.LogError(ex, "批量隐藏分类"); }
                    };
                    cm.Items.Add(one);
                }

                cm.Items.Add(new Separator());

                MenuItem unhideAll = new MenuItem();
                unhideAll.Header = "取消隐藏全部";
                unhideAll.IsEnabled = panel.HiddenCount() > 0;
                unhideAll.Click += delegate
                {
                    try
                    {
                        int n = panel.UnhideAll();
                        if (n > 0) Rebuild();
                    }
                    catch (Exception ex) { Ui.LogError(ex, "取消隐藏全部"); }
                };
                cm.Items.Add(unhideAll);

                cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                cm.IsOpen = true;
            }
            catch (Exception ex) { Ui.LogError(ex, "显示隐藏工具菜单"); }
        }

        // ========== 小工具 ==========

        static Border Chip(string label, bool primary, Action onClick)
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
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                try
                {
                    e.Handled = true;   // 阻止冒泡到行/组标题
                    if (onClick != null) onClick();
                }
                catch (Exception ex) { Ui.LogError(ex, "隐藏项按钮"); }
            };
            return b;
        }
    }
}
