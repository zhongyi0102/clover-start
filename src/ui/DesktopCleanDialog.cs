using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Clover
{
    /// <summary>
    /// 「桌面保持无快捷方式」设置窗口。
    ///
    /// ── 四块内容 ──
    ///
    ///   1. 总开关 + 子选项「同时清理现有的」
    ///   2. 保留名单（可手动增删）
    ///   3. 已移除（可单个恢复 / 全部恢复；恢复即加入保留名单）
    ///   4. 待处理（公共桌面被拒的，点此提权处理）
    /// <summary>
    /// 「整理与隐藏」窗口的标签一：桌面快捷方式。
    ///
    /// 原 DesktopCleanDialog 的内容，改成可嵌入的 Page（不自己开窗口）。
    /// 数据和逻辑与标签二完全独立，只共用外壳。
    /// </summary>
    class DesktopCleanPage
    {
        /// <summary>
        /// 宿主窗口。**只用作 MessageBox.Show 的 owner**，
        /// 不得修改它的 `Content` —— 那是 OrganizeDialog 的外壳（标签栏 + pageHost），
        /// 一旦被覆盖，整个窗口内容会被从视觉树上摘掉（曾经就是这么坏的）。
        /// </summary>
        readonly Window dlg;
        readonly Settings settings;
        readonly AppWatcher watcher;
        readonly Action onChanged;
        readonly PanelWindow panel;

        CheckBox chkEnabled;
        StackPanel keepList;
        StackPanel removedList;
        StackPanel movesList;
        Border pendingBox;
        TextBlock pendingText;

        /// <summary>页面内容（由外壳放进 ContentControl）。</summary>
        public FrameworkElement Content { get; private set; }

        public DesktopCleanPage(Window host, Settings settings, AppWatcher watcher, Action onChanged, PanelWindow panel = null)
        {
            this.dlg = host;
            this.settings = settings;
            this.watcher = watcher;
            this.onChanged = onChanged;
            this.panel = panel;

            BuildUi();
        }

        void BuildUi()
        {
            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.Padding = new Thickness(Ui.S.Xl);

            StackPanel col = new StackPanel();
            col.Orientation = Orientation.Vertical;

            // ---- 标题 ----
            TextBlock title = Ui.T("自动保持桌面空", 15, Ui.Fg);
            title.FontWeight = FontWeights.SemiBold;
            title.Margin = new Thickness(0, 0, 0, Ui.S.Md);
            col.Children.Add(title);

            TextBlock hint = Ui.T(
                "开启后，用户桌面的稳定文档与网页入口移入桌面收纳；确认面板已有的应用快捷方式移到回收站。"
                + "软件本体不删除。例外、临时文件、文件夹、公共桌面与无法确认的入口保留，使用收纳桌面手动处理。",
                11, Ui.Sub);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 0, 0, Ui.S.Lg);
            col.Children.Add(hint);

            // ---- 1. 开关 ----
            chkEnabled = new CheckBox();
            chkEnabled.Content = "自动保持桌面空（默认关闭）";
            chkEnabled.IsChecked = settings.DesktopKeepEmptyEnabled;
            chkEnabled.Foreground = Ui.Fg;
            chkEnabled.Margin = new Thickness(0, 0, 0, Ui.S.Sm);
            chkEnabled.Checked += delegate { OnToggle(true); };
            chkEnabled.Unchecked += delegate { OnToggle(false); };
            col.Children.Add(chkEnabled);

            // ---- 2. 保留名单 ----
            col.Children.Add(SectionHeader("桌面例外", "主动放回或恢复的项目保留，取消例外后可再次处理"));

            StackPanel keepBar = new StackPanel();
            keepBar.Orientation = Orientation.Horizontal;
            keepBar.Margin = new Thickness(0, 0, 0, Ui.S.Sm);

            Button addBtn = SmallButton("手动添加…", delegate { AddKeepManually(); });
            keepBar.Children.Add(addBtn);

            Button pruneBtn = SmallButton("清理失效项", delegate { PruneKeep(); });
            pruneBtn.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
            keepBar.Children.Add(pruneBtn);

            col.Children.Add(keepBar);

            keepList = new StackPanel();
            keepList.Orientation = Orientation.Vertical;
            col.Children.Add(keepList);

            col.Children.Add(SectionHeader("移动记录", "只还原所选记录，同名不覆盖；与快捷方式回收记录分开"));
            movesList = new StackPanel();
            col.Children.Add(movesList);

            // ---- 3. 已移除 ----
            col.Children.Add(SectionHeader("已移除", "点「恢复」会同时加入保留名单，以后不再移除"));

            removedList = new StackPanel();
            removedList.Orientation = Orientation.Vertical;
            col.Children.Add(removedList);

            // ---- 4. 待处理 ----
            pendingBox = new Border();
            pendingBox.Background = Ui.SearchBg;
            pendingBox.BorderBrush = Ui.Line;
            pendingBox.BorderThickness = new Thickness(1);
            pendingBox.CornerRadius = new CornerRadius(Ui.R.Sm);
            pendingBox.Padding = new Thickness(Ui.S.Sm);
            pendingBox.Margin = new Thickness(0, Ui.S.Lg, 0, 0);
            pendingBox.Visibility = Visibility.Collapsed;

            pendingText = Ui.T("", 11, Ui.Accent);
            pendingText.TextWrapping = TextWrapping.Wrap;
            pendingBox.Child = pendingText;

            col.Children.Add(pendingBox);

            sv.Content = col;
            Content = sv;

            RefreshLists();
        }

        // ========== 开关 ==========

        void OnToggle(bool on)
        {
            try
            {
                if (on)
                {
                    // ★ 说明弹窗：默认焦点在「取消」
                    string msg = "将自动处理用户桌面现有及以后新增的稳定文件：\r\n"
                        + "文档与网页入口移入桌面收纳；面板已有的应用快捷方式移到回收站。\r\n\r\n"
                        + "不删除软件本体；例外、临时、占用及未知项目保留。文件夹与公共桌面需手动收纳确认。";

                    MessageBoxResult r = MessageBox.Show(dlg, msg,
                        "四叶草 · 自动保持桌面空",
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                        MessageBoxResult.Cancel);   // ★ 默认焦点在「取消」

                    if (r != MessageBoxResult.OK)
                    {
                        // 用户取消 → 恢复复选框
                        chkEnabled.IsChecked = false;
                        return;
                    }
                }

                settings.DesktopKeepEmptyEnabled = on;
                settings.DesktopCleanEnabled = false;

                if (watcher != null) watcher.SetDesktopClean(on);
                else settings.Save();

                RefreshLists();
                Notify();
            }
            catch (Exception ex) { Ui.LogError(ex, "切换桌面清理开关"); }
        }

        // ========== 保留名单 ==========

        void RefreshKeepList()
        {
            try
            {
                keepList.Children.Clear();

                List<string> list = settings.DesktopKeepTargets ?? new List<string>();
                if (list.Count == 0 && settings.DesktopKeepPaths.Count == 0)
                {
                    keepList.Children.Add(Ui.T("（空）", 11, Ui.Sub));
                    return;
                }

                foreach (string key in list)
                {
                    keepList.Children.Add(MakeKeepRow(key));
                }
                foreach (string path in settings.DesktopKeepPaths.ToArray())
                {
                    Grid row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    TextBlock text = Ui.T(PathName(path), 10.5, Ui.Fg);
                    text.TextTrimming = TextTrimming.CharacterEllipsis;
                    text.ToolTip = path;
                    row.Children.Add(text);
                    string capturedPath = path;
                    Button cancel = SmallButton("取消例外", delegate {
                        lock (settings) { settings.DesktopKeepPaths.Remove(capturedPath); settings.Save(); }
                        RefreshKeepList(); Notify();
                    });
                    Grid.SetColumn(cancel, 1); row.Children.Add(cancel);
                    keepList.Children.Add(row);
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "刷新保留名单"); }
        }

        FrameworkElement MakeKeepRow(string key)
        {
            Grid g = new Grid();
            g.Margin = new Thickness(0, 1, 0, 1);
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            g.ColumnDefinitions[1].Width = GridLength.Auto;

            string display = key ?? "";
            if (display.StartsWith(DesktopKeep.NamePrefix, StringComparison.OrdinalIgnoreCase))
                display = "（按文件名）" + display.Substring(DesktopKeep.NamePrefix.Length);

            TextBlock t = Ui.T(display, 10.5, Ui.Fg);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.ToolTip = key;
            t.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(t, 0);
            g.Children.Add(t);

            string captured = key;
            Button del = SmallButton("取消例外", delegate
            {
                try
                {
                    DesktopKeep.RemoveKey(settings.DesktopKeepTargets, captured);
                    settings.Save();
                    RefreshKeepList();
                    Notify();
                }
                catch (Exception ex) { Ui.LogError(ex, "删除保留项"); }
            });
            Grid.SetColumn(del, 1);
            g.Children.Add(del);

            return g;
        }

        void AddKeepManually()
        {
            try
            {
                string s = TextPromptDialog.Ask(dlg, "手动添加保留项",
                    "输入要保留在桌面的项目完整路径：", "");
                if (string.IsNullOrWhiteSpace(s)) return;

                string key = s.Trim();
                if (!key.StartsWith(DesktopKeep.NamePrefix, StringComparison.OrdinalIgnoreCase))
                    key = DesktopKeep.NormalizeKey(key);

                if (string.IsNullOrEmpty(key)) return;

                DesktopWorkspace.Keep(settings, key);
                RefreshKeepList();
                Notify();
            }
            catch (Exception ex) { Ui.LogError(ex, "手动添加保留项"); }
        }

        void PruneKeep()
        {
            try
            {
                int n = DesktopKeep.Prune(settings.DesktopKeepTargets);
                n += settings.DesktopKeepPaths.RemoveAll(p => !System.IO.File.Exists(p) && !System.IO.Directory.Exists(p));
                settings.Save();
                RefreshKeepList();
                Notify();

                MessageBox.Show(dlg,
                    n > 0 ? ("清理了 " + n + " 个已不存在的条目。") : "没有失效条目。",
                    "四叶草 · 保留名单", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { Ui.LogError(ex, "清理保留名单"); }
        }

        // ========== 已移除 ==========

        void RefreshRemovedList()
        {
            try
            {
                removedList.Children.Clear();

                List<object> list = settings.DesktopRemoved ?? new List<object>();
                if (list.Count == 0)
                {
                    removedList.Children.Add(Ui.T("（空）", 11, Ui.Sub));
                    return;
                }

                // 倒序显示（最新在上）
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    Dictionary<string, object> d = list[i] as Dictionary<string, object>;
                    if (d == null) continue;
                    removedList.Children.Add(MakeRemovedRow(d));
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "刷新已移除列表"); }
        }

        FrameworkElement MakeRemovedRow(Dictionary<string, object> d)
        {
            Grid g = new Grid();
            g.Margin = new Thickness(0, 1, 0, 1);
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            g.ColumnDefinitions[1].Width = GridLength.Auto;

            string name = Str(d, "name");
            string time = Str(d, "removedAt");
            string path = Str(d, "lnkPath");

            TextBlock t = Ui.T(name + "    " + time, 10.5, Ui.Fg);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.ToolTip = path;
            t.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(t, 0);
            g.Children.Add(t);

            Button restore = SmallButton("恢复", delegate { RestoreOne(d); });
            Grid.SetColumn(restore, 1);
            g.Children.Add(restore);

            return g;
        }

        void RestoreOne(Dictionary<string, object> d)
        {
            try
            {
                string path = Str(d, "lnkPath");
                if (string.IsNullOrWhiteSpace(path)) return;

                // ★ 顺序：先加入保留名单，再还原
                //   反过来的话 watcher 会在还原瞬间把它当新快捷方式移走
                DesktopWorkspace.Keep(settings, path);

                int n = UndoBar.RestoreFromRecycleBin(new List<string> { path });
                if (n > 0)
                {
                    settings.DesktopRemoved.Remove(d);
                    settings.Save();
                }

                RefreshLists();
                Notify();

                if (n == 0)
                {
                    MessageBox.Show(dlg,
                        "自动还原失败。\r\n\r\n请手动到回收站右键该文件 → 还原。\r\n"
                        + "（已加入保留名单，还原后不会再被移除）",
                        "四叶草 · 恢复", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "恢复已移除项"); }
        }

        // ========== 待处理 ==========

        void RefreshPending()
        {
            try
            {
                int n = settings.DesktopPendingAdmin == null ? 0 : settings.DesktopPendingAdmin.Count;
                if (n == 0)
                {
                    pendingBox.Visibility = Visibility.Collapsed;
                    return;
                }

                pendingText.Text = "有 " + n + " 个公共桌面快捷方式曾因权限保留。请在收纳预览中确认；本程序不提供永久删除。";
                pendingBox.Visibility = Visibility.Visible;
            }
            catch { }
        }

        // ========== 通用 ==========

        void RefreshLists()
        {
            RefreshKeepList();
            RefreshRemovedList();
            RefreshMoves();
            RefreshPending();
        }

        void RefreshMoves()
        {
            movesList.Children.Clear();
            foreach (object item in settings.DesktopMoves.ToArray())
            {
                Dictionary<string, object> record = item as Dictionary<string, object>;
                if (record == null) continue;
                string from = Str(record, "from"), to = Str(record, "to");
                Grid row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                TextBlock text = Ui.T(Str(record, "action") + " · " + PathName(to) + " · " + Str(record, "time"), 10.5, Ui.Fg);
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                text.ToolTip = from + " → " + to;
                row.Children.Add(text);
                Button restore = SmallButton("还原移动", delegate
                {
                    if (!TextPromptDialog.Confirm(dlg, "还原移动", "将此项目移回原位置？同名不覆盖，桌面项目将加入例外。\r\n" + from, "还原移动")) return;
                    string parent = System.IO.Path.GetDirectoryName(from);
                    if (!System.IO.Directory.Exists(parent)) { MessageBox.Show(dlg, "原目录已不存在，未移动。请先确认位置。", "四叶草"); return; }
                    string target = DeskCollector.MakeUniqueTarget(parent, System.IO.Path.GetFileName(from), System.IO.Directory.Exists(to));
                    if (DeskCollector.DesktopDirs().Exists(p => string.Equals(p, parent, StringComparison.OrdinalIgnoreCase))) DesktopWorkspace.Keep(settings, target);
                    VerifiedFileMove.Result result = VerifiedFileMove.Move(to, target);
                    if (result.Ok) { settings.DesktopMoves.Remove(record); settings.Save(); RefreshMoves(); RefreshKeepList(); Notify(); }
                    else MessageBox.Show(dlg, "未还原：" + result.Message, "四叶草");
                });
                restore.IsEnabled = System.IO.File.Exists(to) || System.IO.Directory.Exists(to);
                Grid.SetColumn(restore, 1); row.Children.Add(restore);
                movesList.Children.Insert(0, row);
            }
            if (movesList.Children.Count == 0) movesList.Children.Add(Ui.T("（空）", 11, Ui.Sub));
        }

        static string PathName(string path) { try { return System.IO.Path.GetFileName(path); } catch { return path; } }

        void Notify()
        {
            try { if (onChanged != null) onChanged(); }
            catch { }
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            try
            {
                if (d == null || !d.ContainsKey(key)) return "";
                return Convert.ToString(d[key]) ?? "";
            }
            catch { return ""; }
        }

        static TextBlock SectionHeader(string title, string sub)
        {
            TextBlock t = Ui.T(title + "    " + sub, 11.5, Ui.Sub);
            t.FontWeight = FontWeights.SemiBold;
            t.Margin = new Thickness(0, Ui.S.Lg, 0, Ui.S.Sm);
            t.TextWrapping = TextWrapping.Wrap;
            return t;
        }

        static Button SmallButton(string label, Action onClick)
        {
            Button b = new Button();
            b.Template = PanelWindow.MakeButtonTemplate(10);
            b.Padding = new Thickness(10, 2, 10, 2);
            b.MinHeight = 26;
            b.Cursor = System.Windows.Input.Cursors.Hand;
            b.Background = Ui.SearchBg;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(1);

            TextBlock t = Ui.T(label, 10.5, Ui.Fg);
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Margin = new Thickness(10, 2, 10, 2);
            b.Content = t;

            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = Ui.SearchBg; };
            b.GotKeyboardFocus += delegate { b.Background = Ui.AccentSoft; b.BorderBrush = Ui.Accent; };
            b.LostKeyboardFocus += delegate { b.Background = Ui.SearchBg; b.BorderBrush = Ui.Line; };
            b.Click += delegate
            {
                try { if (onClick != null) onClick(); }
                catch (Exception ex) { Ui.LogError(ex, "设置按钮"); }
            };

            return b;
        }

    }
}
