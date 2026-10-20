using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Clover
{
    /// <summary>
    /// 支持 UI 虚拟化的 WrapPanel（WPF 自带的 WrapPanel 不虚拟化，
    /// 会为每个子项创建实体控件——113 个磁贴就是约 600 个可视化元素全量常驻）。
    ///
    /// 实现思路（参考 WPF 虚拟化列表的做法）：
    ///   - 继承 VirtualizingPanel，由 WPF 在滚动时通过 ItemContainerGenerator 回收/复用容器
    ///   - 只测量并摆放"当前视口内可见"的行，视口外的子项不生成
    ///   - 按固定行高（ItemHeight）与容器宽度（ItemWidth）计算列数，结构简单可预测
    ///
    /// 使用前提：
    ///   - 必须放在 ScrollViewer 里，且 ScrollViewer 的 CanContentScroll = true
    ///   - 必须设置 ItemWidth / ItemHeight（磁贴是固定尺寸，符合条件）
    ///   - ItemsControl 要开启 VirtualizingPanel.IsVirtualizing="True"
    /// </summary>
    class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
    {
        /// <summary>子项宽度（含外边距）。</summary>
        public double ItemWidth
        {
            get { return (double)GetValue(ItemWidthProperty); }
            set { SetValue(ItemWidthProperty, value); }
        }
        public static readonly DependencyProperty ItemWidthProperty =
            DependencyProperty.Register("ItemWidth", typeof(double), typeof(VirtualizingWrapPanel),
                new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>子项高度（含外边距）。</summary>
        public double ItemHeight
        {
            get { return (double)GetValue(ItemHeightProperty); }
            set { SetValue(ItemHeightProperty, value); }
        }
        public static readonly DependencyProperty ItemHeightProperty =
            DependencyProperty.Register("ItemHeight", typeof(double), typeof(VirtualizingWrapPanel),
                new FrameworkPropertyMetadata(94.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        // ----- 滚动相关（IScrollInfo）-----
        double offsetX;
        double offsetY;
        Size extent = new Size(0, 0);
        Size viewport = new Size(0, 0);

        public bool CanHorizontallyScroll { get; set; }
        public bool CanVerticallyScroll { get; set; }
        public double HorizontalOffset { get { return offsetX; } }
        public double VerticalOffset { get { return offsetY; } }
        public double ExtentWidth { get { return extent.Width; } }
        public double ExtentHeight { get { return extent.Height; } }
        public double ViewportWidth { get { return viewport.Width; } }
        public double ViewportHeight { get { return viewport.Height; } }
        public ScrollViewer ScrollOwner { get; set; }

        int Cols
        {
            get
            {
                double w = viewport.Width;
                if (w <= 0 || ItemWidth <= 0) return 1;
                int c = (int)Math.Floor(w / ItemWidth);
                return c < 1 ? 1 : c;
            }
        }

        public void SetVerticalOffset(double value)
        {
            double max = Math.Max(0, extent.Height - viewport.Height);
            if (value < 0) value = 0;
            if (value > max) value = max;
            if (Math.Abs(value - offsetY) < 0.01) return;
            offsetY = value;
            if (ScrollOwner != null) ScrollOwner.InvalidateScrollInfo();
            InvalidateMeasure();
        }

        public void LineUp() { SetVerticalOffset(offsetY - ItemHeight); }
        public void LineDown() { SetVerticalOffset(offsetY + ItemHeight); }
        public void PageUp() { SetVerticalOffset(offsetY - viewport.Height); }
        public void PageDown() { SetVerticalOffset(offsetY + viewport.Height); }
        public void MouseWheelUp() { SetVerticalOffset(offsetY - ItemHeight * 2); }
        public void MouseWheelDown() { SetVerticalOffset(offsetY + ItemHeight * 2); }
        public void SetHorizontalOffset(double offset) { offsetX = 0; }
        public void LineLeft() { }
        public void LineRight() { }
        public void PageLeft() { }
        public void PageRight() { }
        public void MouseWheelLeft() { }
        public void MouseWheelRight() { }

        public Rect MakeVisible(Visual visual, Rect rectangle)
        {
            try
            {
                if (visual == null) return Rect.Empty;
                UIElement child = visual as UIElement;
                if (child == null) return Rect.Empty;

                int index = InternalChildren.IndexOf(child);
                if (index < 0) return Rect.Empty;

                int cols = Cols;
                int row = index / cols;
                double top = row * ItemHeight;
                double bottom = top + ItemHeight;

                if (top < offsetY) SetVerticalOffset(top);
                else if (bottom > offsetY + viewport.Height) SetVerticalOffset(bottom - viewport.Height);

                return new Rect(0, top, ItemWidth, ItemHeight);
            }
            catch
            {
                return Rect.Empty;
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            try
            {
                // 视口尺寸：可用尺寸无限时退化为当前视口，避免算出 NaN
                double vw = double.IsInfinity(availableSize.Width) ? (viewport.Width > 0 ? viewport.Width : 800) : availableSize.Width;
                double vh = double.IsInfinity(availableSize.Height) ? (viewport.Height > 0 ? viewport.Height : 600) : availableSize.Height;

                viewport = new Size(vw, vh);
                if (ScrollOwner != null) ScrollOwner.InvalidateScrollInfo();

                ItemsControl owner = ItemsControl.GetItemsOwner(this);
                int itemCount = owner != null ? owner.Items.Count : 0;
                if (itemCount == 0)
                {
                    extent = new Size(0, 0);
                    return new Size(vw, 0);
                }

                int cols = Cols;
                int rows = (int)Math.Ceiling((double)itemCount / cols);
                extent = new Size(cols * ItemWidth, rows * ItemHeight);
                if (ScrollOwner != null) ScrollOwner.InvalidateScrollInfo();

                // 只生成视口内的行（上下各留一行缓冲，滚动时更顺）
                int firstRow = (int)Math.Floor(offsetY / ItemHeight);
                int lastRow = (int)Math.Ceiling((offsetY + vh) / ItemHeight);
                if (firstRow < 0) firstRow = 0;
                if (lastRow > rows - 1) lastRow = rows - 1;

                int start = firstRow * cols;
                int end = Math.Min(itemCount - 1, (lastRow + 1) * cols - 1);

                IItemContainerGenerator gen = ItemContainerGenerator;
                GeneratorPosition startPos = gen.GeneratorPositionFromIndex(start);
                int childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;

                using (gen.StartAt(startPos, GeneratorDirection.Forward, true))
                {
                    for (int i = start; i <= end; i++, childIndex++)
                    {
                        bool newlyRealized;
                        UIElement child = gen.GenerateNext(out newlyRealized) as UIElement;
                        if (child == null) break;

                        if (newlyRealized)
                        {
                            if (childIndex >= InternalChildren.Count)
                                AddInternalChild(child);
                            else
                                InsertInternalChild(childIndex, child);
                            gen.PrepareItemContainer(child);
                        }

                        child.Measure(new Size(ItemWidth, ItemHeight));
                    }
                }

                CleanUpItems(start, end);
                return new Size(vw, vh);
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("VirtualizingWrapPanel.MeasureOverride 失败: " + ex.Message);
                return new Size(0, 0);
            }
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            try
            {
                int cols = Cols;
                ItemsControl owner = ItemsControl.GetItemsOwner(this);
                int itemCount = owner != null ? owner.Items.Count : 0;

                IItemContainerGenerator gen = ItemContainerGenerator;
                for (int i = 0; i < InternalChildren.Count; i++)
                {
                    UIElement child = InternalChildren[i];
                    if (child == null) continue;

                    GeneratorPosition pos = new GeneratorPosition(i, 0);
                    int itemIndex = gen.IndexFromGeneratorPosition(pos);
                    if (itemIndex < 0 || itemIndex >= itemCount) continue;

                    int row = itemIndex / cols;
                    int col = itemIndex % cols;
                    child.Arrange(new Rect(col * ItemWidth, row * ItemHeight - offsetY, ItemWidth, ItemHeight));
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("VirtualizingWrapPanel.ArrangeOverride 失败: " + ex.Message);
            }
            return finalSize;
        }

        /// <summary>回收视口外的容器，交给 WPF 复用。</summary>
        void CleanUpItems(int start, int end)
        {
            try
            {
                IItemContainerGenerator gen = ItemContainerGenerator;
                for (int i = InternalChildren.Count - 1; i >= 0; i--)
                {
                    GeneratorPosition pos = new GeneratorPosition(i, 0);
                    int itemIndex = gen.IndexFromGeneratorPosition(pos);
                    if (itemIndex < 0) continue;
                    if (itemIndex < start || itemIndex > end)
                    {
                        gen.Remove(pos, 1);
                        RemoveInternalChildRange(i, 1);
                    }
                }
            }
            catch { }
        }

        /// <summary>设置好子项尺寸后滚动位置会变，重新计算。</summary>
        protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
        {
            base.OnItemsChanged(sender, args);
            offsetY = 0;
            if (ScrollOwner != null) ScrollOwner.InvalidateScrollInfo();
            InvalidateMeasure();
        }
    }
}
