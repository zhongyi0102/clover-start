using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clover
{
    static class ProjectContractTest
    {
        static int passed, failed;
        static readonly JavaScriptSerializer json = new JavaScriptSerializer();
        static void Check(bool ok, string name)
        { if (ok) passed++; else failed++; Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name); }

        [STAThread]
        static int Main()
        {
            string repository = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
            string sandbox = Path.Combine(Path.GetTempPath(), "CloverContract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            Window window = null;
            try
            {
                Check(AppBrand.Name == "归位" && AppBrand.Caption("设置") == "归位 · 设置"
                    && AppBrand.Text("四叶草面板") == "归位面板", "公共品牌与兼容文案统一");
                Check(Autostart.TaskName == "CloverStart", "旧自启任务身份保持兼容");
                string programSource = File.ReadAllText(Path.Combine(repository, "src", "ui", "Program.cs"));
                Check(programSource.Contains("CloverStart_SingleInstance_7f3a9c"), "单实例身份不因品牌变化失效");
                byte[] icon = File.ReadAllBytes(Path.Combine(repository, "res", "brand.ico"));
                Check(BitConverter.ToUInt16(icon, 2) == 1 && BitConverter.ToUInt16(icon, 4) == 8, "ICO包含八个原生尺寸帧");
                bool framesValid = true;
                for (int i = 0; i < 8; i++)
                {
                    int offset = (int)BitConverter.ToUInt32(icon, 6 + i * 16 + 12);
                    int length = (int)BitConverter.ToUInt32(icon, 6 + i * 16 + 8);
                    using (MemoryStream stream = new MemoryStream(icon, offset, length))
                    {
                        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        int size = icon[6 + i * 16] == 0 ? 256 : icon[6 + i * 16];
                        framesValid &= decoder.Frames[0].PixelWidth == size && decoder.Frames[0].PixelHeight == size;
                    }
                }
                Check(framesValid, "所有品牌ICO帧可实际解码且尺寸一致");
                Assembly executable = Assembly.LoadFile(Path.Combine(repository, "app", "CloverStart.exe"));
                Check(Array.IndexOf(executable.GetManifestResourceNames(), "CloverStart.brand.png") >= 0
                    && Array.IndexOf(executable.GetManifestResourceNames(), "CloverStart.clover.png") >= 0, "新品牌嵌入且保留旧资源兼容");

                Ui.ApplyTheme(Ui.ThemeMode.Light);
                Button button = new Button { Content = "操作", Padding = new Thickness(12, 6, 12, 6),
                    Background = Ui.SearchBg, BorderBrush = Ui.Line, BorderThickness = new Thickness(1),
                    HorizontalContentAlignment = HorizontalAlignment.Center, Template = UiButtons.Template(Ui.R.Sm) };
                window = new Window { Content = button, Width = 240, Height = 100, ShowInTaskbar = false };
                window.Show(); window.UpdateLayout(); button.ApplyTemplate();
                Border chrome = (Border)button.Template.FindName("ButtonChrome", button);
                Border focusRing = (Border)button.Template.FindName("FocusRing", button);
                Check(chrome != null && chrome.Padding == button.Padding, "共享按钮模板真实绑定Padding");
                Check(ReferenceEquals(button.Template, UiButtons.Template(Ui.R.Sm))
                    && ReferenceEquals(button.Template, PanelWindow.MakeButtonTemplate(Ui.R.Sm)), "各页面复用同一模板而非重复构造");
                Check(button.Template.Triggers.Count == 4, "按钮覆盖悬停、键盘焦点、按下与禁用状态");
                button.IsEnabled = false; window.UpdateLayout();
                Check(chrome.Opacity == 0.45, "禁用状态有实际可见反馈");
                button.IsEnabled = true;
                Keyboard.ClearFocus(); window.UpdateLayout();
                Size sizeBeforeFocus = button.RenderSize;
                Rect contentBeforeFocus = ((Visual)VisualTreeHelper.GetChild(chrome, 0)).TransformToAncestor(button)
                    .TransformBounds(new Rect(((UIElement)VisualTreeHelper.GetChild(chrome, 0)).RenderSize));
                window.Activate(); button.Focus(); window.UpdateLayout();
                Check(button.IsKeyboardFocused && focusRing.Visibility == Visibility.Visible
                    && focusRing.BorderThickness == new Thickness(2) && !focusRing.IsHitTestVisible,
                    "原生键盘焦点描边实际生效且不拦截点击");
                Rect contentAfterFocus = ((Visual)VisualTreeHelper.GetChild(chrome, 0)).TransformToAncestor(button)
                    .TransformBounds(new Rect(((UIElement)VisualTreeHelper.GetChild(chrome, 0)).RenderSize));
                Check(chrome.BorderThickness == button.BorderThickness && button.RenderSize == sizeBeforeFocus
                    && contentBeforeFocus == contentAfterFocus, "聚焦不改变普通边框、按钮尺寸或内容位置");
                Color oldAccent = Ui.Accent.Color;
                Ui.Accent.Color = Color.FromRgb(130, 60, 80); window.UpdateLayout();
                Check(((SolidColorBrush)chrome.BorderBrush).Color == Ui.Accent.Color
                    && ((SolidColorBrush)focusRing.BorderBrush).Color == Ui.Accent.Color && !Ui.Accent.IsFrozen,
                    "缓存模板不会冻结主题刷子，焦点随主题更新");
                Ui.Accent.Color = oldAccent;
                Button compact = new Button { Content = "来源", Padding = new Thickness(12, 0, 12, 0),
                    Height = 28, BorderThickness = new Thickness(1), Template = UiButtons.Template(14) };
                Button neighbor = new Button { Content = "另一来源", Template = UiButtons.Template(14) };
                WrapPanel row = new WrapPanel(); row.Children.Add(compact); row.Children.Add(neighbor);
                window.Content = row; window.UpdateLayout();
                Size compactSize = compact.RenderSize;
                Point neighborAt = neighbor.TranslatePoint(new Point(), row);
                compact.Focus(); window.UpdateLayout();
                Check(compact.IsKeyboardFocused && compact.RenderSize == compactSize
                    && neighbor.TranslatePoint(new Point(), row) == neighborAt,
                    "自动宽度按钮聚焦不挤动相邻按钮");
                Check(Ui.IsContextMenuKey(Key.Apps, ModifierKeys.None)
                    && Ui.IsContextMenuKey(Key.F10, ModifierKeys.Shift)
                    && !Ui.IsContextMenuKey(Key.F10, ModifierKeys.None)
                    && !Ui.IsContextMenuKey(Key.F10, ModifierKeys.Control | ModifierKeys.Shift),
                    "菜单键与Shift+F10统一识别，不抢普通F10或其他组合键");
                KeyEventArgs systemKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.F10);
                typeof(KeyEventArgs).GetMethod("MarkSystem", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(systemKey, null);
                Check(systemKey.Key == Key.System && Ui.EffectiveKey(systemKey) == Key.F10,
                    "系统键路由仍识别实际F10");
                TestConfirmFocus(window, true, "取消");
                TestConfirmFocus(window, false, "继续");
                WallpaperPickerDialog picker = new WallpaperPickerDialog(window, sandbox, null, null, null);
                Button card = (Button)typeof(WallpaperPickerDialog).GetMethod("MakeCard", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(picker, new object[] { new WallpaperEngine.Item { Title = "测试壁纸", Dir = sandbox } });
                window.Content = card; window.UpdateLayout(); card.ApplyTemplate();
                Check(card.Focusable && ReferenceEquals(card.Template, UiButtons.Template(Ui.R.Sm))
                    && System.Windows.Automation.AutomationProperties.GetName(card) == "选择壁纸：测试壁纸",
                    "壁纸卡片是带辅助名称的原生按钮，复用项目交互模板");
                window.Activate(); card.Focus(); window.UpdateLayout();
                Check(card.IsKeyboardFocused && ((Border)card.Template.FindName("FocusRing", card)).Visibility == Visibility.Visible,
                    "壁纸卡片键盘焦点可见");
                int cardClicks = 0; card.Click += delegate { cardClicks++; };
                card.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
                    { RoutedEvent = Keyboard.KeyDownEvent });
                Check(cardClicks == 1, "壁纸卡片按Enter执行已有选择动作");
                card.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Space)
                    { RoutedEvent = Keyboard.KeyDownEvent });
                card.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Space)
                    { RoutedEvent = Keyboard.KeyUpEvent });
                Check(cardClicks == 2, "壁纸卡片按空格也执行已有选择动作");
                window.Close(); window = null;
                TestSettingsLayout(sandbox);

                string tiles = File.ReadAllText(Path.Combine(repository, "src", "ui", "PanelWindow.Tiles.cs"));
                Check(tiles.Contains("Header = \"应用分类\"") && !tiles.Contains("Header = \"标记为低价值\"")
                    && !tiles.Contains("Header = \"取消低价值标记\""), "低价值修改只保留统一分类入口");
                Check(File.Exists(Path.Combine(repository, "docs", "PRODUCT-RULES.md"))
                    && File.Exists(Path.Combine(repository, "docs", "UPGRADE.md")), "当前规则与用户数据安全更新文档存在");
                TestUpdates(repository, sandbox);
            }
            catch (Exception ex) { Check(false, "异常：" + ex); }
            finally
            {
                if (window != null) window.Close();
                try { Directory.Delete(sandbox, true); } catch { }
            }
            Console.WriteLine("通过 " + passed + " 项，失败 " + failed + " 项");
            return failed == 0 ? 0 : 1;
        }

        static void TestConfirmFocus(Window owner, bool destructive, string expected)
        {
            bool observed = false;
            string actual = "";
            var timer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(60) };
            timer.Tick += delegate
            {
                foreach (Window dialog in owner.OwnedWindows)
                {
                    if (dialog.Title != "确认焦点测试" || !dialog.IsVisible) continue;
                    Button focused = Keyboard.FocusedElement as Button;
                    actual = focused == null ? "" : System.Windows.Automation.AutomationProperties.GetName(focused);
                    observed = true; timer.Stop();
                    if (destructive && focused != null)
                        focused.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), 0, Key.Enter)
                            { RoutedEvent = Keyboard.KeyDownEvent });
                    if (dialog.IsVisible) dialog.DialogResult = false;
                    break;
                }
            };
            timer.Start();
            bool accepted;
            try { accepted = TextPromptDialog.Confirm(owner, "确认焦点测试", "只检查焦点，不执行任何操作", "继续", destructive); }
            finally { timer.Stop(); }
            Check(observed && actual == expected && !accepted,
                (destructive ? "危险确认默认聚焦取消" : "普通确认保持执行按钮焦点") + "，关闭不执行");
        }

        static void Pump()
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(new Action(delegate { }),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        static List<T> Visuals<T>(DependencyObject root) where T : DependencyObject
        {
            List<T> result = new List<T>();
            if (root is T) result.Add((T)root);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                result.AddRange(Visuals<T>(VisualTreeHelper.GetChild(root, i)));
            return result;
        }

        static T Named<T>(DependencyObject root, string name) where T : DependencyObject
        {
            foreach (T element in Visuals<T>(root))
                if (System.Windows.Automation.AutomationProperties.GetName(element) == name) return element;
            return null;
        }

        static TextBlock Text(DependencyObject root, string text)
        {
            foreach (TextBlock block in Visuals<TextBlock>(root)) if (block.Text == text) return block;
            return null;
        }

        static bool Inside(FrameworkElement element, FrameworkElement parent)
        {
            Rect bounds = element.TransformToAncestor(parent).TransformBounds(new Rect(element.RenderSize));
            return bounds.Left >= -1 && bounds.Right <= parent.ActualWidth + 1
                && bounds.Top >= -1 && bounds.Bottom <= parent.ActualHeight + 1;
        }

        static void SaveSettings(Window window, string name)
        {
            FrameworkElement visual = (FrameworkElement)window.Content;
            visual.UpdateLayout();
            Rect rendered = visual.LayoutTransform.TransformBounds(new Rect(visual.RenderSize));
            RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Ceiling(rendered.Width),
                (int)Math.Ceiling(rendered.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
            using (FileStream stream = File.Create(path)) encoder.Save(stream);
            Console.WriteLine("Screenshot: " + path);
        }

        static void Toggle(CheckBox check)
        {
            var peer = new System.Windows.Automation.Peers.CheckBoxAutomationPeer(check);
            var provider = (System.Windows.Automation.Provider.IToggleProvider)
                peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle);
            provider.Toggle(); Pump();
        }

        static void TestSettingsLayout(string sandbox)
        {
            string settingsRoot = Path.Combine(sandbox, "settings layout", new string('目', 60));
            Directory.CreateDirectory(settingsRoot);
            Settings settings = Settings.Load(settingsRoot);
            settings.FullscreenDetect = false;
            settings.HoverReveal = true;
            settings.MiniTaskbar = false;
            settings.Save();
            Window dialog = null;
            bool enabled = OverlayGate.Enabled, reveal = OverlayGate.HoverReveal;
            try
            {
                Ui.ApplyTheme(Ui.ThemeMode.Light);
                dialog = SettingsWindow.BuildForTest(null, settings, null, settingsRoot, SettingsWindow.Section.General);
                Check(dialog != null && dialog.Width == 580 && !double.IsInfinity(dialog.MaxHeight)
                    && !double.IsInfinity(dialog.MaxWidth), "设置页默认加宽并限制在显示器工作区");
                dialog.Show(); dialog.UpdateLayout(); Pump();
                ScrollViewer scroll = null;
                foreach (ScrollViewer candidate in Visuals<ScrollViewer>(dialog))
                    if (candidate.Name == "SettingsScroll") scroll = candidate;
                Check(scroll != null && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled,
                    "设置页只有内容纵向滚动");
                Check(Visuals<CheckBox>(dialog).Count == 5 && Text(dialog, "常规").FontSize == 16,
                    "五个现有开关保留，标题字号明确");
                Check(Text(dialog, "修改即时生效") != null && Named<Button>(dialog, "保存") == null,
                    "即时生效说明可见，不引入保存按钮");
                Expander details = Named<Expander>(dialog, "诊断详情");
                Check(details != null && !details.IsExpanded && Text(dialog, "任务栏定位") == null,
                    "诊断默认折叠，不堆技术坐标");
                var header = (System.Windows.Controls.Primitives.ToggleButton)details.Template.FindName("HeaderSite", details);
                header.Focus(); Pump();
                header.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), 0, Key.Space)
                    { RoutedEvent = Keyboard.KeyDownEvent });
                header.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), 0, Key.Space)
                    { RoutedEvent = Keyboard.KeyUpEvent });
                Pump();
                Check(details.IsExpanded && Text(dialog, "任务栏定位") != null
                    && ((Border)header.Template.FindName("HeaderChrome", header)).BorderBrush == Ui.Accent,
                    "诊断折叠入口Space可展开，焦点使用项目配色");
                var expanderPeer = new System.Windows.Automation.Peers.ExpanderAutomationPeer(details);
                var expansion = (System.Windows.Automation.Provider.IExpandCollapseProvider)
                    expanderPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.ExpandCollapse);
                expansion.Collapse(); Pump();
                Check(!details.IsExpanded, "诊断保留原生辅助折叠语义");
                Check(Text(dialog, "运行中 · 系统接管未启用") != null,
                    "维护默认显示简短运行状态");

                CheckBox fullscreen = Named<CheckBox>(dialog, "全屏时隐藏悬浮按钮");
                CheckBox hover = Named<CheckBox>(dialog, "鼠标移到按钮位置时临时显示");
                CheckBox mini = Named<CheckBox>(dialog, "迷你任务栏");
                Check(!hover.IsEnabled && hover.IsChecked == true
                    && System.Windows.Automation.AutomationProperties.GetHelpText(hover).Contains("需先开启"),
                    "依赖关闭时明确禁用原因且保留子项值");
                Toggle(fullscreen);
                Check(hover.IsEnabled && hover.IsChecked == true && settings.FullscreenDetect,
                    "父项启用后子项恢复可操作，不覆盖原值");
                Toggle(fullscreen);
                Check(!hover.IsEnabled && Settings.Load(settingsRoot).HoverReveal,
                    "父项关闭不清空子项或持久化状态");
                scroll.ScrollToTop(); Pump();
                dialog.Activate(); fullscreen.Focus(); dialog.UpdateLayout(); Pump();
                Border focus = (Border)fullscreen.Template.FindName("SettingFocus", fullscreen);
                Check(fullscreen.IsKeyboardFocused && focus.Visibility == Visibility.Visible
                    && focus.BorderThickness == new Thickness(2) && !focus.IsHitTestVisible,
                    "整行开关键盘焦点可见且不占布局空间");
                IInputElement rowHit = fullscreen.InputHitTest(new Point(fullscreen.ActualWidth / 2, fullscreen.ActualHeight / 2));
                DependencyObject rowElement = rowHit as DependencyObject;
                while (rowElement != null && !(rowElement is CheckBox)) rowElement = VisualTreeHelper.GetParent(rowElement);
                Check(ReferenceEquals(rowElement, fullscreen) && fullscreen.ActualHeight >= 44,
                    "文字与右侧开关之间的行空白也命中原生开关，不局限小按钮");
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), 0, Key.Space)
                    { RoutedEvent = Keyboard.KeyDownEvent };
                fullscreen.RaiseEvent(key);
                fullscreen.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), 0, Key.Space)
                    { RoutedEvent = Keyboard.KeyUpEvent });
                Pump();
                Check(fullscreen.IsChecked == true && settings.FullscreenDetect, "原生开关Space立即切换并写盘");
                Toggle(mini);
                Check(settings.MiniTaskbar && Settings.Load(settingsRoot).MiniTaskbar,
                    "整行开关保留原操作和即时持久化");
                Border track = (Border)mini.Template.FindName("ToggleTrack", mini);
                Border thumb = (Border)mini.Template.FindName("ToggleThumb", mini);
                Check(thumb.HorizontalAlignment == HorizontalAlignment.Right
                    && ((SolidColorBrush)track.Background).Color == Ui.Accent.Color,
                    "开启状态同时以位置和项目强调色表达");
                Toggle(mini); Toggle(fullscreen);
                scroll.ScrollToTop(); Pump(); SaveSettings(dialog, "settings-light.png");

                foreach (double width in new double[] { 580, 420 })
                {
                    dialog.Width = width; dialog.Height = 400; dialog.UpdateLayout(); Pump();
                    Button change = Named<Button>(dialog, "更改目录…");
                    Button open = Named<Button>(dialog, "打开目录");
                    Button reset = Named<Button>(dialog, "恢复默认");
                    WrapPanel actions = VisualTreeHelper.GetParent(change) as WrapPanel;
                    Check(actions != null && Inside(change, actions) && Inside(open, actions) && Inside(reset, actions)
                        && actions.ActualWidth <= scroll.ViewportWidth + 1, "目录按钮不溢出，宽度 " + width);
                    Button copy = Named<Button>(dialog, "复制存放目录");
                    TextBlock path = null;
                    foreach (TextBlock block in Visuals<TextBlock>(dialog))
                        if (block.Text.Contains("desks")) path = block;
                    Check(path != null && path.Text == (string)path.ToolTip
                        && path.TextTrimming == TextTrimming.CharacterEllipsis && copy.ActualHeight <= 36,
                        "长目录有完整悬停说明，复制按钮不拉高，宽度 " + width);
                    details.IsExpanded = true; dialog.UpdateLayout(); Pump();
                    Check(Text(dialog, "任务栏定位") != null && Named<Button>(dialog, "复制任务栏定位").ActualHeight <= 36,
                        "展开诊断保留详情和复制，不拉高按钮，宽度 " + width);
                    var bar = (System.Windows.Controls.Primitives.ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
                    var barTrack = (System.Windows.Controls.Primitives.Track)bar.Template.FindName("PART_Track", bar);
                    Check(barTrack.Thumb != null && barTrack.DecreaseRepeatButton.Command
                        == System.Windows.Controls.Primitives.ScrollBar.PageUpCommand && barTrack.IncreaseRepeatButton.Command
                        == System.Windows.Controls.Primitives.ScrollBar.PageDownCommand,
                        "自绘滚动条保留原生拖拽和上下翻页部件，宽度 " + width);
                    scroll.ScrollToTop(); Pump();
                    System.Windows.Controls.Primitives.ScrollBar.PageDownCommand.Execute(null, bar);
                    Pump();
                    Check(scroll.VerticalOffset > 0 && Math.Abs(bar.Value - scroll.VerticalOffset) < 1,
                        "滚动条翻页实际滚动内容，位置同步，宽度 " + width);
                    details.IsExpanded = false; scroll.ScrollToBottom(); Pump();
                    Button rollback = Named<Button>(dialog, "撤销系统改动");
                    Check(Inside(rollback, scroll) && Inside(Text(dialog, "修改即时生效"), (FrameworkElement)dialog.Content)
                        && scroll.VerticalOffset > 0, "小高度滚到底能到达系统撤销，说明常驻，宽度 " + width);
                }
                SaveSettings(dialog, "settings-narrow.png");
                dialog.Width = 580; dialog.Height = Math.Min(740, dialog.MaxHeight); scroll.ScrollToTop();
                Ui.ApplyTheme(Ui.ThemeMode.Dark); dialog.UpdateLayout(); Pump();
                Check(((SolidColorBrush)dialog.Background).Color == Ui.Bg.Color
                    && ((SolidColorBrush)thumb.Background).Color == Ui.Sub.Color,
                    "深色使用共享表面与开关刷子，无浅色残留");
                Check(!Ui.Sub.IsFrozen && !Ui.Fg.IsFrozen && !Ui.Accent.IsFrozen,
                    "所有自绘模板不冻结共享配色，主题仍可实时修改");
                SaveSettings(dialog, "settings-dark.png");
                Ui.ApplyTheme(Ui.ThemeMode.Light);
                ((FrameworkElement)dialog.Content).LayoutTransform = new ScaleTransform(1.5, 1.5);
                dialog.Width = 420; dialog.Height = 400; dialog.UpdateLayout(); Pump();
                Button scaledChange = Named<Button>(dialog, "更改目录…");
                WrapPanel scaledActions = (WrapPanel)VisualTreeHelper.GetParent(scaledChange);
                Check(Inside(scaledChange, scaledActions) && Inside(Named<Button>(dialog, "恢复默认"), scaledActions)
                    && scaledActions.ActualWidth <= scroll.ViewportWidth + 1,
                    "窄窗口150%内容缩放后目录按钮自然换行不截断");
                details.IsExpanded = true; scroll.ScrollToBottom(); Pump();
                Check(Inside(Named<Button>(dialog, "撤销系统改动"), scroll), "缩放并展开诊断后底部操作仍可达");
                SaveSettings(dialog, "settings-scaled.png");
            }
            finally
            {
                if (dialog != null) dialog.Close();
                OverlayGate.Enabled = enabled; OverlayGate.HoverReveal = reveal;
                Ui.ApplyTheme(Ui.ThemeMode.Light);
            }
        }

        static void Manifest(string source, object[] entries)
        {
            File.WriteAllText(Path.Combine(source, "release-manifest.json"), json.Serialize(new {
                format = 1, version = BuildInfo.Version, files = entries }), Encoding.UTF8);
        }
        static object Entry(string source, string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(File.ReadAllBytes(Path.Combine(source, path.Replace('/', '\\'))));
                return new { path = path, sha256 = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant() };
            }
        }
        static int Update(string repository, string source, string target, bool dry = false, bool expectSuccess = true)
        {
            ProcessStartInfo info = new ProcessStartInfo {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(repository, "tools", "update.ps1")
                    + "\" -SourcePackage \"" + source + "\" -TargetDirectory \"" + target + "\"" + (dry ? " -DryRun" : ""),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.StandardErrorEncoding = Encoding.UTF8;
            using (Process process = Process.Start(info))
            {
                StringBuilder errors = new StringBuilder();
                process.OutputDataReceived += delegate { };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) {
                    if (args.Data != null) lock (errors) errors.AppendLine(args.Data);
                };
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                if (!process.WaitForExit(20000)) { process.Kill(); throw new Exception("临时更新测试超时"); }
                process.WaitForExit();
                if (process.ExitCode != 0 && expectSuccess) Console.WriteLine("UPDATE_DIAGNOSTIC " + errors.ToString());
                return process.ExitCode;
            }
        }
        static void TestUpdates(string repository, string sandbox)
        {
            string source = Path.Combine(sandbox, "source package"), target = Path.Combine(sandbox, "old installation");
            Directory.CreateDirectory(Path.Combine(source, "app")); Directory.CreateDirectory(Path.Combine(source, "res"));
            Directory.CreateDirectory(Path.Combine(target, "app")); Directory.CreateDirectory(Path.Combine(target, "config"));
            Directory.CreateDirectory(Path.Combine(target, "desks")); Directory.CreateDirectory(Path.Combine(target, "res"));
            string targetExe = Path.Combine(target, "app", "CloverStart.exe");
            File.WriteAllText(targetExe, "old-program");
            File.Copy(Path.Combine(repository, "app", "CloverStart.exe"), Path.Combine(source, "app", "CloverStart.exe"));
            File.WriteAllText(Path.Combine(source, "res", "brand.png"), "new-brand");
            File.WriteAllText(Path.Combine(target, "config", "settings.json"), "user-settings");
            File.WriteAllText(Path.Combine(target, "desks", "report.docx"), "user-document");
            File.WriteAllText(Path.Combine(target, "res", "custom.png"), "user-wallpaper");
            object exeEntry = Entry(source, "app/CloverStart.exe"), brandEntry = Entry(source, "res/brand.png");
            Manifest(source, new[] { exeEntry, brandEntry });
            Check(Update(repository, source, target, true) == 0 && File.ReadAllText(targetExe) == "old-program"
                && !Directory.Exists(Path.Combine(target, "update-backups")), "更新预览完全只读且支持空格路径");
            Check(Update(repository, source, target) == 0 && new FileInfo(targetExe).Length == new FileInfo(Path.Combine(source, "app", "CloverStart.exe")).Length,
                "合法清单执行真实程序更新");
            string[] backups = Directory.GetDirectories(Path.Combine(target, "update-backups"));
            Check(backups.Length == 1 && File.ReadAllText(Path.Combine(backups[0], "app", "CloverStart.exe")) == "old-program", "旧程序已完整备份可恢复");
            Check(File.ReadAllText(Path.Combine(target, "config", "settings.json")) == "user-settings"
                && File.ReadAllText(Path.Combine(target, "desks", "report.docx")) == "user-document"
                && File.ReadAllText(Path.Combine(target, "res", "custom.png")) == "user-wallpaper", "更新不改变设置、收纳文档及自定义壁纸");
            Manifest(source, new object[] { exeEntry, new { path="config/settings.json", sha256=new string('0', 64) } });
            Check(Update(repository, source, target, true, false) != 0, "拒绝清单覆盖用户配置");
            Manifest(source, new object[] { exeEntry, new { path="../outside.txt", sha256=new string('0', 64) } });
            Check(Update(repository, source, target, true, false) != 0, "拒绝清单路径穿越");
            Manifest(source, new[] { exeEntry, exeEntry });
            Check(Update(repository, source, target, true, false) != 0, "拒绝重复更新路径");
            Manifest(source, new object[] { new { path="app/CloverStart.exe", sha256=new string('0', 64) } });
            Check(Update(repository, source, target, true, false) != 0, "拒绝损坏或不匹配SHA256");
            Manifest(source, new[] { exeEntry, brandEntry });
            File.WriteAllText(targetExe, "rollback-original");
            using (FileStream locked = File.Open(Path.Combine(target, "res", "brand.png"), FileMode.Open, FileAccess.Read, FileShare.Read))
                Check(Update(repository, source, target, false, false) != 0 && File.ReadAllText(targetExe) == "rollback-original",
                    "后续资源被占用导致更新失败时，已替换程序自动回退");
            Check(Update(repository, source, source, true, false) != 0, "拒绝原地把来源当目标");
        }
    }
}
