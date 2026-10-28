using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 毛玻璃背景（参考 GlassBoard 的做法，纯 WPF 实现）。
    ///
    /// **原理**：不调用系统亚克力 API（DwmExtendFrameIntoClientArea 之类），
    /// 而是「截取窗口所在位置的桌面背景 → 缩到 1/8 → 模糊 → 当图片垫在最底层」。
    ///
    /// 为什么这么做而不是用系统 API：
    ///   · 系统亚克力只对「窗口自身背景」生效，WPF 的 AllowsTransparency=true
    ///     窗口（本项目正是）会让它失效
    ///   · 系统 API 在 Win10/11 上行为不一致，且随系统更新变化
    ///   · 截屏方案完全可控，效果可预期
    ///
    /// 代价：
    ///   · 截屏有开销（一次约 5-15 ms），所以**必须缓存**，只在
    ///     位置/尺寸/DPI/主题变化时才重截
    ///   · 窗口可见时重截要**先把自己移出屏幕**，否则会截到自己形成"镜中镜"
    ///
    /// 性能与降级：
    ///   · 图片缩到 1/8：模糊半径固定时，图越小 GPU 开销越低，
    ///     反正要模糊，细节本来就看不见
    ///   · 软件渲染（RenderCapability.Tier &lt; 2）时**完全不做**：
    ///     整窗模糊在软件渲染下极贵，会让动画卡顿
    /// </summary>
    class GlassBackdrop
    {
        /// <summary>
        /// 软件渲染降级判定。
        ///
        /// Tier 0 = 无硬件加速，Tier 1 = 部分，Tier 2 = 完整。
        /// 只取高位（(tier &gt;&gt; 16)）才是真正的渲染层级别。
        /// </summary>
        static readonly bool SlowRender = (RenderCapability.Tier >> 16) < 2;

        /// <summary>缩图倍率。1/8 是"够糊又够快"的平衡点。</summary>
        const int Downscale = 8;

        /// <summary>
        /// 截屏时向内收缩的边距（DIP）。
        ///
        /// 为什么要收缩：窗口边缘是圆角，截到的四个角是圆角外的桌面，
        /// 直接铺上去会在圆角处露出不匹配的颜色。收缩后只用中间部分，
        /// 拉伸铺满时圆角区域被拉伸的像素覆盖，看不出接缝。
        /// </summary>
        const double Inset = 14;

        readonly Window owner;
        /// <summary>承载背景图的容器（背景会被插到它的最底层）。</summary>
        readonly Panel host;
        Border outerLayer;             // 外层：控制整体玻璃浓度
        Border innerLayer;             // 内层：图片 + 模糊

        BitmapImage cached;
        string cacheKey = "";
        BlurEffect blur;

        DispatcherTimer refreshTimer;

        /// <summary>
        /// 圆角半径。
        ///
        /// 为什么从外面传而不是读 host.CornerRadius：宿主是一个普通 Grid
        /// （为了能在同一层里叠「背景 + 内容」），Grid 没有 CornerRadius 属性。
        /// 圆角属于面板（root Border），所以由调用方传入。
        /// </summary>
        public double CornerRadius = 16;

        public GlassBackdrop(Window owner, Panel host)
        {
            this.owner = owner;
            this.host = host;
        }

        /// <summary>当前是否可用（软件渲染时不可用）。</summary>
        public static bool Supported { get { return !SlowRender; } }

        /// <summary>玻璃浓度 0-100。0 = 完全关闭（不截屏、不垫底）。</summary>
        public double OpacityPercent = 72;

        /// <summary>模糊半径。</summary>
        public double BlurRadius = 18;

        /// <summary>
        /// 按当前状态刷新背景。
        ///
        /// 调用时机：窗口显示后、位置/尺寸变化后、主题切换后。
        /// 内部有缓存键，没变化时直接复用（开销接近 0）。
        /// </summary>
        public void Refresh()
        {
            try
            {
                if (!Supported) return;
                if (OpacityPercent <= 0) { Clear(); return; }
                if (owner == null || host == null) return;

                // 窗口尺寸太小就不折腾了（还没布局完）
                if (owner.ActualWidth < 56 || owner.ActualHeight < 56) return;

                IntPtr hwnd;
                try { hwnd = new WindowInteropHelper(owner).EnsureHandle(); }
                catch (InvalidOperationException) { return; }   // 窗口已关闭

                Native.RECT rect;
                if (!Native.GetWindowRect(hwnd, out rect)) return;
                int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
                if (w < 56 || h < 56) return;

                int dpi = DpiOf(hwnd);
                string key = rect.Left + "_" + rect.Top + "_" + w + "_" + h + "_" + dpi
                           + "_" + Ui.AppliedTheme;

                if (cached != null && cacheKey == key)
                {
                    Attach(cached);
                    return;
                }

                // 窗口可见时直接截屏会截到自己 → 先移出屏幕
                bool wasVisible = owner.IsVisible;
                double oldLeft = owner.Left, oldTop = owner.Top;
                if (wasVisible)
                {
                    owner.Left = -30000;
                    owner.Top = -30000;
                }

                try
                {
                    cached = Capture(rect, dpi);
                    cacheKey = key;
                }
                finally
                {
                    if (wasVisible)
                    {
                        owner.Left = oldLeft;
                        owner.Top = oldTop;
                    }
                }

                if (cached != null) Attach(cached);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "毛玻璃背景");
            }
        }

        /// <summary>
        /// 延迟刷新（合并多次请求）。
        ///
        /// 切换壁纸/显示器时系统会连发多条广播，1.2 秒内只重截一次。
        /// </summary>
        public void RefreshSoon()
        {
            try
            {
                if (!Supported) return;
                if (refreshTimer == null)
                {
                    refreshTimer = new DispatcherTimer();
                    refreshTimer.Interval = TimeSpan.FromMilliseconds(1200);
                    refreshTimer.Tick += delegate
                    {
                        refreshTimer.Stop();
                        Refresh();
                    };
                }
                refreshTimer.Stop();
                refreshTimer.Start();
            }
            catch { }
        }

        /// <summary>截取窗口所在区域并缩小、模糊。</summary>
        BitmapImage Capture(Native.RECT rect, int dpi)
        {
            try
            {
                double scale = dpi / 96.0;
                int inset = (int)Math.Round(Inset * scale);

                int srcX = rect.Left + inset;
                int srcY = rect.Top + inset;
                int srcW = Math.Max(1, (rect.Right - rect.Left) - inset * 2);
                int srcH = Math.Max(1, (rect.Bottom - rect.Top) - inset * 2);

                // 屏幕边界保护：窗口可能有一部分在屏幕外（拖到边缘时）
                if (srcX < 0) { srcW += srcX; srcX = 0; }
                if (srcY < 0) { srcH += srcY; srcY = 0; }
                if (srcW < 1 || srcH < 1) return null;

                using (System.Drawing.Bitmap full = new System.Drawing.Bitmap(srcW, srcH))
                {
                    using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(full))
                    {
                        g.CopyFromScreen(srcX, srcY, 0, 0, new System.Drawing.Size(srcW, srcH));
                    }

                    // 缩到 1/8：模糊时细节不可见，但 GPU 开销降 64 倍
                    int sw = Math.Max(1, srcW / Downscale);
                    int sh = Math.Max(1, srcH / Downscale);
                    using (System.Drawing.Bitmap small = new System.Drawing.Bitmap(full, sw, sh))
                    using (MemoryStream ms = new MemoryStream())
                    {
                        small.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        ms.Position = 0;

                        BitmapImage img = new BitmapImage();
                        img.BeginInit();
                        img.CacheOption = BitmapCacheOption.OnLoad;
                        img.StreamSource = ms;
                        img.EndInit();
                        img.Freeze();   // 冻结后可跨线程/跨次复用
                        return img;
                    }
                }
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "截取毛玻璃背景");
                return null;
            }
        }

        /// <summary>把背景图挂到最底层（幂等：已有就更新，没有就建）。</summary>
        void Attach(BitmapImage img)
        {
            try
            {
                if (img == null || host == null) return;

                if (blur == null)
                {
                    blur = new BlurEffect();
                    blur.RenderingBias = RenderingBias.Performance;
                }
                blur.Radius = BlurRadius;

                if (outerLayer == null)
                {
                    innerLayer = new Border();
                    innerLayer.Tag = "glassBackdrop";
                    innerLayer.Effect = blur;

                    outerLayer = new Border();
                    outerLayer.Tag = "glassBackdrop";
                    outerLayer.IsHitTestVisible = false;   // 不挡鼠标
                    outerLayer.Child = innerLayer;

                    host.Children.Insert(0, outerLayer);
                }

                innerLayer.Background = new ImageBrush(img) { Stretch = Stretch.Fill };
                innerLayer.CornerRadius = new CornerRadius(CornerRadius);
                outerLayer.CornerRadius = new CornerRadius(CornerRadius);

                // 圆角裁剪：不裁的话图片会盖到圆角外面，露出方角
                double w = host.ActualWidth > 0 ? host.ActualWidth : owner.ActualWidth;
                double h = host.ActualHeight > 0 ? host.ActualHeight : owner.ActualHeight;
                RectangleGeometry clip = new RectangleGeometry(
                    new Rect(0, 0, Math.Max(1, w), Math.Max(1, h)),
                    CornerRadius, CornerRadius);
                innerLayer.Clip = clip;
                outerLayer.Clip = clip;

                outerLayer.Opacity = Math.Max(0, Math.Min(1, OpacityPercent / 100.0));
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "挂载毛玻璃背景");
            }
        }

        /// <summary>移除背景（关闭该功能或切换主题时）。</summary>
        public void Clear()
        {
            try
            {
                if (outerLayer != null && host != null)
                {
                    host.Children.Remove(outerLayer);
                }
                outerLayer = null;
                innerLayer = null;
                cached = null;
                cacheKey = "";
            }
            catch { }
        }

        /// <summary>停止定时器（退出时调用）。</summary>
        public void Stop()
        {
            try { if (refreshTimer != null) { refreshTimer.Stop(); refreshTimer = null; } }
            catch { }
        }

        /// <summary>窗口所在显示器的 DPI（Win10 1607+ 用 GetDpiForWindow，失败回退系统 DPI）。</summary>
        static int DpiOf(IntPtr hwnd)
        {
            try
            {
                int dpi = Native.GetDpiForWindow(hwnd);
                if (dpi > 0) return dpi;
            }
            catch { }
            try
            {
                uint d = Native.GetDpiForSystem();
                if (d > 0) return (int)d;
            }
            catch { }
            return 96;
        }
    }
}
