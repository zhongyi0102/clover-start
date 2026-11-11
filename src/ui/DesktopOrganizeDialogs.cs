using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Clover
{
    /// <summary>
    /// 智能整理规则管理。规则按从上到下的顺序匹配；编辑只改配置，不碰文件。
    /// </summary>
    class DesktopOrganizeRulesDialog
    {
        readonly Window dlg;
        readonly string baseDir;
        readonly Dictionary<string, string> desks;
        readonly DesktopOrganizeRules store;
        readonly StackPanel list;
        readonly TextBlock empty;

        bool changed;

        public DesktopOrganizeRulesDialog(Window owner, string baseDir,
            Dictionary<string, string> desks)
        {
            this.baseDir = baseDir ?? "";
            this.desks = desks ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            store = DesktopOrganizeRules.Load(this.baseDir);

            dlg = new Window();
            dlg.Title = "四叶草 · 整理规则";
            dlg.Width = 760;
            dlg.Height = 590;
            dlg.MinWidth = 600;
            dlg.MinHeight = 430;
            dlg.Owner = owner;
            dlg.WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            dlg.ResizeMode = ResizeMode.CanResize;
            dlg.ShowInTaskbar = false;
            dlg.Background = Ui.Bg;

            Grid shell = new Grid();
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            StackPanel header = new StackPanel();
            header.Margin = new Thickness(Ui.S.Xl, Ui.S.Lg, Ui.S.Xl, Ui.S.Md);

            TextBlock title = Ui.T("智能整理规则", 15, Ui.Fg, FontWeights.SemiBold);
            header.Children.Add(title);

            TextBlock hint = Ui.T(
                "从上到下匹配；多条命中时采用最前一条。执行前仍会逐项预览。",
                11, Ui.Sub);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, Ui.S.Xs, 0, 0);
            header.Children.Add(hint);
            Grid.SetRow(header, 0);
            shell.Children.Add(header);

            StackPanel actions = new StackPanel();
            actions.Orientation = Orientation.Horizontal;
            actions.Margin = new Thickness(Ui.S.Xl, 0, Ui.S.Xl, Ui.S.Md);
            actions.Children.Add(ActionButton("新建规则", true, delegate { AddRule(); }, "新建整理规则"));
            Grid.SetRow(actions, 1);
            shell.Children.Add(actions);

            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.Margin = new Thickness(Ui.S.Xl, 0, Ui.S.Xl, 0);

            list = new StackPanel();
            list.Orientation = Orientation.Vertical;
            empty = Ui.T("还没有规则。新建一条规则后，再从桌面收纳页预览执行。", 11.5, Ui.Sub);
            empty.TextWrapping = TextWrapping.Wrap;
            empty.Margin = new Thickness(0, Ui.S.Lg, 0, 0);
            list.Children.Add(empty);
            scroll.Content = list;
            Grid.SetRow(scroll, 2);
            shell.Children.Add(scroll);

            StackPanel footer = new StackPanel();
            footer.Orientation = Orientation.Horizontal;
            footer.HorizontalAlignment = HorizontalAlignment.Right;
            footer.Margin = new Thickness(Ui.S.Xl, Ui.S.Md, Ui.S.Xl, Ui.S.Lg);
            footer.Children.Add(ActionButton("关闭", false, delegate { dlg.Close(); }, "关闭规则管理"));
            Grid.SetRow(footer, 3);
            shell.Children.Add(footer);

            DialogChrome.UseCloseOnlyTitleBar(dlg, shell);
            dlg.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { dlg.Close(); e.Handled = true; }
            };
            Refresh();
        }

        public bool ShowDialog()
        {
            try
            {
                DialogChrome.Attach(dlg);
                dlg.ShowDialog();
                return changed;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "显示智能整理规则");
                return false;
            }
        }

        internal Window WindowForTest { get { return dlg; } }

        void Refresh()
        {
            list.Children.Clear();
            if (store.Items.Count == 0)
            {
                list.Children.Add(empty);
                return;
            }

            for (int i = 0; i < store.Items.Count; i++)
                list.Children.Add(RuleRow(store.Items[i], i));
        }

        FrameworkElement RuleRow(DesktopOrganizeRule rule, int index)
        {
            Border box = new Border();
            box.Background = Ui.SearchBg;
            box.BorderBrush = Ui.Line;
            box.BorderThickness = new Thickness(1);
            box.CornerRadius = new CornerRadius(Ui.R.Sm);
            box.Padding = new Thickness(Ui.S.Md);
            box.Margin = new Thickness(0, 0, 0, Ui.S.Sm);

            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel text = new StackPanel();
            text.Orientation = Orientation.Vertical;

            StackPanel nameLine = new StackPanel();
            nameLine.Orientation = Orientation.Horizontal;

            CheckBox enabled = new CheckBox();
            enabled.IsChecked = rule.Enabled;
            enabled.VerticalAlignment = VerticalAlignment.Center;
            enabled.ToolTip = "启用或停用这条规则";
            AutomationProperties.SetName(enabled, "启用规则 " + (rule.Name ?? ""));
            enabled.Checked += delegate { rule.Enabled = true; SaveAndRefresh(false); };
            enabled.Unchecked += delegate { rule.Enabled = false; SaveAndRefresh(false); };
            nameLine.Children.Add(enabled);

            TextBlock name = Ui.T((index + 1) + ". " + (rule.Name ?? ""), 12.5, Ui.Fg,
                FontWeights.SemiBold);
            name.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
            nameLine.Children.Add(name);
            text.Children.Add(nameLine);

            TextBlock condition = Ui.T(DesktopOrganizePlanner.ConditionSummary(rule), 10.5, Ui.Sub);
            condition.TextWrapping = TextWrapping.Wrap;
            condition.Margin = new Thickness(24, Ui.S.Xs, 0, 0);
            text.Children.Add(condition);

            TextBlock result = Ui.T(ActionSummary(rule), 10.5, Ui.Sub);
            result.TextWrapping = TextWrapping.Wrap;
            result.Margin = new Thickness(24, 2, 0, 0);
            text.Children.Add(result);

            Grid.SetColumn(text, 0);
            row.Children.Add(text);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.VerticalAlignment = VerticalAlignment.Center;
            buttons.Margin = new Thickness(Ui.S.Md, 0, 0, 0);
            buttons.Children.Add(SmallButton("上移", delegate { Move(index, -1); }, index > 0));
            buttons.Children.Add(SmallButton("下移", delegate { Move(index, 1); }, index + 1 < store.Items.Count));
            buttons.Children.Add(SmallButton("编辑", delegate { EditRule(rule, index); }, true));
            buttons.Children.Add(SmallButton("删除", delegate { DeleteRule(rule, index); }, true));
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);

            box.Child = row;
            return box;
        }

        static string ActionSummary(DesktopOrganizeRule rule)
        {
            string action = DesktopOrganizeRules.ActionText(rule.Action);
            if (rule.Action == DesktopRuleAction.Ignore) return "动作：忽略";
            string kind = rule.TargetKind == DesktopRuleTargetKind.Desk ? "桌面" : "文件夹";
            return "动作：" + action + "到" + kind + "「" + (rule.Target ?? "") + "」";
        }

        void AddRule()
        {
            if (store.Items.Count >= DesktopOrganizeRules.MaxRules)
            {
                MessageBox.Show(dlg, "最多支持 " + DesktopOrganizeRules.MaxRules + " 条规则。",
                    "四叶草 · 整理规则", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DesktopOrganizeRule rule = new DesktopOrganizeRule();
            rule.Id = Guid.NewGuid().ToString("N");
            rule.TargetKind = DesktopRuleTargetKind.Desk;
            foreach (KeyValuePair<string, string> kv in desks) { rule.Target = kv.Key; break; }

            DesktopOrganizeRule edited = DesktopOrganizeRuleEditorDialog.Edit(dlg, rule, desks, true);
            if (edited == null) return;
            store.Items.Add(edited);
            SaveAndRefresh(true);
        }

        void EditRule(DesktopOrganizeRule rule, int index)
        {
            DesktopOrganizeRule edited = DesktopOrganizeRuleEditorDialog.Edit(dlg, rule, desks, false);
            if (edited == null) return;
            store.Items[index] = edited;
            SaveAndRefresh(true);
        }

        void DeleteRule(DesktopOrganizeRule rule, int index)
        {
            if (!TextPromptDialog.Confirm(dlg, "删除规则",
                "删除「" + (rule.Name ?? "") + "」？\r\n\r\n这只删除规则，不会移动或删除任何文件。",
                "删除规则")) return;
            store.Items.RemoveAt(index);
            SaveAndRefresh(true);
        }

        void Move(int index, int offset)
        {
            int target = index + offset;
            if (index < 0 || target < 0 || index >= store.Items.Count || target >= store.Items.Count)
                return;
            DesktopOrganizeRule rule = store.Items[index];
            store.Items.RemoveAt(index);
            store.Items.Insert(target, rule);
            SaveAndRefresh(true);
        }

        void SaveAndRefresh(bool rebuild)
        {
            string error;
            if (!store.Save(out error))
            {
                MessageBox.Show(dlg, "规则未能保存：\r\n" + error,
                    "四叶草 · 整理规则", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            changed = true;
            if (rebuild) Refresh();
        }

        static Border ActionButton(string label, bool primary, Action onClick, string automationName)
        {
            Border button = InteractiveBorder(label, primary, onClick, 32, 14);
            AutomationProperties.SetName(button, automationName);
            return button;
        }

        static Border SmallButton(string label, Action onClick, bool enabled)
        {
            Border button = InteractiveBorder(label, false, onClick, 28, 10);
            button.Margin = new Thickness(Ui.S.Xs, 0, 0, 0);
            button.IsEnabled = enabled;
            button.Opacity = enabled ? 1 : 0.4;
            AutomationProperties.SetName(button, label + "规则");
            return button;
        }

        internal static Border InteractiveBorder(string label, bool primary, Action onClick,
            double height, double horizontalPadding)
        {
            Border b = new Border();
            b.Height = height;
            b.MinWidth = 64;
            b.CornerRadius = new CornerRadius(Ui.R.Sm);
            b.Padding = new Thickness(horizontalPadding, 0, horizontalPadding, 0);
            b.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
            b.Cursor = Cursors.Hand;
            b.Focusable = true;
            b.Background = primary ? Ui.Accent : Ui.SearchBg;
            b.BorderBrush = primary ? Ui.Accent : Ui.Line;
            b.BorderThickness = new Thickness(primary ? 0 : 1);

            TextBlock text = Ui.T(label, 11.5,
                primary ? Brushes.White : Ui.Fg,
                primary ? FontWeights.SemiBold : FontWeights.Normal);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.HorizontalAlignment = HorizontalAlignment.Center;
            b.Child = text;

            b.MouseEnter += delegate { if (b.IsEnabled) b.Opacity = 0.84; };
            b.MouseLeave += delegate { if (b.IsEnabled) b.Opacity = 1; };
            b.GotKeyboardFocus += delegate { if (!primary) b.Background = Ui.AccentSoft; };
            b.LostKeyboardFocus += delegate { if (!primary) b.Background = Ui.SearchBg; };
            b.MouseLeftButtonUp += delegate
            {
                if (b.IsEnabled && onClick != null) onClick();
            };
            b.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (!b.IsEnabled || (e.Key != Key.Enter && e.Key != Key.Space)) return;
                if (onClick != null) onClick();
                e.Handled = true;
            };
            return b;
        }
    }

    /// <summary>单条规则编辑表单。校验在“保存”时一次性展示，不在打开时标红。</summary>
    static class DesktopOrganizeRuleEditorDialog
    {
        sealed class TargetChoice
        {
            public string Name = "";
            public string Path = "";
            public override string ToString() { return Name; }
        }

        public static DesktopOrganizeRule Edit(Window owner, DesktopOrganizeRule source,
            Dictionary<string, string> desks, bool isNew)
        {
            DesktopOrganizeRule result = null;
            DesktopOrganizeRule working = DesktopOrganizeRules.Sanitize(source);
            Dictionary<string, string> deskMap = desks
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            Window dlg = new Window();
            dlg.Title = "四叶草 · " + (isNew ? "新建整理规则" : "编辑整理规则");
            dlg.Width = 600;
            dlg.Height = 650;
            dlg.MinWidth = 520;
            dlg.MinHeight = 520;
            dlg.Owner = owner;
            dlg.WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            dlg.ResizeMode = ResizeMode.CanResize;
            dlg.ShowInTaskbar = false;
            dlg.Background = Ui.Bg;

            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            StackPanel root = new StackPanel();
            root.Margin = new Thickness(Ui.S.Xl);

            TextBlock title = Ui.T(isNew ? "新建整理规则" : "编辑整理规则",
                15, Ui.Fg, FontWeights.SemiBold);
            root.Children.Add(title);
            TextBlock intro = Ui.T("所有条件同时满足才算命中；留空表示不限制。", 11, Ui.Sub);
            intro.Margin = new Thickness(0, Ui.S.Xs, 0, Ui.S.Lg);
            root.Children.Add(intro);

            TextBox name = Input(working.Name);
            AddField(root, "规则名称", name, "例如：截图归档");

            TextBox pattern = Input(working.NamePattern);
            AddField(root, "文件名通配符", pattern,
                "支持 * 和 ?；多个模式用分号分开。例如：Screenshot*;截图*");

            TextBox extensions = Input(working.Extensions);
            AddField(root, "扩展名", extensions,
                "不区分大小写；可写 jpg;png 或 .jpg;.png。文件夹只会命中留空的规则。");

            Grid sizeRow = new Grid();
            sizeRow.ColumnDefinitions.Add(new ColumnDefinition());
            sizeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Ui.S.Md) });
            sizeRow.ColumnDefinitions.Add(new ColumnDefinition());
            TextBox minMb = Input(BytesToMb(working.MinSizeBytes));
            TextBox maxMb = Input(BytesToMb(working.MaxSizeBytes));
            Grid.SetColumn(minMb, 0); sizeRow.Children.Add(minMb);
            Grid.SetColumn(maxMb, 2); sizeRow.Children.Add(maxMb);
            AddField(root, "大小范围（MB）", sizeRow, "左边最小、右边最大；仅应用于文件。");

            Grid dateRow = new Grid();
            dateRow.ColumnDefinitions.Add(new ColumnDefinition());
            dateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Ui.S.Md) });
            dateRow.ColumnDefinitions.Add(new ColumnDefinition());
            TextBox after = Input(working.ModifiedAfter);
            TextBox before = Input(working.ModifiedBefore);
            Grid.SetColumn(after, 0); dateRow.Children.Add(after);
            Grid.SetColumn(before, 2); dateRow.Children.Add(before);
            AddField(root, "修改日期（yyyy-MM-dd）", dateRow, "左边起点、右边终点；两端都包含。");

            ComboBox action = Combo();
            action.Items.Add("移动");
            action.Items.Add("复制");
            action.Items.Add("忽略");
            action.SelectedIndex = working.Action == DesktopRuleAction.Copy ? 1
                : (working.Action == DesktopRuleAction.Ignore ? 2 : 0);
            AddField(root, "动作", action, "忽略规则用于明确保留项目，不做任何文件写入。");

            ComboBox targetKind = Combo();
            targetKind.Items.Add("桌面收纳标签");
            targetKind.Items.Add("本地文件夹");
            targetKind.SelectedIndex = working.TargetKind == DesktopRuleTargetKind.Folder ? 1 : 0;
            AddField(root, "目标类型", targetKind, "桌面标签会跟随存放根目录；本地文件夹使用绝对路径。");

            ComboBox deskTarget = Combo();
            foreach (KeyValuePair<string, string> kv in deskMap)
            {
                TargetChoice choice = new TargetChoice { Name = kv.Key, Path = kv.Value };
                deskTarget.Items.Add(choice);
                if (string.Equals(kv.Key, working.Target, StringComparison.OrdinalIgnoreCase))
                    deskTarget.SelectedItem = choice;
            }
            if (deskTarget.SelectedIndex < 0 && deskTarget.Items.Count > 0) deskTarget.SelectedIndex = 0;

            Grid folderTarget = new Grid();
            folderTarget.ColumnDefinitions.Add(new ColumnDefinition());
            folderTarget.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBox folder = Input(working.TargetKind == DesktopRuleTargetKind.Folder ? working.Target : "");
            Grid.SetColumn(folder, 0); folderTarget.Children.Add(folder);
            Border browse = DesktopOrganizeRulesDialog.InteractiveBorder("选择…", false, delegate
            {
                string picked = PickFolder(folder.Text, dlg);
                if (picked != null) folder.Text = picked;
            }, 32, 12);
            browse.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
            AutomationProperties.SetName(browse, "选择目标文件夹");
            Grid.SetColumn(browse, 1); folderTarget.Children.Add(browse);

            StackPanel targetHost = new StackPanel();
            targetHost.Children.Add(deskTarget);
            targetHost.Children.Add(folderTarget);
            AddField(root, "目标", targetHost, "移动和复制永不覆盖同名项目；预览会显示最终改名后的路径。");

            CheckBox enabled = new CheckBox();
            enabled.Content = "启用这条规则";
            enabled.IsChecked = working.Enabled;
            enabled.Foreground = Ui.Fg;
            enabled.Margin = new Thickness(0, Ui.S.Sm, 0, 0);
            root.Children.Add(enabled);

            TextBlock error = Ui.T("", 11, Ui.Fg);
            error.TextWrapping = TextWrapping.Wrap;
            error.Margin = new Thickness(0, Ui.S.Md, 0, 0);
            error.Visibility = Visibility.Collapsed;
            AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
            root.Children.Add(error);

            StackPanel footer = new StackPanel();
            footer.Orientation = Orientation.Horizontal;
            footer.HorizontalAlignment = HorizontalAlignment.Right;
            footer.Margin = new Thickness(0, Ui.S.Lg, 0, 0);

            Border cancel = DesktopOrganizeRulesDialog.InteractiveBorder("取消", false,
                delegate { dlg.Close(); }, 32, 14);
            Border save = DesktopOrganizeRulesDialog.InteractiveBorder("保存规则", true, null, 32, 14);
            AutomationProperties.SetName(cancel, "取消编辑规则");
            AutomationProperties.SetName(save, "保存整理规则");
            footer.Children.Add(cancel);
            footer.Children.Add(save);
            root.Children.Add(footer);

            Action updateTargetVisibility = delegate
            {
                bool ignore = action.SelectedIndex == 2;
                bool useFolder = targetKind.SelectedIndex == 1;
                targetKind.IsEnabled = !ignore;
                targetKind.Opacity = ignore ? 0.45 : 1;
                deskTarget.Visibility = !ignore && !useFolder ? Visibility.Visible : Visibility.Collapsed;
                folderTarget.Visibility = !ignore && useFolder ? Visibility.Visible : Visibility.Collapsed;
            };
            action.SelectionChanged += delegate { updateTargetVisibility(); };
            targetKind.SelectionChanged += delegate { updateTargetVisibility(); };
            updateTargetVisibility();

            Action doSave = null;
            doSave = delegate
            {
                DesktopOrganizeRule candidate = working.Clone();
                candidate.Name = name.Text;
                candidate.NamePattern = pattern.Text;
                candidate.Extensions = extensions.Text;
                string parseError;
                candidate.MinSizeBytes = ParseMb(minMb.Text, out parseError);
                if (parseError != null) { ShowError(error, "最小大小：" + parseError); minMb.Focus(); return; }
                candidate.MaxSizeBytes = ParseMb(maxMb.Text, out parseError);
                if (parseError != null) { ShowError(error, "最大大小：" + parseError); maxMb.Focus(); return; }
                candidate.ModifiedAfter = after.Text;
                candidate.ModifiedBefore = before.Text;
                candidate.Action = action.SelectedIndex == 1 ? DesktopRuleAction.Copy
                    : (action.SelectedIndex == 2 ? DesktopRuleAction.Ignore : DesktopRuleAction.Move);
                candidate.TargetKind = targetKind.SelectedIndex == 1
                    ? DesktopRuleTargetKind.Folder : DesktopRuleTargetKind.Desk;
                TargetChoice selectedDesk = deskTarget.SelectedItem as TargetChoice;
                candidate.Target = candidate.TargetKind == DesktopRuleTargetKind.Folder
                    ? folder.Text : (selectedDesk == null ? "" : selectedDesk.Name);
                candidate.Enabled = enabled.IsChecked == true;

                string validation = DesktopOrganizeRules.Validate(candidate);
                if (validation != null)
                {
                    ShowError(error, validation);
                    if (string.IsNullOrWhiteSpace(candidate.Name)) name.Focus();
                    return;
                }
                candidate = DesktopOrganizeRules.Sanitize(candidate);
                if (string.IsNullOrEmpty(candidate.Id)) candidate.Id = Guid.NewGuid().ToString("N");
                result = candidate;
                dlg.Close();
            };
            AttachAction(save, doSave);

            scroll.Content = root;
            DialogChrome.UseCloseOnlyTitleBar(dlg, scroll);
            dlg.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { dlg.Close(); e.Handled = true; }
            };
            dlg.Loaded += delegate { name.Focus(); name.SelectAll(); };

            try
            {
                DialogChrome.Attach(dlg);
                dlg.ShowDialog();
            }
            catch (Exception ex) { Ui.LogError(ex, "编辑智能整理规则"); }
            return result;
        }

        static void AddField(Panel root, string label, UIElement control, string hint)
        {
            TextBlock l = Ui.T(label, 11.5, Ui.Fg, FontWeights.SemiBold);
            l.Margin = new Thickness(0, Ui.S.Md, 0, Ui.S.Xs);
            root.Children.Add(l);
            root.Children.Add(control);
            TextBlock h = Ui.T(hint, 10, Ui.Sub);
            h.TextWrapping = TextWrapping.Wrap;
            h.Margin = new Thickness(0, 3, 0, 0);
            root.Children.Add(h);
        }

        static void AttachAction(Border button, Action action)
        {
            button.MouseLeftButtonUp += delegate
            {
                if (button.IsEnabled && action != null) action();
            };
            button.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (!button.IsEnabled || (e.Key != Key.Enter && e.Key != Key.Space)) return;
                if (action != null) action();
                e.Handled = true;
            };
        }

        static TextBox Input(string text)
        {
            TextBox box = new TextBox();
            box.Text = text ?? "";
            box.Height = 32;
            box.FontFamily = Ui.YaHei;
            box.FontSize = 12;
            box.Foreground = Ui.Fg;
            box.Background = Ui.SearchBg;
            box.BorderBrush = Ui.Line;
            box.BorderThickness = new Thickness(1);
            box.Padding = new Thickness(Ui.S.Sm, 0, Ui.S.Sm, 0);
            box.VerticalContentAlignment = VerticalAlignment.Center;
            return box;
        }

        static ComboBox Combo()
        {
            ComboBox box = new ComboBox();
            box.Height = 32;
            box.FontFamily = Ui.YaHei;
            box.FontSize = 12;
            box.Foreground = Ui.Fg;
            box.Background = Ui.SearchBg;
            box.BorderBrush = Ui.Line;
            box.Padding = new Thickness(Ui.S.Sm, 0, Ui.S.Sm, 0);
            return box;
        }

        static string PickFolder(string current, Window owner)
        {
            try
            {
                using (System.Windows.Forms.FolderBrowserDialog pick =
                    new System.Windows.Forms.FolderBrowserDialog())
                {
                    pick.Description = "选择智能整理的目标文件夹";
                    pick.ShowNewFolderButton = true;
                    if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                        pick.SelectedPath = current;
                    return pick.ShowDialog() == System.Windows.Forms.DialogResult.OK
                        ? pick.SelectedPath : null;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "打不开选目录窗口：" + ex.Message,
                    "四叶草 · 整理规则", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
        }

        static long ParseMb(string raw, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(raw)) return -1;
            double mb;
            if (!double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture, out mb)
                && !double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out mb))
            {
                error = "请输入数字，或留空表示不限。";
                return -1;
            }
            if (mb < 0) { error = "不能小于 0。"; return -1; }
            double bytes = mb * 1024d * 1024d;
            if (bytes > long.MaxValue) { error = "数值太大。"; return -1; }
            return (long)Math.Round(bytes);
        }

        static string BytesToMb(long bytes)
        {
            if (bytes < 0) return "";
            return (bytes / 1024d / 1024d).ToString("0.###",
                System.Globalization.CultureInfo.CurrentCulture);
        }

        static void ShowError(TextBlock error, string text)
        {
            error.Text = "无法保存：" + text;
            error.Visibility = Visibility.Visible;
            AutomationProperties.SetHelpText(error, error.Text);
            error.Focusable = true;
            error.Focus();
        }
    }

    /// <summary>整理执行前的逐项预览；只有“移动/复制”行可勾选。</summary>
    class DesktopOrganizePreviewDialog
    {
        readonly Window dlg;
        readonly DesktopOrganizePlan plan;
        readonly TextBlock summary;
        bool confirmed;

        public DesktopOrganizePreviewDialog(Window owner, DesktopOrganizePlan plan,
            int scanSkipped)
        {
            this.plan = plan ?? new DesktopOrganizePlan();
            dlg = new Window();
            dlg.Title = "四叶草 · 智能整理预览";
            dlg.Width = 980;
            dlg.Height = 620;
            dlg.MinWidth = 760;
            dlg.MinHeight = 430;
            dlg.Owner = owner;
            dlg.WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
            dlg.ResizeMode = ResizeMode.CanResize;
            dlg.ShowInTaskbar = false;
            dlg.Background = Ui.Bg;

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            StackPanel header = new StackPanel();
            header.Margin = new Thickness(Ui.S.Xl, Ui.S.Lg, Ui.S.Xl, Ui.S.Sm);
            TextBlock title = Ui.T("检查后再执行", 15, Ui.Fg, FontWeights.SemiBold);
            header.Children.Add(title);
            summary = Ui.T("", 11, Ui.Sub);
            summary.Margin = new Thickness(0, Ui.S.Xs, 0, 0);
            summary.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetLiveSetting(summary, AutomationLiveSetting.Polite);
            header.Children.Add(summary);
            Grid.SetRow(header, 0); root.Children.Add(header);

            TextBlock safety = Ui.T(
                "同名项目会在预览中自动改名，绝不覆盖。预览后源或目标发生变化时，该项会停止并要求重新预览。",
                10.5, Ui.Sub);
            safety.TextWrapping = TextWrapping.Wrap;
            safety.Margin = new Thickness(Ui.S.Xl, 0, Ui.S.Xl, Ui.S.Md);
            if (scanSkipped > 0)
                safety.Text += " 另有 " + scanSkipped + " 个隐藏、系统或链接项目未纳入扫描。";
            Grid.SetRow(safety, 1); root.Children.Add(safety);

            FrameworkElement headings = MakeHeadings();
            Grid.SetRow(headings, 2); root.Children.Add(headings);

            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.Margin = new Thickness(Ui.S.Xl, 0, Ui.S.Xl, 0);
            StackPanel rows = new StackPanel();
            foreach (DesktopOrganizePlanItem item in this.plan.Items)
                rows.Children.Add(MakeRow(item));
            scroll.Content = rows;
            Grid.SetRow(scroll, 3); root.Children.Add(scroll);

            StackPanel footer = new StackPanel();
            footer.Orientation = Orientation.Horizontal;
            footer.HorizontalAlignment = HorizontalAlignment.Right;
            footer.Margin = new Thickness(Ui.S.Xl, Ui.S.Md, Ui.S.Xl, Ui.S.Lg);
            Border cancel = DesktopOrganizeRulesDialog.InteractiveBorder("取消", false,
                delegate { dlg.Close(); }, 32, 14);
            Border execute = DesktopOrganizeRulesDialog.InteractiveBorder("执行选中项", true,
                delegate
                {
                    if (SelectedCount() <= 0) return;
                    confirmed = true;
                    dlg.Close();
                }, 32, 14);
            AutomationProperties.SetName(cancel, "取消智能整理");
            AutomationProperties.SetName(execute, "执行选中的整理项目");
            footer.Children.Add(cancel);
            footer.Children.Add(execute);
            Grid.SetRow(footer, 4); root.Children.Add(footer);

            DialogChrome.UseCloseOnlyTitleBar(dlg, root);
            dlg.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { dlg.Close(); e.Handled = true; }
            };
            dlg.Loaded += delegate { cancel.Focus(); };
            RefreshSummary();
        }

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
                Ui.LogError(ex, "显示智能整理预览");
                return false;
            }
        }

        internal Window WindowForTest { get { return dlg; } }

        FrameworkElement MakeHeadings()
        {
            Grid row = MakeColumns();
            row.Margin = new Thickness(Ui.S.Xl, 0, Ui.S.Xl, Ui.S.Xs);
            AddCell(row, "执行", 0, true);
            AddCell(row, "来源", 1, true);
            AddCell(row, "命中规则", 2, true);
            AddCell(row, "动作", 3, true);
            AddCell(row, "最终位置 / 状态", 4, true);
            return row;
        }

        FrameworkElement MakeRow(DesktopOrganizePlanItem item)
        {
            Border border = new Border();
            border.BorderBrush = Ui.Line;
            border.BorderThickness = new Thickness(0, 1, 0, 0);
            border.Padding = new Thickness(0, Ui.S.Sm, 0, Ui.S.Sm);

            Grid row = MakeColumns();
            CheckBox choose = new CheckBox();
            choose.IsChecked = item.Selected;
            choose.IsEnabled = item.Executable;
            choose.Opacity = item.Executable ? 1 : 0.4;
            choose.HorizontalAlignment = HorizontalAlignment.Center;
            choose.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(choose, "执行 " + item.Name);
            choose.Checked += delegate { item.Selected = true; RefreshSummary(); };
            choose.Unchecked += delegate { item.Selected = false; RefreshSummary(); };
            Grid.SetColumn(choose, 0); row.Children.Add(choose);

            StackPanel source = new StackPanel();
            TextBlock name = Ui.T(item.Name, 11.5, Ui.Fg, FontWeights.SemiBold);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.ToolTip = item.Source;
            source.Children.Add(name);
            TextBlock sourcePath = Ui.T(item.SourceLabel + " · " + item.Source, 9.5, Ui.Sub);
            sourcePath.TextTrimming = TextTrimming.CharacterEllipsis;
            sourcePath.ToolTip = item.Source;
            source.Children.Add(sourcePath);
            Grid.SetColumn(source, 1); row.Children.Add(source);

            string rules = item.MatchedRules.Count == 0
                ? "—" : string.Join("、", item.MatchedRules.ToArray());
            TextBlock ruleText = Ui.T(rules, 10.5, item.MatchedRules.Count == 0 ? Ui.Sub : Ui.Fg);
            ruleText.TextWrapping = TextWrapping.Wrap;
            ruleText.ToolTip = rules;
            Grid.SetColumn(ruleText, 2); row.Children.Add(ruleText);

            TextBlock action = Ui.T(ActionText(item), 10.5,
                item.Executable ? Ui.AccentText : Ui.Sub,
                item.Executable ? FontWeights.SemiBold : FontWeights.Normal);
            Grid.SetColumn(action, 3); row.Children.Add(action);

            string destination = item.Executable ? item.TargetPath : StateText(item);
            if (!string.IsNullOrWhiteSpace(item.Reason))
                destination += (destination.Length == 0 ? "" : "\r\n") + item.Reason;
            TextBlock target = Ui.T(destination, 10.5, item.Executable ? Ui.Fg : Ui.Sub);
            target.TextWrapping = TextWrapping.Wrap;
            target.ToolTip = item.Executable ? item.TargetPath : item.Reason;
            Grid.SetColumn(target, 4); row.Children.Add(target);

            border.Child = row;
            return border;
        }

        static Grid MakeColumns()
        {
            Grid grid = new Grid();
            grid.MinWidth = 900;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return grid;
        }

        static void AddCell(Grid row, string text, int column, bool heading)
        {
            TextBlock cell = Ui.T(text, 10.5, Ui.Sub,
                heading ? FontWeights.SemiBold : FontWeights.Normal);
            cell.Margin = new Thickness(Ui.S.Xs, 0, Ui.S.Xs, 0);
            Grid.SetColumn(cell, column);
            row.Children.Add(cell);
        }

        static string ActionText(DesktopOrganizePlanItem item)
        {
            if (item.State == DesktopPlanState.Ignored) return "忽略";
            if (!item.Executable) return "不执行";
            return DesktopOrganizeRules.ActionText(item.Action);
        }

        static string StateText(DesktopOrganizePlanItem item)
        {
            switch (item.State)
            {
                case DesktopPlanState.Ignored: return "保留原位";
                case DesktopPlanState.Unmatched: return "未匹配";
                case DesktopPlanState.InvalidTarget: return "目标不可用";
                case DesktopPlanState.Missing: return "源项目不存在";
                default: return "不执行";
            }
        }

        int SelectedCount()
        {
            int count = 0;
            foreach (DesktopOrganizePlanItem item in plan.Items)
                if (item != null && item.Executable && item.Selected) count++;
            return count;
        }

        void RefreshSummary()
        {
            int selected = SelectedCount();
            int nonExecutable = plan.Items.Count - plan.ReadyCount;
            summary.Text = "已选择 " + selected + " 项；可执行 " + plan.ReadyCount
                + " 项；忽略、未匹配或不可执行 " + nonExecutable + " 项。";
        }
    }
}
