using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 「文件」标签拖文件夹加来源的规则测试（接入点 3）。
    ///
    /// 重点是 `DropSpec.ForFolderSource(remaining)` 在**真实界面路径**上的行为：
    /// `AllowDirectoriesOnly` 和 `MaxFiles` 第一次被真实使用，必须与
    /// DropSpecTest 里步骤 1 的断言一致。
    ///
    /// **全部用临时目录，不碰真实 config。**
    /// </summary>
    static class FolderSourceDropTest
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
            Console.WriteLine("=== 文件夹来源拖入测试（接入点 3）===");
            Console.WriteLine();

            try
            {
                TestHintFromConstants();
                TestMixedDrop();
                TestRemainingShortfall();
                TestRemainingZero();
                TestFieldBehavior();
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
                "foldrop-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(d);
            return d;
        }

        static void Clean(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }

        // ---------- 1. 文案由常量生成 ----------
        static void TestHintFromConstants()
        {
            Console.WriteLine("--- 1. 约束行 / 已满文案由常量生成 ---");

            string hint = DropSpec.FolderHintText();
            string full = DropSpec.FolderFullText();

            Check(hint == "只收文件夹，最多 10 个", "★约束行完整", hint);
            Check(full == "已达上限 10 个", "★已满文案完整", full);

            // 上限来自 DropSpec.FolderSourceMax（FileFolders.MaxFolders 反向引用它）
            Check(hint.IndexOf(DropSpec.FolderSourceMax.ToString(), StringComparison.Ordinal) >= 0,
                "★约束行里的数字就是常量", hint);
            Check(full.IndexOf(DropSpec.FolderSourceMax.ToString(), StringComparison.Ordinal) >= 0,
                "★已满文案里的数字就是常量", full);

            // 全角标点
            Check(hint.IndexOf('，') > 0, "★约束行用全角逗号", hint);
            Check(hint.IndexOf(',') < 0, "★约束行没有半角逗号", hint);

            // 措辞是「只收文件夹」而不是「不支持文件」
            Check(hint.IndexOf("只收文件夹", StringComparison.Ordinal) >= 0,
                "★措辞是「只收文件夹」", hint);
            Check(hint.IndexOf("不支持", StringComparison.Ordinal) < 0,
                "★不写成「不支持」", hint);

            Check(hint.Contains(DropSpec.FolderSourceMax.ToString()), "★与 FolderSourceMax 同源");

            Console.WriteLine();
        }

        // ---------- 2. 混合拖入：文件夹被收，文件被拒 ----------
        static void TestMixedDrop()
        {
            Console.WriteLine("--- 2. 文件夹 + 文件混合 ---");

            string dir = NewDir("mix");
            try
            {
                string f1 = Path.Combine(dir, "src1");
                string f2 = Path.Combine(dir, "src2");
                Directory.CreateDirectory(f1);
                Directory.CreateDirectory(f2);
                string file = Path.Combine(dir, "note.txt");
                File.WriteAllText(file, "x");

                // 剩余名额充足
                DropSpec spec = DropSpec.ForFolderSource(DropSpec.FolderSourceMax);
                DropSpec.Validation v = spec.Validate(new string[] { f1, file, f2 });

                Check(v.Accepted.Count == 2, "★收 2 个文件夹", Convert.ToString(v.Accepted.Count));
                Check(v.RejectedItems.Count == 1, "★拒 1 个文件", Convert.ToString(v.RejectedItems.Count));
                Check(v.RejectedItems[0].Reason == DropSpec.RejectReason.NotDirectory,
                    "★原因是「不是文件夹」", v.RejectedItems[0].Reason.ToString());

                // 汇总文案（走 BuildSummary，不另写措辞）
                //
                // 单原因时会带上扩展名（.txt）—— 这是 BuildSummary 的既定行为：
                // 单原因才列扩展名，多原因只报数量。这里如实断言。
                string s = DropSpec.BuildSummary(2, v.RejectedItems);
                Check(s == "已放入 2 个，跳过 1 个不是文件夹（.txt）", "★汇总文案", s);

                // 文件真的没被当成来源（Accepted 里只有两个目录）
                bool noFile = true;
                foreach (string p in v.Accepted)
                    if (string.Equals(p, file, StringComparison.OrdinalIgnoreCase)) noFile = false;
                Check(noFile, "★文件不在 Accepted 里");

                Console.WriteLine();
            }
            finally { Clean(dir); }
        }

        // ---------- 3. 名额不足：3 个目录只剩 2 个名额 ----------
        static void TestRemainingShortfall()
        {
            Console.WriteLine("--- 3. 剩余名额不足（3 个目录 / 2 个名额）---");

            string dir = NewDir("short");
            try
            {
                string[] dirs = new string[3];
                for (int i = 0; i < 3; i++)
                {
                    dirs[i] = Path.Combine(dir, "src" + i);
                    Directory.CreateDirectory(dirs[i]);
                }

                DropSpec spec = DropSpec.ForFolderSource(2);
                DropSpec.Validation v = spec.Validate(dirs);

                Check(v.Accepted.Count == 2, "★收 2 个", Convert.ToString(v.Accepted.Count));
                Check(v.RejectedItems.Count == 1, "★拒 1 个", Convert.ToString(v.RejectedItems.Count));
                Check(v.RejectedItems[0].Reason == DropSpec.RejectReason.TooMany,
                    "★原因是超出数量", v.RejectedItems[0].Reason.ToString());

                // 被拒的是第三个（保持传入顺序）
                Check(Path.GetFileName(v.RejectedItems[0].Path) == "src2",
                    "★被拒的是第 3 个", Path.GetFileName(v.RejectedItems[0].Path));

                string s = DropSpec.BuildSummary(2, v.RejectedItems);
                Check(s == "已放入 2 个，跳过 1 个超出数量上限", "★汇总文案", s);

                Console.WriteLine();
            }
            finally { Clean(dir); }
        }

        // ---------- 4. 名额为 0：全部被拒 ----------
        static void TestRemainingZero()
        {
            Console.WriteLine("--- 4. remaining = 0（已满）全部被拒 ---");

            string dir = NewDir("zero");
            try
            {
                string f1 = Path.Combine(dir, "src1");
                string f2 = Path.Combine(dir, "src2");
                Directory.CreateDirectory(f1);
                Directory.CreateDirectory(f2);

                // ForFolderSource(0) 的语义：MaxFiles=0 → 不限。
                // 所以**不能**用它来表达"已满"，调用方必须显式判断
                // remaining <= 0 并直接构造 TooMany 拒绝。
                DropSpec spec = DropSpec.ForFolderSource(0);
                Check(spec.MaxFiles == 0, "★ForFolderSource(0) → MaxFiles=0（不限）",
                    Convert.ToString(spec.MaxFiles));

                DropSpec.Validation v = spec.Validate(new string[] { f1, f2 });
                Check(v.Accepted.Count == 2,
                    "★所以它不会拒绝（证明调用方必须自己判 remaining<=0）",
                    Convert.ToString(v.Accepted.Count));

                // 模拟 FilesTabView.HandleFolderDrop 里 remaining<=0 的分支：
                // 直接构造 TooMany 拒绝列表
                List<DropSpec.Rejected> all = new List<DropSpec.Rejected>();
                foreach (string p in new string[] { f1, f2 })
                {
                    DropSpec.Rejected r = new DropSpec.Rejected();
                    r.Path = p;
                    r.Name = Path.GetFileName(p);
                    r.Reason = DropSpec.RejectReason.TooMany;
                    all.Add(r);
                }

                string s = DropSpec.BuildSummary(0, all);
                Check(s == "没有放入任何文件，跳过 2 个超出数量上限", "★已满时的汇总文案", s);

                Console.WriteLine();
            }
            finally { Clean(dir); }
        }

        // ---------- 5. 两个字段的真实行为（与步骤 1 一致）----------
        static void TestFieldBehavior()
        {
            Console.WriteLine("--- 5. AllowDirectoriesOnly / MaxFiles 行为 ---");

            string dir = NewDir("field");
            try
            {
                string folder = Path.Combine(dir, "f");
                Directory.CreateDirectory(folder);
                string file = Path.Combine(dir, "a.txt");
                File.WriteAllText(file, "x");

                // AllowDirectoriesOnly = true
                DropSpec s1 = DropSpec.ForFolderSource(10);
                Check(s1.AllowDirectoriesOnly, "★ForFolderSource 设了 AllowDirectoriesOnly");
                Check(s1.MaxFiles == 10, "★MaxFiles = 剩余名额", Convert.ToString(s1.MaxFiles));
                Check(s1.Accept.Count == 0, "Accept 为空（不限扩展名，靠目录判定）");
                Check(s1.RejectExts.Count == 0, "RejectExts 为空");

                // 目录通过，文件报 NotDirectory（不是 UnsupportedType）
                DropSpec.Validation v1 = s1.Validate(new string[] { folder, file });
                Check(v1.Accepted.Count == 1, "★目录通过");
                Check(v1.RejectedItems[0].Reason == DropSpec.RejectReason.NotDirectory,
                    "★文件报「不是文件夹」而非「不支持的类型」",
                    v1.RejectedItems[0].Reason.ToString());

                // MaxFiles 只统计已通过的项：混入文件不会占用名额
                DropSpec s2 = DropSpec.ForFolderSource(1);
                DropSpec.Validation v2 = s2.Validate(new string[] { file, folder });
                Check(v2.Accepted.Count == 1, "★文件不占名额，目录仍能收");
                Check(v2.RejectedItems.Count == 1
                    && v2.RejectedItems[0].Reason == DropSpec.RejectReason.NotDirectory,
                    "★只拒了文件，没报超数量");

                // 负数名额 → 0（不限）—— 调用方要自己防
                DropSpec s3 = DropSpec.ForFolderSource(-1);
                Check(s3.MaxFiles == 0, "★负数 → 0（不限）", Convert.ToString(s3.MaxFiles));

                Console.WriteLine();
            }
            finally { Clean(dir); }
        }
    }
}
