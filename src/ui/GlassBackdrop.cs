using System;
using System.Collections.Generic;
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

        // 注：这两个常量用于**截屏模式**（当前模式）。
        // 若切回固定背景图（CaptureFromFile），它们就不需要了。

        /// <summary>
        /// 缩图倍率（截屏后先缩小再模糊，大幅降低 GPU 开销）。
        ///
        /// 1/4 是实测的平衡点：
        ///   · 1/8 太小 —— 1053×632 会变成 131×79，壁纸的色块边界全丢，
        ///     拉回原尺寸后只剩一片平均色，看不出"透的是壁纸"
        ///   · 1/2 太大 —— 模糊开销翻 4 倍，但没有可见收益
        /// </summary>
        const int Downscale = 4;

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
        Border outerLayer;             // 外层：统一裁剪圆角
        Border innerLayer;             // 内层：图片 + 模糊
        Border tintLayer;              // 染色层：叠在模糊图上，提供"玻璃色调"
        Border grainLayer;             // 噪点层：磨砂颗粒感
        Border highlightLayer;         // 高光层：内边缘 1px 亮线，模拟玻璃厚度

        ImageSource cached;
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

        public GlassBackdrop(Window owner, Panel host, string resDirectory)
        {
            this.owner = owner;
            this.host = host;
            this.resDir = resDirectory;
        }

        /// <summary>当前是否可用（软件渲染时不可用）。</summary>
        public static bool Supported { get { return !SlowRender; } }

        /// <summary>玻璃浓度 0-100。0 = 完全关闭（不读图、不垫底）。</summary>
        public double OpacityPercent = 72;

        /// <summary>
        /// 模糊半径（作用在裁剪后的背景图上）。
        ///
        /// 固定背景图模式下，图片按面板比例裁剪后被 Stretch.Fill 铺满，
        /// 所以 Radius 就是**视觉上的模糊像素数**。
        ///
        /// 10.0 是"看得出是渐变、但看不出任何边界"的程度 ——
        /// 青灰背景本身就是柔和的光斑叠加，模糊只是让边缘不显生硬。
        /// 若切回截屏模式（CaptureFromScreen），需要改成 5.0：
        /// 那时图片先缩了 4 倍，视觉模糊 = Radius × 4。
        /// </summary>
        public double BlurRadius = 10.0;

        /// <summary>
        /// 染色强度（0-1）。
        ///
        /// **为什么必须染色**：毛玻璃的本质是"透出背景色 + 模糊"。
        /// 如果壁纸本身接近纯白（实测这台机器平均亮度 244/255），
        /// 透出来还是白的 —— 用户会觉得"看不出效果"。
        /// 真正的毛玻璃（macOS、Win11 亚克力）都会**染色**：
        /// 叠一层极淡的强调色，让面板在任何壁纸上都有明确的"玻璃色调"，
        /// 而不是完全被动地复制背景。
        ///
        /// 0.10 = 10% 的强调色，足够看出色调、又不会盖住壁纸的明暗层次。
        /// </summary>
        public double TintStrength = 0.10;

        /// <summary>
        /// 噪点强度（0-1）。
        ///
        /// **这是白壁纸上唯一有效的质感来源**。
        /// 实测这台机器的壁纸平均亮度 244/255（接近纯白），
        /// 模糊后就是一片白 —— 染色能给出色调，但"磨砂感"必须靠颗粒。
        ///
        /// 真磨砂玻璃的微观结构就是无数微小颗粒，所以噪点不是"装饰"，
        /// 而是这种材质的物理特征。2.5% 的强度：能看出颗粒、又不脏。
        /// </summary>
        public double GrainStrength = 0.025;

        /// <summary>
        /// 内边缘高光强度（0-1）。
        ///
        /// 模拟玻璃厚度的折射：面板内侧一圈极淡的亮线，
        /// 让面板看起来有"厚度"而不是一张贴纸。
        /// </summary>
        public double HighlightStrength = 0.30;

        /// <summary>缓存的噪点图（生成一次，全局复用）。</summary>
        static BitmapSource grainTile;

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

        /// <summary>
        /// 背景图文件名（res 目录下）。
        ///
        /// **为什么不再截屏**：
        /// 截屏方案依赖桌面壁纸，壁纸一换观感全变，而且壁纸上的文字/图案
        /// 会透过来干扰阅读 —— 实测壁纸上的"我们买了50箱泡面"透过
        /// 77% 不透明度的面板仍然清晰可辨。
        ///
        /// 固定背景图则完全可控：配色自己定，永远不干扰文字。
        ///
        /// 这张 background-cyangray.png 是**青灰玻璃**：
        ///   · 打底 #172A30（深青），对角渐变到 #3E484F（青灰）
        ///   · 左上青蓝光斑 #4FC3D9（alpha 52）—— 模拟玻璃受光
        ///   · 右下冷灰光斑 #455058（alpha 45）
        ///   · 中部极淡水平亮带（alpha 28）—— 玻璃厚度感
        ///   · 细噪点 ±2 灰度 —— 磨砂质感
        ///   实测采样：左上 #234048、中心 #39535C、右下 #3D4950，
        ///   平均 RGB(50,73,81)，亮度差 33（有层次不死板）。
        ///
        /// 与面板主色 #172A30 同族，所以整体协调。
        /// </summary>
        const string BackgroundFile = "background-cyangray.png";

        /// <summary>背景图所在目录（res）。构造时传入。</summary>
        string resDir;

        /// <summary>已加载的背景图（只读一次，之后复用）。</summary>
        BitmapImage backgroundImage;

        /// <summary>截取窗口所在区域并缩小、模糊。</summary>
        /// <summary>
        /// 取得背景图（读 res\background-cyangray.png 并裁剪）。
        ///
        /// **当前用固定背景图**：用户明确要"青灰玻璃"这种固定观感，
        /// 不跟随桌面壁纸。截屏方案（CopyFromScreen）的代码保留在
        /// CaptureFromScreen() 里，想切回去改这一行即可。
        /// </summary>
        ImageSource Capture(Native.RECT rect, int dpi)
        {
            try
            {
                return CaptureFromFile(rect, dpi);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "读取毛玻璃背景");
                return null;
            }
        }

        /// <summary>
        /// 截取桌面壁纸（备用方案，当前未调用）。
        ///
        /// 保留它是因为：截屏模式的"透出真实桌面"是另一种合理审美，
        /// 而且它已经调好了（Downscale 4 / Inset 14 / BlurRadius 5）。
        /// 想切回去只需把 Capture() 里的调用换成 CaptureFromScreen。
        /// </summary>
        ImageSource CaptureFromScreen(Native.RECT rect, int dpi)
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
                        img.Freeze();
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

        /// <summary>
        /// 从 res 读固定背景图，并按面板比例裁剪。
        ///
        /// 为什么不直接整图铺：背景图是 1920×1200（屏幕尺寸），
        /// 面板只是其中一块。整图 Stretch.Fill 会把画面压扁变形。
        /// 这里按面板的宽高比从对应位置裁一块。
        ///
        /// 为什么裁剪位置跟着面板走：让背景随面板移动而"平移"，
        /// 视觉上像一张固定的壁纸在面板后面（而不是每块面板都显示
        /// 同一块画面，那样移动时会觉得背景"粘"在窗口上）。
        /// </summary>
        ImageSource CaptureFromFile(Native.RECT rect, int dpi)
        {
            try
            {
                if (backgroundImage == null)
                {
                    if (string.IsNullOrEmpty(resDir)) return null;
                    string path = Path.Combine(resDir, BackgroundFile);
                    if (!File.Exists(path)) return null;

                    BitmapImage bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri(path, UriKind.Absolute);
                    bi.EndInit();
                    bi.Freeze();
                    backgroundImage = bi;
                }

                BitmapImage cached = backgroundImage;

                // 按面板在屏幕上的相对位置，从背景图里裁对应区域
                double scrW = Math.Max(1, SystemParameters.PrimaryScreenWidth);
                double scrH = Math.Max(1, SystemParameters.PrimaryScreenHeight);
                double relX = (rect.Left + (rect.Right - rect.Left) / 2.0) / scrW;
                double relY = (rect.Top + (rect.Bottom - rect.Top) / 2.0) / scrH;

                double panelW = Math.Max(1, rect.Right - rect.Left);
                double panelH = Math.Max(1, rect.Bottom - rect.Top);

                // 裁剪窗口：以面板中心为锚点，尺寸按面板比例
                double cropW = cached.PixelWidth * Math.Min(1.0, panelW / scrW + 0.35);
                double cropH = cropW * (panelH / panelW);
                if (cropH > cached.PixelHeight)
                {
                    cropH = cached.PixelHeight;
                    cropW = cropH * (panelW / panelH);
                }

                int cx = (int)Math.Round(relX * cached.PixelWidth);
                int cy = (int)Math.Round(relY * cached.PixelHeight);
                int x0 = (int)Math.Max(0, Math.Min(cached.PixelWidth - cropW, cx - cropW / 2));
                int y0 = (int)Math.Max(0, Math.Min(cached.PixelHeight - cropH, cy - cropH / 2));
                int cw = (int)Math.Max(1, Math.Min(cropW, cached.PixelWidth - x0));
                int ch = (int)Math.Max(1, Math.Min(cropH, cached.PixelHeight - y0));

                CroppedBitmap crop = new CroppedBitmap(cached, new Int32Rect(x0, y0, cw, ch));
                crop.Freeze();
                return crop;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "读取背景图");
                return null;
            }
        }

        /// <summary>
        /// 生成 64×64 的噪点平铺图。
        ///
        /// 做法：随机灰度 + 随机 alpha，画到一个 WPF 位图上并冻结。
        /// 冻结后可跨窗口/跨次复用，所以做成 static 缓存（只生成一次）。
        ///
        /// 为什么是 64×64：够大以至于平铺时看不出重复，又够小（16 KB）
        /// 以至于不影响内存。视觉上人眼对这么细的随机噪点分辨不出周期性。
        /// </summary>
        static BitmapSource BuildGrainTile()
        {
            const int N = 64;
            try
            {
                // 用 Bgra32 直接写像素，比逐点 SetPixel 快几百倍
                int stride = N * 4;
                byte[] pixels = new byte[N * N * 4];
                Random rnd = new Random(20261028);   // 固定种子：每次生成同一张，避免闪烁

                for (int i = 0; i < N * N; i++)
                {
                    // 灰度量：0-255 随机；alpha：随机但整体很低（真正的强度由
                    // ImageBrush.Opacity 控制，这里给满 alpha 保证颗粒清晰）
                    byte v = (byte)rnd.Next(0, 256);
                    int o = i * 4;
                    pixels[o + 0] = v;      // B
                    pixels[o + 1] = v;      // G
                    pixels[o + 2] = v;      // R
                    pixels[o + 3] = 255;    // A
                }

                BitmapSource bmp = BitmapSource.Create(
                    N, N, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>把背景图挂到最底层（幂等：已有就更新，没有就建）。</summary>
        void Attach(ImageSource img)
        {
            try
            {
                if (img == null || host == null) return;

                if (blur == null)
                {
                    blur = new BlurEffect();
                    // Quality 而不是 Performance：缩图后图很小，Quality 的开销
                    // 可以忽略，但边缘过渡明显更平滑（Performance 会有块状感）
                    blur.RenderingBias = RenderingBias.Quality;
                }
                blur.Radius = BlurRadius;

                if (outerLayer == null)
                {
                    // 结构（自下而上，四层叠加 = 真玻璃质感）：
                    //   outerLayer（裁剪圆角）
                    //     └ Grid
                    //         ├ innerLayer    （模糊图 → 透出壁纸明暗）
                    //         ├ tintLayer     （染色   → 玻璃色调）
                    //         ├ grainLayer    （噪点   → 磨砂颗粒，白壁纸上的关键）
                    //         └ highlightLayer（内边缘高光 → 玻璃厚度）
                    //
                    // 为什么用 Grid 叠多层而不是在图上画：
                    // ImageBrush 无法叠加颜色/纹理，每层必须独立，
                    // 否则换色/调强度都要重新处理位图（那要几百毫秒）。
                    innerLayer = new Border();
                    innerLayer.Tag = "glassBackdrop";
                    innerLayer.Effect = blur;

                    tintLayer = new Border();
                    tintLayer.Tag = "glassBackdropTint";

                    grainLayer = new Border();
                    grainLayer.Tag = "glassBackdropGrain";

                    highlightLayer = new Border();
                    highlightLayer.Tag = "glassBackdropHighlight";

                    Grid stack = new Grid();
                    stack.Children.Add(innerLayer);
                    stack.Children.Add(tintLayer);
                    stack.Children.Add(grainLayer);
                    stack.Children.Add(highlightLayer);

                    outerLayer = new Border();
                    outerLayer.Tag = "glassBackdrop";
                    outerLayer.IsHitTestVisible = false;   // 不挡鼠标
                    outerLayer.Child = stack;

                    host.Children.Insert(0, outerLayer);
                }

                // ---- 噪点（磨砂颗粒）----
                //
                // 用 ImageBrush + TileMode.Tile 平铺一张 64×64 的随机噪点图。
                // 为什么平铺而不是整图：一张 1053×632 的噪点图要 2.5 MB 内存，
                // 而 64×64 只有 16 KB，平铺后视觉上分辨不出重复。
                try
                {
                    if (grainLayer != null && GrainStrength > 0)
                    {
                        if (grainTile == null) grainTile = BuildGrainTile();

                        ImageBrush gb = new ImageBrush(grainTile);
                        gb.TileMode = TileMode.Tile;
                        gb.Stretch = Stretch.None;
                        gb.Viewport = new Rect(0, 0, 64, 64);
                        gb.ViewportUnits = BrushMappingMode.Absolute;
                        gb.Opacity = GrainStrength;
                        gb.Freeze();
                        grainLayer.Background = gb;
                    }
                    else if (grainLayer != null)
                    {
                        grainLayer.Background = null;
                    }
                }
                catch { }

                // ---- 内边缘高光（玻璃厚度）----
                //
                // 用 BorderBrush + 1px 内边距实现：外圈亮、内部透明。
                // 为什么不用 DropShadowEffect(InnerShadow)：WPF 没有内阴影，
                // 用 Border 的边框是最省的做法，且不引入额外 Effect 开销。
                try
                {
                    if (highlightLayer != null && HighlightStrength > 0)
                    {
                        byte a = (byte)Math.Round(Math.Max(0, Math.Min(1, HighlightStrength)) * 255);
                        SolidColorBrush hl = new SolidColorBrush(Color.FromArgb(a, 255, 255, 255));
                        hl.Freeze();
                        highlightLayer.BorderBrush = hl;
                        highlightLayer.BorderThickness = new Thickness(1);
                        highlightLayer.CornerRadius = new CornerRadius(CornerRadius);
                    }
                }
                catch { }

                // 染色：用当前强调色 + 极低 alpha。
                // 强调色随主题变（深色下更亮），所以每次 Attach 都重设。
                try
                {
                    Color accent = Ui.Accent.Color;
                    byte a = (byte)Math.Round(Math.Max(0, Math.Min(1, TintStrength)) * 255);
                    SolidColorBrush tint = new SolidColorBrush(
                        Color.FromArgb(a, accent.R, accent.G, accent.B));
                    tint.Freeze();
                    tintLayer.Background = tint;
                }
                catch { }

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

                // 图片层用**满不透明**，玻璃浓度改由底色的 alpha 控制。
                //
                // 为什么改：原先两层都在压不透明度（图 0.8 × 底色留 0.68），
                // 壁纸实际只透出 26% —— 实测五个采样点差值全是 11，
                // 说明背景变成了均匀纯色，完全看不出"透的是壁纸"。
                // 现在图 100% 铺开，只靠底色 alpha 调节"多少底色盖在上面"，
                // 链路简单、可控，也不会重复衰减。
                outerLayer.Opacity = 1.0;
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
