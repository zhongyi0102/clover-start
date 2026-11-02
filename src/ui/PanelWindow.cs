using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 主面板。为便于排查，按职责拆成 partial 文件：
    ///   PanelWindow.cs         窗口生命周期、显示/隐藏、布局存取、启动应用
    ///   PanelWindow.Ui.cs      控件搭建（BuildUi 及通用小控件）
    ///   PanelWindow.Nav.cs     左侧分类、搜索、应用磁贴渲染与拖拽排序
    ///   PanelWindow.Tasks.cs   左侧“任务栏”列表（当前打开的窗口）
    ///   PanelWindow.Data.cs    目录/配置加载、刷新、对外接口
    /// </summary>
    partial class PanelWindow : Window
    {
        string baseDir;
        TriggerWindow trigger;
        AppWatcher watcher;

        /// <summary>文件监听器（TriggerWindow 打开桌面设置时需要它来切换监听）。</summary>
        internal AppWatcher Watcher
        {
            get { return watcher; }
        }
        Settings settings;
        Catalog catalog;
        string current = "";
        DateTime suppressDeactivate = DateTime.MinValue;
        /// <summary>程序内部清空搜索框时置 true，避免触发一次多余的全量重渲染。</summary>
        bool searchSuppress;
        /// <summary>面板是否至少被真正激活过一次；从未激活时不要自动收起。</summary>
        bool everActivated;
        string layoutPath;
        /// <summary>上次浏览的分类（重启后恢复到它，见 config\last-category.json）。</summary>
        string lastCatPath;
        Border searchBorder;
        Border minBtn;              // 最小化（与关闭同行为：HidePanel）
        Border closeBtn;
        UsageStore usage;
        List<string> hiddenCats;
        bool taskMenuOpen;
        MiniTaskbarWindow miniBar;
        bool showingCommon;
        Dictionary<string, List<string>> customOrders = new Dictionary<string, List<string>>();
        List<AppItem> renderedApps = new List<AppItem>();
        string orderPath;
        Border rootBorder;
        Border maxBtn;
        bool expanded;
        /// <summary>
        /// 面板外边距（root 的 Margin），供 PositionPanel 算展开/还原尺寸。
        ///
        /// 注意：缩放命中测试**不再用它** —— 那套判定已改成屏幕坐标
        /// 直接和 GetWindowRect 比较，见 OnResizeHitTest。
        /// </summary>
        double edgeMargin = 16;
        StackPanel taskStack;
        DispatcherTimer taskTimer;

        // ---- 拖放检测（防止拖拽中途被自动收起）----
        //
        // 问题：从资源管理器/桌面拖文件到面板时，焦点离开面板 → Deactivated
        // → 250ms 后 HidePanel()，拖放目标消失。
        //
        // 对策（B 为主 + A 收尾）：
        //   · B：Deactivated 时若左键仍按住，改用 80ms 轮询，等松手再判断
        //   · A：DragEnter/DragOver 置 dragOverPanel，Drop/DragLeave 清除
        //
        // 为什么要 B：Deactivated 发生在 DragEnter **之前**，
        // 那时 dragOverPanel 还是 false，光靠 A 挡不住。
        DispatcherTimer dragWatchTimer;

        /// <summary>拖拽正在面板上方（DragEnter 置位，Drop/DragLeave 清除）。</summary>
        bool dragOverPanel;

        /// <summary>轮询起点，用于兜底超时。</summary>
        DateTime dragWatchStart;

        /// <summary>兜底：轮询最多持续这么久，避免标志卡死导致面板再也不收。</summary>
        static readonly TimeSpan DragWatchLimit = TimeSpan.FromSeconds(30);

        /// <summary>拖拽经过时的虚线高亮层。</summary>
        Border dropHighlight;

        StackPanel navStack;

        /// <summary>用户自定义标签（+ 号新建的），持久化在 config\nav-tabs.json。</summary>
        NavTabs customTabs;

        /// <summary>旧标签迁移冲突提示（面板首次显示时弹一次）。</summary>
        string deskMigrationNotice;
        DeskMigrationNotice deskMigrationNoticeStore;

        /// <summary>收纳/放回桌面的撤销历史（最近 5 批，持久化）。</summary>
        DeskUndo deskUndo;

        // ---- 虚拟桌面（自定义标签）----
        Canvas deskCanvas;
        Canvas deskOverlay;
        VirtualDesks virtualDesks;
        DesktopView deskView;
        WrapPanel tilePanel;
        StackPanel deskToolRow;        // 「桌面文件」页顶部按钮行
        TextBlock deskNotice;          // 该页的提示行
        Grid deskPanel;                // 该页整体容器（按钮 + 提示 + 画布）

        // ---- 「文件」标签（2-3）----
        StackPanel filesPanel;         // 整个文件标签的容器
        StackPanel filesChipRow;       // 来源胶囊行
        StackPanel filesExtRow;        // 第二行：后缀筛选
        StackPanel filesUndoHost;      // 底部撤销提示条容器
        TextBlock filesNotice;         // 提示行
        StackPanel filesListPanel;     // 文件列表
        FilesTabView filesView;        // 逻辑

        /// <summary>
        /// 「拖入被跳过项」的提示条。懒创建，挂在跨标签的 `toastHost` 上。
        ///
        /// 单独一个实例而不是复用 `undoBarForDesktop`：两者可能几乎同时显示
        /// （拖入被跳过 + 桌面快捷方式被移除），共用一个会把前一句顶掉。
        /// </summary>
        UndoBar dropTipBar;

        /// <summary>跨标签提示条宿主（footer 上方，不占布局空间）。</summary>
        StackPanel toastHost;

        /// <summary>
        /// 「桌面文件」页的空状态提示层。懒建在 `PanelWindow.Ui.cs` 的 BuildUi 里，
        /// 常驻，只切 Visibility。
        /// </summary>
        DropHint deskDropHint;

        /// <summary>「文件」标签的来源放置提示层（常驻，只切 Visibility）。</summary>
        DropHint filesDropHint;
        TextBox searchBox;
        TextBlock searchHint;
        TextBlock footerCount;

        /// <summary>
        /// 分类栏容器（顶部横条）。
        ///
        /// 布局从"左侧竖栏"改成"顶部横条"后，原来的 navColumn
        /// （ColumnDefinition，折叠时改宽度）已经不存在了 ——
        /// 横条折叠靠隐藏 navScroll，没有"宽度"这个概念。
        /// 字段名保留为 navGrid，是因为折叠、徽标、选中态等
        /// 二十多处逻辑都引用它；角色仍是"分类导航容器"。
        /// </summary>
        Grid navGrid;
        /// <summary>分类胶囊的容器（折叠时隐藏它）。</summary>
        ScrollViewer navScroll;
        /// <summary>侧栏下半部分（任务栏列表，折叠时隐藏）。</summary>
        Grid taskGrid;
        /// <summary>侧栏收起/展开的切换按钮。</summary>
        Border navToggle;
        /// <summary>侧栏是否已收起。</summary>
        bool navCollapsed;

        /// <summary>磁贴区的滚动容器（记住滚动位置要用）。</summary>
        ScrollViewer contentScroll;

        /// <summary>
        /// 各分类的滚动偏移（分类名 -> 垂直偏移）。
        /// 按分类分别记：切走再回来还在原处，而不是全局共用一个值。
        /// </summary>
        Dictionary<string, double> scrollOffsets = new Dictionary<string, double>();

        /// <summary>
        /// 程序自己要恢复滚动位置时置 true，避免那次位移被当成"用户滚动"记下来。
        /// 不设它的话：恢复动作本身会覆盖掉刚读到的值，多切几次就漂了。
        /// </summary>
        bool suppressScrollRecord;

        /// <summary>
        /// <summary>
        /// 毛玻璃背景（截桌面 → 缩 1/8 → 模糊 → 垫底）。
        /// 由 BuildUi 创建；软件渲染时内部自动禁用。
        /// </summary>
        GlassBackdrop glassBackdrop;

        /// <summary>视频背景层（Wallpaper Engine video 类）。</summary>
        VideoBackdrop videoBackdrop;

        /// <summary>背景暗度遮罩（叠在背景之上、内容之下）。</summary>
        Border darkOverlay;

        /// <summary>搜索结果列表容器（与 tilePanel 互斥显示）。</summary>
        StackPanel searchResultPanel;
        /// <summary>搜索结果的逻辑控制（Instant + Deep 两级）。</summary>
        SearchResultList searchResults;

        /// <summary>
        /// 每块磁贴的图标容器（应用 → 容器），供就地刷新"运行中"小圆点用。
        ///
        /// 为什么不靠重渲染：一块磁贴约 6 个可视化元素，119 个就是 700+ 控件，
        /// 而运行状态每 1.5 秒可能变一次。为了几颗 8px 的小点重建整棵树不值当。
        /// 这里只增删一颗小圆点，代价是 O(应用数) 的字典查表，且不触发布局。
        ///
        /// 为什么记全部磁贴而不是只记"有小圆点的"：
        /// 应用可能在面板开着期间**刚启动**（比如你点了 Chrome），
        /// 这时需要**补画**圆点 —— 只记已有的就无法补。
        /// </summary>
        readonly Dictionary<string, Grid> tileIconBoxes =
            new Dictionary<string, Grid>(StringComparer.OrdinalIgnoreCase);

        List<string> navCats = new List<string>();
        List<Border> navItems = new List<Border>();
        List<Border> navBars = new List<Border>();
        List<TextBlock> navNames = new List<TextBlock>();
        List<TextBlock> navCounts = new List<TextBlock>();

        Rect normalRect = Rect.Empty;

        /// <summary>用户手工指定的分类覆盖表（右键「移到分类」写入）。</summary>
        CategoryOverrides overrides;

        /// <summary>被隐藏的应用清单（右键「从面板隐藏」写入）。</summary>
        HiddenApps hiddenApps;

        /// <summary>分类的顺序与显示名（右键分类「重命名 / 上移下移」写入）。</summary>
        // Categories categories 字段已删除（分类系统下线，无调用方）

        /// <summary>
        /// 「隐藏的项目」虚拟分类的标识。
        /// 用不可见字符开头，确保和任何真实分类名都不会撞。
        /// </summary>
        public const string HiddenCategory = "\u0001hidden";

        /// <summary>
        /// 拖拽插入标记的覆盖层（Canvas，绝对定位）。
        ///
        /// 为什么用独立 Canvas 而不是往 tilePanel 里插元素：
        /// WrapPanel 会把任何子元素当成一个格子参与排版，
        /// 插进去会让磁贴位置跳动。Canvas 是绝对坐标，不影响布局。
        /// </summary>
        Canvas insertCanvas;
        Border insertMarker;

        /// <summary>当前拖动会插入到哪个下标（-1 = 无）。</summary>
        int insertIndex = -1;

        /// <summary>
        /// 是否需要在下次显示时定位。
        ///
        /// 构造函数里 TaskbarGeometry 还没探测完（StartCell = Rect.Empty），
        /// 那时算位置会走兜底分支、跑到工作区左下角。
        /// 所以把首次定位延后到 ShowPanel，用这个标记保证只做一次。
        /// </summary>
        bool needPosition;

        /// <summary>
        /// 拖动时跟随鼠标的"浮层"磁贴（幽灵）。
        ///
        /// 为什么需要它：原来的实现里，被拖的磁贴**留在原位**、只是变半透明，
        /// 用户拖动时看到的是"插入标记在动、磁贴在原地不动"，
        /// 完全不符合"图标跟着手走"的直觉。
        ///
        /// 做法：拖动开始时把磁贴的样子复制一份到 Canvas 上（绝对定位），
        /// 每帧跟着鼠标移动；原磁贴保持半透明当"占位幽灵"。
        /// 松手后浮层平滑飞到目标格，然后隐藏、重建列表。
        /// </summary>
        Border dragGhost;
        /// <summary>浮层里的图标（缩放用）。</summary>
        ScaleTransform dragGhostScale;
        /// <summary>浮层当前的 X/Y（用 TranslateTransform，避免每帧改 Canvas.Left 触发重排）。</summary>
        TranslateTransform dragGhostPos;
        /// <summary>拖动开始时鼠标相对磁贴中心的偏移，保证浮层不跳。</summary>
        Point dragGrabOffset;

        public PanelWindow(string baseDir, TriggerWindow trigger, Settings settings)
        {
            this.baseDir = baseDir;
            this.trigger = trigger;
            this.settings = settings ?? Settings.Load(baseDir);
            this.overrides = CategoryOverrides.Load(baseDir);
            this.hiddenApps = HiddenApps.Load(baseDir);
            catalog = Catalog.Load(baseDir);            usage = UsageStore.Load(baseDir);
            // 分类系统已下线：settings 里不再有 hiddenCategories，
            // 逐条隐藏走 hiddenApps（HiddenApps.Load 已在上面加载）。
            // HideCategories 仍保留（通用 API + 有测试），但传空列表 = 不过滤。
            hiddenCats = new List<string>();
            catalog.ApplyUsage(usage);
            catalog.HideCategories(hiddenCats);
            layoutPath = Path.Combine(baseDir, "config", "layout.json");
            lastCatPath = Path.Combine(baseDir, "config", "last-category.json");
            orderPath = Path.Combine(baseDir, "config", "order.json");
            LoadCustomOrders();

            // 用户自定义标签：先加载，RebuildNav 会读它
            customTabs = new NavTabs(baseDir);
            customTabs.Load();

            // 默认内置「桌面文件」启动即存在：不等用户首次点击标签才建目录。
            try
            {
                virtualDesks = new VirtualDesks(baseDir);
                virtualDesks.Load();
                if (string.IsNullOrEmpty(virtualDesks.Root))
                    virtualDesks.Root = VirtualDesks.DefaultRoot(baseDir);

                deskMigrationNoticeStore = new DeskMigrationNotice(baseDir);
                deskMigrationNoticeStore.Load();

                // 兼容历史名称（「虚拟桌面」「桌面」）：安全迁移到内置「桌面文件」。
                // 目标非空时绝不合并，只记录冲突提示。
                MigrateLegacyVirtualDesktop();

                virtualDesks.EnsureDesk(VirtualDesktopCategory);
                virtualDesks.Save();

                deskUndo = new DeskUndo(baseDir);
                deskUndo.Load();
            }
            catch (Exception ex) { Ui.LogError(ex, "初始化默认虚拟桌面"); }

            LoadLastCategory();

            // 后台预热"应用是否在运行"要用的 .lnk 目标缓存。
            //
            // 首次解析 112 个快捷方式实测 239 ms（每个 1-3 ms 的 COM 调用）。
            // 放在这里（而不是等用户打开面板）是因为：面板首次渲染时若缓存没热，
            // 就只画不出小圆点，等预热完成由 1.5 秒的定时器补上 ——
            // 用户看不到卡顿，最多第一眼少几个点。
            // WarmUpAsync 内部起线程，不会阻塞构造函数。
            WarmRunningCache();

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 640;
            MinHeight = 440;
            ShowInTaskbar = false;
            Topmost = true;
            Title = "四叶草面板";
            Width = 912;
            Height = 632;
            WindowStartupLocation = WindowStartupLocation.Manual;

            // 首次定位延后到 ShowPanel，不在这里做。
            //
            // 为什么：PositionPanel() 依赖 TaskbarGeometry.CloverRect()，
            // 而那个值来自 UIAutomation 异步探测 —— 构造函数执行时
            // StartCell 还是 Rect.Empty，于是走兜底分支，
            // 面板跑到工作区左下角（实测 @(88,530)），而不是按钮旁边。
            //
            // 延后到 ShowPanel 时几何已就绪，定位才准确。
            // 用 needPosition 标记"这次是首次显示"，避免每次打开都重定位
            // （用户拖过面板后位置存在 layout.json 里，不能被覆盖）。
            needPosition = !LoadLayout();
            try
            {
                BuildUi();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "构建面板界面");
            }

            // ★ 启动时应用一次完整的背景设置。
            //
            // 为什么必须有这一步：以前只在「外观…」窗口里调 ApplyGlassSetting()，
            // 所以重启程序后背景一直是默认状态 —— WE 壁纸/自定义图/视频都不生效，
            // 用户必须打开一次外观窗口才"活过来"。
            try { ApplyGlassSetting(); }
            catch (Exception ex) { Ui.LogError(ex, "启动时应用背景设置"); }

            // 校正分类：last-category.json 里可能记着已删除的标签
            // （办公/开发/桌面/壁纸/收藏夹/隐藏的项目…），一律回退到「常用」。
            // 必须在 BuildUi 之后（navItems 已建好）调用。
            NormalizeCurrentCategory();

            // 任务栏几何从"估算"变成"精确"后，重新定位面板。
            //
            // 为什么需要：首次 PositionPanel() 时 UIA 探测还没回来，
            // StartCell 是估算值 (0, sh-48)，于是面板被放到估算位置；
            // 探测完成后真实按钮位置才可用（实测差 99px）。
            // 这里在回调里再定位一次，误差归零。
            //
            // 只对"从未精确定位过"的面板生效：用户手动拖过位置的话，
            // layout.json 里存的是他的选择，不能被自动定位覆盖。
            TaskbarGeometry.GeometryRefined = delegate
            {
                try
                {
                    if (!IsVisible) return;          // 没显示就不用管
                    if (!needPosition) return;       // 用户已手动定位过
                    PositionPanel();
                    Ui.Log("几何精确化后重新定位面板");
                }
                catch { }
            };

            Deactivated += delegate(object s, EventArgs e)
            {
                Ui.Log("Deactivated active=" + IsActive + " everActivated=" + everActivated
                    + " suppress=" + (DateTime.Now < suppressDeactivate) + " menu=" + taskMenuOpen
                    + " lbtn=" + Native.IsLeftButtonDown() + " dragOver=" + dragOverPanel);
                if (taskMenuOpen) return;
                if (DateTime.Now < suppressDeactivate) return;
                if (trigger != null && trigger.IsActive) return;

                // ★ 左键仍按住 → 可能正在拖拽（从桌面/资源管理器拖文件进来），
                //   不能立刻走隐藏。改为短周期轮询，等松手再判断。
                if (Native.IsLeftButtonDown())
                {
                    Ui.Log("左键按住，启动拖放轮询");
                    StartDragWatch();
                    return;
                }

                ScheduleAutoHide();
            };
            Activated += delegate
            {
                everActivated = true;
                Ui.Log("Activated");
                // 面板被激活时刷新前台窗口缓存：此刻鼠标通常还没移到任务栏列表上，
                // 先把缓存更新好，划过时高亮就是对的（避免读到最多 1.5 秒前的旧值）。
                RefreshActiveCache();
            };
            // ---- 背景自适应：尺寸或位置变化都要重取景 ----
            //
            // 「跟随桌面」截的是窗口底下的桌面；固定图/自定义/WE scene
            // 是按面板在屏幕上的相对位置从大图裁一块。
            // 只听 SizeChanged 的话，拖动面板（尺寸不变）时取景不更新。
            // 250ms 合并：拖动中背景静止，松手约 1/4 秒后跟上。
            // ---- 拖放接线（A 部分：只做事件与高亮，不处理 Drop 的移动逻辑）----
            //
            // 真正的拖入/拖出（移动文件、落点判断）留给第 2 步。
            // 这里的作用只有一个：在拖拽经过时置 dragOverPanel，
            // 防止 Deactivated 的自动收起把面板收走。
            // ---- 拖放接线（A 部分：只做事件与高亮，不处理 Drop 的移动逻辑）----
            //
            // 事件挂在**窗口**上，不挂 Content：挂 Content 时拖过不同子元素
            // 会连续冒泡出 Enter/Leave；挂窗口则整个客户区只有一个拖放目标。
            AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs e) { OnPanelDragEnter(e); };
            DragOver += delegate(object s, DragEventArgs e) { OnPanelDragOver(e); };
            DragLeave += delegate(object s, DragEventArgs e) { OnPanelDragLeave(e); };
            Drop += delegate(object s, DragEventArgs e) { OnPanelDrop(e); };
            SizeChanged += delegate { ScheduleBackdropReframe(); };
            LocationChanged += delegate { ScheduleBackdropReframe(); };

            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                // ---- 「文件」标签优先接管键盘 ----
                //
                // 为什么放在最前：文件列表的 ↑↓/Enter/空格/Delete 语义与磁贴完全不同，
                // 必须先给它机会处理，处理不了（返回 false）再落到下面的磁贴逻辑。
                //
                // 搜索框里有焦点时不接管（否则空格会被当预览、Delete 会被当删除）：
                // 用户正在输入，键盘属于输入框。唯一例外是 Esc —— 仍走分层。
                bool inSearchBox = searchBox != null && searchBox.IsKeyboardFocusWithin;

                if (current == FilesCategory && filesView != null)
                {
                    // Esc：分层处理（预览 → 搜索框 → 选择 → 收起面板）
                    if (e.Key == Key.Escape)
                    {
                        // 1) 预览打开 → 先关预览
                        if (filesView.IsPreviewOpen)
                        {
                            filesView.ClosePreview();
                            e.Handled = true;
                            return;
                        }
                        // 2) 搜索框有内容 → 清空
                        if (searchBox != null && searchBox.Text.Length > 0)
                        {
                            searchSuppress = true;
                            try { searchBox.Text = ""; }
                            finally { searchSuppress = false; }
                            if (searchHint != null) searchHint.Visibility = Visibility.Visible;
                            filesView.ApplyFilter("");
                            e.Handled = true;
                            return;
                        }
                        // 3) 有选中 → 清选择
                        if (filesView.HandleKey(e.Key, Keyboard.Modifiers))
                        {
                            e.Handled = true;
                            return;
                        }
                        // 4) 都清完了 → 交给下面（收起面板）
                    }
                    else if (!inSearchBox)
                    {
                        // 非 Esc 且焦点不在搜索框 → 交给文件列表
                        if (filesView.HandleKey(e.Key, Keyboard.Modifiers))
                        {
                            e.Handled = true;
                            return;
                        }
                    }
                }

                if (e.Key == Key.Escape)
                {
                    HidePanel();
                    e.Handled = true;
                    return;
                }

                // 搜索框里：方向键交给光标移动，只在上下/回车时接管，
                // 这样输入文字时按左右键不会跳磁贴选中。
                bool inSearch = searchBox != null && searchBox.IsKeyboardFocusWithin;
                switch (e.Key)
                {
                    case Key.Right:
                    case Key.Left:
                        if (inSearch) return;
                        if (MoveSelection(e.Key)) e.Handled = true;
                        return;
                    case Key.Up:
                    case Key.Down:
                    case Key.Home:
                    case Key.End:
                        if (MoveSelection(e.Key)) e.Handled = true;
                        return;
                    case Key.Enter:
                        if (LaunchSelected()) e.Handled = true;
                        return;
                }
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr h = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(h, -20, Native.GetWindowLong(h, -20) | 0x80);
            HwndSource.FromHwnd(h).AddHook(OnResizeHitTest);
        }

        /// <summary>
        /// 面板四边四角缩放。
        ///
        /// ── 为什么改用屏幕坐标 ──
        ///
        /// 旧实现用 `PointFromScreen` 得到**客户区坐标**，再和 `edgeMargin`
        /// （窗口外边距 16）比较 —— 坐标系错位：
        ///   · 真正的窗口边缘客户区坐标 ≈ 0，判断不成立 → HTCLIENT
        ///   · 只有「客户区坐标 ≈ 16」那一圈碰巧命中
        /// 实测结果就是「只有左边缘内 16px 能拖，右/上/下全废」。
        ///
        /// 现在直接用屏幕坐标和 GetWindowRect 比较，窗口最外圈 6px 就是缩放区，
        /// 和外观窗口用的是同一套判定。
        /// </summary>
        IntPtr OnResizeHitTest(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_NCHITTEST = 0x0084;
            if (msg != WM_NCHITTEST) return IntPtr.Zero;

            try
            {
                // 最大化/展开状态下不接管，交给系统（那里本来就没有可拖的边框）
                if (expanded) return IntPtr.Zero;

                long lp = lParam.ToInt64();
                int sx = (short)(lp & 0xFFFF);
                int sy = (short)((lp >> 16) & 0xFFFF);

                Native.RECT r;
                if (!Native.GetWindowRect(hwnd, out r)) return IntPtr.Zero;

                const int Band = 6;   // 缩放热区宽度（物理像素）

                bool left = sx <= r.Left + Band;
                bool right = sx >= r.Right - Band;
                bool top = sy <= r.Top + Band;
                bool bottom = sy >= r.Bottom - Band;

                // 点在窗口外时不接管
                if (sx < r.Left || sx > r.Right || sy < r.Top || sy > r.Bottom)
                    return IntPtr.Zero;

                int ht = 0;
                if (left && top) ht = Native.HTTOPLEFT;
                else if (right && top) ht = Native.HTTOPRIGHT;
                else if (left && bottom) ht = Native.HTBOTTOMLEFT;
                else if (right && bottom) ht = Native.HTBOTTOMRIGHT;
                else if (left) ht = Native.HTLEFT;
                else if (right) ht = Native.HTRIGHT;
                else if (top) ht = Native.HTTOP;
                else if (bottom) ht = Native.HTBOTTOM;

                if (ht == 0) return IntPtr.Zero;

                handled = true;
                return new IntPtr(ht);
            }
            catch { }

            return IntPtr.Zero;
        }
        void PositionPanel()
        {
            Rect wa = SystemParameters.WorkArea;
            // edgeMargin 参与展开/还原时的尺寸计算（见下方 expanded 分支）
            double em = edgeMargin;

            // 面板位置**跟随四叶草按钮**，而不是硬编码。
            //
            // 原先写的是 `Left = 55 + 6 - 16`，即假设"开始按钮固定占屏幕 0-55px"。
            // 但四叶草按钮的真实位置来自 TaskbarGeometry（UIAutomation 探测 +
            // 三级回退），两者用不同参考系 —— 任务栏不在底部、或开始按钮不居中时，
            // 面板就会和按钮错位（按钮在 A 处，面板从 B 处开始）。
            //
            // 现在统一：面板左缘 = 四叶草按钮右缘 + 间距，纵向贴任务栏。
            try
            {
                // ★ 优先用「四叶草按钮的真实右边缘」定位。
                //
                // 为什么不用 TaskbarGeometry.CloverRect()：
                // 那套基于"开始按钮格"，算出来 x≈59；
                // 而四叶草按钮实际贴在**任务栏图标组末尾**（实测 x=411）。
                // 两套机制不一致，面板和按钮会错位 350px。
                //
                // TriggerWindow 已经用 UIA 探测出了真实位置，这里直接复用，
                // 保证"面板从按钮右边开始"这件事真的成立。
                double buttonRight;
                if (TriggerWindow.TryGetButtonRight(out buttonRight))
                {
                    if (TaskbarGeometry.CurrentEdge == TaskbarGeometry.Edge.Bottom)
                    {
                        // ⚠ TryGetButtonRight 返回的是四叶草按钮的**左缘** X
                        // （它就是 TriggerWindow 用来设置 Left 的那个值），
                        // 所以要先加按钮宽度才是"右缘"。
                        // 实测：按钮 Left=411、Width=44 → 右缘 455；
                        // 不加宽度会算出 Left=401，面板压在按钮上（差 54px）。
                        double btnW = 44;
                        Left = buttonRight + btnW + 6 - 16;   // 16 = root 的 Margin
                        Top = wa.Bottom - Height + 10;
                        if (Top < 0) Top = 0;
                        if (Left < 0) Left = 0;
                        return;
                    }
                }

                Rect clover = TaskbarGeometry.CloverRect();
                if (!clover.IsEmpty && clover.Width > 0)
                {
                    switch (TaskbarGeometry.CurrentEdge)
                    {
                        case TaskbarGeometry.Edge.Top:
                            Left = clover.X - 16;
                            Top = wa.Top + TaskbarGeometry.Thickness + 6;
                            break;
                        case TaskbarGeometry.Edge.Left:
                            Left = wa.Left + TaskbarGeometry.Thickness + 6;
                            Top = clover.Bottom + 6;
                            break;
                        case TaskbarGeometry.Edge.Right:
                            Left = wa.Right - Width - 6;
                            Top = clover.Bottom + 6;
                            break;
                        default:   // Bottom
                            Left = clover.Right + 6 - 16;   // 16 = root 的 Margin
                            Top = wa.Bottom - Height + 10;
                            break;
                    }
                    if (Top < 0) Top = 0;
                    if (Left < 0) Left = 0;
                    return;
                }
            }
            catch { }

            // 兜底：拿不到按钮位置时用工作区右下角（不依赖"开始按钮在 0-55"这个假设）
            Left = wa.Left + 8;
            Top = wa.Bottom - Height + 10;
            if (Top < 0) Top = 0;
            if (Left < 0) Left = 0;
        }

        public void ShowPanel()
        {
            // 抑制窗口必须在 Show() 之前武装：Show() 会同步派发 Deactivated，
            // 之前放在 Show() 之后赋值，导致第一次 Deactivated 完全绕过抑制、面板秒收。
            suppressDeactivate = DateTime.Now.AddMilliseconds(600);

            // 清空搜索框会触发 TextChanged -> OnSearchChanged -> RenderApps，
            // 所以先解除事件、清空、再重挂，避免每次打开面板都强制重建全部磁贴。
            if (searchBox != null && searchBox.Text.Length > 0)
            {
                searchSuppress = true;
                try { searchBox.Text = ""; }
                finally { searchSuppress = false; }
            }
            if (searchHint != null) searchHint.Visibility = Visibility.Visible;

            // 先刷前台窗口缓存（不受枚举限流影响），再决定要不要全量刷新任务列表。
            // RefreshTaskList 有 800ms 最小间隔，可能整段被跳过；那样缓存的句柄就是旧的，
            // 面板刚打开时划过任务栏会高亮错行。
            RefreshActiveCache();
            RefreshTaskList();
            // 只有在内容可能变化时才重建磁贴：反复开关面板不必每次销毁/重建
            // 113 个控件（会持续推高内存，实测 30 次开关涨 100+ MB）。
            RenderAppsIfNeeded();

            if (!IsVisible)
            {
                // 首次显示前才定位：此时 TaskbarGeometry 已探测完毕
                if (needPosition)
                {
                    // 注意：这里**不清** needPosition。
                    // 首次定位用的是估算几何值（UIA 探测是异步的，还没回来），
                    // 要让 GeometryRefined 回调有机会再精修一次。
                    // 用户手动拖动面板时 SaveLayout 才把 needPosition 清掉。
                    PositionPanel();
                }
                Opacity = 0;
                Show();
            }
            Activate();
            IntPtr h = new WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero) Native.SetForegroundWindow(h);

            // 复用同一个 reveal 动画对象，避免每次显示都新建 DoubleAnimation
            // （实测反复开关面板会造成托管堆持续增长）。
            // 用 Ui.ReusableEnter：带 EaseOut 缓动，且**未冻结**
            // —— 因为下面每回合都要改 From（从当前透明度起）。
            if (showAnim == null)
            {
                showAnim = Ui.ReusableEnter(1, 150);
            }
            showAnim.From = 0;
            BeginAnimation(OpacityProperty, showAnim);

            // 轻微上移：淡入的同时从下方 8px 升到原位，让面板"活"一下。
            //
            // 用 rootBorder 的 RenderTransform 而不是改 Top/Margin：
            // RenderTransform 是布局之后应用的渲染变换，**不会触发布局重排**，
            // 也不会和新窗口位置打架（面板位置由 layout.json 记住，不能动）。
            // showShift 已由 BuildUi 接好，这里只跑动画。
            if (showShift != null)
            {
                if (showShiftAnim == null)
                {
                    // EaseOut：快起慢收，面板"落"得稳，不像被推到位置就急停
                    showShiftAnim = Ui.ReusableEnter(0, 160);
                }
                showShiftAnim.From = ShowShiftPx;
                showShift.BeginAnimation(TranslateTransform.YProperty, showShiftAnim);
            }

            if (searchBox != null) searchBox.Focus();

            // 毛玻璃背景：在**动画结束后**再截屏。
            //
            // 为什么不能立即做：此刻面板正从透明淡入 + 从下方上移，
            // 窗口位置还没稳定，截到的背景会错位。
            // 160 ms 是 showShiftAnim 的时长，加一点余量。
            RefreshGlassSoon(220);

            // 面板已显示：立即按当前环境恢复/保持暂停；后续变化由自适应定时器接管。
            RefreshVideoPolicy();
        }

        /// <summary>
        /// 把焦点与前台激活状态抢回面板。
        ///
        /// ── 什么时候需要 ──
        ///
        /// 调用任何 Shell 模态操作之后：`SHFileOperation`（回收站删除）、
        /// `SHObjectProperties`（属性对话框）、`OpenAs_RunDLL`（打开方式）。
        /// 这些操作由 explorer.exe 代理执行，会让面板失去前台激活状态 ——
        /// 表现就是"删完文件后键盘不响应、面板标题栏变灰"。
        ///
        /// ── 为什么不能只调 Activate() ──
        ///
        /// WPF 的 `Activate()` 走的是 `SetForegroundWindow`，但 Windows 有
        /// **前台锁定**（foreground lock）：非前台进程调它会被静默忽略。
        /// 实测删除后立刻 `Activate()` 经常无效。
        ///
        /// 可靠做法是组合：
        ///   1. `Activate()` —— 常规路径
        ///   2. `ShowWindow(SW_RESTORE)` —— 防御性（若被最小化）
        ///   3. `SetForegroundWindow` —— 真正抢前台
        ///   4. 仍失败 → `Topmost` 抖动，强制提到 Z 序顶端
        ///
        /// 最后把焦点还给搜索框（与 ShowPanel 一致，用户可直接打字搜索）。
        /// </summary>
        public void ReActivate()
        {
            try
            {
                if (!IsVisible) return;

                Activate();

                IntPtr h = IntPtr.Zero;
                try { h = new WindowInteropHelper(this).Handle; }
                catch { }

                if (h != IntPtr.Zero)
                {
                    try { Native.ShowWindow(h, 9 /* SW_RESTORE */); } catch { }

                    bool ok = false;
                    try { ok = Native.SetForegroundWindow(h); } catch { }

                    if (!ok)
                    {
                        try
                        {
                            bool wasTop = Topmost;
                            Topmost = true;
                            Topmost = wasTop;
                        }
                        catch { }
                    }
                }

                if (searchBox != null)
                {
                    try { searchBox.Focus(); } catch { }
                }
            }
            catch (Exception ex)
            {
                try { Ui.LogError(ex, "重新激活面板"); } catch { }
            }
        }

        /// <summary>
        /// 按毛玻璃开关调整面板背景的不透明度。
        ///
        /// 核心问题：面板背景（root.Background = Ui.Bg）是**不透明**的，
        /// 它会盖在毛玻璃图上面，让图完全看不见。
        /// 所以开毛玻璃时必须把背景改成半透明 —— 但要保留足够的不透明度，
        /// 否则文字会直接压在模糊的壁纸上、对比度不够。
        ///
        /// 取值依据：
        ///   · 毛玻璃 80% → 背景留 72% 不透明 → 壁纸色透出约 28%
        ///     这个比例下背景色仍主导，文字对比度基本不受影响
        ///   · 毛玻璃越浓，背景越透
        ///   · 关闭毛玻璃（0）→ 背景恢复完全不透明
        /// </summary>
        void ApplyGlassTint()
        {
            try
            {
                Border root = rootBorder;
                if (root == null) return;

                int g = settings == null ? 0 : settings.GlassOpacity;
                if (g <= 0)
                {
                    // 关闭：恢复不透明纯色
                    root.Background = Ui.Bg;
                    return;
                }

                // 底色不透明度 = 多少"面板色"盖在壁纸上。
                //
                // ── 系数为什么从 0.50 提到 0.68 ──
                //
                // 0.50 时壁纸只透出 22%（glassOpacity=45），叠上 12px 视觉模糊
                // 和 10% 染色后，背景只剩一层极淡色晕 —— 实测把面板从 471,223
                // 移到 211,223，背景图确实换了（采样 R255→R204），
                // 但肉眼完全看不出，用户会以为"自适应没生效"。
                //
                // 0.68 后的透过率：
                //   浓度 20 → 14%    45 → 31%    60 → 41%
                //   浓度 80 → 54%   100 → 68%
                // 比 0.50 明显更透，但没到 0.78 那种"亮壁纸全白"的程度。
                //
                // 注意：这是与具体壁纸相关的经验值。壁纸极亮时仍会偏白，
                // 那属于"用户该换个壁纸或调低浓度"的范围，不是代码问题。
                double alpha = 1.0 - (g / 100.0) * 0.68;
                // 下限 0.30（透过 70%）：浓度拉到 100 时也不至于完全透明。
                if (alpha < 0.30) alpha = 0.30;

                // 用 Ui.Bg 的颜色 + 自定义 alpha 建一个新刷子。
                // 不能直接改 Ui.Bg 的 Color —— 那是全局共享刷子，
                // 改了会影响所有窗口。
                Color c = Ui.Bg.Color;
                SolidColorBrush b = new SolidColorBrush(
                    Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B));
                b.Freeze();
                root.Background = b;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "应用毛玻璃底色");
            }
        }

        /// <summary>
        /// 背景重取景的合并调度：250ms 内的多次尺寸/位置变化只触发一次刷新。
        ///
        /// 与 RefreshGlassSoon(220) 的区别：那个用于弹出动画后的首次取景，
        /// 这个用于拖动/缩放过程中的持续跟随 —— 间隔更短、且不注入参数
        /// （外观参数没变，只换背景图）。
        /// </summary>
        DispatcherTimer reframeTimer;
        void ScheduleBackdropReframe()
        {
            try
            {
                if (glassBackdrop == null || settings == null || settings.GlassOpacity <= 0) return;
                if (!IsVisible) return;

                if (reframeTimer == null)
                {
                    reframeTimer = new DispatcherTimer();
                    reframeTimer.Interval = TimeSpan.FromMilliseconds(250);
                    reframeTimer.Tick += delegate
                    {
                        reframeTimer.Stop();
                        // 直接 Refresh()：外层 ScheduleBackdropReframe 已经
                        // 做过 250ms 合并，再走 RefreshSoon 的 1.2s 定时器会把
                        // 灵敏度吃掉（拖完后背景要近 1.5 秒才对齐）。
                        try { glassBackdrop.Refresh(); } catch { }
                    };
                }
                reframeTimer.Stop();
                reframeTimer.Start();
            }
            catch { }
        }
        /// <summary>
        /// 延迟刷新毛玻璃背景。
        ///
        /// 用 DispatcherTimer 而不是 Thread.Sleep：界面线程不能阻塞。
        /// 延迟是为了等弹出动画（160 ms）跑完、窗口位置稳定后再截屏。
        /// </summary>
        void RefreshGlassSoon(int delayMs)
        {
            try
            {
                if (glassBackdrop == null) return;
                if (settings == null || settings.GlassOpacity <= 0) return;

                // 外观参数必须在这里也注入 —— 否则启动后 blur/tint/brightness 全是默认值
                InjectGlassParams();
                glassBackdrop.OpacityPercent = settings.GlassOpacity;
                glassBackdrop.CornerRadius = Ui.R.Lg;
                ApplyGlassTint();

                DispatcherTimer t = new DispatcherTimer();
                t.Interval = TimeSpan.FromMilliseconds(delayMs);
                t.Tick += delegate
                {
                    t.Stop();
                    if (!IsVisible) return;
                    glassBackdrop.Refresh();

                    // ★ 毛玻璃层刚建出来（Refresh 里 Attach），此时它在索引 0，
                    //   会把之前插好的视频层盖住 —— 立刻重新校正层级
                    if (videoBackdrop != null) videoBackdrop.VerifyLayer();
                };
                t.Start();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "调度毛玻璃刷新");
            }
        }

        /// <summary>弹出时面板从下方升起的距离（像素）。</summary>
        const double ShowShiftPx = 8;

        DoubleAnimation showAnim;
        /// <summary>面板整体的位移变换（弹出/收起动画用），由 BuildUi 建立。</summary>
        TranslateTransform showShift;
        DoubleAnimation showShiftAnim;
        DoubleAnimation hideShiftAnim;

        /// <summary>
        /// 原来的自动收起：延迟到本调度周期之后确认，仍非激活才收起。
        /// </summary>
        void ScheduleAutoHide()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                try
                {
                    if (!IsVisible) return;
                    if (taskMenuOpen) return;
                    if (DateTime.Now < suppressDeactivate) return;
                    if (trigger != null && trigger.IsActive) return;
                    if (!everActivated) return;
                    if (IsActive) return;
                    if (dragOverPanel) return;          // 拖拽在面板上，别收
                    if (Native.IsLeftButtonDown()) { StartDragWatch(); return; }
                    HidePanel();
                }
                catch (Exception ex) { Ui.LogError(ex, "自动收起面板"); }
            }));
        }

        /// <summary>
        /// 左键按住时启动短周期轮询：每 80ms 看一次左键是否松开。
        ///
        /// 松手后：面板重新激活 / 鼠标仍在面板内 / 拖拽仍在面板上 → 不收起；
        /// 否则走原来的隐藏逻辑。
        ///
        /// 兜底：轮询超过 DragWatchLimit（30 秒）强制停表并按常规逻辑判断，
        /// 避免 GetAsyncKeyState 状态异常时面板永远收不起来。
        /// </summary>
        void StartDragWatch()
        {
            try
            {
                if (dragWatchTimer == null)
                {
                    dragWatchTimer = new DispatcherTimer();
                    dragWatchTimer.Interval = TimeSpan.FromMilliseconds(80);
                    dragWatchTimer.Tick += delegate { OnDragWatchTick(); };
                }

                dragWatchStart = DateTime.Now;
                if (!dragWatchTimer.IsEnabled) dragWatchTimer.Start();
            }
            catch (Exception ex) { Ui.LogError(ex, "启动拖放轮询"); }
        }

        void StopDragWatch()
        {
            try { if (dragWatchTimer != null) dragWatchTimer.Stop(); } catch { }
        }

        void OnDragWatchTick()
        {
            try
            {
                if (!IsVisible) { StopDragWatch(); return; }

                // 兜底超时：标志可能卡住，强制结束轮询
                if (DateTime.Now - dragWatchStart > DragWatchLimit)
                {
                    Ui.Log("拖放轮询超时（" + DragWatchLimit.TotalSeconds + "s），强制结束");
                    StopDragWatch();
                    dragOverPanel = false;
                    ScheduleAutoHide();
                    return;
                }

                // 左键还按着 → 继续等
                if (Native.IsLeftButtonDown()) return;

                // ---- 松手了 ----
                StopDragWatch();

                // 拖拽标志清掉（Drop 可能没触发，这里兜底）
                dragOverPanel = false;

                // 面板重新激活 → 不收
                if (IsActive) { Ui.Log("松手后面板已激活，不收起"); return; }

                // 鼠标仍在面板范围内 → 不收
                if (IsMouseOverPanel()) { Ui.Log("松手后鼠标仍在面板内，不收起"); return; }

                Ui.Log("松手后鼠标在面板外，收起");
                HidePanel();
            }
            catch (Exception ex) { Ui.LogError(ex, "拖放轮询"); StopDragWatch(); }
        }

        /// <summary>鼠标光标当前是否落在面板窗口范围内（屏幕坐标）。</summary>
        /// <summary>
        /// 拖拽经过时在内容区画一圈虚线，提示"可以放这里"。
        ///
        /// 用 Border 而不是改面板边框：面板的边框是玻璃层的一部分，
        /// 改它会影响圆角与阴影。
        /// </summary>
        // ========== 拖放事件处理 ==========
        //
        // 全部挂在窗口级（见构造里的接线），所以整个客户区只有一个拖放目标。
        // 高亮层是常驻的，这里只切 Visibility，不增删子元素。

        void OnPanelDragEnter(DragEventArgs e)
        {
            try
            {
                dragOverPanel = true;
                StopDragWatch();          // 已经进来了，不用再轮询

                bool internalDrag = HasInternalDesktopDrag(e);
                e.Effects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
                SetDropHighlight(!internalDrag);
                e.Handled = true;
                Ui.Log(internalDrag ? "DragEnter（内部拖出，不高亮）" : "DragEnter");
            }
            catch (Exception ex) { Ui.LogError(ex, "DragEnter"); }
        }

        void OnPanelDragOver(DragEventArgs e)
        {
            try
            {
                dragOverPanel = true;

                // DragOver 在拖放期间每几十毫秒触发一次，**每次赋值 e.Effects
                // 都会让系统重画光标**。只在结果真的变了时才写 ——
                // 这是拖放循环里最容易被忽略的一处开销。
                DragDropEffects want = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
                if (e.Effects != want) e.Effects = want;

                // 不重复 SetDropHighlight：那会启动动画，见 SetActive 的说明
                e.Handled = true;
            }
            catch { }
        }

        /// <summary>
        /// 拖拽离开。
        ///
        /// ★ 不信任事件本身：拖过子元素边界时系统会派发假的 DragLeave。
        /// 以光标是否真的在窗口矩形内为准 —— 在里面就当没离开。
        /// </summary>
        void OnPanelDragLeave(DragEventArgs e)
        {
            try
            {
                if (IsMouseOverPanel())
                {
                    // 还在面板上，忽略这次 Leave
                    e.Handled = true;
                    return;
                }

                // 内部拖出一旦离开窗口，马上清理自己的拖放状态；
                // 后续由 80ms 轮询等松手，面板不会中途消失。
                if (HasInternalDesktopDrag(e) && !IsMouseOverPanel())
                {
                    dragOverPanel = false;
                    SetDropHighlight(false);
                    StartDragWatch();
                    Ui.Log("DragLeave（内部拖出）");
                    e.Handled = true;
                    return;
                }

                // 左键还按着 → 外部拖入可能只是掠过边界，等松手再说
                if (Native.IsLeftButtonDown())
                {
                    e.Handled = true;
                    return;
                }

                dragOverPanel = false;
                SetDropHighlight(false);
                Ui.Log("DragLeave（真的离开）");
            }
            catch (Exception ex) { Ui.LogError(ex, "DragLeave"); }
        }

        void OnPanelDrop(DragEventArgs e)
        {
            try
            {
                dragOverPanel = false;
                StopDragWatch();
                SetDropHighlight(false);

                if (!HasFiles(e)) { e.Handled = true; return; }

                // ---- 「文件」标签：拖文件夹进来加来源 ----
                //
                // 与「桌面文件」页的语义完全不同：那边是把文件**搬进去**，
                // 这边只是**记录路径**（不搬、不复制、不递归统计）。
                // 所以单独一个分支，早于下面的桌面判断。
                if (current == FilesCategory)
                {
                    string[] fp = null;
                    try { fp = e.Data.GetData(DataFormats.FileDrop) as string[]; }
                    catch { }
                    e.Handled = true;
                    if (fp == null || fp.Length == 0) return;

                    if (filesView == null) return;
                    string msg = filesView.HandleFolderDrop(fp);
                    if (!string.IsNullOrEmpty(msg)) ShowToast(msg);
                    return;
                }

                // 只在虚拟桌面标签里接收：应用磁贴没有"落位"概念
                if (!IsDeskTab(current) || deskView == null)
                {
                    Ui.Log("Drop 忽略：当前不是虚拟桌面标签（" + current + "）");
                    e.Handled = true;
                    return;
                }

                string[] paths = null;
                try { paths = e.Data.GetData(DataFormats.FileDrop) as string[]; }
                catch { }
                if (paths == null || paths.Length == 0) { e.Handled = true; return; }

                EnsureVirtualDesks();
                string dest = virtualDesks == null ? null : virtualDesks.FolderOf(current);
                if (string.IsNullOrEmpty(dest)) { e.Handled = true; return; }

                // 松手坐标（相对 canvas，已是 DIP）
                double dropX = 0, dropY = 0;
                try
                {
                    Point p = e.GetPosition(deskCanvas);
                    dropX = p.X;
                    dropY = p.Y;
                }
                catch { }

                DeskImporter.Result r = DeskImporter.Import(paths, dest);
                Ui.Log("Drop 导入: 成功 " + r.ImportedCount
                    + " / 跳过 " + r.SkippedCount
                    + " / 失败 " + r.Failed.Count);

                if (r.AnyImported)
                {
                    deskView.ReloadAndPlace(current, r.Imported, dropX, dropY);
                    if (virtualDesks != null) virtualDesks.Save();

                    // 上次事故暴露的缺口：每个成功移入的项都写「原路径 → 新路径」。
                    try
                    {
                        foreach (DeskUndo.Entry entry in r.Moved)
                            Ui.Log("拖入: " + entry.From + " → " + entry.To);
                    }
                    catch { }
                }

                // 失败才用模态框（少数场景，必须让用户看见）；
                // 被跳过（.exe/.lnk 等）改用底部提示条，非阻塞。
                if (!r.AllOk)
                {
                    string detail = "";
                    for (int i = 0; i < r.Failed.Count && i < 5; i++)
                    {
                        detail += "\r\n  · " + r.Failed[i];
                        if (i < r.Reasons.Count && !string.IsNullOrEmpty(r.Reasons[i]))
                            detail += "（" + r.Reasons[i] + "）";
                    }
                    MessageBox.Show(this,
                        r.Failed.Count + " 个文件未能移入：" + detail,
                        "四叶草 · 拖入",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                // ---- 被跳过的项：汇总成一句话，非阻塞 ----
                //
                // 这是**行为变化**：以前 .exe/.lnk 静默跳过，用户拖 5 个进来
                // 只出现 3 个会以为程序坏了。现在告知，但不逐个列文件名 ——
                // 拖 20 个快捷方式进来时逐条列会占满整屏。
                ShowDropSkipTip(r);

                e.Handled = true;
            }
            catch (Exception ex) { Ui.LogError(ex, "Drop"); }
        }

        /// <summary>
        /// 弹一条底部提示（非阻塞，5 秒自动消失）。没有拒绝项时什么都不做。
        ///
        /// 文案由调用方用 `DropSpec.BuildSummary` 生成，这里只负责显示。
        /// </summary>
        void ShowToast(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return;

                // 宿主：跨标签的 toastHost（「桌面文件」页也看得见）
                if (dropTipBar == null && toastHost != null)
                {
                    dropTipBar = new UndoBar(toastHost);
                    dropTipBar.OnFocusBack = delegate
                    {
                        try { if (deskCanvas != null) deskCanvas.Focus(); } catch { }
                    };
                    // 提示条消失后把容器命中关回去，别挡住底下的按钮
                    dropTipBar.OnVisibilityChanged = delegate(bool on)
                    {
                        try { if (toastHost != null) toastHost.IsHitTestVisible = on; }
                        catch { }
                    };
                }
                if (dropTipBar == null) return;

                dropTipBar.Show(text, null);
                Ui.Log("提示: " + text);
            }
            catch (Exception ex) { Ui.LogError(ex, "显示提示条"); }
        }

        /// <summary>
        /// 把「被跳过的项」汇总成一条底部提示（不抢焦点，时长沿用 UndoBar 的 5 秒）。
        ///
        /// 没有任何跳过项时不显示 —— 全部成功不该弹一句「跳过 0 个」。
        /// 文案由 `DropSpec.BuildSummary` 生成（纯函数，可单测），这里只负责显示。
        /// </summary>
        void ShowDropSkipTip(DeskImporter.Result r)
        {
            try
            {
                if (r == null || r.SkippedReasons == null || r.SkippedReasons.Count == 0) return;

                // 还原成 DropSpec.Rejected 列表（BuildSummary 的入参类型）
                List<DropSpec.Rejected> rejected = new List<DropSpec.Rejected>();
                for (int i = 0; i < r.SkippedReasons.Count; i++)
                {
                    DropSpec.Rejected one = new DropSpec.Rejected();
                    one.Reason = r.SkippedReasons[i];
                    one.Name = i < r.Skipped.Count ? r.Skipped[i] : "";
                    one.Ext = i < r.SkippedExts.Count ? r.SkippedExts[i] : "";
                    rejected.Add(one);
                }

                string text = DropSpec.BuildSummary(r.ImportedCount, rejected);
                ShowToast(text);
            }
            catch (Exception ex) { Ui.LogError(ex, "显示拖入提示"); }
        }

        static bool HasInternalDesktopDrag(DragEventArgs e)
        {
            try
            {
                return e.Data != null
                    && e.Data.GetDataPresent(DesktopDragGesture.InternalDataFormat);
            }
            catch { return false; }
        }

        static bool HasFiles(DragEventArgs e)
        {
            try
            {
                return e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
            }
            catch { return false; }
        }

        /// <summary>
        /// 切换拖放高亮的可见性。
        ///
        /// 高亮层在 BuildUi 里一次性创建（常驻），这里只切 Visibility ——
        /// 动态 Add/Remove 子元素会改变命中测试区域，导致 Enter/Leave 抖动。
        /// </summary>
        void SetDropHighlight(bool on)
        {
            try
            {
                if (dropHighlight == null) return;
                dropHighlight.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

                // 空桌面提示层跟着一起高亮（它自己不接收事件，由这里驱动）
                if (deskDropHint != null && deskDropHint.Visible)
                    deskDropHint.SetActive(on);

                // 「文件」标签的来源提示层同理
                if (filesDropHint != null && filesDropHint.Visible)
                    filesDropHint.SetActive(on);
            }
            catch (Exception ex) { Ui.LogError(ex, "拖放高亮"); }
        }

        /// <summary>
        /// 按「当前桌面有几项」决定空状态提示层的显隐。
        ///
        /// 有内容 → 隐藏（不然会盖在图标上）；0 项 → 显示。
        /// 只在「桌面文件」页有意义，切走时由 RenderDeskTab 的显隐逻辑一并处理。
        /// </summary>
        void UpdateDeskDropHint(int itemCount)
        {
            try
            {
                if (deskDropHint == null) return;

                bool empty = itemCount <= 0;
                deskDropHint.SetVisible(empty);

                // 隐藏时 SetVisible 内部已经清掉高亮态；
                // 显示时如果正拖着文件进来，要立刻是高亮态。
                if (empty && dragOverPanel)
                    deskDropHint.SetActive(true);
            }
            catch (Exception ex) { Ui.LogError(ex, "更新放置提示"); }
        }
        bool IsMouseOverPanel()
        {
            try
            {
                Native.POINT p;
                if (!Native.GetCursorPos(out p)) return false;

                Native.RECT r;
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (!Native.GetWindowRect(hwnd, out r)) return false;

                return p.X >= r.Left && p.X <= r.Right
                    && p.Y >= r.Top && p.Y <= r.Bottom;
            }
            catch { return false; }
        }

        public void HidePanel()
        {
            Ui.Log("HidePanel, visible=" + IsVisible);
            if (!IsVisible) return;

            // 开始收起就暂停视频，不等淡出完成，也不浪费后台解码。
            try { if (videoBackdrop != null) videoBackdrop.ApplyPolicy(false, false, VideoBackdrop.IsOnBattery()); }
            catch { }
            SaveLayout();
            // 复用单个动画对象，避免每次隐藏都新建 DoubleAnimation + Clock
            // （实测反复开关面板会造成托管堆持续增长）。
            if (hideAnim == null)
            {
                // EaseIn：慢起快收，走得干脆（出场比入场略快，观感上"收"得利落）
                hideAnim = Ui.ReusableExit(0, 110);
                hideAnim.Completed += delegate
                {
                    BeginAnimation(OpacityProperty, null);
                    Hide();
                };
            }
            hideAnim.From = Opacity;
            BeginAnimation(OpacityProperty, hideAnim);

            // 收起时向下方滑出一点，与弹出的上移对称（观感上"缩回去"而不是"凭空消失"）。
            //
            // 注意：目标值是 +ShowShiftPx（往下），不是 0 —— 静止位置本来就是 0，
            // 动画到 0 等于什么都没做。滑走停在下方，下次 ShowPanel 再从 +ShowShiftPx
            // 动画回 0，两边首尾相接。
            if (showShift != null)
            {
                if (hideShiftAnim == null)
                {
                    hideShiftAnim = Ui.ReusableExit(0, 110);
                }
                hideShiftAnim.From = showShift.Y;
                hideShiftAnim.To = ShowShiftPx;
                showShift.BeginAnimation(TranslateTransform.YProperty, hideShiftAnim);
            }
        }

        DoubleAnimation hideAnim;

        void ToggleExpand()
        {
            try
            {
                if (!expanded)
                {
                    normalRect = new Rect(Left, Top, Width, Height);
                    expanded = true;
                    edgeMargin = 0;
                    rootBorder.Margin = new Thickness(0);
                    rootBorder.CornerRadius = new CornerRadius(0);
                    // 最大化时铺满工作区：外层也要去掉投影，否则阴影会溢出到屏幕外
                    // （外层没有 Margin，投影会画在窗口边界之外，看起来像屏幕边缘发黑）
                    SetShadowHostEffect(false);
                    Rect wa = SystemParameters.WorkArea;
            // edgeMargin 参与展开/还原时的尺寸计算（见下方 expanded 分支）
            double em = edgeMargin;
                    Left = wa.Left;
                    Top = wa.Top;
                    Width = wa.Width;
                    Height = wa.Height;
                    maxBtn.Child = MakeGlyph("\uE923");
                    Ui.Log("expand -> " + Width + "x" + Height);
                }
                else
                {
                    expanded = false;
                    edgeMargin = 16;
                    rootBorder.Margin = new Thickness(16);
                    rootBorder.CornerRadius = new CornerRadius(Ui.R.Lg);
                    // 还原：恢复双层投影
                    SetShadowHostEffect(true);
                    Left = normalRect.X;
                    Top = normalRect.Y;
                    Width = normalRect.Width;
                    Height = normalRect.Height;
                    maxBtn.Child = MakeGlyph("\uE922");
                    Ui.Log("restore -> " + Left + "," + Top + " " + Width + "x" + Height);
                }
            }
            catch { }
        }

        bool LoadLayout()
        {
            try
            {
                if (!File.Exists(layoutPath)) return false;
                System.Web.Script.Serialization.JavaScriptSerializer ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                string layoutJson = JsonIO.Read(layoutPath);
                if (string.IsNullOrEmpty(layoutJson)) return false;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(layoutJson);
                double l = Convert.ToDouble(d["left"]);
                double t = Convert.ToDouble(d["top"]);
                double w = Convert.ToDouble(d["width"]);
                double h = Convert.ToDouble(d["height"]);
                if (w < MinWidth || h < MinHeight) return false;
                Width = w;
                Height = h;
                Left = l;
                Top = t;
                ClampToScreen();
                return true;
            }
            catch { return false; }
        }

        void SaveLayout()
        {
            try
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["left"] = expanded ? normalRect.X : Left;
                d["top"] = expanded ? normalRect.Y : Top;
                d["width"] = expanded ? normalRect.Width : Width;
                d["height"] = expanded ? normalRect.Height : Height;
                System.Web.Script.Serialization.JavaScriptSerializer ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                JsonIO.WriteAtomic(layoutPath, ser.Serialize(d));

                // 位置已落盘 → 以后不再自动定位（尊重用户的选择）。
                // 用户拖动面板会走到这里，此后 GeometryRefined 回调不再重定位。
                needPosition = false;
            }
            catch { }
        }

        void ClampToScreen()
        {
            double sw = SystemParameters.PrimaryScreenWidth;
            double sh = SystemParameters.PrimaryScreenHeight;
            if (Left < -60) Left = 0;
            if (Top < 0) Top = 0;
            if (Left > sw - 120) Left = sw - 120;
            if (Top > sh - 120) Top = sh - 120;
            if (Width > sw + 40) Width = sw + 40;
        }

        void Launch(AppItem a)
        {
            Ui.Log("Launch: " + a.Name + " path=" + a.Path);
            try
            {
                // ---- 商店应用：走 explorer.exe shell:AppsFolder\<AUMID> ----
                //
                // 为什么不能直接 Process.Start(shell:AppsFolder\xxx)：
                // UseShellExecute=true 对 shell: 协议的处理不稳定
                // （实测直接启动有时静默失败），而 explorer.exe 明确支持
                // 用 shell 命名空间路径打开应用。
                //
                // 判断依据用 Aumid 字段（去重时已填好），而不是解析 Path 字符串 ——
                // 更可靠，也不依赖路径格式。
                if (AppPaths.IsShellPath(a.Path))
                {
                    string aumid = string.IsNullOrEmpty(a.Aumid)
                        ? AppPaths.ExtractAumid(a.Path)
                        : a.Aumid;

                    if (string.IsNullOrEmpty(aumid))
                    {
                        MessageBox.Show("这个商店应用缺少 AppUserModelID，无法启动。\r\n" + a.Path,
                            "四叶草面板");
                        return;
                    }

                    ProcessStartInfo spsi = new ProcessStartInfo("explorer.exe");
                    spsi.Arguments = AppPaths.ShellAppsPrefix + aumid;
                    spsi.UseShellExecute = true;
                    Process.Start(spsi);

                    if (usage != null) usage.Record(a.Path);
                    a.Usage++;
                    a.LastUsed = DateTime.UtcNow.Ticks;
                    HidePanel();
                    return;
                }

                ProcessStartInfo psi = new ProcessStartInfo(a.Path);
                psi.UseShellExecute = true;
                Process.Start(psi);
                if (usage != null) usage.Record(a.Path);
                a.Usage++;
                a.LastUsed = DateTime.UtcNow.Ticks;
                HidePanel();
            }
            catch (Exception ex)
            {
                Ui.Log("Launch error: " + ex.Message);
                MessageBox.Show("无法启动 " + a.Name + "\r\n" + ex.Message, "四叶草面板");
            }
        }
    }
}
