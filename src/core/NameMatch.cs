using System;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 应用名的归一化与匹配（属于 core 层，零 WPF 依赖）。
    ///
    /// 为什么单独抽出来：分类规则要按「别名」匹配应用名，而直接子串匹配会误判——
    /// 最典型的是 `chrome` 命中了 `ChromeDriver`。这里提供三种匹配模式，
    /// 把「怎么算命中」变成可用数据表达的东西，而不是每遇到一个误判就打一次补丁。
    ///
    /// 归一化（A3）：先做 FormKC，把全角字符、兼容字符折叠成标准形式
    /// （例如输入法打出的全角 `Ｗｏｒｄ` → `word`），再转小写。
    /// </summary>
    static class NameMatch
    {
        /// <summary>
        /// 轻量折叠：只做 FormKC + 转小写，**保留标点**。
        ///
        /// 给内置正则规则用。不能直接换成 Normalize()，因为 Normalize 会把
        /// 标点变成空格（`7-Zip` → `7 zip`），而内置正则里写的是 `7-zip`，
        /// 换了就会漏判。
        ///
        /// 效果：全角 `Ｗｏｒｄ` → `word`（能被内置 `word` 规则命中），
        /// 而 `7-Zip` → `7-zip`（标点保留）。
        /// </summary>
        public static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { s = s.Normalize(NormalizationForm.FormKC); }
            catch { }
            return s.ToLowerInvariant();
        }

        /// <summary>
        /// 归一化为「词序列」形式：
        ///   - FormKC 折叠全角/兼容字符
        ///   - 转小写
        ///   - 保留 ASCII 字母数字与 CJK 字符，其余一律变成空格
        ///   - 连续空格压缩成一个，首尾去空格
        ///
        /// 保留空格是有意的：`word` 模式要靠空格判断词边界。
        /// 例如 `ChromeDriver` → `chromedriver`，`7-Zip File Manager` → `7 zip file manager`。
        /// </summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { s = s.Normalize(NormalizationForm.FormKC); }
            catch { /* 极端情况下保留原串，不让归一化失败阻断分类 */ }
            s = s.ToLowerInvariant();

            StringBuilder sb = new StringBuilder(s.Length + 2);
            bool lastSpace = true; // 起始视为已有空格，避免首部出现空格
            foreach (char c in s)
            {
                if (IsKeepable(c))
                {
                    sb.Append(c);
                    lastSpace = false;
                }
                else if (!lastSpace)
                {
                    sb.Append(' ');
                    lastSpace = true;
                }
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 归一化后**去掉所有空格**的形式，用于 `exact` 模式。
        /// 例如 `7-Zip` → `7zip`，`QQ 音乐` → `qq音乐`。
        /// </summary>
        public static string Canonical(string s)
        {
            return Normalize(s).Replace(" ", "");
        }

        /// <summary>归一化后是否含指定子串（`sub` 模式）。</summary>
        public static bool Contains(string text, string term)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term)) return false;
            return text.IndexOf(term, StringComparison.Ordinal) >= 0;
        }

        /// <summary>归一化后是否完全相同（`exact` 模式）。</summary>
        public static bool Exact(string text, string term)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term)) return false;
            return string.Equals(text, term, StringComparison.Ordinal);
        }

        /// <summary>归一化后是否以指定前缀开头（`prefix` 模式）。</summary>
        public static bool Prefix(string text, string term)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term)) return false;
            return text.StartsWith(term, StringComparison.Ordinal);
        }

        /// <summary>
        /// `word` 模式：按**词**匹配，而不是按子串。
        ///
        /// 规则（与主流做法一致）：
        ///   - 别名的首/尾若**是 ASCII 字母**，则要求正文中相邻字符也不是 ASCII 字母
        ///   - 别名首/尾若不是 ASCII 字母（例如中文），不设边界要求 → 等价于子串匹配
        ///
        /// 效果：
        ///   `chrome` 对 `chromedriver` → 不命中（尾部 `e` 后面跟着字母 `d`）
        ///   `word`   对 `microsoft word` → 命中
        ///   `微信`   对 `微信电脑版`     → 命中（中文不设边界，见上）
        ///
        /// 中文别名走子串匹配是**有意为之**：中文没有空格分词，若强行加边界，
        /// 「微信电脑版」这类名字会全部漏判。
        /// </summary>
        public static bool Word(string text, string term)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term)) return false;

            int from = 0;
            while (from <= text.Length - term.Length)
            {
                int at = text.IndexOf(term, from, StringComparison.Ordinal);
                if (at < 0) return false;

                int end = at + term.Length;
                bool leftOk = !IsAsciiLetter(term[0]) || at == 0 || !IsAsciiLetter(text[at - 1]);
                bool rightOk = !IsAsciiLetter(term[term.Length - 1]) || end == text.Length || !IsAsciiLetter(text[end]);

                if (leftOk && rightOk) return true;
                from = at + 1;
            }
            return false;
        }

        static bool IsAsciiLetter(char c)
        {
            return c >= 'a' && c <= 'z';
        }

        /// <summary>归一化时要保留的字符：ASCII 字母数字 + CJK。</summary>
        static bool IsKeepable(char c)
        {
            if (c >= 'a' && c <= 'z') return true;
            if (c >= '0' && c <= '9') return true;
            return IsCjk(c);
        }

        /// <summary>CJK 及日韩字符（这些语言不用空格分词，不能被拆散）。</summary>
        static bool IsCjk(char c)
        {
            return (c >= 0x3040 && c <= 0x30FF)     // 日文假名
                || (c >= 0x3400 && c <= 0x4DBF)     // CJK 扩展 A
                || (c >= 0x4E00 && c <= 0x9FFF)     // CJK 统一表意
                || (c >= 0xAC00 && c <= 0xD7AF)     // 韩文音节
                || (c >= 0xF900 && c <= 0xFAFF)     // CJK 兼容表意
                || (c >= 0xFF00 && c <= 0xFFEF);    // 全角形式（FormKC 后大多已折叠，兜底保留）
        }
    }
}
