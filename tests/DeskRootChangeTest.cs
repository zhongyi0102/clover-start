using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 桌面存放目录切换：路径校验 / 盘点 / 全有或全无迁移 / 回滚 / 路径越界判定。
    ///
    /// **全部用临时目录，不碰真实 config 与 desks。**
    /// </summary>
    static class DeskRootChangeTest
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
            Console.WriteLine("=== 桌面存放目录切换测试 ===");
            Console.WriteLine();

            try
            {
                TestValidate();
                TestIsUnder();
                TestInspect();
                TestMigrateHappy();
                TestMigratePrecheckAllOrNothing();
                TestMigrateRollback();
                TestOldImplementationLeavesHalf();
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
                "droot-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        static void Clean(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }

        // ---------- 1. 校验 ----------
        static void TestValidate()
        {
            Console.WriteLine("--- 1. 路径校验 ---");

            string oldRoot = NewDir("old");
            string newRoot = NewDir("new");
            try
            {
                DeskRootChange.Check c = DeskRootChange.Validate(newRoot, oldRoot);
                Check(c.Ok, "★合法目录通过", c.Message);
                Check(c.Full == newRoot, "★返回规范化完整路径", c.Full);

                // 尾部斜杠被规整掉
                c = DeskRootChange.Validate(newRoot + "\\", oldRoot);
                Check(c.Ok && c.Full == newRoot, "★尾部斜杠被规整", c.Full);

                // 带引号的路径（从资源管理器「复制路径」粘来的）
                c = DeskRootChange.Validate("\"" + newRoot + "\"", oldRoot);
                Check(c.Ok, "★去掉引号", c.Message);

                // 空
                Check(DeskRootChange.Validate("", oldRoot).Verdict == DeskRootChange.Verdict.Empty,
                    "★空串被拒");
                Check(DeskRootChange.Validate("   ", oldRoot).Verdict == DeskRootChange.Verdict.Empty,
                    "★全空白被拒");
                Check(DeskRootChange.Validate(null, oldRoot).Verdict == DeskRootChange.Verdict.Empty,
                    "★null 被拒");

                // 相对路径
                Check(DeskRootChange.Validate("desks", oldRoot).Verdict
                    == DeskRootChange.Verdict.NotAbsolute, "★相对路径被拒");

                // 是文件
                string file = Path.Combine(oldRoot, "a.txt");
                File.WriteAllText(file, "x");
                Check(DeskRootChange.Validate(file, oldRoot).Verdict
                    == DeskRootChange.Verdict.IsFile, "★文件路径被拒");

                // 同一个
                Check(DeskRootChange.Validate(oldRoot, oldRoot).Verdict
                    == DeskRootChange.Verdict.Same, "★同一个目录被拒");
                Check(DeskRootChange.Validate(oldRoot + "\\", oldRoot).Verdict
                    == DeskRootChange.Verdict.Same, "★同一个目录（带斜杠）被拒");

                // 新目录在旧目录里面
                string inside = Path.Combine(oldRoot, "sub");
                Check(DeskRootChange.Validate(inside, oldRoot).Verdict
                    == DeskRootChange.Verdict.InsideOld, "★旧目录的子目录被拒");

                // 新目录是旧目录的上级
                string parent = Path.GetDirectoryName(oldRoot);
                Check(DeskRootChange.Validate(parent, oldRoot).Verdict
                    == DeskRootChange.Verdict.ContainsOld, "★旧目录的上级被拒");

                // 建不出来（盘符不存在）
                DeskRootChange.Check bad = DeskRootChange.Validate(@"Q:\nope\desks", oldRoot);
                Check(bad.Verdict == DeskRootChange.Verdict.CannotCreate, "★不存在的盘被拒", bad.Verdict.ToString());
                Check(bad.Message.Length > 0, "★拒绝时带原因");

                // 每条拒绝都必须有可读原因
                Check(DeskRootChange.Validate("", oldRoot).Message.Length > 0, "空串有原因");
                Check(DeskRootChange.Validate("desks", oldRoot).Message.Length > 0, "相对路径有原因");
                Check(DeskRootChange.Validate(file, oldRoot).Message.Length > 0, "文件路径有原因");
                Check(DeskRootChange.Validate(oldRoot, oldRoot).Message.Length > 0, "同一个有原因");
                Check(DeskRootChange.Validate(inside, oldRoot).Message.Length > 0, "子目录有原因");
                Check(DeskRootChange.Validate(parent, oldRoot).Message.Length > 0, "上级有原因");

                Console.WriteLine();
            }
            finally { Clean(oldRoot); Clean(newRoot); }
        }

        // ---------- 2. 路径越界判定 ----------
        static void TestIsUnder()
        {
            Console.WriteLine("--- 2. IsUnder ---");

            Check(DeskRootChange.IsUnder(@"C:\a\b", @"C:\a"), "子目录");
            Check(!DeskRootChange.IsUnder(@"C:\a", @"C:\a"), "★相等不算子目录");
            Check(!DeskRootChange.IsUnder(@"C:\a", @"C:\a\b"), "反向不算");

            // 关键回归：前缀相同但不同目录
            Check(!DeskRootChange.IsUnder(@"C:\desks2", @"C:\desks"),
                "★C:\\desks2 不是 C:\\desks 的子目录");
            Check(!DeskRootChange.IsUnder(@"C:\desks2\x", @"C:\desks"),
                "★C:\\desks2\\x 不是 C:\\desks 的子目录");

            Check(DeskRootChange.IsUnder(@"C:\desks\x", @"C:\desks"), "真正的子目录仍判 true");
            Check(DeskRootChange.IsUnder(@"C:\A\B", @"c:\a"), "★大小写不敏感");

            Check(!DeskRootChange.IsUnder("", @"C:\a"), "空串不算");
            Check(!DeskRootChange.IsUnder(@"C:\a", ""), "父空不算");

            Console.WriteLine();
        }

        // ---------- 3. 盘点 ----------
        static void TestInspect()
        {
            Console.WriteLine("--- 3. 盘点 ---");

            string root = NewDir("plan");
            try
            {
                // 工作：2 个文件 + 1 个子文件夹（内含 1 个文件）
                string work = Path.Combine(root, "工作");
                Directory.CreateDirectory(Path.Combine(work, "子"));
                File.WriteAllText(Path.Combine(work, "a.txt"), "1");
                File.WriteAllText(Path.Combine(work, "b.txt"), "22");
                File.WriteAllText(Path.Combine(work, "子", "c.txt"), "333");

                // 空的桌面
                Directory.CreateDirectory(Path.Combine(root, "空桌面"));

                // 目录里还有用户自己丢的无关文件夹 → 不在 names 里，不统计
                Directory.CreateDirectory(Path.Combine(root, "无关"));
                File.WriteAllText(Path.Combine(root, "无关", "x.txt"), "x");

                List<string> names = new List<string> { "工作", "空桌面", "没建过" };
                DeskRootChange.Plan p = DeskRootChange.Inspect(root, names);

                Check(p.DeskCount == 2, "★只统计真实存在的桌面", p.DeskCount.ToString());
                Check(p.Files == 3, "★文件数含子文件夹", p.Files.ToString());
                Check(p.Entries == 3, "★顶层项数 = 2 文件 + 1 文件夹", p.Entries.ToString());
                Check(!p.Empty, "非空");

                // 无关文件夹没被算进去
                Check(!p.Desks.Contains("无关"), "★不在清单里的文件夹不统计");

                // 空清单
                Check(DeskRootChange.Inspect(root, new List<string>()).DeskCount == 0, "空清单 → 0 个桌面");
                Check(DeskRootChange.Inspect(root, null).DeskCount == 0, "null 清单 → 0 个桌面");
                Check(DeskRootChange.Inspect(null, names).DeskCount == 0, "null 根 → 0 个桌面");

                // 非法名字被跳过
                DeskRootChange.Plan p2 = DeskRootChange.Inspect(root,
                    new List<string> { "..", "工作" });
                Check(p2.DeskCount == 1, "★非法桌面名被跳过", p2.DeskCount.ToString());

                Console.WriteLine();
            }
            finally { Clean(root); }
        }

        // ---------- 4. 正常迁移 ----------
        static void TestMigrateHappy()
        {
            Console.WriteLine("--- 4. 正常迁移 ---");

            string oldRoot = NewDir("mig-old");
            string newRoot = NewDir("mig-new");
            try
            {
                string work = Path.Combine(oldRoot, "工作");
                Directory.CreateDirectory(Path.Combine(work, "子"));
                File.WriteAllText(Path.Combine(work, "a.txt"), "1");
                File.WriteAllText(Path.Combine(work, "子", "b.txt"), "22");

                string play = Path.Combine(oldRoot, "娱乐");
                Directory.CreateDirectory(play);
                File.WriteAllText(Path.Combine(play, "c.txt"), "333");

                List<string> names = new List<string> { "工作", "娱乐", "没建过" };
                DeskRootChange.Result r = DeskRootChange.Migrate(oldRoot, newRoot, names);

                Check(r.AllOk, "★全部成功", string.Join(";", r.Reasons.ToArray()));
                Check(r.MovedDesks == 2, "搬了 2 个桌面", r.MovedDesks.ToString());
                Check(r.MovedFiles == 3, "★搬了 3 个文件", r.MovedFiles.ToString());
                Check(r.Skipped == 1, "★不存在的桌面计入跳过", r.Skipped.ToString());

                Check(File.Exists(Path.Combine(newRoot, "工作", "a.txt")), "★文件到了新位置");
                Check(File.Exists(Path.Combine(newRoot, "工作", "子", "b.txt")), "★子文件夹跟着走");
                Check(File.Exists(Path.Combine(newRoot, "娱乐", "c.txt")), "第二个桌面也到了");

                Check(!Directory.Exists(Path.Combine(oldRoot, "工作")), "★旧位置已清空");
                Check(!Directory.Exists(Path.Combine(oldRoot, "娱乐")), "★旧位置已清空（第二个）");

                // 内容核对（不只是存在）
                Check(File.ReadAllText(Path.Combine(newRoot, "娱乐", "c.txt")) == "333",
                    "★文件内容完整");

                Console.WriteLine();
            }
            finally { Clean(oldRoot); Clean(newRoot); }
        }

        // ---------- 5. 预检不过 → 一个都不搬 ----------
        static void TestMigratePrecheckAllOrNothing()
        {
            Console.WriteLine("--- 5. 预检不过时全有或全无 ---");

            string oldRoot = NewDir("pre-old");
            string newRoot = NewDir("pre-new");
            try
            {
                string a = Path.Combine(oldRoot, "甲");
                Directory.CreateDirectory(a);
                File.WriteAllText(Path.Combine(a, "a.txt"), "1");

                string b = Path.Combine(oldRoot, "乙");
                Directory.CreateDirectory(b);
                File.WriteAllText(Path.Combine(b, "b.txt"), "2");

                // 新目录里已经有一个同名文件夹 → 预检就该拦下
                Directory.CreateDirectory(Path.Combine(newRoot, "乙"));
                File.WriteAllText(Path.Combine(newRoot, "乙", "占位.txt"), "z");

                DeskRootChange.Result r = DeskRootChange.Migrate(oldRoot, newRoot,
                    new List<string> { "甲", "乙" });

                Check(!r.AllOk, "★报告失败");
                Check(r.Failed.Count == 1 && r.Failed[0] == "乙", "★失败项是冲突的那个");
                Check(r.MovedDesks == 0, "★一个都没搬");
                Check(r.MovedFiles == 0, "★文件数 0");

                Check(Directory.Exists(Path.Combine(oldRoot, "甲")), "★甲仍在原处");
                Check(File.Exists(Path.Combine(oldRoot, "甲", "a.txt")), "★甲的文件仍在原处");
                Check(!Directory.Exists(Path.Combine(newRoot, "甲")), "★甲没被搬过去");
                Check(File.Exists(Path.Combine(newRoot, "乙", "占位.txt")), "★已存在的目标未被覆盖");

                Console.WriteLine();
            }
            finally { Clean(oldRoot); Clean(newRoot); }
        }

        // ---------- 6. 中途失败 → 回滚 ----------
        static void TestMigrateRollback()
        {
            Console.WriteLine("--- 6. 中途失败回滚 ---");

            string oldRoot = NewDir("rb-old");
            string newRoot = NewDir("rb-new");
            FileStream locked = null;
            try
            {
                string a = Path.Combine(oldRoot, "甲");
                Directory.CreateDirectory(a);
                File.WriteAllText(Path.Combine(a, "a.txt"), "1");

                string b = Path.Combine(oldRoot, "乙");
                Directory.CreateDirectory(b);
                string bFile = Path.Combine(b, "b.txt");
                File.WriteAllText(bFile, "2");

                // 独占打开乙里的文件：预检看不出来，真搬的时候复制会失败
                locked = new FileStream(bFile, FileMode.Open, FileAccess.Read, FileShare.None);

                DeskRootChange.Result r = DeskRootChange.Migrate(oldRoot, newRoot,
                    new List<string> { "甲", "乙" });

                Check(!r.AllOk, "★报告失败");
                Check(r.MovedDesks == 0, "★回滚后搬走计数归零", r.MovedDesks.ToString());
                Check(r.MovedFiles == 0, "★回滚后文件计数归零", r.MovedFiles.ToString());

                Check(Directory.Exists(Path.Combine(oldRoot, "甲")), "★甲被搬回原处");
                Check(File.Exists(Path.Combine(oldRoot, "甲", "a.txt")), "★甲的文件回来了");
                Check(!Directory.Exists(Path.Combine(newRoot, "甲")), "★新目录里没有残留的甲");
                Check(Directory.Exists(Path.Combine(oldRoot, "乙")), "★乙没动");

                bool saidRollback = false;
                foreach (string s in r.Reasons)
                    if (s.Contains("回滚")) { saidRollback = true; break; }
                Check(saidRollback, "★结果里说明了已回滚");

                Console.WriteLine();
            }
            finally
            {
                if (locked != null) { locked.Dispose(); locked = null; }
                Clean(oldRoot); Clean(newRoot);
            }
        }

        // ---------- 7. 对照：单遍实现确实会半搬 ----------
        static void TestOldImplementationLeavesHalf()
        {
            Console.WriteLine("--- 7. 对照：单遍实现会半搬 ---");

            string oldRoot = NewDir("half-old");
            string newRoot = NewDir("half-new");
            try
            {
                Directory.CreateDirectory(Path.Combine(oldRoot, "甲"));
                File.WriteAllText(Path.Combine(oldRoot, "甲", "a.txt"), "1");

                Directory.CreateDirectory(Path.Combine(oldRoot, "乙"));
                File.WriteAllText(Path.Combine(oldRoot, "乙", "b.txt"), "2");

                Directory.CreateDirectory(Path.Combine(newRoot, "乙"));   // 乙会冲突

                DeskRootChange.Result r = DeskRootChange.MigrateOneByOne(oldRoot, newRoot,
                    new List<string> { "甲", "乙" });

                Check(!r.AllOk, "单遍：报告失败");
                Check(r.MovedDesks == 1, "★单遍：甲已经搬走了（这正是要避免的半搬状态）",
                    r.MovedDesks.ToString());
                Check(!Directory.Exists(Path.Combine(oldRoot, "甲")), "★单遍：旧位置已没有甲");
                Check(Directory.Exists(Path.Combine(newRoot, "甲")), "★单遍：新位置有甲");

                Console.WriteLine();
            }
            finally { Clean(oldRoot); Clean(newRoot); }
        }
    }
}
