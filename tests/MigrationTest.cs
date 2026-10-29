using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// Migration 测试：分类隐藏 → 逐条隐藏 的一次性迁移。
    ///
    /// 全部用临时目录 + 构造的假数据，不碰真实 config。
    /// </summary>
    static class MigrationTest
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
            Console.WriteLine("=== Migration 测试 ===");
            Console.WriteLine();

            TestBasicMigration();
            TestIdempotent();
            TestNoCategoryField();
            TestEmptyHiddenCategories();
            TestPreservesExistingHidden();
            TestCorruptHiddenFileNotOverwritten();
            TestMissingAppsFile();
            TestMarkerFileWritten();
            TestMarkerPolicy();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 基本迁移 ----------
        static void TestBasicMigration()
        {
            Console.WriteLine("--- 1. 基本迁移 ---");

            string root = NewRoot();
            try
            {
                // 旧配置：隐藏「系统工具」
                WriteSettings(root, new string[] { "系统工具" });
                // 旧 apps.json：4 条，2 条属于系统工具
                WriteApps(root,
                    new string[] { "cmd", @"C:\Windows\System32\cmd.exe", "系统工具" },
                    new string[] { "regedit", @"C:\Windows\regedit.exe", "系统工具" },
                    new string[] { "微信", @"D:\Weixin\Weixin.exe", "网络通讯" },
                    new string[] { "Word", @"C:\Office\WINWORD.EXE", "办公" });

                Migration.Result r = Migration.Run(root, Settings.Load(root));

                Check(r.Performed, "迁移执行了", r.SkipReason ?? "");
                Check(r.TotalApps == 4, "读到 4 条", "实际 " + r.TotalApps);
                Check(r.AppsWithCategory == 4, "4 条都有 category", "实际 " + r.AppsWithCategory);
                Check(r.Matched == 2, "匹配到 2 条系统工具", "实际 " + r.Matched);
                Check(r.NewlyHidden == 2, "新隐藏 2 条", "实际 " + r.NewlyHidden);

                HiddenApps h = HiddenApps.Load(root);
                Check(h.Count == 2, "hidden-apps.json 有 2 条", "实际 " + h.Count);
                Check(h.IsHidden(@"C:\Windows\System32\cmd.exe"), "cmd 被隐藏");
                Check(h.IsHidden(@"C:\Windows\regedit.exe"), "regedit 被隐藏");
                Check(!h.IsHidden(@"D:\Weixin\Weixin.exe"), "微信未被隐藏");
                Check(!h.IsHidden(@"C:\Office\WINWORD.EXE"), "Word 未被隐藏");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 2. 幂等 ----------
        static void TestIdempotent()
        {
            Console.WriteLine("--- 2. 幂等（重复运行结果不变）---");

            string root = NewRoot();
            try
            {
                WriteSettings(root, new string[] { "系统工具" });
                WriteApps(root,
                    new string[] { "cmd", @"C:\Windows\cmd.exe", "系统工具" },
                    new string[] { "微信", @"D:\Weixin.exe", "网络通讯" });

                Migration.Result r1 = Migration.Run(root, Settings.Load(root));
                Check(r1.Performed, "第一次执行了迁移");
                Check(r1.NewlyHidden == 1, "第一次新隐藏 1 条", "实际 " + r1.NewlyHidden);

                HiddenApps after1 = HiddenApps.Load(root);
                List<string> snap1 = after1.Snapshot();

                // 第二次：标记文件已存在，应该跳过
                Migration.Result r2 = Migration.Run(root, Settings.Load(root));
                Check(!r2.Performed, "第二次跳过（标记文件存在）", r2.SkipReason ?? "");
                Check(r2.SkipReason != null && r2.SkipReason.IndexOf("已迁移过", StringComparison.Ordinal) >= 0,
                    "跳过原因是「已迁移过」", r2.SkipReason ?? "null");

                HiddenApps after2 = HiddenApps.Load(root);
                Check(after2.Count == snap1.Count, "第二次后隐藏数不变",
                    snap1.Count + " → " + after2.Count);

                // 第三次：删掉标记文件再跑，靠 Hide 去重保证结果一致
                try { File.Delete(Migration.MarkerPath(root)); } catch { }
                Migration.Result r3 = Migration.Run(root, Settings.Load(root));
                HiddenApps after3 = HiddenApps.Load(root);
                Check(after3.Count == snap1.Count, "删标记后重跑：隐藏数仍不变（Hide 去重）",
                    snap1.Count + " → " + after3.Count);
                Check(r3.AlreadyHidden == 1, "重跑时 1 条被判为「已隐藏」", "实际 " + r3.AlreadyHidden);
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 3. apps.json 无 category 字段 → 跳过 ----------
        static void TestNoCategoryField()
        {
            Console.WriteLine("--- 3. apps.json 无 category 字段 ---");

            string root = NewRoot();
            try
            {
                WriteSettings(root, new string[] { "系统工具" });
                // 分类移除后的新格式：没有 category
                WriteAppsNoCategory(root,
                    new string[] { "cmd", @"C:\Windows\cmd.exe" },
                    new string[] { "微信", @"D:\Weixin.exe" });

                Migration.Result r = Migration.Run(root, Settings.Load(root));

                Check(!r.Performed, "不执行迁移", r.SkipReason ?? "");
                Check(r.AppsWithCategory == 0, "有 category 的条目为 0", "实际 " + r.AppsWithCategory);
                Check(r.SkipReason != null && r.SkipReason.IndexOf("category", StringComparison.OrdinalIgnoreCase) >= 0,
                    "跳过原因提到 category", r.SkipReason ?? "null");

                HiddenApps h = HiddenApps.Load(root);
                Check(h.Count == 0, "没有隐藏任何应用", "实际 " + h.Count);
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 4. hiddenCategories 为空 → 跳过 ----------
        static void TestEmptyHiddenCategories()
        {
            Console.WriteLine("--- 4. hiddenCategories 为空 ---");

            string root = NewRoot();
            try
            {
                WriteSettings(root, new string[0]);
                WriteApps(root,
                    new string[] { "cmd", @"C:\Windows\cmd.exe", "系统工具" });

                Migration.Result r = Migration.Run(root, Settings.Load(root));

                Check(!r.Performed, "不执行迁移", r.SkipReason ?? "");
                Check(r.SkipReason != null && r.SkipReason.IndexOf("hiddenCategories", StringComparison.Ordinal) >= 0,
                    "跳过原因提到 hiddenCategories", r.SkipReason ?? "null");

                HiddenApps h = HiddenApps.Load(root);
                Check(h.Count == 0, "没有隐藏任何应用");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 5. 保留已有隐藏项 ----------
        static void TestPreservesExistingHidden()
        {
            Console.WriteLine("--- 5. 不覆盖已有隐藏项 ---");

            string root = NewRoot();
            try
            {
                WriteSettings(root, new string[] { "系统工具" });
                WriteApps(root,
                    new string[] { "cmd", @"C:\Windows\cmd.exe", "系统工具" },
                    new string[] { "微信", @"D:\Weixin.exe", "网络通讯" });

                // 用户之前已手动隐藏了微信
                HiddenApps pre = HiddenApps.Load(root);
                pre.Hide(@"D:\Weixin.exe");
                Check(pre.Count == 1, "预置：微信已隐藏");

                Migration.Result r = Migration.Run(root, Settings.Load(root));

                Check(r.Performed, "迁移执行了");
                Check(r.NewlyHidden == 1, "新隐藏 1 条（cmd）", "实际 " + r.NewlyHidden);

                HiddenApps h = HiddenApps.Load(root);
                Check(h.Count == 2, "总共 2 条（微信 + cmd）", "实际 " + h.Count);
                Check(h.IsHidden(@"D:\Weixin.exe"), "★原有的微信隐藏项被保留");
                Check(h.IsHidden(@"C:\Windows\cmd.exe"), "新增的 cmd 隐藏项");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 6. hidden-apps.json 损坏 → 不覆盖 ----------
        static void TestCorruptHiddenFileNotOverwritten()
        {
            Console.WriteLine("--- 6. hidden-apps.json 读不出时不覆盖 ---");

            string root = NewRoot();
            try
            {
                WriteSettings(root, new string[] { "系统工具" });
                WriteApps(root,
                    new string[] { "cmd", @"C:\Windows\cmd.exe", "系统工具" });

                // 造一个"存在但结构不对"的 hidden-apps.json
                string hiddenPath = HiddenApps.PathOf(root);
                string corrupt = "{\"version\":1,\"数据\":\"损坏了\"}";
                File.WriteAllText(hiddenPath, corrupt, new UTF8Encoding(false));

                Migration.Result r = Migration.Run(root, Settings.Load(root));

                Check(!r.Performed, "不执行迁移（保护原文件）", r.SkipReason ?? "");
                Check(r.SkipReason != null && r.SkipReason.IndexOf("读不出", StringComparison.Ordinal) >= 0,
                    "跳过原因说明读不出", r.SkipReason ?? "null");

                // 原文件必须没被动过
                string after = File.ReadAllText(hiddenPath, Encoding.UTF8);
                Check(after == corrupt, "★原文件内容完全未被修改");
                Check(!r.ShouldWriteMarker, "★不写标记（下次启动重试）");
                Check(!File.Exists(Migration.MarkerPath(root)), "★标记文件未创建");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 7. apps.json 不存在 ----------
        static void TestMissingAppsFile()
        {
            Console.WriteLine("--- 7. apps.json 不存在 ---");

            string root = NewRoot();
            try
            {
                WriteSettings(root, new string[] { "系统工具" });
                // 不写 apps.json

                Migration.Result r = Migration.Run(root, Settings.Load(root));

                Check(!r.Performed, "不执行迁移", r.SkipReason ?? "");
                Check(r.SkipReason != null && r.SkipReason.IndexOf("apps.json", StringComparison.Ordinal) >= 0,
                    "跳过原因提到 apps.json", r.SkipReason ?? "null");
                Check(!r.ShouldWriteMarker, "apps.json 缺失是临时状态 → 不写标记");
                Check(!File.Exists(Migration.MarkerPath(root)), "标记文件未创建（下次重试）");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 8. 标记文件内容 ----------
        static void TestMarkerFileWritten()
        {
            Console.WriteLine("--- 8. 标记文件 ---");

            string root = NewRoot();
            try
            {
                WriteSettings(root, new string[] { "系统工具", "开发" });
                WriteApps(root,
                    new string[] { "cmd", @"C:\Windows\cmd.exe", "系统工具" },
                    new string[] { "devenv", @"C:\VS\devenv.exe", "开发" },
                    new string[] { "微信", @"D:\Weixin.exe", "网络通讯" });

                Migration.Result r = Migration.Run(root, Settings.Load(root));

                Check(File.Exists(Migration.MarkerPath(root)), "标记文件已创建");
                Check(r.Matched == 2, "匹配 2 条（系统工具 + 开发）", "实际 " + r.Matched);
                Check(r.HiddenCategories.Count == 2, "记录了 2 个旧隐藏分类",
                    "实际 " + r.HiddenCategories.Count);

                // 标记文件是合法 JSON 且记录 performed=true
                string json = File.ReadAllText(Migration.MarkerPath(root), Encoding.UTF8);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                Check(d != null, "标记文件是合法 JSON");
                if (d != null)
                {
                    Check(Convert.ToBoolean(d["performed"]), "标记文件记录 performed=true");
                    Check(Convert.ToInt32(d["newlyHidden"]) == 2, "标记文件记录 newlyHidden=2",
                        Convert.ToString(d["newlyHidden"]));
                }
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 9. 终态写标记 / 临时错误不写 ----------
        static void TestMarkerPolicy()
        {
            Console.WriteLine("--- 9. 标记写入策略 ---");

            // 终态 1：hiddenCategories 为空 → 写标记
            {
                string root = NewRoot();
                try
                {
                    WriteSettings(root, new string[0]);
                    WriteApps(root, new string[] { "cmd", @"C:\Windows\cmd.exe", "系统工具" });

                    Migration.Result r = Migration.Run(root, Settings.Load(root));
                    Check(!r.Performed, "hiddenCategories 为空：不迁移");
                    Check(r.ShouldWriteMarker, "★终态 → 应该写标记");
                    Check(File.Exists(Migration.MarkerPath(root)), "标记文件已创建");
                }
                finally { Clean(root); }
            }

            // 终态 2：apps.json 无 category → 写标记
            {
                string root = NewRoot();
                try
                {
                    WriteSettings(root, new string[] { "系统工具" });
                    WriteAppsNoCategory(root, new string[] { "cmd", @"C:\Windows\cmd.exe" });

                    Migration.Result r = Migration.Run(root, Settings.Load(root));
                    Check(!r.Performed, "无 category：不迁移");
                    Check(r.ShouldWriteMarker, "★终态（旧数据已不存在）→ 应该写标记");
                    Check(File.Exists(Migration.MarkerPath(root)), "标记文件已创建");
                }
                finally { Clean(root); }
            }

            // 终态 3：没有条目匹配 → 写标记
            {
                string root = NewRoot();
                try
                {
                    WriteSettings(root, new string[] { "不存在的分类" });
                    WriteApps(root, new string[] { "cmd", @"C:\Windows\cmd.exe", "系统工具" });

                    Migration.Result r = Migration.Run(root, Settings.Load(root));
                    Check(!r.Performed, "无匹配条目：不迁移");
                    Check(r.ShouldWriteMarker, "★终态（没有需要迁移的条目）→ 应该写标记");
                }
                finally { Clean(root); }
            }

            // 临时 1：baseDir 为空 → 不写
            {
                Migration.Result r = Migration.Run(null, null);
                Check(!r.Performed, "baseDir 为空：不迁移");
                Check(!r.ShouldWriteMarker, "★临时错误 → 不写标记");
            }

            // 临时 2：apps.json 不存在 → 不写
            {
                string root = NewRoot();
                try
                {
                    WriteSettings(root, new string[] { "系统工具" });
                    Migration.Result r = Migration.Run(root, Settings.Load(root));
                    Check(!r.Performed, "apps.json 不存在：不迁移");
                    Check(!r.ShouldWriteMarker, "★临时错误 → 不写标记");
                    Check(!File.Exists(Migration.MarkerPath(root)), "标记文件未创建");
                }
                finally { Clean(root); }
            }
            Console.WriteLine();
        }

        // ---------- 辅助 ----------

        static string NewRoot()
        {
            string d = Path.Combine(Path.GetTempPath(), "migrate-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            Directory.CreateDirectory(Path.Combine(d, "config"));
            return d;
        }

        static void Clean(string root)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }

        static void WriteSettings(string root, string[] hiddenCategories)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["theme"] = "dark";
            d["hiddenCategories"] = new List<string>(hiddenCategories);

            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            File.WriteAllText(Path.Combine(root, "config", "settings.json"),
                ser.Serialize(d), new UTF8Encoding(false));
        }

        /// <summary>写带 category 字段的旧格式 apps.json。</summary>
        static void WriteApps(string root, params string[][] rows)
        {
            List<object> arr = new List<object>();
            foreach (string[] r in rows)
            {
                Dictionary<string, object> m = new Dictionary<string, object>();
                m["name"] = r[0];
                m["path"] = r[1];
                m["category"] = r[2];
                arr.Add(m);
            }
            WriteAppsCore(root, arr);
        }

        /// <summary>写不带 category 字段的新格式 apps.json。</summary>
        static void WriteAppsNoCategory(string root, params string[][] rows)
        {
            List<object> arr = new List<object>();
            foreach (string[] r in rows)
            {
                Dictionary<string, object> m = new Dictionary<string, object>();
                m["name"] = r[0];
                m["path"] = r[1];
                arr.Add(m);
            }
            WriteAppsCore(root, arr);
        }

        static void WriteAppsCore(string root, List<object> arr)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["updated"] = "2026-01-01 00:00:00";
            d["apps"] = arr;

            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            File.WriteAllText(Path.Combine(root, "config", "apps.json"),
                ser.Serialize(d), new UTF8Encoding(false));
        }
    }
}
