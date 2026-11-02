using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Clover
{
    /// <summary>
    /// 「更改桌面存放目录」确认对话框：三选一，**默认是「搬过去」**。
    ///
    /// ── 为什么不用 MessageBox ──
    ///
    /// 三个选项，MessageBox 最多两个按钮，且无法把「会搬多少个文件」写清楚。
    /// 这里必须在用户按下之前就把数量摊开：搬 3 个文件和搬 3000 个文件，
    /// 是完全不同的决定。
    ///
    /// ── 为什么默认是「搬过去」而不是「取消」──
    ///
    /// 与 `DeskDeleteDialog`（默认取消）**刻意不同**：那次是删标签、动的是
    /// 用户的文件去向，误按代价高。这次用户已经在设置里主动点了「更改…」
    /// 并选好了目标目录，意图明确；此时默认「只改指针」会把界面变成空的，
    /// 反而像文件丢了。所以默认给最符合意图的那一项，并在提示里写清
    /// 「全部核对成功才切换，失败自动搬回」。
    ///
    /// Esc 仍然是取消 —— 这是键盘上的逃生门，不参与「默认」的取舍。
    /// </summary>
    static class DeskRootDialog
    {
        /// <summary>
        /// 默认选项。抽成常量而不是散落在构建代码里 —— 测试要断言
        /// 「默认是搬过去」，而对话框本身没法在无人值守环境里弹出来。
        /// </summary>
        public const Choice DefaultChoice = Choice.Migrate;

        /// <summary>用户的选择。</summary>
        public enum Choice
        {
            /// <summary>取消（含直接关窗 / Esc）。</summary>
            Cancel = 0,

            /// <summary>把现有桌面搬到新目录，然后切换。</summary>
            Migrate,

            /// <summary>只改指针，磁盘上的文件留在原处。</summary>
            PointerOnly
        }

        /// <summary>
        /// 弹窗询问。
        /// </summary>
        /// <param name="owner">所有者窗口</param>
        /// <param name="oldRoot">当前存放目录</param>
        /// <param name="newRoot">目标存放目录</param>
        /// <param name="plan">盘点结果（几个桌面、几个文件）</param>
        public static Choice Ask(Window owner, string oldRoot, string newRoot,
            DeskRootChange.Plan plan)
        {
            Choice result = Choice.Cancel;

            try
            {
                if (plan == null) plan = new DeskRootChange.Plan();

                Window dlg = new Window();
                dlg.Title = "更改桌面存放目录";
                dlg.Width = 520;
                dlg.Height = 430;
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
                TextBlock title = Ui.T("更改「桌面文件」的存放目录", 14, Ui.Fg, FontWeights.SemiBold);
                root.Children.Add(title);

                // ---- 数量（用户最先要看的）----
                TextBlock count = Ui.T(BuildCountText(plan), 12, Ui.AccentText, FontWeights.SemiBold);
                count.TextWrapping = TextWrapping.Wrap;
                count.Margin = new Thickness(0, Ui.S.Sm, 0, Ui.S.Sm);
                root.Children.Add(count);

                // ---- 路径对照 ----
                root.Children.Add(PathRow("当前", oldRoot));
                root.Children.Add(PathRow("新位置", newRoot));

                // ---- 说明 ----
                TextBlock body = Ui.T(
                    "请选择这些文件怎么处理：", 11.5, Ui.Sub);
                body.TextWrapping = TextWrapping.Wrap;
                body.Margin = new Thickness(0, Ui.S.Sm, 0, Ui.S.Sm);
                root.Children.Add(body);

                // ---- 三个按钮 ----
                StackPanel btns = new StackPanel();
                btns.Orientation = Orientation.Vertical;

                Border bMove = MakeButton(
                    "搬过去（推荐）",
                    "把这 " + plan.DeskCount + " 个桌面、共 " + plan.Files
                        + " 个文件移到新位置。全部核对成功后才切换目录；"
                        + "任何一项失败会自动搬回原处，目录保持不变。",
                    true,
                    delegate { result = Choice.Migrate; dlg.Close(); });
                btns.Children.Add(bMove);

                Border bPointer = MakeButton(
                    "只改指针，不搬文件",
                    "存放目录立即指向新位置，界面暂时是空的；"
                        + "现有文件仍留在上面的「当前」目录里，需要你自己搬。",
                    false,
                    delegate { result = Choice.PointerOnly; dlg.Close(); });
                btns.Children.Add(bPointer);

                Border bCancel = MakeButton("取消", "什么都不做，目录保持不变", false,
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

                // 默认 = 搬过去：焦点落在它上面，Enter / 空格直接确认
                dlg.Loaded += delegate
                {
                    try { bMove.Focus(); } catch { }
                };

                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "更改存放目录对话框");
                result = Choice.Cancel;
            }

            return result;
        }

        /// <summary>
        /// 「将迁移 3 个桌面，共 128 个文件」—— 数量必须具体，不能只说「一些」。
        ///
        /// 公开给测试：这句话是需求里明确要求的「让用户心里有数」，
        /// 而对话框本身在无人值守环境里弹不出来。
        /// </summary>
        public static string BuildCountText(DeskRootChange.Plan plan)
        {
            try
            {
                if (plan == null) return "";
                if (plan.DeskCount == 0)
                    return "当前目录里没有已登记的桌面，切换后新目录会从空开始。";

                string s = "将迁移 " + plan.DeskCount + " 个桌面，共 " + plan.Files + " 个文件";
                if (plan.Files == 0) s += "（都是空文件夹）";
                return s + "。";
            }
            catch { return ""; }
        }

        static Grid PathRow(string label, string path)
        {
            Grid g = new Grid();
            g.Margin = new Thickness(0, 1, 0, 1);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock l = Ui.T(label + "：", 10.5, Ui.Sub);
            l.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(l, 0);
            g.Children.Add(l);

            TextBlock v = Ui.T(string.IsNullOrEmpty(path) ? "（未设置）" : path, 10.5, Ui.Fg);
            v.TextWrapping = TextWrapping.Wrap;
            v.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(v, 1);
            g.Children.Add(v);

            return g;
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
            b.Focusable = true;

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

            // 键盘：焦点在这块上时，Enter / 空格 = 点它
            b.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter || e.Key == Key.Space)
                {
                    if (onClick != null) onClick();
                    e.Handled = true;
                }
            };

            return b;
        }
    }
}
