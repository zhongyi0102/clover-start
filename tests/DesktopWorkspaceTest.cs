using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clover
{
    static class DesktopWorkspaceTest
    {
        static int passed, failed;
        static void Check(bool ok, string name) { if (ok) passed++; else failed++; Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name); }
        static T Get<T>(object obj, string field) { return (T)obj.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj); }
        static void Pump() { Dispatcher.CurrentDispatcher.Invoke(new Action(delegate { }), DispatcherPriority.ApplicationIdle); }
        [STAThread]
        static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "CloverWorkspace-" + Guid.NewGuid().ToString("N"));
            string desktop = Path.Combine(root, "desktop"), storage = Path.Combine(root, "storage");
            Directory.CreateDirectory(desktop); Directory.CreateDirectory(storage);
            Application application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            PanelWindow panel = null;
            try
            {
                Ui.ApplyTheme(Ui.ThemeMode.Light);
                Settings settings = Settings.Load(root);
                Check(!settings.DesktopKeepEmptyEnabled, "自动保持桌面空默认关闭");
                settings.DesktopCleanEnabled = true; settings.Save();
                Check(!Settings.Load(root).DesktopKeepEmptyEnabled, "旧清理授权不会自动开启新的文档移动");
                string program = Path.Combine(root, "WINWORD.EXE"); File.WriteAllText(program, "program");
                string doc = Path.Combine(root, "报告.docx"); File.WriteAllText(doc, "document");
                string documentLink = Path.Combine(desktop, "报告.lnk");
                Link(documentLink, program, "/n \"" + doc + "\"");
                string appLink = Path.Combine(desktop, "Word.lnk"); Link(appLink, program, "");
                string alternate = Path.Combine(desktop, "另一入口.lnk"); Link(alternate, program, "--mode alternate");
                Check(DesktopContentRules.Classify(documentLink) == DesktopContentKind.NonApplication, "Word加文档参数是文档入口，不是应用");
                Check(DesktopContentRules.HasDocumentArgument("\"E:\\已失效文档.xlsx\""), "失效文档参数也不会误删为应用");
                Check(DesktopContentRules.ApplicationIdentity(appLink) != DesktopContentRules.ApplicationIdentity(alternate), "相同程序不同参数不能误认为同一入口");
                Check(DesktopContentRules.ApplicationIdentity(documentLink) == null, "文档入口不参加应用身份匹配");
                AppWatcher watcher = new AppWatcher(root, null, settings);
                Check(!watcher.IsPanelApplication(appLink), "不在面板的快捷方式不能清理");
                Catalog.RegisterManual(root, new AppItem { Name = "Word", Path = appLink, Category = "其他" });
                Check(watcher.IsPanelApplication(appLink) && !watcher.IsPanelApplication(alternate), "面板匹配目标和参数而非名字");
                string knownIdentity = DesktopContentRules.ApplicationIdentity(appLink);
                string note = Path.Combine(desktop, "note.txt"); File.WriteAllText(note, "note");
                string temp = Path.Combine(desktop, "~$draft.docx"); File.WriteAllText(temp, "temp");
                string unknown = Path.Combine(desktop, "broken.lnk"); File.WriteAllText(unknown, "broken");
                string outside = Path.Combine(root, "outside.txt"); File.WriteAllText(outside, "outside");
                int recycleCalls = 0;
                DesktopWorkspace workspace = new DesktopWorkspace(settings, delegate { return storage; },
                    p => DesktopContentRules.ApplicationIdentity(p) == knownIdentity,
                    p => { recycleCalls++; return true; }, new List<string> { desktop });
                DeskCollector.ScanResult preview = workspace.Preview();
                Check(preview.Items.Exists(c => c.Path == appLink && c.ApplicationShortcut), "确认列表明确区分回收应用快捷方式");
                Check(!preview.Items.Exists(c => c.Path == alternate || c.Path == temp || c.Path == unknown), "未知、未匹配和Office临时项目不进入处理列表");
                string locked = Path.Combine(desktop, "locked.docx"); File.WriteAllText(locked, "locked");
                using (FileStream stream = File.Open(locked, FileMode.Open, FileAccess.Write, FileShare.Read))
                {
                    DesktopWorkspace.Result lockedResult = workspace.Execute(new List<string> { locked }, storage);
                    Check(lockedResult.Files.MovedCount == 0 && File.Exists(locked), "其他程序占用写入的文档保留原处");
                }
                DesktopWorkspace.Result result = workspace.Execute(new List<string> { documentLink, note, appLink, alternate, temp, unknown, outside, program }, storage);
                Check(result.Files.MovedCount == 2 && result.Recycled.Count == 1 && recycleCalls == 1, "文档与应用分别计数，只回收确认的快捷方式");
                Check(File.Exists(doc) && File.Exists(program) && File.Exists(outside), "不移动原文档、不删除软件本体、不碰桌面以外项目");
                Check(!File.Exists(documentLink) && DesktopContentRules.ShortcutProperty(Path.Combine(storage, "报告.lnk"), "Arguments").Contains(doc), "只移动文档快捷方式并保留参数");
                Check(File.Exists(temp) && File.Exists(alternate) && File.Exists(unknown), "保留项目仍在原处");
                Check(settings.DesktopMoves.Count == 2 && Settings.Load(root).DesktopMoves.Count == 2, "移动记录独立持久化");
                File.WriteAllText(note, "existing");
                DeskCollector.MoveResult back = workspace.PutBack(new List<string> { Path.Combine(storage, "note.txt") }, desktop);
                Check(back.MovedCount == 1 && back.Moved[0].To != note && File.ReadAllText(note) == "existing", "放回同名不覆盖");
                Check(DesktopWorkspace.IsKept(settings, back.Moved[0].To) && DesktopWorkspace.IsKept(Settings.Load(root), back.Moved[0].To), "放回前登记例外且重启保留");
                Check(!workspace.Preview().Items.Exists(c => c.Path == back.Moved[0].To), "放回项目不会立即再次收纳");
                workspace.Execute(new List<string> { back.Moved[0].To }, storage);
                Check(File.Exists(back.Moved[0].To), "显式处理也尊重例外");
                string failBack = Path.Combine(storage, "failed-back.txt"); File.WriteAllText(failBack, "locked");
                string goodBack = Path.Combine(storage, "good-back.txt"); File.WriteAllText(goodBack, "good");
                using (FileStream stream = File.Open(failBack, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    DeskCollector.MoveResult mixed = workspace.PutBack(new List<string> { failBack, null, goodBack, goodBack }, desktop);
                    Check(mixed.MovedCount == 1 && mixed.Failed.Count == 2 && File.Exists(failBack), "放回逐项报错，后续成功项继续且重复项只处理一次");
                    Check(!DesktopWorkspace.IsKept(settings, Path.Combine(desktop, "failed-back.txt")), "放回失败撤销本次新增例外");
                    DesktopWorkspace.Keep(settings, Path.Combine(desktop, "failed-back.txt"));
                    workspace.PutBack(new List<string> { failBack }, desktop);
                    Check(DesktopWorkspace.IsKept(settings, Path.Combine(desktop, "failed-back.txt")), "放回失败不撤销已有用户例外");
                }
                Check(workspace.PutBack(null, desktop).MovedCount == 0, "空放回批次不崩溃");
                FieldInfo busyField = typeof(DesktopWorkspace).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic);
                busyField.SetValue(workspace, 1);
                Check(workspace.PutBack(new List<string> { failBack }, desktop).Failed.Count == 1
                    && File.Exists(failBack), "放回与自动收纳共享互斥，不并发移动");
                busyField.SetValue(workspace, 0);
                workspace.Dispose();

                string autoDesktop = Path.Combine(root, "auto-desktop"); Directory.CreateDirectory(autoDesktop);
                string autoFile = Path.Combine(autoDesktop, "stable.txt"); File.WriteAllText(autoFile, "stable");
                File.SetLastWriteTimeUtc(autoFile, DateTime.UtcNow.AddSeconds(-10));
                DesktopWorkspace automatic = new DesktopWorkspace(settings, delegate { return storage; }, p => false,
                    p => false, new List<string> { autoDesktop }, autoDesktop);
                MethodInfo tick = typeof(DesktopWorkspace).GetMethod("AutoTick", BindingFlags.Instance | BindingFlags.NonPublic);
                settings.DesktopKeepEmptyEnabled = false; tick.Invoke(automatic, null);
                Check(File.Exists(autoFile), "自动开关关闭时不处理文件");
                settings.DesktopKeepEmptyEnabled = true; tick.Invoke(automatic, null);
                Check(File.Exists(autoFile), "第一次观察稳定文件不会立即搬动");
                tick.Invoke(automatic, null);
                Check(!File.Exists(autoFile) && File.Exists(Path.Combine(storage, "stable.txt")), "两次稳定观察后自动收纳到统一目录");
                string writing = Path.Combine(autoDesktop, "writing.txt"); File.WriteAllText(writing, "one");
                File.SetLastWriteTimeUtc(writing, DateTime.UtcNow.AddSeconds(-10)); tick.Invoke(automatic, null);
                File.AppendAllText(writing, "changed"); File.SetLastWriteTimeUtc(writing, DateTime.UtcNow.AddSeconds(-10)); tick.Invoke(automatic, null);
                Check(File.Exists(writing), "仍在变化的文件不会被收走");
                automatic.Stop(); tick.Invoke(automatic, null);
                Check(File.Exists(writing), "停用后迟到的自动回调不能继续移动");
                settings.DesktopKeepEmptyEnabled = false; automatic.Dispose();

                panel = new PanelWindow(root, null, settings);
                panel.ShowPanel(); panel.TestSwitchCategory("");
                Pump();
                WrapPanel nav = Get<WrapPanel>(panel, "navStack");
                bool plus = false; Button more = null;
                foreach (UIElement element in nav.Children)
                {
                    Button button = element as Button;
                    if (button == null) continue;
                    if (Convert.ToString(button.ToolTip) == "新建桌面") plus = true;
                    if (Convert.ToString(button.Tag) == "apps-more") more = button;
                }
                Check(!plus && more == null, "顶部没有新建收纳页和重复的应用更多入口");
                Button rescan = Get<Button>(panel, "rescanAppsButton");
                TextBlock footerCount = Get<TextBlock>(panel, "footerCount");
                Check(rescan != null && rescan.Visibility == Visibility.Visible && rescan.Focusable
                    && AutomationProperties.GetName(rescan) == "重新扫描应用"
                    && rescan.ContextMenu == null, "重新扫描是可键盘操作的直接按钮，无隐藏重复菜单");
                Check(rescan != null && ReferenceEquals(rescan.Parent, footerCount.Parent)
                    && Grid.GetColumn(rescan) < Grid.GetColumn(footerCount), "扫描按钮位于右下角统计前");
                Check(NavNamesOnly(panel), "常用和全部只显示名称，不显示数量");
                Check(Get<List<TextBlock>>(panel, "navNames")[0].Margin.Left > 0
                    && Get<List<TextBlock>>(panel, "navNames")[1].Margin.Right > 0, "去掉数字后导航名称仍有清晰间距");
                for (int i = 0; i < 60 && panel.TestDeferredRenderPending(); i++) { Pump(); System.Threading.Thread.Sleep(10); }
                List<Border> originalTiles = new List<Border>(Get<List<Border>>(panel, "renderedTiles"));
                WrapPanel originalNav = Get<WrapPanel>(panel, "navStack");
                panel.RefreshCatalogQuiet(); Pump();
                List<Border> refreshedTiles = Get<List<Border>>(panel, "renderedTiles");
                Check(originalTiles.Count > 0 && refreshedTiles.Count > 0 && ReferenceEquals(originalTiles[0], refreshedTiles[0])
                    && ReferenceEquals(originalNav, Get<WrapPanel>(panel, "navStack")), "重扫目录局部更新复用应用磁贴与导航");
                Check(NavNamesOnly(panel), "重扫后导航数字不会重新出现");
                Catalog panelCatalog = Get<Catalog>(panel, "catalog");
                AppItem hiddenFixture = new AppItem { Name = "已隐藏测试应用", Path = Path.Combine(root, "hidden.exe"), Hidden = true };
                AppItem lowFixture = new AppItem { Name = "低价值测试应用", Path = Path.Combine(root, "low.exe"), Role = AppRole.LowValue, LowValue = true };
                panelCatalog.Apps.Add(hiddenFixture); panelCatalog.Apps.Add(lowFixture);
                MethodInfo refreshFooter = typeof(PanelWindow).GetMethod("RefreshFooterCount", BindingFlags.Instance | BindingFlags.NonPublic);
                refreshFooter.Invoke(panel, new object[] { false });
                Check(HasFooterLink(footerCount, "已隐藏") && HasFooterLink(footerCount, "低价值"), "隐藏和低价值仍由右下角数量进入");
                Hyperlink footerLink = null;
                foreach (Inline inline in footerCount.Inlines) { footerLink = inline as Hyperlink; if (footerLink != null) break; }
                panel.Activate(); Keyboard.Focus(footerLink); Pump();
                KeyEventArgs linkEnter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Enter);
                linkEnter.RoutedEvent = Keyboard.PreviewKeyDownEvent;
                panel.RaiseEvent(linkEnter);
                Check(ReferenceEquals(Keyboard.FocusedElement, footerLink) && !linkEnter.Handled, "底部链接的回车不会被应用启动快捷键抢走");
                FieldInfo currentField = typeof(PanelWindow).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic);
                currentField.SetValue(panel, "常用"); refreshFooter.Invoke(panel, new object[] { false });
                Check(HasFooterLink(footerCount, "已隐藏") && HasFooterLink(footerCount, "低价值"), "常用页也保留两类底部管理入口");
                currentField.SetValue(panel, ""); refreshFooter.Invoke(panel, new object[] { false });
                panel.Width = 760; panel.Height = 570; panel.UpdateLayout(); Pump();
                SaveScreenshot(panel, "application-footer-panel.png");
                panel.Width = 640; panel.UpdateLayout(); Pump();
                Check(FooterFits(rescan, footerCount), "640像素窄窗口扫描与统计完整显示且不重叠");
                footerCount.Text = "共 110 个应用，已隐藏 60 个，27 个低价值";
                panel.UpdateLayout(); Pump();
                Check(FooterFits(rescan, footerCount), "实际量级的三组统计在窄窗口下也完整显示");
                refreshFooter.Invoke(panel, new object[] { false }); panel.UpdateLayout(); Pump();
                TextBlock footerHint = Get<TextBlock>(panel, "footerHint");
                Check(footerHint.TextTrimming == TextTrimming.CharacterEllipsis && footerHint.ToolTip != null,
                    "底部提示可省略且悬停可读全文");
                SaveScreenshot(panel, "application-footer-narrow.png");
                Ui.ApplyTheme(Ui.ThemeMode.Dark); panel.UpdateLayout(); Pump();
                SaveScreenshot(panel, "application-footer-dark.png");
                Ui.ApplyTheme(Ui.ThemeMode.Light);
                panelCatalog.Apps.Remove(hiddenFixture); panelCatalog.Apps.Remove(lowFixture);
                panel.TestSwitchCategory(PanelWindow.FilesCategory); Pump();
                Check(rescan.Visibility == Visibility.Collapsed, "文件库不显示应用扫描按钮");
                panel.TestSwitchCategory(PanelWindow.VirtualDesktopCategory); Pump();
                Check(rescan.Visibility == Visibility.Collapsed, "桌面收纳不显示应用扫描按钮");
                WrapPanel toolbar = Get<WrapPanel>(panel, "deskToolRow");
                VirtualDesks panelDesks = Get<VirtualDesks>(panel, "virtualDesks");
                string panelDeskFolder = panelDesks.FolderOf(PanelWindow.VirtualDesktopCategory);
                File.WriteAllText(Path.Combine(panelDeskFolder, "空白取消选择.txt"), "fixture");
                DesktopView panelDeskView = Get<DesktopView>(panel, "deskView");
                panelDeskView.Load(PanelWindow.VirtualDesktopCategory);
                Check(footerCount.Text == "1 个文件" && footerCount.ToolTip == null,
                    "文件库切到桌面收纳后显示当前内容数量，清理旧状态和悬停说明");
                panelDeskView.ApplyFilter("不存在的内容");
                Check(footerCount.Text == "找到 0 / 1 项", "桌面收纳筛选同步当前页统计");
                panelDeskView.ApplyFilter("");
                panel.TestDeskSelectAll();
                Check(panelDeskView.SelectedPaths().Count > 0, "桌面收纳项目可进入选中状态");
                Canvas deskCanvas = Get<Canvas>(panel, "deskCanvas");
                panel.Activate(); deskCanvas.Focus();
                KeyEventArgs deskMenuKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Apps)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                panel.RaiseEvent(deskMenuKey);
                ContextMenu deskMenu = Get<ContextMenu>(panelDeskView, "activeItemMenu");
                Check(deskMenuKey.Handled && deskMenu != null && deskMenu.IsOpen
                    && deskMenu.Placement == System.Windows.Controls.Primitives.PlacementMode.Bottom
                    && deskMenu.PlacementTarget == panelDeskView.TestTile("空白取消选择.txt"),
                    "桌面收纳菜单键贴近选中图标");
                deskMenu.IsOpen = false; Pump();
                Check(deskCanvas.IsKeyboardFocused && panelDeskView.SelectedPaths().Count == 1,
                    "桌面键盘菜单关闭保留选择并回到画布");
                Check(panelDeskView.HandleKey(Key.F10, ModifierKeys.Shift)
                    && Get<ContextMenu>(panelDeskView, "activeItemMenu").IsOpen,
                    "桌面收纳Shift+F10打开已有菜单");
                Get<ContextMenu>(panelDeskView, "activeItemMenu").IsOpen = false; Pump();
                toolbar.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent
                });
                Check(panelDeskView.SelectedPaths().Count == 0,
                    "桌面收纳点击工具栏空白处取消当前选择");
                int buttons = 0; Button deskMore = null;
                foreach (UIElement element in toolbar.Children) { Button button = element as Button; if (button != null) { buttons++; deskMore = button; } }
                Check(buttons == 2 && Get<Button>(panel, "deskBackButton") == null, "顶部仅收纳和更多，不再突出放回");
                Check(deskMore != null && ((MenuItem)deskMore.ContextMenu.Items[0]).Header.ToString() == "桌面设置…"
                    && ((MenuItem)deskMore.ContextMenu.Items[2]).Header.ToString() == "全部放回桌面…", "桌面设置归收纳且全部放回放在更多底部");
                OrganizeDialog hidden = new OrganizeDialog(panel, root, settings, null, panel, null, OrganizeDialog.Tab.Hidden);
                Check(hidden.TestWindow.Title == "归位 · 已隐藏的应用", "隐藏管理独立标题");
                hidden.TestWindow.Close();
                OrganizeDialog configure = new OrganizeDialog(panel, root, settings, null, panel, null, OrganizeDialog.Tab.Desktop);
                Check(configure.TestWindow.Title == "归位 · 桌面设置" && configure.TestPageHasContent(), "统一桌面设置可正常构建");
                configure.TestWindow.Show(); configure.TestWindow.UpdateLayout(); Pump();
                SaveScreenshot(configure.TestWindow, "desktop-workspace-settings.png");
                configure.TestWindow.Close();
                panel.ShowPanel(); panel.TestSwitchCategory(PanelWindow.VirtualDesktopCategory);
                panel.Width = 760; panel.Height = 570; panel.UpdateLayout(); Pump();
                FrameworkElement panelContent = (FrameworkElement)panel.Content;
                RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Ceiling(panelContent.ActualWidth),
                    (int)Math.Ceiling(panelContent.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(panelContent); PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream file = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "desktop-workspace-panel.png"))) encoder.Save(file);
                Check(HasVisiblePixels(bitmap), "收纳界面截图包含真实界面内容，不是隐藏窗口黑图");
            }
            catch (Exception ex) { Check(false, "异常：" + ex); }
            finally
            {
                if (panel != null) panel.Close(); application.Shutdown();
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine("通过 " + passed + " 项，失败 " + failed + " 项");
            return failed == 0 ? 0 : 1;
        }
        static void SaveScreenshot(Window window, string name)
        {
            RenderTargetBitmap bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window); PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream file = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name))) encoder.Save(file);
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
        static bool NavNamesOnly(PanelWindow panel)
        {
            List<TextBlock> counts = Get<List<TextBlock>>(panel, "navCounts");
            foreach (TextBlock count in counts)
                if (count.Visibility != Visibility.Collapsed || count.Text.Length != 0) return false;
            List<TextBlock> names = Get<List<TextBlock>>(panel, "navNames");
            return names.Count >= 2 && names[0].Text == "常用" && names[1].Text == "全部";
        }
        static bool HasFooterLink(TextBlock footer, string label)
        {
            foreach (Inline inline in footer.Inlines)
            {
                Hyperlink link = inline as Hyperlink;
                if (link == null || !link.IsEnabled || !link.Focusable) continue;
                foreach (Inline content in link.Inlines)
                {
                    Run run = content as Run;
                    if (run != null && run.Text.Contains(label)) return true;
                }
            }
            return false;
        }
        static bool FooterFits(Button rescan, TextBlock count)
        {
            Grid footer = rescan.Parent as Grid;
            if (footer == null || rescan.ActualWidth <= 0 || count.ActualWidth <= 0) return false;
            Point scanAt = rescan.TranslatePoint(new Point(), footer);
            Point countAt = count.TranslatePoint(new Point(), footer);
            return scanAt.X >= 0 && scanAt.X + rescan.ActualWidth <= countAt.X
                && countAt.X + count.ActualWidth <= footer.ActualWidth + 0.5;
        }
        static void Link(string path, string target, string args)
        {
            object shell = null, link = null;
            try
            {
                Type type = Type.GetTypeFromProgID("WScript.Shell"); shell = Activator.CreateInstance(type);
                link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                link.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                link.GetType().InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { args });
                link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally { if (link != null) Marshal.ReleaseComObject(link); if (shell != null) Marshal.ReleaseComObject(shell); }
        }
    }
}
