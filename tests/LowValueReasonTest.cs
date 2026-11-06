using System;
using System.Collections.Generic;

namespace Clover
{
    /// <summary>
    /// LowValueRules.Explain 测试：原因分支、原因顺序、不误伤主程序、异常兜底。
    ///
    /// **纯逻辑**（失效判定用不存在的路径字符串，不创建/删除任何文件；
    /// 扩展名分支会创建临时文件，测完删掉，与 LowValueTest 同一套做法）。
    ///
    /// 这套测试的存在意义：Explain 是「低价值可解释」的数据来源，
    /// 原因报错比"判错低价值"更糟 —— 用户看到「低价值：目标不存在」却明明能启动，
    /// 会直接不信这个功能。所以每个分支的原因都要单独钉住。
    /// </summary>
    static class LowValueReasonTest
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
            Console.WriteLine("=== LowValueRules.Explain 原因测试 ===");
            Console.WriteLine();

            TestNotLow();
            TestMissingTarget();
            TestNonExecutableTarget();
            TestNameKeyword();
            TestKeywordOrder();
            TestMainExecutableGuard();
            TestReasonOrder();
            TestDetailReadable();
            TestIsLowValueWrapper();
            TestExceptionFallback();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 不是低价值 ----------
        static void TestNotLow()
        {
            Console.WriteLine("--- 1. 正常应用 → 无原因 ---");

            LowValueVerdict v = Explain("Google Chrome", null);
            Check(!v.IsLowValue, "★Chrome 不是低价值");
            Check(v.Reason == LowValueReason.None, "Chrome 的 Reason = None", v.Reason.ToString());
            Check(string.IsNullOrEmpty(v.Detail), "Chrome 的 Detail 为空", v.Detail);

            v = Explain("微信", null);
            Check(!v.IsLowValue && v.Reason == LowValueReason.None, "★微信 不是低价值且无原因");

            v = Explain("", null);
            Check(!v.IsLowValue && v.Reason == LowValueReason.None, "空名 → 无原因");

            v = Explain(null, null);
            Check(!v.IsLowValue && v.Reason == LowValueReason.None, "null 名 → 无原因");

            Console.WriteLine();
        }

        // ---------- 2. 失效快捷方式 ----------
        static void TestMissingTarget()
        {
            Console.WriteLine("--- 2. 目标不存在 ---");

            LowValueVerdict v = Explain("正常应用", @"Z:\definitely-not-exist-xyz\app.exe");
            Check(v.IsLowValue, "★目标不存在 → 低价值");
            Check(v.Reason == LowValueReason.MissingTarget, "★原因 = MissingTarget", v.Reason.ToString());
            Check(v.Detail.IndexOf("不存在", StringComparison.Ordinal) >= 0,
                "Detail 写明「不存在」", v.Detail);

            // null 目标不算失效（商店应用没有文件路径）
            v = Explain("普通应用", null);
            Check(!v.IsLowValue, "★null 目标不判失效");

            Console.WriteLine();
        }

