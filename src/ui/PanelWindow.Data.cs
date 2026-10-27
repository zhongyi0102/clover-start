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
                                AppIcons.Clear();
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

        /// <summary>隐藏项数量（供右键菜单显示）。</summary>
        public int HiddenCount()
        {
            try { return catalog == null ? 0 : catalog.HiddenItems().Count; }
            catch { return 0; }
        }

        /// <summary>
        /// 当前浏览的分类（对外只读）。
        /// 供验收测试断言「重启后是否停在有效分类」，避免那条边界再次悄悄坏掉。
        /// </summary>
        public string CurrentCategory()
        {
            return current;
        }

        /// <summary>当前磁贴区的滚动偏移（验收测试用；-1 表示取不到）。</summary>
        public double CurrentScrollOffset()
        {
            try { return contentScroll == null ? -1 : contentScroll.VerticalOffset; }
            catch { return -1; }
        }

        /// <summary>已记录滚动位置的分类数（验收测试用）。</summary>
        public int ScrollMemoryCount()
        {
            try { return scrollOffsets.Count; }
            catch { return -1; }
        }

        /// <summary>某个分类是否已有记录的滚动位置。</summary>
        public bool HasScrollMemory(string cat)
        {
            try { return cat != null && scrollOffsets.ContainsKey(cat); }
            catch { return false; }
        }

        /// <summary>供测试直接设置滚动偏移（模拟用户滚动）。</summary>
        public void ScrollToForTest(double offset)
        {
            try
            {
                if (contentScroll == null) return;
                contentScroll.UpdateLayout();
                contentScroll.ScrollToVerticalOffset(offset);
                contentScroll.UpdateLayout();
                scrollOffsets[current] = contentScroll.VerticalOffset;
            }
            catch { }
        }

        /// <summary>
        /// 供测试切换分类（绕开鼠标点击）。
        ///
        /// 为什么不走真实点击：合成鼠标事件会让面板失焦，触发已存在的
        /// "点击面板外自动收起"，测试就断在中间了。这里直接走切换逻辑，
        /// 覆盖的是滚动记忆本身。
        /// </summary>
        public void SwitchCategoryForTest(string cat)
        {
            try
            {
                SaveScrollOffset();
                current = cat;
                SaveLastCategory();
                RebuildNav();
                RenderApps();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "测试切换分类");
            }
        }

        /// <summary>
        /// 一键隐藏一整类（如「系统工具」45 个）。
        /// 返回实际隐藏的数量。
        /// </summary>
        public int HideCategory(string category)
        {
            try
            {
                if (string.IsNullOrEmpty(category)) return 0;
                if (hiddenApps == null) hiddenApps = HiddenApps.Load(baseDir);

                int n = 0;
                foreach (AppItem a in catalog.Apps)
                {
                    if (a.Hidden || a.Category != category) continue;
                    if (string.IsNullOrEmpty(a.Path)) continue;
                    if (hiddenApps.Hide(a.Path)) { a.Hidden = true; n++; }
                }
                if (n > 0)
                {
                    Ui.Log("隐藏分类: " + category + " (" + n + " 个)");
                    if (current == category) { current = ""; SaveLastCategory(); }
                    RebuildNav();
                    RenderApps();
                }
                return n;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "隐藏分类");
                return 0;
            }
        }

        /// <summary>取消隐藏全部，并把界面切到「隐藏的项目」查看结果。</summary>
        public int UnhideAll()
        {
            try
            {
                if (hiddenApps == null) hiddenApps = HiddenApps.Load(baseDir);
                int n = hiddenApps.Clear();
                if (n == 0) return 0;

                // 逐个清标记（Clear 只清了文件，内存里的标记还在）
                foreach (AppItem a in catalog.Apps) a.Hidden = false;

                Ui.Log("取消隐藏全部: " + n + " 个");
                current = "";
                SaveLastCategory();
                RebuildNav();
                RenderApps();
                return n;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "取消隐藏全部");
                return 0;
            }
        }

        /// <summary>跳到「隐藏的项目」分类（右键菜单用）。</summary>
        public void ShowHiddenItems()
        {
            try
            {
                current = HiddenCategory;
                SaveLastCategory();
                RebuildNav();
                RenderApps();
                ShowPanel();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "打开隐藏的项目");
            }
        }

        /// <summary>
        /// 读取上次浏览的分类（重启后恢复）。
        ///
        /// 用途：你频繁用 dev-run.ps1 重启程序看效果，每次都要重新点分类很烦。
        /// 位置由 layout.json 保存，分类由这里保存，两者合起来就是"重启后回到原样"。
        ///
        /// 只存分类名，不存搜索词——搜索是临时的，恢复它反而意外。
        /// </summary>
        void LoadLastCategory()
        {
            try
            {
                if (!File.Exists(lastCatPath)) return;
                string json = JsonIO.Read(lastCatPath);
                if (string.IsNullOrEmpty(json)) return;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return;

                object v;
                if (!d.TryGetValue("category", out v) || v == null) return;
                string cat = Convert.ToString(v);
                if (string.IsNullOrEmpty(cat)) return;

                // 「全部」永远有效
                if (cat == "") { current = ""; return; }

                // 「常用」即使暂时为空也接受：界面会显示"从面板启动几次就会出现"的提示，
                // 那是有意义的空状态，不是错误。
                if (cat == "常用") { current = "常用"; return; }

                // 「隐藏的项目」只在**确实还有隐藏项**时才有效。
                // 否则会出现：取消隐藏全部 → 重启 → 停在一个已消失的分类上，面板一片空白。
                if (cat == HiddenCategory)
                {
                    if (catalog.HiddenItems().Count > 0) { current = cat; return; }
                    Ui.Log("上次分类「隐藏的项目」已无内容，回退到「全部」");
                    return;
                }

                // 普通分类：必须还在 Order 里，**且还有可见项**。
                // 只查 Order 不够——单个隐藏会把某分类掏空，那样也该回退。
                bool existsInOrder = false;
                foreach (string c in catalog.Order)
                {
                    if (string.Equals(c, cat, StringComparison.Ordinal)) { existsInOrder = true; break; }
                }
                if (existsInOrder && catalog.ByCategory(cat).Count > 0)
                {
                    current = cat;
                    return;
                }

                // 分类已不存在或已被掏空 → 保持默认（全部）
                Ui.Log("上次分类「" + cat + "」已不存在或没有可见项，回退到「全部」");
            }
            catch (Exception ex)
            {
                Ui.Log("读取上次分类失败（忽略，用默认）: " + ex.Message);
            }
        }

        /// <summary>记住当前分类，供下次启动恢复。</summary>
        void SaveLastCategory()
        {
            try
            {
                if (string.IsNullOrEmpty(lastCatPath)) return;
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["category"] = current;
                d["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                JavaScriptSerializer ser = new JavaScriptSerializer();
                JsonIO.WriteAtomic(lastCatPath, ser.Serialize(d));
            }
            catch { }
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
                    // 「全部」分类内部标识是空串，但落到文件里写的是 AllKey
                    // （空串做 JSON 键会让 PowerShell ConvertFrom-Json 之类的工具报错）。
                    // 两种写法都接受，老文件照常能读。
                    customOrders[RestoreCatKey(kv.Key)] = list;
                }
            }
            catch { }
        }

        /// <summary>
        /// 「全部」分类在 order.json 里的键名。
        /// 用可读的名字而不是空串——空串是合法的 JSON 键，但会让
        /// PowerShell 的 ConvertFrom-Json、jq 之外的工具报错，手工排查时很别扭。
        /// </summary>
        const string AllKey = "__all__";

        /// <summary>存盘时把内部标识转成文件里的键名。</summary>
        public static string FileCatKey(string internalKey)
        {
            return string.IsNullOrEmpty(internalKey) ? AllKey : internalKey;
        }

        /// <summary>读取时把文件里的键名转回内部标识（兼容老的 "" 写法）。</summary>
        public static string RestoreCatKey(string fileKey)
        {
            return fileKey == AllKey ? "" : fileKey;
        }

        void SaveCustomOrders()
        {
            try
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                foreach (KeyValuePair<string, List<string>> kv in customOrders)
                    d[FileCatKey(kv.Key)] = kv.Value;
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
