using System;
using System.Diagnostics;

namespace Clover
{
    /// <summary>拼音搜索测试：全拼、首字母、中英混合、边界与性能。</summary>
    static class PinyinTest
    {
        static int failed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name + (cond || detail.Length == 0 ? "" : "  [" + detail + "]"));
            if (!cond) failed++;
        }

        static void Match(string name, string query, bool expected)
        {
            bool actual = Pinyin.Matches(name, query);
            Check(actual == expected, "「" + name + "」匹配「" + query + "」= " + expected,
                "实际 " + actual);
        }

        static void Main()
        {
            // ---------- 单字拼音 ----------
            Check(Pinyin.Of('微') == "wei", "微 -> wei", Pinyin.Of('微') ?? "null");
            Check(Pinyin.Of('信') == "xin", "信 -> xin", Pinyin.Of('信') ?? "null");
            Check(Pinyin.Of('剪') == "jian", "剪 -> jian", Pinyin.Of('剪') ?? "null");
            Check(Pinyin.Of('映') == "ying", "映 -> ying", Pinyin.Of('映') ?? "null");
            Check(Pinyin.Of('a') == null, "非汉字返回 null");

            // ---------- 首字母 ----------
            Check(Pinyin.ToInitials("微信") == "wx", "微信 首字母 = wx", Pinyin.ToInitials("微信"));
            Check(Pinyin.ToInitials("剪映专业版") == "jyzyb", "剪映专业版 首字母", Pinyin.ToInitials("剪映专业版"));
            Check(Pinyin.ToInitials("Google Chrome") == "google chrome", "英文首字母保持原样", Pinyin.ToInitials("Google Chrome"));

            // ---------- 全拼 ----------
            Check(Pinyin.ToFullPinyin("微信") == "weixin", "微信 全拼 = weixin", Pinyin.ToFullPinyin("微信"));
            Check(Pinyin.ToFullPinyin("大肥鱼桌宠") == "dafeiyuzhuochong", "大肥鱼桌宠 全拼", Pinyin.ToFullPinyin("大肥鱼桌宠"));

            // ---------- Matches：中文直接匹配（保持原有行为）----------
            Match("微信", "微信", true);
            Match("微信", "微", true);
            Match("微信", "不存在的词", false);

            // ---------- Matches：首字母 ----------
            Match("微信", "wx", true);
            Match("微信", "WX", true);
            Match("剪映专业版", "jy", true);
            Match("大肥鱼桌宠", "dfyzc", true);

            // ---------- Matches：全拼 ----------
            Match("微信", "weixin", true);
            Match("微信", "weix", true);
            Match("剪映专业版", "jianying", true);
            Match("大肥鱼桌宠", "zhuochong", true);

            // ---------- Matches：英文直接匹配 ----------
            Match("Google Chrome", "chrome", true);
            Match("Google Chrome", "CHROME", true);
            Match("Google Chrome", "google", true);
            Match("Google Chrome", "chro", true);

            // ---------- Matches：不匹配 ----------
            Match("微信", "qq", false);
            Match("Google Chrome", "firefox", false);

            // ---------- 边界 ----------
            Match("微信", "", true);           // 空查询 -> 全部匹配
            Match("", "wx", false);            // 空名称不匹配非空查询
            Check(Pinyin.Matches(null, "") == true, "null 名称 + 空查询 = true");
            Check(Pinyin.Matches(null, "wx") == false, "null 名称 + 非空查询 = false");

            // 中文查询不应被当成拼音处理
            Match("微信", "微信", true);
            Match("微信", "wx微信", false);

            // ---------- 混拼（当前实现：字母部分整体匹配，不做逐字混排）----------
            // "wei信" 含中文，走直接匹配分支，不匹配
            Match("微信", "wei信", false);

            // ---------- 覆盖率：真实应用名 ----------
            string[] realNames = new string[]
            {
                "微信", "剪映专业版", "大肥鱼桌宠", "Google Chrome", "Microsoft Word",
                "Visual Studio Code", "PotPlayer", "哔哩哔哩", "钉钉", "网易云音乐"
            };
            int covered = 0;
            foreach (string n in realNames)
            {
                string ini = Pinyin.ToInitials(n);
                // 首字母里不应出现 '\0'（表示有字查不到拼音）
                bool ok = ini.IndexOf('\0') < 0;
                if (ok) covered++;
                else Console.WriteLine("      未覆盖: " + n + " -> " + ini.Replace('\0', '?'));
            }
            Check(covered == realNames.Length, "真实应用名全部能转拼音", covered + "/" + realNames.Length);

            // ---------- 性能：113 个应用 × 搜索 ----------
            string[] many = new string[113];
            for (int i = 0; i < many.Length; i++) many[i] = "测试应用名称" + i;
            Stopwatch sw = Stopwatch.StartNew();
            int hits = 0;
            for (int round = 0; round < 20; round++)
            {
                foreach (string n in many) if (Pinyin.Matches(n, "csyy")) hits++;
            }
            sw.Stop();
            double perSearch = sw.Elapsed.TotalMilliseconds / (20.0 * many.Length);
            Check(perSearch < 1.0, "单次匹配 < 1ms（实测 " + perSearch.ToString("0.0000") + " ms）");
            Check(hits > 0, "拼音搜索能命中测试应用");

            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
