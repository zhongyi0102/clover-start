using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

namespace Clover
{
    /// <summary>
    /// 自定义背景图仓库测试：导入往返、坏文件拒绝、缺失回退、大图缩放、清除。
    ///
    /// **全部用临时目录，不碰真实 config。**
    /// </summary>
    static class BackgroundStoreTest
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
            Console.WriteLine("=== 自定义背景图测试 ===");
            Console.WriteLine();

            ApplicationShim();

            try
            {
                TestImportRoundtrip();
                TestBadFileRejected();
                TestMissingFallback();
                TestLargeImageScaled();
                TestSmallImageNotUpscaled();
                TestClear();
                TestFormatSupport();
            }
            catch (Exception ex)
            {
                Check(false, "测试不抛异常", ex.GetType().Name + ": " + ex.Message);
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // WPF BitmapImage 需要 Application 上下文（某些情况），这里不建也行
        static void ApplicationShim() { }

        static string NewRoot()
        {
            string d = Path.Combine(Path.GetTempPath(),
                "bg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(d, "config"));
            return d;
        }

        /// <summary>造一张纯色测试图。</summary>
        static string MakeImage(string dir, string name, int w, int h, ImageFormat fmt)
        {
            string p = Path.Combine(dir, name);
            using (Bitmap b = new Bitmap(w, h))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.SteelBlue);
                    g.FillRectangle(Brushes.OrangeRed, 0, 0, Math.Max(1, w / 3), Math.Max(1, h / 3));
                }
                b.Save(p, fmt);
            }
            return p;
        }

        // ---------- 1. 导入往返 ----------
        static void TestImportRoundtrip()
        {
            Console.WriteLine("--- 1. 导入往返 ---");

            string root = NewRoot();
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            try
            {
                string img = MakeImage(src, "wall.png", 200, 120, ImageFormat.Png);

                Check(!BackgroundStore.Exists(root), "★导入前不存在");

                string err;
                string saved = BackgroundStore.Import(root, img, out err);

                Check(saved != null, "★导入成功", err ?? "");
                Check(err == null, "无错误", err ?? "");
                Check(File.Exists(saved), "★文件已落盘", saved ?? "");
                Check(BackgroundStore.Exists(root), "★Exists 返回 true");

                string found = BackgroundStore.Find(root);
                Check(found != null, "★Find 找得到", found ?? "");
                Check(found == saved, "Find 与 Import 返回一致");

                // 文件名固定为 custom.*
                Check(Path.GetFileNameWithoutExtension(found) == "custom",
                    "★文件名固定为 custom", Path.GetFileName(found));

                // 能真正解码
                int w, h;
                string derr;
                Check(BackgroundStore.TryDecode(found, out w, out h, out derr),
                    "★保存的文件能解码", derr ?? "");
                Check(w == 200 && h == 120, "尺寸保持 200x120", w + "x" + h);

                // 覆盖导入（换一张）
                string img2 = MakeImage(src, "wall2.jpg", 300, 180, ImageFormat.Jpeg);
                string saved2 = BackgroundStore.Import(root, img2, out err);
                Check(saved2 != null, "★再次导入成功", err ?? "");
                Check(Path.GetExtension(saved2).ToLowerInvariant() == ".jpg",
                    "★扩展名跟随新图", Path.GetExtension(saved2));

                // 旧扩展名的文件应被清掉（不残留两个 custom.*）
                string[] all = Directory.GetFiles(BackgroundStore.DirOf(root), "custom.*");
                Check(all.Length == 1, "★只保留一个 custom.*", all.Length + " 个");
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 2. 坏文件被拒绝 ----------
        static void TestBadFileRejected()
        {
            Console.WriteLine("--- 2. 坏文件被拒绝且不影响旧设置 ---");

            string root = NewRoot();
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            try
            {
                // 先正常导入一张
                string good = MakeImage(src, "good.png", 100, 80, ImageFormat.Png);
                string err;
                string saved = BackgroundStore.Import(root, good, out err);
                Check(saved != null, "基线导入成功", err ?? "");
                string baseline = BackgroundStore.Find(root);

                // 造一个假的 png（其实是文本）
                string fake = Path.Combine(src, "fake.png");
                File.WriteAllText(fake, "this is not an image at all");

                string err2;
                string r2 = BackgroundStore.Import(root, fake, out err2);
                Check(r2 == null, "★坏文件导入返回 null");
                Check(!string.IsNullOrEmpty(err2), "★给出了错误原因", err2 ?? "");

                // 旧图必须原样保留
                string after = BackgroundStore.Find(root);
                Check(after == baseline, "★旧图未被破坏", after ?? "(null)");
                Check(File.Exists(after), "★旧图文件仍在");

                // 扩展名不支持的也拒绝
                string txt = Path.Combine(src, "a.txt");
                File.WriteAllText(txt, "hello");
                string err3;
                Check(BackgroundStore.Import(root, txt, out err3) == null,
                    "★不支持的扩展名被拒绝", err3 ?? "");
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 3. 文件缺失回退 ----------
        static void TestMissingFallback()
        {
            Console.WriteLine("--- 3. 文件缺失 → 回退 ---");

            string root = NewRoot();
            try
            {
                // 没有任何自定义图
                Check(BackgroundStore.Find(root) == null, "★空目录 Find 返回 null");
                Check(!BackgroundStore.Exists(root), "★Exists 为 false");
                Check(BackgroundStore.Load(root, 3840) == null, "★Load 返回 null（调用方回退）");

                // 目录都不存在
                string root2 = Path.Combine(root, "nope");
                Check(BackgroundStore.Find(root2) == null, "★目录不存在 Find 返回 null");
                Check(BackgroundStore.Load(root2, 3840) == null, "★目录不存在 Load 返回 null");

                // 放一个损坏的 custom.png
                string dir = BackgroundStore.DirOf(root);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "custom.png"), "broken");

                Check(BackgroundStore.Exists(root), "文件在 → Exists 为 true");
                Check(BackgroundStore.Load(root, 3840) == null,
                    "★损坏文件 Load 返回 null（回退内置图）");

                // 解码也失败
                int w, h;
                string e;
                Check(!BackgroundStore.TryDecode(Path.Combine(dir, "custom.png"), out w, out h, out e),
                    "★损坏文件 TryDecode 返回 false");
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 4. 大图缩放 ----------
        static void TestLargeImageScaled()
        {
            Console.WriteLine("--- 4. 大图长边缩到 3840 ---");

            string root = NewRoot();
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            try
            {
                // 造一张 5000 x 2000 的图（长边 5000 > 3840）
                string big = MakeImage(src, "big.png", 5000, 2000, ImageFormat.Png);

                int ow, oh;
                string de;
                Check(BackgroundStore.TryDecode(big, out ow, out oh, out de),
                    "原图可解码", de ?? "");
                Check(ow == 5000 && oh == 2000, "原图 5000x2000", ow + "x" + oh);

                string err;
                string saved = BackgroundStore.Import(root, big, out err);
                Check(saved != null, "★大图导入成功", err ?? "");

                int nw, nh;
                Check(BackgroundStore.TryDecode(saved, out nw, out nh, out de),
                    "保存后可解码", de ?? "");

                Check(Math.Max(nw, nh) == 3840, "★长边 = 3840", Math.Max(nw, nh).ToString());

                // 等比：5000x2000 → 3840x1536
                int expectH = (int)Math.Round(2000 * (3840.0 / 5000.0));
                Check(Math.Abs(nh - expectH) <= 1,
                    "★保持宽高比", nh + " vs 期望 " + expectH);
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 5. 小图不放大 ----------
        static void TestSmallImageNotUpscaled()
        {
            Console.WriteLine("--- 5. 小图不放大 ---");

            string root = NewRoot();
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            try
            {
                string small = MakeImage(src, "small.png", 320, 240, ImageFormat.Png);

                string err;
                string saved = BackgroundStore.Import(root, small, out err);
                Check(saved != null, "小图导入成功", err ?? "");

                int w, h;
                string de;
                BackgroundStore.TryDecode(saved, out w, out h, out de);
                Check(w == 320 && h == 240, "★尺寸保持 320x240（没放大）", w + "x" + h);
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 6. 清除 ----------
        static void TestClear()
        {
            Console.WriteLine("--- 6. 清除 ---");

            string root = NewRoot();
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            try
            {
                string img = MakeImage(src, "a.png", 100, 100, ImageFormat.Png);
                string err;
                BackgroundStore.Import(root, img, out err);
                Check(BackgroundStore.Exists(root), "导入后存在");

                bool removed = BackgroundStore.RemoveAll(root);
                Check(removed, "★RemoveAll 返回 true");
                Check(!BackgroundStore.Exists(root), "★清除后不存在");
                Check(BackgroundStore.Find(root) == null, "★清除后 Find 返回 null");
                Check(BackgroundStore.Load(root, 3840) == null, "★清除后 Load 返回 null");

                // 再清一次（幂等，不报错）
                bool again = BackgroundStore.RemoveAll(root);
                Check(!again, "再次清除返回 false（没有可删的）");

                // 目录不存在时也不报错
                bool threw = false;
                try { BackgroundStore.RemoveAll(Path.Combine(root, "nope")); }
                catch { threw = true; }
                Check(!threw, "★目录不存在时不抛异常");
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 7. 格式支持 ----------
        static void TestFormatSupport()
        {
            Console.WriteLine("--- 7. 支持的格式 ---");

            string[] expect = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff" };
            foreach (string e in expect)
                Check(BackgroundStore.IsSupportedExt("x" + e), "支持 " + e);

            Check(!BackgroundStore.IsSupportedExt("x.txt"), "不支持 .txt");
            Check(!BackgroundStore.IsSupportedExt("x.svg"), "不支持 .svg");
            Check(!BackgroundStore.IsSupportedExt(""), "空串不支持");
            Check(!BackgroundStore.IsSupportedExt(null), "null 不支持");

            // 大小写不敏感
            Check(BackgroundStore.IsSupportedExt("X.PNG"), "★大写 .PNG 也支持");

            // 实测：各格式都能被 WPF 解码（webp 已验证）
            string root = NewRoot();
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            try
            {
                var cases = new[]
                {
                    new { n = "a.png", f = ImageFormat.Png },
                    new { n = "a.jpg", f = ImageFormat.Jpeg },
                    new { n = "a.bmp", f = ImageFormat.Bmp },
                    new { n = "a.gif", f = ImageFormat.Gif },
                    new { n = "a.tif", f = ImageFormat.Tiff },
                };

                foreach (var c in cases)
                {
                    string p = MakeImage(src, c.n, 64, 48, c.f);
                    int w, h;
                    string de;
                    bool ok = BackgroundStore.TryDecode(p, out w, out h, out de);
                    Check(ok, "★" + c.n + " 可解码", de ?? "");
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }
    }
}
