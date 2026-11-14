using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clover
{
    static class ResponsiveLayoutTest
    {
        static int passed, failed;
        static void Check(bool ok, string name)
        {
            if (ok) passed++; else failed++;
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
        }
        static T Field<T>(object value, string name)
        {
            return (T)value.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
        }
        static void Pump()
        {
            Dispatcher.CurrentDispatcher.Invoke(new Action(delegate { }),
                DispatcherPriority.ApplicationIdle);
        }
        static void Ready(PanelWindow panel)
        {
            for (int i = 0; i < 120 && !panel.TestDeferredRenderComplete(); i++)
            { Pump(); System.Threading.Thread.Sleep(20); }
            panel.UpdateLayout(); Pump();
        }
        static Rect Bounds(FrameworkElement element, Visual target)
        {
            return element.TransformToVisual(target).TransformBounds(new Rect(element.RenderSize));
        }
        static bool IsInside(DependencyObject child, DependencyObject parent)
        {
            while (child != null)
            {
                if (child == parent) return true;
                child = VisualTreeHelper.GetParent(child);
            }
            return false;
        }
        static void Save(PanelWindow panel, string name)
        {
            FrameworkElement content = (FrameworkElement)panel.Content;
            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight),
                96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream output = File.Create(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, name))) encoder.Save(output);
        }

        static void TestPanelMath()
        {
            foreach (double width in new double[] { 560, 599, 600, 604, 850, 950, 1400 })
            {
                ResponsiveTilePanel grid = new ResponsiveTilePanel();
                Border section = new Border { Margin = new Thickness(2),
                    Child = new TextBlock { Text = "Section", Height = 20 } };
                grid.Children.Add(section);
                List<Border> tiles = new List<Border>();
                for (int i = 0; i < 37; i++)
                {
                    Border tile = new Border { Width = PanelWindow.TileWidthPx,
                        Height = PanelWindow.TileHeightPx, Margin = new Thickness(2) };
                    tiles.Add(tile); grid.Children.Add(tile);
                }
                grid.Measure(new Size(width, double.PositiveInfinity));
                grid.Arrange(new Rect(0, 0, width, grid.DesiredSize.Height));
                grid.UpdateLayout();
                int columns = Math.Max(1, (int)Math.Floor(width / 100));
                Rect first = Bounds(tiles[0], grid);
                Rect last = Bounds(tiles[columns - 1], grid);
                Rect next = Bounds(tiles[columns], grid);
                Check(section.ActualWidth <= width && section.ActualWidth >= width - 4.1,
                    "section fills one safe row at " + width);
                Check(Math.Abs(first.Left - (width - last.Right)) < 0.1,
                    "left and right spaces balance at " + width);
                Check(Math.Abs(first.Top - last.Top) < 0.1 && next.Top >= first.Bottom,
                    "column breakpoint is stable at " + width);
                Check(first.Width == 96 && first.Height == 90,
                    "tile size stays fixed at " + width);
                Check(first.Top >= section.ActualHeight,
                    "section never shares a tile row at " + width);
            }
        }

        [STAThread]
        static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "CloverResize-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            PanelWindow panel = null;
            try
            {
                Ui.ApplyTheme(Ui.ThemeMode.Light);
                TestPanelMath();
                List<object> entries = new List<object>();
                for (int i = 0; i < 150; i++)
                {
                    string path = Path.Combine(root, "fixture" + i + ".exe");
                    File.WriteAllText(path, "fixture");
                    entries.Add(new Dictionary<string, object> {
                        { "name", "Long application name " + i + " 2026.11.13 (64-bit)" },
                        { "path", path }, { "category", "Other" } });
                }
                File.WriteAllText(Path.Combine(root, "config", "apps.json"),
                    new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                        { "apps", entries } }));
                Settings settings = Settings.Load(root);
                settings.AutoAppearance = false; settings.GlassOpacity = 0; settings.Save();
                panel = new PanelWindow(root, null, Settings.Load(root));
                // 离屏布局夹具不能被用户操作造成的前台焦点变化自动收起。
                typeof(PanelWindow).GetField("suppressDeactivate", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(panel, DateTime.MaxValue);
                panel.Left = -30000; panel.Top = -30000;
                panel.Width = 912; panel.Height = 632; panel.Show();
                panel.TestSwitchCategory(""); Ready(panel);
                Check(panel.TestDeferredRenderComplete()
                    && panel.TestRenderedTileSnapshot().Count == 150,
                    "real panel fixture rendered");

                ScrollViewer scroll = Field<ScrollViewer>(panel, "contentScroll");
                WrapPanel grid = Field<WrapPanel>(panel, "tilePanel");
                List<Border> original = panel.TestRenderedTileSnapshot();
                foreach (double width in new double[] { 640, 912, 1050, 1500, 641, 1000 })
                {
                    panel.ScrollToForTest(0); Pump(); panel.Width = width;
                    panel.UpdateLayout(); Pump();
                    List<Border> tiles = panel.TestRenderedTileSnapshot();
                    Check(object.ReferenceEquals(original[0], tiles[0])
                        && object.ReferenceEquals(original[149], tiles[149]),
                        "resize keeps tile identity at " + width);

                    Border search = Field<Border>(panel, "searchBorder");
                    TextBlock title = null;
                    foreach (object child in ((Grid)search.Parent).Children)
                    {
                        TextBlock text = child as TextBlock;
                        if (text != null) title = text;
                    }
                    Check(title != null && Bounds(search, panel).Left
                        - Bounds(title, panel).Right >= Ui.S.Md - 0.5,
                        "brand and search stay separated at " + width);
                    Button close = Field<Button>(panel, "closeBtn");
                    Check(Bounds(search, panel).Right <= Bounds(close, panel).Left
                        && Bounds(close, panel).Right <= panel.ActualWidth - 16,
                        "header controls do not collide at " + width);

                    int columns = Math.Max(1, (int)Math.Floor(grid.ActualWidth / 100));
                    Rect first = Bounds(tiles[0], grid);
                    Rect last = Bounds(tiles[columns - 1], grid);
                    Check(Math.Abs(first.Left - (grid.ActualWidth - last.Right)) < 1,
                        "real grid uses full width at " + width);
                    Point firstHit = tiles[0].TranslatePoint(new Point(48, 45), panel);
                    Point lastHit = tiles[columns - 1].TranslatePoint(new Point(48, 45), panel);
                    Check(IsInside(panel.InputHitTest(firstHit) as DependencyObject, tiles[0]),
                        "first tile click target tracks layout at " + width);
                    Check(IsInside(panel.InputHitTest(lastHit) as DependencyObject,
                        tiles[columns - 1]), "last tile click target tracks layout at " + width);

                    Button rescan = Field<Button>(panel, "rescanAppsButton");
                    TextBlock count = Field<TextBlock>(panel, "footerCount");
                    Check(Bounds(rescan, panel).Right <= Bounds(count, panel).Left + 0.5
                        && Bounds(count, panel).Right <= panel.ActualWidth - 16,
                        "footer stays readable at " + width);
                    TextBlock name = (TextBlock)((Border)((StackPanel)
                        tiles[0].Child).Children[1]).Child;
                    Check(name.ActualHeight <= 30
                        && name.TextTrimming == TextTrimming.CharacterEllipsis,
                        "long names stay within two lines at " + width);
                    if (width == 640 || width == 1050 || width == 1500)
                        Save(panel, "responsive-panel-" + width + ".png");
                }

                panel.Width = 912; panel.Height = 520; panel.UpdateLayout(); Pump();
                panel.ScrollToForTest(380); Pump();
                Border anchor = null; double relative = 0;
                foreach (Border tile in original)
                {
                    double y = Bounds(tile, grid).Top - scroll.VerticalOffset;
                    if (y + tile.ActualHeight > 0) { anchor = tile; relative = y; break; }
                }
                panel.Width = 640; panel.UpdateLayout(); Pump();
                Check(anchor != null && Math.Abs(Bounds(anchor, grid).Top
                    - scroll.VerticalOffset - relative) < 1,
                    "narrowing preserves visible application anchor");
                panel.Width = 1200; panel.UpdateLayout(); Pump();
                Check(Math.Abs(Bounds(anchor, grid).Top
                    - scroll.VerticalOffset - relative) < 1,
                    "widening preserves visible application anchor");
                Check(object.ReferenceEquals(scroll.Content,
                    Field<ScrollViewer>(panel, "contentScroll").Content),
                    "resize keeps the content host");
                panel.Height = panel.MinHeight; panel.UpdateLayout(); Pump();
                Check(scroll.ViewportHeight > 200
                    && Field<Button>(panel, "rescanAppsButton").IsVisible,
                    "minimum height keeps content and footer accessible");

                // 人为把分组边界放在不满行之后，方向键必须取真实下一行的对应列。
                panel.ScrollToForTest(0); panel.UpdateLayout(); Pump();
                int partial = Math.Max(2, (int)Math.Floor(grid.ActualWidth / 100)) - 1;
                Border sectionBreak = new Border { Height = 24, Background = Brushes.Transparent };
                grid.Children.Insert(grid.Children.IndexOf(original[partial]), sectionBreak);
                panel.UpdateLayout(); Pump();
                MethodInfo select = typeof(PanelWindow).GetMethod("SelectTile", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo move = typeof(PanelWindow).GetMethod("MoveSelection", BindingFlags.Instance | BindingFlags.NonPublic);
                select.Invoke(panel, new object[] { partial - 1, false });
                TextBlock tileName = (TextBlock)((Border)((StackPanel)original[partial - 1].Child).Children[1]).Child;
                Point selectedAt = tileName.TranslatePoint(new Point(), grid);
                select.Invoke(panel, new object[] { -1, false }); panel.UpdateLayout();
                Check((tileName.TranslatePoint(new Point(), grid) - selectedAt).Length < 0.1,
                    "application selection does not shift icon or label layout");
                select.Invoke(panel, new object[] { partial - 1, false });
                move.Invoke(panel, new object[] { Key.Down });
                Check(Field<int>(panel, "selectedIndex") == partial + partial - 1,
                    "down follows actual column across an incomplete section row");
                select.Invoke(panel, new object[] { partial - 1, false });
                sectionBreak.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseDownEvent });
                Check(Field<int>(panel, "selectedIndex") == -1,
                    "section row blank space clears application selection");
                grid.Children.Remove(sectionBreak); panel.UpdateLayout(); Pump();
                select.Invoke(panel, new object[] { 0, false });
                original[1].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                    { RoutedEvent = UIElement.MouseRightButtonDownEvent });
                Check(Field<int>(panel, "selectedIndex") == 1 && ReferenceEquals(Field<Border>(panel, "selectedTile"), original[1]),
                    "右键应用先同步当前选中项而非保留上一应用高亮");

                TextBox emptySearch = Field<TextBox>(panel, "searchBox");
                panel.Activate(); emptySearch.Focus();
                foreach (Key editKey in new[] { Key.Home, Key.End })
                {
                    KeyEventArgs edit = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, editKey)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    panel.RaiseEvent(edit);
                    Check(!edit.Handled && Field<int>(panel, "selectedIndex") == 1,
                        "空搜索框 " + editKey + " 不改变应用选择");
                }
                grid.Focus();
                KeyEventArgs appMenuKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Apps)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                panel.RaiseEvent(appMenuKey);
                ContextMenu appMenu = Field<ContextMenu>(panel, "activeTileMenu");
                Check(appMenuKey.Handled && appMenu != null && appMenu.IsOpen
                    && ReferenceEquals(appMenu.PlacementTarget, original[1])
                    && appMenu.Placement == System.Windows.Controls.Primitives.PlacementMode.Bottom,
                    "应用菜单键打开当前选中应用的菜单，位置跟随磁贴");
                appMenu.IsOpen = false; Pump();
                Check(grid.IsKeyboardFocused && Field<int>(panel, "selectedIndex") == 1,
                    "关闭键盘应用菜单回到应用网格并保留选择");
                panel.ScrollToForTest(100); panel.UpdateLayout(); Pump();
                double beforeSameTab = scroll.VerticalOffset;
                Border sameTile = Field<Border>(panel, "selectedTile");
                WrapPanel sameNav = Field<WrapPanel>(panel, "navStack");
                foreach (UIElement child in sameNav.Children)
                {
                    Button tab = child as Button;
                    if (tab != null && System.Windows.Automation.AutomationProperties.GetName(tab) == "全部标签")
                        tab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                Check(ReferenceEquals(sameTile, Field<Border>(panel, "selectedTile"))
                    && Math.Abs(scroll.VerticalOffset - beforeSameTab) < 0.5,
                    "重复点击全部标签保留选择和滚动位置");

                panel.TestSetSearchText("nonexistent-keyboard-fixture");
                for (int i = 0; i < 100 && !panel.TestSearchResultVisible(); i++)
                { Pump(); System.Threading.Thread.Sleep(20); }
                SearchResultList results = Field<SearchResultList>(panel, "searchResults");
                results.Clear();
                typeof(SearchResultList).GetMethod("AppendBatch", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(results, new object[] { new List<SearchResult> {
                        new SearchResult { Name = "First", Path = Path.Combine(root, "missing-first.exe"), Source = "已安装" },
                        new SearchResult { Name = "Second", Path = Path.Combine(root, "missing-second.exe"), Source = "已安装" } } });
                TextBox searchInput = Field<TextBox>(panel, "searchBox");
                panel.Activate(); searchInput.Focus();
                KeyEventArgs searchDown = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Down)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                panel.RaiseEvent(searchDown);
                Border firstSearch = Keyboard.FocusedElement as Border;
                KeyEventArgs secondDown = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Down)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                panel.RaiseEvent(secondDown);
                Border secondSearch = Keyboard.FocusedElement as Border;
                Check(searchDown.Handled && secondDown.Handled && firstSearch != null && secondSearch != null
                    && ((SearchResult)firstSearch.Tag).Name == "First" && ((SearchResult)secondSearch.Tag).Name == "Second",
                    "search arrow keys navigate current result rows rather than hidden application tiles");
                KeyEventArgs enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                panel.RaiseEvent(enter);
                Check(enter.Handled && Field<int>(panel, "selectedIndex") == -1,
                    "search Enter is handled by current results without selecting or launching a hidden tile");
                firstSearch.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                    { RoutedEvent = UIElement.MouseRightButtonDownEvent });
                Check(firstSearch.IsKeyboardFocused && !secondSearch.IsKeyboardFocused,
                    "右键搜索结果先同步当前选中项");
                KeyEventArgs searchMenuKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Apps)
                    { RoutedEvent = Keyboard.KeyDownEvent };
                firstSearch.RaiseEvent(searchMenuKey);
                ContextMenu searchMenu = Field<ContextMenu>(results, "activeMenu");
                Check(searchMenuKey.Handled && searchMenu != null && searchMenu.IsOpen
                    && searchMenu.Placement == System.Windows.Controls.Primitives.PlacementMode.Bottom
                    && ReferenceEquals(searchMenu.PlacementTarget, firstSearch),
                    "搜索结果菜单键定位当前结果");
                searchMenu.IsOpen = false; Pump();
                firstSearch.Focus();
                firstSearch.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Apps)
                    { RoutedEvent = Keyboard.KeyDownEvent });
                ContextMenu blankMenu = Field<ContextMenu>(results, "activeMenu");
                StackPanel searchHost = Field<StackPanel>(panel, "searchResultPanel");
                searchHost.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                Check(!blankMenu.IsOpen && searchHost.IsKeyboardFocused && !firstSearch.IsKeyboardFocused
                    && !searchInput.IsKeyboardFocused && panel.TestSearchResultCount() == 2
                    && searchInput.Text == "nonexistent-keyboard-fixture", "搜索空白取消焦点与菜单，保留词和结果");
                Check(!results.HandleKey(Key.Enter, false), "搜索空白取消后Enter不启动首项");
                firstSearch.Focus();
                foreach (UIElement child in Field<WrapPanel>(panel, "navStack").Children)
                {
                    Button tab = child as Button;
                    if (tab != null && System.Windows.Automation.AutomationProperties.GetName(tab) == "全部标签")
                        tab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                Check(panel.TestSearchResultCount() == 2 && Field<TextBox>(panel, "searchBox").Text == "nonexistent-keyboard-fixture",
                    "重复点击当前标签不清搜索与结果");
                int classificationCalls = 0;
                results.ClassificationChanged = delegate { classificationCalls++; };
                firstSearch.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                    { RoutedEvent = UIElement.MouseRightButtonUpEvent });
                ContextMenu oldMenu = Field<ContextMenu>(results, "activeMenu");
                Check(oldMenu != null && oldMenu.IsOpen && ReferenceEquals(oldMenu.PlacementTarget, firstSearch)
                    && ReferenceEquals(firstSearch.Background, Ui.Hover), "搜索右键菜单打开后仍标明操作对象");
                int previousGeneration = Field<int>(results, "generation");
                panel.TestSetSearchText("new-nonexistent-keyboard-fixture");
                Check(results.ShownCount == 0 && !results.Completed && Field<int>(results, "generation") > previousGeneration
                    && !oldMenu.IsOpen && Field<TextBlock>(panel, "footerCount").Text == "搜索中…",
                    "搜索词变化立即取消旧结果、旧回调与旧菜单，无需等待防抖");
                KeyEventArgs pendingEnter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                searchInput.Focus(); panel.RaiseEvent(pendingEnter);
                Check(pendingEnter.Handled && results.ShownCount == 0,
                    "防抖窗口内回车不打开上一搜索结果");
                MenuItem staleOption = (MenuItem)((MenuItem)oldMenu.Items[0]).Items[1];
                staleOption.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(classificationCalls == 0 && !(bool)typeof(SearchResultList)
                    .GetMethod("IsCurrentResult", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(results, new object[] { firstSearch.Tag }), "旧结果及已关闭菜单不能继续修改分类");
                for (int i = 0; i < 100 && !panel.TestSearchCompleted(); i++)
                { Pump(); System.Threading.Thread.Sleep(20); }
                Check(panel.TestSearchCompleted() && panel.TestSearchResultCount() == 0,
                    "新搜索完成后旧后台结果不会回填");
                results.ClassificationChanged = null;
                panel.TestSetSearchText(""); panel.TestSwitchCategory(""); Ready(panel);

                List<string> deskNames = new List<string>();
                for (int i = 1; i <= NavTabs.MaxTabs; i++) deskNames.Add("工作区" + i);
                panel.TestSetCustomTabs(deskNames);
                panel.TestSwitchCategory(PanelWindow.VirtualDesktopCategory);
                panel.Width = 640; panel.Height = 440; panel.UpdateLayout(); Pump();
                WrapPanel nav = Field<WrapPanel>(panel, "navStack");
                Grid deskPanel = Field<Grid>(panel, "deskPanel");
                WrapPanel tools = Field<WrapPanel>(panel, "deskToolRow");
                Check(nav.ActualHeight > 32 && deskPanel.ActualHeight > 180,
                    "ten desktop tabs wrap without collapsing the content area");
                bool toolsFit = true;
                foreach (FrameworkElement tool in tools.Children)
                {
                    Point at = tool.TranslatePoint(new Point(), tools);
                    if (at.X < -0.5 || at.X + tool.ActualWidth > tools.ActualWidth + 0.5)
                        toolsFit = false;
                }
                Check(toolsFit && tools.ActualHeight > 0,
                    "desktop toolbar stays inside the narrow panel");
                Rect toolBounds = Bounds(tools, panel);
                Check(scroll.VerticalOffset < 0.5 && toolBounds.Top >= 0
                    && toolBounds.Bottom <= panel.ActualHeight,
                    "switching from a scrolled app page keeps desktop actions visible");
                Save(panel, "responsive-desktop-640x440.png");
            }
            catch (Exception ex) { Check(false, ex.ToString()); }
            finally
            {
                try { if (panel != null) panel.Close(); } catch { }
                try { app.Shutdown(); } catch { }
            }
            Console.WriteLine("通过 " + passed + " 项，失败 " + failed + " 项");
            return failed == 0 ? 0 : 1;
        }
    }
}
