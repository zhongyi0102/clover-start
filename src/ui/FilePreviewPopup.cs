using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;

namespace Clover
{
    /// <summary>
    /// 「文件」页的空格预览浮层。
    ///
    /// ── 为什么用 Popup 而不是面板内分栏 ──
    ///
    /// 面板内分栏会挤占列表宽度；预览是"临时看一眼"的辅助动作，
    /// 不该改变主视图布局。Popup 浮在面板外侧，关掉就消失，零副作用。
    ///
    /// ── 位置策略 ──
    ///
    /// 优先右侧 → 放不下放左侧 → 都不行放下方。
    ///
    /// ── 读取放后台 ──
    ///
    /// 文本/图片读取可能几十毫秒到几百毫秒（大图、网络盘），
    /// 不能卡界面线程。用递增 token 丢弃过期结果：
    /// 用户连按 ↓ 时只有最后一次的读取结果会被显示。
    ///
    /// ── 不抢焦点 ──
    ///
    /// Popup 的 `StaysOpen=true` + `Focusable=false`，打开时面板不失焦 ——
    /// 否则会触发"点击面板外自动收起"，一预览面板就没了。
    /// </summary>
    class FilePreviewPopup
    {
        /// <summary>文本预览最多读多少字节（防止打开 100 MB 日志卡住）。</summary>
        const int MaxTextBytes = 200 * 1024;

        /// <summary>图片预览的最大边长（超过则等比缩小，避免解码超大图）。</summary>
        const int MaxImageDim = 1200;

        readonly Popup popup;
        readonly Border root;
        readonly StackPanel body;
        readonly TextBlock titleText;

        /// <summary>当前请求的 token。每次 Show 递增，过期结果直接丢弃。</summary>
        int token;

        /// <summary>当前是否打开。</summary>
        public bool IsOpen
        {
            get { try { return popup != null && popup.IsOpen; } catch { return false; } }
        }

        public FilePreviewPopup(Window owner)
        {
            // ---- 外壳 ----
            root = new Border();
            root.Width = 360;
            root.MaxHeight = 460;
            root.CornerRadius = new CornerRadius(Ui.R.Lg);
            root.Background = Ui.Bg;
            root.BorderBrush = Ui.Edge;
            root.BorderThickness = new Thickness(1);
            root.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 4,
                Opacity = 0.35,
                Color = Colors.Black
            };

            StackPanel outer = new StackPanel();
            outer.Orientation = Orientation.Vertical;
            outer.Margin = new Thickness(Ui.S.Lg);

            // 标题（文件名）
            titleText = Ui.T("", 12.5, Ui.Fg);
            titleText.FontWeight = FontWeights.SemiBold;
            titleText.TextTrimming = TextTrimming.CharacterEllipsis;
            titleText.Margin = new Thickness(0, 0, 0, Ui.S.Sm);
            outer.Children.Add(titleText);

            // 内容区（每次 Show 重建）
            body = new StackPanel();
            body.Orientation = Orientation.Vertical;
            outer.Children.Add(body);

            root.Child = outer;

            popup = new Popup();
            popup.Child = root;
            popup.PlacementTarget = owner;
            popup.Placement = PlacementMode.Right;
            popup.StaysOpen = true;       // 不因点击外部而关闭（预览要靠空格/Esc 关）
            popup.AllowsTransparency = true;
            popup.PopupAnimation = PopupAnimation.Fade;
            popup.HorizontalOffset = Ui.S.Md;
            popup.VerticalOffset = 0;
        }

        // ========== 显示 / 关闭 ==========

