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
                // 应用清单换了：旧的 lnk→目标 缓存可能指向已卸载的程序，清掉重建
                RunningApps.Clear();
                WarmRunningCache();
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

        /// <summary>
        /// 后台预热"应用是否在运行"要用的 .lnk 目标缓存。
        ///
        /// 首次解析 112 个快捷方式实测 239 ms（COM 调用，每个 1-3 ms）。
        /// 这代价若落在用户打开面板那一刻会明显卡一下，
        /// 所以挪到后台线程提前做——面板每次渲染只读缓存（实测 0 ms）。
        /// 内部起线程，不会阻塞界面。
        /// </summary>
        void WarmRunningCache()
        {
            try
            {
                if (catalog == null || catalog.Apps == null) return;
                List<string> paths = new List<string>(catalog.Apps.Count);
                foreach (AppItem a in catalog.Apps)
                {
                    if (!string.IsNullOrEmpty(a.Path)) paths.Add(a.Path);
                }
                RunningApps.WarmUpAsync(paths);
            }
            catch { }
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
        void ReloadCatalog()
        {
            catalog = Catalog.Load(baseDir);
            if (usage != null) catalog.ApplyUsage(usage);
            hiddenCats = new List<string>();   // 分类系统已下线，不再从 settings 读
            catalog.HideCategories(hiddenCats);
        }

        /// <summary>切换“启用迷你任务栏”后调用：关掉时清空并隐藏整条迷你任务栏。</summary>
        public void ApplyMiniTaskbarSetting()
        {
            try
            {
                if (miniBar == null) return;
                miniBar.Enabled = settings.EffectiveMiniTaskbar;   // 安全模式下自动为 false
                if (!miniBar.Enabled) miniBar.Clear();
                OverlayGate.Refresh();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "应用迷你任务栏开关");
            }
        }

        /// <summary>
        /// 切换「毛玻璃背景」后调用。
        ///
        /// 关闭时要**主动移除**已挂上的背景层并把底色改回不透明 ——
        /// 只改设置不管界面的话，用户会看到"勾掉了但还在"。
        /// </summary>
        /// <summary>
        /// 把 settings 里的外观参数注入 GlassBackdrop。
        ///
        /// **为什么必须抽成独立方法**：这三条路径都要注入，
        /// 否则参数只在其中一条生效（第 1 个提交的真实 bug）：
        ///   · ApplyGlassSetting  —— 用户点「毛玻璃背景」菜单开关
        ///   · RefreshGlassSoon   —— 启动 / 显示面板 / 窗口尺寸变化
        ///   · ApplyTheme         —— 切主题
        ///
        /// 之前只有 ApplyGlassSetting 注入，所以改 settings.json 后重启
        /// 只有 glassOpacity 生效、blur/tint/brightness 全是默认值。
        ///
        /// 这些参数只影响 Attach 阶段的合成，**不需要重新截屏**。
        /// 数值越界由 Settings 读取时已回退默认，这里直接赋值。
        /// </summary>
        /// <summary>
        /// 外观窗口拖动滑块时的**轻量预览**。
        ///
        /// 与 ApplyGlassSetting 的区别：
        ///   · 只重跑 Attach（合成），**不重新截屏**（cached 原图还在）
        ///   · 不写盘（写盘由调用方在松手时做）
        ///   · 异常只写日志，绝不抛给界面
        ///
        /// 为什么这样够快：外观参数（blur/tint/brightness）只影响 Attach 阶段，
        /// 而截屏结果缓存在 glassBackdrop.cached 里。所以拖动滑块时
        /// 每次只花几毫秒重新合成，不是 50-150ms 重新截屏。
        /// </summary>
        /// <summary>当前文字保底的补偿 alpha（0 = 未补偿）。供外观窗口显示提示用。</summary>
        public double CurrentGuardAlpha
        {
            get { try { return glassBackdrop == null ? 0 : glassBackdrop.GuardAlpha; } catch { return 0; } }
        }

        public void ApplyGlassPreview()
        {
            try
            {
                if (glassBackdrop == null || settings == null) return;

                InjectGlassParams();

                // 浓度 0 → 关掉玻璃（与正式路径一致）
                if (settings.GlassOpacity <= 0)
                {
                    glassBackdrop.Clear();
                    glassBackdrop.OpacityPercent = 0;
                    ApplyGlassTint();
                    return;
                }

                glassBackdrop.OpacityPercent = settings.GlassOpacity;
                glassBackdrop.CornerRadius = Ui.R.Lg;
                ApplyGlassTint();

                // 关键：直接 Refresh 会走「重截」分支吗？
                // 不会 —— Refresh 里先比 cacheKey，条件没变就只调 Attach。
                // 所以这里调 Refresh 是安全的，开销只有合成那几毫秒。
                if (IsVisible) glassBackdrop.Refresh();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "毛玻璃预览");
            }
        }

        void InjectGlassParams()
        {
            try
            {
                if (glassBackdrop == null || settings == null) return;

                glassBackdrop.BlurRadius = settings.GlassBlur;
                glassBackdrop.TintStrength = settings.GlassTintStrength / 100.0;
                glassBackdrop.Brightness = settings.GlassBrightness;
                glassBackdrop.ContrastGuardEnabled = settings.TextContrastGuard;

                // 染色色：空 = 用强调色（改动前的行为）
                byte tr, tg, tb;
                if (!string.IsNullOrEmpty(settings.GlassTint)
                    && Settings.TryParseHexColor(settings.GlassTint, out tr, out tg, out tb))
                {
                    glassBackdrop.TintColor = System.Windows.Media.Color.FromRgb(tr, tg, tb);
                }
                else
                {
                    glassBackdrop.TintColor = null;   // 用 Ui.Accent
                }
            }
            catch { }
        }

        public void ApplyGlassSetting()
        {
            try
            {
                if (glassBackdrop == null) return;

                // 先同步背景模式（fixed / screen）—— 它决定 OpacityPercent 之外
                // 的另一半行为：读固定图还是截屏。
                glassBackdrop.FollowWallpaper = settings != null
                    && string.Equals(settings.GlassMode, "screen", StringComparison.OrdinalIgnoreCase);

                // 外观参数注入（抽成独立方法，启动/切主题/改设置三条路径都调）
                InjectGlassParams();

                if (settings.GlassOpacity <= 0)
                {
                    glassBackdrop.Clear();
                    glassBackdrop.OpacityPercent = 0;
                    ApplyGlassTint();     // 底色恢复不透明
                    return;
                }

                glassBackdrop.OpacityPercent = settings.GlassOpacity;
                glassBackdrop.CornerRadius = Ui.R.Lg;
                ApplyGlassTint();         // 底色改半透明

                // 面板没显示时先记下设置，等 ShowPanel 再取背景
                if (IsVisible) RefreshGlassSoon(50);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "应用毛玻璃设置");
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

        /// <summary>
        /// 查看隐藏的项目（右键菜单用）。
        ///
        /// 分类标签只剩「常用」「全部」后，没有「隐藏的项目」页了 ——
        /// 改成弹一个对话框逐个恢复。
        ///
        /// 为什么不只留「取消隐藏全部」：用户误隐藏了一个常用软件时，
        /// 得先全部恢复、再重新隐藏其余的，很别扭。逐个恢复才是对的。
        /// </summary>
        public void ShowHiddenItems()
        {
            try
            {
                List<AppItem> hidden = catalog.HiddenItems();
                if (hidden == null || hidden.Count == 0) return;

                HiddenItemsDialog.Show(this, hidden, delegate(string path)
                {
                    try
                    {
                        // 复用已有的 SetAppHidden（内部写 hidden-apps.json + 刷新）
                        AppItem target = null;
                        foreach (AppItem a in catalog.Apps)
                        {
                            if (a != null && string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))
                            { target = a; break; }
                        }
                        if (target == null) return false;

                        SetAppHidden(target, false);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Ui.LogError(ex, "恢复隐藏项");
                        return false;
                    }
                });
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

                // ---- 分类系统下线：只接受「常用」和「全部(__all__)」两个键 ----
                //
                // 老 order.json 里可能还有「办公」「开发」这类分类键。它们已经没有
                // 对应的界面标签，继续读进来只会白占内存、并在下次存盘时被写回去。
                //
                // **安全顺序**（用户明确要求）：
                //   ① 先解析成功（走到这里说明 JSON 是好的）
                //   ② 只保留两个已知键，其余**跳过不读**
                //   ③ 若发现旧键 → 先备份原文件（order.json.bak-cleanup）
                //   ④ 备份成功后才写回清理后的版本
                //   ⑤ 任何一步失败 → 保留原文件不动，只记日志
                //
                // 注意：跳过旧键**不等于删除数据** —— 在备份成功之前，
                // 原文件（含「办公」的 13 条排序）始终留在磁盘上。
                List<string> droppedKeys = new List<string>();
                foreach (KeyValuePair<string, object> kv in d)
                {
                    // 已知键：__all__（= 全部）和 常用
                    bool known = kv.Key == AllKey || kv.Key == "常用";
                    if (!known)
                    {
                        droppedKeys.Add(kv.Key);
                        continue;
                    }

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
                    customOrders[RestoreCatKey(kv.Key)] = list;
                }

                // ---- 发现旧键 → 备份后清理 ----
                if (droppedKeys.Count > 0)
                {
                    string backupPath = orderPath + ".bak-cleanup";
                    bool backedUp = false;
                    try
                    {
                        File.Copy(orderPath, backupPath, true);
                        backedUp = File.Exists(backupPath)
                            && new FileInfo(backupPath).Length > 0;
                    }
                    catch (Exception bex)
                    {
                        Ui.LogError(bex, "order.json 备份");
                    }

                    if (!backedUp)
                    {
                        // 备份失败 → 不写回，保留原文件（下次启动再试）
                        Ui.Log("order.json 有 " + droppedKeys.Count
                            + " 个废弃分类键，但备份失败，已保留原文件不清理");
                    }
                    else
                    {
                        // 只写两个已知键
                        SaveCustomOrders();
                        Ui.Log("order.json 已清理 " + droppedKeys.Count
                            + " 个废弃分类键（" + string.Join(", ", droppedKeys.ToArray())
                            + "），原文件备份到 order.json.bak-cleanup");
                    }
                }
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "读 order.json");
            }
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
