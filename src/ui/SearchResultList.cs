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
    /// 与磁贴区分开：结果还可能来自开始菜单或注册表，不一定有 AppItem。
    /// 因此使用独立列表，提供启动、打开所在位置和复制路径。
    ///
    /// 两级加载：目录中的应用从内存立即匹配；开始菜单与注册表从内存索引
    /// 在后台补充。图标也异步加载，首次冷建索引不会阻塞界面线程。
    ///
    /// 取消机制：每次输入新词就取消上一次的搜索，
    /// 避免连续打字累积成多个并发搜索。
    /// </summary>
    class SearchResultList
    {
        readonly StackPanel host;

        /// <summary>已显示的结果（用于去重与计数）。</summary>
        readonly HashSet<string> shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int resultCount;
        int generation;
        bool completed;

        /// <summary>结果条数或完成状态变化时通知宿主更新底栏。</summary>
        public Action ResultsChanged;

        /// <summary>结果成功打开后通知宿主记录真实启动历史。</summary>
        public Action<string> ResultOpened;

        /// <summary>
        /// 当前已显示的结果条数（底部计数栏用）。
        ///
        /// 计数随结果追加而增长 —— 这正是期望行为：
        /// 用户看到"找到 N 条结果"在慢慢变大，知道搜索还在进行。
        /// </summary>
        public int ShownCount
        {
            get { return resultCount; }
        }

        public bool Completed { get { return completed; } }

        /// <summary>结果条数上限（界面上超过这个数也没人翻）。</summary>
        const int MaxDisplay = 60;

        public SearchResultList(StackPanel host)
        {
            this.host = host;
        }

        /// <summary>清空并取消正在进行的搜索。</summary>
        public void Clear()
        {
            try
            {
                unchecked { generation++; }
                shown.Clear();
                resultCount = 0;
                completed = true;
                if (host != null) host.Children.Clear();
                NotifyChanged();
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

        /// <summary>按路径查询是否手动固定，供搜索个性化排序使用。</summary>
        public Func<string, bool> PinnedProvider;

        /// <summary>系统索引结果也可读取启动历史，避免只对目录结果个性化。</summary>
        public Func<string, int> UsageProvider;
        public Func<string, long> LastUsedProvider;

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
                    if (!SearchRanking.Matches(a.Name, query)) continue;

                    SearchResult r = new SearchResult();
                    r.Name = a.Name;
                    r.Path = a.Path;
                    r.IsFolder = false;
                    r.Source = "已安装";
                    r.Ext = Path.GetExtension(a.Path).ToLowerInvariant();
                    r.Usage = a.Usage;
                    r.LastUsed = a.LastUsed;
                    r.Pinned = PinnedProvider != null && PinnedProvider(a.Path);
                    list.Add(r);
                }

                SearchRanking.Sort(list, query);
            }
            catch { }
            return list;
        }

        /// <summary>
        /// 执行一次完整搜索。**必须在界面线程调用**。
        ///
        /// 两路职责（不是"完全统一"，是主 + 补充）：
        ///   · 主：MatchCatalog —— 已扫描进面板的应用清单，**支持拼音**
        ///     （`wx` / `weixin` → 微信），这是系统索引做不到的
        ///   · 补充：SystemSearch.Instant —— 查询预热后的开始菜单 + 注册表
        ///     App Paths / Uninstall 内存索引，能发现没被扫进清单的程序
        ///     （`notepad` / `7z` / `code` 这类只在注册表里的）
        ///
        /// **为什么两路都要**（实测数据）：
        ///   · 只有 MatchCatalog：`notepad` / `7z` 搜不到（0 条）
        ///   · 只有系统索引：`wx` / `weixin` 搜不到（0 条，无拼音）
        ///
        /// 补充结果**总是执行**（不因主搜索有结果就跳过）。返回后两路先
        /// 合并去重，再按匹配质量和用户习惯统一重排，最后应用 MaxDisplay。
        /// </summary>
        public void Search(string query)
        {
            try
            {
                Clear();
                if (host == null) return;
                if (string.IsNullOrWhiteSpace(query)) return;
                completed = false;
                int ownGeneration = generation;

                // ---- 第零级：已扫描的应用清单 ----
                //
                // ★ 这一步是必须的，不是优化：
                // 搜索时面板会把 tilePanel 设成 Collapsed（让位给搜索结果），
                // 而 SystemSearch.Instant 只搜"开始菜单 + 注册表"，
                // **不包含 catalog.Apps 里那 119 个已扫描应用**。
                // 结果是：拼音搜索（weixin / wx）在界面上永远搜不到微信 ——
                // 应用清单匹配到了，但磁贴被隐藏，搜索结果里又没有它。
                //
                // 探针实测 Pinyin.Matches("微信", "weixin") == true，
                // 所以匹配逻辑本身没问题，是数据源漏了这一档。
                List<SearchResult> ownMain = MatchCatalog(query);
                RenderMerged(ownMain, null, query, false);

                // ---- 第一级：系统补充（后台）----
                // 即使搜索索引尚未预热，首次递归开始菜单/读取注册表也不会占住 UI。
                ThreadPool.QueueUserWorkItem(delegate
                {
                    List<SearchResult> instant = SystemSearch.Instant(query);
                    try
                    {
                        host.Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (ownGeneration != generation) return;
                            DecorateUsage(instant);
                            RenderMerged(ownMain, instant, query, true);
                        }));
                    }
                    catch { }
                });
                NotifyChanged();

            }

            catch (Exception ex)
            {
                Ui.LogError(ex, "搜索");
            }
        }

        /// <summary>
        /// 把统一排名后的结果渲染为列表，并做最后一道防御性去重。
        /// 来源不再切成大分组，否则一个精确注册表结果仍会被视觉上压到
        /// 模糊目录结果之后；来源改为每行的次要说明。
        /// </summary>
        void AppendBatch(List<SearchResult> batch)
        {
            try
            {
                if (host == null || batch == null) return;

                foreach (SearchResult r in batch)
                {
                    if (r == null) continue;
                    if (resultCount >= MaxDisplay) return;
                    // ---- 去重：路径 + 归一化名字，双键 ----
                    //
                    // 为什么单靠 Path 不够（实测）：同一应用在两路里路径不同 ——
                    //   微信  →  Catalog: ...\微信.lnk      Instant: ...\Weixin.exe
                    //   Steam →  Catalog: ...\Steam.lnk     Instant: ...\uninstall.exe
                    //   QQ    →  Catalog: ...\QQ.lnk        Instant: ...\QQ.exe
                    // 但它们的 **Name 完全相同**，所以再加一道名字键就能拦住。
                    //
                    // 名字键**只用 Trim + 小写**，不用 NameMatch.Normalize：
                    // Normalize 是为分类设计的（丢掉所有非字母数字），会把
                    //   Notepad++  →  notepad     ← 和 Notepad 撞车
                    // 实测 Notepad++ 被误合并成 Notepad，所以这里用最保守的键。
                    //
                    // 保守键能覆盖实测里的全部重复场景（微信/Steam/QQ/迅雷），
                    // 且不会误合并 Notepad vs Notepad++ / Chrome vs Chrome Beta。
                    // 代价：全角ＱＱ vs 半角QQ 不会合并（少见，可接受）。
                    if (!shown.Add(r.Path)) continue;

                    // 只对**非空**名字加键：空名的结果不做名字去重。
                    string normName = (r.Name ?? "").Trim().ToLowerInvariant();
                    if (normName.Length > 0 && !shown.Add("N:" + normName)) continue;

                    host.Children.Add(MakeRow(r));
                    resultCount++;
                }

                NotifyChanged();
            }
            catch { }
        }

        void DecorateUsage(List<SearchResult> results)
        {
            if (results == null) return;
            foreach (SearchResult r in results)
            {
                if (r == null || string.IsNullOrEmpty(r.Path)) continue;
                try { if (UsageProvider != null) r.Usage = UsageProvider(r.Path); } catch { }
                try { if (LastUsedProvider != null) r.LastUsed = LastUsedProvider(r.Path); } catch { }
                try { if (PinnedProvider != null) r.Pinned = PinnedProvider(r.Path); } catch { }
            }
        }

        /// <summary>
        /// 后台来源返回后统一重排，而不是简单追加。这样精确匹配、固定项和常用项
        /// 不会因为来自注册表就永远排在一个较差的目录子串结果后面。
        /// </summary>
        void RenderMerged(List<SearchResult> main, List<SearchResult> extra,
            string query, bool final)
        {
            if (host == null) return;
            shown.Clear();
            resultCount = 0;
            host.Children.Clear();

            List<SearchResult> merged = SearchRanking.Merge(main, extra, query, MaxDisplay);
            AppendBatch(merged);
            if (final)
            {
                completed = true;
                if (resultCount == 0) ShowEmpty(query);
            }
            NotifyChanged();
        }

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

            TextBlock sub = Ui.T(ResultDetail(r), 10.5, Ui.Sub);
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
                    ImageSource src;
                    if (!AppIcons.TryGetCached(r.Path, out src))
                    {
                        Grid holder = new Grid { Width = 22, Height = 22 };
                        TextBlock wait = new TextBlock();
                        wait.Text = "\uE7C3";
                        wait.FontFamily = Ui.Mdl;
                        wait.FontSize = 14;
                        wait.Foreground = Ui.Sub;
                        wait.Opacity = 0.55;
                        wait.HorizontalAlignment = HorizontalAlignment.Center;
                        wait.VerticalAlignment = VerticalAlignment.Center;
                        holder.Children.Add(wait);
                        int ownGeneration = generation;
                        AppIcons.GetAsync(r.Path, delegate(ImageSource loaded)
                        {
                            try
                            {
                                host.Dispatcher.BeginInvoke(new Action(delegate
                                {
                                    if (ownGeneration != generation || loaded == null) return;
                                    holder.Children.Clear();
                                    holder.Children.Add(MakeIconImage(loaded));
                                }));
                            }
                            catch { }
                        });
                        return holder;
                    }
                    if (src != null)
                        return MakeIconImage(src);
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

        static Image MakeIconImage(ImageSource src)
        {
            Image img = new Image();
            img.Source = src;
            img.Width = 18;
            img.Height = 18;
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        void ShowEmpty(string query)
        {
            if (host == null || resultCount != 0) return;
            StackPanel empty = new StackPanel();
            empty.Margin = new Thickness(Ui.S.Xl, Ui.S.Xl + Ui.S.Sm, 0, 0);
            TextBlock icon = Ui.T("\uE721", 22, Ui.Sub);
            icon.FontFamily = Ui.Mdl;
            icon.Opacity = 0.55;
            empty.Children.Add(icon);
            TextBlock title = Ui.T("没有找到「" + (query ?? "") + "」", 13.5, Ui.Fg,
                FontWeights.SemiBold);
            title.Margin = new Thickness(0, Ui.S.Sm, 0, 0);
            empty.Children.Add(title);
            TextBlock hint = Ui.T("换个说法试试，或者用拼音首字母（如 wx → 微信）", 12, Ui.Sub);
            hint.Margin = new Thickness(0, Ui.S.Xs, 0, 0);
            empty.Children.Add(hint);
            host.Children.Add(empty);
        }

        void NotifyChanged()
        {
            try { if (ResultsChanged != null) ResultsChanged(); }
            catch { }
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

        static string ResultDetail(SearchResult r)
        {
            if (r == null) return "";
            string path = ShortPath(r);
            if (string.IsNullOrWhiteSpace(r.Source)) return path;
            if (string.IsNullOrWhiteSpace(path)
                || string.Equals(path, r.Source, StringComparison.OrdinalIgnoreCase))
                return r.Source;
            return r.Source + " · " + path;
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
                else if (AppPaths.IsShellPath(r.Path))
                {
                    ProcessStartInfo shell = new ProcessStartInfo("explorer.exe");
                    shell.Arguments = r.Path;
                    shell.UseShellExecute = true;
                    Process.Start(shell);
                    if (ResultOpened != null) ResultOpened(r.Path);
                }
                else
                {
                    if (File.Exists(r.Path))
                    {
                        Process.Start(new ProcessStartInfo(r.Path) { UseShellExecute = true });
                        if (ResultOpened != null) ResultOpened(r.Path);
                    }
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
