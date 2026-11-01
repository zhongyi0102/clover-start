using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Clover
{
    /// <summary>
    /// 「删除标签」确认对话框：三个按钮 + 默认焦点在「取消」。
    ///
    /// ── 为什么不用 MessageBox ──
    ///
    /// 需求要三个选项（放回桌面 / 保留 / 取消），MessageBox 最多两个按钮
    /// （YesNoCancel 的文案是英文 Yes/No/Cancel，且无法说明"文件会去哪"）。
    ///
    /// ── 默认焦点在取消 ──
    ///
    /// 删除是不可逆操作（虽然文件进桌面不是删除），误按回车不该执行。
    /// 所以「取消」设 IsDefault 并在显示后主动 Focus。
    /// </summary>
    static class DeskDeleteDialog
    {
        /// <summary>用户的选择。</summary>
        public enum Choice
        {
            /// <summary>取消（含直接关窗）。</summary>
            Cancel = 0,
            /// <summary>文件放回真实桌面。</summary>
            MoveToDesktop,
            /// <summary>保留在存放文件夹。</summary>
            KeepInPlace
        }

        /// <summary>
        /// 弹窗询问。返回用户选择。
        /// </summary>
        /// <param name="owner">所有者窗口</param>
        /// <param name="deskName">标签名</param>
        /// <param name="deskFolder">该标签对应的存放文件夹（用于在文案里显示）</param>
        public static Choice Ask(Window owner, string deskName, string deskFolder)
        {
            Choice result = Choice.Cancel;

            try
            {
                Window dlg = new Window();
                dlg.Title = "删除标签";
                dlg.Width = 460;
                dlg.Height = 260;
                dlg.WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen;
                dlg.Owner = owner;
                dlg.ResizeMode = ResizeMode.NoResize;
                dlg.ShowInTaskbar = false;
                dlg.Background = Ui.Bg;
                dlg.WindowStyle = WindowStyle.SingleBorderWindow;

                StackPanel root = new StackPanel();
                root.Margin = new Thickness(Ui.S.Xl);

                // ---- 标题 ----
                TextBlock title = Ui.T("删除标签「" + deskName + "」", 14, Ui.Fg, FontWeights.SemiBold);
                root.Children.Add(title);

                // ---- 说明 ----
                TextBlock body = Ui.T(
                    "这个标签对应的文件夹里还有文件。请选择这些文件怎么处理：",
                    11.5, Ui.Sub);
                body.TextWrapping = TextWrapping.Wrap;
                body.Margin = new Thickness(0, Ui.S.Sm, 0, Ui.S.Sm);
                root.Children.Add(body);

                // ---- 路径 ----
                TextBlock path = Ui.T(deskFolder ?? "", 10.5, Ui.Sub);
                path.TextWrapping = TextWrapping.Wrap;
                path.Margin = new Thickness(0, 0, 0, Ui.S.Md);
                root.Children.Add(path);

                // ---- 三个按钮 ----
                StackPanel btns = new StackPanel();
                btns.Orientation = Orientation.Vertical;

                Border bMove = MakeButton(
                    "文件放回桌面",
                    "把文件移到桌面，然后删除这个空文件夹",
                    true,
                    delegate { result = Choice.MoveToDesktop; dlg.Close(); });
                btns.Children.Add(bMove);

                Border bKeep = MakeButton(
                    "保留在存放文件夹",
                    "只删掉标签，文件夹和文件都留在原处",
                    false,
                    delegate { result = Choice.KeepInPlace; dlg.Close(); });
                btns.Children.Add(bKeep);

                Border bCancel = MakeButton("取消", "什么都不做", false,
                    delegate { result = Choice.Cancel; dlg.Close(); });
                btns.Children.Add(bCancel);

                root.Children.Add(btns);

                dlg.Content = root;

                // Esc = 取消
                dlg.PreviewKeyDown += delegate(object s, KeyEventArgs e)
                {
                    if (e.Key == Key.Escape)
                    {
                        result = Choice.Cancel;
                        dlg.Close();
                        e.Handled = true;
                    }
                };

                // 显示后把焦点给「取消」
                dlg.Loaded += delegate
                {
                    try { bCancel.Focus(); } catch { }
                };

                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "删除标签对话框");
                result = Choice.Cancel;
            }

            return result;
        }

        static Border MakeButton(string label, string hint, bool primary, Action onClick)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(Ui.R.Sm);
            b.Padding = new Thickness(12, 8, 12, 8);
            b.Margin = new Thickness(0, 0, 0, Ui.S.Xs);
            b.Cursor = Cursors.Hand;
            b.Background = primary ? Ui.Accent : Ui.SearchBg;
            b.BorderBrush = primary ? Ui.Accent : Ui.Line;
            b.BorderThickness = new Thickness(primary ? 0 : 1);

            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Vertical;

            TextBlock t = Ui.T(label, 12,
                primary ? Brushes.White : Ui.Fg,
                primary ? FontWeights.SemiBold : FontWeights.Normal);
            sp.Children.Add(t);

            TextBlock h = Ui.T(hint, 10,
                primary ? Brushes.White : Ui.Sub);
            h.Opacity = primary ? 0.85 : 1.0;
            h.Margin = new Thickness(0, 2, 0, 0);
            h.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(h);

            b.Child = sp;

            b.MouseEnter += delegate { b.Opacity = 0.88; };
            b.MouseLeave += delegate { b.Opacity = 1.0; };
            b.MouseLeftButtonUp += delegate { if (onClick != null) onClick(); };
            return b;
        }
    }
}
