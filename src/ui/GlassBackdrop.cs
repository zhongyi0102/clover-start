using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Rectangle = System.Windows.Shapes.Rectangle;
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
        /// <summary>
        /// 截屏降采样倍数。
        ///
        /// ── 为什么从 4 改成 2 ──
        ///
        /// 4 倍时：面板 912x632 → 截 884x604 → 缩到 221x151 → 再放大 4.1 倍。
        /// 放大 4 倍必然糊，这是画质的主要瓶颈（模糊只是掩盖了它）。
        ///
        /// 2 倍时：442x302 → 放大 2.05 倍，细节保留明显更好，
        /// 内存/耗时增加约 4 倍，但单帧只有 0.5 MB 级别，可接受。
        ///
        /// 副作用：视觉模糊半径 = BlurRadius × Downscale，
        /// 所以同样的 BlurRadius 看起来会"没那么糊"—— 这正是要的效果。
        /// </summary>
        const int Downscale = 2;

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
        Border brightLayer;            // 亮度层：半透明白/黑，实现提亮/压暗
        Border grainLayer;             // 噪点层：磨砂颗粒感
        Border highlightLayer;         // 高光层：内边缘 1px 亮线，模拟玻璃厚度
        Canvas guardLayer;              // 文字保底层：**只在文字区域**画半透明矩形

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

        // ---------- 外观参数（第 1 个提交新增）----------
        //
        // 由 ApplyGlassSettings 从 settings.json 注入。
        // 这些是「运行时可变」的参数，改了要 Refresh() 才生效。

        /// <summary>自定义染色色。null = 用当前强调色（改动前的行为）。</summary>
        public Color? TintColor;

        /// <summary>亮度偏移 -50 ~ +50（作用在模糊后的底图上）。</summary>
        public int Brightness;

        /// <summary>
        /// 文字清晰保底：是否启用。关掉时 guardLayer 会被清空（不残留蒙层）。
        /// </summary>
        public bool ContrastGuardEnabled = true;

        /// <summary>
        /// 当前保底计算的 alpha（诊断/测试用）。0 = 未补偿。
        /// </summary>
        public double GuardAlpha;


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
                // 缓存键必须包含背景来源身份。
                //
                // 之前只区分 screen / fixed：从固定图切到 WE scene 时，窗口位置、
                // DPI、主题都没变，便直接命中旧的 fixed 缓存，导致“提示已应用，
                // 面板仍是青灰背景”。WE 还必须带完整路径，否则两个 scene 之间
                // 切换也会继续复用上一张预览。
                string sourceKey;
                if (UseWeImage)
                {
                    string p = "";
                    try { p = System.IO.Path.GetFullPath(WeImagePath ?? ""); }
                    catch { p = WeImagePath ?? ""; }
                    sourceKey = "we:" + p.ToLowerInvariant();
                }
                else if (UseCustomImage)
                {
                    string p = BackgroundStore.Find(BaseDir) ?? "";
                    try { p = System.IO.Path.GetFullPath(p); } catch { }
                    sourceKey = "custom:" + p.ToLowerInvariant();
                }
                else
                {
                    sourceKey = FollowWallpaper ? "screen" : "fixed";
                }

                string key = rect.Left + "_" + rect.Top + "_" + w + "_" + h + "_" + dpi
                           + "_" + Ui.AppliedTheme
                           + "_" + sourceKey;

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
        /// 取得背景图。
        ///
        /// 两种模式（由 settings 的 glassMode 决定）：
        ///   · "fixed"  → 读 res\background-cyangray.png（观感稳定，默认）
        ///   · "screen" → 截取桌面壁纸并模糊（透出真实桌面）
        ///   · "custom" → 读用户导入的 config\background\custom.*（缺失则回退 fixed）
        ///   · "we"     → 读 WE 壁纸的静态图（scene 类；video 类由 VideoBackdrop 处理）
        ///
        /// 为什么要两种：固定图的配色完全可控，但"透出桌面"是另一种合理审美
        /// （换壁纸整个面板氛围跟着变）。用户自己选。
        /// </summary>
        ImageSource Capture(Native.RECT rect, int dpi)
        {
            try
            {
                // 自定义图优先：找不到/读不出会回退到内置固定图
                // WE 壁纸静态图优先（scene 类的 preview.*）
                if (UseWeImage)
                {
                    ImageSource we = CaptureFromWeImage(rect);
                    if (we != null) return we;
                }

                if (UseCustomImage)
                {
                    ImageSource custom = CaptureFromCustom(rect);
                    if (custom != null) return custom;
                    // 回退：继续往下走固定图分支（不 return）
                }

                if (FollowWallpaper) return CaptureFromScreen(rect, dpi);
                return CaptureFromFile(rect, dpi);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "取得毛玻璃背景");
                return null;
            }
        }

        /// <summary>
        /// 背景是否跟随桌面壁纸。由设置决定，切换后需要 Refresh。
        ///
        /// true  → 截屏 + 模糊（CaptureFromScreen）
        /// false → 读固定图（CaptureFromFile）
        /// </summary>
        public bool FollowWallpaper = false;

        /// <summary>
        /// 背景是否用用户导入的自定义图（`config\background\custom.*`）。
        ///
        /// true  → 读自定义图（找不到就回退内置固定图，见 Capture）
        /// false → 走原有逻辑（FollowWallpaper 决定 fixed / screen）
        /// </summary>
        public bool UseCustomImage = false;

        /// <summary>程序根目录（找 config\background\custom.* 用）。</summary>
        public string BaseDir = "";

        /// <summary>
        /// 背景是否用 WE 壁纸的静态图（scene 类的 preview.*）。
        ///
        /// 与 UseCustomImage 互斥：两个都为 false 时走固定图 / 跟随桌面。
        /// </summary>
        public bool UseWeImage = false;

        /// <summary>WE 壁纸静态图的绝对路径（每次定位后由上层注入）。</summary>
        public string WeImagePath = "";

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
        /// <summary>
        /// 从用户导入的自定义图裁剪一块。
        ///
        /// 裁剪逻辑与 `CaptureFromFile` 一致（按面板在屏幕上的相对位置取景），
        /// 区别只是图源换成了 `config\background\custom.*`。
        ///
        /// **失败一律返回 null** —— 调用方会回退到内置固定图，
        /// 不弹窗、不报错（用户可能手动删了那张图）。
        /// </summary>
        /// <summary>
        /// 从 WE 壁纸的静态图裁剪一块（scene 类的 preview.*）。
        ///
        /// 裁剪逻辑与 `CaptureFromCustom` 一致，只是图源换成了
        /// 上层注入的 `WeImagePath`。
        ///
        /// **失败返回 null** → 回退固定图（用户可能取消订阅了）。
        /// </summary>
        ImageSource CaptureFromWeImage(Native.RECT rect)
        {
            try
            {
                string path = WeImagePath;

                // 路径变了就重读（换壁纸 / 首次加载）
                if (weImage == null
                    || !string.Equals(weImagePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    weImage = null;
                    weImagePath = path;

                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

                    BitmapImage bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri(path, UriKind.Absolute);
                    bi.DecodePixelWidth = 3840;   // 限制解码尺寸
                    bi.EndInit();
                    bi.Freeze();
                    weImage = bi;
                }

                if (weImage == null) return null;

                BitmapImage cached = weImage;

                // ── 铺满面板（cover），取景锚点对准主体 ──
                //
                // 要求是「主体完整可见、多余部分裁掉」：
                //   · 铺满 → 不留白、不变形（等比放大到覆盖整个面板）
                //   · 多余裁掉 → 面板比例和图片比例不一致时，裁掉超出的一边
                //   · 主体优先 → 锚点不是机械居中，而是对准人物所在区域
                //
                // 主体位置：实测这张 WE 贴图的人物在纵向 13%~89%，
                // 中心约 51%。裁切时以这个点为锚，避免把头或脚裁掉。
                double panelW = Math.Max(1, rect.Right - rect.Left);
                double panelH = Math.Max(1, rect.Bottom - rect.Top);
                double panelRatio = panelW / panelH;

                double srcW = cached.PixelWidth;
                double srcH = cached.PixelHeight;
                double srcRatio = srcW / srcH;

                double cropW, cropH;
                if (srcRatio > panelRatio)
                {
                    // 源图更宽 → 高度铺满，裁左右
                    cropH = srcH;
                    cropW = srcH * panelRatio;
                }
                else
                {
                    // 源图更高（竖图铺横面板）→ 宽度铺满，裁上下
                    cropW = srcW;
                    cropH = srcW / panelRatio;
                }

                cropW = Math.Max(1, Math.Min(cropW, srcW));
                cropH = Math.Max(1, Math.Min(cropH, srcH));

                // 锚点：横向居中，纵向对准主体（51%）
                const double AnchorY = 0.28;
                double x = (srcW - cropW) / 2.0;
                double y = srcH * AnchorY - cropH / 2.0;

                x = Math.Max(0, Math.Min(x, srcW - cropW));
                y = Math.Max(0, Math.Min(y, srcH - cropH));

                Int32Rect src = new Int32Rect(
                    (int)Math.Round(x), (int)Math.Round(y),
                    Math.Max(1, (int)Math.Round(cropW)),
                    Math.Max(1, (int)Math.Round(cropH)));

                CroppedBitmap crop = new CroppedBitmap(cached, src);
                crop.Freeze();
                return crop;
            }            catch (Exception ex)
            {
                try { Ui.Log("WE 壁纸静态图读取失败，回退固定图: " + ex.Message); } catch { }
                return null;
            }
        }

        /// <summary>已加载的 WE 静态图。</summary>
        BitmapImage weImage;
        string weImagePath = "";

        /// <summary>丢弃 WE 静态图缓存（换壁纸后调）。</summary>
        public void InvalidateWeCache()
        {
            weImage = null;
            weImagePath = "";
        }

        ImageSource CaptureFromCustom(Native.RECT rect)
        {
            try
            {
                if (customImage == null)
                {
                    if (string.IsNullOrEmpty(BaseDir)) return null;

                    // 解码宽度限制到 3840：4K 图全解码要几十 MB，
                    // 而背景最终只显示在面板那么大一块上
                    customImage = BackgroundStore.Load(BaseDir, BackgroundStore.MaxLongEdge);
                    if (customImage == null) return null;
                }

                BitmapImage cached = customImage;

                double scrW = Math.Max(1, SystemParameters.PrimaryScreenWidth);
                double scrH = Math.Max(1, SystemParameters.PrimaryScreenHeight);
                double relX = (rect.Left + (rect.Right - rect.Left) / 2.0) / scrW;
                double relY = (rect.Top + (rect.Bottom - rect.Top) / 2.0) / scrH;

                double panelW = Math.Max(1, rect.Right - rect.Left);
                double panelH = Math.Max(1, rect.Bottom - rect.Top);

                // 等比映射：面板占屏幕多大比例，就从图里取多大比例。
                //
                // 旧公式 `panelW/scrW + 0.35` 有两个毛病：
                //   1. 凭空多取 35%，面板 640 宽时要裁 68% 的图，取景和面板
                //      尺寸不成比例 —— 缩放窗口时背景跳变，不像"同一张图在动"
                //   2. 被 Math.Min(1.0, ...) 钳住：面板宽 ≥ 1248（65% 屏宽）
                //      后恒为 100%，尺寸再变背景完全不动 —— 这就是"大小不自适应"
                double cropW = cached.PixelWidth * Math.Min(1.0, panelW / scrW);
                double cropH = cropW * (panelH / panelW);

                if (cropH > cached.PixelHeight)
                {
                    cropH = cached.PixelHeight;
                    cropW = cropH * (panelW / panelH);
                }
                if (cropW > cached.PixelWidth)
                {
                    cropW = cached.PixelWidth;
                    cropH = cropW * (panelH / panelW);
                }

                double x = cached.PixelWidth * relX - cropW / 2.0;
                double y = cached.PixelHeight * relY - cropH / 2.0;

                x = Math.Max(0, Math.Min(x, cached.PixelWidth - cropW));
                y = Math.Max(0, Math.Min(y, cached.PixelHeight - cropH));

                Int32Rect src = new Int32Rect(
                    (int)Math.Round(x), (int)Math.Round(y),
                    Math.Max(1, (int)Math.Round(cropW)),
                    Math.Max(1, (int)Math.Round(cropH)));

                CroppedBitmap crop = new CroppedBitmap(cached, src);
                crop.Freeze();
                return crop;
            }
            catch (Exception ex)
            {
                try { Ui.Log("自定义背景裁剪失败，回退内置图: " + ex.Message); } catch { }
                return null;
            }
        }

        /// <summary>已加载的自定义背景图（只读一次）。</summary>
        BitmapImage customImage;

        /// <summary>丢弃缓存（导入/清除后调用，让下次重读）。</summary>
        public void InvalidateCustomCache()
        {
            customImage = null;
        }

        ImageSource CaptureFromFile(Native.RECT rect, int dpi)
        {
            try
            {
                if (backgroundImage == null)
                {
                    if (string.IsNullOrEmpty(resDir)) return null;
                    string path = System.IO.Path.Combine(resDir, BackgroundFile);
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
                // 等比映射：面板占屏幕多大比例，就从图里取多大比例。
                //
                // 旧公式 `panelW/scrW + 0.35` 有两个毛病：
                //   1. 凭空多取 35%，面板 640 宽时要裁 68% 的图，取景和面板
                //      尺寸不成比例 —— 缩放窗口时背景跳变，不像"同一张图在动"
                //   2. 被 Math.Min(1.0, ...) 钳住：面板宽 ≥ 1248（65% 屏宽）
                //      后恒为 100%，尺寸再变背景完全不动 —— 这就是"大小不自适应"
                double cropW = cached.PixelWidth * Math.Min(1.0, panelW / scrW);
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
        /// <summary>
        /// 应用文字清晰保底：按区域在 guardLayer 上画半透明矩形。
        ///
        /// **四条硬约束**：
        ///   1. 关掉时**彻底清空** guardLayer（不残留黑/白蒙层）
        ///   2. **只画文字区域**，不整面板压暗/提亮（否则毁掉毛玻璃观感）
        ///   3. 某区域已达标 → 不画（保护原有外观）
        ///   4. 用**降采样图**算，不碰全尺寸位图（性能）
        ///
        /// 采样来源：把当前已合成的背景（innerLayer 的图 + 染色 + 亮度）
        /// 用 RenderTargetBitmap 渲染到 64×40 的小图上，再交给 ContrastGuard。
        /// </summary>
        void AttachGuardLayer(ImageSource img)
        {
            try
            {
                if (guardLayer == null) return;

                // ---- 约束 1：关掉时彻底清空 ----
                guardLayer.Children.Clear();
                GuardAlpha = 0;
                if (!ContrastGuardEnabled) return;

                if (host == null || img == null) return;
                double pw = host.ActualWidth > 0 ? host.ActualWidth : owner.ActualWidth;
                double ph = host.ActualHeight > 0 ? host.ActualHeight : owner.ActualHeight;
                if (pw < 56 || ph < 56) return;

                // ---- 约束 4：降采样采样 ----
                //
                // 把「底图 + 亮度层 + 染色层」按顺序画到 64×40 的小图上。
                // 为什么不用 RenderTargetBitmap 渲染整个视觉树：那会把文字也画进去，
                // 而且开销大。这里只关心**背景色**，所以手动复刻三层叠加。
                byte[] samples = SampleBackground(img, pw, ph);
                if (samples == null) return;

                bool dark = Ui.AppliedTheme == Ui.ThemeMode.Dark;
                Color fg = Ui.Fg.Color, sub = Ui.Sub.Color;

                List<ContrastGuard.RegionNeed> needs = ContrastGuard.ComputeRegions(
                    samples, ContrastGuard.SampleW, ContrastGuard.SampleH, dark,
                    fg.R, fg.G, fg.B, sub.R, sub.G, sub.B);

                if (needs == null || needs.Count == 0) return;

                // ---- 约束 2 + 3：只画需要的区域 ----
                double maxAlpha = 0;
                foreach (ContrastGuard.RegionNeed need in needs)
                {
                    if (need.Alpha <= 0) continue;
                    if (need.Alpha > maxAlpha) maxAlpha = need.Alpha;

                    byte ov = (byte)(need.IsBlack ? 0 : 255);
                    byte a = (byte)Math.Round(Math.Max(0, Math.Min(1, need.Alpha)) * 255);
                    if (a == 0) continue;

                    Rectangle r = new Rectangle();
                    r.Fill = new SolidColorBrush(Color.FromArgb(a, ov, ov, ov));
                    r.Width = need.Region.W * pw;
                    r.Height = need.Region.H * ph;
                    r.RadiusX = r.RadiusY = 4;   // 轻微圆角，避免硬边
                    Canvas.SetLeft(r, need.Region.X * pw);
                    Canvas.SetTop(r, need.Region.Y * ph);
                    guardLayer.Children.Add(r);
                }
                GuardAlpha = maxAlpha;
            }
            catch (Exception ex)
            {
                // 异常：清空并回退（不补偿，绝不留下半成品蒙层）
                try { if (guardLayer != null) guardLayer.Children.Clear(); } catch { }
                GuardAlpha = 0;
                Ui.LogError(ex, "文字清晰保底");
            }
        }

        /// <summary>
        /// 把当前背景合成结果采样成 ContrastGuard 需要的 BGRA 数组。
        ///
        /// 复刻 Attach 的叠加顺序：底图 → 亮度层 → 染色层。
        /// 不含噪点（2.5%，影响可忽略）和高光（1px 边框，不在文字区域）。
        /// </summary>
        byte[] SampleBackground(ImageSource img, double pw, double ph)
        {
            try
            {
                int w = ContrastGuard.SampleW, h = ContrastGuard.SampleH;

                DrawingVisual dv = new DrawingVisual();
                using (DrawingContext dc = dv.RenderOpen())
                {
                    dc.DrawImage(img, new Rect(0, 0, w, h));

                    // 亮度层
                    int b = Math.Max(-50, Math.Min(50, Brightness));
                    if (b != 0)
                    {
                        byte a = (byte)Math.Round(Math.Abs(b) / 50.0 * 0.5 * 255);
                        Color c = b > 0 ? Colors.White : Colors.Black;
                        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)), null,
                            new Rect(0, 0, w, h));
                    }

                    // 染色层
                    Color tintColor = TintColor.HasValue ? TintColor.Value : Ui.Accent.Color;
                    byte ta = (byte)Math.Round(Math.Max(0, Math.Min(1, TintStrength)) * 255);
                    if (ta > 0)
                    {
                        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(ta, tintColor.R, tintColor.G, tintColor.B)),
                            null, new Rect(0, 0, w, h));
                    }
                }

                RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();

                byte[] px = new byte[w * h * 4];
                rtb.CopyPixels(px, w * 4, 0);
                return px;
            }
            catch { return null; }
        }

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

                    // 亮度层：叠一层半透明白/黑实现提亮/压暗。
                    // 为什么不用 Effect 调亮度：WPF 没有内置亮度 Effect，
                    // 而叠一层纯色是零成本（GPU 一次 alpha 混合）。
                    brightLayer = new Border();
                    brightLayer.Tag = "glassBackdropBright";

                    grainLayer = new Border();
                    grainLayer.Tag = "glassBackdropGrain";

                    highlightLayer = new Border();
                    highlightLayer.Tag = "glassBackdropHighlight";

                    // 文字保底层：Canvas 才能按区域精确定位（Grid 会拉伸子元素）
                    guardLayer = new Canvas();
                    guardLayer.Tag = "glassBackdropGuard";
                    guardLayer.IsHitTestVisible = false;

                    Grid stack = new Grid();
                    stack.Children.Add(innerLayer);
                    stack.Children.Add(brightLayer);   // 亮度层压在模糊图上方、染色下方
                    stack.Children.Add(tintLayer);
                    stack.Children.Add(grainLayer);
                    stack.Children.Add(highlightLayer);
                    stack.Children.Add(guardLayer);   // 保底层压在最上面

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
                    // 染色色优先用自定义 TintColor，否则用当前强调色（改动前的行为）
                    Color tintColor = TintColor.HasValue ? TintColor.Value : Ui.Accent.Color;
                    byte a = (byte)Math.Round(Math.Max(0, Math.Min(1, TintStrength)) * 255);
                    SolidColorBrush tint = new SolidColorBrush(
                        Color.FromArgb(a, tintColor.R, tintColor.G, tintColor.B));
                    tint.Freeze();
                    tintLayer.Background = tint;
                }
                catch { }

                // 高质量缩放：默认是 Linear，放大时明显发糊。
                // HighQuality 用 Fant 重采样，代价是每帧多几毫秒 GPU，
                // 但背景是静态缓存（只有尺寸/位置变才重算），完全划算。
                RenderOptions.SetBitmapScalingMode(innerLayer, BitmapScalingMode.HighQuality);
                innerLayer.Background = new ImageBrush(img) { Stretch = Stretch.Fill };

                // 文字清晰保底：必须放在所有层都设好之后
                // （它要采样「底图+亮度+染色」的合成结果）
                AttachGuardLayer(img);
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
