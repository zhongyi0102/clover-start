using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 把外部拖进来的文件**复制**进某个虚拟桌面的存放文件夹。
    ///
    /// ── 需求边界 ──
    ///
    ///   1. 只处理**非应用文件**（文档、图片、压缩包、文件夹…）。
    ///      `.exe` / `.lnk` 直接忽略：不弹窗、不复制、不提示。
    ///      理由：应用有自己的归宿（「常用」「全部」），虚拟桌面只放文件。
    ///   2. **复制而不是移动** —— 源文件永远留在原处。
    ///      用户从桌面拖文件进来，桌面上的那份必须还在（那是他的工作区）；
    ///      面板里只是多一份副本。用移动会让用户以为文件丢了。
    ///   3. 同名冲突按「保留两者」处理：改成 `名称 (1)`，绝不覆盖。
    ///   4. 被占用失败时源文件保持原位，返回原因给调用方汇总。
    ///
    /// ── 为什么单独成类 ──
    ///
    /// 过滤规则、冲突改名都需要单元测试；塞进 Drop 事件里就没法测了。
    /// </summary>
    public static class DeskImporter
    {
        /// <summary>
        /// 不接收的扩展名（应用类，有自己的归宿）。
        ///
        /// **单一来源是 `DropSpec.AppExts`** —— 过滤规则、拖放提示的约束文案、
        /// 拒绝原因的措辞都读那一份，避免三处各写一遍后分叉。
        /// </summary>
        static readonly string[] BlockedExt = DropSpec.AppExts;

        /// <summary>一次导入的结果。</summary>
        public class Result
        {
            /// <summary>成功复制进来的文件名（用于落位）。</summary>
            public List<string> Imported = new List<string>();

            /// <summary>
            /// 成功复制的「原路径 → 新路径」记录（供日志追溯）。
            ///
            /// 字段名保留 `Moved` 是为了不改调用方（PanelWindow 的日志循环），
            /// 但语义已经是「复制」。**这些记录不能拿去撤销** ——
            /// 撤销会把副本移回原处、覆盖原文件，而原文件本来就没动过。
            /// </summary>
            public List<DeskUndo.Entry> Moved = new List<DeskUndo.Entry>();

            /// <summary>被过滤掉的（.exe/.lnk 等），不提示。</summary>
            public List<string> Skipped = new List<string>();

            /// <summary>
            /// 被跳过的原因，与 `Skipped` 一一对应。
            ///
            /// 步骤 2 靠它把「跳过 3 个应用类文件」汇总成一句话，
            /// 而不是把 3 个文件名逐个列出来。
            /// </summary>
            public List<DropSpec.RejectReason> SkippedReasons =
                new List<DropSpec.RejectReason>();

            /// <summary>被跳过项的扩展名（小写含点，与 Skipped 对应；目录为空串）。</summary>
            public List<string> SkippedExts = new List<string>();

            /// <summary>失败的文件名。</summary>
            public List<string> Failed = new List<string>();

            /// <summary>失败原因，与 Failed 一一对应。</summary>
            public List<string> Reasons = new List<string>();

            public int ImportedCount { get { return Imported.Count; } }
            public int SkippedCount { get { return Skipped.Count; } }
            public bool AllOk { get { return Failed.Count == 0; } }
            public bool AnyImported { get { return Imported.Count > 0; } }
        }

        /// <summary>
        /// 这个路径是否允许进入虚拟桌面。
        ///
        /// 文件夹一律允许（用户可能想整理一整个目录）；
        /// 文件按扩展名过滤。
        ///
        /// **保留为独立 API**：`Import` 现在走 `DropSpec`，但这个方法
        /// 有测试在用，且语义更直白（只要一个 bool）。两处判定读同一份
        /// `BlockedExt`，不会分叉。
        /// </summary>
        public static bool IsAllowed(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return false;

                // 文件夹放行
                if (Directory.Exists(path)) return true;

                string ext = Path.GetExtension(path);
                if (string.IsNullOrEmpty(ext)) return true;    // 无扩展名：当普通文件

                foreach (string b in BlockedExt)
                {
                    if (string.Equals(b, ext, StringComparison.OrdinalIgnoreCase)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 把一批路径移入 destDir。
        ///
        /// 返回详细结果，调用方据此决定落位与提示。
        /// </summary>
        public static Result Import(IEnumerable<string> paths, string destDir)
        {
            return Import(paths, destDir, null);
        }

        /// <summary>
        /// 同上，但可以指定收件规则（`DropSpec`）。
        ///
        /// **传 null = 用 `DropSpec.ForDesk()`**，行为与改造前完全一致：
        /// 只拒应用类，其余照收，被拒项进 `Skipped` 不提示。
        ///
        /// 为什么要接受外部 spec：同一个导入器以后可能被别的入口复用
        /// （比如"把文件收进某个自定义桌面"时带上额外的类型限制），
        /// 但默认路径必须保持原样 —— 这是本次改动的硬约束。
        /// </summary>
        public static Result Import(IEnumerable<string> paths, string destDir, DropSpec spec)
        {
            Result r = new Result();
            try
            {
                if (paths == null || string.IsNullOrEmpty(destDir)) return r;

                if (!Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                DropSpec rules = spec ?? DropSpec.ForDesk();

                foreach (string raw in paths)
                {
                    if (string.IsNullOrEmpty(raw)) continue;

                    string src;
                    try { src = Path.GetFullPath(raw); }
                    catch { continue; }

                    // ---- 过滤应用类 ----
                    // 走 DropSpec 而不是自己比扩展名：规则只有一份，
                    // 拒绝原因也一并带出来（步骤 2 要用它汇总提示）。
                    DropSpec.Validation one = rules.Validate(new string[] { src });
                    if (!one.AnyAccepted)
                    {
                        string name = Path.GetFileName(src);

                        if (one.AnyRejected)
                        {
                            DropSpec.Rejected rej = one.RejectedItems[0];

                            // 「应用类」沿用老字段（静默跳过语义）；
                            // 其它拒绝原因目前 DeskImporter 用不到，也归到 Skipped，
                            // 保证「导入结果行为不变」。
                            r.Skipped.Add(name);
                            r.SkippedReasons.Add(rej.Reason);
                            r.SkippedExts.Add(rej.Ext);
                        }
                        continue;
                    }

                    if (!File.Exists(src) && !Directory.Exists(src))
                    {
                        r.Failed.Add(Path.GetFileName(src));
                        r.Reasons.Add("源文件不存在");
                        continue;
                    }

                    // 已经在目标目录里 → 视为成功，不重复复制
                    //
                    // 复制语义下这种情况很少见（源在 destDir 里说明用户是从
                    // 桌面页内部拖的），但仍要拦：否则会在同一个目录里
                    // 复制出「a (1).txt」。
                    try
                    {
                        string parent = Path.GetDirectoryName(src);
                        if (!string.IsNullOrEmpty(parent)
                            && string.Equals(Path.GetFullPath(parent).TrimEnd('\\'),
                                             Path.GetFullPath(destDir).TrimEnd('\\'),
                                             StringComparison.OrdinalIgnoreCase))
                        {
                            r.Imported.Add(Path.GetFileName(src));
                            continue;
                        }
                    }
                    catch { }

                    MoveOne(src, destDir, r);
                }
            }
            catch (Exception ex)
            {
                r.Failed.Add("(整体)");
                r.Reasons.Add(ex.Message);
            }
            return r;
        }

        /// <summary>
        /// 复制一个文件/文件夹进 destDir。
        ///
        /// 名字里的 "Move" 是历史遗留 —— 从移动改成复制时为了不改调用点保留了
        /// 方法名。**现在它调的是 `VerifiedFileMove.Copy`，源文件不动。**
        /// </summary>
        static void MoveOne(string src, string destDir, Result r)
        {
            string name = Path.GetFileName(src);
            bool isFolder;
            try { isFolder = Directory.Exists(src); }
            catch { isFolder = false; }

            try
            {
                string target = MakeUniqueTarget(destDir, name, isFolder);

                // ★ Copy 而不是 Move：源文件必须留在原处。
                VerifiedFileMove.Result copied = VerifiedFileMove.Copy(src, target);
                if (!copied.Ok) throw new IOException(copied.Message);

                r.Imported.Add(Path.GetFileName(copied.Target));

                DeskUndo.Entry rec = new DeskUndo.Entry();
                rec.From = src;
                rec.To = copied.Target;
                rec.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                r.Moved.Add(rec);
            }
            catch (IOException ex)
            {
                r.Failed.Add(name);
                r.Reasons.Add(IsSharing(ex) ? "文件正在使用" : ex.Message);
            }
            catch (UnauthorizedAccessException)
            {
                r.Failed.Add(name);
                r.Reasons.Add("没有权限");
            }
            catch (Exception ex)
            {
                r.Failed.Add(name);
                r.Reasons.Add(ex.Message);
            }
        }

        /// <summary>同名时生成「名称 (1)」「名称 (2)」…，绝不覆盖。</summary>
        public static string MakeUniqueTarget(string dir, string fileName, bool isFolder)
        {
            try
            {
                string target = Path.Combine(dir, fileName);
                if (!Exists(target)) return target;

                string baseName = Path.GetFileNameWithoutExtension(fileName);
                string ext = Path.GetExtension(fileName);

                for (int n = 1; n <= 9999; n++)
                {
                    string cand = Path.Combine(dir, baseName + " (" + n + ")" + ext);
                    if (!Exists(cand)) return cand;
                }

                return Path.Combine(dir,
                    baseName + " (" + DateTime.Now.ToString("HHmmssfff") + ")" + ext);
            }
            catch
            {
                return Path.Combine(dir, fileName);
            }
        }

        static bool Exists(string path)
        {
            try { return File.Exists(path) || Directory.Exists(path); }
            catch { return false; }
        }

        static bool IsSharing(IOException ex)
        {
            try
            {
                int code = System.Runtime.InteropServices.Marshal.GetHRForException(ex) & 0xFFFF;
                return code == 32 || code == 33;
            }
            catch { return false; }
        }
    }
}
