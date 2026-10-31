using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

namespace Clover
{
    /// <summary>
    /// FileOps 测试：重命名校验、删除走回收站、受保护目录、剪切粘贴、新建文件夹、日志。
    ///
    /// **全部用假文件系统**（FakeFs）—— 不碰真实回收站，不动真实文件。
    /// 假实现内部用内存字典模拟路径存在性，所以能测「可移动盘」「网络盘」
    /// 「跨卷」这些真实环境里不好造的场景。
    /// </summary>
    static class FileOpsTest
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
            Console.WriteLine("=== FileOps 测试 ===");
            Console.WriteLine();

            FileOps.Warn = delegate(string m) { };

            TestValidateName();
            TestRenameSuccess();
            TestRenameRejections();
            TestRenameCaseOnly();
            TestRenameSourceRoot();
            TestDeleteToRecycleBin();
            TestDeleteRemovableRejected();
            TestDeleteProtected();
            TestProtectedPrecision();
            TestCut();
            TestPaste();
            TestPasteNoOverwrite();
            TestPasteIntoItself();
            TestPasteSameVolume();
            TestNewFolder();
            TestFailureNoRetry();
            TestLogAppendAndTrim();
            TestElevation();
            TestSourceRootAndInside();
            TestStructLayout();
            TestRealFileSystem();
            TestReparsePoint();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 名称校验 ----------
        static void TestValidateName()
        {
            Console.WriteLine("--- 1. 名称校验 ---");

            Check(FileOps.ValidateName("正常名字.txt").Ok, "★合法名称通过");
            Check(FileOps.ValidateName("a").Ok, "单字符通过");

            Check(FileOps.ValidateName("").Status == FileOps.OpStatus.EmptyName, "★空名拒绝");
            Check(FileOps.ValidateName(null).Status == FileOps.OpStatus.EmptyName, "★null 拒绝");
            Check(FileOps.ValidateName("   ").Status == FileOps.OpStatus.EmptyName, "★纯空格拒绝");

            foreach (char c in new char[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' })
            {
                FileOps.OpResult r = FileOps.ValidateName("a" + c + "b.txt");
                Check(r.Status == FileOps.OpStatus.InvalidChars,
                    "★非法字符 '" + c + "' 拒绝", r.Status.ToString());
            }

            Check(FileOps.ValidateName("abc.").Status == FileOps.OpStatus.TrailingDotOrSpace,
                "★末尾点拒绝");
            Check(FileOps.ValidateName("abc ").Status == FileOps.OpStatus.TrailingDotOrSpace,
                "★末尾空格拒绝");

            foreach (string n in new string[] {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM9", "LPT1", "LPT9",
                "con", "con.txt", "COM1.log" })
            {
                FileOps.OpResult r = FileOps.ValidateName(n);
                Check(r.Status == FileOps.OpStatus.ReservedName,
                    "★保留名「" + n + "」拒绝", r.Status.ToString());
            }

            Check(FileOps.ValidateName("COM0.txt").Ok, "COM0 不是保留名（允许）");
            Check(FileOps.ValidateName("CONSOLE.txt").Ok, "CONSOLE 不是保留名（允许）");

            Check(FileOps.ValidateName(new string('a', 255)).Ok, "255 字符允许");
            Check(FileOps.ValidateName(new string('a', 256)).Status == FileOps.OpStatus.NameTooLong,
                "★256 字符拒绝");

            Console.WriteLine();
        }

        // ---------- 2. 重命名成功 ----------
        static void TestRenameSuccess()
        {
            Console.WriteLine("--- 2. 重命名成功 ---");

            FakeFs fs = new FakeFs();
            fs.AddFile(@"C:\work\old.txt");
            Swap(fs);

            FileOps.OpResult r = FileOps.Rename(@"C:\work\old.txt", "new.txt", @"C:\work");
            Check(r.Ok, "★重命名成功", r.Message);
            Check(r.Path == @"C:\work\new.txt", "返回新路径", r.Path);
            Check(!fs.Exists(@"C:\work\old.txt"), "旧路径不存在");
            Check(fs.Exists(@"C:\work\new.txt"), "新路径存在");
            Check(fs.MoveCount == 1, "只调了 1 次移动", Convert.ToString(fs.MoveCount));

            // 目录重命名
            fs = new FakeFs();
            fs.AddDir(@"C:\work\olddir");
            Swap(fs);
            r = FileOps.Rename(@"C:\work\olddir", "newdir", @"C:\work");
            Check(r.Ok, "目录重命名成功", r.Message);

            Console.WriteLine();
        }

        // ---------- 3. 重命名拒绝 ----------
        static void TestRenameRejections()
        {
            Console.WriteLine("--- 3. 重命名拒绝 ---");

            FakeFs fs = new FakeFs();
            fs.AddFile(@"C:\work\a.txt");
            fs.AddFile(@"C:\work\b.txt");
            Swap(fs);

            Check(FileOps.Rename(@"C:\work\a.txt", "b.txt", @"C:\work").Status
                == FileOps.OpStatus.AlreadyExists, "★同名拒绝");
            Check(fs.Exists(@"C:\work\a.txt"), "★拒绝后原文件仍在");
            Check(fs.Exists(@"C:\work\b.txt"), "★拒绝后目标文件未被覆盖");

            // 大小写不敏感的同名
            Check(FileOps.Rename(@"C:\work\a.txt", "B.TXT", @"C:\work").Status
                == FileOps.OpStatus.AlreadyExists, "★大小写不敏感的同名也拒绝");

            Check(FileOps.Rename(@"C:\work\a.txt", "", @"C:\work").Status
                == FileOps.OpStatus.EmptyName, "空名拒绝");
            Check(FileOps.Rename(@"C:\work\a.txt", "x:y.txt", @"C:\work").Status
                == FileOps.OpStatus.InvalidChars, "非法字符拒绝");
            Check(FileOps.Rename(@"C:\work\a.txt", "CON", @"C:\work").Status
                == FileOps.OpStatus.ReservedName, "保留名拒绝");

            Check(FileOps.Rename(@"C:\work\nothere.txt", "x.txt", @"C:\work").Status
                == FileOps.OpStatus.NotFound, "不存在的路径拒绝");

            // 名称没变
            Check(FileOps.Rename(@"C:\work\a.txt", "a.txt", @"C:\work").Status
                == FileOps.OpStatus.AlreadyExists, "名称没变 → AlreadyExists");

            Console.WriteLine();
        }

        // ---------- 4. 仅大小写变化 ----------
        static void TestRenameCaseOnly()
        {
            Console.WriteLine("--- 4. 仅大小写变化 ---");

            FakeFs fs = new FakeFs();
            fs.AddFile(@"C:\work\abc.txt");
            Swap(fs);

            FileOps.OpResult r = FileOps.Rename(@"C:\work\abc.txt", "ABC.txt", @"C:\work");
            Check(r.Ok, "★仅大小写变化允许", r.Message);
            Check(fs.Exists(@"C:\work\ABC.txt"), "新名字存在", r.Path);
            // 走的是两步（临时名 → 目标名）
            Check(fs.MoveCount == 2, "★走两步移动（临时名中转）", Convert.ToString(fs.MoveCount));

            Console.WriteLine();
        }

        // ---------- 5. 来源文件夹本身不可改 ----------
        static void TestRenameSourceRoot()
        {
            Console.WriteLine("--- 5. 来源文件夹本身不可修改 ---");

            FakeFs fs = new FakeFs();
            fs.AddDir(@"D:\Projects");
            fs.AddFile(@"D:\Projects\a.txt");
            Swap(fs);

            // 对来源根本身重命名 → 拒绝
            Check(FileOps.Rename(@"D:\Projects", "Projects2", @"D:\Projects").Status
                == FileOps.OpStatus.SourceRoot, "★来源根重命名拒绝");

            // 来源根里的文件 → 允许
            Check(FileOps.Rename(@"D:\Projects\a.txt", "b.txt", @"D:\Projects").Ok,
                "★来源根里的文件可重命名");

            // 删除来源根 → 拒绝
            Check(FileOps.Delete(new List<string> { @"D:\Projects" }, @"D:\Projects", IntPtr.Zero).Status
                == FileOps.OpStatus.SourceRoot, "★来源根删除拒绝");

            // 剪切来源根 → 拒绝
            Check(FileOps.Cut(new List<string> { @"D:\Projects" }, @"D:\Projects").Status
                == FileOps.OpStatus.SourceRoot, "★来源根剪切拒绝");

            Console.WriteLine();
        }

        // ---------- 6. 删除走回收站 ----------
        static void TestDeleteToRecycleBin()
        {
            Console.WriteLine("--- 6. 删除走回收站 ---");

            FakeFs fs = new FakeFs();
            fs.AddFile(@"C:\work\a.txt");
            fs.AddFile(@"C:\work\b.txt");
            Swap(fs);

            FileOps.OpResult r = FileOps.Delete(
                new List<string> { @"C:\work\a.txt" }, @"C:\work", IntPtr.Zero);
            Check(r.Ok, "★删除成功", r.Message);
            Check(r.Count == 1, "处理 1 项", Convert.ToString(r.Count));
            Check(fs.RecycleCalls == 1, "★走了回收站接口", Convert.ToString(fs.RecycleCalls));
            Check(fs.HardDeleteCalls == 0, "★没有硬删除", Convert.ToString(fs.HardDeleteCalls));

            // 批量
            fs = new FakeFs();
            fs.AddFile(@"C:\work\a.txt");
            fs.AddFile(@"C:\work\b.txt");
            fs.AddFile(@"C:\work\c.txt");
            Swap(fs);

            r = FileOps.Delete(new List<string> {
                @"C:\work\a.txt", @"C:\work\b.txt", @"C:\work\c.txt" },
                @"C:\work", IntPtr.Zero);
            Check(r.Ok, "批量删除成功", r.Message);
            Check(r.Count == 3, "★批量 3 项", Convert.ToString(r.Count));
            Check(fs.RecycleCalls == 1, "★批量只调 1 次回收站（不是 3 次）",
                Convert.ToString(fs.RecycleCalls));

            // 空列表
            Check(FileOps.Delete(new List<string>(), @"C:\work", IntPtr.Zero).Status
                == FileOps.OpStatus.Invalid, "空列表拒绝");
            Check(FileOps.Delete(null, @"C:\work", IntPtr.Zero).Status
                == FileOps.OpStatus.Invalid, "null 拒绝");

            Console.WriteLine();
        }

        // ---------- 7. 可移动盘拒绝删除 ----------
        static void TestDeleteRemovableRejected()
        {
            Console.WriteLine("--- 7. 可移动盘 / 网络盘拒绝删除 ---");

            FakeFs fs = new FakeFs();
            fs.AddFile(@"E:\usb\a.txt");
            fs.SetDriveType(@"E:\", DriveType.Removable);
            Swap(fs);

            FileOps.OpResult r = FileOps.Delete(
                new List<string> { @"E:\usb\a.txt" }, @"E:\usb", IntPtr.Zero);
            Check(r.Status == FileOps.OpStatus.NoRecycleBin, "★可移动盘拒绝删除",
                r.Status.ToString());
            Check(r.Message.IndexOf("回收站") >= 0, "★提示提到回收站", r.Message);
            Check(fs.RecycleCalls == 0, "没有调用回收站");
            Check(fs.Exists(@"E:\usb\a.txt"), "★文件仍在");

            // 网络盘
            fs = new FakeFs();
            fs.AddFile(@"Z:\share\a.txt");
            fs.SetDriveType(@"Z:\", DriveType.Network);
            Swap(fs);

            r = FileOps.Delete(new List<string> { @"Z:\share\a.txt" }, @"Z:\share", IntPtr.Zero);
            Check(r.Status == FileOps.OpStatus.NoRecycleBin, "★网络盘拒绝删除");

            Console.WriteLine();
        }

        // ---------- 8. 受保护目录拒绝 ----------
        static void TestDeleteProtected()
        {
            Console.WriteLine("--- 8. 受保护目录拒绝 ---");

            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(win)) { Console.WriteLine("  (跳过：读不到 Windows 目录)"); Console.WriteLine(); return; }

            FakeFs fs = new FakeFs();
            string target = Path.Combine(win, "System32", "test.dll");
            fs.AddFile(target);
            Swap(fs);

            FileOps.OpResult r = FileOps.Delete(
                new List<string> { target }, Path.Combine(win, "System32"), IntPtr.Zero);
            Check(r.Status == FileOps.OpStatus.Protected, "★Windows 下的文件拒绝删除",
                r.Status.ToString());

            r = FileOps.Rename(target, "test2.dll", Path.Combine(win, "System32"));
            Check(r.Status == FileOps.OpStatus.Protected, "★Windows 下的文件拒绝重命名");

            Console.WriteLine();
        }

        // ---------- 9. 受保护判定精确性 ----------
        static void TestProtectedPrecision()
        {
            Console.WriteLine("--- 9. 受保护判定精确性（不误伤用户文件夹）---");

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
            {
                Check(FileOps.IsProtected(profile), "★用户文件夹根受保护");

                // 其下子目录不受保护
                Check(!FileOps.IsProtected(Path.Combine(profile, "Desktop")),
                    "★用户 Desktop 不受保护（可修改）");
                Check(!FileOps.IsProtected(Path.Combine(profile, "Documents")),
                    "★用户 Documents 不受保护（可修改）");
                Check(!FileOps.IsProtected(Path.Combine(profile, "Projects")),
                    "★用户 Projects 不受保护");
                Check(!FileOps.IsProtected(Path.Combine(profile, "Desktop", "sub")),
                    "用户 Desktop 的子目录也不受保护");
            }

            // 驱动器根：精确匹配
            Check(FileOps.IsProtected(@"C:\"), "★C:\\ 受保护");
            Check(FileOps.IsProtected(@"C:"), "★C: 受保护");
            Check(!FileOps.IsProtected(@"C:\Users"), "★C:\\Users 不受保护（不是根）");
            Check(!FileOps.IsProtected(@"C:\Temp"), "C:\\Temp 不受保护");

            // 系统目录：整棵树
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(win))
            {
                Check(FileOps.IsProtected(win), "★Windows 根受保护");
                Check(FileOps.IsProtected(Path.Combine(win, "System32")),
                    "★Windows\\System32 受保护");
                Check(FileOps.IsProtected(Path.Combine(win, "System32", "drivers")),
                    "★深层子目录也受保护");
            }

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(pf))
            {
                Check(FileOps.IsProtected(pf), "★Program Files 受保护");
                Check(FileOps.IsProtected(Path.Combine(pf, "SomeApp")),
                    "★Program Files 子目录受保护");
            }

            // 前缀匹配不得误伤：C:\WindowsApps 不是 C:\Windows
            if (!string.IsNullOrEmpty(win))
            {
                Check(!FileOps.IsProtected(win + "Apps"),
                    "★前缀相似但不同名 → 不受保护（C:\\WindowsApps）");
            }

            Console.WriteLine();
        }

        // ---------- 10. 剪切 ----------
        static void TestCut()
        {
            Console.WriteLine("--- 10. 剪切 ---");

            FakeFs fs = new FakeFs();
            fs.AddFile(@"C:\work\a.txt");
            fs.AddFile(@"C:\work\b.txt");
            Swap(fs);

            FileOps.OpResult r = FileOps.Cut(
                new List<string> { @"C:\work\a.txt", @"C:\work\b.txt" }, @"C:\work");
            Check(r.Ok, "★剪切成功", r.Message);
            Check(r.Count == 2, "2 项", Convert.ToString(r.Count));
            Check(fs.MoveCount == 0, "★剪切不移动任何文件（只放剪贴板）",
                Convert.ToString(fs.MoveCount));
            Check(fs.Exists(@"C:\work\a.txt"), "★文件仍在原位");

            Check(FileOps.Cut(new List<string>(), @"C:\work").Status
                == FileOps.OpStatus.Invalid, "空列表拒绝");
            Check(FileOps.Cut(new List<string> { @"C:\work\nothere" }, @"C:\work").Status
                == FileOps.OpStatus.NotFound, "不存在的路径拒绝");

            Console.WriteLine();
        }

        // ---------- 11. 粘贴 ----------
        static void TestPaste()
        {
            Console.WriteLine("--- 11. 粘贴 ---");

            FakeFs fs = new FakeFs();
            fs.AddFile(@"C:\work\a.txt");
            fs.AddDir(@"C:\dest");
            Swap(fs);

            FileOps.OpResult r = FileOps.Paste(
                new List<string> { @"C:\work\a.txt" }, @"C:\dest", IntPtr.Zero);
            Check(r.Ok, "★粘贴成功", r.Message);
            Check(r.Count == 1, "1 项", Convert.ToString(r.Count));
            Check(fs.Exists(@"C:\dest\a.txt"), "★目标位置有文件");
            Check(!fs.Exists(@"C:\work\a.txt"), "★源文件已移走");

            // 目标不存在
            Check(FileOps.Paste(new List<string> { @"C:\work\a.txt" }, @"C:\nodest", IntPtr.Zero).Status
                == FileOps.OpStatus.NotFound, "目标不存在拒绝");

            // 空列表
            Check(FileOps.Paste(new List<string>(), @"C:\dest", IntPtr.Zero).Status
                == FileOps.OpStatus.Invalid, "空列表拒绝");

            Console.WriteLine();
        }

        // ---------- 12. 粘贴不覆盖 ----------
        static void TestPasteNoOverwrite()
        {
            Console.WriteLine("--- 12. 粘贴不覆盖 ---");

            FakeFs fs = new FakeFs();
            fs.AddFile(@"C:\work\a.txt", "SRC");
            fs.AddDir(@"C:\dest");
            fs.AddFile(@"C:\dest\a.txt", "EXISTING");
            Swap(fs);

            FileOps.OpResult r = FileOps.Paste(
                new List<string> { @"C:\work\a.txt" }, @"C:\dest", IntPtr.Zero);
            Check(r.Status == FileOps.OpStatus.AlreadyExists, "★目标已存在拒绝粘贴",
                r.Status.ToString());
            Check(fs.MoveCount == 0, "★没有执行任何移动");
            Check(fs.Exists(@"C:\work\a.txt"), "★源文件仍在");
            Check(fs.GetContent(@"C:\dest\a.txt") == "EXISTING", "★目标文件内容未被覆盖");

            Console.WriteLine();
        }

        // ---------- 13. 禁止粘贴到自身子目录 ----------
        static void TestPasteIntoItself()
        {
            Console.WriteLine("--- 13. 禁止粘贴到自身子目录 ---");

            FakeFs fs = new FakeFs();
            fs.AddDir(@"C:\work\proj");
            fs.AddDir(@"C:\work\proj\sub");
            Swap(fs);

            FileOps.OpResult r = FileOps.Paste(
                new List<string> { @"C:\work\proj" }, @"C:\work\proj\sub", IntPtr.Zero);
            Check(r.Status == FileOps.OpStatus.IntoItself, "★粘贴到自身子目录拒绝",
                r.Status.ToString());
            Check(fs.MoveCount == 0, "没有执行移动");

            // 粘贴到自己的父目录（同一位置）→ AlreadyExists
            r = FileOps.Paste(new List<string> { @"C:\work\proj" }, @"C:\work", IntPtr.Zero);
            Check(r.Status == FileOps.OpStatus.AlreadyExists, "★粘贴到原位置拒绝",
                r.Status.ToString());

            Console.WriteLine();
        }

        // ---------- 14. 同卷 vs 跨卷 ----------
        static void TestPasteSameVolume()
        {
            Console.WriteLine("--- 14. 同卷 / 跨卷 ---");

            // 同卷
            FakeFs fs = new FakeFs();
            fs.AddFile(@"C:\work\a.txt");
            fs.AddDir(@"C:\dest");
            Swap(fs);

            FileOps.Paste(new List<string> { @"C:\work\a.txt" }, @"C:\dest", IntPtr.Zero);
            Check(fs.SameVolumeMoveCount == 1, "★同卷走 MoveWithinVolume",
                Convert.ToString(fs.SameVolumeMoveCount));
            Check(fs.CrossVolumeMoveCount == 0, "同卷不走跨卷接口");

            // 跨卷
            fs = new FakeFs();
            fs.AddFile(@"C:\work\a.txt");
            fs.AddDir(@"D:\dest");
            Swap(fs);

            FileOps.Paste(new List<string> { @"C:\work\a.txt" }, @"D:\dest", IntPtr.Zero);
            Check(fs.CrossVolumeMoveCount == 1, "★跨卷走 MoveAcrossVolume（系统文件操作）",
                Convert.ToString(fs.CrossVolumeMoveCount));
            Check(fs.SameVolumeMoveCount == 0, "跨卷不走同卷接口");
            Check(fs.CopyCalls == 0, "★不自己复制再删除", Convert.ToString(fs.CopyCalls));

            Console.WriteLine();
        }

        // ---------- 15. 新建文件夹 ----------
        static void TestNewFolder()
        {
            Console.WriteLine("--- 15. 新建文件夹 ---");

            FakeFs fs = new FakeFs();
            fs.AddDir(@"C:\work");
            Swap(fs);

            FileOps.OpResult r = FileOps.NewFolder(@"C:\work");
            Check(r.Ok, "★新建成功", r.Message);
            Check(r.Path == @"C:\work\新建文件夹", "默认名「新建文件夹」", r.Path);
            Check(fs.Exists(@"C:\work\新建文件夹"), "目录已创建");

            // 重名加序号
            r = FileOps.NewFolder(@"C:\work");
            Check(r.Path == @"C:\work\新建文件夹 (2)", "★重名加 (2)", r.Path);

            r = FileOps.NewFolder(@"C:\work");
            Check(r.Path == @"C:\work\新建文件夹 (3)", "★再重名加 (3)", r.Path);

            // 目标不存在
            Check(FileOps.NewFolder(@"C:\nodest").Status == FileOps.OpStatus.NotFound,
                "目标不存在拒绝");

            // 受保护
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(win))
            {
                Check(FileOps.NewFolder(win).Status == FileOps.OpStatus.Protected,
                    "★受保护目录拒绝新建");
            }

            Console.WriteLine();
        }

        // ---------- 16. 失败不重试且不删原文件 ----------
        static void TestFailureNoRetry()
        {
            Console.WriteLine("--- 16. 失败不重试、不删原文件 ---");

            FakeFs fs = new FakeFs();
            fs.AddFile(@"C:\work\a.txt");
            fs.FailMoves = true;   // 所有移动都失败
            Swap(fs);

            FileOps.OpResult r = FileOps.Rename(@"C:\work\a.txt", "b.txt", @"C:\work");
            Check(!r.Ok, "★失败时返回失败", r.Status.ToString());
            Check(fs.MoveAttempts == 1, "★只尝试 1 次（不重试）",
                Convert.ToString(fs.MoveAttempts));
            Check(fs.Exists(@"C:\work\a.txt"), "★原文件仍在（没被删）");
            Check(!fs.Exists(@"C:\work\b.txt"), "目标未创建");

            // 删除失败
            fs = new FakeFs();
            fs.AddFile(@"C:\work\a.txt");
            fs.FailRecycle = true;
            Swap(fs);

            r = FileOps.Delete(new List<string> { @"C:\work\a.txt" }, @"C:\work", IntPtr.Zero);
            Check(!r.Ok, "★删除失败返回失败");
            Check(fs.RecycleCalls == 1, "★回收站只调 1 次（不重试）",
                Convert.ToString(fs.RecycleCalls));
            Check(fs.Exists(@"C:\work\a.txt"), "★原文件仍在");

            Console.WriteLine();
        }

        // ---------- 17. 日志追加与裁剪 ----------
        static void TestLogAppendAndTrim()
        {
            Console.WriteLine("--- 17. 日志追加与裁剪 ---");

            string root = NewRoot();
            try
            {
                FileOps.Init(root);

                FakeFs fs = new FakeFs();
                fs.AddFile(@"C:\work\a.txt");
                Swap(fs);

                // 做几次操作
                for (int i = 0; i < 5; i++)
                {
                    fs.AddFile(@"C:\work\f" + i + ".txt");
                    FileOps.Rename(@"C:\work\f" + i + ".txt", "g" + i + ".txt", @"C:\work");
                }

                int n = FileOps.LogCount();
                Check(n >= 5, "★日志已追加（" + n + " 条）", Convert.ToString(n));

                string logFile = Path.Combine(root, "config", "file-ops-log.json");
                Check(File.Exists(logFile), "日志文件已创建");

                string json = File.ReadAllText(logFile, Encoding.UTF8);
                Check(json.IndexOf("\"ops\"", StringComparison.Ordinal) >= 0, "有 ops 数组");
                Check(json.IndexOf("\"time\"", StringComparison.Ordinal) >= 0, "记录含时间");
                Check(json.IndexOf("\"op\"", StringComparison.Ordinal) >= 0, "记录含操作");
                Check(json.IndexOf("\"path\"", StringComparison.Ordinal) >= 0, "记录含路径");
                Check(json.IndexOf("\"result\"", StringComparison.Ordinal) >= 0, "记录含结果");

                // 裁剪：写超过上限的条目
                FileOps.Init(root);
                // 直接构造 1000+ 条日志
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"ops\":[");
                for (int i = 0; i < FileOps.MaxLogEntries + 50; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"time\":\"2026-01-01 00:00:00\",\"op\":\"test\",\"path\":\"p");
                    sb.Append(i);
                    sb.Append("\",\"result\":\"ok\"}");
                }
                sb.Append("]}");
                File.WriteAllText(logFile, sb.ToString(), new UTF8Encoding(false));

                Check(FileOps.LogCount() == FileOps.MaxLogEntries + 50,
                    "构造了 " + (FileOps.MaxLogEntries + 50) + " 条",
                    Convert.ToString(FileOps.LogCount()));

                // 再写一条 → 应裁到 1000
                fs.AddFile(@"C:\work\z.txt");
                FileOps.Rename(@"C:\work\z.txt", "z2.txt", @"C:\work");

                int after = FileOps.LogCount();
                Check(after == FileOps.MaxLogEntries,
                    "★超过 " + FileOps.MaxLogEntries + " 条自动裁剪到上限",
                    Convert.ToString(after));

                // 裁剪的是最旧的：最后一条应该是新写的
                string afterJson = File.ReadAllText(logFile, Encoding.UTF8);
                Check(afterJson.IndexOf("\"op\":\"rename\"", StringComparison.Ordinal) >= 0,
                    "★最新的记录保留");
                Check(afterJson.IndexOf("\"p0\"", StringComparison.Ordinal) < 0,
                    "★最旧的记录被裁掉");
            }
            finally { Clean(root); }

            // 日志写失败不影响操作
            FileOps.Init(@"Z:\definitely-not-writable-xyz");
            FakeFs fs2 = new FakeFs();
            fs2.AddFile(@"C:\work\q.txt");
            Swap(fs2);
            FileOps.OpResult r = FileOps.Rename(@"C:\work\q.txt", "q2.txt", @"C:\work");
            Check(r.Ok, "★日志写失败时操作仍成功", r.Message);

            Console.WriteLine();
        }

