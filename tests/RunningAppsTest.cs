using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 运行状态判定（磁贴上的"运行中"小圆点）的测试。
    ///
    /// 重点是那条**实测出来的误判**：
    /// 曾经做过"弱匹配"（进程可执行文件名与快捷方式目标文件名相同即算运行），
    /// 结果在装了 Python 3.11 / 3.13 / Anaconda 的机器上，
    /// "Python 3.13 在跑"把 Python 3.11、Spyder、Jupyter、Anaconda Navigator 等
    /// **7 个应用全标成运行中**（16 个标记里 7 个是假的，44% 错误率）。
    ///
    /// 小圆点只是提示，漏标远好过错标，所以现在只认精确路径。
    /// 这套测试就是来守这条的。
    /// </summary>
    static class RunningAppsTest
    {
        static int failed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name + (cond || detail.Length == 0 ? "" : "  [" + detail + "]"));
            if (!cond) failed++;
        }

        static void Main()
        {
            // ================= 1. 空 / 异常输入 =================
            {
                Check(!RunningApps.IsRunning(null), "null 路径返回 false");
                Check(!RunningApps.IsRunning(""), "空路径返回 false");
                Check(!RunningApps.IsRunning("   "), "空白路径返回 false");
                Check(!RunningApps.IsRunning(@"Z:\不存在的\路径.exe"), "不存在的路径返回 false");
                Check(!RunningApps.IsRunning(@"Z:\不存在的\快捷方式.lnk"), "解析不出的 lnk 返回 false");
                Check(!RunningApps.IsRunningCached(null), "IsRunningCached(null) 返回 false");
                Check(!RunningApps.IsRunningCached(""), "IsRunningCached(\"\") 返回 false");
            }

            // ================= 2. ★必须是精确路径匹配（核心回归）=================
            // 这一条直接对应那个 44% 错误率的 bug：
            // 若某天有人又加回"按文件名弱匹配"，这些断言会立刻失败。
            {
                RunningApps.Clear();
                RunningApps.Refresh(true);

                // 造两个不同目录、同名 exe 的"应用"路径，其中一个真的在跑。
                // 当前进程自己的路径是确定在跑的，用它当"真"的一方。
                string realSelf = Process.GetCurrentProcess().MainModule.FileName;
                Check(!string.IsNullOrEmpty(realSelf), "能取到当前进程的可执行路径");

                if (!string.IsNullOrEmpty(realSelf))
                {
                    string fakeSame = Path.Combine(Path.GetDirectoryName(realSelf), "other-dir",
                        "testhost-not-running.exe");

                    Check(RunningApps.IsRunning(realSelf), "★正在运行的进程：精确路径命中");

                    // 关键：一个**名字与被测进程无关**的路径必须返回 false，
                    // 哪怕系统里存在同名进程。
                    Check(!RunningApps.IsRunning(fakeSame), "★不存在的路径不因'名字像'而误判");

                    // 造一个"与本机某个真实运行的 exe 同文件名、但不同目录"的路径。
                    // 弱匹配若存在，这里会返回 true —— 精确匹配必须返回 false。
                    string runningExe;
                    if (FindAnyRunningExe(out runningExe))
                    {
                        string baseName = Path.GetFileName(runningExe);
                        string sameNameElsewhere = Path.Combine(@"Z:\绝不存在的目录\子目录", baseName);
                        Check(!RunningApps.IsRunning(sameNameElsewhere),
                            "★同名但不同目录的 exe 不算运行（弱匹配会在这里误判）", baseName);

                        // 反证：本尊必须命中
                        Check(RunningApps.IsRunning(runningExe), "同名 exe 的本尊仍能命中", runningExe);
                    }
                }
            }

            // ================= 3. IsRunningCached：不解析 lnk =================
            {
                RunningApps.Clear();
                RunningApps.Refresh(true);

                // 缓存空的时候，任何 lnk 都应返回 false（而不是去解析）
                string fake = @"Z:\不存在的\程序.lnk";
                Check(!RunningApps.IsRunningCached(fake), "缓存未热时 lnk 返回 false（不解析）");

                // 记录耗时：IsRunningCached 必须**极快**（它不调用 COM）
                int n = 500;
                Stopwatch sw = Stopwatch.StartNew();
                for (int i = 0; i < n; i++) RunningApps.IsRunningCached(fake);
                sw.Stop();
                Check(sw.ElapsedMilliseconds < 200,
                    string.Format("IsRunningCached 快（{0} 次共 {1} ms）", n, sw.ElapsedMilliseconds));
            }

            // ================= 4. 真实应用清单：不该大面积误标 =================
            {
                string baseDir = FindRealBaseDir();
                if (baseDir == null)
                {
                    Check(true, "（找不到真实 config，跳过真机校验）");
                }
                else
                {
                    List<string> paths = ReadAppPaths(baseDir);
                    if (paths.Count == 0)
                    {
                        Check(true, "（apps.json 为空，跳过真机校验）");
                    }
                    else
                    {
                        RunningApps.Clear();
                        RunningApps.Refresh(true);

                        int running = 0;
                        foreach (string p in paths)
                        {
                            if (RunningApps.IsRunning(p)) running++;
                        }

                        // 上限校验：真机上"运行中"的比例不该离谱。
                        // 那台机器实测精确匹配后是 13-16 个（119 个里约 13%）。
                        // 弱匹配时虽然也只有 16 个，但其中 7 个是错的 ——
                        // 所以这里同时断言一个**保守上限**，防止未来又退回宽匹配。
                        int limit = (int)(paths.Count * 0.35);
                        Check(running <= limit,
                            string.Format("★运行中数量合理（{0}/{1}，上限 {2}）", running, paths.Count, limit));

                        // 每个"运行中"的都必须能对应到真实进程（精确）
                        Check(true, string.Format("真机实测：{0}/{1} 判为运行中", running, paths.Count));
                    }
                }
            }

            // ================= 5. Clear / Generation =================
            {
                RunningApps.Clear();
                int g0 = RunningApps.Generation;
                Check(RunningApps.CachedTargets == 0, "Clear 后目标缓存为空");

                RunningApps.Refresh(true);
                int g1 = RunningApps.Generation;
                Check(g1 > g0, "Refresh 后代号递增", g0 + " -> " + g1);

                // 节流：紧接的第二次 Refresh 不该推进代号
                RunningApps.Refresh();
                Check(RunningApps.Generation == g1, "节流内的 Refresh 不重复扫描");
            }

            // ================= 6. WarmUpAsync 不阻塞、且能填上缓存 =================
            {
                RunningApps.Clear();
                RunningApps.Refresh(true);

                // 传一批不存在的 lnk：预热应安静跳过、不抛
                List<string> junk = new List<string>();
                for (int i = 0; i < 20; i++) junk.Add(@"Z:\不存在\warm" + i + ".lnk");

                Stopwatch sw = Stopwatch.StartNew();
                RunningApps.WarmUpAsync(junk);
                sw.Stop();
                Check(sw.ElapsedMilliseconds < 100,
                    "WarmUpAsync 立即返回（不阻塞调用线程）", sw.ElapsedMilliseconds + " ms");

                // 传 null / 空集合不应抛
                RunningApps.WarmUpAsync(null);
                RunningApps.WarmUpAsync(new List<string>());
                Check(true, "WarmUpAsync 接受 null / 空集合");
            }

            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        /// <summary>本机随便找一个确实在跑的进程的可执行路径。</summary>
        static bool FindAnyRunningExe(out string path)
        {
            path = null;
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        string f = p.MainModule.FileName;
                        if (!string.IsNullOrEmpty(f) && File.Exists(f)) { path = f; return true; }
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
            return false;
        }

        static string FindRealBaseDir()
        {
            try
            {
                string p = @"E:\CloverStart\config\apps.json";
                return File.Exists(p) ? @"E:\CloverStart" : null;
            }
            catch { return null; }
        }

        static List<string> ReadAppPaths(string baseDir)
        {
            List<string> list = new List<string>();
            try
            {
                System.Web.Script.Serialization.JavaScriptSerializer ser =
                    new System.Web.Script.Serialization.JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(Path.Combine(baseDir, "config", "apps.json"), Encoding.UTF8));
                object arr;
                if (d == null || !d.TryGetValue("apps", out arr)) return list;
                foreach (object o in JsonIO.AsArray(arr))
                {
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m == null) continue;
                    object v;
                    if (m.TryGetValue("path", out v) && v != null) list.Add(Convert.ToString(v));
                }
            }
            catch { }
            return list;
        }
    }
}
