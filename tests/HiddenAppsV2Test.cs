using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// HiddenApps v2 测试：隐藏原因、规范化路径、例外名单、向后兼容、未知字段保留。
    ///
    /// **全部用临时目录，不碰真实 config。**
    /// </summary>
    static class HiddenAppsTest
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
            Console.WriteLine("=== HiddenApps v2 测试 ===");
            Console.WriteLine();

            HiddenApps.Warn = delegate(string s) { Console.WriteLine("  [warn] " + s); };

            TestNormalize();
            TestReason();
            TestV1Compat();
            TestV2Roundtrip();
            TestExceptions();
            TestUnknownFields();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static string NewRoot()
        {
            string d = Path.Combine(Path.GetTempPath(),
                "ha-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(d, "config"));
            return d;
        }

        static void WriteJson(string root, string json)
        {
            File.WriteAllText(HiddenApps.PathOf(root), json, new UTF8Encoding(false));
        }

        // ---------- 1. 规范化 ----------
        static void TestNormalize()
        {
            Console.WriteLine("--- 1. Normalize ---");

            Check(HiddenApps.Normalize(@"C:\A\B") == @"c:\a\b", "★大小写归一",
                HiddenApps.Normalize(@"C:\A\B"));
            Check(HiddenApps.Normalize("C:/A/B") == @"c:\a\b", "★正斜杠归一",
                HiddenApps.Normalize("C:/A/B"));
            Check(HiddenApps.Normalize(@"C:\A\B\") == @"c:\a\b", "★去尾斜杠",
                HiddenApps.Normalize(@"C:\A\B\"));
            Check(HiddenApps.Normalize(null) == "", "null → 空");
            Check(HiddenApps.Normalize("") == "", "空串 → 空");

            // 两种写法必须一致（reasons 的 key 靠这个对上）
            string a = HiddenApps.Normalize(@"C:\Program Files\X\a.lnk");
            string b = HiddenApps.Normalize(@"c:/program files/x/a.lnk");
            Check(a == b, "★等价写法一致", a + " vs " + b);

            Console.WriteLine();
        }

        // ---------- 2. HideReason ----------
        static void TestReason()
        {
            Console.WriteLine("--- 2. HideReason ---");

            HideReason m = HideReason.Manual();
            Check(m.Type == HideReason.Kind.Manual, "Manual 类型");
            Check(m.DisplayText == "手动隐藏", "★Manual 显示文字", m.DisplayText);
            Check(m.GroupKey == "manual", "Manual 组 key", m.GroupKey);
            Check(m.GroupOrder == 0, "★Manual 排序权重 0（最上面）", Convert.ToString(m.GroupOrder));

            HideReason b = HideReason.Batch("系统工具");
            Check(b.Type == HideReason.Kind.Batch, "Batch 类型");
            Check(b.DisplayText == "批量隐藏：系统工具", "★Batch 显示文字", b.DisplayText);
            Check(b.GroupKey == "batch:系统工具", "Batch 组 key", b.GroupKey);
            Check(b.GroupOrder == 1, "Batch 排序权重 1", Convert.ToString(b.GroupOrder));

            // 序列化往返
            Dictionary<string, object> d = b.ToDict();
            Check(Convert.ToString(d["kind"]) == "batch", "ToDict kind");
            Check(Convert.ToString(d["category"]) == "系统工具", "ToDict category");

            HideReason back = HideReason.FromDict(d);
            Check(back.Type == HideReason.Kind.Batch, "★FromDict 往返类型");
            Check(back.Category == "系统工具", "★FromDict 往返分类", back.Category);

            // 空分类
            HideReason empty = HideReason.Batch("");
            Check(empty.DisplayText == "批量隐藏：未分类", "空分类兜底", empty.DisplayText);

            // null 字典
            Check(HideReason.FromDict(null).Type == HideReason.Kind.Manual, "FromDict(null) → Manual");

            Console.WriteLine();
        }

        // ---------- 3. v1 向后兼容 ----------
        static void TestV1Compat()
        {
            Console.WriteLine("--- 3. v1 兼容（只有 paths）---");

            string root = NewRoot();
            try
            {
                WriteJson(root, "{\"version\":1,\"paths\":[\"C:\\\\A\\\\a.lnk\",\"C:\\\\A\\\\b.lnk\"]}");

                HiddenApps h = HiddenApps.Load(root);
                Check(h.Count == 2, "★读到 2 项", Convert.ToString(h.Count));
                Check(h.IsHidden(@"C:\A\a.lnk"), "IsHidden 大小写不敏感");

                // ★ 旧版本没有 reasons → 全部按手动隐藏
                HideReason r = h.ReasonOf(@"C:\A\a.lnk");
                Check(r.Type == HideReason.Kind.Manual, "★v1 项按手动隐藏处理");
                Check(r.DisplayText == "手动隐藏", "v1 项显示文字", r.DisplayText);

                Check(h.ExceptionCount == 0, "v1 没有例外名单");
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 4. v2 往返 ----------
        static void TestV2Roundtrip()
        {
            Console.WriteLine("--- 4. v2 往返 ---");

            string root = NewRoot();
            try
            {
                HiddenApps h = HiddenApps.Load(root);

                h.Hide(@"C:\A\a.lnk", HideReason.Manual());
                h.Hide(@"C:\A\b.lnk", HideReason.Batch("系统工具"));
                h.Hide(@"C:\A\c.lnk", HideReason.Batch("工具/命令行"));

                Check(h.Count == 3, "★隐藏 3 项", Convert.ToString(h.Count));

                // 重新读
                HiddenApps h2 = HiddenApps.Load(root);
                Check(h2.Count == 3, "★重读 3 项", Convert.ToString(h2.Count));

                Check(h2.ReasonOf(@"C:\A\a.lnk").Type == HideReason.Kind.Manual,
                    "★a 是手动隐藏");
                Check(h2.ReasonOf(@"C:\A\b.lnk").Type == HideReason.Kind.Batch,
                    "★b 是批量隐藏");
                Check(h2.ReasonOf(@"C:\A\b.lnk").Category == "系统工具",
                    "★b 的分类", h2.ReasonOf(@"C:\A\b.lnk").Category);
                Check(h2.ReasonOf(@"C:\A\c.lnk").Category == "工具/命令行",
                    "★c 的分类", h2.ReasonOf(@"C:\A\c.lnk").Category);

                // 大小写差异也能查到
                Check(h2.ReasonOf(@"c:/a/B.LNK").Category == "系统工具",
                    "★大小写+斜杠差异仍能查到原因");

                // 未记录项 → 手动
                Check(h2.ReasonOf(@"C:\not\there.lnk").Type == HideReason.Kind.Manual,
                    "★未记录 → 手动隐藏");

                // Show 移除
                Check(h2.Show(@"C:\A\a.lnk"), "Show 成功");
                Check(!h2.IsHidden(@"C:\A\a.lnk"), "Show 后不可见");
                Check(h2.Count == 2, "剩 2 项", Convert.ToString(h2.Count));

                // 原因也跟着删了
                HiddenApps h3 = HiddenApps.Load(root);
                Check(h3.Count == 2, "重读剩 2 项", Convert.ToString(h3.Count));
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 5. 例外名单 ----------
        static void TestExceptions()
        {
            Console.WriteLine("--- 5. 例外名单 ---");

            string root = NewRoot();
            try
            {
                HiddenApps h = HiddenApps.Load(root);

                h.Hide(@"C:\A\b.lnk", HideReason.Batch("系统工具"));
                Check(h.ExceptionCount == 0, "★初始无例外");

                // 从批量隐藏恢复 → 写例外
                h.ShowWithException(@"C:\A\b.lnk");
                Check(h.ExceptionCount == 1, "★ShowWithException 写入例外",
                    Convert.ToString(h.ExceptionCount));
                Check(h.IsException(@"C:\A\b.lnk"), "★IsException 为真");
                Check(!h.IsHidden(@"C:\A\b.lnk"), "已不再隐藏");

                // 重读保留
                HiddenApps h2 = HiddenApps.Load(root);
                Check(h2.ExceptionCount == 1, "★重读例外保留",
                    Convert.ToString(h2.ExceptionCount));
                Check(h2.IsException(@"c:/a/B.LNK"), "★例外大小写不敏感");

                // 移除例外
                Check(h2.RemoveException(@"C:\A\b.lnk"), "★移除例外成功");
                Check(h2.ExceptionCount == 0, "例外清空", Convert.ToString(h2.ExceptionCount));

                // 手动隐藏的项恢复 → 不写例外
                HiddenApps h3 = HiddenApps.Load(root);
                h3.Hide(@"C:\A\m.lnk", HideReason.Manual());
                h3.Show(@"C:\A\m.lnk");   // 普通 Show，不写例外
                Check(h3.ExceptionCount == 0, "★手动隐藏恢复不写例外",
                    Convert.ToString(h3.ExceptionCount));

                // 快照
                h3.ShowWithException(@"C:\A\x.lnk");
                List<string> snap = h3.ExceptionSnapshot();
                Check(snap.Count == 1, "★ExceptionSnapshot 1 条", Convert.ToString(snap.Count));
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 6. 未知字段保留 ----------
        static void TestUnknownFields()
        {
            Console.WriteLine("--- 6. 未知字段保留 ---");

            string root = NewRoot();
            try
            {
                // 造一个带未来字段的文件
                WriteJson(root,
                    "{\"version\":2,\"paths\":[\"C:\\\\A\\\\a.lnk\"],"
                    + "\"futureKey\":\"futureValue\",\"futureNum\":42}");

                HiddenApps h = HiddenApps.Load(root);
                Check(h.Count == 1, "读到 1 项", Convert.ToString(h.Count));

                // 触发一次写回
                h.Hide(@"C:\A\b.lnk", HideReason.Manual());

                string json = File.ReadAllText(HiddenApps.PathOf(root), Encoding.UTF8);
                Check(json.IndexOf("futureKey", StringComparison.Ordinal) >= 0,
                    "★未知字段 futureKey 保留");
                Check(json.IndexOf("futureValue", StringComparison.Ordinal) >= 0,
                    "★未知字段值保留");
                Check(json.IndexOf("futureNum", StringComparison.Ordinal) >= 0,
                    "★未知数字字段保留");

                // 正常字段也还在
                Check(json.IndexOf("\"paths\"", StringComparison.Ordinal) >= 0, "paths 存在");
                Check(json.IndexOf("\"reasons\"", StringComparison.Ordinal) >= 0, "★reasons 存在");
                Check(json.IndexOf("batchExceptions", StringComparison.Ordinal) >= 0,
                    "★batchExceptions 存在");
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }
    }
}