        /// <summary>
        /// 显示某个条目的预览。异步读取，旧请求自动作废。
        /// </summary>
        public void Show(FileScanner.Entry entry, Window owner)
        {
            try
            {
                if (entry == null) { Close(); return; }

                int myToken = ++token;

                titleText.Text = entry.Name;
                body.Children.Clear();
                body.Children.Add(Ui.T("读取中…", 11.5, Ui.Sub));

                PositionPopup(owner);
                popup.IsOpen = true;

                // 后台读取
                Thread t = new Thread(delegate()
                {
                    object content = null;
                    string error = null;
                    try { content = BuildContent(entry); }
                    catch (Exception ex) { error = ex.Message; }

                    // 回到界面线程，且只在 token 仍是最新时才更新
                    Application.Current.Dispatcher.BeginInvoke((Action)delegate
                    {
                        try
                        {
                            if (myToken != token) return;   // 已被更新的请求取代 → 丢弃
                            if (!popup.IsOpen) return;

                            body.Children.Clear();
                            if (error != null)
                            {
                                body.Children.Add(Ui.T("读取失败：" + error, 11, Ui.Sub));
                                return;
                            }
                            AppendContent(content, entry, owner);
                        }
                        catch (Exception ex) { Ui.LogError(ex, "预览内容回填"); }
                    });
                });
                t.IsBackground = true;
                t.Start();
            }
            catch (Exception ex) { Ui.LogError(ex, "显示预览"); }
        }

        /// <summary>关闭预览，并作废未完成的读取。</summary>
        public void Close()
        {
            try
            {
                token++;                 // 让所有在途请求失效
                if (popup != null) popup.IsOpen = false;
                if (body != null) body.Children.Clear();
            }
            catch { }
        }

        // ========== 位置 ==========

        /// <summary>
        /// 右侧优先 → 左侧 → 下方。用屏幕工作区判断放不放得下。
        /// </summary>
        void PositionPopup(Window owner)
        {
            try
            {
                if (owner == null) return;

                double w = root.Width;
                double panelLeft = owner.Left;
                double panelRight = owner.Left + owner.Width;
                double panelTop = owner.Top;
                double panelBottom = owner.Top + owner.Height;

                // 屏幕工作区（用 SystemParameters，多显示器时取主屏；够用）
                double screenW = SystemParameters.WorkArea.Right;
                double screenH = SystemParameters.WorkArea.Bottom;
                double screenLeft = SystemParameters.WorkArea.Left;

                const double gap = 8;

                if (panelRight + gap + w <= screenW)
                {
                    // 右侧放得下
                    popup.Placement = PlacementMode.Right;
                    popup.HorizontalOffset = gap;
                    popup.VerticalOffset = 0;
                }
                else if (panelLeft - gap - w >= screenLeft)
                {
                    // 左侧放得下
                    popup.Placement = PlacementMode.Left;
                    popup.HorizontalOffset = -gap;
                    popup.VerticalOffset = 0;
                }
                else
                {
                    // 下方
                    popup.Placement = PlacementMode.Bottom;
                    popup.HorizontalOffset = 0;
                    popup.VerticalOffset = gap;
                }
            }
            catch { }
        }

        // ========== 内容构造（后台线程）==========

        /// <summary>后台线程构造预览内容。返回的对象在界面线程消费。</summary>
        object BuildContent(FileScanner.Entry e)
        {
            if (e == null) return null;

            // ---- 文件夹 ----
            if (e.IsFolder)
            {
                int count = 0;
                try
                {
                    count = Directory.GetFileSystemEntries(e.Path).Length;
                }
                catch { count = -1; }
                return new FolderInfo { Count = count };
            }

            string ext = (e.Ext ?? "").ToLowerInvariant();

            // ---- 图片 ----
            if (IsImageExt(ext))
            {
                try
                {
                    BitmapImage img = new BitmapImage();
                    img.BeginInit();
                    img.UriSource = new Uri(e.Path, UriKind.Absolute);
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    // 超大图按边长限制解码（DecodePixelWidth 需要按比例算）
                    if (img.PixelWidth > MaxImageDim || img.PixelHeight > MaxImageDim)
                    {
                        if (img.PixelWidth >= img.PixelHeight)
                            img.DecodePixelWidth = MaxImageDim;
                        else
                            img.DecodePixelHeight = MaxImageDim;
                    }
                    img.EndInit();
                    img.Freeze();     // 跨线程使用必须 Freeze
                    return img;
                }
                catch { return null; }   // 解码失败 → 回落到基本信息
            }

