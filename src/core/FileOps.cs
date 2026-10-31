using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 所有会改动用户文件的操做的**唯一入口**。
    ///
    /// ── 为什么集中在这一个类 ──
    ///
    /// 界面层（src\ui）**不得直接调用** File.Move / File.Delete /
    /// Directory.Move / Directory.Delete / File.Replace。全部走这里：
    ///
    ///   · 删除必须进回收站（不能静默永久删除）
    ///   · 绝不覆盖已有文件
    ///   · 受保护目录要拒绝
    ///   · 失败只记日志、不重试、不删原文件
    ///   · 每次操作追加到 config\file-ops-log.json（只追加，超 1000 条裁剪最旧）
    ///
    /// ── 可注入 ──
    ///
    /// 系统调用通过 IFileSystem 接口封装，测试用 FakeFileSystem 替换，
    /// 这样 FileOpsTest 能在临时目录里验证全部逻辑，**不碰真实回收站**。
    /// </summary>
    public static class FileOps
    {
        // ========== 可注入的系统调用 ==========

        /// <summary>系统文件操作的抽象。测试替换成假实现。</summary>
        public interface IFileSystem
        {
            /// <summary>移到回收站。返回 true = 成功。</summary>
            bool DeleteToRecycleBin(List<string> paths, IntPtr hwnd, out string error);

            /// <summary>同卷移动（系统重命名式，不复制）。</summary>
            bool MoveWithinVolume(string src, string dst, out string error);

            /// <summary>跨卷移动（交给系统文件操作，不自己复制再删除）。</summary>
            bool MoveAcrossVolume(string src, string dst, IntPtr hwnd, out string error);

            /// <summary>新建目录。</summary>
            bool CreateDirectory(string path, out string error);

            bool FileExists(string path);
            bool DirectoryExists(string path);
            bool PathExists(string path);

            /// <summary>两个路径是否在同一卷。</summary>
            bool IsSameVolume(string a, string b);

            /// <summary>卷根（"C:\" / "D:\"）。</summary>
            string VolumeRoot(string path);

            /// <summary>驱动器类型（Fixed / Removable / Network / ...）。</summary>
            DriveType GetDriveType(string volumeRoot);
        }

        /// <summary>当前使用的实现。测试替换它。</summary>
        public static IFileSystem Fs = new RealFileSystem();

        /// <summary>告警回调（core 层不直接依赖 UI 的日志）。</summary>
        public static Action<string> Warn = delegate { };

        /// <summary>日志文件路径（由 Init 设置）。</summary>
        static string logPath;

        /// <summary>日志最多保留多少条。</summary>
        public const int MaxLogEntries = 1000;

        /// <summary>初始化（设置日志路径）。</summary>
        public static void Init(string baseDir)
        {
            try
            {
                logPath = string.IsNullOrEmpty(baseDir)
                    ? null
                    : Path.Combine(baseDir, "config", "file-ops-log.json");
            }
            catch { logPath = null; }
        }

        // ========== 结果类型 ==========

        public enum OpStatus
        {
            Ok = 0,
            /// <summary>名称为空。</summary>
            EmptyName,
            /// <summary>含非法字符 \ / : * ? " < > |</summary>
            InvalidChars,
            /// <summary>末尾是点或空格。</summary>
            TrailingDotOrSpace,
            /// <summary>保留名（CON/PRN/AUX/NUL/COM1-9/LPT1-9）。</summary>
            ReservedName,
            /// <summary>超过 255 字符。</summary>
            NameTooLong,
            /// <summary>目标已存在（不区分大小写）。</summary>
            AlreadyExists,
            /// <summary>路径不存在。</summary>
            NotFound,
            /// <summary>受保护目录。</summary>
            Protected,
            /// <summary>该位置没有回收站（可移动盘 / 网络盘）。</summary>
            NoRecycleBin,
            /// <summary>不能对来源文件夹本身做修改。</summary>
            SourceRoot,
            /// <summary>不能把文件夹粘贴到它自己的子目录。</summary>
            IntoItself,
            /// <summary>系统调用失败。</summary>
            Failed,
            /// <summary>参数非法。</summary>
            Invalid,
        }

        /// <summary>一次操作的结果。</summary>
        public class OpResult
        {
            public OpStatus Status = OpStatus.Ok;
            /// <summary>给用户看的一句话（失败原因）。</summary>
            public string Message = "";
            /// <summary>成功时的产物路径（重命名/新建）。</summary>
            public string Path = "";
            /// <summary>成功处理的条数（批量删除/粘贴）。</summary>
            public int Count;

            public bool Ok { get { return Status == OpStatus.Ok; } }

            public static OpResult Fail(OpStatus s, string msg)
            {
                OpResult r = new OpResult();
                r.Status = s;
                r.Message = msg ?? "";
                return r;
            }

            public static OpResult Success(string path, int count)
            {
                OpResult r = new OpResult();
                r.Status = OpStatus.Ok;
                r.Path = path ?? "";
                r.Count = count;
                return r;
            }
        }

        // ========== 重命名 ==========

        /// <summary>
        /// 重命名（不移动、不覆盖）。
        ///
        /// 校验顺序：空 → 长度 → 非法字符 → 末尾点/空格 → 保留名 → 同名 → 受保护 → 来源根
        /// </summary>
        public static OpResult Rename(string path, string newName, string sourceRoot)
        {
            try
            {
                // ★ 先规范化：Path.GetDirectoryName 不会解析 ".."，
                //   传 "C:\Temp\..\Windows\a.txt" 时 dir 会是 "C:\Temp\..\Windows"，
                //   而 IsProtected 是字符串前缀匹配 —— 保护就绕过去了。
                //   必须先 GetFullPath 再判保护。
                string full;
                if (!TryNormalize(path, out full))
                    return OpResult.Fail(OpStatus.Invalid, "路径无效");

                if (!Fs.PathExists(full))
                    return OpResult.Fail(OpStatus.NotFound, "路径不存在");

                OpResult v = ValidateName(newName);
                if (!v.Ok) return v;

                string name = (newName ?? "").Trim();
                string dir = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(dir))
                    return OpResult.Fail(OpStatus.Invalid, "无法确定父目录");

                // 不能对来源文件夹本身做修改
                if (IsSourceRoot(full, sourceRoot))
                    return OpResult.Fail(OpStatus.SourceRoot,
                        "不能修改来源文件夹本身；想去掉它请用「移除」");

                // ★ Reparse Point 检查：逐组件确认路径上没有 Junction / 符号链接。
                //   任何可疑（含无法确认）都拒绝 —— 不退回原始字符串继续。
                PathSafety ps = CheckPathSafety(full);
                if (ps == PathSafety.ReparsePoint)
                    return OpResult.Fail(OpStatus.Protected,
                        "路径上存在 Junction 或符号链接，已拒绝重命名（防止绕过保护）");
                if (ps == PathSafety.Indeterminate)
                    return OpResult.Fail(OpStatus.Invalid,
                        "无法确认路径安全（权限不足或路径异常），已拒绝重命名");

                // 受保护目录（用规范化后的真实父目录）
                if (IsProtected(dir))
                    return OpResult.Fail(OpStatus.Protected, "此位置受保护，不能重命名");

                string dst = Path.Combine(dir, name);

                // 规范化后重新比较：path 可能是 "C:\a\.\b.txt"，full 是 "C:\a\b.txt"
                bool sameIgnoringCase = string.Equals(full, dst, StringComparison.OrdinalIgnoreCase);
                bool exactlySame = string.Equals(full, dst, StringComparison.Ordinal);

                if (exactlySame)
                    return OpResult.Fail(OpStatus.AlreadyExists, "名称没有变化");

                // 目标已存在 → 拒绝
                // 例外：仅大小写变化（a.txt → A.txt）在 Windows 上是允许的，
                // 但必须确认目标不是"另一个文件"—— 大小写不敏感比较相等即视为同一个。
                if (Fs.PathExists(dst))
                {
                    if (!sameIgnoringCase)
                        return OpResult.Fail(OpStatus.AlreadyExists,
                            "「" + name + "」已存在，不会覆盖");
                    // sameIgnoringCase 且不是 exactlySame → 仅大小写变化，允许
                }


                // 仅大小写变化：Windows 上直接 Move 到"同名不同大小写"会失败，
                // 需要先改成一个临时名再改成目标名。
                if (sameIgnoringCase && !exactlySame)
                {
                    string tmp = Path.Combine(dir, Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp-rename");
                    string err;
                    if (!Fs.MoveWithinVolume(full, tmp, out err))
                    {
                        Log("rename", full, "failed", err);
                        return OpResult.Fail(OpStatus.Failed, "重命名失败：" + err);
                    }
                    if (!Fs.MoveWithinVolume(tmp, dst, out err))
                    {
                        // 回滚
                        string err2;
                        Fs.MoveWithinVolume(tmp, full, out err2);
                        Log("rename", full, "failed", err);
                        return OpResult.Fail(OpStatus.Failed, "重命名失败：" + err);
                    }
                    Log("rename", full, "ok", dst);
                    return OpResult.Success(dst, 1);
                }

                string e2;
                if (!Fs.MoveWithinVolume(full, dst, out e2))
                {
                    Log("rename", full, "failed", e2);
                    return OpResult.Fail(OpStatus.Failed, "重命名失败：" + e2);
                }

                Log("rename", full, "ok", dst);
                return OpResult.Success(dst, 1);
            }
            catch (Exception ex)
            {
                Log("rename", path, "failed", ex.Message);   // catch 里用原始入参（full 可能未赋值）
                return OpResult.Fail(OpStatus.Failed, "重命名失败：" + ex.Message);
            }
        }

        /// <summary>校验一个文件名（不含路径）。</summary>
        public static OpResult ValidateName(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
                    return OpResult.Fail(OpStatus.EmptyName, "名称不能为空");

                // 先查末尾点/空格 —— 必须在 Trim 之前，
                // 否则 "abc " 被 Trim 成 "abc" 就永远查不出来了。
                // Windows 上 "abc." 和 "abc " 实际会创建成 "abc"，导致名字与用户输入不符。
                if (name.EndsWith(".") || name.EndsWith(" "))
                    return OpResult.Fail(OpStatus.TrailingDotOrSpace,
                        "名称不能以点或空格结尾");

                string n = name.Trim();

                if (n.Length > 255)
                    return OpResult.Fail(OpStatus.NameTooLong, "名称不能超过 255 个字符");

                // 非法字符
                char[] bad = new char[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
                foreach (char c in bad)
                {
                    if (n.IndexOf(c) >= 0)
                        return OpResult.Fail(OpStatus.InvalidChars,
                            "名称不能包含 \\ / : * ? \" < > |");
                }
                // 控制字符
                foreach (char c in n)
                {
                    if (c < 32)
                        return OpResult.Fail(OpStatus.InvalidChars, "名称不能包含控制字符");
                }

                // 保留名：CON PRN AUX NUL COM1-9 LPT1-9（扩展名不算，CON.txt 也保留）
                string stem = n;
                int dot = n.IndexOf('.');
                if (dot > 0) stem = n.Substring(0, dot);
                stem = stem.Trim().ToUpperInvariant();

                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL")
                    return OpResult.Fail(OpStatus.ReservedName,
                        "「" + stem + "」是系统保留名，不能使用");

                if (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")))
                {
                    char d = stem[3];
                    if (d >= '1' && d <= '9')
                        return OpResult.Fail(OpStatus.ReservedName,
                            "「" + stem + "」是系统保留名，不能使用");
                }

                return OpResult.Success(null, 0);
            }
            catch (Exception ex)
            {
                return OpResult.Fail(OpStatus.Invalid, "名称校验失败：" + ex.Message);
            }
        }

        // ========== 删除 ==========

        /// <summary>
        /// 删除（走回收站）。批量。
        ///
        /// **调用方必须先弹自己的确认框**（显示文件名或数量 + 「将移到回收站」）。
        /// 这里不再弹确认 —— 只做「能不能删」的判定和实际删除。
        /// </summary>
        public static OpResult Delete(List<string> paths, string sourceRoot, IntPtr hwnd)
        {
            try
            {
                if (paths == null || paths.Count == 0)
                    return OpResult.Fail(OpStatus.Invalid, "没有选中任何项目");

                // ---- 逐个校验，任何一个不合格就整批拒绝（不做半途而废的删除）----
                // 规范化后的路径单独收集：实际删除和日志都用它，
                // 否则 "C:\Temp\..\Windows\a.dll" 会绕过 IsProtected 的前缀匹配。
                List<string> norm = new List<string>();

                foreach (string raw in paths)
                {
                    string p;
                    if (!TryNormalize(raw, out p))
                        return OpResult.Fail(OpStatus.Invalid, "路径无效：" + raw);

                    if (!Fs.PathExists(p))
                        return OpResult.Fail(OpStatus.NotFound, "路径不存在：" + raw);

                    if (IsSourceRoot(p, sourceRoot))
                        return OpResult.Fail(OpStatus.SourceRoot,
                            "不能删除来源文件夹本身；想去掉它请用「移除」");

                    // ★ Reparse Point 检查（fail-closed）
                    PathSafety ps = CheckPathSafety(p);
                    if (ps == PathSafety.ReparsePoint)
                        return OpResult.Fail(OpStatus.Protected,
                            "「" + Path.GetFileName(p) + "」路径上存在 Junction 或符号链接，已拒绝删除");
                    if (ps == PathSafety.Indeterminate)
                        return OpResult.Fail(OpStatus.Invalid,
                            "无法确认「" + Path.GetFileName(p) + "」路径安全，已拒绝删除");

                    string dir = Path.GetDirectoryName(p);
                    if (IsProtected(dir))
                        return OpResult.Fail(OpStatus.Protected,
                            "「" + Path.GetFileName(p) + "」所在位置受保护，不能删除");

                    // 可移动盘 / 网络盘：没有回收站
                    string vol = Fs.VolumeRoot(p);
                    DriveType dt = Fs.GetDriveType(vol);
                    if (dt == DriveType.Removable || dt == DriveType.Network)
                        return OpResult.Fail(OpStatus.NoRecycleBin,
                            "此位置没有回收站（" + vol + "），不能删除");

                    norm.Add(p);
                }

                string err;
                if (!Fs.DeleteToRecycleBin(norm, hwnd, out err))
                {
                    Log("delete", string.Join("|", norm.ToArray()), "failed", err);
                    return OpResult.Fail(OpStatus.Failed, "删除失败：" + err);
                }

                Log("delete", string.Join("|", norm.ToArray()), "ok", norm.Count + " 项");
                return OpResult.Success(null, norm.Count);
            }
            catch (Exception ex)
            {
                Log("delete", paths == null ? "" : string.Join("|", paths.ToArray()),
                    "failed", ex.Message);
                return OpResult.Fail(OpStatus.Failed, "删除失败：" + ex.Message);
            }
        }

        // ========== 剪切 / 粘贴 ==========

        /// <summary>
        /// 剪切：把路径列表 + Preferred DropEffect=Move 放进剪贴板。
        /// 真正的移动发生在 Paste。
        /// </summary>
        public static OpResult Cut(List<string> paths, string sourceRoot)
        {
            try
            {
                if (paths == null || paths.Count == 0)
                    return OpResult.Fail(OpStatus.Invalid, "没有选中任何项目");

                foreach (string raw in paths)
                {
                    // 与 Delete / Paste 一致：先规范化再判保护，
                    // 否则 "C:\Temp\..\Windows" 这类路径会绕过前缀匹配。
                    string p;
                    if (!TryNormalize(raw, out p))
                        return OpResult.Fail(OpStatus.Invalid, "路径无效：" + raw);

                    if (!Fs.PathExists(p))
                        return OpResult.Fail(OpStatus.NotFound, "路径不存在：" + raw);

                    if (IsSourceRoot(p, sourceRoot))
                        return OpResult.Fail(OpStatus.SourceRoot,
                            "不能剪切来源文件夹本身；想去掉它请用「移除」");

                    // ★ Reparse Point 检查（fail-closed）
                    PathSafety ps = CheckPathSafety(p);
                    if (ps == PathSafety.ReparsePoint)
                        return OpResult.Fail(OpStatus.Protected,
                            "「" + Path.GetFileName(p) + "」路径上存在 Junction 或符号链接，已拒绝剪切");
                    if (ps == PathSafety.Indeterminate)
                        return OpResult.Fail(OpStatus.Invalid,
                            "无法确认「" + Path.GetFileName(p) + "」路径安全，已拒绝剪切");

                    string dir = Path.GetDirectoryName(p);
                    if (IsProtected(dir))
                        return OpResult.Fail(OpStatus.Protected,
                            "「" + Path.GetFileName(p) + "」所在位置受保护，不能剪切");
                }

                return OpResult.Success(null, paths.Count);
            }
            catch (Exception ex)
            {
                return OpResult.Fail(OpStatus.Failed, "剪切失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 粘贴到 destDir。
        ///
        /// 规则：
        ///   · 同卷 → 系统重命名式移动（快，不复制）
        ///   · 跨卷 → 交给系统文件操作（不自己复制再删除）
        ///   · 目标已存在 → **拒绝**（不覆盖）
        ///   · 禁止把文件夹粘贴到它自己的子目录里
        /// </summary>
        public static OpResult Paste(List<string> paths, string destDir, IntPtr hwnd)
        {
            try
            {
                if (paths == null || paths.Count == 0)
                    return OpResult.Fail(OpStatus.Invalid, "剪贴板里没有可粘贴的项目");

                // ★ 目标目录先规范化：Path 前缀匹配判保护时，
                //   未规范化的 "C:\Temp\..\Windows" 会绕过 IsProtected。
                //   规范化失败直接拒绝 —— 不猜用户想粘到哪。
                string dest;
                if (!TryNormalize(destDir, out dest))
                    return OpResult.Fail(OpStatus.Invalid, "目标路径无效：" + destDir);

                if (!Fs.DirectoryExists(dest))
                    return OpResult.Fail(OpStatus.NotFound, "目标文件夹不存在");

                // ★ 目标目录 Reparse Point 检查（fail-closed）
                PathSafety psDest = CheckPathSafety(dest);
                if (psDest == PathSafety.ReparsePoint)
                    return OpResult.Fail(OpStatus.Protected,
                        "目标目录路径上存在 Junction 或符号链接，已拒绝粘贴（防止绕过保护）");
                if (psDest == PathSafety.Indeterminate)
                    return OpResult.Fail(OpStatus.Invalid,
                        "无法确认目标目录路径安全，已拒绝粘贴");

                if (IsProtected(dest))
                    return OpResult.Fail(OpStatus.Protected, "目标位置受保护，不能粘贴");

                // ---- 先全部预检，任何一个不合格就整批拒绝 ----
                List<string[]> plan = new List<string[]>();

                foreach (string rawSrc in paths)
                {
                    // 源路径同样规范化（"C:\a\..\b" 与 "C:\b" 要视为同一个）
                    string src;
                    if (!TryNormalize(rawSrc, out src))
                        return OpResult.Fail(OpStatus.Invalid, "源路径无效：" + rawSrc);

                    if (!Fs.PathExists(src))
                        return OpResult.Fail(OpStatus.NotFound, "源路径不存在：" + rawSrc);

                    // ★ 源路径 Reparse Point 检查（fail-closed）—— 不能只保护目标而漏掉源
                    PathSafety psSrc = CheckPathSafety(src);
                    if (psSrc == PathSafety.ReparsePoint)
                        return OpResult.Fail(OpStatus.Protected,
                            "源「" + rawSrc + "」路径上存在 Junction 或符号链接，已拒绝粘贴");
                    if (psSrc == PathSafety.Indeterminate)
                        return OpResult.Fail(OpStatus.Invalid,
                            "无法确认源「" + rawSrc + "」路径安全，已拒绝粘贴");

                    string name = Path.GetFileName(src.TrimEnd('\\'));
                    if (string.IsNullOrEmpty(name))
                        return OpResult.Fail(OpStatus.Invalid, "无法确定名称：" + src);

                    string dst = Path.Combine(dest, name);

                    // 禁止把文件夹粘贴到它自己的子目录
                    if (Fs.DirectoryExists(src) && IsInside(dest, src))
                        return OpResult.Fail(OpStatus.IntoItself,
                            "不能把「" + name + "」粘贴到它自己的子目录里");

                    // 源和目标是同一个位置 → 无需操作
                    if (string.Equals(
                        Path.GetDirectoryName(src.TrimEnd('\\')), dest,
                        StringComparison.OrdinalIgnoreCase))
                        return OpResult.Fail(OpStatus.AlreadyExists, "已经在目标位置了");

                    // 目标已存在 → 拒绝（绝不覆盖）
                    if (Fs.PathExists(dst))
                        return OpResult.Fail(OpStatus.AlreadyExists,
                            "「" + name + "」在目标位置已存在，不会覆盖");

                    plan.Add(new string[] { src, dst });
                }

                // ---- 执行 ----
                int done = 0;
                foreach (string[] pair in plan)
                {
                    string src = pair[0], dst = pair[1];
                    string err;

                    bool ok;
                    if (Fs.IsSameVolume(src, dst))
                    {
                        // 同卷：系统重命名式移动
                        ok = Fs.MoveWithinVolume(src, dst, out err);
                    }
                    else
                    {
                        // 跨卷：交给系统文件操作
                        ok = Fs.MoveAcrossVolume(src, dst, hwnd, out err);
                    }

                    if (!ok)
                    {
                        Log("paste", src, "failed", err);
                        return OpResult.Fail(OpStatus.Failed,
                            "移动「" + Path.GetFileName(src) + "」失败：" + err
                            + (done > 0 ? "（已完成 " + done + " 项）" : ""));
                    }
                    done++;
                }

                Log("paste", dest, "ok", done + " 项");
                return OpResult.Success(dest, done);
            }
            catch (Exception ex)
            {
                Log("paste", destDir, "failed", ex.Message);
                return OpResult.Fail(OpStatus.Failed, "粘贴失败：" + ex.Message);
            }
        }

        // ========== 新建文件夹 ==========

        /// <summary>
        /// 在 dir 里新建文件夹。默认名「新建文件夹」，重名加序号 (2)(3)…
        /// 返回创建的完整路径。
        /// </summary>
        public static OpResult NewFolder(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir))
                    return OpResult.Fail(OpStatus.NotFound, "目标文件夹不存在");

                // 先规范化：受保护判定是前缀匹配，
                // "C:\Temp\..\Windows" 未规范化会绕过它。
                string baseDir;
                if (!TryNormalize(dir, out baseDir))
                    return OpResult.Fail(OpStatus.Invalid, "目标路径无效：" + dir);

                // ★ Reparse Point 检查（fail-closed）
                PathSafety psNew = CheckPathSafety(baseDir);
                if (psNew == PathSafety.ReparsePoint)
                    return OpResult.Fail(OpStatus.Protected,
                        "目标路径上存在 Junction 或符号链接，已拒绝新建（防止绕过保护）");
                if (psNew == PathSafety.Indeterminate)
                    return OpResult.Fail(OpStatus.Invalid,
                        "无法确认目标路径安全，已拒绝新建");

                // 受保护检查放在「存在性」之前 —— 受保护目录即便不存在也不该被当作可写
                if (IsProtected(baseDir))
                    return OpResult.Fail(OpStatus.Protected, "此位置受保护，不能新建文件夹");

                if (!Fs.DirectoryExists(baseDir))
                    return OpResult.Fail(OpStatus.NotFound, "目标文件夹不存在");

                const string BaseName = "新建文件夹";
                string name = BaseName;
                int n = 2;
                while (Fs.PathExists(Path.Combine(baseDir, name)))
                {
                    name = BaseName + " (" + n + ")";
                    n++;
                    if (n > 1000)
                        return OpResult.Fail(OpStatus.Failed, "重名次数过多");
                }

                string full = Path.Combine(baseDir, name);
                string err;
                if (!Fs.CreateDirectory(full, out err))
                {
                    Log("newfolder", full, "failed", err);
                    return OpResult.Fail(OpStatus.Failed, "新建文件夹失败：" + err);
                }

                Log("newfolder", full, "ok", name);
                return OpResult.Success(full, 1);
            }
            catch (Exception ex)
            {
                Log("newfolder", dir, "failed", ex.Message);
                return OpResult.Fail(OpStatus.Failed, "新建文件夹失败：" + ex.Message);
            }
        }

        // ========== Reparse Point 检测（R1 修复）==========

        /// <summary>
        /// 路径安全检查结果。
        /// </summary>
        public enum PathSafety
        {
            /// <summary>安全：路径上没有任何 Reparse Point。</summary>
            Safe = 0,
            /// <summary>路径上有 Reparse Point（Junction / 符号链接 / 其他）。</summary>
            ReparsePoint,
            /// <summary>无法确定（权限不足、IO 错误、路径不存在等）→ 调用方必须拒绝。</summary>
            Indeterminate,
        }

        /// <summary>
        /// 检查 path 从根到目标之间**每一个已存在的路径组件**是否带 ReparsePoint 属性。
        ///
        /// ── 为什么必须逐组件检查 ──
        ///
        /// 只查最后一层不够：`D:\safe\junction\to\Windows` 里，
        /// `to` 本身可能不是 Reparse Point，但它的父级 `junction` 是。
        /// 只查叶子会漏掉中间组件。
        ///
        /// ── 为什么不用 Path.GetFullPath 就够 ──
        ///
        /// GetFullPath 是纯字符串运算，只处理 `.` 和 `..`，
        /// 完全不碰文件系统，因此对 Junction / 符号链接零感知。
        /// 已实证：`C:\Users\All Users`（Junction → C:\ProgramData）
        /// 规范化后仍是 `C:\Users\All Users`。
        ///
        /// ── fail-closed ──
        ///
        /// 任何异常（权限不足 / IO 错误 / 路径过长）都返回 Indeterminate，
        /// 调用方据此**拒绝操作**。不退回原始字符串继续 —— 那正是漏洞来源。
        /// </summary>
        /// <param name="path">已规范化的绝对路径</param>
        public static PathSafety CheckPathSafety(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return PathSafety.Indeterminate;

                // 逐级向上，检查每个已存在的组件
                string cur = path.TrimEnd('\\');
                if (cur.Length == 0) return PathSafety.Indeterminate;

                // 防御性上限：正常路径不会超过 64 层
                for (int depth = 0; depth < 64; depth++)
                {
                    // 盘符根（"C:"）或 UNC 根（"\\\\server\\share"）→ 到此为止
                    if (cur.Length <= 2 || (cur.Length == 3 && cur[1] == ':' && cur[2] == '\\'))
                        break;
                    if (cur.StartsWith(@"\\\\"))
                    {
                        // UNC：检查到 \\\\server\\share 就不再往上
                        string[] parts = cur.TrimStart('\\').Split('\\');
                        if (parts.Length <= 2) break;
                    }

                    FileAttributes attr;
                    try
                    {
                        attr = File.GetAttributes(cur);
                    }
                    catch (FileNotFoundException)
                    {
                        // 这一层不存在：继续往上查父级
                        // （新建文件/新建文件夹的目标本来就不存在，其父级仍要检查）
                        string parent0 = Path.GetDirectoryName(cur);
                        if (string.IsNullOrEmpty(parent0) || parent0 == cur) break;
                        cur = parent0.TrimEnd('\\');
                        continue;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        string parent0 = Path.GetDirectoryName(cur);
                        if (string.IsNullOrEmpty(parent0) || parent0 == cur) break;
                        cur = parent0.TrimEnd('\\');
                        continue;
                    }

                    if ((attr & FileAttributes.ReparsePoint) != 0)
                        return PathSafety.ReparsePoint;

                    string parent = Path.GetDirectoryName(cur);
                    if (string.IsNullOrEmpty(parent) || parent == cur) break;
                    cur = parent.TrimEnd('\\');
                }

                return PathSafety.Safe;
            }
            catch
            {
                // 任何未预期异常 → 无法确定 → 调用方必须拒绝
                return PathSafety.Indeterminate;
            }
        }

        /// <summary>
        /// 组合判定：路径安全 + 不受保护，才允许修改。
        ///
        /// 返回 null 表示允许；否则返回应报给用户的失败结果。
        /// </summary>
        static OpResult GuardPath(string fullPath, string what)
        {
            try
            {
                PathSafety ps = CheckPathSafety(fullPath);
                if (ps == PathSafety.ReparsePoint)
                    return OpResult.Fail(OpStatus.Protected,
                        "「" + what + "」路径上存在 Junction 或符号链接，已拒绝操作（防止绕过保护）");
                if (ps == PathSafety.Indeterminate)
                    return OpResult.Fail(OpStatus.Invalid,
                        "「" + what + "」无法确认路径安全（权限不足或路径异常），已拒绝操作");

                string dir = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
                if (IsProtected(dir))
                    return OpResult.Fail(OpStatus.Protected, "「" + what + "」所在位置受保护");

                return null;   // 允许
            }
            catch (Exception ex)
            {
                return OpResult.Fail(OpStatus.Invalid, "「" + what + "」路径检查失败：" + ex.Message);
            }
        }

        // ========== 受保护目录判定 ==========

        /// <summary>
        /// 该路径是否受保护（不能做修改类操作）。
        ///
        /// **判定必须精确**，不能用前缀匹配误伤用户文件夹下的 Desktop / Documents：
        ///
        ///   · 驱动器根（C:\）           → 精确匹配
        ///   · 用户文件夹根（C:\Users\x） → 只精确匹配，其下子目录不受保护
        ///   · Windows / Program Files / Program Files (x86) / ProgramData → 整棵树
        /// </summary>
        public static bool IsProtected(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return false;

                string p = path.TrimEnd('\\');
                if (p.Length == 0) return false;

                // ---- 1. 驱动器根：精确匹配 ----
                // "C:" 或 "C:\" 都算根；"C:\Users" 不算
                if (p.Length == 2 && p[1] == ':') return true;

                // ---- 2. 用户文件夹根：只精确匹配 ----
                // C:\Users\xxx → 保护；C:\Users\xxx\Desktop → 不保护
                string usersRoot = null;
                try
                {
                    string profile = Environment.GetFolderPath(
                        Environment.SpecialFolder.UserProfile);
                    if (!string.IsNullOrEmpty(profile))
                        usersRoot = profile.TrimEnd('\\');
                }
                catch { }

                if (!string.IsNullOrEmpty(usersRoot)
                    && string.Equals(p, usersRoot, StringComparison.OrdinalIgnoreCase))
                    return true;

                // ---- 3. 系统目录：整棵树 ----
                foreach (string sys in SystemTreeRoots())
                {
                    if (string.IsNullOrEmpty(sys)) continue;
                    string s = sys.TrimEnd('\\');
                    if (s.Length == 0) continue;

                    if (string.Equals(p, s, StringComparison.OrdinalIgnoreCase)) return true;
                    if (p.StartsWith(s + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                }

                return false;
            }
            catch { return false; }
        }

        /// <summary>整棵树受保护的系统目录列表。</summary>
        static List<string> SystemTreeRoots()
        {
            List<string> list = new List<string>();
            try
            {
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(win)) list.Add(win);

                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrEmpty(pf)) list.Add(pf);

                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrEmpty(pf86)) list.Add(pf86);

                // ProgramData 没有 SpecialFolder 枚举，用 CommonApplicationData 的父目录
                string cad = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (!string.IsNullOrEmpty(cad))
                {
                    // C:\ProgramData → 取 C:\ProgramData 本身
                    string parent = Path.GetDirectoryName(cad.TrimEnd('\\'));
                    string pd = Path.Combine(parent ?? cad, "ProgramData");
                    list.Add(pd);
                    list.Add(cad);   // 有些系统 CommonApplicationData 就是 ProgramData
                }
            }
            catch { }
            return list;
        }

        // ========== 路径规范化 ==========

        /// <summary>
        /// 路径规范化：
        ///   · GetFullPath 展开相对路径和 ".."（这是判保护前必须做的一步）
        ///   · 统一反斜杠（/ → \）
        ///   · 去掉尾部斜杠（但盘符根 D:\ 保留）
        ///   · 不做大小写转换（比较时用 OrdinalIgnoreCase）
        ///
        /// 为什么放在 FileOps 而不是 FileFolders：
        ///   FileOps 的每个入口都要先规范化再判保护，它必须自包含 ——
        ///   FileOps 必须自包含 —— 调 FileFolders 会让编译依赖变重，
        ///   而 run-tests.ps1 里三个套件的依赖列表都得跟着改。
        ///   两份实现语义必须一致（各自有测试锁定）。
        /// </summary>
        public static bool TryNormalize(string raw, out string normalized)
        {
            normalized = null;
            try
            {
                if (string.IsNullOrWhiteSpace(raw)) return false;

                string p = raw.Trim().Trim('"');
                if (p.Length == 0) return false;

                p = p.Replace('/', '\\');
                p = Path.GetFullPath(p);

                // 去尾部斜杠，但 "D:\" 这种盘符根要保留
                if (p.Length > 3 && p.EndsWith("\\")) p = p.TrimEnd('\\');

                if (p.Length == 0) return false;
                normalized = p;
                return true;
            }
            catch { return false; }
        }

        /// <summary>path 是不是 sourceRoot 本身（不能对来源文件夹做修改）。</summary>
        public static bool IsSourceRoot(string path, string sourceRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(sourceRoot)) return false;
                return string.Equals(
                    path.TrimEnd('\\'), sourceRoot.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>child 是否在 parent 内部（含深层子目录）。</summary>
        public static bool IsInside(string child, string parent)
        {
            try
            {
                if (string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent)) return false;
                string c = child.TrimEnd('\\');
                string p = parent.TrimEnd('\\');
                if (c.Length == 0 || p.Length == 0) return false;

                if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase)) return true;
                return c.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ========== 日志（只追加，超 1000 条裁剪最旧）==========

        /// <summary>
        /// 追加一条操作日志。**写失败不影响操作**（调用方已经完成实际操作）。
        ///
        /// 记录内容：时间、操作、路径、结果。**不含文件内容**。
        /// </summary>
        static void Log(string op, string path, string result, string detail)
        {
            try
            {
                if (string.IsNullOrEmpty(logPath)) return;

                List<object> entries = ReadLogEntries();

                Dictionary<string, object> e = new Dictionary<string, object>();
                e["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                e["op"] = op ?? "";
                e["path"] = path ?? "";
                e["result"] = result ?? "";
                if (!string.IsNullOrEmpty(detail)) e["detail"] = detail;
                entries.Add(e);

                // 超过上限 → 裁剪最旧的
                if (entries.Count > MaxLogEntries)
                    entries.RemoveRange(0, entries.Count - MaxLogEntries);

                Dictionary<string, object> root = new Dictionary<string, object>();
                root["ops"] = entries;

                System.Web.Script.Serialization.JavaScriptSerializer ser =
                    new System.Web.Script.Serialization.JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                JsonIO.WriteAtomic(logPath, ser.Serialize(root));
            }
            catch (Exception ex)
            {
                // 日志失败不影响操作
                try { Warn("写 file-ops-log.json 失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>读现有日志条目（损坏 → 空列表）。</summary>
        static List<object> ReadLogEntries()
        {
            List<object> list = new List<object>();
            try
            {
                if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return list;

                string json = File.ReadAllText(logPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return list;

                System.Web.Script.Serialization.JavaScriptSerializer ser =
                    new System.Web.Script.Serialization.JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return list;

                object arr;
                if (!d.TryGetValue("ops", out arr) || arr == null) return list;

                foreach (object o in JsonIO.AsArray(arr))
                {
                    if (o != null) list.Add(o);
                }
            }
            catch { }
            return list;
        }

        /// <summary>读日志条目数（测试用）。</summary>
        public static int LogCount()
        {
            try { return ReadLogEntries().Count; }
            catch { return 0; }
        }

        // ========== 真实实现 ==========

        /// <summary>真实的系统调用实现（生产用）。</summary>
        public class RealFileSystem : IFileSystem
        {
            public bool DeleteToRecycleBin(List<string> paths, IntPtr hwnd, out string error)
            {
                error = null;
                try
                {
                    if (paths == null || paths.Count == 0) return true;

                    // SHFileOperation + FOF_ALLOWUNDO：移到回收站
                    //             + FOF_WANTNUKEWARNING：如果确实无法进回收站，
                    //               系统会弹「将永久删除」警告，而不是静默删掉
                    //             + FOF_NOCONFIRMATION 之外不要再加别的，避免绕过确认
                    SHFILEOPSTRUCT op = new SHFILEOPSTRUCT();
                    op.hwnd = hwnd;
                    op.wFunc = FO_DELETE;
                    op.pFrom = BuildDoubleNullTerminated(paths);
                    op.pTo = null;
                    op.fFlags = FOF_ALLOWUNDO | FOF_WANTNUKEWARNING
                        | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR;

                    int r = SHFileOperation(ref op);
                    if (r != 0)
                    {
                        error = "系统返回错误码 " + r;
                        return false;
                    }
                    if (op.fAnyOperationsAborted)
                    {
                        error = "操作被取消";
                        return false;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            public bool MoveWithinVolume(string src, string dst, out string error)
            {
                error = null;
                try
                {
                    if (Directory.Exists(src)) Directory.Move(src, dst);
                    else File.Move(src, dst);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            public bool MoveAcrossVolume(string src, string dst, IntPtr hwnd, out string error)
            {
                error = null;
                try
                {
                    // 交给系统文件操作（SHFileOperation FO_MOVE）
                    // 不自己复制再删除：那样中断会留下半成品，而且丢时间戳/属性
                    SHFILEOPSTRUCT op = new SHFILEOPSTRUCT();
                    op.hwnd = hwnd;
                    op.wFunc = FO_MOVE;
                    op.pFrom = BuildDoubleNullTerminated(new List<string> { src });
                    op.pTo = dst + "\0\0";
                    op.fFlags = FOF_NOCONFIRMMKDIR;

                    int r = SHFileOperation(ref op);
                    if (r != 0)
                    {
                        error = "系统返回错误码 " + r;
                        return false;
                    }
                    if (op.fAnyOperationsAborted)
                    {
                        error = "操作被取消";
                        return false;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            public bool CreateDirectory(string path, out string error)
            {
                error = null;
                try
                {
                    Directory.CreateDirectory(path);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            public bool FileExists(string path)
            {
                try { return File.Exists(path); } catch { return false; }
            }

            public bool DirectoryExists(string path)
            {
                try { return Directory.Exists(path); } catch { return false; }
            }

            public bool PathExists(string path)
            {
                try { return File.Exists(path) || Directory.Exists(path); }
                catch { return false; }
            }

            public bool IsSameVolume(string a, string b)
            {
                try
                {
                    string ra = VolumeRoot(a), rb = VolumeRoot(b);
                    return string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }

            public string VolumeRoot(string path)
            {
                try
                {
                    if (string.IsNullOrEmpty(path)) return "";
                    string r = Path.GetPathRoot(path);
                    return r ?? "";
                }
                catch { return ""; }
            }

            public DriveType GetDriveType(string volumeRoot)
            {
                try
                {
                    if (string.IsNullOrEmpty(volumeRoot)) return DriveType.Unknown;
                    return new DriveInfo(volumeRoot).DriveType;
                }
                catch { return DriveType.Unknown; }
            }

            /// <summary>SHFileOperation 要求以 \0 分隔、\0\0 结尾的双 null 串。</summary>
            static string BuildDoubleNullTerminated(List<string> paths)
            {
                StringBuilder sb = new StringBuilder();
                foreach (string p in paths)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    sb.Append(p);
                    sb.Append('\0');
                }
                sb.Append('\0');
                return sb.ToString();
            }

            // ---- P/Invoke ----

            const uint FO_DELETE = 0x0003;
            const uint FO_MOVE = 0x0001;

            const ushort FOF_ALLOWUNDO = 0x0040;         // 进回收站
            const ushort FOF_WANTNUKEWARNING = 0x4000;   // 无法进回收站时警告
            const ushort FOF_NOCONFIRMMKDIR = 0x0200;
            const ushort FOF_NOERRORUI = 0x0400;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct SHFILEOPSTRUCT
            {
                public IntPtr hwnd;
                public uint wFunc;
                public string pFrom;
                public string pTo;
                public ushort fFlags;
                [MarshalAs(UnmanagedType.Bool)]
                public bool fAnyOperationsAborted;
                public IntPtr hNameMappings;
                public string lpszProgressTitle;
            }

            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
        }
    }
}
