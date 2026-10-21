using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clover
{
    /// <summary>左侧分类导航、搜索过滤、应用磁贴渲染与拖拽排序。</summary>
    partial class PanelWindow
    {
        /// <summary>键盘选中的应用磁贴（null 表示没有选中）。</summary>
        Border selectedTile;
        /// <summary>键盘选中项在 renderedApps 中的下标，-1 表示没有选中。</summary>
        int selectedIndex = -1;

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

                if (index < 0 || index >= renderedApps.Count) return;
                if (index >= tilePanel.Children.Count) return;
                Border tile = tilePanel.Children[index] as Border;
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

            List<string> cats = new List<string>();
            cats.Add("常用");
            cats.Add("");
            cats.AddRange(catalog.ActiveCategories());

            foreach (string cat in cats)
            {
                string c = cat;
                int count = c == "常用" ? CommonApps().Count : catalog.ByCategory(c).Count;
                if (c != "" && c != "常用" && count == 0) continue;

                string label = c == "" ? "全部" : c;

                Border item = new Border();
                item.Height = 34;
                item.CornerRadius = new CornerRadius(8);
                item.Margin = new Thickness(0, 2, 0, 2);
                item.Background = Brushes.Transparent;
                item.Cursor = Cursors.Hand;

                Grid g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions[0].Width = new GridLength(3);
                g.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                g.ColumnDefinitions[2].Width = GridLength.Auto;

                Border bar = new Border();
                bar.Width = 3;
                bar.Height = 16;
                bar.CornerRadius = new CornerRadius(2);
                bar.Background = Ui.Accent;
                bar.HorizontalAlignment = HorizontalAlignment.Left;
                bar.Visibility = Visibility.Collapsed;
                Grid.SetColumn(bar, 0);
                g.Children.Add(bar);

                TextBlock name = new TextBlock();
                name.Text = label;
                name.FontFamily = Ui.YaHei;
                name.FontSize = 12.5;
                name.Foreground = Ui.Fg;
                name.VerticalAlignment = VerticalAlignment.Center;
                name.Margin = new Thickness(10, 0, 0, 0);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(name, 1);
                g.Children.Add(name);

                TextBlock cnt = new TextBlock();
                cnt.Text = count.ToString();
                cnt.FontFamily = Ui.YaHei;
                cnt.FontSize = 11;
                cnt.Foreground = Ui.Sub;
                cnt.VerticalAlignment = VerticalAlignment.Center;
                cnt.Margin = new Thickness(4, 0, 10, 0);
                Grid.SetColumn(cnt, 2);
                g.Children.Add(cnt);

                item.Child = g;
                item.MouseEnter += delegate { if (current != c) item.Background = Ui.Hover; };
                item.MouseLeave += delegate { if (current != c) item.Background = Brushes.Transparent; };
                item.MouseLeftButtonUp += delegate
                {
                    current = c;
                    UpdateNav();
                    RenderApps();
                };

                navStack.Children.Add(item);
                navCats.Add(c);
                navItems.Add(item);
                navBars.Add(bar);
                navNames.Add(name);
                navCounts.Add(cnt);
            }

            UpdateNav();
        }

        void UpdateNav()
        {
            for (int i = 0; i < navItems.Count; i++)
            {
                bool sel = navCats[i] == current;
                navItems[i].Background = sel ? Ui.AccentSoft : Brushes.Transparent;
                navBars[i].Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
                navNames[i].Foreground = sel ? Ui.AccentText : Ui.Fg;
                navNames[i].FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        void OnSearchChanged()
        {
            // 程序自己清空搜索框时不重建（打开面板的场景）
            if (searchSuppress) return;
            if (searchHint != null)
                searchHint.Visibility = searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderApps();
        }

        List<AppItem> CommonApps()
        {
            List<AppItem> list = new List<AppItem>();
            foreach (AppItem a in catalog.Apps)
            {
                if (a.Usage > 0) list.Add(a);
            }
            list.Sort(RenderCompare);
            if (list.Count > 12) list = list.GetRange(0, 12);
            return list;
        }

        /// <summary>当前已渲染内容的签名，用于判断是否真的需要重建磁贴。</summary>
        string renderedSig = "";

        /// <summary>
        /// 内容没变就不重建磁贴。避免“每次打开面板都销毁并重建全部控件”，
        /// 那是重复开关面板时内存持续上涨的主因。
        /// </summary>
        void RenderAppsIfNeeded()
        {
            try
            {
                string sig = CurrentContentSig();
                if (sig == renderedSig) return;
                RenderApps();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "按需渲染磁贴");
            }
        }

        void RenderApps()
        {
            if (tilePanel == null) return;
            try
            {
                // 主动重建时同步刷新签名，避免下次 RenderAppsIfNeeded 误判为“没变”
                renderedSig = CurrentContentSig();
                RenderAppsCore();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "渲染应用磁贴");
            }
        }

        /// <summary>当前界面内容的签名（分类 + 搜索词 + 应用数 + 自定义排序长度）。</summary>
        string CurrentContentSig()
        {
            try
            {
                string q = searchBox == null ? "" : searchBox.Text.Trim();
                int orderCount = 0;
                List<string> order;
                if (customOrders.TryGetValue(current, out order) && order != null) orderCount = order.Count;
                return current + "\u0001" + q + "\u0001" + catalog.Apps.Count + "\u0001" + orderCount;
            }
            catch
            {
                return "";
            }
        }

        void RenderAppsCore()
        {
            tilePanel.Children.Clear();

            string q = searchBox == null ? "" : searchBox.Text.Trim();
            showingCommon = (q.Length == 0 && current == "常用");
            List<AppItem> list;

            if (q.Length > 0)
            {
                list = new List<AppItem>();
                foreach (AppItem a in catalog.Apps)
                {
                    // 支持中文/英文直接匹配，也支持全拼（weixin）和首字母（wx）
                    if (Pinyin.Matches(a.Name, q))
                        list.Add(a);
                }
                list.Sort(RenderCompare);
            }
            else if (current == "常用")
            {
                list = CommonApps();
            }
            else
            {
                list = catalog.ByCategory(current);
            }

            ApplyCustomOrder(list);
            renderedApps = new List<AppItem>(list);
            // 重渲染会丢弃旧的 Border 对象，选中状态必须一起清掉（否则 selectedTile 指向已移除的控件）
            selectedTile = null;
            selectedIndex = -1;
            foreach (AppItem a in list)
                tilePanel.Children.Add(MakeTile(a));

            if (list.Count == 0)
            {
                TextBlock none = Ui.T(q.Length > 0 ? "没有找到匹配的应用" : (current == "常用" ? "还没有常用应用：从面板启动几次就会出现" : "这个分类里没有应用"), 13, Ui.Sub);
                none.Margin = new Thickness(18, 18, 0, 0);
                tilePanel.Children.Add(none);
            }

            footerCount.Text = "共 " + catalog.Apps.Count + " 个应用";
        }

        static int RenderCompare(AppItem a, AppItem b)
        {
            int c = b.Usage.CompareTo(a.Usage);
            if (c != 0) return c;
            c = b.LastUsed.CompareTo(a.LastUsed);
            if (c != 0) return c;
            return string.Compare(a.Name, b.Name, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), System.Globalization.CompareOptions.None);
        }

        Border MakeTile(AppItem a)
        {
            Border b = new Border();
            b.Width = 96;
            b.Height = 90;
            b.CornerRadius = new CornerRadius(10);
            b.Margin = new Thickness(2);
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;
            b.ToolTip = a.Name;

            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;

            ImageSource icon = AppIcons.Get(a.Path);
            if (icon != null)
            {
                Image img = new Image();
                img.Source = icon;
                img.Width = 32;
                img.Height = 32;
                img.HorizontalAlignment = HorizontalAlignment.Center;
                img.Margin = new Thickness(0, 0, 0, 6);
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                sp.Children.Add(img);
            }
            else
            {
                Border ph = new Border();
                ph.Width = 32;
                ph.Height = 32;
                ph.CornerRadius = new CornerRadius(7);
                ph.Background = Ui.AccentSoft;
                ph.HorizontalAlignment = HorizontalAlignment.Center;
                ph.Margin = new Thickness(0, 0, 0, 6);
                string initial = string.IsNullOrEmpty(a.Name) ? "?" : a.Name.Substring(0, 1);
                TextBlock init = Ui.T(initial, 13, Ui.AccentText, FontWeights.SemiBold);
                init.HorizontalAlignment = HorizontalAlignment.Center;
                init.VerticalAlignment = VerticalAlignment.Center;
                ph.Child = init;
                sp.Children.Add(ph);
            }

            TextBlock name = new TextBlock();
            name.Text = a.Name;
            name.FontFamily = Ui.YaHei;
            name.FontSize = 11.5;
            name.Foreground = Ui.Fg;
            name.TextWrapping = TextWrapping.Wrap;
            name.TextAlignment = TextAlignment.Center;
            name.LineHeight = 15;
            name.MaxWidth = 88;

            Border nameClip = new Border();
            nameClip.MaxHeight = 30;
            nameClip.ClipToBounds = true;
            nameClip.Child = name;
            nameClip.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(nameClip);

            b.Child = sp;
            Point downPt = new Point();
            bool tileDrag = false;
            b.MouseEnter += delegate
            {
                if (!ReferenceEquals(b, selectedTile)) b.Background = Ui.Hover;
            };
            b.MouseLeave += delegate
            {
                if (!ReferenceEquals(b, selectedTile)) b.Background = Brushes.Transparent;
            };
            b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                downPt = e.GetPosition(tilePanel);
                tileDrag = false;
                // 鼠标点击磁贴时，键盘选中跟随到它，方向键从当前位置继续
                SelectTile(renderedApps.IndexOf(a), false);
                try { b.CaptureMouse(); } catch { }
            };
            b.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                Point p = e.GetPosition(tilePanel);
                if (!tileDrag && (Math.Abs(p.X - downPt.X) > 6 || Math.Abs(p.Y - downPt.Y) > 6))
                {
                    tileDrag = true;
                    b.Opacity = 0.45;
                }
            };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                try { b.ReleaseMouseCapture(); } catch { }
                b.Opacity = 1.0;
                Ui.Log("tile up drag=" + tileDrag + " " + a.Name);
                if (tileDrag)
                {
                    tileDrag = false;
                    DropTile(a);
                }
                else
                {
                    Launch(a);
                }
            };
            b.MouseRightButtonUp += delegate { ShowTileMenu(a); };
            return b;
        }

        void DropTile(AppItem a)
        {
            try
            {
                if (tilePanel == null || renderedApps == null || renderedApps.Count == 0) return;
                Point p = Mouse.GetPosition(tilePanel);
                int target = -1;
                int n = Math.Min(renderedApps.Count, tilePanel.Children.Count);
                for (int i = 0; i < n; i++)
                {
                    FrameworkElement fe = tilePanel.Children[i] as FrameworkElement;
                    if (fe == null) continue;
                    Point tl = fe.TranslatePoint(new Point(0, 0), tilePanel);
                    if (p.X >= tl.X && p.X <= tl.X + fe.ActualWidth && p.Y >= tl.Y && p.Y <= tl.Y + fe.ActualHeight)
                    {
                        target = i;
                        break;
                    }
                }
                int from = renderedApps.IndexOf(a);
                Ui.Log("DropTile " + a.Name + " from=" + from + " target=" + target + " n=" + n);
                if (target < 0) return;
                if (from < 0 || from == target) return;
                List<AppItem> newOrder = new List<AppItem>(renderedApps);
                AppItem moved = newOrder[from];
                newOrder.RemoveAt(from);
                if (target > newOrder.Count) target = newOrder.Count;
                newOrder.Insert(target, moved);
                List<string> paths = new List<string>();
                foreach (AppItem it in newOrder) paths.Add(it.Path);
                customOrders[current] = paths;
                SaveCustomOrders();
                RenderApps();
            }
            catch { }
        }

        void ShowTileMenu(AppItem a)
        {
            if (a == null || string.IsNullOrEmpty(a.Path)) return;
            ContextMenu cm = new ContextMenu();

            // ---- 打开文件所在位置（所有分类都有）----
            MenuItem openDir = new MenuItem();
            openDir.Header = "打开文件所在位置";
            openDir.Click += delegate { OpenContainingFolder(a); };
            cm.Items.Add(openDir);

            // ---- 移到分类 →（子菜单）----
            // 结果存 config\category-overrides.json，优先级高于关键词规则，
            // 重新扫描后依然保持。
            MenuItem moveTo = new MenuItem();
            moveTo.Header = "移到分类";
            foreach (string cat in MovableCategories())
            {
                string target = cat;
                MenuItem sub = new MenuItem();
                sub.Header = target;
                bool isCurrent = string.Equals(a.Category, target, StringComparison.Ordinal);
                sub.IsChecked = isCurrent;
                sub.IsEnabled = !isCurrent; // 已在当前分类就没必要再移
                sub.Click += delegate { MoveAppToCategory(a, target); };
                moveTo.Items.Add(sub);
            }

            // 已经有手工覆盖时，提供「恢复自动分类」
            string overridden = GetOverride(a.Path);
            if (overridden != null)
            {
                moveTo.Items.Add(new Separator());
                MenuItem reset = new MenuItem();
                reset.Header = "恢复自动分类";
                reset.Click += delegate { MoveAppToCategory(a, null); };
                moveTo.Items.Add(reset);
            }

            cm.Items.Add(moveTo);

            // ---- 从常用移除（只在「常用」分类出现）----
            if (showingCommon)
            {
                cm.Items.Add(new Separator());

                MenuItem rm = new MenuItem();
                rm.Header = "从常用移除";
                rm.IsEnabled = a.Usage > 0;
                rm.Click += delegate
                {
                    if (usage != null) usage.Remove(a.Path);
                    a.Usage = 0;
                    a.LastUsed = 0;
                    RebuildNav();
                    RenderApps();
                };
                cm.Items.Add(rm);
            }

            taskMenuOpen = true;
            cm.Closed += delegate { taskMenuOpen = false; };
            cm.Placement = PlacementMode.MousePoint;
            cm.IsOpen = true;
        }

        /// <summary>可选的目标分类：默认分类 + 当前已存在的其它分类。</summary>
        List<string> MovableCategories()
        {
            List<string> list = new List<string>();
            foreach (string c in Catalog.DefaultOrder)
            {
                if (!list.Contains(c)) list.Add(c);
            }
            // 补上数据里出现过、但不在默认表里的分类（例如用户自己加过的）
            try
            {
                if (catalog != null && catalog.Order != null)
                {
                    foreach (string c in catalog.Order)
                    {
                        if (!string.IsNullOrEmpty(c) && !list.Contains(c)) list.Add(c);
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>读某应用的覆盖分类；无覆盖返回 null。</summary>
        string GetOverride(string appPath)
        {
            try
            {
                return overrides == null ? null : overrides.Get(appPath);
            }
            catch { return null; }
        }

        /// <summary>
        /// 把应用移到指定分类（category 传 null 表示恢复自动分类）。
        /// 写入 category-overrides.json，然后重新加载目录并刷新界面。
        /// </summary>
        void MoveAppToCategory(AppItem a, string category)
        {
            try
            {
                if (a == null || string.IsNullOrEmpty(a.Path)) return;
                if (overrides == null) overrides = CategoryOverrides.Load(baseDir);

                bool changed;
                if (string.IsNullOrEmpty(category))
                {
                    changed = overrides.Remove(a.Path);
                    Ui.Log("恢复自动分类: " + a.Name);
                }
                else
                {
                    changed = overrides.Set(a.Path, category);
                    Ui.Log("移到分类: " + a.Name + " -> " + category);
                }

                if (!changed) return;

                // 重新加载目录（会重新应用覆盖表），保持当前选中分类
                ReloadCatalog();
                RebuildNav();
                RenderApps();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "移到分类");
            }
        }

        /// <summary>
        /// 在资源管理器里打开该应用所在目录并选中它。
        /// 应用路径可能是 .exe（便携识别）或 .lnk（开始菜单），两种情况都要能定位。
        /// </summary>
        void OpenContainingFolder(AppItem a)
        {
            try
            {
                string path = a == null ? null : a.Path;
                if (string.IsNullOrEmpty(path))
                {
                    MessageBox.Show("这个应用没有可定位的路径。", "四叶草面板");
                    return;
                }

                string arg;
                string err;
                if (!TryBuildExplorerArgs(path, out arg, out err))
                {
                    MessageBox.Show(err, "四叶草面板");
                    return;
                }

                Process.Start("explorer.exe", arg);
                Ui.Log("打开文件位置: " + path + "  参数: " + arg);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "打开文件所在位置");
                try
                {
                    MessageBox.Show("无法打开文件位置：" + ex.Message, "四叶草面板");
                }
                catch { }
            }
        }

        /// <summary>
        /// 根据应用路径生成 explorer.exe 的参数（纯逻辑，可单测）。
        ///
        /// 三种情况：
        ///   1. 是存在的文件（.exe / .lnk 解析后的目标）→ `/select,"目标"` 打开父目录并选中
        ///   2. 文件不存在但目录还在 → `"目录"` 直接打开目录
        ///   3. 都不行 → 返回 false，err 里是给用户看的提示
        ///
        /// .lnk 会先解析真实目标；解析失败就退回到 .lnk 自身所在目录。
        /// </summary>
        public static bool TryBuildExplorerArgs(string path, out string args, out string error)
        {
            args = null;
            error = null;

            if (string.IsNullOrEmpty(path))
            {
                error = "这个应用没有可定位的路径。";
                return false;
            }

            string target = path;
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string real = ResolveShortcutTarget(path);
                if (!string.IsNullOrEmpty(real) && File.Exists(real)) target = real;
            }

            // 情况 1：文件存在 → 打开父目录并选中它
            if (File.Exists(target))
            {
                args = "/select,\"" + target + "\"";
                return true;
            }

            // 情况 2：文件没了，但目录还在 → 直接打开目录
            string dir = Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(dir)) dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                args = "\"" + dir + "\"";
                return true;
            }

            // 情况 3：都找不到
            error = "找不到这个文件：\r\n" + target;
            return false;
        }

        /// <summary>解析 .lnk 的目标路径；无效或解析失败返回 null（不会返回空字符串）。</summary>
        public static string ResolveShortcutTarget(string lnk)
        {
            try
            {
                if (string.IsNullOrEmpty(lnk)) return null;
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                object shell = Activator.CreateInstance(shellType);
                object sc = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                if (sc == null) return null;
                object tp = sc.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.GetProperty, null, sc, null);
                string s = tp as string;
                // WScript.Shell 对无效/不存在的快捷方式返回空字符串而不是 null，
                // 这里统一成 null，避免调用方误以为"解析成功但目标为空"。
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch
            {
                return null;
            }
        }
    }
}
