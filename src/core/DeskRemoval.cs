using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 删除虚拟桌面标签时，处理它对应文件夹里的文件。
    ///
    /// ── 两种去向 ──
    ///
    ///   · **放回桌面**：把文件移到真实桌面，然后删掉空的存放文件夹
    ///   · **保留在原处**：只删标签记录，文件夹和文件都不动
    ///
    /// ── 为什么不直接写在 UI 里 ──
    ///
    /// 搬运 + 同名冲突改名 + 占用失败汇总，这些逻辑需要单元测试；
    /// 塞进 ContextMenu 的 delegate 里就没法测了。
    ///
    /// ── 关键约束 ──
    ///
    ///   1. **不覆盖**：目标同名时自动改成「名称 (1)」「名称 (2)」…
    ///   2. **失败保持原位**：被占用的文件不动，最后汇总报告
    ///   3. **只在全部成功后才删空文件夹**：有失败就不删
    ///   4. 只处理**直接子项**，不递归（子文件夹整体搬走即可）
    /// </summary>
    public static class DeskRemoval
    {
        /// <summary>一次删除的结果。</summary>
        public class Result
        {
            /// <summary>成功放回桌面的文件数。</summary>
            public int Moved;

            /// <summary>因占用/权限等原因未能放回的项（文件名）。</summary>
            public List<string> Failed = new List<string>();

            /// <summary>失败原因（每项一句话，与 Failed 对应）。</summary>
            public List<string> Reasons = new List<string>();

            /// <summary>存放文件夹是否已被删除（空了才删）。</summary>
            public bool FolderDeleted;

            /// <summary>是否全部成功（没有失败项）。</summary>
            public bool AllOk { get { return Failed.Count == 0; } }
        }

        /// <summary>
        /// 把一个虚拟桌面的文件**放回真实桌面**，然后删掉空文件夹。
        ///
        /// 返回详细结果，调用方据此决定是否删除标签记录。
        /// </summary>
        public static Result MoveToDesktop(string deskFolder)
        {
            Result r = new Result();
            try
            {
                if (string.IsNullOrEmpty(deskFolder)) return r;
                if (!Directory.Exists(deskFolder)) return r;

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrEmpty(desktop))
                {
                    r.Failed.Add("(桌面)");
                    r.Reasons.Add("取不到真实桌面路径");
                    return r;
                }

                // ---- 逐个搬 ----
                List<string> files = new List<string>(Directory.GetFiles(deskFolder));
                List<string> dirs = new List<string>(Directory.GetDirectories(deskFolder));

                files.Sort(StringComparer.OrdinalIgnoreCase);
                dirs.Sort(StringComparer.OrdinalIgnoreCase);

                foreach (string f in files) MoveOne(f, desktop, r, false);
                foreach (string d in dirs) MoveOne(d, desktop, r, true);

                // ---- 全成功才删空文件夹 ----
                if (r.AllOk)
                {
                    try
                    {
                        // 只删空目录：万一有残留（比如刚被别的程序写进文件）就不删
                        if (Directory.Exists(deskFolder))
                        {
                            if (Directory.GetFileSystemEntries(deskFolder).Length == 0)
                            {
                                Directory.Delete(deskFolder, false);
                                r.FolderDeleted = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        r.Failed.Add("(文件夹)");
                        r.Reasons.Add("删空文件夹失败：" + ex.Message);
                    }
                }

                return r;
            }
            catch (Exception ex)
            {
                r.Failed.Add("(整体)");
                r.Reasons.Add(ex.Message);
                return r;
            }
        }

        /// <summary>搬一个文件或文件夹，冲突时自动改名。</summary>
        static void MoveOne(string source, string desktop, Result r, bool isFolder)
        {
            try
            {
                string name = Path.GetFileName(source);
                string target = MakeUniqueTarget(desktop, name, isFolder);

                VerifiedFileMove.Result moved = VerifiedFileMove.Move(source, target);
                if (!moved.Ok) throw new IOException(moved.Message);

                r.Moved++;
            }
            catch (IOException ex)
            {
                // 被占用 / 权限不足：文件保持原位
                r.Failed.Add(Path.GetFileName(source));
                r.Reasons.Add(IsSharing(ex) ? "文件正在使用" : ex.Message);
            }
            catch (UnauthorizedAccessException)
            {
                r.Failed.Add(Path.GetFileName(source));
                r.Reasons.Add("没有权限");
            }
            catch (Exception ex)
            {
                r.Failed.Add(Path.GetFileName(source));
                r.Reasons.Add(ex.Message);
            }
        }

        /// <summary>
        /// 生成一个不冲突的目标路径：同名时依次试「名称 (1)」「名称 (2)」…
        ///
        /// **绝不覆盖已有文件**。
        /// </summary>
        public static string MakeUniqueTarget(string dir, string fileName, bool isFolder)
        {
            try
            {
                string target = Path.Combine(dir, fileName);
                if (!Exists(target)) return target;

                string baseName = Path.GetFileNameWithoutExtension(fileName);
                string ext = Path.GetExtension(fileName);

                // 文件夹没有扩展名，GetExtension 返回空 —— 走同一个分支也正确
                for (int n = 1; n <= 9999; n++)
                {
                    string cand = Path.Combine(dir, baseName + " (" + n + ")" + ext);
                    if (!Exists(cand)) return cand;
                }

                // 极端情况：9999 个同名，加时间戳兜底
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
            try
            {
                return File.Exists(path) || Directory.Exists(path);
            }
            catch { return false; }
        }

        static bool IsSharing(IOException ex)
        {
            try
            {
                // ERROR_SHARING_VIOLATION(32) / ERROR_LOCK_VIOLATION(33)
                int code = System.Runtime.InteropServices.Marshal.GetHRForException(ex) & 0xFFFF;
                return code == 32 || code == 33;
            }
            catch { return false; }
        }

        /// <summary>
        /// 删除标签时的「保留在原处」：什么都不做，只返回文件夹路径给调用方提示。
        /// </summary>
        public static string KeepInPlace(string deskFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(deskFolder)) return "";
                return deskFolder;
            }
            catch { return ""; }
        }

        /// <summary>
        /// 判断一个虚拟桌面里有没有文件（决定要不要弹窗）。
        /// </summary>
        public static bool HasContent(string deskFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(deskFolder)) return false;
                if (!Directory.Exists(deskFolder)) return false;
                return Directory.GetFileSystemEntries(deskFolder).Length > 0;
            }
            catch { return false; }
        }
    }
}
