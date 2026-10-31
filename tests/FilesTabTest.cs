using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// FileScanner 测试：过滤规则、四种排序、文件夹在前、拼音搜索、
    /// 500 返回上限、5000 枚举上限、异常处理、格式化。
    ///
    /// **全部用临时目录**，不碰真实用户文件夹。
    /// 枚举上限用「造 5001 个空文件」验证 —— 在 SSD 上大约 1-2 秒。
    /// </summary>
    static class FilesTabTest
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
            Console.WriteLine("=== FileScanner 测试 ===");
            Console.WriteLine();

            TestBasicListing();
            TestFilterRules();
            TestHiddenAndSystem();
            TestFolderFirst();
            TestSortModified();
            TestSortName();
            TestSortSize();
            TestSortType();
            TestPinyinFilter();
            TestReturnLimit();
            TestEnumerateLimit();
            TestErrors();
            TestFormatting();
            TestReadOnly();
            TestExtGroups();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 基本列举 ----------
        static void TestBasicListing()
        {
            Console.WriteLine("--- 1. 基本列举（不递归）---");

            string root = NewRoot();
            try
            {
                MakeFile(root, "a.txt", 100);
                MakeFile(root, "b.pdf", 200);
                Directory.CreateDirectory(Path.Combine(root, "sub"));
                // 子目录里的文件不应出现（不递归）
                MakeFile(Path.Combine(root, "sub"), "deep.txt", 50);

                FileScanner.Result r = FileScanner.List(root, FileScanner.SortKey.Modified);

                Check(r.Error == null, "无错误", r.Error ?? "");
                Check(r.Items.Count == 3, "★3 项（2 文件 + 1 文件夹）", Convert.ToString(r.Items.Count));

                int folders = 0, files = 0;
                bool hasDeep = false;
                foreach (FileScanner.Entry e in r.Items)
                {
                    if (e.IsFolder) folders++; else files++;
                    if (e.Name == "deep.txt") hasDeep = true;
                }
                Check(folders == 1, "1 个文件夹", Convert.ToString(folders));
                Check(files == 2, "2 个文件", Convert.ToString(files));
                Check(!hasDeep, "★不递归（子目录里的文件不出现）");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 2. 过滤规则 ----------
        static void TestFilterRules()
        {
            Console.WriteLine("--- 2. 过滤规则 ---");

            string root = NewRoot();
            try
            {
                // 应显示
                MakeFile(root, "keep.txt", 10);
                MakeFile(root, "keep.url", 10);
                MakeFile(root, "keep.exe", 10);
                MakeFile(root, "keep.msi", 10);

                // 应隐藏
                MakeFile(root, "hide.lnk", 10);
                MakeFile(root, "desktop.ini", 10);
                MakeFile(root, "~$word.docx", 10);
                MakeFile(root, "x.tmp", 10);
                MakeFile(root, "x.crdownload", 10);
                MakeFile(root, "x.part", 10);
                MakeFile(root, "x.download", 10);

                FileScanner.Result r = FileScanner.List(root, FileScanner.SortKey.Name);

                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FileScanner.Entry e in r.Items) names.Add(e.Name);

                Check(names.Contains("keep.txt"), "★.txt 显示");
                Check(names.Contains("keep.url"), "★.url 显示");
                Check(names.Contains("keep.exe"), "★.exe 显示");
                Check(names.Contains("keep.msi"), "★.msi 显示");

                Check(!names.Contains("hide.lnk"), "★.lnk 不显示");
                Check(!names.Contains("desktop.ini"), "★desktop.ini 不显示");
                Check(!names.Contains("~$word.docx"), "★~$ 开头不显示");
                Check(!names.Contains("x.tmp"), "★.tmp 不显示");
                Check(!names.Contains("x.crdownload"), "★.crdownload 不显示");
                Check(!names.Contains("x.part"), "★.part 不显示");
                Check(!names.Contains("x.download"), "★.download 不显示");

                Check(r.Items.Count == 4, "★恰好 4 项可见", Convert.ToString(r.Items.Count));
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 3. 隐藏 / 系统属性 ----------
        static void TestHiddenAndSystem()
        {
            Console.WriteLine("--- 3. 隐藏 / 系统属性 ---");

            string root = NewRoot();
            try
            {
                string visible = MakeFile(root, "visible.txt", 10);
                string hidden = MakeFile(root, "hidden.txt", 10);
                string system = MakeFile(root, "system.txt", 10);

                File.SetAttributes(hidden, FileAttributes.Hidden);
                File.SetAttributes(system, FileAttributes.System);

                FileScanner.Result r = FileScanner.List(root, FileScanner.SortKey.Name);

                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FileScanner.Entry e in r.Items) names.Add(e.Name);

                Check(names.Contains("visible.txt"), "★普通文件显示");
                Check(!names.Contains("hidden.txt"), "★隐藏属性文件不显示");
                Check(!names.Contains("system.txt"), "★系统属性文件不显示");
                Check(r.Items.Count == 1, "★只剩 1 项", Convert.ToString(r.Items.Count));
            }
            finally
            {
                // 清属性再删（否则删不掉）
                try
                {
                    foreach (string f in Directory.GetFiles(root))
                        File.SetAttributes(f, FileAttributes.Normal);
                }
                catch { }
                Clean(root);
            }
            Console.WriteLine();
        }

        // ---------- 4. 文件夹在前 ----------
        static void TestFolderFirst()
        {
            Console.WriteLine("--- 4. 文件夹始终在前 ---");

            string root = NewRoot();
            try
            {
                // 文件名字母序在前，文件夹在后 —— 但排序后文件夹必须在前
                MakeFile(root, "aaa.txt", 10);
                Directory.CreateDirectory(Path.Combine(root, "zzz"));

                foreach (FileScanner.SortKey k in new FileScanner.SortKey[] {
                    FileScanner.SortKey.Modified, FileScanner.SortKey.Name,
                    FileScanner.SortKey.Size, FileScanner.SortKey.Type })
                {
                    FileScanner.Result r = FileScanner.List(root, k);
                    Check(r.Items.Count == 2, k + "：2 项");
                    if (r.Items.Count == 2)
                        Check(r.Items[0].IsFolder, "★" + k + "：文件夹在前", r.Items[0].Name);
                }
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 5. 按修改时间 ----------
        static void TestSortModified()
        {
            Console.WriteLine("--- 5. 排序：修改时间降序 ---");

            string root = NewRoot();
            try
            {
                string old = MakeFile(root, "old.txt", 10);
                string mid = MakeFile(root, "mid.txt", 10);
                string newf = MakeFile(root, "new.txt", 10);

                File.SetLastWriteTime(old, DateTime.Now.AddDays(-10));
                File.SetLastWriteTime(mid, DateTime.Now.AddDays(-5));
                File.SetLastWriteTime(newf, DateTime.Now);

                FileScanner.Result r = FileScanner.List(root, FileScanner.SortKey.Modified);

                Check(r.Items.Count == 3, "3 项");
                if (r.Items.Count == 3)
                {
                    Check(r.Items[0].Name == "new.txt", "★最新在最前", r.Items[0].Name);
                    Check(r.Items[1].Name == "mid.txt", "中间第二", r.Items[1].Name);
                    Check(r.Items[2].Name == "old.txt", "最旧在最后", r.Items[2].Name);
                }
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 6. 按名称（拼音）----------
        static void TestSortName()
        {
            Console.WriteLine("--- 6. 排序：名称（逐字拼音）---");

            string root = NewRoot();
            try
            {
                MakeFile(root, "banana.txt", 10);
                MakeFile(root, "apple.txt", 10);
                MakeFile(root, "cherry.txt", 10);

                FileScanner.Result r = FileScanner.List(root, FileScanner.SortKey.Name);

                Check(r.Items.Count == 3, "3 项");
                if (r.Items.Count == 3)
                {
                    Check(r.Items[0].Name == "apple.txt", "★apple 第一", r.Items[0].Name);
                    Check(r.Items[1].Name == "banana.txt", "banana 第二", r.Items[1].Name);
                    Check(r.Items[2].Name == "cherry.txt", "cherry 第三", r.Items[2].Name);
                }

                // 中文按拼音：阿(a) < 波(b) < 次(c)
                string root2 = NewRoot();
                try
                {
                    MakeFile(root2, "次.txt", 10);
                    MakeFile(root2, "阿.txt", 10);
                    MakeFile(root2, "波.txt", 10);

                    FileScanner.Result r2 = FileScanner.List(root2, FileScanner.SortKey.Name);
                    Check(r2.Items.Count == 3, "中文 3 项");
                    if (r2.Items.Count == 3)
                    {
                        Check(r2.Items[0].Name == "阿.txt", "★中文按拼音：阿 < 波", r2.Items[0].Name);
                        Check(r2.Items[2].Name == "次.txt", "次 最后", r2.Items[2].Name);
                    }
                }
                finally { Clean(root2); }
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 7. 按大小 ----------
        static void TestSortSize()
        {
            Console.WriteLine("--- 7. 排序：大小降序 ---");

            string root = NewRoot();
            try
            {
                MakeFile(root, "small.txt", 10);
                MakeFile(root, "big.txt", 5000);
                MakeFile(root, "mid.txt", 500);

                FileScanner.Result r = FileScanner.List(root, FileScanner.SortKey.Size);

                Check(r.Items.Count == 3, "3 项");
                if (r.Items.Count == 3)
                {
                    Check(r.Items[0].Name == "big.txt", "★最大在最前", r.Items[0].Name);
                    Check(r.Items[1].Name == "mid.txt", "中间第二", r.Items[1].Name);
                    Check(r.Items[2].Name == "small.txt", "最小在最后", r.Items[2].Name);
                    Check(r.Items[0].Size == 5000, "大小正确", Convert.ToString(r.Items[0].Size));
                }
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 8. 按类型 ----------
        static void TestSortType()
        {
            Console.WriteLine("--- 8. 排序：类型（扩展名）---");

            string root = NewRoot();
            try
            {
                MakeFile(root, "z.txt", 10);
                MakeFile(root, "a.pdf", 10);
                MakeFile(root, "m.docx", 10);
                MakeFile(root, "b.pdf", 10);

                FileScanner.Result r = FileScanner.List(root, FileScanner.SortKey.Type);

                Check(r.Items.Count == 4, "4 项");
                if (r.Items.Count == 4)
                {
                    // .docx < .pdf < .txt
                    Check(r.Items[0].Ext == ".docx", "★.docx 最前", r.Items[0].Ext);
                    Check(r.Items[1].Ext == ".pdf", ".pdf 第二", r.Items[1].Ext);
                    Check(r.Items[2].Ext == ".pdf", ".pdf 第三", r.Items[2].Ext);
                    Check(r.Items[3].Ext == ".txt", ".txt 最后", r.Items[3].Ext);
                    // 同扩展名按名称
                    Check(r.Items[1].Name == "a.pdf", "★同扩展名按名称", r.Items[1].Name);
                }
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 9. 拼音搜索 ----------
        static void TestPinyinFilter()
        {
            Console.WriteLine("--- 9. 拼音过滤 ---");

            string root = NewRoot();
            try
            {
                MakeFile(root, "微信截图.png", 10);
                MakeFile(root, "报告.docx", 10);
                MakeFile(root, "readme.txt", 10);

                FileScanner.Result r = FileScanner.List(root, FileScanner.SortKey.Name);
                Check(r.Items.Count == 3, "3 项");

                // 中文
                Check(FileScanner.Filter(r.Items, "微信").Count == 1, "★中文匹配");
                // 全拼
                Check(FileScanner.Filter(r.Items, "weixin").Count == 1, "★全拼 weixin");
                // 首字母
                Check(FileScanner.Filter(r.Items, "wx").Count == 1, "★首字母 wx");
                // 英文
                Check(FileScanner.Filter(r.Items, "readme").Count == 1, "★英文匹配");
                Check(FileScanner.Filter(r.Items, "README").Count == 1, "★英文大小写不敏感");
                // 无命中
                Check(FileScanner.Filter(r.Items, "zzzzz").Count == 0, "无命中 → 0");
                // 空查询 → 原列表
                Check(FileScanner.Filter(r.Items, "").Count == 3, "★空查询返回全部");
                Check(FileScanner.Filter(r.Items, null).Count == 3, "null 查询返回全部");
                Check(FileScanner.Filter(r.Items, "  ").Count == 3, "空白查询返回全部");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 10. 500 返回上限 ----------
        static void TestReturnLimit()
        {
            Console.WriteLine("--- 10. 500 返回上限 ---");

            string root = NewRoot();
            try
            {
                for (int i = 0; i < 520; i++)
                    MakeFile(root, "f" + i.ToString("D4") + ".txt", 10);

                FileScanner.Result r = FileScanner.List(root, FileScanner.SortKey.Name);

                Check(r.Error == null, "无错误", r.Error ?? "");
                Check(r.Items.Count == FileScanner.MaxReturn, "★返回恰好 " + FileScanner.MaxReturn + " 项",
                    Convert.ToString(r.Items.Count));
                Check(r.ReturnTruncated, "★ReturnTruncated = true");
                Check(r.FilteredCount == 520, "FilteredCount = 520", Convert.ToString(r.FilteredCount));
                Check(r.Notice != null && r.Notice.IndexOf("500") >= 0, "★Notice 提到 500", r.Notice ?? "null");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 11. 5000 枚举上限 ----------
        static void TestEnumerateLimit()
        {
            Console.WriteLine("--- 11. 5000 枚举上限 ---");

            string root = NewRoot();
            try
            {
                // 造 5001 个空文件 —— SSD 上约 1-2 秒
                for (int i = 0; i < FileScanner.MaxEnumerate + 1; i++)
                    MakeFile(root, "e" + i.ToString("D5") + ".txt", 1);

                FileScanner.Result r = FileScanner.List(root, FileScanner.SortKey.Name);

                Check(r.Error == null, "无错误", r.Error ?? "");
                Check(r.EnumerateTruncated, "★EnumerateTruncated = true");
                Check(r.ScannedCount <= FileScanner.MaxEnumerate,
                    "★扫描数不超过 " + FileScanner.MaxEnumerate, Convert.ToString(r.ScannedCount));
                Check(r.Items.Count == FileScanner.MaxReturn, "返回仍是 500 上限",
                    Convert.ToString(r.Items.Count));
                Check(r.Notice != null && r.Notice.IndexOf("过大") >= 0, "★Notice 提到目录过大", r.Notice ?? "null");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 12. 错误处理 ----------
        static void TestErrors()
        {
            Console.WriteLine("--- 12. 错误处理 ---");

            // 12a. 目录不存在
            FileScanner.Result r = FileScanner.List(@"Z:\definitely-not-exist-xyz", FileScanner.SortKey.Name);
            Check(r.Error != null, "★不存在的目录 → 有错误信息", r.Error ?? "null");
            Check(r.Items != null && r.Items.Count == 0, "★返回空列表");
            Check(r.Notice != null, "Notice 返回错误信息");

            // 12b. 路径为 null / 空
            r = FileScanner.List(null, FileScanner.SortKey.Name);
            Check(r.Error != null, "★null 路径 → 有错误");

            r = FileScanner.List("", FileScanner.SortKey.Name);
            Check(r.Error != null, "★空路径 → 有错误");

            // 12c. 路径是文件不是目录
            string root = NewRoot();
            try
            {
                string f = MakeFile(root, "notadir.txt", 10);
                r = FileScanner.List(f, FileScanner.SortKey.Name);
                Check(r.Error != null, "★传文件路径 → 有错误", r.Error ?? "null");
            }
            finally { Clean(root); }

            // 12d. 不抛异常（各种奇怪输入）
            bool threw = false;
            foreach (string bad in new string[] { "C:\\", "\\\\", "::::", new string('x', 300), "\\\\.\\PhysicalDrive0" })
            {
                try { FileScanner.List(bad, FileScanner.SortKey.Name); }
                catch { threw = true; break; }
            }
            Check(!threw, "★各种奇怪输入都不抛异常");
            Console.WriteLine();
        }

        // ---------- 13. 格式化 ----------
        static void TestFormatting()
        {
            Console.WriteLine("--- 13. 格式化辅助 ---");

            Check(FileScanner.FormatSize(0) == "0 B", "0 → 0 B", FileScanner.FormatSize(0));
            Check(FileScanner.FormatSize(512) == "512 B", "512 → 512 B", FileScanner.FormatSize(512));
            Check(FileScanner.FormatSize(1024) == "1 KB", "1024 → 1 KB", FileScanner.FormatSize(1024));
            Check(FileScanner.FormatSize(1536) == "1.5 KB", "1536 → 1.5 KB", FileScanner.FormatSize(1536));
            Check(FileScanner.FormatSize(1024 * 1024) == "1 MB", "1MB", FileScanner.FormatSize(1024 * 1024));
            Check(FileScanner.FormatSize(1024L * 1024 * 1024) == "1 GB", "1GB",
                FileScanner.FormatSize(1024L * 1024 * 1024));
            Check(FileScanner.FormatSize(-1) == "-", "负数 → -");

            // 时间格式化
            DateTime now = DateTime.Now;
            Check(FileScanner.FormatTime(now) == now.ToString("HH:mm"), "★今天 → 只显示时间",
                FileScanner.FormatTime(now));
            Check(FileScanner.FormatTime(DateTime.MinValue) == "", "MinValue → 空");
            Check(FileScanner.FormatTime(new DateTime(2020, 3, 15)) == "2020-03-15",
                "★往年 → 完整日期", FileScanner.FormatTime(new DateTime(2020, 3, 15)));
            Console.WriteLine();
        }

        // ---------- 14. 只读（不修改文件）----------
        static void TestReadOnly()
        {
            Console.WriteLine("--- 14. 只读（不修改任何文件）---");

            string root = NewRoot();
            try
            {
                string f = MakeFile(root, "data.txt", 100);
                DateTime before = File.GetLastWriteTime(f);
                long sizeBefore = new FileInfo(f).Length;
                string contentBefore = File.ReadAllText(f, Encoding.UTF8);

                // 列举多次
                for (int i = 0; i < 3; i++)
                {
                    FileScanner.List(root, FileScanner.SortKey.Name);
                    FileScanner.List(root, FileScanner.SortKey.Modified);
                    FileScanner.List(root, FileScanner.SortKey.Size);
                    FileScanner.List(root, FileScanner.SortKey.Type);
                }

                Check(File.GetLastWriteTime(f) == before, "★修改时间未被改");
                Check(new FileInfo(f).Length == sizeBefore, "★大小未被改");
                Check(File.ReadAllText(f, Encoding.UTF8) == contentBefore, "★内容未被改");
                Check(File.Exists(f), "★文件仍在");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 15. 后缀分组（FileExtGroups）----------
        static void TestExtGroups()
        {
            Console.WriteLine("--- 15. 后缀分组 ---");

            List<FileExtGroups.Group> builtin = FileExtGroups.Builtin();
            List<FileExtGroups.Group> custom = new List<FileExtGroups.Group>();

            // ---- 固定分组命中 ----
            Check(FileExtGroups.MatchGroups(".docx", builtin, custom).Contains("doc"),
                "★.docx → doc 组");
            Check(FileExtGroups.MatchGroups(".xlsx", builtin, custom).Contains("xls"),
                "★.xlsx → xls 组");
            Check(FileExtGroups.MatchGroups(".pdf", builtin, custom).Contains("pdf"),
                "★.pdf → pdf 组");
            Check(FileExtGroups.MatchGroups(".jpg", builtin, custom).Contains("img"),
                "★.jpg → 图片组");
            Check(FileExtGroups.MatchGroups(".zip", builtin, custom).Contains("zip"),
                "★.zip → 压缩包组");
            Check(FileExtGroups.MatchGroups(".exe", builtin, custom).Contains("setup"),
                "★.exe → 安装包组");

            // ---- 大小写与点号归一化 ----
            Check(FileExtGroups.MatchGroups(".DOCX", builtin, custom).Contains("doc"),
                "★大写 .DOCX 也命中");
            Check(FileExtGroups.Normalize(".PDF") == "pdf", "Normalize 去点去大写",
                FileExtGroups.Normalize(".PDF"));
            Check(FileExtGroups.Normalize("pdf") == "pdf", "Normalize 无点也 OK");

            // ---- 「其他」：不属于任何固定组 ----
            Check(FileExtGroups.IsOther(".md", builtin, custom), "★.md → 其他");
            Check(FileExtGroups.IsOther(".py", builtin, custom), "★.py → 其他");
            Check(FileExtGroups.IsOther(".log", builtin, custom), ".log → 其他");
            Check(!FileExtGroups.IsOther(".pdf", builtin, custom), ".pdf 不属于其他");

            // ---- 「其他」：无扩展名也算 ----
            Check(FileExtGroups.IsOther("", builtin, custom), "★无扩展名 → 其他");
            Check(FileExtGroups.IsOther(null, builtin, custom), "★null 扩展名 → 其他");

            // ---- 自定义后缀从「其他」里移出 ----
            FileExtGroups.Group mdGroup = new FileExtGroups.Group("md", "md", new string[] { "md" });
            mdGroup.IsCustom = true;
            custom.Add(mdGroup);
            Check(!FileExtGroups.IsOther(".md", builtin, custom),
                "★加了自定义 md 后，.md 不再属于其他");
            Check(FileExtGroups.MatchGroups(".md", builtin, custom).Contains("md"),
                "★.md 命中自定义组");
            Check(FileExtGroups.IsOther(".py", builtin, custom),
                "未加自定义的 .py 仍在其他");

            // ---- Passes 筛选 ----
            HashSet<string> sel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Check(FileExtGroups.Passes(".docx", sel, builtin, custom),
                "★空选择 → 全部通过");

            sel.Add("doc");
            Check(FileExtGroups.Passes(".docx", sel, builtin, custom),
                "★选了 doc → .docx 通过");
            Check(!FileExtGroups.Passes(".pdf", sel, builtin, custom),
                "★选了 doc → .pdf 不通过");

            sel.Add("pdf");
            Check(FileExtGroups.Passes(".pdf", sel, builtin, custom),
                "★doc+pdf 并集 → .pdf 通过");
            Check(!FileExtGroups.Passes(".md", sel, builtin, custom),
                "★doc+pdf 并集 → .md 不通过");

            // ---- 选「其他」 ----
            HashSet<string> selOther = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            selOther.Add(FileExtGroups.OtherKey);
            Check(FileExtGroups.Passes(".py", selOther, builtin, custom),
                "★选其他 → .py 通过");
            Check(FileExtGroups.Passes("", selOther, builtin, custom),
                "★选其他 → 无扩展名通过");
            Check(!FileExtGroups.Passes(".pdf", selOther, builtin, custom),
                "★选其他 → .pdf 不通过");

            // ---- KeepEntry：四种组合（类型过滤的唯一判定）----
            HashSet<string> empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> docOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            docOnly.Add("doc");

            // ① 后缀空 + 文件夹关 → 只放行文件
            Check(!FileExtGroups.KeepEntry(true, "", false, empty, builtin, custom),
                "★①文件夹关 → 文件夹不放行");
            Check(FileExtGroups.KeepEntry(false, ".docx", false, empty, builtin, custom),
                "★①文件夹关 → 文件放行");
            Check(FileExtGroups.KeepEntry(false, ".pdf", false, empty, builtin, custom),
                "①文件夹关 → 任意后缀文件都放行");

            // ② 后缀空 + 文件夹开 → 只放行文件夹（关键：文件一律排除）
            Check(FileExtGroups.KeepEntry(true, "", true, empty, builtin, custom),
                "★②文件夹开 → 文件夹放行");
            Check(!FileExtGroups.KeepEntry(false, ".docx", true, empty, builtin, custom),
                "★②文件夹开 → .docx 文件**不放行**（这是本轮修的 bug）");
            Check(!FileExtGroups.KeepEntry(false, ".doc", true, empty, builtin, custom),
                "★②文件夹开 → .doc 文件**不放行**");
            Check(!FileExtGroups.KeepEntry(false, "", true, empty, builtin, custom),
                "★②文件夹开 → 无扩展名文件也不放行");

            // ③ 后缀非空 + 文件夹关 → 只放行匹配的文件
            Check(!FileExtGroups.KeepEntry(true, "", false, docOnly, builtin, custom),
                "★③选 doc + 文件夹关 → 文件夹不放行");
            Check(FileExtGroups.KeepEntry(false, ".docx", false, docOnly, builtin, custom),
                "★③选 doc → .docx 放行");
            Check(!FileExtGroups.KeepEntry(false, ".pdf", false, docOnly, builtin, custom),
                "★③选 doc → .pdf 不放行");

            // ④ 后缀非空 + 文件夹开 → 文件夹 + 匹配的文件
            Check(FileExtGroups.KeepEntry(true, "", true, docOnly, builtin, custom),
                "★④选 doc + 文件夹开 → 文件夹放行");
            Check(FileExtGroups.KeepEntry(false, ".docx", true, docOnly, builtin, custom),
                "★④选 doc + 文件夹开 → .docx 放行");
            Check(!FileExtGroups.KeepEntry(false, ".pdf", true, docOnly, builtin, custom),
                "★④选 doc + 文件夹开 → .pdf 仍不放行");

            // null 集合不崩
            Check(!FileExtGroups.KeepEntry(true, "", false, null, builtin, custom),
                "KeepEntry(null 集合) 文件夹关 → false");
            Check(FileExtGroups.KeepEntry(false, ".txt", false, null, builtin, custom),
                "KeepEntry(null 集合) 文件 → true");

            Console.WriteLine();
        }


        static string NewRoot()
        {
            string d = Path.Combine(Path.GetTempPath(), "fs-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        static string MakeFile(string dir, string name, int bytes)
        {
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, name);
            byte[] data = new byte[bytes];
            File.WriteAllBytes(p, data);
            return p;
        }

        static void Clean(string d)
        {
            try
            {
                if (!Directory.Exists(d)) return;
                foreach (string f in Directory.GetFiles(d, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(d, true);
            }
            catch { }
        }
    }
}
