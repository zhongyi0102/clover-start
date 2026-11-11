using System;
using System.Collections.Generic;

namespace Clover
{
    /// <summary>
    /// 搜索结果的匹配与个性化排序。
    ///
    /// 这是纯 core 逻辑：不读文件、不依赖 WPF。界面把目录结果和系统索引结果
    /// 交进来后，统一按「匹配质量 → 手动固定 → 使用次数 → 最近使用」排序。
    /// </summary>
    static class SearchRanking
    {
        public const int NoMatch = 1000;

        /// <summary>
        /// 合并两路结果、保守去重并统一排序。主结果先参与去重，因此同一个应用
        /// 同时以目录快捷方式和注册表 exe 出现时，保留信息更完整的目录项。
        /// </summary>
        public static List<SearchResult> Merge(List<SearchResult> main,
            List<SearchResult> extra, string query, int cap)
        {
            List<SearchResult> merged = new List<SearchResult>();
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AppendUnique(merged, paths, names, main);
            AppendUnique(merged, paths, names, extra);
            Sort(merged, query);

            if (cap >= 0 && merged.Count > cap)
                merged.RemoveRange(cap, merged.Count - cap);
            return merged;
        }

        static void AppendUnique(List<SearchResult> target, HashSet<string> paths,
            HashSet<string> names, List<SearchResult> source)
        {
            if (source == null) return;
            foreach (SearchResult r in source)
            {
                if (r == null || string.IsNullOrWhiteSpace(r.Path)) continue;
                if (!paths.Add(r.Path)) continue;

                // 名字键刻意只做 Trim + 忽略大小写。不能用 NameMatch.Normalize：
                // 后者会把 Notepad++ 折成 notepad，误伤两个不同应用。
                string name = (r.Name ?? "").Trim();
                if (name.Length > 0 && !names.Add(name)) continue;
                target.Add(r);
            }
        }

        public static void Sort(List<SearchResult> list, string query)
        {
            if (list == null || list.Count < 2) return;
            list.Sort(delegate(SearchResult a, SearchResult b)
            {
                return Compare(a, b, query);
            });
        }

        internal static int Compare(SearchResult a, SearchResult b, string query)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return 1;
            if (b == null) return -1;

            int qa = MatchTier(a.Name, query);
            int qb = MatchTier(b.Name, query);
            int c = qa.CompareTo(qb);
            if (c != 0) return c;

            // 同等匹配质量下，尊重用户的明确选择和真实使用习惯。
            c = b.Pinned.CompareTo(a.Pinned);
            if (c != 0) return c;
            c = b.Usage.CompareTo(a.Usage);
            if (c != 0) return c;
            c = b.LastUsed.CompareTo(a.LastUsed);
            if (c != 0) return c;

            c = SourceRank(a).CompareTo(SourceRank(b));
            if (c != 0) return c;

            int la = string.IsNullOrEmpty(a.Name) ? int.MaxValue : a.Name.Length;
            int lb = string.IsNullOrEmpty(b.Name) ? int.MaxValue : b.Name.Length;
            c = la.CompareTo(lb);
            if (c != 0) return c;

            c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 匹配层级（数字越小越好）：原名精确、原名前缀、词首、原名包含、
        /// 拼音首字母、全拼。多词查询采用 AND 语义。
        /// </summary>
        public static int MatchTier(string name, string query)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(query))
                return NoMatch;

            string foldedName = NameMatch.Fold(name).Trim();
            string foldedQuery = NameMatch.Fold(query).Trim();
            if (foldedName.Length == 0 || foldedQuery.Length == 0) return NoMatch;

            if (string.Equals(foldedName, foldedQuery, StringComparison.Ordinal)) return 0;
            if (foldedName.StartsWith(foldedQuery, StringComparison.Ordinal)) return 1;

            string[] terms = foldedQuery.Split(new char[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 0) return NoMatch;

            int worst = 0;
            foreach (string term in terms)
            {
                int tier = SingleTermTier(foldedName, name, term);
                if (tier >= NoMatch) return NoMatch;
                if (tier > worst) worst = tier;
            }

            // 多词完全命中虽然不等于原名精确，但应优先于普通单词子串。
            return terms.Length > 1 && worst < 3 ? 2 : worst;
        }

        public static bool Matches(string name, string query)
        {
            return MatchTier(name, query) < NoMatch;
        }

        static int SingleTermTier(string foldedName, string originalName, string term)
        {
            if (string.Equals(foldedName, term, StringComparison.Ordinal)) return 0;
            if (foldedName.StartsWith(term, StringComparison.Ordinal)) return 1;

            // 空格/标点后的词首比任意位置的子串更接近用户意图。
            string normalized = NameMatch.Normalize(foldedName);
            if (NameMatch.Word(normalized, NameMatch.Normalize(term))) return 2;
            if (foldedName.IndexOf(term, StringComparison.Ordinal) >= 0) return 3;

            if (!IsAsciiQuery(term)) return NoMatch;
            string compactTerm = Compact(term);
            if (compactTerm.Length == 0) return NoMatch;

            string initials = Compact(Pinyin.ToInitials(originalName));
            string full = Compact(Pinyin.ToFullPinyin(originalName));
            if (string.Equals(initials, compactTerm, StringComparison.Ordinal)) return 4;
            if (initials.StartsWith(compactTerm, StringComparison.Ordinal)) return 5;
            if (string.Equals(full, compactTerm, StringComparison.Ordinal)) return 6;
            if (full.StartsWith(compactTerm, StringComparison.Ordinal)) return 7;
            if (initials.IndexOf(compactTerm, StringComparison.Ordinal) >= 0) return 8;
            if (full.IndexOf(compactTerm, StringComparison.Ordinal) >= 0) return 9;
            return NoMatch;
        }

        static bool IsAsciiQuery(string value)
        {
            foreach (char c in value)
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) continue;
                return false;
            }
            return true;
        }

        static string Compact(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (!char.IsWhiteSpace(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        static int SourceRank(SearchResult r)
        {
            if (r == null) return 9;
            switch (r.Source)
            {
                case "已安装": return 0;
                case "开始菜单": return 1;
                case "注册表": return 2;
                default: return 3;
            }
        }
    }
}
