using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// 自定义背景图仓库：导入、校验、缩放、读取、清除。
    ///
    /// ── 存哪里 ──
    ///
    /// `config\background\custom.<ext>`，**固定文件名**，不记路径配置键。
    ///
    /// 为什么不存 `glassBackgroundPath`：
    ///   · 只可能有一张自定义图（三选一互斥），存路径等于把"哪个文件"
    ///     这个状态分散到两处（`GlassMode="custom"` + 路径），容易不一致
    ///   · 用户把原图删了/移走后，路径失效 → 还得再处理一次"文件不存在"
    ///   · 固定名 + 复制过来，原图怎么折腾都不影响
    ///
    /// 扩展名保留（`custom.png` / `custom.jpg`）只是为了**读的时候能选对解码器**，
    /// 程序内部一律用 `Find()` 扫描，不依赖具体扩展名。
    ///
    /// ── 导入流程 ──
    ///
    ///   1. 先解码验证（真正 `BitmapImage.EndInit()`，不是只看扩展名）
    ///   2. 长边 > 3840 → 等比缩到 3840（**不放大**小图）
    ///   3. 写临时文件 → 成功后原子替换旧文件
    ///   4. 失败时**不改变任何现有设置**，旧图原样保留
    ///
    /// ── 格式支持 ──
    ///
    /// jpg / jpeg / png / bmp / gif / webp / tif / tiff
    ///
    /// **webp 已实测 WPF 可解码**（Windows 10/11 的 WIC 自带解码器），
    /// 所以保留。gif 只取第一帧（`BitmapImage` 默认行为）。
    /// </summary>
    public static class BackgroundStore
    {
        /// <summary>支持导入的扩展名（小写，含点）。</summary>
        public static readonly string[] SupportedExts = new string[]
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff",
        };

        /// <summary>长边上限。超过则等比缩小，小图不放大。</summary>
        public const int MaxLongEdge = 3840;

        /// <summary>自定义图的固定文件名前缀。</summary>
        const string BaseName = "custom";

        // ========== 路径 ==========

        /// <summary>自定义图所在目录：`config\background`。</summary>
        public static string DirOf(string baseDir)
        {
            try { return Path.Combine(baseDir, "config", "background"); }
            catch { return ""; }
        }

        /// <summary>
        /// 找到当前的自定义图。**不依赖具体扩展名** —— 扫目录找 `custom.*`。
        /// 找不到返回 null。
        /// </summary>
        public static string Find(string baseDir)
        {
            try
            {
                string dir = DirOf(baseDir);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

                foreach (string ext in SupportedExts)
                {
                    string p = Path.Combine(dir, BaseName + ext);
                    if (File.Exists(p)) return p;
                }

                // 兜底：扫一遍（用户可能放了 custom.PNG 之类的大小写变体）
                // 只删 custom.*（正在导入的临时文件用 .importing- 前缀，不会被误删）
                foreach (string f in Directory.GetFiles(dir, BaseName + ".*"))
                {
                    string e = (Path.GetExtension(f) ?? "").ToLowerInvariant();
                    foreach (string ext in SupportedExts)
                        if (e == ext) return f;
                }

                return null;
            }
            catch { return null; }
        }

        /// <summary>自定义图是否存在。</summary>
        public static bool Exists(string baseDir)
        {
            return !string.IsNullOrEmpty(Find(baseDir));
        }

        // ========== 校验 ==========

        /// <summary>扩展名是否受支持。</summary>
        public static bool IsSupportedExt(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                string e = (Path.GetExtension(path) ?? "").ToLowerInvariant();
                foreach (string s in SupportedExts)
                    if (e == s) return true;
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// 真正解码一次，验证文件是不是有效图片。
        ///
        /// **不看扩展名** —— 把 .txt 改名成 .png 也能被识破。
        /// </summary>
        /// <returns>有效返回 true，并给出像素尺寸</returns>
        public static bool TryDecode(string path, out int width, out int height, out string error)
        {
            width = 0;
            height = 0;
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path)) { error = "路径为空"; return false; }
                if (!File.Exists(path)) { error = "文件不存在"; return false; }

                BitmapImage bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(path, UriKind.Absolute);
                bi.EndInit();          // ← 这里才真正解码，失败会抛
                bi.Freeze();

                width = bi.PixelWidth;
                height = bi.PixelHeight;

                if (width <= 0 || height <= 0) { error = "图片尺寸无效"; return false; }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ========== 导入 ==========

        /// <summary>
        /// 导入一张图片作为自定义背景。
        ///
        /// **失败时不改动任何现有文件** —— 旧图原样保留。
        /// </summary>
        /// <param name="baseDir">程序根目录</param>
        /// <param name="srcPath">用户选的原图</param>
        /// <param name="error">失败原因（成功时为 null）</param>
        /// <returns>成功返回保存后的路径</returns>
        public static string Import(string baseDir, string srcPath, out string error)
        {
            error = null;
            try
            {
                // ---- 1. 扩展名检查 ----
                if (!IsSupportedExt(srcPath))
                {
                    error = "不支持的格式（支持 jpg/png/bmp/gif/webp/tif）";
                    return null;
                }

                // ---- 2. 真正解码验证 ----
                int w, h;
                string derr;
                if (!TryDecode(srcPath, out w, out h, out derr))
                {
                    error = "无法读取该图片" + (string.IsNullOrEmpty(derr) ? "" : "（" + derr + "）");
                    return null;
                }

                // ---- 3. 目标路径 ----
                string dir = DirOf(baseDir);
                if (string.IsNullOrEmpty(dir)) { error = "目录路径无效"; return null; }
                Directory.CreateDirectory(dir);

                string ext = (Path.GetExtension(srcPath) ?? ".png").ToLowerInvariant();
                string finalPath = Path.Combine(dir, BaseName + ext);

                // ---- 4. 写临时文件（同目录，保证原子替换）----
                // ★ 临时文件名**不能**匹配 custom.* —— RemoveAll 会按 custom.* 删，
                //   那样会把正在导入的临时文件一起删掉（曾踩过）
                string tmpPath = Path.Combine(dir, ".importing-" + Guid.NewGuid().ToString("N") + ext);

                bool needScale = (Math.Max(w, h) > MaxLongEdge);

                if (needScale) SaveScaled(srcPath, tmpPath, w, h);
                else File.Copy(srcPath, tmpPath, true);

                // ---- 5. 校验临时文件真的可读 ----
                int w2, h2;
                string e2;
                if (!TryDecode(tmpPath, out w2, out h2, out e2))
                {
                    try { File.Delete(tmpPath); } catch { }
                    error = "保存后校验失败";
                    return null;
                }

                // ---- 6. 原子替换：先删旧的其它扩展名，再改名 ----
                RemoveAll(baseDir);   // 清掉 custom.* （含旧扩展名）
                File.Move(tmpPath, finalPath);

                Ui.Log("自定义背景已导入: " + srcPath + " → " + finalPath
                    + "  " + w + "x" + h + (needScale ? (" → " + w2 + "x" + h2) : ""));

                return finalPath;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { Ui.LogError(ex, "导入背景图"); } catch { }
                return null;
            }
        }

        /// <summary>按长边上限等比缩放后保存（不放大）。</summary>
        static void SaveScaled(string srcPath, string dstPath, int w, int h)
        {
            double scale = (double)MaxLongEdge / Math.Max(w, h);
            int nw = Math.Max(1, (int)Math.Round(w * scale));
            int nh = Math.Max(1, (int)Math.Round(h * scale));

            BitmapImage bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(srcPath, UriKind.Absolute);

            // 用 DecodePixelWidth 让解码器直接降采样，避免先解出原尺寸大图
            if (w >= h) bi.DecodePixelWidth = nw;
            else bi.DecodePixelHeight = nh;

            bi.EndInit();
            bi.Freeze();

            BitmapEncoder enc = ChooseEncoder(dstPath);
            enc.Frames.Add(BitmapFrame.Create(bi));
            using (FileStream fs = new FileStream(dstPath, FileMode.Create, FileAccess.Write))
            {
                enc.Save(fs);
            }
        }

        /// <summary>按扩展名选编码器。webp 没有内置编码器 → 存成 png。</summary>
        static BitmapEncoder ChooseEncoder(string dstPath)
        {
            string e = (Path.GetExtension(dstPath) ?? "").ToLowerInvariant();
            switch (e)
            {
                case ".jpg":
                case ".jpeg":
                    return new JpegBitmapEncoder { QualityLevel = 92 };
                case ".bmp":
                    return new BmpBitmapEncoder();
                case ".gif":
                    return new GifBitmapEncoder();
                case ".tif":
                case ".tiff":
                    return new TiffBitmapEncoder();
                default:
                    // .png 以及 webp（WPF 只能解不能编）
                    return new PngBitmapEncoder();
            }
        }

        // ========== 清除 ==========

        /// <summary>删除所有自定义图。返回是否删掉了东西。</summary>
        public static bool RemoveAll(string baseDir)
        {
            bool any = false;
            try
            {
                string dir = DirOf(baseDir);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

                // 只删 custom.*（正在导入的临时文件用 .importing- 前缀，不会被误删）
                foreach (string f in Directory.GetFiles(dir, BaseName + ".*"))
                {
                    try { File.Delete(f); any = true; }
                    catch (Exception ex)
                    {
                        try { Log.Warn("删除背景图失败: " + f + " -> " + ex.Message); } catch { }
                    }
                }
            }
            catch { }
            return any;
        }

        // ========== 视频 / 预览图（Wallpaper Engine 导入）==========

        /// <summary>背景视频的固定文件名。</summary>
        const string VideoBaseName = "custom-video";

        /// <summary>找当前的背景视频（custom-video.*）。找不到返回 null。</summary>
        public static string FindVideo(string baseDir)
        {
            try
            {
                string dir = DirOf(baseDir);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

                foreach (string f in Directory.GetFiles(dir, VideoBaseName + ".*"))
                {
                    string e = (Path.GetExtension(f) ?? "").ToLowerInvariant();
                    if (e == ".mp4" || e == ".webm" || e == ".avi" || e == ".mkv") return f;
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>是否有背景视频。</summary>
        public static bool HasVideo(string baseDir)
        {
            return !string.IsNullOrEmpty(FindVideo(baseDir));
        }

        /// <summary>
        /// 复制视频到 `config\background\custom-video.<ext>`。
        ///
        /// **只保留一份**：先写 `.importing-<guid>` 临时文件，成功后删旧的再改名。
        /// 失败时旧视频原样保留。
        ///
        /// 视频体积大（几十到几百 MB），调用方应在**后台线程**执行并显示进度。
        /// </summary>
        /// <param name="progress">进度回调（0.0 ~ 1.0），可为 null</param>
        /// <param name="cancel">取消令牌，可为 null</param>
        public static string ImportVideo(string baseDir, string srcPath,
            Action<double> progress, Func<bool> cancel, out string error)
        {
            error = null;
            string tmpPath = null;
            try
            {
                if (string.IsNullOrWhiteSpace(srcPath) || !File.Exists(srcPath))
                {
                    error = "源文件不存在";
                    return null;
                }

                string ext = (Path.GetExtension(srcPath) ?? ".mp4").ToLowerInvariant();
                if (ext != ".mp4" && ext != ".webm" && ext != ".avi" && ext != ".mkv")
                {
                    error = "不支持的视频格式";
                    return null;
                }

                string dir = DirOf(baseDir);
                if (string.IsNullOrEmpty(dir)) { error = "目录路径无效"; return null; }
                Directory.CreateDirectory(dir);

                // 临时文件不能匹配 custom* / custom-video*，避免被 RemoveAll 误删
                tmpPath = Path.Combine(dir, ".importing-" + Guid.NewGuid().ToString("N") + ext);

                long total = new FileInfo(srcPath).Length;
                long copied = 0;

                using (FileStream src = new FileStream(srcPath, FileMode.Open,
                    FileAccess.Read, FileShare.Read))
                using (FileStream dst = new FileStream(tmpPath, FileMode.Create,
                    FileAccess.Write, FileShare.None))
                {
                    byte[] buf = new byte[1024 * 512];   // 512 KB
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (cancel != null && cancel()) { error = "已取消"; break; }

                        dst.Write(buf, 0, n);
                        copied += n;

                        if (progress != null && total > 0)
                        {
                            try { progress((double)copied / total); } catch { }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(error))
                {
                    try { File.Delete(tmpPath); } catch { }
                    return null;
                }

                // ---- 原子替换：删旧视频，改名 ----
                string old = FindVideo(baseDir);
                if (!string.IsNullOrEmpty(old))
                {
                    try { File.Delete(old); } catch { }
                }

                string finalPath = Path.Combine(dir, VideoBaseName + ext);
                File.Move(tmpPath, finalPath);
                tmpPath = null;

                Ui.Log("背景视频已导入: " + srcPath + " → " + finalPath
                    + "  " + (total / 1024 / 1024) + " MB");

                return finalPath;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { Ui.LogError(ex, "导入背景视频"); } catch { }
                return null;
            }
            finally
            {
                // 无论成败，临时文件都不留
                if (!string.IsNullOrEmpty(tmpPath))
                {
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                }
            }
        }

        /// <summary>删除背景视频。返回是否删掉了东西。</summary>
        public static bool RemoveVideo(string baseDir)
        {
            try
            {
                string f = FindVideo(baseDir);
                if (string.IsNullOrEmpty(f)) return false;
                File.Delete(f);
                return true;
            }
            catch { return false; }
        }

        // ========== 来源记录 ==========

        /// <summary>
        /// 读来源记录（导入的壁纸来自哪个 WE 壁纸）。
        ///
        /// 存 `config\background\source.json`：
        /// `{"id":"123456","title":"xxx","type":"video"}`
        /// </summary>
        public static Dictionary<string, string> ReadSource(string baseDir)
        {
            try
            {
                string p = Path.Combine(DirOf(baseDir), "source.json");
                if (!File.Exists(p)) return null;

                string json = JsonIO.Read(p);
                if (string.IsNullOrEmpty(json)) return null;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return null;

                Dictionary<string, string> r = new Dictionary<string, string>();
                foreach (KeyValuePair<string, object> kv in d)
                    r[kv.Key] = Convert.ToString(kv.Value) ?? "";
                return r;
            }
            catch { return null; }
        }

        /// <summary>写来源记录。</summary>
        public static void WriteSource(string baseDir, string id, string title, string type)
        {
            try
            {
                string dir = DirOf(baseDir);
                if (string.IsNullOrEmpty(dir)) return;
                Directory.CreateDirectory(dir);

                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = id ?? "";
                d["title"] = title ?? "";
                d["type"] = type ?? "";

                JavaScriptSerializer ser = new JavaScriptSerializer();
                JsonIO.WriteAtomic(Path.Combine(dir, "source.json"), ser.Serialize(d));
            }
            catch (Exception ex)
            {
                try { Log.Warn("写背景来源记录失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>清来源记录。</summary>
        public static void ClearSource(string baseDir)
        {
            try
            {
                string p = Path.Combine(DirOf(baseDir), "source.json");
                if (File.Exists(p)) File.Delete(p);
            }
            catch { }
        }

        // ========== 读取（供 GlassBackdrop 用）==========

        /// <summary>
        /// 读自定义图为 BitmapImage。失败返回 null（调用方回退内置固定图）。
        ///
        /// 用 `DecodePixelWidth` 限制解码尺寸：4K 图全尺寸解码要几十 MB 显存，
        /// 而背景最终只显示在面板那么大一块上，没必要。
        /// </summary>
        public static BitmapImage Load(string baseDir, int maxDecodeWidth)
        {
            try
            {
                string path = Find(baseDir);
                if (string.IsNullOrEmpty(path)) return null;

                BitmapImage bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(path, UriKind.Absolute);
                if (maxDecodeWidth > 0) bi.DecodePixelWidth = maxDecodeWidth;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch (Exception ex)
            {
                try { Log.Warn("读取自定义背景图失败，回退内置图: " + ex.Message); } catch { }
                return null;
            }
        }
    }
}
