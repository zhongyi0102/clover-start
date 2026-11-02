using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// DropSpec 校验测试：正常 / 全拒绝 / 混合 / 超数量 / 超大小 / 空输入 / 目录模式。
    ///
    /// **全部用临时目录。**
    /// </summary>
    static class DropSpecTest
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
            Console.WriteLine("=== DropSpec 校验测试 ===");
            Console.WriteLine();

            try
            {
                TestNormal();
                TestAllRejected();
                TestMixed();
                TestTooMany();
                TestTooLarge();
                TestEmpty();
                TestDirectoryMode();
                TestBackgroundPreset();
                TestReasonText();
                TestCounting();
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
                "dropspec-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(d);
            return d;
        }

        static void Clean(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }

        // ---------- 1. 正常 ----------
        static void TestNormal()
        {
            Console.WriteLine("--- 1. 正常通过 ---");

            string dir = NewDir("ok");
            try
            {
                string a = Path.Combine(dir, "a.txt");
                string b = Path.Combine(dir, "b.pdf");
                File.WriteAllText(a, "x");
                File.WriteAllText(b, "y");

                DropSpec spec = DropSpec.ForDesk();
                DropSpec.Validation v = spec.Validate(new string[] { a, b });

                Check(v.Accepted.Count == 2, "★两个都接受", Convert.ToString(v.Accepted.Count));
                Check(v.RejectedItems.Count == 0, "★没有拒绝项");
                Check(v.AllAccepted, "AllAccepted");
                Check(v.Ignored == 0, "没有忽略项");
                Check(v.Accepted[0] == Path.GetFullPath(a), "★保持传入顺序", v.Accepted[0]);
                Check(v.Accepted[1] == Path.GetFullPath(b), "第二个顺序也对");

                Console.WriteLine();
            }
            finally { Clean(dir); }
        }

        // ---------- 2. 全拒绝 ----------
        static void TestAllRejected()
        {
            Console.WriteLine("--- 2. 全部被拒 ---");

            string dir = NewDir("rej");
            try
            {
                string e = Path.Combine(dir, "app.exe");
                string l = Path.Combine(dir, "link.lnk");
                File.WriteAllText(e, "x");
                File.WriteAllText(l, "y");

                DropSpec.Validation v = DropSpec.ForDesk().Validate(new string[] { e, l });

                Check(v.Accepted.Count == 0, "★一个都没接受");
                Check(v.RejectedItems.Count == 2, "★两个都拒绝", Convert.ToString(v.RejectedItems.Count));
                Check(!v.AllAccepted, "★AllAccepted 为 false");
                Check(!v.AnyAccepted, "AnyAccepted 为 false");
                Check(v.AnyRejected, "AnyRejected 为 true");

                // 原因必须是「应用类文件」而不是「不支持的类型」
                bool allAppFile = true;
                foreach (DropSpec.Rejected r in v.RejectedItems)
                    if (r.Reason != DropSpec.RejectReason.AppFile) allAppFile = false;
                Check(allAppFile, "★原因都是应用类文件");

                // 扩展名被记下来（步骤 2 汇总用）
                List<string> exts = v.RejectedExts();
                Check(exts.Count == 2 && exts.Contains(".exe") && exts.Contains(".lnk"),
                    "★扩展名已记录", string.Join(",", exts.ToArray()));

                Console.WriteLine();
            }
            finally { Clean(dir); }
        }

        // ---------- 3. 混合 ----------
        static void TestMixed()
        {
            Console.WriteLine("--- 3. 混合（部分接受部分拒绝） ---");

            string dir = NewDir("mix");
            try
            {
                string ok1 = Path.Combine(dir, "doc.txt");
                string bad = Path.Combine(dir, "setup.exe");
                string ok2 = Path.Combine(dir, "pic.png");
                string bad2 = Path.Combine(dir, "old.msi");
                File.WriteAllText(ok1, "x");
                File.WriteAllText(bad, "x");
                File.WriteAllText(ok2, "x");
                File.WriteAllText(bad2, "x");

                DropSpec.Validation v = DropSpec.ForDesk().Validate(
                    new string[] { ok1, bad, ok2, bad2 });

                Check(v.Accepted.Count == 2, "★接受 2 个", Convert.ToString(v.Accepted.Count));
                Check(v.RejectedItems.Count == 2, "★拒绝 2 个", Convert.ToString(v.RejectedItems.Count));
                Check(!v.AllAccepted, "混合时 AllAccepted 为 false");
                Check(v.AnyAccepted && v.AnyRejected, "两边都非空");

                // 接受项顺序不乱
                Check(Path.GetFileName(v.Accepted[0]) == "doc.txt", "★第一个接受项是 doc.txt");
                Check(Path.GetFileName(v.Accepted[1]) == "pic.png", "★第二个接受项是 pic.png");

                Console.WriteLine();
            }
            finally { Clean(dir); }
        }

        // ---------- 4. 超数量 ----------
        static void TestTooMany()
        {
            Console.WriteLine("--- 4. 超出数量上限 ---");

            string dir = NewDir("many");
            try
            {
                string[] paths = new string[5];
                for (int i = 0; i < 5; i++)
                {
                    paths[i] = Path.Combine(dir, "f" + i + ".txt");
                    File.WriteAllText(paths[i], "x");
                }

                DropSpec spec = DropSpec.ForDesk();
                spec.MaxFiles = 2;

                DropSpec.Validation v = spec.Validate(paths);

                Check(v.Accepted.Count == 2, "★只收前 2 个", Convert.ToString(v.Accepted.Count));
                Check(v.RejectedItems.Count == 3, "★后 3 个被拒", Convert.ToString(v.RejectedItems.Count));

                bool allTooMany = true;
                foreach (DropSpec.Rejected r in v.RejectedItems)
                    if (r.Reason != DropSpec.RejectReason.TooMany) allTooMany = false;
                Check(allTooMany, "★原因是超出数量");

                // 数量判定在类型判定之后：一个 .exe 混在超量区里仍报「应用类」
                string exe = Path.Combine(dir, "z.exe");
                File.WriteAllText(exe, "x");
                DropSpec.Validation v2 = spec.Validate(new string[] { paths[0], paths[1], exe });
                Check(v2.RejectedItems.Count == 1, "第三个被拒");
                Check(v2.RejectedItems[0].Reason == DropSpec.RejectReason.AppFile,
                    "★类型优先于数量", v2.RejectedItems[0].Reason.ToString());

                // MaxFiles = 0 表示不限
                spec.MaxFiles = 0;
                Check(spec.Validate(paths).Accepted.Count == 5, "★0 = 不限");

                Console.WriteLine();
            }
            finally { Clean(dir); }
        }

        // ---------- 5. 超大小 ----------
        static void TestTooLarge()
        {
            Console.WriteLine("--- 5. 超出大小上限 ---");

            string dir = NewDir("size");
            try
            {
                string small = Path.Combine(dir, "small.txt");
                string big = Path.Combine(dir, "big.txt");
                File.WriteAllText(small, "12345");
                File.WriteAllText(big, new string('x', 5000));

                DropSpec spec = DropSpec.ForDesk();
                spec.MaxSizeBytes = 1000;

                DropSpec.Validation v = spec.Validate(new string[] { small, big });

                Check(v.Accepted.Count == 1, "★小的通过", Convert.ToString(v.Accepted.Count));
                Check(Path.GetFileName(v.Accepted[0]) == "small.txt", "接受的是 small");
                Check(v.RejectedItems.Count == 1, "★大的被拒");
                Check(v.RejectedItems[0].Reason == DropSpec.RejectReason.TooLarge,
                    "★原因是超出大小");

                // 边界：正好等于上限算通过
                string exact = Path.Combine(dir, "exact.txt");
                File.WriteAllText(exact, new string('x', 1000));
                DropSpec.Validation v2 = spec.Validate(new string[] { exact });
                Check(v2.Accepted.Count == 1, "★正好等于上限 → 通过");

                // 目录不做递归统计（避免拖整个大目录卡住）
                string sub = Path.Combine(dir, "folder");
                Directory.CreateDirectory(sub);
                File.WriteAllText(Path.Combine(sub, "huge.bin"), new string('x', 9000));
                DropSpec.Validation v3 = spec.Validate(new string[] { sub });
                Check(v3.Accepted.Count == 1, "★目录不按大小拒绝");

                Console.WriteLine();
            }
            finally { Clean(dir); }
        }

        // ---------- 6. 空输入 ----------
        static void TestEmpty()
        {
            Console.WriteLine("--- 6. 空输入 ---");

            DropSpec spec = DropSpec.ForDesk();

            DropSpec.Validation v = spec.Validate(null);
            Check(v.Accepted.Count == 0 && v.RejectedItems.Count == 0, "★null → 全空");
            Check(!v.AllAccepted, "null 时 AllAccepted 为 false");

            DropSpec.Validation v2 = spec.Validate(new string[0]);
            Check(v2.Accepted.Count == 0, "★空数组 → 无接受");

            DropSpec.Validation v3 = spec.Validate(new string[] { null, "", "   " });
            Check(v3.Accepted.Count == 0, "★空/空白 → 无接受");
            Check(v3.RejectedItems.Count == 0, "★空/空白不算拒绝");
            Check(v3.Ignored == 3, "★空/空白计入 Ignored", Convert.ToString(v3.Ignored));

            Console.WriteLine();
        }

        // ---------- 7. 目录模式 ----------
        static void TestDirectoryMode()
        {
            Console.WriteLine("--- 7. 只收文件夹 ---");

            string dir = NewDir("dironly");
            try
            {
                string folder = Path.Combine(dir, "src");
                Directory.CreateDirectory(folder);
                string file = Path.Combine(dir, "a.txt");
                File.WriteAllText(file, "x");

                DropSpec spec = DropSpec.ForFolderSource(10);
                DropSpec.Validation v = spec.Validate(new string[] { folder, file });

                Check(v.Accepted.Count == 1, "★只收文件夹", Convert.ToString(v.Accepted.Count));
                Check(v.Accepted[0] == Path.GetFullPath(folder), "接受的是那个目录");
                Check(v.RejectedItems.Count == 1, "★文件被拒");
                Check(v.RejectedItems[0].Reason == DropSpec.RejectReason.NotDirectory,
                    "★原因是不是文件夹");

                // 名额用尽
                DropSpec full = DropSpec.ForFolderSource(1);
                DropSpec.Validation v2 = full.Validate(new string[] { folder, dir });
                Check(v2.Accepted.Count == 1, "★只收 1 个");
                Check(v2.RejectedItems.Count == 1
                    && v2.RejectedItems[0].Reason == DropSpec.RejectReason.TooMany,
                    "★第二个报超数量");

                // 名额 <= 0 视为不限
                DropSpec none = DropSpec.ForFolderSource(0);
                Check(none.Validate(new string[] { folder }).Accepted.Count == 1, "0 名额 = 不收");
                DropSpec neg = DropSpec.ForFolderSource(-1);
                Check(neg.MaxFiles == 0, "★负数名额 → 0（不限）", Convert.ToString(neg.MaxFiles));

                Console.WriteLine();
            }
            finally { Clean(dir); }
        }

        // ---------- 8. 背景预设 ----------
        static void TestBackgroundPreset()
        {
            Console.WriteLine("--- 8. 背景图预设 ---");

            DropSpec s = DropSpec.ForBackground();
            Check(s.MaxFiles == 1, "★最多 1 张", Convert.ToString(s.MaxFiles));
            Check(s.MaxLongEdge == 3840, "★长边 3840", Convert.ToString(s.MaxLongEdge));
            Check(s.Accept.Count == 8, "★8 种扩展名", Convert.ToString(s.Accept.Count));
            Check(!s.AllowDirectoriesOnly, "不限制为目录");
            Check(s.RejectExts.Count == 0, "背景预设没有额外拒收项");

            // 与 BackgroundStore 读同一份的断言在 BackgroundStoreTest 里
            // （那条要引用 ui 层的 BackgroundStore，本套件保持纯 core 可单跑）

            // 桌面预设
            DropSpec d = DropSpec.ForDesk();
            Check(d.Accept.Count == 0, "桌面预设不限类型");
            Check(d.RejectExts.Count == 14, "★桌面预设 14 个应用类", Convert.ToString(d.RejectExts.Count));
            Check(d.MaxFiles == 0 && d.MaxSizeBytes == 0, "桌面预设不限数量与大小");

            // 常量自洽：预设与常量不能各写一份
            Check(DropSpec.ForBackground().MaxLongEdge == DropSpec.BackgroundLongEdge,
                "★预设读的就是常量");
            Check(DropSpec.BackgroundExts.Length == 8, "★背景扩展名 8 个",
                Convert.ToString(DropSpec.BackgroundExts.Length));
            Check(DropSpec.AppExts.Length == 14, "★应用类扩展名 14 个",
                Convert.ToString(DropSpec.AppExts.Length));

            // ---- 约束文案由常量生成 ----
            string hint = DropSpec.BackgroundHintText();
            Check(hint == "支持 jpg/jpeg/png/bmp/gif/webp/tif/tiff，长边超过 3840 会等比缩小",
                "★约束文案完整", hint);
            Check(hint.Contains("3840"), "★含长边常量");
            Check(hint.Contains("会等比缩小"), "★措辞是「会等比缩小」");
            Check(hint.IndexOf("不支持", StringComparison.Ordinal) < 0,
                "★不写成「不支持」", hint);

            // 每个扩展名都出现在文案里（去点后）
            bool allExts = true;
            foreach (string ext in DropSpec.BackgroundExts)
            {
                string bare = ext.TrimStart('.');
                if (hint.IndexOf(bare, StringComparison.Ordinal) < 0) allExts = false;
            }
            Check(allExts, "★每个扩展名都出现在文案里");

            // 全角标点
            Check(hint.IndexOf('，') > 0, "★用全角逗号", hint);
            Check(hint.IndexOf(',') < 0, "★没有半角逗号", hint);

            // 改常量 → 文案跟着变（用反射临时改不了 readonly，所以验证派生关系：
            // 文案里的数字必须等于常量值，扩展名列表必须来自 BackgroundExts）
            Check(hint.IndexOf(DropSpec.BackgroundLongEdge.ToString(), StringComparison.Ordinal) >= 0,
                "★文案里的数字就是常量值");
            int slashCount = 0;
            foreach (char c in hint) if (c == '/') slashCount++;
            Check(slashCount == DropSpec.BackgroundExts.Length - 1,
                "★斜杠数 = 扩展名数 - 1", slashCount + " vs " + (DropSpec.BackgroundExts.Length - 1));

            Console.WriteLine();
        }

        // ---------- 9. 原因文案 ----------
        static void TestReasonText()
        {
            Console.WriteLine("--- 9. 原因文案 ---");

            Check(DropSpec.ReasonText(DropSpec.RejectReason.AppFile) == "应用类文件",
                "应用类");
            Check(DropSpec.ReasonText(DropSpec.RejectReason.UnsupportedType) == "不支持的类型",
                "不支持");
            Check(DropSpec.ReasonText(DropSpec.RejectReason.TooMany) == "超出数量上限",
                "超数量");
            Check(DropSpec.ReasonText(DropSpec.RejectReason.TooLarge) == "超出大小上限",
                "超大小");
            Check(DropSpec.ReasonText(DropSpec.RejectReason.NotDirectory) == "不是文件夹",
                "非目录");

            // 每个枚举值都要有非空文案（漏一个就会给用户看空白）
            foreach (DropSpec.RejectReason r in Enum.GetValues(typeof(DropSpec.RejectReason)))
            {
                Check(DropSpec.ReasonText(r).Length > 0, "枚举 " + r + " 有文案");
            }

            Console.WriteLine();
        }

        // ---------- 10. 汇总计数 ----------
        static void TestCounting()
        {
            Console.WriteLine("--- 10. 按原因汇总 ---");

            string dir = NewDir("count");
            try
            {
                string e1 = Path.Combine(dir, "a.exe");
                string e2 = Path.Combine(dir, "b.exe");
                string e3 = Path.Combine(dir, "c.lnk");
                string bad = Path.Combine(dir, "d.xyz");
                File.WriteAllText(e1, "x");
                File.WriteAllText(e2, "x");
                File.WriteAllText(e3, "x");
                File.WriteAllText(bad, "x");

                DropSpec spec = DropSpec.ForDesk();
                spec.Accept.Add(".txt");   // 加上白名单，让 .xyz 报「不支持」

                DropSpec.Validation v = spec.Validate(new string[] { e1, e2, e3, bad });
                Dictionary<DropSpec.RejectReason, int> counts = v.CountByReason();

                Check(counts.ContainsKey(DropSpec.RejectReason.AppFile)
                    && counts[DropSpec.RejectReason.AppFile] == 3,
                    "★应用类 3 个", counts.ContainsKey(DropSpec.RejectReason.AppFile)
                        ? Convert.ToString(counts[DropSpec.RejectReason.AppFile]) : "缺键");
                Check(counts.ContainsKey(DropSpec.RejectReason.UnsupportedType)
                    && counts[DropSpec.RejectReason.UnsupportedType] == 1,
                    "★不支持 1 个");

                List<string> exts = v.RejectedExts();
                // 三种被拒项各有一种扩展名：.exe / .lnk / .xyz（.exe 出现两次但去重）
                Check(exts.Count == 3, "★去重后 3 种扩展名", string.Join(",", exts.ToArray()));
                Check(exts[0] == ".exe" && exts[1] == ".lnk" && exts[2] == ".xyz",
                    "★按字母序", string.Join(",", exts.ToArray()));

                Console.WriteLine();
            }
            finally { Clean(dir); }
        }
    }
}
