using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 虚拟桌面的图标视图：大图标 + 名称、**自由摆放**、框选、多选。
    ///
    /// ── 为什么用 Canvas 而不是 WrapPanel ──
    ///
    /// 桌面图标的本质是「每个图标有独立坐标」，WrapPanel 会自动排列，
    /// 和「拖到哪停在哪」根本冲突。Canvas 天然支持绝对定位。
    ///
    /// ── 坐标存哪 ──
    ///
    /// `VirtualDesks` 里按「文件名 → (x,y)」。本控件只管画和改，
    /// 写盘由调用方（PanelWindow）在拖动结束/整理后统一 Save。
    ///
    /// ── 交互 ──
    ///
    ///   · 拖动图标   自由移动，松手写坐标
    ///   · 框选       空白处按下拖动出选框
    ///   · Ctrl 点选  切换单项
    ///   · Shift 连选 按列表顺序范围选择
    ///   · Ctrl+A     全选
    ///   · 双击       打开
    ///   · 右键       空白 / 图标 两套菜单
    ///
    /// ── 已知边界 ──
    ///
    /// 第 1 步**不做**拖入拖出（那是第 2 步）。本控件只处理面板内部的
    /// 鼠标事件，不用 WPF 的 DragDrop —— 这样第 2 步接入系统拖入时
    /// 两者不会互相打架。
    /// </summary>
    class DesktopView
    {
        // ---- 布局常量 ----
        /// <summary>图标格子的宽（含留白）。</summary>
        public const double CellW = 96;

        /// <summary>图标格子的高（含留白）。</summary>
        public const double CellH = 96;

        /// <summary>图标本体的边长。</summary>
        public const double IconSize = 48;

        /// <summary>内容区左上角留白，避免图标贴着边。</summary>
        public const double PadX = 8;
        public const double PadY = 8;

        // ---- 宿主 ----
        readonly PanelWindow owner;
        readonly Canvas canvas;
        readonly Canvas overlay;      // 框选矩形画在这层

        // ---- 数据 ----
        readonly VirtualDesks desks;
        string deskName;

        /// <summary>当前显示的条目（按名称排序，保持选择顺序稳定）。</summary>
        readonly List<Item> items = new List<Item>();

        /// <summary>当前选中（文件名集合）。</summary>
        readonly HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ---- 拖动状态 ----
        bool dragging;
        Point dragStart;                  // 相对 canvas
        readonly Dictionary<string, Point> dragOrigins =
            new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        Item dragAnchor;                  // 按下的那个图标
        bool dragMoved;
        double dragPreviewDx;
        double dragPreviewDy;

        /// <summary>按下时刻（用于「按住超时即拖出」判定）。</summary>
        DateTime dragPressTime;

        /// <summary>本次拖动已经转成系统拖出（防止重复发起）。</summary>
        bool dragOutStarted;

        // ---- 框选状态 ----
        bool bandSelecting;
        Point bandStart;
        System.Windows.Shapes.Rectangle bandRect;

        /// <summary>一个图标条目。</summary>
        class Item
        {
            public string Name = "";      // 文件名（含扩展名）
            public string FullPath = "";
            public bool IsFolder;
            public double X, Y;
            public Border Host;           // 整个格子（可点区域）
            public TranslateTransform DragTransform; // 拖动预览：不触发布局
            public bool IsSelected;
        }

        /// <summary>坐标变化后的回调（让宿主写盘）。</summary>
        public Action OnLayoutChanged;

        public DesktopView(PanelWindow owner, Canvas canvas, Canvas overlay,
            VirtualDesks desks, string deskName)
        {
            this.owner = owner;
            this.canvas = canvas;
            this.overlay = overlay;
            this.desks = desks;
            this.deskName = deskName;

            HookInput();
        }

        // ========== 载入 ==========

        /// <summary>
        /// 切到某个虚拟桌面并重建图标。
        ///
        /// 文件夹不存在时自动创建（新建标签时目录可能还没建出来）。
        /// </summary>
        public void Load(string name)
        {
            try
            {
                deskName = name;
                canvas.Children.Clear();
                items.Clear();
                selected.Clear();
                dragOrigins.Clear();
                HideBand();

                if (desks == null || string.IsNullOrEmpty(name)) return;

                string dir = desks.FolderOf(name);
                if (string.IsNullOrEmpty(dir)) return;

                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    Ui.LogError(ex, "创建虚拟桌面文件夹");
                    return;
                }

                // ---- 列目录 ----
                List<string> files = new List<string>();
                try
                {
                    foreach (string f in Directory.GetFiles(dir)) files.Add(f);
                    foreach (string d in Directory.GetDirectories(dir)) files.Add(d);
                }
                catch (Exception ex)
                {
                    Ui.LogError(ex, "列虚拟桌面目录");
                    return;
                }

                files.Sort(StringComparer.OrdinalIgnoreCase);

                // ---- 建条目 ----
                double nextFreeX = PadX;
                double nextFreeY = PadY;
                double viewH = Math.Max(1, canvas.ActualHeight);

                foreach (string path in files)
                {
                    Item it = new Item();
                    it.Name = Path.GetFileName(path);
                    it.FullPath = path;
                    try { it.IsFolder = Directory.Exists(path); } catch { it.IsFolder = false; }

                    double x, y;
                    if (desks.TryGetPos(name, it.Name, out x, out y))
                    {
                        it.X = x;
                        it.Y = y;
                    }
                    else
                    {
                        // 没有坐标的新文件：放到当前空位
                        FindFreeSlot(ref nextFreeX, ref nextFreeY, viewH);
                        it.X = nextFreeX;
                        it.Y = nextFreeY;
                        desks.SetPos(name, it.Name, it.X, it.Y);
                    }

                    items.Add(it);
                    canvas.Children.Add(BuildTile(it));
                }

                // 清掉磁盘上已不存在的文件的坐标记录
                PruneStalePositions();

                if (OnLayoutChanged != null) { try { OnLayoutChanged(); } catch { } }

                Ui.Log("虚拟桌面「" + name + "」载入 " + items.Count + " 项");
            }
            catch (Exception ex) { Ui.LogError(ex, "载入虚拟桌面"); }
        }

        /// <summary>找一个当前没被占用的格子（按列往下走，到底换列）。</summary>
        void FindFreeSlot(ref double x, ref double y, double viewH)
        {
            try
            {
                double maxY = Math.Max(CellH, viewH - CellH);

                for (int guard = 0; guard < 2000; guard++)
                {
                    if (IsSlotFree(x, y))
                    {
                        // 下一个候选位置
                        y += CellH;
                        if (y > maxY) { y = PadY; x += CellW; }
                        return;
                    }

                    y += CellH;
                    if (y > maxY) { y = PadY; x += CellW; }
                }
            }
            catch { }
        }

        bool IsSlotFree(double x, double y)
        {
            try
            {
                foreach (Item it in items)
                {
                    if (Math.Abs(it.X - x) < CellW * 0.6 && Math.Abs(it.Y - y) < CellH * 0.6)
                        return false;
                }
            }
            catch { }
            return true;
        }

        void PruneStalePositions()
        {
            try
            {
                if (desks == null) return;

                HashSet<string> live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Item it in items) live.Add(it.Name);

                VirtualDesks.Desk desk;
                if (!desks.Desks.TryGetValue(deskName, out desk) || desk == null) return;

                List<string> dead = new List<string>();
                foreach (string k in desk.Items.Keys)
                {
                    if (!live.Contains(k)) dead.Add(k);
                }
                foreach (string k in dead) desks.RemovePos(deskName, k);
            }
            catch { }
        }

        // ========== 建格子 ==========

        FrameworkElement BuildTile(Item it)
        {
            Border cell = new Border();
            cell.Width = CellW;
            cell.Height = CellH;
            cell.CornerRadius = new CornerRadius(Ui.R.Sm);
            cell.Background = Brushes.Transparent;
            cell.Cursor = Cursors.Hand;
            cell.Tag = it;

            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Vertical;
            sp.VerticalAlignment = VerticalAlignment.Center;

            // 图标
            Image img = new Image();
            img.Width = IconSize;
            img.Height = IconSize;
            img.Stretch = Stretch.Uniform;
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            try
            {
                ImageSource src = AppIcons.Get(it.FullPath);
                if (src != null) img.Source = src;
            }
            catch { }
            sp.Children.Add(img);

            // 名称（最多两行，超出省略）
            TextBlock name = Ui.T(it.Name, 11, Ui.Fg);
            name.TextAlignment = TextAlignment.Center;
            name.TextWrapping = TextWrapping.Wrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxHeight = 30;
            name.Margin = new Thickness(2, 4, 2, 0);
            name.ToolTip = it.Name;    // 悬停显示完整名称
            sp.Children.Add(name);

            cell.Child = sp;
            Canvas.SetLeft(cell, it.X);
            Canvas.SetTop(cell, it.Y);

            it.DragTransform = new TranslateTransform(0, 0);
            cell.RenderTransform = it.DragTransform;

            it.Host = cell;
            return cell;
        }

        // ========== 输入 ==========

        void HookInput()
        {
            try
            {
                // 空白处：框选 / 清空选择
                canvas.MouseLeftButtonDown += OnCanvasDown;
                canvas.MouseMove += OnCanvasMove;
                canvas.MouseLeftButtonUp += OnCanvasUp;

                canvas.MouseRightButtonUp += OnCanvasRightUp;

                canvas.PreviewKeyDown += OnKey;
            }
            catch (Exception ex) { Ui.LogError(ex, "挂接桌面视图输入"); }
        }

        void OnCanvasDown(object s, MouseButtonEventArgs e)
        {
            try
            {
                Item hit = HitItem(e.GetPosition(canvas));

                if (hit == null)
                {
                    // 空白处 → 开始框选（先清空选择，除非按着 Ctrl）
                    bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                    if (!ctrl) ClearSelection();

                    bandSelecting = true;
                    bandStart = e.GetPosition(canvas);
                    ShowBand(bandStart, bandStart);
                    canvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }

                // 图标上按下 → 准备拖动
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                bool ctrl2 = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

                if (shift) SelectRange(hit);
                else if (ctrl2) ToggleSelect(hit);
                else if (!hit.IsSelected) SelectOnly(hit);

                dragging = true;
                dragMoved = false;
                dragOutStarted = false;
                dragPreviewDx = 0;
                dragPreviewDy = 0;
                dragPressTime = DateTime.Now;
                dragAnchor = hit;
                dragStart = e.GetPosition(canvas);
                dragOrigins.Clear();

                foreach (Item it in items)
                {
                    if (it.IsSelected) dragOrigins[it.Name] = new Point(it.X, it.Y);
                }

                canvas.CaptureMouse();
                e.Handled = true;
            }
            catch (Exception ex) { Ui.LogError(ex, "桌面按下"); }
        }

        void OnCanvasMove(object s, MouseEventArgs e)
        {
            try
            {
                if (bandSelecting)
                {
                    ShowBand(bandStart, e.GetPosition(canvas));
                    return;
                }

                if (!dragging) return;

                Point p = e.GetPosition(canvas);
                double dx = p.X - dragStart.X;
                double dy = p.Y - dragStart.Y;

                double minDx = SystemParameters.MinimumHorizontalDragDistance;
                double minDy = SystemParameters.MinimumVerticalDragDistance;
                if (!dragMoved && (Math.Abs(dx) >= minDx || Math.Abs(dy) >= minDy))
                    dragMoved = true;
                if (!dragMoved) return;

                // ---- 面板内自由摆放 → 拖出（B 方案）----
                //
                // 触发条件（二选一，均为常量，便于调参）：
                //   1. 指针离开「按下时的格子」超过半格
                //   2. 按住超过 0.3 秒仍未松手
                //
                // 必须用「按下时的格子」：图标会跟着指针移动，
                // 拿图标当前位置判定永远判不出来 —— 这正是旧 bug 的根因。
                bool leftCell = DesktopDragGesture.HasLeftOriginCell(
                    dragStart.X, dragStart.Y, p.X, p.Y,
                    CellW, CellH, DesktopDragGesture.LeaveCellRatio);

                bool heldLong = DesktopDragGesture.HasHeldLongEnough(
                    dragPressTime, DateTime.Now, DesktopDragGesture.HoldSeconds);

                if (!dragOutStarted && (leftCell || heldLong))
                {
                    dragOutStarted = true;

                    // 保留当前预览偏移，让图标停在当前位置；系统拖放结束后再恢复。
                    dragPreviewDx = dx;
                    dragPreviewDy = dy;
                    ApplyDragPreview(dx, dy);
                    SetDraggedOpacity(0.55);

                    try { canvas.ReleaseMouseCapture(); } catch { }
                    BeginDragOut();

                    SetDraggedOpacity(1.0);
                    ClearDragPreview();
                    ResetDragState();
                    return;
                }
                // 面板内部仍是自由摆放，但只更新 RenderTransform：
                // 它在布局之后由合成层处理，不会像 Canvas.SetLeft/Top 一样每帧重排。
                dragPreviewDx = dx;
                dragPreviewDy = dy;
                ApplyDragPreview(dx, dy);
            }
            catch (Exception ex) { Ui.LogError(ex, "桌面拖动"); }
        }

        /// <summary>只更新合成层位移，不触发布局重排。</summary>
        void ApplyDragPreview(double dx, double dy)
        {
            try
            {
                foreach (Item it in items)
                {
                    if (!it.IsSelected || it.DragTransform == null) continue;
                    it.DragTransform.X = dx;
                    it.DragTransform.Y = dy;
                }
            }
            catch { }
        }

        /// <summary>系统拖放期间淡化原位图标，表示它正在被拖走。</summary>
        void SetDraggedOpacity(double opacity)
        {
            try
            {
                foreach (Item it in items)
                {
                    if (!it.IsSelected || it.Host == null) continue;
                    it.Host.Opacity = opacity;
                }
            }
            catch { }
        }

        /// <summary>清除所有选中图标的合成层拖动预览。</summary>
        void ClearDragPreview()
        {
            try
            {
                foreach (Item it in items)
                {
                    if (it.DragTransform == null) continue;
                    it.DragTransform.X = 0;
                    it.DragTransform.Y = 0;
                }
            }
            catch { }
        }

        /// <summary>结束一次拖动，防止 DoDragDrop 返回后 MouseUp 再提交旧坐标。</summary>
        void ResetDragState()
        {
            dragging = false;
            dragMoved = false;
            dragOutStarted = false;
            dragPreviewDx = 0;
            dragPreviewDy = 0;
            dragAnchor = null;
            dragOrigins.Clear();
        }

        /// <summary>
        /// 发起系统拖拽（拖出面板）。
        ///
        /// ── 铁律：本方法绝不删除任何源文件 ──
        ///
        /// 早期版本在拖完后"补删源文件"，理由是 DoDragDrop 返回 Copy 时
        /// 认为对方只做了复制。实测发现：桌面根本没接收文件，
        /// 而源文件被我们删掉了 —— 用户文件凭空消失。
        ///
        /// 现在的策略：
        ///   · 给 DataObject 加 Preferred DropEffect = Move，让系统执行移动
        ///   · 拖完只核对源文件还在不在：
        ///       还在   → 保留图标，什么都不做
        ///       不在了 → 对方真的移走了，移除图标
        ///   · 桌面拒收时兜底是右键「放回桌面」，不在这里补救
        /// </summary>
        void BeginDragOut()
        {
            try
            {
                List<string> paths = new List<string>();
                List<string> names = new List<string>();
                List<long> sizes = new List<long>();
                foreach (Item it in items)
                {
                    if (!it.IsSelected) continue;
                    if (!File.Exists(it.FullPath) && !Directory.Exists(it.FullPath)) continue;
                    paths.Add(it.FullPath);
                    names.Add(it.Name);
                    try { sizes.Add(it.IsFolder ? -1 : new FileInfo(it.FullPath).Length); }
                    catch { sizes.Add(-2); }
                }

                if (paths.Count == 0) return;

                DataObject data = new DataObject();
                data.SetData(DataFormats.FileDrop, paths.ToArray());
                data.SetData(DesktopDragGesture.InternalDataFormat, "1");

                // 告诉目标"这是移动"：桌面和资源管理器读到 Preferred DropEffect
                // 会自己执行移动。缺了这一项，系统不知道该复制还是移动。
                try
                {
                    System.IO.MemoryStream ms = new System.IO.MemoryStream();
                    System.IO.BinaryWriter bw = new System.IO.BinaryWriter(ms);
                    bw.Write((int)DragDropEffects.Move);
                    bw.Flush();
                    data.SetData("Preferred DropEffect", ms);
                }
                catch (Exception ex) { Ui.Log("设置 Preferred DropEffect 失败: " + ex.Message); }

                // DoDragDrop 返回后光标仍停在目标上。记录窗口句柄，便于解析资源管理器目录。
                IntPtr targetHwnd = IntPtr.Zero;

                Ui.Log("拖出开始: 文件数=" + paths.Count + "（半格/0.3秒触发）");
                DragDropEffects eff = DragDrop.DoDragDrop(canvas, data,
                    DragDropEffects.Copy | DragDropEffects.Move);

                targetHwnd = DropTarget.RootWindowAtCursor();
                DropTarget.Kind kind = DropTarget.KindAtCursor();
                string targetDir = DropTargetPath.Resolve(kind, targetHwnd);
                Ui.Log("拖出结束: effects=" + eff + " 落点=" + kind
                    + " 文件数=" + paths.Count
                    + (string.IsNullOrEmpty(targetDir) ? " 目标路径未知" : " 目标目录=" + targetDir));

                // ---- 只核对，绝不删源 ----
                int goneCount = 0;
                for (int i = 0; i < paths.Count; i++)
                {
                    string path = paths[i];
                    string name = names[i];

                    bool stillThere = false;
                    try { stillThere = File.Exists(path) || Directory.Exists(path); }
                    catch { }

                    if (stillThere)
                    {
                        Ui.Log("源文件仍在，保留图标: " + name);
                        continue;
                    }

                    string targetPath = string.IsNullOrEmpty(targetDir)
                        ? "" : Path.Combine(targetDir, name);
                    bool targetVerified = VerifyMovedTarget(targetPath, sizes[i]);

                    desks.RemovePos(deskName, name);
                    goneCount++;

                    if (targetVerified)
                        Ui.Log("拖出移动: " + path + " → " + targetPath);
                    else
                        Ui.Log("拖出移动: " + path + " → （目标路径未知，落点=" + kind + "）");
                }

                if (goneCount > 0)
                {
                    if (OnLayoutChanged != null) { try { OnLayoutChanged(); } catch { } }
                    Load(deskName);
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "拖出面板"); }
        }
        /// <summary>
        /// 核对系统拖放后的目标：文件存在且大小一致；文件夹存在。
        /// expectedSize = -1 表示文件夹；-2 表示源大小读取失败，此时不宣称已核对。
        /// </summary>
        static bool VerifyMovedTarget(string targetPath, long expectedSize)
        {
            try
            {
                if (string.IsNullOrEmpty(targetPath)) return false;
                if (expectedSize == -1) return Directory.Exists(targetPath);
                if (expectedSize < 0) return false;
                return File.Exists(targetPath) && new FileInfo(targetPath).Length == expectedSize;
            }
            catch { return false; }
        }

        void OnCanvasUp(object s, MouseButtonEventArgs e)
        {
            try
            {
                if (bandSelecting)
                {
                    bandSelecting = false;
                    try { canvas.ReleaseMouseCapture(); } catch { }
                    ApplyBandSelection(bandStart, e.GetPosition(canvas));
                    HideBand();
                    e.Handled = true;
                    return;
                }

                if (dragging)
                {
                    dragging = false;
                    try { canvas.ReleaseMouseCapture(); } catch { }

                    if (dragMoved && !dragOutStarted)
                    {
                        // RenderTransform 只负责预览；松手时一次性写入真实 Canvas 坐标。
                        foreach (Item it in items)
                        {
                            if (!it.IsSelected) continue;

                            Point origin;
                            if (!dragOrigins.TryGetValue(it.Name, out origin)) continue;

                            it.X = Math.Max(0, origin.X + dragPreviewDx);
                            it.Y = Math.Max(0, origin.Y + dragPreviewDy);
                            if (it.Host != null)
                            {
                                Canvas.SetLeft(it.Host, it.X);
                                Canvas.SetTop(it.Host, it.Y);
                            }
                            desks.SetPos(deskName, it.Name, it.X, it.Y);
                        }
                        if (OnLayoutChanged != null) { try { OnLayoutChanged(); } catch { } }
                    }
                    else if (dragAnchor != null && !dragMoved)
                    {
                        // 单击（没拖动）—— 保留选择，不执行额外动作。
                    }

                    ClearDragPreview();
                    ResetDragState();
                    e.Handled = true;
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "桌面松手"); }
        }

        void OnCanvasRightUp(object s, MouseButtonEventArgs e)
        {
            try
            {
                Item hit = HitItem(e.GetPosition(canvas));

                if (hit != null)
                {
                    if (!hit.IsSelected) SelectOnly(hit);
                    ShowItemMenu(hit);
                }
                else
                {
                    ShowBlankMenu();
                }
                e.Handled = true;
            }
            catch (Exception ex) { Ui.LogError(ex, "桌面右键"); }
        }

        void OnKey(object s, KeyEventArgs e)
        {
            try
            {
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

                if (ctrl && e.Key == Key.A)
                {
                    foreach (Item it in items) SetSelected(it, true);
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Escape)
                {
                    ClearSelection();
                    e.Handled = true;
                    return;
                }
            }
            catch { }
        }

        // ========== 命中与选择 ==========

        Item HitItem(Point p)
        {
            try
            {
                // 倒序：后加的在上层
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    Item it = items[i];
                    if (p.X >= it.X && p.X <= it.X + CellW &&
                        p.Y >= it.Y && p.Y <= it.Y + CellH)
                        return it;
                }
            }
            catch { }
            return null;
        }

        void ClearSelection()
        {
            foreach (Item it in items) SetSelected(it, false);
        }

        void SelectOnly(Item one)
        {
            foreach (Item it in items) SetSelected(it, ReferenceEquals(it, one));
        }

        void ToggleSelect(Item one)
        {
            SetSelected(one, !one.IsSelected);
        }

        void SelectRange(Item to)
        {
            try
            {
                int a = items.FindIndex(delegate(Item x) { return x.IsSelected; });
                int b = items.IndexOf(to);
                if (a < 0 || b < 0) { SelectOnly(to); return; }

                int lo = Math.Min(a, b), hi = Math.Max(a, b);
                for (int i = 0; i < items.Count; i++)
                    SetSelected(items[i], i >= lo && i <= hi);
            }
            catch { SelectOnly(to); }
        }

        void SetSelected(Item it, bool on)
        {
            try
            {
                if (it == null) return;

                it.IsSelected = on;
                if (on) selected.Add(it.Name);
                else selected.Remove(it.Name);

                if (it.Host != null)
                {
                    it.Host.Background = on ? Ui.AccentSoft : Brushes.Transparent;
                    it.Host.BorderBrush = on ? Ui.Accent : Brushes.Transparent;
                    it.Host.BorderThickness = new Thickness(on ? 1 : 0);
                }
            }
            catch { }
        }

        // ========== 框选 ==========

        void ShowBand(Point a, Point b)
        {
            try
            {
                if (overlay == null) return;

                if (bandRect == null)
                {
                    bandRect = new System.Windows.Shapes.Rectangle();
                    bandRect.Fill = new SolidColorBrush(Color.FromArgb(40, 56, 140, 72));
                    bandRect.Stroke = Ui.Accent;
                    bandRect.StrokeThickness = 1;
                    bandRect.StrokeDashArray = new DoubleCollection(new double[] { 3, 2 });
                    bandRect.IsHitTestVisible = false;
                    overlay.Children.Add(bandRect);
                }

                double x = Math.Min(a.X, b.X);
                double y = Math.Min(a.Y, b.Y);
                double w = Math.Abs(b.X - a.X);
                double h = Math.Abs(b.Y - a.Y);

                Canvas.SetLeft(bandRect, x);
                Canvas.SetTop(bandRect, y);
                bandRect.Width = Math.Max(1, w);
                bandRect.Height = Math.Max(1, h);
                bandRect.Visibility = Visibility.Visible;
            }
            catch { }
        }

        void HideBand()
        {
            try
            {
                if (bandRect != null) bandRect.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        void ApplyBandSelection(Point a, Point b)
        {
            try
            {
                double x1 = Math.Min(a.X, b.X), x2 = Math.Max(a.X, b.X);
                double y1 = Math.Min(a.Y, b.Y), y2 = Math.Max(a.Y, b.Y);

                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

                foreach (Item it in items)
                {
                    bool hit = !(it.X + CellW < x1 || it.X > x2 ||
                                 it.Y + CellH < y1 || it.Y > y2);
                    if (hit) SetSelected(it, true);
                    else if (!ctrl) SetSelected(it, false);
                }
            }
            catch { }
        }

        // ========== 菜单 ==========

        void ShowBlankMenu()
        {
            try
            {
                ContextMenu cm = new ContextMenu();

                MenuItem refresh = new MenuItem();
                refresh.Header = "刷新";
                refresh.Click += delegate { Load(deskName); };
                cm.Items.Add(refresh);

                MenuItem newFolder = new MenuItem();
                newFolder.Header = "新建文件夹";
                newFolder.Click += delegate { CreateNew(true); };
                cm.Items.Add(newFolder);

                MenuItem newText = new MenuItem();
                newText.Header = "新建文本文档";
                newText.Click += delegate { CreateNew(false); };
                cm.Items.Add(newText);

                MenuItem arrange = new MenuItem();
                arrange.Header = "自动排列图标";
                arrange.Click += delegate { ArrangeAll(); };
                cm.Items.Add(arrange);

                cm.PlacementTarget = canvas;
                cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                cm.IsOpen = true;
            }
            catch (Exception ex) { Ui.LogError(ex, "桌面空白菜单"); }
        }

        void ShowItemMenu(Item it)
        {
            try
            {
                if (it == null) return;

                ContextMenu cm = new ContextMenu();

                MenuItem open = new MenuItem();
                open.Header = "打开";
                open.Click += delegate { OpenItem(it); };
                cm.Items.Add(open);

                MenuItem reveal = new MenuItem();
                reveal.Header = "打开所在位置";
                reveal.Click += delegate { RevealItem(it); };
                cm.Items.Add(reveal);

                cm.Items.Add(new Separator());

                MenuItem rename = new MenuItem();
                rename.Header = "重命名";
                rename.Click += delegate { RenameItem(it); };
                cm.Items.Add(rename);

                MenuItem back = new MenuItem();
                back.Header = "放回桌面";
                back.Click += delegate { PutBackToDesktop(it); };
                cm.Items.Add(back);

                MenuItem del = new MenuItem();
                del.Header = "删除";
                del.Click += delegate { DeleteSelected(); };
                cm.Items.Add(del);

                cm.Items.Add(new Separator());

                MenuItem props = new MenuItem();
                props.Header = "属性";
                props.Click += delegate { ShowProperties(it); };
                cm.Items.Add(props);

                cm.PlacementTarget = it.Host;
                cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                cm.IsOpen = true;
            }
            catch (Exception ex) { Ui.LogError(ex, "桌面图标菜单"); }
        }

        // ========== 操作（第 1 步：基础文件操作） ==========

        void OpenItem(Item it)
        {
            try
            {
                if (it == null) return;
                if (!File.Exists(it.FullPath) && !Directory.Exists(it.FullPath)) return;

                System.Diagnostics.Process.Start("explorer.exe", "\"" + it.FullPath + "\"");
            }
            catch (Exception ex) { Ui.LogError(ex, "打开桌面项目"); }
        }

        void RevealItem(Item it)
        {
            try
            {
                if (it == null) return;
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + it.FullPath + "\"");
            }
            catch (Exception ex) { Ui.LogError(ex, "打开所在位置"); }
        }

        void CreateNew(bool folder)
        {
            try
            {
                string dir = desks == null ? null : desks.FolderOf(deskName);
                if (string.IsNullOrEmpty(dir)) return;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                if (folder)
                {
                    FileOps.OpResult r = FileOps.NewFolder(dir);
                    if (r != null && !r.Ok)
                    {
                        MessageBox.Show(owner, r.Message, "四叶草 · 新建文件夹",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                }
                else
                {
                    // 新建文本文档：找一个不冲突的名字
                    string baseName = "新建文本文档";
                    string path = Path.Combine(dir, baseName + ".txt");
                    int n = 1;
                    while (File.Exists(path))
                    {
                        path = Path.Combine(dir, baseName + " (" + n + ").txt");
                        n++;
                        if (n > 999) break;
                    }
                    File.WriteAllText(path, "", new System.Text.UTF8Encoding(false));
                }

                Load(deskName);
            }
            catch (Exception ex) { Ui.LogError(ex, "新建项目"); }
        }

        void RenameItem(Item it)
        {
            try
            {
                if (it == null) return;

                string nn = TextPromptDialog.Ask(owner, "重命名", "新名称", it.Name);
                if (nn == null) return;
                nn = nn.Trim();
                if (nn.Length == 0 || nn == it.Name) return;

                FileOps.OpResult r = FileOps.Rename(it.FullPath, nn, null);
                if (r != null && !r.Ok)
                {
                    MessageBox.Show(owner, r.Message, "四叶草 · 重命名",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 坐标跟着改名
                double x, y;
                if (desks.TryGetPos(deskName, it.Name, out x, out y))
                {
                    desks.RemovePos(deskName, it.Name);
                    desks.SetPos(deskName, nn, x, y);
                }

                Load(deskName);
            }
            catch (Exception ex) { Ui.LogError(ex, "重命名桌面项目"); }
        }

        void DeleteSelected()
        {
            try
            {
                List<string> targets = new List<string>();
                foreach (Item it in items)
                {
                    if (it.IsSelected) targets.Add(it.FullPath);
                }
                if (targets.Count == 0) return;

                string msg = targets.Count == 1
                    ? ("确定删除「" + Path.GetFileName(targets[0]) + "」？\r\n\r\n文件会移到回收站。")
                    : ("确定删除这 " + targets.Count + " 项？\r\n\r\n文件会移到回收站。");

                if (!TextPromptDialog.Confirm(owner, "删除", msg, "删除")) return;

                FileOps.OpResult r = FileOps.Delete(targets, null,
                    new System.Windows.Interop.WindowInteropHelper(owner).Handle);

                if (r != null && !r.Ok)
                {
                    MessageBox.Show(owner, r.Message, "四叶草 · 删除",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Load(deskName);
            }
            catch (Exception ex) { Ui.LogError(ex, "删除桌面项目"); }
        }

        /// <summary>
        /// 放回真实桌面（右键菜单，桌面拒收拖放时的兜底）。
        ///
        /// 这是程序自己移动用户文件，必须最保守：
        ///   1. 目标同名 → 自动改成「名称 (1)」，绝不覆盖
        ///   2. 移动后核对：目标存在且大小一致，才算成功
        ///   3. 核对失败 → 保留源文件、保留图标、报错
        ///   4. 只有核对通过才移除图标
        /// </summary>
        void PutBackToDesktop(Item it)
        {
            try
            {
                if (it == null) return;

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrEmpty(desktop))
                {
                    MessageBox.Show(owner, "取不到桌面路径。", "四叶草 · 放回桌面",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 1. 目标同名时自动改名，绝不覆盖
                string target = DeskRemoval.MakeUniqueTarget(desktop, it.Name, it.IsFolder);

                // 2. 安全移动：先复制并核对目标，再删除源
                VerifiedFileMove.Result moved = VerifiedFileMove.Move(it.FullPath, target);
                if (!moved.Ok)
                {
                    Ui.Log("放回桌面失败: " + it.Name + " / " + moved.Message);
                    MessageBox.Show(owner,
                        "放回桌面失败：" + moved.Message + "\r\n\r\n"
                        + (moved.SourceStillExists
                            ? "文件仍在原处：" + it.FullPath
                            : "请检查目标：" + moved.Target),
                        "四叶草 · 放回桌面",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 3. 核对式移动成功后才移除图标
                desks.RemovePos(deskName, it.Name);
                if (OnLayoutChanged != null) { try { OnLayoutChanged(); } catch { } }
                Load(deskName);
                Ui.Log("放回桌面成功: " + it.Name + " -> " + target);
            }
            catch (Exception ex) { Ui.LogError(ex, "放回桌面"); }
        }
        void ShowProperties(Item it)
        {
            try
            {
                if (it == null) return;
                IntPtr hwnd = IntPtr.Zero;
                try
                {
                    if (owner != null)
                        hwnd = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
                }
                catch { }

                Native.ShowFileProperties(it.FullPath, hwnd);
            }
            catch (Exception ex) { Ui.LogError(ex, "显示属性"); }
        }

        /// <summary>
        /// 自动排列：按格子铺开（一次性整理，不改变自由摆放模式 ——
        /// 整理完用户仍可自由拖动）。
        /// </summary>
        /// <summary>
        /// 拖入文件后调用：重载列表，并让新出现的图标落在指定坐标。
        ///
        /// Load 会把"没有坐标的文件"自动排到空位；但拖入时用户已经
        /// 指明了位置（松手点），必须覆盖掉那个自动排布。
        /// </summary>
        public void ReloadAndPlace(string deskName, List<string> names, double dropX, double dropY)
        {
            try
            {
                // 先把坐标写进去，再 Load —— 这样 Load 不会再自动排位
                if (names != null && names.Count > 0)
                {
                    double x = Math.Max(PadX, dropX - CellW / 2);
                    double y = Math.Max(PadY, dropY - CellH / 2);

                    int i = 0;
                    foreach (string n in names)
                    {
                        if (string.IsNullOrEmpty(n)) continue;

                        // 多个文件时按格子错开，避免叠在一起
                        double px = x + (i % 6) * CellW;
                        double py = y + (i / 6) * CellH;
                        desks.SetPos(deskName, n, px, py);
                        i++;
                    }
                }

                Load(deskName);
            }
            catch (Exception ex) { Ui.LogError(ex, "拖入后落位"); }
        }

        /// <summary>当前选中的完整路径（供「放回桌面」使用）。</summary>
        public List<string> SelectedPaths()
        {
            List<string> r = new List<string>();
            try
            {
                foreach (Item it in items)
                {
                    if (!it.IsSelected) continue;
                    if (string.IsNullOrEmpty(it.FullPath)) continue;
                    r.Add(it.FullPath);
                }
            }
            catch { }
            return r;
        }

        /// <summary>当前桌面名。</summary>
        public string CurrentDesk { get { return deskName; } }

        public void ArrangeAll()
        {
            try
            {
                double viewH = Math.Max(CellH, canvas.ActualHeight);

                double x = PadX, y = PadY;
                foreach (Item it in items)
                {
                    it.X = x;
                    it.Y = y;
                    Canvas.SetLeft(it.Host, x);
                    Canvas.SetTop(it.Host, y);
                    desks.SetPos(deskName, it.Name, x, y);

                    y += CellH;
                    if (y + CellH > viewH) { y = PadY; x += CellW; }
                }

                if (OnLayoutChanged != null) { try { OnLayoutChanged(); } catch { } }
            }
            catch (Exception ex) { Ui.LogError(ex, "自动排列"); }
        }

        /// <summary>供宿主查询：当前桌面有几个图标。</summary>
        public int Count { get { return items.Count; } }

        /// <summary>供宿主查询：当前选中的文件名。</summary>
        public List<string> SelectedNames
        {
            get
            {
                List<string> r = new List<string>();
                try
                {
                    foreach (Item it in items) if (it.IsSelected) r.Add(it.Name);
                }
                catch { }
                return r;
            }
        }
    }
}
