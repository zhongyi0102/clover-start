using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 监听开始菜单与桌面：
    /// 1) 新软件安装（开始菜单出现 .lnk）-> 自动重新扫描并刷新面板
    /// 2) 桌面上出现/存在的快捷方式 -> 自动移到 desktop-backup 备份文件夹（可恢复），
    ///    并把快捷方式指向的程序登记到 manual-apps.json，保证仍能在面板里启动
    /// </summary>
    class AppWatcher
    {
        string baseDir;
        PanelWindow panel;
        Settings settings;
        List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        DispatcherTimer debounce;
        DispatcherTimer periodic;
        DispatcherTimer initial;
        bool startMenuDirty;
        bool desktopDirty;
        string backupDir;
        string logPath;

        /// <summary>desktop-backup 超过这个数量时只写警告，**不删除任何文件**。</summary>
        const int BackupWarnThreshold = 200;

        /// <summary>本次运行是否已经为"备份过多"写过警告（只写一次，避免刷屏）。</summary>
        bool backupWarned;

        /// <summary>是否自动把桌面快捷方式移到 desktop-backup。对应设置 desktopAutoClean。</summary>
        public bool AutoCleanEnabled
        {
            get { return settings == null || settings.DesktopAutoClean; }
            set
            {
                if (settings == null) return;
                settings.DesktopAutoClean = value;
                settings.Save();
                Ui.Log("桌面快捷方式自动移走 -> " + value);
            }
        }

        public AppWatcher(string baseDir, PanelWindow panel, Settings settings)
        {
            this.baseDir = baseDir;
            this.panel = panel;
            this.settings = settings ?? Settings.Load(baseDir);
            this.backupDir = Path.Combine(baseDir, "desktop-backup");
            this.logPath = Path.Combine(backupDir, "cleanup.log");
        }

        public void Start()
        {
            debounce = new DispatcherTimer();
            debounce.Interval = TimeSpan.FromMilliseconds(2500);
            debounce.Tick += OnDebounce;

            periodic = new DispatcherTimer();
            periodic.Interval = TimeSpan.FromMinutes(15);
            periodic.Tick += delegate { RefreshCatalog(); };
            periodic.Start();

            string[] startMenus = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs")
            };
            foreach (string d in startMenus)
                Watch(d, true, true);

            string[] desktops = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };
            foreach (string d in desktops)
                Watch(d, false, false);

            // 启动后 2 秒做一次初始桌面清理（功能关闭时跳过）
            initial = new DispatcherTimer();
            initial.Interval = TimeSpan.FromSeconds(2);
            initial.Tick += delegate
            {
                initial.Stop();
                if (!AutoCleanEnabled)
                {
                    Ui.Log("桌面快捷方式自动移走已关闭，跳过初始清理");
                    return;
                }
                CleanDesktopNow(false);
            };
            initial.Start();
        }

        void Watch(string dir, bool recursive, bool isStartMenu)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                FileSystemWatcher w = new FileSystemWatcher();
                w.Path = dir;
                w.IncludeSubdirectories = recursive;
                w.Filter = "*.lnk";
                w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime;
                w.InternalBufferSize = 64 * 1024;
                w.Created += delegate { OnChange(isStartMenu); };
                w.Changed += delegate { OnChange(isStartMenu); };
                w.Renamed += delegate { OnChange(isStartMenu); };
                w.Deleted += delegate { OnChange(isStartMenu); };
                // 缓冲区溢出（大量文件变化）时事件会丢失，必须主动兜底做一次全量扫描
                w.Error += delegate(object s, ErrorEventArgs e)
                {
                    Ui.Log("文件监听缓冲区溢出/出错，改为全量扫描: " + (e.GetException() == null ? "未知" : e.GetException().Message));
                    OnChange(isStartMenu);
                };
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("监听目录失败: " + dir + " -> " + ex.Message);
            }
        }

        /// <summary>停止并释放所有文件监听器（退出时调用）。</summary>
        public void Stop()
        {
            try { if (debounce != null) debounce.Stop(); } catch { }
            try { if (periodic != null) periodic.Stop(); } catch { }
            try { if (initial != null) initial.Stop(); } catch { }

            foreach (FileSystemWatcher w in watchers)
            {
                try
                {
                    w.EnableRaisingEvents = false;
                    w.Dispose();
                }
                catch { }
            }
            watchers.Clear();
        }

        void OnChange(bool isStartMenu)
        {
            panel.Dispatcher.BeginInvoke((Action)delegate
            {
                if (isStartMenu) startMenuDirty = true;
                else desktopDirty = true;
                debounce.Stop();
                debounce.Start();
            });
        }

        void OnDebounce(object sender, EventArgs e)
        {
            debounce.Stop();
            if (startMenuDirty)
            {
                startMenuDirty = false;
                RunScan("开始菜单变化");
            }
            if (desktopDirty)
            {
                desktopDirty = false;
                if (AutoCleanEnabled) CleanDesktopNow(false);
                else Ui.Log("桌面快捷方式自动移走已关闭，忽略本次变化");
            }
        }

        void RefreshCatalog()
        {
            RunScan("定时兜底");
        }

        /// <summary>
        /// 扫描放到后台线程执行，完成后回到界面线程刷新面板。
        /// 以前这里直接调 AppScan.Run，便携软件目录递归时会长时间占住界面线程，面板卡死。
        /// </summary>
        void RunScan(string reason)
        {
            try
            {
                ScanService.RunAsync(baseDir, reason, delegate(bool ok)
                {
                    if (!ok) return;
                    try
                    {
                        panel.Dispatcher.BeginInvoke(new Action(delegate
                        {
                            try { panel.RefreshCatalogQuiet(); }
                            catch (Exception ex) { Ui.LogError(ex, "刷新面板(" + reason + ")"); }
                        }));
                    }
                    catch (Exception ex)
                    {
                        Ui.LogError(ex, "回到界面线程刷新面板");
                    }
                });
            }
            catch (Exception ex)
            {
                ErrorLog.Hook(ex, "启动后台扫描");
            }
        }

        /// <summary>
        /// 把 desktop-backup 里的 .lnk 放回桌面。返回成功还原的数量。
        ///
        /// 规则：
        ///   - 只处理 .lnk，其他类型不动
        ///   - 桌面已有同名文件时加后缀（还原-1、还原-2…），**绝不覆盖**
        ///   - 还原成功的从 backup 里移除；失败的只写 cleanup.log，不重试
        /// </summary>
        public int RestoreDesktopNow()
        {
            try
            {
                return RestoreDesktopCore();
            }
            catch (Exception ex)
            {
                ErrorLog.Hook(ex, "还原桌面快捷方式");
                return 0;
            }
        }

        int RestoreDesktopCore()
        {
            int restored = 0;
            int failed = 0;

            if (!Directory.Exists(backupDir)) return 0;

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
            {
                Log("还原失败：找不到桌面目录");
                return 0;
            }

            // 核心搬进静态方法，便于单元测试（不依赖 PanelWindow/WPF）
            int[] r = RestoreLinks(backupDir, desktop, delegate(string msg) { Log(msg); });
            restored = r[0];
            failed = r[1];

            Log("还原完成：成功 " + restored + " 个，失败 " + failed + " 个。");
            if (restored > 0)
            {
                try { panel.RefreshCatalogQuiet(); }
                catch (Exception ex) { Ui.LogError(ex, "刷新面板（还原桌面）"); }
            }
            return restored;
        }

        /// <summary>
        /// 把 backupDir 里的 .lnk 还原到 destDir（纯文件操作，可单独测试）。
        ///
        /// 规则：
        ///   - 只处理 .lnk，其他类型不动
        ///   - 目标目录已有同名文件时加「-还原N」后缀，**绝不覆盖**
        ///   - 成功的从 backupDir 移除；失败的保留在原地，只回调日志，**不重试**
        ///
        /// 返回 { 成功数, 失败数 }。
        /// </summary>
        public static int[] RestoreLinks(string backupDir, string destDir, Action<string> log)
        {
            int restored = 0;
            int failed = 0;
            if (log == null) log = delegate { };

            try
            {
                if (!Directory.Exists(backupDir)) return new int[] { 0, 0 };
                if (string.IsNullOrEmpty(destDir) || !Directory.Exists(destDir)) return new int[] { 0, 0 };

                string[] files;
                try { files = Directory.GetFiles(backupDir, "*.lnk"); }
                catch (Exception ex)
                {
                    log("还原失败：无法枚举 backup 目录 -> " + ex.Message);
                    return new int[] { 0, 0 };
                }

                foreach (string src in files)
                {
                    string name = Path.GetFileName(src);
                    try
                    {
                        string dest = MakeUniqueDest(destDir, name);
                        File.Move(src, dest);
                        restored++;
                        log("已还原到桌面: " + name + " -> " + Path.GetFileName(dest));
                    }
                    catch (Exception ex)
                    {
                        // 失败只记日志，不重试（避免死循环），文件留在 backup 里
                        failed++;
                        log("还原失败（跳过，不重试）: " + name + " -> " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                log("还原过程异常: " + ex.Message);
            }
            return new int[] { restored, failed };
        }

        /// <summary>
        /// 在目标目录里生成不冲突的文件名：重名时插入「还原-N」后缀，绝不覆盖。
        /// </summary>
        public static string MakeUniqueDest(string dir, string fileName)
        {
            string dest = Path.Combine(dir, fileName);
            if (!File.Exists(dest)) return dest;

            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);

            for (int i = 1; i < 10000; i++)
            {
                string candidate = Path.Combine(dir, baseName + "-还原" + i + ext);
                if (!File.Exists(candidate)) return candidate;
            }
            // 极端情况：加时间戳兜底
            return Path.Combine(dir, baseName + "-还原" + DateTime.Now.ToString("yyyyMMddHHmmss") + ext);
        }

        /// <summary>把桌面上的 .lnk 全部移到备份目录；返回移动数量。</summary>
        public int CleanDesktopNow(bool explicitRun)
        {
            try
            {
                return CleanDesktopCore(explicitRun);
            }
            catch (Exception ex)
            {
                ErrorLog.Hook(ex, "清理桌面快捷方式");
                return 0;
            }
        }

        int CleanDesktopCore(bool explicitRun)
        {
            int movedCount = 0;
            try { Directory.CreateDirectory(backupDir); }
            catch { return 0; }

            List<AppItem> manual = Catalog.LoadManual(baseDir);
            int added = 0;

            string[] desktops = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };

            foreach (string d in desktops)
            {
                if (!Directory.Exists(d)) continue;
                string[] files;
                try { files = Directory.GetFiles(d, "*.lnk"); }
                catch { continue; }

                foreach (string f in files)
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (name.EndsWith(" - 快捷方式")) name = name.Substring(0, name.Length - " - 快捷方式".Length).Trim();
                    string target = ReadLnkTarget(f);
                    string args = ReadLnkArgs(f);
                    bool known = (target != null && panel.CatalogHasPath(target)) || ManualContains(manual, target, name);

                    string moved = MoveToBackup(f);
                    if (moved == null) continue;

                    movedCount++;
                    Log("已移走桌面快捷方式: " + Path.GetFileName(f) + " -> " + moved);

                    bool addable = !known
                        && string.IsNullOrEmpty(args)
                        && target != null
                        && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(target)
                        && target.IndexOf("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) < 0
                        && !target.EndsWith("\\cmd.exe", StringComparison.OrdinalIgnoreCase)
                        && !target.EndsWith("\\wscript.exe", StringComparison.OrdinalIgnoreCase)
                        && !target.EndsWith("\\cscript.exe", StringComparison.OrdinalIgnoreCase);

                    if (addable && !panel.CatalogHasName(name))
                    {
                        AppItem a = new AppItem();
                        a.Name = name;
                        a.Path = target;
                        a.Category = Catalog.GuessCategory(name);
                        manual.Add(a);
                        added++;
                        Log("已加入面板（来自桌面快捷方式）: " + name + " [" + a.Category + "]");
                    }
                }
            }

            if (added > 0)
                Catalog.SaveManual(baseDir, manual);
            if (movedCount > 0)
            {
                try { panel.RefreshCatalogQuiet(); }
                catch (Exception ex) { Ui.LogError(ex, "刷新面板（桌面清理）"); }
            }

            if (explicitRun)
                Log("手动清理完成，移动 " + movedCount + " 个快捷方式，新增 " + added + " 个应用到面板。");
            return movedCount;
        }

        string MoveToBackup(string file)
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string name = Path.GetFileName(file);
                string dest = Path.Combine(backupDir, stamp + "_" + name);
                int i = 1;
                // 重名时加时间后缀，绝不覆盖已有备份
                while (File.Exists(dest))
                {
                    dest = Path.Combine(backupDir, stamp + "-" + i + "_" + name);
                    i++;
                }
                File.Move(file, dest);
                CheckBackupSize();
                return Path.GetFileName(dest);
            }
            catch (Exception ex)
            {
                // 权限不足 / 文件被占用：只记日志，不重试（避免死循环）
                Log("移动失败（跳过，不重试）: " + Path.GetFileName(file) + " -> " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// desktop-backup 数量检查：**只警告，不删除**。
        /// 备份是用户的还原凭据，程序无权静默清理。同一次运行只写一条 WARN。
        /// </summary>
        void CheckBackupSize()
        {
            try
            {
                if (backupWarned) return;
                if (!Directory.Exists(backupDir)) return;

                int count = Directory.GetFiles(backupDir, "*.lnk").Length;
                if (count <= BackupWarnThreshold) return;

                backupWarned = true;
                ErrorLog.Warn("desktop-backup 已有 " + count + " 个快捷方式（超过 "
                    + BackupWarnThreshold + " 个），不会自动删除。"
                    + "需要清理请手动处理，或用右键菜单「还原桌面快捷方式」放回桌面。");
                Log("desktop-backup 数量 " + count + " 超过阈值 " + BackupWarnThreshold + "，仅警告不删除");
            }
            catch { }
        }

        static bool ManualContains(List<AppItem> list, string target, string name)
        {
            foreach (AppItem a in list)
            {
                if (target != null && string.Equals(a.Path, target, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static string ReadLnkTarget(string lnk)
        {
            try
            {
                object sc = CreateShortcut(lnk);
                if (sc == null) return null;
                object tp = sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null);
                string s = tp as string;
                if (string.IsNullOrEmpty(s)) return null;
                return s;
            }
            catch { return null; }
        }

        static string ReadLnkArgs(string lnk)
        {
            try
            {
                object sc = CreateShortcut(lnk);
                if (sc == null) return "";
                object a = sc.GetType().InvokeMember("Arguments", BindingFlags.GetProperty, null, sc, null);
                string s = a as string;
                return s == null ? "" : s;
            }
            catch { return ""; }
        }

        static object CreateShortcut(string lnk)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                object shell = Activator.CreateInstance(shellType);
                return shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            }
            catch { return null; }
        }

        void Log(string line)
        {
            try
            {
                Directory.CreateDirectory(backupDir);
                File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine, new System.Text.UTF8Encoding(false));
            }
            catch { }
        }
    }
}
