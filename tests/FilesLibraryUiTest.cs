using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clover
{
    static class FilesLibraryUiTest
    {
        static int passed, failed;
        static void Check(bool value, string name)
        {
            if (value) passed++; else failed++;
            Console.WriteLine((value ? "PASS  " : "FAIL  ") + name);
        }
        static T Get<T>(object obj, string field)
        {
            return (T)obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
        }
        static void Invoke(object obj, string method, params object[] args)
        {
            obj.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(obj, args);
        }
        static void Pump()
        {
            Dispatcher.CurrentDispatcher.Invoke(new Action(delegate { }), DispatcherPriority.ApplicationIdle);
        }
        static void Wait(FilesTabView view, TextBlock notice)
        {
            for (int i = 0; i < 400 && view.Status == "更新中…"; i++)
            {
                Pump(); System.Threading.Thread.Sleep(10);
            }
            Pump();
            Check(view.Status != "更新中…", "异步扫描正常完成");
        }
        static Button ButtonWithTag(Panel host, string tag)
        {
            foreach (UIElement child in host.Children)
            {
                Button button = child as Button;
                if (button != null && Convert.ToString(button.Tag) == tag) return button;
            }
            return null;
        }
        static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
        static Border Row(FilesTabView view, string path)
        {
            Border row;
            return Get<Dictionary<string, Border>>(view, "rowMap").TryGetValue(path, out row) ? row : null;
        }
        static FileScanner.Entry Current(FilesTabView view)
        {
            return (FileScanner.Entry)typeof(FilesTabView).GetMethod("CurrentEntry",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, null);
        }
        static MenuItem MenuAction(ContextMenu menu, string title)
        {
            foreach (object item in menu.Items)
            {
                MenuItem action = item as MenuItem;
                if (action != null && Convert.ToString(action.Header) == title) return action;
            }
            return null;
        }
        static void CancelBatchDelete(FilesTabView view, int count)
        {
            bool sawDialog = false, cancelFocused = false;
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += delegate
            {
                foreach (Window dialog in Application.Current.Windows)
                {
                    if (!dialog.Title.Contains("移到回收站")) continue;
                    sawDialog = true;
                    Button cancel = Keyboard.FocusedElement as Button;
                    cancelFocused = cancel != null && System.Windows.Automation.AutomationProperties
                        .GetName(cancel) == "取消移到回收站";
                    timer.Stop(); dialog.Close(); break;
                }
            };
            timer.Start();
            try { view.ConfirmAndDelete(); }
            finally { timer.Stop(); }
            Check(sawDialog && cancelFocused && view.TestSelectedCount() == count,
                count + "个普通文件删除先确认，默认取消且保留多选");
        }
        [STAThread]
        static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "CloverFilesUi-" + Guid.NewGuid().ToString("N"));
            string first = Path.Combine(root, "项目"), second = Path.Combine(root, "资料");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Window window = null;
            PanelWindow panel = null;
            FilesTabView view = null;
            IDataObject previousClipboard = Clipboard.GetDataObject();
            try
            {
                Ui.ApplyTheme(Ui.ThemeMode.Light);
                for (int i = 0; i < 530; i++) File.WriteAllText(Path.Combine(first, "file" + i.ToString("D4") + ".txt"), "x");
                File.WriteAllText(Path.Combine(first, "最后报告.pdf"), "pdf");
                File.WriteAllText(Path.Combine(first, "同名.txt"), "first");
                File.WriteAllText(Path.Combine(second, "同名.txt"), "second");
                Directory.CreateDirectory(Path.Combine(first, "文件夹"));
                File.WriteAllText(Path.Combine(first, "文件夹", "子目录文件.txt"), "deep");
                FileFolders folders = FileFolders.Load(root); folders.Add(first); folders.Add(second);
                Settings settings = Settings.Load(root); settings.FilesSort = "name:asc";
                settings.AutoAppearance = false; // this fixture explicitly tests manual theme switching
                WrapPanel chips = new WrapPanel { Orientation = Orientation.Horizontal };
                WrapPanel exts = new WrapPanel { Orientation = Orientation.Horizontal };
                StackPanel list = new StackPanel(), undo = new StackPanel(), content = new StackPanel();
                TextBlock notice = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Sub };
                content.Children.Add(chips);
                content.Children.Add(exts);
                content.Children.Add(notice); content.Children.Add(list); content.Children.Add(undo);
                ScrollViewer scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                Border testSurface = new Border { Background = Ui.Bg, Child = scroll };
                window = new Window { Width = 640, Height = 460, Content = testSurface, Background = Ui.Bg };
                window.Show();
                view = new FilesTabView(null, settings, root, chips, exts, list, notice, undo);
                view.AttachSelectionSurface(content);
                view.OnEnter(); view.Refresh(true); Wait(view, notice); window.UpdateLayout();
                string longSourceName = "来源" + new string('长', 100);
                folders.Rename(first, longSourceName); view.ReloadFolders(); view.Refresh(true); Wait(view, notice);
                window.UpdateLayout();
                Button longChip = ButtonWithTag(chips, first);
                TextBlock longLabel = longChip.Content as TextBlock;
                Check(longLabel != null && longLabel.Text == longSourceName
                    && longLabel.TextTrimming == TextTrimming.CharacterEllipsis && longChip.ActualWidth <= 280.1
                    && longLabel.ActualWidth < longChip.ActualWidth,
                    "长来源名限制宽度并省略显示，不撑破来源栏");
                Check(Convert.ToString(longChip.ToolTip).Contains(longSourceName)
                    && Convert.ToString(longChip.ToolTip).Contains(first), "长来源悬停可读完整名称和路径");
                window.Width = 240; window.UpdateLayout(); Pump(); window.UpdateLayout();
                Check(longChip.ActualWidth + longChip.Margin.Right <= chips.ActualWidth + 0.1,
                    "单个长来源按钮随更窄窗口缩小");
                window.Width = 640; window.UpdateLayout(); Pump(); window.UpdateLayout();
                Check(longChip.ActualWidth > 200 && longChip.MaxWidth == 280,
                    "放宽窗口恢复来源名可用宽度且复用按钮");
                folders.Rename(first, "项目"); view.ReloadFolders(); view.Refresh(true); Wait(view, notice); window.UpdateLayout();
                Check(view.TestRowCount() == 500 && ButtonWithTag(list, "file-load-more") != null, "初始仅显示500项，提供加载更多");
                Check(Get<List<FileScanner.Entry>>(view, "visible")[0].IsFolder, "默认全部且文件夹在前");
                Check(Get<FileScanner.Result>(view, "cached").Items.Count == 534, "已扫描元数据未提前按500截断");
                Check(notice.Text == "2 个来源 · 仅第一层" && !notice.Text.Contains("扫描"), "上方仅一行来源范围，不堆重复统计");
                Check(view.Status.Contains("显示 500 / 534 项") && view.Status.Contains("个文件夹"), "分页数量与条目类型由独立底部状态提供");
                bool extRefresh = false;
                foreach (UIElement child in exts.Children)
                {
                    Button button = child as Button;
                    if (button != null && Convert.ToString(button.Content).Contains("刷新")) extRefresh = true;
                }
                Check(!extRefresh, "后缀筛选栏不再混入刷新操作");
                Grid folderSlot = (Grid)((Grid)Row(view, Path.Combine(first, "文件夹")).Child).Children[0];
                Check(folderSlot.Width == 24 && folderSlot.Height == 24 && !folderSlot.IsHitTestVisible
                    && (folderSlot.Children[0] is Image || folderSlot.Children[0] is System.Windows.Shapes.Path),
                    "文件夹有固定图标槽，系统图标或矢量兜底均不依赖缺字字体");
                view.HandleKey(Key.A, ModifierKeys.Control);
                Check(view.TestSelectedCount() == 500, "全选只选已显示项，不操作隐藏分页结果");
                content.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent
                });
                Check(view.TestSelectedCount() == 0, "点击文件页内容空白处取消当前选择");
                view.ApplyFilter("最后报告"); window.UpdateLayout();
                Check(view.TestRowCount() == 1 && Get<List<FileScanner.Entry>>(view, "visible")[0].Name == "最后报告.pdf",
                    "搜索找到排序后500项之外的文件");
                Check(view.TestSelectedCount() == 0, "筛选剔除旧选中，不自动改选匹配文件");
                view.ApplyFilter("");
                Click(ButtonWithTag(exts, "file-mode:Files"));
                Click(ButtonWithTag(exts, "pdf"));
                Check(view.TestRowCount() == 1, "先后缀筛选再显示分页，末尾PDF可见");
                Click(ButtonWithTag(exts, "file-mode:Folders"));
                Check(view.TestRowCount() == 1 && Get<List<FileScanner.Entry>>(view, "visible")[0].IsFolder,
                    "文件夹模式忽略文件后缀筛选");
                Check(!ButtonWithTag(exts, "pdf").IsEnabled, "文件夹模式禁用后缀按钮并解释原因");
                Click(ButtonWithTag(exts, "file-mode:All"));
                view.ApplyFilter(""); view.HandleKey(Key.A, ModifierKeys.Control);
                view.HandleKey(Key.C, ModifierKeys.Control);
                System.Collections.Specialized.StringCollection keyboardCopy = Clipboard.GetFileDropList();
                string folderPath = Path.Combine(first, "文件夹");
                Check(keyboardCopy.Count == view.TestSelectedCount() && keyboardCopy.Contains(folderPath),
                    "Ctrl+C复制当前选中集合，包含文件夹");
                Invoke(view, "ShowItemMenu", Get<List<FileScanner.Entry>>(view, "visible")[0], false);
                ContextMenu copyMenu = Get<ContextMenu>(view, "activeItemMenu");
                copyMenu.IsOpen = false;
                MenuAction(copyMenu, "复制文件").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(Clipboard.GetFileDropList().Count == keyboardCopy.Count
                    && Clipboard.GetFileDropList().Contains(folderPath), "菜单关闭后正常复制仍执行，与Ctrl+C相同");
                Check(view.TestRowCount() == 2, "切回全部保留后缀选择：文件夹加PDF");
                Click(ButtonWithTag(exts, "pdf"));
                Click(ButtonWithTag(list, "file-load-more"));
                Check(view.TestRowCount() == 534 && ButtonWithTag(list, "file-load-more") == null, "加载更多只扩展展示，不重新扫描");
                view.ApplyFilter("同名");
                string a = Path.Combine(first, "同名.txt"), b = Path.Combine(second, "同名.txt");
                Border kept = Row(view, a), other = Row(view, b);
                Grid keptGrid = (Grid)kept.Child;
                TextBlock aSource = (TextBlock)keptGrid.Children[4];
                TextBlock bSource = (TextBlock)((Grid)other.Child).Children[4];
                Check(view.TestRowCount() == 2 && aSource.Text != bSource.Text
                    && Convert.ToString(aSource.ToolTip) == first && Convert.ToString(bSource.ToolTip) == second,
                    "同名文件分别显示来源名称与完整来源路径");
                Check(kept.Height == 52 && Grid.GetColumn(aSource) == 1
                    && keptGrid.ColumnDefinitions[4].Width.Value == 0
                    && ((TextBlock)keptGrid.Children[1]).VerticalAlignment == VerticalAlignment.Top,
                    "640像素多来源时来源移到名称下方，独立来源列收起");
                view.HandleKey(Key.A, ModifierKeys.Control);
                File.WriteAllText(a, "changed contents");
                view.Refresh(true);
                Check(Row(view, a) == kept && view.TestSelectedCount() == 2, "扫描期间保留行和多选");
                Wait(view, notice);
                Check(Row(view, a) == kept && Row(view, b) == other && view.TestSelectedCount() == 2,
                    "刷新复用行并保留全部多选");
                Check(((TextBlock)((Grid)kept.Child).Children[3]).Text == "16 B"
                    && ((FileScanner.Entry)kept.Tag).Size == 16, "复用行显示和事件元数据同时更新");
                Button sort = (Button)((Grid)((Border)list.Children[0]).Child).Children[0];
                Click(sort);
                Check(Row(view, a) == kept && view.TestSelectedCount() == 2, "排序保留行身份和多选");
                view.OnLeave(); view.OnEnter(); view.Refresh(false); view.RestorePageScroll();
                Check(view.FilterQuery == "同名" && view.TestSelectedCount() == 2 && Row(view, a) == kept,
                    "返回页面保留搜索、选择和缓存行");
                view.TestSetSources(new List<string> { first }); view.Refresh(true); Wait(view, notice);
                Check(view.TestRowCount() == 1 && notice.Text.Contains(first)
                    && ((Grid)kept.Child).ColumnDefinitions[4].Width.Value == 0, "单来源显示完整路径，隐藏冗余来源列");
                view.TestSetSources(null); view.ApplyFilter(""); view.Refresh(true); Wait(view, notice);
                window.UpdateLayout(); scroll.ScrollToVerticalOffset(2300); scroll.UpdateLayout();
                double oldOffset = scroll.VerticalOffset;
                Border scrollRow = Row(view, Path.Combine(first, "file0050.txt"));
                // 上面的名称排序已切到降序；zzz 才会插在当前阅读项之前。
                File.WriteAllText(Path.Combine(first, "zzz-new.txt"), "new");
                view.Refresh(true); Wait(view, notice); scroll.UpdateLayout();
                Check(oldOffset > 0 && scroll.VerticalOffset > oldOffset && ReferenceEquals(scrollRow,
                    Row(view, Path.Combine(first, "file0050.txt"))), "顶部插入文件保留阅读锚点与行，不跳回顶部");
                double saved = scroll.VerticalOffset;
                view.OnLeave(); scroll.ScrollToTop(); scroll.UpdateLayout(); view.OnEnter(); view.RestorePageScroll(); scroll.UpdateLayout();
                Check(Math.Abs(scroll.VerticalOffset - saved) < 1, "返回页面恢复滚动位置");
                view.ApplyFilter("子目录文件");
                Check(view.TestRowCount() == 0 && notice.Text.Contains("第一层"), "子目录不递归搜索，空结果仍说明范围");
                FileScanner.Result bounded = Get<FileScanner.Result>(view, "cached");
                bounded.EnumerateTruncated = true; Invoke(view, "RenderList", bounded);
                Check(notice.Text.Contains("5000") && notice.Text.Contains("可能不完整") && notice.Text.Contains("没有匹配"),
                    "无匹配也明确显示扫描截断，不能误报全目录无结果");
                Check(notice.TextWrapping == TextWrapping.Wrap, "扫描不完整提示保留换行，不截断异常");
                bounded.EnumerateTruncated = false;
                view.ApplyFilter("同名"); view.HandleKey(Key.A, ModifierKeys.Control);
                File.Delete(a); File.Delete(b); view.Refresh(true); Wait(view, notice);
                Check(view.TestSelectedCount() == 0 && view.TestRowCount() == 0, "选中的文件消失后不改选其它文件");
                view.TestSetSources(new List<string> { first }); view.ApplyFilter("file000"); view.Refresh(true);
                view.OnLeave(); Pump(); view.OnEnter(); view.Refresh(false); Wait(view, notice);
                Check(view.TestRowCount() == 10, "首次扫描切页取消后，快速返回不会复用过期快照");
                view.TestSetSources(new List<string> { first }); view.ApplyFilter(""); view.Refresh(true);
                view.TestSetSources(new List<string> { second }); view.Refresh(true); Wait(view, notice);
                Check(view.TestRowCount() == 0 && notice.Text.Contains(second), "快速换来源丢弃过期扫描结果");
                Check(view.HandleKey(Key.F5, ModifierKeys.None), "空列表F5仍可更新"); Wait(view, notice);
                view.TestSetSources(null); view.ApplyFilter("file000"); view.Refresh(true); Wait(view, notice); window.UpdateLayout();
                scroll.ScrollToTop(); scroll.UpdateLayout();
                RenderTargetBitmap bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(testSurface);
                PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string screenshot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "files-library-narrow.png");
                using (FileStream stream = File.Create(screenshot)) encoder.Save(stream);
                Check(HasVisiblePixels(bitmap), "文件库窄窗口截图包含真实界面内容");
                Console.WriteLine("Screenshot: " + screenshot);

                Settings panelSettings = Settings.Load(root); panelSettings.AutoAppearance = false;
                // 上面的排序测试已保存倒序；导航夹具固定正序，避免把文件名当作当前排序。
                panelSettings.FilesSort = "name:asc";
                panel = new PanelWindow(root, null, panelSettings); panel.Show(); panel.TestSwitchCategory("文件"); Pump();
                panel.TestSetSearchText("file000"); Pump(); panel.TestSwitchCategory("常用"); Pump();
                Check(Get<TextBox>(panel, "searchBox").Text.Length == 0, "文件搜索不泄漏到应用页");
                panel.TestSwitchCategory("文件"); Pump();
                Check(Get<TextBox>(panel, "searchBox").Text == "file000", "实际PanelWindow返回文件页恢复搜索框");
                panel.HidePanel(); panel.ShowPanel(); Pump();
                Check(Get<TextBox>(panel, "searchBox").Text == "file000", "收起并重新打开面板也保留文件搜索");
                panel.Width = 640; panel.Height = 460;
                for (int i = 0; i < 200 && Get<FilesTabView>(panel, "filesView").Status == "更新中…"; i++)
                {
                    Pump(); System.Threading.Thread.Sleep(10);
                }
                panel.UpdateLayout();
                Check(panel.TestFilesRowCount() == 10, "实际面板后台读取后使用当前恢复的搜索词");
                Check(Get<TextBlock>(panel, "footerCount").Text == "10 个文件", "数量统一在右下角，应用异步回调不覆盖文件状态");
                Button refresh = Get<Button>(panel, "refreshFilesButton");
                TextBlock footer = Get<TextBlock>(panel, "footerCount");
                Check(refresh.Visibility == Visibility.Visible && refresh.Focusable
                    && ReferenceEquals(refresh.Parent, footer.Parent) && Grid.GetColumn(refresh) < Grid.GetColumn(footer),
                    "刷新在右下角数量前，独立且可键盘操作");
                Check(!panel.TestFooterHint().Contains("F5") && Convert.ToString(refresh.ToolTip).Contains("F5"),
                    "F5只在刷新按钮提示中说明，不重复占底栏");
                FilesTabView panelFiles = Get<FilesTabView>(panel, "filesView");
                Border panelKept = Row(panelFiles, Path.Combine(first, "file0000.txt"));
                panel.Activate();
                Invoke(panelFiles, "ClearSelection");
                panelFiles.HandleKey(Key.Down, ModifierKeys.None);
                panelFiles.HandleKey(Key.Down, ModifierKeys.Shift);
                panelFiles.HandleKey(Key.Down, ModifierKeys.Shift);
                Check(panelFiles.TestSelectedCount() == 3 && Current(panelFiles).Name == "file0002.txt",
                    "连续Shift向下从当前行扩展，不回到范围起点");
                panelFiles.HandleKey(Key.Up, ModifierKeys.Shift);
                Check(panelFiles.TestSelectedCount() == 2 && Current(panelFiles).Name == "file0001.txt",
                    "Shift反向移动收缩范围，当前操作目标同步");
                CancelBatchDelete(panelFiles, 2);
                Check(File.Exists(Path.Combine(first, "file0000.txt")) && File.Exists(Path.Combine(first, "file0001.txt")),
                    "取消两项删除不改变临时来源文件");
                for (int i = 0; i < 3; i++) panelFiles.HandleKey(Key.Down, ModifierKeys.Shift);
                CancelBatchDelete(panelFiles, 5);
                bool batchIntact = true;
                for (int i = 0; i < 5; i++) batchIntact &= File.Exists(Path.Combine(first, "file000" + i + ".txt"));
                Check(batchIntact, "取消五项删除保留所有临时来源文件");
                panelFiles.HandleKey(Key.A, ModifierKeys.Control);
                Border tabTarget = Row(panelFiles, Path.Combine(first, "file0007.txt"));
                tabTarget.Focus();
                Check(Current(panelFiles).Name == "file0007.txt" && panelFiles.TestSelectedCount() == 10,
                    "Tab焦点行决定打开和预览目标，不改变多选集合");
                panelFiles.Refresh(true); Wait(panelFiles, null);
                Check(Current(panelFiles).Name == "file0007.txt" && panelFiles.TestSelectedCount() == 10,
                    "刷新按路径保留当前操作目标和多选");
                StackPanel panelList = Get<StackPanel>(panelFiles, "listPanel");
                Button panelSort = (Button)((Grid)((Border)panelList.Children[0]).Child).Children[0];
                Click(panelSort);
                Check(Current(panelFiles).Name == "file0007.txt" && panelFiles.TestSelectedCount() == 10,
                    "排序后当前打开与预览目标按路径保持，不切到第一项");
                Click(panelSort);
                Invoke(panelFiles, "ClearSelection");
                panelFiles.HandleKey(Key.Down, ModifierKeys.None);
                panelFiles.HandleKey(Key.Down, ModifierKeys.Shift);
                panelFiles.Refresh(true); Wait(panelFiles, null);
                panelFiles.HandleKey(Key.Down, ModifierKeys.Shift);
                Check(panelFiles.TestSelectedCount() == 3 && Current(panelFiles).Name == "file0002.txt",
                    "刷新保留范围起点与移动位置，可继续Shift扩展");
                Invoke(panelFiles, "ClearSelection");
                Check(Current(panelFiles) == null && !panelFiles.HandleKey(Key.Enter, ModifierKeys.None),
                    "取消选择后不沿用旧操作目标");
                panelFiles.HandleKey(Key.A, ModifierKeys.Control);
                panelKept.Focus();
                KeyEventArgs fileMenuKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Apps)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                panel.RaiseEvent(fileMenuKey);
                ContextMenu fileMenu = Get<ContextMenu>(panelFiles, "activeItemMenu");
                Check(fileMenuKey.Handled && fileMenu != null && fileMenu.IsOpen
                    && fileMenu.Placement == System.Windows.Controls.Primitives.PlacementMode.Bottom
                    && ReferenceEquals(fileMenu.PlacementTarget, panelKept) && panelFiles.TestSelectedCount() == 10,
                    "文件菜单键对应焦点行且不清多选");
                fileMenu.IsOpen = false; Pump();
                Check(panelKept.IsKeyboardFocused && panelFiles.FilterQuery == "file000"
                    && panelFiles.TestSelectedCount() == 10, "文件菜单关闭返回列表，保留搜索与多选");
                Check(panelFiles.HandleKey(Key.F10, ModifierKeys.Shift)
                    && Get<ContextMenu>(panelFiles, "activeItemMenu").IsOpen,
                    "文件列表Shift+F10可打开同一菜单");
                Get<ContextMenu>(panelFiles, "activeItemMenu").IsOpen = false; Pump();
                Get<TextBox>(panel, "searchBox").Focus();
                Invoke(panelFiles, "RestoreListFocus", new object[] { null });
                Check(Keyboard.FocusedElement is Border && panelFiles.TestSelectedCount() == 10
                    && !Get<TextBox>(panel, "searchBox").IsKeyboardFocusWithin,
                    "文件操作后的重新激活回到文件行，不回搜索框");
                panelFiles.HandleKey(Key.Down, ModifierKeys.None);
                Border afterArrow = Keyboard.FocusedElement as Border;
                Check(afterArrow != null && panelFiles.TestSelectedCount() == 1
                    && panelFiles.HandleKey(Key.Apps, ModifierKeys.None)
                    && ReferenceEquals(Get<ContextMenu>(panelFiles, "activeItemMenu").PlacementTarget, afterArrow),
                    "方向键移动后菜单跟随新选择，不使用旧焦点行");
                Get<ContextMenu>(panelFiles, "activeItemMenu").IsOpen = false; Pump();
                panelFiles.HandleKey(Key.A, ModifierKeys.Control);
                foreach (UIElement child in Get<WrapPanel>(panel, "navStack").Children)
                {
                    Button tab = child as Button;
                    if (tab != null && System.Windows.Automation.AutomationProperties.GetName(tab) == "文件库标签")
                        Click(tab);
                }
                Check(panelFiles.FilterQuery == "file000" && panelFiles.TestSelectedCount() == 10
                    && ReferenceEquals(panelKept, Row(panelFiles, Path.Combine(first, "file0000.txt"))),
                    "重复点击文件库标签不清搜索、多选或重建行");
                Click(refresh);
                for (int i = 0; i < 200 && panelFiles.Status == "更新中…"; i++)
                { Pump(); System.Threading.Thread.Sleep(10); }
                Check(panelFiles.FilterQuery == "file000" && ReferenceEquals(panelKept, Row(panelFiles, Path.Combine(first, "file0000.txt"))),
                    "点击底部刷新保留搜索和列表行");
                panelKept.Focus(); panelFiles.HandleKey(Key.Apps, ModifierKeys.None);
                ContextMenu staleMenu = Get<ContextMenu>(panelFiles, "activeItemMenu");
                Clipboard.SetText("stale-file-menu-sentinel");
                panelFiles.ApplyFilter("file0009");
                Check(!staleMenu.IsOpen && panelFiles.TestSelectedCount() == 1, "筛选关闭旧文件菜单且保留仍可见选择");
                MenuAction(staleMenu, "复制文件").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(Clipboard.ContainsText() && Clipboard.GetText() == "stale-file-menu-sentinel",
                    "旧文件菜单不能改为复制筛选后剩下的项目");
                panelFiles.ApplyFilter("file000");
                panelKept.Focus(); panelFiles.HandleKey(Key.Apps, ModifierKeys.None);
                ContextMenu leavingMenu = Get<ContextMenu>(panelFiles, "activeItemMenu");
                panelFiles.OnLeave();
                MenuAction(leavingMenu, "复制路径").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(!leavingMenu.IsOpen && Clipboard.GetText() == "stale-file-menu-sentinel",
                    "离开文件页关闭菜单，旧复制路径回调失效");
                panelFiles.OnEnter();
                panel.TestSwitchCategory(""); Pump();
                Check(refresh.Visibility == Visibility.Collapsed, "应用页不出现文件刷新按钮");
                panel.TestSwitchCategory(PanelWindow.VirtualDesktopCategory); Pump();
                Check(refresh.Visibility == Visibility.Collapsed, "桌面收纳不出现文件刷新按钮");
                panel.TestSwitchCategory("文件"); Pump();
                panel.TestSetSearchText(""); Pump(); panel.UpdateLayout();
                WrapPanel filters = Get<WrapPanel>(panel, "filesExtRow");
                bool filtersFit = true;
                foreach (FrameworkElement filter in filters.Children)
                {
                    Point at = filter.TranslatePoint(new Point(), filters);
                    if (at.X < 0 || at.X + filter.ActualWidth > filters.ActualWidth + 0.5) filtersFit = false;
                }
                Check(filtersFit && filters.ActualHeight > 28, "640像素筛选自然换行，所有按钮可见且无横向滚动条");
                FrameworkElement surface = (FrameworkElement)panel.Content;
                RenderTargetBitmap panelBitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight,
                    96, 96, PixelFormats.Pbgra32);
                panelBitmap.Render(surface);
                PngBitmapEncoder panelEncoder = new PngBitmapEncoder(); panelEncoder.Frames.Add(BitmapFrame.Create(panelBitmap));
                using (FileStream stream = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "files-library-panel.png")))
                    panelEncoder.Save(stream);
                Color lightAccent = Ui.Accent.Color;
                Ui.ApplyTheme(Ui.ThemeMode.Dark);
                Check(!Ui.Accent.IsFrozen && Ui.AppliedTheme == Ui.ThemeMode.Dark,
                    "文件筛选按钮模板不冻结主题刷子，深色主题仍可切换");
                panel.UpdateLayout();
                panelBitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                panelBitmap.Render(surface);
                panelEncoder = new PngBitmapEncoder(); panelEncoder.Frames.Add(BitmapFrame.Create(panelBitmap));
                using (FileStream stream = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "files-library-panel-dark.png")))
                    panelEncoder.Save(stream);
                Ui.ApplyTheme(Ui.ThemeMode.Light);
                TestFileDisplay(root);
            }
            catch (Exception ex) { Check(false, ex.ToString()); }
            finally
            {
                try { if (previousClipboard != null) Clipboard.SetDataObject(previousClipboard, true); else Clipboard.Clear(); }
                catch { }
                if (view != null) view.Dispose();
                if (panel != null) panel.Close();
                if (window != null) window.Close();
                app.Shutdown();
                // 测试独占、明确创建的临时目录，绝不使用用户来源。
                Directory.Delete(root, true);
            }
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            return failed == 0 ? 0 : 1;
        }

        static Image Icon(Border row)
        {
            Grid slot = (Grid)((Grid)row.Child).Children[0];
            return slot.Children.Count > 0 ? slot.Children[0] as Image : null;
        }

        static void SaveDisplay(Window window, string name)
        {
            FrameworkElement visual = (FrameworkElement)window.Content;
            visual.UpdateLayout();
            RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
                (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
            using (FileStream stream = File.Create(path)) encoder.Save(stream);
            Console.WriteLine("Screenshot: " + path);
        }

        static void TestFileDisplay(string sandbox)
        {
            string root = Path.Combine(sandbox, "display-test"), documents = Path.Combine(root, "文档");
            Directory.CreateDirectory(documents);
            string[] names = new string[] { "实验报告+神江+202471030634.docx", "实验报告模板.docx",
                "Pyecharts练习数据.xlsx", "第4章 使用matplotlib绘制简单图表.pptx", "课程说明.pdf", "资料归档.zip",
                "网页入口.html", "未知格式.cloverunknown", "文件名称很长但不应挤占时间与大小列_" + new string('长', 45) + ".docx" };
            Dictionary<string, byte[]> contents = new Dictionary<string, byte[]>();
            foreach (string name in names)
            {
                string path = Path.Combine(documents, name);
                File.WriteAllText(path, "test-only-not-a-real-document");
                contents[path] = File.ReadAllBytes(path);
            }
            string folder = Path.Combine(documents, "26年5月4日更新-最新音源");
            Directory.CreateDirectory(folder);
            FileFolders sources = FileFolders.Load(root); sources.Add(documents);
            Settings settings = Settings.Load(root); settings.AutoAppearance = false; settings.FilesSort = "name:asc";
            Window window = null;
            FilesTabView view = null;
            try
            {
                Ui.ApplyTheme(Ui.ThemeMode.Light);
                WrapPanel chips = new WrapPanel(), filters = new WrapPanel();
                StackPanel list = new StackPanel(), undo = new StackPanel(), content = new StackPanel();
                TextBlock notice = Ui.T("", 12, Ui.Sub);
                content.Children.Add(chips); content.Children.Add(filters); content.Children.Add(notice);
                content.Children.Add(list); content.Children.Add(undo);
                ScrollViewer scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                Border surface = new Border { Background = Ui.Bg, Padding = new Thickness(12), Child = scroll };
                window = new Window { Width = 760, Height = 620, Content = surface, ShowInTaskbar = false };
                window.Show();
                view = new FilesTabView(null, settings, root, chips, filters, list, notice, undo);
                view.OnEnter(); view.Refresh(true); Wait(view, notice); window.UpdateLayout();
                Border report = Row(view, Path.Combine(documents, names[0]));
                Grid reportSlot = (Grid)((Grid)report.Child).Children[0];
                view.HandleKey(Key.A, ModifierKeys.Control);
                int selectedCount = view.TestSelectedCount();
                int pending = 0;
                for (int tick = 0; tick < 400; tick++)
                {
                    Pump(); window.UpdateLayout();
                    pending = 0;
                    foreach (Border row in Get<Dictionary<string, Border>>(view, "rowMap").Values)
                        if (Icon(row) == null) pending++;
                    if (pending == 0) break;
                    System.Threading.Thread.Sleep(10);
                }
                Check(pending == 0, "Word、Excel、PPT、PDF、网页、压缩包和文件夹均读到Windows关联图标");
                Check(ReferenceEquals(report, Row(view, Path.Combine(documents, names[0])))
                    && ReferenceEquals(reportSlot, ((Grid)report.Child).Children[0]) && selectedCount == view.TestSelectedCount(),
                    "异步补图标只更新图标槽，不重建行或丢多选");
                Check(ReferenceEquals(Icon(report).Source, Icon(Row(view, Path.Combine(documents, names[1]))).Source),
                    "同类型文档跨路径复用同一系统图标");
                Check(Icon(Row(view, folder)) != null && Convert.ToString(((Grid)((Grid)Row(view, folder).Child).Children[0]).Tag) == "folder",
                    "文件夹使用Windows文件夹图标而非通用纸张");
                foreach (string name in names)
                {
                    Border row = Row(view, Path.Combine(documents, name));
                    Image icon = Icon(row);
                    Check(icon != null && ((BitmapSource)icon.Source).IsFrozen && icon.Width == 24 && icon.Height == 24,
                        "图标已冻结可跨线程且大小一致：" + Path.GetExtension(name));
                }
                Check(FileListIcons.Key("DOCX", false) == FileListIcons.Key(".docx", false)
                    && FileListIcons.Key(".exe", true) == "folder" && !FileListIcons.HasOwnIcon(".docx")
                    && FileListIcons.HasOwnIcon(".EXE"), "文档按类型归并，独立程序仍按实际路径取图标");

                // 对Windows结果做像素对照，而非只检查“有一张图片”。
                foreach (string ext in new string[] { ".docx", ".xlsx", ".pptx", ".pdf", ".zip", ".html" })
                {
                    Image actual = null;
                    foreach (string name in names) if (name.EndsWith(ext, StringComparison.Ordinal))
                        { actual = Icon(Row(view, Path.Combine(documents, name))); break; }
                    BitmapSource expected = (BitmapSource)IconCache.GetFileType(ext, false);
                    BitmapSource image = actual == null ? null : actual.Source as BitmapSource;
                    Check(image != null && SamePixels(image, expected), ext + "显示与Windows关联图标逐像素一致");
                }
                view.HandleKey(Key.Escape, ModifierKeys.None); scroll.ScrollToTop(); Pump(); window.UpdateLayout();
                SaveDisplay(window, "files-display-light.png");
                foreach (double width in new double[] { 640, 760, 1050 })
                {
                    window.Width = width; window.UpdateLayout(); Pump(); window.UpdateLayout();
                    Grid row = (Grid)report.Child;
                    FrameworkElement icon = (FrameworkElement)row.Children[0];
                    TextBlock name = (TextBlock)row.Children[1];
                    TextBlock time = (TextBlock)row.Children[2];
                    Point iconAt = icon.TranslatePoint(new Point(), row), nameAt = name.TranslatePoint(new Point(), row);
                    Point timeAt = time.TranslatePoint(new Point(), row);
                    Check(iconAt.X + icon.ActualWidth <= nameAt.X + 0.5
                        && nameAt.X + name.ActualWidth <= timeAt.X + 0.5
                        && row.ActualWidth <= list.ActualWidth + 0.5, "图标、名称与时间不重叠，窗口宽度 " + width);
                    Check(ReferenceEquals(report, Row(view, Path.Combine(documents, names[0])))
                        && ReferenceEquals(reportSlot, row.Children[0]), "缩放不重建文件行或图标槽，窗口宽度 " + width);
                }
                TextBlock longName = (TextBlock)((Grid)Row(view, Path.Combine(documents, names[names.Length - 1])).Child).Children[1];
                Check(longName.Text == names[names.Length - 1] && longName.TextTrimming == TextTrimming.CharacterEllipsis
                    && Convert.ToString(longName.ToolTip).Contains(names[names.Length - 1]),
                    "长文件名不裁剪底层文本，省略显示且悬停可读完整路径");
                window.Width = 640; window.Height = 460; Pump(); window.UpdateLayout();
                SaveDisplay(window, "files-display-narrow.png");
                Ui.ApplyTheme(Ui.ThemeMode.Dark); window.UpdateLayout(); Pump();
                Check(!Ui.Sub.IsFrozen && !Ui.Accent.IsFrozen && SamePixels((BitmapSource)Icon(report).Source,
                    (BitmapSource)IconCache.GetFileType(".docx", false)), "深色主题不染色文件图标、不冻结共享刷子");
                SaveDisplay(window, "files-display-dark.png");
                view.ApplyFilter("实验报告"); view.Refresh(true); Wait(view, notice); Pump();
                Check(ReferenceEquals(report, Row(view, Path.Combine(documents, names[0])))
                    && ReferenceEquals(reportSlot, ((Grid)report.Child).Children[0]), "筛选和刷新保留行与已加载图标");
                bool unchanged = true;
                foreach (KeyValuePair<string, byte[]> file in contents)
                    unchanged &= Convert.ToBase64String(File.ReadAllBytes(file.Key)) == Convert.ToBase64String(file.Value);
                Check(unchanged && Directory.Exists(folder), "图标显示不更改文档内容或目录");
                Check(IconCache.GetFileType("../.docx", false) == null, "关联图标仅接受类型，不允许真实路径输入");
                for (int i = 0; i < FileListIcons.Capacity + 8; i++)
                {
                    string key = FileListIcons.Key(".clovericon" + i, false);
                    bool done = false;
                    FileListIcons.GetAsync(key, delegate { done = true; });
                    for (int tick = 0; tick < 400 && !done; tick++) { Pump(); System.Threading.Thread.Sleep(1); }
                }
                Check(FileListIcons.CountForTest <= FileListIcons.Capacity, "文件类型图标缓存有界，不随来源增长无限积累");
                view.Dispose(); view = null; Pump();
                Ui.ApplyTheme(Ui.ThemeMode.Light);
            }
            finally
            {
                if (view != null) view.Dispose();
                if (window != null) window.Close();
                Ui.ApplyTheme(Ui.ThemeMode.Light);
            }
        }

        static bool SamePixels(BitmapSource left, BitmapSource right)
        {
            if (left == null || right == null || left.PixelWidth != right.PixelWidth || left.PixelHeight != right.PixelHeight)
                return false;
            FormatConvertedBitmap a = new FormatConvertedBitmap(left, PixelFormats.Bgra32, null, 0);
            FormatConvertedBitmap b = new FormatConvertedBitmap(right, PixelFormats.Bgra32, null, 0);
            int stride = left.PixelWidth * 4;
            byte[] first = new byte[stride * left.PixelHeight], second = new byte[first.Length];
            a.CopyPixels(first, stride, 0); b.CopyPixels(second, stride, 0);
            return Convert.ToBase64String(first) == Convert.ToBase64String(second);
        }

        static bool HasVisiblePixels(BitmapSource bitmap)
        {
            if (bitmap == null || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return false;
            int stride = bitmap.PixelWidth * 4;
            byte[] pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            for (int i = 0; i + 3 < pixels.Length; i += 4)
                if (pixels[i + 3] > 0 && (pixels[i] > 4 || pixels[i + 1] > 4 || pixels[i + 2] > 4)) return true;
            return false;
        }
    }
}
