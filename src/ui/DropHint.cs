using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Clover
{
    /// <summary>
    /// 「把文件拖到这里」的空状态提示层。
    ///
    /// ── 形态 ──
    ///
    /// 常驻控件，**只切 Visibility / Opacity**，运行期不增删任何子元素。
    /// 这一条是硬约束：项目里踩过两次 —— 动态 Add/Remove 会改变命中测试区域，
    /// 系统在边界反复派发 Enter/Leave，表现为高亮 7~8ms 抖动。
    ///
    /// ── 为什么不接收事件 ──
    ///
    /// `IsHitTestVisible = false` 只加在**自己（root）身上**。它没有可点击的
    /// 子元素（纯文字 + 字形 + 两条装饰边框），关掉命中不影响任何交互；
    /// 而框选绑在底下的 `deskCanvas` 上，DropHint 不参与命中就不会抢走它。
    ///
    /// 高亮态由外部（`PanelWindow` 的 DragEnter/DragLeave）驱动 `SetActive`，
    /// 自己不听任何拖放事件 —— 这样整个面板只有一个拖放目标，不会分叉。
    ///
    /// ── 边框为什么是两条常驻矩形 ──
    ///
    /// 需求要「常态 1px 虚线 / 拖入态 1.5px 实线」，而 WPF 的 `Border`
    /// **不支持虚线**（没有 StrokeDashArray），`Rectangle` 支持但换线型
    /// 无法动画。所以两条都常驻：
    ///
    ///   dashRect   Stroke=Ui.Line,   1px,   虚线  → 常态可见
    ///   solidRect  Stroke=Ui.Accent, 1.5px, 实线  → 拖入态可见
    ///
    /// 切换时只做 120ms 的 Opacity 交叉淡入淡出 —— 平滑、且子元素数量恒定。
    ///
    /// ── 样式（只用现有令牌）──
    ///
    ///   圆角        Ui.R.Md (12)
    ///   内边距      Ui.S.Xl (20)
    ///   常态        1px 虚线 Ui.Line，底色透明
    ///   拖入态      1.5px 实线 Ui.Accent，底色 Ui.AccentSoft
    ///   过渡        边框 + 底色 120ms（不做缩放/弹跳）
    ///   主文案      13px Ui.Fg
    ///   约束行      10.5px Ui.Sub
    ///   图标        32px Segoe MDL2 字形（沿用项目做法，不用 emoji）
    /// </summary>
    class DropHint
    {
        /// <summary>过渡时长（边框交叉淡化 + 底色）。</summary>
        const int FadeMs = 120;

        readonly Border root;
        readonly Grid layers;
        readonly Rectangle dashRect;
        readonly Rectangle solidRect;
        readonly TextBlock icon;
        readonly TextBlock title;
        readonly TextBlock hint;

        /// <summary>当前是否处于拖入高亮态。</summary>
        public bool IsActive { get; private set; }

        /// <summary>常态主文案。可变 —— 见 SetTitle。</summary>
        string idleTitle;

        /// <summary>拖入态文案。</summary>
        const string ActiveTitle = "松开即可放入";

        /// <summary>
        /// 构造。**构造时把所有子元素建好**，之后只改属性与 Visibility/Opacity。
        /// </summary>
        /// <param name="title">常态主文案</param>
        /// <param name="hint">约束行；null 或空 = 建出来但隐藏（子元素数量仍恒定）</param>
        public DropHint(string title, string hint)
        {
            idleTitle = string.IsNullOrEmpty(title) ? "把文件拖到这里" : title;

            // ---------- 根 ----------
            root = new Border();
            root.CornerRadius = new CornerRadius(Ui.R.Md);
            root.Padding = new Thickness(Ui.S.Xl);
            root.Margin = new Thickness(Ui.S.Lg);
            root.HorizontalAlignment = HorizontalAlignment.Stretch;
            root.VerticalAlignment = VerticalAlignment.Center;
            root.Background = new SolidColorBrush(Colors.Transparent);

            // ★ 只关自己的命中测试。没有可点击子元素，所以这一行不会
            //   影响任何交互，也不会把底下的框选抢走。
            root.IsHitTestVisible = false;
            root.Visibility = Visibility.Collapsed;

            // ---------- 叠层：两条边框 + 内容 ----------
            layers = new Grid();

            // 虚线层（常态）
            dashRect = new Rectangle();
            dashRect.Stroke = Ui.Line;
            dashRect.StrokeThickness = 1;
            dashRect.StrokeDashArray = MakeDash();
            dashRect.RadiusX = Ui.R.Md;
            dashRect.RadiusY = Ui.R.Md;
            dashRect.IsHitTestVisible = false;
            dashRect.Opacity = 1;
            layers.Children.Add(dashRect);

            // 实线层（拖入态）
            //
            // 初始 Visibility=Collapsed：常态根本不需要它参与渲染。
            // 高亮时才显示并淡入 —— 见 ApplyBorderInstantly。
            solidRect = new Rectangle();
            solidRect.Stroke = Ui.Accent;
            solidRect.StrokeThickness = 1.5;
            solidRect.RadiusX = Ui.R.Md;
            solidRect.RadiusY = Ui.R.Md;
            solidRect.IsHitTestVisible = false;
            solidRect.Opacity = 0;
            solidRect.Visibility = Visibility.Collapsed;
            layers.Children.Add(solidRect);

            // 内容
            StackPanel col = new StackPanel();
            col.Orientation = Orientation.Vertical;
            col.HorizontalAlignment = HorizontalAlignment.Center;
            col.VerticalAlignment = VerticalAlignment.Center;

            // ---- 图标：Segoe MDL2 线性字形（项目现有做法）----
            // \uE8E5 是「文件」类字形，语义贴合"往这里放文件"
            icon = new TextBlock();
            icon.Text = "\uE8E5";
            icon.FontFamily = Ui.Mdl;
            icon.FontSize = 32;
            icon.Foreground = Ui.Sub;
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            col.Children.Add(icon);

            // ---- 主文案 ----
            this.title = Ui.T(idleTitle, 13, Ui.Fg);
            this.title.HorizontalAlignment = HorizontalAlignment.Center;
            this.title.Margin = new Thickness(0, Ui.S.Md, 0, 0);
            col.Children.Add(this.title);

            // ---- 约束行 ----
            //
            // ★ 即使没传约束文案也照样建出来、只切 Visibility：
            //   这样「有约束」和「没约束」两种用法的子元素数量一致，
            //   运行期永远不会增删子元素。
            this.hint = Ui.T(hint ?? "", 10.5, Ui.Sub);
            this.hint.HorizontalAlignment = HorizontalAlignment.Center;
            this.hint.TextWrapping = TextWrapping.Wrap;
            this.hint.TextAlignment = TextAlignment.Center;
            this.hint.Margin = new Thickness(0, Ui.S.Xs, 0, 0);
            this.hint.Visibility = string.IsNullOrEmpty(hint)
                ? Visibility.Collapsed : Visibility.Visible;
            col.Children.Add(this.hint);

            layers.Children.Add(col);
            root.Child = layers;
        }

        // ========== 对外 ==========

        /// <summary>根元素（由宿主加进可视树）。</summary>
        public FrameworkElement Root { get { return root; } }

        /// <summary>整体是否可见（测试用）。</summary>
        public bool Visible
        {
            get { return root != null && root.Visibility == Visibility.Visible; }
        }

        /// <summary>约束行当前是否显示（测试用）。</summary>
        public bool HintVisible
        {
            get { return hint != null && hint.Visibility == Visibility.Visible; }
        }

        /// <summary>当前主文案（测试用）。</summary>
        public string TitleText
        {
            get { return title == null ? "" : title.Text; }
        }

        /// <summary>当前约束行文案（测试用）。</summary>
        public string HintText
        {
            get { return hint == null ? "" : hint.Text; }
        }

        /// <summary>
        /// 换文案。**只改文本，不增删子元素**。
        ///
        /// 为什么需要它：有的入口文案会随状态变（比如「文件」标签的来源
        /// 加满 10 个后，提示要从「把文件夹拖进来」变成「已达上限 10 个」）。
        /// 换成重建一个 DropHint 会破坏「常驻」这条约束。
        /// </summary>
        public void SetTitle(string newTitle, string newHint)
        {
            try
            {
                if (!string.IsNullOrEmpty(newTitle))
                {
                    idleTitle = newTitle;
                    // 高亮态时不要覆盖「松开即可放入」
                    if (!IsActive && title != null) title.Text = newTitle;
                }

                if (hint != null)
                {
                    if (!string.IsNullOrEmpty(newHint))
                    {
                        hint.Text = newHint;
                        hint.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        hint.Text = "";
                        hint.Visibility = Visibility.Collapsed;
                    }
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "更新放置提示文案"); }
        }

        /// <summary>
        /// 显示 / 隐藏。**只切 Visibility**，不动子元素。
        ///
        /// 隐藏时顺便清掉高亮态：否则「拖入中被隐藏 → 再显示」会残留实线边框。
        /// </summary>
        public void SetVisible(bool on)
        {
            try
            {
                if (root == null) return;

                if (!on) SetActive(false);
                root.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) { Ui.LogError(ex, "显示/隐藏放置提示"); }
        }

        /// <summary>
        /// 切换拖入高亮态。由宿主的 DragEnter / DragLeave 驱动。
        ///
        /// 已经处于目标状态时直接返回 —— DragOver 高频触发，
        /// 反复启动动画会让边框看起来在闪。
        ///
        /// ── 为什么只有实线矩形做动画 ──
        ///
        /// 高亮态**只在拖放期间出现**，而这个窗口是 `AllowsTransparency=true`
        /// 的分层窗口，任何子元素变化都要重合成整个窗口；同时 OLE 拖放循环
        /// 会节流消息泵，动画本来就跑不满帧。
        ///
        /// 第一版同时给虚线矩形、实线矩形、底色三个属性做动画，实测卡顿。
        /// 根因是**虚线矩形的透明度动画最贵** —— `StrokeDashArray` 每帧都要
        /// 重新切分线段。所以：
        ///
        ///   · 虚线矩形：不做动画，高亮时直接 Collapsed（连渲染都省掉）
        ///   · 实线矩形：保留 120ms 透明度淡入（普通矩形，无切分）
        ///   · 底色：瞬时切换，不做 ColorAnimation
        /// </summary>
        public void SetActive(bool on)
        {
            try
            {
                if (root == null || IsActive == on) return;
                IsActive = on;

                // ---- 文字与图标色 ----
                if (title != null) title.Text = on ? ActiveTitle : idleTitle;
                if (icon != null) icon.Foreground = on ? Ui.Accent : Ui.Sub;

                // ---- 边框：硬切线型，只有实线那一层做淡入 ----
                ApplyBorderInstantly(on);

                // ---- 底色：瞬时切换 ----
                ApplyBackground(on);
            }
            catch (Exception ex) { Ui.LogError(ex, "切换放置提示高亮"); }
        }

        /// <summary>
        /// 边框切换：**不碰虚线矩形的透明度**，只切 Visibility + 给实线做淡入。
        ///
        /// 高亮时虚线矩形的 Visibility 变 Collapsed —— 不只是不透明度为 0，
        /// 而是彻底不参与渲染，省掉每帧的虚线切分。
        /// </summary>
        void ApplyBorderInstantly(bool active)
        {
            try
            {
                if (active)
                {
                    dashRect.Visibility = Visibility.Collapsed;
                    solidRect.Visibility = Visibility.Visible;
                    solidRect.Opacity = 0;
                    Fade(solidRect, 1.0);      // 唯一的一个动画
                }
                else
                {
                    solidRect.Visibility = Visibility.Collapsed;
                    solidRect.Opacity = 0;
                    dashRect.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }

        /// <summary>
        /// 底色：直接换刷子，不做 ColorAnimation。
        ///
        /// 换 Brush 实例是硬切，但这里只切换一次、且底色是纯色填充，
        /// 视觉上几乎看不出差别；换来的是拖放期间不再有持续的合成压力。
        /// </summary>
        void ApplyBackground(bool on)
        {
            try
            {
                SolidColorBrush bg = root.Background as SolidColorBrush;
                if (bg == null || bg.IsFrozen)
                {
                    bg = new SolidColorBrush(Colors.Transparent);
                    root.Background = bg;
                }

                bg.BeginAnimation(SolidColorBrush.ColorProperty, null);   // 清掉可能残留的动画
                bg.Color = on ? Ui.AccentSoft.Color : Colors.Transparent;
            }
            catch { }
        }

        // ========== 内部 ==========

        static void Fade(UIElement e, double to)
        {
            try
            {
                if (e == null) return;
                if (Ui.ReducedMotion)
                {
                    e.BeginAnimation(UIElement.OpacityProperty, null);
                    e.Opacity = to;
                    return;
                }
                DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(FadeMs));
                a.FillBehavior = FillBehavior.HoldEnd;
                e.BeginAnimation(UIElement.OpacityProperty, a);
            }
            catch { }
        }

        static DoubleCollection MakeDash()
        {
            try
            {
                DoubleCollection d = new DoubleCollection();
                d.Add(4);
                d.Add(4);
                return d;
            }
            catch { return null; }
        }
    }
}
