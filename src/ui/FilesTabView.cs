using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 「文件」标签的界面：来源胶囊行 + 排序行 + 文件列表 + 右键菜单 + 多选 + 键盘。
    ///
    /// 聚合浏览。支持打开、复制拖出、移到回收站和撤销；
    /// 不递归、不监视、不做全盘搜索，不因浏览移动文件。
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
    ///   · Ctrl+C     复制选中的文件
    ///   · Delete     移到回收站（先确认，可撤销）
    ///   · 拖出       复制文件，不移动来源文件
    ///
    /// **拖动排序只对应用磁贴有效**：文件列表行不参与 tilePanel 的拖动换位逻辑。
    /// </summary>
    class FilesTabView
    {
        readonly PanelWindow owner;
        readonly Settings settings;
        readonly string baseDir;

        readonly Panel chipRow;           // 第一行：来源
        readonly Panel extRow;            // 第二行：后缀
        readonly StackPanel listPanel;
        readonly TextBlock noticeText;
        Panel selectionSurface;
        TextBlock statusText;
        string status = "";

        FileFolders folders;

        // ---- 筛选状态（只属于「文件」页，跨标签保留）----
        /// <summary>选中的来源路径集合。空 = 全选（显示所有来源）。</summary>
        readonly HashSet<string> selectedSources =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>选中的后缀分组 key 集合。空 = 全部后缀。</summary>
        readonly HashSet<string> selectedExts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        FileExtGroups.EntryMode entryMode = FileExtGroups.EntryMode.All;
        int displayLimit = FileScanner.MaxReturn;
        int matchedCount;
        bool showSourceColumn;
        bool compactSourceLayout;
        volatile int loadGeneration;
        volatile bool disposed;
        bool active;
        double savedScrollOffset;
        bool hasRenderedRows;
        System.Windows.Threading.DispatcherTimer loadTimeout;

        /// <summary>固定后缀分组（7 组）。</summary>
        readonly List<FileExtGroups.Group> builtinGroups = FileExtGroups.Builtin();

        /// <summary>用户自定义后缀分组（每个后缀一组）。</summary>
        readonly List<FileExtGroups.Group> customGroups = new List<FileExtGroups.Group>();

        // ---- 排序状态（只属于「文件」页，跨标签保留）----
        FileScanner.SortKey sort = FileScanner.SortKey.Modified;
        bool sortAsc = false;   // 默认降序

        DateTime lastLoad = DateTime.MinValue;
        FileScanner.Result cached;

        string filterQuery = "";

        /// <summary>当前显示的条目（筛选 + 排序后）。</summary>
        List<FileScanner.Entry> visible = new List<FileScanner.Entry>();

        /// <summary>选中的条目路径集合（大小写不敏感）。</summary>
        readonly HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>上次点击的索引（Shift 范围选择用）。</summary>
        int anchorIndex = -1;
        string currentPath;

        /// <summary>行控件缓存：路径 → Border（重绘选中态用，不必重建整列表）。</summary>
        readonly Dictionary<string, Border> rowMap =
            new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);

        /// <summary>空格预览浮层。懒创建（第一次按空格才建）。</summary>
        FilePreviewPopup preview;

        /// <summary>拖拽起始点（用于阈值判断，避免单击/双击误触发拖拽）。</summary>
        Point dragStart = new Point(double.NaN, double.NaN);

        /// <summary>底部「撤销」提示条。懒创建。</summary>
        UndoBar undoBar;

        /// <summary>提示条的宿主容器（filesPanel 的末尾）。</summary>
        StackPanel undoHost;

        /// <summary>
        /// 「把文件夹拖进来」的放置提示层（常驻，只切 Visibility）。
        ///
        /// 由 `PanelWindow.Ui.cs` 建好后通过 <see cref="AttachDropHint"/> 注入 ——
        /// FilesTabView 只拿到各个容器的引用，不负责建控件。
        /// </summary>
        DropHint dropHint;

        public FilesTabView(PanelWindow owner, Settings settings, string baseDir,
            Panel chipHost, Panel extHost, StackPanel listHost, TextBlock noticeHost,
            StackPanel undoHost)
        {
            this.owner = owner;
            this.settings = settings;
            this.baseDir = baseDir;
            this.chipRow = chipHost;
            this.extRow = extHost;
            this.listPanel = listHost;
            this.noticeText = noticeHost;
            this.undoHost = undoHost;

            if (chipRow != null)
                chipRow.SizeChanged += delegate { UpdateSourceChipWidths(); };

            if (listPanel != null)
            {
                listPanel.Focusable = true;
                listPanel.SizeChanged += delegate
                {
                    try
                    {
                        bool compact = ShouldCompactSourceColumn();
                        if (compact == compactSourceLayout) return;
                        compactSourceLayout = compact;
                        if (active && cached != null) RenderList(cached);
                    }
                    catch (Exception ex) { Ui.LogError(ex, "调整文件列表列宽"); }
                };
            }

            this.folders = FileFolders.Load(baseDir);

            // 来源多选：从 settings 读（含旧值迁移）
            if (settings != null && settings.FilesSources != null)
            {
                foreach (string s in settings.FilesSources)
                    if (!string.IsNullOrWhiteSpace(s)) selectedSources.Add(s.Trim());
            }

            // 排序：从 settings 读 `列:方向`
            ParseSortSetting(settings == null ? "" : settings.FilesSort,
                out sort, out sortAsc);
        }
        /// <summary>重新读 FileFolders 配置（用户增删改后调）。</summary>
        public void ReloadFolders()
        {
            try
            {
                folders = FileFolders.Load(baseDir);
                NormalizeSource();
                lastLoad = DateTime.MinValue;
            }
            catch (Exception ex) { Ui.LogError(ex, "重读文件夹清单"); }
        }

        // ========== 放置提示（拖文件夹进来加来源）==========

        /// <summary>
        /// 注入放置提示层。由 PanelWindow 在建好控件后调用一次。
        ///
        /// 为什么不在构造函数里建：`FilesTabView` 只拿到 chipRow / extRow /
        /// listPanel / noticeText / undoHost 五个容器引用，没有自己的布局位置；
        /// 提示层要插在 chipRow 之后，那是 `PanelWindow.Ui.cs` 的职责。
        /// </summary>
        public void AttachDropHint(DropHint hint)
        {
            try
            {
                dropHint = hint;
                RefreshDropHint();
            }
            catch (Exception ex) { Ui.LogError(ex, "接入放置提示"); }
        }

        /// <summary>
        /// 接入文件页的背景承载面。点击列表之外的空白，或列表行之间的空隙时，
        /// 清空当前选择；子控件（文件行、筛选、表头、按钮）仍按自己的语义处理。
        /// </summary>
        internal void AttachSelectionSurface(Panel surface)
        {
            try
            {
                if (ReferenceEquals(selectionSurface, surface)) return;
                if (selectionSurface != null)
                    selectionSurface.MouseLeftButtonDown -= OnSelectionSurfaceMouseLeftButtonDown;

                selectionSurface = surface;
                if (selectionSurface == null) return;

                // Panel 没有背景时，子控件之外的区域不参与命中测试。
                if (selectionSurface.Background == null)
                    selectionSurface.Background = Brushes.Transparent;
                selectionSurface.MouseLeftButtonDown += OnSelectionSurfaceMouseLeftButtonDown;
            }
            catch (Exception ex) { Ui.LogError(ex, "接入文件页空白选择"); }
        }

        void OnSelectionSurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (!active || e == null || e.ChangedButton != MouseButton.Left) return;

                object hit = e.OriginalSource;
                bool blankSurface = ReferenceEquals(hit, selectionSurface);
                bool rowGap = ReferenceEquals(hit, listPanel);
                if (!blankSurface && !rowGap) return;

                ClearSelection();
                listPanel.Focus();
            }
            catch (Exception ex) { Ui.LogError(ex, "文件页点击空白处"); }
        }

        /// <summary>
        /// 按「已有几个来源」更新提示层：没满就邀请拖入，满了就显示已达上限。
        ///
        /// 文案全部来自 `DropSpec` 的常量派生函数，不在这里手写。
        /// </summary>
        void RefreshDropHint()
        {
            try
            {
                if (dropHint == null) return;
                if (folders == null) folders = FileFolders.Load(baseDir);

                int have = folders == null ? 0 : folders.Items.Count;
                bool full = have >= FileFolders.MaxFolders;

                if (full)
                {
                    // 满了：不再邀请拖入，改显示已达上限
                    dropHint.SetTitle(DropSpec.FolderFullText(), DropSpec.FolderHintText());
                    dropHint.SetActive(false);
                }
                else
                {
                    dropHint.SetTitle("把文件夹拖进来", DropSpec.FolderHintText());
                }

                // 来源存在后收起大型提示，避免它长期占据文件列表上方；
                // 真正拖入窗口时由 PanelWindow 临时显示并点亮。
                dropHint.SetVisible(have == 0);
            }
            catch (Exception ex) { Ui.LogError(ex, "刷新放置提示"); }
        }

        /// <summary>拖入期间临时显示来源提示；离开后恢复为空状态策略。</summary>
        public void SetDropCue(bool on)
        {
            try
            {
                if (dropHint == null) return;
                if (on)
                {
                    dropHint.SetVisible(true);
                    dropHint.SetActive(true);
                }
                else
                {
                    dropHint.SetActive(false);
                    RefreshDropHint();
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "切换文件库拖入提示"); }
        }

        /// <summary>
        /// 拖入一批路径，按 `DropSpec.ForFolderSource(remaining)` 校验并逐个加入。
        ///
        /// 返回给用户的汇总文案（null = 不需要提示）。
        /// 不搬运、不复制、不递归统计大小 —— 只记录路径。
        /// </summary>
        internal string HandleFolderDrop(IEnumerable<string> paths)
        {
            try
            {
                if (paths == null) return null;
                if (folders == null) folders = FileFolders.Load(baseDir);

                int have = folders.Items.Count;
                int remaining = FileFolders.MaxFolders - have;

                // remaining <= 0 → ForFolderSource 里会变成 0（不限），
                // 但那不是我们要的语义。这里显式处理"已满"。
                if (remaining <= 0)
                {
                    // 全部按「超出数量」拒绝
                    List<DropSpec.Rejected> all = new List<DropSpec.Rejected>();
                    foreach (string p in paths)
                    {
                        if (string.IsNullOrWhiteSpace(p)) continue;
                        DropSpec.Rejected r = new DropSpec.Rejected();
                        r.Path = p;
                        try { r.Name = Path.GetFileName(p.TrimEnd('\\', '/')); }
                        catch { r.Name = p; }
                        r.Reason = DropSpec.RejectReason.TooMany;
                        all.Add(r);
                    }
                    RefreshDropHint();
                    return DropSpec.BuildSummary(0, all);
                }

                DropSpec spec = DropSpec.ForFolderSource(remaining);
                DropSpec.Validation v = spec.Validate(paths);

                int added = 0;
                int dup = 0;

                foreach (string p in v.Accepted)
                {
                    // 走现有的 FileFolders.Add：去重、上限、网络路径拒绝都在里面
                    FileFolders.AddResult r = folders.Add(p);
                    if (r == FileFolders.AddResult.Ok) added++;
                    else if (r == FileFolders.AddResult.Duplicate) dup++;
                }

                if (added > 0)
                {
                    NormalizeSource();
                    SaveSource();
                    selected.Clear();
                    anchorIndex = -1;
                    currentPath = null;
                    Refresh(true);
                }

                RefreshDropHint();

                // 汇总：被拒项和「已存在」都并进同一个「跳过」计数，
                // 避免「跳过」在一句话里出现两次。文案规则全在 DropSpec 里。
                return DropSpec.BuildSummary(added, v.RejectedItems, dup);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "拖入文件夹来源");
                return null;
            }
        }

        /// <summary>刷新整个视图。force=false 时 30 秒内复用缓存。</summary>
        public void Refresh(bool force)
        {
            try
            {
                BuildChips();
                BuildExtRow();

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
                SetStatus("更新未完成");
            }
        }

        /// <summary>返回页面只激活，不清筛选、分页、选择和缓存。</summary>
        public void OnEnter() { active = true; PublishStatus(); }
        internal string FilterQuery { get { return filterQuery; } }
        internal string Status { get { return status; } }

        /// <summary>数量集中在面板右下角，来源范围和异常留在列表上方。</summary>
        internal void AttachStatus(TextBlock host) { statusText = host; PublishStatus(); }
        void SetStatus(string text) { status = text ?? ""; PublishStatus(); }
        void PublishStatus()
        {
            if (!active || statusText == null) return;
            statusText.Text = status;
            statusText.ToolTip = cached == null ? null : "共扫描 " + cached.ScannedCount
                + " 项；" + matchedCount + " 项符合当前筛选；全选只选已显示项";
        }

        /// <summary>
        /// 离开时关闭预览、使异步回调过期，保留有限的浏览状态供返回使用。
        /// </summary>
        public void OnLeave()
        {
            try
            {
                InvalidateItemMenu();
                ClosePreview();
                ScrollViewer scroll = FindScrollViewer();
                if (scroll != null) savedScrollOffset = scroll.VerticalOffset;
                active = false;
                loadGeneration++; // 丢弃离开页面前尚未完成的扫描回调，不清浏览状态。
                if (loadTimeout != null) loadTimeout.Stop();
            }
            catch (Exception ex) { Ui.LogError(ex, "离开文件标签"); }
        }

        public void Dispose()
        {
            disposed = true;
            OnLeave();
            if (selectionSurface != null)
                selectionSurface.MouseLeftButtonDown -= OnSelectionSurfaceMouseLeftButtonDown;
            selectionSurface = null;
            ClearList();
            cached = null;
        }

        internal void RestorePageScroll()
        {
            ScrollViewer scroll = FindScrollViewer();
            if (scroll == null) return;
            scroll.UpdateLayout();
            scroll.ScrollToVerticalOffset(savedScrollOffset);
        }

        /// <summary>搜索框文字变化：只过滤当前来源的文件名。</summary>
        public void ApplyFilter(string query)
        {
            try
            {

                string next = query ?? "";
                if (filterQuery == next) return;
                filterQuery = next;
                displayLimit = FileScanner.MaxReturn;
                if (cached != null) RenderList(cached);
            }
            catch (Exception ex) { Ui.LogError(ex, "过滤文件列表"); }
        }
        /// <summary>当前生效的来源路径（测试用；也是"修正后"的真实值）。</summary>
        /// <summary>测试用：直接设置来源多选集合。</summary>
        public void TestSetSources(List<string> paths)
        {
            try
            {

                selectedSources.Clear();
                if (paths != null)
                {
                    foreach (string s in paths)
                        if (!string.IsNullOrWhiteSpace(s)) selectedSources.Add(s.Trim());
                }
            }
            catch { }
        }

        /// <summary>测试用：当前选中项数。</summary>
        public int TestSelectedCount()
        {
            try { return selected.Count; }
            catch { return 0; }
        }

        /// <summary>测试用：当前显示的行数（不含表头）。</summary>
        public int TestRowCount()
        {
            try { return visible == null ? 0 : visible.Count; }
            catch { return 0; }
        }

        /// <summary>测试用：读来源多选集合。</summary>
        public List<string> TestGetSources()
        {
            try { return new List<string>(selectedSources); }
            catch { return new List<string>(); }
        }

        public string CurrentSourcePath()

        {
            try { return selectedSources.Count > 0 ? (new List<string>(selectedSources))[0] : ""; }
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

                // ---- Esc 分层（预览 → 搜索 → 选择 → 交上层）----
                if (key == Key.Escape)
                {
                    if (IsPreviewOpen) { ClosePreview(); return true; }
                    if (selected.Count > 0)
                    {
                        ClearSelection();
                        listPanel.Focus();
                        return true;
                    }
                    return false;   // 都清完了 → 交给上层（清搜索框 / 收起面板）
                }

                if (key == Key.F5) { Refresh(true); return true; }
                if (visible.Count == 0) return false;

                if (Ui.IsContextMenuKey(key, mods))
                {
                    FileScanner.Entry target = null;
                    foreach (FileScanner.Entry entry in visible)
                    {
                        Border row;
                        if (rowMap.TryGetValue(entry.Path, out row) && row.IsKeyboardFocusWithin)
                        { target = entry; break; }
                    }
                    if (target == null) target = CurrentEntry();
                    if (target == null) return false;
                    SelectMenuTarget(target);
                    ShowItemMenu(target, true);
                    return true;
                }

                // Ctrl+A 全选
                if (key == Key.A && (mods & ModifierKeys.Control) != 0)
                {
                    selected.Clear();
                    foreach (FileScanner.Entry e in visible) if (e != null) selected.Add(e.Path);
                    UpdateSelectionVisuals();
                    return true;
                }

                // Ctrl+C 复制到剪贴板（FileDrop）
                if (key == Key.C && (mods & ModifierKeys.Control) != 0)
                {
                    CopySelectedToClipboard();
                    return true;
                }

                // F5 刷新
                if (key == Key.F5) { Refresh(true); return true; }

                // Delete 删除（进回收站，带确认）
                if (key == Key.Delete) { ConfirmAndDelete(); return true; }

                // 空格：预览开关
                if (key == Key.Space) { TogglePreview(); return true; }

                // Enter 打开（等同双击）
                if (key == Key.Enter)
                {
                    FileScanner.Entry e = CurrentEntry();
                    if (e != null) { OpenEntry(e); return true; }
                    return false;
                }

                // ↑↓ 移动选中（预览打开时同步更新内容）
                if (key == Key.Up || key == Key.Down)
                {
                    MoveSelection(key == Key.Up ? -1 : 1, (mods & ModifierKeys.Shift) != 0);
                    if (IsPreviewOpen)
                    {
                        FileScanner.Entry e = CurrentEntry();
                        if (e != null && preview != null) preview.Show(e, owner);
                    }
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

        /// <summary>与右键复制共用规则，包含文件和文件夹；不移动来源内容。</summary>
        public void CopySelectedToClipboard()
        {
            CopySelectedIncludingFolders();
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

                Button allSources = MakeChipButton("全部来源", delegate
                {
                    selectedSources.Clear();
                    SaveSource();
                    displayLimit = FileScanner.MaxReturn;
                    Refresh(true);
                }, true);
                allSources.Tag = "file-all-sources";
                allSources.Background = selectedSources.Count == 0 ? Ui.Accent : Ui.SearchBg;
                allSources.Foreground = selectedSources.Count == 0 ? Brushes.White : Ui.Sub;
                chipRow.Children.Add(allSources);

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

        Button MakeChip(FileFolders.Folder f, bool available, bool isSelected)
        {
            Button b = MakeChipButton("", null, true);
            b.Height = 28;
            b.Padding = new Thickness(12, 0, 12, 0);
            b.Margin = new Thickness(0, 0, Ui.S.Sm, chipRow is WrapPanel ? Ui.S.Xs : 0);
            b.Cursor = Cursors.Hand;
            b.Background = isSelected ? Ui.Accent : Ui.SearchBg;
            b.BorderBrush = isSelected ? Ui.Accent : Ui.Line;
            b.BorderThickness = new Thickness(1);
            b.Tag = f.Path;

            // 失效来源：灰显 + 明确提示「不可用」
            if (!available) b.Opacity = 0.45;

            string label = f.Name ?? "";
            if (!available) label += "（不可用）";

            b.Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
            b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            b.MaxWidth = SourceChipMaxWidth();
            b.Foreground = isSelected ? Brushes.White : Ui.Fg;
            System.Windows.Automation.AutomationProperties.SetName(b, "来源 " + label + "，" + f.Path);

            b.ToolTip = available
                ? label + "\n" + f.Path
                : (label + "\n" + f.Path + "\n\n不可用：目录可能已删除，或外置盘未插入。\n右键可以移除这条记录。");

            b.Click += delegate
            {
                try
                {
                    // ★ 复选：已选中就取消，未选中就加入，**不清空其他已选项**
                    if (!selectedSources.Remove(f.Path))
                        selectedSources.Add(f.Path);

                    SaveSource();
                    displayLimit = FileScanner.MaxReturn;
                    Refresh(true);   // 重新扫描 + 走「选中保持」
                }
                catch (Exception ex) { Ui.LogError(ex, "切换文件来源"); }
            };

            b.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                ShowChipMenu(f, b);
            };
            b.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Apps || (e.Key == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
                {
                    e.Handled = true;
                    ShowChipMenu(f, b);
                }
            };

            return b;
        }

        double SourceChipMaxWidth()
        {
            double width = chipRow == null ? 0 : chipRow.ActualWidth;
            return width > 0 ? Math.Min(280, Math.Max(0, width - Ui.S.Sm)) : 280;
        }

        void UpdateSourceChipWidths()
        {
            if (chipRow == null) return;
            double maxWidth = SourceChipMaxWidth();
            foreach (UIElement child in chipRow.Children)
            {
                Button chip = child as Button;
                if (chip != null && chip.Tag is string && chip.Content is TextBlock)
                    chip.MaxWidth = maxWidth;
            }
        }

        Button MakeChipButton(string label, Action onClick, bool wide)
        {
            Button b = MakeExtChipButton(label, onClick);
            b.Height = 28;
            b.Padding = new Thickness(wide ? 12 : 10, 0, wide ? 12 : 10, 0);
            b.Margin = new Thickness(0, 0, Ui.S.Sm, chipRow is WrapPanel ? Ui.S.Xs : 0);
            b.Cursor = Cursors.Hand;
            b.Background = Ui.SearchBg;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(1);

            b.FontSize = 11.5;
            if (label.StartsWith("+")) System.Windows.Automation.AutomationProperties.SetName(b, "添加文件库来源");
            return b;
        }

        void ShowChipMenu(FileFolders.Folder f, FrameworkElement chip)
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

        // ========== 第二行：后缀筛选 ==========

        void BuildExtRow()
        {
            try
            {

                if (extRow == null) return;
                extRow.Children.Clear();

                extRow.Children.Add(MakeModeButton("全部", FileExtGroups.EntryMode.All));
                extRow.Children.Add(MakeModeButton("文件", FileExtGroups.EntryMode.Files));
                extRow.Children.Add(MakeModeButton("文件夹", FileExtGroups.EntryMode.Folders));

                // 7 个固定分组
                foreach (FileExtGroups.Group g in builtinGroups)
                    extRow.Children.Add(MakeExtChip(g));

                // 用户自定义后缀（每个单独一组）
                foreach (FileExtGroups.Group g in customGroups)
                    extRow.Children.Add(MakeExtChip(g));

                // 「其他」组（排除法）
                extRow.Children.Add(MakeExtChip(FileExtGroups.Other()));

                // 末尾「+」加自定义后缀
                Button add = MakeExtAddButton();
                extRow.Children.Add(add);

            }
            catch (Exception ex) { Ui.LogError(ex, "构建后缀行"); }
        }

        /// <summary>明确、互斥的条目范围按钮。</summary>
        Button MakeModeButton(string label, FileExtGroups.EntryMode mode)
        {
            bool on = entryMode == mode;
            Button b = MakeExtChipButton(label, delegate
            {
                entryMode = mode;
                displayLimit = FileScanner.MaxReturn;
                BuildExtRow();
                RenderList(cached);
            });
            b.Background = on ? Ui.Accent : Brushes.Transparent;
            b.BorderBrush = on ? Ui.Accent : Ui.Line;
            b.Foreground = on ? Brushes.White : Ui.Sub;
            b.Tag = "file-mode:" + mode;
            b.ToolTip = "显示" + label + "；后缀筛选只影响文件，不影响文件夹";
            return b;
        }

        Button MakeExtChip(FileExtGroups.Group g)
        {
            bool on = selectedExts.Contains(g.Key);
            Button b = MakeExtChipButton(g.Label, delegate
            {
                if (!selectedExts.Remove(g.Key)) selectedExts.Add(g.Key);
                displayLimit = FileScanner.MaxReturn;
                BuildExtRow();
                RenderList(cached);
            });
            b.Background = on ? Ui.Accent : Brushes.Transparent;
            b.BorderBrush = on ? Ui.Accent : Ui.Line;
            b.Foreground = on ? Brushes.White : Ui.Sub;
            b.Tag = g.Key;
            b.IsEnabled = entryMode != FileExtGroups.EntryMode.Folders;

            // 自定义组带提示：显示它覆盖的扩展名
            if (g.IsCustom)
                b.ToolTip = "自定义后缀：" + string.Join(" / ", g.Exts.ToArray());
            else if (g.IsOther)
                b.ToolTip = "不属于任何固定分组、也没有自定义后缀的扩展名；无扩展名的文件也算这里";
            if (!b.IsEnabled) b.ToolTip = "仅看文件夹时不使用后缀筛选；切回全部或文件会恢复已有选择";
            ToolTipService.SetShowOnDisabled(b, true);

            return b;
        }

        Button MakeExtChipButton(string label, Action onClick)
        {
            Button b = new Button();
            b.Template = UiButtons.Template(14);
            b.HorizontalContentAlignment = HorizontalAlignment.Center;
            b.VerticalContentAlignment = VerticalAlignment.Center;
            b.Height = 28;
            b.Padding = new Thickness(10, 0, 10, 0);
            b.Margin = new Thickness(0, 0, Ui.S.Xs, 0);
            if (extRow is WrapPanel) b.Margin = new Thickness(0, 0, Ui.S.Xs, Ui.S.Xs);
            b.Cursor = Cursors.Hand;
            b.Background = Ui.SearchBg;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(1);

            b.Content = label;
            b.FontFamily = Ui.YaHei;
            b.FontSize = 10.5;
            b.HorizontalContentAlignment = HorizontalAlignment.Center;
            b.Foreground = Ui.Sub;
            System.Windows.Automation.AutomationProperties.SetName(b, label);
            b.Click += delegate { if (onClick != null) onClick(); };
            return b;
        }

        /// <summary>末尾「+」：加自定义后缀。</summary>
        Button MakeExtAddButton()
        {
            Button b = MakeExtChipButton("+", delegate { PromptAddExt(); });
            System.Windows.Automation.AutomationProperties.SetName(b, "添加自定义后缀");
            b.ToolTip = "添加自定义后缀（如 md、py、log）";
            return b;
        }

        /// <summary>弹输入框加自定义后缀。</summary>
        void PromptAddExt()
        {
            try
            {

                string input = TextPromptDialog.Ask(owner,
                    "添加自定义后缀",
                    "输入后缀（不含点，可用逗号分隔多个，如 md,py）：", "");
                if (input == null) return;

                string[] parts = input.Split(new char[] { ',', ' ', ';', '；' },
                    StringSplitOptions.RemoveEmptyEntries);
                int added = 0;
                foreach (string raw in parts)
                {
                    string e = FileExtGroups.Normalize(raw);
                    if (e.Length == 0) continue;
                    // 已在自定义组里 → 跳过
                    bool exists = false;
                    foreach (FileExtGroups.Group g in customGroups)
                        if (g.Exts.Contains(e)) { exists = true; break; }
                    if (exists) continue;

                    FileExtGroups.Group ng = new FileExtGroups.Group(e, e, new string[] { e });
                    ng.IsCustom = true;
                    customGroups.Add(ng);
                    added++;
                }

                if (added == 0)
                {
                    MessageBox.Show("没有可添加的新后缀。", AppBrand.Text("四叶草 · 文件"));
                    return;
                }

                BuildExtRow();
                RenderList(cached);
            }
            catch (Exception ex) { Ui.LogError(ex, "添加自定义后缀"); }
        }
        void LoadAndRender()
        {
            try
            {
                InvalidateItemMenu();
                int generation = ++loadGeneration;
                if (loadTimeout != null) loadTimeout.Stop();
                lastLoad = DateTime.MinValue; // 扫描被切页取消后，下次返回必须重新读取。
                if (!HasSources)
                {
                    ClearList();
                    SetStatus("尚未添加来源");
                    SetNotice("文件库还没有来源。点上面的「+」或拖入文件夹开始使用；这里浏览来源，不会自动搬动文件。");
                    cached = null;
                    return;
                }

                // 确定要读的来源列表：
                //   选中了若干 → 只读这些（并集）
                //   一个都没选 → 读全部来源
                List<string> srcs = EffectiveSources();
                if (srcs.Count == 0)
                {
                    ClearList();
                    SetStatus("没有可用来源");
                    SetNotice("文件库还没有可用来源。点上面的「+」或拖入文件夹开始使用。");
                    cached = null;
                    return;
                }

                // 有快照时只在底部显示更新中，上方范围高度不变，避免挤动阅读位置。
                if (cached == null) SetNotice("正在读取来源第一层…");
                SetStatus("更新中…");

                FileScanner.SortKey sk = sort;
                bool asc = sortAsc;

                // 扫描绝不 Join 界面线程；旧行保持可见，晚到的旧扫描不能覆盖新来源。
                System.Windows.Threading.Dispatcher dispatcher = listPanel.Dispatcher;
                System.Windows.Threading.DispatcherTimer timeout = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Background, dispatcher);
                loadTimeout = timeout;
                timeout.Interval = TimeSpan.FromSeconds(8);
                timeout.Tick += delegate
                {
                    timeout.Stop();
                    if (disposed || generation != loadGeneration) return;
                    loadGeneration++;
                    SetNotice("读取超时，未完成更新；保留原列表。可按 F5 重试。");
                    SetStatus("更新未完成");
                };
                timeout.Start();
                System.Threading.Thread t = new System.Threading.Thread(delegate()
                {
                    FileScanner.Result r;
                    try { r = FileScanner.ScanMany(srcs, sk, asc, delegate { return disposed || generation != loadGeneration; }); }
                    catch (Exception ex)
                    {
                        r = new FileScanner.Result();
                        r.Error = "读取失败: " + ex.Message;
                    }
                    if (dispatcher.HasShutdownStarted) return;
                    try { dispatcher.BeginInvoke(new Action(delegate
                    {
                        timeout.Stop();
                        if (disposed || generation != loadGeneration) return;
                        if (!string.IsNullOrEmpty(r.Error) && cached != null)
                        {
                            SetNotice(r.Error + "；未完成更新，保留原列表。可按 F5 重试。");
                            SetStatus("更新未完成");
                            return;
                        }
                        cached = r;
                        lastLoad = DateTime.Now;
                        RenderList(r);
                    }), System.Windows.Threading.DispatcherPriority.Background); }
                    catch (InvalidOperationException) { }
                });
                t.IsBackground = true;
                t.Start();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "读取文件目录");
                SetNotice("读取失败：" + ex.Message);
                SetStatus("更新未完成");
            }
        }

        /// <summary>清空列表控件与瞬时状态（不动筛选/排序状态）。</summary>
        void ClearList()
        {
            InvalidateItemMenu();
            currentPath = null;
            if (listPanel != null) listPanel.Children.Clear();
            visible = new List<FileScanner.Entry>();
            rowMap.Clear();
            selected.Clear();
            anchorIndex = -1;
        }

        ScrollViewer FindScrollViewer()
        {
            DependencyObject node = listPanel;
            while (node != null)
            {
                ScrollViewer scroll = node as ScrollViewer;
                if (scroll != null) return scroll;
                node = VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        int VisibleIndex(string path)
        {
            return visible.FindIndex(delegate(FileScanner.Entry item)
                { return string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase); });
        }

        string SourceLabel(string root)
        {
            if (folders != null)
                foreach (FileFolders.Folder f in folders.Items)
                    if (string.Equals(f.Path, root, StringComparison.OrdinalIgnoreCase)) return f.Name;
            return root;
        }

        string ScopeNotice(FileScanner.Result r)
        {
            List<string> sources = EffectiveSources();
            string text = sources.Count == 1 ? "来源：" + sources[0] : sources.Count + " 个来源";
            text += " · 仅第一层";
            if (r.EnumerateTruncated) text += "\n扫描达到 " + FileScanner.MaxEnumerate + " 项上限，搜索结果可能不完整";
            if (!string.IsNullOrEmpty(r.Warning)) text += "\n" + r.Warning;
            return text;
        }

        // 仅调整变动位置，不清空再添加所有行；路径对应控件与焦点保持稳定。
        void ReconcileRows(List<UIElement> desired)
        {
            HashSet<UIElement> keep = new HashSet<UIElement>(desired);
            for (int i = listPanel.Children.Count - 1; i >= 0; i--)
                if (!keep.Contains(listPanel.Children[i])) listPanel.Children.RemoveAt(i);
            for (int i = 0; i < desired.Count; i++)
            {
                if (i < listPanel.Children.Count && ReferenceEquals(listPanel.Children[i], desired[i])) continue;
                int existing = listPanel.Children.IndexOf(desired[i]);
                if (existing >= 0) listPanel.Children.RemoveAt(existing);
                listPanel.Children.Insert(i, desired[i]);
            }
        }

        /// <summary>
        /// 实际要读取的来源列表。
        ///   · selectedSources 非空 → 只取其中仍存在的
        ///   · selectedSources 为空 → 取全部来源
        /// </summary>
        List<string> EffectiveSources()
        {
            List<string> list = new List<string>();
            try
            {

                if (folders == null) return list;

                if (selectedSources.Count == 0)
                {
                    foreach (FileFolders.Folder f in folders.Items)
                        if (f != null && !string.IsNullOrEmpty(f.Path)) list.Add(f.Path);
                    return list;
                }

                // 选中模式：按 folders 顺序输出，保证稳定
                foreach (FileFolders.Folder f in folders.Items)
                {
                    if (f == null) continue;
                    if (selectedSources.Contains(f.Path)) list.Add(f.Path);
                }
                return list;
            }
            catch { return list; }
        }

        void RenderList(FileScanner.Result r)
        {
            try
            {
                InvalidateItemMenu();
                if (listPanel == null) return;

                string anchorPath = anchorIndex >= 0 && anchorIndex < visible.Count ? visible[anchorIndex].Path : null;
                IInputElement focus = Keyboard.FocusedElement;
                ScrollViewer scroll = FindScrollViewer();
                if (scroll != null) scroll.UpdateLayout();
                double offset = scroll == null ? 0 : scroll.VerticalOffset;
                Border topRow = null;
                double topY = 0;
                if (scroll != null && active)
                    foreach (FileScanner.Entry item in visible)
                    {
                        Border row;
                        if (!rowMap.TryGetValue(item.Path, out row) || !listPanel.Children.Contains(row)) continue;
                        double y = row.TranslatePoint(new Point(), scroll).Y;
                        if (y + row.ActualHeight > 0) { topRow = row; topY = y; break; }
                    }

                if (r == null)
                {
                    return;
                }

                if (!string.IsNullOrEmpty(r.Error))
                {
                    SetNotice(r.Error);
                    SetStatus("读取未完成");
                    return;
                }

                // ---- 1. 来源筛选（selectedSources 为空 = 全通过）----
                List<FileScanner.Entry> bySource = new List<FileScanner.Entry>();
                foreach (FileScanner.Entry e in r.Items)
                {
                    if (e == null) continue;
                    if (selectedSources.Count > 0)
                    {
                        if (string.IsNullOrEmpty(e.SourceRoot)) continue;
                        if (!selectedSources.Contains(e.SourceRoot)) continue;
                    }
                    bySource.Add(e);
                }

                // ---- 2. 搜索过滤（当前来源的文件名）----
                List<FileScanner.Entry> byQuery = FileScanner.Filter(bySource, filterQuery);

                // ---- 3. 明确范围 + 后缀（后缀仅影响文件）----
                List<FileScanner.Entry> byType = new List<FileScanner.Entry>();
                foreach (FileScanner.Entry e in byQuery)
                {
                    // 判定逻辑集中在 FileExtGroups.KeepEntry（纯函数，可单测）
                    if (FileExtGroups.KeepEntry(e.IsFolder, e.Ext, entryMode,
                            selectedExts, builtinGroups, customGroups))
                        byType.Add(e);
                }

                // ---- 4. 排序（Sort 内部保证文件夹在前）----
                FileScanner.Sort(byType, sort, sortAsc);

                matchedCount = byType.Count;
                visible = byType.GetRange(0, Math.Min(displayLimit, byType.Count));
                PruneSelection();
                anchorIndex = anchorPath == null ? -1 : VisibleIndex(anchorPath);
                if (VisibleIndex(currentPath) < 0) currentPath = null;
                showSourceColumn = EffectiveSources().Count > 1;
                compactSourceLayout = ShouldCompactSourceColumn();

                // 删除真正消失的路径缓存；筛选隐藏的行仍可复用，最多为扫描预算大小。
                HashSet<string> alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FileScanner.Entry item in r.Items) alive.Add(item.Path);
                foreach (string path in new List<string>(rowMap.Keys))
                    if (!alive.Contains(path)) rowMap.Remove(path);

                List<UIElement> desired = new List<UIElement>();
                if (visible.Count > 0) desired.Add(MakeHeader());

                // ---- 6. 行 ----
                foreach (FileScanner.Entry e in visible)
                {
                    Border row;
                    if (!rowMap.TryGetValue(e.Path, out row)) row = MakeRow(e, VisibleIndex(e.Path));
                    else UpdateRow(row, e);
                    if (row != null)
                    {
                        desired.Add(row);
                        rowMap[e.Path] = row;
                    }
                }
                if (matchedCount > visible.Count)
                {
                    Button more = MakeExtChipButton("加载更多（还有 " + (matchedCount - visible.Count) + " 项）", delegate
                    {
                        displayLimit = Math.Min(FileScanner.MaxEnumerate, displayLimit + FileScanner.MaxReturn);
                        RenderList(cached);
                    });
                    more.Tag = "file-load-more";
                    more.HorizontalAlignment = HorizontalAlignment.Left;
                    desired.Add(more);
                }
                ReconcileRows(desired);
                if (!hasRenderedRows && visible.Count > 0)
                {
                    selected.Add(visible[0].Path);
                    anchorIndex = 0;
                    currentPath = visible[0].Path;
                    hasRenderedRows = true;
                }

                UpdateSelectionVisuals();

                // ---- 7. 底部统计 ----
                string emptyNotice = visible.Count > 0 ? ""
                    : (filterQuery.Length > 0 ? "已扫描范围内没有匹配「" + filterQuery + "」的文件或文件夹"
                       : "当前范围内没有可显示的文件或文件夹");
                SetNotice(ScopeNotice(r) + (emptyNotice.Length > 0 ? "\n" + emptyNotice : ""));
                SetStatus(visible.Count > 0 ? BuildStats(matchedCount) : "0 项");
                if (scroll != null && active)
                {
                    scroll.UpdateLayout();
                    if (topRow != null && desired.Contains(topRow))
                        offset += topRow.TranslatePoint(new Point(), scroll).Y - topY;
                    scroll.ScrollToVerticalOffset(offset);
                }
                Border focusedRow = focus as Border;
                if (focusedRow != null && desired.Contains(focusedRow)) focusedRow.Focus();
            }
            catch (Exception ex) { Ui.LogError(ex, "渲染文件列表"); }
        }

        /// <summary>
        /// 底部统计，按当前结果走：
        ///   · 只有文件夹 → 「显示 N 个文件夹」
        ///   · 只有文件   → 「显示 N 个文件」
        ///   · 两者都有   → 「显示 N 项（x 个文件夹 / y 个文件）」
        /// </summary>
        string BuildStats(int filtered)
        {
            try
            {

                int folders = 0, files = 0;
                foreach (FileScanner.Entry e in visible)
                {
                    if (e == null) continue;
                    if (e.IsFolder) folders++; else files++;
                }

                StringBuilder sb = new StringBuilder();

                if (folders == 0)
                {
                    // 只有文件
                    sb.Append(files);
                    sb.Append(" 个文件");
                }
                else if (files == 0)
                {
                    // 只有文件夹
                    sb.Append(folders);
                    sb.Append(" 个文件夹");
                }
                else
                {
                    // 两者都有
                    sb.Append(folders);
                    sb.Append(" 个文件夹 · ");
                    sb.Append(files);
                    sb.Append(" 个文件");
                }

                if (filtered > visible.Count)
                {
                    sb.Insert(0, "显示 " + visible.Count + " / " + filtered + " 项 · ");
                }
                return sb.ToString();
            }
            catch { return "显示 " + filtered + " 项"; }
        }
        /// <summary>表头：名称 / 修改时间 / 大小，可点击排序。</summary>
        Border MakeHeader()
        {
            try
            {

                Border b = new Border();
                b.Height = 28;
                b.Padding = new Thickness(Ui.S.Sm, 0, Ui.S.Sm, 0);
                b.Margin = new Thickness(0, 0, 0, Ui.S.Xs);
                b.Background = Brushes.Transparent;
                b.BorderBrush = Ui.Line;
                b.BorderThickness = new Thickness(0, 0, 0, 1);

                Grid g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions[0].Width = new GridLength(32);
                g.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                ApplyFileColumns(g);

                // 名称列（第 1 列，占满）
                FrameworkElement nameH = MakeHeaderCell("名称", FileScanner.SortKey.Name);
                Grid.SetColumn(nameH, 1);
                g.Children.Add(nameH);

                // 修改时间列
                FrameworkElement timeH = MakeHeaderCell("修改时间", FileScanner.SortKey.Modified);
                Grid.SetColumn(timeH, 2);
                g.Children.Add(timeH);

                // 大小列
                FrameworkElement sizeH = MakeHeaderCell("大小", FileScanner.SortKey.Size);
                Grid.SetColumn(sizeH, 3);
                g.Children.Add(sizeH);

                TextBlock sourceH = Ui.T("来源", 10.5, Ui.Sub);
                sourceH.VerticalAlignment = VerticalAlignment.Center;
                sourceH.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
                sourceH.Visibility = showSourceColumn && !compactSourceLayout
                    ? Visibility.Visible : Visibility.Collapsed;
                Grid.SetColumn(sourceH, 4);
                g.Children.Add(sourceH);

                b.Child = g;
                return b;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "构建表头");
                return null;
            }
        }

        FrameworkElement MakeHeaderCell(string label, FileScanner.SortKey key)
        {
            bool active = (sort == key);
            string arrow = active ? (sortAsc ? " ▲" : " ▼") : "";

            Button t = MakeExtChipButton(label + arrow, null);
            t.Foreground = active ? Ui.AccentText : Ui.Sub;
            t.Background = Brushes.Transparent;
            t.BorderThickness = new Thickness(0);
            t.Padding = new Thickness(0);
            t.Margin = new Thickness(0);
            t.HorizontalContentAlignment = HorizontalAlignment.Left;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Cursor = Cursors.Hand;
            if (key == FileScanner.SortKey.Size) t.HorizontalAlignment = HorizontalAlignment.Right;
            System.Windows.Automation.AutomationProperties.SetName(t, "按" + label + "排序");

            t.Click += delegate
            {
                try
            {

                    if (sort == key)
                    {
                        sortAsc = !sortAsc;   // 同列 → 切换方向
                    }
                    else
                    {
                        sort = key;
                        sortAsc = key == FileScanner.SortKey.Name || key == FileScanner.SortKey.Type;
                    }
                    SaveSort();
                    RenderList(cached);
                }
                catch (Exception ex) { Ui.LogError(ex, "切换排序"); }
            };

            return t;
        }

        /// <summary>一行：图标 + 名称 + 时间 + 大小。</summary>
        Border MakeRow(FileScanner.Entry e, int index)
        {
            try
            {

                Border b = new Border();
                b.Height = compactSourceLayout && showSourceColumn ? 52 : 44;
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
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions[0].Width = new GridLength(32);
                g.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                ApplyFileColumns(g);

                // ---- 图标 ----
                //
                // 关联图标按类型异步复用，自带图标按路径异步读取。
                FrameworkElement icon = MakeIcon(e);
                Grid.SetColumn(icon, 0);
                g.Children.Add(icon);

                TextBlock name = Ui.T(e.Name, 12.5, Ui.Fg);
                name.VerticalAlignment = VerticalAlignment.Center;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.ToolTip = e.Path;
                name.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
                if (compactSourceLayout && showSourceColumn)
                {
                    name.VerticalAlignment = VerticalAlignment.Top;
                    name.Margin = new Thickness(0, 6, Ui.S.Sm, 0);
                }
                Grid.SetColumn(name, 1);
                g.Children.Add(name);

                TextBlock time = Ui.T(FileScanner.FormatTime(e.Modified), 11, Ui.Sub);
                time.VerticalAlignment = VerticalAlignment.Center;
                time.ToolTip = e.Modified == DateTime.MinValue ? "" : e.Modified.ToString("yyyy-MM-dd HH:mm:ss");
                Grid.SetColumn(time, 2);
                g.Children.Add(time);

                TextBlock size = Ui.T(e.IsFolder ? "" : FileScanner.FormatSize(e.Size), 11, Ui.Sub);
                size.VerticalAlignment = VerticalAlignment.Center;
                size.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(size, 3);
                g.Children.Add(size);

                TextBlock source = Ui.T(SourceLabel(e.SourceRoot), 11, Ui.Sub);
                source.VerticalAlignment = VerticalAlignment.Center;
                source.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
                source.TextTrimming = TextTrimming.CharacterEllipsis;
                source.ToolTip = e.SourceRoot;
                source.Visibility = showSourceColumn ? Visibility.Visible : Visibility.Collapsed;
                Grid.SetColumn(source, compactSourceLayout ? 1 : 4);
                if (compactSourceLayout && showSourceColumn)
                {
                    source.VerticalAlignment = VerticalAlignment.Bottom;
                    source.Margin = new Thickness(0, 0, Ui.S.Sm, 5);
                }
                g.Children.Add(source);
                System.Windows.Automation.AutomationProperties.SetName(b,
                    e.Name + (e.IsFolder ? "，文件夹" : "，文件") + "，来源 " + e.SourceRoot);

                b.Child = g;

                b.MouseEnter += delegate
                {
                    if (!selected.Contains(e.Path)) b.Background = Ui.Hover;
                };
                b.MouseLeave += delegate
                {
                    if (!selected.Contains(e.Path))
                        b.Background = b.IsKeyboardFocused ? Ui.Hover : Brushes.Transparent;
                };
                b.GotKeyboardFocus += delegate
                {
                    if (VisibleIndex(e.Path) >= 0) currentPath = e.Path;
                    if (!selected.Contains(e.Path)) b.Background = Ui.Hover;
                };
                b.LostKeyboardFocus += delegate
                {
                    if (!selected.Contains(e.Path))
                        b.Background = b.IsMouseOver ? Ui.Hover : Brushes.Transparent;
                };

                // ---- 双击：打开（文件 → 默认程序；文件夹 → 资源管理器）----
                b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs ev)
                {
                    try
            {

                        dragStart = ev.GetPosition(b);   // 记录拖拽起点
                        if (ev.ClickCount == 2)
                        {
                            ev.Handled = true;
                            OpenEntry(e);
                        }
                    }
                    catch (Exception ex) { Ui.LogError(ex, "双击打开"); }
                };

                b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs ev)
                {
                    dragStart = new Point(double.NaN, double.NaN);   // 松手清起点
                    if (ev.ClickCount == 2) return;                  // 双击的 Up 不当作单击
                    HandleRowClick(e, VisibleIndex(e.Path), ev);
                    b.Focus();
                };

                b.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs ev)
                {
                    ev.Handled = true;
                    // 右键时如果这项没被选中，先只选它（资源管理器行为）
                    SelectMenuTarget(e);
                    b.Focus();
                    ShowItemMenu(e);
                };
                b.KeyDown += delegate(object s, KeyEventArgs ev)
                {
                    if (!Ui.IsContextMenuKey(Ui.EffectiveKey(ev), Keyboard.Modifiers)) return;
                    SelectMenuTarget(e);
                    ShowItemMenu(e, true);
                    ev.Handled = true;
                };

                // ---- 拖出（只允许复制）----
                b.PreviewMouseMove += delegate(object s, MouseEventArgs ev)
                {
                    try
            {

                        if (ev.LeftButton != MouseButtonState.Pressed) return;
                        if (!selected.Contains(e.Path)) return;
                        if (selected.Count == 0) return;

                        // ★ 拖拽阈值：移动距离超过系统最小拖拽距离才真正开始拖，
                        //   否则单击/双击会被误判成拖拽。
                        if (double.IsNaN(dragStart.X)) return;
                        Point now = ev.GetPosition(b);
                        double dx = Math.Abs(now.X - dragStart.X);
                        double dy = Math.Abs(now.Y - dragStart.Y);
                        if (dx < SystemParameters.MinimumHorizontalDragDistance
                            && dy < SystemParameters.MinimumVerticalDragDistance) return;

                        List<string> paths = SelectedFilePaths();
                        if (paths.Count == 0) return;

                        System.Collections.Specialized.StringCollection sc =
                            new System.Collections.Specialized.StringCollection();
                        foreach (string p in paths) sc.Add(p);

                        System.Windows.DataObject data = new System.Windows.DataObject();
                        data.SetFileDropList(sc);

                        // 打内部标记：拖回面板自身时不该被当成"外部文件拖入"。
                        //
                        // 不打标记的后果（实测）：在「文件」页拖一个文件、拖回面板
                        // 自身范围，PanelWindow 判成外部拖入 → 光标显示"可放" →
                        // 松手走「加来源」分支 → 把文件当文件夹校验 → 弹
                        // 「没有放入任何文件，跳过 1 个不是文件夹」。
                        //
                        // 用 DropCursor.InternalFilesFormat 而不是
                        // DesktopDragGesture.InternalDataFormat：后者会让
                        // OnPanelDragLeave 触发 StartDragWatch，拖到资源管理器
                        // 松手后面板会收起 —— 而"文件"页拖出是复制，用户
                        // 往往还要回来继续操作。详见该常量的注释。
                        try
                        {
                            data.SetData(DropCursor.InternalFilesFormat, "1");
                        }
                        catch (Exception ex) { Ui.Log("设置文件页内部拖动标记失败: " + ex.Message); }

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

        void UpdateRow(Border row, FileScanner.Entry latest)
        {
            // 事件闭包持有原 Entry，更新其元数据，而不是替换 Tag 后留下过期闭包。
            FileScanner.Entry item = row.Tag as FileScanner.Entry;
            Grid grid = row.Child as Grid;
            if (item == null || grid == null) return;
            bool newIcon = item.Ext != latest.Ext || item.IsFolder != latest.IsFolder;
            item.Name = latest.Name;
            item.IsFolder = latest.IsFolder;
            item.Ext = latest.Ext;
            item.Size = latest.Size;
            item.Modified = latest.Modified;
            item.SourceRoot = latest.SourceRoot;
            if (newIcon)
            {
                grid.Children.RemoveAt(0);
                grid.Children.Insert(0, MakeIcon(item));
            }
            ((TextBlock)grid.Children[1]).Text = item.Name;
            ((TextBlock)grid.Children[1]).ToolTip = item.Path;
            ((TextBlock)grid.Children[2]).Text = FileScanner.FormatTime(item.Modified);
            ((TextBlock)grid.Children[2]).ToolTip = item.Modified == DateTime.MinValue ? "" : item.Modified.ToString("yyyy-MM-dd HH:mm:ss");
            ((TextBlock)grid.Children[3]).Text = item.IsFolder ? "" : FileScanner.FormatSize(item.Size);
            TextBlock source = (TextBlock)grid.Children[4];
            source.Text = SourceLabel(item.SourceRoot);
            source.ToolTip = item.SourceRoot;
            source.Visibility = showSourceColumn ? Visibility.Visible : Visibility.Collapsed;
            ApplyFileColumns(grid);
            TextBlock name = (TextBlock)grid.Children[1];
            row.Height = compactSourceLayout && showSourceColumn ? 52 : 44;
            name.VerticalAlignment = compactSourceLayout && showSourceColumn
                ? VerticalAlignment.Top : VerticalAlignment.Center;
            name.Margin = compactSourceLayout && showSourceColumn
                ? new Thickness(0, 6, Ui.S.Sm, 0) : new Thickness(0, 0, Ui.S.Sm, 0);
            Grid.SetColumn(source, compactSourceLayout ? 1 : 4);
            source.VerticalAlignment = compactSourceLayout
                ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            source.Margin = compactSourceLayout
                ? new Thickness(0, 0, Ui.S.Sm, 5) : new Thickness(Ui.S.Sm, 0, 0, 0);
            row.BeginAnimation(UIElement.OpacityProperty, null);
            row.Opacity = 1;
            row.Visibility = Visibility.Visible;
            System.Windows.Automation.AutomationProperties.SetName(row,
                item.Name + (item.IsFolder ? "，文件夹" : "，文件") + "，来源 " + item.SourceRoot);
        }

        bool ShouldCompactSourceColumn()
        {
            return showSourceColumn && listPanel != null
                && listPanel.ActualWidth > 0 && listPanel.ActualWidth < 720;
        }

        void ApplyFileColumns(Grid grid)
        {
            if (grid == null || grid.ColumnDefinitions.Count < 5) return;
            grid.ColumnDefinitions[0].Width = new GridLength(32);
            grid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[2].Width = new GridLength(compactSourceLayout ? 88 : 110);
            grid.ColumnDefinitions[3].Width = new GridLength(compactSourceLayout ? 64 : 76);
            grid.ColumnDefinitions[4].Width = new GridLength(
                showSourceColumn && !compactSourceLayout ? 132 : 0);
        }

        // ========== 选中保持 ==========

        /// <summary>当前选中的第一项路径（没有则 null）。重建前记它，重建后恢复。</summary>
        string FirstSelectedPath()
        {
            try
            {

                foreach (FileScanner.Entry e in visible)
                    if (e != null && selected.Contains(e.Path)) return e.Path;
            }
            catch { }
            return null;
        }

        // ========== 预览 ==========

        /// <summary>
        /// 空格：打开/关闭预览。已打开时切换选中会同步更新内容。
        /// </summary>
        public void TogglePreview()
        {
            try
            {

                if (preview != null && preview.IsOpen)
                {
                    preview.Close();
                    return;
                }

                FileScanner.Entry e = CurrentEntry();
                if (e == null) return;

                if (preview == null) preview = new FilePreviewPopup(owner);
                preview.Show(e, owner);
            }
            catch (Exception ex) { Ui.LogError(ex, "切换预览"); }
        }

        /// <summary>关闭预览（切标签、Esc、删除后调用）。</summary>
        public void ClosePreview()
        {
            try { if (preview != null) preview.Close(); }
            catch { }
        }

        /// <summary>预览是否打开。</summary>
        public bool IsPreviewOpen
        {
            get { try { return preview != null && preview.IsOpen; } catch { return false; } }
        }

        // ========== 删除 ==========

        /// <summary>
        /// Delete / 右键删除的统一入口。
        ///
        /// ── 按需确认 ──
        ///
        ///   · 单个**文件** → 不弹确认，直接删，底部出「已移到回收站 [撤销]」
        ///   · 文件夹 / 一次多项 → 弹确认框（警告图标，「移到回收站 / 取消」，
        ///     默认焦点「取消」，居中于面板）
        ///
        /// ── 删除后 ──
        ///
        ///   · 该行淡出后收起
        ///   · 选中落到原位置的下一项 → 没有选上一项 → 列表空则不选
        ///   · 底部统计立即刷新
        ///
        /// ── 部分失败 ──
        ///
        /// 只对**被拒绝**的项提示提权，成功的项不重复处理。
        /// </summary>
        public void ConfirmAndDelete()
        {
            try
            {
                List<FileScanner.Entry> targets = new List<FileScanner.Entry>();
                foreach (FileScanner.Entry e in visible)
                    if (e != null && selected.Contains(e.Path)) targets.Add(e);

                if (targets.Count == 0) return;

                // ---- 是否需要确认 ----
                bool needConfirm = false;
                bool hasFolder = false;
                foreach (FileScanner.Entry t in targets)
                    if (t.IsFolder) { hasFolder = true; break; }

                if (hasFolder || targets.Count > 1) needConfirm = true;

                if (needConfirm)
                {
                    if (!ConfirmDeleteDialog(targets.Count, hasFolder)) return;
                }

                // ---- 记住删除位置（用于删后选中）----
                int firstIdx = -1;
                for (int i = 0; i < visible.Count; i++)
                    if (visible[i] != null && selected.Contains(visible[i].Path)) { firstIdx = i; break; }

                // ---- 执行删除 ----
                List<string> paths = new List<string>();
                foreach (FileScanner.Entry t in targets) paths.Add(t.Path);

                IntPtr hwnd = IntPtr.Zero;
                try
                {
                    if (owner != null)
                        hwnd = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
                }
                catch { }

                FileOps.OpResult res = FileOps.Delete(paths, null, hwnd);

                ClosePreview();

                // ---- 全成功 ----
                if (res.Ok)
                {
                    FinishDelete(targets, firstIdx, targets.Count);
                    return;
                }

                // ---- 失败：判断是否权限问题 ----
                if (IsPermissionFailure(res))
                {
                    // 只对**被拒绝**的项提权；整批拒绝时 targets 就是全部被拒的
                    // （FileOps.Delete 是「任何一项不合格就整批拒绝」，所以这里全是被拒项）
                    if (!AskElevate("删除 " + targets.Count + " 项", true)) return;

                    List<FileScanner.Entry> elevatedOk = new List<FileScanner.Entry>();
                    List<FileScanner.Entry> elevatedFail = new List<FileScanner.Entry>();
                    string firstErr = null;

                    foreach (FileScanner.Entry t in targets)
                    {
                        string e2;
                        if (Elevation.DeleteAsAdmin(t.Path, out e2)) elevatedOk.Add(t);
                        else { elevatedFail.Add(t); if (firstErr == null) firstErr = e2; }
                    }

                    if (elevatedOk.Count > 0)
                        FinishDelete(elevatedOk, firstIdx, elevatedOk.Count);

                    if (elevatedFail.Count > 0)
                    {
                        // 部分失败：只报告失败项
                        MessageBox.Show(owner,
                            "提权删除完成 " + elevatedOk.Count + " 项，失败 " + elevatedFail.Count + " 项。"
                            + (string.IsNullOrEmpty(firstErr) ? "" : "\r\n\r\n" + firstErr),
                            AppBrand.Text("四叶草 · 删除"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        // MessageBox 也会失焦，补一次
                        try { RestoreListFocus(); } catch { }
                    }
                    return;
                }

                // ---- 非权限失败 ----
                MessageBox.Show(owner, res.Message, AppBrand.Text("四叶草 · 移到回收站"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                try { RestoreListFocus(); } catch { }
            }
            catch (Exception ex) { Ui.LogError(ex, "删除文件"); }
        }

        /// <summary>
        /// 删除成功后的收尾：淡出 → 刷新 → 选中 → 统计 → 撤销条。
        /// </summary>
        void FinishDelete(List<FileScanner.Entry> deleted, int firstIdx, int count)
        {
            try
            {
                // ---- 1. 淡出被删的行（视觉反馈，不阻塞）----
                List<string> gone = new List<string>();
                foreach (FileScanner.Entry t in deleted)
                    if (t != null) gone.Add(t.Path);

                FadeOutRows(gone);

                // 先同步移除成功删除的条目，再后台重扫；下一项选择不能基于旧列表。
                HashSet<string> goneSet = new HashSet<string>(gone, StringComparer.OrdinalIgnoreCase);
                if (cached != null)
                {
                    cached.Items.RemoveAll(delegate(FileScanner.Entry item) { return goneSet.Contains(item.Path); });
                    RenderList(cached);
                }

                // ---- 2. 刷新（重扫 + 重排）----
                Refresh(true);

                // ---- 3. 选中：原位置的下一项 → 上一项 → 不选 ----
                try
                {
                    if (visible.Count == 0)
                    {
                        selected.Clear();
                        anchorIndex = -1;
                        currentPath = null;
                    }
                    else
                    {
                        int target = firstIdx;
                        if (target >= visible.Count) target = visible.Count - 1;
                        if (target < 0) target = 0;
                        selected.Clear();
                        if (visible[target] != null)
                        {
                            selected.Add(visible[target].Path);
                            anchorIndex = target;
                            currentPath = visible[target].Path;
                        }
                    }
                    UpdateSelectionVisuals();
                }
                catch { }

                // ---- 4. 底部统计立即刷新 ----
                if (cached != null)
                {
                    try { SetNotice(ScopeNotice(cached)); SetStatus(BuildStats(matchedCount)); }
                    catch { }
                }

                // ---- 5. 撤销条（单个文件才给撤销；批量/文件夹走确认框，不给撤销）----
                bool single = (deleted.Count == 1);
                bool singleFile = single && deleted[0] != null && !deleted[0].IsFolder;

                if (singleFile)
                {
                    if (undoBar == null && undoHost != null)
                        undoBar = new UndoBar(undoHost);

                    // 点按钮后焦点回到文件列表（不跳搜索框、不触发面板外收起）
                    undoBar.OnFocusBack = delegate
                    {
                        try { RestoreListFocus(); } catch { }
                    };

                    if (undoBar != null)
                    {
                        string name = deleted[0].Name;
                        List<string> undoPaths = new List<string>(gone);
                        undoBar.Show("已移到回收站：" + name, delegate
                        {
                            try
                            {
                                int n = UndoBar.RestoreFromRecycleBin(undoPaths);
                                if (n > 0)
                                {
                                    Refresh(true);
                                    MessageBox.Show(owner, "已还原 " + n + " 项。",
                                        AppBrand.Text("四叶草 · 撤销"), MessageBoxButton.OK, MessageBoxImage.Information);
                                }
                                else
                                {
                                    MessageBox.Show(owner,
                                        "自动还原失败。\r\n\r\n请手动到回收站右键该文件 → 还原。",
                                        AppBrand.Text("四叶草 · 撤销"), MessageBoxButton.OK, MessageBoxImage.Information);
                                }
                            }
                            catch (Exception ex) { Ui.LogError(ex, "撤销删除"); }
                        });
                    }
                }

                // ---- 6. 抢回焦点与前台激活 ----
                //
                // SHFileOperation（回收站删除）由 explorer.exe 代理执行，
                // 会抢走前台激活状态。不恢复的话，删完文件面板键盘就不响应了。
                try { RestoreListFocus(); } catch { }
            }
            catch (Exception ex) { Ui.LogError(ex, "删除收尾"); }
        }

        /// <summary>把指定路径的行淡出后移除（120ms，不阻塞后续刷新）。</summary>
        void FadeOutRows(List<string> paths)
        {
            try
            {
                if (paths == null || paths.Count == 0) return;
                HashSet<string> set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<string, Border> kv in rowMap)
                {
                    if (kv.Value == null || !set.Contains(kv.Key)) continue;
                    Border row = kv.Value;
                    if (Ui.ReducedMotion)
                    {
                        row.BeginAnimation(UIElement.OpacityProperty, null);
                        row.Visibility = Visibility.Collapsed;
                        continue;
                    }
                    DoubleAnimation fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120));
                    fade.Completed += delegate
                    {
                        try { row.Visibility = Visibility.Collapsed; } catch { }
                    };
                    row.BeginAnimation(UIElement.OpacityProperty, fade);
                }
            }
            catch { }
        }

        /// <summary>这个 FileOps 失败是不是「权限不足」。</summary>
        static bool IsPermissionFailure(FileOps.OpResult res)
        {
            try
            {
                if (res == null || res.Message == null) return false;

                if (res.Status == FileOps.OpStatus.Failed)
                {
                    return res.Message.IndexOf("拒绝访问", StringComparison.Ordinal) >= 0
                        || res.Message.IndexOf("权限", StringComparison.Ordinal) >= 0
                        || res.Message.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0
                        || res.Message.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// 删除确认框：警告图标，「移到回收站 / 取消」，默认焦点在「取消」，居中于面板。
        ///
        /// 为什么自己画而不是 MessageBox：MessageBox 的按钮文字固定为「确定/取消」，
        /// 无法写成「移到回收站」。而删除是不可逆操作，按钮上写明动作很重要。
        /// </summary>
        bool ConfirmDeleteDialog(int count, bool hasFolder)
        {
            try
            {
                Window dlg = new Window();
                dlg.WindowStyle = WindowStyle.None;
                dlg.AllowsTransparency = true;
                dlg.Background = Brushes.Transparent;
                dlg.ResizeMode = ResizeMode.NoResize;
                dlg.ShowInTaskbar = false;
                dlg.SizeToContent = SizeToContent.WidthAndHeight;
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                dlg.Title = AppBrand.Text("四叶草 · 移到回收站");

                bool result = false;

                Border card = new Border();
                card.CornerRadius = new CornerRadius(Ui.R.Lg);
                card.Background = Ui.Bg;
                card.BorderBrush = Ui.Edge;
                card.BorderThickness = new Thickness(1);
                card.Padding = new Thickness(Ui.S.Xl);
                card.MinWidth = 320;
                card.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35, Color = Colors.Black
                };

                StackPanel col = new StackPanel();
                col.Orientation = Orientation.Vertical;

                // 标题行：警告图标 + 文字
                StackPanel head = new StackPanel();
                head.Orientation = Orientation.Horizontal;
                head.Margin = new Thickness(0, 0, 0, Ui.S.Md);

                TextBlock ico = new TextBlock();
                ico.FontFamily = Ui.Mdl;
                ico.FontSize = 20;
                ico.Text = "\uE7BA";     // Warning
                ico.Foreground = Ui.Accent;
                ico.VerticalAlignment = VerticalAlignment.Center;
                ico.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
                head.Children.Add(ico);

                string body = count == 1
                    ? "将把 1 项移到回收站。"
                    : ("将把 " + count + " 项移到回收站。");
                if (hasFolder) body += "\r\n文件夹包含的内容会一并移到回收站。";

                TextBlock txt = Ui.T(body, 12, Ui.Fg);
                txt.TextWrapping = TextWrapping.Wrap;
                txt.MaxWidth = 280;
                txt.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(txt);

                col.Children.Add(head);

                // 按钮行
                StackPanel btns = new StackPanel();
                btns.Orientation = Orientation.Horizontal;
                btns.HorizontalAlignment = HorizontalAlignment.Right;

                Button cancelBtn = new Button();
                cancelBtn.Template = UiButtons.Template(14);
                cancelBtn.Padding = new Thickness(14, 5, 14, 5);
                cancelBtn.Cursor = System.Windows.Input.Cursors.Hand;
                cancelBtn.Background = Ui.SearchBg;
                cancelBtn.BorderBrush = Ui.Line;
                cancelBtn.BorderThickness = new Thickness(1);
                cancelBtn.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
                cancelBtn.HorizontalContentAlignment = HorizontalAlignment.Center;
                cancelBtn.VerticalContentAlignment = VerticalAlignment.Center;
                TextBlock ct = Ui.T("取消", 11.5, Ui.Fg);
                ct.VerticalAlignment = VerticalAlignment.Center;
                cancelBtn.Content = ct;
                System.Windows.Automation.AutomationProperties.SetName(cancelBtn, "取消移到回收站");
                cancelBtn.Click += delegate { dlg.Close(); };
                btns.Children.Add(cancelBtn);

                Button okBtn = new Button();
                okBtn.Template = UiButtons.Template(14);
                okBtn.Padding = new Thickness(14, 5, 14, 5);
                okBtn.Cursor = System.Windows.Input.Cursors.Hand;
                okBtn.Background = Ui.Accent;
                okBtn.BorderBrush = Ui.Accent;
                okBtn.BorderThickness = new Thickness(1);
                okBtn.HorizontalContentAlignment = HorizontalAlignment.Center;
                okBtn.VerticalContentAlignment = VerticalAlignment.Center;
                TextBlock ot = Ui.T("移到回收站", 11.5, Brushes.White);
                ot.VerticalAlignment = VerticalAlignment.Center;
                okBtn.Content = ot;
                System.Windows.Automation.AutomationProperties.SetName(okBtn, "确认移到回收站");
                okBtn.Click += delegate { result = true; dlg.Close(); };
                btns.Children.Add(okBtn);

                col.Children.Add(btns);
                card.Child = col;
                dlg.Content = card;

                // Esc = 取消（默认焦点语义）
                dlg.PreviewKeyDown += delegate(object s, System.Windows.Input.KeyEventArgs e)
                {
                    if (e.Key == System.Windows.Input.Key.Escape) { dlg.Close(); e.Handled = true; }
                };

                // 默认落在取消；只有用户主动 Tab 到删除按钮后，Enter/空格才会执行。
                dlg.Loaded += delegate { try { cancelBtn.Focus(); } catch { } };

                dlg.ShowDialog();

                // 模态框关闭后把焦点还回面板（否则键盘不响应）
                try { RestoreListFocus(); } catch { }
                return result;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "删除确认框");
                return false;
            }
        }
        // ========== 打开条目 ==========

        /// <summary>打开条目：文件 → 默认程序；文件夹 → 资源管理器。双击/回车共用。</summary>
        void OpenEntry(FileScanner.Entry e)
        {
            try
            {

                if (e == null) return;
                if (e.IsFolder) OpenInExplorer(e.Path);
                else OpenFile(e.Path);
            }
            catch (Exception ex) { Ui.LogError(ex, "打开条目"); }
        }

        // ========== 复制文件 ==========

        /// <summary>把当前选中项（含文件夹）放进剪贴板 FileDrop。</summary>
        void CopySelectedIncludingFolders()
        {
            try
            {

                List<string> paths = new List<string>();
                foreach (FileScanner.Entry e in visible)
                {
                    if (e == null || !selected.Contains(e.Path)) continue;
                    try { if (File.Exists(e.Path) || Directory.Exists(e.Path)) paths.Add(e.Path); }
                    catch { }
                }
                if (paths.Count == 0) return;

                System.Collections.Specialized.StringCollection sc =
                    new System.Collections.Specialized.StringCollection();
                foreach (string p in paths) sc.Add(p);

                System.Windows.DataObject data = new System.Windows.DataObject();
                data.SetFileDropList(sc);
                Clipboard.SetDataObject(data, true);

                SetNotice("已复制 " + paths.Count + " 项到剪贴板");
            }
            catch (Exception ex) { Ui.LogError(ex, "复制文件"); }
        }

        /// <summary>24px 稳定图标槽：立即兜底，后台原位填入系统关联图标。</summary>
        FrameworkElement MakeIcon(FileScanner.Entry e)
        {
            Grid slot = new Grid { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
            string key = e.IsFolder || !FileListIcons.HasOwnIcon(e.Ext)
                ? FileListIcons.Key(e.Ext, e.IsFolder)
                : "path:" + e.Path + ":" + e.Modified.Ticks + ":" + e.Size;
            slot.Tag = key;
            slot.Children.Add(MakeFileOutline(e.IsFolder));
            ImageSource source;
            bool own = !e.IsFolder && FileListIcons.HasOwnIcon(e.Ext);
            bool hit = own ? AppIcons.TryGetCached(e.Path, out source) : FileListIcons.TryGet(key, out source);
            if (hit) { ApplyFileIcon(slot, source); return slot; }

            // 队列不强持有页面；切页、删行、类型变化或 Dispose 后迟到结果不污染新图标。
            WeakReference page = new WeakReference(this), target = new WeakReference(slot);
            var dispatcher = listPanel.Dispatcher;
            Action<ImageSource> complete = delegate(ImageSource image)
            {
                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
                dispatcher.BeginInvoke(new Action(delegate
                {
                    FilesTabView view = page.Target as FilesTabView;
                    Grid current = target.Target as Grid;
                    if (view == null || view.disposed || current == null || Convert.ToString(current.Tag) != key) return;
                    ApplyFileIcon(current, image);
                }), System.Windows.Threading.DispatcherPriority.Background);
            };
            try
            {
                if (own) AppIcons.GetAsync(e.Path, complete);
                else FileListIcons.GetAsync(key, complete);
            }
            catch { }
            return slot;
        }

        static void ApplyFileIcon(Grid slot, ImageSource source)
        {
            if (source == null) return;
            slot.Children.Clear();
            Image image = new Image { Source = source, Width = 24, Height = 24, Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            slot.Children.Add(image);
        }

        // 用矢量轮廓兜底，不依赖系统图标字体，避免缺字时文件夹变成方框。
        FrameworkElement MakeFileOutline(bool folder)
        {
            System.Windows.Shapes.Path outline = new System.Windows.Shapes.Path();
            outline.Data = Geometry.Parse(folder
                ? "M1,5 L1,3 L8,3 L10,5 L19,5 L19,17 L1,17 Z M1,7 L19,7"
                : "M4,1 L12,1 L17,6 L17,19 L4,19 Z M12,1 L12,6 L17,6");
            outline.Stroke = folder ? Ui.Accent : Ui.Sub;
            outline.StrokeThickness = 1.3;
            outline.Fill = folder ? Ui.AccentSoft : Brushes.Transparent;
            outline.Width = 24; outline.Height = 24;
            outline.Stretch = Stretch.Uniform;
            outline.VerticalAlignment = VerticalAlignment.Center;
            outline.HorizontalAlignment = HorizontalAlignment.Center;
            outline.Tag = folder ? "file-icon:folder" : "file-icon:document";
            return outline;
        }

        // ========== 选择 ==========

        void ClearSelection()
        {
            try
            {
                if (selected.Count == 0 && anchorIndex < 0 && currentPath == null) return;
                selected.Clear();
                anchorIndex = -1;
                currentPath = null;
                dragStart = new Point(double.NaN, double.NaN);
                ClosePreview();
                UpdateSelectionVisuals();
            }
            catch (Exception ex) { Ui.LogError(ex, "清空文件选择"); }
        }

        void HandleRowClick(FileScanner.Entry e, int index, MouseButtonEventArgs ev)
        {
            try
            {

                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                currentPath = e.Path;

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

        void MoveSelection(int delta, bool extend)
        {
            try
            {

                if (visible.Count == 0) return;

                int cur = VisibleIndex(currentPath);
                if (cur < 0 && anchorIndex >= 0 && anchorIndex < visible.Count) cur = anchorIndex;

                int next = cur < 0 ? 0 : Math.Max(0, Math.Min(visible.Count - 1, cur + delta));

                selected.Clear();
                if (extend)
                {
                    if (anchorIndex < 0 || anchorIndex >= visible.Count) anchorIndex = cur < 0 ? next : cur;
                    for (int i = Math.Min(anchorIndex, next); i <= Math.Max(anchorIndex, next); i++)
                        selected.Add(visible[i].Path);
                }
                else
                {
                    selected.Add(visible[next].Path);
                    anchorIndex = next;
                }
                currentPath = visible[next].Path;
                UpdateSelectionVisuals();

                // 滚到可见
                Border row;
                if (rowMap.TryGetValue(visible[next].Path, out row) && row != null)
                {
                    row.Focus();
                    row.BringIntoView();
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "移动选择"); }
        }

        void UpdateSelectionVisuals()
        {
            try
            {
                InvalidateItemMenu();
                foreach (KeyValuePair<string, Border> kv in rowMap)
                {
                    if (kv.Value == null) continue;
                    bool on = selected.Contains(kv.Key);
                    kv.Value.Background = on ? Ui.AccentSoft
                        : kv.Value.IsKeyboardFocused || kv.Value.IsMouseOver ? Ui.Hover : Brushes.Transparent;
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

        FileScanner.Entry CurrentEntry()
        {
            int index = VisibleIndex(currentPath);
            return index >= 0 ? visible[index] : FirstSelected();
        }

        /// <summary>现有拖出流程使用的普通文件路径；剪贴板复制另含文件夹。</summary>
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

        void SelectMenuTarget(FileScanner.Entry entry)
        {
            currentPath = entry.Path;
            if (selected.Contains(entry.Path)) return;
            selected.Clear();
            selected.Add(entry.Path);
            anchorIndex = VisibleIndex(entry.Path);
            UpdateSelectionVisuals();
        }

        void RestoreListFocus(Border preferred = null)
        {
            Border focused = preferred ?? Keyboard.FocusedElement as Border;
            foreach (FileScanner.Entry item in visible)
            {
                Border candidate;
                if (selected.Contains(item.Path) && rowMap.TryGetValue(item.Path, out candidate)
                    && ReferenceEquals(candidate, focused) && candidate.IsVisible)
                {
                    if (owner != null) owner.ReActivate(candidate);
                    else candidate.Focus();
                    return;
                }
            }
            FileScanner.Entry entry = FirstSelected();
            Border row;
            IInputElement target = entry != null && rowMap.TryGetValue(entry.Path, out row) && row.IsVisible
                ? (IInputElement)row : listPanel;
            if (owner != null) owner.ReActivate(target);
            else if (target != null) Keyboard.Focus(target);
        }

        ContextMenu activeItemMenu;
        int itemMenuGeneration;

        void InvalidateItemMenu()
        {
            unchecked { itemMenuGeneration++; }
            ContextMenu previous = activeItemMenu;
            activeItemMenu = null;
            if (previous != null) previous.IsOpen = false;
        }

        bool IsMenuTargetCurrent(int ticket, string path, HashSet<string> selection)
        {
            if (ticket != itemMenuGeneration || disposed || VisibleIndex(path) < 0
                || !selected.SetEquals(selection)) return false;
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            foreach (string target in selection)
                if (!File.Exists(target) && !Directory.Exists(target)) return false;
            return true;
        }

        void ShowItemMenu(FileScanner.Entry e, bool keyboard = false)
        {
            try
            {
                if (e == null || VisibleIndex(e.Path) < 0) return;
                InvalidateItemMenu();
                int ticket = itemMenuGeneration;
                HashSet<string> selection = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
                Func<bool> valid = delegate { return IsMenuTargetCurrent(ticket, e.Path, selection); };
                ContextMenu cm = new ContextMenu();

                MenuItem open = new MenuItem();
                open.Header = "打开";
                open.Click += delegate
                {
                    if (!valid()) return;
                    if (e.IsFolder) OpenInExplorer(e.Path);
                    else OpenFile(e.Path);
                };
                cm.Items.Add(open);

                // 打开方式…：只对文件（文件夹没有"打开方式"）
                if (!e.IsFolder)
                {
                    MenuItem openWith = new MenuItem();
                    openWith.Header = "打开方式…";
                    openWith.Click += delegate { if (valid()) OpenWith(e.Path); };
                    cm.Items.Add(openWith);

                    // 以管理员身份运行：只对可执行类扩展名显示
                    if (IsRunnableExt(e.Ext))
                    {
                        MenuItem runAs = new MenuItem();
                        runAs.Header = "以管理员身份运行";
                        runAs.Click += delegate
                        {
                            if (!valid()) return;
                            if (!Elevation.RunAs(e.Path, null))
                            {
                                // UAC 被取消是静默的；只有真的失败才提示
                                // （RunAs 内部已区分，这里不再弹框）
                            }
                        };
                        cm.Items.Add(runAs);
                    }
                }

                MenuItem reveal = new MenuItem();
                reveal.Header = "在资源管理器中显示";
                reveal.Click += delegate { if (valid()) RevealInExplorer(e.Path); };
                cm.Items.Add(reveal);

                cm.Items.Add(new Separator());

                MenuItem copy = new MenuItem();
                copy.Header = "复制路径";
                copy.Click += delegate { if (valid()) CopyText(e.Path); };
                cm.Items.Add(copy);

                // 复制文件：把选中项（含文件夹）放进剪贴板 FileDrop
                MenuItem copyFile = new MenuItem();
                copyFile.Header = "复制文件";
                copyFile.Click += delegate { if (valid()) CopySelectedIncludingFolders(); };
                cm.Items.Add(copyFile);

                cm.Items.Add(new Separator());

                // 删除：进回收站，带确认
                MenuItem del = new MenuItem();
                del.Header = "删除（移到回收站）";
                del.Click += delegate { if (valid()) ConfirmAndDelete(); };
                cm.Items.Add(del);

                cm.Items.Add(new Separator());
                MenuItem props = new MenuItem();
                props.Header = "属性";
                props.Click += delegate { if (valid()) ShowProperties(e.Path); };
                cm.Items.Add(props);

                Border anchor;
                rowMap.TryGetValue(e.Path, out anchor);
                cm.PlacementTarget = anchor;
                cm.Placement = keyboard ? System.Windows.Controls.Primitives.PlacementMode.Bottom
                    : System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                cm.Closed += delegate { if (keyboard && active && valid()) RestoreListFocus(anchor); };
                activeItemMenu = cm;
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
                        AppBrand.Text("四叶草 · 文件"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string picked = PickFolder();
                if (string.IsNullOrEmpty(picked)) return;

                FileFolders.AddResult r = folders.Add(picked);
                if (r != FileFolders.AddResult.Ok)
                {
                    MessageBox.Show(AddResultText(r, picked), AppBrand.Text("四叶草 · 文件"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                NormalizeSource();
                if (folders.Items.Count > 0)
                    selectedSources.Clear();
                    selectedSources.Add(folders.Items[folders.Items.Count - 1].Path);
                SaveSource();

                selected.Clear();
                anchorIndex = -1;
                currentPath = null;
                Refresh(true);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "添加文件夹");
                MessageBox.Show("添加失败：" + ex.Message, AppBrand.Text("四叶草 · 文件"));
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
                MessageBox.Show("打不开选目录对话框：" + ex.Message, AppBrand.Text("四叶草 · 文件"));
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
                    MessageBox.Show("重命名失败。", AppBrand.Text("四叶草 · 文件"));
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
                    AppBrand.Text("四叶草 · 文件"),
                    MessageBoxButton.OKCancel, MessageBoxImage.Question);

                if (r != MessageBoxResult.OK) return;

                if (!folders.Remove(f.Path))
                {
                    MessageBox.Show("移除失败。", AppBrand.Text("四叶草 · 文件"));
                    return;
                }

                if (selectedSources.Contains(f.Path))
                {
                    selectedSources.Clear();
                    NormalizeSource();
                    SaveSource();
                }

                selected.Clear();
                anchorIndex = -1;
                currentPath = null;
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
                    MessageBox.Show("目录不存在：\r\n" + path, AppBrand.Text("四叶草 · 文件"));
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
                    MessageBox.Show(owner, "文件不存在：\r\n" + path, AppBrand.Text("四叶草 · 文件"));
                    return;
                }

                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                    {
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    // ★ 按需提权：只有「权限不足」才提示，其余照常报错
                    if (Elevation.IsAccessDenied(ex))
                    {
                        if (AskElevate("打开「" + Path.GetFileName(path) + "」", false))
                        {
                            if (!Elevation.OpenAs(path))
                                MessageBox.Show(owner, "提权打开失败或被取消。", AppBrand.Text("四叶草 · 文件"));
                        }
                        return;
                    }

                    Ui.LogError(ex, "打开文件");
                    MessageBox.Show(owner, "打不开这个文件：\r\n" + ex.Message, AppBrand.Text("四叶草 · 文件"));
                }
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "打开文件");
            }
        }

        /// <summary>
        /// 提示「权限不足，是否以管理员身份重试？」。
        ///
        /// **默认焦点在「取消」**（`MessageBoxResult.Cancel` 作为 defaultResult）。
        /// 返回 true = 用户点了「重试」。
        /// </summary>
        /// <param name="what">动作描述（如「打开 xxx」）</param>
        /// <param name="isDelete">删除场景要额外提示「永久删除」</param>
        bool AskElevate(string what, bool isDelete)
        {
            try
            {
                string msg = "权限不足，是否以管理员身份重试？\r\n\r\n" + what;
                if (isDelete)
                {
                    msg += "\r\n\r\n注意：提权删除将永久删除，不进回收站。";
                }

                MessageBoxResult r = MessageBox.Show(owner, msg,
                    AppBrand.Text("四叶草 · 需要管理员权限"),
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                    MessageBoxResult.Cancel);   // ★ 默认焦点在「取消」

                return r == MessageBoxResult.OK;
            }
            catch { return false; }
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
                MessageBox.Show("打不开「打开方式」对话框：\r\n" + ex.Message, AppBrand.Text("四叶草 · 文件"));
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
                MessageBox.Show("打不开属性对话框：\r\n" + ex.Message, AppBrand.Text("四叶草 · 文件"));
            }
        }

        void CopyText(string text)
        {
            try { Clipboard.SetText(text ?? ""); }
            catch (Exception ex) { Ui.LogError(ex, "复制路径"); }
        }

        // ========== 状态维护 ==========

        /// <summary>该来源胶囊是否处于选中态。</summary>
        /// <summary>是否是可执行类扩展名（决定右键是否显示「以管理员身份运行」）。</summary>
        static bool IsRunnableExt(string ext)
        {
            try
            {
                string e = (ext ?? "").Trim().ToLowerInvariant();
                return e == ".exe" || e == ".bat" || e == ".cmd" || e == ".msi";
            }
            catch { return false; }
        }
        bool IsCurrent(string path)
        {
            try
            {

                if (string.IsNullOrEmpty(path)) return false;
                // 空集合 = 没有任何来源被选中（胶囊全不高亮），
                // 但**过滤时显示全部来源的文件**（见 RenderList 的 selectedSources.Count == 0 分支）
                if (selectedSources.Count == 0) return false;
                return selectedSources.Contains(path);
            }
            catch { return false; }
        }

        /// <summary>
        /// 把 selectedSources 修正成有效值：
        ///   · 剔除已从 FileFolders 移除的路径
        ///   · 清空后保持空（= 全选），不回退到第一个 —— 多选语义下「空 = 全部」
        /// </summary>
        void NormalizeSource()
        {
            try
            {

                if (folders == null || folders.Items.Count == 0)
                {
                    selectedSources.Clear();
                    return;
                }

                if (selectedSources.Count == 0) return;   // 空 = 全选，保持不变

                // 剔除已不在清单里的
                List<string> alive = new List<string>();
                foreach (FileFolders.Folder f in folders.Items)
                {
                    if (f == null) continue;
                    if (selectedSources.Contains(f.Path)) alive.Add(f.Path);
                }

                if (alive.Count != selectedSources.Count)
                {
                    selectedSources.Clear();
                    foreach (string s in alive) selectedSources.Add(s);
                }
            }
            catch { }
        }

        void SaveSource()
        {
            try
            {

                if (settings == null) return;
                settings.FilesSources = new List<string>(selectedSources);
                settings.Save();
            }
            catch (Exception ex) { Ui.LogError(ex, "保存文件来源"); }
        }

        void SaveSort()
        {
            try
            {

                if (settings == null) return;
                settings.FilesSort = SortToString(sort) + (sortAsc ? ":asc" : ":desc");
                settings.Save();
            }
            catch (Exception ex) { Ui.LogError(ex, "保存文件排序"); }
        }

        /// <summary>解析 `列:方向`（兼容旧值「只有列名」→ 降序）。</summary>
        static void ParseSortSetting(string s, out FileScanner.SortKey key, out bool asc)
        {
            key = FileScanner.SortKey.Modified;
            asc = false;
            try
            {

                string v = (s ?? "").Trim().ToLowerInvariant();
                if (v.Length == 0) return;

                string col = v;
                int colon = v.IndexOf(':');
                if (colon > 0)
                {
                    col = v.Substring(0, colon).Trim();
                    asc = (v.Substring(colon + 1).Trim() == "asc");
                }

                switch (col)
                {
                    case "name": key = FileScanner.SortKey.Name; break;
                    case "size": key = FileScanner.SortKey.Size; break;
                    default: key = FileScanner.SortKey.Modified; break;
                }
            }
            catch { }
        }

        static string SortToString(FileScanner.SortKey k)
        {
            switch (k)
            {
                case FileScanner.SortKey.Name: return "name";
                case FileScanner.SortKey.Size: return "size";
                default: return "modified";
            }
        }

        void SetNotice(string text)
        {
            try
            {

                if (noticeText == null) return;
                noticeText.Text = text ?? "";
                // 普通范围一行省略；异常和空结果显式换行，不能把“不完整”提示截掉。
                noticeText.TextWrapping = text != null && text.Contains("\n") ? TextWrapping.Wrap : TextWrapping.NoWrap;
                noticeText.TextTrimming = noticeText.TextWrapping == TextWrapping.Wrap
                    ? TextTrimming.None : TextTrimming.CharacterEllipsis;
                noticeText.ToolTip = (text ?? "") + "\n仅浏览来源第一层，不搜索子文件夹。"
                    + (cached == null ? "" : "\n共扫描 " + cached.ScannedCount + " 项；" + matchedCount + " 项符合当前筛选。");
                noticeText.Visibility = string.IsNullOrEmpty(text)
                    ? Visibility.Collapsed : Visibility.Visible;
            }
            catch { }
        }
    }
}
