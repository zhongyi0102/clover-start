using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clover
{
    // 保留套件名；验证新版三类取代旧的自动完全隐藏。
    static class SuggestedHideUiTest
    {
        static int passed, failed;
        static void Check(bool ok, string name) { if (ok) passed++; else failed++; Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name); }
        static void Pump() { Dispatcher.CurrentDispatcher.Invoke(new Action(delegate { }), DispatcherPriority.ApplicationIdle); }
        static void Wait(PanelWindow panel) { for (int i = 0; i < 100 && !panel.TestDeferredRenderComplete(); i++) { Pump(); System.Threading.Thread.Sleep(20); } panel.UpdateLayout(); }
        static Border Tile(PanelWindow panel, string path) { int i = panel.TestRenderedIndexOfPath(path); return i < 0 ? null : panel.TestRenderedTileSnapshot()[i]; }
        static bool HasText(DependencyObject item, string text)
        {
            TextBlock block = item as TextBlock;
            if (block != null && block.Text.Contains(text)) return true;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) if (HasText(VisualTreeHelper.GetChild(item, i), text)) return true;
            return false;
        }
        static void WriteApps(string root, List<AppItem> apps)
        {
            List<object> entries = new List<object>();
            foreach (AppItem app in apps) entries.Add(new Dictionary<string, object> { { "name", app.Name }, { "path", app.Path }, { "category", "开发" }, { "source", app.Source ?? "" } });
            File.WriteAllText(Path.Combine(root, "config", "apps.json"), new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "apps", entries } }));
        }
        [STAThread]
        static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "CloverThreeClassesUi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            Application application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            PanelWindow panel = null;
            try
            {
                Ui.ApplyTheme(Ui.ThemeMode.Light);
                List<AppItem> apps = new List<AppItem>();
                foreach (string name in new[] { "Python", "Python 用户界面", "IDLE (Python 3.13 64-bit)", "Anaconda Navigator", "Spyder", "Python 手动隐藏", "手册" })
                {
                    string path = Path.Combine(root, "app" + apps.Count + (name == "手册" ? ".html" : ".exe"));
                    File.WriteAllText(path, "fixture"); apps.Add(new AppItem { Name = name, Path = path });
                }
                AppItem storeA = new AppItem { Name = "ChatGPT", Path = @"shell:AppsFolder\Test.ChatGPT_123!App", Source = AppPaths.SourceStore };
                AppItem storeB = new AppItem { Name = "Claude", Path = @"shell:AppsFolder\Test.Claude_123!App", Source = AppPaths.SourceStore };
                AppItem storeLow = new AppItem { Name = "Dolby Access", Path = @"shell:AppsFolder\Test.Dolby_123!App", Source = AppPaths.SourceStore };
                AppItem shellUser = new AppItem { Name = "控制面板", Path = @"shell:AppsFolder\Test.ControlPanel", Source = AppPaths.SourceAppsFolder };
                apps.AddRange(new[] { storeA, storeB, storeLow, shellUser });
                WriteApps(root, apps);
                HiddenApps hidden = HiddenApps.Load(root);
                hidden.Hide(apps[0].Path, HideReason.Automatic("运行环境入口")); hidden.Hide(apps[5].Path);
                Settings settings = Settings.Load(root);
                panel = new PanelWindow(root, null, settings); panel.Show(); panel.TestSwitchCategory(""); Wait(panel);
                Check(Tile(panel, apps[0].Path) == null && !HiddenApps.Load(root).IsHidden(apps[0].Path), "电脑类不平铺，但不再使用完全隐藏");
                Check(Tile(panel, apps[2].Path) != null && panel.TestClassificationOf(apps[2].Path) == AppRole.LowValue, "IDLE 为低价值界面");
                Check(Tile(panel, apps[3].Path) != null && panel.TestClassificationOf(apps[3].Path) == AppRole.LowValue, "Navigator 为低价值界面");
                Check(Tile(panel, apps[5].Path) == null && HiddenApps.Load(root).IsHidden(apps[5].Path), "手动隐藏保持不展示");
                Check(panel.TestSearchAllowed(new SearchResult { Name = "Python", Path = apps[0].Path })
                    && panel.TestSearchRole(new SearchResult { Name = "Python", Path = apps[0].Path }) == 2,
                    "生产搜索允许电脑类且注入最后一组排序");
                Check(!panel.TestSearchAllowed(new SearchResult { Name = "Python 手动隐藏", Path = apps[5].Path }),
                    "生产搜索过滤手动隐藏项");
                Check(!panel.TestSearchAllowed(new SearchResult { Name = "卸载", Path = Path.Combine(root, "uninstall.exe") }),
                    "系统补充不能重新带回卸载入口");
                Check(Tile(panel, apps[6].Path) == null, "文档入口不加入应用");
                Check(panel.TestAuxiliaryHeaderCount() == 0 && HasText(panel, "用户用的"), "新版标题正确且无旧辅助区域");
                Check(panel.TestRenderedIndexOfPath(shellUser.Path) < panel.TestRenderedIndexOfPath(storeA.Path)
                    && panel.TestRenderedIndexOfPath(apps[4].Path) < panel.TestRenderedIndexOfPath(storeA.Path),
                    "全部页普通应用与真正商店应用集中，AppsFolder系统入口不算商店");
                Check(panel.TestRenderedIndexOfPath(storeB.Path) < panel.TestRenderedIndexOfPath(apps[2].Path)
                    && panel.TestRenderedIndexOfPath(apps[2].Path) < panel.TestRenderedIndexOfPath(storeLow.Path),
                    "角色优先，用户商店仍在低价值之前，低价值内也集中商店");
                Border storeTile = Tile(panel, storeA.Path);
                panel.TestOrder(new List<string> { storeB.Path, apps[4].Path, storeLow.Path, shellUser.Path, storeA.Path,
                    apps[3].Path, apps[1].Path, apps[2].Path });
                Check(panel.TestRenderedIndexOfPath(apps[4].Path) == 0 && panel.TestRenderedIndexOfPath(shellUser.Path) == 1
                    && panel.TestRenderedIndexOfPath(apps[1].Path) == 2 && panel.TestRenderedIndexOfPath(storeB.Path) == 3
                    && panel.TestRenderedIndexOfPath(storeA.Path) == 4, "旧人工混排稳定分区，保留普通和商店各自顺序");
                Check(ReferenceEquals(storeTile, Tile(panel, storeA.Path)), "集中排序复用现有商店磁贴");
                panel.TestCommitDrag(panel.TestRenderedIndexOfPath(storeA.Path), panel.TestRenderedIndexOfPath(storeB.Path));
                Check(panel.TestRenderedIndexOfPath(storeA.Path) == 3 && panel.TestRenderedIndexOfPath(storeB.Path) == 4,
                    "商店组内拖动仍可调整顺序");
                panel.TestCommitDrag(panel.TestRenderedIndexOfPath(storeB.Path), 0);
                Check(panel.TestRenderedIndexOfPath(storeB.Path) == 3 && panel.TestRenderedIndexOfPath(apps[4].Path) == 0,
                    "跨组拖动不打散普通与商店集中规则");
                panel.RefreshCatalogQuiet(); Wait(panel);
                Check(panel.TestRenderedIndexOfPath(storeB.Path) == 3 && panel.TestRenderedIndexOfPath(storeA.Path) == 4,
                    "重载保持商店组内拖动顺序");
                Check(panel.TestDragHit(0, 1, 0.25, 0.5) == 1 && panel.TestDragHit(0, 1, 0.75, 0.5) == 2,
                    "左右半格分别表示插入前后，不必拖出整个图标边缘");
                Check(panel.TestDragHit(0, 3, 0.75, 0.5) == 3, "预览落点受商店边界约束，与实际换位一致");
                List<Border> animationTiles = panel.TestRenderedTileSnapshot();
                string movedPath = apps[4].Path;
                panel.TestAnimatedDragDrop(0, 3);
                Check(panel.TestRenderedIndexOfPath(movedPath) == 2, "松手立即提交，不等落入动画再换位");
                Check(Ui.ReducedMotion || panel.TestAnimatedDragTileCount() > 0, "周围受影响磁贴同时短促让位");
                Check(ReferenceEquals(animationTiles[0], Tile(panel, movedPath)), "落入动画保留磁贴身份和选择映射");
                panel.TestCancelTileDrag();
                Check(!panel.TestDragGhostVisible() && panel.TestAnimatedDragTileCount() == 0
                    && Math.Abs(Tile(panel, movedPath).Opacity - 1) < 0.01, "中断落入清理浮层、动画和透明度");
                DateTime cancelDeadline = DateTime.UtcNow.AddMilliseconds(250);
                while (DateTime.UtcNow < cancelDeadline) { Pump(); System.Threading.Thread.Sleep(10); }
                Check(panel.TestRenderedIndexOfPath(movedPath) == 2, "旧落入回调不会再次提交改变顺序");
                panel.TestCommitDrag(panel.TestRenderedIndexOfPath(movedPath), 0);
                panel.TestAnimatedDragDrop(0, 3);
                DateTime completeDeadline = DateTime.UtcNow.AddMilliseconds(300);
                while (DateTime.UtcNow < completeDeadline) { Pump(); System.Threading.Thread.Sleep(10); }
                Check(!panel.TestDragGhostVisible() && Math.Abs(Tile(panel, movedPath).Opacity - 1) < 0.01,
                    "自然落入完成后恢复原磁贴，无残留浮层或透明度");
                panel.TestCancelTileDrag();
                panel.TestCommitDrag(panel.TestRenderedIndexOfPath(movedPath), 0);
                Border spyder = Tile(panel, apps[4].Path), idle = Tile(panel, apps[2].Path);
                panel.TestClassification(apps[4].Path, AppRole.LowValue);
                Check(ReferenceEquals(spyder, Tile(panel, apps[4].Path)) && ReferenceEquals(idle, Tile(panel, apps[2].Path)), "改为低价值复用全部现有图标");
                panel.TestClassification(apps[4].Path, AppRole.Computer);
                Check(Tile(panel, apps[4].Path) == null && ReferenceEquals(idle, Tile(panel, apps[2].Path)), "改电脑类只移除目标图标");
                panel.TestClassification(apps[4].Path, AppRole.User);
                Check(Tile(panel, apps[4].Path) != null && ReferenceEquals(idle, Tile(panel, apps[2].Path)), "改回用户类不重建其他图标");
                Check(panel.TestRenderedIndexOfPath(apps[4].Path) < panel.TestRenderedIndexOfPath(storeB.Path)
                    && panel.TestRenderedIndexOfPath(storeLow.Path) > panel.TestRenderedIndexOfPath(apps[2].Path),
                    "重新分类的增量刷新仍保持商店集中");
                panel.TestSetCollapseLowValue(true); Check(Tile(panel, apps[2].Path) == null, "低价值可收起");
                panel.TestSetCollapseLowValue(false);
                AppItem newApp = new AppItem { Name = "Node.js", Path = Path.Combine(root, "new-node.exe") };
                File.WriteAllText(newApp.Path, "fixture"); apps.Add(newApp); WriteApps(root, apps);
                panel.RefreshCatalogQuiet(); Wait(panel);
                Check(panel.TestClassificationOf(newApp.Path) == AppRole.User && Tile(panel, newApp.Path) != null, "新安装命中旧后台规则也默认用户类");
                panel.RefreshCatalogQuiet(); Wait(panel);
                Check(panel.TestClassificationOf(newApp.Path) == AppRole.User, "重复扫描不降级新应用");
                AppItem newStore = new AppItem { Name = "新商店应用", Path = @"shell:AppsFolder\Test.New_123!App", Source = AppPaths.SourceStore };
                apps.Add(newStore); WriteApps(root, apps); panel.RefreshCatalogQuiet(); Wait(panel);
                Check(panel.TestClassificationOf(newStore.Path) == AppRole.User
                    && panel.TestRenderedIndexOfPath(newStore.Path) > panel.TestRenderedIndexOfPath(newApp.Path)
                    && panel.TestRenderedIndexOfPath(newStore.Path) < panel.TestRenderedIndexOfPath(apps[2].Path),
                    "未来新商店应用默认用户类，并进入用户类商店集中区");
                panel.TestSetCollapseLowValue(true);
                Check(Tile(panel, storeLow.Path) == null && Tile(panel, storeB.Path) != null, "折叠低价值不影响用户商店区");
                panel.TestSetCollapseLowValue(false);

                Dictionary<string, string> shortcutCache = (Dictionary<string, string>)typeof(ShortcutResolver)
                    .GetField("cache", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
                string sharedLink = Path.Combine(root, "shared-host.lnk");
                shortcutCache[sharedLink] = apps[0].Path;
                Check(panel.TestSearchRole(new SearchResult { Name = "Jupyter 新入口", Path = sharedLink }) == 0,
                    "唯一电脑宿主不能覆盖不同功能入口的默认用户类");
                string hiddenHostLink = Path.Combine(root, "hidden-host.lnk");
                shortcutCache[hiddenHostLink] = apps[5].Path;
                Check(panel.TestSearchAllowed(new SearchResult { Name = "共享宿主桌宠", Path = hiddenHostLink }),
                    "隐藏一个入口不连带屏蔽共享宿主的其他应用");
                SearchResult extra = new SearchResult { Name = "搜索新应用", Path = Path.Combine(root, "extra.exe") };
                File.WriteAllText(extra.Path, "fixture");
                panel.TestClassifySearch(extra, AppRole.LowValue);
                panel.RefreshCatalogQuiet(); Wait(panel);
                Check(panel.TestClassificationOf(extra.Path) == AppRole.LowValue && Tile(panel, extra.Path) != null,
                    "搜索补充入口调整分类后重载保留在面板");

                StackPanel host = new StackPanel(); SearchResultList search = new SearchResultList(host);
                AppItem u = new AppItem { Name = "Python 用户界面", Path = apps[1].Path, Role = AppRole.User };
                AppItem l = new AppItem { Name = "Python Shell", Path = apps[2].Path, Role = AppRole.LowValue, Usage = 100 };
                AppItem c = new AppItem { Name = "Python", Path = apps[0].Path, Role = AppRole.Computer, Usage = 999 };
                AppItem h = new AppItem { Name = "Python 手动隐藏", Path = apps[5].Path, Hidden = true };
                search.CatalogProvider = delegate { return new List<AppItem> { c, h, l, u }; };
                SystemSearch.SetIndexBuilderForTest(delegate { return new List<SearchResult>(); });
                search.Search("Python");
                for (int i = 0; i < 100 && !search.Completed; i++) { Pump(); System.Threading.Thread.Sleep(10); }
                List<SearchResult> shown = new List<SearchResult>();
                foreach (UIElement child in host.Children) { Border row = child as Border; if (row != null) shown.Add(row.Tag as SearchResult); }
                Check(shown.Count == 3 && shown[0].RoleRank == 0 && shown[1].RoleRank == 1 && shown[2].RoleRank == 2, "真实搜索按用户/低价值/电脑排序，不显示手动隐藏");
                Check(HasText(host, "电脑用的（仅搜索显示）") && HasText(host, "低价值"), "真实搜索有明确分组标题");
                Check(HasText(host, "电脑用的 ·") && HasText(host, "用户用的 ·"), "每行显示类别说明");
                Check(SearchResultList.ResolveClickAction(1) == SearchResultList.SearchResultClickAction.Select
                    && SearchResultList.ResolveClickAction(2) == SearchResultList.SearchResultClickAction.Open
                    && SearchResultList.ResolveClickAction(3) == SearchResultList.SearchResultClickAction.Select,
                    "搜索结果单击只选择，只有双击启动");
                Border focusedSearchRow = null;
                foreach (UIElement child in host.Children)
                {
                    Border row = child as Border;
                    if (row != null && row.Tag is SearchResult) { focusedSearchRow = row; break; }
                }
                Window searchWindow = new Window { Width = 760, Height = 450, Content = host, ShowInTaskbar = false };
                searchWindow.Show(); searchWindow.Activate(); focusedSearchRow.Focus(); Pump();
                focusedSearchRow.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
                Check(focusedSearchRow.IsKeyboardFocused && ReferenceEquals(focusedSearchRow.Background, Ui.Hover),
                    "搜索结果鼠标离开后保留键盘焦点视觉");
                host.Measure(new Size(760, 450)); host.Arrange(new Rect(0, 0, 760, host.DesiredSize.Height)); host.UpdateLayout();
                RenderTargetBitmap searchBitmap = new RenderTargetBitmap(760, (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                searchBitmap.Render(host); PngBitmapEncoder searchEncoder = new PngBitmapEncoder(); searchEncoder.Frames.Add(BitmapFrame.Create(searchBitmap));
                using (FileStream stream = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app-classification-search.png"))) searchEncoder.Save(stream);
                searchWindow.Close();
                AppItem sharedUser = new AppItem { Name = "Python 用户界面", Path = apps[1].Path };
                Check(shown[0].Name == sharedUser.Name, "精确电脑结果不能挤到用户结果前面");
                HiddenAppsPage page = new HiddenAppsPage(new Window(), panel, settings, null);
                page.OnShown();
                page.Content.Measure(new Size(520, 550)); page.Content.Arrange(new Rect(0, 0, 520, 550)); page.Content.UpdateLayout();
                Check(!HasText(page.Content, "电脑用的（搜索可见）") && HasText(page.Content, "手动隐藏不参与搜索"), "隐藏管理不混入电脑类");
                Check(panel.HiddenSnapshot().Find(a => a.Path == apps[0].Path) == null, "电脑类不属于手动隐藏名单");
                Window hiddenWindow = new Window { Content = page.Content, Width = 520, Height = 550, ShowInTaskbar = false };
                hiddenWindow.Show(); hiddenWindow.Activate(); page.OnShown(); Pump();
                TextBox hiddenSearch = (TextBox)typeof(HiddenAppsPage)
                    .GetField("searchBox", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(page);
                hiddenSearch.Text = "Python 手动隐藏"; Pump();
                typeof(HiddenAppsPage).GetMethod("MoveSelection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(page, new object[] { 1 });
                hiddenSearch.Focus();
                KeyEventArgs hiddenEnter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(hiddenWindow), 0, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                page.Content.RaiseEvent(hiddenEnter);
                Check(!hiddenEnter.Handled && HiddenApps.Load(root).IsHidden(apps[5].Path) && hiddenSearch.IsKeyboardFocused,
                    "隐藏管理搜索框回车不恢复已选应用");
                ScrollViewer hiddenScroll = (ScrollViewer)typeof(HiddenAppsPage)
                    .GetField("scroll", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(page);
                hiddenScroll.Focus();
                KeyEventArgs listEnter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(hiddenWindow), 0, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                page.Content.RaiseEvent(listEnter);
                Check(listEnter.Handled && !HiddenApps.Load(root).IsHidden(apps[5].Path),
                    "隐藏管理列表的Enter仍可恢复用户选中项");
                hiddenWindow.Close();
                panel.TestClassification(apps[0].Path, AppRole.User);
                Check(Tile(panel, apps[0].Path) != null, "电脑类仍通过分类操作改为用户类");
                panel.RefreshCatalogQuiet(); Wait(panel);
                Check(Tile(panel, apps[0].Path) != null, "重新扫描保留分类选择");
                RenderTargetBitmap bitmap = new RenderTargetBitmap((int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(panel); PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream stream = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app-classification-panel.png"))) encoder.Save(stream);
                SystemSearch.SetIndexBuilderForTest(null);
            }
            catch (Exception ex) { Check(false, "异常：" + ex); }
            finally { if (panel != null) panel.Close(); application.Shutdown(); try { Directory.Delete(root, true); } catch { } }
            Console.WriteLine("通过 " + passed + " / 失败 " + failed); return failed == 0 ? 0 : 1;
        }
    }
}
