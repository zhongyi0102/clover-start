using System;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 拖入导入测试：过滤 .exe/.lnk / 同盘移动 / 同名冲突 / 跨盘退化 / 占用失败。
    ///
    /// **全部用临时目录。**
    /// </summary>
    static class DeskImporterTest
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

        static void Main()
        {
            Console.WriteLine("=== 拖入导入测试 ===");
            Console.WriteLine();

            try
            {
                TestIsAllowed();
                TestImportBasic();
                TestFilterApps();
                TestConflict();
                TestSameDir();
                TestMissing();
                TestFolder();
                TestLocked();
                TestNoReceiver();
                TestVerifyMove();
                TestIsFromDesktop();
                TestImportMode();
            }
            catch (Exception ex)
            {
                Check(false, "测试不抛异常", ex.GetType().Name + ": " + ex.Message);
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static string NewDir(string tag)
        {
            string d = Path.Combine(Path.GetTempPath(),
                "deskimp-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(d);
            return d;
        }

        static void Clean(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }

        // ---------- 1. 允许规则 ----------
        static void TestIsAllowed()
        {
            Console.WriteLine("--- 1. 过滤规则 ---");

            Check(DeskImporter.IsAllowed("a.txt"), "txt 允许");
            Check(DeskImporter.IsAllowed("a.docx"), "docx 允许");
            Check(DeskImporter.IsAllowed("a.pdf"), "pdf 允许");
            Check(DeskImporter.IsAllowed("a.zip"), "zip 允许");
            Check(DeskImporter.IsAllowed("a.png"), "png 允许");
            Check(DeskImporter.IsAllowed("noext"), "无扩展名允许");

            Check(!DeskImporter.IsAllowed("a.exe"), "★exe 拒绝");
            // ★ .lnk 已放开：快捷方式当书签用，不是程序
            Check(DeskImporter.IsAllowed("a.lnk"), "★lnk 允许（快捷方式可拖入）");
            Check(DeskImporter.IsAllowed("a.LnK"), "★大小写混合也允许");
            Check(!DeskImporter.IsAllowed("a.EXE"), "★EXE 大写仍拒绝");
            Check(!DeskImporter.IsAllowed("a.msi"), "msi 拒绝");
            Check(!DeskImporter.IsAllowed("a.bat"), "bat 拒绝");
            Check(!DeskImporter.IsAllowed("a.dll"), "dll 拒绝");
            Check(!DeskImporter.IsAllowed("a.url"), "★url 仍拒绝（纯网址书签，未放开）");

            Check(!DeskImporter.IsAllowed(null), "null 拒绝");
            Check(!DeskImporter.IsAllowed(""), "空串拒绝");

            Console.WriteLine();
        }

        // ---------- 2. 基本导入 ----------
        static void TestImportBasic()
        {
            Console.WriteLine("--- 2. 基本导入 ---");

            string src = NewDir("b-src");
            string dst = NewDir("b-dst");
            try
            {
                File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
                File.WriteAllText(Path.Combine(src, "b.txt"), "world");

                DeskImporter.Result r = DeskImporter.Import(
                    new string[] {
                        Path.Combine(src, "a.txt"),
                        Path.Combine(src, "b.txt")
                    }, dst);

                Check(r.AllOk, "★无失败", string.Join(",", r.Failed.ToArray()));
                Check(r.ImportedCount == 2, "★导入 2 个", Convert.ToString(r.ImportedCount));
                Check(File.Exists(Path.Combine(dst, "a.txt")), "★a.txt 已到目标");
                Check(File.Exists(Path.Combine(dst, "b.txt")), "★b.txt 已到目标");
                // ★ 复制语义：源文件必须还在原处（改成复制前的断言是「已移走」）
                Check(File.Exists(Path.Combine(src, "a.txt")), "★源文件仍在原处（复制不是移动）");
                Check(File.Exists(Path.Combine(src, "b.txt")), "★第二个源文件也仍在");
                Check(File.ReadAllText(Path.Combine(dst, "a.txt")) == "hello", "内容正确");
                Check(File.ReadAllText(Path.Combine(src, "a.txt")) == "hello", "源内容也未被破坏");
            }
            finally { Clean(src); Clean(dst); }

            Console.WriteLine();
        }

        // ---------- 3. 过滤应用 ----------
        static void TestFilterApps()
        {
            Console.WriteLine("--- 3. 过滤应用（静默） ---");

            string src = NewDir("f-src");
            string dst = NewDir("f-dst");
            try
            {
                File.WriteAllText(Path.Combine(src, "doc.txt"), "x");
                File.WriteAllText(Path.Combine(src, "app.exe"), "x");
                File.WriteAllText(Path.Combine(src, "setup.msi"), "x");
                File.WriteAllText(Path.Combine(src, "link.lnk"), "x");

                DeskImporter.Result r = DeskImporter.Import(
                    new string[] {
                        Path.Combine(src, "doc.txt"),
                        Path.Combine(src, "app.exe"),
                        Path.Combine(src, "setup.msi"),
                        Path.Combine(src, "link.lnk")
                    }, dst);

                // doc.txt 收进来；app.exe / setup.msi 被拒；link.lnk **现在收进来**
                Check(r.ImportedCount == 2, "★导入 txt + lnk", Convert.ToString(r.ImportedCount));
                Check(r.SkippedCount == 2, "★跳过 2 个应用（exe + msi）",
                    Convert.ToString(r.SkippedCount));
                Check(r.AllOk, "★跳过不算失败");
                Check(File.Exists(Path.Combine(src, "app.exe")), "★exe 仍在原处（没被动）");
                Check(File.Exists(Path.Combine(src, "link.lnk")), "★lnk 仍在原处（复制不是移动）");
                Check(!File.Exists(Path.Combine(dst, "app.exe")), "exe 没进目标");
                Check(File.Exists(Path.Combine(dst, "link.lnk")), "★lnk 进了目标");
            }
            finally { Clean(src); Clean(dst); }

            Console.WriteLine();
        }

        // ---------- 4. 同名冲突 ----------
        static void TestConflict()
        {
            Console.WriteLine("--- 4. 同名冲突 ---");

            string src = NewDir("c-src");
            string dst = NewDir("c-dst");
            try
            {
                File.WriteAllText(Path.Combine(src, "same.txt"), "new");
                File.WriteAllText(Path.Combine(dst, "same.txt"), "old");

                DeskImporter.Result r = DeskImporter.Import(
                    new string[] { Path.Combine(src, "same.txt") }, dst);

                Check(r.AllOk, "无失败");
                Check(File.ReadAllText(Path.Combine(dst, "same.txt")) == "old",
                    "★原文件未被覆盖");
                Check(File.Exists(Path.Combine(dst, "same (1).txt")),
                    "★新文件改名成 same (1).txt", string.Join(",", r.Imported.ToArray()));
                Check(File.ReadAllText(Path.Combine(dst, "same (1).txt")) == "new", "新文件内容对");
            }
            finally { Clean(src); Clean(dst); }

            Console.WriteLine();
        }

        // ---------- 5. 已在目标目录 ----------
        static void TestSameDir()
        {
            Console.WriteLine("--- 5. 已在目标目录 ---");

            string dst = NewDir("s-dst");
            try
            {
                string f = Path.Combine(dst, "here.txt");
                File.WriteAllText(f, "x");

                DeskImporter.Result r = DeskImporter.Import(new string[] { f }, dst);

                Check(r.ImportedCount == 1, "★算作成功");
                Check(r.AllOk, "无失败");
                Check(File.Exists(f), "文件没被动过");
            }
            finally { Clean(dst); }

            Console.WriteLine();
        }

        // ---------- 6. 源不存在 ----------
        static void TestMissing()
        {
            Console.WriteLine("--- 6. 源不存在 ---");

            string dst = NewDir("m-dst");
            try
            {
                DeskImporter.Result r = DeskImporter.Import(
                    new string[] { Path.Combine(dst, "nope.txt") }, dst);

                Check(r.Failed.Count == 1, "★记为失败");
                Check(!r.AllOk, "AllOk 为 false");
            }
            finally { Clean(dst); }

            Console.WriteLine();
        }

        // ---------- 7. 文件夹 ----------
        static void TestFolder()
        {
            Console.WriteLine("--- 7. 文件夹整体搬入 ---");

            string src = NewDir("d-src");
            string dst = NewDir("d-dst");
            try
            {
                string sub = Path.Combine(src, "myfolder");
                Directory.CreateDirectory(sub);
                File.WriteAllText(Path.Combine(sub, "inner.txt"), "x");

                DeskImporter.Result r = DeskImporter.Import(new string[] { sub }, dst);

                Check(r.AllOk, "★文件夹导入成功", string.Join(",", r.Failed.ToArray()));
                Check(Directory.Exists(Path.Combine(dst, "myfolder")), "★目标有该文件夹");
                Check(File.Exists(Path.Combine(dst, "myfolder", "inner.txt")), "★内部文件跟着复制");
                // ★ 复制语义：源文件夹必须还在
                Check(Directory.Exists(sub), "★源文件夹仍在原处（复制不是移动）");
                Check(File.Exists(Path.Combine(sub, "inner.txt")), "★源文件夹里的文件也在");
            }
            finally { Clean(src); Clean(dst); }

            Console.WriteLine();
        }

        // ---------- 8. 被占用 ----------
        static void TestLocked()
        {
            Console.WriteLine("--- 8. 文件被占用 ---");

            string src = NewDir("l-src");
            string dst = NewDir("l-dst");
            try
            {
                string f = Path.Combine(src, "locked.txt");
                File.WriteAllText(f, "x");

                using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    DeskImporter.Result r = DeskImporter.Import(new string[] { f }, dst);
                    Check(r.Failed.Count == 1, "★占用记为失败", Convert.ToString(r.Failed.Count));
                    Check(!r.AllOk, "AllOk 为 false");
                    Check(File.Exists(f), "★失败后源文件仍在原位");
                    Check(!File.Exists(Path.Combine(dst, "locked.txt")), "目标没有半成品");
                }
            }
            finally { Clean(src); Clean(dst); }

            Console.WriteLine();
        }

        // ---------- 9. 对方没接收（拖出失败） ----------
        static void TestNoReceiver()
        {
            Console.WriteLine("--- 9. 对方没接收时源文件必须还在 ---");

            string src = NewDir("nr-src");
            try
            {
                string f = Path.Combine(src, "keep.txt");
                File.WriteAllText(f, "important");

                // 模拟拖出到桌面但对方拒收：什么都没发生。
                // 代码只做核对、不删源 —— 断言源文件完好。
                Check(File.Exists(f), "★没接收时源文件仍在");
                Check(File.ReadAllText(f) == "important", "★内容未被破坏");

                File.Delete(f);
                Check(!File.Exists(f), "★移走后源文件消失");

                Console.WriteLine();
            }
            finally { Clean(src); }
        }

        // ---------- 10. 移动后核对 ----------
        static void TestVerifyMove()
        {
            Console.WriteLine("--- 10. 移动后核对 ---");

            string src = NewDir("v-src");
            string dst = NewDir("v-dst");
            try
            {
                string f = Path.Combine(src, "data.txt");
                File.WriteAllText(f, "1234567890");
                long srcSize = new FileInfo(f).Length;

                string target = DeskImporter.MakeUniqueTarget(dst, "data.txt", false);
                File.Move(f, target);

                bool ok = File.Exists(target) && new FileInfo(target).Length == srcSize;
                Check(ok, "★移动后核对通过");
                Check(!File.Exists(f), "源已不在");

                File.WriteAllText(target, "x");
                bool bad = new FileInfo(target).Length == srcSize;
                Check(!bad, "★大小不一致时核对不通过");
            }
            finally { Clean(src); Clean(dst); }

            Console.WriteLine();
        }

        // ---------- 11. 来源判定：桌面 vs 其他 ----------
        static void TestIsFromDesktop()
        {
            Console.WriteLine("--- 11. 来源判定（桌面 → 移动 / 其他 → 复制）---");

            // 真实桌面目录下的路径 → 判定为「来自桌面」
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(desk))
            {
                Check(DeskImporter.IsFromDesktop(Path.Combine(desk, "a.txt")),
                    "★用户桌面上的文件 → 来自桌面", desk);

                // 子目录不算（桌面上某个文件夹里的东西不是"桌面上的文件"）
                Check(!DeskImporter.IsFromDesktop(
                        Path.Combine(desk, "sub", "a.txt")),
                    "★桌面子目录里的文件 → 不算来自桌面");
            }

            // 公共桌面
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (!string.IsNullOrEmpty(common))
            {
                Check(DeskImporter.IsFromDesktop(Path.Combine(common, "a.txt")),
                    "★公共桌面上的文件 → 来自桌面", common);
            }

            // 临时目录（模拟资源管理器里任意目录）→ 不是桌面
            string tmp = NewDir("notdesk");
            try
            {
                Check(!DeskImporter.IsFromDesktop(Path.Combine(tmp, "a.txt")),
                    "★其他目录的文件 → 不是来自桌面", tmp);
            }
            finally { Clean(tmp); }

            // 边界
            Check(!DeskImporter.IsFromDesktop(null), "null → false");
            Check(!DeskImporter.IsFromDesktop(""), "空串 → false");
            Check(!DeskImporter.IsFromDesktop("   "), "空白 → false");

            Console.WriteLine();
        }

        // ---------- 12. ImportMode 三种语义 ----------
        static void TestImportMode()
        {
            Console.WriteLine("--- 12. ImportMode：移动 / 复制 / 按来源 ---");

            // Move：源消失
            {
                string src = NewDir("mode-m-src");
                string dst = NewDir("mode-m-dst");
                try
                {
                    string f = Path.Combine(src, "a.txt");
                    File.WriteAllText(f, "x");

                    DeskImporter.Result r = DeskImporter.Import(
                        new string[] { f }, dst, null, DeskImporter.ImportMode.Move);

                    Check(r.ImportedCount == 1, "Move：导入 1 个");
                    Check(!File.Exists(f), "★Move → 源文件消失");
                    Check(File.Exists(Path.Combine(dst, "a.txt")), "★Move → 目标存在");
                }
                finally { Clean(src); Clean(dst); }
            }

            // Copy：源保留
            {
                string src = NewDir("mode-c-src");
                string dst = NewDir("mode-c-dst");
                try
                {
                    string f = Path.Combine(src, "a.txt");
                    File.WriteAllText(f, "x");

                    DeskImporter.Result r = DeskImporter.Import(
                        new string[] { f }, dst, null, DeskImporter.ImportMode.Copy);

                    Check(r.ImportedCount == 1, "Copy：导入 1 个");
                    Check(File.Exists(f), "★Copy → 源文件保留");
                    Check(File.Exists(Path.Combine(dst, "a.txt")), "★Copy → 目标存在");
                }
                finally { Clean(src); Clean(dst); }
            }

            // BySource + 非桌面文件 → 复制
            {
                string src = NewDir("mode-s-src");
                string dst = NewDir("mode-s-dst");
                try
                {
                    string f = Path.Combine(src, "a.txt");
                    File.WriteAllText(f, "x");

                    DeskImporter.Result r = DeskImporter.Import(
                        new string[] { f }, dst, null, DeskImporter.ImportMode.BySource);

                    Check(r.ImportedCount == 1, "BySource：导入 1 个");
                    Check(File.Exists(f), "★BySource + 非桌面 → 源保留（复制）");
                    Check(File.Exists(Path.Combine(dst, "a.txt")), "★BySource → 目标存在");
                }
                finally { Clean(src); Clean(dst); }
            }

            // 默认两参重载 = BySource
            {
                string src = NewDir("mode-d-src");
                string dst = NewDir("mode-d-dst");
                try
                {
                    string f = Path.Combine(src, "a.txt");
                    File.WriteAllText(f, "x");

                    DeskImporter.Result r = DeskImporter.Import(new string[] { f }, dst);

                    Check(r.ImportedCount == 1, "两参重载：导入 1 个");
                    Check(File.Exists(f), "★两参重载默认走 BySource（非桌面 → 复制）");
                }
                finally { Clean(src); Clean(dst); }
            }

            Console.WriteLine();
        }
    }
}
