using System;
using System.IO;

namespace Clover
{
    /// <summary>核对式用户文件移动测试。</summary>
    static class VerifiedFileMoveTest
    {
        static int passed;
        static int failed;

        static void Check(bool cond, string name, string detail = "")
        {
            if (cond) { passed++; Console.WriteLine("PASS  " + name); }
            else
            {
                failed++;
                Console.WriteLine("FAIL  " + name
                    + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
            }
        }

        static string NewRoot(string tag)
        {
            string root = Path.Combine(Path.GetTempPath(),
                "verified-move-" + tag + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        static void Clean(string root)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }

        static void Main()
        {
            TestFile();
            TestDirectory();
            TestConflict();
            TestLocked();
            TestSamePath();
            TestMissing();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static void TestFile()
        {
            string root = NewRoot("file");
            try
            {
                string src = Path.Combine(root, "src.txt");
                string dst = Path.Combine(root, "out", "dst.txt");
                File.WriteAllText(src, "important-content");
                long size = new FileInfo(src).Length;

                VerifiedFileMove.Result r = VerifiedFileMove.Move(src, dst);
                Check(r.Ok, "★文件移动成功", r.Message);
                Check(!File.Exists(src), "★成功后源文件消失");
                Check(File.Exists(dst), "★目标文件存在");
                Check(new FileInfo(dst).Length == size, "★目标大小一致");
                Check(File.ReadAllText(dst) == "important-content", "★目标内容一致");
                Check(Directory.GetFiles(Path.GetDirectoryName(dst), ".clover-move-*", SearchOption.TopDirectoryOnly).Length == 0,
                    "★无临时文件残留");
            }
            finally { Clean(root); }
        }

        static void TestDirectory()
        {
            string root = NewRoot("dir");
            try
            {
                string src = Path.Combine(root, "source");
                string dst = Path.Combine(root, "target");
                Directory.CreateDirectory(Path.Combine(src, "nested"));
                File.WriteAllText(Path.Combine(src, "a.txt"), "aaa");
                File.WriteAllText(Path.Combine(src, "nested", "b.bin"), "bbbbbb");

                VerifiedFileMove.Result r = VerifiedFileMove.Move(src, dst);
                Check(r.Ok, "★嵌套文件夹移动成功", r.Message);
                Check(!Directory.Exists(src), "★成功后源文件夹消失");
                Check(File.ReadAllText(Path.Combine(dst, "a.txt")) == "aaa", "★根文件一致");
                Check(File.ReadAllText(Path.Combine(dst, "nested", "b.bin")) == "bbbbbb", "★嵌套文件一致");
            }
            finally { Clean(root); }
        }

        static void TestConflict()
        {
            string root = NewRoot("conflict");
            try
            {
                string src = Path.Combine(root, "src.txt");
                string dst = Path.Combine(root, "dst.txt");
                File.WriteAllText(src, "new");
                File.WriteAllText(dst, "old");

                VerifiedFileMove.Result r = VerifiedFileMove.Move(src, dst);
                Check(!r.Ok, "★目标冲突拒绝移动");
                Check(File.Exists(src), "★冲突时源文件保留");
                Check(File.ReadAllText(src) == "new", "★冲突时源内容未变");
                Check(File.ReadAllText(dst) == "old", "★冲突时目标未覆盖");
            }
            finally { Clean(root); }
        }

        static void TestLocked()
        {
            string root = NewRoot("locked");
            try
            {
                string src = Path.Combine(root, "src.txt");
                string dst = Path.Combine(root, "dst.txt");
                File.WriteAllText(src, "locked");

                using (FileStream held = new FileStream(src, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    VerifiedFileMove.Result r = VerifiedFileMove.Move(src, dst);
                    Check(!r.Ok, "★占用文件移动失败");
                    Check(File.Exists(src), "★占用失败时源文件保留");
                    Check(!File.Exists(dst), "★占用失败时不发布目标");
                    Check(Directory.GetFiles(root, ".clover-move-*", SearchOption.TopDirectoryOnly).Length == 0,
                        "★占用失败无临时文件残留");
                }
            }
            finally { Clean(root); }
        }

        static void TestSamePath()
        {
            string root = NewRoot("same");
            try
            {
                string src = Path.Combine(root, "a.txt");
                File.WriteAllText(src, "same");
                VerifiedFileMove.Result r = VerifiedFileMove.Move(src, src);
                Check(!r.Ok, "★同路径拒绝");
                Check(File.Exists(src), "★同路径源保留");
            }
            finally { Clean(root); }
        }

        static void TestMissing()
        {
            string root = NewRoot("missing");
            try
            {
                string src = Path.Combine(root, "missing.txt");
                string dst = Path.Combine(root, "dst.txt");
                VerifiedFileMove.Result r = VerifiedFileMove.Move(src, dst);
                Check(!r.Ok, "★源不存在返回失败");
                Check(!File.Exists(dst), "★源不存在不创建目标");
            }
            finally { Clean(root); }
        }
    }
}
