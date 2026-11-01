using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// Wallpaper Engine 集成测试：目录查找、project.json 解析、路径穿越防护、
    /// 视频复制替换回滚、暗度遮罩、回退逻辑。
    ///
    /// **全部用临时目录，不碰真实 WE 目录和 config。**
    /// </summary>
    static class WallpaperEngineTest
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
            Console.WriteLine("=== Wallpaper Engine 集成测试 ===");
            Console.WriteLine();

            try
            {
                TestSafeCombine();
                TestParseWallpaper();
                TestTypeFilter();
                TestLibraryFoldersVdf();
                TestVideoImport();
                TestDarkness();
                TestResolveById();
                TestWeVideoStaysAttached();
                TestAdaptiveVideoPolicy();
                TestFallback();
            }
            catch (Exception ex)
            {
                Check(false, "测试不抛异常", ex.GetType().Name + ": " + ex.Message);
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static string NewRoot()
        {
            string d = Path.Combine(Path.GetTempPath(),
                "we-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(d, "config"));
            return d;
        }

        static string MakePng(string dir, string name, int w, int h)
        {
            string p = Path.Combine(dir, name);
            using (Bitmap b = new Bitmap(w, h))
            {
                using (Graphics g = Graphics.FromImage(b)) g.Clear(Color.DarkSlateBlue);
                b.Save(p, ImageFormat.Png);
            }
            return p;
        }

        // ---------- 1. 路径穿越防护 ----------
        static void TestSafeCombine()
        {
            Console.WriteLine("--- 1. 路径穿越防护 ---");

            string baseDir = Path.Combine(Path.GetTempPath(), "we-safe");

            // 正常相对路径
            string ok = WallpaperEngine.SafeCombine(baseDir, "preview.jpg");
            Check(ok != null, "★正常相对路径通过", ok ?? "null");
            Check(ok != null && ok.EndsWith("preview.jpg", StringComparison.OrdinalIgnoreCase),
                "拼出的路径正确", ok ?? "");

            // 子目录
            Check(WallpaperEngine.SafeCombine(baseDir, "sub\\a.png") != null, "子目录路径通过");

            // ---- 攻击向量 ----
            Check(WallpaperEngine.SafeCombine(baseDir, "..\\..\\windows\\system32\\x.dll") == null,
                "★拒绝 ../ 穿越");
            Check(WallpaperEngine.SafeCombine(baseDir, "a\\..\\..\\b") == null,
                "★拒绝中间 ../");
            Check(WallpaperEngine.SafeCombine(baseDir, "C:\\Windows\\x.exe") == null,
                "★拒绝绝对路径（盘符）");
            Check(WallpaperEngine.SafeCombine(baseDir, "\\\\server\\share\\x") == null,
                "★拒绝 UNC 路径");
            Check(WallpaperEngine.SafeCombine(baseDir, "\\windows\\x") == null,
                "★拒绝根路径");
            Check(WallpaperEngine.SafeCombine(baseDir, "") == null, "拒绝空路径");
            Check(WallpaperEngine.SafeCombine(baseDir, null) == null, "拒绝 null");
            Check(WallpaperEngine.SafeCombine(null, "a.png") == null, "拒绝 null 基目录");

            // 正斜杠形式的穿越
            Check(WallpaperEngine.SafeCombine(baseDir, "../../etc/passwd") == null,
                "★拒绝正斜杠穿越");

            Console.WriteLine();
        }

        // ---------- 2. project.json 解析 ----------
        static void TestParseWallpaper()
        {
            Console.WriteLine("--- 2. project.json 解析 ---");

            string root = NewRoot();
            try
            {
                // ---- 正常 video ----
                string vdir = Path.Combine(root, "111");
                Directory.CreateDirectory(vdir);
                MakePng(vdir, "preview.png", 320, 180);
                File.WriteAllText(Path.Combine(vdir, "a.mp4"), "fake video data");
                File.WriteAllText(Path.Combine(vdir, "project.json"),
                    "{\"type\":\"video\",\"title\":\"测试视频\",\"file\":\"a.mp4\",\"preview\":\"preview.png\"}",
                    new UTF8Encoding(false));

                WallpaperEngine.Item v = WallpaperEngine.ParseWallpaper(vdir);
                Check(v != null, "★video 解析成功");
                if (v != null)
                {
                    Check(v.Type == "video", "类型 = video", v.Type);
                    Check(v.Title == "测试视频", "标题正确", v.Title);
                    Check(v.Id == "111", "ID = 目录名", v.Id);
                    Check(v.MainFile != null && File.Exists(v.MainFile), "主文件存在");
                    Check(v.PreviewFile != null && File.Exists(v.PreviewFile), "预览图存在");
                    Check(v.PreviewWidth == 320 && v.PreviewHeight == 180,
                        "预览图尺寸读到 320x180", v.PreviewWidth + "x" + v.PreviewHeight);
                    Check(!v.IsScene, "不是 scene");
                    Check(v.LowResPreview, "★320 宽 < 1280 → 判定为低分辨率",
                        "Width=" + v.PreviewWidth);
                }

                // ---- 大小写混用 ----
                string cdir = Path.Combine(root, "222");
                Directory.CreateDirectory(cdir);
                MakePng(cdir, "preview.jpg", 640, 360);
                File.WriteAllText(Path.Combine(cdir, "project.json"),
                    "{\"type\":\"Video\",\"title\":\"大写类型\",\"file\":\"b.mp4\",\"preview\":\"preview.jpg\"}",
                    new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(cdir, "b.mp4"), "x");

                WallpaperEngine.Item c = WallpaperEngine.ParseWallpaper(cdir);
                Check(c != null, "★type=\"Video\" 也能解析");
                Check(c != null && c.Type == "video", "★类型归一成小写", c == null ? "" : c.Type);

                // ---- 不支持的类型 ----
                string wdir = Path.Combine(root, "333");
                Directory.CreateDirectory(wdir);
                File.WriteAllText(Path.Combine(wdir, "project.json"),
                    "{\"type\":\"web\",\"title\":\"网页壁纸\"}", new UTF8Encoding(false));
                Check(WallpaperEngine.ParseWallpaper(wdir) == null, "★web 类型不列出");

                string adir = Path.Combine(root, "444");
                Directory.CreateDirectory(adir);
                File.WriteAllText(Path.Combine(adir, "project.json"),
                    "{\"type\":\"application\",\"title\":\"应用\"}", new UTF8Encoding(false));
                Check(WallpaperEngine.ParseWallpaper(adir) == null, "★application 不列出");

                // ---- 坏 JSON ----
                string bdir = Path.Combine(root, "555");
                Directory.CreateDirectory(bdir);
                File.WriteAllText(Path.Combine(bdir, "project.json"),
                    "{ 这不是合法 JSON ", new UTF8Encoding(false));
                Check(WallpaperEngine.ParseWallpaper(bdir) == null, "★坏 JSON 返回 null");

                // ---- 缺 project.json ----
                string ndir = Path.Combine(root, "666");
                Directory.CreateDirectory(ndir);
                Check(WallpaperEngine.ParseWallpaper(ndir) == null, "★缺 project.json 返回 null");

                // ---- 缺 file（video）----
                string mdir = Path.Combine(root, "777");
                Directory.CreateDirectory(mdir);
                File.WriteAllText(Path.Combine(mdir, "project.json"),
                    "{\"type\":\"video\",\"title\":\"没主文件\"}", new UTF8Encoding(false));
                Check(WallpaperEngine.ParseWallpaper(mdir) == null, "★video 缺 file 返回 null");

                // ---- scene 有预览图 ----
                string sdir = Path.Combine(root, "888");
                Directory.CreateDirectory(sdir);
                MakePng(sdir, "preview.png", 800, 450);
                File.WriteAllText(Path.Combine(sdir, "project.json"),
                    "{\"type\":\"scene\",\"title\":\"场景壁纸\",\"preview\":\"preview.png\"}",
                    new UTF8Encoding(false));

                WallpaperEngine.Item s = WallpaperEngine.ParseWallpaper(sdir);
                Check(s != null, "★scene 有预览图 → 解析成功");
                if (s != null)
                {
                    Check(s.IsScene, "IsScene = true");
                    Check(s.TypeLabel == "仅静态预览", "★类型标签 = 仅静态预览", s.TypeLabel);
                    Check(string.IsNullOrEmpty(s.MainFile), "scene 没有主文件");
                }

                // ---- scene 没预览图 ----
                string s2 = Path.Combine(root, "999");
                Directory.CreateDirectory(s2);
                File.WriteAllText(Path.Combine(s2, "project.json"),
                    "{\"type\":\"scene\",\"title\":\"没预览\"}", new UTF8Encoding(false));
                Check(WallpaperEngine.ParseWallpaper(s2) == null,
                    "★scene 没预览图 → 不列出（无法用）");

                // ---- 路径穿越攻击 ----
                string edir = Path.Combine(root, "aaa");
                Directory.CreateDirectory(edir);
                File.WriteAllText(Path.Combine(edir, "project.json"),
                    "{\"type\":\"video\",\"title\":\"穿越\",\"file\":\"..\\\\..\\\\windows\\\\notepad.exe\"}",
                    new UTF8Encoding(false));
                Check(WallpaperEngine.ParseWallpaper(edir) == null,
                    "★file 含 .. 穿越 → 拒绝");

                // ---- 绝对路径攻击 ----
                string edir2 = Path.Combine(root, "bbb");
                Directory.CreateDirectory(edir2);
                File.WriteAllText(Path.Combine(edir2, "project.json"),
                    "{\"type\":\"video\",\"title\":\"绝对\",\"file\":\"C:\\\\Windows\\\\notepad.exe\"}",
                    new UTF8Encoding(false));
                Check(WallpaperEngine.ParseWallpaper(edir2) == null,
                    "★file 是绝对路径 → 拒绝");
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 3. 类型过滤 ----------
        static void TestTypeFilter()
        {
            Console.WriteLine("--- 3. 类型过滤 ---");

            Check(WallpaperEngine.IsSupportedType("video"), "video 支持");
            Check(WallpaperEngine.IsSupportedType("scene"), "scene 支持");
            Check(WallpaperEngine.IsSupportedType("VIDEO"), "★大写 VIDEO 支持");
            Check(WallpaperEngine.IsSupportedType("Scene"), "★混合大小写 Scene 支持");
            Check(!WallpaperEngine.IsSupportedType("web"), "web 不支持");
            Check(!WallpaperEngine.IsSupportedType("application"), "application 不支持");
            Check(!WallpaperEngine.IsSupportedType(""), "空串不支持");
            Check(!WallpaperEngine.IsSupportedType(null), "null 不支持");

            Console.WriteLine();
        }

        // ---------- 4. libraryfolders.vdf 解析 ----------
        static void TestLibraryFoldersVdf()
        {
            Console.WriteLine("--- 4. libraryfolders.vdf 解析 ---");

            string root = NewRoot();
            try
            {
                string vdf = Path.Combine(root, "libraryfolders.vdf");
                File.WriteAllText(vdf,
                    "\"libraryfolders\"\n{\n"
                    + "\t\"0\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\Program Files\\\\steam\"\n\t}\n"
                    + "\t\"1\"\n\t{\n\t\t\"path\"\t\t\"E:\\\\SteamLibrary\"\n\t}\n"
                    + "}\n",
                    new UTF8Encoding(false));

                List<string> libs = WallpaperEngine.ParseLibraryFolders(vdf);
                Check(libs.Count == 2, "★解析出 2 个库", Convert.ToString(libs.Count));
                Check(libs.Contains(@"D:\Program Files\steam"), "库 1 正确", string.Join(" | ", libs.ToArray()));
                Check(libs.Contains(@"E:\SteamLibrary"), "★库 2 正确（反斜杠还原）");

                // 不存在的文件
                Check(WallpaperEngine.ParseLibraryFolders(
                    Path.Combine(root, "nope.vdf")).Count == 0, "不存在的 vdf → 空列表");

                // 空内容
                string empty = Path.Combine(root, "empty.vdf");
                File.WriteAllText(empty, "", new UTF8Encoding(false));
                Check(WallpaperEngine.ParseLibraryFolders(empty).Count == 0, "空 vdf → 空列表");
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            // 找不到目录时不抛异常
            bool threw = false;
            try { WallpaperEngine.ScanAuto(); } catch { threw = true; }
            Check(!threw, "★ScanAuto 不抛异常");

            threw = false;
            try { WallpaperEngine.Scan(null); } catch { threw = true; }
            Check(!threw, "Scan(null) 不抛异常");
            Check(WallpaperEngine.Scan(null).Items.Count == 0, "Scan(null) 返回空");

            Console.WriteLine();
        }

        // ---------- 5. 视频复制 ----------
        static void TestVideoImport()
        {
            Console.WriteLine("--- 5. 视频复制 / 替换 / 回滚 ---");

            string root = NewRoot();
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            try
            {
                // 造一个假 mp4（内容不重要，只测复制）
                string v1 = Path.Combine(src, "v1.mp4");
                File.WriteAllBytes(v1, new byte[1024 * 200]);

                Check(!BackgroundStore.HasVideo(root), "★初始没有视频");

                string err;
                string saved = BackgroundStore.ImportVideo(root, v1, null, null, out err);
                Check(saved != null, "★复制成功", err ?? "");
                Check(err == null, "无错误", err ?? "");
                Check(File.Exists(saved), "文件已落盘", saved ?? "");
                Check(BackgroundStore.HasVideo(root), "★HasVideo = true");

                string found = BackgroundStore.FindVideo(root);
                Check(found != null, "FindVideo 找得到");
                Check(Path.GetFileNameWithoutExtension(found) == "custom-video",
                    "★文件名固定 custom-video", Path.GetFileName(found));

                // ---- 替换 ----
                string v2 = Path.Combine(src, "v2.mp4");
                File.WriteAllBytes(v2, new byte[1024 * 300]);
                string saved2 = BackgroundStore.ImportVideo(root, v2, null, null, out err);
                Check(saved2 != null, "★替换成功", err ?? "");

                string[] all = Directory.GetFiles(BackgroundStore.DirOf(root), "custom-video.*");
                Check(all.Length == 1, "★只保留一份视频", all.Length + " 个");

                // ---- 进度回调 ----
                double lastP = -1;
                string v3 = Path.Combine(src, "v3.mp4");
                File.WriteAllBytes(v3, new byte[1024 * 500]);
                BackgroundStore.ImportVideo(root, v3,
                    delegate(double p) { lastP = p; }, null, out err);
                Check(lastP >= 0.99, "★进度回调到达 100%", lastP.ToString("0.00"));

                // ---- 取消 ----
                int calls = 0;
                string v4 = Path.Combine(src, "v4.mp4");
                File.WriteAllBytes(v4, new byte[1024 * 1024]);
                string saved4 = BackgroundStore.ImportVideo(root, v4,
                    null,
                    delegate { calls++; return calls > 1; },   // 第二次就取消
                    out err);
                Check(saved4 == null, "★取消返回 null");
                Check(err == "已取消", "★错误信息 = 已取消", err ?? "");

                // 取消后旧视频仍在
                Check(BackgroundStore.HasVideo(root), "★取消后旧视频仍在");

                // ---- 源文件不存在 ----
                string bad = BackgroundStore.ImportVideo(root,
                    Path.Combine(src, "nope.mp4"), null, null, out err);
                Check(bad == null, "源文件不存在 → 返回 null");
                Check(BackgroundStore.HasVideo(root), "★失败后旧视频仍在");

                // ---- 不支持的扩展名 ----
                string txt = Path.Combine(src, "a.txt");
                File.WriteAllText(txt, "x");
                string r5 = BackgroundStore.ImportVideo(root, txt, null, null, out err);
                Check(r5 == null, "★不支持扩展名 → 拒绝");

                // ---- 删除 ----
                Check(BackgroundStore.RemoveVideo(root), "RemoveVideo 成功");
                Check(!BackgroundStore.HasVideo(root), "★删除后没有视频");

                // 幂等
                Check(!BackgroundStore.RemoveVideo(root), "再删返回 false");

                // ---- 来源记录 ----
                BackgroundStore.WriteSource(root, "123456", "测试壁纸", "video");
                Dictionary<string, string> s = BackgroundStore.ReadSource(root);
                Check(s != null, "★来源记录可读");
                Check(s != null && s["id"] == "123456", "ID 正确", s == null ? "" : s["id"]);
                Check(s != null && s["title"] == "测试壁纸", "标题正确", s == null ? "" : s["title"]);

                BackgroundStore.ClearSource(root);
                Check(BackgroundStore.ReadSource(root) == null, "★清除后读不到");
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 6. 暗度 ----------
        static void TestDarkness()
        {
            Console.WriteLine("--- 6. 背景暗度配置 ---");

            string root = NewRoot();
            try
            {
                // 默认 0
                Settings s = Settings.Load(root);
                Check(s.GlassDarkness == 0, "★默认暗度 = 0", Convert.ToString(s.GlassDarkness));

                // 往返
                s.GlassDarkness = 35;
                s.Save();
                Settings s2 = Settings.Load(root);
                Check(Math.Abs(s2.GlassDarkness - 35) < 0.01,
                    "★往返保持 35", Convert.ToString(s2.GlassDarkness));

                // 越界钳制
                File.WriteAllText(Path.Combine(root, "config", "settings.json"),
                    "{\"glassDarkness\":999}", new UTF8Encoding(false));
                Settings s3 = Settings.Load(root);
                Check(s3.GlassDarkness <= 60, "★超过 60 被钳制", Convert.ToString(s3.GlassDarkness));

                File.WriteAllText(Path.Combine(root, "config", "settings.json"),
                    "{\"glassDarkness\":-50}", new UTF8Encoding(false));
                Settings s4 = Settings.Load(root);
                Check(s4.GlassDarkness >= 0, "★负数被钳制到 0", Convert.ToString(s4.GlassDarkness));

                // 非法值
                File.WriteAllText(Path.Combine(root, "config", "settings.json"),
                    "{\"glassDarkness\":\"abc\"}", new UTF8Encoding(false));
                Settings s5 = Settings.Load(root);
                Check(s5.GlassDarkness >= 0 && s5.GlassDarkness <= 60,
                    "★非法值回退默认", Convert.ToString(s5.GlassDarkness));
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 6b. 引用模式（ResolveById / RelativeTo）----------
        static void TestResolveById()
        {
            Console.WriteLine("--- 6b. 引用模式：按 ID 定位 ---");

            // 相对路径计算
            string dir = Path.Combine(Path.GetTempPath(), "we-ref", "123456");
            string f1 = Path.Combine(dir, "a.mp4");
            string rel = WallpaperEngine.RelativeTo(dir, f1);
            Check(rel == "a.mp4", "★RelativeTo 得到 a.mp4", rel ?? "null");

            // 子目录
            string f2 = Path.Combine(dir, "sub\\b.mp4");
            Check(WallpaperEngine.RelativeTo(dir, f2) == "sub\\b.mp4",
                "★子目录相对路径", WallpaperEngine.RelativeTo(dir, f2) ?? "null");

            // 目录外的文件 → 空
            string outside = Path.Combine(Path.GetTempPath(), "other.mp4");
            Check(WallpaperEngine.RelativeTo(dir, outside) == "",
                "★目录外文件返回空");

            Check(WallpaperEngine.RelativeTo(null, f1) == "", "null 目录返回空");
            Check(WallpaperEngine.RelativeTo(dir, null) == "", "null 文件返回空");

            // ---- ResolveById：找不到 → Error ----
            WallpaperEngine.Resolved r = WallpaperEngine.ResolveById(
                "no-such-id-xyz", "a.mp4", "video");
            Check(!r.Ok, "★不存在的 ID → 不 Ok");
            Check(!string.IsNullOrEmpty(r.Error), "★给出了失败原因", r.Error ?? "");

            // 空 ID
            WallpaperEngine.Resolved r2 = WallpaperEngine.ResolveById("", "", "");
            Check(!r2.Ok, "★空 ID → 不 Ok");

            // 相对路径穿越也要拒绝（配置可能被手改坏）
            WallpaperEngine.Resolved r3 = WallpaperEngine.ResolveById(
                "123", "..\\..\\windows\\notepad.exe", "video");
            Check(!r3.Ok, "★相对路径含 .. → 定位失败");

            WallpaperEngine.Resolved r4 = WallpaperEngine.ResolveById(
                "123", "C:\\Windows\\notepad.exe", "video");
            Check(!r4.Ok, "★相对路径是绝对路径 → 定位失败");

            Console.WriteLine();
        }

        // ---------- 6c. WE 视频模式不得被 custom 模式立即卸载 ----------
        static void TestWeVideoStaysAttached()
        {
            Console.WriteLine("--- 6c. WE 视频调用链 ---");

            string root = NewRoot();
            string weRoot = Path.Combine(root, "workshop");
            string id = "1234567890";
            string itemDir = Path.Combine(weRoot, id);
            Directory.CreateDirectory(itemDir);
            string video = Path.Combine(itemDir, "wall.mp4");
            File.WriteAllBytes(video, new byte[] { 0, 0, 0, 0 });

            System.Windows.Application app = null;
            PanelWindow panel = null;
            try
            {
                WallpaperEngine.TestRootsOverride = new List<string> { weRoot };

                Settings s = Settings.Load(root);
                s.GlassMode = "we";
                s.GlassOpacity = 92;
                s.WeWallpaperId = id;
                s.WeWallpaperTitle = "测试视频";
                s.WeWallpaperType = "video";
                s.WeWallpaperFile = "wall.mp4";

                if (System.Windows.Application.Current == null)
                {
                    app = new System.Windows.Application();
                    app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
                }

                // 构造函数会走真实的 BuildUi → ApplyGlassSetting 调用链。
                panel = new PanelWindow(root, null, s);
                string actual = panel.TestVideoBackdropPath();

                Check(!string.IsNullOrEmpty(actual),
                    "★WE 视频设置后视频层仍保留（不被 custom 处理卸载）",
                    actual ?? "(null)");
                Check(string.Equals(actual, video, StringComparison.OrdinalIgnoreCase),
                    "★视频层引用的是按 ID 定位到的原 mp4",
                    actual ?? "(null)");
            }
            finally
            {
                WallpaperEngine.TestRootsOverride = null;
                try { if (panel != null) panel.Close(); } catch { }
                try { if (app != null) app.Shutdown(); } catch { }
                try { Directory.Delete(root, true); } catch { }
            }

            Console.WriteLine();
        }
        // ---------- 6d. 视频自适应暂停 / 恢复 ----------
        static void TestAdaptiveVideoPolicy()
        {
            Console.WriteLine("--- 6d. 视频自适应暂停 / 恢复 ---");

            string root = NewRoot();
            string weRoot = Path.Combine(root, "workshop");
            string id = "99887766";
            string itemDir = Path.Combine(weRoot, id);
            Directory.CreateDirectory(itemDir);
            string video = Path.Combine(itemDir, "wall.mp4");
            File.WriteAllBytes(video, new byte[] { 0, 0, 0, 0 });

            System.Windows.Application app = null;
            PanelWindow panel = null;
            try
            {
                WallpaperEngine.TestRootsOverride = new List<string> { weRoot };
                Settings s = Settings.Load(root);
                s.GlassMode = "we";
                s.GlassOpacity = 92;
                s.WeWallpaperId = id;
                s.WeWallpaperTitle = "策略测试";
                s.WeWallpaperType = "video";
                s.WeWallpaperFile = "wall.mp4";

                if (System.Windows.Application.Current == null)
                {
                    app = new System.Windows.Application();
                    app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
                }

                panel = new PanelWindow(root, null, s);

                panel.TestApplyVideoPolicy(false, false, false);
                Check(panel.TestVideoIsPaused(), "★面板隐藏 → 暂停");

                panel.TestApplyVideoPolicy(true, false, false);
                Check(!panel.TestVideoIsPaused(), "★面板显示且非全屏/非电池 → 恢复");

                panel.TestApplyVideoPolicy(true, true, false);
                Check(panel.TestVideoIsPaused(), "★全屏应用 → 暂停");

                panel.TestApplyVideoPolicy(true, false, false);
                Check(!panel.TestVideoIsPaused(), "★退出全屏 → 恢复");

                panel.TestApplyVideoPolicy(true, false, true);
                Check(panel.TestVideoIsPaused(), "★电池供电 → 暂停");

                panel.TestApplyVideoPolicy(true, false, false);
                Check(!panel.TestVideoIsPaused(), "★接回电源 → 恢复");
            }
            finally
            {
                WallpaperEngine.TestRootsOverride = null;
                try { if (panel != null) panel.Close(); } catch { }
                try { if (app != null) app.Shutdown(); } catch { }
                try { Directory.Delete(root, true); } catch { }
            }

            Console.WriteLine();
        }
        // ---------- 7. 回退逻辑 ----------
        static void TestFallback()
        {
            Console.WriteLine("--- 7. 回退逻辑 ---");

            string root = NewRoot();
            try
            {
                // 没有自定义图、没有视频 → 各 Find 都返回 null
                Check(BackgroundStore.Find(root) == null, "无静态图 → Find 返回 null");
                Check(BackgroundStore.FindVideo(root) == null, "无视频 → FindVideo 返回 null");
                Check(BackgroundStore.Load(root, 3840) == null, "无静态图 → Load 返回 null");

                // 目录不存在
                string nope = Path.Combine(root, "nope");
                Check(BackgroundStore.FindVideo(nope) == null, "目录不存在 → FindVideo null");
                Check(BackgroundStore.ReadSource(nope) == null, "目录不存在 → ReadSource null");

                // 坏图 → Load 返回 null（调用方回退）
                string dir = BackgroundStore.DirOf(root);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "custom.png"), "broken");
                Check(BackgroundStore.Load(root, 3840) == null,
                    "★损坏图 → Load 返回 null（回退内置图）");

                // 坏视频文件不影响 FindVideo（只找文件，不解码）
                File.WriteAllText(Path.Combine(dir, "custom-video.mp4"), "broken");
                Check(BackgroundStore.FindVideo(root) != null,
                    "坏视频文件仍能被 FindVideo 找到（解码失败由 MediaFailed 处理）");
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }
    }
}
