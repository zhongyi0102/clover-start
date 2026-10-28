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

                // ---- 第一级：Instant（同步，很快）----
                List<SearchResult> instant = SystemSearch.Instant(query);
                AppendBatch(instant, true);

                if (instant.Count == 0)
                {
                    // 没有任何即时结果时给个提示，避免用户以为"没反应"
                    AddStatusLine("正在搜索更多位置…");
                }

                // ---- 第二级：Deep（后台流式）----
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

        /// <summary>追加一批结果（去重、限量）。</summary>
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
                    host.Children.Add(MakeRow(r));
                }

                // Instant 阶段没有结果时，先不显示"无结果"——Deep 可能还有
                if (isInstant && shown.Count == 0) return;
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
        /// 一行搜索结果：图标 + 名字 + 来源/路径。
        ///
        /// 图标：文件用系统图标，文件夹用文件夹图标。
        /// 用 SHGetFileInfo 太重（每次几十 ms），这里用简化策略：
        /// exe/lnk 用 AppIcons（已有缓存），其他用文字标签。
        /// </summary>
        Border MakeRow(SearchResult r)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(Ui.R.Sm);
            b.Margin = new Thickness(0, 1, 0, 1);
            b.Padding = new Thickness(Ui.S.Sm, 6, Ui.S.Sm, 6);
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;
            b.Tag = r;

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = new GridLength(28);
            g.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);

            // 图标位
            FrameworkElement ico = MakeIcon(r);
            ico.HorizontalAlignment = HorizontalAlignment.Left;
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

                MenuItem loc = new MenuItem();
                loc.Header = "打开所在位置";
                loc.Click += delegate
                {
                    try
                    {
                        string dir = r.IsFolder ? Path.GetDirectoryName(r.Path) : Path.GetDirectoryName(r.Path);
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
                    try { Clipboard.SetText(r.Path); } catch { }
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
