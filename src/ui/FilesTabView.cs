using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Clover
{
    /// <summary>
    /// 「文件」标签的界面：来源胶囊行 + 排序行 + 文件列表 + 右键菜单 + 多选 + 键盘。
    ///
    /// **只读浏览与打开**。不移动、不删除、不重命名、不改内容、不递归、
    /// 不监视、不读文件内容、不做缩略图、不做全盘搜索。
    ///
    /// 数据来源全部是 FileFolders（用户手动添加），不内置桌面或任何固定来源。
    ///
    /// ── 键盘与鼠标 ──
    ///
    ///   · ↑↓        在当前列表里移动选中项
    ///   · Enter     打开选中项（文件夹 → 资源管理器；文件 → 默认程序）
    ///   · Esc       清空选择
    ///   · Ctrl+A    全选
    ///   · Ctrl+点击  切换单项选中
    ///   · Shift+点击 范围选中
    ///   · Ctrl+C    把选中的文件放进剪贴板（FileDrop，只读）
    ///   · 拖出       DoDragDrop(Copy) —— 只允许复制，不允许移动
    ///
    /// **拖动排序只对应用磁贴有效**：文件列表行不参与 tilePanel 的拖动换位逻辑。
    /// </summary>
    class FilesTabView
    {
        readonly PanelWindow owner;
        readonly Settings settings;
        readonly string baseDir;

        readonly StackPanel chipRow;
        readonly StackPanel listPanel;
        readonly TextBlock noticeText;
        readonly StackPanel sortRow;

        FileFolders folders;

        string currentSource = "";
        FileScanner.SortKey sort = FileScanner.SortKey.Modified;

        DateTime lastLoad = DateTime.MinValue;
        FileScanner.Result cached;

        string filterQuery = "";

        /// <summary>当前显示的条目（过滤后）。</summary>
        List<FileScanner.Entry> visible = new List<FileScanner.Entry>();

        /// <summary>选中的条目路径集合（大小写不敏感）。</summary>
        readonly HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>上次点击的索引（Shift 范围选择用）。</summary>
        int anchorIndex = -1;

        /// <summary>行控件缓存：路径 → Border（重绘选中态用，不必重建整列表）。</summary>
        readonly Dictionary<string, Border> rowMap =
            new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);

        /// <summary>图标缓存：扩展名 → ImageSource（null = 用字形兜底）。</summary>
        readonly Dictionary<string, ImageSource> iconByExt =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);


        public FilesTabView(PanelWindow owner, Settings settings, string baseDir,
            StackPanel chipHost, StackPanel listHost, TextBlock noticeHost, StackPanel sortHost)
        {
            this.owner = owner;
            this.settings = settings;
            this.baseDir = baseDir;
            this.chipRow = chipHost;
            this.listPanel = listHost;
            this.noticeText = noticeHost;
            this.sortRow = sortHost;

            this.folders = FileFolders.Load(baseDir);
            this.currentSource = settings == null ? "" : (settings.FilesSource ?? "");
            this.sort = ParseSort(settings == null ? "" : settings.FilesSort);
        }

        // ========== 对外接口 ==========

        public void ReloadFolders()
        {
            try
            {
                folders = FileFolders.Load(baseDir);
                NormalizeSource();
            }
            catch (Exception ex) { Ui.LogError(ex, "重读文件夹清单"); }
        }

        /// <summary>刷新整个视图。force=false 时 30 秒内复用缓存。</summary>
        public void Refresh(bool force)
        {
            try
            {
                BuildChips();
                BuildSortRow();

                if (!force && cached != null
                    && (DateTime.Now - lastLoad).TotalSeconds < 30)
                {
                    RenderList(cached);
                    return;
                }

                LoadAndRender();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "刷新文件标签");
                SetNotice("加载失败：" + ex.Message);
            }
        }

        /// <summary>搜索框文字变化：只过滤当前来源的文件名。</summary>
        public void ApplyFilter(string query)
        {
            try
            {
                filterQuery = query ?? "";
                if (cached != null) RenderList(cached);
            }
            catch (Exception ex) { Ui.LogError(ex, "过滤文件列表"); }
        }
        /// <summary>当前生效的来源路径（测试用；也是"修正后"的真实值）。</summary>
        public string CurrentSourcePath()
        {
            try { return currentSource ?? ""; }
            catch { return ""; }
        }


        public bool HasSources
        {
            get { return folders != null && folders.Items.Count > 0; }
        }

        // ========== 键盘 / 剪贴板 / 拖出（供 PanelWindow 路由）==========

        /// <summary>
        /// 处理键盘。返回 true 表示已消费（调用方不要再往下传）。
        /// </summary>
        public bool HandleKey(Key key, ModifierKeys mods)
        {
            try
            {
                if (visible.Count == 0) return false;

                // Ctrl+A 全选
                if (key == Key.A && (mods & ModifierKeys.Control) != 0)
                {
                    selected.Clear();
                    foreach (FileScanner.Entry e in visible) selected.Add(e.Path);
                    UpdateSelectionVisuals();
                    return true;
                }

                // Ctrl+C 复制到剪贴板（FileDrop，只读）
                if (key == Key.C && (mods & ModifierKeys.Control) != 0)
                {
                    CopySelectedToClipboard();
                    return true;
                }

                // F5 刷新
                if (key == Key.F5)
                {
                    Refresh(true);
                    return true;
                }

                // Esc 清空选择
                if (key == Key.Escape)
                {
                    if (selected.Count == 0) return false;   // 没选择时交给上层（关闭面板）
                    selected.Clear();
                    anchorIndex = -1;
                    UpdateSelectionVisuals();
                    return true;
                }

                // Enter 打开
                if (key == Key.Enter)
                {
                    FileScanner.Entry e = FirstSelected();
                    if (e != null)
                    {
                        if (e.IsFolder) OpenInExplorer(e.Path);
                        else OpenFile(e.Path);
                        return true;
                    }
                    return false;
                }

                // ↑↓ 移动选中
                if (key == Key.Up || key == Key.Down)
                {
                    MoveSelection(key == Key.Up ? -1 : 1);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "文件标签键盘处理");
                return false;
            }
        }

        /// <summary>把选中的文件放进剪贴板（FileDrop）。只读 —— 不移动任何文件。</summary>
        public void CopySelectedToClipboard()
        {
            try
            {
                List<string> paths = SelectedFilePaths();
                if (paths.Count == 0) return;

                System.Collections.Specialized.StringCollection sc =
                    new System.Collections.Specialized.StringCollection();
                foreach (string p in paths) sc.Add(p);

                System.Windows.DataObject data = new System.Windows.DataObject();
                data.SetFileDropList(sc);
                Clipboard.SetDataObject(data, true);

                SetNotice("已复制 " + paths.Count + " 个文件到剪贴板");
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "复制文件到剪贴板");
                SetNotice("复制失败：" + ex.Message);
            }
        }

        // ========== 来源胶囊行 ==========

        void BuildChips()
        {
            try
            {
                if (chipRow == null) return;
                chipRow.Children.Clear();

                if (folders == null || folders.Items.Count == 0)
                {
                    chipRow.Children.Add(MakeChipButton("+ 添加文件夹", delegate { PromptAddFolder(); }, true));
                    return;
                }

                foreach (FileFolders.Folder f in folders.Items)
                {
                    if (f == null) continue;
                    bool available = FileFolders.IsAvailable(f.Path);
                    bool isCurrent = IsCurrent(f.Path);
                    chipRow.Children.Add(MakeChip(f, available, isCurrent));
                }

                chipRow.Children.Add(MakeChipButton("+", delegate { PromptAddFolder(); }, false));
            }
            catch (Exception ex) { Ui.LogError(ex, "构建来源胶囊"); }
        }

        Border MakeChip(FileFolders.Folder f, bool available, bool isSelected)
        {
            Border b = new Border();
            b.Height = 28;
            b.CornerRadius = new CornerRadius(b.Height / 2);
            b.Padding = new Thickness(12, 0, 12, 0);
            b.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
            b.Cursor = Cursors.Hand;
            b.Background = isSelected ? Ui.Accent : Ui.SearchBg;
            b.BorderBrush = isSelected ? Ui.Accent : Ui.Line;
            b.BorderThickness = new Thickness(1);
            b.Tag = f.Path;

            // 失效来源：灰显 + 明确提示「不可用」
            if (!available) b.Opacity = 0.45;

            string label = f.Name ?? "";
            if (!available) label += "（不可用）";

            TextBlock t = Ui.T(label, 11.5, isSelected ? Brushes.White : Ui.Fg);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            b.ToolTip = available
                ? f.Path
                : (f.Path + "\n\n不可用：目录可能已删除，或外置盘未插入。\n右键可以移除这条记录。");

            b.MouseLeftButtonUp += delegate
            {
                try
                {
                    currentSource = f.Path;
                    SaveSource();
                    selected.Clear();
                    anchorIndex = -1;
                    Refresh(true);
                }
                catch (Exception ex) { Ui.LogError(ex, "切换文件来源"); }
            };

            b.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                ShowChipMenu(f, b);
            };

            return b;
        }

        Border MakeChipButton(string label, Action onClick, bool wide)
        {
            Border b = new Border();
            b.Height = 28;
            b.CornerRadius = new CornerRadius(b.Height / 2);
            b.Padding = new Thickness(wide ? 12 : 10, 0, wide ? 12 : 10, 0);
            b.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
            b.Cursor = Cursors.Hand;
            b.Background = Ui.SearchBg;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(1);

            TextBlock t = Ui.T(label, 11.5, Ui.Sub);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = Ui.SearchBg; };
            b.MouseLeftButtonUp += delegate { if (onClick != null) onClick(); };
            return b;
        }

        void ShowChipMenu(FileFolders.Folder f, Border chip)
        {
            try
            {
                ContextMenu cm = new ContextMenu();

                MenuItem open = new MenuItem();
                open.Header = "在资源管理器中打开";
                open.IsEnabled = FileFolders.IsAvailable(f.Path);
                open.Click += delegate { OpenInExplorer(f.Path); };
                cm.Items.Add(open);

                cm.Items.Add(new Separator());

                MenuItem rename = new MenuItem();
                rename.Header = "重命名显示名…";
                rename.Click += delegate { PromptRename(f); };
                cm.Items.Add(rename);

                MenuItem remove = new MenuItem();
                remove.Header = "移除";
                remove.ToolTip = "只从列表里移除，不会删除磁盘上的文件夹";
                remove.Click += delegate { ConfirmRemove(f); };
                cm.Items.Add(remove);

                cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                cm.IsOpen = true;
            }
            catch (Exception ex) { Ui.LogError(ex, "来源右键菜单"); }
        }

        // ========== 排序行 ==========

        void BuildSortRow()
        {
            try
            {
                if (sortRow == null) return;
                sortRow.Children.Clear();

                AddSortButton("修改时间", FileScanner.SortKey.Modified);
                AddSortButton("名称", FileScanner.SortKey.Name);
                AddSortButton("大小", FileScanner.SortKey.Size);
                AddSortButton("类型", FileScanner.SortKey.Type);

                Border refresh = MakeChipButton("刷新 (F5)", delegate { Refresh(true); }, false);
                refresh.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
                sortRow.Children.Add(refresh);
            }
            catch (Exception ex) { Ui.LogError(ex, "构建排序行"); }
        }

        void AddSortButton(string label, FileScanner.SortKey key)
        {
            bool on = (sort == key);
            Border b = new Border();
            b.Height = 24;
            b.CornerRadius = new CornerRadius(b.Height / 2);
            b.Padding = new Thickness(10, 0, 10, 0);
            b.Margin = new Thickness(0, 0, Ui.S.Xs, 0);
            b.Cursor = Cursors.Hand;
            b.Background = on ? Ui.Accent : Brushes.Transparent;
            b.BorderBrush = on ? Ui.Accent : Ui.Line;
            b.BorderThickness = new Thickness(1);

            TextBlock t = Ui.T(label, 10.5, on ? Brushes.White : Ui.Sub);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            b.MouseLeftButtonUp += delegate
            {
                try
                {
                    sort = key;
                    SaveSort();
                    Refresh(true);
                }
                catch (Exception ex) { Ui.LogError(ex, "切换排序"); }
            };

            sortRow.Children.Add(b);
        }

        // ========== 列表 ==========

        void LoadAndRender()
        {
            try
            {
                if (!HasSources)
                {
                    if (listPanel != null) listPanel.Children.Clear();
                    visible.Clear();
                    rowMap.Clear();
                    selected.Clear();
                    SetNotice("点上面的「+」添加要读取的文件夹");
                    cached = null;
                    return;
                }

                NormalizeSource();

                if (string.IsNullOrEmpty(currentSource))
                {
                    if (listPanel != null) listPanel.Children.Clear();
                    visible.Clear();
                    rowMap.Clear();
                    SetNotice("点上面的「+」添加要读取的文件夹");
                    cached = null;
                    return;
                }

                // ---- 读取中提示 ----
                SetNotice("正在读取…");

                string src = currentSource;
                FileScanner.SortKey sk = sort;

                FileScanner.Result r = null;
                System.Threading.Thread t = new System.Threading.Thread(delegate()
                {
                    try { r = FileScanner.List(src, sk); }
                    catch (Exception ex)
                    {
                        r = new FileScanner.Result();
                        r.Error = "读取失败: " + ex.Message;
                    }
                });
                t.IsBackground = true;
                t.Start();
                t.Join(5000);


                if (r == null)
                {
                    r = new FileScanner.Result();
                    r.Error = "读取超时（目录可能过大）";
                }

                cached = r;
                lastLoad = DateTime.Now;
                RenderList(r);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "读取文件目录");
                SetNotice("读取失败：" + ex.Message);
            }
        }

        void RenderList(FileScanner.Result r)
        {
            try
            {
                if (listPanel == null) return;
                listPanel.Children.Clear();
                rowMap.Clear();

                if (r == null)
                {
                    SetNotice(null);
                    visible.Clear();
                    return;
                }

                if (!string.IsNullOrEmpty(r.Error))
                {
                    SetNotice(r.Error);
                    visible.Clear();
                    return;
                }

                List<FileScanner.Entry> items = FileScanner.Filter(r.Items, filterQuery);

                if (items.Count == 0)
                {
                    visible.Clear();
                    SetNotice(filterQuery.Length > 0
                        ? "没有匹配「" + filterQuery + "」的文件"
                        : "这个文件夹是空的");
                    return;
                }

                visible = items;

                // 清掉已经不存在的选中项
                PruneSelection();

                SetNotice(r.Notice);

                int shown = 0;
                foreach (FileScanner.Entry e in items)
                {
                    if (shown >= FileScanner.MaxReturn) break;
                    Border row = MakeRow(e, shown);
                    if (row != null)
                    {
                        listPanel.Children.Add(row);
                        rowMap[e.Path] = row;
                        shown++;
                    }
                }

                UpdateSelectionVisuals();
            }
            catch (Exception ex) { Ui.LogError(ex, "渲染文件列表"); }
        }

        /// <summary>一行：图标 + 名称 + 时间 + 大小。</summary>
        Border MakeRow(FileScanner.Entry e, int index)
        {
            try
            {
                Border b = new Border();
                b.Height = 44;
                b.CornerRadius = new CornerRadius(Ui.R.Sm);
                b.Padding = new Thickness(Ui.S.Sm, 0, Ui.S.Sm, 0);
                b.Margin = new Thickness(0, 0, 0, 2);
                b.Background = Brushes.Transparent;
                b.Cursor = Cursors.Hand;
                b.Tag = e;
                b.Focusable = true;

                Grid g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions[0].Width = new GridLength(32);
                g.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                g.ColumnDefinitions[2].Width = new GridLength(110);
                g.ColumnDefinitions[3].Width = new GridLength(76);

                // ---- 图标 ----
                //
                // 策略：按扩展名缓存。
                //   · .exe / .ico / .msi 等自带图标的 → 走 IconCache（同步取，约 1-3ms）
                //   · 其余（.txt / .pdf / 文件夹）→ 用 MDL2 字形（零成本）
                // 首次取到的 ImageSource 存进 iconByExt，同扩展名后续直接复用。
                FrameworkElement icon = MakeIcon(e);
                Grid.SetColumn(icon, 0);
                g.Children.Add(icon);

                TextBlock name = Ui.T(e.Name, 12.5, Ui.Fg);
                name.VerticalAlignment = VerticalAlignment.Center;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.ToolTip = e.Path;
                Grid.SetColumn(name, 1);
                g.Children.Add(name);

                TextBlock time = Ui.T(FileScanner.FormatTime(e.Modified), 11, Ui.Sub);
                time.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(time, 2);
                g.Children.Add(time);

                TextBlock size = Ui.T(e.IsFolder ? "" : FileScanner.FormatSize(e.Size), 11, Ui.Sub);
                size.VerticalAlignment = VerticalAlignment.Center;
                size.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(size, 3);
                g.Children.Add(size);

                b.Child = g;

                b.MouseEnter += delegate
                {
                    if (!selected.Contains(e.Path)) b.Background = Ui.Hover;
                };
                b.MouseLeave += delegate
                {
                    if (!selected.Contains(e.Path)) b.Background = Brushes.Transparent;
                };

                b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs ev)
                {
                    HandleRowClick(e, index, ev);
                };

                b.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs ev)
                {
                    ev.Handled = true;
                    // 右键时如果这项没被选中，先只选它（资源管理器行为）
                    if (!selected.Contains(e.Path))
                    {
                        selected.Clear();
                        selected.Add(e.Path);
                        anchorIndex = index;
                        UpdateSelectionVisuals();
                    }
                    ShowItemMenu(e);
                };

                // ---- 拖出（只允许复制）----
                b.PreviewMouseMove += delegate(object s, MouseEventArgs ev)
                {
                    try
                    {
                        if (ev.LeftButton != MouseButtonState.Pressed) return;
                        if (!selected.Contains(e.Path)) return;
                        if (selected.Count == 0) return;

                        List<string> paths = SelectedFilePaths();
                        if (paths.Count == 0) return;

                        System.Collections.Specialized.StringCollection sc =
                            new System.Collections.Specialized.StringCollection();
                        foreach (string p in paths) sc.Add(p);

                        System.Windows.DataObject data = new System.Windows.DataObject();
                        data.SetFileDropList(sc);

                        // ★ 只允许 Copy：拖到资源管理器是"复制"，源文件不动
                        DragDrop.DoDragDrop(b, data, DragDropEffects.Copy);
                    }
                    catch (Exception ex) { Ui.LogError(ex, "拖出文件"); }
                };

                return b;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "创建文件列表行");
                return null;
            }
        }

        /// <summary>按扩展名缓存图标；取不到就用字形。</summary>
        FrameworkElement MakeIcon(FileScanner.Entry e)
        {
            // 文件夹：固定字形
            if (e.IsFolder)
            {
                TextBlock f = new TextBlock();
                f.FontFamily = Ui.Mdl;
                f.FontSize = 16;
                f.Text = "\uE8B7";
                f.Foreground = Ui.Accent;
                f.VerticalAlignment = VerticalAlignment.Center;
                f.HorizontalAlignment = HorizontalAlignment.Center;
                return f;
            }

            // 自带图标的扩展名才走 IconCache（其余用字形，零成本）
            string ext = e.Ext ?? "";
            bool hasOwnIcon = ext == ".exe" || ext == ".ico" || ext == ".msi"
                || ext == ".url" || ext == ".appref-ms";

            if (hasOwnIcon)
            {
                ImageSource img = null;
                if (!iconByExt.TryGetValue(ext, out img))
                {
                    // 该扩展名第一次出现：真的去取一次
                    try { img = IconCache.Get(e.Path); }
                    catch { img = null; }
                    iconByExt[ext] = img;   // 存 null 也存 —— 表示"取过了，没有"
                }

                if (img != null)
                {
                    Image im = new Image();
                    im.Source = img;
                    im.Width = 20;
                    im.Height = 20;
                    im.Stretch = Stretch.Uniform;
                    im.VerticalAlignment = VerticalAlignment.Center;
                    im.HorizontalAlignment = HorizontalAlignment.Center;
                    return im;
                }
            }

            // 兜底：字形
            TextBlock t = new TextBlock();
            t.FontFamily = Ui.Mdl;
            t.FontSize = 16;
            t.Text = "\uE7C3";
            t.Foreground = Ui.Sub;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            return t;
        }

        // ========== 选择 ==========

        void HandleRowClick(FileScanner.Entry e, int index, MouseButtonEventArgs ev)
        {
            try
            {
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

                if (shift && anchorIndex >= 0 && anchorIndex < visible.Count)
                {
                    // 范围选中
                    int a = Math.Min(anchorIndex, index);
                    int z = Math.Max(anchorIndex, index);
                    if (!ctrl) selected.Clear();
                    for (int i = a; i <= z && i < visible.Count; i++)
                        selected.Add(visible[i].Path);
                }
                else if (ctrl)
                {
                    // 切换单项
                    if (!selected.Remove(e.Path)) selected.Add(e.Path);
                    anchorIndex = index;
                }
                else
                {
                    // 单选
                    selected.Clear();
                    selected.Add(e.Path);
                    anchorIndex = index;
                }

                UpdateSelectionVisuals();
            }
            catch (Exception ex) { Ui.LogError(ex, "文件行点击"); }
        }

        void MoveSelection(int delta)
        {
            try
            {
                if (visible.Count == 0) return;

                int cur = -1;
                if (anchorIndex >= 0 && anchorIndex < visible.Count) cur = anchorIndex;

                int next = cur < 0 ? 0 : Math.Max(0, Math.Min(visible.Count - 1, cur + delta));

                selected.Clear();
                selected.Add(visible[next].Path);
                anchorIndex = next;
                UpdateSelectionVisuals();

                // 滚到可见
                Border row;
                if (rowMap.TryGetValue(visible[next].Path, out row) && row != null)
                    row.BringIntoView();
            }
            catch (Exception ex) { Ui.LogError(ex, "移动选择"); }
        }

        void UpdateSelectionVisuals()
        {
            try
            {
                foreach (KeyValuePair<string, Border> kv in rowMap)
                {
                    if (kv.Value == null) continue;
                    bool on = selected.Contains(kv.Key);
                    kv.Value.Background = on ? Ui.AccentSoft : Brushes.Transparent;
                }
            }
            catch { }
        }

        void PruneSelection()
        {
            try
            {
                if (selected.Count == 0) return;
                HashSet<string> alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FileScanner.Entry e in visible) alive.Add(e.Path);

                List<string> dead = new List<string>();
                foreach (string p in selected) if (!alive.Contains(p)) dead.Add(p);
                foreach (string p in dead) selected.Remove(p);
            }
            catch { }
        }

        FileScanner.Entry FirstSelected()
        {
            try
            {
                foreach (FileScanner.Entry e in visible)
                    if (selected.Contains(e.Path)) return e;
            }
            catch { }
            return null;
        }

        /// <summary>选中项里的文件路径（**不含文件夹** —— FileDrop 只放文件）。</summary>
        List<string> SelectedFilePaths()
        {
            List<string> list = new List<string>();
            try
            {
                foreach (FileScanner.Entry e in visible)
                {
                    if (!selected.Contains(e.Path)) continue;
                    if (e.IsFolder) continue;
                    if (!File.Exists(e.Path)) continue;
                    list.Add(e.Path);
                }
            }
            catch { }
            return list;
        }

        // ========== 右键菜单 ==========

        void ShowItemMenu(FileScanner.Entry e)
        {
            try
            {
                ContextMenu cm = new ContextMenu();

                MenuItem open = new MenuItem();
                open.Header = "打开";
                open.Click += delegate
                {
                    if (e.IsFolder) OpenInExplorer(e.Path);
                    else OpenFile(e.Path);
                };
                cm.Items.Add(open);

                // 打开方式…：只对文件（文件夹没有"打开方式"）
                if (!e.IsFolder)
                {
                    MenuItem openWith = new MenuItem();
                    openWith.Header = "打开方式…";
                    openWith.Click += delegate { OpenWith(e.Path); };
                    cm.Items.Add(openWith);
                }

                MenuItem reveal = new MenuItem();
                reveal.Header = "在资源管理器中显示";
                reveal.Click += delegate { RevealInExplorer(e.Path); };
                cm.Items.Add(reveal);

                cm.Items.Add(new Separator());

                MenuItem copy = new MenuItem();
                copy.Header = "复制路径";
                copy.Click += delegate { CopyText(e.Path); };
                cm.Items.Add(copy);

                cm.Items.Add(new Separator());

                MenuItem props = new MenuItem();
                props.Header = "属性";
                props.Click += delegate { ShowProperties(e.Path); };
                cm.Items.Add(props);

                cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                cm.IsOpen = true;
            }
            catch (Exception ex) { Ui.LogError(ex, "文件右键菜单"); }
        }

        // ========== 操作 ==========

        void PromptAddFolder()
        {
            try
            {
                if (folders == null) folders = FileFolders.Load(baseDir);

                if (folders.Items.Count >= FileFolders.MaxFolders)
                {
                    MessageBox.Show(
                        "最多添加 " + FileFolders.MaxFolders + " 个文件夹。\r\n请先移除一些再添加。",
                        "四叶草 · 文件", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string picked = PickFolder();
                if (string.IsNullOrEmpty(picked)) return;

                FileFolders.AddResult r = folders.Add(picked);
                if (r != FileFolders.AddResult.Ok)
                {
                    MessageBox.Show(AddResultText(r, picked), "四叶草 · 文件",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                NormalizeSource();
                if (folders.Items.Count > 0)
                    currentSource = folders.Items[folders.Items.Count - 1].Path;
                SaveSource();

                selected.Clear();
                anchorIndex = -1;
                Refresh(true);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "添加文件夹");
                MessageBox.Show("添加失败：" + ex.Message, "四叶草 · 文件");
            }
        }

        /// <summary>弹系统自带的选目录对话框，**默认从桌面位置打开**。</summary>
        string PickFolder()
        {
            try
            {
                using (System.Windows.Forms.FolderBrowserDialog dlg =
                    new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "选择要显示在「文件」标签里的文件夹";
                    dlg.ShowNewFolderButton = false;

                    // 默认从桌面位置打开（用户最常从桌面选）
                    try
                    {
                        string desktop = Environment.GetFolderPath(
                            Environment.SpecialFolder.Desktop);
                        if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
                            dlg.SelectedPath = desktop;
                    }
                    catch { }

                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
                    return dlg.SelectedPath;
                }
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "选目录对话框");
                MessageBox.Show("打不开选目录对话框：" + ex.Message, "四叶草 · 文件");
                return null;
            }
        }

        static string AddResultText(FileFolders.AddResult r, string path)
        {
            switch (r)
            {
                case FileFolders.AddResult.NotExist:
                    return "目录不存在：\r\n" + path;
                case FileFolders.AddResult.NotDirectory:
                    return "这是文件，不是文件夹：\r\n" + path;
                case FileFolders.AddResult.NetworkPath:
                    return "不支持网络路径（UNC 或映射的网络盘）。\r\n请选择本地磁盘上的文件夹。";
                case FileFolders.AddResult.Duplicate:
                    return "这个文件夹已经添加过了。";
                case FileFolders.AddResult.TooMany:
                    return "最多添加 " + FileFolders.MaxFolders + " 个文件夹。";
                case FileFolders.AddResult.InvalidPath:
                    return "路径无效：\r\n" + path;
                default:
                    return "添加失败。";
            }
        }

        void PromptRename(FileFolders.Folder f)
        {
            try
            {
                string input = TextPromptDialog.Ask(
                    owner, "重命名文件夹", "显示名（留空恢复默认）：", f.Name);
                if (input == null) return;

                if (!folders.Rename(f.Path, input))
                {
                    MessageBox.Show("重命名失败。", "四叶草 · 文件");
                    return;
                }
                Refresh(true);
            }
            catch (Exception ex) { Ui.LogError(ex, "重命名文件夹"); }
        }

        void ConfirmRemove(FileFolders.Folder f)
        {
            try
            {
                MessageBoxResult r = MessageBox.Show(
                    "从列表里移除「" + (f.Name ?? "") + "」？\r\n\r\n"
                    + "只移除记录，不会删除磁盘上的文件夹和文件。\r\n"
                    + f.Path,
                    "四叶草 · 文件",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question);

                if (r != MessageBoxResult.OK) return;

                if (!folders.Remove(f.Path))
                {
                    MessageBox.Show("移除失败。", "四叶草 · 文件");
                    return;
                }

                if (string.Equals(currentSource, f.Path, StringComparison.OrdinalIgnoreCase))
                {
                    currentSource = "";
                    NormalizeSource();
                    SaveSource();
                }

                selected.Clear();
                anchorIndex = -1;
                Refresh(true);
            }
            catch (Exception ex) { Ui.LogError(ex, "移除文件夹"); }
        }

        // ========== 系统操作（全部只读）==========

        void OpenInExplorer(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    MessageBox.Show("目录不存在：\r\n" + path, "四叶草 · 文件");
                    return;
                }
                System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex) { Ui.LogError(ex, "打开文件夹"); }
        }

        void OpenFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show("文件不存在：\r\n" + path, "四叶草 · 文件");
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "打开文件");
                MessageBox.Show("打不开这个文件：\r\n" + ex.Message, "四叶草 · 文件");
            }
        }

        /// <summary>打开方式…：拉起系统的「打开方式」对话框。</summary>
        void OpenWith(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                // rundll32 shell32.dll,OpenAs_RunDLL <path> 是系统自带的"打开方式"对话框
                System.Diagnostics.Process.Start("rundll32.exe",
                    "shell32.dll,OpenAs_RunDLL \"" + path + "\"");
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "打开方式");
                MessageBox.Show("打不开「打开方式」对话框：\r\n" + ex.Message, "四叶草 · 文件");
            }
        }

        void RevealInExplorer(string path)
        {
            try
            {
                if (File.Exists(path))
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (Directory.Exists(path))
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex) { Ui.LogError(ex, "在资源管理器中显示"); }
        }

        /// <summary>属性：拉起系统的文件属性对话框。</summary>
        void ShowProperties(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return;
                // 属性对话框需要知道"调用方的所有者窗口句柄"，否则会飘在桌面
                IntPtr ownerHwnd = IntPtr.Zero;
                try
                {
                    if (owner != null)
                        ownerHwnd = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
                }
                catch { }

                Native.ShowFileProperties(path, ownerHwnd);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "显示属性");
                MessageBox.Show("打不开属性对话框：\r\n" + ex.Message, "四叶草 · 文件");
            }
        }

        void CopyText(string text)
        {
            try { Clipboard.SetText(text ?? ""); }
            catch (Exception ex) { Ui.LogError(ex, "复制路径"); }
        }

        // ========== 状态维护 ==========

        bool IsCurrent(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(currentSource))
                    return folders != null && folders.Items.Count > 0
                        && folders.Items[0] != null
                        && string.Equals(folders.Items[0].Path, path, StringComparison.OrdinalIgnoreCase);

                return string.Equals(currentSource, path, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>把 currentSource 修正成有效值（空/失效 → 第一个；无来源 → 空）。</summary>
        void NormalizeSource()
        {
            try
            {
                if (folders == null || folders.Items.Count == 0)
                {
                    currentSource = "";
                    return;
                }

                if (string.IsNullOrEmpty(currentSource))
                {
                    currentSource = folders.Items[0].Path;
                    return;
                }

                // 在清单里找得到就保留（哪怕目录暂时不可用 —— 用户可能只是没插盘）
                foreach (FileFolders.Folder f in folders.Items)
                {
                    if (f == null) continue;
                    if (string.Equals(f.Path, currentSource, StringComparison.OrdinalIgnoreCase))
                        return;
                }

                // 已移除 / 失效 → 回退第一个
                currentSource = folders.Items[0].Path;
            }
            catch { }
        }

        void SaveSource()
        {
            try
            {
                if (settings == null) return;
                settings.FilesSource = currentSource ?? "";
                settings.Save();
            }
            catch (Exception ex) { Ui.LogError(ex, "保存文件来源"); }
        }

        void SaveSort()
        {
            try
            {
                if (settings == null) return;
                settings.FilesSort = SortToString(sort);
                settings.Save();
            }
            catch (Exception ex) { Ui.LogError(ex, "保存文件排序"); }
        }

        static FileScanner.SortKey ParseSort(string s)
        {
            try
            {
                switch ((s ?? "").Trim().ToLowerInvariant())
                {
                    case "name": return FileScanner.SortKey.Name;
                    case "size": return FileScanner.SortKey.Size;
                    case "type": return FileScanner.SortKey.Type;
                    default: return FileScanner.SortKey.Modified;
                }
            }
            catch { return FileScanner.SortKey.Modified; }
        }

        static string SortToString(FileScanner.SortKey k)
        {
            switch (k)
            {
                case FileScanner.SortKey.Name: return "name";
                case FileScanner.SortKey.Size: return "size";
                case FileScanner.SortKey.Type: return "type";
                default: return "modified";
            }
        }

        void SetNotice(string text)
        {
            try
            {
                if (noticeText == null) return;
                noticeText.Text = text ?? "";
                noticeText.Visibility = string.IsNullOrEmpty(text)
                    ? Visibility.Collapsed : Visibility.Visible;
            }
            catch { }
        }
    }
}