        // ---------- 22. 提权（Elevation）----------
        static void TestElevation()
        {
            Console.WriteLine("--- 22. 提权工具 ---");

            // ---- IsAccessDenied ----
            Check(Elevation.IsAccessDenied(new UnauthorizedAccessException()),
                "★UnauthorizedAccessException → 权限不足");
            Check(Elevation.IsAccessDenied(
                new System.ComponentModel.Win32Exception(5)),
                "★Win32Exception(5=拒绝访问) → 权限不足");
            Check(Elevation.IsAccessDenied(
                new System.ComponentModel.Win32Exception(1314)),
                "★Win32Exception(1314=权限未持有) → 权限不足");
            Check(!Elevation.IsAccessDenied(
                new System.ComponentModel.Win32Exception(2)),
                "Win32Exception(2=文件不存在) → 不是权限问题");
            Check(!Elevation.IsAccessDenied(new InvalidOperationException()),
                "InvalidOperationException → 不是权限问题");
            Check(!Elevation.IsAccessDenied(null), "null → false");

            // 内层异常
            Check(Elevation.IsAccessDenied(
                new Exception("outer", new UnauthorizedAccessException())),
                "★内层是 UnauthorizedAccessException → 权限不足");

            // ---- IsUserCancelled ----
            Check(Elevation.IsUserCancelled(
                new System.ComponentModel.Win32Exception(1223)),
                "★Win32Exception(1223=UAC取消) → 用户取消");
            Check(!Elevation.IsUserCancelled(
                new System.ComponentModel.Win32Exception(5)),
                "Win32Exception(5) → 不是用户取消");
            Check(!Elevation.IsUserCancelled(new Exception("x")),
                "普通异常 → 不是用户取消");

            // ---- QuoteCmdArg 转义 ----
            Check(Elevation.QuoteCmdArg(@"C:\a.txt") == "\"C:\\a.txt\"",
                "★普通路径加引号", Elevation.QuoteCmdArg(@"C:\a.txt"));

            string withSpace = Elevation.QuoteCmdArg(@"C:\a b\x.txt");
            Check(withSpace == "\"C:\\a b\\x.txt\"",
                "★含空格路径加引号", withSpace);

            // 末尾反斜杠必须加倍，否则 "C:\dir\" 的 \" 会被当转义引号
            string trailing = Elevation.QuoteCmdArg(@"C:\dir\");
            Check(trailing.EndsWith("\\\\\""), "★末尾反斜杠加倍", trailing);
            Check(trailing == "\"C:\\dir\\\\\"", "★末尾反斜杠结果精确", trailing);

            string quoteInside = Elevation.QuoteCmdArg("C:\\a\"b\\x.txt");
            Check(quoteInside.Contains("\\\""), "★内部引号转义", quoteInside);

            string chinese = Elevation.QuoteCmdArg(@"D:\中文 目录\x.txt");
            Check(chinese == "\"D:\\中文 目录\\x.txt\"",
                "★中文 + 空格路径", chinese);

            Check(Elevation.QuoteCmdArg(null) == "\"\"", "null → 空引号");
            Check(Elevation.QuoteCmdArg("") == "\"\"", "空串 → 空引号");

            // ---- RunAs 对不存在的路径返回 false（不抛）----
            bool threw = false;
            bool r1 = false;
            try { r1 = Elevation.RunAs(@"Z:\no-such-app-xyz.exe", null); }
            catch { threw = true; }
            Check(!threw, "★RunAs 不存在路径不抛异常");
            Check(!r1, "★RunAs 不存在路径返回 false");

            // ---- DeleteAsAdmin 对不存在的路径返回 false（不抛）----
            threw = false;
            bool r2 = false;
            string err = null;
            try { r2 = Elevation.DeleteAsAdmin(@"Z:\no-such-file-xyz.txt", out err); }
            catch { threw = true; }
            Check(!threw, "★DeleteAsAdmin 不存在路径不抛异常");
            Check(!r2, "★DeleteAsAdmin 不存在路径返回 false");

            Console.WriteLine();
        }


        /// <summary>内存文件系统：模拟存在性、卷、驱动器类型，不碰真实磁盘。</summary>
        class FakeFs : FileOps.IFileSystem
        {
            readonly HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string, string> contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string, DriveType> driveTypes = new Dictionary<string, DriveType>(StringComparer.OrdinalIgnoreCase);

            public int MoveCount;
            public int MoveAttempts;
            public int SameVolumeMoveCount;
            public int CrossVolumeMoveCount;
            public int RecycleCalls;
            public int HardDeleteCalls;
            public int CopyCalls;

            public bool FailMoves;
            public bool FailRecycle;

            public void AddFile(string path) { AddFile(path, "x"); }
            public void AddFile(string path, string content)
            {
                paths.Add(Norm(path));
                contents[Norm(path)] = content;
                // 补父目录
                string d = Path.GetDirectoryName(Norm(path));
                while (!string.IsNullOrEmpty(d) && d.Length > 3)
                {
                    paths.Add(d);
                    d = Path.GetDirectoryName(d);
                }
            }
            public void AddDir(string path)
            {
                paths.Add(Norm(path));
                string d = Path.GetDirectoryName(Norm(path));
                while (!string.IsNullOrEmpty(d) && d.Length > 3)
                {
                    paths.Add(d);
                    d = Path.GetDirectoryName(d);
                }
            }
            public void SetDriveType(string root, DriveType t) { driveTypes[Norm(root)] = t; }

            public string GetContent(string path)
            {
                string c;
                return contents.TryGetValue(Norm(path), out c) ? c : null;
            }

            public bool Exists(string path) { return paths.Contains(Norm(path)); }

            static string Norm(string p)
            {
                if (string.IsNullOrEmpty(p)) return "";
                return p.Replace('/', '\\').TrimEnd('\\').Length == 2
                    ? p.Replace('/', '\\').TrimEnd('\\') + "\\"
                    : p.Replace('/', '\\').TrimEnd('\\');
            }

            public bool DeleteToRecycleBin(List<string> list, IntPtr hwnd, out string error)
            {
                error = null;
                RecycleCalls++;
                if (FailRecycle) { error = "模拟失败"; return false; }
                foreach (string p in list) paths.Remove(Norm(p));
                return true;
            }

            public bool MoveWithinVolume(string src, string dst, out string error)
            {
                error = null;
                MoveAttempts++;
                if (FailMoves) { error = "模拟移动失败"; return false; }
                MoveCount++;
                SameVolumeMoveCount++;
                if (!paths.Contains(Norm(src))) { error = "源不存在"; return false; }
                paths.Remove(Norm(src));
                paths.Add(Norm(dst));
                return true;
            }

            public bool MoveAcrossVolume(string src, string dst, IntPtr hwnd, out string error)
            {
                error = null;
                MoveAttempts++;
                if (FailMoves) { error = "模拟移动失败"; return false; }
                MoveCount++;
                CrossVolumeMoveCount++;
                if (!paths.Contains(Norm(src))) { error = "源不存在"; return false; }
                paths.Remove(Norm(src));
                paths.Add(Norm(dst));
                return true;
            }

            public bool CreateDirectory(string path, out string error)
            {
                error = null;
                AddDir(path);
                return true;
            }

            public bool FileExists(string path) { return paths.Contains(Norm(path)); }
            public bool DirectoryExists(string path) { return paths.Contains(Norm(path)); }
            public bool PathExists(string path) { return paths.Contains(Norm(path)); }

            public bool IsSameVolume(string a, string b)
            {
                return string.Equals(VolumeRoot(a), VolumeRoot(b), StringComparison.OrdinalIgnoreCase);
            }

            public string VolumeRoot(string path)
            {
                if (string.IsNullOrEmpty(path)) return "";
                string n = Norm(path);
                if (n.Length >= 2 && n[1] == ':') return n.Substring(0, 2) + "\\";
                return "";
            }

            public DriveType GetDriveType(string volumeRoot)
            {
                DriveType t;
                if (driveTypes.TryGetValue(Norm(volumeRoot), out t)) return t;
                return DriveType.Fixed;
            }
        }

        // ---------- 18. IsSourceRoot / IsInside 直接断言 ----------
        static void TestSourceRootAndInside()
        {
            Console.WriteLine("--- 18. IsSourceRoot / IsInside ---");

            // ---- IsSourceRoot：完全相同 ----
            Check(FileOps.IsSourceRoot(@"C:\work", @"C:\work"), "★IsSourceRoot 完全相同");
            Check(FileOps.IsSourceRoot(@"C:\work\", @"C:\work"), "★IsSourceRoot 尾部斜杠不同仍相等");
            Check(FileOps.IsSourceRoot(@"C:\work", @"C:\work\"), "★IsSourceRoot 反向尾斜杠");

            // ---- IsSourceRoot：大小写 ----
            Check(FileOps.IsSourceRoot(@"C:\WORK", @"C:\work"), "★IsSourceRoot 大小写不敏感");
            Check(FileOps.IsSourceRoot(@"c:\WoRk", @"C:\work"), "★IsSourceRoot 混合大小写");

            // ---- IsSourceRoot：不同路径 ----
            Check(!FileOps.IsSourceRoot(@"C:\work\sub", @"C:\work"),
                "★IsSourceRoot 子目录不等于根");
            Check(!FileOps.IsSourceRoot(@"C:\work2", @"C:\work"),
                "★IsSourceRoot 相似前缀不算（C:\\work2 vs C:\\work）");
            Check(!FileOps.IsSourceRoot(@"C:\other", @"C:\work"), "IsSourceRoot 完全不同的路径");

            // ---- IsSourceRoot：空 / 非法 ----
            Check(!FileOps.IsSourceRoot(null, @"C:\work"), "★IsSourceRoot null 路径 → false");
            Check(!FileOps.IsSourceRoot(@"C:\work", null), "★IsSourceRoot null 根 → false");
            Check(!FileOps.IsSourceRoot("", @"C:\work"), "IsSourceRoot 空路径 → false");
            Check(!FileOps.IsSourceRoot("   ", @"C:\work"), "IsSourceRoot 空白 → false");

            // ---- IsInside：父子 ----
            Check(FileOps.IsInside(@"C:\work\sub", @"C:\work"), "★IsInside 直接子目录");
            Check(FileOps.IsInside(@"C:\work\a\b\c", @"C:\work"), "★IsInside 深层子目录");
            Check(FileOps.IsInside(@"C:\work", @"C:\work"), "★IsInside 相同路径视为包含");
            Check(FileOps.IsInside(@"C:\work\", @"C:\work"), "IsInside 尾斜杠不同");

            // ---- IsInside：大小写 ----
            Check(FileOps.IsInside(@"C:\WORK\SUB", @"C:\work"), "★IsInside 大小写不敏感");

            // ---- IsInside：非父子 ----
            Check(!FileOps.IsInside(@"C:\work", @"C:\work\sub"),
                "★IsInside 父不在子内（反向）");
            Check(!FileOps.IsInside(@"C:\workshop", @"C:\work"),
                "★IsInside 相似前缀不算（C:\\workshop vs C:\\work）");
            Check(!FileOps.IsInside(@"C:\other\sub", @"C:\work"), "IsInside 完全无关路径");
            Check(!FileOps.IsInside(@"C:\work2\sub", @"C:\work"),
                "IsInside 兄弟目录（C:\\work2 vs C:\\work）");

            // ---- IsInside：空 / 非法 ----
            Check(!FileOps.IsInside(null, @"C:\work"), "★IsInside null 子 → false");
            Check(!FileOps.IsInside(@"C:\work\sub", null), "★IsInside null 父 → false");
            Check(!FileOps.IsInside("", @"C:\work"), "IsInside 空子 → false");
            Check(!FileOps.IsInside(@"C:\work\sub", ""), "IsInside 空父 → false");
            // 空白串不 Trim：两个相同空白串按"相等"处理（返回 true）。
            // 这不是缺陷 —— 调用方传入的都是真实路径，空白串在更上层就被拒了。
            Check(FileOps.IsInside("   ", "   "), "IsInside 相同空白串视为相等");

            Console.WriteLine();
        }

        // ---------- 19. SHFILEOPSTRUCT 结构体布局 ----------
        static void TestStructLayout()
        {
            Console.WriteLine("--- 19. SHFILEOPSTRUCT 结构体布局 ---");

            // 通过反射拿到私有嵌套类型（生产代码里它是 private）
            Type t = typeof(FileOps.RealFileSystem).GetNestedType("SHFILEOPSTRUCT",
                System.Reflection.BindingFlags.NonPublic);
            Check(t != null, "★SHFILEOPSTRUCT 类型存在");
            if (t == null) { Console.WriteLine(); return; }

            // ---- 字段顺序与类型（SHFILEOPSTRUCTW 的官方布局）----
            System.Reflection.FieldInfo[] fields = t.GetFields(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);

            string[] expectNames = { "hwnd", "wFunc", "pFrom", "pTo", "fFlags",
                                     "fAnyOperationsAborted", "hNameMappings", "lpszProgressTitle" };
            Check(fields.Length == expectNames.Length,
                "★字段数 = 8", Convert.ToString(fields.Length));

            for (int i = 0; i < expectNames.Length && i < fields.Length; i++)
            {
                Check(fields[i].Name == expectNames[i],
                    "★字段 " + i + " 名称 = " + expectNames[i],
                    fields[i].Name);
            }

            // ---- 关键字段类型 ----
            if (fields.Length >= 8)
            {
                Check(fields[0].FieldType == typeof(IntPtr), "hwnd 是 IntPtr", fields[0].FieldType.Name);
                Check(fields[1].FieldType == typeof(uint), "wFunc 是 uint", fields[1].FieldType.Name);
                Check(fields[2].FieldType == typeof(string), "pFrom 是 string", fields[2].FieldType.Name);
                Check(fields[3].FieldType == typeof(string), "pTo 是 string", fields[3].FieldType.Name);
                Check(fields[4].FieldType == typeof(ushort), "fFlags 是 ushort（FILEOP_FLAGS）",
                    fields[4].FieldType.Name);
                Check(fields[5].FieldType == typeof(bool), "fAnyOperationsAborted 是 bool",
                    fields[5].FieldType.Name);
                Check(fields[6].FieldType == typeof(IntPtr), "hNameMappings 是 IntPtr",
                    fields[6].FieldType.Name);
                Check(fields[7].FieldType == typeof(string), "lpszProgressTitle 是 string",
                    fields[7].FieldType.Name);
            }

            // ---- CharSet.Unicode（决定 pFrom/pTo 按 UTF-16 封送）----
            StructLayoutAttribute sla = t.StructLayoutAttribute;
            Check(sla != null, "★有 StructLayout 特性");
            if (sla != null)
            {
                Check(sla.CharSet == CharSet.Unicode, "★CharSet = Unicode",
                    sla.CharSet.ToString());
                Check(sla.Value == LayoutKind.Sequential, "★LayoutKind = Sequential",
                    sla.Value.ToString());
            }

            // ---- 大小合理性（x64 下约 48-56 字节；只断言范围，避免架构耦合）----
            int size = Marshal.SizeOf(t);
            Check(size >= 40 && size <= 64, "★结构体大小合理（40-64）",
                size + " 字节");

            Console.WriteLine();
        }

        // ---------- 20. RealFileSystem 真实 API 验证 ----------
        static void TestRealFileSystem()
        {
            Console.WriteLine("--- 20. RealFileSystem 真实 API ---");

            // ★ 安全边界：只在 Path.GetTempPath() 下建专用目录，
            //   绝不碰用户文档/桌面/下载。
            string root = Path.Combine(Path.GetTempPath(),
                "fops-real-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            FileOps.IFileSystem real = new FileOps.RealFileSystem();
            try
            {
                Directory.CreateDirectory(root);

                // ---- 20a. CreateDirectory ----
                string newDir = Path.Combine(root, "created");
                string err;
                bool ok = real.CreateDirectory(newDir, out err);
                Check(ok, "★真实 CreateDirectory 成功", err ?? "");
                Check(Directory.Exists(newDir), "★目录确实创建了");

                // ---- 20b. PathExists / FileExists / DirectoryExists ----
                Check(real.DirectoryExists(newDir), "★真实 DirectoryExists");
                Check(real.PathExists(newDir), "★真实 PathExists（目录）");
                Check(!real.FileExists(newDir), "真实 FileExists 对目录返回 false");

                string f1 = Path.Combine(root, "a.txt");
                File.WriteAllText(f1, "hello", new UTF8Encoding(false));
                Check(real.FileExists(f1), "★真实 FileExists");
                Check(real.PathExists(f1), "★真实 PathExists（文件）");

                // ---- 20c. MoveWithinVolume（同卷重命名式移动）----
                string f2 = Path.Combine(root, "b.txt");
                ok = real.MoveWithinVolume(f1, f2, out err);
                Check(ok, "★真实 MoveWithinVolume 成功", err ?? "");
                Check(!File.Exists(f1), "★源文件已消失");
                Check(File.Exists(f2), "★目标文件存在");
                Check(File.ReadAllText(f2, Encoding.UTF8) == "hello", "★内容完整");

                // ---- 20d. MoveWithinVolume（目录）----
                string d1 = Path.Combine(root, "dir1");
                string d2 = Path.Combine(root, "dir2");
                Directory.CreateDirectory(d1);
                File.WriteAllText(Path.Combine(d1, "inner.txt"), "x", new UTF8Encoding(false));
                ok = real.MoveWithinVolume(d1, d2, out err);
                Check(ok, "★真实目录移动成功", err ?? "");
                Check(Directory.Exists(d2), "★目标目录存在");
                Check(File.Exists(Path.Combine(d2, "inner.txt")), "★目录内文件跟着走了");

                // ---- 20e. IsSameVolume / VolumeRoot / GetDriveType ----
                string vr = real.VolumeRoot(root);
                Check(!string.IsNullOrEmpty(vr), "★VolumeRoot 非空", vr ?? "null");
                Check(real.IsSameVolume(root, newDir), "★同一卷 → IsSameVolume true");
                Check(real.IsSameVolume(root, root), "★相同路径 → IsSameVolume true");
                DriveType dt = real.GetDriveType(vr);
                Check(dt != DriveType.Unknown, "★GetDriveType 有有效值", dt.ToString());

                // ---- 20f. ★真实删除进回收站 ----
                //   先记录删除前的回收站项数（用 SHQueryRecycleBinW）
                string victim = Path.Combine(root, "to-recycle.txt");
                File.WriteAllText(victim, "delete-me", new UTF8Encoding(false));

                long itemsBefore, sizeBefore;
                bool canQuery = QueryRecycleBin(vr, out itemsBefore, out sizeBefore);

                ok = real.DeleteToRecycleBin(new List<string> { victim }, IntPtr.Zero, out err);
                Check(ok, "★真实 DeleteToRecycleBin 成功", err ?? "");
                Check(!File.Exists(victim), "★文件已从原位置消失");

                if (canQuery)
                {
                    long itemsAfter, sizeAfter;
                    bool ok2 = QueryRecycleBin(vr, out itemsAfter, out sizeAfter);
                    Check(ok2, "删除后可查询回收站");
                    Check(itemsAfter > itemsBefore,
                        "★回收站项数增加（证明进了回收站，不是永久删除）",
                        itemsBefore + " → " + itemsAfter);
                }
                else
                {
                    Console.WriteLine("  跳过回收站计数校验（SHQueryRecycleBin 不可用）");
                }

                // ---- 20g. 删除不存在的文件 → 失败且不抛异常 ----
                bool threw = false;
                bool r2 = false;
                try
                {
                    r2 = real.DeleteToRecycleBin(
                        new List<string> { Path.Combine(root, "no-such-file.txt") },
                        IntPtr.Zero, out err);
                }
                catch { threw = true; }
                Check(!threw, "★删除不存在的文件不抛异常");
                Check(!r2, "★删除不存在的文件返回 false", r2.ToString());

                // ---- 20h. 空列表 → 直接返回 true（无需操作）----
                Check(real.DeleteToRecycleBin(new List<string>(), IntPtr.Zero, out err),
                    "★空列表删除返回 true");

                // ---- 20i. 跨卷移动（若存在第二个盘）----
                string other = FindOtherVolume(vr);
                if (other != null)
                {
                    string src = Path.Combine(root, "cross.txt");
                    File.WriteAllText(src, "cross", new UTF8Encoding(false));
                    string otherDir = Path.Combine(other,
                        "fops-cross-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    try
                    {
                        Directory.CreateDirectory(otherDir);
                        string dst = Path.Combine(otherDir, "cross.txt");
                        ok = real.MoveAcrossVolume(src, dst, IntPtr.Zero, out err);
                        Check(ok, "★真实跨卷移动成功", err ?? "");
                        Check(!File.Exists(src), "★跨卷：源已消失");
                        Check(File.Exists(dst), "★跨卷：目标存在");
                        Check(File.ReadAllText(dst, Encoding.UTF8) == "cross", "★跨卷：内容完整");
                    }
                    finally
                    {
                        // 清理跨卷临时目录（只删自己建的那个）
                        try { if (Directory.Exists(otherDir)) Directory.Delete(otherDir, true); } catch { }
                    }
                }
                else
                {
                    Console.WriteLine("  跳过跨卷移动（本机只有一个卷）");
                }
            }
            finally
            {
                // ★ 清理：只删本次创建的 root（名字带 fops-real- 前缀 + guid）
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
                // 恢复默认实现，避免影响后续用例
                FileOps.Fs = new FileOps.RealFileSystem();
            }

            Console.WriteLine();
        }

        /// <summary>查询某卷回收站项数与大小。不可用时返回 false。</summary>
        static bool QueryRecycleBin(string volumeRoot, out long items, out long bytes)
        {
            items = 0; bytes = 0;
            try
            {
                SHQUERYRBINFO info = new SHQUERYRBINFO();
                info.cbSize = (uint)Marshal.SizeOf(typeof(SHQUERYRBINFO));
                int hr = SHQueryRecycleBin(volumeRoot, ref info);
                if (hr != 0) return false;
                items = info.i64NumItems;
                bytes = info.i64Size;
                return true;
            }
            catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct SHQUERYRBINFO
        {
            public uint cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        /// <summary>找一个与 baseVolume 不同的、可写的固定盘。没有则返回 null。</summary>
        static string FindOtherVolume(string baseVolume)
        {
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed) continue;
                        if (!d.IsReady) continue;
                        string root = d.RootDirectory.FullName;
                        if (string.Equals(root, baseVolume, StringComparison.OrdinalIgnoreCase))
                            continue;
                        return root;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }


        // ---------- 21. Reparse Point 绕过防护（R1 修复）----------
        static void TestReparsePoint()
        {
            Console.WriteLine("--- 21. Reparse Point 绕过防护 ---");

            // ★ 只在测试自己创建的临时目录里操作；只清理自己创建的对象。
            string root = Path.Combine(Path.GetTempPath(),
                "fops-rp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string junction = Path.Combine(root, "link-to-plain");
            bool junctionMade = false;

            FileOps.IFileSystem real = new FileOps.RealFileSystem();
            FileOps.IFileSystem saved = FileOps.Fs;
            try
            {
                Directory.CreateDirectory(root);

                // ---- 21a. 普通目录 / 文件 → Safe ----
                string plain = Path.Combine(root, "plain");
                Directory.CreateDirectory(plain);
                Check(FileOps.CheckPathSafety(plain) == FileOps.PathSafety.Safe,
                    "★普通目录 → Safe", FileOps.CheckPathSafety(plain).ToString());

                string plainFile = Path.Combine(plain, "a.txt");
                File.WriteAllText(plainFile, "x", new UTF8Encoding(false));
                Check(FileOps.CheckPathSafety(plainFile) == FileOps.PathSafety.Safe,
                    "★普通文件 → Safe", FileOps.CheckPathSafety(plainFile).ToString());

                // ---- 21b. Junction（指向测试自己的目录，不是系统目录）----
                junctionMade = TryMakeJunction(junction, plain);
                if (junctionMade)
                {
                    Check(FileOps.CheckPathSafety(junction) == FileOps.PathSafety.ReparsePoint,
                        "★Junction 本身 → ReparsePoint",
                        FileOps.CheckPathSafety(junction).ToString());

                    string mid = Path.Combine(junction, "deep");
                    Check(FileOps.CheckPathSafety(mid) == FileOps.PathSafety.ReparsePoint,
                        "★路径中间组件是 Junction → ReparsePoint",
                        FileOps.CheckPathSafety(mid).ToString());

                    string jf = Path.Combine(junction, "a.txt");
                    Check(FileOps.CheckPathSafety(jf) == FileOps.PathSafety.ReparsePoint,
                        "★Junction 下的文件 → ReparsePoint",
                        FileOps.CheckPathSafety(jf).ToString());

                    // ---- 21c~21h. 五个入口全部拒绝 ----
                    FileOps.Fs = real;

                    FileOps.OpResult r = FileOps.Rename(jf, "b.txt", plain);
                    Check(!r.Ok, "★Rename 拒绝 Junction 路径", r.Status.ToString());
                    Check(r.Message.IndexOf("Junction") >= 0 || r.Message.IndexOf("符号链接") >= 0,
                        "★Rename 拒绝原因提到 Junction/符号链接", r.Message);

                    r = FileOps.Delete(new List<string> { jf }, plain, IntPtr.Zero);
                    Check(!r.Ok, "★Delete 拒绝 Junction 路径", r.Status.ToString());

                    r = FileOps.Cut(new List<string> { jf }, plain);
                    Check(!r.Ok, "★Cut 拒绝 Junction 路径", r.Status.ToString());

                    string srcFile = Path.Combine(root, "src.txt");
                    File.WriteAllText(srcFile, "s", new UTF8Encoding(false));
                    r = FileOps.Paste(new List<string> { srcFile }, junction, IntPtr.Zero);
                    Check(!r.Ok, "★Paste 拒绝 Junction 目标目录", r.Status.ToString());

                    r = FileOps.Paste(new List<string> { jf }, root, IntPtr.Zero);
                    Check(!r.Ok, "★Paste 拒绝 Junction 源路径", r.Status.ToString());

                    r = FileOps.NewFolder(junction);
                    Check(!r.Ok, "★NewFolder 拒绝 Junction 目标", r.Status.ToString());
                }
                else
                {
                    Console.WriteLine("  SKIP Junction 检测（无法创建 Junction）");
                }

                // ---- 21i. SymbolicLink（需 SeCreateSymbolicLinkPrivilege）----
                string symDir = Path.Combine(root, "sym-dir");
                if (TryMakeSymlink(symDir, plain, true))
                {
                    Check(FileOps.CheckPathSafety(symDir) == FileOps.PathSafety.ReparsePoint,
                        "★目录符号链接 → ReparsePoint",
                        FileOps.CheckPathSafety(symDir).ToString());
                }
                else
                {
                    Console.WriteLine("  SKIP 目录符号链接（无 SeCreateSymbolicLinkPrivilege）");
                }

                string symFile = Path.Combine(root, "sym-file.txt");
                if (TryMakeSymlink(symFile, plainFile, false))
                {
                    Check(FileOps.CheckPathSafety(symFile) == FileOps.PathSafety.ReparsePoint,
                        "★文件符号链接 → ReparsePoint",
                        FileOps.CheckPathSafety(symFile).ToString());
                }
                else
                {
                    Console.WriteLine("  SKIP 文件符号链接（无 SeCreateSymbolicLinkPrivilege）");
                }

                // ---- 21j. 不存在路径不误报 ----
                FileOps.PathSafety ghostPs = FileOps.CheckPathSafety(
                    Path.Combine(root, "ghost", "deep", "x.txt"));
                Check(ghostPs != FileOps.PathSafety.ReparsePoint,
                    "★不存在的路径不误报为 ReparsePoint", ghostPs.ToString());

                // ---- 21k. null / 空 → Indeterminate（fail-closed）----
                Check(FileOps.CheckPathSafety(null) == FileOps.PathSafety.Indeterminate,
                    "★null → Indeterminate");
                Check(FileOps.CheckPathSafety("") == FileOps.PathSafety.Indeterminate,
                    "★空串 → Indeterminate");

                // ---- 21l. 普通路径仍允许（回归：不过度拦截）----
                FileOps.Fs = real;
                string okFile = Path.Combine(plain, "ok.txt");
                File.WriteAllText(okFile, "o", new UTF8Encoding(false));
                FileOps.OpResult rOk = FileOps.Rename(okFile, "ok2.txt", plain);
                Check(rOk.Ok, "★普通路径 Rename 仍允许", rOk.Message);

                FileOps.OpResult rNew = FileOps.NewFolder(plain);
                Check(rNew.Ok, "★普通路径 NewFolder 仍允许", rNew.Message);
            }
            finally
            {
                FileOps.Fs = saved;
                if (junctionMade) { try { cmdRemoveDir(junction); } catch { } }
                try { cmdRemoveDir(Path.Combine(root, "sym-dir")); } catch { }
                try { if (File.Exists(Path.Combine(root, "sym-file.txt"))) File.Delete(Path.Combine(root, "sym-file.txt")); } catch { }
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }

            Console.WriteLine();
        }

        /// <summary>用 mklink /J 创建目录联接。</summary>
        static bool TryMakeJunction(string link, string target)
        {
            try
            {
                RunCmd("/c mklink /J \"" + link + "\" \"" + target + "\"");
                if (!Directory.Exists(link)) return false;
                return (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
            }
            catch { return false; }
        }

        /// <summary>用 mklink 创建符号链接（需 SeCreateSymbolicLinkPrivilege）。</summary>
        static bool TryMakeSymlink(string link, string target, bool dir)
        {
            try
            {
                string a = dir
                    ? "/c mklink /D \"" + link + "\" \"" + target + "\""
                    : "/c mklink \"" + link + "\" \"" + target + "\"";
                RunCmd(a);
                if (dir) { if (!Directory.Exists(link)) return false; }
                else { if (!File.Exists(link)) return false; }
                return (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
            }
            catch { return false; }
        }

        /// <summary>删目录联接本身（rmdir 不递归进目标）。</summary>
        static void cmdRemoveDir(string path)
        {
            try { RunCmd("/c rmdir \"" + path + "\""); } catch { }
        }

        static void RunCmd(string args)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi =
                    new System.Diagnostics.ProcessStartInfo("cmd.exe", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(5000);
                }
            }
            catch { }
        }

        static void Swap(FakeFs fs)

        {
            FileOps.Fs = fs;
            // 注意：不在这里清 logPath —— 需要日志的用例自己 Init(root)，
            // 不需要日志的用例靠 Main 开头那次 Init(null)。
        }

        static string NewRoot()
        {
            string d = Path.Combine(Path.GetTempPath(), "fo-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            Directory.CreateDirectory(Path.Combine(d, "config"));
            return d;
        }

        static void Clean(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }
    }
}
