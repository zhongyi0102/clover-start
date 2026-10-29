using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 搜索结果列表（系统级搜索的界面部分）。
    ///
    /// 与磁贴区分开：搜索结果可能是文件、文件夹、注册表项，
    /// 不是 AppItem —— 磁贴的启动/拖动/右键那套完全不适用。
    /// 所以用独立的列表，只做"点击打开"。
    ///
    /// 两级加载（参考 GlassBoard）：
    ///   1. Instant：同步、&lt; 40ms，立刻显示
    ///   2. Deep：后台线程，分批追加到同一列表
    ///
    /// 取消机制：每次输入新词就取消上一次的 Deep，
    /// 否则连续打字会累积成多个并发磁盘遍历。
    /// </summary>
    class SearchResultList
    {
        readonly StackPanel host;

        /// <summary>当前这次搜索的取消源。</summary>
        CancellationTokenSource cts;

        /// <summary>已显示的结果（用于去重与计数）。</summary>
        readonly HashSet<string> shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 当前已显示的结果条数（底部计数栏用）。
        ///
        /// Deep 是流式追加的，计数会随时间增长 —— 这正是期望行为：
        /// 用户看到"找到 N 条结果"在慢慢变大，知道搜索还在进行。
        /// </summary>
        public int ShownCount
        {
            get { return shown.Count; }
        }

        /// <summary>结果条数上限（界面上超过这个数也没人翻）。</summary>
        const int MaxDisplay = 60;

        public SearchResultList(StackPanel host)
        {
            this.host = host;
        }

        /// <summary>清空并取消正在进行的 Deep。</summary>
        public void Clear()
        {
            try
            {
                if (cts != null) { cts.Cancel(); cts = null; }
                shown.Clear();
                if (host != null) host.Children.Clear();
            }
            catch { }
        }

        /// <summary>
        /// 应用清单（由 PanelWindow 注入）。
        ///
        /// 为什么要注入而不是直接读 Catalog：
        /// Catalog 每次 Load 都要解析 JSON，搜索是高频操作（每敲一个字），
        /// 不能每次都读盘。PanelWindow 已经持有内存中的 catalog，传进来即可。
        /// </summary>
        public Func<List<AppItem>> CatalogProvider;

        /// <summary>
        /// 是否启用磁盘文件搜索（默认 **false**）。
        ///
        /// 由 settings.DiskSearchEnabled 注入。关掉时只搜
        /// 「已安装应用（带拼音）+ 开始菜单 + 注册表」，结果全是可启动的程序。
        /// 磁盘搜索的代码（SystemSearch.Deep）保留，打开开关即可恢复。
        /// </summary>
        public bool EnableDiskSearch = false;

        /// <summary>
        /// 在已扫描的应用清单里匹配（支持中文/英文/全拼/首字母）。
        ///
        /// 与 SystemSearch 的分工：
        ///   · 这里搜的是"已经扫描进面板的 119 个应用" —— 带拼音支持
        ///   · SystemSearch 搜的是"系统里存在但可能没被扫进面板的程序"
        /// 两者互补，缺一不可。
        /// </summary>
        List<SearchResult> MatchCatalog(string query)
        {
            List<SearchResult> list = new List<SearchResult>();
            try
            {
                if (CatalogProvider == null) return list;
                List<AppItem> apps = CatalogProvider();
                if (apps == null) return list;

                foreach (AppItem a in apps)
                {
                    if (a == null || string.IsNullOrEmpty(a.Path)) continue;
                    if (!Pinyin.Matches(a.Name, query)) continue;

                    SearchResult r = new SearchResult();
                    r.Name = a.Name;
                    r.Path = a.Path;
                    r.IsFolder = false;
                    r.Source = "已安装";
                    r.Ext = Path.GetExtension(a.Path).ToLowerInvariant();
                    list.Add(r);
                }

                // 名字短的排前面（搜 "chr" 时 Chrome 应该比 ChromeDriver-helper 靠前）
                list.Sort(delegate(SearchResult x, SearchResult y)
                {
                    int lx = x.Name == null ? 0 : x.Name.Length;
                    int ly = y.Name == null ? 0 : y.Name.Length;
                    if (lx != ly) return lx.CompareTo(ly);
                    return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
                });
            }
            catch { }
            return list;
        }

        /// <summary>
        /// 执行一次完整搜索（Instant 立即显示 + Deep 后台追加）。
        /// 必须在界面线程调用。
        /// </summary>
        public void Search(string query)
        {
            try
            {
                Clear();
                if (host == null) return;
                if (string.IsNullOrWhiteSpace(query)) return;

                // ---- 第零级：已扫描的应用清单 ----
                //
                // ★ 这一步是必须的，不是优化：
                // 搜索时面板会把 tilePanel 设成 Collapsed（让位给搜索结果），
                // 而 SystemSearch.Instant 只搜"开始菜单 + 注册表 + 磁盘"，
                // **不包含 catalog.Apps 里那 119 个已扫描应用**。
                // 结果是：拼音搜索（weixin / wx）在界面上永远搜不到微信 ——
                // 应用清单匹配到了，但磁贴被隐藏，搜索结果里又没有它。
                //
                // 探针实测 Pinyin.Matches("微信", "weixin") == true，
                // 所以匹配逻辑本身没问题，是数据源漏了这一档。
                AppendBatch(MatchCatalog(query), true);

                // ---- 第一级：Instant（同步，很快）----
                List<SearchResult> instant = SystemSearch.Instant(query);
                AppendBatch(instant, true);

                if (shown.Count == 0)
                {
                    // 三档都没结果时给个提示，避免用户以为"没反应"
                    AddStatusLine("正在搜索更多位置…");
                }

                // ---- 第二级：Deep（后台流式）----
                //
                // 受 EnableDiskSearch 开关控制（由 settings.DiskSearchEnabled 注入）。
                // 关掉时整段跳过 —— 不发起线程、不遍历磁盘、结果里只有可启动的程序。
                //
                // 为什么要有这个开关：磁盘遍历即使有 900ms 时间预算，也会让
                // 搜索结果混进大量文件名（"报告.docx"、"报告.pdf"…），
                // 而用户打开启动面板通常是想**启动程序**，不是找文件。
                // 找文件用资源管理器更合适。
                if (!EnableDiskSearch)
                {
                    cts = null;
                    return;
                }

                cts = new CancellationTokenSource();
                CancellationToken token = cts.Token;
                string q = query;

                System.Threading.ThreadPool.QueueUserWorkItem(delegate(object state)
                {
                    try
                    {
                        SystemSearch.Deep(q, delegate(List<SearchResult> batch)
                        {
                            // 回调在后台线程 → 必须切回界面线程再碰控件
                            try
                            {
                                if (token.IsCancellationRequested) return;
                                host.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                                    new Action(delegate
                                    {
                                        try
                                        {
                                            if (token.IsCancellationRequested) return;
                                            AppendBatch(batch, false);
                                        }
                                        catch { }
                                    }));
                            }
                            catch { }
                        }, token);
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "搜索");
            }
        }

        /// <summary>
        /// 追加一批结果（去重、限量），并在来源变化时插入分组标题。
        ///
        /// 为什么分组：实测搜 "chrome" 时三种来源混在一起 ——
        /// 磁贴级的"Google Chrome"和磁盘上的"chrome_100_percent.pak"
        /// 看起来权重相同，用户分不清哪条是"程序"哪条是"文件"。
        /// 按来源加小标题（应用 / 系统 / 磁盘）后一眼可辨。
        /// </summary>
        void AppendBatch(List<SearchResult> batch, bool isInstant)
        {
            try
            {
                if (host == null || batch == null) return;

                foreach (SearchResult r in batch)
                {
                    if (r == null) continue;
                    if (shown.Count >= MaxDisplay) return;
                    if (!shown.Add(r.Path)) continue;

                    // 来源变化时插入分组标题（去重后第一次遇到该来源）
                    string groupKey = "G:" + r.Source;
                    if (shown.Add(groupKey))
                    {
                        AddGroupHeader(r.Source);
                    }

                    host.Children.Add(MakeRow(r));
                }

                // Instant 阶段没有结果时，先不显示"无结果"——Deep 可能还有
                if (isInstant && shown.Count == 0) return;
            }
            catch { }
        }

        /// <summary>分组小标题（如「应用」「磁盘文件」）。</summary>
        void AddGroupHeader(string source)
        {
            try
            {
                string label;
                switch (source)
                {
                    case "已安装": label = "应用（已安装）"; break;
                    case "开始菜单": label = "应用（开始菜单）"; break;
                    case "注册表": label = "应用（注册表）"; break;
                    case "磁盘": label = "磁盘文件"; break;
                    default: label = source; break;
                }

                Border b = new Border();
                b.Margin = new Thickness(0, Ui.S.Sm, 0, Ui.S.Xs);
                TextBlock t = Ui.T(label, 10.5, Ui.Sub);
                t.Margin = new Thickness(Ui.S.Xs, 0, 0, 0);
                b.Child = t;
                host.Children.Add(b);
            }
            catch { }
        }

        void AddStatusLine(string text)
        {
            try
            {
                TextBlock t = Ui.T(text, 12, Ui.Sub);
                t.Margin = new Thickness(Ui.S.Sm, Ui.S.Sm, 0, 0);
                host.Children.Add(t);
            }
            catch { }
        }

        /// <summary>
        /// 一行搜索结果：图标 + 名字 + 路径。
        ///
        /// 图标列**固定 30px 宽、内容居中**：
        /// 第一版让图标自适应尺寸，结果 exe 图标（18px）和文件字形（15px）
        /// 大小不一，左列参差不齐 —— 看起来乱。固定列宽后所有行对齐。
        /// </summary>
        Border MakeRow(SearchResult r)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(Ui.R.Sm);
            b.Margin = new Thickness(0, 1, 0, 1);
            b.Padding = new Thickness(Ui.S.Xs, 5, Ui.S.Sm, 5);
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;
            b.Tag = r;

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = new GridLength(34);   // 固定：图标列对齐
            g.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);

            // 图标位
            FrameworkElement ico = MakeIcon(r);
            ico.HorizontalAlignment = HorizontalAlignment.Center;
            ico.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(ico, 0);
            g.Children.Add(ico);

            // 文字：名字（主）+ 路径（次）
            StackPanel text = new StackPanel();
            text.VerticalAlignment = VerticalAlignment.Center;

            TextBlock name = Ui.T(r.Name, 12.5, Ui.Fg);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(name);

            TextBlock sub = Ui.T(ShortPath(r), 10.5, Ui.Sub);
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(sub);

            Grid.SetColumn(text, 1);
            g.Children.Add(text);

            b.Child = g;

            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonDown += delegate { b.Background = Ui.Pressed; };
            b.MouseLeftButtonUp += delegate
            {
                b.Background = Ui.Hover;
                Open(r);
            };
            b.MouseRightButtonUp += delegate
            {
                ShowRowMenu(r, b);
            };

            return b;
        }

        /// <summary>图标：exe/lnk 用应用图标缓存，文件夹用字形，其他用文件字形。</summary>
        FrameworkElement MakeIcon(SearchResult r)
        {
            try
            {
                // 文件夹
                if (r.IsFolder)
                {
                    TextBlock t = new TextBlock();
                    t.Text = "\uE8B7";   // Folder
                    t.FontFamily = Ui.Mdl;
                    t.FontSize = 15;
                    t.Foreground = Ui.AccentText;
                    t.Width = 22;
                    t.TextAlignment = TextAlignment.Center;
                    return t;
                }

                // exe / lnk：复用应用图标缓存（已有）
                if (r.Ext == ".exe" || r.Ext == ".lnk" || r.Ext == ".url" || r.Ext == ".appref-ms")
                {
                    ImageSource src = AppIcons.Get(r.Path);
                    if (src != null)
                    {
                        Image img = new Image();
                        img.Source = src;
                        img.Width = 18;
                        img.Height = 18;
                        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                        return img;
                    }
                }

                // 其他文件：按扩展名给个字形
                TextBlock f = new TextBlock();
                f.Text = "\uE7C3";   // Page
                f.FontFamily = Ui.Mdl;
                f.FontSize = 14;
                f.Foreground = Ui.Sub;
                f.Width = 22;
                f.TextAlignment = TextAlignment.Center;
                return f;
            }
            catch
            {
                return new Border { Width = 18, Height = 18 };
            }
        }

        /// <summary>路径缩写：只显示最后两级，太长会撑爆行宽。</summary>
        static string ShortPath(SearchResult r)
        {
            try
            {
                string p = r.Path;
                if (string.IsNullOrEmpty(p)) return r.Source;

                if (r.IsFolder) return p;

                // 文件：显示"目录\文件名.扩展名"
                string dir = Path.GetDirectoryName(p);
                if (string.IsNullOrEmpty(dir)) return p;

                string[] parts = dir.Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length <= 2) return dir;

                return "...\\" + parts[parts.Length - 2] + "\\" + parts[parts.Length - 1];
            }
            catch { return r.Source; }
        }

        /// <summary>打开：文件夹用资源管理器，文件用默认程序。</summary>
        void Open(SearchResult r)
        {
            try
            {
                if (r == null || string.IsNullOrEmpty(r.Path)) return;
                if (r.IsFolder)
                {
                    if (Directory.Exists(r.Path)) Process.Start("explorer.exe", "\"" + r.Path + "\"");
                }
                else
                {
                    if (File.Exists(r.Path)) Process.Start(new ProcessStartInfo(r.Path) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "打开搜索结果");
            }
        }

        /// <summary>
        /// 「打开所在位置」应该打开哪个目录。
        ///
        /// 商店应用（shell:AppsFolder\...）没有文件系统位置，返回 null ——
        /// 调用方据此弹提示，不尝试打开。
        ///
        /// 抽成静态方法是为了可单测：不需要真的弹菜单、不需要真的打开资源管理器。
        /// </summary>
        public static string LocationDirOf(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return null;
                if (AppPaths.IsShellPath(path)) return null;
                string dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) return null;
                return Directory.Exists(dir) ? dir : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 「复制路径」应该复制什么。
        ///
        /// 商店应用复制 AppUserModelID（唯一能标识它的字符串），
        /// 而不是 shell:AppsFolder\... 整个前缀 —— 后者对用户没用。
        /// </summary>
        public static string CopyTextOf(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return "";
                if (AppPaths.IsShellPath(path))
                {
                    string aumid = AppPaths.ExtractAumid(path);
                    return string.IsNullOrEmpty(aumid) ? path : aumid;
                }
                return path;
            }
            catch { return path ?? ""; }
        }

        /// <summary>右键：打开 / 打开所在位置 / 复制路径。</summary>
        void ShowRowMenu(SearchResult r, FrameworkElement anchor)
        {
            try
            {
                ContextMenu cm = new ContextMenu();

                MenuItem open = new MenuItem();
                open.Header = "打开";
                open.Click += delegate { Open(r); };
                cm.Items.Add(open);

                // ---- 打开所在位置 ----
                //
                // 商店应用（shell:AppsFolder\...）没有文件系统位置，
                // Path.GetDirectoryName 会返回 "shell:AppsFolder" 这种非法目录。
                // 这里与面板磁贴保持一致：弹提示，不尝试打开。
                MenuItem loc = new MenuItem();
                loc.Header = "打开所在位置";
                loc.Click += delegate
                {
                    try
                    {
                        if (AppPaths.IsShellPath(r.Path))
                        {
                            MessageBox.Show("商店应用没有文件位置。\r\n\r\n"
                                + "它由 Microsoft Store 管理，安装文件在受保护的 WindowsApps 目录下。\r\n"
                                + "如需管理，请打开「设置 → 应用 → 已安装的应用」。", "四叶草");
                            return;
                        }

                        string dir = Path.GetDirectoryName(r.Path);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                            Process.Start("explorer.exe", "\"" + dir + "\"");
                    }
                    catch { }
                };
                cm.Items.Add(loc);

                MenuItem copy = new MenuItem();
                copy.Header = "复制路径";
                copy.Click += delegate
                {
                    try
                    {
                        if (AppPaths.IsShellPath(r.Path))
                        {
                            string aumid = AppPaths.ExtractAumid(r.Path);
                            Clipboard.SetText(string.IsNullOrEmpty(aumid) ? r.Path : aumid);
                        }
                        else
                        {
                            Clipboard.SetText(r.Path);
                        }
                    }
                    catch { }
                };
                cm.Items.Add(copy);

                cm.PlacementTarget = anchor;
                cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                cm.IsOpen = true;
            }
            catch { }
        }
    }
}
