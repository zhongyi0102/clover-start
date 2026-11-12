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
    /// 2) 应用快捷方式先保存完整入口并登记到面板，确认交接成功后才移到回收站。
    ///    文件/文件夹快捷方式、网页链接与未知入口不自动清理。
    /// </summary>
    class AppWatcher
    {
        string baseDir;
        volatile PanelWindow panel;
        readonly Dispatcher dispatcher;
        readonly object pendingRemovedGate = new object();
        readonly List<Dictionary<string, object>> pendingDesktopRemoved =
            new List<Dictionary<string, object>>();
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
        DesktopWorkspace desktopWorkspace;
        public DesktopWorkspace DesktopWorkspace { get { return EnsureDesktopWorkspace(); } }

        DesktopWorkspace EnsureDesktopWorkspace()
        {
            if (desktopWorkspace != null) return desktopWorkspace;
            desktopCleaner = desktopCleaner ?? new DesktopCleaner(baseDir, settings);
            desktopCleaner.EnsureApplicationEntry = PreserveDesktopApplication;
            desktopWorkspace = new DesktopWorkspace(settings, delegate
            {
                VirtualDesks desks = new VirtualDesks(baseDir);
                desks.Load();
                if (string.IsNullOrEmpty(desks.Root)) desks.Root = VirtualDesks.DefaultRoot(baseDir);
                desks.EnsureDesk(PanelWindow.VirtualDesktopCategory);
                return desks.FolderOf(PanelWindow.VirtualDesktopCategory);
            }, IsPanelApplication, desktopCleaner.CleanManual);
            desktopWorkspace.OnCompleted = delegate(DesktopWorkspace.Result result)
            {
                if (panel != null) dispatcher.BeginInvoke(new Action(delegate
                {
                    if (panel != null) panel.DesktopWorkspaceUpdated(result);
                }));
            };
            return desktopWorkspace;
        }

        internal bool IsPanelApplication(string path)
        {
            string identity = DesktopContentRules.ApplicationIdentity(path);
            if (string.IsNullOrEmpty(identity)) return false;
            foreach (AppItem app in Catalog.Load(baseDir).Apps)
                if (app != null && string.Equals(identity, DesktopContentRules.ApplicationIdentity(app.Path), StringComparison.Ordinal)) return true;
            return false;
        }

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
            // 监听器允许先于主面板启动。保存创建它的 UI Dispatcher，
            // 这样开始菜单发生变化时不必依赖 panel.Dispatcher 才能做防抖。
            this.dispatcher = Dispatcher.CurrentDispatcher;
        }

        /// <summary>
        /// 主面板首次打开后补挂。开机阶段面板按需创建，监听器仍要立即工作；
        /// 扫描完成但面板尚不存在时无需刷新 UI，首次构造会直接读取最新目录。
        /// </summary>
        public void AttachPanel(PanelWindow value)
        {
            panel = value;
            if (value == null) return;

            // 启动补扫可能在主面板创建前移走桌面快捷方式。
            // 把这期间的提示合并后补给面板，不能因为懒加载丢掉撤销入口。
            List<Dictionary<string, object>> pending = null;
            lock (pendingRemovedGate)
            {
                if (pendingDesktopRemoved.Count > 0)
                {
                    pending = new List<Dictionary<string, object>>(pendingDesktopRemoved);
                    pendingDesktopRemoved.Clear();
                }
            }
            if (pending != null)
            {
                try
                {
                    value.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        try { value.ShowDesktopRemovedTip(pending); }
                        catch (Exception ex) { Ui.LogError(ex, "补发桌面移除提示"); }
                    }));
                }
                catch (Exception ex) { Ui.LogError(ex, "调度桌面移除提示"); }
            }
        }

        public void Start()
        {
            debounce = new DispatcherTimer();
            debounce.Interval = TimeSpan.FromMilliseconds(2500);
            debounce.Tick += OnDebounce;

            periodic = new DispatcherTimer();
            periodic.Interval = TimeSpan.FromHours(6);
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
            // 不做这一步，新用户首次运行会看到空面板，要等 6 小时兜底才有应用。
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
            if (settings.DesktopKeepEmptyEnabled) EnsureDesktopWorkspace().Start();
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
                    desktopCleaner.EnsureApplicationEntry = PreserveDesktopApplication;

                    // 移除后弹轻提示（面板负责显示「保留 / 撤销」）
                    desktopCleaner.OnRemoved = NotifyDesktopRemoved;
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
                settings.DesktopKeepEmptyEnabled = enabled;
                settings.DesktopCleanEnabled = false;
                settings.Save();
                DesktopWorkspace workspace = EnsureDesktopWorkspace();
                if (desktopCleaner != null) desktopCleaner.Stop();
                if (enabled) workspace.Start(); else workspace.Stop();
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
            try { if (desktopWorkspace != null) desktopWorkspace.Stop(); } catch { }
        }

        void OnChange(bool isStartMenu)
        {
            try
            {
                dispatcher.BeginInvoke((Action)delegate
                {
                    if (debounce == null || ErrorLog.ShuttingDown) return;
                    if (isStartMenu) startMenuDirty = true;
                    debounce.Stop();
                    debounce.Start();
                });
            }
            catch { }
        }

        void OnDebounce(object sender, EventArgs e)
        {
            debounce.Stop();
            if (startMenuDirty)
            {
                startMenuDirty = false;
                SystemSearch.Invalidate();
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
                ScanService.RunAsync(baseDir, reason, delegate(ScanResult result)
                {
                    if (result == null || !result.Success || !result.Changed) return;
                    CatalogPreloader.Invalidate();
                    SystemSearch.Invalidate();
                    PanelWindow target = panel;
                    if (target == null)
                    {
                        Log.Diagnostic("后台扫描内容有变化(" + reason
                            + ")，主面板尚未创建；首次打开时读取最新目录");
                        CatalogPreloader.WarmAsync(baseDir);
                        return;
                    }
                    try
                    {
                        target.Dispatcher.BeginInvoke(new Action(delegate
                        {
                            try { target.RefreshCatalogQuiet(); }
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
        /// 桌面清理回调来自后台线程。面板已有时切回它的 Dispatcher；
        /// 尚未创建时先排队，首次打开后合并补发。
        /// </summary>
        void NotifyDesktopRemoved(List<Dictionary<string, object>> removed)
        {
            try
            {
                if (removed == null || removed.Count == 0) return;

                PanelWindow target = panel;
                if (target == null)
                {
                    lock (pendingRemovedGate)
                    {
                        // AttachPanel 可能正好在 target 的第一次读取之后完成。
                        // 锁内再读一次，避免它已经冲刷完队列、这里却随后追加，
                        // 导致这批撤销提示一直等不到下一次挂接。
                        target = panel;
                        if (target == null)
                        {
                            pendingDesktopRemoved.AddRange(removed);
                            return;
                        }
                    }
                }

                target.Dispatcher.BeginInvoke(new Action(delegate
                {
                    try { target.ShowDesktopRemovedTip(removed); }
                    catch (Exception ex) { Ui.LogError(ex, "桌面移除提示"); }
                }));
            }
            catch (Exception ex) { Ui.LogError(ex, "调度桌面移除提示"); }
        }

        readonly object desktopEntryGate = new object();
        internal bool PreserveDesktopApplication(string source)
        {
            try
            {
                lock (desktopEntryGate)
                {
                    if (!IsPanelApplication(source)) return false;
                    string identity = DesktopContentRules.ApplicationIdentity(source);
                    foreach (AppItem app in Catalog.Load(baseDir).Apps)
                        if (!string.Equals(app.Path, source, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(identity, DesktopContentRules.ApplicationIdentity(app.Path), StringComparison.Ordinal))
                            return true; // 已有独立可用入口，不再登记重复应用。
                    string fingerprint = DesktopContentRules.Fingerprint(source);
                    if (string.IsNullOrEmpty(fingerprint)) return false;
                    // 保存 lnk 本身，保留启动参数、工作目录与图标。
                    string folder = Path.Combine(baseDir, "config", "desktop-app-entries");
                    Directory.CreateDirectory(folder);
                    string target = Path.Combine(folder, fingerprint + Path.GetExtension(source));
                    if (!File.Exists(target) && !VerifiedFileMove.Copy(source, target).Ok) return false;
                    if (DesktopContentRules.Fingerprint(target) != fingerprint
                        || DesktopContentRules.Fingerprint(source) != fingerprint) return false;
                    Catalog existing = Catalog.Load(baseDir);
                    if (existing.ContainsPath(target)) return true;
                    string manual = Path.Combine(baseDir, "config", "manual-apps.json");
                    System.Web.Script.Serialization.JavaScriptSerializer serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                    string old = JsonIO.Read(manual);
                    Dictionary<string, object> data = string.IsNullOrEmpty(old)
                        ? new Dictionary<string, object>() : serializer.Deserialize<Dictionary<string, object>>(old);
                    List<object> apps = new List<object>();
                    object array;
                    if (data.TryGetValue("apps", out array))
                        foreach (object item in JsonIO.AsArray(array)) apps.Add(item);
                    bool registered = false;
                    foreach (object entry in apps)
                    {
                        Dictionary<string, object> app = entry as Dictionary<string, object>;
                        object appPath;
                        if (app != null && app.TryGetValue("path", out appPath)
                            && string.Equals(Convert.ToString(appPath), target, StringComparison.OrdinalIgnoreCase))
                            registered = true;
                    }
                    if (!registered)
                    {
                        string appName = Path.GetFileNameWithoutExtension(source);
                        if (existing.ContainsName(appName)) appName += " · 桌面入口 " + fingerprint.Substring(0, 6);
                        apps.Add(new Dictionary<string, object> {
                            { "name", appName }, { "path", target }, { "category", "其他" }
                        });
                    }
                    data["apps"] = apps;
                    if (!JsonIO.WriteAtomic(manual, serializer.Serialize(data)).Success) return false;
                    string saved = JsonIO.Read(manual);
                    if (string.IsNullOrEmpty(saved) || saved.IndexOf(Path.GetFileName(target), StringComparison.Ordinal) < 0)
                        return false;
                    AppItem original = existing.Apps.Find(a => string.Equals(a.Path, source, StringComparison.OrdinalIgnoreCase));
                    if (original != null)
                    {
                        AppClassification classification = new AppClassification(baseDir);
                        HiddenApps hidden = HiddenApps.Load(baseDir);
                        classification.Apply(existing.Apps, settings, hidden);
                        classification.Set(new AppItem { Path = target }, original.Role);
                        if (hidden.IsHidden(source)) hidden.Hide(target, hidden.ReasonOf(source));
                    }
                    Catalog check = Catalog.Load(baseDir);
                    if (!check.ContainsPath(target)) return false;
                    CatalogPreloader.Invalidate();
                    if (panel != null) dispatcher.BeginInvoke(new Action(delegate { panel.RefreshCatalogQuiet(); }));
                    return true;
                }
            }
            catch (Exception ex) { Log.Warn("桌面应用入口未交接，保留原处：" + ex.Message); return false; }
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
