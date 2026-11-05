using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            AutomationProperties.SetName(searchBox, "搜索应用");

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

            // 主面板的“最小化”不是系统任务栏最小化，而是收起到常驻四叶草；
            // 可再次点击四叶草或使用全局快捷键恢复。按 Windows 惯例放在最左侧。
            minBtn = MakeHeaderButton("\uE921", delegate { HidePanel(); });
            minBtn.ToolTip = "最小化到四叶草";
            AutomationProperties.SetName(minBtn, "最小化主面板");
            winButtons.Children.Add(minBtn);

            maxBtn = MakeHeaderButton("\uE922", delegate { ToggleExpand(); });
            maxBtn.ToolTip = "最大化 / 还原";
            AutomationProperties.SetName(maxBtn, "最大化或还原面板");
            winButtons.Children.Add(maxBtn);

            closeBtn = MakeHeaderButton("\uE711", delegate { HidePanel(); });
            closeBtn.ToolTip = "收起面板";
            AutomationProperties.SetName(closeBtn, "收起面板");
            winButtons.Children.Add(closeBtn);

            header.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                DependencyObject d = e.OriginalSource as DependencyObject;
                if (IsDescendantOf(d, searchBorder)
                    || IsDescendantOf(d, minBtn) || IsDescendantOf(d, maxBtn)
                    || IsDescendantOf(d, closeBtn)) return;
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

            // 固定页 + 最多 10 个自定义桌面在 640px 最小宽度下放不进一行。
            // WrapPanel 让自定义桌面自然换到第二行，避免横向滚动和裁切。
            navStack = new WrapPanel();
            navStack.Orientation = Orientation.Horizontal;
            navStack.VerticalAlignment = VerticalAlignment.Center;
            navStack.HorizontalAlignment = HorizontalAlignment.Stretch;
            navStack.Margin = new Thickness(Ui.S.Xs, Ui.S.Xs, Ui.S.Xs, Ui.S.Xs);

            // ScrollViewer 只作为既有折叠逻辑的容器；内容按可用宽度换行。
            navScroll = new ScrollViewer();
            navScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            navScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            navScroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            navScroll.VerticalAlignment = VerticalAlignment.Center;
            navScroll.Content = navStack;
            // ScrollViewer 默认会给横向内容“无限宽”，WrapPanel 因此可能永远不换行。
            // 明确跟随可视宽度，窄窗口或自定义桌面较多时才会产生真实的第二行。
            navStack.SetBinding(FrameworkElement.WidthProperty,
                new System.Windows.Data.Binding("ActualWidth") { Source = navScroll });
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

            // ---- 「把文件夹拖进来」放置提示（常驻）----
            //
            // 放在来源胶囊行之后：用户看到 chip 行，紧接着看到"还能加"。
            // 复用 DropHint，样式令牌一个不改。
            // 文案由 DropSpec 从 FileFolders.MaxFolders 派生，不在这里手写。
            filesDropHint = new DropHint("把文件夹拖进来", DropSpec.FolderHintText());
            filesDropHint.Root.Margin = new Thickness(0, 0, 0, Ui.S.Sm);
            filesPanel.Children.Add(filesDropHint.Root);

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

            // ---- 虚拟桌面层（自定义标签用）----
            //
            // 结构：Grid 三行（按钮 Auto / 提示 Auto / 图标画布 Star）。
            // ★ 不能用纵向 StackPanel：StackPanel 不会把剩余高度分给 Canvas，
            // 实测视口 449px 时画布永远只有 MinHeight=240px，图标拖到下半页就被裁掉。
            deskPanel = new Grid();
            deskPanel.Visibility = Visibility.Collapsed;
            deskPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            deskPanel.VerticalAlignment = VerticalAlignment.Stretch;
            deskPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            deskPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            deskPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            deskToolRow = new StackPanel();
            deskToolRow.Orientation = Orientation.Horizontal;
            deskToolRow.Margin = new Thickness(0, 0, 0, Ui.S.Sm);
            Grid.SetRow(deskToolRow, 0);
            deskPanel.Children.Add(deskToolRow);

            deskNotice = Ui.T("", 10.5, Ui.Sub);
            deskNotice.TextWrapping = TextWrapping.Wrap;
            deskNotice.Margin = new Thickness(0, 0, 0, Ui.S.Xs);
            deskNotice.Visibility = Visibility.Collapsed;
            Grid.SetRow(deskNotice, 1);
            deskPanel.Children.Add(deskNotice);

            deskCanvas = new Canvas();
            deskCanvas.Background = Brushes.Transparent;
            deskCanvas.ClipToBounds = true;
            deskCanvas.MinHeight = 240;
            deskCanvas.HorizontalAlignment = HorizontalAlignment.Stretch;
            deskCanvas.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetRow(deskCanvas, 2);
            deskPanel.Children.Add(deskCanvas);

            // ---- 空桌面提示层（常驻）----
            //
            // 与 deskCanvas **同一个 Grid 单元格**，叠在它上面。
            // 为什么放这里而不是 contentStack（像 deskOverlay 那样）：
            // contentStack 是整个内容区，「文件」页和「常用」页也会被它盖住。
            // 放 deskPanel 第 2 行才能自然跟着桌面页显隐。
            //
            // ★ IsHitTestVisible 只关在 DropHint 自己（root）身上，
            //   绝不加在 deskCanvas 或 deskPanel 上 —— 框选绑在 deskCanvas，
            //   关掉它的命中就等于废掉框选。
            deskDropHint = new DropHint("把文件拖到桌面收纳", "文件会进入当前收纳目录，可在资源管理器中正常访问");
            Grid.SetRow(deskDropHint.Root, 2);
            Panel.SetZIndex(deskDropHint.Root, 5);   // 盖在图标之上，但不吃事件
            deskPanel.Children.Add(deskDropHint.Root);

            contentStack.Children.Add(deskPanel);

            deskOverlay = new Canvas();
            deskOverlay.IsHitTestVisible = false;
            deskOverlay.Visibility = Visibility.Collapsed;
            deskOverlay.HorizontalAlignment = HorizontalAlignment.Stretch;
            deskOverlay.VerticalAlignment = VerticalAlignment.Stretch;
            contentStack.Children.Add(deskOverlay);

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
            footerHint = Ui.T("Esc 收起 · 输入即可搜索应用 · 方向键选择，Enter 启动", 11, Ui.Sub);
            footerHint.HorizontalAlignment = HorizontalAlignment.Left;
            footer.Children.Add(footerHint);
            footerCount = Ui.T("", 11, Ui.Sub);
            footerCount.HorizontalAlignment = HorizontalAlignment.Right;
            footer.Children.Add(footerCount);
            Grid.SetRow(footer, 2);
            grid.Children.Add(footer);

            // ---------------- 跨标签提示条宿主 ----------------
            //
            // 为什么单独一个容器：`filesUndoHost` 在 filesPanel 里面，
            // 切到「桌面文件」页时整块被隐藏 —— 拖入提示会看不见。
            //
            // 叠在 footer 上方（第 1 行 body 的底部），不占布局空间：
            // 用 VerticalAlignment=Bottom + Margin，空的时候高度为 0，
            // 不挤压内容区。这样「文件」页的撤销条和拖入提示条各自独立，
            // 互不顶掉。
            toastHost = new StackPanel();
            toastHost.Orientation = Orientation.Vertical;
            toastHost.VerticalAlignment = VerticalAlignment.Bottom;
            toastHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            toastHost.Margin = new Thickness(Ui.S.Xl, 0, Ui.S.Xl, Ui.S.Xs);
            // ★ 默认不吃命中，避免空容器（或哪怕只有 0 高度）挡住底下的
            //   分类栏折叠按钮 —— 实测过：空 StackPanel 在 Stretch 对齐下
            //   仍会参与命中测试，把 navToggle 的点击吃掉。
            //
            //   有提示条时由 ShowToast 把它打开；隐藏时再关掉。
            //   「不能一律关掉」是因为关掉会连子元素的命中一起废掉，
            //   提示条上的「撤销 / 保留」就点不动了。
            toastHost.IsHitTestVisible = false;
            Grid.SetRow(toastHost, 1);
            Panel.SetZIndex(toastHost, 10);       // 盖在内容之上
            grid.Children.Add(toastHost);

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
            // ---- 视频背景层（最底层，索引 0 之前）----
            videoBackdrop = new VideoBackdrop(this, contentHost);

            // ---- 背景暗度遮罩 ----
            //
            // 叠在"背景之上、内容之下"。用 Border 而不是画进背景图里：
            // 这样改暗度是改一个 Opacity 值，不用重算毛玻璃，能做到即时生效。
            darkOverlay = new Border();
            darkOverlay.Background = Brushes.Black;
            darkOverlay.Opacity = 0;
            darkOverlay.IsHitTestVisible = false;
            darkOverlay.CornerRadius = new CornerRadius(Ui.R.Lg);
            contentHost.Children.Add(darkOverlay);

            // ---- 拖放高亮层（已移除）----
            //
            // 这里原来有一个铺满 contentHost 的虚线 Border：
            //   BorderBrush = Ui.Accent, 2px, CornerRadius = Ui.R.Md? 否，是 Ui.R.Lg
            //   Margin = 6, IsHitTestVisible = false
            //   Background = Color.FromArgb(24, 56, 140, 72)   ← 硬编码绿，不跟主题
            //
            // 移除原因见 SetDropHighlight 的说明：
            //   · 在不接收文件的页（常用/全部）也会亮，属于误导
            //   · 硬编码底色在深色主题下与 Ui.Accent 打架
            //   · 有 DropHint 的页已有局部高亮，面板级是冗余
            //
            // 现在高亮统一由 DropHint 承担，没有全局层了。

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
            UpdateContextChrome();
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

        Button MakeHeaderButton(string glyph, Action action)
        {
            Button b = new Button();
            b.Width = 32;
            b.Height = 32;
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Cursor = Cursors.Hand;
            b.Background = Brushes.Transparent;
            b.BorderBrush = Brushes.Transparent;
            b.BorderThickness = new Thickness(0);
            b.Padding = new Thickness(0);
            b.Margin = new Thickness(2, 0, 0, 0);
            b.Template = MakeButtonTemplate(Ui.R.Sm);
            b.Content = MakeGlyph(glyph);
            b.Click += delegate { if (action != null) action(); };
            b.MouseEnter += delegate { if (!b.IsKeyboardFocused) b.Background = Ui.Hover; };
            b.MouseLeave += delegate { if (!b.IsKeyboardFocused) b.Background = Brushes.Transparent; };
            b.GotKeyboardFocus += delegate { b.Background = Ui.AccentSoft; };
            b.LostKeyboardFocus += delegate { b.Background = Brushes.Transparent; };
            return b;
        }

        static ControlTemplate MakeButtonTemplate(double radius)
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "ButtonChrome";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            border.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.TemplatedParent)
                });
            border.SetBinding(Border.BorderBrushProperty,
                new System.Windows.Data.Binding("BorderBrush")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.TemplatedParent)
                });
            border.SetBinding(Border.BorderThicknessProperty,
                new System.Windows.Data.Binding("BorderThickness")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.TemplatedParent)
                });

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;

            Trigger pressed = new Trigger();
            pressed.Property = Button.IsPressedProperty;
            pressed.Value = true;
            pressed.Setters.Add(new Setter(Border.OpacityProperty, 0.82, "ButtonChrome"));
            template.Triggers.Add(pressed);
            return template;
        }

        /// <summary>
        /// 侧栏顶部的收起/展开按钮。
        ///
        /// 为什么单独做一个而不是靠双击分隔线：按钮更可发现，且状态一目了然
        /// （展开时显示 ◀、收起时显示 ▶）。
        /// </summary>
        Button MakeNavToggle()
        {
            Button b = new Button();
            b.Width = 32;
            b.Height = 32;
            b.HorizontalAlignment = HorizontalAlignment.Right;
            b.VerticalAlignment = VerticalAlignment.Top;
            b.Margin = new Thickness(0, Ui.S.Xs, Ui.S.Sm, 0);
            b.Cursor = Cursors.Hand;
            b.Background = Brushes.Transparent;
            b.BorderBrush = Brushes.Transparent;
            b.BorderThickness = new Thickness(0);
            b.Padding = new Thickness(0);
            b.Template = MakeButtonTemplate(Ui.R.Sm);
            b.ToolTip = "收起 / 展开分类栏";
            AutomationProperties.SetName(b, "收起或展开分类栏");
            b.Content = MakeGlyph("\uE76B");   // ChevronLeft
            b.Click += delegate { ToggleNavCollapsed(); };
            b.MouseEnter += delegate { if (!b.IsKeyboardFocused) b.Background = Ui.Hover; };
            b.MouseLeave += delegate { if (!b.IsKeyboardFocused) b.Background = Brushes.Transparent; };
            b.GotKeyboardFocus += delegate { b.Background = Ui.AccentSoft; };
            b.LostKeyboardFocus += delegate { b.Background = Brushes.Transparent; };
            return b;
        }

        /// <summary>侧栏展开宽度 / 收起宽度。</summary>
        public const double NavWidthExpanded = 160;
        public const double NavWidthCollapsed = 44;

        /// <summary>侧栏折叠按钮（验收测试用来检查它真的落在侧栏区域内）。</summary>
        public Button NavToggle { get { return navToggle; } }
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
                    navToggle.Content = MakeGlyph(navCollapsed ? "\uE76C" : "\uE76B");
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
                if (navToggle != null) navToggle.Content = MakeGlyph("\uE76C");

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
