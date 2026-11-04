using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 「这个位置收不收这个文件」的统一声明 + 校验。
    ///
    /// ── 为什么需要它 ──
    ///
    /// 项目里有 4 处会接收外部文件/目录：
    ///
    ///   1. 「桌面文件」页拖入（DeskImporter）
    ///   2. 外观窗口的「自定义背景」导入
    ///   3. 「文件」标签添加来源文件夹
    ///   4. （将来）任何新的放置入口
    ///
    /// 这 4 处各自写一套「收什么、拒什么、为什么拒」，迟早分叉 ——
    /// 而且拒绝的理由只有 UI 自己知道，用户看不到。
    ///
    /// 现在把**规则**收在这里（纯逻辑、可单测），把**呈现**留给 UI。
    ///
    /// ── 不做什么 ──
    ///
    ///   · 不移动、不复制、不删除任何文件（那是 VerifiedFileMove 的事）
    ///   · 不解码图片（core 层零 WPF 依赖，读像素是 UI 的事）
    ///   · 不弹窗、不打日志（core 只返回结论）
    ///
    /// ── MaxLongEdge 的特殊地位 ──
    ///
    /// 它只是**声明**（"超过这个长边会被等比缩小"），`Validate` 不实现它 ——
    /// 要知道图片长边必须先解码，而解码在 UI 层（`BackgroundStore.Import`）。
    /// 放在这里是为了让「约束文案」和「真实行为」读同一个常量，不会说一套做一套。
    /// </summary>
    public class DropSpec
    {
        // ========== 拒绝原因 ==========

        /// <summary>为什么拒收。</summary>
        public enum RejectReason
        {
            /// <summary>扩展名不在 Accept 白名单里。</summary>
            UnsupportedType = 0,

            /// <summary>应用类文件（.exe / .lnk 等），有自己的归宿。</summary>
            AppFile,

            /// <summary>已经收满 MaxFiles 个了。</summary>
            TooMany,

            /// <summary>单个文件超过 MaxSizeBytes。</summary>
            TooLarge,

            /// <summary>这个位置只收文件夹，给的是文件。</summary>
            NotDirectory,
        }

        /// <summary>一条被拒记录。</summary>
        public class Rejected
        {
            public string Path = "";

            /// <summary>文件名（不含目录），用于提示文案。</summary>
            public string Name = "";

            public RejectReason Reason = RejectReason.UnsupportedType;

            /// <summary>扩展名（小写含点），没有则为空串。用于按类型汇总。</summary>
            public string Ext = "";
        }

        /// <summary>一次校验的结论。</summary>
        public class Validation
        {
            /// <summary>通过校验、可以收的路径（保持传入顺序）。</summary>
            public List<string> Accepted = new List<string>();

            /// <summary>被拒的项。</summary>
            public List<Rejected> RejectedItems = new List<Rejected>();

            /// <summary>空路径 / null 的个数（既不算接受也不算拒绝，只是跳过）。</summary>
            public int Ignored;

            public bool AllAccepted
            {
                get { return RejectedItems.Count == 0 && Accepted.Count > 0; }
            }

            public bool AnyAccepted { get { return Accepted.Count > 0; } }
            public bool AnyRejected { get { return RejectedItems.Count > 0; } }

            /// <summary>按原因数一数，返回「原因 → 数量」。</summary>
            public Dictionary<RejectReason, int> CountByReason()
            {
                Dictionary<RejectReason, int> d = new Dictionary<RejectReason, int>();
                try
                {
                    foreach (Rejected r in RejectedItems)
                    {
                        int n;
                        d.TryGetValue(r.Reason, out n);
                        d[r.Reason] = n + 1;
                    }
                }
                catch { }
                return d;
            }

            /// <summary>被拒项里出现过的扩展名（小写含点，去重、按字母序）。</summary>
            public List<string> RejectedExts()
            {
                List<string> exts = new List<string>();
                try
                {
                    foreach (Rejected r in RejectedItems)
                    {
                        if (string.IsNullOrEmpty(r.Ext)) continue;
                        if (!exts.Contains(r.Ext)) exts.Add(r.Ext);
                    }
                    exts.Sort(StringComparer.OrdinalIgnoreCase);
                }
                catch { }
                return exts;
            }
        }

        // ========== 规则字段 ==========

        /// <summary>
        /// 允许的扩展名（小写含点）。**空 = 不限**。
        ///
        /// 例：背景图只收 `.jpg .png …`；桌面文件页不限。
        /// </summary>
        public List<string> Accept = new List<string>();

        /// <summary>
        /// 明确拒收的扩展名（小写含点）。**优先于 Accept 判定** ——
        /// `.exe` 就算 Accept 里写了也要报「应用类文件」而不是「不支持的类型」，
        /// 因为两者对用户是不同的事。
        /// </summary>
        public List<string> RejectExts = new List<string>();

        /// <summary>最多收几个。**0 = 不限**。只统计通过前面判定的项。</summary>
        public int MaxFiles = 0;

        /// <summary>单个文件最大字节数。**0 = 不限**。目录不做递归统计（见 Validate）。</summary>
        public long MaxSizeBytes = 0;

        /// <summary>
        /// 图片长边上限。**0 = 不限**。
        ///
        /// 只是声明：`Validate` 不解码图片，所以不据此拒绝。
        /// 真正执行缩放的是 `BackgroundStore.Import`，两处读同一个常量。
        /// </summary>
        public int MaxLongEdge = 0;

        /// <summary>只收文件夹（给「添加来源」这类入口用）。</summary>
        public bool AllowDirectoriesOnly = false;

        // ========== 校验 ==========

        /// <summary>
        /// 按本 spec 判定一批路径。
        ///
        /// 判定顺序（决定报哪个原因，别随意调换）：
        ///   1. 空 / null            → 只计入 Ignored，不进任何列表
        ///   2. 只要文件夹却没给目录   → NotDirectory
        ///   3. 命中 RejectExts      → AppFile（优先于类型白名单）
        ///   4. 不在 Accept 白名单   → UnsupportedType
        ///   5. 已经收满 MaxFiles    → TooMany
        ///   6. 超过 MaxSizeBytes    → TooLarge
        ///
        /// 为什么先判类型再判容量：类型是「这个文件根本不该来」，
        /// 容量是「来晚了 / 太大了」。混着报会让用户以为换个文件就行。
        /// </summary>
        public Validation Validate(IEnumerable<string> paths)
        {
            Validation v = new Validation();
            try
            {
                if (paths == null) return v;

                foreach (string raw in paths)
                {
                    if (string.IsNullOrWhiteSpace(raw)) { v.Ignored++; continue; }

                    string full;
                    try { full = Path.GetFullPath(raw); }
                    catch { v.Ignored++; continue; }

                    bool isDir = SafeIsDir(full);
                    string ext = isDir ? "" : SafeExt(full);

                    // ---- 2. 只要文件夹 ----
                    if (AllowDirectoriesOnly && !isDir)
                    {
                        v.RejectedItems.Add(Make(full, ext, RejectReason.NotDirectory));
                        continue;
                    }

                    // ---- 3. 应用类 ----
                    if (!isDir && Hits(RejectExts, ext))
                    {
                        v.RejectedItems.Add(Make(full, ext, RejectReason.AppFile));
                        continue;
                    }

                    // ---- 4. 类型白名单 ----
                    if (!isDir && Accept.Count > 0 && !Hits(Accept, ext))
                    {
                        v.RejectedItems.Add(Make(full, ext, RejectReason.UnsupportedType));
                        continue;
                    }

                    // ---- 5. 数量 ----
                    if (MaxFiles > 0 && v.Accepted.Count >= MaxFiles)
                    {
                        v.RejectedItems.Add(Make(full, ext, RejectReason.TooMany));
                        continue;
                    }

                    // ---- 6. 大小 ----
                    // 目录不递归统计：扫一个几万文件的目录会让拖放卡住几秒，
                    // 而且用户拖整个文件夹进来时"太大"这个理由本身也没意义。
                    if (!isDir && MaxSizeBytes > 0)
                    {
                        long len = SafeLength(full);
                        if (len > MaxSizeBytes)
                        {
                            v.RejectedItems.Add(Make(full, ext, RejectReason.TooLarge));
                            continue;
                        }
                    }

                    v.Accepted.Add(full);
                }
            }
            catch { }
            return v;
        }

        // ========== 预设（三处入口共用同一份定义）==========

        /// <summary>
        /// 应用类扩展名：有自己的归宿，不进「桌面文件」。
        ///
        /// ── 为什么 .lnk 不在这个列表里 ──
        ///
        /// 快捷方式本身不是程序，是一个指向目标的文件。用户可以把它放进
        /// 「桌面文件」页当书签用（双击 → Shell 按 .lnk 关联打开 → 跳转到目标）。
        ///
        /// 历史：早期把 .lnk 和 .exe 一起拒收，理由是"应用有自己的归宿"。
        /// 但实测用户就是想放快捷方式（尤其从资源管理器拖一个文件夹的
        /// 快捷方式进来当入口），拒收反而是错的。
        ///
        /// 注意：`.url` 仍在列表里 —— 它是纯文本网址书签，
        /// 和 .lnk 不是一回事，放开它需要额外判定协议。
        /// </summary>
        public static readonly string[] AppExts =
        {
            ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".pif",
            ".appref-ms", ".url", ".msc", ".cpl", ".dll", ".sys"
        };

        /// <summary>
        /// 背景图支持的扩展名（小写含点）。
        ///
        /// **定义在 core 而不是 ui**：`DropSpec` 在 core，而 core 不允许引用
        /// `BackgroundStore`（它在 ui）。所以常量放这里，由
        /// `BackgroundStore.SupportedExts` 反向引用 —— 全仓只有一份定义，
        /// 拖放提示的约束文案和真正导入时的判定读的是同一个数组。
        /// </summary>
        public static readonly string[] BackgroundExts =
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff",
        };

        /// <summary>背景图长边上限：超过则等比缩小，小图不放大。</summary>
        public const int BackgroundLongEdge = 3840;

        /// <summary>
        /// 「文件」标签最多能加几个来源。
        ///
        /// **定义在 core 的 DropSpec 而不是 FileFolders**：提示文案要读它，
        /// 而 `DropSpec` 不该反向依赖 `FileFolders`（那会让 DropSpec 的
        /// 单测必须拖上 FileFolders + JsonIO + Log 一整条链）。
        /// 由 `FileFolders.MaxFolders` 反向引用同一份 —— 全仓只有一个数字。
        /// </summary>
        public const int FolderSourceMax = 10;

        /// <summary>
        /// 「文件」标签添加来源的约束文案。
        ///
        /// 上限取自 <see cref="FolderSourceMax"/>，不硬编码。
        /// </summary>
        public static string FolderHintText()
        {
            try
            {
                return "只收文件夹，最多 " + FolderSourceMax + " 个";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 已经收满时的文案（放置提示不再邀请拖入，改显示这句）。
        /// </summary>
        public static string FolderFullText()
        {
            try
            {
                return "已达上限 " + FolderSourceMax + " 个";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 背景图放置区的约束文案。
        ///
        /// 格式列表和 3840 **全部从常量取**，不手写 —— 这样改了
        /// `BackgroundExts` / `BackgroundLongEdge`，提示文案自动跟着变，
        /// 不会出现「提示说支持 tif、实际拒收」这种分叉。
        ///
        /// 措辞是「会等比缩小」而不是「不支持」：超长边不是拒绝理由，
        /// 是自动处理。写成「不支持」会让用户以为大图进不来。
        /// </summary>
        public static string BackgroundHintText()
        {
            try
            {
                // 扩展名去点、按原顺序拼接（.jpg/.jpeg 保留两者，用户可能真有 .jpeg）
                StringBuilder sb = new StringBuilder();
                sb.Append("支持 ");
                for (int i = 0; i < BackgroundExts.Length; i++)
                {
                    if (i > 0) sb.Append("/");
                    sb.Append(BackgroundExts[i].TrimStart('.'));
                }
                sb.Append("，长边超过 ");
                sb.Append(BackgroundLongEdge);
                sb.Append(" 会等比缩小");
                return sb.ToString();
            }
            catch { return ""; }
        }


        /// <summary>
        /// 「桌面文件」页拖入：不限类型，但拒应用类。
        ///
        /// 行为与改造前的 `DeskImporter.IsAllowed` **完全一致** ——
        /// 这是步骤 1 的硬要求（不改导入行为）。
        /// </summary>
        public static DropSpec ForDesk()
        {
            DropSpec s = new DropSpec();
            s.RejectExts.AddRange(AppExts);
            return s;
        }

        /// <summary>
        /// 「自定义背景」：只收图片，长边超过 3840 会等比缩小。
        ///
        /// 扩展名与长边都取自 core 里的常量，`BackgroundStore` 反向引用同一份。
        /// </summary>
        public static DropSpec ForBackground()
        {
            DropSpec s = new DropSpec();
            s.Accept.AddRange(BackgroundExts);
            s.MaxFiles = 1;
            s.MaxLongEdge = BackgroundLongEdge;
            return s;
        }

        /// <summary>
        /// 「文件」标签添加来源：只收文件夹，上限与 `FileFolders.MaxFolders` 一致。
        ///
        /// 注意 MaxFiles 是「本次最多收几个」，而 FileFolders 的上限是
        /// 「清单里总共几个」。调用方要先按剩余名额设 MaxFiles。
        /// </summary>
        public static DropSpec ForFolderSource(int remaining)
        {
            DropSpec s = new DropSpec();
            s.AllowDirectoriesOnly = true;
            s.MaxFiles = remaining < 0 ? 0 : remaining;
            return s;
        }

        /// <summary>给用户看的一句话（按原因）。UI 层汇总时复用，避免各处措辞不一。</summary>
        public static string ReasonText(RejectReason r)
        {
            switch (r)
            {
                case RejectReason.AppFile: return "应用类文件";
                case RejectReason.TooMany: return "超出数量上限";
                case RejectReason.TooLarge: return "超出大小上限";
                case RejectReason.NotDirectory: return "不是文件夹";
                default: return "不支持的类型";
            }
        }

        // ========== 提示文案 ==========

        /// <summary>扩展名最多列几种，超出写「等」。</summary>
        public const int MaxExtsShown = 3;

        /// <summary>
        /// 把一次校验结果汇总成**一句话**，给 UI 直接显示。
        ///
        /// ── 为什么放在 core ──
        ///
        /// 文案规则（几种原因、怎么排序、扩展名列几种、单复数怎么处理）
        /// 一旦散在 UI 里就没法单测，改一次要手动点一遍才知道有没有说错话。
        /// 这里做成纯函数：输入「接受数 + 被拒列表」，输出字符串，可穷举断言。
        ///
        /// ── 返回 null 的语义 ──
        ///
        /// **没有拒绝项 → 返回 null**。调用方据此不显示任何提示 ——
        /// 「全部成功」不该弹一句「已放入 3 个，跳过 0 个」。
        ///
        /// ── 文案模板 ──
        ///
        ///   单原因：`已放入 2 个，跳过 3 个应用类文件(.exe/.lnk)`
        ///   多原因：`已放入 2 个，跳过 4 个：应用类文件 3、不支持的类型 1`
        ///   全被拒：`没有放入任何文件，跳过 3 个应用类文件(.exe/.lnk)`
        ///
        /// 多原因按数量**从多到少**排；数量相同按枚举值排（保证同一输入永远
        /// 产出同一句话，否则测试和用户都会看到随机顺序）。
        /// </summary>
        /// <param name="acceptedCount">成功放入的数量</param>
        /// <param name="rejected">被拒项</param>
        public static string BuildSummary(int acceptedCount, IEnumerable<Rejected> rejected)
        {
            return BuildSummary(acceptedCount, rejected, 0);
        }

        /// <summary>
        /// 同上，但可以把「已存在（跳过）」的项并进同一个计数。
        ///
        /// ── 为什么需要这个重载 ──
        ///
        /// 「文件」标签拖入目录时，「不是文件夹」和「已存在」会同时出现。
        /// 第一版把它们拼成两句：
        /// <code>
        /// 已放入 1 个，跳过 1 个不是文件夹（.txt）；已存在 1 个（跳过）
        /// </code>
        /// 「跳过」出现了两次，读起来像两个独立结论。
        ///
        /// 现在把 `alreadyExists` 并进「跳过」总数，已存在项作为附加说明：
        /// <code>
        /// 已放入 1 个，跳过 2 个：不是文件夹 1、已存在 1
        /// </code>
        ///
        /// **只在有拒绝项时才提及已存在**：如果全部都是新目录（`rejected` 为空、
        /// `alreadyExists > 0`），返回 null —— 重复拖同一批目录不该弹提示，
        /// 因为用户没做错任何事，chip 行本来就没变化。
        /// </summary>
        public static string BuildSummary(int acceptedCount, IEnumerable<Rejected> rejected,
            int alreadyExists)
        {
            try
            {
                List<Rejected> list = new List<Rejected>();
                if (rejected != null)
                {
                    foreach (Rejected r in rejected)
                        if (r != null) list.Add(r);
                }

                if (alreadyExists < 0) alreadyExists = 0;

                // 没有拒绝项也没有已存在 → 不提示
                if (list.Count == 0 && alreadyExists == 0) return null;

                if (acceptedCount < 0) acceptedCount = 0;

                // ---- 只有「已存在」，没有任何拒绝 ----
                //
                // 这不算失败，也不该占用「跳过」的措辞（那是给"进不来"的东西用的）。
                // 用「另有」单独说一句。
                if (list.Count == 0)
                {
                    if (acceptedCount > 0)
                        return "已放入 " + acceptedCount + " 个，另有 " + alreadyExists
                            + " 个已存在";
                    return "这 " + alreadyExists + " 个都已经在列表里了";
                }

                int skipped = list.Count + alreadyExists;

                // ---- 前缀 ----
                string head = acceptedCount > 0
                    ? "已放入 " + acceptedCount + " 个，跳过 " + skipped + " 个"
                    : "没有放入任何文件，跳过 " + skipped + " 个";

                // ---- 按原因分组，数量降序、同数量按枚举值升序 ----
                List<KeyValuePair<RejectReason, int>> groups = GroupByReason(list);

                // ---- 只有一种拒绝原因 + 没有已存在：带上扩展名 ----
                if (groups.Count == 1 && alreadyExists == 0)
                {
                    string exts = FormatExts(list);
                    return head + ReasonText(groups[0].Key) + exts;
                }

                // ---- 否则：原因 + 数量，已存在并进同一句 ----
                StringBuilder sb = new StringBuilder(head);
                sb.Append("：");
                for (int i = 0; i < groups.Count; i++)
                {
                    if (i > 0) sb.Append("、");
                    sb.Append(ReasonText(groups[i].Key));
                    sb.Append(" ");
                    sb.Append(groups[i].Value);
                }
                if (alreadyExists > 0)
                {
                    if (groups.Count > 0) sb.Append("、");
                    sb.Append("已存在 ");
                    sb.Append(alreadyExists);
                }
                return sb.ToString();
            }
            catch { return null; }
        }

        /// <summary>
        /// 把扩展名列表格式化成 `（.exe/.lnk）` / `（.exe/.lnk/.msi 等）`。
        ///
        /// 去重、按字母序（用户扫一眼就能确认"是不是我那些文件"）；
        /// 超过 <see cref="MaxExtsShown"/> 种时只列前几种 + 「等」，
        /// 否则一条提示能占满整行。
        ///
        /// 括号用**全角**：中文句子里夹半角括号会显得挤，且与项目里
        /// 「（都是空文件夹）」等既有文案保持一致。
        /// </summary>
        public static string FormatExts(IEnumerable<Rejected> rejected)
        {
            try
            {
                List<string> exts = new List<string>();
                if (rejected != null)
                {
                    foreach (Rejected r in rejected)
                    {
                        if (r == null || string.IsNullOrEmpty(r.Ext)) continue;
                        if (!exts.Contains(r.Ext)) exts.Add(r.Ext);
                    }
                }
                if (exts.Count == 0) return "";

                exts.Sort(StringComparer.OrdinalIgnoreCase);

                int shown = Math.Min(exts.Count, MaxExtsShown);
                StringBuilder sb = new StringBuilder("（");
                for (int i = 0; i < shown; i++)
                {
                    if (i > 0) sb.Append("/");
                    sb.Append(exts[i]);
                }
                if (exts.Count > shown) sb.Append(" 等");
                sb.Append("）");
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>
        /// 按原因分组计数，**数量降序、同数量按枚举值升序**。
        ///
        /// 为什么不用 Dictionary 直接遍历：字典顺序在 .NET 里是实现细节，
        /// 同一批输入两次跑可能给出不同顺序 —— 用户会觉得"上次和这次说的不一样"。
        /// </summary>
        public static List<KeyValuePair<RejectReason, int>> GroupByReason(IEnumerable<Rejected> rejected)
        {
            List<KeyValuePair<RejectReason, int>> result =
                new List<KeyValuePair<RejectReason, int>>();
            try
            {
                Dictionary<RejectReason, int> counts = new Dictionary<RejectReason, int>();
                if (rejected != null)
                {
                    foreach (Rejected r in rejected)
                    {
                        if (r == null) continue;
                        int n;
                        counts.TryGetValue(r.Reason, out n);
                        counts[r.Reason] = n + 1;
                    }
                }

                foreach (KeyValuePair<RejectReason, int> kv in counts)
                    result.Add(kv);

                result.Sort(delegate(KeyValuePair<RejectReason, int> a,
                                     KeyValuePair<RejectReason, int> b)
                {
                    int c = b.Value.CompareTo(a.Value);          // 数量多的在前
                    if (c != 0) return c;
                    return ((int)a.Key).CompareTo((int)b.Key);   // 同数量按枚举值
                });
            }
            catch { }
            return result;
        }


        // ========== 内部工具 ==========

        static Rejected Make(string full, string ext, RejectReason reason)
        {
            Rejected r = new Rejected();
            r.Path = full;
            r.Ext = ext ?? "";
            r.Reason = reason;
            try { r.Name = Path.GetFileName(full.TrimEnd('\\', '/')); }
            catch { r.Name = full; }
            return r;
        }

        static bool Hits(List<string> list, string ext)
        {
            try
            {
                if (list == null || string.IsNullOrEmpty(ext)) return false;
                foreach (string x in list)
                {
                    if (string.Equals(x, ext, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        static bool SafeIsDir(string p)
        {
            try { return Directory.Exists(p); } catch { return false; }
        }

        static string SafeExt(string p)
        {
            try { return (Path.GetExtension(p) ?? "").ToLowerInvariant(); }
            catch { return ""; }
        }

        static long SafeLength(string p)
        {
            try
            {
                FileInfo fi = new FileInfo(p);
                return fi.Exists ? fi.Length : 0;
            }
            catch { return 0; }
        }
    }
}
