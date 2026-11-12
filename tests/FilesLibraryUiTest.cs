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
        static Button ButtonWithTag(StackPanel host, string tag)
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
                StackPanel chips = new StackPanel { Orientation = Orientation.Horizontal };
                StackPanel exts = new StackPanel { Orientation = Orientation.Horizontal };
                StackPanel list = new StackPanel(), undo = new StackPanel(), content = new StackPanel();
                TextBlock notice = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Sub };
                content.Children.Add(new ScrollViewer { Content = chips, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
                content.Children.Add(new ScrollViewer { Content = exts, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
                content.Children.Add(notice); content.Children.Add(list); content.Children.Add(undo);
                ScrollViewer scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                window = new Window { Width = 640, Height = 460, Content = scroll, Background = Ui.Bg };
                window.Show();
                view = new FilesTabView(null, settings, root, chips, exts, list, notice, undo);
                view.OnEnter(); view.Refresh(true); Wait(view, notice); window.UpdateLayout();
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
                Check(((Grid)Row(view, Path.Combine(first, "文件夹")).Child).Children[0] is System.Windows.Shapes.Path,
                    "文件夹使用矢量图标，不依赖缺字的系统图标字体");
                view.HandleKey(Key.A, ModifierKeys.Control);
                Check(view.TestSelectedCount() == 500, "全选只选已显示项，不操作隐藏分页结果");
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
                Check(view.TestRowCount() == 2, "切回全部保留后缀选择：文件夹加PDF");
                Click(ButtonWithTag(exts, "pdf"));
                Click(ButtonWithTag(list, "file-load-more"));
                Check(view.TestRowCount() == 534 && ButtonWithTag(list, "file-load-more") == null, "加载更多只扩展展示，不重新扫描");
                view.ApplyFilter("同名");
                string a = Path.Combine(first, "同名.txt"), b = Path.Combine(second, "同名.txt");
                Border kept = Row(view, a), other = Row(view, b);
                TextBlock aSource = (TextBlock)((Grid)kept.Child).Children[4];
                TextBlock bSource = (TextBlock)((Grid)other.Child).Children[4];
                Check(view.TestRowCount() == 2 && aSource.Text != bSource.Text
                    && Convert.ToString(aSource.ToolTip) == first && Convert.ToString(bSource.ToolTip) == second,
                    "同名文件分别显示来源名称与完整来源路径");
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
                bitmap.Render(scroll);
                PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string screenshot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "files-library-narrow.png");
                using (FileStream stream = File.Create(screenshot)) encoder.Save(stream);
                Console.WriteLine("Screenshot: " + screenshot);

                Settings panelSettings = Settings.Load(root); panelSettings.AutoAppearance = false;
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
                Click(refresh);
                for (int i = 0; i < 200 && panelFiles.Status == "更新中…"; i++)
                { Pump(); System.Threading.Thread.Sleep(10); }
                Check(panelFiles.FilterQuery == "file000" && ReferenceEquals(panelKept, Row(panelFiles, Path.Combine(first, "file0000.txt"))),
                    "点击底部刷新保留搜索和列表行");
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
            }
            catch (Exception ex) { Check(false, ex.ToString()); }
            finally
            {
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
    }
}
