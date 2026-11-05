using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 把外部拖进来的文件**收进**某个虚拟桌面的存放文件夹。
    ///
    /// ── 需求边界 ──
    ///
    ///   1. 只处理**非应用文件**（文档、图片、压缩包、文件夹…）。
    ///      `.exe` / `.lnk` 直接忽略：不弹窗、不复制、不提示。
    ///      理由：应用有自己的归宿（「常用」「全部」），虚拟桌面只放文件。
    ///   2. **按源位置决定移动还是复制**：
    ///        · 源文件在真实桌面目录里 → **移动**（桌面上的东西被收进来了）
    ///        · 源文件在其他位置（资源管理器任意目录）→ **复制**（源留着）
    ///      判据是「文件在哪」而不是「从哪个窗口拖的」—— 后者拿不到：
    ///      WPF 的 `DragEventArgs` 不包含源窗口句柄，外部拖入无法反查来源。
    ///   3. 同名冲突按「保留两者」处理：改成 `名称 (1)`，绝不覆盖。
    ///   4. 被占用失败时源文件保持原位，返回原因给调用方汇总。
    ///
    /// ── 为什么单独成类 ──
    ///
    /// 过滤规则、来源判定、冲突改名都需要单元测试；塞进 Drop 事件里就没法测了。
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
        /// 这个路径是否在真实桌面目录里（用户桌面 + 公共桌面）。
        ///
        /// ── 为什么用「文件在哪」而不是「从哪拖的」──
        ///
        /// 拖入时拿不到来源窗口：WPF 的 `DragEventArgs` 没有源句柄，
        /// 外部程序（资源管理器 / 桌面）发起的拖拽在 WPF 侧完全看不到是谁发起的。
        /// `GetCursorPos + WindowFromPoint` 也没用 —— 那一刻光标已经在面板上了。
        ///
        /// 所以改用文件自身位置判断，实际效果与「从桌面拖 / 从资源管理器拖」
        /// 等价：
        ///   · 桌面上的文件 → 在桌面目录 → 移动（收进来）
        ///   · 资源管理器里任意目录的文件 → 不在桌面目录 → 复制（源留着）
        ///
        /// 唯一的不精确：在资源管理器里**打开桌面目录**、从那里拖，
        /// 会被判成移动。语义上也说得通 —— 文件确实在桌面上。
        /// </summary>
        public static bool IsFromDesktop(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;

                string full = Path.GetFullPath(path);
                string parent = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(parent)) return false;

                foreach (string desk in DesktopKeep.DesktopDirs())
                {
                    if (string.IsNullOrEmpty(desk)) continue;
                    if (string.Equals(
                        parent.TrimEnd('\\'), desk.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 把一批路径收进 destDir。
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
        /// **传 null = 用 `DropSpec.ForDesk()`**：只拒应用类，其余照收。
        /// </summary>
        public static Result Import(IEnumerable<string> paths, string destDir, DropSpec spec)
        {
            return Import(paths, destDir, spec, ImportMode.BySource);
        }

        /// <summary>收进来的方式。</summary>
        public enum ImportMode
        {
            /// <summary>
            /// 按源位置自动决定（默认）：
            /// 桌面来的移动，其余复制。见 <see cref="IsFromDesktop"/>。
            /// </summary>
            BySource = 0,

            /// <summary>一律移动（源消失）。</summary>
            Move,

            /// <summary>一律复制（源保留）。</summary>
            Copy,
        }

        /// <summary>
        /// 完整版：可以显式指定移动还是复制。
        /// </summary>
        public static Result Import(IEnumerable<string> paths, string destDir,
            DropSpec spec, ImportMode mode)
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

                    // 已经在目标目录里 → 视为成功，不重复搬
                    //
                    // 这种情况说明用户是从桌面页内部拖的，不该再复制/移动一次，
                    // 否则会在同一个目录里弄出「a (1).txt」。
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

                    // ---- 决定这一项是移动还是复制 ----
                    bool useMove;
                    if (mode == ImportMode.Move) useMove = true;
                    else if (mode == ImportMode.Copy) useMove = false;
                    else useMove = IsFromDesktop(src);

                    ImportOne(src, destDir, useMove, r);
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
        /// 收一个文件/文件夹进 destDir。
        ///
        /// `useMove = true` 走 `VerifiedFileMove.Move`（源消失）；
        /// `useMove = false` 走 `VerifiedFileMove.Copy`（源保留）。
        /// 两条路共用同一套核对保证，只在最后「删不删源」上不同。
        /// </summary>
        static void ImportOne(string src, string destDir, bool useMove, Result r)
        {
            string name = Path.GetFileName(src);
            bool isFolder;
            try { isFolder = Directory.Exists(src); }
            catch { isFolder = false; }

            try
            {
                string target = MakeUniqueTarget(destDir, name, isFolder);

                VerifiedFileMove.Result done = useMove
                    ? VerifiedFileMove.Move(src, target)
                    : VerifiedFileMove.Copy(src, target);

                if (!done.Ok) throw new IOException(done.Message);

                r.Imported.Add(Path.GetFileName(done.Target));

                // 记一条能区分「搬走」和「复制」的日志：
                // 出了问题时用户/我们需要一眼看出源文件该不该还在。
                try
                {
                    Log.Info((useMove ? "拖入移动: " : "拖入复制: ")
                        + src + " → " + done.Target);
                }
                catch { }
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
