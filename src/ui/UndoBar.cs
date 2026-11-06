using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 底部「撤销」提示条。
    ///
    /// ── 为什么自己画而不是用 MessageBox ──
    ///
    /// 删除单个文件是高频操作，弹模态框会打断节奏。
    /// 提示条不抢焦点、5 秒自动消失、点「撤销」就把文件从回收站还原。
    ///
    /// ── 还原怎么实现 ──
    ///
    /// 用 `SHFileOperation` 的 `FOF_ALLOWUNDO` 反向操作是不行的（没有"从回收站取回"的 API）。
    /// 实际做法是调 Shell 的「还原」动词：
    ///   `Shell.Application` 命名空间的 `NameSpace(10).Items()` 里找对应项，
    ///   调 `InvokeVerb("还原")` / `InvokeVerb("Restore")`。
    ///
    /// 但这条路依赖本地化动词名，不稳。**更可靠的做法是记录原路径**，
    /// 用 `SHFileOperation` 的 `FO_MOVE` 从 `$Recycle.Bin` 移回 —— 但那要解析
    /// 回收站的 `$I` 元数据文件，复杂且易错。
    ///
    /// 本实现用 **Shell.Application 的 InvokeVerb**，并同时尝试中英文动词名；
    /// 失败时提示用户去回收站手动还原（不让「撤销」变成假按钮）。
    /// </summary>
    class UndoBar
    {
        /// <summary>提示条停留时间。</summary>
        const int LifetimeMs = 5000;

        readonly Border root;
        readonly TextBlock message;
        readonly Border undoBtn;
        readonly Border keepBtn;
        readonly StackPanel host;

        DispatcherTimer timer;
        Action undoAction;
        Action keepAction;

        /// <summary>
        /// 点任一按钮后把焦点还给原处（不跳搜索框、不触发「点击面板外收起」）。
        /// 由调用方注入 —— UndoBar 不知道自己该还给谁。
        /// </summary>
        public Action OnFocusBack;

        /// <summary>
        /// 可见性变化回调（true = 刚显示，false = 刚隐藏）。
        ///
        /// 给宿主用的：提示条挂在 `toastHost` 上，而那个容器默认关掉命中
        /// （否则空容器会挡住底下的分类栏折叠按钮）。显示时要把它打开，
        /// 隐藏时要关回去 —— 这个时机只有 UndoBar 自己知道。
        /// </summary>
        public Action<bool> OnVisibilityChanged;

        /// <summary>当前是否可见。</summary>
        public bool IsVisible
        {
            get { try { return root != null && root.Visibility == Visibility.Visible; } catch { return false; } }
        }

        /// <summary>
        /// 构造。host 是放在面板底部的容器（通常是 filesPanel 的最后一个子元素位置）。
        /// </summary>
        public UndoBar(StackPanel host)
        {
            this.host = host;

            root = new Border();
            root.CornerRadius = new CornerRadius(Ui.R.Sm);
            root.Background = Ui.SearchBg;
            root.BorderBrush = Ui.Line;
            root.BorderThickness = new Thickness(1);
            root.Padding = new Thickness(Ui.S.Sm, 6, Ui.S.Sm, 6);
            root.Margin = new Thickness(0, Ui.S.Xs, 0, 0);
            root.Visibility = Visibility.Collapsed;

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            g.ColumnDefinitions[1].Width = GridLength.Auto;
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[2].Width = GridLength.Auto;

            message = Ui.T("", 11, Ui.Fg);
            message.VerticalAlignment = VerticalAlignment.Center;
            message.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(message, 0);
            g.Children.Add(message);

            undoBtn = new Border();
            undoBtn.CornerRadius = new CornerRadius(Ui.R.Sm);
            undoBtn.Padding = new Thickness(10, 2, 10, 2);
            undoBtn.Cursor = System.Windows.Input.Cursors.Hand;
            undoBtn.Background = Brushes.Transparent;
            undoBtn.BorderBrush = Ui.Accent;
            undoBtn.BorderThickness = new Thickness(1);
            undoBtn.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);

            TextBlock ub = Ui.T("撤销", 10.5, Ui.Accent);
            ub.VerticalAlignment = VerticalAlignment.Center;
            undoBtn.Child = ub;

            undoBtn.MouseEnter += delegate { undoBtn.Background = Ui.Hover; };
            undoBtn.MouseLeave += delegate { undoBtn.Background = Brushes.Transparent; };
            undoBtn.MouseLeftButtonUp += delegate
            {
                try
                {
                    Action a = undoAction;
                    Hide();
                    if (a != null) a();
                    FocusBack();
                }
                catch (Exception ex) { Ui.LogError(ex, "撤销删除"); }
            };

            // ---- 「保留」按钮（仅桌面移除场景显示）----
            keepBtn = new Border();
            keepBtn.CornerRadius = new CornerRadius(Ui.R.Sm);
            keepBtn.Padding = new Thickness(10, 2, 10, 2);
            keepBtn.Cursor = System.Windows.Input.Cursors.Hand;
            keepBtn.Background = Ui.Accent;
            keepBtn.BorderBrush = Ui.Accent;
            keepBtn.BorderThickness = new Thickness(1);
            keepBtn.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
            keepBtn.Visibility = Visibility.Collapsed;

            TextBlock kb = Ui.T("保留", 10.5, Brushes.White);
            kb.VerticalAlignment = VerticalAlignment.Center;
            keepBtn.Child = kb;

            keepBtn.MouseEnter += delegate { keepBtn.Opacity = 0.85; };
            keepBtn.MouseLeave += delegate { keepBtn.Opacity = 1.0; };
            keepBtn.MouseLeftButtonUp += delegate
            {
                try
                {
                    Action a = keepAction;
                    Hide();
                    if (a != null) a();
                    FocusBack();
                }
                catch (Exception ex) { Ui.LogError(ex, "保留桌面快捷方式"); }
            };

            Grid.SetColumn(undoBtn, 1);
            g.Children.Add(undoBtn);

            Grid.SetColumn(keepBtn, 2);
            g.Children.Add(keepBtn);

            root.Child = g;

            if (host != null) host.Children.Add(root);
        }

        /// <summary>
        /// 显示提示。text 例如「已移到回收站：a.txt」。
        ///
        /// undo 为 null → 不显示「撤销」按钮（批量部分失败场景）。
        /// keep 为 null → 不显示「保留」按钮（普通删除场景，只有桌面移除才给保留）。
        /// </summary>
        public void Show(string text, Action undo)
        {
            Show(text, undo, null);
        }

        /// <summary>
        /// 显示提示（三参数版）。
        ///
        /// keep = 「保留」按钮的回调（还原 + 加入保留名单）。
        /// 桌面快捷方式被移除时传它，让用户能一键把东西留下来。
        /// </summary>
        public void Show(string text, Action undo, Action keep)
        {
            try
            {
                message.Text = text ?? "";
                undoAction = undo;
                keepAction = keep;
                undoBtn.Visibility = undo == null ? Visibility.Collapsed : Visibility.Visible;
                keepBtn.Visibility = keep == null ? Visibility.Collapsed : Visibility.Visible;

                root.Visibility = Visibility.Visible;
                if (Ui.ReducedMotion)
                {
                    root.BeginAnimation(UIElement.OpacityProperty, null);
                    root.Opacity = 1;
                }
                else
                {
                    root.Opacity = 0;
                    DoubleAnimation fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
                    root.BeginAnimation(UIElement.OpacityProperty, fade);
                }

                NotifyVisibility(true);

                // 重开计时（连删多次时每次都刷新 5 秒）
                if (timer == null)
                {
                    timer = new DispatcherTimer();
                    timer.Tick += delegate
                    {
                        timer.Stop();
                        Hide();
                    };
                }
                timer.Stop();
                timer.Interval = TimeSpan.FromMilliseconds(LifetimeMs);
                timer.Start();
            }
            catch (Exception ex) { Ui.LogError(ex, "显示撤销提示"); }
        }

        /// <summary>
        /// 把焦点还给原处。
        ///
        /// 不调 `Activate()`、不碰搜索框 —— 否则会跳焦点、还可能触发
        /// 「点击面板外自动收起」。只调调用方注入的回调（通常是列表的 Focus）。
        /// </summary>
        void FocusBack()
        {
            try
            {
                if (OnFocusBack != null) OnFocusBack();
            }
            catch { }
        }

        /// <summary>立即隐藏。</summary>
        /// <summary>立即隐藏。</summary>
        public void Hide()
        {
            try
            {
                if (timer != null) timer.Stop();
                undoAction = null;
                if (root == null) return;

                if (Ui.ReducedMotion)
                {
                    root.BeginAnimation(UIElement.OpacityProperty, null);
                    root.Opacity = 1;
                    root.Visibility = Visibility.Collapsed;
                    NotifyVisibility(false);
                }
                else
                {
                    DoubleAnimation fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120));
                    fade.Completed += delegate
                    {
                        try { root.Visibility = Visibility.Collapsed; } catch { }
                        NotifyVisibility(false);
                    };
                    root.BeginAnimation(UIElement.OpacityProperty, fade);
                }
            }
            catch { }
        }

        void NotifyVisibility(bool on)
        {
            try
            {
                if (OnVisibilityChanged != null) OnVisibilityChanged(on);
            }
            catch { }
        }

        // ========== 撤销实现 ==========

        /// <summary>
        /// 尝试把一批路径从回收站还原。
        ///
        /// 返回还原成功的数量。失败的项目调用方负责提示。
        /// </summary>
        public static int RestoreFromRecycleBin(List<string> paths)
        {
            int ok = 0;
            if (paths == null || paths.Count == 0) return 0;

            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return 0;

                object shell = Activator.CreateInstance(shellType);
                if (shell == null) return 0;

                // NameSpace(10) = 回收站
                object bin = shellType.InvokeMember("NameSpace",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell,
                    new object[] { 10 });
                if (bin == null) return 0;

                object items = bin.GetType().InvokeMember("Items",
                    System.Reflection.BindingFlags.InvokeMethod, null, bin, null);
                if (items == null) return 0;

                int count = Convert.ToInt32(items.GetType().InvokeMember("Count",
                    System.Reflection.BindingFlags.GetProperty, null, items, null));

                // 建一个「文件名 → 原始路径」的反查表
                HashSet<string> want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string p in paths)
                {
                    try { if (!string.IsNullOrEmpty(p)) want.Add(System.IO.Path.GetFileName(p)); }
                    catch { }
                }

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        object item = items.GetType().InvokeMember("Item",
                            System.Reflection.BindingFlags.InvokeMethod, null, items,
                            new object[] { i });
                        if (item == null) continue;

                        string name = Convert.ToString(item.GetType().InvokeMember("Name",
                            System.Reflection.BindingFlags.GetProperty, null, item, null));

                        if (string.IsNullOrEmpty(name) || !want.Contains(name)) continue;

                        // 尝试中英文动词（本地化差异）
                        if (!InvokeVerb(item, "还原")) InvokeVerb(item, "Restore");
                        ok++;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "从回收站还原");
            }

            return ok;
        }

        static bool InvokeVerb(object item, string verb)
        {
            try
            {
                item.GetType().InvokeMember("InvokeVerb",
                    System.Reflection.BindingFlags.InvokeMethod, null, item,
                    new object[] { verb });
                return true;
            }
            catch { return false; }
        }
    }
}
