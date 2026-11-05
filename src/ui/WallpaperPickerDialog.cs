using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 「Wallpaper Engine 壁纸…」选择窗口：缩略图网格 + 搜索 + 类型标签。
    ///
    /// ── 只列可用类型 ──
    ///
    ///   · **video** → 复制 mp4，播放
    ///   · **scene** → 复制 preview.*，**仅静态预览**（素材在 scene.pkg 里读不出来）
    ///
    /// web / application 不列出。
    ///
    /// ── 缩略图异步加载 ──
    ///
    /// 每个缩略图单独一个后台线程解码 + `DecodePixelWidth` 限制尺寸，
    /// 解好再回界面线程贴上去。**打开窗口时不等任何图**。
    ///
    /// ── 安全 ──
    ///
    /// 所有路径都经 `WallpaperEngine.SafeCombine` 校验（拒绝 `..` / 绝对路径）。
    /// </summary>
    class WallpaperPickerDialog
    {
        readonly Window dlg;
        readonly string baseDir;
        readonly PanelWindow panel;
        readonly Action onPicked;
        readonly Settings settings;

        TextBox searchBox;
        TextBlock searchHint;
        WrapPanel grid;
        TextBlock statusText;

        /// <summary>全部扫描到的条目。</summary>
        List<WallpaperEngine.Item> all = new List<WallpaperEngine.Item>();

        /// <summary>用户选中的条目（null = 没选）。</summary>
        public WallpaperEngine.Item Picked { get; private set; }

        public WallpaperPickerDialog(Window owner, string baseDir, Settings settings, PanelWindow panel, Action onPicked)
        {
            this.baseDir = baseDir;
            this.settings = settings;
            this.panel = panel;
            this.onPicked = onPicked;

            dlg = new Window();
            dlg.Title = "四叶草 · Wallpaper Engine 壁纸";
            dlg.Width = 760;
            dlg.Height = 620;
            dlg.WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen;
            dlg.Owner = owner;
            dlg.Background = Ui.Bg;
            dlg.ResizeMode = ResizeMode.CanResize;
            // 不占任务栏（这是配置对话框，一闪而过）。
            // 配合 DialogChrome.Attach 去掉最小化按钮 ——
            // 否则最小化后任务栏没图标，窗口再也找不回来。
            dlg.ShowInTaskbar = false;
            dlg.MinWidth = 520;
            dlg.MinHeight = 420;

            BuildUi();
        }

        void BuildUi()
        {
            Grid layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition());   // 搜索
            layout.RowDefinitions.Add(new RowDefinition());   // 网格
            layout.RowDefinitions.Add(new RowDefinition());   // 底部
            layout.RowDefinitions[0].Height = GridLength.Auto;
            layout.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
            layout.RowDefinitions[2].Height = GridLength.Auto;

            // ---- 搜索 ----
            StackPanel top = new StackPanel();
            top.Orientation = Orientation.Vertical;
            top.Margin = new Thickness(Ui.S.Xl, Ui.S.Lg, Ui.S.Xl, Ui.S.Sm);

            Border searchWrap = new Border();
            searchWrap.CornerRadius = new CornerRadius(Ui.R.Sm);
            searchWrap.Background = Ui.SearchBg;
            searchWrap.BorderBrush = Ui.Line;
            searchWrap.BorderThickness = new Thickness(1);
            searchWrap.Height = 32;

            Grid sg = new Grid();
            searchBox = new TextBox();
            searchBox.Background = Brushes.Transparent;
            searchBox.BorderThickness = new Thickness(0);
            searchBox.Foreground = Ui.Fg;
            searchBox.CaretBrush = Ui.Fg;
            searchBox.FontSize = 12;
            searchBox.Padding = new Thickness(10, 0, 10, 0);
            searchBox.VerticalContentAlignment = VerticalAlignment.Center;
            searchBox.TextChanged += delegate
            {
                try
                {
                    if (searchHint != null)
                        searchHint.Visibility = searchBox.Text.Length == 0
                            ? Visibility.Visible : Visibility.Collapsed;
                    RebuildGrid();
                }
                catch (Exception ex) { Ui.LogError(ex, "搜索壁纸"); }
            };
            sg.Children.Add(searchBox);

            searchHint = Ui.T("搜索标题…", 12, Ui.Sub);
            searchHint.Margin = new Thickness(12, 0, 0, 0);
            searchHint.VerticalAlignment = VerticalAlignment.Center;
            searchHint.IsHitTestVisible = false;
            sg.Children.Add(searchHint);

            searchWrap.Child = sg;
            top.Children.Add(searchWrap);

            TextBlock note = Ui.T(
                "壁纸来自 Steam 创意工坊，版权归原作者所有，仅供本机个人使用；不随程序分发、不上传。",
                10, Ui.Sub);
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(0, 6, 0, 0);
            top.Children.Add(note);

            Grid.SetRow(top, 0);
            layout.Children.Add(top);

            // ---- 网格 ----
            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            sv.Margin = new Thickness(Ui.S.Xl, 0, Ui.S.Xl, 0);

            grid = new WrapPanel();
            grid.Orientation = Orientation.Horizontal;
            sv.Content = grid;

            Grid.SetRow(sv, 1);
            layout.Children.Add(sv);

            // ---- 底部 ----
            Grid footer = new Grid();
            footer.Margin = new Thickness(Ui.S.Xl, Ui.S.Sm, Ui.S.Xl, Ui.S.Lg);
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            footer.ColumnDefinitions[1].Width = GridLength.Auto;

            statusText = Ui.T("正在扫描…", 11, Ui.Sub);
            statusText.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(statusText, 0);
            footer.Children.Add(statusText);

            StackPanel btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;

            Border manual = Chip("手动选择目录…", false, delegate { PickManualDir(); });
            btns.Children.Add(manual);

            Border cancel = Chip("取消", false, delegate { dlg.Close(); });
            cancel.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
            btns.Children.Add(cancel);

            Grid.SetColumn(btns, 1);
            footer.Children.Add(btns);

            Grid.SetRow(footer, 2);
            layout.Children.Add(footer);

            dlg.Content = layout;

            dlg.PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { dlg.Close(); e.Handled = true; }
            };

            // 扫描放后台，不卡窗口打开
            dlg.Loaded += delegate { StartScan(null); };
        }

        // ========== 扫描 ==========

        void StartScan(string manualRoot)
        {
            try
            {
                statusText.Text = "正在扫描 Wallpaper Engine 壁纸…";

                System.Threading.Thread t = new System.Threading.Thread(delegate()
                {
                    WallpaperEngine.ScanResult r;
                    try
                    {
                        if (string.IsNullOrEmpty(manualRoot))
                        {
                            r = WallpaperEngine.ScanAuto();
                        }
                        else
                        {
                            List<string> roots = new List<string>();
                            roots.Add(manualRoot);
                            r = WallpaperEngine.Scan(roots);
                        }
                    }
                    catch (Exception ex)
                    {
                        r = new WallpaperEngine.ScanResult();
                        r.Error = ex.Message;
                    }

                    try
                    {
                        Application.Current.Dispatcher.BeginInvoke((Action)delegate
                        {
                            try
                            {
                                all = r.Items ?? new List<WallpaperEngine.Item>();
                                RebuildGrid();
                                UpdateStatus(r);
                            }
                            catch (Exception ex) { Ui.LogError(ex, "填充壁纸列表"); }
                        });
                    }
                    catch { }
                });
                t.IsBackground = true;
                t.Start();
            }
            catch (Exception ex) { Ui.LogError(ex, "扫描壁纸"); }
        }

        void UpdateStatus(WallpaperEngine.ScanResult r)
        {
            try
            {
                if (statusText == null) return;

                if (all.Count == 0)
                {
                    if (r != null && r.RootCount == 0)
                        statusText.Text = "没找到 Wallpaper Engine 壁纸目录。点「手动选择目录…」指定。";
                    else
                        statusText.Text = "这个目录里没有可用的壁纸（只支持 video / scene）。";
                }
                else
                {
                    int v = 0, s = 0;
                    foreach (WallpaperEngine.Item it in all)
                    {
                        if (it.IsScene) s++; else v++;
                    }
                    statusText.Text = "共 " + all.Count + " 个（视频 " + v + " · 场景 " + s + "）";
                }
            }
            catch { }
        }

        void PickManualDir()
        {
            try
            {
                using (System.Windows.Forms.FolderBrowserDialog fbd =
                    new System.Windows.Forms.FolderBrowserDialog())
                {
                    fbd.Description = "选择 Wallpaper Engine 壁纸目录"
                        + "（通常是 ...\\steamapps\\workshop\\content\\431960）";
                    if (fbd.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                    StartScan(fbd.SelectedPath);
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "手动选择壁纸目录"); }
        }

        // ========== 网格 ==========

        void RebuildGrid()
        {
            try
            {
                if (grid == null) return;
                grid.Children.Clear();

                string q = searchBox == null ? "" : (searchBox.Text ?? "").Trim();

                int shown = 0;
                foreach (WallpaperEngine.Item it in all)
                {
                    if (q.Length > 0)
                    {
                        bool hit = false;
                        try { hit = Pinyin.Matches(it.Title ?? "", q); }
                        catch { hit = false; }
                        if (!hit) continue;
                    }

                    grid.Children.Add(MakeCard(it));
                    shown++;
                }

                if (shown == 0 && all.Count > 0)
                {
                    TextBlock none = Ui.T("没有匹配的壁纸", 11.5, Ui.Sub);
                    none.Margin = new Thickness(0, Ui.S.Md, 0, 0);
                    grid.Children.Add(none);
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "重建壁纸网格"); }
        }

        FrameworkElement MakeCard(WallpaperEngine.Item it)
        {
            Border card = new Border();
            card.Width = 168;
            card.Margin = new Thickness(0, 0, Ui.S.Sm, Ui.S.Sm);
            card.CornerRadius = new CornerRadius(Ui.R.Sm);
            card.Background = Ui.SearchBg;
            card.BorderBrush = Ui.Line;
            card.BorderThickness = new Thickness(1);
            card.Cursor = Cursors.Hand;
            card.ToolTip = it.Title + "\n" + it.Dir;

            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Vertical;

            // ---- 缩略图（异步）----
            Grid thumbWrap = new Grid();
            thumbWrap.Height = 94;
            thumbWrap.Background = Brushes.Black;
            thumbWrap.ClipToBounds = true;

            TextBlock placeholder = Ui.T("加载中…", 10, Ui.Sub);
            placeholder.HorizontalAlignment = HorizontalAlignment.Center;
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            thumbWrap.Children.Add(placeholder);

            Image img = new Image();
            img.Stretch = Stretch.UniformToFill;
            img.Visibility = Visibility.Collapsed;
            thumbWrap.Children.Add(img);

            sp.Children.Add(thumbWrap);

            LoadThumbAsync(it, img, placeholder);

            // ---- 标题 ----
            TextBlock title = Ui.T(it.Title ?? "", 11, Ui.Fg);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Margin = new Thickness(6, 4, 6, 0);
            title.MaxHeight = 32;
            sp.Children.Add(title);

            // ---- 类型标签 ----
            StackPanel tags = new StackPanel();
            tags.Orientation = Orientation.Horizontal;
            tags.Margin = new Thickness(6, 2, 6, 6);

            TextBlock typeTag = Ui.T(it.TypeLabel, 9.5,
                it.IsScene ? Ui.Sub : Ui.Accent);
            tags.Children.Add(typeTag);

            if (it.LowResPreview)
            {
                TextBlock low = Ui.T("  · 预览图分辨率较低", 9.5, Ui.Sub);
                tags.Children.Add(low);
            }
            sp.Children.Add(tags);

            card.Child = sp;

            card.MouseEnter += delegate { card.BorderBrush = Ui.Accent; };
            card.MouseLeave += delegate { card.BorderBrush = Ui.Line; };
            card.MouseLeftButtonUp += delegate { Choose(it); };

            return card;
        }

        /// <summary>
        /// 后台解码缩略图，解好贴上去。
        ///
        /// 用 `DecodePixelWidth = 336`（卡片宽的 2 倍，够清晰又不吃内存）。
        /// </summary>
        void LoadThumbAsync(WallpaperEngine.Item it, Image target, TextBlock placeholder)
        {
            try
            {
                string path = it.PreviewFile;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    if (placeholder != null) placeholder.Text = "无预览图";
                    return;
                }

                System.Threading.Thread t = new System.Threading.Thread(delegate()
                {
                    BitmapImage bi = null;
                    try
                    {
                        bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.UriSource = new Uri(path, UriKind.Absolute);
                        bi.DecodePixelWidth = 336;
                        bi.EndInit();
                        bi.Freeze();
                    }
                    catch { bi = null; }

                    if (bi == null)
                    {
                        try
                        {
                            Application.Current.Dispatcher.BeginInvoke((Action)delegate
                            {
                                if (placeholder != null) placeholder.Text = "无法预览";
                            });
                        }
                        catch { }
                        return;
                    }

                    try
                    {
                        Application.Current.Dispatcher.BeginInvoke((Action)delegate
                        {
                            try
                            {
                                target.Source = bi;
                                target.Visibility = Visibility.Visible;
                                if (placeholder != null) placeholder.Visibility = Visibility.Collapsed;
                            }
                            catch { }
                        });
                    }
                    catch { }
                });
                t.IsBackground = true;
                t.Start();
            }
            catch { }
        }

        // ========== 选择 ==========

        /// <summary>
        /// 选中一个壁纸：**只保存引用，不复制任何文件**。
        ///
        /// 存 ID + 标题 + 类型 + 主文件相对路径，下次启动按 ID 重新定位。
        /// 好处：不占额外空间、WE 更新壁纸后自动用新版、用户取消订阅则静默失效。
        /// </summary>
        void Choose(WallpaperEngine.Item it)
        {
            try
            {
                if (it == null || settings == null) return;

                // ---- 确定主文件与相对路径 ----
                string mainFile = it.IsScene ? it.PreviewFile : it.MainFile;
                if (string.IsNullOrEmpty(mainFile) || !File.Exists(mainFile))
                {
                    MessageBox.Show(dlg, "这个壁纸的文件已不存在，无法使用。",
                        "四叶草 · 壁纸", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string rel = WallpaperEngine.RelativeTo(it.Dir, mainFile);
                if (string.IsNullOrEmpty(rel))
                {
                    MessageBox.Show(dlg, "无法确定壁纸文件的相对路径。",
                        "四叶草 · 壁纸", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // ---- 保存引用 ----
                settings.WeWallpaperId = it.Id;
                settings.WeWallpaperTitle = it.Title;
                settings.WeWallpaperType = it.Type;
                settings.WeWallpaperFile = rel;
                settings.GlassMode = "we";
                settings.Save();

                Ui.Log("选用 WE 壁纸: " + it.Id + " \"" + it.Title + "\" [" + it.Type + "] "
                    + rel + "（引用，未复制）");

                // ★ 先真正应用，再提示成功。
                // 以前先弹“已选用”，用户点确定后才执行 onPicked；
                // 即使后续应用失败，也已经看到成功提示，造成假成功。
                Picked = it;
                if (onPicked != null) onPicked();   // 失败会抛到下面 catch，显示失败提示

                MessageBox.Show(dlg,
                    "壁纸已应用：\r\n\r\n"
                    + "  " + it.Title + "\r\n"
                    + "  类型：" + it.TypeLabel + "\r\n"
                    + "  文件：" + rel + "\r\n\r\n"
                    + "只记录引用，没有复制文件。"
                    + "壁纸被删除或取消订阅时会自动回退到内置背景。",
                    "四叶草 · 壁纸已应用",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                dlg.Close();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "选择壁纸");
                MessageBox.Show(dlg, "选择失败：" + ex.Message,
                    "四叶草 · 壁纸", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ========== 小工具 ==========

        static Border Chip(string label, bool primary, Action onClick)
        {
            Border b = new Border();
            b.Height = 28;
            b.CornerRadius = new CornerRadius(b.Height / 2);
            b.Padding = new Thickness(14, 0, 14, 0);
            b.Cursor = Cursors.Hand;
            b.Background = primary ? Ui.Accent : Ui.SearchBg;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(primary ? 0 : 1);

            TextBlock t = Ui.T(label, 11.5, primary ? Brushes.White : Ui.Fg,
                primary ? FontWeights.SemiBold : FontWeights.Normal);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            b.MouseEnter += delegate { b.Opacity = 0.85; };
            b.MouseLeave += delegate { b.Opacity = 1.0; };
            b.MouseLeftButtonUp += delegate
            {
                try { if (onClick != null) onClick(); }
                catch (Exception ex) { Ui.LogError(ex, "壁纸窗口按钮"); }
            };
            return b;
        }

        public void Show()
        {
            try { DialogChrome.Attach(dlg); dlg.ShowDialog(); }
            catch (Exception ex) { Ui.LogError(ex, "显示壁纸选择窗口"); }
        }
    }
}
