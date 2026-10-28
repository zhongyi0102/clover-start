using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Clover
{
    /// <summary>
    /// 一个最小的文本输入对话框（用于分类重命名）。
    ///
    /// 为什么不用 Microsoft.VisualBasic 的 Interaction.InputBox：
    /// 那是 WinForms 控件，**不跟随主题**，深色模式下会弹出一个刺眼的浅色框，
    /// 与本项目「所有界面统一配色」的做法冲突。自己画一个反而更短、更一致。
    ///
    /// 复用 Ui 的颜色刷子，所以主题切换后它也是对的。
    /// </summary>
    static class TextPromptDialog
    {
        /// <summary>
        /// 弹出输入框。返回 null 表示用户取消；否则返回输入的文本（可能为空）。
        /// </summary>
        public static string Ask(Window owner, string title, string label, string initial)
        {
            Window dlg = new Window();
            string result = null;

            try
            {
                dlg.WindowStyle = WindowStyle.None;
                dlg.AllowsTransparency = true;
                dlg.Background = Brushes.Transparent;
                dlg.ResizeMode = ResizeMode.NoResize;
                dlg.ShowInTaskbar = false;
                dlg.SizeToContent = SizeToContent.WidthAndHeight;
                dlg.Title = title;
                if (owner != null && owner.IsVisible)
                {
                    dlg.Owner = owner;
                    dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }
                else
                {
                    dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }

                Border root = new Border();
                root.CornerRadius = new CornerRadius(12);
                root.Background = Ui.Bg;
                root.BorderBrush = Ui.Line;
                root.BorderThickness = new Thickness(1);
                root.Margin = new Thickness(12);
                root.Effect = Ui.Shadow(20, 0.25, 4);

                StackPanel sp = new StackPanel();
                sp.Margin = new Thickness(18, 14, 18, 14);

                TextBlock head = Ui.T(label, 13, Ui.Fg, FontWeights.SemiBold);
                head.Margin = new Thickness(0, 0, 0, 10);
                sp.Children.Add(head);

                TextBox box = new TextBox();
                box.Text = initial ?? "";
                box.Width = 260;
                box.Height = 32;
                box.FontFamily = Ui.YaHei;
                box.FontSize = 13;
                box.Foreground = Ui.Fg;
                box.Background = Ui.SearchBg;
                box.BorderBrush = Ui.Line;
                box.BorderThickness = new Thickness(1);
                box.Padding = new Thickness(8, 0, 8, 0);
                box.VerticalContentAlignment = VerticalAlignment.Center;
                sp.Children.Add(box);

                StackPanel buttons = new StackPanel();
                buttons.Orientation = Orientation.Horizontal;
                buttons.HorizontalAlignment = HorizontalAlignment.Right;
                buttons.Margin = new Thickness(0, 14, 0, 0);

                Border cancel = MakeButton("取消", delegate { result = null; dlg.DialogResult = false; });
                Border ok = MakeButton("确定", delegate { result = box.Text; dlg.DialogResult = true; });
                buttons.Children.Add(cancel);
                buttons.Children.Add(ok);
                sp.Children.Add(buttons);

                root.Child = sp;
                dlg.Content = root;

                // 回车 = 确定，Esc = 取消
                box.KeyDown += delegate(object s, KeyEventArgs e)
                {
                    if (e.Key == Key.Enter) { result = box.Text; dlg.DialogResult = true; e.Handled = true; }
                    else if (e.Key == Key.Escape) { result = null; dlg.DialogResult = false; e.Handled = true; }
                };
                dlg.PreviewKeyDown += delegate(object s, KeyEventArgs e)
                {
                    if (e.Key == Key.Escape) { result = null; dlg.DialogResult = false; e.Handled = true; }
                };

                // 无边框窗口要自己支持拖动
                root.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
                {
                    try
                    {
                        DependencyObject d = e.OriginalSource as DependencyObject;
                        if (IsDescendantOf(d, box) || IsDescendantOf(d, ok) || IsDescendantOf(d, cancel)) return;
                        dlg.DragMove();
                    }
                    catch { }
                };

                // 全选，方便直接覆盖输入
                dlg.Loaded += delegate
                {
                    box.Focus();
                    box.SelectAll();
                };

                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("输入对话框失败: " + ex.Message);
                return null;
            }
            finally
            {
                try { dlg.Close(); } catch { }
            }

            return result;
        }

        /// <summary>
        /// 是/否确认框（主题化，跟随深浅色）。
        ///
        /// 为什么不用 MessageBox：WinForms 的 MessageBox **不跟随主题**，
        /// 深色模式下会弹出一个刺眼的浅色框。自己画一个更一致，也更短。
        /// </summary>
        public static bool Confirm(Window owner, string title, string message, string okText = "确定")
        {
            bool result = false;
            Window dlg = new Window();

            try
            {
                dlg.WindowStyle = WindowStyle.None;
                dlg.AllowsTransparency = true;
                dlg.Background = Brushes.Transparent;
                dlg.ResizeMode = ResizeMode.NoResize;
                dlg.ShowInTaskbar = false;
                dlg.SizeToContent = SizeToContent.WidthAndHeight;
                dlg.Title = title;
                if (owner != null && owner.IsVisible)
                {
                    dlg.Owner = owner;
                    dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }
                else
                {
                    dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }

                Border root = new Border();
                root.CornerRadius = new CornerRadius(12);
                root.Background = Ui.Bg;
                root.BorderBrush = Ui.Line;
                root.BorderThickness = new Thickness(1);
                root.Margin = new Thickness(12);
                root.Effect = Ui.Shadow(20, 0.25, 4);

                StackPanel sp = new StackPanel();
                sp.Margin = new Thickness(20, 16, 20, 14);

                TextBlock head = Ui.T(title, 13.5, Ui.Fg, FontWeights.SemiBold);
                head.Margin = new Thickness(0, 0, 0, 10);
                sp.Children.Add(head);

                TextBlock body = Ui.T(message, 12.5, Ui.Fg);
                body.TextWrapping = TextWrapping.Wrap;
                body.MaxWidth = 380;
                sp.Children.Add(body);

                StackPanel buttons = new StackPanel();
                buttons.Orientation = Orientation.Horizontal;
                buttons.HorizontalAlignment = HorizontalAlignment.Right;
                buttons.Margin = new Thickness(0, 16, 0, 0);

                Border cancel = MakeButton("取消", delegate { result = false; dlg.DialogResult = false; });
                Border ok = MakeButton(okText, delegate { result = true; dlg.DialogResult = true; });
                buttons.Children.Add(cancel);
                buttons.Children.Add(ok);
                sp.Children.Add(buttons);

                root.Child = sp;
                dlg.Content = root;

                dlg.PreviewKeyDown += delegate(object s, KeyEventArgs e)
                {
                    if (e.Key == Key.Escape) { result = false; dlg.DialogResult = false; e.Handled = true; }
                };

                root.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
                {
                    try
                    {
                        DependencyObject d = e.OriginalSource as DependencyObject;
                        if (IsDescendantOf(d, ok) || IsDescendantOf(d, cancel)) return;
                        dlg.DragMove();
                    }
                    catch { }
                };

                dlg.Loaded += delegate { try { ok.Focus(); } catch { } };

                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("确认框失败: " + ex.Message);
                return false;
            }
            finally
            {
                try { dlg.Close(); } catch { }
            }

            return result;
        }

        static Border MakeButton(string text, Action onClick)
        {
            Border b = new Border();
            b.Height = 30;
            b.MinWidth = 66;
            b.CornerRadius = new CornerRadius(7);
            b.Margin = new Thickness(6, 0, 0, 0);
            b.Cursor = Cursors.Hand;
            b.Background = Ui.SearchBg;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(1);

            TextBlock t = Ui.T(text, 12.5, Ui.Fg);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = Ui.SearchBg; };
            b.MouseLeftButtonUp += delegate { try { onClick(); } catch { } };
            return b;
        }

        static bool IsDescendantOf(DependencyObject d, DependencyObject parent)
        {
            while (d != null)
            {
                if (d == parent) return true;
                try { d = VisualTreeHelper.GetParent(d); }
                catch { return false; }
            }
            return false;
        }
    }
}
