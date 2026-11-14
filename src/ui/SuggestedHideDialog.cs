using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Clover
{
    /// <summary>逐项决定隐藏、暂不处理、保留；窗口关闭或取消不产生任何决定。</summary>
    sealed class SuggestedHideDialog
    {
        readonly Window dlg;
        readonly List<AppHideSuggestion> suggestions;
        readonly List<ComboBox> selectors = new List<ComboBox>();
        readonly Button cancel, confirm;
        readonly TextBlock status;
        bool confirmed;

        internal SuggestedHideDialog(Window owner, List<AppHideSuggestion> candidates)
        {
            suggestions = candidates ?? new List<AppHideSuggestion>();
            dlg = new Window { Title = AppBrand.Text("四叶草 · 建议隐藏"), Width = 600, Height = 620,
                MinWidth = 460, MinHeight = 360, Owner = owner, ShowInTaskbar = false,
                WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
                Background = Ui.Bg, FontFamily = Ui.YaHei };
            Grid layout = new Grid { Margin = new Thickness(Ui.S.Xl, Ui.S.Lg, Ui.S.Xl, Ui.S.Lg) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            StackPanel head = new StackPanel { Margin = new Thickness(0, 0, 0, Ui.S.Md) };
            head.Children.Add(Ui.T("把用不到的配套入口收起来", 16, Ui.Fg, FontWeights.SemiBold));
            TextBlock hint = Ui.T("只隐藏面板入口，不卸载、不删除，不影响脚本和其他软件调用。\n"
                + "未使用的明确配套入口已自动隐藏，可从隐藏管理恢复。\n"
                + "“暂不处理”不改状态；“保留，不再建议”会跳过自动隐藏。", 12, Ui.Sub);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, Ui.S.Sm, 0, 0);
            head.Children.Add(hint);
            layout.Children.Add(head);

            StackPanel rows = new StackPanel();
            if (suggestions.Count == 0)
            {
                TextBlock empty = Ui.T("没有待确认的建议。未知应用、主动使用、固定到常用和保留例外不会列入。", 12, Ui.Sub);
                empty.TextWrapping = TextWrapping.Wrap;
                rows.Children.Add(empty);
            }
            foreach (AppHideSuggestion suggestion in suggestions)
            {
                Grid row = new Grid { Margin = new Thickness(0, 0, 0, Ui.S.Md) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                StackPanel text = new StackPanel { Margin = new Thickness(0, 0, Ui.S.Md, 0) };
                TextBlock name = Ui.T(suggestion.App.Name, 13, Ui.Fg, FontWeights.SemiBold);
                name.TextWrapping = TextWrapping.Wrap;
                name.ToolTip = suggestion.App.Path;
                text.Children.Add(name);
                TextBlock reason = Ui.T(suggestion.Group + " · " + suggestion.Explanation, 11.5, Ui.Sub);
                reason.TextWrapping = TextWrapping.Wrap;
                reason.Margin = new Thickness(0, 4, 0, 0);
                text.Children.Add(reason);
                if (suggestion.App.Usage > 0)
                    text.Children.Add(Ui.T("有启动记录，默认暂不处理。", 11, Ui.Sub));
                row.Children.Add(text);
                ComboBox choice = new ComboBox { VerticalAlignment = VerticalAlignment.Top, MinHeight = 32,
                    FontSize = 11.5, Foreground = SystemColors.ControlTextBrush, Background = SystemColors.WindowBrush };
                choice.Items.Add("隐藏此入口");
                choice.Items.Add("暂不处理");
                choice.Items.Add("保留，不再建议");
                choice.SelectedIndex = suggestion.DefaultHide ? 0 : 1;
                AutomationProperties.SetName(choice, suggestion.App.Name + " 的显示决定");
                AutomationProperties.SetHelpText(choice, suggestion.Explanation);
                choice.SelectionChanged += delegate { UpdateStatus(); };
                selectors.Add(choice);
                Grid.SetColumn(choice, 1);
                row.Children.Add(choice);
                rows.Children.Add(row);
            }
            ScrollViewer scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 1);
            layout.Children.Add(scroll);

            Grid footer = new Grid { Margin = new Thickness(0, Ui.S.Md, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            status = Ui.T("", 11.5, Ui.Sub);
            status.VerticalAlignment = VerticalAlignment.Center;
            status.TextWrapping = TextWrapping.Wrap;
            footer.Children.Add(status);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal };
            cancel = MakeButton("取消", false, delegate { dlg.Close(); });
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(Ui.S.Sm, 0, Ui.S.Sm, 0);
            confirm = MakeButton("确认这些选择", true, delegate { confirmed = true; dlg.Close(); });
            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
            Grid.SetColumn(buttons, 1);
            footer.Children.Add(buttons);
            Grid.SetRow(footer, 2);
            layout.Children.Add(footer);
            dlg.Content = layout;
            dlg.Loaded += delegate { cancel.Focus(); };
            dlg.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { dlg.Close(); e.Handled = true; }
                // 没有默认确认；只有明确聚焦确认按钮才允许回车执行。
                else if (e.Key == Key.Enter && Keyboard.FocusedElement != confirm && Keyboard.FocusedElement != cancel)
                    e.Handled = true;
            };
            UpdateStatus();
        }

        internal static Button MakeButton(string label, bool primary, Action click)
        {
            Button button = new Button { MinHeight = 32, MinWidth = label.Length * 12 + 28,
                Padding = new Thickness(14, 0, 14, 0),
                Background = primary ? Ui.Accent : Ui.SearchBg, BorderBrush = Ui.Line,
                BorderThickness = new Thickness(primary ? 0 : 1), Cursor = Cursors.Hand,
                Template = PanelWindow.MakeButtonTemplate(Ui.R.Sm),
                Content = Ui.T(label, 11.5, primary ? Brushes.White : Ui.Fg) };
            AutomationProperties.SetName(button, label);
            button.GotKeyboardFocus += delegate { button.Opacity = 0.8; button.BorderThickness = new Thickness(2); };
            button.LostKeyboardFocus += delegate { button.Opacity = 1; button.BorderThickness = new Thickness(primary ? 0 : 1); };
            button.Click += delegate { click(); };
            button.IsEnabledChanged += delegate { button.Opacity = button.IsEnabled ? 1 : 0.45; };
            return button;
        }

        void UpdateStatus()
        {
            if (status == null) return;
            int hide = 0, keep = 0;
            foreach (ComboBox selector in selectors)
            {
                if (selector.SelectedIndex == 0) hide++;
                if (selector.SelectedIndex == 2) keep++;
            }
            status.Text = "隐藏 " + hide + " 项 · 保留 " + keep + " 项";
            confirm.IsEnabled = hide + keep > 0;
            confirm.ToolTip = confirm.IsEnabled ? "只应用本次选择" : "先为至少一项选择隐藏或保留";
        }

        internal List<AppHideChoice> Choices()
        {
            List<AppHideChoice> result = new List<AppHideChoice>();
            if (!confirmed) return result;
            for (int i = 0; i < suggestions.Count; i++)
                result.Add(new AppHideChoice { Path = suggestions[i].App.Path,
                    Action = selectors[i].SelectedIndex == 0 ? AppHideAction.Hide
                        : selectors[i].SelectedIndex == 2 ? AppHideAction.Keep : AppHideAction.Skip });
            return result;
        }

        internal bool ShowDialog()
        {
            DialogChrome.Attach(dlg);
            dlg.ShowDialog();
            return confirmed;
        }

        internal Window TestWindow { get { return dlg; } }
        internal List<ComboBox> TestSelectors { get { return selectors; } }
        internal Button TestCancel { get { return cancel; } }
        internal Button TestConfirm { get { return confirm; } }
    }
}