        // ---------- 3. 非可执行目标 ----------
        static void TestNonExecutableTarget()
        {
            Console.WriteLine("--- 3. 目标是 .url/.txt/.pdf/.chm/.html ---");

            // ★ 扩展名判定必须在「目标存在」的前提下测 ——
            //   目标不存在会先命中 MissingTarget，测不到扩展名分支。
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "lvr-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                System.IO.Directory.CreateDirectory(tmp);

                foreach (string ext in new string[] { ".url", ".txt", ".pdf", ".chm", ".html", ".htm" })
                {
                    string p = System.IO.Path.Combine(tmp, "x" + ext);
                    System.IO.File.WriteAllText(p, "x");

                    LowValueVerdict v = Explain("普通应用名", p);
                    Check(v.IsLowValue, "★目标 " + ext + "（存在）→ 低价值", p);
                    Check(v.Reason == LowValueReason.NonExecutableTarget,
                        "★目标 " + ext + " → 原因 = NonExecutableTarget", v.Reason.ToString());
                    Check(v.Detail.IndexOf(ext, StringComparison.OrdinalIgnoreCase) >= 0,
                        "Detail 里带扩展名 " + ext, v.Detail);
                }

                foreach (string ext in new string[] { ".exe", ".lnk", ".docx" })
                {
                    string p = System.IO.Path.Combine(tmp, "x" + ext);
                    System.IO.File.WriteAllText(p, "x");
                    LowValueVerdict v = Explain("普通应用名", p);
                    Check(!v.IsLowValue && v.Reason == LowValueReason.None,
                        "目标 " + ext + "（存在）→ 不是低价值、无原因", p);
                }
            }
            finally
            {
                try { if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true); } catch { }
            }

            Console.WriteLine();
        }

        // ---------- 4. 名称关键词 ----------
        static void TestNameKeyword()
        {
            Console.WriteLine("--- 4. 名称命中关键词 ---");

            LowValueVerdict v = Explain("卸载 微信", null);
            Check(v.IsLowValue, "★「卸载 微信」→ 低价值");
            Check(v.Reason == LowValueReason.NameKeyword, "★原因 = NameKeyword", v.Reason.ToString());
            Check(v.Detail.IndexOf("卸载", StringComparison.Ordinal) >= 0,
                "★Detail 里带命中的关键词「卸载」", v.Detail);

            v = Explain("Release Notes", null);
            Check(v.IsLowValue && v.Reason == LowValueReason.NameKeyword,
                "「Release Notes」→ NameKeyword", v.Reason.ToString());

            v = Explain("用户手册", null);
            Check(v.IsLowValue && v.Reason == LowValueReason.NameKeyword,
                "「用户手册」→ NameKeyword", v.Reason.ToString());

            // 大小写不敏感：原因一样给
            v = Explain("UNINSTALL", null);
            Check(v.IsLowValue && v.Reason == LowValueReason.NameKeyword,
                "「UNINSTALL」→ NameKeyword", v.Reason.ToString());

            Console.WriteLine();
        }

        // ---------- 5. 命中的关键词写进 Detail ----------
        static void TestKeywordOrder()
        {
            Console.WriteLine("--- 5. Detail 用的是**实际命中**的关键词 ---");

            // NameKeywords 里 "卸载" 排在 "Uninstaller" 之前，
            // 「卸载程序」应报「卸载」而不是 "Uninstaller"
            LowValueVerdict v = Explain("卸载程序", null);
            Check(v.Detail.IndexOf("卸载", StringComparison.Ordinal) >= 0,
                "★「卸载程序」的 Detail 含「卸载」", v.Detail);

            // 只有英文命中时，报英文关键词
            v = Explain("WeChat Uninstall", null);
            Check(v.Detail.IndexOf("Uninstall", StringComparison.OrdinalIgnoreCase) >= 0,
                "★「WeChat Uninstall」的 Detail 含 Uninstall", v.Detail);

            Console.WriteLine();
        }

        // ---------- 6. 不误伤主程序（Geek Uninstaller）----------
        static void TestMainExecutableGuard()
        {
            Console.WriteLine("--- 6. 不误伤主程序（IsMainExecutable 保护）---");

            // ★ 验收要点第 9 条：Geek Uninstaller 仍不被判低价值。
            //
            // 保护链路（真实数据形态）：
            //   AppItem.Path 是**已存在的 .lnk 路径**（死链在扫描时就被 IsLiveLnk /
            //   FilterDeadLinks 清掉了，所以「目标不存在」这条不会先命中），
            //   于是 exeFileName = ExeNameOf(lnk) = "Geek Uninstaller"，
            //   与 Name 归一化后相等 → IsMainExecutable 命中 → 不做关键词判定。
            //
            // 夹具必须真的创建那个 .lnk 文件：用不存在的路径测，
            // 规则 1（失效快捷方式）会先命中，测不到主程序保护 ——
            // 这正是第一版测试写错的地方。
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "lvg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                System.IO.Directory.CreateDirectory(tmp);
                string lnk = System.IO.Path.Combine(tmp, "Geek Uninstaller.lnk");
                System.IO.File.WriteAllText(lnk, "x");

                string exe = LowValueRules.ExeNameOf(lnk);
                Check(string.Equals(exe, "Geek Uninstaller", StringComparison.OrdinalIgnoreCase),
                    "夹具：lnk 文件名取出的 exe 名 = Geek Uninstaller", exe);

                LowValueVerdict v = Explain("Geek Uninstaller", lnk, exe);
                Check(!v.IsLowValue, "★Geek Uninstaller 仍不被判低价值（真实 lnk 形态）");
                Check(v.Reason == LowValueReason.None,
                    "★Geek Uninstaller 的 Reason = None", v.Reason.ToString());
            }
            finally
            {
                try { if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true); } catch { }
            }

            // 同一套保护对其它"产品名含关键词"的主程序也要成立
            LowValueVerdict v2 = Explain("Notepad", null, "notepad");
            Check(!v2.IsLowValue, "★Notepad ↔ notepad.exe → 主程序，不判低价值");

            v2 = Explain("Help & Manual", null, "Help & Manual");
            Check(!v2.IsLowValue, "★「Help & Manual」↔ 同名 exe → 主程序，不判低价值");

            // 但**卸载入口**（名字是「卸载 X」，exe 是 unins000）仍要判低价值
            v2 = Explain("卸载 微信", null, "unins000");
            Check(v2.IsLowValue && v2.Reason == LowValueReason.NameKeyword,
                "★「卸载 微信」+ unins000.exe → 仍判低价值");

            Console.WriteLine();
        }

        // ---------- 7. 原因优先级 ----------
        static void TestReasonOrder()
        {
            Console.WriteLine("--- 7. 判定顺序（原因不能报错）---");

            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "lvo-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                System.IO.Directory.CreateDirectory(tmp);

                // 目标不存在 + 名字含「卸载」→ 必须先报 MissingTarget
                // （否则原因会写成"名称含卸载"，而真正的问题是这个快捷方式已经废了）
                LowValueVerdict v = Explain("卸载 微信", @"Z:\no\such\uninstall.exe");
                Check(v.IsLowValue, "目标不存在 + 名字含关键词 → 低价值");
                Check(v.Reason == LowValueReason.MissingTarget,
                    "★原因优先报 MissingTarget，不是 NameKeyword", v.Reason.ToString());

                // 目标存在但是 .pdf + 名字含「手册」→ 必须先报 NonExecutableTarget
                string pdf = System.IO.Path.Combine(tmp, "手册.pdf");
                System.IO.File.WriteAllText(pdf, "x");
                v = Explain("用户手册", pdf);
                Check(v.IsLowValue, "目标是 .pdf + 名字含关键词 → 低价值");
                Check(v.Reason == LowValueReason.NonExecutableTarget,
                    "★原因优先报 NonExecutableTarget，不是 NameKeyword", v.Reason.ToString());

                // 目标存在且是 .exe + 名字含关键词 → 报 NameKeyword
                string exe = System.IO.Path.Combine(tmp, "unins000.exe");
                System.IO.File.WriteAllText(exe, "x");
                v = Explain("卸载 某软件", exe);
                Check(v.Reason == LowValueReason.NameKeyword,
                    "目标 .exe 存在 + 名字含关键词 → NameKeyword", v.Reason.ToString());
            }
            finally
            {
                try { if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true); } catch { }
            }

            Console.WriteLine();
        }

        // ---------- 8. Detail 是给人看的 ----------
        static void TestDetailReadable()
        {
            Console.WriteLine("--- 8. Detail 可直接显示（不是枚举名）---");

            LowValueVerdict[] samples = new LowValueVerdict[]
            {
                Explain("卸载 微信", null),
                Explain("正常应用", @"Z:\no\such\app.exe"),
            };

            foreach (LowValueVerdict v in samples)
            {
                Check(v.IsLowValue, "样本本身是低价值");
                Check(!string.IsNullOrWhiteSpace(v.Detail), "★低价值必定带非空 Detail");
                Check(v.Detail != v.Reason.ToString(),
                    "★Detail 不是枚举名（" + v.Reason + "）", v.Detail);
                Check(!v.Detail.Contains("_"), "★Detail 不含下划线（人话，不是标识符）", v.Detail);
            }

            Console.WriteLine();
        }

        // ---------- 9. IsLowValue 是薄包装 ----------
        static void TestIsLowValueWrapper()
        {
            Console.WriteLine("--- 9. IsLowValue 与 Explain 结论一致 ---");

            string[] names = { "卸载 微信", "Help", "Google Chrome", "微信", "EULA", "" };
            string[] targets = { null, @"Z:\no\such\a.exe" };

            foreach (string n in names)
            {
                foreach (string t in targets)
                {
                    bool byBool = LowValueRules.IsLowValue(n, t, LowValueRules.ExeNameOf(t));
                    bool byVerdict = LowValueRules.Explain(n, t, LowValueRules.ExeNameOf(t)).IsLowValue;
                    Check(byBool == byVerdict,
                        "★一致：" + (string.IsNullOrEmpty(n) ? "(空名)" : n) + " / " + (t == null ? "null 目标" : "不存在目标"),
                        byBool + " vs " + byVerdict);
                }
            }

            Console.WriteLine();
        }

        // ---------- 10. 异常兜底 ----------
        static void TestExceptionFallback()
        {
            Console.WriteLine("--- 10. 异常兜底（宁可漏判，不可误伤）---");

            // 含非法字符的路径：File.Exists 不抛，但 Path.GetExtension 可能抛。
            // 无论内部怎么走，Explain 都不能抛异常，且不能误判成低价值。
            LowValueVerdict v = null;
            bool threw = false;
            try
            {
                v = LowValueRules.Explain("正常应用", "::::", null);
            }
            catch { threw = true; }

            Check(!threw, "★怪路径不抛异常");
            if (v != null)
            {
                // "::::" 既不存在也不是已知扩展名 → 走 MissingTarget（这是合理判定）
                Check(v.IsLowValue ? v.Reason == LowValueReason.MissingTarget : v.Reason == LowValueReason.None,
                    "怪路径的结果原因自洽", v.Reason.ToString());
            }

            threw = false;
            try { LowValueRules.Explain(null, null, null); }
            catch { threw = true; }
            Check(!threw, "★全 null 不抛异常");

            Console.WriteLine();
        }

        // ---------- 辅助 ----------
        static LowValueVerdict Explain(string name, string target)
        {
            return LowValueRules.Explain(name, target, LowValueRules.ExeNameOf(target));
        }

        static LowValueVerdict Explain(string name, string target, string exe)
        {
            return LowValueRules.Explain(name, target, exe);
        }
    }
}
