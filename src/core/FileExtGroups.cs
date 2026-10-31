using System;
using System.Collections.Generic;

namespace Clover
{
    /// <summary>
    /// 「文件」标签第二行筛选栏的**后缀分组定义**。
    ///
    /// ── 分组规则 ──
    ///
    ///   · 前 7 组是固定分组（doc / xls / ppt / pdf / 图片 / 压缩包 / 安装包）
    ///   · 「其他」= **不属于任何固定组、也不属于用户自定义后缀**的所有扩展名，
    ///     以及**没有扩展名的文件**（Ext 为空串）
    ///   · 用户可以通过末尾「+」添加自定义后缀（如 md、py），
    ///     这些后缀单独成组，且会从「其他」里移出来
    ///
    /// ── 为什么放 core ──
    ///
    /// 分组判定是纯逻辑，界面只负责画标签。放 core 能单独测，
    /// 也避免把扩展名清单散落在 WPF 代码里。
    /// </summary>
    public static class FileExtGroups
    {
        /// <summary>「其他」组的内部 key。</summary>
        public const string OtherKey = "__other__";

        /// <summary>一个分组：显示名 + 扩展名集合（小写、不含点）。</summary>
        public class Group
        {
            /// <summary>内部 key（固定组用英文标识，自定义组用扩展名本身）。</summary>
            public string Key = "";

            /// <summary>显示名（如「文档」「图片」「md」）。</summary>
            public string Label = "";

            /// <summary>扩展名集合（小写、不含点）。「其他」组为空 —— 它靠排除法判定。</summary>
            public List<string> Exts = new List<string>();

            /// <summary>是不是「其他」组（排除法）。</summary>
            public bool IsOther;

            /// <summary>是不是用户自定义组。</summary>
            public bool IsCustom;

            public Group() { }

            public Group(string key, string label, string[] exts)
            {
                Key = key;
                Label = label;
                if (exts != null)
                {
                    foreach (string e in exts)
                    {
                        string x = Normalize(e);
                        if (x.Length > 0) Exts.Add(x);
                    }
                }
            }
        }

