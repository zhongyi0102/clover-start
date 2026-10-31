using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clover
{
    /// <summary>面板界面搭建：整体骨架 + 通用小控件（图标按钮、字形）。</summary>
    partial class PanelWindow
    {
        void BuildUi()
        {
            Border root = new Border();
            root.CornerRadius = new CornerRadius(Ui.R.Lg);
            // 注意：毛玻璃开启时这个不透明度会被改成半透明（见 ApplyGlassTint）。
            // 纯色背景盖在毛玻璃图上会把图完全遮死 —— 第一版就踩了这个坑，
            // 开了毛玻璃只看到 13% 的边缘漏光。
            root.Background = Ui.Bg;
            // 外框用 Edge（更淡）而不是 Line：玻璃质感要求"外框最淡、内部稍实"。
            // 同一个颜色用在两处会让边界显得过重。
            root.BorderBrush = Ui.Edge;
            root.BorderThickness = new Thickness(1);
            root.Margin = new Thickness(16);
            // 投影交给 ApplyShadow()：深色主题下要加深，否则黑投影落在深底上几乎看不见。
            // 存进 rootBorder 供切主题时重设（见 ApplyTheme）。
            rootBorder = root;
            ApplyShadow();

            Grid grid = new Grid();
            // 布局：三行（原来是四行，快速启动区已删除）
            //   0 = header（搜索框 + 窗口按钮）
            //   1 = body（分类胶囊 + 内容区）
            //   2 = footer（提示 + 计数）
            //
            // 为什么删掉快速启动区：它和分类栏的「常用」页完全重复 ——
            // 同一批应用在同一屏里出现两次，占了 40px 高度却没有任何
            // 新信息。要快速启动就用「常用」分类（那里还有完整的
            // 右键菜单、拖拽排序、运行中圆点）。
            grid.RowDefinitions.Add(new RowDefinition());   // 0 header
            grid.RowDefinitions.Add(new RowDefinition());   // 1 body
            grid.RowDefinitions.Add(new RowDefinition());   // 2 footer
            grid.RowDefinitions[0].Height = new GridLength(64);
            grid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
            grid.RowDefinitions[2].Height = new GridLength(32);

            // ---------------- header ----------------
            Grid header = new Grid();
            header.Margin = new Thickness(Ui.S.Lg, 0, Ui.S.Md, 0);
            header.Background = Brushes.Transparent;
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions[0].Width = GridLength.Auto;          // logo
            header.ColumnDefinitions[1].Width = GridLength.Auto;          // 弹性留白
            // 第 2 列（搜索框）从 Auto 改成 Star：
            // Auto 会让搜索框停在 300px 的固定宽度，右侧全是空白；
            // Star 让它吃掉剩余空间，成为顶行的主角（目标图就是这样）。
            header.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            header.ColumnDefinitions[3].Width = GridLength.Auto;          // 窗口按钮

            Image logo = new Image();
            logo.Source = Ui.LoadCloverIcon(Path.Combine(baseDir, "res", "clover.png"));
            logo.Width = 28;
            logo.Height = 28;
            logo.VerticalAlignment = VerticalAlignment.Center;
            logo.Margin = new Thickness(0, 0, 12, 0);
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            Grid.SetColumn(logo, 0);
            header.Children.Add(logo);

            TextBlock title = Ui.T("四叶草", 16, Ui.Fg, FontWeights.SemiBold);
            title.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(title, 1);
            header.Children.Add(title);

            searchBorder = new Border();
            // 300 → 自适应拉伸（目标样式：搜索框占顶部主要宽度）
            //
            // 原来写死 300px，在一排按钮旁边显得局促，像"附带的输入框"；
            // 目标图的搜索框是**主角**——占掉顶行大部分空间。
            // 改成不设宽度 + HorizontalAlignment.Stretch，由外层 Grid 的
            // Star 列分配；同时给 MinWidth 兜底，避免窗口很窄时被压没。
            searchBorder.MinWidth = 240;
            searchBorder.Height = 36;
            searchBorder.CornerRadius = new CornerRadius(searchBorder.Height / 2);
            searchBorder.Background = Ui.SearchBg;
            searchBorder.BorderBrush = Ui.Line;
            searchBorder.BorderThickness = new Thickness(1);
            searchBorder.VerticalAlignment = VerticalAlignment.Center;
            searchBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
            searchBorder.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
            Grid.SetColumn(searchBorder, 2);

            Grid sg = new Grid();
            sg.ColumnDefinitions.Add(new ColumnDefinition());
            sg.ColumnDefinitions.Add(new ColumnDefinition());
            sg.ColumnDefinitions[0].Width = new GridLength(34);
            sg.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);

            TextBlock sic = new TextBlock();
            sic.Text = "\uE721";
            sic.FontFamily = Ui.Mdl;
            sic.FontSize = 12;
            sic.Foreground = Ui.Sub;
            sic.HorizontalAlignment = HorizontalAlignment.Center;
            sic.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(sic, 0);
            sg.Children.Add(sic);

            Grid fg = new Grid();
            Grid.SetColumn(fg, 1);
            searchHint = new TextBlock();
            searchHint.Text = "搜索应用（支持拼音 / 首字母）…";
            searchHint.FontFamily = Ui.YaHei;
            searchHint.FontSize = 12.5;
            searchHint.Foreground = Ui.Sub;
            searchHint.VerticalAlignment = VerticalAlignment.Center;
            searchHint.Margin = new Thickness(2, 0, 0, 0);
            searchHint.IsHitTestVisible = false;

            searchBox = new TextBox();
            searchBox.Background = Brushes.Transparent;
            searchBox.BorderThickness = new Thickness(0);
            searchBox.FontFamily = Ui.YaHei;
            searchBox.FontSize = 12.5;
            searchBox.Foreground = Ui.Fg;
            searchBox.VerticalContentAlignment = VerticalAlignment.Center;
            searchBox.Padding = new Thickness(0, 0, 8, 0);
            searchBox.TextChanged += delegate { OnSearchChanged(); };

            fg.Children.Add(searchHint);
            fg.Children.Add(searchBox);
            sg.Children.Add(fg);
            searchBorder.Child = sg;
            header.Children.Add(searchBorder);

            StackPanel winButtons = new StackPanel();
            winButtons.Orientation = Orientation.Horizontal;
            winButtons.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(winButtons, 3);
            header.Children.Add(winButtons);

            // 三个窗口按钮，从左到右：最小化 / 最大化·还原 / 关闭。
            //
            // 「最小化」和「关闭」行为相同（都是 HidePanel）—— 面板不是常规窗口，
            // 它没有任务栏图标（ShowInTaskbar = false），真正最小化到任务栏会
            // 变得不可恢复。所以两者都只是收起面板，区别在图标语义：
            //   · 最小化（—）：用户以为"收起来，待会儿还要用"
            //   · 关闭（×）：用户以为"用完了"
            // 保留两个是照顾习惯 —— 很多人下意识点 —。

            minBtn = MakeHeaderButton("\uE921", delegate { HidePanel(); });
            minBtn.ToolTip = "收起面板（等同于关闭）";
            winButtons.Children.Add(minBtn);

            maxBtn = MakeHeaderButton("\uE922", delegate { ToggleExpand(); });
            maxBtn.ToolTip = "最大化 / 还原";
            winButtons.Children.Add(maxBtn);

            closeBtn = MakeHeaderButton("\uE711", delegate { HidePanel(); });
            closeBtn.ToolTip = "关闭面板";
            winButtons.Children.Add(closeBtn);

            header.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                DependencyObject d = e.OriginalSource as DependencyObject;
                if (IsDescendantOf(d, searchBorder) || IsDescendantOf(d, minBtn)
                    || IsDescendantOf(d, maxBtn) || IsDescendantOf(d, closeBtn)) return;
                try { DragMove(); } catch { }
            };

            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            // ---------------- body ----------------
            //
            // 目标样式（参考用户给的启动台截图）：分类栏在**顶部**横向排列，
            // 不是左侧竖排。所以 body 从"三列"改成"三行"：
            //   行 0 = 分类胶囊条（原侧栏）
            //   行 1 = 分隔线
            //   行 2 = 内容区（磁贴 / 搜索结果）
            //
            // 为什么保留 navGrid 这个字段名：折叠、计数徽标、选中态等
            // 二十多处逻辑都引用它，改名等于全项目搜索替换，风险大于收益。
            // 结构变了但角色没变——它仍然是"分类导航容器"。
            Grid body = new Grid();
            body.RowDefinitions.Add(new RowDefinition());
            body.RowDefinitions.Add(new RowDefinition());
            body.RowDefinitions.Add(new RowDefinition());
            body.RowDefinitions[0].Height = GridLength.Auto;                        // 分类条
            body.RowDefinitions[1].Height = new GridLength(1);                      // 分隔线
            body.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);   // 内容
            Grid.SetRow(body, 1);   // 第 0 行 header、第 1 行 body、第 2 行 footer

            navGrid = new Grid();
            navGrid.ColumnDefinitions.Add(new ColumnDefinition());
            navGrid.ColumnDefinitions.Add(new ColumnDefinition());
            navGrid.ColumnDefinitions[0].Width = GridLength.Auto;   // 折叠按钮
            navGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);

            // 折叠切换按钮：横向条上放在最左，点击收起/展开整条分类栏。
            navToggle = MakeNavToggle();
            Grid.SetColumn(navToggle, 0);
            navGrid.Children.Add(navToggle);

            // 分类胶囊条：横向排列，**不用 ScrollViewer**。
            //
            // ★ 为什么去掉 ScrollViewer：
            //   实测 navStack.ActualWidth = 0 / navScroll.ActualWidth = 0，
            //   10 个胶囊全建出来了，但整条宽度被压成 0，只剩第一个可见。
            //   根因：ScrollViewer 在"Auto 高度的行 + 横向内容"这个组合下，
            //   测量阶段给内容无限宽、自己却算出 0 宽（WPF 已知的测量陷阱）。
            //
            //   改用 Grid + 横向 StackPanel 直接铺：
            //   分类数量是有限的（7 个真实分类 + 常用/全部/桌面/收藏夹/隐藏 ≈ 12 个上限），
            //   12 个 30px 高的胶囊横排远不会超出面板宽度，
            //   所以根本不需要滚动条。
            //
            //   万一以后分类多到放不下，正确的做法是换 WrapPanel（换行），
            //   而不是横向滚动 —— 滚动条在一条 34px 高的工具栏上很难用。
            navStack = new StackPanel();
            navStack.Orientation = Orientation.Horizontal;
            navStack.VerticalAlignment = VerticalAlignment.Center;
            navStack.Margin = new Thickness(Ui.S.Xs, Ui.S.Xs, Ui.S.Xs, Ui.S.Xs);

            // navScroll 字段保留（折叠逻辑和 NavColumnValue 都引用它），
            // 但现在它只是 navStack 的透明容器，不参与滚动。
            navScroll = new ScrollViewer();
            navScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            navScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            navScroll.VerticalAlignment = VerticalAlignment.Center;
            navScroll.Content = navStack;
            Grid.SetColumn(navScroll, 1);
            navGrid.Children.Add(navScroll);

            Grid.SetRow(navGrid, 0);
            body.Children.Add(navGrid);

            // 横向分隔线（原先是竖线）
            Border sep = new Border();
            sep.Height = 1;
            sep.Background = Ui.Line;
            sep.Margin = new Thickness(Ui.S.Md, 0, Ui.S.Md, 0);
            Grid.SetRow(sep, 1);
            body.Children.Add(sep);

            // taskGrid（任务栏列表）在横向布局下不再需要：
            // 它是侧栏下半部分的内容，顶部条放不下。保留字段但移出可视树，
            // 避免 RefreshTasks 里一堆 null 检查。
            // taskGrid（任务栏列表）在顶部横条布局下不再显示：
            // 它是原侧栏下半部分的内容，横条放不下。
            //
            // 但仍要建出 taskStack 并挂在 taskGrid 里 ——
            // RefreshTasks 会遍历 taskStack.Children 更新运行中窗口列表，
            // 字段为 null 时它会提前 return（不崩，但也不工作）。
            // 保留初始化是为了：以后想把这功能挪到别处时，数据层还在。
            taskGrid = new Grid();
            taskGrid.Visibility = Visibility.Collapsed;
            taskStack = new StackPanel();
            taskGrid.Children.Add(taskStack);

            // 磁贴区滚动容器（存成字段，记住滚动位置要用）
            contentScroll = new ScrollViewer();
            contentScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            contentScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            contentScroll.Padding = new Thickness(10, 4, 8, 6);
            tilePanel = new WrapPanel();
            tilePanel.Orientation = Orientation.Horizontal;

            // 搜索结果列表（与磁贴区互斥显示）。
            //
            // 为什么用独立容器而不是把结果塞进 tilePanel：
            // 搜索结果可能是文件/文件夹，不是 AppItem ——
            // 磁贴的启动、拖动换位、右键菜单那套逻辑完全不适用。
            // 混在一起会让"拖动换位"试图去拖动一个文件。
            searchResultPanel = new StackPanel();
            searchResultPanel.Orientation = Orientation.Vertical;
            searchResultPanel.Visibility = Visibility.Collapsed;
            searchResults = new SearchResultList(searchResultPanel);
            // 注入应用清单：搜索时要先在这 119 个已扫描应用里匹配（支持拼音），
            // 否则拼音搜索（weixin/wx）在界面上永远搜不到 —— 磁贴被隐藏了，
            // 而 SystemSearch 不包含已扫描清单。
            searchResults.CatalogProvider = delegate { return catalog == null ? null : catalog.Apps; };

            // 两者放进同一个 Grid，靠 Visibility 切换
            Grid contentStack = new Grid();
            contentStack.Children.Add(tilePanel);
            contentStack.Children.Add(searchResultPanel);

            // ---- 「文件」标签面板（与磁贴区、搜索结果三者互斥显示）----
            //
            // 结构：来源筛选行 + 后缀筛选行 + 提示行 + 文件列表（含表头）
            // 全部在 StackPanel 里竖排，外面套 ScrollViewer（contentScroll 已有）。
            filesPanel = new StackPanel();
            filesPanel.Orientation = Orientation.Vertical;
            filesPanel.Visibility = Visibility.Collapsed;

            // 来源胶囊行（横向，可滚动）
            ScrollViewer chipScroll = new ScrollViewer();
            chipScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            chipScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            chipScroll.Margin = new Thickness(0, 0, 0, Ui.S.Sm);
            filesChipRow = new StackPanel();
            filesChipRow.Orientation = Orientation.Horizontal;
            chipScroll.Content = filesChipRow;
            filesPanel.Children.Add(chipScroll);

            // 后缀筛选行（横向，可滚动 —— 分组 + 自定义 + 刷新可能超出宽度）
            ScrollViewer extScroll = new ScrollViewer();
            extScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            extScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            extScroll.Margin = new Thickness(0, 0, 0, Ui.S.Sm);
            filesExtRow = new StackPanel();
            filesExtRow.Orientation = Orientation.Horizontal;
            extScroll.Content = filesExtRow;
            filesPanel.Children.Add(extScroll);

            // 提示行（错误 / 截断 / 空目录）
            filesNotice = Ui.T("", 11, Ui.Sub);
            filesNotice.Margin = new Thickness(2, 0, 0, Ui.S.Sm);
            filesNotice.TextWrapping = TextWrapping.Wrap;
            filesNotice.Visibility = Visibility.Collapsed;
            filesPanel.Children.Add(filesNotice);

            // 文件列表（纵向）
            filesListPanel = new StackPanel();
            filesListPanel.Orientation = Orientation.Vertical;
            filesPanel.Children.Add(filesListPanel);

            // 撤销提示条（固定在文件面板底部）
            filesUndoHost = new StackPanel();
            filesUndoHost.Orientation = Orientation.Vertical;
            filesPanel.Children.Add(filesUndoHost);

            contentStack.Children.Add(filesPanel);

            // ---- 拖拽插入标记（参考 GlassBoard 的 insertMarker）----
            //
            // 拖动磁贴时，在"将要插入的位置"画一根竖条，
            // 让用户明确知道松手后会插到哪里。
            //
            // 为什么叠在 contentStack 而不是塞进 tilePanel：
            // tilePanel 是 WrapPanel，往里插非磁贴元素会被当成一个"格子"
            // 参与排版，导致磁贴位置跳动。用独立的 Canvas 覆盖层，
            // 用绝对坐标定位，完全不影响布局。
            insertCanvas = new Canvas();
            insertCanvas.IsHitTestVisible = false;   // 不挡鼠标
            insertCanvas.Visibility = Visibility.Collapsed;
            // ★ Canvas 在 Grid 里**不会自动撑满**（它不像 Grid/Border 那样
            // 有默认的 Stretch 行为），必须显式设对齐方式。
            // 不设的话 Canvas 尺寸是 0x0，里面的标记和浮层全被裁掉 ——
            // 实测拖动时只看到浮层（因为浮层用 TranslateTransform 移出了
            // Canvas 边界仍能渲染），但插入标记（用 Canvas.Left/Top 定位）
            // 完全不可见。
            insertCanvas.HorizontalAlignment = HorizontalAlignment.Stretch;
            insertCanvas.VerticalAlignment = VerticalAlignment.Stretch;

            insertMarker = new Border();
            insertMarker.Width = 3;
            // 圆角用「高度的一半」= 胶囊形（设计令牌体系里的合法档位之一）。
            // 3px 宽竖条配胶囊圆角，正是现代落点指示的做法（Notion / Linear 同款）。
            // 不能写死数值：令牌测试只认 Sm/Md/Lg/细条(2)/圆点(4)/胶囊(Height/2)，
            // 写 1.5 或 999 都会被判为"不在体系内"。
            insertMarker.CornerRadius = new CornerRadius(2);
            insertMarker.Background = Ui.Accent;
            insertMarker.Opacity = 0.9;
            insertMarker.Visibility = Visibility.Collapsed;
            insertCanvas.Children.Add(insertMarker);

            contentStack.Children.Add(insertCanvas);

            contentScroll.Content = contentStack;
            Grid.SetRow(contentScroll, 2);
            body.Children.Add(contentScroll);

            grid.Children.Add(body);

            // ---------------- footer ----------------
            Grid footer = new Grid();
            footer.Margin = new Thickness(Ui.S.Xl, 0, Ui.S.Xl, 0);
            TextBlock hint = Ui.T("Esc 关闭 · 点击面板外自动收起 · 右键四叶草按钮可设置", 11, Ui.Sub);
            hint.HorizontalAlignment = HorizontalAlignment.Left;
            footer.Children.Add(hint);
            footerCount = Ui.T("", 11, Ui.Sub);
            footerCount.HorizontalAlignment = HorizontalAlignment.Right;
            footer.Children.Add(footerCount);
            Grid.SetRow(footer, 2);
            grid.Children.Add(footer);

            // ---------------- 毛玻璃底 ----------------
            //
            // 结构：root 里套一个 Grid 容器，毛玻璃图插在这个容器的**最底层**，
            // 内容 grid 在它上面。
            //
            // 为什么不在 root 里直接插：root 只能有一个 Child。所以用一个
            // 内层 Grid 同时装「背景层」和「内容层」。
            Grid contentHost = new Grid();
            contentHost.Children.Add(grid);      // 先加内容（索引 0）
            root.Child = contentHost;

            // 毛玻璃的宿主是 contentHost（不是 root）：
            // Attach 会 Insert(0, 背景)，插到 contentHost 里才会「在内容之下」；
            // 插到 root 里会变成 root 的第二个 Child（非法，root 只能有一个）。
            glassBackdrop = new GlassBackdrop(this, contentHost, Path.Combine(baseDir, "res"));
            glassBackdrop.OpacityPercent = 0;    // 默认关闭，由设置决定

            // ---------------- 双层投影 ----------------
            //
            // WPF 一个元素只能挂一个 Effect，所以用两层容器：
            //   外层 shadowHost → 远距投影（大模糊、大偏移、低不透明）
            //   内层 root       → 近距投影（小模糊、小偏移、较高不透明）
            //
            // 单层投影无论怎么调，要么像"贴在纸上"（太硬），要么像"糊了一团"（太软）。
            // 真实的光是两层：近距离锐利的接触阴影 + 远距离弥散的环境阴影。
            // 两层叠起来才有"面板浮在桌面上方"的立体感。
            //
            // Margin 的处理：root 本来有 16px 外边距（给投影留空间）。
            // 现在外层不设 Margin，root 保留 —— 外层尺寸自动等于 root 的边界，
            // 所以投影有足够空间展开，布局不变。
            Border shadowHost = new Border();
            shadowHost.Background = Brushes.Transparent;   // 只作投影载体，不画任何东西
            shadowHost.Effect = Ui.PanelShadowFar();
            shadowHost.Child = root;

            Content = shadowHost;

            // 弹出/收起动画用的位移变换，在这里就接好（而不是等第一次 ShowPanel）。
            // 好处：窗口一构造完就是可断言的状态，测试能直接检查是否接线。
            //
            // 注意：位移变换挂在**外层** shadowHost 上，不挂 root ——
            // 如果挂 root，它的投影会跟着一起位移，看起来像"影子在抖"。
            // 挂外层则整体（含两层投影）一起平移，观感统一。
            showShift = new TranslateTransform(0, 0);
            shadowHost.RenderTransform = showShift;

            // 滚动位置变化时记下来（按分类分别记），供下次切回该分类时恢复
            contentScroll.ScrollChanged += delegate(object s, ScrollChangedEventArgs e)
            {
                try
                {
                    if (suppressScrollRecord) return;
                    if (e.VerticalChange == 0 && e.ExtentHeightChange == 0) return;
                    scrollOffsets[current] = contentScroll.VerticalOffset;
                }
                catch { }
            };

            RebuildNav();
            RenderApps();

            // 恢复上次的侧栏收起状态（必须在 RebuildNav 之后：
            // 那一步会把分类项建出来，收起逻辑要作用于已存在的控件）
            ApplyNavCollapsedFromSettings();

            StartTaskTimer();
        }

        /// <summary>
        /// 按当前主题重设**双层**投影（切主题时调用）。
        ///
        /// 两层分别在：
        ///   · rootBorder（内层）→ 近距：勾出接触感
        ///   · shadowHost（外层）→ 远距：拉开与桌面的距离
        /// 只改其中一层会让两层不匹配（例如深色下近距变浓、远距还是浅色值），
        /// 所以必须同时重设。
        /// </summary>
        void ApplyShadow()
        {
            try
            {
                if (rootBorder != null) rootBorder.Effect = Ui.PanelShadowNear();
                SetShadowHostEffect(!expanded);
            }
            catch { }
        }

        /// <summary>
        /// 开关**外层**远距投影。
        ///
        /// 最大化时必须关掉：外层没有 Margin，投影会画到窗口边界之外，
        /// 在屏幕边缘看起来像"发黑"。还原后再打开。
        ///
        /// 内层（rootBorder）不用动 —— 它自己带 16px Margin（或最大化时的 0），
        /// 投影始终在窗口内部。
        /// </summary>
        void SetShadowHostEffect(bool on)
        {
            try
            {
                Border host = null;
                if (rootBorder != null) host = rootBorder.Parent as Border;
                if (host == null) host = Content as Border;
                if (host != null) host.Effect = on ? Ui.PanelShadowFar() : null;
            }
            catch { }
        }

        /// <summary>
        /// 主题变了以后由 TriggerWindow 调用。
        ///
        /// 面板上绝大多数颜色走 Ui 刷子（共享对象），改 .Color 就自动重绘了；
        /// 这里只需要补那些**构建时定死**的部分：投影效果。
        /// </summary>
        public void ApplyTheme()
        {
            try
            {
                ApplyShadow();

                // 毛玻璃也要重截：缓存键里含主题（深浅色影响图片透明度），
                // 不重截的话切主题后背景还是旧亮度，看起来像"没生效"。
                // 切主题后强调色变了（染色默认用强调色），外观参数要重新注入
                InjectGlassParams();
                ApplyGlassTint();
                if (glassBackdrop != null && settings != null && settings.GlassOpacity > 0)
                {
                    if (IsVisible) RefreshGlassSoon(120);
                }
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "面板应用主题");
            }
        }

        TextBlock MakeGlyph(string glyph)
        {
            TextBlock t = new TextBlock();
            t.Text = glyph;
            t.FontFamily = Ui.Mdl;
            t.FontSize = 12;
            t.Foreground = Ui.Sub;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }

        Border MakeHeaderButton(string glyph, Action action)
        {
            Border b = new Border();
            b.Width = 32;
            b.Height = 32;
            b.CornerRadius = new CornerRadius(Ui.R.Sm);
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Cursor = Cursors.Hand;
            b.Background = Brushes.Transparent;
            b.Margin = new Thickness(2, 0, 0, 0);
            b.Child = MakeGlyph(glyph);
            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += delegate { if (action != null) action(); };
            return b;
        }

        /// <summary>
        /// 侧栏顶部的收起/展开按钮。
        ///
        /// 为什么单独做一个而不是靠双击分隔线：按钮更可发现，且状态一目了然
        /// （展开时显示 ◀、收起时显示 ▶）。
        /// </summary>
        Border MakeNavToggle()
        {
            Border b = new Border();
            b.Width = 22;
            b.Height = 22;
            b.CornerRadius = new CornerRadius(Ui.R.Sm);
            b.HorizontalAlignment = HorizontalAlignment.Right;
            b.VerticalAlignment = VerticalAlignment.Top;
            b.Margin = new Thickness(0, Ui.S.Xs, Ui.S.Sm, 0);
            b.Cursor = Cursors.Hand;
            b.Background = Brushes.Transparent;
            b.ToolTip = "收起 / 展开分类栏";
            b.Child = MakeGlyph("\uE76B");   // ChevronLeft
            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += delegate { ToggleNavCollapsed(); };
            return b;
        }

        /// <summary>侧栏展开宽度 / 收起宽度。</summary>
        public const double NavWidthExpanded = 160;
        public const double NavWidthCollapsed = 44;

        /// <summary>侧栏折叠按钮（验收测试用来检查它真的落在侧栏区域内）。</summary>
        public Border NavToggle { get { return navToggle; } }
        /// <summary>当前是否已收起。</summary>
        public bool NavIsCollapsed { get { return navCollapsed; } }
        /// <summary>
        /// 分类栏的"宽度"。
        ///
        /// 改成顶部横条后没有列宽了，这里返回导航区的实际宽度，
        /// 保留这个属性是为了不破坏既有测试与诊断代码。
        /// -1 表示尚未建立。
        /// </summary>
        public double NavColumnValue
        {
            get
            {
                if (navScroll == null) return -1;
                if (navCollapsed) return 0;
                return navScroll.ActualWidth > 0 ? navScroll.ActualWidth : NavWidthExpanded;
            }
        }
        /// <summary>供测试直接触发折叠（绕开鼠标点击）。</summary>
        public void ToggleNavNow() { ToggleNavCollapsed(); }

        /// <summary>
        /// 收起或展开分类栏。
        ///
        /// 布局从"左侧竖栏"改成"顶部横条"后，折叠不再是改列宽，
        /// 而是把整条分类胶囊藏起来（只留折叠按钮），把高度让给内容区。
        /// </summary>
        void ToggleNavCollapsed()
        {
            try
            {
                navCollapsed = !navCollapsed;

                // 收起时把分类条整体藏起来（保留容器，避免重建）
                if (navScroll != null) navScroll.Visibility =
                    navCollapsed ? Visibility.Collapsed : Visibility.Visible;
                if (taskGrid != null) taskGrid.Visibility =
                    navCollapsed ? Visibility.Collapsed : Visibility.Visible;

                // 切换箭头方向：横条用左右箭头语义（收起 = 往右折）
                if (navToggle != null)
                {
                    navToggle.Child = MakeGlyph(navCollapsed ? "\uE76C" : "\uE76B");
                }

                // 记住这个偏好：你在 dev-run 里反复重启时，不该每次都手动收起一遍
                if (settings != null)
                {
                    settings.NavCollapsed = navCollapsed;
                    settings.Save();
                }

                // 收起后内容区变高，重排一次让图标重新流动
                if (tilePanel != null) tilePanel.UpdateLayout();

                Ui.Log("分类栏 " + (navCollapsed ? "收起" : "展开"));
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "切换侧栏");
            }
        }

        /// <summary>
        /// 按设置应用侧栏的收起状态（构造时调一次，恢复上次的样子）。
        ///
        /// 与 ToggleNavCollapsed 分开：那个是"切换"（会写设置），
        /// 这个是"应用已知状态"（只读不写），混在一起会导致构造时白写一次盘。
        /// </summary>
        void ApplyNavCollapsedFromSettings()
        {
            try
            {
                if (settings == null || !settings.NavCollapsed) return;
                navCollapsed = true;

                // 顶部横条布局下没有"列宽"可调，收起 = 隐藏整条胶囊
                if (navScroll != null) navScroll.Visibility = Visibility.Collapsed;
                if (taskGrid != null) taskGrid.Visibility = Visibility.Collapsed;
                if (navToggle != null) navToggle.Child = MakeGlyph("\uE76C");

                Ui.Log("分类栏按上次设置收起");
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "恢复侧栏收起状态");
            }
        }

        static bool IsDescendantOf(DependencyObject d, DependencyObject parent)
        {
            while (d != null)
            {
                if (d == parent) return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }
    }
}
