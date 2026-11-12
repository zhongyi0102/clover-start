using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clover
{
    partial class PanelWindow
    {
        // 分组标题只用于识别既有控件；拖动后会复用并重新安放，不创建新控件。
        const string TileSectionCommonTag = "tile-section:common";
        const string TileSectionLowValueTag = "tile-section:low-value";
        const string TileSectionLowValueCollapsedTag = "tile-section:low-value-collapsed";
        readonly List<Rect> dragTileBounds = new List<Rect>();
        AppItem dragSourceApp;
        Border dragSourceTile;
        int dragDropGeneration;
        int dragNearestRow = -1;
        readonly List<Border> animatedDragTiles = new List<Border>();

        /// <summary>
        /// 拖拽开始：记录实际列数，供插入标记和松手落点使用。
        /// </summary>
        void BeginDragVisual()
        {
            try
            {
                if (tilePanel == null) return;
                ClearTileMoveAnimations();
                dragNearestRow = -1;
                if (insertCanvas != null) insertCanvas.Visibility = Visibility.Visible;

                // 磁贴实际列数 = 面板宽度 / 磁贴宽度
                tileColumns = Math.Max(1, (int)(tilePanel.ActualWidth / (TileWidthPx + 4)));
                dragTileBounds.Clear();
                foreach (Border tile in renderedTiles)
                {
                    Point point = tile.TranslatePoint(new Point(0, 0), tilePanel);
                    dragTileBounds.Add(new Rect(point, new Size(tile.ActualWidth, tile.ActualHeight)));
                }
            }
            catch { }
        }


        /// <summary>
        /// 创建拖动浮层（幽灵磁贴）：图标 + 名字 + 轻微放大。
        ///
        /// 为什么复制一份而不是直接移动原磁贴：
        /// 原磁贴是 WrapPanel 的子元素，移动它会触发整个网格重排
        /// （其他磁贴全部跳位，卡顿且闪烁）。复制到 Canvas 上绝对定位，
        /// 完全不碰布局 —— 这是所有成熟拖拽实现的通行做法。
        ///
        /// 浮层只用边框和 1.04 倍缩放表达“拿起来了”。动态 DropShadowEffect
        /// 在透明 WPF 窗口中每帧重绘成本较高，会放大上下拖动时的卡顿。
        /// </summary>
        void ShowDragGhost(AppItem a, Point p)
        {
            try
            {
                if (insertCanvas == null) return;
                HideDragGhost();

                Border ghost = new Border();
                ghost.Width = TileWidthPx;
                ghost.Height = TileHeightPx;
                ghost.CornerRadius = new CornerRadius(Ui.R.Sm);
                ghost.Background = Ui.Bg;          // 实体背景，否则透出下面的磁贴
                ghost.BorderBrush = Ui.Line;
                ghost.BorderThickness = new Thickness(1);
                ghost.IsHitTestVisible = false;    // 不挡鼠标事件
                ghost.Opacity = 0.96;

                // 内容：图标 + 名字（与 MakeTile 同构，但不带事件）
                StackPanel sp = new StackPanel();
                sp.VerticalAlignment = VerticalAlignment.Center;

                // 已挂载的图标直接复用；不在开始拖动时同步读盘/解码。
                ImageSource icon = null;
                TileIconState iconState;
                if (dragSourceTile != null && tileIconStatesByTile.TryGetValue(dragSourceTile, out iconState)
                    && iconState.Box != null && iconState.Box.Children.Count > 0)
                {
                    Image image = iconState.Box.Children[0] as Image;
                    if (image != null) icon = image.Source;
                }
                if (icon != null)
                {
                    Image img = new Image();
                    img.Source = icon;
                    img.Width = IconSizePx;
                    img.Height = IconSizePx;
                    img.HorizontalAlignment = HorizontalAlignment.Center;
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    sp.Children.Add(img);
                }
                else
                {
                    Border ph = new Border();
                    ph.Width = IconSizePx;
                    ph.Height = IconSizePx;
                    ph.CornerRadius = new CornerRadius(Ui.R.Sm);
                    ph.Background = Ui.AccentSoft;
                    ph.HorizontalAlignment = HorizontalAlignment.Center;
                    string initial = string.IsNullOrEmpty(a.Name) ? "?" : a.Name.Substring(0, 1);
                    TextBlock init = Ui.T(initial, 14.5, Ui.AccentText, FontWeights.SemiBold);
                    init.HorizontalAlignment = HorizontalAlignment.Center;
                    init.VerticalAlignment = VerticalAlignment.Center;
                    ph.Child = init;
                    sp.Children.Add(ph);
                }

                TextBlock name = new TextBlock();
                name.Text = DisplayNameOf(a);
                name.FontFamily = Ui.YaHei;
                name.FontSize = 11.5;
                name.Foreground = Ui.Fg;
                name.TextWrapping = TextWrapping.Wrap;
                name.TextAlignment = TextAlignment.Center;
                name.LineHeight = 15;
                name.MaxWidth = 88;
                name.Margin = new Thickness(0, Ui.S.Xs, 0, 0);

                Border clip = new Border();
                clip.MaxHeight = 30;
                clip.ClipToBounds = true;
                clip.Child = name;
                clip.HorizontalAlignment = HorizontalAlignment.Center;
                sp.Children.Add(clip);

                ghost.Child = sp;

                // 位移用 TranslateTransform（每帧改 X/Y 不触发重排）
                dragGhostPos = new TranslateTransform(0, 0);
                ghost.RenderTransform = dragGhostPos;

                // 缩放用 RenderTransformOrigin 中心放大
                dragGhostScale = new ScaleTransform(1.04, 1.04);
                ghost.RenderTransformOrigin = new Point(0.5, 0.5);
                TransformGroup tg = new TransformGroup();
                tg.Children.Add(dragGhostScale);
                tg.Children.Add(dragGhostPos);
                ghost.RenderTransform = tg;

                insertCanvas.Children.Add(ghost);
                insertCanvas.Visibility = Visibility.Visible;
                dragGhost = ghost;

                // ★ 把插入标记提到最上层：浮层后加入，会盖住标记。
                // 实测拖动时只看到浮层、看不到竖条标记 —— 就是层叠顺序问题。
                // 移除再添加 = 移到 Children 末尾（WPF 的 z-order 就是列表顺序）。
                if (insertMarker != null && insertCanvas.Children.Contains(insertMarker))
                {
                    insertCanvas.Children.Remove(insertMarker);
                    insertCanvas.Children.Add(insertMarker);
                }

                // 初始位置：让鼠标正好落在"抓取点"上（不跳）
                Canvas.SetLeft(ghost, 0);
                Canvas.SetTop(ghost, 0);
                MoveDragGhost(p);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "创建拖动浮层");
            }
        }


        /// <summary>浮层跟随鼠标（每帧调用，只改 Transform）。</summary>
        void MoveDragGhost(Point p)
        {
            try
            {
                if (dragGhost == null || dragGhostPos == null) return;
                Point overlay = tilePanel.TranslatePoint(p, insertCanvas);
                dragGhostPos.X = overlay.X - dragGrabOffset.X;
                dragGhostPos.Y = overlay.Y - dragGrabOffset.Y;
            }
            catch { }
        }


        /// <summary>移除浮层。</summary>
        void HideDragGhost()
        {
            try
            {
                if (dragGhost != null && insertCanvas != null)
                {
                    insertCanvas.Children.Remove(dragGhost);
                }
                dragGhost = null;
                dragGhostScale = null;
                dragGhostPos = null;
            }
            catch { }
        }


        /// <summary>
        /// 拖动中：只更新插入标记位置，周围磁贴保持静止。
        ///
        /// 落点使用拖动开始时缓存的磁贴矩形，包含分组标题占用的空间。
        /// 原实现用 `Mouse.GetPosition` 逐个判断 `p.X >= tl.X && p.X <= tl.X+width`，
        /// 有两个致命问题：
        ///   · 空状态时 tilePanel.Children[0] 是 StackPanel 不是磁贴，index 对不上
        ///   · 鼠标落在磁贴间隙时所有判断都失败 → target = -1（实测日志确实如此）
        ///
        /// 旧实现每次跨行都会给一整行磁贴重新创建 200ms 位移动画；鼠标位于
        /// 行边界时还会反复重启动画。这里把高频路径收敛为常量级工作：移动
        /// 浮层、计算下标、移动插入线。真正的顺序变化只在松手时提交一次。
        /// </summary>
        void UpdateDragPreview(Point p)
        {
            try
            {
                if (tilePanel == null || insertCanvas == null || insertMarker == null) return;
                if (renderedApps == null || renderedApps.Count == 0) return;

                // 用浮层的**中心**判断落点，而不是鼠标位置 ——
                // 鼠标可能抓在磁贴边角，用中心更符合"这块磁贴会掉到哪"的直觉
                double cx = p.X - dragGrabOffset.X + TileWidthPx / 2.0;
                double cy = p.Y - dragGrabOffset.Y + TileHeightPx / 2.0;

                // 分组标题会占满一行；只在拖动开始读取布局，移动时使用缓存矩形。
                if (dragTileBounds.Count != renderedApps.Count) return;
                // 先选行，再选列；纵向距离与横向距离不能混成一个分数。
                int row = 0;
                double best = double.MaxValue;
                for (int i = 0; i < dragTileBounds.Count; i++)
                {
                    Rect bounds = dragTileBounds[i];
                    double distance = Math.Abs(cy - (bounds.Top + bounds.Height / 2));
                    if (distance < best) { best = distance; row = i; }
                }
                // 行边界保留少量迟滞，指针手抖不会让插入线反复上下跳。
                if (dragNearestRow >= 0 && dragNearestRow < dragTileBounds.Count)
                {
                    Rect previous = dragTileBounds[dragNearestRow];
                    if (Math.Abs(cy - (previous.Top + previous.Height / 2)) <= best + Ui.S.Sm) row = dragNearestRow;
                }
                dragNearestRow = row;
                int idx = row;
                best = double.MaxValue;
                for (int i = 0; i < dragTileBounds.Count; i++)
                {
                    Rect bounds = dragTileBounds[i];
                    if (Math.Abs(bounds.Top - dragTileBounds[row].Top) > 1) continue;
                    double distance = Math.Abs(cx - (bounds.Left + bounds.Width / 2));
                    if (distance < best) { best = distance; idx = i; }
                }
                Rect nearest = dragTileBounds[idx];
                if (cx >= nearest.Left + nearest.Width / 2 - 0.1) idx++;
                idx = ClampDragInsertion(dragSourceApp, idx);
                idx = Math.Min(idx, renderedApps.Count);
                if (idx == insertIndex) return;    // 没变化就不重画，省开销

                insertIndex = idx;

                // ---- 插入标记位置（胶囊竖条）----
                Point marker = DragInsertionPoint(idx);
                Point overlayPoint = tilePanel.TranslatePoint(new Point(marker.X - 4, marker.Y + 4), insertCanvas);
                Canvas.SetLeft(insertMarker, overlayPoint.X);
                Canvas.SetTop(insertMarker, overlayPoint.Y);
                insertMarker.Height = Math.Max(24, TileHeightPx - 12);
                insertMarker.Visibility = Visibility.Visible;

            }
            catch { }
        }

        Point DragInsertionPoint(int index)
        {
            if (dragTileBounds.Count == 0) return new Point();
            if (dragSourceApp != null && index > 0 && index <= dragTileBounds.Count
                && SameDragGroup(dragSourceApp, renderedApps[index - 1])
                && (index == renderedApps.Count || !SameDragGroup(dragSourceApp, renderedApps[index])))
            {
                Rect edge = dragTileBounds[index - 1];
                return new Point(edge.Right + 4, edge.Top);
            }
            if (index < dragTileBounds.Count)
            {
                Rect bounds = dragTileBounds[Math.Max(0, index)];
                return bounds.TopLeft;
            }
            Rect last = dragTileBounds[dragTileBounds.Count - 1];
            return new Point(last.Right + 4, last.Top);
        }

        bool SameDragGroup(AppItem a, AppItem b)
        {
            if (showingCommon) return true;
            return a.LowValue == b.LowValue && HasStoreLabel(a) == HasStoreLabel(b);
        }

        int ClampDragInsertion(AppItem app, int index)
        {
            if (app == null || renderedApps == null) return index;
            int source = renderedApps.IndexOf(app);
            if (source < 0) return index;
            int first = source, last = source;
            while (first > 0 && SameDragGroup(app, renderedApps[first - 1])) first--;
            while (last + 1 < renderedApps.Count && SameDragGroup(app, renderedApps[last + 1])) last++;
            return Math.Max(first, Math.Min(last + 1, index));
        }

        void RestoreDragSource()
        {
            if (dragSourceTile != null)
            {
                UpdateTilePresentation(dragSourceTile, dragSourceApp);
                dragSourceTile.Cursor = Cursors.Hand;
            }
            dragSourceTile = null;
            dragSourceApp = null;
        }

        void CancelTileDrag()
        {
            dragDropGeneration++;
            RestoreDragSource();
            EndDragVisual();
            ClearTileMoveAnimations();
            try { Mouse.Capture(null); } catch { }
        }


        /// <summary>拖拽结束：收起标记和浮层。</summary>
        void EndDragVisual()
        {
            try
            {
                insertIndex = -1;
                if (insertMarker != null) insertMarker.Visibility = Visibility.Collapsed;
                if (insertCanvas != null) insertCanvas.Visibility = Visibility.Collapsed;
                HideDragGhost();
            }
            catch { }
        }


        /// <summary>
        /// 拖拽结束但**保留浮层**：用于"平滑落入"动画。
        ///
        /// 与 EndDragVisual 的区别：不清 dragGhost、不隐藏 Canvas，
        /// 因为接下来要让浮层飞到目标格再消失。
        /// </summary>
        void EndDragVisualKeepGhost()
        {
            try
            {
                insertIndex = -1;
                if (insertMarker != null) insertMarker.Visibility = Visibility.Collapsed;
                // 注意：不调 HideDragGhost()、不隐藏 insertCanvas
            }
            catch { }
        }


        /// <summary>
        /// 松手即提交排序；周围磁贴同时让位，浮层落到实际最终格。
        ///
        /// 为什么要有这一步：如果松手瞬间直接换位，浮层消失与磁贴换位在
        /// 同一帧发生，视觉上会像“啪”地跳过去。
        /// 松手即变更实际排列，周围可见磁贴与浮层同步用 200ms 位移衔接。
        /// 只测量一次前后位置，再用 RenderTransform 呈现，不逐帧重排。
        ///
        /// 落点无效（target &lt; 0）时直接取消，浮层飞回原位。
        /// </summary>
        void DropDragGhost(AppItem a, int target)
        {
            try
            {
                int generation = ++dragDropGeneration;
                Border source = dragSourceTile;
                CommitDrag(a, target, true);
                tilePanel.UpdateLayout();
                if (dragGhost == null || dragGhostPos == null || insertCanvas == null)
                {
                    // 没有浮层（异常情况）：直接提交
                    HideDragGhost();
                    if (insertCanvas != null) insertCanvas.Visibility = Visibility.Collapsed;
                    RestoreDragSource();
                    return;
                }

                if (Ui.ReducedMotion)
                {
                    HideDragGhost();
                    if (insertCanvas != null) insertCanvas.Visibility = Visibility.Collapsed;
                    RestoreDragSource();
                    return;
                }

                // 按实际磁贴位置落下，不能忽略分组标题占用的空间。
                double dstX = 0, dstY = 0;
                int finalIndex = renderedApps == null ? -1 : renderedApps.IndexOf(a);
                if (finalIndex >= 0 && finalIndex < renderedTiles.Count)
                {
                    Border finalTile = renderedTiles[finalIndex];
                    Point destination = finalTile.TranslatePoint(new Point(), insertCanvas);
                    TranslateTransform offset = finalTile.RenderTransform as TranslateTransform;
                    if (offset != null) { destination.X -= offset.X; destination.Y -= offset.Y; }
                    dstX = destination.X;
                    dstY = destination.Y;
                    if (source != null) source.Opacity = 0;
                }
                else
                {
                    // 落点无效：飞回原位（用当前浮层位置，等于原地淡出）
                    dstX = dragGhostPos.X;
                    dstY = dragGhostPos.Y;
                }

                CubicEase ease = FrozenCubic();

                // 位置：200ms 飞到目标格
                DoubleAnimation ax = new DoubleAnimation(dstX, TimeSpan.FromMilliseconds(DragDropMs));
                ax.EasingFunction = ease;
                DoubleAnimation ay = new DoubleAnimation(dstY, TimeSpan.FromMilliseconds(DragDropMs));
                ay.EasingFunction = ease;

                // 缩放：从 1.04 回落到 1.0（"放下"的手感）
                if (dragGhostScale != null)
                {
                    DoubleAnimation sx = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(DragDropMs));
                    sx.EasingFunction = ease;
                    DoubleAnimation sy = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(DragDropMs));
                    sy.EasingFunction = ease;
                    dragGhostScale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
                    dragGhostScale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
                }

                // 顺序已经提交；完成回调只移除本轮浮层并恢复源磁贴。
                // generation 阻止连续拖动时旧回调清除新浮层。
                ay.Completed += delegate
                {
                    try
                    {
                        if (generation != dragDropGeneration) return;
                        RestoreDragSource();
                        HideDragGhost();
                        if (insertCanvas != null) insertCanvas.Visibility = Visibility.Collapsed;
                    }
                    catch { }
                };

                dragGhostPos.BeginAnimation(TranslateTransform.XProperty, ax);
                dragGhostPos.BeginAnimation(TranslateTransform.YProperty, ay);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "拖动落入动画");
                CancelTileDrag();
            }
        }


        /// <summary>
        /// 提交拖拽：按插入下标重排 renderedApps，复用现有磁贴原地换位，
        /// 再写入 customOrders。这里刻意不调用 RenderApps：重建全部磁贴会让
        /// 图标重新挂载、选中态清空并产生用户可见的整页刷新。
        ///
        /// target 由调用方传入（在 EndDragVisual 重置之前取好的值）。
        /// </summary>
        void CommitDrag(AppItem a, int target, bool animate = false)
        {
            try
            {
                int from = renderedApps == null ? -1 : renderedApps.IndexOf(a);
                Ui.Log("CommitDrag " + a.Name + " from=" + from + " target=" + target);

                if (target < 0) return;
                if (from < 0) return;
                if (from == target) return;

                // 分批渲染通常只持续几个 Dispatcher 周期。若用户在这极短窗口里
                // 已经开始拖动，不提交半份列表，也不退回全量刷新；等内容稳定后再拖。
                if (tileRenderPending || pendingRenderApps != null) return;

                List<AppItem> newOrder = new List<AppItem>(renderedApps);
                AppItem moved = newOrder[from];
                newOrder.RemoveAt(from);

                // 目标下标是"移除前"的位置，移除后要修正：
                // 若目标在源之后，删掉源会让目标前移一位
                if (target > from) target--;
                if (target < 0) target = 0;
                if (target > newOrder.Count) target = newOrder.Count;

                // 插入线紧邻原位置时，修正后的目标仍是原下标；不要触发布局或写盘。
                if (target == from) return;

                newOrder.Insert(target, moved);

                // 常用一套顺序；全部仍保持角色与商店分区。
                if (current == "") RegroupAllSections(newOrder);

                Dictionary<Border, Point> positions = animate && !Ui.ReducedMotion ? CaptureTilePositions() : null;
                ClearTileMoveAnimations();
                if (!ApplyTileOrderInPlace(newOrder))
                {
                    Ui.Log("CommitDrag 已取消：磁贴映射不完整");
                    return;
                }
                if (positions != null) AnimateTileMoves(positions, dragSourceTile);

                List<string> paths = new List<string>();
                foreach (AppItem it in newOrder) paths.Add(it.Path);
                // 折叠区未渲染的条目不能从人工顺序中丢失。
                List<string> savedOrder;
                if (customOrders.TryGetValue(current, out savedOrder))
                    foreach (string path in savedOrder)
                        if (!paths.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) paths.Add(path);
                customOrders[current] = paths;
                SaveCustomOrders();
                UpdateCommonListHeader();
                // 排序已经反映在当前控件树里；同步签名，下一次打开面板不会把它
                // 误判成外部内容变化而再次重建。
                renderedSig = CurrentContentSig();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "提交拖拽");
            }
        }

        Dictionary<Border, Point> CaptureTilePositions()
        {
            Dictionary<Border, Point> positions = new Dictionary<Border, Point>();
            foreach (Border tile in renderedTiles) positions[tile] = tile.TranslatePoint(new Point(), tilePanel);
            return positions;
        }

        void ClearTileMoveAnimations()
        {
            foreach (Border tile in animatedDragTiles)
            {
                TranslateTransform move = tile.RenderTransform as TranslateTransform;
                if (move == null) continue;
                move.BeginAnimation(TranslateTransform.XProperty, null);
                move.BeginAnimation(TranslateTransform.YProperty, null);
                move.X = move.Y = 0;
            }
            animatedDragTiles.Clear();
        }

        void AnimateTileMoves(Dictionary<Border, Point> before, Border dragged)
        {
            tilePanel.UpdateLayout();
            foreach (Border tile in renderedTiles)
            {
                Point previous;
                if (tile == dragged || !before.TryGetValue(tile, out previous)) continue;
                Point next = tile.TranslatePoint(new Point(), tilePanel);
                Vector delta = previous - next;
                if (delta.Length < 0.1) continue;
                Point viewport = tile.TranslatePoint(new Point(), contentScroll);
                if (viewport.Y < -TileHeightPx || viewport.Y > contentScroll.ActualHeight + TileHeightPx) continue;
                TranslateTransform move = new TranslateTransform();
                tile.RenderTransform = move;
                animatedDragTiles.Add(tile);
                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(delta.X, 0, TimeSpan.FromMilliseconds(DragDropMs))
                    { EasingFunction = FrozenCubic(), FillBehavior = FillBehavior.Stop });
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(delta.Y, 0, TimeSpan.FromMilliseconds(DragDropMs))
                    { EasingFunction = FrozenCubic(), FillBehavior = FillBehavior.Stop });
            }
        }


        /// <summary>
        /// 使用当前 Border/Image 控件重建排列，不重新创建磁贴、不重新请求图标。
        /// Children.Clear/Add 都发生在同一个 UI 调度周期，用户只看到最终换位，
        /// 不会看到中间空白，也不会丢失滚动位置或选中态。
        /// </summary>
        bool ApplyTileOrderInPlace(List<AppItem> newOrder)
        {
            try
            {
                if (tilePanel == null || newOrder == null || renderedApps == null) return false;
                if (renderedApps.Count != renderedTiles.Count || newOrder.Count != renderedTiles.Count)
                    return false;

                Dictionary<AppItem, Border> tileOf = new Dictionary<AppItem, Border>();
                for (int i = 0; i < renderedApps.Count; i++)
                {
                    if (renderedApps[i] == null || renderedTiles[i] == null) return false;
                    tileOf[renderedApps[i]] = renderedTiles[i];
                }

                List<Border> newTiles = new List<Border>();
                foreach (AppItem app in newOrder)
                {
                    Border tile;
                    if (app == null || !tileOf.TryGetValue(app, out tile)) return false;
                    newTiles.Add(tile);
                }

                Border commonHeader = null;
                Border lowHeader = null;
                Border collapsedLowRow = null;
                Border activeHeader = null;
                Border auxiliaryHeader = null;
                List<UIElement> extras = new List<UIElement>();
                List<UIElement> oldChildren = new List<UIElement>();
                foreach (UIElement child in tilePanel.Children) oldChildren.Add(child);
                foreach (UIElement child in oldChildren)
                {
                    Border border = child as Border;
                    if (border != null && renderedTiles.Contains(border)) continue;
                    string tag = border == null ? null : border.Tag as string;
                    if (tag == TileSectionCommonTag) commonHeader = border;
                    else if (tag == TileSectionLowValueTag) lowHeader = border;
                    else if (tag == TileSectionLowValueCollapsedTag) collapsedLowRow = border;
                    else if (tag == TileSectionActiveTag) activeHeader = border;
                    else if (tag == TileSectionAuxiliaryTag) auxiliaryHeader = border;
                    else extras.Add(child);
                }

                int firstLow = -1;
                if (current == "")
                {
                    for (int i = 0; i < newOrder.Count; i++)
                    {
                        if (newOrder[i] != null && newOrder[i].LowValue)
                        {
                            firstLow = i;
                            break;
                        }
                    }
                }

                // 一次性重新挂接同一批控件。没有 MakeTile、没有图标解码，也没有
                // Dispatcher 分批重建，因此只是一次普通 WrapPanel 重新布局。
                tilePanel.Children.Clear();
                int activeCount = current == "" ? CountActiveRole(newOrder) : 0;
                for (int i = 0; i < newTiles.Count; i++)
                {
                    if (current == "" && i == 0 && activeHeader != null)
                        tilePanel.Children.Add(activeHeader);
                    if (current == "" && i == activeCount && auxiliaryHeader != null)
                        tilePanel.Children.Add(auxiliaryHeader);
                    if (showingCommon && i == 0 && commonHeader != null)
                        tilePanel.Children.Add(commonHeader);
                    if (i == firstLow && lowHeader != null)
                        tilePanel.Children.Add(lowHeader);
                    tilePanel.Children.Add(newTiles[i]);
                }
                if (auxiliaryHeader != null && activeCount == newTiles.Count)
                    tilePanel.Children.Add(auxiliaryHeader);
                if (collapsedLowRow != null) tilePanel.Children.Add(collapsedLowRow);
                foreach (UIElement extra in extras) tilePanel.Children.Add(extra);

                renderedApps = new List<AppItem>(newOrder);
                renderedTiles.Clear();
                renderedTiles.AddRange(newTiles);
                tileAppIndex.Clear();
                for (int i = 0; i < renderedTiles.Count; i++) tileAppIndex[renderedTiles[i]] = i;

                selectedIndex = selectedTile == null ? -1 : renderedTiles.IndexOf(selectedTile);
                tilePanel.InvalidateMeasure();
                ScheduleViewportIconRefresh();
                return true;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "磁贴原地换位");
                return false;
            }
        }


        /// <summary>验收用：保存当前磁贴对象身份，确认拖动没有销毁重建。</summary>
        internal List<Border> TestRenderedTileSnapshot()
        {
            return new List<Border>(renderedTiles);
        }

        internal int TestAuxiliaryHeaderCount()
        {
            int count = 0;
            foreach (UIElement child in tilePanel.Children)
            {
                Border header = child as Border;
                if (header != null && string.Equals(header.Tag as string, TileSectionAuxiliaryTag, StringComparison.Ordinal)) count++;
            }
            return count;
        }


        /// <summary>验收用：走真实提交路径；target 语义与拖动插入线一致。</summary>
        internal void TestCommitDrag(int from, int target)
        {
            if (renderedApps == null || from < 0 || from >= renderedApps.Count) return;
            CommitDrag(renderedApps[from], target);
        }

        internal int TestDragHit(int from, int tile, double xFraction, double yFraction)
        {
            CancelTileDrag();
            dragSourceApp = renderedApps[from];
            BeginDragVisual();
            dragGrabOffset = new Point(TileWidthPx / 2, TileHeightPx / 2);
            Rect bounds = dragTileBounds[tile];
            UpdateDragPreview(new Point(bounds.Left + bounds.Width * xFraction, bounds.Top + bounds.Height * yFraction));
            int result = insertIndex;
            CancelTileDrag();
            return result;
        }

        internal void TestAnimatedDragDrop(int from, int target)
        {
            CancelTileDrag();
            dragSourceApp = renderedApps[from];
            dragSourceTile = renderedTiles[from];
            BeginDragVisual();
            dragGrabOffset = new Point(TileWidthPx / 2, TileHeightPx / 2);
            Point start = dragTileBounds[from].TopLeft;
            ShowDragGhost(dragSourceApp, new Point(start.X + TileWidthPx / 2, start.Y + TileHeightPx / 2));
            DropDragGhost(dragSourceApp, ClampDragInsertion(dragSourceApp, target));
        }

        internal int TestAnimatedDragTileCount() { return animatedDragTiles.Count; }
        internal bool TestDragGhostVisible() { return dragGhost != null; }
        internal void TestCancelTileDrag() { CancelTileDrag(); }


        /// <summary>验收用：拖动预览只移动插入线，不给任何磁贴附加位移。</summary>
        internal bool TestDragPreviewKeepsTilesStationary(int from, int target)
        {
            if (renderedApps == null || from < 0 || from >= renderedApps.Count) return false;
            try
            {
                BeginDragVisual();
                dragGrabOffset = new Point(TileWidthPx / 2, TileHeightPx / 2);
                insertIndex = -1;

                int bounded = Math.Max(0, Math.Min(target, renderedApps.Count));
                Point point = DragInsertionPoint(bounded);
                Point p = bounded < dragTileBounds.Count ? point : new Point(point.X + 1, point.Y);
                UpdateDragPreview(p);

                if (insertIndex != bounded) return false;
                Point expected = tilePanel.TranslatePoint(new Point(point.X - 4, point.Y + 4), insertCanvas);
                if (Math.Abs(Canvas.GetTop(insertMarker) - expected.Y) > 0.1) return false;
                foreach (Border tile in renderedTiles)
                {
                    FrameworkElement content = tile == null ? null : tile.Child as FrameworkElement;
                    TranslateTransform shift = content == null
                        ? null : content.RenderTransform as TranslateTransform;
                    if (shift != null && (Math.Abs(shift.X) > 0.01 || Math.Abs(shift.Y) > 0.01))
                        return false;
                }
                return true;
            }
            finally
            {
                EndDragVisual();
            }
        }
    }
}
