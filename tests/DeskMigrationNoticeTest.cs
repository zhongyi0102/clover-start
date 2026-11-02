using System;
using System.IO;

namespace Clover
{
    static class DeskMigrationNoticeTest
    {
        static int passed, failed;
        static void Check(bool c, string n)
        {
            if (c) { passed++; Console.WriteLine("PASS  " + n); }
            else { failed++; Console.WriteLine("FAIL  " + n); }
        }

        static void Main()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "desknotice-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            try
            {
                DeskMigrationNotice a = new DeskMigrationNotice(root);
                a.Load();
                Check(a.ShouldShow("old|target"), "★首次提示");
                Check(!a.ShouldShow("old|target"), "★同进程不重复提示");

                DeskMigrationNotice b = new DeskMigrationNotice(root);
                b.Load();
                Check(!b.ShouldShow("old|target"), "★重启后不重复提示");
                Check(b.ShouldShow("old2|target"), "★冲突签名变化后重新提示");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }

            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
