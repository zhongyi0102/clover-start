using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 「收纳桌面」与「放回桌面」的文件搬运。
    ///
    /// ── 安全底线（沿用上次事故的教训）──
    ///
    ///   1. 程序不删除任何用户文件：只做「核对式移动」。
    ///   2. 移动前确认目标不存在；移动后确认目标存在且大小一致。
    ///   3. 每次移动都写「原路径 → 新路径」日志 —— 上次事故就是缺这条。
    ///   4. 被占用 / 无权限的文件跳过并汇总，不中断整批。
    ///
    /// ── 与「删除标签」的区别 ──
    ///
    /// 这里不碰标签本身，只搬文件；删标签是另一条路径（DeskRemoval）。
    /// </summary>
    public static class DeskCollector
    {
        /// <summary>不收纳的扩展名（应用类）。</summary>
        static readonly string[] BlockedExt =
        {
            ".exe", ".lnk", ".msi", ".bat", ".cmd", ".com", ".scr", ".pif",
            ".appref-ms", ".url", ".msc", ".cpl", ".dll", ".sys", ".jar", ".ps1", ".vbs"
        };

        /// <summary>一个待收纳的候选文件。</summary>
        public class Candidate
        {
            public string Path = "";
            public long Size;
            public string Source = "";   // "用户桌面" / "公共桌面"
        }

        /// <summary>扫描结果。</summary>
        public class ScanResult
        {
            public List<Candidate> Items = new List<Candidate>();
            public List<string> Skipped = new List<string>();   // 应用类等
        }

        /// <summary>搬运结果。</summary>
        public class MoveResult
        {
            public List<DeskUndo.Entry> Moved = new List<DeskUndo.Entry>();
            public List<string> Failed = new List<string>();
            public List<string> Reasons = new List<string>();

            public int MovedCount { get { return Moved.Count; } }
            public bool AllOk { get { return Failed.Count == 0; } }
            public bool AnyMoved { get { return Moved.Count > 0; } }
        }

        /// <summary>该扩展名是否允许收纳。</summary>
        public static bool IsAllowed(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return false;
                if (Directory.Exists(path)) return true;   // 文件夹允许

                string ext = Path.GetExtension(path);
                if (string.IsNullOrEmpty(ext)) return true;

                foreach (string b in BlockedExt)
                    if (string.Equals(b, ext, StringComparison.OrdinalIgnoreCase)) return false;

                return true;
            }
            catch { return false; }
        }

        /// <summary>用户桌面 + 公共桌面。</summary>
        public static List<string> DesktopDirs()
        {
            List<string> dirs = new List<string>();
            try
            {
                string user = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(user) && Directory.Exists(user)) dirs.Add(user);
            }
            catch { }

            try
            {
                string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                if (!string.IsNullOrEmpty(common) && Directory.Exists(common)
                    && !dirs.Contains(common))
                    dirs.Add(common);
            }
            catch { }

            return dirs;
        }

        /// <summary>扫描桌面上的可收纳文件（只列，不动）。</summary>
        public static ScanResult Scan()
        {
            ScanResult r = new ScanResult();
            try
            {
                foreach (string dir in DesktopDirs())
                {
                    string label = IsCommonDesktop(dir) ? "公共桌面" : "用户桌面";

                    try
                    {
                        foreach (string f in Directory.GetFiles(dir))
                        {
                            try
                            {
                                FileAttributes attr = File.GetAttributes(f);
                                if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                                {
                                    r.Skipped.Add(Path.GetFileName(f));
                                    continue;
                                }
                            }
                            catch { }

                            if (!IsAllowed(f))
                            {
                                r.Skipped.Add(Path.GetFileName(f));
                                continue;
                            }

                            Candidate c = new Candidate();
                            c.Path = f;
                            c.Source = label;
                            try { c.Size = new FileInfo(f).Length; } catch { }
                            r.Items.Add(c);
                        }

                        foreach (string sub in Directory.GetDirectories(dir))
                        {
                            try
                            {
                                FileAttributes attr = File.GetAttributes(sub);
                                if ((attr & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                                {
                                    r.Skipped.Add(Path.GetFileName(sub));
                                    continue;
                                }
                            }
                            catch { }

                            Candidate c = new Candidate();
                            c.Path = sub;
                            c.Source = label;
                            c.Size = -1;
                            r.Items.Add(c);
                        }
                    }
                    catch (Exception ex)
                    {
                        try { Log.Warn("扫描桌面失败: " + dir + " / " + ex.Message); } catch { }
                    }
                }

                r.Items.Sort(delegate(Candidate a, Candidate b)
                {
                    return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
                });
            }
            catch (Exception ex)
            {
                try { Log.Warn("扫描桌面异常: " + ex.Message); } catch { }
            }
            return r;
        }

        static bool IsCommonDesktop(string dir)
        {
            try
            {
                string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                return !string.IsNullOrEmpty(common)
                    && string.Equals(dir, common, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// 把一批文件收纳进 destDir。
        ///
        /// 同名自动加 (1)；占用/无权限跳过并汇总；每个成功项写路径日志。
        /// </summary>
        public static MoveResult Collect(List<string> paths, string destDir)
        {
            MoveResult r = new MoveResult();
            try
            {
                if (paths == null || string.IsNullOrEmpty(destDir)) return r;
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

                foreach (string raw in paths)
                {
                    if (string.IsNullOrEmpty(raw)) continue;

                    string src;
                    try { src = Path.GetFullPath(raw); } catch { continue; }

                    if (!File.Exists(src) && !Directory.Exists(src))
                    {
                        r.Failed.Add(Path.GetFileName(src));
                        r.Reasons.Add("源文件不存在");
                        continue;
                    }

                    if (!IsAllowed(src))
                    {
                        r.Failed.Add(Path.GetFileName(src));
                        r.Reasons.Add("应用类文件不收纳");
                        continue;
                    }

                    string name = Path.GetFileName(src);
                    bool isFolder;
                    try { isFolder = Directory.Exists(src); } catch { isFolder = false; }

                    try
                    {
                        string target = MakeUniqueTarget(destDir, name, isFolder);
                        VerifiedFileMove.Result moved = VerifiedFileMove.Move(src, target);
                        if (!moved.Ok)
                        {
                            r.Failed.Add(name);
                            r.Reasons.Add(moved.Message);
                            continue;
                        }

                        DeskUndo.Entry e = new DeskUndo.Entry();
                        e.From = src;
                        e.To = moved.Target;
                        e.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        r.Moved.Add(e);

                        // 上次事故暴露的缺口：必须记录「原路径 → 新路径」
                        try { Log.Info("收纳: " + e.From + " → " + e.To); } catch { }
                    }
                    catch (Exception ex)
                    {
                        r.Failed.Add(name);
                        r.Reasons.Add(ex.Message);
                    }
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
        /// 把虚拟桌面里的文件移回真实桌面。
        ///
        /// 同名自动加 (1)；占用/无权限跳过并汇总。
        /// </summary>
        public static MoveResult PutBack(List<string> paths, string desktopDir)
        {
            MoveResult r = new MoveResult();
            try
            {
                if (paths == null || paths.Count == 0) return r;

                if (string.IsNullOrEmpty(desktopDir) || !Directory.Exists(desktopDir))
                {
                    r.Failed.Add("(桌面)");
                    r.Reasons.Add("取不到桌面路径");
                    return r;
                }

                foreach (string raw in paths)
                {
                    if (string.IsNullOrEmpty(raw)) continue;

                    string src;
                    try { src = Path.GetFullPath(raw); } catch { continue; }

                    if (!File.Exists(src) && !Directory.Exists(src))
                    {
                        r.Failed.Add(Path.GetFileName(src));
                        r.Reasons.Add("源文件不存在");
                        continue;
                    }

                    string name = Path.GetFileName(src);
                    bool isFolder;
                    try { isFolder = Directory.Exists(src); } catch { isFolder = false; }

                    try
                    {
                        string target = MakeUniqueTarget(desktopDir, name, isFolder);
                        VerifiedFileMove.Result moved = VerifiedFileMove.Move(src, target);
                        if (!moved.Ok)
                        {
                            r.Failed.Add(name);
                            r.Reasons.Add(moved.Message);
                            continue;
                        }

                        DeskUndo.Entry e = new DeskUndo.Entry();
                        e.From = src;
                        e.To = moved.Target;
                        e.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        r.Moved.Add(e);

                        try { Log.Info("放回桌面: " + e.From + " → " + e.To); } catch { }
                    }
                    catch (Exception ex)
                    {
                        r.Failed.Add(name);
                        r.Reasons.Add(ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                r.Failed.Add("(整体)");
                r.Reasons.Add(ex.Message);
            }
            return r;
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
            catch { return Path.Combine(dir, fileName); }
        }

        static bool Exists(string path)
        {
            try { return File.Exists(path) || Directory.Exists(path); }
            catch { return false; }
        }
    }
}
