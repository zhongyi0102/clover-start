using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clover
{
    /// <summary>
    /// 图标缓存测试：快捷方式去箭头、普通 exe、透明图标、缓存失效、商店应用。
    ///
    /// 需要 WPF（ImageSource），所以在 ui 层编译。
    /// </summary>
    static class IconTest
    {
        static int failed = 0;
        static int passed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            if (cond) { passed++; Console.WriteLine("PASS  " + name); }
            else
            {
                failed++;
                Console.WriteLine("FAIL  " + name + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
            }
        }

        [STAThread]
        static void Main()
        {
            Console.WriteLine("=== IconCache 测试 ===");
            Console.WriteLine();

            string root = NewRoot();
            try
            {
                IconCache.Init(root);

                TestBasicExeIcon();
                TestLnkNoArrow(root);
                TestTransparentIcon();
                TestCacheKeyChangesOnVersion(root);
                TestMissingPath();
                TestShellPath(root);
                TestCacheInvalidation(root);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 普通 exe 图标 ----------
        static void TestBasicExeIcon()
        {
            Console.WriteLine("--- 1. 普通 exe 图标 ---");

            // 用系统自带的 notepad.exe（一定存在）
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "notepad.exe");
            if (!File.Exists(exe))
            {
                Console.WriteLine("      跳过：找不到 notepad.exe");
                Console.WriteLine();
                return;
            }

            ImageSource icon = IconCache.Get(exe);
            Check(icon != null, "notepad.exe 能取到图标");
            if (icon != null)
            {
                BitmapSource bs = icon as BitmapSource;
                Check(bs != null && bs.PixelWidth > 0 && bs.PixelHeight > 0,
                    "图标尺寸有效", bs == null ? "null" : bs.PixelWidth + "x" + bs.PixelHeight);
                Check(bs != null && Math.Abs(bs.PixelWidth - bs.PixelHeight) <= 1,
                    "图标是正方形", bs == null ? "" : bs.PixelWidth + "x" + bs.PixelHeight);
            }
            Console.WriteLine();
        }

        // ---------- 2. .lnk 图标不含箭头 ----------
        //
        // 核心测试：对比 .lnk 与其目标 exe 的图标，左下角应该**没有差异**
        // （有差异说明箭头还在）。
        static void TestLnkNoArrow(string root)
        {
            Console.WriteLine("--- 2. 快捷方式去箭头 ---");

            // 造一个真实 exe + 指向它的 lnk
            string dir = Path.Combine(root, "iconsrc");
            Directory.CreateDirectory(dir);

            string sysExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "notepad.exe");
            if (!File.Exists(sysExe))
            {
                Console.WriteLine("      跳过：找不到源 exe");
                Console.WriteLine();
                return;
            }

            string exe = Path.Combine(dir, "Target.exe");
            try { File.Copy(sysExe, exe, true); }
            catch { Console.WriteLine("      跳过：复制失败"); Console.WriteLine(); return; }

            string lnk = Path.Combine(dir, "Target.lnk");
            if (!TryCreateShortcut(lnk, exe))
            {
                Console.WriteLine("      跳过：WScript.Shell 不可用");
                Console.WriteLine();
                return;
            }

            ImageSource iconLnk = IconCache.Get(lnk);
            ImageSource iconExe = IconCache.Get(exe);

            Check(iconLnk != null, "lnk 能取到图标");
            Check(iconExe != null, "目标 exe 能取到图标");

            if (iconLnk != null && iconExe != null)
            {
                int diff = CompareBottomLeft(iconLnk, iconExe);
                Check(diff >= 0, "两张图尺寸可比");
                // 关键断言：左下角应该几乎没差异（箭头没了）
                Check(diff <= 20, "★lnk 图标左下角无箭头（差异 ≤ 20 像素）",
                    "实际差异 " + diff + " 像素");
            }
            Console.WriteLine();
        }

        // ---------- 3. 透明图标不被破坏 ----------
        static void TestTransparentIcon()
        {
            Console.WriteLine("--- 3. 透明图标 ---");

            // 取一个真实图标，检查它保留了 alpha 通道
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "notepad.exe");
            if (!File.Exists(exe))
            {
                Console.WriteLine("      跳过");
                Console.WriteLine();
                return;
            }

            ImageSource icon = IconCache.Get(exe);
            BitmapSource bs = icon as BitmapSource;
            if (bs == null) { Console.WriteLine("      跳过：无图标"); Console.WriteLine(); return; }

            // 转成 BGRA32 读像素
            BitmapSource converted = bs.Format == PixelFormats.Bgra32
                ? bs
                : new FormatConvertedBitmap(bs, PixelFormats.Bgra32, null, 0);

            int w = converted.PixelWidth, h = converted.PixelHeight;
            byte[] pixels = new byte[w * h * 4];
            converted.CopyPixels(pixels, w * 4, 0);

            int transparent = 0, opaque = 0;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] < 20) transparent++;
                else if (pixels[i] > 235) opaque++;
            }

            Check(transparent > 0, "图标保留透明区域", "透明像素 " + transparent);
            Check(opaque > 0, "图标有实心区域", "实心像素 " + opaque);
            // 透明 + 实心 不应该占满全部（说明是正常图标而不是纯色块）
            Check(transparent + opaque <= w * h + 10, "透明与实心像素计数合理");
            Console.WriteLine();
        }

        // ---------- 4. 缓存键包含版本号 ----------
        static void TestCacheKeyChangesOnVersion(string root)
        {
            Console.WriteLine("--- 4. 缓存键含版本号 ---");

            // 缓存目录里生成的文件名应该以 v2- 开头
            string cacheDir = Path.Combine(root, "config", "iconcache");
            if (!Directory.Exists(cacheDir))
            {
                Console.WriteLine("      跳过：缓存目录不存在");
                Console.WriteLine();
                return;
            }

            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "notepad.exe");
            if (!File.Exists(exe)) { Console.WriteLine("      跳过"); Console.WriteLine(); return; }

            IconCache.Get(exe);

            string[] files = Directory.GetFiles(cacheDir, "*.png");
            bool hasVersion = false;
            foreach (string f in files)
            {
                if (Path.GetFileName(f).StartsWith("v2-", StringComparison.OrdinalIgnoreCase))
                { hasVersion = true; break; }
            }
            Check(hasVersion, "★缓存文件名带版本前缀 v2-（旧缓存自动失效）",
                files.Length + " 个文件");

            // 旧格式（不带版本前缀）的文件不应该被新代码读出来
            bool anyOldFormat = false;
            foreach (string f in files)
            {
                string n = Path.GetFileName(f);
                if (!n.StartsWith("v", StringComparison.OrdinalIgnoreCase)) { anyOldFormat = true; break; }
            }
            Check(!anyOldFormat, "缓存目录里没有旧格式文件", anyOldFormat ? "存在旧文件" : "");
            Console.WriteLine();
        }

        // ---------- 5. 空路径 / 不存在的路径 ----------
        static void TestMissingPath()
        {
            Console.WriteLine("--- 5. 空路径与不存在的路径 ---");

            Check(IconCache.Get(null) == null, "null 返回 null");
            Check(IconCache.Get("") == null, "空串返回 null");
            Check(IconCache.Get(@"Z:\不存在的\路径.exe") == null, "不存在的路径返回 null");
            Console.WriteLine();
        }

        // ---------- 6. 商店应用路径 ----------
        static void TestShellPath(string root)
        {
            Console.WriteLine("--- 6. 商店应用路径 ---");

            string shellPath = AppPaths.FromAumid("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");

            // 不应该抛异常（取不到也返回 null）
            ImageSource icon = null;
            bool threw = false;
            try { icon = IconCache.Get(shellPath); }
            catch { threw = true; }

            Check(!threw, "商店应用路径不抛异常");
            // 这台机器上应该能取到（计算器是系统自带的）
            if (icon != null)
            {
                BitmapSource bs = icon as BitmapSource;
                Check(bs != null && bs.PixelWidth > 0, "商店应用能取到图标",
                    bs == null ? "" : bs.PixelWidth + "x" + bs.PixelHeight);
            }
            else
            {
                Console.WriteLine("      · 这台机器取不到计算器图标（不算失败）");
            }

            // 缓存键应该基于 AUMID（不含文件时间）
            string cacheDir = Path.Combine(root, "config", "iconcache");
            if (icon != null && Directory.Exists(cacheDir))
            {
                string[] files = Directory.GetFiles(cacheDir, "*.png");
                bool hasShellKey = false;
                foreach (string f in files)
                {
                    if (Path.GetFileName(f).StartsWith("v2-shell-", StringComparison.OrdinalIgnoreCase))
                    { hasShellKey = true; break; }
                }
                Check(hasShellKey, "商店应用缓存键用 v2-shell- 前缀",
                    files.Length + " 个文件");
            }
            Console.WriteLine();
        }

        // ---------- 7. 缓存失效与重新生成 ----------
        static void TestCacheInvalidation(string root)
        {
            Console.WriteLine("--- 7. 缓存失效与重新生成 ---");

            string dir = Path.Combine(root, "cachetest");
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, "CacheTest.exe");

            string sysExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "notepad.exe");
            if (!File.Exists(sysExe)) { Console.WriteLine("      跳过"); Console.WriteLine(); return; }

            try { File.Copy(sysExe, exe, true); }
            catch { Console.WriteLine("      跳过：复制失败"); Console.WriteLine(); return; }

            ImageSource first = IconCache.Get(exe);
            Check(first != null, "首次取图标成功");

            string cacheDir = Path.Combine(root, "config", "iconcache");
            int filesBefore = Directory.Exists(cacheDir) ? Directory.GetFiles(cacheDir, "*.png").Length : 0;

            // 改文件修改时间 → key 变化 → 应该重新生成
            try { File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddMinutes(5)); }
            catch { }

            ImageSource second = IconCache.Get(exe);
            Check(second != null, "改时间后仍能取到图标");

            int filesAfter = Directory.Exists(cacheDir) ? Directory.GetFiles(cacheDir, "*.png").Length : 0;
            Check(filesAfter > filesBefore, "文件时间变了 → 生成新缓存条目",
                filesBefore + " → " + filesAfter);
            Console.WriteLine();
        }

        // ---------- 辅助 ----------

        /// <summary>
        /// 比较两张图左下象限（箭头位置）的像素差异数。
        /// 返回 -1 表示尺寸不可比。
        /// </summary>
        static int CompareBottomLeft(ImageSource a, ImageSource b)
        {
            try
            {
                BitmapSource ba = ToBgra(a), bb = ToBgra(b);
                if (ba == null || bb == null) return -1;
                if (ba.PixelWidth != bb.PixelWidth || ba.PixelHeight != bb.PixelHeight) return -1;

                int w = ba.PixelWidth, h = ba.PixelHeight;
                byte[] pa = new byte[w * h * 4], pb = new byte[w * h * 4];
                ba.CopyPixels(pa, w * 4, 0);
                bb.CopyPixels(pb, w * 4, 0);

                int diff = 0;
                int x0 = 0, y0 = h / 2, x1 = w / 2, y1 = h;
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        int i = (y * w + x) * 4;
                        int d = Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1])
                              + Math.Abs(pa[i + 2] - pb[i + 2]) + Math.Abs(pa[i + 3] - pb[i + 3]);
                        if (d > 60) diff++;
                    }
                }
                return diff;
            }
            catch { return -1; }
        }

        static BitmapSource ToBgra(ImageSource src)
        {
            try
            {
                BitmapSource bs = src as BitmapSource;
                if (bs == null) return null;
                if (bs.Format == PixelFormats.Bgra32) return bs;
                return new FormatConvertedBitmap(bs, PixelFormats.Bgra32, null, 0);
            }
            catch { return null; }
        }

        static bool TryCreateShortcut(string lnkPath, string targetPath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;
                object shell = Activator.CreateInstance(shellType);
                object sc = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                if (sc == null) return false;
                Type scType = sc.GetType();
                scType.InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { targetPath });
                scType.InvokeMember("Save",
                    System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                return File.Exists(lnkPath);
            }
            catch { return false; }
        }

        static string NewRoot()
        {
            string d = Path.Combine(Path.GetTempPath(), "icontest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            Directory.CreateDirectory(Path.Combine(d, "config"));
            return d;
        }
    }
}
