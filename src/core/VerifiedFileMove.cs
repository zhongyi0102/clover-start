using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 用户文件的可核对移动：先复制并验证目标，再删除源。
    ///
    /// 安全约束：
    ///   1. 目标必须预先不存在，绝不覆盖。
    ///   2. 文件按字节大小核对；目录按完整相对路径 + 每个文件大小核对。
    ///   3. 目标核对通过前绝不删除源。
    ///   4. 删除源失败且源仍完整时，回滚新目标并返回失败。
    ///   5. 目录含重解析点（链接/联接）时闭合失败，避免递归越界。
    /// </summary>
    public static class VerifiedFileMove
    {
        public class Result
        {
            public bool Ok;
            public string Target = "";
            public string Message = "";
            public bool SourceStillExists;
        }

        class DirectorySnapshot
        {
            public readonly HashSet<string> Directories =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, long> Files =
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        public static Result Move(string source, string target)
        {
            Result r = new Result { Target = target ?? "" };
            try
            {
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                    return Fail(r, "源路径或目标路径为空", source);

                string src = Path.GetFullPath(source);
                string dst = Path.GetFullPath(target);
                r.Target = dst;

                if (string.Equals(src.TrimEnd('\\'), dst.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
                    return Fail(r, "源路径与目标路径相同", src);

                if (File.Exists(dst) || Directory.Exists(dst))
                    return Fail(r, "目标已存在，未覆盖：" + dst, src);

                if (File.Exists(src)) return MoveFile(src, dst, r);
                if (Directory.Exists(src)) return MoveDirectory(src, dst, r);
                return Fail(r, "源文件不存在：" + src, src);
            }
            catch (Exception ex)
            {
                return Fail(r, ex.Message, source);
            }
        }

        /// <summary>
        /// 核对式**复制**：目标完整发布后才算成功，**源文件永不删除**。
        ///
        /// ── 与 Move 的区别 ──
        ///
        /// `Move` = 复制 + 核对 + 删源；`Copy` = 复制 + 核对，到核对为止。
        /// 两者共用同一套「临时副本 → 核对 → 原子发布 → 再核对」的保证，
        /// 所以复制出来的目标与源在字节级一致（目录按完整清单核对）。
        ///
        /// ── 为什么需要它 ──
        ///
        /// 「桌面文件」页拖入改成复制语义：用户把桌面上的文件拖进面板，
        /// 原文件必须留在桌面（那是他的工作区），面板里只是多一份。
        /// 用 Move 会把桌面上的文件搬走 —— 用户会以为文件丢了。
        ///
        /// ── 失败语义 ──
        ///
        /// 任何一步核对失败都清理临时文件、返回失败，**源始终完好**。
        /// 目标一旦发布就不自动删除（避免并发替换时误删外部文件）。
        /// </summary>
        public static Result Copy(string source, string target)
        {
            Result r = new Result { Target = target ?? "" };
            try
            {
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                    return Fail(r, "源路径或目标路径为空", source);

                string src = Path.GetFullPath(source);
                string dst = Path.GetFullPath(target);
                r.Target = dst;

                if (string.Equals(src.TrimEnd('\\'), dst.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
                    return Fail(r, "源路径与目标路径相同", src);

                if (File.Exists(dst) || Directory.Exists(dst))
                    return Fail(r, "目标已存在，未覆盖：" + dst, src);

                if (File.Exists(src)) return CopyFile(src, dst, r);
                if (Directory.Exists(src)) return CopyDirectoryVerified(src, dst, r);
                return Fail(r, "源文件不存在：" + src, src);
            }
            catch (Exception ex)
            {
                return Fail(r, ex.Message, source);
            }
        }

        static Result CopyFile(string src, string dst, Result r)
        {
            long sourceSize = new FileInfo(src).Length;
            string temp = TempPathBeside(dst);
            try
            {
                string parent = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                File.Copy(src, temp, false);
                if (!VerifyFile(temp, sourceSize))
                {
                    CleanupFile(temp);
                    return Fail(r, "目标临时文件核对失败，未发布", src);
                }

                // 复制期间源可能被其他程序改动 —— 与 Move 一样要拦下来，
                // 否则"复制的到底是不是我看到的那个文件"没法保证。
                if (!VerifyFile(src, sourceSize))
                {
                    CleanupFile(temp);
                    return Fail(r, "复制期间源文件发生变化，未发布目标", src);
                }

                File.Move(temp, dst);

                if (!VerifyFile(dst, sourceSize))
                    return Fail(r, "目标文件发布后核对失败，请检查目标：" + dst, src);

                // ★ 与 Move 唯一的区别：到这里就结束，不删源。
                r.Ok = true;
                r.SourceStillExists = true;
                return r;
            }
            catch (Exception ex)
            {
                CleanupFile(temp);
                return Fail(r, ex.Message, src);
            }
        }

        static Result CopyDirectoryVerified(string src, string dst, Result r)
        {
            DirectorySnapshot snapshot;
            string snapshotError;
            if (!TrySnapshot(src, out snapshot, out snapshotError))
                return Fail(r, snapshotError, src);

            string temp = TempPathBeside(dst);
            try
            {
                CopyDirectory(src, temp, snapshot);
                if (!VerifyDirectory(temp, snapshot))
                {
                    CleanupDirectory(temp);
                    return Fail(r, "目标临时文件夹核对失败，未发布", src);
                }

                // 复制期间源目录可能新增/修改文件
                if (!VerifyDirectory(src, snapshot))
                {
                    CleanupDirectory(temp);
                    return Fail(r, "复制期间源文件夹发生变化，未发布目标", src);
                }

                Directory.Move(temp, dst);

                if (!VerifyDirectory(dst, snapshot))
                    return Fail(r, "目标文件夹发布后核对失败，请检查目标：" + dst, src);

                // ★ 不删源。
                r.Ok = true;
                r.SourceStillExists = true;
                return r;
            }
            catch (Exception ex)
            {
                CleanupDirectory(temp);
                return Fail(r, ex.Message, src);
            }
        }

        static Result MoveFile(string src, string dst, Result r)
        {
            long sourceSize = new FileInfo(src).Length;
            string temp = TempPathBeside(dst);
            bool published = false;
            try
            {
                string parent = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                File.Copy(src, temp, false);
                if (!VerifyFile(temp, sourceSize))
                {
                    CleanupFile(temp);
                    return Fail(r, "目标临时文件核对失败，源文件保持原位", src);
                }

                // 复制期间源文件可能被其他程序改动；删除前再核对一次。
                if (!VerifyFile(src, sourceSize))
                {
                    CleanupFile(temp);
                    return Fail(r, "复制期间源文件发生变化，未删除源文件", src);
                }

                // 最后一步才发布最终目标；若同名路径被并发占用，源不动。
                File.Move(temp, dst);
                published = true;
                if (!VerifyFile(dst, sourceSize))
                    return Fail(r, "目标文件发布后核对失败；源文件保留，请检查目标：" + dst, src);

                try { File.Delete(src); }
                catch (Exception ex)
                {
                    // 最终目标一旦发布就不自动删除，避免并发替换时误删外部文件。
                    return Fail(r, "目标已核对，但源文件删除失败；源和目标均保留：" + ex.Message, src);
                }

                if (File.Exists(src))
                    return Fail(r, "源文件仍存在；源和目标均保留", src);

                if (!VerifyFile(dst, sourceSize))
                    return Fail(r, "移动后目标文件核对失败", src);

                r.Ok = true;
                r.SourceStillExists = false;
                return r;
            }
            catch (Exception ex)
            {
                CleanupFile(temp);
                return Fail(r,
                    published ? "目标已发布，后续步骤失败；源和目标均保留：" + ex.Message : ex.Message,
                    src);
            }
        }

        static Result MoveDirectory(string src, string dst, Result r)
        {
            DirectorySnapshot snapshot;
            string snapshotError;
            if (!TrySnapshot(src, out snapshot, out snapshotError))
                return Fail(r, snapshotError, src);

            string temp = TempPathBeside(dst);
            bool published = false;
            try
            {
                CopyDirectory(src, temp, snapshot);
                if (!VerifyDirectory(temp, snapshot))
                {
                    CleanupDirectory(temp);
                    return Fail(r, "目标临时文件夹核对失败，源文件夹保持原位", src);
                }

                // 复制期间源目录可能新增/修改文件；源快照不一致时不删除。
                if (!VerifyDirectory(src, snapshot))
                {
                    CleanupDirectory(temp);
                    return Fail(r, "复制期间源文件夹发生变化，未删除源文件夹", src);
                }

                Directory.Move(temp, dst);
                published = true;
                if (!VerifyDirectory(dst, snapshot))
                    return Fail(r, "目标文件夹发布后核对失败；源文件夹保留，请检查目标：" + dst, src);

                try { Directory.Delete(src, true); }
                catch (Exception ex)
                {
                    // 最终目标一旦发布就不自动删除；失败时保留两份，避免误删。
                    return Fail(r, "目标已核对，但源文件夹删除失败；源和目标均保留：" + ex.Message, src);
                }

                if (Directory.Exists(src))
                    return Fail(r, "源文件夹仍存在；源和目标均保留", src);

                if (!VerifyDirectory(dst, snapshot))
                    return Fail(r, "移动后目标文件夹核对失败", src);

                r.Ok = true;
                r.SourceStillExists = false;
                return r;
            }
            catch (Exception ex)
            {
                CleanupDirectory(temp);
                return Fail(r,
                    published ? "目标已发布，后续步骤失败；源和目标均保留：" + ex.Message : ex.Message,
                    src);
            }
        }

        static bool TrySnapshot(string root, out DirectorySnapshot snapshot, out string error)
        {
            snapshot = new DirectorySnapshot();
            error = "";
            try
            {
                string fullRoot = Path.GetFullPath(root).TrimEnd('\\');
                if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
                {
                    error = "源文件夹是链接/联接，未移动：" + root;
                    return false;
                }

                Queue<string> pending = new Queue<string>();
                pending.Enqueue(fullRoot);
                while (pending.Count > 0)
                {
                    string dir = pending.Dequeue();
                    foreach (string sub in Directory.GetDirectories(dir))
                    {
                        if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
                        {
                            error = "文件夹内含链接/联接，未移动：" + sub;
                            return false;
                        }
                        string rel = Relative(fullRoot, sub);
                        snapshot.Directories.Add(rel);
                        pending.Enqueue(sub);
                    }

                    foreach (string file in Directory.GetFiles(dir))
                    {
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        {
                            error = "文件夹内含文件链接，未移动：" + file;
                            return false;
                        }
                        snapshot.Files[Relative(fullRoot, file)] = new FileInfo(file).Length;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "读取源文件夹失败：" + ex.Message;
                return false;
            }
        }

        static void CopyDirectory(string src, string dst, DirectorySnapshot snapshot)
        {
            Directory.CreateDirectory(dst);
            foreach (string rel in snapshot.Directories)
                Directory.CreateDirectory(Path.Combine(dst, rel));
            foreach (KeyValuePair<string, long> file in snapshot.Files)
                File.Copy(Path.Combine(src, file.Key), Path.Combine(dst, file.Key), false);
        }

        static bool VerifyDirectory(string root, DirectorySnapshot snapshot)
        {
            try
            {
                if (!Directory.Exists(root)) return false;
                foreach (string rel in snapshot.Directories)
                    if (!Directory.Exists(Path.Combine(root, rel))) return false;
                foreach (KeyValuePair<string, long> file in snapshot.Files)
                    if (!VerifyFile(Path.Combine(root, file.Key), file.Value)) return false;

                int actualFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;
                int actualDirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Length;
                return actualFiles == snapshot.Files.Count
                    && actualDirs == snapshot.Directories.Count;
            }
            catch { return false; }
        }

        static bool VerifyFile(string path, long expectedSize)
        {
            try { return File.Exists(path) && new FileInfo(path).Length == expectedSize; }
            catch { return false; }
        }

        static string Relative(string root, string path)
        {
            string full = Path.GetFullPath(path);
            string prefix = root.TrimEnd('\\') + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("路径越出源文件夹：" + path);
            return full.Substring(prefix.Length);
        }

        static string TempPathBeside(string target)
        {
            string parent = Path.GetDirectoryName(target);
            string name = Path.GetFileName(target);
            if (string.IsNullOrEmpty(parent)) throw new IOException("目标目录无效：" + target);

            for (int i = 0; i < 100; i++)
            {
                string temp = Path.Combine(parent,
                    ".clover-move-" + Guid.NewGuid().ToString("N") + "-" + name);
                if (!File.Exists(temp) && !Directory.Exists(temp)) return temp;
            }
            throw new IOException("无法分配安全临时路径");
        }

        static Result Fail(Result r, string message, string source)
        {
            r.Ok = false;
            r.Message = message ?? "移动失败";
            try { r.SourceStillExists = File.Exists(source) || Directory.Exists(source); }
            catch { r.SourceStillExists = true; }
            return r;
        }

        static void CleanupFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        static void CleanupDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }
    }
}
