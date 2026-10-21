using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 一键回滚：把程序在系统里造成的**所有**改动集中撤销，让机器回到
    /// “从没装过四叶草”的状态。参考 ExplorerPatcher 的安全卸载思路。
    ///
    /// 和 --restore-system 的区别：
    ///   --restore-system  只撤销**系统级状态**（任务栏可见性/透明样式），用于紧急恢复
    ///   --reset-all       全量回滚：系统状态 + 开机自启 + 停止进程 + 停止看门狗
    ///
    /// 每一步独立 try/catch，单步失败不影响后续步骤；最后输出一份逐项结果。
    /// </summary>
    static class ResetAll
    {
        /// <summary>单项回滚结果。</summary>
        class Step
        {
            public string Name;
            public bool Ok;
            public string Detail;
        }

        /// <summary>
        /// 执行全量回滚。
        /// keepConfig=true 时保留 config 下的用户数据（应用清单、使用记录、设置），
        /// 只撤销“对系统的影响”。
        /// </summary>
        public static int Run(string baseDir, bool keepConfig)
        {
            // 兜底：回滚过程绝不能被未处理异常打断，否则会留下半完成状态。
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try
                {
                    Console.Error.WriteLine("回滚时发生未处理异常: " + e.ExceptionObject);
                }
                catch { }
            };

            List<Step> steps = new List<Step>();

            Log.Init(baseDir);
            TaskbarVisibility.Init(baseDir);
            SystemState.Init(baseDir);

            Log.Info(BuildInfo.Describe + " 开始一键回滚（keepConfig=" + keepConfig + "）");

            // ---------- 1. 恢复系统任务栏（可见性 + EX 样式）----------
            steps.Add(Do("恢复系统任务栏", delegate
            {
                TaskbarVisibility.RestoreSystemState("一键回滚");
                bool visible = TaskbarVisibility.IsTrayVisible();
                if (!visible) throw new Exception("任务栏仍不可见");
            }));

            // ---------- 2. 清除系统状态标记 ----------
            steps.Add(Do("清除 system-state.json", delegate
            {
                SystemState.Clear(baseDir);
                if (File.Exists(SystemState.PathOf(baseDir)))
                    throw new Exception("文件未能删除");
            }));

            // ---------- 3. 恢复任务栏自动隐藏设置 ----------
            // 我们不主动改 ABM_SETSTATE，但某些系统状态下隐藏任务栏会连带影响，
            // 这里显式确保“不自动隐藏”以外的状态没被改坏。
            steps.Add(Do("校验任务栏自动隐藏状态", delegate
            {
                int state = Native.GetTaskbarState();
                // ABS_AUTOHIDE = 1；我们从不设置它，这里只记录，不改动
                if ((state & 1) != 0)
                    throw new Exception("任务栏处于自动隐藏状态（本程序不会设置此项，可能是用户原有的设置，未修改）");
            }));

            // ---------- 4. 移除开机自启快捷方式 ----------
            steps.Add(Do("移除开机自启", delegate
            {
                string lnk = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Startup), "四叶草启动器.lnk");
                if (!File.Exists(lnk)) return; // 本来就没有，算成功
                File.Delete(lnk);
                if (File.Exists(lnk)) throw new Exception("快捷方式未能删除");
            }));

            // ---------- 5. 停止看门狗进程 ----------
            // 看门狗是 --guard 模式，特征：进程名相同、但不持有窗口。
            // 关键：必须排除「当前进程」，否则会把自己杀掉（winexe 的 MainWindowHandle 也是 0）。
            steps.Add(Do("停止看门狗进程", delegate
            {
                int selfPid = Process.GetCurrentProcess().Id;
                Process[] all = null;
                try { all = Process.GetProcessesByName("CloverStart"); }
                catch { return; }
                if (all == null) return;

                foreach (Process p in all)
                {
                    if (p == null) continue;
                    try
                    {
                        if (p.Id == selfPid) continue;              // 绝不杀自己
                        if (p.MainWindowHandle != IntPtr.Zero) continue;  // 有窗口的是主进程
                        p.Kill();
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }));

            // ---------- 6. 清理崩溃守卫状态 ----------
            steps.Add(Do("清除 crash-guard.json", delegate
            {
                // 注意顺序：先 ClearSafeMode（它会重写文件），再删除文件，
                // 否则删完又被写回来。
                CrashGuard.ClearSafeMode();
                string guard = Path.Combine(baseDir, "config", "crash-guard.json");
                if (File.Exists(guard)) File.Delete(guard);
                if (File.Exists(guard)) throw new Exception("文件未能删除");
            }));

            // ---------- 7. 可选：清理配置与缓存 ----------
            if (!keepConfig)
            {
                steps.Add(Do("清理图标缓存", delegate
                {
                    string iconDir = Path.Combine(baseDir, "config", "iconcache");
                    if (!Directory.Exists(iconDir)) return;
                    DeleteDirectoryRobust(iconDir);
                    if (Directory.Exists(iconDir)) throw new Exception("目录未能删除");
                }));

                steps.Add(Do("清理桌面快捷方式备份", delegate
                {
                    string backup = Path.Combine(baseDir, "desktop-backup");
                    if (!Directory.Exists(backup)) return;
                    DeleteDirectoryRobust(backup);
                    if (Directory.Exists(backup)) throw new Exception("目录未能删除");
                }));
            }

            // ---------- 输出结果 ----------
            // 主程序是 winexe，从命令行运行时没有控制台；此时改用弹窗展示。
            StringBuilder text = new StringBuilder();
            text.AppendLine("四叶草一键回滚结果");
            text.AppendLine(new string('-', 50));

            int failed = 0;
            foreach (Step s in steps)
            {
                if (s.Ok)
                {
                    text.AppendLine("[OK]   " + s.Name);
                }
                else
                {
                    failed++;
                    text.AppendLine("[失败] " + s.Name + "  -> " + s.Detail);
                    Log.Warn("一键回滚失败项: " + s.Name + " -> " + s.Detail);
                }
            }

            text.AppendLine(new string('-', 50));
            if (failed == 0)
            {
                text.AppendLine("全部完成。系统已恢复到本程序介入前的状态。");
                Log.Info("一键回滚完成，无失败项");
            }
            else
            {
                text.AppendLine("有 " + failed + " 项未能完成，详见 config\\error.log。");
                Log.Warn("一键回滚完成，失败 " + failed + " 项");
            }

            if (!keepConfig)
            {
                text.AppendLine();
                text.AppendLine("注意：配置与缓存已清理，程序下次启动会重新扫描并重建。");
            }

            string report = text.ToString();

            // 主程序是 winexe：从命令行调用时通常拿不到可用控制台。
            // 这里统一走「写日志 + 尽力写控制台」，不弹窗阻塞（回滚应当在无人值守下也能完成）。
            Log.Info(report.Replace(Environment.NewLine, " | "));
            try { Console.WriteLine(report); } catch { }

            return failed;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        /// <summary>
        /// 尽力删除整个目录：先清只读属性，再逐项删，最后删目录本身。
        /// 直接 Directory.Delete(recursive) 在遇到只读/占用文件时会整批失败。
        /// </summary>
        static void DeleteDirectoryRobust(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;

                // 1. 清掉只读属性
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        FileAttributes attr = File.GetAttributes(f);
                        if ((attr & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
                    }
                    catch { }
                }

                // 2. 逐项删除文件
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch { }
                }

                // 3. 自底向上删子目录
                foreach (string d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
                {
                    try { Directory.Delete(d, false); } catch { }
                }

                // 4. 删根目录
                Directory.Delete(dir, true);
            }
            catch (Exception ex)
            {
                Log.Warn("删除目录失败 " + dir + ": " + ex.Message);
            }
        }

        /// <summary>跑一步，捕获异常并记录结果。</summary>
        static Step Do(string name, Action action)
        {
            Step s = new Step { Name = name, Ok = true };
            try
            {
                action();
                Log.Info("回滚步骤成功: " + name);
            }
            catch (Exception ex)
            {
                s.Ok = false;
                s.Detail = ex.Message;
                Log.Warn("回滚步骤失败: " + name + " -> " + ex.GetType().Name + ": " + ex.Message);
            }
            return s;
        }
    }
}
