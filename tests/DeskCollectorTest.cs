using System;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 收纳桌面 / 放回桌面的回归测试。
    ///
    /// **只用复制出来的临时文件，不碰真实桌面。**
    /// </summary>
    static class DeskCollectorTest
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
                "deskcollect-" + tag + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        static void Clean(string root)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }

        static void Main()
        {
            TestFilter();
            TestCollect();
            TestCollectConflict();
            TestPutBack();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static void TestFilter()
        {
            Check(DeskCollector.IsAllowed("a.txt"), "txt 允许");
            Check(DeskCollector.IsAllowed("a.docx"), "docx 允许");
            Check(!DeskCollector.IsAllowed("a.exe"), "★exe 拒绝");
            // .lnk 在 DeskCollector 里**仍然被拒** —— 它有自己的 BlockedExt，
            // 比 DropSpec.AppExts 多 .jar/.ps1/.vbs。本次只放开了「拖入」，
            // 「收纳」不受影响（收纳是把桌面清空，快捷方式也该被收走才怪，
            // 保持原行为最稳）。
            Check(!DeskCollector.IsAllowed("a.lnk"), "★lnk 在收纳侧仍拒绝");
            Check(!DeskCollector.IsAllowed("a.msi"), "msi 拒绝");
            Check(!DeskCollector.IsAllowed("a.bat"), "bat 拒绝");
            Check(!DeskCollector.IsAllowed(null), "null 拒绝");
        }

        static void TestCollect()
        {
            string root = NewRoot("collect");
            try
            {
                string src = Path.Combine(root, "desk");
                string dst = Path.Combine(root, "deskfiles");
                Directory.CreateDirectory(src);
                File.WriteAllText(Path.Combine(src, "a.txt"), "aaa");
                File.WriteAllText(Path.Combine(src, "b.pdf"), "bbbb");

                DeskCollector.MoveResult r = DeskCollector.Collect(
                    new System.Collections.Generic.List<string> { Path.Combine(src, "a.txt"), Path.Combine(src, "b.pdf") }, dst);

                Check(r.AllOk, "★收纳无失败", string.Join(",", r.Failed.ToArray()));
                Check(r.MovedCount == 2, "★收纳 2 个", Convert.ToString(r.MovedCount));
                Check(File.Exists(Path.Combine(dst, "a.txt")), "★a.txt 已到目标");
                Check(!File.Exists(Path.Combine(src, "a.txt")), "★源已移走");

                // 每条记录必须带原路径与新路径（上次事故的缺口）
                bool hasPaths = r.Moved[0].From.Length > 0 && r.Moved[0].To.Length > 0
                    && r.Moved[0].Time.Length > 0;
                Check(hasPaths, "★记录含原路径/新路径/时间");
            }
            finally { Clean(root); }
        }

        static void TestCollectConflict()
        {
            string root = NewRoot("cc");
            try
            {
                string src = Path.Combine(root, "desk");
                string dst = Path.Combine(root, "deskfiles");
                Directory.CreateDirectory(src);
                Directory.CreateDirectory(dst);
                File.WriteAllText(Path.Combine(src, "same.txt"), "new");
                File.WriteAllText(Path.Combine(dst, "same.txt"), "old");

                DeskCollector.MoveResult r = DeskCollector.Collect(
                    new System.Collections.Generic.List<string> { Path.Combine(src, "same.txt") }, dst);

                Check(r.AllOk, "冲突不报失败");
                Check(File.ReadAllText(Path.Combine(dst, "same.txt")) == "old",
                    "★原文件未被覆盖");
                Check(File.Exists(Path.Combine(dst, "same (1).txt")), "★自动加 (1)");
            }
            finally { Clean(root); }
        }

        static void TestPutBack()
        {
            string root = NewRoot("pb");
            try
            {
                string desk = Path.Combine(root, "deskfiles");
                string desktop = Path.Combine(root, "desktop");
                Directory.CreateDirectory(desk);
                Directory.CreateDirectory(desktop);
                File.WriteAllText(Path.Combine(desk, "x.txt"), "x");

                DeskCollector.MoveResult r = DeskCollector.PutBack(
                    new System.Collections.Generic.List<string> { Path.Combine(desk, "x.txt") }, desktop);

                Check(r.AllOk, "★放回桌面成功", string.Join(",", r.Failed.ToArray()));
                Check(File.Exists(Path.Combine(desktop, "x.txt")), "★文件到了桌面");
                Check(!File.Exists(Path.Combine(desk, "x.txt")), "★原位置已空");
            }
            finally { Clean(root); }
        }

    }
}
