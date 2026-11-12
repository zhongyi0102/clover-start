using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>分类导航、内容层切换与键盘选中协调。</summary>
    partial class PanelWindow
    {
        /// <summary>键盘选中的应用磁贴（null 表示没有选中）。</summary>
        Border selectedTile;
        /// <summary>键盘选中项在 renderedApps 中的下标，-1 表示没有选中。</summary>
        int selectedIndex = -1;

        // ---- 磁贴 ↔ 应用下标的显式映射 ----
        //
        // ── 为什么需要它（这是一次真实修复，不是重构洁癖）──
        //
        // 以前 `tilePanel.Children[i]` 被当成 `renderedApps[i]` 的磁贴，这个约定
        // 没有写在任何类型里，只存在于三处代码的假设中：
        //   · SelectTile            用 Children[index] 取磁贴
        //   · DropTile              用 Children[i] 的几何求落点，再把 i 当应用下标
        //   · 拖动排序用 Children[i] 做落点与换位
        // 另外旧清理逻辑曾遍历 Children 靠 `as Border` 过滤，空状态时
        // Children[0] 是 StackPanel 而不是磁贴（注释里已记过这个坑）。
        //
        // 一旦往 Children 里插入任何**非磁贴元素**（低价值段的节标题、
        // 展开/收起行），三处会同时错位：选中高亮到错误磁贴、拖拽落点整体偏移、
        // 排序错位。所以折叠功能的前置条件就是先把映射显式化。
        //
        // 修法：Children 退回**纯渲染容器**的角色，谁想往里塞什么都行；
        // 所有索引依赖改走下面两个字段。
        //
        // **不变式（必须始终成立）**：
        //   renderedTiles.Count == renderedApps.Count
        //   且 renderedTiles[i] 就是 renderedApps[i] 的磁贴。

        /// <summary>与 renderedApps 一一对应的磁贴。索引即应用索引，与 Children 无关。</summary>
        readonly List<Border> renderedTiles = new List<Border>();

        /// <summary>磁贴 → 应用下标。拖动/选中用它反查，不再用 Children.IndexOf。</summary>
        readonly Dictionary<Border, int> tileAppIndex = new Dictionary<Border, int>();

        sealed class TileIconState
        {
            public AppItem App;
            public Grid Box;
            public FrameworkElement Visual;
            public TransformGroup Transform;
            public int Generation;
            public bool WantsImage;
            public bool RequestPending;
            public bool Unavailable;
        }

        /// <summary>
        /// 每块磁贴的图标挂载状态。磁贴本身始终存在以保持键盘/拖拽索引稳定，
        /// 真实 Image 只在视口前后一屏内挂载，远处恢复首字占位以释放位图引用。
        /// </summary>
        readonly Dictionary<Grid, TileIconState> tileIconStates =
            new Dictionary<Grid, TileIconState>();
        readonly Dictionary<Border, TileIconState> tileIconStatesByTile =
            new Dictionary<Border, TileIconState>();

        bool viewportIconRefreshScheduled;

        /// <summary>每批创建的磁贴数；一批控制在一帧内，批次之间允许输入与绘制插队。</summary>
        const int TileRenderBatchSize = 14;

        /// <summary>每次重渲染递增。旧批次和旧图标回调看到不同代次就立即退出。</summary>
        int tileRenderGeneration;

        /// <summary>当前代次是否仍在分批创建磁贴。</summary>
        bool tileRenderPending;

        /// <summary>本代次最终要渲染的应用快照；renderedApps 只放已经创建好的磁贴。</summary>
        List<AppItem> pendingRenderApps;

        /// <summary>正常段结束下标；低价值节标题必须恰好插在这里。</summary>
        int pendingNormalCount;

        int pendingLowCount;
        bool pendingCollapseLow;
        string pendingQuery = "";
        bool pendingSearching;
        int usageRevision;

        /// <summary>
        /// 悬停时图标放大的比例（纯视觉，不改布局）。
        ///
        /// 1.12 → 1.25：参考 GlassBoard 的 Dock 悬停（它用 1.0→1.6）。
        /// 磁贴是 96px 宽的网格项，不像 Dock 条那样有"把邻居挤开"的空间，
        /// 1.6 会明显盖住相邻磁贴的图标。1.25 是"看得出放大但不越界"的平衡点。
        /// </summary>
        const double IconHoverScale = 1.06;

        /// <summary>
        /// 悬停时图标**上浮**的像素数。
        ///
        /// 这是 Dock 最标志性的移动效果（GlassBoard 用 -5px × MotionLevel）。
        /// 配合放大一起做，才像"图标被拎起来了"；
        /// 只放大不上浮会显得"在原地胀大"，比较闷。
        ///
        /// 用负值是往上（WPF 的 Y 轴向下为正）。
        /// </summary>
        const double IconLiftPx = 1.0;

        /// <summary>
        /// 按下态只移动 1px，并控制在 80ms 内。启动应用属于高频操作，
        /// 不使用会抢注意力的弹跳动画。
        /// </summary>
        const double IconPressPx = 1.0;

        /// <summary>
        /// 拖拽松手后的落入动画时长（毫秒）。
        ///
        /// 高频 MouseMove 路径不做动画；只有松手这一刻用 200ms EaseOut
        /// 表达“放到该位置”，既保留空间反馈，也不拖慢上下跨行移动。
        /// </summary>
        const int DragDropMs = 200;

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
        /// 按下反馈。松手/离开时仍沿用悬停复位动画。
        /// </summary>
        static readonly DoubleAnimation IconPress = FrozenShift(-IconLiftPx, IconPressPx, 80);

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

                // 下标语义是**应用下标**（renderedApps 的下标），不是 Children 的下标。
                // 空状态时 renderedTiles 为空，越界检查在这里自然返回 —— 不会像以前
                // 那样把 Children[0] 的 StackPanel 当磁贴。
                if (index < 0 || index >= renderedTiles.Count) return;
                if (renderedApps == null || index >= renderedApps.Count) return;
                Border tile = renderedTiles[index];
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

            // ★ 只保留两个标签：「常用」「全部」。
            //
            // 为什么砍掉其余（办公/开发/…/桌面/收藏夹/壁纸/隐藏的项目）：
            // 分类越多，找应用时越要在标签之间来回切；而「全部」配上
            // 拼音搜索已经能覆盖绝大多数场景。实测 119 个应用平铺后
            // 一屏能看 40+ 个，翻两屏就到头了。
            //
            // 注意：Catalog / CategoryRules / CategoryOverrides / Categories
            // 这些**类和数据都保留**（下一阶段再清理），只是界面不再展示。
            List<string> cats = new List<string>();
            cats.Add("常用");
            cats.Add("");

            // 第三个标签「文件」（showFilesTab 关闭时不加）
            //
            // **这里不算数量**：文件数要读磁盘，而 RebuildNav 每次切换分类
            // 都会跑。算数量会让每次点标签都卡一下 —— 所以文件标签不显示计数。
            bool hasFilesTab = settings == null || settings.ShowFilesTab;
            if (hasFilesTab) cats.Add(FilesCategory);

            // 默认内置「虚拟桌面」：始终存在，不需要用户点 + 创建。
            // 放在「文件」之后、所有额外自定义桌面之前。
            cats.Add(VirtualDesktopCategory);

            // 用户自定义标签（+ 号建的），排在内置「虚拟桌面」之后
            if (customTabs != null)
            {
                foreach (NavTabs.Tab t in customTabs.Items)
                {
                    if (t == null || string.IsNullOrEmpty(t.Name)) continue;
                    // 旧版本的「桌面 / 虚拟桌面」会在首次进入桌面收纳时安全迁移。
                    // 检查完成前先隐藏别名，避免导航同时出现“桌面”和“桌面收纳”；
                    // 若检测到两边都有文件，迁移会保留旧项并在检查后重新显示。
                    if (!legacyDeskMigrationChecked && IsLegacyDesktopName(t.Name)) continue;
                    if (cats.Contains(t.Name)) continue;
                    cats.Add(t.Name);
                }
            }

            foreach (string cat in cats)
            {
                string c = cat;
                // 显示名
                string label = DisplayCategoryName(c);

                // 分类项：横向胶囊按钮
                Button item = new Button();
                item.Height = 30;
                item.Margin = new Thickness(0, 2, 4, 2);
                item.Padding = new Thickness(10, 0, 10, 0);
                item.Background = Brushes.Transparent;
                item.BorderBrush = Brushes.Transparent;
                item.BorderThickness = new Thickness(0);
                item.Template = MakeButtonTemplate(item.Height / 2);
                item.Cursor = Cursors.Hand;

                Grid g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                // 左侧竖条已废弃，但保留元素（宽度 0）——
                // navBars 列表被 UpdateNav 直接索引，删掉会破坏下标对应。
                g.ColumnDefinitions[0].Width = new GridLength(0);
                g.ColumnDefinitions[1].Width = GridLength.Auto;

                Border bar = new Border();
                bar.Width = 0;
                bar.Height = 0;
                bar.Visibility = Visibility.Collapsed;
                Grid.SetColumn(bar, 0);
                g.Children.Add(bar);

                TextBlock name = new TextBlock();
                name.Text = label;
                name.FontFamily = Ui.YaHei;
                name.FontSize = 12;
                name.Foreground = Ui.Fg;
                name.VerticalAlignment = VerticalAlignment.Center;
                // 模板不转发 Button.Padding；名称自己留出间距，去掉数字后也不挤成一串。
                name.Margin = new Thickness(10, 0, 10, 0);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(name, 1);
                g.Children.Add(name);

                TextBlock cnt = new TextBlock();
                // 数量统一放在右下角，保留元素仅为了维持既有导航映射。
                cnt.Text = "";
                cnt.Visibility = Visibility.Collapsed;
                cnt.FontFamily = Ui.YaHei;
                cnt.FontSize = 10.5;
                cnt.Foreground = Ui.Sub;
                cnt.VerticalAlignment = VerticalAlignment.Center;
                cnt.Margin = new Thickness(5, 0, 0, 0);
                Grid.SetColumn(cnt, 2);
                g.Children.Add(cnt);

                item.Content = g;
                item.SnapsToDevicePixels = true;
                AutomationProperties.SetName(item, label + "标签");
                item.MouseEnter += delegate
                {
                    if (current != c && !item.IsKeyboardFocused) item.Background = Ui.Hover;
                };
                item.MouseLeave += delegate
                {
                    if (current != c && !item.IsKeyboardFocused) item.Background = Brushes.Transparent;
                };
                item.Click += delegate
                {
                    // 先把当前分类的滚动位置存下来（必须在改 current 之前）
                    SaveScrollOffset();
                    SwitchCategory(c);
                };
                if (IsDeskTab(c))
                {
                    DispatcherTimer reveal = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    reveal.Tick += delegate
                    {
                        reveal.Stop();
                        SwitchCategory(c);
                        dragOverPanel = true;
                    };
                    item.AllowDrop = true;
                    item.DragEnter += delegate(object sender, DragEventArgs e)
                    {
                        string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                        if (paths == null || paths.Length == 0 || HasAnyInternalDrag(e)) return;
                        foreach (string path in paths) if (!DeskImporter.IsFromDesktop(path)) return;
                        if (!reveal.IsEnabled) reveal.Start();
                    };
                    item.DragLeave += delegate { reveal.Stop(); };
                    item.Drop += delegate { reveal.Stop(); };
                    item.Unloaded += delegate { reveal.Stop(); };
                }
                item.GotKeyboardFocus += delegate
                {
                    if (current != c) item.Background = Ui.AccentSoft;
                };
                item.LostKeyboardFocus += delegate
                {
                    if (current != c) item.Background = Brushes.Transparent;
                };

                // 右键：只有额外自定义桌面能改名/删除；内置四个不给菜单
                if (NavTabs.IsReserved(c) || c == "")
                {
                    item.ToolTip = null;
                }
                else
                {
                    item.ToolTip = "右键：重命名 / 删除";
                    item.MouseRightButtonUp += delegate(object s2, MouseButtonEventArgs e2)
                    {
                        e2.Handled = true;
                        ShowTabMenu(c, item);
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

        /// <summary>
        /// 保持导航数量隐藏，不销毁并重建整条导航栏。
        /// 保留既有刷新调用；应用数量只由右下角统计呈现。
        /// </summary>
        void RefreshNavCountsInPlace()
        {
            try
            {
                for (int i = 0; i < navCats.Count && i < navCounts.Count; i++)
                {
                    TextBlock countText = navCounts[i];
                    if (countText == null) continue;

                    countText.Text = "";
                    countText.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "就地刷新标签数量");
            }
        }


        static string DisplayCategoryName(string category)
        {
            if (string.IsNullOrEmpty(category)) return "全部";
            if (category == FilesCategory) return "文件库";
            if (category == VirtualDesktopCategory) return "桌面收纳";
            return category;
        }

        void UpdateNav()
        {
            if (rescanAppsButton != null)
                rescanAppsButton.Visibility = current == FilesCategory || IsDeskTab(current)
                    ? Visibility.Collapsed : Visibility.Visible;
            if (refreshFilesButton != null)
                refreshFilesButton.Visibility = current == FilesCategory ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < navItems.Count; i++)
            {
                bool sel = navCats[i] == current;
                // 胶囊的选中态用**实色强调底 + 白字**（目标图就是这样）：
                // 竖排列表时代用的是"淡色底 + 左侧竖条"，那套在横条上不成立 ——
                // 横条没有左边缘可言，竖条只会悬在胶囊里很怪。
                navItems[i].Background = sel ? Ui.Accent : Brushes.Transparent;
                navBars[i].Visibility = Visibility.Collapsed;   // 竖条已废弃
                navNames[i].Foreground = sel ? Brushes.White : Ui.Fg;
                navNames[i].FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal;
                if (i < navCounts.Count && navCounts[i] != null)
                {
                    navCounts[i].Foreground = sel ? Brushes.White : Ui.Sub;
                    navCounts[i].Opacity = sel ? 0.85 : 1.0;
                }
            }
        }

        /// <summary>让搜索语义与底部快捷提示随当前页面变化。</summary>
        void UpdateContextChrome()
        {
            try
            {
                string searchName;
                string hint;
                string footer;
                if (current == FilesCategory)
                {
                    searchName = "筛选文件库";
                    hint = "筛选当前文件来源…";
                    footer = "文件库 · 双击打开 · Ctrl 多选 · Delete 移到回收站";
                }
                else if (IsDeskTab(current))
                {
                    searchName = "搜索桌面收纳";
                    hint = "搜索桌面收纳中的文件…";
                    footer = "桌面收纳 · 桌面拖入默认移动，Ctrl 复制 · 框选／自由摆放 · Ctrl+Z 撤销摆放";
                }
                else
                {
                    searchName = "搜索应用";
                    hint = "搜索应用（支持拼音 / 首字母）…";
                    footer = "单击选中 · 双击启动 · 拖动排序 · 方向键选择 · Enter 启动";
                }

                if (searchHint != null) searchHint.Text = hint;
                if (searchBox != null) AutomationProperties.SetName(searchBox, searchName);
                if (footerHint != null) footerHint.Text = footer;
            }
            catch (Exception ex) { Ui.LogError(ex, "更新页面提示"); }
        }

        /// <summary>当前已渲染内容的签名，用于判断是否真的需要重建磁贴。</summary>
        string renderedSig = "";

        /// <summary>
        /// 内容区四个互斥层。**同一时间只能有一个可见**。
        ///
        /// 为什么用枚举而不是四个 bool：四层各自 Visible/Collapsed 的组合有 16 种，
        /// 手动维护必然漏 —— 加「文件」标签时就漏了把 filesPanel 设回 Collapsed，
        /// 结果切回「常用」后文件页还压在上面。用枚举 + 单一入口，
        /// 新增层时只需在这里加一个 case，不可能漏。
        /// </summary>
        enum ContentLayer
        {
            /// <summary>应用磁贴（常用 / 全部）。</summary>
            Tiles,
            /// <summary>全局搜索结果列表。</summary>
            SearchResults,
            /// <summary>「文件」标签面板。</summary>
            Files,
            /// <summary>虚拟桌面图标层（自定义标签）。</summary>
            Desks,
        }

        /// <summary>
        /// 切换分类（常用 / 全部 / 文件 / 虚拟桌面 / 额外桌面）。**所有切换入口都走这里**，
        /// 不要各自写 `current = x; RenderApps();` —— 那样必然漏掉状态隔离。
        ///
        /// 做三件事：
        ///   1. 让离开的标签释放自己的瞬时状态（文件标签清选择/过滤/缓存）
        ///   2. 清空搜索框 —— 否则在文件页搜过 "abc"，切到「常用」会用 "abc" 过滤应用
        ///   3. 重建标签选中态 + 渲染新标签
        /// </summary>
        void SwitchCategory(string cat)
        {
            try
            {
                string next = cat ?? "";
                if (DeskTransferPending) { ShowToast("正在转移桌面内容，请完成后再切换页面"); return; }
                bool wasFiles = (current == FilesCategory);
                bool willFiles = (next == FilesCategory);
                if (current != next) SaveScrollOffset();

                // ---- 1. 离开的标签释放状态 ----
                if (wasFiles && !willFiles && filesView != null)
                    filesView.OnLeave();

                // ---- 2. 清空搜索框（跨标签不共用搜索词）----
                //   searchSuppress 防止 TextChanged 触发一次多余渲染
                if (searchBox != null && searchBox.Text.Length > 0)
                {
                    searchSuppress = true;
                    try { searchBox.Text = ""; }
                    finally { searchSuppress = false; }
                    if (searchHint != null) searchHint.Visibility = Visibility.Visible;
                }

                // ---- 3. 切换 + 渲染 ----
                current = next;
                if (contentScroll != null)
                    contentScroll.HorizontalScrollBarVisibility = IsDeskTab(next)
                        ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
                SaveLastCategory();
                UpdateNav();
                UpdateContextChrome();

                // 文件搜索属于文件库自身；返回时恢复，不带入应用或桌面。
                if (willFiles && filesView != null)
                {
                    filesView.OnEnter();
                    if (searchBox != null)
                    {
                        searchSuppress = true;
                        try { searchBox.Text = filesView.FilterQuery; }
                        finally { searchSuppress = false; }
                        if (searchHint != null) searchHint.Visibility = searchBox.Text.Length == 0
                            ? Visibility.Visible : Visibility.Collapsed;
                    }
                }

                RenderApps();
                if (!wasFiles && willFiles && filesView != null) filesView.RestorePageScroll();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "切换分类");
            }
        }

        void ShowContentLayer(ContentLayer layer)

        {
            try
            {
                if (tilePanel != null)
                    tilePanel.Visibility = layer == ContentLayer.Tiles
                        ? Visibility.Visible : Visibility.Collapsed;

                if (searchResultPanel != null)
                    searchResultPanel.Visibility = layer == ContentLayer.SearchResults
                        ? Visibility.Visible : Visibility.Collapsed;

                if (filesPanel != null)
                    filesPanel.Visibility = layer == ContentLayer.Files
                        ? Visibility.Visible : Visibility.Collapsed;

                if (deskPanel != null)
                    deskPanel.Visibility = layer == ContentLayer.Desks
                        ? Visibility.Visible : Visibility.Collapsed;

                if (deskCanvas != null)
                    deskCanvas.Visibility = layer == ContentLayer.Desks
                        ? Visibility.Visible : Visibility.Collapsed;

                if (deskOverlay != null)
                    deskOverlay.Visibility = layer == ContentLayer.Desks
                        ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "切换内容层");
            }
        }

        void RenderFilesTab()
        {
            try
            {
                StopDeferredRendering();
                EnsureFilesSurface();
                // 三者互斥：只显示文件层
                ShowContentLayer(ContentLayer.Files);
                if (searchResults != null) searchResults.Clear();
                if (footerCount != null) footerCount.Text = ""; // 文件数量由文件视图更新，不能沿用应用统计。

                // 首次创建 FilesTabView
                if (filesView == null && filesPanel != null)
                {
                    filesView = new FilesTabView(this, settings, baseDir,
                        filesChipRow, filesExtRow, filesListPanel, filesNotice,
                        filesUndoHost);
                    filesView.AttachStatus(footerCount);

                    // 注入放置提示层（FilesTabView 只拿容器引用，不负责建控件）
                    if (filesDropHint != null) filesView.AttachDropHint(filesDropHint);
                }

                if (filesView != null)
                {
                    filesView.OnEnter();
                    // 搜索词只传给文件视图做本地过滤
                    string q = searchBox == null ? "" : searchBox.Text.Trim();
                    filesView.ApplyFilter(q);
                    filesView.Refresh(false);
                }
                MarkFirstContentReady();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "渲染文件标签");
            }
        }

        /// <summary>
        /// 判定一个应用是否低价值。**手动标记优先于自动规则**：
        ///   · 在 LowValueUnmarked 里 → 一律不低价值（最高优先）
        ///   · 在 LowValueMarked 里   → 一律低价值
        ///   · 否则走 LowValueRules.Explain 自动判定
        ///
        /// 返回带原因的结构，供 ToolTip / 低价值清单窗口显示"为什么"。
        /// </summary>
        LowValueVerdict ExplainLowValue(AppItem a)
        {
            try
            {
                if (a == null || string.IsNullOrEmpty(a.Path)) return LowValueVerdict.NotLow;

                if (settings != null)
                {
                    if (settings.LowValueUnmarked != null)
                    {
                        foreach (string p in settings.LowValueUnmarked)
                            if (string.Equals(p, a.Path, StringComparison.OrdinalIgnoreCase))
                                return LowValueVerdict.NotLow;
                    }

                    if (settings.LowValueMarked != null)
                    {
                        foreach (string p in settings.LowValueMarked)
                            if (string.Equals(p, a.Path, StringComparison.OrdinalIgnoreCase))
                                return new LowValueVerdict(true, LowValueReason.ManualMarked, "已手动标记");
                    }
                }

                return a.Role == AppRole.LowValue
                    ? new LowValueVerdict(true, LowValueReason.ManualMarked, a.RoleDetail ?? "低价值")
                    : LowValueVerdict.NotLow;
            }
            catch { return LowValueVerdict.NotLow; }
        }


        /// <summary>
        /// 启动时校正分类：last-category.json 里可能记着已被删除的标签
        /// （办公 / 开发 / 桌面 / 壁纸 / 收藏夹 / 隐藏的项目 等）。
        ///
        /// 遇到不存在的分类一律回退到「常用」，不能报错也不能显示空白。
        /// 在 BuildUi 之后、首次渲染之前调用。
        /// </summary>
        // ========== 测试辅助（生产代码不调）==========
        //
        // AcceptanceTest 要验证标签行为，但 RebuildNav / current / LoadLastCategory
        // 都是 private。加这几个 internal 方法让测试能驱动它们，
        // 比反射稳（反射遇到重命名就静默失效）。

        /// <summary>测试用：临时开关「文件」标签并重建标签栏。</summary>
        internal void TestSetShowFilesTab(bool on)
        {
            try
            {
                if (settings != null) settings.ShowFilesTab = on;
                RebuildNav();
            }
            catch (Exception ex) { Ui.LogError(ex, "测试：设置 showFilesTab"); }
        }

        /// <summary>测试用：临时替换自定义桌面，验证导航换行；不写磁盘。</summary>
        internal void TestSetCustomTabs(IEnumerable<string> names)
        {
            try
            {
                if (customTabs == null) return;
                customTabs.Items.Clear();
                if (names != null)
                {
                    foreach (string raw in names)
                    {
                        string name = NavTabs.SanitizeName(raw);
                        if (name.Length == 0 || NavTabs.IsReserved(name)) continue;
                        if (customTabs.FindIndex(name) >= 0) continue;
                        if (customTabs.Items.Count >= NavTabs.MaxTabs) break;
                        customTabs.Items.Add(new NavTabs.Tab(name));
                    }
                }
                RebuildNav();
            }
            catch (Exception ex) { Ui.LogError(ex, "测试：设置自定义桌面"); }
        }

        /// <summary>测试用：切到某个分类。</summary>
        internal void TestSwitchCategory(string cat)
        {
            try
            {
                // 走生产路径，保证测试覆盖与真实切换一致
                SwitchCategory(cat);
            }
            catch (Exception ex) { Ui.LogError(ex, "测试：切换分类"); }
        }

        /// <summary>测试用：写入 last-category.json 并重读。</summary>
        internal void TestSetLastCategory(string cat)
        {
            try
            {
                if (string.IsNullOrEmpty(lastCatPath)) return;
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["category"] = cat ?? "";
                System.Web.Script.Serialization.JavaScriptSerializer ser =
                    new System.Web.Script.Serialization.JavaScriptSerializer();
                JsonIO.WriteAtomic(lastCatPath, ser.Serialize(d));
            }
            catch (Exception ex) { Ui.LogError(ex, "测试：写 last-category"); }
        }

        /// <summary>测试用：重读 last-category.json 并校正分类。</summary>
        internal void TestReloadLastCategory()
        {
            try
            {
                // 模拟「last-category.json 记着一个已不存在的标签」：
                // 直接把 current 设成那个值，再走 NormalizeCurrentCategory。
                // （不去调 LoadLastCategory —— 它遇到未知分类是「什么都不做」，
                //   current 保持调用前的值，那样测不出回退。）
                current = "不存在的分类XYZ";
                NormalizeCurrentCategory();
                UpdateNav();
            }
            catch (Exception ex) { Ui.LogError(ex, "测试：重读 last-category"); }
        }


        /// <summary>测试用：把按键转给文件视图，返回是否被消费。</summary>
        internal bool TestFilesHandleKey(Key key, ModifierKeys mods)
        {
            try
            {
                if (filesView == null) return false;
                return filesView.HandleKey(key, mods);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "测试：文件键盘");
                return false;
            }
        }

        /// <summary>测试用：预览是否打开。</summary>
        internal bool TestFilesPreviewOpen()
        {
            try { return filesView != null && filesView.IsPreviewOpen; }
            catch { return false; }
        }

        /// <summary>测试用：关预览。</summary>
        internal void TestFilesClosePreview()
        {
            try { if (filesView != null) filesView.ClosePreview(); }
            catch { }
        }

        /// <summary>测试用：文件列表里当前选中的项数。</summary>
        internal int TestFilesSelectedCount()
        {
            try { return filesView == null ? 0 : filesView.TestSelectedCount(); }
            catch { return 0; }
        }

        internal bool TestFilesSurfaceCreated()
        {
            return filesPanel != null;
        }

        /// <summary>测试用：清空 file-folders.json（模拟"没有任何来源"）。</summary>
        internal void TestClearFileFolders()
        {
            try
            {
                string p = FileFolders.PathOf(baseDir);
                if (File.Exists(p)) File.Delete(p);
                if (File.Exists(p + ".bak")) File.Delete(p + ".bak");
                if (filesView != null) filesView.ReloadFolders();
            }
            catch (Exception ex) { Ui.LogError(ex, "测试：清空 file-folders"); }
        }

        /// <summary>测试用：添加一个来源目录。</summary>
        internal void TestAddFileFolder(string path)
        {
            try
            {
                FileFolders f = FileFolders.Load(baseDir);
                f.Add(path);
                if (filesView != null) filesView.ReloadFolders();
            }
            catch (Exception ex) { Ui.LogError(ex, "测试：添加来源"); }
        }

        /// <summary>测试用：读文件标签的提示文字。</summary>
        internal string TestFilesNotice()
        {
            try { return filesNotice == null ? "" : (filesNotice.Text ?? ""); }
            catch { return ""; }
        }

        /// <summary>测试用：文件列表行数。</summary>
        internal int TestFilesRowCount()
        {
            try { return filesView == null ? 0 : filesView.TestRowCount(); }
            catch { return 0; }
        }

        /// <summary>测试用：来源胶囊数。</summary>
        internal int TestFilesChipCount()
        {
            try { return filesChipRow == null ? 0 : filesChipRow.Children.Count; }
            catch { return 0; }
        }

        /// <summary>
        /// 测试用：把失效路径塞进来源多选，返回 NormalizeSource 之后的有效来源列表。
        /// 多选语义下：失效路径会被剔除；全被剔除后为空 = 全选。
        /// </summary>
        internal List<string> TestNormalizeFilesSource(string bogusPath)
        {
            List<string> result = new List<string>();
            try
            {
                if (filesView == null)
                {
                    string old = current;
                    current = FilesCategory;
                    RenderApps();
                    current = old;
                }
                if (filesView == null) return result;

                // 只留失效路径 → ReloadFolders 触发 NormalizeSource 把它剔除
                if (settings != null)
                {
                    settings.FilesSources = new List<string>();
                    settings.FilesSources.Add(bogusPath);
                }
                filesView.TestSetSources(new List<string> { bogusPath });
                filesView.ReloadFolders();
                result = filesView.TestGetSources();
                return result;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "测试：修正来源多选");
                return result;
            }
        }

        /// <summary>测试用：文件面板是否可见。</summary>
        internal bool TestFilesPanelVisible()
        {
            try
            {
                return filesPanel != null && filesPanel.Visibility == Visibility.Visible;
            }
            catch { return false; }
        }

        /// <summary>测试用：桌面页是否可见。</summary>
        internal bool TestDeskPanelVisible()
        {
            try { return deskPanel != null && deskPanel.Visibility == Visibility.Visible; }
            catch { return false; }
        }

        /// <summary>测试用：读当前分类。</summary>
        internal string TestCurrentCategory()
        {
            try { return current ?? ""; }
            catch { return ""; }
        }

        void NormalizeCurrentCategory()
        {
            try
            {
                // 只有「常用」和「全部」是合法值
                // 合法值：常用 / 全部(空串) / 文件（且 showFilesTab 开着）
                if (current == "常用" || current == "") return;

                if (current == FilesCategory)
                {
                    // showFilesTab 被关掉时，「文件」不再是合法标签 → 回退常用
                    if (settings != null && !settings.ShowFilesTab)
                    {
                        Ui.Log("「文件」标签已关闭，回退到常用");
                        current = "常用";
                        SaveLastCategory();
                    }
                    return;
                }

                // 默认内置「虚拟桌面」永远合法；目录不在就自动重建。
                if (current == VirtualDesktopCategory)
                {
                    try
                    {
                        EnsureVirtualDesks();
                        if (virtualDesks != null) virtualDesks.EnsureDesk(VirtualDesktopCategory);
                    }
                    catch { }
                    return;
                }

                // 自定义标签（虚拟桌面）：只要还在 nav-tabs.json 里就算合法。
                //
                // 不能把它当成「已不存在的分类」：它不对应 catalog 分类，
                // 对应的是磁盘文件夹。目录不在就自动建，不要回退到「常用」。
                if (customTabs != null && customTabs.FindIndex(current) >= 0)
                {
                    // 目录丢了（被手删 / 换盘）→ 重建空目录，标签仍然有效
                    try
                    {
                        if (virtualDesks == null)
                        {
                            virtualDesks = new VirtualDesks(baseDir);
                            virtualDesks.Load();
                        }
                        if (string.IsNullOrEmpty(virtualDesks.Root))
                            virtualDesks.Root = VirtualDesks.DefaultRoot(baseDir);

                        virtualDesks.EnsureDesk(current);
                    }
                    catch { }
                    return;
                }

                Ui.Log("分类「" + current + "」已不存在，回退到常用");
                current = "常用";
                SaveLastCategory();
            }
            catch { }
        }


        /// <summary>当前磁贴区的列数（拖动落点计算用）。</summary>
        int tileColumns = 1;


        /// <summary>桌面移除提示条（与文件页分开，避免互相覆盖）。</summary>
        UndoBar undoBarForDesktop;
        /// <summary>
        /// 单个隐藏后的轻提示：「已隐藏：xxx　[撤销]」。
        ///
        /// 复用 UndoBar（不抢焦点、5 秒消失、点按钮后焦点回到列表）。
        /// 撤销 = 取消隐藏。
        /// </summary>
        void ShowHiddenTip(AppItem a)
        {
            try
            {
                if (a == null) return;

                UndoBar generalBar = EnsureGeneralUndoBar();
                if (generalBar == null) return;

                AppItem captured = a;
                generalBar.Show("已隐藏：" + (a.Name ?? ""), delegate
                {
                    try { SetAppHidden(captured, false); }
                    catch (Exception ex) { Ui.LogError(ex, "撤销隐藏"); }
                });
            }
            catch (Exception ex) { Ui.LogError(ex, "显示隐藏提示"); }
        }

        /// <summary>磁贴右键菜单里「常用」相关的动作。</summary>
        internal enum CommonMenuAction
        {
            /// <summary>不显示任何常用相关项。</summary>
            None = 0,
            /// <summary>「从最近常用移除」——保留历史，直到下次真实启动前不再显示。</summary>
            DismissRecent
        }

    }
}
