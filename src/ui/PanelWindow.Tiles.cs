using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Clover
{
    partial class PanelWindow
    {
        /// <summary>
        /// 「常用」只包含最近 72 小时启动过的用户应用，无固定名单、无数量上限。
        ///
        /// **不设上限**：早期版本截断到前 12 个，导致右键「添加到常用」在
        /// 使用榜已满时点了没反应 —— 用户看不到任何变化，以为功能坏了。
        /// 现在全部列出，靠磁贴区自身滚动承载。
        ///
        /// 旧固定记录不参与显示、分类或排序；不删除真实启动记录。
        ///
        /// Hidden、低价值与电脑入口始终过滤，启动记录不能覆盖分类。
        /// </summary>
        List<AppItem> CommonApps()
        {
            List<AppItem> recent = new List<AppItem>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long nowTicks = DateTime.UtcNow.Ticks;
            foreach (AppItem a in catalog.Apps)
            {
                if (!IsCommonByUsage(a, CommonDismissedAt(a.Path), nowTicks)) continue;
                if (seen.Add(a.Path)) recent.Add(a);
            }
            // 近三天有持续使用的优先，再比较近期启动次数与最后启动时间。
            recent.Sort(delegate(AppItem a, AppItem b)
            {
                int c = usage == null ? 0 : usage.GetRecentDays(b.Path, nowTicks).CompareTo(usage.GetRecentDays(a.Path, nowTicks));
                if (c == 0 && usage != null) c = usage.GetRecentCount(b.Path, nowTicks).CompareTo(usage.GetRecentCount(a.Path, nowTicks));
                if (c == 0) c = b.LastUsed.CompareTo(a.LastUsed);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return recent;
        }



        /// <summary>
        /// 单条应用能否**因使用记录**进入「常用」页。
        ///
        /// 抽成静态纯函数是为了能被测试直接调用 —— 这里曾经漏掉 `Hidden` 判断，
        /// 导致"隐藏但有使用记录"的应用重新出现在常用页上（用户刚藏起来，它却还在）。
        /// 三天未用只退出常用，不隐藏软件。
        /// </summary>
        internal static bool IsCommonByUsage(AppItem a)
        {
            return IsCommonByUsage(a, 0);
        }


        /// <summary>
        /// dismissedAt 是用户上次从「最近常用」移除它时的 LastUsed 快照。
        /// 再次真实启动会产生更大的 LastUsed，于是自然重新进入列表。
        /// </summary>
        internal static bool IsCommonByUsage(AppItem a, long dismissedAt)
        {
            return IsCommonByUsage(a, dismissedAt, DateTime.UtcNow.Ticks);
        }

        internal static bool IsCommonByUsage(AppItem a, long dismissedAt, long nowTicks)
        {
            return CommonUsagePolicy.IsEligible(a, dismissedAt, nowTicks);
        }


        /// <summary>按路径找应用（忽略大小写）；找不到返回 null。</summary>
        AppItem FindAppByPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || catalog == null) return null;
                foreach (AppItem a in catalog.Apps)
                {
                    if (a == null || string.IsNullOrEmpty(a.Path)) continue;
                    if (string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)) return a;
                }
            }
            catch { }
            return null;
        }


        long CommonDismissedAt(string path)
        {
            long at;
            if (string.IsNullOrEmpty(path) || settings == null || settings.CommonDismissed == null)
                return 0;
            return settings.CommonDismissed.TryGetValue(path, out at) ? at : 0;
        }

        /// <summary>
        /// 内容没变就不重建磁贴。避免“每次打开面板都销毁并重建全部控件”，
        /// 那是重复开关面板时内存持续上涨的主因。
        /// </summary>
        void RenderAppsIfNeeded()
        {
            try
            {
                RefreshNavCountsInPlace();
                string sig = CurrentContentSig();
                if (sig == renderedSig && !tileRenderPending && pendingRenderApps == null)
                {
                    MarkFirstContentReady();
                    return;
                }
                if (current != "常用" || !TryRefreshCurrentTilesInPlace()) RenderApps();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "按需渲染磁贴");
            }
        }


        void RenderApps()
        {
            if (tilePanel == null) return;
            try
            {
                // 主动重建时同步刷新签名，避免下次 RenderAppsIfNeeded 误判为“没变”
                renderedSig = CurrentContentSig();
                RenderAppsCore();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "渲染应用磁贴");
            }
        }


        /// <summary>
        /// 取消旧批次与其图标回调。DispatcherOperation 不需要逐个保存：代次守卫
        /// 会让已排队回调成为 O(1) 空操作。
        /// </summary>
        void StopDeferredRendering()
        {
            unchecked { tileRenderGeneration++; }
            tileRenderPending = false;
            pendingRenderApps = null;
        }


        /// <summary>骨架阶段的静态加载提示；不使用 shimmer/bounce，避免额外动画与分心。</summary>
        void ShowTileLoadingState(string text)
        {
            if (tilePanel == null) return;
            tilePanel.Children.Clear();

            StackPanel loading = new StackPanel();
            loading.Margin = new Thickness(Ui.S.Xl, Ui.S.Xl + Ui.S.Sm, 0, 0);
            loading.HorizontalAlignment = HorizontalAlignment.Left;

            TextBlock glyph = Ui.T("\uE895", 18, Ui.Sub);
            glyph.FontFamily = Ui.Mdl;
            glyph.Opacity = 0.55;
            loading.Children.Add(glyph);

            TextBlock label = Ui.T(text ?? "正在准备内容…", 12, Ui.Sub);
            label.Margin = new Thickness(0, Ui.S.Sm, 0, 0);
            AutomationProperties.SetName(label, text ?? "正在准备内容");
            loading.Children.Add(label);

            tilePanel.Children.Add(loading);
        }


        /// <summary>
        /// 路径列表签名。数量相同但内容/顺序变化时也必须不同，
        /// 否则「换了一个固定应用」会被按需渲染误判成没有变化。
        /// </summary>
        internal static string PathListSignature(List<string> paths)
        {
            if (paths == null) return "0";
            return paths.Count + "\u0002" + string.Join("\u0003", paths.ToArray());
        }


        /// <summary>当前界面内容的签名（分类、搜索、应用、排序、隐藏项与固定名单）。</summary>
        string CurrentContentSig()
        {
            try
            {
                string q = searchBox == null ? "" : searchBox.Text.Trim();
                List<string> order;
                if (!customOrders.TryGetValue(current, out order)) order = null;
                string orderSig = PathListSignature(order);
                // 时间跨过 72 小时边界也必须使签名变化，不依赖新的启动或扫描。
                string commonSig = current == "常用" ? PathListSignature(CommonApps().ConvertAll(a => a.Path)) : "";
                // 隐藏数进签名：隐藏/取消隐藏会改变内容，但 Apps.Count 不变，
                // 不带上它的话「按需渲染」会误判成"没变化"而跳过重建。
                int hiddenCount = catalog == null ? 0 : catalog.HiddenItems().Count;
                // 折叠状态进签名：它是纯 UI 状态，不改 Apps/顺序，
                // 不带上的话「展开→切走→切回」会因签名相同而跳过重建，状态看起来丢了
                string collapseSig = (settings != null && settings.CollapseLowValue) ? "1" : "0";
                collapseSig += settings != null && settings.CollapseAuxiliaryApps ? "A" : "a";
                collapseSig += PathListSignature(settings.ActiveAppRoles) + PathListSignature(settings.AuxiliaryAppRoles)
                    + PathListSignature(settings.ComputerAppRoles) + PathListSignature(settings.LowValueMarked)
                    + PathListSignature(settings.LowValueUnmarked);
                string dismissedSig = LongMapSignature(settings == null ? null : settings.CommonDismissed);
                return current + "\u0001" + q + "\u0001" + catalog.Apps.Count +
                       "\u0001" + orderSig + "\u0001" + hiddenCount + "\u0001" + commonSig +
                       "\u0001" + collapseSig + "\u0001" + dismissedSig + "\u0001" + usageRevision;
            }
            catch
            {
                return "";
            }
        }


        internal static string LongMapSignature(Dictionary<string, long> values)
        {
            if (values == null || values.Count == 0) return "0";
            List<string> keys = new List<string>(values.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            List<string> parts = new List<string>();
            foreach (string key in keys)
                parts.Add(key + "=" + values[key]);
            return values.Count + "\u0002" + string.Join("\u0003", parts.ToArray());
        }


        /// <summary>
        /// 记下当前分类的滚动偏移。
        ///
        /// 必须在**切换 current 之前**调用——切完之后 current 已经变了，
        /// 记下来的偏移会算到新分类头上，越切越乱。
        /// </summary>
        void SaveScrollOffset()
        {
            try
            {
                if (contentScroll == null) return;
                scrollOffsets[current] = contentScroll.VerticalOffset;
            }
            catch { }
        }


        /// <summary>
        /// 恢复当前分类上次的滚动偏移。
        ///
        /// 时机是关键：内容刚重建时 ExtentHeight 还是 0，
        /// 此时 ScrollToVerticalOffset 会被钳到 0（表现为"滚了但立刻弹回顶部"）。
        /// 所以先 UpdateLayout 让布局完成，再设偏移。
        /// </summary>
        void RestoreScrollOffset()
        {
            try
            {
                if (contentScroll == null) return;

                double target;
                // 新页面没有历史记录时应从顶部开始，不能沿用前一个长列表的偏移。
                // 已记录 0 也走同一路径，确保页面切换语义稳定。
                if (!scrollOffsets.TryGetValue(current, out target)) target = 0;
                if (target < 0) target = 0;

                // 布局没跑完就设偏移会被钳掉，先强制布局
                contentScroll.UpdateLayout();

                // 恢复动作本身也会触发 ScrollChanged，不设标记的话
                // 会把刚恢复的值当成"用户滚动"再记一次，多次切换后逐渐漂移
                suppressScrollRecord = true;
                try
                {
                    contentScroll.ScrollToVerticalOffset(target);
                    contentScroll.UpdateLayout();
                }
                finally
                {
                    suppressScrollRecord = false;
                }
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "恢复滚动位置");
            }
        }


        void RenderAppsCore()
        {
            // ---- 「文件」标签：走完全独立的渲染路径 ----
            //
            // 文件标签与应用磁贴是两套东西：
            //   · 磁贴：AppItem、可拖动排序
            //   · 文件：FileScanner.Entry、只读浏览、点击打开
            // 混在一条渲染路径里会让"拖动换位"试图去拖一个文件。
            if (current == FilesCategory)
            {
                RenderFilesTab();
                return;
            }

            // ---- 虚拟桌面（自定义标签）----
            //
            // 自定义标签展示的是磁盘上真实文件夹里的文件，不是应用磁贴，
            // 所以走独立渲染路径。
            if (IsDeskTab(current))
            {
                RenderDeskTab(current);
                return;
            }

            StopDeferredRendering();
            int generation = tileRenderGeneration;
            int contentGeneration = firstContentGeneration;

            tilePanel.Children.Clear();
            tileIconStates.Clear();
            tileIconStatesByTile.Clear();
            // 映射表与 Children 同步清空：Children 是渲染容器，映射表才是索引真相
            renderedTiles.Clear();
            tileAppIndex.Clear();

            string q = searchBox == null ? "" : searchBox.Text.Trim();
            showingCommon = (q.Length == 0 && current == "常用");
            List<AppItem> list;

            // ---- 系统级搜索（已安装目录 + 开始菜单/注册表索引）----
            //
            // 与下面的"应用清单过滤"是两件事，同时进行：
            //   · 应用清单过滤：已扫描的 119 个应用，按拼音/中英文匹配
            //   · 系统级搜索：Catalog 立即匹配，开始菜单/注册表索引后台补齐
            // 用户看到的是统一搜索结果列表；应用目录结果优先，系统索引补充。
            // 两者在同一个 Grid 里，必须**互斥显示**。
            //
            // 第一版只把 searchResultPanel 设 Visible、忘了把 tilePanel 设 Collapsed，
            // 结果两者叠在一起渲染 —— 搜索结果的文字直接压在磁贴上，
            // 用户看到的就是"文字互相重叠、糊成一片"。
            //
            // ★ 应用侧只可能是这两个层之一。文件层在这里必须被收起来 ——
            //   否则从「文件」切回「常用」时，文件页会一直压在上面。
            bool searching = q.Length > 0;
            if (searching)
            {
                ShowContentLayer(ContentLayer.SearchResults);
                if (searchResults != null) searchResults.Search(q);
            }
            else
            {
                ShowContentLayer(ContentLayer.Tiles);
                if (searchResults != null) searchResults.Clear();
            }

            if (q.Length > 0)
            {
                // 搜索时**包含隐藏项**：用户主动搜名字，说明就是想找它
                list = new List<AppItem>();
                foreach (AppItem a in catalog.Apps)
                {
                    // 支持中文/英文直接匹配，也支持全拼（weixin）和首字母（wx）
                    if (!a.Hidden && !a.ExcludedEntry && SearchRanking.Matches(a.Name, q))
                        list.Add(a);
                }
                list.Sort(RenderCompare);
            }
            else if (current == "常用")
            {
                list = CommonApps();

                // 「常用」为空（没有使用记录）时自动切到「全部」。
                //
                // 为什么要这样做：新用户或刚清过 usage.json 的人打开面板，
                // 看到一个空白的「常用」页会以为程序坏了 ——
                // 而实际上 119 个应用都在「全部」里。
                //
                // 切过去而不是只显示提示：用户想要的是"看到应用"，
                // 不是"读一句解释"。切完再顺手写回 last-category，
                // 免得下次又回到空的「常用」。
                if (list.Count == 0)
                {
                    current = "";
                    showingCommon = false;
                    SaveLastCategory();
                    list = AllAppsSorted();
                    // 更新标签选中态（此时 RebuildNav 已完成，navItems 已就绪）
                    UpdateNav();
                }
            }
            else
            {
                // 「全部」：所有可见应用，按名称排序（逐字拼音）
                list = AllAppsSorted();
            }

            ApplyCustomOrder(list);

            // 常用只有一套顺序；全部仍保持角色与商店稳定分区。
            if (current == "") RegroupAllSections(list);

            // ---- 低价值段：在 ApplyCustomOrder **之后**切分 ----
            //
            // 先保留各区内部的人工顺序，再按真实标记分区；
            // 不能把第一个低价值之后的正常应用算作低价值。
            bool collapseLow = (current == "" && settings != null && settings.CollapseLowValue);
            List<AppItem> normalPart;
            List<AppItem> lowPart;
            if (current == "")
                SplitLowValueSegment(list, collapseLow, out normalPart, out lowPart);
            else
            {
                normalPart = new List<AppItem>(list);
                lowPart = new List<AppItem>();
            }

            pendingRenderApps = new List<AppItem>(normalPart);
            pendingRenderApps.AddRange(lowPart);
            pendingNormalCount = normalPart.Count;
            pendingActiveCount = current == "" ? CountActiveRole(normalPart) : 0;
            pendingAuxiliaryCount = current == "" ? normalPart.Count - pendingActiveCount : 0;
            // 收起态不会把低价值项放进 lowPart，但入口仍要显示真实数量。
            pendingLowCount = collapseLow ? (list.Count - normalPart.Count) : lowPart.Count;
            if (current == "" && settings.CollapseAuxiliaryApps)
            {
                pendingRenderApps.RemoveAll(a => !a.LowValue && IsAuxiliaryRole(a));
                pendingNormalCount = pendingActiveCount;
            }
            pendingCollapseLow = collapseLow;
            pendingQuery = q;
            pendingSearching = searching;
            renderedApps = new List<AppItem>();

            // 重渲染会丢弃旧的 Border 对象，选中状态必须一起清掉（否则 selectedTile 指向已移除的控件）
            selectedTile = null;
            selectedIndex = -1;

            tileRenderPending = pendingRenderApps.Count > 0;
            if (pendingRenderApps.Count == 0)
            {
                CompleteTileRendering(generation, list.Count, contentGeneration);
                return;
            }

            // 第一批立即创建，让用户在本次 Dispatcher 回调结束时就看到真实内容；
            // 余下批次排到 Background，窗口绘制和输入都可以插队。
            RenderTileBatch(generation, list.Count, contentGeneration);
        }


        const string TileSectionActiveTag = "app-role-active";
        const string TileSectionAuxiliaryTag = "app-role-auxiliary";
        int pendingActiveCount;
        int pendingAuxiliaryCount;

        bool IsAuxiliaryRole(AppItem app)
        {
            // 旧辅助区域已经合并进低价值，保留接缝兼容已有拖动代码。
            return false;
        }

        int CountActiveRole(List<AppItem> list)
        {
            int count = 0;
            foreach (AppItem app in list) if (!app.LowValue && !IsAuxiliaryRole(app)) count++;
            return count;
        }

        void RegroupAllSections(List<AppItem> list)
        {
            List<AppItem> active = new List<AppItem>();
            List<AppItem> auxiliary = new List<AppItem>();
            List<AppItem> low = new List<AppItem>();
            foreach (AppItem app in list)
            {
                if (app == null || app.Hidden || app.ExcludedEntry || app.Role == AppRole.Computer) continue;
                if (app.LowValue) low.Add(app);
                else if (IsAuxiliaryRole(app)) auxiliary.Add(app);
                else active.Add(app);
            }
            // 在各角色内集中普通入口和商店入口；稳定分区保留组内人工顺序。
            GroupStoreApps(active);
            GroupStoreApps(auxiliary);
            GroupStoreApps(low);
            list.Clear();
            list.AddRange(active);
            list.AddRange(auxiliary);
            list.AddRange(low);
        }

        static bool HasStoreLabel(AppItem app)
        {
            return app != null && app.Source == AppPaths.SourceStore && !string.IsNullOrEmpty(app.Name);
        }

        static void GroupStoreApps(List<AppItem> list)
        {
            List<AppItem> ordinary = new List<AppItem>();
            List<AppItem> store = new List<AppItem>();
            foreach (AppItem app in list)
                if (HasStoreLabel(app)) store.Add(app); else ordinary.Add(app);
            list.Clear();
            list.AddRange(ordinary);
            list.AddRange(store);
        }

        Border MakeRoleSectionHeader(bool auxiliary, int count)
        {
            string title = auxiliary ? "低价值" : "用户用的";
            Border header = MakeCommonSectionHeader(title, count,
                auxiliary ? "配套配置与维护入口" : "正常使用的应用；新应用默认在这里，可拖动排序");
            header.Tag = auxiliary ? TileSectionAuxiliaryTag : TileSectionActiveTag;
            if (auxiliary)
            {
                Grid row = header.Child as Grid;
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Button toggle = MakeDeskToolButton(settings.CollapseAuxiliaryApps ? "展开" : "收起",
                    "只折叠辅助配套，不改变低价值或隐藏状态", delegate
                    {
                        settings.CollapseAuxiliaryApps = !settings.CollapseAuxiliaryApps;
                        settings.Save();
                        if (!TryRefreshCurrentTilesInPlace()) RenderApps();
                    });
                Grid.SetColumn(toggle, 2);
                row.Children.Add(toggle);
            }
            return header;
        }

        void SetAppRole(AppItem app, bool auxiliary)
        {
            SetClassification(app, auxiliary ? AppRole.LowValue : AppRole.User);
        }


        void RenderTileBatch(int generation, int originalListCount, int contentGeneration)
        {
            if (panelClosed || generation != tileRenderGeneration || pendingRenderApps == null) return;

            int start = renderedApps.Count;
            if (current == "" && !pendingSearching)
            {
                if (start == 0 && pendingActiveCount > 0)
                    tilePanel.Children.Add(MakeRoleSectionHeader(false, pendingActiveCount));
                if (start == pendingActiveCount && pendingAuxiliaryCount > 0)
                    tilePanel.Children.Add(MakeRoleSectionHeader(true, pendingAuxiliaryCount));
            }
            if (showingCommon && start == 0)
                tilePanel.Children.Add(MakeCommonListHeader(originalListCount));
            if (start == pendingNormalCount && pendingLowCount > 0 && !pendingCollapseLow)
                tilePanel.Children.Add(MakeLowValueSectionHeader(pendingLowCount));

            int end = Math.Min(pendingRenderApps.Count, start + TileRenderBatchSize);
            if (current == "" && start < pendingActiveCount && end > pendingActiveCount)
                end = pendingActiveCount;
            // 不跨越分段点：标题必须在正常段之后、第一块低价值磁贴之前。
            if (start < pendingNormalCount && end > pendingNormalCount) end = pendingNormalCount;

            for (int i = start; i < end; i++)
                AddTileAt(pendingRenderApps[i], generation);

            if (end >= pendingRenderApps.Count)
            {
                CompleteTileRendering(generation, originalListCount, contentGeneration);
                return;
            }

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(delegate { RenderTileBatch(generation, originalListCount, contentGeneration); }));
        }


        Border MakeCommonSectionHeader(string title, int count, string hint)
        {
            Border b = new Border();
            // 拖动排序改为复用现有磁贴原地换位后，不能再靠 RenderApps 重建分组。
            // 给标题一个稳定标识，原地重排时把同一个标题控件放回正确边界。
            b.Margin = new Thickness(2, renderedApps.Count == 0 ? Ui.S.Xs : Ui.S.Md, 2, Ui.S.Xs);
            b.Padding = new Thickness(0, 0, 0, Ui.S.Xs);
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(0, 0, 0, 1);
            b.Background = Brushes.Transparent;
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            TextBlock name = Ui.T(title + " " + count + " 项", 11, Ui.Fg, FontWeights.SemiBold);
            Grid.SetColumn(name, 0);
            row.Children.Add(name);
            TextBlock detail = Ui.T(hint, 10.5, Ui.Sub);
            detail.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
            detail.VerticalAlignment = VerticalAlignment.Center;
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            detail.ToolTip = hint;
            Grid.SetColumn(detail, 1);
            row.Children.Add(detail);
            b.Child = row;
            AutomationProperties.SetName(b, title + "，" + count + " 项。" + hint);
            return b;
        }

        bool CommonManualSort()
        {
            List<string> order;
            return customOrders.TryGetValue("常用", out order) && order != null && order.Count > 0;
        }

        Border MakeCommonListHeader(int count)
        {
            string mode = CommonManualSort() ? "手动排序" : "自动排序";
            string hint = mode + " · 面板启动记录 · 3 天未用退出本页";
            Border header = MakeCommonSectionHeader("常用", count, hint);
            header.Tag = TileSectionCommonTag;
            Grid row = header.Child as Grid;
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Button reset = MakeDeskToolButton("恢复自动排序", "只恢复常用排序，不删除启动记录，也不影响全部页", RestoreCommonAutoSort);
            reset.IsEnabled = CommonManualSort();
            reset.Opacity = reset.IsEnabled ? 1 : 0.55;
            Grid.SetColumn(reset, 2);
            row.Children.Add(reset);
            return header;
        }

        void UpdateCommonListHeader()
        {
            if (!showingCommon || tilePanel == null) return;
            for (int i = 0; i < tilePanel.Children.Count; i++)
            {
                Border header = tilePanel.Children[i] as Border;
                if (header == null || !Equals(header.Tag, TileSectionCommonTag)) continue;
                tilePanel.Children.RemoveAt(i);
                tilePanel.Children.Insert(i, MakeCommonListHeader(renderedApps.Count));
                break;
            }
        }

        void RestoreCommonAutoSort()
        {
            if (current != "常用" || !CommonManualSort()) return;
            CancelTileDrag();
            customOrders.Remove("常用");
            SaveCustomOrders();
            if (!TryRefreshCurrentTilesInPlace()) RenderApps();
            ShowToast("已恢复自动排序，启动记录已保留");
        }

        internal void TestRestoreCommonAutoSort() { RestoreCommonAutoSort(); }
        internal void TestRecordAppUsage(string path) { RecordAppUsage(path); }
        internal void TestDismissCommon(string path) { DismissRecentCommon(FindAppByPath(path)); }
        internal void TestExpireCommon(string path)
        {
            AppItem item = FindAppByPath(path);
            if (item != null) item.LastUsed = DateTime.UtcNow.Ticks - UsageStore.RecentWindowTicks;
            RenderAppsIfNeeded();
        }

        DispatcherTimer commonExpiryTimer;
        void StartCommonExpiryTimer()
        {
            if (commonExpiryTimer != null) return;
            commonExpiryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            commonExpiryTimer.Interval = TimeSpan.FromSeconds(30);
            commonExpiryTimer.Tick += delegate
            {
                if (panelClosed || !IsVisible || Mouse.LeftButton == MouseButtonState.Pressed || dragSourceApp != null) return;
                RefreshNavCountsInPlace();
                if (current == "常用" && (searchBox == null || searchBox.Text.Length == 0)) RenderAppsIfNeeded();
            };
            commonExpiryTimer.Start();
        }


        void CompleteTileRendering(int generation, int originalListCount, int contentGeneration)
        {
            if (panelClosed || generation != tileRenderGeneration) return;
            if (current == "" && settings.CollapseAuxiliaryApps && pendingAuxiliaryCount > 0
                && pendingRenderApps != null && pendingRenderApps.Count == pendingActiveCount
                && renderedApps.Count == pendingActiveCount)
                tilePanel.Children.Add(MakeRoleSectionHeader(true, pendingAuxiliaryCount));
            tileRenderPending = false;

            if (pendingLowCount > 0 && pendingCollapseLow)
                tilePanel.Children.Add(MakeLowValueCollapsedRow(pendingLowCount));

            // 空状态：只在统一搜索结果列表也没有内容时才显示。
            bool hasSystemResults = searchResultPanel != null
                && searchResultPanel.Visibility == Visibility.Visible
                && searchResultPanel.Children.Count > 0;

            if (originalListCount == 0 && !(pendingQuery.Length > 0 && hasSystemResults))
            {
                string tip, sub, glyph;
                if (pendingQuery.Length > 0)
                {
                    glyph = "\uE721";                                   // 放大镜
                    tip = "没有找到「" + pendingQuery + "」";
                    sub = "换个说法试试，或者用拼音首字母（如 wx → 微信）";
                }
                else if (current == "常用")
                {
                    // 这个分支实际上到不了（「常用」为空时上面已自动切到「全部」），
                    // 但保留它作为防御：万一将来逻辑变化，也不会显示空白页。
                    glyph = "\uE735";                                   // 星标
                    tip = "还没有常用应用";
                    sub = "从面板启动几次，常用的会自动出现在这里";
                }
                else
                {
                    glyph = "\uE7C3";                                   // 页面
                    tip = "没有可显示的应用";
                    sub = "右下角「重新扫描应用」可以重新识别";
                }

                StackPanel empty = new StackPanel();
                empty.HorizontalAlignment = HorizontalAlignment.Left;
                empty.Margin = new Thickness(Ui.S.Xl, Ui.S.Xl + Ui.S.Sm, 0, 0);

                TextBlock ico = new TextBlock();
                ico.Text = glyph;
                ico.FontFamily = Ui.Mdl;
                ico.FontSize = 22;
                ico.Foreground = Ui.Sub;
                ico.Opacity = 0.55;      // 压低存在感：是装饰，不是内容
                ico.HorizontalAlignment = HorizontalAlignment.Left;
                empty.Children.Add(ico);

                TextBlock main = Ui.T(tip, 13.5, Ui.Fg, FontWeights.SemiBold);
                main.Margin = new Thickness(0, Ui.S.Sm, 0, 0);
                empty.Children.Add(main);

                TextBlock hint = Ui.T(sub, 12, Ui.Sub);
                hint.Margin = new Thickness(0, Ui.S.Xs, 0, 0);
                hint.TextWrapping = TextWrapping.Wrap;
                hint.MaxWidth = 320;
                empty.Children.Add(hint);

                tilePanel.Children.Add(empty);
            }

            RefreshFooterCount(pendingSearching);

            // 守卫：映射表必须与 renderedApps 等长。失败只记日志、不抛 ——
            // 渲染出问题不该让面板崩掉，但要让日志里能查到。
            if (renderedTiles.Count != renderedApps.Count)
                Ui.Log("磁贴映射不一致: tiles=" + renderedTiles.Count + " apps=" + renderedApps.Count);

            // 内容重建完再恢复滚动位置（放在最后，确保布局已经反映了新的磁贴集合）
            RestoreScrollOffset();
            pendingRenderApps = null;
            MarkFirstContentReady(contentGeneration);
        }


        /// <summary>刷新底部计数与两个可点击入口，不触碰磁贴或其它界面结构。</summary>
        void RefreshFooterCount(bool searching)
        {
            if (footerCount == null || catalog == null || current == FilesCategory || IsDeskTab(current)) return;
            footerCount.ToolTip = null;

            int visible = VisibleAppCount();
            if (searching)
            {
                int n = searchResults != null ? searchResults.ShownCount : 0;
                footerCount.Text = n > 0 ? "找到 " + n + " 条结果" : "搜索中…";
                return;
            }

            int hiddenCount = HiddenSnapshot().Count;
            int lowCount = CountLowValueVisible();
            footerCount.Inlines.Clear();

            Run head = new Run("共 " + visible + " 个应用");
            head.Foreground = Ui.Sub;
            footerCount.Inlines.Add(head);

            if (hiddenCount > 0)
            {
                Run sep = new Run("，");
                sep.Foreground = Ui.Sub;
                footerCount.Inlines.Add(sep);

                Hyperlink link = new Hyperlink();
                link.Focusable = true;
                link.Inlines.Add(new Run("已隐藏 " + hiddenCount + " 个"));
                link.Foreground = Ui.AccentText;
                link.TextDecorations = TextDecorations.Underline;
                link.Cursor = System.Windows.Input.Cursors.Hand;
                link.ToolTip = "点击查看并恢复已隐藏的应用";
                AutomationProperties.SetName(link, "查看已隐藏的应用");
                AutomationProperties.SetHelpText(link, "打开隐藏管理，不启动应用");
                link.Click += delegate
                {
                    try { ShowHiddenItems(); }
                    catch (Exception ex) { Ui.LogError(ex, "打开整理与隐藏窗口"); }
                };
                footerCount.Inlines.Add(link);
            }

            if (lowCount > 0)
            {
                Run sep = new Run(hiddenCount > 0 ? "，" : "，其中 ");
                sep.Foreground = Ui.Sub;
                footerCount.Inlines.Add(sep);

                Hyperlink lowLink = new Hyperlink();
                lowLink.Focusable = true;
                lowLink.Inlines.Add(new Run(lowCount + " 个低价值"));
                lowLink.Foreground = Ui.AccentText;
                lowLink.TextDecorations = TextDecorations.Underline;
                lowLink.Cursor = System.Windows.Input.Cursors.Hand;
                lowLink.ToolTip = "点击查看低价值清单，可批量取消标记";
                AutomationProperties.SetName(lowLink, "查看低价值应用");
                AutomationProperties.SetHelpText(lowLink, "打开低价值清单，不启动应用");
                lowLink.Click += delegate
                {
                    try { ShowLowValueList(); }
                    catch (Exception ex) { Ui.LogError(ex, "打开低价值清单"); }
                };
                footerCount.Inlines.Add(lowLink);
            }
        }



        /// <summary>
        /// 建一块磁贴、放进 Children、并登记到映射表。
        ///
        /// **所有磁贴都必须走这里** —— 手动 Children.Add + renderedTiles.Add
        /// 两处一旦漏一处，索引映射就会错位（这正是要消灭的那类 bug）。
        /// </summary>
        void AddTileAt(AppItem a, int generation)
        {
            Border tile = MakeTile(a, generation);
            tilePanel.Children.Add(tile);
            // ★ 登记映射：tile 的下标 = 应用下标（与 Children 的位置无关）
            tileAppIndex[tile] = renderedTiles.Count;
            renderedTiles.Add(tile);
            // 只把已经存在对应 Border 的条目暴露给键盘、拖动与测试路径，
            // 因而分批期间也始终维持 tiles == apps 的不变式。
            renderedApps.Add(a);
            Grid box = FindTileIconBox(tile);
            TileIconState state;
            if (box != null && tileIconStates.TryGetValue(box, out state))
                tileIconStatesByTile[tile] = state;
            ScheduleViewportIconRefresh();
        }

        static Grid FindTileIconBox(Border tile)
        {
            try
            {
                StackPanel stack = tile == null ? null : tile.Child as StackPanel;
                if (stack == null) return null;
                foreach (UIElement child in stack.Children)
                {
                    Grid box = child as Grid;
                    if (box != null) return box;
                }
            }
            catch { }
            return null;
        }


        /// <summary>
        /// 用当前 Border/Image 控件协调「常用 / 全部」的最新列表。
        ///
        /// 右键操作通常只影响一个应用；全量 RenderApps 会销毁所有磁贴，造成
        /// 整页闪动、滚动/焦点丢失和重复图标请求。这里复用仍存在的磁贴，只为
        /// 真正新增的应用创建控件，并只移除已经不该显示的控件。
        /// </summary>
        bool TryRefreshCurrentTilesInPlace()
        {
            try
            {
                if (tilePanel == null || catalog == null) return false;
                if (tileRenderPending || pendingRenderApps != null) return false;
                if (renderedApps == null || renderedApps.Count != renderedTiles.Count) return false;
                if (searchBox != null && searchBox.Text.Trim().Length > 0) return false;

                bool common = current == "常用";
                if (!common && current != "") return false;

                List<AppItem> ordered = common ? CommonApps() : AllAppsSorted();
                // 「常用」为空时生产渲染会切回「全部」；这是页面切换，不属于
                // 单条磁贴更新，交回完整渲染路径处理。
                if (ordered.Count == 0 && common) return false;

                ApplyCustomOrder(ordered);
                if (!common) RegroupAllSections(ordered);

                bool collapseLow = !common && settings != null && settings.CollapseLowValue;
                List<AppItem> normalPart;
                List<AppItem> lowPart;
                if (common)
                {
                    normalPart = new List<AppItem>(ordered);
                    lowPart = new List<AppItem>();
                }
                else
                {
                    SplitLowValueSegment(ordered, collapseLow, out normalPart, out lowPart);
                }

                List<AppItem> desired = new List<AppItem>(normalPart);
                desired.AddRange(lowPart);
                int lowSectionCount = collapseLow
                    ? ordered.Count - normalPart.Count
                    : lowPart.Count;
                int auxiliaryCount = common ? 0 : normalPart.Count - CountActiveRole(normalPart);
                if (!common && settings.CollapseAuxiliaryApps)
                {
                    normalPart.RemoveAll(a => IsAuxiliaryRole(a));
                    desired = new List<AppItem>(normalPart);
                    desired.AddRange(lowPart);
                }

                Dictionary<AppItem, Border> oldTiles = new Dictionary<AppItem, Border>();
                for (int i = 0; i < renderedApps.Count; i++)
                {
                    AppItem app = renderedApps[i];
                    Border tile = renderedTiles[i];
                    if (app == null || tile == null || oldTiles.ContainsKey(app)) return false;
                    oldTiles[app] = tile;
                }

                List<Border> desiredTiles = new List<Border>();
                HashSet<Border> kept = new HashSet<Border>();
                foreach (AppItem app in desired)
                {
                    Border tile;
                    if (!oldTiles.TryGetValue(app, out tile))
                    {
                        tile = MakeTile(app, tileRenderGeneration);
                        Grid box = FindTileIconBox(tile);
                        TileIconState iconState;
                        if (box != null && tileIconStates.TryGetValue(box, out iconState))
                            tileIconStatesByTile[tile] = iconState;
                    }
                    UpdateTilePresentation(tile, app);
                    desiredTiles.Add(tile);
                    kept.Add(tile);
                }

                foreach (Border oldTile in renderedTiles)
                {
                    if (kept.Contains(oldTile)) continue;
                    TileIconState state;
                    if (tileIconStatesByTile.TryGetValue(oldTile, out state))
                    {
                        tileIconStatesByTile.Remove(oldTile);
                        if (state != null)
                        {
                            state.WantsImage = false;
                            if (state.Box != null) tileIconStates.Remove(state.Box);
                        }
                    }
                    if (ReferenceEquals(oldTile, selectedTile)) selectedTile = null;
                }

                double scrollBefore = contentScroll == null ? 0 : contentScroll.VerticalOffset;
                showingCommon = common;
                tilePanel.Children.Clear();

                int activeCount = common ? 0 : CountActiveRole(normalPart);
                if (common) tilePanel.Children.Add(MakeCommonListHeader(desiredTiles.Count));

                for (int i = 0; i < desiredTiles.Count; i++)
                {
                    if (!common && i == 0 && activeCount > 0)
                        tilePanel.Children.Add(MakeRoleSectionHeader(false, activeCount));
                    if (!common && i == activeCount && auxiliaryCount > 0)
                        tilePanel.Children.Add(MakeRoleSectionHeader(true, auxiliaryCount));
                    if (!common && !collapseLow && lowSectionCount > 0 && i == normalPart.Count)
                        tilePanel.Children.Add(MakeLowValueSectionHeader(lowSectionCount));
                    tilePanel.Children.Add(desiredTiles[i]);
                }
                if (!common && desiredTiles.Count == activeCount && auxiliaryCount > 0)
                    tilePanel.Children.Add(MakeRoleSectionHeader(true, auxiliaryCount));
                if (!common && collapseLow && lowSectionCount > 0)
                    tilePanel.Children.Add(MakeLowValueCollapsedRow(lowSectionCount));

                renderedApps = new List<AppItem>(desired);
                renderedTiles.Clear();
                renderedTiles.AddRange(desiredTiles);
                tileAppIndex.Clear();
                for (int i = 0; i < renderedTiles.Count; i++)
                    tileAppIndex[renderedTiles[i]] = i;

                selectedIndex = selectedTile == null ? -1 : renderedTiles.IndexOf(selectedTile);
                RefreshNavCountsInPlace();
                RefreshFooterCount(false);
                renderedSig = CurrentContentSig();
                tilePanel.InvalidateMeasure();
                if (contentScroll != null)
                {
                    contentScroll.UpdateLayout();
                    contentScroll.ScrollToVerticalOffset(scrollBefore);
                }
                ScheduleViewportIconRefresh();
                return true;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "就地刷新应用磁贴");
                return false;
            }
        }


        static void UpdateTilePresentation(Border tile, AppItem app)
        {
            if (tile == null) return;
            tile.ToolTip = TileToolTipOf(app);
            bool low = app != null && app.LowValue;
            tile.Opacity = low && !Ui.HasAutomaticPalette ? 0.45 : 1.0;
            Grid box = FindTileIconBox(tile);
            if (box != null) box.Opacity = low && Ui.HasAutomaticPalette ? 0.45 : 1.0;
        }


        /// <summary>
        /// 把已排序好的列表切成「正常段 + 低价值段」。
        ///
        /// 逐项按真实标记稳定分区，保留两区内部人工顺序。
        /// 新应用即使位于旧人工排序末尾，也必须留在正常段。
        ///
        /// collapseLow = true 时低价值段**不渲染磁贴**（lowPart 留空、计数另算），
        /// 只保留一行可点击的展开行。
        /// </summary>
        static void SplitLowValueSegment(List<AppItem> list, bool collapseLow,
            out List<AppItem> normalPart, out List<AppItem> lowPart)
        {
            normalPart = new List<AppItem>();
            lowPart = new List<AppItem>();
            if (list == null) return;

            foreach (AppItem app in list)
            {
                if (app == null || app.Hidden || app.ExcludedEntry || app.Role == AppRole.Computer) continue;
                if (!app.LowValue) normalPart.Add(app);
                else if (!collapseLow) lowPart.Add(app);
            }
        }


        /// <summary>
        /// 低价值段的节标题：「低价值 N 项」+ 说明。
        ///
        /// 这是**普通 Border**，只 Children.Add、不进 renderedTiles ——
        /// 所以它既不可拖拽、也不是落点（DropTile 只遍历 renderedTiles），
        /// 键盘导航也跳过它（MoveSelection 只认 renderedApps.Count）。
        ///
        /// 占满一整行（Width 绑定到 WrapPanel 宽度）：
        /// 半行宽的话，WrapPanel 会把后面的磁贴挤到同一行的右侧，看起来像"标题旁边挂了个应用"。
        /// </summary>
        Border MakeLowValueSectionHeader(int lowCount)
        {
            Border b = new Border();
            b.Tag = TileSectionLowValueTag;
            b.Margin = new Thickness(2, Ui.S.Md, 2, Ui.S.Xs);
            b.Padding = new Thickness(0, 0, 0, Ui.S.Xs);
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(0, 0, 0, 1);   // 下边一条细线，像分组分隔
            b.Background = Brushes.Transparent;
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            g.ColumnDefinitions[1].Width = GridLength.Auto;

            TextBlock title = Ui.T("低价值 " + lowCount + " 项", 11, Ui.Sub, FontWeights.SemiBold);
            title.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(title);

            // 右侧的「收起」入口：与底部统计的低价值链接打开同一个清单窗口之外，
            // 这里只做收起动作（列表里能批量取消标记，语义不同，不混在一起）
            Button toggle = MakeDeskToolButton("收起", "收起低价值段，只留一行入口", delegate { SetCollapseLowValue(true); });
            AutomationProperties.SetName(toggle, "收起低价值");
            Grid.SetColumn(toggle, 1);
            g.Children.Add(toggle);

            b.Child = g;
            return b;
        }


        /// <summary>
        /// 收起态的单行入口：「低价值 N 项（点击展开）」。
        ///
        /// 同样只 Children.Add、不进 renderedTiles —— 它不是磁贴，不参与拖拽与键盘导航。
        /// 键盘导航跳过它是合理的：收起时那些项本来就不在 renderedApps 里。
        /// </summary>
        Button MakeLowValueCollapsedRow(int lowCount)
        {
            Button b = new Button();
            b.Tag = TileSectionLowValueCollapsedTag;
            b.Margin = new Thickness(2, Ui.S.Md, 2, Ui.S.Xs);
            b.Padding = new Thickness(Ui.S.Sm, Ui.S.Sm, Ui.S.Sm, Ui.S.Sm);
            b.Template = UiButtons.Template(Ui.R.Sm);
            b.Background = Ui.SearchBg;
            b.Cursor = System.Windows.Input.Cursors.Hand;
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(1);
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;

            TextBlock glyph = Ui.T("\uE70D", 10.5, Ui.AccentText);   // 向下箭头
            glyph.FontFamily = Ui.Mdl;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            glyph.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
            sp.Children.Add(glyph);

            TextBlock label = Ui.T("低价值 " + lowCount + " 项（点击展开）", 11, Ui.AccentText);
            label.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(label);

            b.Content = sp;
            b.ToolTip = "展开低价值段（卸载器、帮助文档、许可协议等）";
            AutomationProperties.SetName(b, "展开低价值应用，共 " + lowCount + " 项");
            b.Click += delegate
            {
                try { SetCollapseLowValue(false); }
                catch (Exception ex) { Ui.LogError(ex, "展开低价值段"); }
            };
            return b;
        }


        /// <summary>
        /// 设置低价值段折叠状态并写回 settings。
        ///
        /// 展开/收起是纯 UI 状态，但要**持久化** —— 否则切到「常用」再切回来
        /// （每次切换都会全量重建）折叠状态就丢了。
        /// </summary>
        void SetCollapseLowValue(bool collapsed)
        {
            try
            {
                if (settings == null) return;
                if (settings.CollapseLowValue == collapsed) return;

                settings.CollapseLowValue = collapsed;
                settings.Save();

                // 正常磁贴保持原对象；只增删低价值段及其标题。
                if (!TryRefreshCurrentTilesInPlace()) RenderApps();
            }
            catch (Exception ex) { Ui.LogError(ex, "切换低价值段折叠"); }
        }


        /// <summary>
        /// 「全部」页底部统计：共 N 个应用，其中 M 个低价值。
        ///
        /// 只在「全部」页显示低价值数量 —— 「常用」页不受沉底影响，不显示。
        /// </summary>
        string BuildAllStats()
        {
            try
            {
                int total = 0;
                foreach (AppItem a in catalog.Apps)
                {
                    if (a == null || a.Hidden || a.ExcludedEntry || a.Role == AppRole.Computer) continue;
                    total++;
                }
                int low = CountLowValueVisible();

                if (low > 0)
                    return "共 " + total + " 个应用，其中 " + low + " 个低价值";
                return "共 " + total + " 个应用";
            }
            catch { return ""; }
        }


        /// <summary>
        /// 可见（未隐藏）的低价值应用数。
        ///
        /// 底部统计和低价值清单窗口共用这一个计数 —— 否则「其中 9 个低价值」
        /// 点开却是 8 个，数字对不上会直接让人不信这个功能。
        /// </summary>
        int CountLowValueVisible()
        {
            try
            {
                int low = 0;
                if (catalog == null) return 0;
                foreach (AppItem a in catalog.Apps)
                {
                    if (a == null || a.Hidden || a.ExcludedEntry || a.Role == AppRole.Computer) continue;
                    if (a.LowValue) low++;
                }
                return low;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 「全部」的可见应用：低价值整体沉底，各段按逐字拼音排序；
        /// 排序键相同时再用路径兜底，保证刷新前后顺序稳定。
        /// </summary>
        List<AppItem> AllAppsSorted()
        {
            List<AppItem> list = new List<AppItem>();
            try
            {
                // ---- 1. 收集可见应用 + 按目标路径去重 ----
                //
                // 「目标路径相同的快捷方式只保留一个」：开始菜单里同一个程序
                // 常同时存在「用户」和「公共」两份 .lnk，解析后 Path 相同。
                HashSet<string> seenPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (AppItem a in catalog.Apps)
                {
                    if (a == null || a.Hidden || a.ExcludedEntry || a.Role == AppRole.Computer) continue;

                    string key = a.Path ?? "";
                    if (key.Length > 0 && !seenPath.Add(key)) continue;   // 已见过同路径 → 跳过

                    list.Add(a);
                }

                // ---- 2. 标记低价值（连原因一起写进 AppItem，供 ToolTip / 清单窗口用）----
                foreach (AppItem a in list)
                {
                    LowValueVerdict v = ExplainLowValue(a);
                    a.LowValue = v.IsLowValue;
                    a.LowValueReason = v.Reason;
                    a.LowValueDetail = v.Detail ?? "";
                }

                // ---- 3. 排序：低价值整体沉底，各自内部保持字典序 ----
                list.Sort(delegate(AppItem x, AppItem y)
                {
                    // 低价值在后（这一条优先于名称排序）
                    if (x.LowValue != y.LowValue) return x.LowValue ? 1 : -1;

                    int sourceOrder = HasStoreLabel(x).CompareTo(HasStoreLabel(y));
                    if (sourceOrder != 0) return sourceOrder;

                    int c = string.Compare(
                        Pinyin.SortKey(x.Name), Pinyin.SortKey(y.Name), StringComparison.Ordinal);
                    if (c != 0) return c;
                    // 排序键完全相同时用路径兜底，保证顺序稳定（不会每次刷新都跳）
                    return string.Compare(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);
                });
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "排序全部应用");
            }
            return list;
        }


        /// <summary>只要布尔结论时的薄包装（保持既有调用点不变）。</summary>
        bool IsLowValueApp(AppItem a)
        {
            return ExplainLowValue(a).IsLowValue;
        }


        /// <summary>测试用：取当前真实渲染列表第一项的路径。</summary>
        internal string TestFirstRenderedPath()
        {
            try
            {
                return renderedApps != null && renderedApps.Count > 0
                    ? renderedApps[0].Path : null;
            }
            catch { return null; }
        }


        /// <summary>测试用：「全部」页的低价值条目数。</summary>
        internal int TestLowValueCount()
        {
            try
            {
                List<AppItem> list = AllAppsSorted();
                int n = 0;
                foreach (AppItem a in list) if (a != null && a.LowValue) n++;
                return n;
            }
            catch { return 0; }
        }


        /// <summary>测试用：全部页排序后，低价值是否全部排在末尾。</summary>
        internal bool TestLowValueAtEnd()
        {
            try
            {
                List<AppItem> list = renderedApps;
                bool seenLow = false;
                foreach (AppItem a in list)
                {
                    if (a == null) continue;
                    if (a.LowValue) { seenLow = true; }
                    else if (seenLow) return false;   // 低价值后面又出现正常项 → 没沉底
                }
                return true;
            }
            catch { return false; }
        }


        /// <summary>测试用：底部统计文字。</summary>
        internal string TestAllStatsText()
        {
            try { return BuildAllStats(); }
            catch { return ""; }
        }


        /// <summary>测试用：当前磁贴映射表的长度（应恒等于 renderedApps.Count）。</summary>
        internal int TestTileCount()
        {
            try { return renderedTiles.Count; }
            catch { return -1; }
        }


        /// <summary>测试用：当前分批磁贴是否已完整收尾。</summary>
        internal bool TestDeferredRenderComplete()
        {
            try
            {
                return !tileRenderPending && pendingRenderApps == null
                    && !string.IsNullOrEmpty(renderedSig);
            }
            catch { return false; }
        }


        /// <summary>测试用：分批磁贴是否仍有后续工作排队。</summary>
        internal bool TestDeferredRenderPending()
        {
            try { return tileRenderPending && pendingRenderApps != null; }
            catch { return false; }
        }


        /// <summary>测试用：设置低价值段折叠状态并重渲染（走生产路径 SetCollapseLowValue）。</summary>
        internal void TestSetCollapseLowValue(bool collapsed)
        {
            try { SetCollapseLowValue(collapsed); }
            catch (Exception ex) { Ui.LogError(ex, "测试：切换低价值段折叠"); }
        }


        /// <summary>测试用：当前低价值段是否收起。</summary>
        internal bool TestCollapseLowValue()
        {
            try { return settings != null && settings.CollapseLowValue; }
            catch { return false; }
        }


        /// <summary>
        /// 测试用：磁贴映射是否一致 —— tiles == apps，且每个 tile 的 appIndex 正确、
        /// 且每个 tile 确实是 tilePanel 的子元素。
        ///
        /// 这条断言就是「Children 索引映射陷阱」的回归保护：将来谁往 Children 里
        /// 插非磁贴元素（节标题之类），只要映射表没跟着维护，这里立刻报错。
        /// </summary>
        internal bool TestTileMapConsistent()
        {
            try
            {
                if (renderedApps == null) return false;
                if (renderedTiles.Count != renderedApps.Count) return false;
                if (tileAppIndex.Count != renderedTiles.Count) return false;

                for (int i = 0; i < renderedTiles.Count; i++)
                {
                    Border t = renderedTiles[i];
                    if (t == null) return false;
                    int mapped;
                    if (!tileAppIndex.TryGetValue(t, out mapped)) return false;
                    if (mapped != i) return false;
                    if (tilePanel == null || !tilePanel.Children.Contains(t)) return false;
                }
                return true;
            }
            catch { return false; }
        }


        internal int TestMountedTileIconCount()
        {
            try
            {
                int count = 0;
                foreach (TileIconState state in tileIconStates.Values)
                    if (state != null && state.Visual is Image) count++;
                return count;
            }
            catch { return -1; }
        }


        internal int TestWantedTileIconCount()
        {
            try
            {
                int count = 0;
                foreach (TileIconState state in tileIconStates.Values)
                    if (state != null && state.WantsImage) count++;
                return count;
            }
            catch { return -1; }
        }


        internal void TestRefreshViewportTileIcons()
        {
            RefreshViewportTileIcons();
        }


        /// <summary>测试用：设置/取消低价值标记。</summary>
        internal void TestSetLowValue(AppItem a, bool low)
        {
            try { SetLowValue(a, low); }
            catch { }
        }


        /// <summary>测试用：取当前列表中第一个正常应用路径。</summary>
        internal string TestFirstNormalRenderedPath()
        {
            try
            {
                if (renderedApps == null) return null;
                foreach (AppItem a in renderedApps)
                    if (a != null && !a.LowValue && !string.IsNullOrEmpty(a.Path)) return a.Path;
            }
            catch { }
            return null;
        }


        internal int TestRenderedIndexOfPath(string path)
        {
            try
            {
                if (renderedApps == null || string.IsNullOrEmpty(path)) return -1;
                for (int i = 0; i < renderedApps.Count; i++)
                    if (renderedApps[i] != null &&
                        string.Equals(renderedApps[i].Path, path, StringComparison.OrdinalIgnoreCase))
                        return i;
            }
            catch { }
            return -1;
        }


        /// <summary>测试用：按路径走真实低价值标记入口。</summary>
        internal bool TestSetLowValueByPath(string path, bool low)
        {
            try
            {
                AppItem a = FindAppByPath(path);
                if (a == null) return false;
                SetLowValue(a, low);
                return a.LowValue == low;
            }
            catch { return false; }
        }

        internal void TestRole(string path, bool auxiliary) { SetAppRole(FindAppByPath(path), auxiliary); }
        internal bool TestAuxiliary(string path) { return IsAuxiliaryRole(FindAppByPath(path)); }
        internal void TestCollapseAuxiliary(bool collapsed)
        {
            settings.CollapseAuxiliaryApps = collapsed;
            if (!TryRefreshCurrentTilesInPlace()) RenderApps();
        }
        internal void TestOrder(List<string> paths)
        {
            customOrders[current] = paths;
            if (!TryRefreshCurrentTilesInPlace()) RenderApps();
        }


        /// <summary>测试用：磁贴区是否可见。</summary>
        internal bool TestTilePanelVisible()
        {
            try
            {
                return tilePanel != null && tilePanel.Visibility == Visibility.Visible;
            }
            catch { return false; }
        }



        static int RenderCompare(AppItem a, AppItem b)
        {
            int c = b.Usage.CompareTo(a.Usage);
            if (c != 0) return c;
            c = b.LastUsed.CompareTo(a.LastUsed);
            if (c != 0) return c;
            return string.Compare(a.Name, b.Name, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), System.Globalization.CompareOptions.None);
        }


        /// <summary>
        /// 磁贴上显示的名字。
        ///
        /// **商店应用加 `·商店` 后缀**，用来区分同名的桌面版和商店版
        /// （实测有 28 组同名并存，如 `Google Chrome` 桌面版 + 商店版）。
        ///
        /// 为什么只改显示名、不改 AppItem.Name：
        ///   · Name 参与去重（名称+发布者匹配）、usage.json 记录、搜索匹配
        ///   · 改了会破坏已有数据的一致性（用户明确要求"不修改原始 Name"）
        ///   · 显示是纯视觉层的事，隔离在这里最安全
        ///
        /// 后缀用中点 `·` 而不是括号：中点在视觉上更轻，
        /// 且不会让磁贴名字变得太长（磁贴宽度只有 88px）。
        /// </summary>
        static string DisplayNameOf(AppItem a)
        {
            try
            {
                if (a == null) return "";
                string name = a.Name ?? "";
                if (HasStoreLabel(a))
                    return name + " ·商店";
                return name;
            }
            catch { return a == null ? "" : (a.Name ?? ""); }
        }


        /// <summary>
        /// 磁贴 ToolTip：正常应用就是显示名；低价值应用再补一行「低价值：原因」。
        ///
        /// 为什么要这一行：磁贴只是变淡（Opacity 0.45），用户看不出**为什么**，
        /// 唯一的线索是右键菜单。把原因写在悬停提示里，不用点开任何东西就能知道
        /// 是"名称命中关键词"还是"这个快捷方式已经失效了"——后者用户可能需要处理。
        /// </summary>
        static string TileToolTipOf(AppItem a)
        {
            try
            {
                // 磁贴改成双击启动后，单看界面看不出这个变化，
                // 单击没反应会像坏了 —— 所以在提示里写明。
                const string hint = "\n双击启动";

                string name = DisplayNameOf(a);
                if (a == null || !a.LowValue) return name + hint;

                string detail = a.LowValueDetail;
                if (string.IsNullOrWhiteSpace(detail))
                {
                    // 兜底：老数据/手动标记没写 Detail 时给一句能看的说明
                    detail = a.LowValueReason == LowValueReason.ManualMarked
                        ? "已手动标记"
                        : "被规则判为低价值";
                }
                return name + "\n低价值：" + detail + hint;
            }
            catch { return DisplayNameOf(a); }
        }


        Border MakeTile(AppItem a, int generation)
        {
            Border b = new Border();
            b.Width = TileWidthPx;
            b.Height = TileHeightPx;
            b.CornerRadius = new CornerRadius(Ui.R.Sm);
            b.Margin = new Thickness(2);
            b.Background = Brushes.Transparent;
            b.BorderBrush = Brushes.Transparent;
            b.BorderThickness = new Thickness(2);
            b.Cursor = Cursors.Hand;
            b.ToolTip = TileToolTipOf(a);

            // ★ 低价值条目变淡（沉底排序在 AllAppsSorted 里做）
            // 0.45 足够看出"这个不太重要"，又不至于看不清名字
            if (a != null && a.LowValue && !Ui.HasAutomaticPalette) b.Opacity = 0.45;

            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;

            // 图标容器：悬停放大作用在它上面，而不是磁贴本身。
            // 为什么：磁贴放大（96x90 -> 更大）会让 WrapPanel 重新排版，
            // 鼠标附近的磁贴会跳位、闪烁；图标用 RenderTransform 放大则完全不碰布局。
            // RenderTransformOrigin 设成中心，所以放大会从正中向四周扩。
            //
            // 用 TransformGroup 组合「缩放 + 位移」两个变换：
            //   · 缩放 → 悬停时图标变大（1.0 → 1.25）
            //   · 位移 → 悬停上浮（0 → -4px）+ 点击弹跳（0 → -6px → 0）
            // 两者独立，可以同时进行；若只用一个 ScaleTransform 就没法做上浮。
            ScaleTransform iconScale = new ScaleTransform(1.0, 1.0);
            TranslateTransform iconLift = new TranslateTransform(0, 0);
            TransformGroup iconTrans = new TransformGroup();
            iconTrans.Children.Add(iconScale);
            iconTrans.Children.Add(iconLift);

            // 所有磁贴先用轻量首字占位。即使进程缓存已经有图，也只在磁贴
            // 进入视口附近后才把 ImageSource 挂到可视树，避免缓存淘汰后仍被
            // 上百个离屏 Image 控件强引用。
            FrameworkElement iconVisual = MakeTileIconPlaceholder(a);

            iconVisual.RenderTransformOrigin = new Point(0.5, 0.5);
            iconVisual.RenderTransform = iconTrans;

            // 图标外面套一层固定尺寸 Grid，保证占位图与真实图标切换时不改布局。
            Grid iconBox = new Grid();
            iconBox.Width = IconSizePx;
            iconBox.Height = IconSizePx;
            iconBox.HorizontalAlignment = HorizontalAlignment.Center;
            iconBox.Margin = new Thickness(0, 0, 0, Ui.S.Xs);
            iconBox.Children.Add(iconVisual);

            tileIconStates[iconBox] = new TileIconState
            {
                App = a,
                Box = iconBox,
                Visual = iconVisual,
                Transform = iconTrans,
                Generation = generation
            };

            sp.Children.Add(iconBox);
            iconBox.Opacity = a != null && a.LowValue && Ui.HasAutomaticPalette ? 0.45 : 1.0;

            TextBlock name = new TextBlock();
            name.Text = DisplayNameOf(a);
            name.FontFamily = Ui.YaHei;
            name.FontSize = 11.5;
            name.Foreground = Ui.Fg;
            name.TextWrapping = TextWrapping.Wrap;
            name.TextAlignment = TextAlignment.Center;
            name.LineHeight = 15;
            name.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxWidth = 88;
            name.MaxHeight = 30;

            Border nameClip = new Border();
            nameClip.MaxHeight = 30;
            nameClip.ClipToBounds = true;
            nameClip.Child = name;
            nameClip.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(nameClip);

            b.Child = sp;
            Point downPt = new Point();
            bool tileDrag = false;
            // 本次按下的连击数，供松手时判定「双击启动」。
            // 在 Down 里取：Down 一定先于 Up 到达，用它更稳。
            int downClickCount = 0;
            b.MouseEnter += delegate
            {
                if (!ReferenceEquals(b, selectedTile)) b.Background = Ui.TileHover;
                // 悬停：放大 + 上浮（参考 GlassBoard 的 Dock 悬停）。
                // 只动 RenderTransform，不触发布局重排。
                if (!Ui.ReducedMotion)
                {
                    iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, IconZoomIn);
                    iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, IconZoomIn);
                    iconLift.BeginAnimation(TranslateTransform.YProperty, IconLiftUp);
                }
            };
            b.MouseLeave += delegate
            {
                if (!ReferenceEquals(b, selectedTile)) b.Background = Brushes.Transparent;
                if (!Ui.ReducedMotion)
                {
                    iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, IconZoomOut);
                    iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, IconZoomOut);
                    iconLift.BeginAnimation(TranslateTransform.YProperty, IconLiftDown);
                }
            };
            b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                CancelTileDrag();
                downPt = e.GetPosition(tilePanel);
                tileDrag = false;
                downClickCount = e.ClickCount;
                // 按下态：补全"静止/悬停/按下"三态。
                // 点一个启动稍慢的应用时，用户能看到"按下去了"，
                // 不会怀疑是没点中。
                if (!ReferenceEquals(b, selectedTile)) b.Background = Ui.TilePressed;

                // 高频操作只给短促按压反馈，不用弹跳拖慢体感。
                if (!Ui.ReducedMotion)
                    iconLift.BeginAnimation(TranslateTransform.YProperty, IconPress);

                // 鼠标点击磁贴时，键盘选中跟随到它，方向键从当前位置继续
                SelectTile(renderedApps.IndexOf(a), false);
                try { b.CaptureMouse(); } catch { }
            };
            b.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (e.LeftButton != MouseButtonState.Pressed || !b.IsMouseCaptured) return;
                Point p = e.GetPosition(tilePanel);

                // 使用 Windows 的系统拖动阈值，尊重用户鼠标设置并避免轻微手抖误拖。
                if (!tileDrag &&
                    (Math.Abs(p.X - downPt.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                     Math.Abs(p.Y - downPt.Y) >= SystemParameters.MinimumVerticalDragDistance))
                {
                    tileDrag = true;
                    dragSourceApp = a;
                    dragSourceTile = b;
                    b.Opacity = 0.25;          // 原位磁贴变淡，当"占位幽灵"
                    b.Cursor = Cursors.SizeAll;
                    // 拖动时取消悬停放大，否则原位磁贴又大又淡，很怪
                    iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, IconZoomOut);
                    iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, IconZoomOut);
                    iconLift.BeginAnimation(TranslateTransform.YProperty, IconLiftDown);

                    // 抓取点：鼠标相对磁贴左上角的偏移，
                    // 这样浮层不会在开始拖动瞬间"跳"到鼠标中心
                    Point local = tilePanel.TranslatePoint(downPt, b);
                    dragGrabOffset = local;

                    BeginDragVisual();
                    ShowDragGhost(a, p);
                    Ui.Log("DragStart " + a.Name);
                }

                if (tileDrag)
                {
                    // 浮层跟随鼠标（每帧只改 Transform，不碰布局）
                    MoveDragGhost(p);
                    // 落点计算 + 插入线；周围磁贴保持静止，避免跨行批量动画卡顿
                    UpdateDragPreview(p);
                }
            };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                bool releasedDrag = tileDrag;
                if (releasedDrag) UpdateDragPreview(e.GetPosition(tilePanel));
                tileDrag = false;
                try { b.ReleaseMouseCapture(); } catch { }
                if (!releasedDrag) UpdateTilePresentation(b, a);
                b.Cursor = Cursors.Hand;
                // 松开：回到悬停态（鼠标还在磁贴上），而不是透明 ——
                // 否则刚点完会闪一下，像"点完就灭了"
                if (!ReferenceEquals(b, selectedTile)) b.Background = Ui.TileHover;

                // 松手后的三条出口：拖完 → 提交排序；双击 → 启动；其余 → 只选中。
                //
                // ★ 这里从「无条件 Launch」改成了「双击才 Launch」：
                //   磁贴原本一按一松就启动，用户想拖动排序时手指一按程序就开了，
                //   排序根本做不成。现在与桌面图标、资源管理器一致 ——
                //   单击只选中（键盘方向键从这里接着走），双击才启动。
                //   判定逻辑在 core 的 TileClickAction 里，可单测穷举。
                TileClickAction.Action act = TileClickAction.Resolve(releasedDrag, downClickCount);

                if (act == TileClickAction.Action.Drop)
                {
                    tileDrag = false;
                    // 先取 insertIndex，再同步提交排列并让浮层落入最终格。
                    //
                    // 第一版写反了 —— EndDragVisual 把 insertIndex 重置成 -1，
                    // CommitDrag 拿到的永远是 -1，拖拽完全不生效（实测日志
                    // "CommitDrag ... target=-1" 而 DragPreview 明明算出了 idx=2）。
                    int target = insertIndex;

                    // 收起插入标记，但**保留浮层**做一次落入动画
                    EndDragVisualKeepGhost();

                    // 松手即提交；浮层与周围磁贴同时衔接到最终排列
                    DropDragGhost(a, target);
                }
                else if (act == TileClickAction.Action.Launch)
                {
                    Launch(a);
                }
                // else：单击只保留选中态。按下时 SelectTile 已经做过了，
                // 这里不需要再做任何事 —— 也**不要**去清 selectedTile，
                // 否则单击完键盘方向键就没有起点了。
            };
            b.LostMouseCapture += delegate
            {
                if (!tileDrag) return;
                tileDrag = false;
                downClickCount = 0;
                CancelTileDrag();
            };
            b.MouseRightButtonDown += delegate { SelectTile(renderedApps.IndexOf(a), false); };
            b.MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                ShowTileMenu(a, b);
            };
            return b;
        }


        FrameworkElement MakeTileIconImage(ImageSource icon)
        {
            Image img = new Image();
            img.Source = icon;
            img.Width = IconSizePx;
            img.Height = IconSizePx;
            img.HorizontalAlignment = HorizontalAlignment.Center;
            img.VerticalAlignment = VerticalAlignment.Center;
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }


        FrameworkElement MakeTileIconPlaceholder(AppItem a)
        {
            Border ph = new Border();
            ph.Width = IconSizePx;
            ph.Height = IconSizePx;
            ph.CornerRadius = new CornerRadius(Ui.R.Sm);
            ph.Background = Ui.AccentSoft;
            ph.HorizontalAlignment = HorizontalAlignment.Center;
            ph.VerticalAlignment = VerticalAlignment.Center;
            string initial = a == null || string.IsNullOrEmpty(a.Name) ? "?" : a.Name.Substring(0, 1);
            TextBlock init = Ui.T(initial, 14.5, Ui.AccentText, FontWeights.SemiBold);
            init.HorizontalAlignment = HorizontalAlignment.Center;
            init.VerticalAlignment = VerticalAlignment.Center;
            ph.Child = init;
            return ph;
        }


        void QueueTileIcon(TileIconState state)
        {
            if (state == null || state.App == null || state.RequestPending) return;
            state.RequestPending = true;
            string path = state.App.Path;
            int generation = state.Generation;
            FrameworkElement expectedVisual = state.Visual;

            AppIcons.GetAsync(path, delegate(ImageSource loaded)
            {
                if (panelClosed || generation != tileRenderGeneration) return;
                try
                {
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                        new Action(delegate
                        {
                            try
                            {
                                if (panelClosed || generation != tileRenderGeneration) return;
                                TileIconState current;
                                if (!tileIconStates.TryGetValue(state.Box, out current)) return;
                                if (!ReferenceEquals(current, state)) return;
                                state.RequestPending = false;
                                state.Unavailable = loaded == null;
                                if (!state.WantsImage || loaded == null) return;
                                if (!ReferenceEquals(state.Visual, expectedVisual)) return;
                                if (!state.Box.Children.Contains(expectedVisual)) return;

                                FrameworkElement image = MakeTileIconImage(loaded);
                                image.RenderTransformOrigin = new Point(0.5, 0.5);
                                image.RenderTransform = state.Transform;

                                int index = state.Box.Children.IndexOf(expectedVisual);
                                state.Box.Children.Remove(expectedVisual);
                                state.Box.Children.Insert(Math.Max(0, index), image);
                                state.Visual = image;
                            }
                            catch { }
                        }));
                }
                catch { }
            });
        }


        /// <summary>合并布局/滚动造成的密集请求，每个 Dispatcher 周期最多刷新一次。</summary>
        void ScheduleViewportIconRefresh()
        {
            if (viewportIconRefreshScheduled || panelClosed) return;
            try { if (viewportIconIdleTimer != null) viewportIconIdleTimer.Stop(); } catch { }
            viewportIconRefreshScheduled = true;
            try
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(delegate
                    {
                        viewportIconRefreshScheduled = false;
                        RefreshViewportTileIcons();
                    }));
            }
            catch { viewportIconRefreshScheduled = false; }
        }


        /// <summary>
        /// 滚动期间只移动现有视觉内容；停止 120ms 后才切换远近图标。
        /// 视口前后一屏已经预取，因此短暂延后不会让正常滚动露出空白。
        /// </summary>
        void ScheduleViewportIconRefreshAfterScroll()
        {
            if (panelClosed) return;
            try
            {
                if (viewportIconIdleTimer == null)
                {
                    viewportIconIdleTimer = new DispatcherTimer(
                        DispatcherPriority.Background, Dispatcher);
                    viewportIconIdleTimer.Interval = TimeSpan.FromMilliseconds(120);
                    viewportIconIdleTimer.Tick += delegate
                    {
                        viewportIconIdleTimer.Stop();
                        ScheduleViewportIconRefresh();
                    };
                }
                viewportIconIdleTimer.Stop();
                viewportIconIdleTimer.Start();
            }
            catch { }
        }


        /// <summary>内容区滚轮：保留方向与系统行数，只改变呈现为短促像素滚动。</summary>
        void OnContentPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            try
            {
                if (e == null || e.Delta == 0) return;
                if (WheelCameFromNestedScrollViewer(e.OriginalSource as DependencyObject)) return;
                if (StartSmoothWheelScroll(e.Delta)) e.Handled = true;
            }
            catch { }
        }


        /// <summary>文件库等子页面有自己的 ScrollViewer 时，让子控件保留滚轮。</summary>
        bool WheelCameFromNestedScrollViewer(DependencyObject source)
        {
            DependencyObject node = source;
            while (node != null && !ReferenceEquals(node, contentScroll))
            {
                ScrollViewer nested = node as ScrollViewer;
                if (nested != null) return true;
                try { node = VisualTreeHelper.GetParent(node); }
                catch { node = null; }
            }
            return false;
        }


        /// <summary>
        /// 每个标准滚轮刻度对应的像素距离。默认 Windows 设置是 3 行，
        /// 这里按 24px/行计算为 72px；“一次滚动一页”仍遵从系统设置。
        /// 高精度触控板产生的小 Delta 会按比例保留，不强行凑整格。
        /// </summary>
        double WheelPixelsPerNotch()
        {
            int lines = 3;
            try { lines = SystemParameters.WheelScrollLines; } catch { }
            if (lines < 0)
            {
                double viewport = contentScroll == null ? 0 : contentScroll.ViewportHeight;
                if (viewport <= 1 && contentScroll != null) viewport = contentScroll.ActualHeight;
                return Math.Max(48, viewport * 0.85);
            }
            if (lines == 0) return 0;
            return Math.Max(24, Math.Min(8, lines) * 24.0);
        }


        /// <summary>开始或续接一次平滑滚动；到边界时返回 false，让外层保留事件语义。</summary>
        bool StartSmoothWheelScroll(int delta)
        {
            if (contentScroll == null || delta == 0 || contentScroll.ScrollableHeight <= 0) return false;

            double step = WheelPixelsPerNotch();
            if (step <= 0) return false; // 系统设置为“一次滚动 0 行”

            double current = contentScroll.VerticalOffset;
            double movement = -(delta / 120.0) * step;
            bool continuesDirection = smoothScrollActive
                && Math.Sign(smoothScrollTarget - current) == Math.Sign(movement);
            // 同方向滚轮累加目标，快速滚动不会每次从头只走一小步；反向时
            // 立即以当前画面为起点掉头，不能先追完旧目标再回来。
            double baseOffset = continuesDirection ? smoothScrollTarget : current;
            double target = baseOffset + movement;
            target = Math.Max(0, Math.Min(contentScroll.ScrollableHeight, target));
            if (Math.Abs(target - current) < 0.1 && Math.Abs(target - baseOffset) < 0.1)
            {
                if (smoothScrollActive && !continuesDirection)
                {
                    StopSmoothScroll(false);
                    smoothScrollTarget = current;
                    return true;
                }
                return false;
            }

            if (Ui.ReducedMotion)
            {
                StopSmoothScroll(false);
                smoothScrollTarget = target;
                contentScroll.ScrollToVerticalOffset(target);
                return true;
            }

            smoothScrollFrom = current;
            smoothScrollTarget = target;
            smoothScrollStartedAt = Stopwatch.GetTimestamp();
            if (!smoothScrollActive)
            {
                smoothScrollActive = true;
                CompositionTarget.Rendering += OnSmoothScrollFrame;
            }
            return true;
        }


        /// <summary>渲染帧驱动的 140ms EaseOut；只在滚动期间订阅。</summary>
        void OnSmoothScrollFrame(object sender, EventArgs e)
        {
            try
            {
                if (!smoothScrollActive || contentScroll == null || panelClosed || !IsVisible)
                {
                    StopSmoothScroll(false);
                    return;
                }

                double elapsedMs = (Stopwatch.GetTimestamp() - smoothScrollStartedAt)
                    * 1000.0 / Stopwatch.Frequency;
                double t = Math.Max(0, Math.Min(1, elapsedMs / 140.0));
                double remain = 1.0 - t;
                double eased = 1.0 - remain * remain * remain;
                double offset = smoothScrollFrom
                    + (smoothScrollTarget - smoothScrollFrom) * eased;
                contentScroll.ScrollToVerticalOffset(offset);

                if (t >= 1.0 || Math.Abs(contentScroll.VerticalOffset - smoothScrollTarget) < 0.1)
                    StopSmoothScroll(true);
            }
            catch
            {
                StopSmoothScroll(false);
            }
        }


        void StopSmoothScroll(bool snapToTarget)
        {
            if (!smoothScrollActive) return;
            smoothScrollActive = false;
            try { CompositionTarget.Rendering -= OnSmoothScrollFrame; } catch { }
            if (snapToTarget && contentScroll != null)
            {
                try { contentScroll.ScrollToVerticalOffset(smoothScrollTarget); } catch { }
            }
        }


        /// <summary>
        /// 只保留视口前后一屏的真实图标。预取一屏能让连续滚动时图标在进入
        /// 可见区前完成解码；超过这个范围则恢复占位，释放 Image.Source 引用。
        /// </summary>
        void RefreshViewportTileIcons()
        {
            try
            {
                if (panelClosed || contentScroll == null || tilePanel == null) return;
                if (tilePanel.Visibility != Visibility.Visible) return;

                double viewport = contentScroll.ViewportHeight;
                if (viewport <= 1) viewport = contentScroll.ActualHeight;
                if (viewport <= 1) viewport = Math.Max(TileHeightPx * 2, ActualHeight);

                for (int i = 0; i < renderedTiles.Count; i++)
                {
                    Border tile = renderedTiles[i];
                    if (tile == null) continue;

                    TileIconState state;
                    if (!tileIconStatesByTile.TryGetValue(tile, out state) || state == null) continue;

                    bool near = false;
                    try
                    {
                        Point p = tile.TranslatePoint(new Point(0, 0), contentScroll);
                        double h = tile.ActualHeight > 0 ? tile.ActualHeight : TileHeightPx;
                        near = IsTileNearViewport(p.Y, h, viewport);
                    }
                    catch
                    {
                        // 尚未完成首轮布局时至少预取第一批，避免骨架停在占位状态。
                        near = i < TileRenderBatchSize;
                    }

                    SetTileIconWanted(state, near);
                }
            }
            catch { }
        }


        internal static bool IsTileNearViewport(double top, double height, double viewportHeight)
        {
            if (viewportHeight <= 0 || double.IsNaN(top) || double.IsInfinity(top)) return false;
            double bottom = top + Math.Max(1, height);
            return bottom >= -viewportHeight && top <= viewportHeight * 2;
        }


        void SetTileIconWanted(TileIconState state, bool wanted)
        {
            if (state == null) return;
            state.WantsImage = wanted;
            if (wanted)
            {
                if (!(state.Visual is Image) && !state.Unavailable) QueueTileIcon(state);
                return;
            }

            Image old = state.Visual as Image;
            if (old == null || state.Box == null) return;
            FrameworkElement placeholder = MakeTileIconPlaceholder(state.App);
            placeholder.RenderTransformOrigin = new Point(0.5, 0.5);
            placeholder.RenderTransform = state.Transform;
            int index = state.Box.Children.IndexOf(old);
            if (index < 0) return;
            state.Box.Children.RemoveAt(index);
            state.Box.Children.Insert(index, placeholder);
            state.Visual = placeholder;
        }

        void SetLowValue(AppItem a, bool low)
        {
            SetClassification(a, low ? AppRole.LowValue : AppRole.User);
        }

        /// <summary>
        /// 收集「全部」页里被判为低价值的应用（带原因），供清单窗口显示。
        ///
        /// 与底部统计用同一份数据（catalog.Apps 过滤 Hidden），
        /// 这样「其中 9 个低价值」点开就是那 9 个，数字不会对不上。
        /// </summary>
        List<AppItem> LowValueApps()
        {
            List<AppItem> list = new List<AppItem>();
            try
            {
                if (catalog == null) return list;
                foreach (AppItem a in catalog.Apps)
                {
                    if (a == null || a.Hidden || a.ExcludedEntry || a.Role == AppRole.Computer) continue;
                    if (a.LowValue) list.Add(a);
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "收集低价值应用"); }
            return list;
        }

        /// <summary>
        /// 打开「低价值清单」窗口；用户勾选并确认后，批量写入 LowValueUnmarked。
        ///
        /// 为什么批量写一次 Save 而不是逐个调 SetLowValue：
        /// SetLowValue 每次都 Save + 局部协调，勾 9 项就是 9 次磁盘写 + 9 次
        /// 全量重建（每次约 100+ 个磁贴）。这里攒完一次写、一次渲染。
        /// </summary>
        void ShowLowValueList()
        {
            try
            {
                if (settings == null) return;

                List<AppItem> low = LowValueApps();
                LowValueListDialog dlg = new LowValueListDialog(this, low);
                bool confirmed = dlg.ShowDialog();
                if (!confirmed) return;

                List<AppItem> chosen = dlg.SelectedItems;
                if (chosen == null || chosen.Count == 0) return;

                if (settings.LowValueMarked == null) settings.LowValueMarked = new List<string>();
                if (settings.LowValueUnmarked == null) settings.LowValueUnmarked = new List<string>();

                foreach (AppItem a in chosen)
                {
                    if (a == null || string.IsNullOrEmpty(a.Path)) continue;

                    SetClassification(a, AppRole.User, false);
                }

                settings.Save();
                Ui.Log("批量取消低价值标记: " + chosen.Count + " 项");
                if (!TryRefreshCurrentTilesInPlace()) RenderApps();
            }
            catch (Exception ex) { Ui.LogError(ex, "打开低价值清单"); }
        }


        /// <summary>
        /// 常用页仅提供暂时移除，不再提供固定或手动添加。
        /// </summary>
        internal static CommonMenuAction CommonMenuFor(bool showingCommon, int usage)
        {
            if (showingCommon && usage > 0) return CommonMenuAction.DismissRecent;
            return CommonMenuAction.None;
        }


        /// <summary>
        /// 从人工顺序移除路径，不改变其它应用的个人顺序。
        /// </summary>
        internal static bool UpdateCommonOrder(List<string> order, string path)
        {
            if (order == null || string.IsNullOrEmpty(path)) return false;

            int removed = order.RemoveAll(delegate(string p)
            { return string.Equals(p, path, StringComparison.OrdinalIgnoreCase); });

            return removed > 0;
        }


        /// <summary>从常用移除时同步人工顺序并按需存盘。</summary>
        void SyncCommonOrder(string path)
        {
            List<string> order;
            if (!customOrders.TryGetValue("常用", out order) || order == null)
            {
                return;
            }

            if (UpdateCommonOrder(order, path)) SaveCustomOrders();
        }


        /// <summary>
        /// 从「最近常用」移除：保存 LastUsed 快照，绝不删除 usage.json。
        /// 下次真实启动产生更晚的 LastUsed 后，条目会自然重新出现。
        /// </summary>
        void DismissRecentCommon(AppItem a)
        {
            try
            {
                if (a == null || settings == null || string.IsNullOrEmpty(a.Path)) return;
                if (settings.CommonDismissed == null)
                    settings.CommonDismissed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

                long last = a.LastUsed;
                if (usage != null) last = Math.Max(last, usage.GetLast(a.Path));
                if (last <= 0) last = DateTime.UtcNow.Ticks;
                settings.CommonDismissed[a.Path] = last;
                settings.Save();
                SyncCommonOrder(a.Path);

                RefreshNavCountsInPlace();
                if (!TryRefreshCurrentTilesInPlace()) RenderApps();
                ShowToast("已从常用移除，启动记录已保留：" + a.Name);
            }
            catch (Exception ex) { Ui.LogError(ex, "从最近常用移除"); }
        }

        ContextMenu activeTileMenu;

        void ShowTileMenu(AppItem a, Border sourceTile, bool keyboard = false)
        {
            if (a == null || string.IsNullOrEmpty(a.Path)) return;
            ContextMenu cm = new ContextMenu();

            // ---- 打开文件所在位置（所有分类都有）----
            MenuItem openDir = new MenuItem();
            openDir.Header = "打开文件所在位置";
            openDir.Click += delegate { OpenContainingFolder(a); };
            cm.Items.Add(openDir);

            // ---- 隐藏 / 取消隐藏 ----
            //
            // 「移到分类」和「恢复自动分类」已移除：分类标签只剩两个，
            // 手工归类没有意义了。数据层（category-overrides.json）保留，
            // 已有的覆盖记录仍然生效（Catalog 加载时照常应用），
            // 只是界面上不再提供修改入口。
            cm.Items.Add(new Separator());
            if (a.Hidden)
            {
                MenuItem show = new MenuItem();
                show.Header = "取消隐藏";
                show.Click += delegate { SetAppHidden(a, false); };
                cm.Items.Add(show);
            }
            else
            {
                MenuItem hide = new MenuItem();
                hide.Header = "从面板隐藏";
                hide.ToolTip = "不在普通页面或搜索中显示；从右下角已隐藏数量恢复";
                hide.Click += delegate { SetAppHidden(a, true); };
                cm.Items.Add(hide);
            }

            MenuItem role = new MenuItem { Header = "应用分类" };
            MenuItem active = new MenuItem { Header = "用户用的", IsCheckable = true,
                IsChecked = a.Role == AppRole.User };
            active.Click += delegate { SetClassification(a, AppRole.User); };
            MenuItem auxiliary = new MenuItem { Header = "低价值", IsCheckable = true,
                IsChecked = a.Role == AppRole.LowValue };
            auxiliary.Click += delegate { SetClassification(a, AppRole.LowValue); };
            MenuItem computer = new MenuItem { Header = "电脑用的（仅搜索显示）", IsCheckable = true,
                IsChecked = a.Role == AppRole.Computer };
            computer.Click += delegate { SetClassification(a, AppRole.Computer); };
            role.Items.Add(active);
            role.Items.Add(auxiliary);
            role.Items.Add(computer);
            cm.Items.Add(role);

            // 常用仅基于真实启动，保留暂时移除，不提供固定入口。
            switch (CommonMenuFor(showingCommon, a.Usage))
            {
                case CommonMenuAction.DismissRecent:
                    cm.Items.Add(new Separator());

                    MenuItem rm = new MenuItem();
                    rm.Header = "从常用移除";
                    rm.ToolTip = AppBrand.Text("保留启动次数；下次从四叶草启动后会重新出现");
                    rm.Click += delegate { DismissRecentCommon(a); };
                    cm.Items.Add(rm);
                    break;
            }

            // 分类只从上述统一子菜单修改，不重复提供低价值开关。

            // ---- 保留在桌面（「全部」「常用」都有，同一位置切换）----
            //
            // 总开关关闭时也显示 —— 先设置好，以后开启也生效。
            if (a != null && !string.IsNullOrEmpty(a.Path))
            {
                cm.Items.Add(new Separator());

                // 目标解析不出（UWP / 商店应用）→ 置灰
                string target = DesktopKeep.ResolveTarget(a.Path);
                bool resolvable = !string.IsNullOrEmpty(target);

                bool kept = resolvable && IsKeptOnDesktop(target);

                MenuItem keep = new MenuItem();
                keep.Header = kept ? "取消保留在桌面" : "保留在桌面";

                if (!resolvable)
                {
                    keep.IsEnabled = false;
                    keep.ToolTip = "无法识别目标";
                }
                else
                {
                    keep.ToolTip = kept
                        ? "移出保留名单，以后桌面上的这个快捷方式会被自动移除"
                        : "加入保留名单，桌面上的这个快捷方式不会被移除；桌面上没有就自动创建";
                    keep.Click += delegate { ToggleKeepOnDesktop(a, target, kept); };
                }

                cm.Items.Add(keep);
            }

            // ---- 显示在任务栏（钉到迷你任务栏）----
            //
            // 为什么放在磁贴菜单：面板改成顶部横条布局后，原来那个
            // "打开的窗口"任务列表不再显示，「显示在任务栏」失去了入口，
            // 迷你任务栏功能成了死功能。这里把它接到磁贴上。
            //
            // 三种状态：
            //   · 迷你任务栏开关关闭  → 置灰，提示去设置里开
            //   · 应用未运行          → 置灰，提示"应用未运行"
            //   · 应用运行中          → 可点，钉上去
            if (a != null && !string.IsNullOrEmpty(a.Path))
            {
                cm.Items.Add(new Separator());

                bool miniOn = settings != null && settings.EffectiveMiniTaskbar;
                List<TaskWindow> wins = null;
                if (miniOn) wins = TaskList.GetWindowsOfApp(a.Path, a.Name);

                MenuItem pin = new MenuItem();
                pin.Header = "显示在任务栏";

                if (!miniOn)
                {
                    pin.IsEnabled = false;
                    pin.ToolTip = Settings.SafeModeActive
                        ? "安全模式下迷你任务栏已停用"
                        : "迷你任务栏未启用。先到「设置…」→ 任务栏 → 勾选「迷你任务栏」";
                }
                else if (wins == null || wins.Count == 0)
                {
                    pin.IsEnabled = false;
                    pin.ToolTip = "应用未运行（先打开它，再钉到任务栏）";
                }
                else
                {
                    pin.ToolTip = wins.Count == 1
                        ? ("钉到迷你任务栏：" + wins[0].Title)
                        : ("钉 " + wins.Count + " 个窗口到迷你任务栏");

                    List<TaskWindow> captured = wins;
                    pin.Click += delegate
                    {
                        try
                        {
                            MiniTaskbarWindow bar = EnsureMiniBar();
                            if (bar == null) return;
                            foreach (TaskWindow w in captured) bar.Add(w.Hwnd);
                            Ui.Log("钉到迷你任务栏: " + a.Name + " (" + captured.Count + " 个窗口)");
                        }
                        catch (Exception ex) { Ui.LogError(ex, "钉到迷你任务栏"); }
                    };
                }

                cm.Items.Add(pin);
            }

            taskMenuOpen = true;
            activeTileMenu = cm;
            cm.Closed += delegate
            {
                taskMenuOpen = false;
                if (keyboard && tilePanel != null && tilePanel.IsVisible) tilePanel.Focus();

                // ContextMenu 在独立窗口中显示，打开/关闭会打断原磁贴的鼠标状态序列。
                // 显式按当前状态复原，避免菜单关掉后残留按下/悬停色块；键盘选中态
                // 优先，不能被鼠标状态抹掉。
                try
                {
                    if (sourceTile == null) return;
                    if (ReferenceEquals(sourceTile, selectedTile))
                        sourceTile.Background = Ui.AccentSoft;
                    else
                        sourceTile.Background = sourceTile.IsMouseOver
                            ? Ui.TileHover : Brushes.Transparent;
                }
                catch { }
            };
            cm.PlacementTarget = sourceTile;
            cm.Placement = keyboard ? PlacementMode.Bottom : PlacementMode.MousePoint;
            cm.IsOpen = true;
        }


        // MovableCategories() 已删除：它只服务「移动应用到其他分类」菜单，
        // 该菜单随分类系统下线一并移除。

        /// <summary>读某应用的覆盖分类；无覆盖返回 null。</summary>
        string GetOverride(string appPath)
        {
            try
            {
                return overrides == null ? null : overrides.Get(appPath);
            }
            catch { return null; }
        }


        /// <summary>
        /// 把应用移到指定分类（category 传 null 表示恢复自动分类）。
        /// 写入 category-overrides.json，然后重新加载目录并刷新界面。
        /// </summary>
        void MoveAppToCategory(AppItem a, string category)
        {
            try
            {
                if (a == null || string.IsNullOrEmpty(a.Path)) return;
                if (overrides == null) overrides = CategoryOverrides.Load(baseDir);

                bool changed;
                if (string.IsNullOrEmpty(category))
                {
                    changed = overrides.Remove(a.Path);
                    Ui.Log("恢复自动分类: " + a.Name);
                }
                else
                {
                    changed = overrides.Set(a.Path, category);
                    Ui.Log("移到分类: " + a.Name + " -> " + category);
                }

                if (!changed) return;

                // 重新加载目录（会重新应用覆盖表），保持当前选中分类
                ReloadCatalog();
                RebuildNav();
                RenderApps();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "移到分类");
            }
        }


        /// <summary>
        /// 隐藏 / 取消隐藏一个应用。
        /// 写入 hidden-apps.json，标记内存中的条目，然后只移除受影响的磁贴。
        /// </summary>
        void SetAppHidden(AppItem a, bool hidden)
        {
            try
            {
                if (a == null || string.IsNullOrEmpty(a.Path)) return;
                if (hiddenApps == null) hiddenApps = HiddenApps.Load(baseDir);

                bool changed = hidden ? hiddenApps.Hide(a.Path)
                    : hiddenApps.ReasonOf(a.Path).RetainOnRestore
                        ? hiddenApps.ShowWithException(a.Path) : hiddenApps.Show(a.Path);
                if (!changed) return;

                // 就地改标记即可，不必全量重载目录：
                // Catalog.Load 也会重新读 hidden-apps.json，但那样会丢掉 usage 的本次更新。
                a.Hidden = hidden;
                Ui.Log((hidden ? "隐藏" : "取消隐藏") + "应用: " + a.Name);

                // 取消隐藏最后一个后，「隐藏的项目」分类会消失——
                // 若正停在那里，得先离开，否则界面停在空分类上。
                if (!hidden && current == HiddenCategory && catalog.HiddenItems().Count == 0)
                {
                    current = "";
                    SaveLastCategory();
                }

                RefreshHiddenDisplay();

                // 单个隐藏后弹轻提示（不抢焦点）
                if (hidden) ShowHiddenTip(a);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "隐藏应用");
            }
        }


        /// <summary>
        /// 在资源管理器里打开该应用所在目录并选中它。
        /// 应用路径可能是 .exe（便携识别）或 .lnk（开始菜单），两种情况都要能定位。
        /// </summary>
        void OpenContainingFolder(AppItem a)
        {
            try
            {
                string path = a == null ? null : a.Path;
                if (string.IsNullOrEmpty(path))
                {
                    MessageBox.Show("这个应用没有可定位的路径。", AppBrand.Text("四叶草面板"));
                    return;
                }

                string arg;
                string err;
                if (!TryBuildExplorerArgs(path, out arg, out err))
                {
                    MessageBox.Show(err, AppBrand.Text("四叶草面板"));
                    return;
                }

                Process.Start("explorer.exe", arg);
                Ui.Log("打开文件位置: " + path + "  参数: " + arg);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "打开文件所在位置");
                try
                {
                    MessageBox.Show("无法打开文件位置：" + ex.Message, AppBrand.Text("四叶草面板"));
                }
                catch { }
            }
        }


        /// <summary>
        /// 根据应用路径生成 explorer.exe 的参数（纯逻辑，可单测）。
        ///
        /// 三种情况：
        ///   1. 是存在的文件（.exe / .lnk 解析后的目标）→ `/select,"目标"` 打开父目录并选中
        ///   2. 文件不存在但目录还在 → `"目录"` 直接打开目录
        ///   3. 都不行 → 返回 false，err 里是给用户看的提示
        ///
        /// .lnk 会先解析真实目标；解析失败就退回到 .lnk 自身所在目录。
        /// </summary>
        public static bool TryBuildExplorerArgs(string path, out string args, out string error)
        {
            args = null;
            error = null;

            if (string.IsNullOrEmpty(path))
            {
                error = "这个应用没有可定位的路径。";
                return false;
            }

            // ---- 商店应用：没有文件系统位置 ----
            //
            // shell:AppsFolder\<AUMID> 是 Shell 命名空间路径，不对应任何文件/目录。
            // 直接返回提示，避免 Path.GetDirectoryName 拼出非法参数、
            // 或者 explorer 打开一个奇怪的位置。
            if (AppPaths.IsShellPath(path))
            {
                error = "商店应用没有文件位置。\r\n\r\n"
                      + "它由 Microsoft Store 管理，安装文件在受保护的 WindowsApps 目录下。\r\n"
                      + "如需管理，请打开「设置 → 应用 → 已安装的应用」。";
                return false;
            }

            string target = path;
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string real = ResolveShortcutTarget(path);
                if (!string.IsNullOrEmpty(real) && File.Exists(real)) target = real;
            }

            // 情况 1：文件存在 → 打开父目录并选中它
            if (File.Exists(target))
            {
                args = "/select,\"" + target + "\"";
                return true;
            }

            // 情况 2：文件没了，但目录还在 → 直接打开目录
            string dir = Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(dir)) dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                args = "\"" + dir + "\"";
                return true;
            }

            // 情况 3：都找不到
            error = "找不到这个文件：\r\n" + target;
            return false;
        }


        /// <summary>解析 .lnk 的目标路径；无效或解析失败返回 null（不会返回空字符串）。</summary>
        public static string ResolveShortcutTarget(string lnk)
        {
            try
            {
                if (string.IsNullOrEmpty(lnk)) return null;
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                object shell = Activator.CreateInstance(shellType);
                object sc = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                if (sc == null) return null;
                object tp = sc.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.GetProperty, null, sc, null);
                string s = tp as string;
                // WScript.Shell 对无效/不存在的快捷方式返回空字符串而不是 null，
                // 这里统一成 null，避免调用方误以为"解析成功但目标为空"。
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch
            {
                return null;
            }
        }
    }
}