        /// <summary>
        /// 固定分组定义（7 组）。顺序即界面显示顺序。
        /// </summary>
        public static List<Group> Builtin()
        {
            List<Group> list = new List<Group>();

            list.Add(new Group("doc", "doc/docx", new string[] { "doc", "docx", "rtf", "odt", "wps" }));
            list.Add(new Group("xls", "xls/xlsx", new string[] { "xls", "xlsx", "csv", "ods", "et" }));
            list.Add(new Group("ppt", "ppt/pptx", new string[] { "ppt", "pptx", "odp", "dps" }));
            list.Add(new Group("pdf", "pdf", new string[] { "pdf" }));
            list.Add(new Group("img", "图片",
                new string[] { "jpg", "jpeg", "png", "gif", "bmp", "webp", "svg", "ico", "tif", "tiff", "heic", "raw" }));
            list.Add(new Group("zip", "压缩包",
                new string[] { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab" }));
            list.Add(new Group("setup", "安装包",
                new string[] { "exe", "msi", "apk", "msix", "appx", "deb", "rpm", "dmg", "pkg" }));

            return list;
        }

        /// <summary>构造「其他」组。</summary>
        public static Group Other()
        {
            Group g = new Group();
            g.Key = OtherKey;
            g.Label = "其他";
            g.IsOther = true;
            return g;
        }

        /// <summary>归一化扩展名：去点、去空白、转小写。</summary>
        public static string Normalize(string ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return "";
            string s = ext.Trim().ToLowerInvariant();
            while (s.StartsWith(".")) s = s.Substring(1);
            return s.Trim();
        }

        /// <summary>
        /// 判断某个扩展名（Entry.Ext，含点如 ".docx"）属于哪些组。
        ///
        /// 返回该扩展名**实际归属**的组 key 列表：
        ///   · 属于某个固定组 → 该组 key
        ///   · 属于某个自定义组 → 该组 key
        ///   · 都不属于 → OtherKey
        ///
        /// 一个扩展名可能同时属于多个固定组（如 .csv 在 xls 组），
        /// 这里返回所有命中 —— 筛选时用"命中任一选中组即通过"。
        /// </summary>
        public static List<string> MatchGroups(string entryExt, List<Group> builtin, List<Group> custom)
        {
            List<string> hits = new List<string>();
            string e = Normalize(entryExt);

            // 无扩展名 → 直接归「其他」
            if (e.Length == 0)
            {
                hits.Add(OtherKey);
                return hits;
            }

            // 自定义组优先（用户显式添加的后缀应该能覆盖固定分组，
            // 但当前设计里自定义后缀不会和固定组冲突，这里仍先查自定义）
            if (custom != null)
            {
                foreach (Group g in custom)
                {
                    if (g == null || g.IsOther) continue;
                    if (g.Exts.Contains(e)) hits.Add(g.Key);
                }
            }

            if (builtin != null)
            {
                foreach (Group g in builtin)
                {
                    if (g == null || g.IsOther) continue;
                    if (g.Exts.Contains(e)) hits.Add(g.Key);
                }
            }

            // 一个都没命中 → 归「其他」
            if (hits.Count == 0) hits.Add(OtherKey);

            return hits;
        }

        /// <summary>
        /// 这个扩展名是不是「其他」组（即不属于任何固定组、也不属于任何自定义组）。
        /// </summary>
        public static bool IsOther(string entryExt, List<Group> builtin, List<Group> custom)
        {
            List<string> hits = MatchGroups(entryExt, builtin, custom);
            return hits.Count == 1 && hits[0] == OtherKey;
        }

        /// <summary>
        /// 类型过滤的唯一判定入口（文件夹 + 后缀，两个独立状态）。
        ///
        /// 四种组合：
        ///   · 后缀空 + 文件夹关 → 只放行文件（默认）
        ///   · 后缀空 + 文件夹开 → 只放行文件夹（**文件一律排除**）
        ///   · 后缀非空 + 文件夹关 → 只放行扩展名匹配的文件
        ///   · 后缀非空 + 文件夹开 → 文件夹 + 扩展名匹配的文件
        ///
        /// **关键**：第二个分支里，没选后缀的文件不能放行 —— 用户点「文件夹」
        /// 就是要只看文件夹，此时若还放行文件就违背了意图。
        /// </summary>
        /// <param name="isFolder">条目是不是文件夹</param>
        /// <param name="entryExt">扩展名（含点，如 ".docx"；文件夹传空）</param>
        /// <param name="showFolders">「文件夹」标签是否选中</param>
        /// <param name="selectedExts">选中的后缀分组 key 集合（空 = 不限后缀）</param>
        public static bool KeepEntry(bool isFolder, string entryExt,
            bool showFolders, HashSet<string> selectedExts,
            List<Group> builtin, List<Group> custom)
        {
            try
            {
                if (isFolder)
                    return showFolders;   // 文件夹只由 showFolders 决定，与后缀无关

                if (selectedExts == null || selectedExts.Count == 0)
                {
                    // 没选后缀：只有「不显示文件夹」时才放行文件；
                    // 选了「文件夹」= 用户明确只要文件夹 → 文件一律不放行
                    return !showFolders;
                }

                return Passes(entryExt, selectedExts, builtin, custom);
            }
            catch { return false; }
        }

        /// <summary>
        /// 该扩展名是否通过筛选。
        ///
        /// selected 为空 → 全部通过（"都没选 = 全部后缀"）。
        /// 否则：扩展名归属的任一组在 selected 里即通过。
        /// </summary>
        public static bool Passes(string entryExt, HashSet<string> selected,
            List<Group> builtin, List<Group> custom)
        {
            if (selected == null || selected.Count == 0) return true;

            List<string> hits = MatchGroups(entryExt, builtin, custom);
            foreach (string k in hits)
            {
                if (selected.Contains(k)) return true;
            }
            return false;
        }
    }
}
