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

        CheckBox chkEnabled;
        CheckBox chkExisting;
        StackPanel keepList;
        StackPanel removedList;
        Border pendingBox;
        TextBlock pendingText;

        /// <summary>页面内容（由外壳放进 ContentControl）。</summary>
        public FrameworkElement Content { get; private set; }

        public DesktopCleanPage(Window host, Settings settings, AppWatcher watcher, Action onChanged)
        {
            this.dlg = host;
            this.settings = settings;
            this.watcher = watcher;
            this.onChanged = onChanged;

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
            TextBlock title = Ui.T("桌面保持无快捷方式", 15, Ui.Fg);
            title.FontWeight = FontWeights.SemiBold;
            title.Margin = new Thickness(0, 0, 0, Ui.S.Md);
            col.Children.Add(title);

            TextBlock hint = Ui.T(
                "开启后，桌面上的 .lnk / .url 会被自动移到回收站。"
                + "文件、文件夹、文档、图片一律不动。",
                11, Ui.Sub);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 0, 0, Ui.S.Lg);
            col.Children.Add(hint);

            // ---- 1. 开关 ----
            chkEnabled = new CheckBox();
            chkEnabled.Content = "启用（桌面不显示快捷方式）";
            chkEnabled.IsChecked = settings.DesktopCleanEnabled;
            chkEnabled.Foreground = Ui.Fg;
            chkEnabled.Margin = new Thickness(0, 0, 0, Ui.S.Sm);
            chkEnabled.Checked += delegate { OnToggle(true); };
            chkEnabled.Unchecked += delegate { OnToggle(false); };
            col.Children.Add(chkEnabled);

            chkExisting = new CheckBox();
            chkExisting.Content = "同时清理桌面现有的快捷方式";
            chkExisting.IsChecked = settings.DesktopCleanExisting;
            chkExisting.Foreground = Ui.Fg;
            chkExisting.Margin = new Thickness(20, 0, 0, Ui.S.Lg);
            chkExisting.IsEnabled = settings.DesktopCleanEnabled;
            chkExisting.Checked += delegate { SaveExisting(); };
            chkExisting.Unchecked += delegate { SaveExisting(); };
            col.Children.Add(chkExisting);

            // ---- 2. 保留名单 ----
            col.Children.Add(SectionHeader("保留名单", "名单里的快捷方式永远不被移除"));

            StackPanel keepBar = new StackPanel();
            keepBar.Orientation = Orientation.Horizontal;
            keepBar.Margin = new Thickness(0, 0, 0, Ui.S.Sm);

            Border addBtn = SmallButton("手动添加…", delegate { AddKeepManually(); });
            keepBar.Children.Add(addBtn);

            Border pruneBtn = SmallButton("清理失效项", delegate { PruneKeep(); });
            pruneBtn.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
            keepBar.Children.Add(pruneBtn);

            col.Children.Add(keepBar);

            keepList = new StackPanel();
            keepList.Orientation = Orientation.Vertical;
            col.Children.Add(keepList);

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
            pendingBox.Cursor = System.Windows.Input.Cursors.Hand;
            pendingBox.Visibility = Visibility.Collapsed;
            pendingBox.MouseLeftButtonUp += delegate { ProcessPending(); };

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
                chkExisting.IsEnabled = on;

                if (on)
                {
                    // ★ 说明弹窗：默认焦点在「取消」
                    string msg =
                        "开启后，桌面上的快捷方式会被自动移到回收站"
                        + "（可在「已移除」里恢复）。\r\n\r\n"
                        + (settings.DesktopCleanExisting
                            ? "当前的「同时清理现有的」已勾选 —— 现有的快捷方式会立即被清理。"
                            : "当前的「同时清理现有的」未勾选 —— 现有的快捷方式保留，只处理之后新出现的。");

                    MessageBoxResult r = MessageBox.Show(dlg, msg,
                        "四叶草 · 桌面快捷方式",
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                        MessageBoxResult.Cancel);   // ★ 默认焦点在「取消」

                    if (r != MessageBoxResult.OK)
                    {
                        // 用户取消 → 恢复复选框
                        chkEnabled.IsChecked = false;
                        return;
                    }
                }

                settings.DesktopCleanEnabled = on;

                if (watcher != null) watcher.SetDesktopClean(on);
                else settings.Save();

                RefreshLists();
                Notify();
            }
            catch (Exception ex) { Ui.LogError(ex, "切换桌面清理开关"); }
        }

        void SaveExisting()
        {
            try
            {
                settings.DesktopCleanExisting = chkExisting.IsChecked == true;
                settings.Save();
                Notify();
            }
            catch (Exception ex) { Ui.LogError(ex, "保存子选项"); }
        }

        // ========== 保留名单 ==========

        void RefreshKeepList()
        {
            try
            {
                keepList.Children.Clear();

                List<string> list = settings.DesktopKeepTargets ?? new List<string>();
                if (list.Count == 0)
                {
                    keepList.Children.Add(Ui.T("（空）", 11, Ui.Sub));
                    return;
                }

                foreach (string key in list)
                {
                    keepList.Children.Add(MakeKeepRow(key));
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
            Border del = SmallButton("删除", delegate
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
                    "输入目标路径（或 name:文件名.url）：", "");
                if (string.IsNullOrWhiteSpace(s)) return;

                string key = s.Trim();
                if (!key.StartsWith(DesktopKeep.NamePrefix, StringComparison.OrdinalIgnoreCase))
                    key = DesktopKeep.NormalizeKey(key);

                if (string.IsNullOrEmpty(key)) return;

                DesktopKeep.AddKey(settings.DesktopKeepTargets, key);
                settings.Save();
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

            Border restore = SmallButton("恢复", delegate { RestoreOne(d); });
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
                DesktopKeep.Add(settings.DesktopKeepTargets, path);
                settings.Save();

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

                pendingText.Text = "有 " + n + " 个公共桌面快捷方式需要管理员权限，点此处理";
                pendingBox.Visibility = Visibility.Visible;
            }
            catch { }
        }

        void ProcessPending()
        {
            try
            {
                int n = settings.DesktopPendingAdmin == null ? 0 : settings.DesktopPendingAdmin.Count;
                if (n == 0) return;

                // ★ 提权删除是永久删除，必须写明；默认焦点在「取消」
                string msg =
                    "将以管理员身份永久删除 " + n + " 项。\r\n\r\n"
                    + "注意：提权删除将永久删除，不进回收站。";

                MessageBoxResult r = MessageBox.Show(dlg, msg,
                    "四叶草 · 需要管理员权限",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                    MessageBoxResult.Cancel);   // ★ 默认焦点在「取消」

                if (r != MessageBoxResult.OK) return;

                if (watcher == null || watcher.DesktopCleaner == null)
                {
                    MessageBox.Show(dlg, "桌面清理器未初始化。", "四叶草",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int failed;
                int ok = watcher.DesktopCleaner.ProcessPendingAdmin(out failed);

                RefreshLists();
                Notify();

                if (failed > 0)
                {
                    MessageBox.Show(dlg,
                        "提权删除完成 " + ok + " 项，失败 " + failed + " 项。",
                        "四叶草 · 删除", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "处理待提权项"); }
        }

        // ========== 通用 ==========

        void RefreshLists()
        {
            RefreshKeepList();
            RefreshRemovedList();
            RefreshPending();
        }

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

        static Border SmallButton(string label, Action onClick)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(10);
            b.Padding = new Thickness(10, 2, 10, 2);
            b.Cursor = System.Windows.Input.Cursors.Hand;
            b.Background = Ui.SearchBg;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(1);

            TextBlock t = Ui.T(label, 10.5, Ui.Fg);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = Ui.SearchBg; };
            b.MouseLeftButtonUp += delegate
            {
                try { if (onClick != null) onClick(); }
                catch (Exception ex) { Ui.LogError(ex, "设置按钮"); }
            };

            return b;
        }

    }
}
