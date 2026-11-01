using System;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 删除虚拟桌面时的文件搬运测试：
    /// 放回桌面 / 保留原处 / 同名冲突改名 / 占用失败 / 空桌面。
    ///
    /// **全部用临时目录，不碰真实桌面。**
    /// </summary>
    static class DeskRemovalTest
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
            Console.WriteLine("=== 虚拟桌面删除测试 ===");
            Console.WriteLine();

            try
            {
                TestHasContent();
                TestUniqueName();
                TestKeepInPlace();
                TestMoveToDesktop();
                TestMoveConflict();
                TestMoveLocked();
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
                "deskrm-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(d);
            return d;
        }

        static void Clean(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }

        // ---------- 1. 有无内容 ----------
        static void TestHasContent()
        {
            Console.WriteLine("--- 1. 有无内容 ---");

            string d = NewDir("hc");
            try
            {
                Check(!DeskRemoval.HasContent(d), "★空目录 → 无内容");

                File.WriteAllText(Path.Combine(d, "a.txt"), "x");
                Check(DeskRemoval.HasContent(d), "★有文件 → 有内容");

                Directory.CreateDirectory(Path.Combine(d, "sub"));
                Check(DeskRemoval.HasContent(d), "有子目录也算");

                Check(!DeskRemoval.HasContent(null), "null → false");
                Check(!DeskRemoval.HasContent(""), "空串 → false");
                Check(!DeskRemoval.HasContent(Path.Combine(d, "不存在")), "不存在的目录 → false");
            }
            finally { Clean(d); }

            Console.WriteLine();
        }

        // ---------- 2. 同名改名 ----------
        static void TestUniqueName()
        {
            Console.WriteLine("--- 2. 同名冲突改名 ---");

            string d = NewDir("uniq");
            try
            {
                // 不冲突
                string t1 = DeskRemoval.MakeUniqueTarget(d, "a.txt", false);
                Check(Path.GetFileName(t1) == "a.txt", "不冲突时原名", Path.GetFileName(t1));

                // 冲突一次
                File.WriteAllText(Path.Combine(d, "a.txt"), "x");
                string t2 = DeskRemoval.MakeUniqueTarget(d, "a.txt", false);
                Check(Path.GetFileName(t2) == "a (1).txt", "★冲突改成 a (1).txt", Path.GetFileName(t2));

                // 冲突两次
                File.WriteAllText(Path.Combine(d, "a (1).txt"), "x");
                string t3 = DeskRemoval.MakeUniqueTarget(d, "a.txt", false);
                Check(Path.GetFileName(t3) == "a (2).txt", "★再冲突改成 a (2).txt", Path.GetFileName(t3));

                // 文件夹（无扩展名）
                Directory.CreateDirectory(Path.Combine(d, "folder"));
                string t4 = DeskRemoval.MakeUniqueTarget(d, "folder", true);
                Check(Path.GetFileName(t4) == "folder (1)", "★文件夹冲突改名", Path.GetFileName(t4));

                // 多扩展名
                File.WriteAllText(Path.Combine(d, "x.tar.gz"), "x");
                string t5 = DeskRemoval.MakeUniqueTarget(d, "x.tar.gz", false);
                Check(Path.GetFileName(t5) == "x.tar (1).gz", "★多扩展名只改最后一段",
                    Path.GetFileName(t5));
            }
            finally { Clean(d); }

            Console.WriteLine();
        }

        // ---------- 3. 保留原处 ----------
        static void TestKeepInPlace()
        {
            Console.WriteLine("--- 3. 保留在存放文件夹 ---");

            string d = NewDir("keep");
            try
            {
                File.WriteAllText(Path.Combine(d, "a.txt"), "x");
                File.WriteAllText(Path.Combine(d, "b.txt"), "y");

                // KeepInPlace 只返回路径，不动文件
                string p = DeskRemoval.KeepInPlace(d);
                Check(p == d, "★返回原路径", p);
                Check(File.Exists(Path.Combine(d, "a.txt")), "★文件 a 仍在");
                Check(File.Exists(Path.Combine(d, "b.txt")), "★文件 b 仍在");
                Check(Directory.Exists(d), "★目录仍在");

                Check(DeskRemoval.KeepInPlace(null) == "", "null → 空");
                Check(DeskRemoval.KeepInPlace("") == "", "空 → 空");
            }
            finally { Clean(d); }

            Console.WriteLine();
        }

        // ---------- 4. 放回桌面（用临时目录模拟）----------
        static void TestMoveToDesktop()
        {
            Console.WriteLine("--- 4. 放回桌面 ---");

            // 注意：MoveToDesktop 内部用真实桌面路径，这里只验证「空目录」和「不存在」两种情况，
            // 避免往用户桌面写测试文件。
            string d = NewDir("mv");
            try
            {
                // 空目录 → 不报错、不移动、文件夹保留（因为没有文件可移，但也不会删）
                DeskRemoval.Result r1 = DeskRemoval.MoveToDesktop(d);
                Check(r1.AllOk, "★空目录不报错");
                Check(r1.Moved == 0, "空目录移动 0 项", Convert.ToString(r1.Moved));

                // 不存在的目录
                DeskRemoval.Result r2 = DeskRemoval.MoveToDesktop(Path.Combine(d, "nope"));
                Check(r2.AllOk, "不存在的目录不报错");
                Check(r2.Moved == 0, "移动 0 项");

                // null / 空
                Check(DeskRemoval.MoveToDesktop(null).Moved == 0, "null → 0 项");
                Check(DeskRemoval.MoveToDesktop("").Moved == 0, "空 → 0 项");
            }
            finally { Clean(d); }

            Console.WriteLine();
        }

        // ---------- 5. 移动冲突（模拟目标已存在）----------
        static void TestMoveConflict()
        {
            Console.WriteLine("--- 5. 移动时的同名冲突 ---");

            string src = NewDir("mc-src");
            string dst = NewDir("mc-dst");
            try
            {
                File.WriteAllText(Path.Combine(src, "same.txt"), "new");
                File.WriteAllText(Path.Combine(dst, "same.txt"), "old");

                // 用 MakeUniqueTarget 模拟搬运目标
                string t = DeskRemoval.MakeUniqueTarget(dst, "same.txt", false);
                Check(Path.GetFileName(t) == "same (1).txt", "★目标改名避免覆盖",
                    Path.GetFileName(t));

                File.Move(Path.Combine(src, "same.txt"), t);

                Check(File.ReadAllText(Path.Combine(dst, "same.txt")) == "old",
                    "★原文件未被覆盖");
                Check(File.ReadAllText(t) == "new", "新文件已就位");
            }
            finally { Clean(src); Clean(dst); }

            Console.WriteLine();
        }

        // ---------- 6. 被占用失败 ----------
        static void TestMoveLocked()
        {
            Console.WriteLine("--- 6. 文件被占用 ---");

            string src = NewDir("lock-src");
            string dst = NewDir("lock-dst");
            try
            {
                string f = Path.Combine(src, "locked.txt");
                File.WriteAllText(f, "x");

                // 独占打开，模拟被占用
                using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read,
                    FileShare.None))
                {
                    bool threw = false;
                    try
                    {
                        File.Move(f, Path.Combine(dst, "locked.txt"));
                    }
                    catch (IOException) { threw = true; }
                    catch (UnauthorizedAccessException) { threw = true; }

                    Check(threw, "★被占用的文件移动会失败");
                    Check(File.Exists(f), "★失败后文件保持原位");
                }

                // 释放后可以移动
                File.Move(f, Path.Combine(dst, "locked.txt"));
                Check(File.Exists(Path.Combine(dst, "locked.txt")), "释放后移动成功");
            }
            finally { Clean(src); Clean(dst); }

            Console.WriteLine();
        }
    }
}
