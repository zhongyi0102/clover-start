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
        /// <summary>
        /// readonly：构造函数已保证非 null（settings ?? Settings.Load(baseDir)），
        /// 编译期锁住这一点，就不必在每个用法前写 null 检查。
        /// </summary>
        readonly Settings settings;
        List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        DispatcherTimer debounce;
        DispatcherTimer periodic;
        bool startMenuDirty;

        /// <summary>桌面保持无快捷方式。懒创建（开关打开时才需要）。</summary>
        DesktopCleaner desktopCleaner;

        /// <summary>桌面清理器（可能为 null）。</summary>
        public DesktopCleaner DesktopCleaner
        {
            get { return desktopCleaner; }
        }

        public AppWatcher(string baseDir, PanelWindow panel, Settings settings)
        {
            this.baseDir = baseDir;
            this.panel = panel;
            this.settings = settings ?? Settings.Load(baseDir);
        }

        public void Start()
        {
            debounce = new DispatcherTimer();
            debounce.Interval = TimeSpan.FromMilliseconds(2500);
            debounce.Tick += OnDebounce;

            periodic = new DispatcherTimer();
            periodic.Interval = TimeSpan.FromMinutes(15);
            periodic.Tick += delegate
            {
                // 关停期间不再触发扫描：它会往界面线程排一个刷新委托，
                // 而那时面板可能已在销毁中
                if (ErrorLog.ShuttingDown) return;
                RefreshCatalog();
            };
            periodic.Start();

            // ---------- 首次运行检测 ----------
            // apps.json 不存在或没有应用时，立即触发一次后台扫描。
            // 不做这一步，新用户首次运行会看到空面板，要等 15 分钟兜底才有应用。
            // 后台线程跑，不阻塞面板显示；扫完自动刷新。
            if (NeedsInitialScan())
            {
                Ui.Log("首次运行（apps.json 缺失或为空），立即触发后台扫描");
                RunScan("首次运行");
            }

            string[] startMenus = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs")
            };
            foreach (string d in startMenus)
                Watch(d, true, true);

            // ---------- 桌面保持无快捷方式 ----------
            StartDesktopCleanIfEnabled();
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

        /// <summary>
        /// 启动时按开关状态挂桌面监听 + 补扫。
        ///
        /// 补扫范围严格限定（见 DesktopCleaner.SweepOnce）：
        /// 只处理创建时间晚于 max(desktopEnabledAt, desktopLastScan) 的，
        /// 不会把用户早就放好的快捷方式清掉。
        /// </summary>
        void StartDesktopCleanIfEnabled()
        {
            try
            {
                if (settings == null || !settings.DesktopCleanEnabled) return;

                if (desktopCleaner == null)
                {
                    desktopCleaner = new DesktopCleaner(baseDir, settings);

                    // 移除后弹轻提示（面板负责显示「保留 / 撤销」）
                    desktopCleaner.OnRemoved = delegate(List<Dictionary<string, object>> removed)
                    {
                        try
                        {
                            if (panel != null) panel.ShowDesktopRemovedTip(removed);
                        }
                        catch (Exception ex) { Ui.LogError(ex, "桌面移除提示"); }
                    };
                }

                desktopCleaner.Start();

                // 后台补扫，不阻塞启动
                System.Threading.Thread t = new System.Threading.Thread(delegate()
                {
                    try { desktopCleaner.SweepOnce("启动补扫"); }
                    catch (Exception ex) { Ui.LogError(ex, "桌面补扫"); }
                });
                t.IsBackground = true;
                t.Start();
            }
            catch (Exception ex) { Ui.LogError(ex, "启动桌面清理"); }
        }

        /// <summary>
        /// 开关切换。enabled=true 时记录开启时刻并按子选项清理现有；
        /// false 时立即停止监听、释放 watcher。
        /// </summary>
        public void SetDesktopClean(bool enabled)
        {
            try
            {
                if (desktopCleaner == null)
                {
                    desktopCleaner = new DesktopCleaner(baseDir, settings);

                    // 移除后弹轻提示（面板负责显示「保留 / 撤销」）
                    desktopCleaner.OnRemoved = delegate(List<Dictionary<string, object>> removed)
                    {
                        try
                        {
                            if (panel != null) panel.ShowDesktopRemovedTip(removed);
                        }
                        catch (Exception ex) { Ui.LogError(ex, "桌面移除提示"); }
                    };
                }

                if (enabled) desktopCleaner.OnEnabled();
                else desktopCleaner.OnDisabled();
            }
            catch (Exception ex) { Ui.LogError(ex, "切换桌面清理开关"); }
        }

        /// <summary>停止并释放所有文件监听器（退出时调用）。</summary>
        public void Stop()
        {
            try { if (debounce != null) debounce.Stop(); } catch { }
            try { if (periodic != null) periodic.Stop(); } catch { }

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

            try { if (desktopCleaner != null) desktopCleaner.Stop(); } catch { }
        }

        void OnChange(bool isStartMenu)
        {
            panel.Dispatcher.BeginInvoke((Action)delegate
            {
                if (isStartMenu) startMenuDirty = true;
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
        /// 是否需要首次扫描：apps.json 不存在、读不出来、或里面一个应用都没有。
        /// </summary>
        bool NeedsInitialScan()
        {
            try
            {
                string path = Path.Combine(baseDir, "config", "apps.json");
                if (!File.Exists(path)) return true;

                string json = JsonIO.Read(path);
                if (string.IsNullOrEmpty(json)) return true;

                // 数一下 apps 数组
                System.Web.Script.Serialization.JavaScriptSerializer ser =
                    new System.Web.Script.Serialization.JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return true;

                object arr;
                if (!d.TryGetValue("apps", out arr) || arr == null) return true;
                int count = 0;
                foreach (object o in (System.Collections.IEnumerable)arr) count++;
                return count == 0;
            }
            catch
            {
                return true; // 读不出就当作需要扫
            }
        }

    }
}
