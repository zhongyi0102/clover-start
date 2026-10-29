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

        // ---------- 商店应用取图标用的 Shell 接口 ----------
        //
        // SHGetFileInfo 对 shell:AppsFolder\... 取不到图标（返回 0 句柄），
        // 必须走 IShellItemImageFactory —— 这是 Windows 给虚拟命名空间项
        // 提供缩略图/图标的官方接口。
        //
        // 为什么不用手写 IShellFolder + STRRET：
        //   实测那套接口的 Explicit 布局极易出错，抛出的异常连 ToString 都失败。
        //   IShellItemImageFactory 只需要一个方法，稳定得多。

        [System.Runtime.InteropServices.ComImport,
         System.Runtime.InteropServices.Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
         System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [System.Runtime.InteropServices.PreserveSig]
            int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct SIZE
        {
            public int cx;
            public int cy;
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] out object ppv);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);

        /// <summary>SIIGBF_ICONONLY：只取图标，不要缩略图。</summary>
        const int SIIGBF_ICONONLY = 0x8;

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

        /// <summary>
        /// key = 版本号 + 路径前缀 + 修改时间 + 大小；文件不存在时返回 null（不缓存）。
        ///
        /// **版本号在键的最前面**：改了取图标方式后旧缓存文件名全部对不上，
        /// 自然失效（不会出现"代码对了但界面还是旧箭头图标"）。
        /// </summary>
        static string BuildKey(string path)
        {
            try
            {
                // ---- 商店应用：路径不是文件，用 AUMID 当键的一部分 ----
                //
                // shell:AppsFolder\... 不对应任何文件，File.Exists 永远 false。
                // 用 AppUserModelID 作稳定标识（同一应用不会因重装变 key）。
                if (AppPaths.IsShellPath(path))
                {
                    string aumid = AppPaths.ExtractAumid(path);
                    if (string.IsNullOrEmpty(aumid)) return null;
                    return ExtractVersion + "-shell-" + Hash(aumid.ToLowerInvariant());
                }

                if (!File.Exists(path)) return null;
                FileInfo fi = new FileInfo(path);
                return ExtractVersion + "-" + PathKeyPrefix(path)
                     + fi.LastWriteTimeUtc.Ticks.ToString("x") + "_" + fi.Length.ToString("x");
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

        /// <summary>
        /// 图标提取算法的版本号。
        ///
        /// **改了取图标的方式就必须递增这个值** —— 它会进缓存键，
        /// 让所有旧缓存（比如带快捷方式箭头的）自动失效。
        ///
        /// 历史：
        ///   v1 → 直接对路径调 SHGetFileInfo（.lnk 会带上系统合成的箭头）
        ///   v2 → .lnk 先解析目标 exe，取目标图标（去掉箭头）
        /// </summary>
        const string ExtractVersion = "v2";

        static ImageSource Extract(string path)
        {
            try
            {
                // ---- 商店应用：走 IShellItemImageFactory ----
                //
                // shell:AppsFolder\... 是 Shell 命名空间路径，
                // SHGetFileInfo 对它返回 0 句柄（取不到图标），
                // 必须用 IShellItemImageFactory.GetImage。
                if (AppPaths.IsShellPath(path))
                {
                    ImageSource shellIcon = ExtractShellIcon(path);
                    if (shellIcon != null) return shellIcon;
                    // 取不到就返回 null，由界面显示首字占位（不抛异常）
                    return null;
                }

                // ---- 快捷方式去箭头 ----
                //
                // 箭头不是我们画的，是 Windows 的图标合成机制：
                // SHGetFileInfo 对 .lnk 返回的是"目标图标 + 左下角箭头覆盖层"。
                // 实测 Excel.lnk 与其目标 EXCEL.EXE 的图标在左下象限有 164/256
                // 像素不同，差异全部集中在箭头位置。
                //
                // 解决办法：对 .lnk 先解析出目标路径，取**目标 exe 的图标**。
                // 目标取不到时回退到 lnk 自身（宁可带箭头，也不要没图标）。
                string iconSource = path;
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string target = ShortcutResolver.Resolve(path);
                    if (!string.IsNullOrEmpty(target) && File.Exists(target))
                        iconSource = target;
                }

                SHFILEINFO fi = new SHFILEINFO();
                SHGetFileInfo(iconSource, 0, ref fi, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_LARGEICON);

                // 目标是 exe 但取不到图标（比如 lnk 指向文档）→ 回退到 lnk 自身
                if (fi.hIcon == IntPtr.Zero && !string.Equals(iconSource, path, StringComparison.OrdinalIgnoreCase))
                {
                    SHGetFileInfo(path, 0, ref fi, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_LARGEICON);
                }

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

        /// <summary>
        /// 用 IShellItemImageFactory 取商店应用图标。
        /// 取不到返回 null（不抛异常）。
        /// </summary>
        static ImageSource ExtractShellIcon(string shellPath)
        {
            IntPtr hbmp = IntPtr.Zero;
            try
            {
                Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                object obj;
                SHCreateItemFromParsingName(shellPath, IntPtr.Zero, ref iid, out obj);
                if (obj == null) return null;

                IShellItemImageFactory factory = obj as IShellItemImageFactory;
                if (factory == null) return null;

                SIZE size = new SIZE();
                size.cx = 32;
                size.cy = 32;

                int hr = factory.GetImage(size, SIIGBF_ICONONLY, out hbmp);
                if (hr != 0 || hbmp == IntPtr.Zero) return null;

                // HBITMAP → WPF ImageSource
                BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(
                    hbmp, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(32, 32));
                src.Freeze();
                return src;
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("提取商店应用图标失败: " + shellPath + " -> " + ex.Message);
                return null;
            }
            finally
            {
                if (hbmp != IntPtr.Zero)
                {
                    try { DeleteObject(hbmp); } catch { }
                }
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
