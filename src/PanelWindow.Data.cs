using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>数据层：目录/配置的加载与保存、刷新、对外接口（供 TriggerWindow 与 AppWatcher 调用）。</summary>
    partial class PanelWindow
    {
        public void AttachWatcher(AppWatcher w)
        {
            watcher = w;
        }

        public void AttachMiniBar(MiniTaskbarWindow bar)
        {
            miniBar = bar;
        }

        public bool CatalogHasPath(string path)
        {
            return catalog != null && catalog.ContainsPath(path);
        }

        public bool CatalogHasName(string name)
        {
            return catalog != null && catalog.ContainsName(name);
        }

        /// <summary>后台扫描完成后调用：重新读目录并刷新界面（不抛异常）。</summary>
        public void RefreshCatalogQuiet()
        {
            try
            {
                ReloadCatalog();
                RebuildNav();
                RenderApps();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "刷新面板");
            }
            // 任务栏刷新定时器由 StartTaskTimer 统一管理，这里不再重复创建（旧代码每次调用都漏一个）。
            StartTaskTimer();
        }

        public void Rescan()
        {
            try
            {
                // 手动重新扫描也走后台，避免界面卡住；完成后自动刷新
                Ui.Log("手动重新扫描");
                ScanService.RunAsync(baseDir, "手动", delegate(bool ok)
                {
                    if (!ok) return;
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            try
                            {
                                Native.ClearIconCache();
                                Pinyin.ClearCache();
                                ReloadCatalog();
                                RebuildNav();
                                RenderApps();
                            }
                            catch (Exception ex) { Ui.LogError(ex, "重新扫描后刷新"); }
                        }));
                    }
                    catch (Exception ex) { Ui.LogError(ex, "重新扫描回调"); }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("重新扫描失败: " + ex.Message, "四叶草面板");
            }
        }

        public void CleanDesktopFromMenu()
        {
            if (watcher == null) return;
            int n = watcher.CleanDesktopNow(true);
            MessageBox.Show("已把 " + n + " 个桌面快捷方式移到备份文件夹：\r\n" + Path.Combine(baseDir, "desktop-backup"), "四叶草");
        }

        /// <summary>把 desktop-backup 里的快捷方式放回桌面，返回成功数量。</summary>
        public int RestoreDesktopFromMenu()
        {
            if (watcher == null) return 0;
            try
            {
                return watcher.RestoreDesktopNow();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "还原桌面快捷方式");
                return 0;
            }
        }

        void ReloadCatalog()
        {
            catalog = Catalog.Load(baseDir);
            if (usage != null) catalog.ApplyUsage(usage);
            hiddenCats = settings.HiddenCategories;
            catalog.HideCategories(hiddenCats);
        }

        /// <summary>切换“启用迷你任务栏”后调用：关掉时清空并隐藏整条迷你任务栏。</summary>
        public void ApplyMiniTaskbarSetting()
        {
            try
            {
                if (miniBar == null) return;
                miniBar.Enabled = settings.MiniTaskbar;
                if (!settings.MiniTaskbar) miniBar.Clear();
                OverlayGate.Refresh();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "应用迷你任务栏开关");
            }
        }

        /// <summary>切换“自动移走桌面快捷方式”后调用：通知 watcher 立即停/启自动清理。</summary>
        public void ApplyDesktopCleanSetting()
        {
            try
            {
                if (watcher != null) watcher.AutoCleanEnabled = settings.DesktopAutoClean;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "应用桌面清理开关");
            }
        }

        void LoadCustomOrders()
        {
            try
            {
                if (!File.Exists(orderPath)) return;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string orderJson = JsonIO.Read(orderPath);
                if (string.IsNullOrEmpty(orderJson)) return;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(orderJson);
                if (d == null) return;
                foreach (KeyValuePair<string, object> kv in d)
                {
                    List<string> list = new List<string>();
                    object[] arr = kv.Value as object[];
                    if (arr != null)
                    {
                        foreach (object o in arr) list.Add(Convert.ToString(o));
                    }
                    else if (kv.Value is System.Collections.IEnumerable)
                    {
                        foreach (object o in (System.Collections.IEnumerable)kv.Value) list.Add(Convert.ToString(o));
                    }
                    customOrders[kv.Key] = list;
                }
            }
            catch { }
        }

        void SaveCustomOrders()
        {
            try
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                foreach (KeyValuePair<string, List<string>> kv in customOrders) d[kv.Key] = kv.Value;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                JsonIO.WriteAtomic(orderPath, ser.Serialize(d));
            }
            catch { }
        }

        void ApplyCustomOrder(List<AppItem> list)
        {
            List<string> order;
            if (!customOrders.TryGetValue(current, out order) || order == null || order.Count == 0) return;
            Dictionary<string, int> idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < order.Count; i++)
            {
                if (!idx.ContainsKey(order[i])) idx[order[i]] = i;
            }
            List<AppItem> sorted = new List<AppItem>(list);
            List<AppItem> ordered = new List<AppItem>();
            foreach (AppItem a in System.Linq.Enumerable.OrderBy(sorted, x => idx.ContainsKey(x.Path) ? idx[x.Path] : int.MaxValue)) ordered.Add(a);
            list.Clear();
            list.AddRange(ordered);
        }
    }
}
