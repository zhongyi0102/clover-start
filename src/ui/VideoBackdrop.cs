using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 视频背景层：把 mp4 循环静音播放，作为面板背景。
    ///
    /// ── 为什么单独一层，不改 GlassBackdrop ──
    ///
    /// `GlassBackdrop` 是"取一帧图 → 模糊 → 贴到面板后面"的静态管线。
    /// 视频要的是"持续播放的画面"，两者机制不同：
    ///   · 静态：抓一次，缓存，除非参数变否则不重算
    ///   · 视频：`MediaElement` 自己解码播放，**不能每帧都过一遍模糊**
    ///
    /// 所以视频单独一层 `MediaElement` 铺在面板最底层，
    /// 上面再叠 `GlassBackdrop` 的模糊层（低频采样缩略帧）。
    ///
    /// ── 暂停策略 ──
    ///
    ///   · 面板隐藏 / 最小化 → 暂停（不浪费解码）
    ///   · 全屏应用运行 → 暂停
    ///   · 电池供电 → 暂停（省电）
    ///   · 失败 → 静默回退固定图（`MediaFailed` 只记日志）
    ///
    /// ── 音轨 ──
    ///
    /// **一律静音**（`IsMuted = true`）。背景视频出声是骚扰。
    /// </summary>
    class VideoBackdrop
    {
        /// <summary>宿主容器（插到最底层）。</summary>
        readonly Window owner;
        readonly Panel host;

        /// <summary>自适应策略轮询：面板可见/最小化、全屏、电池状态。</summary>
        DispatcherTimer policyTimer;

        /// <summary>视频元素。</summary>
        MediaElement media;

        /// <summary>当前播放的文件路径。</summary>
        string currentPath;

        /// <summary>是否已初始化过（避免重复插入）。</summary>
        bool attached;

        /// <summary>播放失败标记（避免反复重试）。</summary>
        bool failed;

        /// <summary>用户/系统要求的暂停（面板隐藏、全屏、电池）。</summary>
        bool pausedByPolicy;

        /// <summary>当前 media 是否已真正执行过这一版策略。</summary>
        bool policyApplied;

        /// <summary>测试用：当前视频源路径；未加载为 null。</summary>
        internal string CurrentPathForTest { get { return currentPath; } }
        /// <summary>视频是否正在使用。</summary>
        public bool InUse
        {
            get { return media != null && !string.IsNullOrEmpty(currentPath) && !failed; }
        }

        /// <summary>播放失败回调（让上层回退固定图）。</summary>
        public Action OnFailed;

        public VideoBackdrop(Window owner, Panel host)
        {
            this.owner = owner;
            this.host = host;

            StartPolicyTimer();
            if (owner != null)
            {
                owner.Closed += delegate { Stop(); };
            }
        }

        // ========== 加载 ==========

        /// <summary>
        /// 设置（或切换）视频源。传 null / 空 → 卸载。
        ///
        /// 同一路径重复调用是幂等的（不重开）。
        /// </summary>
        public void SetSource(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    Unload();
                    return;
                }

                if (attached && string.Equals(currentPath, path, StringComparison.OrdinalIgnoreCase))
                    return;   // 同一个文件，不重开

                Unload();

                currentPath = path;
                failed = false;
                policyApplied = false;   // 新 MediaElement 必须重新执行一次 Pause/Play 策略

                EnsureMedia();

                // 再校正一次层级：GlassBackdrop.Attach 可能在上次之后
                // 又把 outerLayer 插到了索引 0（盖住 media）
                InsertAboveGlass();

                try
                {
                    media.Source = new Uri(path, UriKind.Absolute);
                    media.Volume = 0;
                    media.IsMuted = true;
                    media.Play();
                    Ui.Log("视频背景开始播放: " + path);
                }
                catch (Exception ex)
                {
                    failed = true;
                    Ui.Log("视频背景启动失败: " + ex.Message);
                    if (OnFailed != null) { try { OnFailed(); } catch { } }
                }
            }
            catch (Exception ex)
            {
                failed = true;
                Ui.LogError(ex, "设置视频背景");
            }
        }

        /// <summary>卸载视频层。</summary>
        public void Unload()
        {
            try
            {
                if (media != null)
                {
                    try { media.Stop(); } catch { }
                    try { media.Close(); } catch { }
                    try { media.Source = null; } catch { }
                    try { if (host != null) host.Children.Remove(media); } catch { }
                    media = null;
                }
                attached = false;
                currentPath = null;
                failed = false;
                policyApplied = false;
            }
            catch { }
        }

        /// <summary>建出 MediaElement 并插到最底层。</summary>
        void EnsureMedia()
        {
            if (media != null) return;

            media = new MediaElement();
            media.LoadedBehavior = MediaState.Manual;
            media.UnloadedBehavior = MediaState.Manual;
            media.Stretch = Stretch.UniformToFill;
            media.IsMuted = true;
            media.Volume = 0;
            media.HorizontalAlignment = HorizontalAlignment.Stretch;
            media.VerticalAlignment = VerticalAlignment.Stretch;
            media.IsHitTestVisible = false;   // 不挡鼠标
            media.SnapsToDevicePixels = false;
            // 高质量缩放：窗口尺寸变化时视频按 UniformToFill 等比填充，
            // 默认的线性插值在大幅缩小时会有锯齿，HighQuality 更平滑。
            RenderOptions.SetBitmapScalingMode(media, BitmapScalingMode.HighQuality);

            media.MediaEnded += delegate
            {
                // 循环：播完从头再来
                try
                {
                    if (media == null || failed) return;
                    media.Position = TimeSpan.Zero;
                    if (!pausedByPolicy) media.Play();
                }
                catch { }
            };

            media.MediaFailed += delegate(object s, ExceptionRoutedEventArgs e)
            {
                try
                {
                    failed = true;
                    string msg = e != null && e.ErrorException != null
                        ? e.ErrorException.Message : "未知错误";
                    Ui.Log("视频背景播放失败，回退固定图: " + msg);
                    if (OnFailed != null) { try { OnFailed(); } catch { } }
                }
                catch { }
            };

            if (host != null)
            {
                // ★ 层级修正：不能插到索引 0 —— 那里会被 GlassBackdrop 的
                //   outerLayer（Opacity=1.0，完全不透明）盖死。
                //   正确位置：毛玻璃层**之上**、内容层之下。
                InsertAboveGlass();
                attached = true;
            }
        }

        /// <summary>
        /// 把 media 插到「毛玻璃层之上、内容层之下」。
        ///
        /// ── 为什么不能 Insert(0) ──
        ///
        /// contentHost 的子元素（从下往上）：
        ///   [0] GlassBackdrop.outerLayer   ← Opacity = 1.0，**完全不透明**
        ///   [1] darkOverlay（暗度遮罩）
        ///   [2] grid（面板内容）
        ///
        /// 如果把 media 插到索引 0，它就在 outerLayer **下面**，
        /// 被那层不透明的背景图完全盖住 —— 视频在播，但一个像素都看不见。
        ///
        /// 正确做法：插到 outerLayer 的**紧后面**（索引 +1），
        /// 这样渲染顺序是 media → 毛玻璃（半透明）→ 内容，视频能透出来。
        /// </summary>
        void InsertAboveGlass()
        {
            try
            {
                if (host == null || media == null) return;

                // 先移除（可能已在树里）
                try { host.Children.Remove(media); } catch { }

                // 找毛玻璃层
                int glassIndex = -1;
                for (int i = 0; i < host.Children.Count; i++)
                {
                    FrameworkElement fe = host.Children[i] as FrameworkElement;
                    if (fe != null && "glassBackdrop".Equals(fe.Tag)) { glassIndex = i; break; }
                }

                if (glassIndex >= 0)
                {
                    // 插到毛玻璃层之后
                    host.Children.Insert(glassIndex + 1, media);
                }
                else
                {
                    // 没有毛玻璃层 → 放最底下即可
                    host.Children.Insert(0, media);
                }

                attached = true;
            }
            catch (Exception ex)
            {
                try { Ui.Log("插入视频层失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// 重新校正层级（外部调）。
        ///
        /// 什么时候需要：`GlassBackdrop.Attach()` 会 `Insert(0, outerLayer)`，
        /// 如果它在我们之后跑，就会把毛玻璃层插到 media 前面把视频盖住。
        /// 每次 `ApplyGlassSetting` 之后调一次，保证顺序始终正确。
        /// </summary>
        public void VerifyLayer()
        {
            try
            {
                if (media == null || host == null) return;
                InsertAboveGlass();
            }
            catch { }
        }

        /// <summary>
        /// 启动自适应策略轮询。700ms 与 OverlayGate 的非全屏刷新频率一致。
        /// 这里只读几个状态；状态没变时 ApplyPolicy 会立即返回。
        /// </summary>
        void StartPolicyTimer()
        {
            try
            {
                if (policyTimer != null) return;
                policyTimer = new DispatcherTimer(DispatcherPriority.Background);
                policyTimer.Interval = TimeSpan.FromMilliseconds(700);
                policyTimer.Tick += delegate
                {
                    if (ErrorLog.ShuttingDown) return;
                    ApplyAdaptivePolicy();
                };
                policyTimer.Start();
            }
            catch { }
        }

        /// <summary>
        /// 根据实时环境自适应播放：面板隐藏/最小化、全屏应用、电池供电时暂停；
        /// 条件恢复后继续播放。
        /// </summary>
        public void ApplyAdaptivePolicy()
        {
            try
            {
                bool visible = owner != null
                    && owner.IsVisible
                    && owner.WindowState != WindowState.Minimized;
                bool fullscreen = false;
                try { fullscreen = FullscreenWatch.IsFullscreen(); } catch { }
                ApplyPolicy(visible, fullscreen, IsOnBattery());
            }
            catch { }
        }
        // ========== 暂停 / 恢复 ==========

        /// <summary>
        /// 按策略暂停或恢复。
        ///
        /// 三个条件任一为 true 就暂停：面板不可见 / 全屏应用 / 电池供电。
        /// </summary>
        public void ApplyPolicy(bool panelVisible, bool fullscreen, bool onBattery)
        {
            try
            {
                bool shouldPause = !panelVisible || fullscreen || onBattery;

                // media 还没建时不要把策略标成“已应用”。否则之后建出新 media，
                // 相同的 shouldPause 会被误判成无需处理，漏掉第一次 Pause/Play。
                if (media == null)
                {
                    pausedByPolicy = shouldPause;
                    policyApplied = false;
                    return;
                }

                if (policyApplied && shouldPause == pausedByPolicy) return;


                pausedByPolicy = shouldPause;
                policyApplied = true;

                if (shouldPause)
                {
                    try { media.Pause(); } catch { policyApplied = false; }
                }
                else if (!failed)
                {
                    try { media.Play(); } catch { policyApplied = false; }
                }
            }
            catch { policyApplied = false; }
        }
        /// <summary>当前是否因策略暂停。</summary>
        public bool IsPaused { get { return pausedByPolicy; } }

        /// <summary>彻底停止（退出时调）。</summary>
        public void Stop()
        {
            try
            {
                if (policyTimer != null)
                {
                    try { policyTimer.Stop(); } catch { }
                    policyTimer = null;
                }
                if (media != null)
                {
                    try { media.Stop(); } catch { }
                    try { media.Close(); } catch { }
                }
            }
            catch { }
        }
        // ========== 系统状态探测 ==========

        /// <summary>当前是否电池供电（暂停视频省电）。</summary>
        public static bool IsOnBattery()
        {
            try
            {
                System.Windows.Forms.PowerStatus ps = System.Windows.Forms.SystemInformation.PowerStatus;
                return ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;
            }
            catch { return false; }
        }
    }
}
