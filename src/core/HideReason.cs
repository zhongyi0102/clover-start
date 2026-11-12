using System;
using System.Collections.Generic;

namespace Clover
{
    /// <summary>
    /// 隐藏原因。用于「已隐藏的应用」列表里的分组与说明文字。
    ///
    /// ── 为什么要记原因 ──
    ///
    /// 老版本只存"哪些被隐藏了"（一堆路径）。用户看到列表时无法判断
    /// 某条是自己手动藏的、还是某次批量隐藏（如「隐藏全部系统工具」）带走的。
    /// 后者恢复时还关系到**例外名单**的写入，所以必须区分。
    /// </summary>
    public class HideReason
    {
        /// <summary>原因类型。</summary>
        public enum Kind
        {
            /// <summary>用户手动隐藏（右键「从面板隐藏」）。</summary>
            Manual = 0,

            /// <summary>批量隐藏（「隐藏全部「系统工具」」等）。</summary>
            Batch = 1,

            /// <summary>用户确认过的具体入口隐藏建议。</summary>
            Suggested = 2,

            /// <summary>明确配套且没有使用记录的入口自动隐藏。</summary>
            Automatic = 3,
            Computer = 4,
        }

        /// <summary>类型。</summary>
        public Kind Type = Kind.Manual;

        /// <summary>
        /// 批量隐藏时的分类名（如「系统工具」）。手动隐藏时为空。
        /// </summary>
        public string Category = "";

        public HideReason() { }

        public HideReason(Kind type, string category)
        {
            Type = type;
            Category = category ?? "";
        }

        /// <summary>手动隐藏。</summary>
        public static HideReason Manual()
        {
            return new HideReason(Kind.Manual, "");
        }

        /// <summary>批量隐藏。</summary>
        public static HideReason Batch(string category)
        {
            return new HideReason(Kind.Batch, category);
        }

        public static HideReason Suggested(string group)
        {
            return new HideReason(Kind.Suggested, group);
        }

        public static HideReason Automatic(string group)
        {
            return new HideReason(Kind.Automatic, group);
        }

        public static HideReason Computer() { return new HideReason(Kind.Computer, ""); }

        public bool RetainOnRestore { get { return Type == Kind.Batch || Type == Kind.Suggested || Type == Kind.Automatic; } }

        /// <summary>
        /// 分组 key。用于「已隐藏的应用」按原因分组。
        ///
        ///   manual      → 手动隐藏
        ///   batch:xxx   → 批量隐藏：xxx
        /// </summary>
        public string GroupKey
        {
            get
            {
                try
                {
                    if (Type == Kind.Computer) return "computer";
                    if (RetainOnRestore)
                    {
                        string c = string.IsNullOrWhiteSpace(Category) ? "未分类" : Category.Trim();
                        return (Type == Kind.Automatic ? "automatic:" : Type == Kind.Suggested ? "suggested:" : "batch:") + c;
                    }
                    return "manual";
                }
                catch { return "manual"; }
            }
        }

        /// <summary>名称下方的小字说明。</summary>
        public string DisplayText
        {
            get
            {
                try
                {
                    if (Type == Kind.Computer) return "电脑用的：平时不显示，支持搜索";
                    if (RetainOnRestore)
                    {
                        string c = string.IsNullOrWhiteSpace(Category) ? "未分类" : Category.Trim();
                        return (Type == Kind.Automatic ? "自动隐藏：" : Type == Kind.Suggested ? "确认建议隐藏：" : "批量隐藏：") + c;
                    }
                    return "手动隐藏";
                }
                catch { return "手动隐藏"; }
            }
        }

        /// <summary>组标题文字。</summary>
        public string GroupTitle
        {
            get
            {
                try
                {
                    if (Type == Kind.Computer) return "电脑用的（搜索可见）";
                    if (RetainOnRestore)
                    {
                        string c = string.IsNullOrWhiteSpace(Category) ? "未分类" : Category.Trim();
                        return (Type == Kind.Automatic ? "自动隐藏：" : Type == Kind.Suggested ? "确认建议隐藏：" : "批量隐藏：") + c;
                    }
                    return "手动隐藏";
                }
                catch { return "手动隐藏"; }
            }
        }

        /// <summary>
        /// 分组的排序权重：**手动隐藏的组放最上面**，其余按分类名字典序。
        /// </summary>
        public int GroupOrder
        {
            get { try { return Type == Kind.Manual ? 0 : 1; } catch { return 0; } }
        }

        /// <summary>从 json 字典解析。</summary>
        public static HideReason FromDict(Dictionary<string, object> d)
        {
            try
            {
                if (d == null) return Manual();

                string kind = d.ContainsKey("kind") ? Convert.ToString(d["kind"]) : "";
                string cat = d.ContainsKey("category") ? Convert.ToString(d["category"]) : "";

                if (string.Equals(kind, "batch", StringComparison.OrdinalIgnoreCase))
                    return Batch(cat);
                if (string.Equals(kind, "suggested", StringComparison.OrdinalIgnoreCase))
                    return Suggested(cat);
                if (string.Equals(kind, "automatic", StringComparison.OrdinalIgnoreCase))
                    return Automatic(cat);
                if (string.Equals(kind, "computer", StringComparison.OrdinalIgnoreCase)) return Computer();

                return Manual();
            }
            catch { return Manual(); }
        }

        /// <summary>转成 json 字典。</summary>
        public Dictionary<string, object> ToDict()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            try
            {
                d["kind"] = Type == Kind.Computer ? "computer" : Type == Kind.Automatic ? "automatic" : Type == Kind.Suggested ? "suggested" : Type == Kind.Batch ? "batch" : "manual";
                if (RetainOnRestore) d["category"] = Category ?? "";
            }
            catch { }
            return d;
        }
    }
}
