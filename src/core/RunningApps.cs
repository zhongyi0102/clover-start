using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 判断"这个应用现在是不是开着"，供磁贴上的小圆点用。
    ///
    /// 做法：拿系统里正在跑的进程的可执行文件路径，与应用的路径比对。
    /// 难点在于 apps.json 里 **112/119 是 .lnk**（开始菜单快捷方式），
    /// 不是 exe —— 必须解析出快捷方式的真实目标才能比。
    ///
    /// 性能约束（重要）：
    ///   · 解析 .lnk 走的是 WScript.Shell COM，**单次约 1-3 ms**，
    ///     119 个全解析要 100+ ms，绝不能放在每次渲染里
    ///   · 枚举进程本身也要几十毫秒
    ///   所以：**两边都缓存** —— 目标路径按 lnk 路径缓存（快捷方式极少变），
    ///   进程集合按时间戳节流（默认 1.5 秒内复用上次结果）。
    ///
    /// 线程约定：只应从界面线程调用。内部有节流，实际开销每 1.5 秒最多一次。
    /// </summary>
    static class RunningApps
    {
        /// <summary>
        /// 进程集合的缓存有效期（毫秒）。
        ///
        /// 为什么是 3 秒而不是跟窗口列表一样 1.5 秒：
        ///   枚举 300+ 进程会为每个进程取 MainModule（实测 47-75 ms），
        ///   面板可见期间每 1.5 秒做一次实测是 469 ms / 12 秒（0.24% 单核）。
        ///   而"应用是不是开着"这个信息**不需要秒级精度** ——
        ///   用户不会盯着磁贴看 Chrome 什么时候启动的；
        ///   窗口列表才需要跟手（那个另有 1.5 秒的定时器，互不影响）。
        ///   拉到 3 秒能把这份开销直接减半，观感上察觉不到。
        /// </summary>
        const int RefreshMs = 3000;

        /// <summary>lnk 路径 -> 真实目标路径（null 表示解析失败）。</summary>
        static readonly Dictionary<string, string> targetCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>目标路径（小写）-> 是否在运行。</summary>
        static readonly Dictionary<string, bool> pathRunning =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        static DateTime lastScan = DateTime.MinValue;
        static int scanGeneration;

        /// <summary>
        /// 刷新进程集合（内部节流，1.5 秒内的重复调用直接返回）。
        ///
        /// 用"枚举所有进程的 MainModule 路径"而不是按窗口枚举：
        /// 后者会漏掉后台驻留但没窗口的程序（如 Docker 托盘、输入法），
        /// 而那些恰恰是用户会问"它开着吗"的。
        /// </summary>
        public static void Refresh(bool force = false)
        {
            try
            {
                if (!force && (DateTime.Now - lastScan).TotalMilliseconds < RefreshMs) return;
                lastScan = DateTime.Now;

                HashSet<string> running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                Process[] all;
                try { all = Process.GetProcesses(); }
                catch { return; }

                foreach (Process p in all)
                {
                    if (p == null) continue;
                    try
                    {
                        // MainModule 在跨位数/受保护进程上会抛异常（正常现象，跳过即可）
                        string f = p.MainModule.FileName;
                        if (!string.IsNullOrEmpty(f)) running.Add(f);
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }

                scanGeneration++;
                pathRunning.Clear();

                // 只对"系统里确实在跑的 exe"建索引，后面查应用时直接命中。
                // 这里刻意**不**额外加"按文件名"的弱索引 —— 实测它会误判
                // （见 IsRunning 的说明：7 个 Python 系应用互相冒认）。
                foreach (string f in running)
                {
                    pathRunning[f] = true;
                }
            }
            catch { }
        }

        /// <summary>
        /// 这个应用现在是否在运行。
        ///
        /// path 可以是 .exe（直接比）或 .lnk（先解析目标再比）。
        /// 只做**精确路径**匹配。
        ///
        /// ⚠ 曾经做过"弱匹配"（进程可执行文件名与目标文件名相同即算运行），
        /// 实测证明它**错得厉害**：那台机器上同时装了 Python 3.11 / 3.13 / Anaconda，
        /// 它们的可执行文件都叫 python.exe / pythonw.exe，
        /// 于是"Python 3.13 在跑"会把 Python 3.11、Spyder、Jupyter、Anaconda Navigator
        /// 等 **7 个应用全标成运行中**（16 个标记里 7 个是假的，44% 错误率）。
        ///
        /// 小圆点只是个提示，**漏标（少一个点）远好过错标（点了一个没开的程序）**，
        /// 所以这里只认精确路径。
        /// </summary>
        public static bool IsRunning(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return false;

                string target = path;
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    target = ResolveTarget(path);
                    if (string.IsNullOrEmpty(target)) return false;
                }

                bool hit;
                return pathRunning.TryGetValue(target, out hit) && hit;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>解析 .lnk 的目标（带缓存）。</summary>
        static string ResolveTarget(string lnk)
        {
            string cached;
            if (targetCache.TryGetValue(lnk, out cached)) return cached;

            string target = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    object shell = Activator.CreateInstance(shellType);
                    object sc = shellType.InvokeMember("CreateShortcut",
                        System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                    if (sc != null)
                    {
                        object tp = sc.GetType().InvokeMember("TargetPath",
                            System.Reflection.BindingFlags.GetProperty, null, sc, null);
                        string s = tp as string;
                        if (!string.IsNullOrEmpty(s)) target = s;
                    }
                }
            }
            catch { }

            targetCache[lnk] = target;
            return target;
        }

        /// <summary>
        /// 只查缓存，**绝不解析** .lnk（界面线程专用）。
        ///
        /// 为什么要单独一个入口：首次解析 112 个快捷方式要 **239 ms**（COM 调用）。
        /// 建磁贴时逐个 IsRunning 就会把这个代价压在界面线程上，面板明显卡一下。
        /// 所以建磁贴时用这个"只看缓存"的版本（缓存没热就先不画点），
        /// 真正的解析交给 WarmUpAsync 在后台做，做完由定时器补上圆点。
        ///
        /// 这与项目里 UIAutomation 那条约定是同一个道理：
        /// **界面线程不做可能长时间阻塞的系统调用**。
        /// </summary>
        public static bool IsRunningCached(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return false;

                string target = path;
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string resolved;
                    // 没解析过就直接说"不知道"（返回 false），不在这里解析
                    if (!targetCache.TryGetValue(path, out resolved) || string.IsNullOrEmpty(resolved))
                        return false;
                    target = resolved;
                }

                bool hit;
                return pathRunning.TryGetValue(target, out hit) && hit;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>已缓存的目标路径数（诊断用）。</summary>
        public static int CachedTargets
        {
            get { return targetCache.Count; }
        }

        /// <summary>
        /// 后台预热：把一批应用路径的 .lnk 目标提前解析并缓存。
        ///
        /// 为什么需要：首次解析 112 个快捷方式实测 **239 ms**（每个 1-3 ms 的 COM 调用）。
        /// 如果这代价落在"用户打开面板"那一刻，会明显卡一下。
        /// 放到后台线程跑，等用户真的打开面板时缓存已经是热的（实测后续 0 ms）。
        ///
        /// 必须在**后台线程**调用（内部起线程），不要在界面线程直接跑。
        /// </summary>
        public static void WarmUpAsync(IEnumerable<string> paths)
        {
            try
            {
                if (paths == null) return;

                // 先拷一份清单：在后台线程里遍历调用方的集合，
                // 而那个集合（catalog.Apps）可能在界面线程被替换掉。
                List<string> copy = new List<string>();
                foreach (string p in paths)
                {
                    if (!string.IsNullOrEmpty(p)) copy.Add(p);
                }
                if (copy.Count == 0) return;

                System.Threading.ThreadPool.QueueUserWorkItem(delegate(object state)
                {
                    try
                    {
                        foreach (string p in copy)
                        {
                            if (p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                            {
                                // ResolveTarget 内部有缓存，重复调用无副作用
                                ResolveTarget(p);
                            }
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        /// <summary>清空缓存（重新扫描应用后调用，避免旧路径残留）。</summary>
        public static void Clear()
        {
            try
            {
                targetCache.Clear();
                pathRunning.Clear();
                lastScan = DateTime.MinValue;
            }
            catch { }
        }

        /// <summary>最近一次扫描的代号，供调用方判断"这次是不是新数据"。</summary>
        public static int Generation
        {
            get { return scanGeneration; }
        }
    }
}