            // ---- 文本 ----
            if (IsTextExt(ext))
            {
                try
                {
                    FileInfo fi = new FileInfo(e.Path);
                    using (FileStream fs = new FileStream(e.Path, FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite))
                    {
                        int len = (int)Math.Min(fs.Length, MaxTextBytes);
                        byte[] buf = new byte[len];
                        int read = fs.Read(buf, 0, len);

                        // 尝试 UTF-8，失败回退 GBK
                        string text = null;
                        try
                        {
                            UTF8Encoding utf8 = new UTF8Encoding(false, true);
                            text = utf8.GetString(buf, 0, read);
                        }
                        catch
                        {
                            try { text = Encoding.GetEncoding("GBK").GetString(buf, 0, read); }
                            catch { text = Encoding.Default.GetString(buf, 0, read); }
                        }

                        return new TextInfo
                        {
                            Text = text,
                            Truncated = fi.Length > MaxTextBytes
                        };
                    }
                }
                catch { return null; }
            }

            // ---- PDF（无内嵌渲染）----
            if (ext == ".pdf") return new PdfInfo();

            // ---- 其他 ----
            return null;
        }

        /// <summary>界面线程把内容画到 body 上。</summary>
        void AppendContent(object content, FileScanner.Entry e, Window owner)
        {
            try
            {
                // ---- 图片 ----
                BitmapImage img = content as BitmapImage;
                if (img != null)
                {
                    Image im = new Image();
                    im.Source = img;
                    im.Stretch = Stretch.Uniform;
                    im.MaxHeight = 300;
                    im.Margin = new Thickness(0, 0, 0, Ui.S.Sm);
                    body.Children.Add(im);

                    AddInfoLine("尺寸", img.PixelWidth + " × " + img.PixelHeight);
                    AddInfoLine("大小", FileScanner.FormatSize(e.Size));
                    AddInfoLine("修改时间", e.Modified == DateTime.MinValue ? "—" : e.Modified.ToString("yyyy-MM-dd HH:mm:ss"));
                    return;
                }

                // ---- 文本 ----
                TextInfo ti = content as TextInfo;
                if (ti != null)
                {
                    ScrollViewer sv = new ScrollViewer();
                    sv.MaxHeight = 300;
                    sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;

                    TextBox tb = new TextBox();
                    tb.Text = ti.Text ?? "";
                    tb.IsReadOnly = true;
                    tb.BorderThickness = new Thickness(0);
                    tb.Background = Brushes.Transparent;
                    tb.Foreground = Ui.Sub;
                    tb.FontFamily = new FontFamily("Consolas, 微软雅黑");
                    tb.FontSize = 11;
                    tb.TextWrapping = TextWrapping.NoWrap;
                    tb.Padding = new Thickness(0);
                    sv.Content = tb;

                    body.Children.Add(sv);
                    if (ti.Truncated)
                        AddInfoLine("提示", "仅预览前 " + (MaxTextBytes / 1024) + " KB");
                    AddInfoLine("大小", FileScanner.FormatSize(e.Size));
                    AddInfoLine("修改时间", e.Modified == DateTime.MinValue ? "—" : e.Modified.ToString("yyyy-MM-dd HH:mm:ss"));
                    return;
                }

                // ---- PDF ----
                PdfInfo pdf = content as PdfInfo;
                if (pdf != null)
                {
                    AddInfoLine("大小", FileScanner.FormatSize(e.Size));
                    AddInfoLine("修改时间", e.Modified == DateTime.MinValue ? "—" : e.Modified.ToString("yyyy-MM-dd HH:mm:ss"));
                    AddNote("PDF 需要外部程序查看");

                    Button open = new Button();
                    open.Content = "用默认程序打开";
                    open.Height = 28;
                    open.Margin = new Thickness(0, Ui.S.Sm, 0, 0);
                    open.Cursor = System.Windows.Input.Cursors.Hand;
                    open.Click += delegate
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(
                                new System.Diagnostics.ProcessStartInfo(e.Path) { UseShellExecute = true });
                        }
                        catch (Exception ex) { Ui.LogError(ex, "打开 PDF"); }
                    };
                    body.Children.Add(open);
                    return;
                }

