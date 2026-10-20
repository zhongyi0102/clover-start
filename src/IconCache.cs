using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clover
{
    /// <summary>
    /// 应用图标缓存：
    /// 1) 进程内字典（同一路径只取一次）；
    /// 2) 磁盘缓存 config\iconcache\&lt;hash&gt;.png，key = 路径 + 文件修改时间 + 大小，
    ///    所以程序升级/换图标后会自动失效重取；
    /// 3) 启动时清理：太久没用（90 天）或数量过多（&gt;800）时删掉最旧的一批，
    ///    避免卸载过的软件把缓存目录撑大。
    /// 取不到的图标返回 null，由调用方显示首字占位，不抛异常。
    /// </summary>
    static class IconCache
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);

        const uint SHGFI_ICON = 0x0100;
        const uint SHGFI_LARGEICON = 0x0000;

        const int MaxCacheFiles = 800;
        const int MaxAgeDays = 90;

        static readonly object gate = new object();
        static readonly Dictionary<string, ImageSource> memory = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        static string cacheDir;
        static bool cleaned;

        /// <summary>初始化缓存目录（应在启动时调用一次）。</summary>
        public static void Init(string baseDir)
        {
            try
            {
                cacheDir = Path.Combine(baseDir, "config", "iconcache");
                Directory.CreateDirectory(cacheDir);
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("图标缓存目录初始化失败: " + ex.Message);
                cacheDir = null;
            }
        }

        /// <summary>取应用图标；取不到返回 null。</summary>
        public static ImageSource Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string key = BuildKey(path);
            if (key == null) return null;

            lock (gate)
            {
                ImageSource hit;
                if (memory.TryGetValue(key, out hit)) return hit;
            }

            ImageSource result = LoadFromDisk(key);
            if (result == null)
            {
                result = Extract(path);
                if (result != null) SaveToDisk(key, result);
            }

            lock (gate)
            {
                memory[key] = result;
                // 同一路径的旧版本 key 顺手清掉，避免内存里堆积
                List<string> stale = null;
                foreach (string k in memory.Keys)
                {
                    if (k == key) continue;
                    if (k.StartsWith(PathKeyPrefix(path), StringComparison.OrdinalIgnoreCase))
                    {
                        if (stale == null) stale = new List<string>();
                        stale.Add(k);
                    }
                }
                if (stale != null) foreach (string k in stale) memory.Remove(k);
            }
            return result;
        }

        /// <summary>key = 路径前缀 + 修改时间 + 大小；文件不存在时返回 null（不缓存）。</summary>
        static string BuildKey(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                FileInfo fi = new FileInfo(path);
                return PathKeyPrefix(path) + fi.LastWriteTimeUtc.Ticks.ToString("x") + "_" + fi.Length.ToString("x");
            }
            catch
            {
                return null;
            }
        }

        static string PathKeyPrefix(string path)
        {
            return Hash(path.ToLowerInvariant()) + "-";
        }

        static string Hash(string text)
        {
            try
            {
                using (SHA1 sha = SHA1.Create())
                {
                    byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                    StringBuilder sb = new StringBuilder(bytes.Length * 2);
                    foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                // 极端情况下退化为简单哈希，仍可用
                return Math.Abs(text.GetHashCode()).ToString("x8");
            }
        }

        static string KeyToFile(string key)
        {
            if (string.IsNullOrEmpty(cacheDir)) return null;
            return Path.Combine(cacheDir, key + ".png");
        }

        static ImageSource LoadFromDisk(string key)
        {
            string file = KeyToFile(key);
            if (file == null) return null;
            try
            {
                if (!File.Exists(file)) return null;
                BitmapImage b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.UriSource = new Uri(file, UriKind.Absolute);
                b.EndInit();
                b.Freeze();
                try { File.SetLastAccessTimeUtc(file, DateTime.UtcNow); } catch { }
                return b;
            }
            catch
            {
                return null;
            }
        }

        static void SaveToDisk(string key, ImageSource image)
        {
            string file = KeyToFile(key);
            if (file == null) return;
            try
            {
                BitmapSource bs = image as BitmapSource;
                if (bs == null) return;
                PngBitmapEncoder enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bs));

                string tmp = file + ".tmp";
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    enc.Save(fs);
                }
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);

                CleanupOnce();
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("写图标缓存失败: " + ex.Message);
            }
        }

        static ImageSource Extract(string path)
        {
            try
            {
                SHFILEINFO fi = new SHFILEINFO();
                SHGetFileInfo(path, 0, ref fi, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_LARGEICON);
                if (fi.hIcon == IntPtr.Zero) return null;
                try
                {
                    ImageSource src = Imaging.CreateBitmapSourceFromHIcon(fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally
                {
                    DestroyIcon(fi.hIcon);
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("提取图标失败: " + path + " -> " + ex.Message);
                return null;
            }
        }

        /// <summary>每个进程只做一次：删掉过期条目，数量超限时删最旧的一半。</summary>
        static void CleanupOnce()
        {
            if (cleaned) return;
            cleaned = true;
            try
            {
                if (string.IsNullOrEmpty(cacheDir) || !Directory.Exists(cacheDir)) return;
                FileInfo[] files = new DirectoryInfo(cacheDir).GetFiles("*.png");
                if (files.Length == 0) return;

                DateTime cutoff = DateTime.UtcNow.AddDays(-MaxAgeDays);
                List<FileInfo> live = new List<FileInfo>();
                int removed = 0;
                foreach (FileInfo f in files)
                {
                    try
                    {
                        DateTime stamp = f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc;
                        if (stamp < cutoff)
                        {
                            f.Delete();
                            removed++;
                        }
                        else
                        {
                            live.Add(f);
                        }
                    }
                    catch { }
                }

                if (live.Count > MaxCacheFiles)
                {
                    live.Sort(delegate(FileInfo a, FileInfo b) { return a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc); });
                    int drop = live.Count - MaxCacheFiles;
                    for (int i = 0; i < drop; i++)
                    {
                        try { live[i].Delete(); removed++; } catch { }
                    }
                }

                if (removed > 0) Ui.Log("图标缓存清理: 删除 " + removed + " 个旧条目");
            }
            catch { }
        }
    }
}
