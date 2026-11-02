using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 「桌面文件」存放目录的切换：校验新目录 → 统计要搬多少东西 → 逐项核对迁移。
    ///
    /// ── 为什么单独一个类 ──
    ///
    /// 换目录有三件事必须做对，而且都要能单测：
    ///   1. 拦住明显不对的目标（相对路径、文件、同一个、互相嵌套）
    ///   2. 迁移前把「几个桌面、几个文件」算准，让用户知道自己在点什么
    ///   3. 迁移逐项走 `VerifiedFileMove`，失败项**留在原处**并逐条报告
    ///
    /// 塞进设置窗口的点击回调里就没法测了。
    ///
    /// ── 不做什么 ──
    ///
    ///   · 不改 `VirtualDesks.Root`（调用方拿到 Ok 之后自己改并 Save）
    ///   · 不删旧目录（可能还有用户自己放的东西，删了没法还原）
    ///   · 不做「只改指针」的分支（那是调用方的选择，这里只管搬）
    /// </summary>
    public static class DeskRootChange
    {
        /// <summary>校验结果分类。</summary>
        public enum Verdict
        {
            Ok = 0,
            Empty,
            NotAbsolute,
            IsFile,
            Same,
            InsideOld,
            ContainsOld,
            CannotCreate,
        }

        /// <summary>一次校验的结果。</summary>
        public class Check
        {
            public Verdict Verdict = Verdict.Empty;

            /// <summary>规范化后的完整路径（Verdict 为 Ok 时有意义）。</summary>
            public string Full = "";

            /// <summary>给用户看的一句话。Ok 时为空。</summary>
            public string Message = "";

            public bool Ok { get { return Verdict == Verdict.Ok; } }
        }

        /// <summary>迁移前的盘点：会搬几个桌面、几个文件。</summary>
        public class Plan
        {
            /// <summary>原目录里真实存在的桌面（目录名）。</summary>
            public List<string> Desks = new List<string>();

            /// <summary>这些桌面里所有文件的总数（含子文件夹里的）。</summary>
            public int Files;

            /// <summary>这些桌面的顶层项数（文件 + 文件夹）。</summary>
            public int Entries;

            public int DeskCount { get { return Desks.Count; } }

            public bool Empty { get { return Desks.Count == 0; } }
        }

        /// <summary>一次迁移的结果。</summary>
        public class Result
        {
            public int MovedDesks;
            public int MovedFiles;

            /// <summary>原目录里就没有、直接跳过的桌面数。</summary>
            public int Skipped;

            /// <summary>失败的桌面名（与 Reasons 一一对应）。</summary>
            public List<string> Failed = new List<string>();

            public List<string> Reasons = new List<string>();

            public bool AllOk { get { return Failed.Count == 0; } }
        }

        // ========== 校验 ==========

        /// <summary>
        /// 校验新目录能不能当存放根目录。
        ///
        /// 会**尝试建目录并写一个探针文件再删掉** —— 这是唯一能确认
        /// 「这个位置真的可用」的办法；只判断字符串会到迁移时才炸。
        /// </summary>
        public static Check Validate(string newRoot, string oldRoot)
        {
            Check c = new Check();
            try
            {
                string raw = (newRoot ?? "").Trim().Trim('"');
                if (raw.Length == 0)
                {
                    c.Verdict = Verdict.Empty;
                    c.Message = "目录为空。";
                    return c;
                }

                if (!Path.IsPathRooted(raw))
                {
                    c.Verdict = Verdict.NotAbsolute;
                    c.Message = "请选一个完整路径（例如 D:\\四叶草\\desks），不要用相对路径。";
                    return c;
                }

                string full = Path.GetFullPath(raw).TrimEnd('\\', '/');
                c.Full = full;

                if (File.Exists(full))
                {
                    c.Verdict = Verdict.IsFile;
                    c.Message = "这个路径是个文件，不是文件夹。";
                    return c;
                }

                string oldFull = SafeFull(oldRoot);
                if (oldFull.Length > 0)
                {
                    if (string.Equals(full, oldFull, StringComparison.OrdinalIgnoreCase))
                    {
                        c.Verdict = Verdict.Same;
                        c.Message = "和当前的存放目录是同一个。";
                        return c;
                    }

                    // 新目录在旧目录里面 → 搬完之后旧目录里套着新目录，越来越深
                    if (IsUnder(full, oldFull))
                    {
                        c.Verdict = Verdict.InsideOld;
                        c.Message = "不能选当前存放目录里面的子文件夹，否则新旧目录会互相嵌套。";
                        return c;
                    }

                    // 新目录是旧目录的上级 → 旧目录本身会留在新目录里，
                    // 它的名字可能和某个桌面重名，之后每次列桌面都要绕开它
                    if (IsUnder(oldFull, full))
                    {
                        c.Verdict = Verdict.ContainsOld;
                        c.Message = "不能选当前存放目录的上级目录：旧目录会留在新目录里，"
                            + "和桌面文件夹混在一起。请选一个不相干的位置。";
                        return c;
                    }
                }

                // 真的建得出来吗
                try { Directory.CreateDirectory(full); }
                catch (Exception ex)
                {
                    c.Verdict = Verdict.CannotCreate;
                    c.Message = "建不出这个文件夹：" + ex.Message;
                    return c;
                }

                // 真的写得进去吗（建目录成功不代表可写，比如只读共享）
                try
                {
                    string probe = Path.Combine(full, ".clover-write-" + Guid.NewGuid().ToString("N") + ".tmp");
                    File.WriteAllText(probe, "x");
                    File.Delete(probe);
                }
                catch (Exception ex)
                {
                    c.Verdict = Verdict.CannotCreate;
                    c.Message = "这个文件夹不可写：" + ex.Message;
                    return c;
                }

                c.Verdict = Verdict.Ok;
                c.Message = "";
                return c;
            }
            catch (Exception ex)
            {
                c.Verdict = Verdict.CannotCreate;
                c.Message = "路径不合法：" + ex.Message;
                return c;
            }
        }

        // ========== 盘点 ==========

        /// <summary>
        /// 数一数原目录里到底有什么：几个桌面、几个文件。
        ///
        /// 只统计 `deskNames` 里列出的桌面 —— 目录里可能有用户自己丢进来的
        /// 无关文件夹，那些不算、也不搬。
        /// </summary>
        public static Plan Inspect(string root, IEnumerable<string> deskNames)
        {
            Plan p = new Plan();
            try
            {
                if (string.IsNullOrEmpty(root) || deskNames == null) return p;

                foreach (string name in deskNames)
                {
                    if (!VirtualDesks.IsSafeName(name)) continue;

                    string dir;
                    try { dir = Path.Combine(root, name); }
                    catch { continue; }

                    if (!Directory.Exists(dir)) continue;

                    p.Desks.Add(name);
                    try { p.Entries += Directory.GetFileSystemEntries(dir).Length; }
                    catch { }
                    p.Files += CountFiles(dir);
                }
            }
            catch { }
            return p;
        }

        /// <summary>递归数文件（遇到链接/联接不进去，避免绕圈）。</summary>
        static int CountFiles(string dir)
        {
            int n = 0;
            try
            {
                Queue<string> pending = new Queue<string>();
                pending.Enqueue(dir);

                while (pending.Count > 0)
                {
                    string d = pending.Dequeue();

                    try
                    {
                        foreach (string f in Directory.GetFiles(d)) n++;
                    }
                    catch { }

                    try
                    {
                        foreach (string s in Directory.GetDirectories(d))
                        {
                            try
                            {
                                if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue;
                            }
                            catch { continue; }
                            pending.Enqueue(s);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return n;
        }

        // ========== 迁移 ==========

        /// <summary>
        /// 把每个桌面文件夹从 oldRoot 搬到 newRoot，逐项核对。
        ///
        /// ── 全有或全无 ──
        ///
        /// 先做一遍**预检**（目标是否已被占用、名字是否合法），任何一项过不了
        /// 就**一个都不搬**。预检通过后开始搬；中途若有失败（占用/权限这类
        /// 预检看不出的），把已经搬成功的**搬回原处**，恢复成没搬过的样子。
        ///
        /// 为什么必须这样：root 是单个值，半搬会让「切换后界面空一半、
        /// 文件却已经在新目录」—— 用户看到的是文件不见了。全有或全无，
        /// 调用方才能安全地改 root。
        /// </summary>
        public static Result Migrate(string oldRoot, string newRoot, IEnumerable<string> deskNames)
        {
            Result r = new Result();
            try
            {
                if (string.IsNullOrEmpty(oldRoot) || string.IsNullOrEmpty(newRoot) || deskNames == null)
                    return r;

                // ---- 1. 预检：把「注定失败」的全部提前拦下 ----
                List<string> todo = new List<string>();
                foreach (string name in deskNames)
                {
                    if (!VirtualDesks.IsSafeName(name))
                    {
                        r.Failed.Add(name ?? "(空名)");
                        r.Reasons.Add("名称不合法，未搬");
                        continue;
                    }

                    string src, dst;
                    try
                    {
                        src = Path.Combine(oldRoot, name);
                        dst = Path.Combine(newRoot, name);
                    }
                    catch (Exception ex)
                    {
                        r.Failed.Add(name);
                        r.Reasons.Add(ex.Message);
                        continue;
                    }

                    if (!Directory.Exists(src))
                    {
                        r.Skipped++;
                        continue;
                    }

                    try
                    {
                        if (Directory.Exists(dst) || File.Exists(dst))
                        {
                            r.Failed.Add(name);
                            r.Reasons.Add("新目录里已经有同名文件夹，未覆盖");
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        r.Failed.Add(name);
                        r.Reasons.Add(ex.Message);
                        continue;
                    }

                    todo.Add(name);
                }

                if (r.Failed.Count > 0) return r;   // 预检就不过，一个都不搬

                // ---- 2. 真搬 ----
                List<string> done = new List<string>();
                foreach (string name in todo)
                {
                    string src = Path.Combine(oldRoot, name);
                    string dst = Path.Combine(newRoot, name);
                    int files = CountFiles(src);

                    VerifiedFileMove.Result moved = VerifiedFileMove.Move(src, dst);
                    if (!moved.Ok)
                    {
                        r.Failed.Add(name);
                        r.Reasons.Add(moved.Message);
                        continue;
                    }

                    done.Add(name);
                    r.MovedDesks++;
                    r.MovedFiles += files;
                    try { Log.Info("桌面存放目录迁移: " + src + " → " + dst); }
                    catch { }
                }

                // ---- 3. 有失败 → 把搬成功的搬回去 ----
                if (r.Failed.Count > 0 && done.Count > 0)
                {
                    int rolledBack = 0;
                    foreach (string name in done)
                    {
                        string src = Path.Combine(oldRoot, name);
                        string dst = Path.Combine(newRoot, name);
                        try
                        {
                            if (!Directory.Exists(src) && Directory.Exists(dst)
                                && VerifiedFileMove.Move(dst, src).Ok)
                                rolledBack++;
                        }
                        catch { }
                    }

                    r.MovedDesks = 0;
                    r.MovedFiles = 0;
                    r.Reasons.Add("已回滚 " + rolledBack + " 个已搬的桌面，存放目录未改变");
                    try { Log.Warn("桌面存放目录迁移失败，已回滚 " + rolledBack + " 项"); }
                    catch { }
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

        /// <summary>
        /// 老的单遍实现（保留给测试做对照：验证「半搬」确实会发生）。
        /// 生产路径走 <see cref="Migrate"/>。
        /// </summary>
        public static Result MigrateOneByOne(string oldRoot, string newRoot, IEnumerable<string> deskNames)
        {
            Result r = new Result();
            try
            {
                if (string.IsNullOrEmpty(oldRoot) || string.IsNullOrEmpty(newRoot) || deskNames == null)
                    return r;

                foreach (string name in deskNames)
                {
                    if (!VirtualDesks.IsSafeName(name))
                    {
                        r.Failed.Add(name ?? "(空名)");
                        r.Reasons.Add("名称不合法，未搬");
                        continue;
                    }

                    string src, dst;
                    try
                    {
                        src = Path.Combine(oldRoot, name);
                        dst = Path.Combine(newRoot, name);
                    }
                    catch (Exception ex)
                    {
                        r.Failed.Add(name);
                        r.Reasons.Add(ex.Message);
                        continue;
                    }

                    if (!Directory.Exists(src))
                    {
                        r.Skipped++;
                        continue;
                    }

                    try
                    {
                        if (Directory.Exists(dst) || File.Exists(dst))
                        {
                            r.Failed.Add(name);
                            r.Reasons.Add("新目录里已经有同名文件夹，未覆盖");
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        r.Failed.Add(name);
                        r.Reasons.Add(ex.Message);
                        continue;
                    }

                    int files = CountFiles(src);

                    VerifiedFileMove.Result moved = VerifiedFileMove.Move(src, dst);
                    if (!moved.Ok)
                    {
                        r.Failed.Add(name);
                        r.Reasons.Add(moved.Message);
                        continue;
                    }

                    r.MovedDesks++;
                    r.MovedFiles += files;
                    try { Log.Info("桌面存放目录迁移: " + src + " → " + dst); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                r.Failed.Add("(整体)");
                r.Reasons.Add(ex.Message);
            }
            return r;
        }

        // ========== 路径工具 ==========

        /// <summary>规范化成完整路径并去掉尾部斜杠；失败返回空串。</summary>
        public static string SafeFull(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return "";
                string full = Path.GetFullPath(path.Trim().Trim('"'));
                return full.TrimEnd('\\', '/');
            }
            catch { return ""; }
        }

        /// <summary>
        /// child 是否在 parent **下面**（不含相等）。
        ///
        /// 必须按分隔符比前缀，否则 `C:\desks2` 会被误判成 `C:\desks` 的子目录。
        /// </summary>
        public static bool IsUnder(string child, string parent)
        {
            try
            {
                string c = SafeFull(child);
                string p = SafeFull(parent);
                if (c.Length == 0 || p.Length == 0) return false;
                if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase)) return false;

                return c.StartsWith(p + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
