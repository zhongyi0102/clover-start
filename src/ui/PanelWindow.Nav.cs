using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Clover
{
    /// <summary>左侧分类导航、搜索过滤、应用磁贴渲染与拖拽排序。</summary>
    partial class PanelWindow
    {
        /// <summary>键盘选中的应用磁贴（null 表示没有选中）。</summary>
        Border selectedTile;
        /// <summary>键盘选中项在 renderedApps 中的下标，-1 表示没有选中。</summary>
        int selectedIndex = -1;

        /// <summary>
        /// 悬停时图标放大的比例（纯视觉，不改布局）。
        ///
        /// 1.12 → 1.25：参考 GlassBoard 的 Dock 悬停（它用 1.0→1.6）。
        /// 磁贴是 96px 宽的网格项，不像 Dock 条那样有"把邻居挤开"的空间，
        /// 1.6 会明显盖住相邻磁贴的图标。1.25 是"看得出放大但不越界"的平衡点。
        /// </summary>
        const double IconHoverScale = 1.25;

        /// <summary>
        /// 悬停时图标**上浮**的像素数。
        ///
        /// 这是 Dock 最标志性的移动效果（GlassBoard 用 -5px × MotionLevel）。
        /// 配合放大一起做，才像"图标被拎起来了"；
        /// 只放大不上浮会显得"在原地胀大"，比较闷。
        ///
        /// 用负值是往上（WPF 的 Y 轴向下为正）。
        /// </summary>
        const double IconLiftPx = 4.0;

        /// <summary>
        /// 点击时的**弹跳**高度与时长。
        ///
        /// GlassBoard 的做法：0 → -7px → 0，420ms，
        /// 上去用 EaseOut（快起慢收）、回来用 EaseIn（慢起快收）。
        /// 这里用 -6px / 350ms：幅度略小、时长略短，
        /// 因为磁贴是"启动应用"的高频操作，反馈要快、不能拖。
        /// </summary>
        const double IconBouncePx = 6.0;
        const int IconBounceMs = 350;

        /// <summary>磁贴图标边长。
        ///
        /// 32 → 36：原先图标只占磁贴宽度 33%，偏小、显得空旷；
        /// Windows 开始菜单的图标占比约 40%，36/96 = 38% 更接近那个观感。
        /// 磁贴本身尺寸不变（96x90），所以排版、拖动换位、滚动记忆都不受影响。
        ///
        /// public：验收测试直接引用它来认图标，而不是再硬编码一个 32 ——
        /// 否则每次调尺寸都要同步改测试，很容易漏。
        /// </summary>
        public const double IconSizePx = 36;

        /// <summary>磁贴尺寸（供验收测试引用，避免硬编码漂移）。</summary>
        public const double TileWidthPx = 96;
        public const double TileHeightPx = 90;

        /// <summary>
        /// 图标悬停动画，**冻结后跨所有磁贴复用**。
        ///
        /// 为什么不每块磁贴新建：一次渲染 100+ 块磁贴，每块各建 2 个动画对象
        /// 会让托管堆持续增长（这个项目踩过同样的坑，见 ShowPanel 里复用
        /// showAnim/hideAnim 的注释）。冻结的动画是不可变的，可以安全共享。
        /// </summary>
        static readonly DoubleAnimation IconZoomIn = FrozenScale(1.0, IconHoverScale, 120);
        static readonly DoubleAnimation IconZoomOut = FrozenScale(IconHoverScale, 1.0, 120);

        /// <summary>悬停上浮 / 复位（起点终点都明确，可冻结共享）。</summary>
        static readonly DoubleAnimation IconLiftUp = FrozenShift(0.0, -IconLiftPx, 160);
        static readonly DoubleAnimation IconLiftDown = FrozenShift(-IconLiftPx, 0.0, 160);

        /// <summary>
        /// 点击弹跳（关键帧：0 → -6px → 0）。
        ///
        /// 为什么用 DoubleAnimationUsingKeyFrames 而不是两个动画串起来：
        /// 关键帧能保证"上去再回来"是一个连续动作，
        /// 中间不会被别的动画打断；而且只占一个动画槽，
        /// 磁贴被复用（滚动重建）时不会残留半截动画。
        /// </summary>
        static DoubleAnimationUsingKeyFrames BuildBounce()
        {
            DoubleAnimationUsingKeyFrames a = new DoubleAnimationUsingKeyFrames();
            a.Duration = TimeSpan.FromMilliseconds(IconBounceMs);

            CubicEase up = new CubicEase();
            up.EasingMode = EasingMode.EaseOut;
            up.Freeze();

            CubicEase down = new CubicEase();
            down.EasingMode = EasingMode.EaseIn;
            down.Freeze();

            a.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(0.0)));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(-IconBouncePx, KeyTime.FromPercent(0.45), up));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0), down));
            a.Freeze();
            return a;
        }

        static readonly DoubleAnimationUsingKeyFrames IconBounce = BuildBounce();

        /// <summary>
        /// 位移动画（起点终点明确，可冻结共享）。
        ///
        /// 与 FrozenScale 分开是因为缓动不同：
        /// 缩放和上浮都用 EaseOut（快起慢收，跟手），
        /// 但位移的"回位"用 EaseOut 也合适 —— 所以共用 FrozenCubic。
        /// </summary>
        static DoubleAnimation FrozenShift(double from, double to, int ms)
        {
            DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            a.From = from;
            a.EasingFunction = FrozenCubic();
            a.Freeze();
            return a;
        }

        /// <summary>
        /// 缩放动画。**起点与终点都明确**，所以可以冻结后跨所有磁贴共享。
        ///
        /// 加 EaseOut 缓动：线性缩放在鼠标划过密集磁贴时显得"一顿一顿"，
        /// 快起慢收则跟手感更顺。（不用 BackEase/ElasticEase —— 回弹会显得轻浮，
        /// 且高频划过时容易晕。）
        /// </summary>
        static DoubleAnimation FrozenScale(double from, double to, int ms)
        {
            DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            a.From = from;
            a.EasingFunction = FrozenCubic();
            a.Freeze();
            return a;
        }

        /// <summary>共享一个冻结的缓动对象（缓动本身不可变，可安全复用）。</summary>
        static CubicEase frozenEase;
        static CubicEase FrozenCubic()
        {
            if (frozenEase == null)
            {
                CubicEase e = new CubicEase();
                e.EasingMode = EasingMode.EaseOut;
                e.Freeze();
                frozenEase = e;
            }
            return frozenEase;
        }

        /// <summary>
        /// 设置键盘选中项。index 越界或 -1 表示清空选中。
        /// scrollIntoView 为 true 时把磁贴滚进可视区域（方向键导航用）。
        /// </summary>
        void SelectTile(int index, bool scrollIntoView)
        {
            try
            {
                if (tilePanel == null) return;

                // 先恢复旧选中项的外观
                if (selectedTile != null)
                {
                    selectedTile.Background = Brushes.Transparent;
                    selectedTile.BorderThickness = new Thickness(0);
                }
                selectedTile = null;
                selectedIndex = -1;

                if (index < 0 || index >= renderedApps.Count) return;
                if (index >= tilePanel.Children.Count) return;
                Border tile = tilePanel.Children[index] as Border;
                if (tile == null) return;

                selectedTile = tile;
                selectedIndex = index;
                tile.Background = Ui.AccentSoft;
                tile.BorderBrush = Ui.Accent;
                tile.BorderThickness = new Thickness(2);

                if (scrollIntoView) tile.BringIntoView();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "设置磁贴选中");
            }
        }

        /// <summary>方向键导航。返回 true 表示已处理（调用方应把事件标记为 Handled）。</summary>
        bool MoveSelection(Key key)
        {
            if (renderedApps == null || renderedApps.Count == 0) return false;

            // WrapPanel 当前一行放几个磁贴；拿不到布局信息时按 8 列估算
            int cols = 8;
            try
            {
                double w = tilePanel.ActualWidth;
                if (w > 0)
                {
                    double unit = 96 + 4; // 磁贴宽 + 左右 margin
                    int c = (int)Math.Floor(w / unit);
                    if (c >= 1) cols = c;
                }
            }
            catch { }

            int n = renderedApps.Count;
            int i = selectedIndex;

            switch (key)
            {
                case Key.Right:
                    i = (i < 0) ? 0 : i + 1;
                    break;
                case Key.Left:
                    i = (i < 0) ? 0 : i - 1;
                    break;
                case Key.Down:
                    i = (i < 0) ? 0 : i + cols;
                    break;
                case Key.Up:
                    i = (i < 0) ? 0 : i - cols;
                    break;
                case Key.Home:
                    i = 0;
                    break;
                case Key.End:
                    i = n - 1;
                    break;
                default:
                    return false;
            }

            if (i < 0) i = 0;
            if (i > n - 1) i = n - 1;
            SelectTile(i, true);
            return true;
        }

        /// <summary>回车启动当前选中的磁贴；没有选中时启动第一个。返回是否已处理。</summary>
        bool LaunchSelected()
        {
            try
            {
                if (renderedApps == null || renderedApps.Count == 0) return false;
                int i = selectedIndex;
                if (i < 0 || i >= renderedApps.Count) i = 0;
                Launch(renderedApps[i]);
                return true;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "回车启动选中应用");
                return false;
            }
        }

        void RebuildNav()
        {
            navStack.Children.Clear();
            navCats.Clear();
            navItems.Clear();
            navBars.Clear();
            navNames.Clear();
            navCounts.Clear();

            List<string> cats = new List<string>();
            cats.Add("常用");
            cats.Add("");
            cats.AddRange(catalog.ActiveCategories());

            // 「收藏夹」只在确实有收藏项时出现（没有就不占地方）
            int pinnedCount = pinnedFolders == null ? 0 : pinnedFolders.Count;
            if (pinnedCount > 0) cats.Add(PinnedCategory);

            // 「桌面」常驻：它是虚拟视图，与"桌面上有几个文件"无关 ——
            // 桌面空了也该能进去看（用户会疑惑"我的桌面去哪了"）
            cats.Add(DesktopCategory);

            // 「隐藏的项目」只在确实有隐藏项时出现（没有就不占地方）
            int hiddenCount = catalog.HiddenItems().Count;
            if (hiddenCount > 0) cats.Add(HiddenCategory);

            foreach (string cat in cats)
            {
                string c = cat;
                int count = c == HiddenCategory ? hiddenCount
                          : (c == PinnedCategory ? pinnedCount
                          : (c == DesktopCategory ? 0
                          : (c == "常用" ? CommonApps().Count : catalog.ByCategory(c).Count)));
                if (c != "" && c != "常用" && c != PinnedCategory && c != DesktopCategory && count == 0) continue;

                // 显示名：被改过名的分类查表，其余用内部名本身。
                // 内部名（c）始终不变——它才是 apps.json / 规则表 / 覆盖表里的键。
                string label;
                if (c == "") label = "全部";
                else if (c == HiddenCategory) label = "隐藏的项目";
                else if (c == PinnedCategory) label = "收藏夹";
                else if (c == DesktopCategory) label = "桌面";
                else label = CategoryDisplayName(c);

                Border item = new Border();
                item.Height = 34;
                item.CornerRadius = new CornerRadius(Ui.R.Sm);
                item.Margin = new Thickness(0, 2, 0, 2);
                item.Background = Brushes.Transparent;
                item.Cursor = Cursors.Hand;
                // 改过名的分类，悬停时提示原本叫什么，避免以后认不出来
                if (c != "" && c != HiddenCategory && categories != null && categories.IsRenamed(c))
                {
                    item.ToolTip = "原名：" + c;
                }

                Grid g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions[0].Width = new GridLength(3);
                g.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                g.ColumnDefinitions[2].Width = GridLength.Auto;

                Border bar = new Border();
                bar.Width = 3;
                bar.Height = 16;
                bar.CornerRadius = new CornerRadius(2);
                bar.Background = Ui.Accent;
                bar.HorizontalAlignment = HorizontalAlignment.Left;
                bar.Visibility = Visibility.Collapsed;
                Grid.SetColumn(bar, 0);
                g.Children.Add(bar);

                TextBlock name = new TextBlock();
                name.Text = label;
                name.FontFamily = Ui.YaHei;
                name.FontSize = 12.5;
                name.Foreground = Ui.Fg;
                name.VerticalAlignment = VerticalAlignment.Center;
                name.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(name, 1);
                g.Children.Add(name);

                TextBlock cnt = new TextBlock();
                cnt.Text = count.ToString();
                cnt.FontFamily = Ui.YaHei;
                cnt.FontSize = 11;
                cnt.Foreground = Ui.Sub;
                cnt.VerticalAlignment = VerticalAlignment.Center;
                cnt.Margin = new Thickness(Ui.S.Xs, 0, Ui.S.Sm, 0);
                Grid.SetColumn(cnt, 2);
                g.Children.Add(cnt);

                item.Child = g;
                item.MouseEnter += delegate { if (current != c) item.Background = Ui.Hover; };
                item.MouseLeave += delegate { if (current != c) item.Background = Brushes.Transparent; };
                item.MouseLeftButtonUp += delegate
                {
                    // 先把当前分类的滚动位置存下来（必须在改 current 之前）
                    SaveScrollOffset();
                    current = c;
                    SaveLastCategory();   // 记住分类，重启后回到这里
                    UpdateNav();
                    RenderApps();
                };

                // 右键分类：重命名 / 上移下移。
                // 只对真实分类提供（「常用」「全部」「隐藏的项目」是虚拟分类，
                // 不参与排序也不该被改名）。
                if (c != "" && c != "常用" && c != HiddenCategory)
                {
                    string cc = c;
                    item.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
                    {
                        ShowCategoryMenu(cc);
                        e.Handled = true;
                    };
                }

                navStack.Children.Add(item);
                navCats.Add(c);
                navItems.Add(item);
                navBars.Add(bar);
                navNames.Add(name);
                navCounts.Add(cnt);
            }

            UpdateNav();
        }

        void UpdateNav()
        {
            for (int i = 0; i < navItems.Count; i++)
            {
                bool sel = navCats[i] == current;
                navItems[i].Background = sel ? Ui.AccentSoft : Brushes.Transparent;
                navBars[i].Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
                navNames[i].Foreground = sel ? Ui.AccentText : Ui.Fg;
                navNames[i].FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        /// <summary>
        /// 分类的显示名。改过名的查表，其余返回内部名。
        ///
        /// 内部名永远不变 —— 它是 apps.json / 规则表 / 覆盖表 / order.json
        /// 里用的键，改了就得四处迁移，而且下次重扫会被写回旧名。
        /// </summary>
        string CategoryDisplayName(string internalName)
        {
            try
            {
                if (categories == null) return internalName;
                return categories.DisplayName(internalName);
            }
            catch { return internalName; }
        }

        /// <summary>
        /// 分类的右键菜单：重命名 / 上移 / 下移。
        /// 「常用」「全部」「隐藏的项目」是虚拟分类，不提供这个菜单。
        /// </summary>
        void ShowCategoryMenu(string cat)
        {
            try
            {
                if (string.IsNullOrEmpty(cat)) return;

                ContextMenu cm = new ContextMenu();

                MenuItem rename = new MenuItem();
                bool renamed = categories != null && categories.IsRenamed(cat);
                rename.Header = "重命名…";
                rename.Click += delegate { PromptRenameCategory(cat); };
                cm.Items.Add(rename);

                if (renamed)
                {
                    MenuItem reset = new MenuItem();
                    reset.Header = "恢复原名（" + cat + "）";
                    reset.Click += delegate { RenameCategory(cat, null); };
                    cm.Items.Add(reset);
                }

                cm.Items.Add(new Separator());

                MenuItem up = new MenuItem();
                up.Header = "上移";
                up.Click += delegate { MoveCategory(cat, -1); };
                cm.Items.Add(up);

                MenuItem down = new MenuItem();
                down.Header = "下移";
                down.Click += delegate { MoveCategory(cat, +1); };
                cm.Items.Add(down);

                // 菜单打开期间抑制面板自动收起（同磁贴菜单的处理）
                taskMenuOpen = true;
                cm.Closed += delegate { taskMenuOpen = false; };
                cm.Placement = PlacementMode.MousePoint;
                cm.IsOpen = true;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "分类右键菜单");
            }
        }

        /// <summary>弹出输入框让用户改分类显示名。</summary>
        void PromptRenameCategory(string cat)
        {
            try
            {
                string currentDisplay = CategoryDisplayName(cat);
                string input = TextPromptDialog.Ask(
                    this, "重命名分类", "把「" + cat + "」显示为：", currentDisplay);
                if (input == null) return;      // 用户取消
                RenameCategory(cat, input.Trim());
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "重命名分类");
            }
        }

        /// <summary>
        /// 改分类显示名。display 传空表示恢复原名。
        /// 改完重建侧栏（名字变了）并刷新内容（底部计数等）。
        /// </summary>
        void RenameCategory(string cat, string display)
        {
            try
            {
                if (categories == null) categories = Categories.Load(baseDir);
                if (!categories.Rename(cat, display)) return;

                Ui.Log("分类重命名: " + cat + " -> " + (string.IsNullOrEmpty(display) ? "(原名)" : display));
                RebuildNav();
                RenderApps();
                SaveLastCategory();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "分类重命名");
            }
        }

        /// <summary>把分类在侧栏里上移或下移一位。</summary>
        void MoveCategory(string cat, int delta)
        {
            try
            {
                if (categories == null) categories = Categories.Load(baseDir);

                // 顺序表要基于**当前实际显示的顺序**来排，
                // 否则用户第一次调顺序时，其余分类的相对次序会被默认表覆盖。
                List<string> cats = new List<string>(catalog.ActiveCategories());
                if (!categories.MoveCategory(cats, cat, delta)) return;

                Ui.Log("分类" + (delta < 0 ? "上移" : "下移") + ": " + cat);
                RebuildNav();
                RenderApps();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "分类排序");
            }
        }

        void OnSearchChanged()
        {
            // 程序自己清空搜索框时不重建（打开面板的场景）
            if (searchSuppress) return;
            if (searchHint != null)
                searchHint.Visibility = searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderApps();
        }

        List<AppItem> CommonApps()
        {
            List<AppItem> list = new List<AppItem>();
            foreach (AppItem a in catalog.Apps)
            {
                if (a.Usage > 0) list.Add(a);
            }
            list.Sort(RenderCompare);
            if (list.Count > 12) list = list.GetRange(0, 12);
            return list;
        }

        /// <summary>
        /// 重建顶部快速启动区。
        ///
        /// 取"用过的里面最常用的几个"（沿用 CommonApps 的排序口径），
        /// 而不是用户手动指定 —— 手选要再加一套配置和一个设置界面，
        /// 而使用频率本来就已经反映了"常用的"。
        ///
        /// 一件都没用过时整个区域不显示（Height=Auto 会收成 0），
        /// 新装用户不会看到一排空位。
        /// </summary>
        void RebuildQuickBar()
        {
            try
            {
                if (quickBar == null) return;
                quickBar.Children.Clear();

                // 注意：不要在这里写 `if (QuickBarCount <= 0) return;` ——
                // QuickBarCount 是 const，编译器能判定该分支恒不成立，
                // 会报 CS0162「检测到无法访问的代码」。要关掉这个功能就把常量改成 0，
                // 并同步删掉这里的取用逻辑。
                List<AppItem> top = CommonApps();
                if (top.Count == 0) return;

                if (top.Count > QuickBarCount) top = top.GetRange(0, QuickBarCount);

                // 左侧小标签，说明这一排是什么（否则用户会以为它们是"置顶的普通磁贴"）
                TextBlock label = Ui.T("常用", 10.5, Ui.Sub);
                label.VerticalAlignment = VerticalAlignment.Center;
                label.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
                quickBar.Children.Add(label);

                foreach (AppItem a in top)
                    quickBar.Children.Add(MakeQuickButton(a));

                Ui.Log("快速启动区: " + top.Count + " 个");
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "重建快速启动区");
            }
        }

        /// <summary>
        /// 按当前状态同步一块磁贴的"运行中"小圆点（增或删）。
        ///
        /// 就地操作，不重建磁贴：面板开着期间应用可能刚启动/刚退出，
        /// 为几颗 8px 的点重建 700+ 控件不值得。
        /// 圆点放在 iconBox 的最后一个子元素，所以"有没有"就是看末位类型。
        /// </summary>
        void SyncRunningDot(AppItem a, Grid iconBox)
        {
            try
            {
                if (a == null || iconBox == null) return;

                bool shouldShow = RunningApps.IsRunningCached(a.Path);

                // 末尾是不是已经有圆点了
                Border existing = null;
                int n = iconBox.Children.Count;
                if (n > 0) existing = iconBox.Children[n - 1] as Border;
                bool hasDot = existing != null && Math.Abs(existing.Width - 8) < 0.1;

                if (shouldShow == hasDot) return;   // 状态没变，不碰

                if (shouldShow)
                {
                    Border dot = new Border();
                    dot.Width = 8;
                    dot.Height = 8;
                    dot.CornerRadius = new CornerRadius(4);   // 正圆
                    dot.Background = Ui.Accent;
                    dot.HorizontalAlignment = HorizontalAlignment.Right;
                    dot.VerticalAlignment = VerticalAlignment.Top;
                    // 略微外扩到图标边框上，像"贴在角上"而不是"压在图标里"
                    dot.Margin = new Thickness(0, -1, -1, 0);
                    dot.ToolTip = a.Name + " 正在运行";
                    iconBox.Children.Add(dot);
                }
                else
                {
                    iconBox.Children.Remove(existing);
                }
            }
            catch { }
        }

        /// <summary>
        /// 就地刷新所有已画磁贴的小圆点。
        ///
        /// 由任务栏刷新定时器（1.5 秒）调用。两件事会需要它：
        ///   1. 后台预热完成后，缓存从"没热"变成"热"，原先没画上的点要补上
        ///   2. 面板开着期间应用启动了/退出了
        /// 只查表 + 增删一个小 Border，不触发布局、不重建磁贴。
        /// </summary>
        void RefreshRunningDots()
        {
            try
            {
                if (tileIconBoxes.Count == 0) return;
                if (renderedApps == null) return;
                if (!IsVisible) return;   // 面板没显示，没人看，省掉

                RunningApps.Refresh();   // 内部 1.5 秒节流

                foreach (AppItem a in renderedApps)
                {
                    if (a == null) continue;
                    Grid box;
                    if (!tileIconBoxes.TryGetValue(a.Path, out box)) continue;
                    SyncRunningDot(a, box);
                }
            }
            catch { }
        }

        /// <summary>
        /// 快速启动按钮：图标 + 名字（横排，比磁贴小）。
        ///
        /// 单独做而不复用 MakeTile：磁贴是 96x90 的竖排大块，
        /// 一排 7 个会占掉 700px 宽，把内容区挤扁。
        /// </summary>
        Border MakeQuickButton(AppItem a)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(Ui.R.Sm);
            b.Height = 30;
            b.Padding = new Thickness(Ui.S.Sm, 0, Ui.S.Sm + 2, 0);
            b.Margin = new Thickness(0, 0, Ui.S.Xs, 0);
            b.Cursor = Cursors.Hand;
            b.Background = Ui.SearchBg;
            b.ToolTip = a.Name;
            b.VerticalAlignment = VerticalAlignment.Center;

            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.VerticalAlignment = VerticalAlignment.Center;

            ImageSource icon = AppIcons.Get(a.Path);
            if (icon != null)
            {
                Image img = new Image();
                img.Source = icon;
                img.Width = 18;
                img.Height = 18;
                img.Margin = new Thickness(0, 0, Ui.S.Xs + 2, 0);
                img.VerticalAlignment = VerticalAlignment.Center;
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                sp.Children.Add(img);
            }

            TextBlock name = Ui.T(a.Name, 12, Ui.Fg);
            name.VerticalAlignment = VerticalAlignment.Center;
            name.MaxWidth = 96;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(name);

            b.Child = sp;

            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = Ui.SearchBg; };
            b.MouseLeftButtonDown += delegate { b.Background = Ui.Pressed; };
            b.MouseLeftButtonUp += delegate
            {
                b.Background = Ui.Hover;
                Launch(a);
            };
            return b;
        }

        /// <summary>当前已渲染内容的签名，用于判断是否真的需要重建磁贴。</summary>
        string renderedSig = "";

        /// <summary>
        /// 内容没变就不重建磁贴。避免“每次打开面板都销毁并重建全部控件”，
        /// 那是重复开关面板时内存持续上涨的主因。
        /// </summary>
        void RenderAppsIfNeeded()
        {
            try
            {
                string sig = CurrentContentSig();
                if (sig == renderedSig) return;
                RenderApps();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "按需渲染磁贴");
            }
        }

        void RenderApps()
        {
            if (tilePanel == null) return;
            try
            {
                // 主动重建时同步刷新签名，避免下次 RenderAppsIfNeeded 误判为“没变”
                renderedSig = CurrentContentSig();
                RenderAppsCore();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "渲染应用磁贴");
            }
        }

        /// <summary>当前界面内容的签名（分类 + 搜索词 + 应用数 + 自定义排序长度）。</summary>
        string CurrentContentSig()
        {
            try
            {
                string q = searchBox == null ? "" : searchBox.Text.Trim();
                int orderCount = 0;
                List<string> order;
                if (customOrders.TryGetValue(current, out order) && order != null) orderCount = order.Count;
                // 隐藏数进签名：隐藏/取消隐藏会改变内容，但 Apps.Count 不变，
                // 不带上它的话「按需渲染」会误判成"没变化"而跳过重建。
                int hiddenCount = catalog == null ? 0 : catalog.HiddenItems().Count;
                return current + "\u0001" + q + "\u0001" + catalog.Apps.Count +
                       "\u0001" + orderCount + "\u0001" + hiddenCount;
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 记下当前分类的滚动偏移。
        ///
        /// 必须在**切换 current 之前**调用——切完之后 current 已经变了，
        /// 记下来的偏移会算到新分类头上，越切越乱。
        /// </summary>
        void SaveScrollOffset()
        {
            try
            {
                if (contentScroll == null) return;
                scrollOffsets[current] = contentScroll.VerticalOffset;
            }
            catch { }
        }

        /// <summary>
        /// 恢复当前分类上次的滚动偏移。
        ///
        /// 时机是关键：内容刚重建时 ExtentHeight 还是 0，
        /// 此时 ScrollToVerticalOffset 会被钳到 0（表现为"滚了但立刻弹回顶部"）。
        /// 所以先 UpdateLayout 让布局完成，再设偏移。
        /// </summary>
        void RestoreScrollOffset()        {
            try
            {
                if (contentScroll == null) return;

                double target;
                if (!scrollOffsets.TryGetValue(current, out target) || target <= 0) return;

                // 布局没跑完就设偏移会被钳掉，先强制布局
                contentScroll.UpdateLayout();

                // 恢复动作本身也会触发 ScrollChanged，不设标记的话
                // 会把刚恢复的值当成"用户滚动"再记一次，多次切换后逐渐漂移
                suppressScrollRecord = true;
                try
                {
                    contentScroll.ScrollToVerticalOffset(target);
                    contentScroll.UpdateLayout();
                }
                finally
                {
                    suppressScrollRecord = false;
                }
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "恢复滚动位置");
            }
        }

        void RenderAppsCore()
        {
            tilePanel.Children.Clear();
            // 磁贴被丢掉了，旧的图标容器引用（指向已移除的控件）也要一起清，
            // 否则 RefreshRunningDots 会去改已经不存在的控件
            tileIconBoxes.Clear();

            string q = searchBox == null ? "" : searchBox.Text.Trim();
            showingCommon = (q.Length == 0 && current == "常用");
            bool showingHidden = (q.Length == 0 && current == HiddenCategory);

            // ---- 收藏夹：内容不是 AppItem，走单独渲染 ----
            //
            // 为什么单独一条路径：PinnedItem 可能是文件夹、任意类型文件，
            // 而 AppItem 的磁贴逻辑（Launch、运行中圆点、拖动换位）都不适用。
            // 强行合并会让每处都长出 if (isFolder) 分支，不如分开。
            if (q.Length == 0 && current == PinnedCategory)
            {
                RenderPinned();
                return;
            }

            // ---- 虚拟桌面：只读真实桌面，不动任何文件 ----
            if (q.Length == 0 && current == DesktopCategory)
            {
                RenderDesktop();
                return;
            }

            List<AppItem> list;

            // ---- 系统级搜索（开始菜单 + 注册表 + 磁盘）----
            //
            // 与下面的"应用清单过滤"是两件事，同时进行：
            //   · 应用清单过滤：已扫描的 119 个应用，按拼音/中英文匹配
            //   · 系统级搜索：全系统的程序与文件，Instant + Deep 两级
            // 用户看到的是两部分结果，应用磁贴在先（更快、更常用），
            // 系统搜索结果在下面（更全、但可能是文件）。
            // 两者在同一个 Grid 里，必须**互斥显示**。
            //
            // 第一版只把 searchResultPanel 设 Visible、忘了把 tilePanel 设 Collapsed，
            // 结果两者叠在一起渲染 —— 搜索结果的文字直接压在磁贴上，
            // 用户看到的就是"文字互相重叠、糊成一片"。
            //
            // 搜索时同时隐藏快速启动区：它是"常用应用"的快捷入口，
            // 用户已经在搜索了，那排按钮只是占地方。
            bool searching = q.Length > 0;
            if (searching)
            {
                if (searchResultPanel != null) searchResultPanel.Visibility = Visibility.Visible;
                if (tilePanel != null) tilePanel.Visibility = Visibility.Collapsed;
                if (quickBar != null) quickBar.Visibility = Visibility.Collapsed;
                if (searchResults != null) searchResults.Search(q);
            }
            else
            {
                if (searchResultPanel != null) searchResultPanel.Visibility = Visibility.Collapsed;
                if (tilePanel != null) tilePanel.Visibility = Visibility.Visible;
                if (quickBar != null) quickBar.Visibility = Visibility.Visible;
                if (searchResults != null) searchResults.Clear();
            }

            if (q.Length > 0)
            {
                // 搜索时**包含隐藏项**：用户主动搜名字，说明就是想找它
                list = new List<AppItem>();
                foreach (AppItem a in catalog.Apps)
                {
                    // 支持中文/英文直接匹配，也支持全拼（weixin）和首字母（wx）
                    if (Pinyin.Matches(a.Name, q))
                        list.Add(a);
                }
                list.Sort(RenderCompare);
            }
            else if (current == "常用")
            {
                list = CommonApps();
            }
            else if (current == HiddenCategory)
            {
                list = catalog.HiddenItems();
            }
            else
            {
                list = catalog.ByCategory(current);
            }

            ApplyCustomOrder(list);
            renderedApps = new List<AppItem>(list);

            // 刷新"哪些应用在运行"，供磁贴右上角的小圆点用。
            //
            // RunningApps.Refresh 内部有 1.5 秒节流（枚举 300+ 进程约 50-75 ms），
            // 而建磁贴只用 IsRunningCached（只读缓存，不解析 lnk）——
            // 首次解析 112 个快捷方式要 239 ms，那代价放在后台预热里做。
            RunningApps.Refresh();

            // 重渲染会丢弃旧的 Border 对象，选中状态必须一起清掉（否则 selectedTile 指向已移除的控件）
            selectedTile = null;
            selectedIndex = -1;
            foreach (AppItem a in list)
                tilePanel.Children.Add(MakeTile(a));

            // 空状态：只在**没有系统搜索结果**时才显示。
            //
            // 为什么要这个判断：搜索时"应用清单里没有"不等于"全系统没有"。
            // 实测搜「报告」时，119 个已扫描应用里确实一个都不匹配，
            // 但磁盘上找到了 4 个 .docx —— 此时若显示"没有找到「报告」"
            // 就与下方真实结果自相矛盾。
            bool hasSystemResults = searchResultPanel != null
                && searchResultPanel.Visibility == Visibility.Visible
                && searchResultPanel.Children.Count > 0;

            if (list.Count == 0 && !(q.Length > 0 && hasSystemResults))
            {
                string tip, sub, glyph;
                if (q.Length > 0)
                {
                    glyph = "\uE721";                                   // 放大镜
                    tip = "没有找到「" + q + "」";
                    sub = "换个说法试试，或者用拼音首字母（如 wx → 微信）";
                }
                else if (current == "常用")
                {
                    glyph = "\uE735";                                   // 星标
                    tip = "还没有常用应用";
                    sub = "从面板启动几次，常用的会自动出现在这里";
                }
                else if (current == HiddenCategory)
                {
                    glyph = "\uE7B3";                                   // 眼睛
                    tip = "没有隐藏的应用";
                    sub = "右键应用磁贴 →「从面板隐藏」可以把用不到的藏起来";
                }
                else
                {
                    glyph = "\uE7C3";                                   // 页面
                    tip = "这个分类里没有应用";
                    sub = "右键四叶草 →「重新扫描应用」可以重新识别";
                }

                StackPanel empty = new StackPanel();
                empty.HorizontalAlignment = HorizontalAlignment.Left;
                empty.Margin = new Thickness(Ui.S.Xl, Ui.S.Xl + Ui.S.Sm, 0, 0);

                TextBlock ico = new TextBlock();
                ico.Text = glyph;
                ico.FontFamily = Ui.Mdl;
                ico.FontSize = 22;
                ico.Foreground = Ui.Sub;
                ico.Opacity = 0.55;      // 压低存在感：是装饰，不是内容
                ico.HorizontalAlignment = HorizontalAlignment.Left;
                empty.Children.Add(ico);

                TextBlock main = Ui.T(tip, 13.5, Ui.Fg, FontWeights.SemiBold);
                main.Margin = new Thickness(0, Ui.S.Sm, 0, 0);
                empty.Children.Add(main);

                TextBlock hint = Ui.T(sub, 12, Ui.Sub);
                hint.Margin = new Thickness(0, Ui.S.Xs, 0, 0);
                hint.TextWrapping = TextWrapping.Wrap;
                hint.MaxWidth = 320;
                empty.Children.Add(hint);

                tilePanel.Children.Add(empty);
            }

            // 底部计数：隐藏项不该算进"应用总数"，否则数字对不上眼前的磁贴
            int visible = catalog.Apps.Count - catalog.HiddenItems().Count;
            // 搜索时计数改成"已找到多少条系统结果" ——
            // 原先显示"共 61 个应用"，但页面显示的是搜索结果，数字与内容无关。
            // SearchResultList 暴露 ShownCount；系统搜索还没出结果时显示"搜索中"。
            if (searching)
            {
                int n = searchResults != null ? searchResults.ShownCount : 0;
                footerCount.Text = n > 0
                    ? "找到 " + n + " 条结果"
                    : "搜索中…";
            }
            else
            {
                footerCount.Text = showingHidden
                    ? "共 " + catalog.HiddenItems().Count + " 个隐藏项 · 右键可取消隐藏"
                    : "共 " + visible + " 个应用";
            }

            // 内容重建完再恢复滚动位置（放在最后，确保布局已经反映了新的磁贴集合）
            RestoreScrollOffset();

            // 快速启动区跟着一起刷新（它取的是"最常用的几个"，
            // 而启动次数只会在 Launch 之后变，所以重渲染时同步一次即可）。
            // 搜索时快速启动区已隐藏，重建是白费——等退出搜索再刷。
            if (!searching) RebuildQuickBar();
        }

        /// <summary>
        /// 渲染「桌面」视图（虚拟桌面）。
        ///
        /// **只读**：读取桌面文件列表并显示，不移动、不删除、不改名。
        /// 真实桌面保持原样 —— 用户不满意这个视图，随时切走即可。
        ///
        /// 按类型分组显示（程序 / 文件夹 / 文档 / 图片 / 视频 / 压缩包 / 其他），
        /// 每组前加小标题。这样即使桌面很乱，面板里也是有条理的。
        /// </summary>
        void RenderDesktop()
        {
            try
            {
                if (tilePanel == null) return;

                // 每次进入都重读：桌面文件随时可能变（下载、新建、删除）
                desktopItems = DesktopItems.Load();

                int n = desktopItems == null ? 0 : desktopItems.Count;

                if (n == 0)
                {
                    StackPanel empty = new StackPanel();
                    empty.Margin = new Thickness(Ui.S.Xl, Ui.S.Xl, 0, 0);

                    TextBlock ico = new TextBlock();
                    ico.Text = "\uE7C4";   // Desktop
                    ico.FontFamily = Ui.Mdl;
                    ico.FontSize = 22;
                    ico.Foreground = Ui.Sub;
                    ico.Opacity = 0.55;
                    empty.Children.Add(ico);

                    TextBlock main = Ui.T("桌面是空的", 13.5, Ui.Fg, FontWeights.SemiBold);
                    main.Margin = new Thickness(0, Ui.S.Sm, 0, 0);
                    empty.Children.Add(main);

                    TextBlock hint = Ui.T("这里显示桌面上的文件和文件夹（只读，不会移动任何东西）",
                        12, Ui.Sub);
                    hint.Margin = new Thickness(0, Ui.S.Xs, 0, 0);
                    hint.TextWrapping = TextWrapping.Wrap;
                    hint.MaxWidth = 380;
                    empty.Children.Add(hint);

                    tilePanel.Children.Add(empty);
                }
                else
                {
                    // 按分组渲染：每组先插一个占满整行的标题，再放磁贴
                    string lastGroup = null;
                    foreach (DesktopItem it in desktopItems)
                    {
                        if (!string.Equals(it.Group, lastGroup, StringComparison.Ordinal))
                        {
                            lastGroup = it.Group;
                            int cnt = DesktopItems.CountGroup(desktopItems, lastGroup);
                            tilePanel.Children.Add(MakeGroupHeader(lastGroup + " · " + cnt));
                        }
                        tilePanel.Children.Add(MakeDesktopTile(it));
                    }
                }

                if (footerCount != null)
                    footerCount.Text = "桌面 " + n + " 项 · 只读，不会移动任何文件";
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "渲染虚拟桌面");
            }
        }

        /// <summary>
        /// 分组标题（占满整行的横条）。
        ///
        /// 为什么要占满一行：WrapPanel 里如果标题不撑满宽度，
        /// 后面的磁贴会挤到它右边，标题就"悬在半空"。
        /// 用 Width 绑定到容器宽度做不到（WrapPanel 无此概念），
        /// 所以给一个足够大的 MinWidth + 让它独占一行（靠一个换行占位）。
        /// </summary>
        FrameworkElement MakeGroupHeader(string text)
        {
            StackPanel wrap = new StackPanel();
            wrap.Orientation = Orientation.Vertical;
            wrap.Margin = new Thickness(2, Ui.S.Sm, 2, 2);

            TextBlock t = Ui.T(text, 10.5, Ui.Sub);
            t.Margin = new Thickness(Ui.S.Xs, 0, 0, 0);
            wrap.Children.Add(t);

            // 用一个不可见的全宽占位把后续磁贴推到下一行
            Border spacer = new Border();
            spacer.Width = Math.Max(200, tilePanel.ActualWidth > 100 ? tilePanel.ActualWidth - 20 : 900);
            spacer.Height = 0.1;
            wrap.Children.Add(spacer);

            return wrap;
        }

        /// <summary>
        /// 桌面项目磁贴：与普通应用同尺寸，图标按类型给字形。
        ///
        /// 为什么用字形而不是真实图标：
        /// 真实图标要 SHGetFileInfo（每个几十 ms），桌面几十项就要上百 ms。
        /// 而这里的目的是"看清桌面有什么"，字形已足够区分类型。
        /// </summary>
        Border MakeDesktopTile(DesktopItem it)
        {
            Border b = new Border();
            b.Width = TileWidthPx;
            b.Height = TileHeightPx;
            b.CornerRadius = new CornerRadius(Ui.R.Sm);
            b.Margin = new Thickness(2);
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;

            string sizeText = it.IsFolder ? "文件夹" : DesktopItems.FormatSize(it.Size);
            b.ToolTip = it.Path + (string.IsNullOrEmpty(sizeText) ? "" : "\n" + sizeText);

            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;

            TextBlock ico = new TextBlock();
            ico.Text = GlyphOf(it);
            ico.FontFamily = Ui.Mdl;
            ico.FontSize = 26;
            ico.Foreground = ColorOf(it);
            ico.HorizontalAlignment = HorizontalAlignment.Center;
            ico.Margin = new Thickness(0, 0, 0, Ui.S.Xs);
            sp.Children.Add(ico);

            TextBlock name = new TextBlock();
            name.Text = it.Name;
            name.FontFamily = Ui.YaHei;
            name.FontSize = 11.5;
            name.Foreground = Ui.Fg;
            name.TextWrapping = TextWrapping.Wrap;
            name.TextAlignment = TextAlignment.Center;
            name.LineHeight = 15;
            name.MaxWidth = 88;

            Border clip = new Border();
            clip.MaxHeight = 30;
            clip.ClipToBounds = true;
            clip.Child = name;
            clip.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(clip);

            b.Child = sp;

            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonDown += delegate { b.Background = Ui.Pressed; };
            b.MouseLeftButtonUp += delegate
            {
                b.Background = Ui.Hover;
                OpenDesktopItem(it);
            };
            b.MouseRightButtonUp += delegate { ShowDesktopItemMenu(it); };

            return b;
        }

        /// <summary>按分组选字形。</summary>
        static string GlyphOf(DesktopItem it)
        {
            if (it.IsFolder) return "\uE8B7";        // Folder
            switch (it.Group)
            {
                case "程序": return "\uE7B8";        // 应用
                case "文档": return "\uE8A5";        // Document
                case "图片": return "\uEB9F";        // Photo
                case "视频": return "\uE714";        // Video
                case "压缩包": return "\uE7B8";      // Archive（MDL2 无专门图标）
                default: return "\uE7C3";            // Page
            }
        }

        /// <summary>按分组选颜色，让不同类型一眼可分。</summary>
        static Brush ColorOf(DesktopItem it)
        {
            if (it.IsFolder) return Ui.AccentText;
            switch (it.Group)
            {
                case "程序": return Ui.AccentText;
                case "文档": return Ui.Sub;
                case "图片": return Ui.Accent;
                case "视频": return Ui.Accent;
                case "压缩包": return Ui.Sub;
                default: return Ui.Sub;
            }
        }

        /// <summary>打开桌面项目：文件夹用资源管理器，文件用默认程序。</summary>
        void OpenDesktopItem(DesktopItem it)
        {
            try
            {
                if (it == null || string.IsNullOrEmpty(it.Path)) return;

                if (it.IsFolder)
                {
                    if (Directory.Exists(it.Path))
                        Process.Start("explorer.exe", "\"" + it.Path + "\"");
                    else
                        MessageBox.Show("文件夹已不存在：\r\n" + it.Path, "四叶草");
                }
                else
                {
                    if (File.Exists(it.Path))
                        Process.Start(new ProcessStartInfo(it.Path) { UseShellExecute = true });
                    else
                        MessageBox.Show("文件已不存在：\r\n" + it.Path, "四叶草");
                }
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "打开桌面项目");
            }
        }

        /// <summary>
        /// 桌面项目右键：打开 / 在资源管理器中显示 / 固定到收藏夹 / 复制路径。
        ///
        /// **刻意不提供"删除"和"重命名"**：
        /// 这是只读视图。给个删除按钮，用户误点就真的删了桌面文件，
        /// 而"整理桌面"的心理预期是"看清楚"，不是"在这里管理文件"。
        /// 真要管理，用「在资源管理器中显示」跳过去。
        /// </summary>
        void ShowDesktopItemMenu(DesktopItem it)
        {
            try
            {
                ContextMenu cm = new ContextMenu();

                MenuItem open = new MenuItem();
                open.Header = "打开";
                open.Click += delegate { OpenDesktopItem(it); };
                cm.Items.Add(open);

                MenuItem loc = new MenuItem();
                loc.Header = "在资源管理器中显示";
                loc.Click += delegate
                {
                    try
                    {
                        if (it.IsFolder)
                            Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(it.Path) + "\"");
                        else
                            Process.Start("explorer.exe", "/select,\"" + it.Path + "\"");
                    }
                    catch { }
                };
                cm.Items.Add(loc);

                cm.Items.Add(new Separator());

                MenuItem pin = new MenuItem();
                pin.Header = "固定到收藏夹";
                pin.Click += delegate
                {
                    if (AddPinned(it.Path, it.Name))
                        MessageBox.Show("已加入收藏夹。", "四叶草");
                    else
                        MessageBox.Show("这个项目已经在收藏夹里了。", "四叶草");
                };
                cm.Items.Add(pin);

                MenuItem copy = new MenuItem();
                copy.Header = "复制路径";
                copy.Click += delegate
                {
                    try { Clipboard.SetText(it.Path); } catch { }
                };
                cm.Items.Add(copy);

                taskMenuOpen = true;
                cm.Closed += delegate { taskMenuOpen = false; };
                cm.Placement = PlacementMode.MousePoint;
                cm.IsOpen = true;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "桌面项目菜单");
            }
        }

        /// <summary>
        /// 渲染「收藏夹」视图。
        ///
        /// 用磁贴形态（与普通应用一致）而不是列表：
        /// 收藏夹通常只有几个到十几个，磁贴更醒目、点击目标更大。
        /// 图标：文件夹用文件夹字形，文件用其扩展名对应的字形。
        /// </summary>
        void RenderPinned()
        {
            try
            {
                if (tilePanel == null) return;

                int n = pinnedFolders == null ? 0 : pinnedFolders.Count;

                if (n == 0)
                {
                    StackPanel empty = new StackPanel();
                    empty.Margin = new Thickness(Ui.S.Xl, Ui.S.Xl, 0, 0);

                    TextBlock ico = new TextBlock();
                    ico.Text = "\uE8B7";   // Folder
                    ico.FontFamily = Ui.Mdl;
                    ico.FontSize = 22;
                    ico.Foreground = Ui.Sub;
                    ico.Opacity = 0.55;
                    empty.Children.Add(ico);

                    TextBlock main = Ui.T("还没有收藏的文件夹", 13.5, Ui.Fg, FontWeights.SemiBold);
                    main.Margin = new Thickness(0, Ui.S.Sm, 0, 0);
                    empty.Children.Add(main);

                    TextBlock hint = Ui.T(
                        "在资源管理器里右键任意文件夹 →「固定到四叶草」即可收藏",
                        12, Ui.Sub);
                    hint.Margin = new Thickness(0, Ui.S.Xs, 0, 0);
                    hint.TextWrapping = TextWrapping.Wrap;
                    hint.MaxWidth = 360;
                    empty.Children.Add(hint);

                    tilePanel.Children.Add(empty);
                }
                else
                {
                    foreach (PinnedItem it in pinnedFolders)
                        tilePanel.Children.Add(MakePinnedTile(it));
                }

                if (footerCount != null)
                    footerCount.Text = "共 " + n + " 个收藏 · 右键可管理";
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "渲染收藏夹");
            }
        }

        /// <summary>
        /// 收藏项磁贴：与普通应用磁贴同尺寸，但图标走文件夹/文件字形。
        ///
        /// 不复用 MakeTile：那个函数依赖 AppItem（路径、分类、运行状态），
        /// 而 PinnedItem 只有路径和名字。共用的只有尺寸常量。
        /// </summary>
        Border MakePinnedTile(PinnedItem it)
        {
            Border b = new Border();
            b.Width = TileWidthPx;
            b.Height = TileHeightPx;
            b.CornerRadius = new CornerRadius(Ui.R.Sm);
            b.Margin = new Thickness(2);
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;
            b.ToolTip = it.Path;

            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;

            // 图标：文件夹 / 文件字形
            TextBlock ico = new TextBlock();
            ico.Text = it.IsFolder ? "\uE8B7" : "\uE7C3";   // Folder / Page
            ico.FontFamily = Ui.Mdl;
            ico.FontSize = 28;
            ico.Foreground = it.IsFolder ? Ui.AccentText : Ui.Sub;
            ico.HorizontalAlignment = HorizontalAlignment.Center;
            ico.Margin = new Thickness(0, 0, 0, Ui.S.Xs);
            sp.Children.Add(ico);

            // 名字
            TextBlock name = new TextBlock();
            name.Text = it.Name;
            name.FontFamily = Ui.YaHei;
            name.FontSize = 11.5;
            name.Foreground = Ui.Fg;
            name.TextWrapping = TextWrapping.Wrap;
            name.TextAlignment = TextAlignment.Center;
            name.LineHeight = 15;
            name.MaxWidth = 88;

            Border clip = new Border();
            clip.MaxHeight = 30;
            clip.ClipToBounds = true;
            clip.Child = name;
            clip.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(clip);

            b.Child = sp;

            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonDown += delegate { b.Background = Ui.Pressed; };
            b.MouseLeftButtonUp += delegate
            {
                b.Background = Ui.Hover;
                OpenPinned(it);
            };
            b.MouseRightButtonUp += delegate { ShowPinnedMenu(it); };

            return b;
        }

        /// <summary>打开收藏项：文件夹用资源管理器，文件用默认程序。</summary>
        void OpenPinned(PinnedItem it)
        {
            try
            {
                if (it == null || string.IsNullOrEmpty(it.Path)) return;

                if (it.IsFolder)
                {
                    if (Directory.Exists(it.Path))
                        Process.Start("explorer.exe", "\"" + it.Path + "\"");
                    else
                        MessageBox.Show("文件夹已不存在：\r\n" + it.Path
                            + "\r\n\r\n可能被移动或删除了。右键可以取消收藏。", "四叶草");
                }
                else
                {
                    if (File.Exists(it.Path))
                        Process.Start(new ProcessStartInfo(it.Path) { UseShellExecute = true });
                    else
                        MessageBox.Show("文件已不存在：\r\n" + it.Path, "四叶草");
                }
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "打开收藏项");
            }
        }

        /// <summary>收藏项右键：打开 / 重命名 / 在资源管理器中显示 / 取消收藏。</summary>
        void ShowPinnedMenu(PinnedItem it)
        {
            try
            {
                ContextMenu cm = new ContextMenu();

                MenuItem open = new MenuItem();
                open.Header = "打开";
                open.Click += delegate { OpenPinned(it); };
                cm.Items.Add(open);

                MenuItem rename = new MenuItem();
                rename.Header = "重命名…";
                rename.Click += delegate
                {
                    string s = TextPromptDialog.Ask(this, "重命名收藏",
                        "给「" + it.Name + "」起个新名字：", it.Name);
                    if (s == null) return;
                    if (PinnedFolders.Rename(baseDir, it.Path, s))
                    {
                        ReloadPinned();
                        RenderApps();
                    }
                };
                cm.Items.Add(rename);

                MenuItem loc = new MenuItem();
                loc.Header = "打开所在位置";
                loc.Click += delegate
                {
                    try
                    {
                        string dir = it.IsFolder ? Path.GetDirectoryName(it.Path) : Path.GetDirectoryName(it.Path);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                            Process.Start("explorer.exe", "\"" + dir + "\"");
                    }
                    catch { }
                };
                cm.Items.Add(loc);

                cm.Items.Add(new Separator());

                MenuItem rm = new MenuItem();
                rm.Header = "取消收藏";
                rm.Click += delegate
                {
                    if (PinnedFolders.Remove(baseDir, it.Path))
                    {
                        ReloadPinned();
                        // 取消最后一个后分类会消失，得先离开
                        if (pinnedFolders.Count == 0 && current == PinnedCategory)
                        {
                            current = "";
                            SaveLastCategory();
                            RebuildNav();
                        }
                        RenderApps();
                    }
                };
                cm.Items.Add(rm);

                taskMenuOpen = true;
                cm.Closed += delegate { taskMenuOpen = false; };
                cm.Placement = PlacementMode.MousePoint;
                cm.IsOpen = true;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "收藏项菜单");
            }
        }

        /// <summary>重新读取收藏列表。</summary>
        void ReloadPinned()
        {
            try { pinnedFolders = PinnedFolders.Load(baseDir); }
            catch { pinnedFolders = new List<PinnedItem>(); }
        }

        /// <summary>添加收藏（供右键菜单 / 外部调用）。返回是否成功。</summary>
        public bool AddPinned(string path, string name)
        {
            try
            {
                bool ok = PinnedFolders.Add(baseDir, path, name);
                if (ok)
                {
                    ReloadPinned();
                    RebuildNav();
                    RenderApps();
                    Ui.Log("收藏: " + path);
                }
                return ok;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "添加收藏");
                return false;
            }
        }

        static int RenderCompare(AppItem a, AppItem b)
        {
            int c = b.Usage.CompareTo(a.Usage);
            if (c != 0) return c;
            c = b.LastUsed.CompareTo(a.LastUsed);
            if (c != 0) return c;
            return string.Compare(a.Name, b.Name, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), System.Globalization.CompareOptions.None);
        }

        Border MakeTile(AppItem a)
        {
            Border b = new Border();
            b.Width = TileWidthPx;
            b.Height = TileHeightPx;
            b.CornerRadius = new CornerRadius(Ui.R.Sm);
            b.Margin = new Thickness(2);
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;
            b.ToolTip = a.Name;

            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;

            // 图标容器：悬停放大作用在它上面，而不是磁贴本身。
            // 为什么：磁贴放大（96x90 -> 更大）会让 WrapPanel 重新排版，
            // 鼠标附近的磁贴会跳位、闪烁；图标用 RenderTransform 放大则完全不碰布局。
            // RenderTransformOrigin 设成中心，所以放大会从正中向四周扩。
            //
            // 用 TransformGroup 组合「缩放 + 位移」两个变换：
            //   · 缩放 → 悬停时图标变大（1.0 → 1.25）
            //   · 位移 → 悬停上浮（0 → -4px）+ 点击弹跳（0 → -6px → 0）
            // 两者独立，可以同时进行；若只用一个 ScaleTransform 就没法做上浮。
            ScaleTransform iconScale = new ScaleTransform(1.0, 1.0);
            TranslateTransform iconLift = new TranslateTransform(0, 0);
            TransformGroup iconTrans = new TransformGroup();
            iconTrans.Children.Add(iconScale);
            iconTrans.Children.Add(iconLift);

            FrameworkElement iconVisual;

            ImageSource icon = AppIcons.Get(a.Path);
            if (icon != null)
            {
                Image img = new Image();
                img.Source = icon;
                img.Width = IconSizePx;
                img.Height = IconSizePx;
                img.HorizontalAlignment = HorizontalAlignment.Center;
                img.VerticalAlignment = VerticalAlignment.Center;
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                iconVisual = img;
            }
            else
            {
                Border ph = new Border();
                ph.Width = IconSizePx;
                ph.Height = IconSizePx;
                ph.CornerRadius = new CornerRadius(Ui.R.Sm);
                ph.Background = Ui.AccentSoft;
                ph.HorizontalAlignment = HorizontalAlignment.Center;
                ph.VerticalAlignment = VerticalAlignment.Center;
                string initial = string.IsNullOrEmpty(a.Name) ? "?" : a.Name.Substring(0, 1);
                TextBlock init = Ui.T(initial, 14.5, Ui.AccentText, FontWeights.SemiBold);
                init.HorizontalAlignment = HorizontalAlignment.Center;
                init.VerticalAlignment = VerticalAlignment.Center;
                ph.Child = init;
                iconVisual = ph;
            }

            iconVisual.RenderTransformOrigin = new Point(0.5, 0.5);
            iconVisual.RenderTransform = iconTrans;

            // 图标外面套一层 Grid，好在右上角叠"运行中"小圆点。
            // 用叠加而不是改图标本身：图标来自系统缓存、可能被多块磁贴共享，
            // 不能往上画东西。
            Grid iconBox = new Grid();
            iconBox.Width = IconSizePx;
            iconBox.Height = IconSizePx;
            iconBox.HorizontalAlignment = HorizontalAlignment.Center;
            iconBox.Margin = new Thickness(0, 0, 0, Ui.S.Xs);
            iconBox.Children.Add(iconVisual);

            // 记下容器，供 RefreshRunningDots 就地增删小圆点
            tileIconBoxes[a.Path] = iconBox;

            // 建磁贴时只读缓存（IsRunningCached 不解析 lnk）：
            // 首次解析 112 个快捷方式要 239 ms，那代价必须留在后台预热里。
            // 缓存还没热的话这里先不画点，等 RefreshRunningDots 补上。
            SyncRunningDot(a, iconBox);

            sp.Children.Add(iconBox);

            TextBlock name = new TextBlock();
            name.Text = a.Name;
            name.FontFamily = Ui.YaHei;
            name.FontSize = 11.5;
            name.Foreground = Ui.Fg;
            name.TextWrapping = TextWrapping.Wrap;
            name.TextAlignment = TextAlignment.Center;
            name.LineHeight = 15;
            name.MaxWidth = 88;

            Border nameClip = new Border();
            nameClip.MaxHeight = 30;
            nameClip.ClipToBounds = true;
            nameClip.Child = name;
            nameClip.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(nameClip);

            b.Child = sp;
            Point downPt = new Point();
            bool tileDrag = false;
            b.MouseEnter += delegate
            {
                if (!ReferenceEquals(b, selectedTile)) b.Background = Ui.Hover;
                // 悬停：放大 + 上浮（参考 GlassBoard 的 Dock 悬停）。
                // 只动 RenderTransform，不触发布局重排。
                iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, IconZoomIn);
                iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, IconZoomIn);
                iconLift.BeginAnimation(TranslateTransform.YProperty, IconLiftUp);
            };
            b.MouseLeave += delegate
            {
                if (!ReferenceEquals(b, selectedTile)) b.Background = Brushes.Transparent;
                iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, IconZoomOut);
                iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, IconZoomOut);
                iconLift.BeginAnimation(TranslateTransform.YProperty, IconLiftDown);
            };
            b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                downPt = e.GetPosition(tilePanel);
                tileDrag = false;
                // 按下态：补全"静止/悬停/按下"三态。
                // 点一个启动稍慢的应用时，用户能看到"按下去了"，
                // 不会怀疑是没点中。
                if (!ReferenceEquals(b, selectedTile)) b.Background = Ui.Pressed;

                // 点击弹跳：0 → -6px → 0。
                // 与悬停上浮共用 TranslateTransform.Y，所以会覆盖悬停状态 ——
                // 这是有意的：弹跳结束后图标停在上浮位置（鼠标还在磁贴上），
                // 视觉上连贯。若弹跳后回到 0 再上浮，会看到"抖两下"。
                iconLift.BeginAnimation(TranslateTransform.YProperty, IconBounce);

                // 鼠标点击磁贴时，键盘选中跟随到它，方向键从当前位置继续
                SelectTile(renderedApps.IndexOf(a), false);
                try { b.CaptureMouse(); } catch { }
            };
            b.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                Point p = e.GetPosition(tilePanel);

                // 拖动阈值 6px：低于这个距离算点击（避免手抖变成拖动）
                if (!tileDrag && (Math.Abs(p.X - downPt.X) > 6 || Math.Abs(p.Y - downPt.Y) > 6))
                {
                    tileDrag = true;
                    b.Opacity = 0.35;          // 拖动项半透明（GlassBoard 用 0.3）
                    b.Cursor = Cursors.SizeAll;
                    // 拖动时取消悬停放大，否则拖动项又大又淡，很怪
                    iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, IconZoomOut);
                    iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, IconZoomOut);
                    iconLift.BeginAnimation(TranslateTransform.YProperty, IconLiftDown);
                    BeginDragVisual(a);
                    Ui.Log("DragStart " + a.Name);
                }

                if (tileDrag)
                {
                    // 拖动过程中持续更新插入标记位置
                    UpdateDragPreview(a, p);
                }
            };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                try { b.ReleaseMouseCapture(); } catch { }
                b.Opacity = 1.0;
                b.Cursor = Cursors.Hand;
                // 松开：回到悬停态（鼠标还在磁贴上），而不是透明 ——
                // 否则刚点完会闪一下，像"点完就灭了"
                if (!ReferenceEquals(b, selectedTile)) b.Background = Ui.Hover;
                if (tileDrag)
                {
                    tileDrag = false;
                    // 顺序很重要：先提交（要用 insertIndex），再收标记。
                    // 第一版写反了 —— EndDragVisual 把 insertIndex 重置成 -1，
                    // CommitDrag 拿到的永远是 -1，拖拽完全不生效（实测日志
                    // "CommitDrag ... target=-1" 而 DragPreview 明明算出了 idx=2）。
                    int target = insertIndex;
                    EndDragVisual();
                    CommitDrag(a, target);
                }
                else
                {
                    Launch(a);
                }
            };
            b.MouseRightButtonUp += delegate { ShowTileMenu(a); };
            return b;
        }

        /// <summary>
        /// 拖拽开始：把整个 tilePanel 的磁贴位置快照下来，
        /// 供"实时让位"计算每个磁贴该偏移多少。
        /// </summary>
        void BeginDragVisual(AppItem dragged)
        {
            try
            {
                if (tilePanel == null) return;
                draggedApp = dragged;
                if (insertCanvas != null) insertCanvas.Visibility = Visibility.Visible;

                // 磁贴实际列数 = 面板宽度 / 磁贴宽度
                tileColumns = Math.Max(1, (int)(tilePanel.ActualWidth / (TileWidthPx + 4)));
            }
            catch { }
        }

        /// <summary>
        /// 拖动中：更新插入标记位置 + **让磁贴实时滑开**（GlassBoard 的 livePreview）。
        ///
        /// 落点用**网格坐标**（列/行）算，不遍历子元素边界。
        /// 原实现用 `Mouse.GetPosition` 逐个判断 `p.X >= tl.X && p.X <= tl.X+width`，
        /// 有两个致命问题：
        ///   · 空状态时 tilePanel.Children[0] 是 StackPanel 不是磁贴，index 对不上
        ///   · 鼠标落在磁贴间隙时所有判断都失败 → target = -1（实测日志确实如此）
        ///
        /// 让位动画（这才是"丝滑"的关键）：
        /// 拖动时把 idx 之后的所有磁贴往右推一格，空出落点位置；
        /// 用 TranslateTransform + 120ms EaseOut 滑过去，而不是瞬间跳。
        /// 松手后 RenderApps 重建，位移归零。
        /// </summary>
        void UpdateDragPreview(AppItem dragged, Point p)
        {
            try
            {
                if (tilePanel == null || insertCanvas == null || insertMarker == null) return;
                if (renderedApps == null || renderedApps.Count == 0) return;

                double cellW = TileWidthPx + 4;    // 磁贴宽 + 左右 Margin
                double cellH = TileHeightPx + 4;
                int cols = Math.Max(1, tileColumns);

                int col = (int)Math.Floor(Math.Max(0, p.X) / cellW);
                int row = (int)Math.Floor(Math.Max(0, p.Y) / cellH);
                if (col < 0) col = 0;
                if (col >= cols) col = cols - 1;
                if (row < 0) row = 0;

                int idx = row * cols + col;
                if (idx > renderedApps.Count) idx = renderedApps.Count;
                if (idx == insertIndex) return;    // 没变化就不重画，省开销

                int prev = insertIndex;
                insertIndex = idx;

                // ---- 插入标记位置（胶囊竖条）----
                double mx = col * cellW - 2;
                double my = row * cellH + 6;
                Canvas.SetLeft(insertMarker, mx);
                Canvas.SetTop(insertMarker, my);
                insertMarker.Height = Math.Max(24, TileHeightPx - 12);
                insertMarker.Visibility = Visibility.Visible;

                // ---- 磁贴让位（丝滑的核心）----
                AnimateTilesForInsert(idx, prev);
            }
            catch { }
        }

        /// <summary>
        /// 让 idx 之后的磁贴往右滑一格，给落点腾位置。
        ///
        /// 实现方式：给每个磁贴的**内容**挂一个 TranslateTransform，
        /// 按"是否需要让位"设偏移量，用 120ms EaseOut 动画过渡。
        /// 为什么动画作用在内容而不是 Border 本身：
        /// Border 是 WrapPanel 的直接子元素，动它会影响布局测量；
        /// 内容用 RenderTransform 则完全不碰布局，只是视觉上滑过去。
        ///
        /// 为什么不真的改 Children 顺序做"真实占位"：
        /// 拖动过程中每改一次顺序都要重建磁贴（重新取图标 = 卡顿），
        /// 而视觉上"滑开"已经足够说明落点，松手时一次性提交即可。
        /// </summary>
        void AnimateTilesForInsert(int idx, int prev)
        {
            try
            {
                if (tilePanel == null || renderedApps == null) return;

                int from = renderedApps.IndexOf(draggedApp);
                double shift = TileWidthPx + 4;   // 一格的宽度

                for (int i = 0; i < renderedApps.Count && i < tilePanel.Children.Count; i++)
                {
                    Border b = tilePanel.Children[i] as Border;
                    if (b == null) continue;

                    // 拖动项自己不动（它跟着鼠标，由 Opacity 表示）
                    if (i == from) continue;

                    // 让位规则：
                    //   拖动项往前移（from < idx）→ [from, idx) 的磁贴左移一格
                    //   拖动项往后移（from > idx）→ [idx, from) 的磁贴右移一格
                    double dx = 0;
                    if (from >= 0 && from < idx && i >= from && i < idx) dx = -shift;
                    else if (from >= 0 && from > idx && i >= idx && i < from) dx = shift;

                    FrameworkElement content = b.Child as FrameworkElement;
                    if (content == null) continue;

                    TranslateTransform tt = content.RenderTransform as TranslateTransform;
                    if (tt == null || tt.IsFrozen)
                    {
                        tt = new TranslateTransform();
                        content.RenderTransform = tt;
                    }

                    // 目标位置没变就不动它（避免每帧重启动画）
                    double cur = tt.X;
                    if (Math.Abs(cur - dx) < 0.5) continue;

                    DoubleAnimation anim = new DoubleAnimation(dx, TimeSpan.FromMilliseconds(120));
                    anim.EasingFunction = FrozenCubic();
                    tt.BeginAnimation(TranslateTransform.XProperty, anim);
                }
            }
            catch { }
        }

        /// <summary>当前被拖动的磁贴（让位计算要跳过它）。</summary>
        AppItem draggedApp;

        /// <summary>拖拽结束：收起标记，把让位产生的位移平滑归零。</summary>
        void EndDragVisual()
        {
            try
            {
                insertIndex = -1;
                draggedApp = null;
                if (insertMarker != null) insertMarker.Visibility = Visibility.Collapsed;
                if (insertCanvas != null) insertCanvas.Visibility = Visibility.Collapsed;

                // 让位位移归零。
                // 为什么要动画而不是直接设 0：松手后 RenderApps 会重建磁贴，
                // 但在"取消拖拽"（落点无效）的情况下不重建 ——
                // 那时如果位移瞬间归零，会看到磁贴"啪"地跳回原位。
                // 用 120ms 滑回去，与让位时的动画对称。
                if (tilePanel != null)
                {
                    foreach (UIElement u in tilePanel.Children)
                    {
                        Border b = u as Border;
                        if (b == null) continue;
                        FrameworkElement content = b.Child as FrameworkElement;
                        if (content == null) continue;
                        TranslateTransform tt = content.RenderTransform as TranslateTransform;
                        if (tt == null) continue;
                        if (Math.Abs(tt.X) < 0.5) continue;

                        DoubleAnimation back = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(120));
                        back.EasingFunction = FrozenCubic();
                        tt.BeginAnimation(TranslateTransform.XProperty, back);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 提交拖拽：按插入下标重排 renderedApps，写入 customOrders 并重渲染。
        ///
        /// target 由调用方传入（在 EndDragVisual 重置之前取好的值）。
        /// </summary>
        void CommitDrag(AppItem a, int target)
        {
            try
            {
                int from = renderedApps == null ? -1 : renderedApps.IndexOf(a);
                Ui.Log("CommitDrag " + a.Name + " from=" + from + " target=" + target);

                if (target < 0) return;
                if (from < 0) return;
                if (from == target) return;

                List<AppItem> newOrder = new List<AppItem>(renderedApps);
                AppItem moved = newOrder[from];
                newOrder.RemoveAt(from);

                // 目标下标是"移除前"的位置，移除后要修正：
                // 若目标在源之后，删掉源会让目标前移一位
                if (target > from) target--;
                if (target < 0) target = 0;
                if (target > newOrder.Count) target = newOrder.Count;

                newOrder.Insert(target, moved);

                List<string> paths = new List<string>();
                foreach (AppItem it in newOrder) paths.Add(it.Path);
                customOrders[current] = paths;
                SaveCustomOrders();
                RenderApps();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "提交拖拽");
            }
        }

        /// <summary>当前磁贴区的列数（拖动落点计算用）。</summary>
        int tileColumns = 1;

        void DropTile(AppItem a)
        {
            try
            {
                if (tilePanel == null || renderedApps == null || renderedApps.Count == 0) return;
                Point p = Mouse.GetPosition(tilePanel);
                int target = -1;
                int n = Math.Min(renderedApps.Count, tilePanel.Children.Count);
                for (int i = 0; i < n; i++)
                {
                    FrameworkElement fe = tilePanel.Children[i] as FrameworkElement;
                    if (fe == null) continue;
                    Point tl = fe.TranslatePoint(new Point(0, 0), tilePanel);
                    if (p.X >= tl.X && p.X <= tl.X + fe.ActualWidth && p.Y >= tl.Y && p.Y <= tl.Y + fe.ActualHeight)
                    {
                        target = i;
                        break;
                    }
                }
                int from = renderedApps.IndexOf(a);
                Ui.Log("DropTile " + a.Name + " from=" + from + " target=" + target + " n=" + n);
                if (target < 0) return;
                if (from < 0 || from == target) return;
                List<AppItem> newOrder = new List<AppItem>(renderedApps);
                AppItem moved = newOrder[from];
                newOrder.RemoveAt(from);
                if (target > newOrder.Count) target = newOrder.Count;
                newOrder.Insert(target, moved);
                List<string> paths = new List<string>();
                foreach (AppItem it in newOrder) paths.Add(it.Path);
                customOrders[current] = paths;
                SaveCustomOrders();
                RenderApps();
            }
            catch { }
        }

        void ShowTileMenu(AppItem a)
        {
            if (a == null || string.IsNullOrEmpty(a.Path)) return;
            ContextMenu cm = new ContextMenu();

            // ---- 打开文件所在位置（所有分类都有）----
            MenuItem openDir = new MenuItem();
            openDir.Header = "打开文件所在位置";
            openDir.Click += delegate { OpenContainingFolder(a); };
            cm.Items.Add(openDir);

            // ---- 移到分类 →（子菜单）----
            // 结果存 config\category-overrides.json，优先级高于关键词规则，
            // 重新扫描后依然保持。
            MenuItem moveTo = new MenuItem();
            moveTo.Header = "移到分类";
            foreach (string cat in MovableCategories())
            {
                string target = cat;
                MenuItem sub = new MenuItem();
                sub.Header = target;
                bool isCurrent = string.Equals(a.Category, target, StringComparison.Ordinal);
                sub.IsChecked = isCurrent;
                sub.IsEnabled = !isCurrent; // 已在当前分类就没必要再移
                sub.Click += delegate { MoveAppToCategory(a, target); };
                moveTo.Items.Add(sub);
            }

            // 已经有手工覆盖时，提供「恢复自动分类」
            string overridden = GetOverride(a.Path);
            if (overridden != null)
            {
                moveTo.Items.Add(new Separator());
                MenuItem reset = new MenuItem();
                reset.Header = "恢复自动分类";
                reset.Click += delegate { MoveAppToCategory(a, null); };
                moveTo.Items.Add(reset);
            }

            cm.Items.Add(moveTo);

            // ---- 隐藏 / 取消隐藏（对所有分类都有）----
            // 「移到分类」是"归错类了，换个位置"；「隐藏」是"完全不想看到"。
            // 两者用途不同，所以并列提供。
            cm.Items.Add(new Separator());
            if (a.Hidden)
            {
                MenuItem show = new MenuItem();
                show.Header = "取消隐藏";
                show.Click += delegate { SetAppHidden(a, false); };
                cm.Items.Add(show);
            }
            else
            {
                MenuItem hide = new MenuItem();
                hide.Header = "从面板隐藏";
                hide.ToolTip = "藏起来，不在任何分类里显示；可在「隐藏的项目」里找回";
                hide.Click += delegate { SetAppHidden(a, true); };
                cm.Items.Add(hide);
            }

            // ---- 从常用移除（只在「常用」分类出现）----
            if (showingCommon)
            {
                cm.Items.Add(new Separator());

                MenuItem rm = new MenuItem();
                rm.Header = "从常用移除";
                rm.IsEnabled = a.Usage > 0;
                rm.Click += delegate
                {
                    if (usage != null) usage.Remove(a.Path);
                    a.Usage = 0;
                    a.LastUsed = 0;
                    RebuildNav();
                    RenderApps();
                };
                cm.Items.Add(rm);
            }

            taskMenuOpen = true;
            cm.Closed += delegate { taskMenuOpen = false; };
            cm.Placement = PlacementMode.MousePoint;
            cm.IsOpen = true;
        }

        /// <summary>可选的目标分类：默认分类 + 当前已存在的其它分类。</summary>
        List<string> MovableCategories()
        {
            List<string> list = new List<string>();
            foreach (string c in Catalog.DefaultOrder)
            {
                if (!list.Contains(c)) list.Add(c);
            }
            // 补上数据里出现过、但不在默认表里的分类（例如用户自己加过的）
            try
            {
                if (catalog != null && catalog.Order != null)
                {
                    foreach (string c in catalog.Order)
                    {
                        if (!string.IsNullOrEmpty(c) && !list.Contains(c)) list.Add(c);
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>读某应用的覆盖分类；无覆盖返回 null。</summary>
        string GetOverride(string appPath)
        {
            try
            {
                return overrides == null ? null : overrides.Get(appPath);
            }
            catch { return null; }
        }

        /// <summary>
        /// 把应用移到指定分类（category 传 null 表示恢复自动分类）。
        /// 写入 category-overrides.json，然后重新加载目录并刷新界面。
        /// </summary>
        void MoveAppToCategory(AppItem a, string category)
        {
            try
            {
                if (a == null || string.IsNullOrEmpty(a.Path)) return;
                if (overrides == null) overrides = CategoryOverrides.Load(baseDir);

                bool changed;
                if (string.IsNullOrEmpty(category))
                {
                    changed = overrides.Remove(a.Path);
                    Ui.Log("恢复自动分类: " + a.Name);
                }
                else
                {
                    changed = overrides.Set(a.Path, category);
                    Ui.Log("移到分类: " + a.Name + " -> " + category);
                }

                if (!changed) return;

                // 重新加载目录（会重新应用覆盖表），保持当前选中分类
                ReloadCatalog();
                RebuildNav();
                RenderApps();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "移到分类");
            }
        }

        /// <summary>
        /// 隐藏 / 取消隐藏一个应用。
        /// 写入 hidden-apps.json，标记内存中的条目，然后刷新界面。
        /// </summary>
        void SetAppHidden(AppItem a, bool hidden)
        {
            try
            {
                if (a == null || string.IsNullOrEmpty(a.Path)) return;
                if (hiddenApps == null) hiddenApps = HiddenApps.Load(baseDir);

                bool changed = hidden ? hiddenApps.Hide(a.Path) : hiddenApps.Show(a.Path);
                if (!changed) return;

                // 就地改标记即可，不必全量重载目录：
                // Catalog.Load 也会重新读 hidden-apps.json，但那样会丢掉 usage 的本次更新。
                a.Hidden = hidden;
                Ui.Log((hidden ? "隐藏" : "取消隐藏") + "应用: " + a.Name);

                // 取消隐藏最后一个后，「隐藏的项目」分类会消失——
                // 若正停在那里，得先离开，否则界面停在空分类上。
                if (!hidden && current == HiddenCategory && catalog.HiddenItems().Count == 0)
                {
                    current = "";
                    SaveLastCategory();
                }

                RebuildNav();
                RenderApps();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "隐藏应用");
            }
        }

        /// <summary>
        /// 在资源管理器里打开该应用所在目录并选中它。
        /// 应用路径可能是 .exe（便携识别）或 .lnk（开始菜单），两种情况都要能定位。
        /// </summary>
        void OpenContainingFolder(AppItem a)
        {
            try
            {
                string path = a == null ? null : a.Path;
                if (string.IsNullOrEmpty(path))
                {
                    MessageBox.Show("这个应用没有可定位的路径。", "四叶草面板");
                    return;
                }

                string arg;
                string err;
                if (!TryBuildExplorerArgs(path, out arg, out err))
                {
                    MessageBox.Show(err, "四叶草面板");
                    return;
                }

                Process.Start("explorer.exe", arg);
                Ui.Log("打开文件位置: " + path + "  参数: " + arg);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "打开文件所在位置");
                try
                {
                    MessageBox.Show("无法打开文件位置：" + ex.Message, "四叶草面板");
                }
                catch { }
            }
        }

        /// <summary>
        /// 根据应用路径生成 explorer.exe 的参数（纯逻辑，可单测）。
        ///
        /// 三种情况：
        ///   1. 是存在的文件（.exe / .lnk 解析后的目标）→ `/select,"目标"` 打开父目录并选中
        ///   2. 文件不存在但目录还在 → `"目录"` 直接打开目录
        ///   3. 都不行 → 返回 false，err 里是给用户看的提示
        ///
        /// .lnk 会先解析真实目标；解析失败就退回到 .lnk 自身所在目录。
        /// </summary>
        public static bool TryBuildExplorerArgs(string path, out string args, out string error)
        {
            args = null;
            error = null;

            if (string.IsNullOrEmpty(path))
            {
                error = "这个应用没有可定位的路径。";
                return false;
            }

            string target = path;
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string real = ResolveShortcutTarget(path);
                if (!string.IsNullOrEmpty(real) && File.Exists(real)) target = real;
            }

            // 情况 1：文件存在 → 打开父目录并选中它
            if (File.Exists(target))
            {
                args = "/select,\"" + target + "\"";
                return true;
            }

            // 情况 2：文件没了，但目录还在 → 直接打开目录
            string dir = Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(dir)) dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                args = "\"" + dir + "\"";
                return true;
            }

            // 情况 3：都找不到
            error = "找不到这个文件：\r\n" + target;
            return false;
        }

        /// <summary>解析 .lnk 的目标路径；无效或解析失败返回 null（不会返回空字符串）。</summary>
        public static string ResolveShortcutTarget(string lnk)
        {
            try
            {
                if (string.IsNullOrEmpty(lnk)) return null;
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                object shell = Activator.CreateInstance(shellType);
                object sc = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                if (sc == null) return null;
                object tp = sc.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.GetProperty, null, sc, null);
                string s = tp as string;
                // WScript.Shell 对无效/不存在的快捷方式返回空字符串而不是 null，
                // 这里统一成 null，避免调用方误以为"解析成功但目标为空"。
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch
            {
                return null;
            }
        }
    }
}
