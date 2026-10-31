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

        readonly StackPanel chipRow;      // 第一行：来源
        readonly StackPanel extRow;       // 第二行：后缀
        readonly StackPanel listPanel;
        readonly TextBlock noticeText;

        FileFolders folders;

        // ---- 筛选状态（只属于「文件」页，跨标签保留）----
        /// <summary>选中的来源路径集合。空 = 全选（显示所有来源）。</summary>
        readonly HashSet<string> selectedSources =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>选中的后缀分组 key 集合。空 = 全部后缀。</summary>
        readonly HashSet<string> selectedExts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 是否显示文件夹（后缀行最前面的「文件夹」标签）。
        ///
        /// **默认 false** —— 文件夹不再混在文件列表里，要看文件夹单独点这个标签。
        /// 只存会话内，不写 settings（和后缀筛选一致）。
        /// </summary>
        bool showFolders = false;

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

        /// <summary>行控件缓存：路径 → Border（重绘选中态用，不必重建整列表）。</summary>
        readonly Dictionary<string, Border> rowMap =
            new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);

        /// <summary>图标缓存：扩展名 → ImageSource（null = 用字形兜底）。</summary>
        readonly Dictionary<string, ImageSource> iconByExt =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        /// <summary>空格预览浮层。懒创建（第一次按空格才建）。</summary>
        FilePreviewPopup preview;

        /// <summary>拖拽起始点（用于阈值判断，避免单击/双击误触发拖拽）。</summary>
        Point dragStart = new Point(double.NaN, double.NaN);

        /// <summary>底部「撤销」提示条。懒创建。</summary>
        UndoBar undoBar;

        /// <summary>提示条的宿主容器（filesPanel 的末尾）。</summary>
        StackPanel undoHost;

        public FilesTabView(PanelWindow owner, Settings settings, string baseDir,
            StackPanel chipHost, StackPanel extHost, StackPanel listHost, TextBlock noticeHost,
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
            }
            catch (Exception ex) { Ui.LogError(ex, "重读文件夹清单"); }
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
            }
        }

        /// <summary>
        /// 进入「文件」标签时调用。**重置全部瞬时状态**，避免与上一次会话串联。
        ///
        /// 为什么必须重置：
        ///   · filterQuery —— 上次在文件页搜过 "abc"，切走再切回不该还在过滤
        ///   · selected / anchorIndex —— 上次选中的项可能已经不存在了
        ///   · cached —— 强制重读，避免显示陈旧目录内容
        ///
        /// 保留的：currentSource / sort / folders —— 那是用户显式选择的偏好，
        /// 存在 settings 里，跨标签切换应该保持。
        /// </summary>
        public void OnEnter()
        {
            try
            {
                filterQuery = "";
                selected.Clear();
                anchorIndex = -1;
                cached = null;
                lastLoad = DateTime.MinValue;
                visible = new List<FileScanner.Entry>();
                rowMap.Clear();
                iconByExt.Clear();
            }
            catch (Exception ex) { Ui.LogError(ex, "进入文件标签"); }
        }

        /// <summary>
        /// 离开「文件」标签时调用。释放瞬时状态，让下次进入从干净状态开始。
        ///
        /// 不清 folders —— 那是从磁盘读的配置，重读有成本，且它没有"跨标签污染"问题。
        /// </summary>
        public void OnLeave()
        {
            try
            {
                ClosePreview();

                filterQuery = "";
                selected.Clear();
                anchorIndex = -1;
                cached = null;
                lastLoad = DateTime.MinValue;
                visible = new List<FileScanner.Entry>();
                rowMap.Clear();
                // 清空列表控件，避免切走后还占着内存里的行对象
                if (listPanel != null) listPanel.Children.Clear();
                SetNotice(null);
            }
            catch (Exception ex) { Ui.LogError(ex, "离开文件标签"); }
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
                        selected.Clear();
                        anchorIndex = -1;
                        UpdateSelectionVisuals();
                        return true;
                    }
                    return false;   // 都清完了 → 交给上层（清搜索框 / 收起面板）
                }

                if (visible.Count == 0) return false;

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
                    FileScanner.Entry e = FirstSelected();
                    if (e != null) { OpenEntry(e); return true; }
                    return false;
                }

                // ↑↓ 移动选中（预览打开时同步更新内容）
                if (key == Key.Up || key == Key.Down)
                {
                    MoveSelection(key == Key.Up ? -1 : 1);
                    if (IsPreviewOpen)
                    {
                        FileScanner.Entry e = FirstSelected();
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
                    // ★ 复选：已选中就取消，未选中就加入，**不清空其他已选项**
                    if (!selectedSources.Remove(f.Path))
                        selectedSources.Add(f.Path);

                    SaveSource();
                    Refresh(true);   // 重新扫描 + 走「选中保持」
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

        // ========== 第二行：后缀筛选 ==========

        void BuildExtRow()
        {
            try
            {

                if (extRow == null) return;
                extRow.Children.Clear();

                // ★「文件夹」标签（最前面）—— 独立于后缀，可单独点选
                extRow.Children.Add(MakeFolderChip());

                // 7 个固定分组
                foreach (FileExtGroups.Group g in builtinGroups)
                    extRow.Children.Add(MakeExtChip(g));

                // 用户自定义后缀（每个单独一组）
                foreach (FileExtGroups.Group g in customGroups)
                    extRow.Children.Add(MakeExtChip(g));

                // 「其他」组（排除法）
                extRow.Children.Add(MakeExtChip(FileExtGroups.Other()));

                // 末尾「+」加自定义后缀
                Border add = MakeExtAddButton();
                extRow.Children.Add(add);

                // 刷新按钮（末尾）
                Border refresh = MakeExtChipButton("刷新 (F5)", delegate { Refresh(true); });
                refresh.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
                extRow.Children.Add(refresh);
            }
            catch (Exception ex) { Ui.LogError(ex, "构建后缀行"); }
        }

        /// <summary>
        /// 「文件夹」标签 —— 与后缀标签并列，但语义独立：
        ///   · 未选中（默认）：列表里**不出现**任何文件夹
        ///   · 选中：显示来源下的文件夹（可与后缀并集，此时文件夹排在文件前）
        /// </summary>
        Border MakeFolderChip()
        {
            bool on = showFolders;

            Border b = new Border();
            b.Height = 24;
            b.CornerRadius = new CornerRadius(b.Height / 2);
            b.Padding = new Thickness(10, 0, 10, 0);
            b.Margin = new Thickness(0, 0, Ui.S.Xs, 0);
            b.Cursor = Cursors.Hand;
            b.Background = on ? Ui.Accent : Brushes.Transparent;
            b.BorderBrush = on ? Ui.Accent : Ui.Line;
            b.BorderThickness = new Thickness(1);
            b.Tag = "__folders__";
            b.ToolTip = on
                ? "正在显示文件夹；再点一次只看文件"
                : "点一下显示文件夹（默认只显示文件）";

            TextBlock t = Ui.T("文件夹", 10.5, on ? Brushes.White : Ui.Sub);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            b.MouseLeftButtonUp += delegate
            {
                try
            {

                    showFolders = !showFolders;
                    BuildExtRow();
                    RenderList(cached);
                }
                catch (Exception ex) { Ui.LogError(ex, "切换文件夹显示"); }
            };

            return b;
        }

        Border MakeExtChip(FileExtGroups.Group g)
        {
            bool on = selectedExts.Contains(g.Key);

            Border b = new Border();
            b.Height = 24;
            b.CornerRadius = new CornerRadius(b.Height / 2);
            b.Padding = new Thickness(10, 0, 10, 0);
            b.Margin = new Thickness(0, 0, Ui.S.Xs, 0);
            b.Cursor = Cursors.Hand;
            b.Background = on ? Ui.Accent : Brushes.Transparent;
            b.BorderBrush = on ? Ui.Accent : Ui.Line;
            b.BorderThickness = new Thickness(1);
            b.Tag = g.Key;

            TextBlock t = Ui.T(g.Label, 10.5, on ? Brushes.White : Ui.Sub);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            // 自定义组带提示：显示它覆盖的扩展名
            if (g.IsCustom)
                b.ToolTip = "自定义后缀：" + string.Join(" / ", g.Exts.ToArray());
            else if (g.IsOther)
                b.ToolTip = "不属于任何固定分组、也没有自定义后缀的扩展名；无扩展名的文件也算这里";

            b.MouseLeftButtonUp += delegate
            {
                try
            {

                    if (!selectedExts.Remove(g.Key)) selectedExts.Add(g.Key);
                    BuildExtRow();       // 重绘选中态
                    RenderList(cached);  // 立即重新过滤
                }
                catch (Exception ex) { Ui.LogError(ex, "切换后缀筛选"); }
            };

            return b;
        }

        Border MakeExtChipButton(string label, Action onClick)
        {
            Border b = new Border();
            b.Height = 24;
            b.CornerRadius = new CornerRadius(b.Height / 2);
            b.Padding = new Thickness(10, 0, 10, 0);
            b.Margin = new Thickness(0, 0, Ui.S.Xs, 0);
            b.Cursor = Cursors.Hand;
            b.Background = Ui.SearchBg;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(1);

            TextBlock t = Ui.T(label, 10.5, Ui.Sub);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = Ui.SearchBg; };
            b.MouseLeftButtonUp += delegate { if (onClick != null) onClick(); };
            return b;
        }

        /// <summary>末尾「+」：加自定义后缀。</summary>
        Border MakeExtAddButton()
        {
            Border b = MakeExtChipButton("+", delegate { PromptAddExt(); });
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
                    MessageBox.Show("没有可添加的新后缀。", "四叶草 · 文件");
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

                if (!HasSources)
                {
                    ClearList();
                    SetNotice("点上面的「+」添加要读取的文件夹");
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
                    SetNotice("点上面的「+」添加要读取的文件夹");
                    cached = null;
                    return;
                }

                SetNotice("正在读取…");

                FileScanner.SortKey sk = sort;
                bool asc = sortAsc;

                FileScanner.Result r = null;
                System.Threading.Thread t = new System.Threading.Thread(delegate()
                {
                    try { r = FileScanner.ListMany(srcs, sk, asc); }
                    catch (Exception ex)
                    {
                        r = new FileScanner.Result();
                        r.Error = "读取失败: " + ex.Message;
                    }
                });
                t.IsBackground = true;
                t.Start();
                t.Join(8000);

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

        /// <summary>清空列表控件与瞬时状态（不动筛选/排序状态）。</summary>
        void ClearList()
        {
            if (listPanel != null) listPanel.Children.Clear();
            visible = new List<FileScanner.Entry>();
            rowMap.Clear();
            selected.Clear();
            anchorIndex = -1;
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

                if (listPanel == null) return;

                // ★ 重建前记住当前选中，重建后尽量恢复（切筛选/排序/刷新都走这里）
                string keepPath = FirstSelectedPath();

                listPanel.Children.Clear();
                rowMap.Clear();

                if (r == null)
                {
                    SetNotice(null);
                    visible = new List<FileScanner.Entry>();
                    return;
                }

                if (!string.IsNullOrEmpty(r.Error))
                {
                    SetNotice(r.Error);
                    visible = new List<FileScanner.Entry>();
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

                // ---- 3. 类型过滤：文件夹（布尔）+ 后缀（集合），两者独立 ----
                //
                // showFolders 与 selectedExts 是**两个独立状态**，不互相塞：
                //   · showFolders   → 是否放行文件夹
                //   · selectedExts  → 放行哪些后缀的文件（空 = 不限后缀，但受 showFolders 约束）
                //
                // 四种组合：
                //   后缀空 + 文件夹关 → 只放行文件（默认）
                //   后缀空 + 文件夹开 → 只放行文件夹（**文件一律排除**）
                //   后缀非空 + 文件夹关 → 只放行匹配的文件
                //   后缀非空 + 文件夹开 → 文件夹 + 匹配的文件
                List<FileScanner.Entry> byType = new List<FileScanner.Entry>();
                foreach (FileScanner.Entry e in byQuery)
                {
                    // 判定逻辑集中在 FileExtGroups.KeepEntry（纯函数，可单测）
                    if (FileExtGroups.KeepEntry(e.IsFolder, e.Ext, showFolders,
                            selectedExts, builtinGroups, customGroups))
                        byType.Add(e);
                }

                // ---- 4. 排序（Sort 内部保证文件夹在前）----
                FileScanner.Sort(byType, sort, sortAsc);

                if (byType.Count == 0)
                {
                    visible = new List<FileScanner.Entry>();
                    if (filterQuery.Length > 0)
                        SetNotice("没有匹配「" + filterQuery + "」的文件");
                    else if (selectedSources.Count > 0 || selectedExts.Count > 0 || showFolders)
                        SetNotice("当前筛选条件下没有文件");
                    else
                        SetNotice("这个文件夹是空的");
                    return;
                }

                visible = byType;
                PruneSelection();

                // ---- 5. 表头 ----
                listPanel.Children.Add(MakeHeader());

                // ---- 6. 行 ----
                int shown = 0;
                foreach (FileScanner.Entry e in byType)
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

                // ---- 6.5 恢复选中：还在就保持，不在就选第一项 ----
                RestoreSelection(keepPath);

                UpdateSelectionVisuals();

                // ---- 7. 底部统计 ----
                SetNotice(BuildStats(byType.Count, r.ScannedCount));
            }
            catch (Exception ex) { Ui.LogError(ex, "渲染文件列表"); }
        }

        /// <summary>
        /// 底部统计，按当前结果走：
        ///   · 只有文件夹 → 「显示 N 个文件夹」
        ///   · 只有文件   → 「显示 N 个文件」
        ///   · 两者都有   → 「显示 N 项（x 个文件夹 / y 个文件）」
        /// </summary>
        string BuildStats(int filtered, int scanned)
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
                    sb.Append("显示 ");
                    sb.Append(files);
                    sb.Append(" 个文件");
                }
                else if (files == 0)
                {
                    // 只有文件夹
                    sb.Append("显示 ");
                    sb.Append(folders);
                    sb.Append(" 个文件夹");
                }
                else
                {
                    // 两者都有
                    sb.Append("显示 ");
                    sb.Append(folders + files);
                    sb.Append(" 项（");
                    sb.Append(folders);
                    sb.Append(" 个文件夹 / ");
                    sb.Append(files);
                    sb.Append(" 个文件）");
                }

                if (scanned > filtered)
                {
                    sb.Append(" · 共扫描 ");
                    sb.Append(scanned);
                    sb.Append(" 项");
                }
                if (filtered > FileScanner.MaxReturn)
                {
                    sb.Append(" · 仅显示前 ");
                    sb.Append(FileScanner.MaxReturn);
                    sb.Append(" 项");
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
                g.ColumnDefinitions[0].Width = new GridLength(32);
                g.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
                g.ColumnDefinitions[2].Width = new GridLength(110);
                g.ColumnDefinitions[3].Width = new GridLength(76);

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

            TextBlock t = Ui.T(label + arrow, 10.5, active ? Ui.Accent : Ui.Sub);
            t.VerticalAlignment = VerticalAlignment.Center;
            t.Cursor = Cursors.Hand;
            if (key == FileScanner.SortKey.Size) t.HorizontalAlignment = HorizontalAlignment.Right;

            t.MouseLeftButtonUp += delegate
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
                        sortAsc = false;      // 换列 → 默认降序
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

        /// <summary>
        /// 重建后恢复选中：
        ///   · 原选中还在列表里 → 保持
        ///   · 不在 → 选中第一项
        ///   · 列表空 → 不选
        /// </summary>
        void RestoreSelection(string keepPath)
        {
            try
            {

                selected.Clear();
                anchorIndex = -1;

                if (visible == null || visible.Count == 0) return;

                if (!string.IsNullOrEmpty(keepPath))
                {
                    for (int i = 0; i < visible.Count; i++)
                    {
                        if (visible[i] != null &&
                            string.Equals(visible[i].Path, keepPath, StringComparison.OrdinalIgnoreCase))
                        {
                            selected.Add(visible[i].Path);
                            anchorIndex = i;
                            return;
                        }
                    }
                }

                // 没找到 → 选第一项
                if (visible[0] != null)
                {
                    selected.Add(visible[0].Path);
                    anchorIndex = 0;
                }
            }
            catch { }
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

                FileScanner.Entry e = FirstSelected();
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
        ///   · 文件夹 / 一次超过 5 项 → 弹确认框（警告图标，「移到回收站 / 取消」，
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

                if (hasFolder || targets.Count > 5) needConfirm = true;

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
                            "四叶草 · 删除", MessageBoxButton.OK, MessageBoxImage.Warning);
                        // MessageBox 也会失焦，补一次
                        try { if (owner != null) owner.ReActivate(); } catch { }
                    }
                    return;
                }

                // ---- 非权限失败 ----
                MessageBox.Show(owner, res.Message, "四叶草 · 移到回收站",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                try { if (owner != null) owner.ReActivate(); } catch { }
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

                // ---- 2. 刷新（重扫 + 重排）----
                Refresh(true);

                // ---- 3. 选中：原位置的下一项 → 上一项 → 不选 ----
                try
                {
                    if (visible.Count == 0)
                    {
                        selected.Clear();
                        anchorIndex = -1;
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
                        }
                    }
                    UpdateSelectionVisuals();
                }
                catch { }

                // ---- 4. 底部统计立即刷新 ----
                if (cached != null)
                {
                    try { SetNotice(BuildStats(visible.Count, cached.ScannedCount)); }
                    catch { }
                }

                // ---- 5. 撤销条（单个文件才给撤销；批量/文件夹走确认框，不给撤销）----
                bool single = (deleted.Count == 1);
                bool singleFile = single && deleted[0] != null && !deleted[0].IsFolder;

                if (singleFile)
                {
                    if (undoBar == null && undoHost != null)
                        undoBar = new UndoBar(undoHost);

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
                                        "四叶草 · 撤销", MessageBoxButton.OK, MessageBoxImage.Information);
                                }
                                else
                                {
                                    MessageBox.Show(owner,
                                        "自动还原失败。\r\n\r\n请手动到回收站右键该文件 → 还原。",
                                        "四叶草 · 撤销", MessageBoxButton.OK, MessageBoxImage.Information);
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
                try { if (owner != null) owner.ReActivate(); } catch { }
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
                dlg.Title = "四叶草 · 移到回收站";

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

                Border cancelBtn = new Border();
                cancelBtn.CornerRadius = new CornerRadius(14);
                cancelBtn.Padding = new Thickness(14, 5, 14, 5);
                cancelBtn.Cursor = System.Windows.Input.Cursors.Hand;
                cancelBtn.Background = Ui.SearchBg;
                cancelBtn.BorderBrush = Ui.Line;
                cancelBtn.BorderThickness = new Thickness(1);
                cancelBtn.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
                TextBlock ct = Ui.T("取消", 11.5, Ui.Fg);
                ct.VerticalAlignment = VerticalAlignment.Center;
                cancelBtn.Child = ct;
                cancelBtn.MouseLeftButtonUp += delegate { dlg.Close(); };
                btns.Children.Add(cancelBtn);

                Border okBtn = new Border();
                okBtn.CornerRadius = new CornerRadius(14);
                okBtn.Padding = new Thickness(14, 5, 14, 5);
                okBtn.Cursor = System.Windows.Input.Cursors.Hand;
                okBtn.Background = Ui.Accent;
                okBtn.BorderBrush = Ui.Accent;
                okBtn.BorderThickness = new Thickness(1);
                TextBlock ot = Ui.T("移到回收站", 11.5, Brushes.White);
                ot.VerticalAlignment = VerticalAlignment.Center;
                okBtn.Child = ot;
                okBtn.MouseLeftButtonUp += delegate { result = true; dlg.Close(); };
                btns.Children.Add(okBtn);

                col.Children.Add(btns);
                card.Child = col;
                dlg.Content = card;

                // Esc = 取消（默认焦点语义）
                dlg.PreviewKeyDown += delegate(object s, System.Windows.Input.KeyEventArgs e)
                {
                    if (e.Key == System.Windows.Input.Key.Escape) { dlg.Close(); e.Handled = true; }
                    else if (e.Key == System.Windows.Input.Key.Enter)
                    {
                        // 回车不默认删除 —— 默认焦点在「取消」，这里也不给 Enter 删除
                        e.Handled = true;
                    }
                };

                dlg.ShowDialog();

                // 模态框关闭后把焦点还回面板（否则键盘不响应）
                try { if (owner != null) owner.ReActivate(); } catch { }
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

                    // 以管理员身份运行：只对可执行类扩展名显示
                    if (IsRunnableExt(e.Ext))
                    {
                        MenuItem runAs = new MenuItem();
                        runAs.Header = "以管理员身份运行";
                        runAs.Click += delegate
                        {
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
                reveal.Click += delegate { RevealInExplorer(e.Path); };
                cm.Items.Add(reveal);

                cm.Items.Add(new Separator());

                MenuItem copy = new MenuItem();
                copy.Header = "复制路径";
                copy.Click += delegate { CopyText(e.Path); };
                cm.Items.Add(copy);

                // 复制文件：把选中项（含文件夹）放进剪贴板 FileDrop
                MenuItem copyFile = new MenuItem();
                copyFile.Header = "复制文件";
                copyFile.Click += delegate { CopySelectedIncludingFolders(); };
                cm.Items.Add(copyFile);

                cm.Items.Add(new Separator());

                // 删除：进回收站，带确认
                MenuItem del = new MenuItem();
                del.Header = "删除（移到回收站）";
                del.Click += delegate { ConfirmAndDelete(); };
                cm.Items.Add(del);

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
                    selectedSources.Clear();
                    selectedSources.Add(folders.Items[folders.Items.Count - 1].Path);
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

                if (selectedSources.Contains(f.Path))
                {
                    selectedSources.Clear();
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
                    MessageBox.Show(owner, "文件不存在：\r\n" + path, "四叶草 · 文件");
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
                                MessageBox.Show(owner, "提权打开失败或被取消。", "四叶草 · 文件");
                        }
                        return;
                    }

                    Ui.LogError(ex, "打开文件");
                    MessageBox.Show(owner, "打不开这个文件：\r\n" + ex.Message, "四叶草 · 文件");
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
                    "四叶草 · 需要管理员权限",
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
                noticeText.Visibility = string.IsNullOrEmpty(text)
                    ? Visibility.Collapsed : Visibility.Visible;
            }
            catch { }
        }
    }
}
