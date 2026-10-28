using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>一张壁纸。</summary>
    class WallpaperItem
    {
        public string Path;
        public string Name;
        public long Size;
        public DateTime Modified;
    }

    /// <summary>
    /// 壁纸库：扫描一个文件夹里的图片，供面板显示缩略图。
    ///
    /// 支持系统能设成壁纸的格式（.jpg / .png / .bmp）——
    /// 这三个是 SPI_SETDESKWALLPAPER 认的；.webp / .gif 系统不认，
    /// 扫进来只会让用户点了没反应，所以不列入。
    ///
    /// 为什么不缓存缩略图到磁盘：
    /// 壁纸文件夹通常几十到几百张，WPF 的 BitmapImage 带 DecodePixelWidth
    /// 解码很快（每张约 3-8 ms），而且面板只在打开时读当前页。
    /// 做磁盘缓存反而要处理失效、清理、路径变更，不划算。
    /// </summary>
    static class WallpaperLibrary
    {
        /// <summary>系统能设成桌面壁纸的扩展名。</summary>
        static readonly HashSet<string> AllowedExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp"
        };

        /// <summary>扫描上限：太多的话面板加载会慢。</summary>
        const int MaxItems = 500;

        /// <summary>
        /// 扫描文件夹里的壁纸（按修改时间倒序，最近改过的排前面）。
        /// 目录不存在或无权限时返回空列表，不抛异常。
        /// </summary>
        public static List<WallpaperItem> Scan(string folder)
        {
            List<WallpaperItem> list = new List<WallpaperItem>();
            try
            {
                if (string.IsNullOrWhiteSpace(folder)) return list;
                if (!Directory.Exists(folder)) return list;

                foreach (string f in Directory.EnumerateFiles(folder))
                {
                    if (list.Count >= MaxItems) break;
                    try
                    {
                        string ext = Path.GetExtension(f);
                        if (!AllowedExt.Contains(ext)) continue;

                        FileInfo fi = new FileInfo(f);
                        WallpaperItem it = new WallpaperItem();
                        it.Path = f;
                        it.Name = Path.GetFileNameWithoutExtension(f);
                        it.Size = fi.Length;
                        it.Modified = fi.LastWriteTime;
                        list.Add(it);
                    }
                    catch { }
                }

                // 最近修改的排前面（用户刚放进来的壁纸通常就是想用的）
                list.Sort(delegate(WallpaperItem a, WallpaperItem b)
                {
                    return b.Modified.CompareTo(a.Modified);
                });
            }
            catch (Exception ex)
            {
                Log.Warn("扫描壁纸目录失败: " + ex.Message);
            }
            return list;
        }

        /// <summary>判断文件是否是支持的壁纸格式。</summary>
        public static bool IsSupported(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                return AllowedExt.Contains(Path.GetExtension(path));
            }
            catch { return false; }
        }

        /// <summary>文件大小的人类可读形式。</summary>
        public static string FormatSize(long bytes)
        {
            try
            {
                if (bytes <= 0) return "";
                if (bytes < 1024) return bytes + " B";
                if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
                return (bytes / 1024.0 / 1024).ToString("0.#") + " MB";
            }
            catch { return ""; }
        }
    }
}