                // ---- 文件夹 ----
                FolderInfo fi2 = content as FolderInfo;
                if (fi2 != null)
                {
                    AddInfoLine("类型", "文件夹");
                    AddInfoLine("包含", fi2.Count >= 0 ? (fi2.Count + " 项") : "无法读取");
                    AddInfoLine("修改时间", e.Modified == DateTime.MinValue ? "—" : e.Modified.ToString("yyyy-MM-dd HH:mm:ss"));
                    AddInfoLine("位置", e.Path);
                    return;
                }

                // ---- 其他类型 ----
                AddInfoLine("类型", string.IsNullOrEmpty(e.Ext) ? "无扩展名" : e.Ext);
                AddInfoLine("大小", FileScanner.FormatSize(e.Size));
                AddInfoLine("修改时间", e.Modified == DateTime.MinValue ? "—" : e.Modified.ToString("yyyy-MM-dd HH:mm:ss"));
                AddInfoLine("位置", e.Path);
                AddNote("此类型暂不支持预览");
            }
            catch (Exception ex) { Ui.LogError(ex, "绘制预览内容"); }
        }

        void AddInfoLine(string label, string value)
        {
            try
            {
                StackPanel row = new StackPanel();
                row.Orientation = Orientation.Horizontal;
                row.Margin = new Thickness(0, 0, 0, 3);

                TextBlock l = Ui.T(label + "：", 10.5, Ui.Sub);
                l.MinWidth = 56;
                row.Children.Add(l);

                TextBlock v = Ui.T(value ?? "", 10.5, Ui.Fg);
                v.TextTrimming = TextTrimming.CharacterEllipsis;
                v.MaxWidth = 240;
                v.ToolTip = value;
                row.Children.Add(v);

                body.Children.Add(row);
            }
            catch { }
        }

        void AddNote(string text)
        {
            try
            {
                TextBlock t = Ui.T(text, 10.5, Ui.Sub);
                t.Margin = new Thickness(0, Ui.S.Xs, 0, 0);
                t.TextWrapping = TextWrapping.Wrap;
                body.Children.Add(t);
            }
            catch { }
        }

        // ========== 扩展名判定 ==========

        static bool IsImageExt(string ext)
        {
            switch (ext)
            {
                case ".jpg": case ".jpeg": case ".png": case ".gif":
                case ".bmp": case ".webp": case ".ico": case ".tif": case ".tiff":
                    return true;
                default: return false;
            }
        }

        static bool IsTextExt(string ext)
        {
            switch (ext)
            {
                case ".txt": case ".md": case ".log": case ".json": case ".xml":
                case ".csv": case ".ini": case ".cfg": case ".conf": case ".yml":
                case ".yaml": case ".html": case ".htm": case ".css": case ".js":
                case ".ts": case ".cs": case ".c": case ".h": case ".cpp": case ".hpp":
                case ".py": case ".java": case ".go": case ".rs": case ".rb": case ".php":
                case ".sh": case ".bat": case ".ps1": case ".sql": case ".toml":
                    return true;
                default: return false;
            }
        }

        // ========== 内容载体 ==========

        class TextInfo { public string Text; public bool Truncated; }
        class FolderInfo { public int Count; }
        class PdfInfo { }
    }
}
