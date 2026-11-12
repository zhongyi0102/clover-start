using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Clover
{
    /// <summary>同一套桌面策略：文档移动，已确认的应用快捷方式回收，未知保留。</summary>
    sealed class DesktopWorkspace : IDisposable
    {
        readonly Settings settings;
        readonly Func<string> destination;
        readonly Func<string, bool> knownApplication;
        readonly Func<string, bool> recycleShortcut;
        readonly List<string> directories;
        readonly string automaticDesktop;
        Timer timer;
        int busy;
        int checking;
        volatile bool stopped;
        readonly Dictionary<string, string> stable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Action<Result> OnCompleted;

        public sealed class Result
        {
            public DeskCollector.MoveResult Files = new DeskCollector.MoveResult();
            public List<string> Recycled = new List<string>();
            public List<string> Retained = new List<string>();
            public List<string> Reasons = new List<string>();
        }

        public DesktopWorkspace(Settings settings, Func<string> destination, Func<string, bool> knownApplication,
            Func<string, bool> recycleShortcut, List<string> directories = null, string automaticDesktop = null)
        {
            this.settings = settings;
            this.destination = destination;
            this.knownApplication = knownApplication;
            this.recycleShortcut = recycleShortcut;
            this.directories = directories ?? DeskCollector.DesktopDirs();
            this.automaticDesktop = automaticDesktop ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        public DeskCollector.ScanResult Preview(bool automatic = false)
        {
            List<string> dirs = directories;
            if (automatic)
            {
                dirs = directories.FindAll(d => string.Equals(d, automaticDesktop, StringComparison.OrdinalIgnoreCase));
            }
            DeskCollector.ScanResult scan = DeskCollector.Scan(dirs, true);
            scan.Items.RemoveAll(c =>
            {
                bool keep = IsKept(settings, c.Path) || (c.ApplicationShortcut
                    && (DesktopKeep.IsKept(settings.DesktopKeepTargets, c.Path)
                        || knownApplication == null || !knownApplication(c.Path)));
                if (keep) scan.Skipped.Add(Path.GetFileName(c.Path));
                return keep;
            });
            return scan;
        }

        public Result Execute(List<string> paths, string dest, bool automatic = false)
        {
            Result result = new Result();
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            {
                result.Retained.Add("(本次操作)"); result.Reasons.Add("桌面正在处理中，请稍后重试"); return result;
            }
            try
            {
                foreach (string path in paths ?? new List<string>())
                {
                    if (automatic && (stopped || !settings.DesktopKeepEmptyEnabled || Settings.SafeModeActive)) break;
                    try
                    {
                        if (!IsDesktopChild(path) || IsKept(settings, path) || DesktopContentRules.IsTemporary(path))
                        { Retain(result, path, "例外、临时项目或已不在桌面，保留原处"); continue; }
                        DesktopContentKind kind = DesktopContentRules.Classify(path);
                        if (kind == DesktopContentKind.Application)
                        {
                            if (!(path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                                || DesktopKeep.IsKept(settings.DesktopKeepTargets, path)
                                || knownApplication == null || !knownApplication(path) || recycleShortcut == null || !recycleShortcut(path))
                                Retain(result, path, "应用入口未确认、已设例外或回收失败，保留原处");
                            else result.Recycled.Add(path);
                        }
                        else if (kind == DesktopContentKind.NonApplication)
                        {
                            DeskCollector.MoveResult moved;
                            if (Directory.Exists(path)) moved = DeskCollector.Collect(new List<string> { path }, dest);
                            else
                            {
                                // 移动全过程禁止写入；允许核对复制读取和本次移动删除源。
                                using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                                    moved = DeskCollector.Collect(new List<string> { path }, dest);
                            }
                            result.Files.Moved.AddRange(moved.Moved);
                            result.Files.Failed.AddRange(moved.Failed);
                            result.Files.Reasons.AddRange(moved.Reasons);
                            Record(moved, "收纳");
                        }
                        else Retain(result, path, "无法确认入口身份，保留原处");
                    }
                    catch (Exception ex) { Retain(result, path, "被占用或处理失败：" + ex.Message); }
                }
            }
            finally { Interlocked.Exchange(ref busy, 0); }
            return result;
        }

        bool IsDesktopChild(string path)
        {
            try
            {
                string parent = Path.GetDirectoryName(Path.GetFullPath(path));
                return directories.Exists(d => string.Equals(Path.GetFullPath(d).TrimEnd('\\'), parent, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        static void Retain(Result r, string path, string reason)
        { r.Retained.Add(Path.GetFileName(path)); r.Reasons.Add(reason); }

        public void Record(DeskCollector.MoveResult result, string action)
        {
            if (result == null || !result.AnyMoved) return;
            lock (settings)
            {
                foreach (DeskCollector.MoveEntry entry in result.Moved)
                    settings.DesktopMoves.Add(new Dictionary<string, object> {
                        { "from", entry.From }, { "to", entry.To }, { "time", entry.Time }, { "action", action }
                    });
                while (settings.DesktopMoves.Count > 200) settings.DesktopMoves.RemoveAt(0);
                settings.Save();
            }
        }

        public static bool IsKept(Settings settings, string path)
        {
            if (settings == null) return false;
            lock (settings) return settings.DesktopKeepPaths.Exists(p =>
                string.Equals(DesktopKeep.NormalizeKey(p), DesktopKeep.NormalizeKey(path), StringComparison.OrdinalIgnoreCase));
        }

        public static void Keep(Settings settings, string path)
        {
            if (settings == null || string.IsNullOrWhiteSpace(path)) return;
            lock (settings)
            {
                if (!IsKept(settings, path)) settings.DesktopKeepPaths.Add(Path.GetFullPath(path));
                settings.Save();
            }
        }

        public DeskCollector.MoveResult PutBack(List<string> paths, string desktop)
        {
            DeskCollector.MoveResult result = new DeskCollector.MoveResult();
            foreach (string path in paths)
            {
                string target = DeskCollector.MakeUniqueTarget(desktop, Path.GetFileName(path), Directory.Exists(path));
                Keep(settings, target); // 必须先于文件发布，避免 Created 被重新收走。
                VerifiedFileMove.Result moved = VerifiedFileMove.Move(path, target);
                if (moved.Ok) result.Moved.Add(new DeskCollector.MoveEntry {
                    From = path, To = moved.Target, Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
                else { result.Failed.Add(Path.GetFileName(path)); result.Reasons.Add(moved.Message); }
            }
            Record(result, "放回");
            return result;
        }

        public void Start()
        {
            Stop();
            lock (stable) stable.Clear();
            stopped = false;
            timer = new Timer(delegate { AutoTick(); }, null, 3000, 3000);
        }

        void AutoTick()
        {
            if (stopped || !settings.DesktopKeepEmptyEnabled || Settings.SafeModeActive || Interlocked.CompareExchange(ref checking, 1, 0) != 0) return;
            try
            {
                List<string> ready = new List<string>();
                lock (stable) foreach (DeskCollector.Candidate candidate in Preview(true).Items)
                {
                    // 目录无法用单个时间戳判断内部是否写完，自动处理留给手动确认。
                    if (Directory.Exists(candidate.Path)) continue;
                    FileInfo file = new FileInfo(candidate.Path);
                    string stamp = file.Length + ":" + file.LastWriteTimeUtc.Ticks;
                    string previous;
                    if (stable.TryGetValue(candidate.Path, out previous) && previous == stamp
                        && DateTime.UtcNow - file.LastWriteTimeUtc >= TimeSpan.FromSeconds(3)) ready.Add(candidate.Path);
                    stable[candidate.Path] = stamp;
                }
                if (ready.Count == 0) return;
                Result result = Execute(ready, destination(), true);
                if (OnCompleted != null && (result.Files.AnyMoved || result.Recycled.Count > 0)) OnCompleted(result);
            }
            catch (Exception ex) { Log.Warn("自动保持桌面空：" + ex.Message); }
            finally { Interlocked.Exchange(ref checking, 0); }
        }

        public void Stop() { stopped = true; if (timer != null) { timer.Dispose(); timer = null; } }
        public void Dispose() { Stop(); }
    }
}
