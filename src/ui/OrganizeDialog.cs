using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Clover
{
    /// <summary>
    /// 「整理与隐藏」窗口外壳：两个标签页。
    ///
    /// ── 为什么合并 ──
    ///
    /// 「桌面快捷方式」和「已隐藏的应用」都是"清理掉不想看的东西"，
    /// 分成两个窗口各占一个菜单项，菜单越来越长，用户也记不住哪个在哪个。
    /// 合并成一个窗口、两个标签，**数据和逻辑完全独立**，只共用外壳。
    ///
    /// ── 标签 ──
    ///
    ///   1. 桌面快捷方式（原 DesktopCleanDialog 的内容）
    ///   2. 已隐藏的应用（原 HiddenItemsDialog 的内容）
    ///
    /// ── 焦点 ──
    ///
    /// 关闭后不抢主面板焦点、不触发「点击面板外自动收起」。
    /// </summary>
    class OrganizeDialog
    {
        /// <summary>标签索引。</summary>
        public enum Tab
        {
            Desktop = 0,
            Hidden = 1,
        }

        readonly string baseDir;
        readonly Window dlg;
        readonly Settings settings;
        readonly AppWatcher watcher;
        readonly PanelWindow panel;
        readonly Action onChanged;

        Tab current = Tab.Desktop;

        Border tabDesktopBtn;
        Border tabHiddenBtn;
        ContentControl pageHost;

        DesktopCleanPage desktopPage;
        HiddenAppsPage hiddenPage;

        /// <summary>
        /// 构造。
        /// </summary>
        /// <param name="initialTab">打开时定位到哪个标签</param>
        public OrganizeDialog(Window owner, string baseDir, Settings settings, AppWatcher watcher,
            PanelWindow panel, Action onChanged, Tab initialTab)
        {
            this.baseDir = baseDir;
            this.settings = settings;
            this.watcher = watcher;
            this.panel = panel;
            this.onChanged = onChanged;
            this.current = initialTab;

            dlg = new Window();
            dlg.Title = "四叶草 · 整理与隐藏";
            dlg.Width = 560;
            dlg.Height = 700;
            dlg.WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen;
            dlg.Owner = owner;
            dlg.Background = Ui.Bg;
            dlg.ResizeMode = ResizeMode.CanResize;
            // 不占任务栏（这是配置对话框，一闪而过），所以自绘标题栏不会
            // 提供最小化入口；否则最小化后任务栏没有图标，窗口无法找回。
            dlg.ShowInTaskbar = false;
            dlg.MinWidth = 460;
            dlg.MinHeight = 480;

            // 位置记忆：有记录就恢复（此时不要再 CenterOwner），没记录保持居中
            if (WindowPos.Attach(baseDir, WindowPos.Organize, dlg))
                dlg.WindowStartupLocation = WindowStartupLocation.Manual;

            BuildShell();
        }

        void BuildShell()
        {
            Grid layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition());   // 标签栏
            layout.RowDefinitions.Add(new RowDefinition());   // 内容
            layout.RowDefinitions[0].Height = GridLength.Auto;
            layout.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);

            // ---- 标签栏 ----
            StackPanel tabs = new StackPanel();
            tabs.Orientation = Orientation.Horizontal;
            tabs.Margin = new Thickness(Ui.S.Xl, Ui.S.Lg, Ui.S.Xl, 0);

            tabDesktopBtn = MakeTab("桌面快捷方式", Tab.Desktop);
            tabs.Children.Add(tabDesktopBtn);

            tabHiddenBtn = MakeTab("已隐藏的应用", Tab.Hidden);
            tabHiddenBtn.Margin = new Thickness(Ui.S.Xs, 0, 0, 0);
            tabs.Children.Add(tabHiddenBtn);

            Grid.SetRow(tabs, 0);
            layout.Children.Add(tabs);

            // ---- 内容 ----
            pageHost = new ContentControl();
            pageHost.Margin = new Thickness(0, Ui.S.Md, 0, 0);
            Grid.SetRow(pageHost, 1);
            layout.Children.Add(pageHost);

            // 原生标题栏即使清掉 WS_MINIMIZEBOX，在部分 Windows 主题下仍会
            // 留下截图中的灰色“—”占位。改用只含最大化/还原与关闭的标题栏。
            DialogChrome.UseMaximizeCloseTitleBar(dlg, layout);

            // Esc：先清搜索，再关闭（不影响主面板）
            dlg.PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key != Key.Escape) return;

                try
                {
                    if (hiddenPage != null && hiddenPage.HandleEscape()) { e.Handled = true; return; }
                }
                catch { }

                dlg.Close();
                e.Handled = true;
            };

            SwitchTab(current);
        }

        Border MakeTab(string label, Tab tab)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(Ui.R.Sm);
            b.Padding = new Thickness(14, 6, 14, 6);
            b.Cursor = Cursors.Hand;
            b.Background = Brushes.Transparent;
            b.BorderThickness = new Thickness(0, 0, 0, 2);
            b.BorderBrush = Brushes.Transparent;
            b.Tag = tab;

            TextBlock t = Ui.T(label, 12.5, Ui.Sub);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            b.MouseEnter += delegate
            {
                try
                {
                    if ((Tab)b.Tag != current) b.Background = Ui.Hover;
                }
                catch { }
            };
            b.MouseLeave += delegate
            {
                try
                {
                    if ((Tab)b.Tag != current) b.Background = Brushes.Transparent;
                }
                catch { }
            };
            b.MouseLeftButtonUp += delegate
            {
                try { SwitchTab((Tab)b.Tag); }
                catch (Exception ex) { Ui.LogError(ex, "切换整理与隐藏标签"); }
            };

            return b;
        }

        /// <summary>切到指定标签。</summary>
        public void SwitchTab(Tab tab)
        {
            try
            {
                current = tab;

                // 标签外观
                UpdateTabLook(tabDesktopBtn, Tab.Desktop);
                UpdateTabLook(tabHiddenBtn, Tab.Hidden);

                // 内容（懒创建，只建一次）
                if (tab == Tab.Desktop)
                {
                    if (desktopPage == null)
                        desktopPage = new DesktopCleanPage(dlg, settings, watcher, Notify);
                    pageHost.Content = desktopPage.Content;
                }
                else
                {
                    if (hiddenPage == null)
                        hiddenPage = new HiddenAppsPage(dlg, panel, settings, Notify);
                    pageHost.Content = hiddenPage.Content;
                    hiddenPage.OnShown();
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "切换整理与隐藏标签"); }
        }

        void UpdateTabLook(Border b, Tab tab)
        {
            try
            {
                if (b == null) return;

                bool on = (tab == current);
                b.BorderBrush = on ? Ui.Accent : Brushes.Transparent;
                b.Background = Brushes.Transparent;

                TextBlock t = b.Child as TextBlock;
                if (t != null)
                {
                    t.Foreground = on ? Ui.Fg : Ui.Sub;
                    t.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                }
            }
            catch { }
        }

        void Notify()
        {
            try { if (onChanged != null) onChanged(); }
            catch { }
        }

        // ========== 测试辅助（生产代码不调）==========
        //
        // AcceptanceTest 要验证"切标签不会把宿主窗口的内容搞丢"，
        // 但 dlg / pageHost 都是 private。加这几个 internal 访问器，
        // 比反射稳（反射遇到重命名就静默失效）。

        /// <summary>测试用：宿主窗口。</summary>
        internal Window TestWindow
        {
            get { return dlg; }
        }

        /// <summary>测试用：内容容器。</summary>
        internal ContentControl TestPageHost
        {
            get { return pageHost; }
        }

        /// <summary>测试用：当前标签。</summary>
        internal Tab TestCurrentTab
        {
            get { return current; }
        }

        /// <summary>测试用：外壳根元素是不是 Grid（没被页面覆盖）。</summary>
        internal bool TestShellIntact()
        {
            try
            {
                Grid g = FindShellGrid(dlg.Content);
                if (g == null) return false;

                // pageHost 必须真的挂在这棵树上
                if (pageHost == null) return false;
                if (!g.Children.Contains(pageHost)) return false;

                // 标签栏也还在（Grid 至少 2 个子元素：tabs + pageHost）
                if (g.Children.Count < 2) return false;

                return true;
            }
            catch { return false; }
        }

        /// <summary>从「标题栏外壳 → 内容」结构里取回真正的页面外壳。</summary>
        static Grid FindShellGrid(object content)
        {
            try
            {
                Grid direct = content as Grid;
                if (direct != null) return direct;

                Border outer = content as Border;
                Grid chrome = outer == null ? null : outer.Child as Grid;
                if (chrome == null || chrome.Children.Count < 2) return null;
                return chrome.Children[1] as Grid;
            }
            catch { return null; }
        }

        /// <summary>测试用：pageHost 是否挂在视觉树上。</summary>
        internal bool TestPageHostInTree()
        {
            try
            {
                if (pageHost == null) return false;
                return System.Windows.Media.VisualTreeHelper.GetParent(pageHost) != null;
            }
            catch { return false; }
        }

        /// <summary>测试用：当前页面内容是否非空。</summary>
        internal bool TestPageHasContent()
        {
            try
            {
                if (pageHost == null) return false;
                return pageHost.Content != null;
            }
            catch { return false; }
        }

        /// <summary>测试用：标题栏动作的可访问名称（应只有最大化/还原和关闭）。</summary>
        internal List<string> TestCaptionActions()
        {
            List<string> actions = new List<string>();
            try
            {
                Border outer = dlg.Content as Border;
                Grid chrome = outer == null ? null : outer.Child as Grid;
                DependencyObject titleBar = chrome != null && chrome.Children.Count > 0
                    ? chrome.Children[0] : null;
                CollectCaptionActions(titleBar, actions);
            }
            catch { }
            return actions;
        }

        static void CollectCaptionActions(DependencyObject node, List<string> actions)
        {
            if (node == null) return;

            Button button = node as Button;
            if (button != null)
            {
                string name = System.Windows.Automation.AutomationProperties.GetName(button);
                if (!string.IsNullOrEmpty(name)) actions.Add(name);
            }

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                CollectCaptionActions(VisualTreeHelper.GetChild(node, i), actions);
        }

        public void Show()
        {
            try { DialogChrome.Attach(dlg); dlg.ShowDialog(); }
            catch (Exception ex) { Ui.LogError(ex, "显示整理与隐藏窗口"); }
        }
    }
}
