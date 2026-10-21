using System;
using System.Collections.Generic;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 拼音搜索支持：
    ///   - 全拼：输入 "weixin" 匹配「微信」
    ///   - 首字母：输入 "wx" 匹配「微信」
    ///   - 中英混合：应用名本身含英文时按原样匹配
    /// 数据表来自 PinyinData（由 tools\make-pinyin.ps1 从系统拼音库生成）。
    ///
    /// 性能：拼音串按应用名缓存，重复搜索不会反复转换；搜索是逐字符比对，
    /// 113 个应用 × 每次输入几个字符，开销可以忽略。
    /// </summary>
    static class Pinyin
    {
        /// <summary>码位 -> 音节下标，懒加载一次。</summary>
        static Dictionary<int, int> codeIndex;
        static readonly object gate = new object();

        /// <summary>应用名 -> (完整拼音, 首字母串) 缓存。</summary>
        static readonly Dictionary<string, string[]> nameCache = new Dictionary<string, string[]>(StringComparer.Ordinal);

        static Dictionary<int, int> Index
        {
            get
            {
                if (codeIndex != null) return codeIndex;
                lock (gate)
                {
                    if (codeIndex != null) return codeIndex;
                    Dictionary<int, int> map = new Dictionary<int, int>(PinyinData.CodeDelta.Length);
                    int code = 0;
                    for (int i = 0; i < PinyinData.CodeDelta.Length; i++)
                    {
                        code += PinyinData.CodeDelta[i];
                        map[code] = PinyinData.PinyinIndex[i];
                    }
                    codeIndex = map;
                    return codeIndex;
                }
            }
        }

        /// <summary>取单个汉字的无调拼音；非汉字或查不到时返回 null。</summary>
        public static string Of(char c)
        {
            int idx;
            if (!Index.TryGetValue(c, out idx)) return null;
            if (idx < 0 || idx >= PinyinData.Syllables.Length) return null;
            return PinyinData.Syllables[idx];
        }

        /// <summary>取 [完整拼音, 首字母串]，结果按名字缓存。</summary>
        static string[] Forms(string name)
        {
            string[] hit;
            if (nameCache.TryGetValue(name, out hit)) return hit;

            StringBuilder full = new StringBuilder(name.Length * 3);
            StringBuilder ini = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                string py = Of(c);
                if (py != null)
                {
                    full.Append(py);
                    ini.Append(py[0]);
                }
                else
                {
                    char lc = char.ToLowerInvariant(c);
                    full.Append(lc);
                    ini.Append(lc);
                }
            }

            hit = new string[] { full.ToString(), ini.ToString() };
            lock (gate)
            {
                if (!nameCache.ContainsKey(name)) nameCache[name] = hit;
            }
            return hit;
        }

        /// <summary>把字符串转成完整拼音串（非汉字原样保留，小写）。</summary>
        public static string ToFullPinyin(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return Forms(text)[0];
        }

        /// <summary>把字符串转成首字母串（汉字取拼音首字母，ASCII 原样小写）。</summary>
        public static string ToInitials(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return Forms(text)[1];
        }

        /// <summary>
        /// 判断应用名是否匹配搜索词。任一命中即返回 true：
        ///   1. 原始名称包含（大小写不敏感）—— 保持原有的英文/中文直接搜索
        ///   2. 首字母串包含查询（"wx" -> "微信"）
        ///   3. 完整拼音串包含查询（"weixin" -> "微信"）
        /// 查询本身含非 ASCII 时只做第 1 步，避免把中文当拼音去匹配。
        /// </summary>
        public static bool Matches(string name, string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            if (string.IsNullOrEmpty(name)) return false;

            if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            if (!IsAsciiLowerOrDigit(query)) return false;

            string q = query.ToLowerInvariant();
            string[] forms = Forms(name);
            if (forms[1].IndexOf(q, StringComparison.Ordinal) >= 0) return true;
            if (forms[0].IndexOf(q, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static bool IsAsciiLowerOrDigit(string s)
        {
            foreach (char c in s)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>重新扫描后清空缓存（应用名变了需要重算）。</summary>
        public static void ClearCache()
        {
            lock (gate) { nameCache.Clear(); }
        }
    }
}
