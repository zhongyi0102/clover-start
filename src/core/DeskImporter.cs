using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 把外部拖进来的文件**移动**进某个虚拟桌面的存放文件夹。
    ///
    /// ── 需求边界 ──
    ///
    ///   1. 只处理**非应用文件**（文档、图片、压缩包、文件夹…）。
    ///      `.exe` / `.lnk` 直接忽略：不弹窗、不移动、不提示。
    ///      理由：应用有自己的归宿（「常用」「全部」），虚拟桌面只放文件。
    ///   2. 同盘用 `File.Move`（底层 rename，瞬时）；跨盘自动退化为复制+删除。
    ///   3. 同名冲突按「保留两者」处理：改成 `名称 (1)`，绝不覆盖。
    ///   4. 被占用失败时文件保持原位，返回原因给调用方汇总。
    ///
    /// ── 为什么单独成类 ──
    ///
    /// 过滤规则、跨盘退化、冲突改名都需要单元测试；
    /// 塞进 Drop 事件里就没法测了。
    /// </summary>
    public static class DeskImporter
    {
        /// <summary>不接收的扩展名（应用类，有自己的归宿）。</summary>
        static readonly string[] BlockedExt =
        {
            ".exe", ".lnk", ".msi", ".bat", ".cmd", ".com", ".scr", ".pif",
            ".appref-ms", ".url", ".msc", ".cpl", ".dll", ".sys"
        };

        /// <summary>一次导入的结果。</summary>
        public class Result
        {
            /// <summary>成功移入的文件名（用于落位）。</summary>
            public List<string> Imported = new List<string>();

            /// <summary>被过滤掉的（.exe/.lnk 等），不提示。</summary>
            public List<string> Skipped = new List<string>();

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
            Result r = new Result();
            try
            {
                if (paths == null || string.IsNullOrEmpty(destDir)) return r;

                if (!Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                foreach (string raw in paths)
                {
                    if (string.IsNullOrEmpty(raw)) continue;

                    string src;
                    try { src = Path.GetFullPath(raw); }
                    catch { continue; }

                    // ---- 过滤应用类 ----
                    if (!IsAllowed(src))
                    {
                        r.Skipped.Add(Path.GetFileName(src));
                        continue;
                    }

                    if (!File.Exists(src) && !Directory.Exists(src))
                    {
                        r.Failed.Add(Path.GetFileName(src));
                        r.Reasons.Add("源文件不存在");
                        continue;
                    }

                    // 已经在目标目录里 → 视为成功，不重复搬
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

        static void MoveOne(string src, string destDir, Result r)
        {
            string name = Path.GetFileName(src);
            bool isFolder;
            try { isFolder = Directory.Exists(src); }
            catch { isFolder = false; }

            try
            {
                string target = MakeUniqueTarget(destDir, name, isFolder);

                if (isFolder)
                {
                    MoveFolder(src, target);
                }
                else
                {
                    MoveFile(src, target);
                }

                r.Imported.Add(Path.GetFileName(target));
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

        /// <summary>
        /// 移动文件。同盘 File.Move 就是 rename；跨盘 .NET 会抛异常，
        /// 这时退化为「复制 + 删源」。
        /// </summary>
        static void MoveFile(string src, string target)
        {
            try
            {
                File.Move(src, target);
            }
            catch (IOException)
            {
                // 跨盘（或目标已存在）：先复制再删
                File.Copy(src, target, false);
                try { File.Delete(src); }
                catch
                {
                    // 复制成功但删不掉源：保留副本，抛出让人知道源还在
                    throw new IOException("已复制到目标，但源文件删除失败（可能被占用）");
                }
            }
        }

        /// <summary>移动文件夹，跨盘时递归复制后删源。</summary>
        static void MoveFolder(string src, string target)
        {
            try
            {
                Directory.Move(src, target);
            }
            catch (IOException)
            {
                CopyDir(src, target);
                try { Directory.Delete(src, true); }
                catch
                {
                    throw new IOException("已复制到目标，但源文件夹删除失败（可能被占用）");
                }
            }
        }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);

            foreach (string f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), false);

            foreach (string d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
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
