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
        /// 标签栏末尾的圆形「+」：新建标签。
        ///
        /// 样式对齐「文件」页来源行末尾那个 +：圆形（CornerRadius = 高度/2）、
        /// 半透明浅灰底、悬停变深。
        /// </summary>
        Button MakeAddTabButton()
        {
            Button b = new Button();
            b.Width = 30;
            b.Height = 30;
            b.Margin = new Thickness(0, 2, 4, 2);
            b.Cursor = Cursors.Hand;
            b.Background = Ui.SearchBg;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(0);
            b.Template = MakeButtonTemplate(b.Height / 2);
            b.ToolTip = "新建桌面";
            AutomationProperties.SetName(b, "新建桌面");

            TextBlock t = new TextBlock();
            t.Text = "+";
            t.FontFamily = Ui.YaHei;
            t.FontSize = 15;
            t.Foreground = Ui.Sub;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Content = t;

            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = b.IsKeyboardFocused ? Ui.AccentSoft : Ui.SearchBg; };
            b.GotKeyboardFocus += delegate { b.Background = Ui.AccentSoft; };
            b.LostKeyboardFocus += delegate { b.Background = b.IsMouseOver ? Ui.Hover : Ui.SearchBg; };
            b.Click += delegate { PromptNewTab(); };
            return b;
        }


        /// <summary>新建标签：弹小输入框，回车确认。</summary>
        /// <summary>自定义标签的右键菜单：重命名 / 删除。</summary>
        void ShowTabMenu(string name, FrameworkElement anchor)
        {
            try
            {
                ContextMenu cm = new ContextMenu();
                MenuItem rename = new MenuItem();
                rename.Header = "重命名…";
                rename.Click += delegate
                {
                    try
                    {
                        string nn = TextPromptDialog.Ask(this, "重命名标签", "新名字（最多 12 字）", name);
                        if (nn == null) return;
                        string err = customTabs.Rename(name, nn);
                        if (err != null)
                        {
                            MessageBox.Show(this, err, "四叶草 · 重命名标签",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                            return;
                        }
                        string renamed = NavTabs.SanitizeName(nn);
                        bool wasCurrent = (current == name);
                        RebuildNav();
                        if (wasCurrent) SwitchCategory(renamed);
                        Ui.Log("重命名标签: " + name + " -> " + renamed);
                    }
                    catch (Exception ex) { Ui.LogError(ex, "重命名标签"); }
                };
                cm.Items.Add(rename);
                MenuItem del = new MenuItem();
                del.Header = "删除";
                del.Click += delegate { DeleteTabWithFiles(name); };
                cm.Items.Add(del);
                cm.PlacementTarget = anchor;
                cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                cm.IsOpen = true;
            }
            catch (Exception ex) { Ui.LogError(ex, "标签右键菜单"); }
        }


        /// <summary>
        /// 删除一个自定义标签，并处理它文件夹里的文件。
        ///
        ///   1. 文件夹为空 / 不存在 → 不弹窗，直接删
        ///   2. 「放回桌面」→ 搬文件，全成功才删空文件夹与标签
        ///   3. 「保留在存放文件夹」→ 只删标签与坐标
        ///
        /// 有文件搬不走（被占用等）时**不删标签**，汇总提示后保持现状。
        /// </summary>
        void DeleteTabWithFiles(string name)
        {
            try
            {
                if (customTabs == null) return;
                EnsureVirtualDesks();
                string dir = virtualDesks == null ? null : virtualDesks.FolderOf(name);

                if (!DeskRemoval.HasContent(dir))
                {
                    DoRemoveTab(name);
                    try
                    {
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)
                            && Directory.GetFileSystemEntries(dir).Length == 0)
                            Directory.Delete(dir, false);
                    }
                    catch { }
                    return;
                }

                DeskDeleteDialog.Choice choice = DeskDeleteDialog.Ask(this, name, dir);
                if (choice == DeskDeleteDialog.Choice.Cancel) return;

                if (choice == DeskDeleteDialog.Choice.KeepInPlace)
                {
                    DoRemoveTab(name);
                    MessageBox.Show(this,
                        "标签已删除。\r\n\r\n文件保留在：\r\n" + dir,
                        "四叶草 · 删除标签",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    Ui.Log("删除标签（保留文件）: " + name + " -> " + dir);
                    return;
                }

                DeskRemoval.Result r = DeskRemoval.MoveToDesktop(dir);

                if (!r.AllOk)
                {
                    string detail = "";
                    for (int i = 0; i < r.Failed.Count && i < 5; i++)
                    {
                        detail += "\r\n  · " + r.Failed[i];
                        if (i < r.Reasons.Count && !string.IsNullOrEmpty(r.Reasons[i]))
                            detail += "（" + r.Reasons[i] + "）";
                    }
                    if (r.Failed.Count > 5) detail += "\r\n  · 还有 " + (r.Failed.Count - 5) + " 项…";

                    MessageBox.Show(this,
                        r.Failed.Count + " 个文件未能放回，仍保留在：\r\n" + dir
                        + "\r\n" + detail
                        + "\r\n\r\n标签暂未删除，请关闭占用这些文件的程序后重试。",
                        "四叶草 · 删除标签",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    Ui.Log("删除标签失败（有文件未放回）: " + name + " 失败 " + r.Failed.Count + " 项");
                    return;
                }

                DoRemoveTab(name);
                Ui.Log("删除标签（已放回桌面 " + r.Moved + " 项）: " + name);
            }
            catch (Exception ex) { Ui.LogError(ex, "删除标签"); }
        }


        /// <summary>只删标签记录与坐标（不动磁盘）。</summary>
        void DoRemoveTab(string name)
        {
            try
            {
                string err = customTabs.Remove(name);
                if (err != null)
                {
                    MessageBox.Show(this, err, "四叶草 · 删除标签",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (virtualDesks != null) virtualDesks.ForgetDesk(name);
                bool wasCurrent = (current == name);
                RebuildNav();
                if (wasCurrent) SwitchCategory("常用");
            }
            catch (Exception ex) { Ui.LogError(ex, "移除标签记录"); }
        }


        /// <summary>
        /// 把历史名称的桌面安全迁移为内置「桌面文件」。
        ///
        /// 依次尝试「虚拟桌面」「桌面」两个旧名。
        /// 只有在目标目录/记录都不存在时才改名；若目标已存在且非空，
        /// 不合并、不覆盖，保留旧目录并记录冲突原因（供面板提示一次）。
        /// </summary>
        void MigrateLegacyVirtualDesktop()
        {
            try
            {
                if (customTabs == null || virtualDesks == null) return;

                string targetDir = virtualDesks.FolderOf(VirtualDesktopCategory);
                bool targetHasContent = !string.IsNullOrEmpty(targetDir)
                    && Directory.Exists(targetDir)
                    && Directory.GetFileSystemEntries(targetDir).Length > 0;

                foreach (string legacy in LegacyDesktopNames)
                {
                    bool asCustom = customTabs.FindIndex(legacy) >= 0;
                    bool asDesk = virtualDesks.Desks.ContainsKey(legacy);
                    if (!asCustom && !asDesk) continue;

                    string oldDir = virtualDesks.FolderOf(legacy);
                    bool oldHasContent = !string.IsNullOrEmpty(oldDir)
                        && Directory.Exists(oldDir)
                        && Directory.GetFileSystemEntries(oldDir).Length > 0;

                    // 上一次迁移可能已把文件和坐标接管到新名称，但在清理旧标签前
                    // 异常退出，留下“目标有内容 + 旧目录为空”的半收尾状态。
                    // 这时不能重新创建旧桌面再尝试改名；只清理确认无内容的旧壳。
                    if (targetHasContent && !oldHasContent)
                    {
                        try
                        {
                            if (asDesk) virtualDesks.ForgetDesk(legacy);
                            if (!string.IsNullOrEmpty(oldDir) && Directory.Exists(oldDir)
                                && Directory.GetFileSystemEntries(oldDir).Length == 0)
                                Directory.Delete(oldDir, false);

                            RemoveLegacyNavRecord(legacy);
                            Ui.Log("已清理迁移完成后残留的旧标签「" + legacy + "」");
                        }
                        catch (Exception ex)
                        {
                            Ui.LogError(ex, "清理旧桌面残留");
                        }
                        continue;
                    }

                    // 目标已有内容 → 绝不合并。保留旧目录与旧标签，并留下提示。
                    if (targetHasContent && oldHasContent)
                    {
                        string signature = legacy + "|" + oldDir + "|" + targetDir;
                        bool show = deskMigrationNoticeStore == null
                            || deskMigrationNoticeStore.ShouldShow(signature);
                        if (show)
                        {
                            deskMigrationNotice =
                                "「" + legacy + "」和「" + VirtualDesktopCategory + "」里都有文件，"
                                + "为避免覆盖没有自动合并。\r\n\r\n"
                                + "旧文件保留在：\r\n" + oldDir;
                        }
                        Ui.Log("旧标签「" + legacy + "」未迁移：目标「" + VirtualDesktopCategory
                            + "」非空，保留旧目录 " + oldDir
                            + (show ? "（本次提示）" : "（已提示过，不再弹窗）"));
                        continue;
                    }

                    bool targetExists = virtualDesks.Desks.ContainsKey(VirtualDesktopCategory)
                        || (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir));

                    if (targetExists && !targetHasContent)
                    {
                        // 目标目录/记录存在但为空：可以安全接管旧目录。
                        // RenameDesk 会因目标记录已存在而拒绝，因此先移除空记录和空目录。
                        try
                        {
                            virtualDesks.ForgetDesk(VirtualDesktopCategory);
                            if (Directory.Exists(targetDir)
                                && Directory.GetFileSystemEntries(targetDir).Length == 0)
                                Directory.Delete(targetDir, false);
                        }
                        catch (Exception ex)
                        {
                            deskMigrationNotice =
                                "自动迁移失败：无法清理空的目标目录。\r\n\r\n" + targetDir
                                + "\r\n" + ex.Message;
                            Ui.Log("旧标签「" + legacy + "」迁移失败：清空目标目录失败 " + ex.Message);
                            continue;
                        }
                    }

                    // 旧版本可能只有磁盘目录、尚未写入 virtual-desks.json；先纳入记录。
                    if (virtualDesks.EnsureDesk(legacy) == null)
                    {
                        Ui.Log("旧标签「" + legacy + "」迁移失败：无法读取旧目录，保留原数据");
                        continue;
                    }

                    string err = virtualDesks.RenameDesk(legacy, VirtualDesktopCategory);
                    if (err != null)
                    {
                        Ui.Log("旧标签「" + legacy + "」迁移失败（保留原数据）: " + err);
                        continue;
                    }

                    // 只有目标确认存在、源确认消失后，才移除旧标签记录。
                    bool sourceGone = string.IsNullOrEmpty(oldDir) || !Directory.Exists(oldDir);
                    if (!sourceGone)
                    {
                        try
                        {
                            if (Directory.GetFileSystemEntries(oldDir).Length == 0)
                            {
                                Directory.Delete(oldDir, false);
                                sourceGone = !Directory.Exists(oldDir);
                            }
                        }
                        catch { }
                    }
                    bool moved = !string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir)
                        && sourceGone;
                    if (!moved)
                    {
                        Ui.Log("旧标签「" + legacy + "」迁移后核对失败：保留旧标签记录，请检查 " + targetDir);
                        continue;
                    }

                    RemoveLegacyNavRecord(legacy);

                    targetHasContent = true;
                    Ui.Log("旧标签「" + legacy + "」已迁移为内置「" + VirtualDesktopCategory + "」");
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "迁移旧虚拟桌面"); }
        }


        /// <summary>确保 virtualDesks 已初始化（多处共用）。</summary>
        void EnsureVirtualDesks()
        {
            try
            {
                if (virtualDesksReady) return;
                if (virtualDesks == null)
                {
                    virtualDesks = new VirtualDesks(baseDir);
                    virtualDesks.Load();
                }
                if (string.IsNullOrEmpty(virtualDesks.Root))
                    virtualDesks.Root = VirtualDesks.DefaultRoot(baseDir);

                deskMigrationNoticeStore = new DeskMigrationNotice(baseDir);
                deskMigrationNoticeStore.Load();

                // 这些步骤过去在 PanelWindow 构造函数里执行，会在用户从未使用
                // 桌面收纳时也访问磁盘。现在统一随 VirtualDesks 首次需求执行。
                MigrateLegacyVirtualDesktop();
                legacyDeskMigrationChecked = true;
                virtualDesks.EnsureDesk(VirtualDesktopCategory);
                virtualDesks.Save();

                // 上次停在旧别名，而别名已成功迁移时，当前页也跟着切到新名称。
                if (IsLegacyDesktopName(current)
                    && (customTabs == null || customTabs.FindIndex(current) < 0))
                {
                    current = VirtualDesktopCategory;
                    SaveLastCategory();
                }

                virtualDesksReady = true;
                if (navStack != null) RebuildNav();
            }
            catch (Exception ex) { Ui.LogError(ex, "初始化虚拟桌面存储"); }
        }


        static bool IsLegacyDesktopName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string legacy in LegacyDesktopNames)
                if (string.Equals(name, legacy, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }


        /// <summary>
        /// 清理 nav-tabs.json 里的旧别名。通常直接改当前 customTabs；若测试或
        /// 其他临时界面正在使用未保存的标签快照，再从磁盘加载一份做幂等清理。
        /// </summary>
        void RemoveLegacyNavRecord(string legacy)
        {
            try
            {
                if (customTabs != null && customTabs.FindIndex(legacy) >= 0)
                {
                    string err = customTabs.Remove(legacy);
                    if (err == null) return;
                    Ui.Log("清理旧标签「" + legacy + "」失败: " + err);
                }

                NavTabs persisted = new NavTabs(baseDir);
                persisted.Load();
                if (persisted.FindIndex(legacy) < 0) return;
                string persistedError = persisted.Remove(legacy);
                if (persistedError != null)
                    Ui.Log("清理磁盘旧标签「" + legacy + "」失败: " + persistedError);
            }
            catch (Exception ex) { Ui.LogError(ex, "清理旧桌面标签记录"); }
        }


        // ========== 存放目录（设置窗口用）==========

        /// <summary>当前「桌面文件」存放根目录；未初始化时返回默认值。</summary>
        public string DeskRootPath()
        {
            try
            {
                EnsureVirtualDesks();
                if (virtualDesks == null) return "";
                return virtualDesks.Root ?? "";
            }
            catch { return ""; }
        }


        /// <summary>所有已登记的桌面名（内置 + 自定义标签），用于盘点与迁移。</summary>
        List<string> DeskNamesSnapshot()
        {
            List<string> names = new List<string>();
            try
            {
                EnsureVirtualDesks();
                if (virtualDesks != null)
                {
                    foreach (KeyValuePair<string, VirtualDesks.Desk> kv in virtualDesks.Desks)
                        if (!string.IsNullOrEmpty(kv.Key)) names.Add(kv.Key);
                }

                // 标签在 nav-tabs 里但还没登记成桌面的，也算 —— 它们的目录同样要跟着走
                if (customTabs != null)
                {
                    foreach (NavTabs.Tab t in customTabs.Items)
                    {
                        if (t == null || string.IsNullOrEmpty(t.Name)) continue;
                        bool exists = false;
                        foreach (string n in names)
                        {
                            if (string.Equals(n, t.Name, StringComparison.OrdinalIgnoreCase))
                            { exists = true; break; }
                        }
                        if (!exists) names.Add(t.Name);
                    }
                }

                bool hasBuiltin = false;
                foreach (string n in names)
                {
                    if (string.Equals(n, VirtualDesktopCategory, StringComparison.OrdinalIgnoreCase))
                    { hasBuiltin = true; break; }
                }
                if (!hasBuiltin) names.Add(VirtualDesktopCategory);
            }
            catch { }
            return names;
        }


        /// <summary>
        /// 切换「桌面文件」存放目录（带三选一弹窗）。
        ///
        /// 流程：校验 → 盘点（几个桌面 / 几个文件）→ 三选一 → 迁移或只改指针 → 刷新。
        ///
        /// 返回：
        ///   null = 用户取消（调用方什么都不用做，不必提示）
        ///   ""   = 成功
        ///   其他 = 失败原因（原样给用户看）
        /// </summary>
        public string ChangeDeskRoot(Window owner, string newRoot)
        {
            try
            {
                EnsureVirtualDesks();
                if (virtualDesks == null) return "存放目录还没初始化，稍后再试。";

                string oldRoot = virtualDesks.Root;

                DeskRootChange.Check check = DeskRootChange.Validate(newRoot, oldRoot);
                if (!check.Ok) return check.Message;

                List<string> names = DeskNamesSnapshot();
                DeskRootChange.Plan plan = DeskRootChange.Inspect(oldRoot, names);

                DeskRootDialog.Choice choice = DeskRootDialog.Ask(owner, oldRoot, check.Full, plan);
                if (choice == DeskRootDialog.Choice.Cancel) return null;

                return ApplyDeskRootChange(oldRoot, check.Full, names, plan, choice);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "切换桌面存放目录");
                return "切换失败：" + ex.Message;
            }
        }


        /// <summary>
        /// 三选一已经定下来的执行部分（不弹窗，可被测试直接调用）。
        ///
        /// 返回约定同 <see cref="ChangeDeskRoot"/>。
        /// </summary>
        internal string ApplyDeskRootChange(string oldRoot, string newRoot,
            List<string> names, DeskRootChange.Plan plan, DeskRootDialog.Choice choice)
        {
            try
            {
                if (virtualDesks == null) return "存放目录还没初始化，稍后再试。";
                if (choice == DeskRootDialog.Choice.Cancel) return null;
                if (plan == null) plan = new DeskRootChange.Plan();
                if (names == null) names = DeskNamesSnapshot();

                if (choice == DeskRootDialog.Choice.Migrate)
                {
                    DeskRootChange.Result r = DeskRootChange.Migrate(oldRoot, newRoot, plan.Desks);
                    if (!r.AllOk)
                    {
                        string msg = "迁移没有完成，存放目录保持不变。";
                        for (int i = 0; i < r.Failed.Count && i < 6; i++)
                        {
                            msg += "\r\n  · " + r.Failed[i];
                            if (i < r.Reasons.Count && !string.IsNullOrEmpty(r.Reasons[i]))
                                msg += "（" + r.Reasons[i] + "）";
                        }
                        try { Log.Warn("切换桌面存放目录失败: " + msg.Replace("\r\n", " ")); } catch { }
                        return msg;
                    }
                }

                // 到这里才动 root：前面任何一步失败都不会留下「半搬」状态
                virtualDesks.Root = newRoot;
                virtualDesks.Save();

                // 新目录下补齐每个桌面的文件夹（迁移时已存在，只改指针时是新建）
                foreach (string n in names)
                {
                    try { virtualDesks.EnsureDesk(n); } catch { }
                }
                virtualDesks.Save();

                // 界面刷新：当前若停在某个桌面页，重载图标
                try
                {
                    if (IsDeskTab(current))
                    {
                        if (deskView != null) deskView.Load(current);
                        RebuildDeskToolRow();
                    }
                }
                catch { }

                try
                {
                    Log.Info("桌面存放目录已切换: " + oldRoot + " → " + newRoot
                        + "（" + (choice == DeskRootDialog.Choice.Migrate
                            ? "已迁移 " + plan.DeskCount + " 个桌面 / " + plan.Files + " 个文件"
                            : "只改指针，文件留在原处") + "）");
                }
                catch { }

                return "";
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "执行桌面存放目录切换");
                return "切换失败：" + ex.Message;
            }
        }


        void PromptNewTab()
        {
            try
            {
                string name = TextPromptDialog.Ask(this, "新建桌面", "桌面名称（最多 12 字）", "");
                if (name == null) return;   // 用户取消

                string err = customTabs.Add(name);
                if (err != null)
                {
                    MessageBox.Show(this, err, "四叶草 · 新建桌面",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string added = NavTabs.SanitizeName(name);
                RebuildNav();
                SwitchCategory(added);
                Ui.Log("新建桌面: " + added);
            }
            catch (Exception ex) { Ui.LogError(ex, "新建标签"); }
        }


        /// <summary>
        /// 当前标签是不是一个虚拟桌面（内置「桌面文件」或用户额外创建的桌面）。
        ///
        /// 「常用 / 全部 / 文件」不是；内置「桌面文件」永远是；
        /// 其余只有确实存在 customTabs 里的才算。
        /// </summary>
        bool IsDeskTab(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) return false;
                if (name == VirtualDesktopCategory) return true;
                if (name == "常用") return false;
                if (name == FilesCategory) return false;
                if (customTabs == null) return false;
                return customTabs.FindIndex(name) >= 0;
            }
            catch { return false; }
        }


        /// <summary>
        /// 重建「桌面收纳」页顶部的按钮行。
        /// 旧的收纳/放回入口保持不变；智能整理作为显式的“规则 + 预览”入口补充。
        /// </summary>
        void RebuildDeskToolRow()
        {
            try
            {
                if (deskToolRow == null) return;
                deskToolRow.Children.Clear();

                deskToolRow.Children.Add(MakeDeskToolButton(
                    "收纳桌面", "把桌面上的文件收进这个桌面", delegate { CollectDesktop(); }));

                deskToolRow.Children.Add(MakeDeskToolButton(
                    "放回桌面", "把选中（或全部）文件移回桌面", delegate { PutBackToDesktopBatch(); }));

                deskToolRow.Children.Add(MakeDeskToolButton(
                    "智能整理…", "按规则逐项预览后整理桌面", delegate { SmartOrganizeDesktop(); }));

                deskToolRow.Children.Add(MakeDeskToolButton(
                    "整理规则…", "新建、编辑和调整桌面整理规则", delegate { ManageOrganizeRules(); }));
            }
            catch (Exception ex) { Ui.LogError(ex, "重建桌面工具行"); }
        }


        Dictionary<string, string> OrganizeDeskTargets()
        {
            Dictionary<string, string> result =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                EnsureVirtualDesks();
                if (virtualDesks == null) return result;

                virtualDesks.EnsureDesk(VirtualDesktopCategory);
                foreach (KeyValuePair<string, VirtualDesks.Desk> kv in virtualDesks.Desks)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    string path = virtualDesks.FolderOf(kv.Key);
                    if (string.IsNullOrEmpty(path)) continue;
                    try { if (!Directory.Exists(path)) Directory.CreateDirectory(path); }
                    catch { continue; }
                    result[kv.Key] = path;
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "读取智能整理目标"); }
            return result;
        }


        void ManageOrganizeRules()
        {
            try
            {
                DesktopOrganizeRulesDialog dialog = new DesktopOrganizeRulesDialog(
                    this, baseDir, OrganizeDeskTargets());
                dialog.ShowDialog();
            }
            catch (Exception ex) { Ui.LogError(ex, "管理智能整理规则"); }
        }


        void SmartOrganizeDesktop()
        {
            try
            {
                DesktopOrganizeRules rules = DesktopOrganizeRules.Load(baseDir);
                bool hasEnabled = false;
                foreach (DesktopOrganizeRule rule in rules.Items)
                    if (rule != null && rule.Enabled) { hasEnabled = true; break; }

                if (!hasEnabled)
                {
                    if (TextPromptDialog.Confirm(this, "智能整理",
                        "还没有启用的整理规则。\r\n\r\n先新建规则，再回来预览执行。",
                        "打开规则管理")) ManageOrganizeRules();
                    return;
                }

                DesktopOrganizeScan scan = DesktopOrganizePlanner.Scan(DeskCollector.DesktopDirs());
                if (scan.Paths.Count == 0)
                {
                    MessageBox.Show(this, "桌面上没有可整理的顶层项目。",
                        "四叶草 · 智能整理", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                DesktopOrganizePlan plan = DesktopOrganizePlanner.Build(
                    scan.Paths, rules.Items, OrganizeDeskTargets());
                DesktopOrganizePreviewDialog preview = new DesktopOrganizePreviewDialog(
                    this, plan, scan.Skipped.Count);
                if (!preview.ShowDialog()) return;

                DesktopOrganizeExecution done = DesktopOrganizeExecutor.Execute(plan.Items);
                ReportOrganizeResult(done);
                RefreshDeskAfterMove();
            }
            catch (Exception ex) { Ui.LogError(ex, "智能整理桌面"); }
        }


        void ReportOrganizeResult(DesktopOrganizeExecution result)
        {
            try
            {
                if (result == null) return;
                string message = "智能整理：成功 " + result.Succeeded + " 项。";
                if (result.Failed > 0)
                {
                    message += "\r\n\r\n失败 " + result.Failed + " 项：";
                    int shown = 0;
                    foreach (DesktopOrganizeExecutionItem item in result.Items)
                    {
                        if (item == null || item.Ok) continue;
                        message += "\r\n  · "
                            + (item.PlanItem == null ? "未知项目" : item.PlanItem.Name)
                            + "（" + (item.Message ?? "失败") + "）";
                        if (++shown >= 6) break;
                    }
                    if (result.Failed > shown)
                        message += "\r\n  · 还有 " + (result.Failed - shown) + " 项…";
                }
                MessageBox.Show(this, message, "四叶草 · 智能整理",
                    MessageBoxButton.OK,
                    result.Failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch { }
        }


        Button MakeDeskToolButton(string label, string tip, Action onClick, bool primary = false)
        {
            Button b = new Button();
            b.Height = 26;
            b.Padding = new Thickness(12, 0, 12, 0);
            b.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
            b.Cursor = Cursors.Hand;
            b.Background = primary ? Ui.Accent : Ui.SearchBg;
            b.BorderBrush = primary ? Ui.Accent : Ui.Line;
            b.BorderThickness = new Thickness(primary ? 0 : 1);
            b.Template = MakeButtonTemplate(b.Height / 2);
            b.ToolTip = tip;
            AutomationProperties.SetName(b, label);
            AutomationProperties.SetHelpText(b, tip);

            TextBlock t = Ui.T(label, 11.5, primary ? Brushes.White : Ui.Sub,
                primary ? FontWeights.SemiBold : FontWeights.Normal);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Content = t;

            b.MouseEnter += delegate
            {
                if (primary) b.Opacity = 0.84;
                else if (!b.IsKeyboardFocused) b.Background = Ui.Hover;
            };
            b.MouseLeave += delegate
            {
                b.Opacity = 1;
                if (!primary)
                    b.Background = b.IsKeyboardFocused ? Ui.AccentSoft : Ui.SearchBg;
            };
            b.GotKeyboardFocus += delegate
            {
                if (primary) b.Opacity = 0.84;
                else b.Background = Ui.AccentSoft;
            };
            b.LostKeyboardFocus += delegate
            {
                if (primary) b.Opacity = b.IsMouseOver ? 0.84 : 1;
                else b.Background = b.IsMouseOver ? Ui.Hover : Ui.SearchBg;
            };
            b.Click += delegate { if (onClick != null) onClick(); };
            return b;
        }


        void SetDeskNotice(string text)
        {
            try
            {
                if (deskNotice == null) return;
                deskNotice.Text = text ?? "";
                deskNotice.Visibility = string.IsNullOrEmpty(text)
                    ? Visibility.Collapsed : Visibility.Visible;
            }
            catch { }
        }


        /// <summary>当前桌面标签对应的磁盘目录。</summary>
        string CurrentDeskFolder()
        {
            try
            {
                EnsureVirtualDesks();
                if (virtualDesks == null) return null;
                return virtualDesks.FolderOf(current);
            }
            catch { return null; }
        }


        /// <summary>收纳桌面：先扫描并弹窗确认，再搬运。</summary>
        void CollectDesktop()
        {
            try
            {
                string dest = CurrentDeskFolder();
                if (string.IsNullOrEmpty(dest))
                {
                    MessageBox.Show(this, "当前桌面没有可用的存放目录。", "四叶草 · 收纳桌面",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                DeskCollector.ScanResult scan = DeskCollector.Scan();
                if (scan.Items.Count == 0)
                {
                    string msg = "桌面上没有可收纳的文件。";
                    if (scan.Skipped.Count > 0)
                        msg += "\r\n\r\n已跳过 " + scan.Skipped.Count + " 个应用类文件。";
                    MessageBox.Show(this, msg, "四叶草 · 收纳桌面",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (!ConfirmCollect(scan, dest)) return;

                List<string> paths = new List<string>();
                foreach (DeskCollector.Candidate c in scan.Items) paths.Add(c.Path);

                DeskCollector.MoveResult r = DeskCollector.Collect(paths, dest);

                ReportMoveResult("收纳桌面", r, dest);
                RefreshDeskAfterMove();
            }
            catch (Exception ex) { Ui.LogError(ex, "收纳桌面"); }
        }


        /// <summary>确认弹窗：列出将被收纳的文件名。</summary>
        bool ConfirmCollect(DeskCollector.ScanResult scan, string dest)
        {
            try
            {
                Window dlg = new Window();
                dlg.Title = "收纳桌面";
                dlg.Width = 520;
                dlg.Height = 460;
                dlg.Owner = this;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                dlg.ResizeMode = ResizeMode.CanResize;
                dlg.ShowInTaskbar = false;
                dlg.Background = Ui.Bg;

                Grid root = new Grid();
                root.Margin = new Thickness(Ui.S.Xl);
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition());
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                TextBlock title = Ui.T("将收纳 " + scan.Items.Count + " 个文件到「"
                    + DisplayCategoryName(VirtualDesktopCategory) + "」", 14, Ui.Fg, FontWeights.SemiBold);
                Grid.SetRow(title, 0);
                root.Children.Add(title);

                TextBlock path = Ui.T("目标位置：" + dest, 10.5, Ui.Sub);
                path.TextWrapping = TextWrapping.Wrap;
                path.Margin = new Thickness(0, Ui.S.Xs, 0, Ui.S.Sm);
                Grid.SetRow(path, 1);
                root.Children.Add(path);

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                foreach (DeskCollector.Candidate c in scan.Items)
                    sb.AppendLine("· " + Path.GetFileName(c.Path) + "   （" + c.Source + "）");

                if (scan.Skipped.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("以下应用类文件会跳过（" + scan.Skipped.Count + " 个）：");
                    int n = 0;
                    foreach (string s in scan.Skipped)
                    {
                        if (n++ >= 8) { sb.AppendLine("  …"); break; }
                        sb.AppendLine("· " + s);
                    }
                }

                TextBox list = new TextBox();
                list.Text = sb.ToString();
                list.IsReadOnly = true;
                list.TextWrapping = TextWrapping.NoWrap;
                list.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                list.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                list.FontFamily = Ui.YaHei;
                list.FontSize = 11.5;
                list.Background = Ui.SearchBg;
                list.Foreground = Ui.Fg;
                list.BorderBrush = Ui.Line;
                list.Padding = new Thickness(Ui.S.Sm);
                Grid.SetRow(list, 2);
                root.Children.Add(list);

                bool ok = false;
                StackPanel btns = new StackPanel();
                btns.Orientation = Orientation.Horizontal;
                btns.HorizontalAlignment = HorizontalAlignment.Right;
                btns.Margin = new Thickness(0, Ui.S.Md, 0, 0);

                Button cancel = MakeDeskToolButton("取消", "不做任何改动", delegate { dlg.Close(); });

                Button confirm = MakeDeskToolButton("确认收纳",
                    "把这些文件移进当前收纳目录", delegate { ok = true; dlg.Close(); }, true);

                btns.Children.Add(cancel);
                btns.Children.Add(confirm);
                Grid.SetRow(btns, 3);
                root.Children.Add(btns);

                dlg.Content = root;
                dlg.ShowDialog();
                return ok;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "收纳确认弹窗");
                return false;
            }
        }


        /// <summary>放回桌面：有选中就放选中的，没有就放全部。</summary>
        void PutBackToDesktopBatch()
        {
            try
            {
                string dir = CurrentDeskFolder();
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    MessageBox.Show(this, "当前桌面没有可用的存放目录。", "四叶草 · 放回桌面",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                List<string> paths = new List<string>();

                List<string> sel = deskView == null ? null : deskView.SelectedPaths();
                if (sel != null && sel.Count > 0)
                {
                    paths.AddRange(sel);
                }
                else
                {
                    foreach (string f in Directory.GetFiles(dir)) paths.Add(f);
                    foreach (string d in Directory.GetDirectories(dir)) paths.Add(d);
                }

                if (paths.Count == 0)
                {
                    MessageBox.Show(this, "这个桌面里没有文件。", "四叶草 · 放回桌面",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (!TextPromptDialog.Confirm(this, "放回桌面",
                    "将 " + paths.Count + " 项放回真实桌面？\r\n\r\n"
                    + "同名项目会自动改名，不会覆盖桌面上的现有文件。",
                    "放回桌面")) return;

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                DeskCollector.MoveResult r = DeskCollector.PutBack(paths, desktop);

                ReportMoveResult("放回桌面", r, desktop);
                RefreshDeskAfterMove();
            }
            catch (Exception ex) { Ui.LogError(ex, "放回桌面"); }
        }


        /// <summary>搬运结果汇总提示。</summary>
        void ReportMoveResult(string action, DeskCollector.MoveResult r, string where)
        {
            try
            {
                string msg = action + "：成功 " + r.MovedCount + " 个。";
                if (!r.AllOk)
                {
                    msg += "\r\n\r\n" + r.Failed.Count + " 个跳过：";
                    for (int i = 0; i < r.Failed.Count && i < 6; i++)
                    {
                        msg += "\r\n  · " + r.Failed[i];
                        if (i < r.Reasons.Count && !string.IsNullOrEmpty(r.Reasons[i]))
                            msg += "（" + r.Reasons[i] + "）";
                    }
                }
                if (!string.IsNullOrEmpty(where)) msg += "\r\n\r\n位置：" + where;

                MessageBox.Show(this, msg, "四叶草 · " + action,
                    MessageBoxButton.OK,
                    r.AllOk ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch { }
        }


        /// <summary>搬运后刷新图标列表与工具行。</summary>
        void RefreshDeskAfterMove()
        {
            try
            {
                if (deskView != null) deskView.Load(current);
                if (virtualDesks != null) virtualDesks.Save();
                RebuildDeskToolRow();
                SetDeskNotice("");
            }
            catch (Exception ex) { Ui.LogError(ex, "刷新桌面"); }
        }


        /// <summary>
        /// 渲染一个虚拟桌面：显示它对应磁盘文件夹里的文件图标。
        ///
        /// 首次进入时懒创建 VirtualDesks / DesktopView（和 FilesTabView 一个套路）。
        /// </summary>
        void RenderDeskTab(string name)
        {
            try
            {
                StopDeferredRendering();
                EnsureDeskSurface();
                ShowContentLayer(ContentLayer.Desks);
                if (searchResults != null) searchResults.Clear();

                if (deskCanvas == null || deskOverlay == null) return;

                // 首次：建存储与视图（复用统一初始化）
                EnsureVirtualDesks();
                name = current;

                if (deskView == null)
                {
                    deskView = new DesktopView(this, deskCanvas, deskOverlay, virtualDesks, name);
                    deskView.OnLayoutChanged = delegate
                    {
                        try { if (virtualDesks != null) virtualDesks.Save(); } catch { }
                    };
                    deskView.OnItemCountChanged = delegate(int count)
                    {
                        try { UpdateDeskDropHint(count); } catch { }
                    };
                }

                // 确保磁盘文件夹存在
                virtualDesks.EnsureDesk(name);

                // 尺寸变了才能算空位，先跑一次布局
                deskCanvas.UpdateLayout();
                deskView.Load(name);
                deskView.ApplyFilter(searchBox == null ? "" : searchBox.Text);

                RebuildDeskToolRow();

                // 旧标签迁移冲突：进这个页时提示一次，说明为什么没合并。
                if (!string.IsNullOrEmpty(deskMigrationNotice))
                {
                    SetDeskNotice(deskMigrationNotice);
                    string notice = deskMigrationNotice;
                    deskMigrationNotice = null;
                    try
                    {
                        MessageBox.Show(this, notice, "四叶草 · 旧桌面未自动合并",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch { }
                }
                MarkFirstContentReady();
            }
            catch (Exception ex) { Ui.LogError(ex, "渲染虚拟桌面"); }
        }


        internal bool TestDeskSurfaceCreated()
        {
            return deskPanel != null;
        }


        /// <summary>测试用：读取桌面收纳页顶部操作按钮的显示文字。</summary>
        internal List<string> TestDeskToolLabels()
        {
            List<string> labels = new List<string>();
            try
            {
                foreach (Button button in TestDeskToolButtons())
                {
                    TextBlock text = button.Content as TextBlock;
                    if (text != null) labels.Add(text.Text ?? "");
                }
            }
            catch { }
            return labels;
        }


        /// <summary>测试用：读取桌面收纳页顶部的原生操作按钮。</summary>
        internal List<Button> TestDeskToolButtons()
        {
            List<Button> buttons = new List<Button>();
            try
            {
                if (deskToolRow == null) return buttons;
                foreach (UIElement child in deskToolRow.Children)
                {
                    Button button = child as Button;
                    if (button != null) buttons.Add(button);
                }
            }
            catch { }
            return buttons;
        }


        /// <summary>测试用：桌面页与图标画布的实际高度。</summary>
        internal double[] TestDeskHeights()
        {
            try
            {
                return new double[]
                {
                    contentScroll == null ? 0 : contentScroll.ViewportHeight,
                    deskPanel == null ? 0 : deskPanel.ActualHeight,
                    deskCanvas == null ? 0 : deskCanvas.ActualHeight
                };
            }
            catch { return new double[] { 0, 0, 0 }; }
        }


        /// <summary>
        /// 桌面清理器移除快捷方式后的轻提示（不抢焦点）。
        ///
        /// 单个：「已移除桌面快捷方式：xxx  [保留] [撤销]」
        /// 批量：「已移除 N 个桌面快捷方式  [撤销]」（不给「保留」，批量保留语义不清）
        /// </summary>
        internal void ShowDesktopRemovedTip(List<Dictionary<string, object>> removed)
        {
            try
            {
                if (removed == null || removed.Count == 0) return;

                UndoBar generalBar = EnsureGeneralUndoBar();
                if (generalBar == null) return;

                List<string> paths = new List<string>();
                foreach (Dictionary<string, object> d in removed)
                {
                    string p = d.ContainsKey("lnkPath") ? Convert.ToString(d["lnkPath"]) : null;
                    if (!string.IsNullOrWhiteSpace(p)) paths.Add(p);
                }
                if (paths.Count == 0) return;

                if (removed.Count == 1)
                {
                    string name = removed[0].ContainsKey("name")
                        ? Convert.ToString(removed[0]["name"]) : "";

                    string onePath = paths[0];

                    // 「撤销」：先登记一次性豁免，再还原（否则还原动作又触发 Created 被移除）
                    Action undo = delegate
                    {
                        try
                        {
                            if (watcher != null && watcher.DesktopCleaner != null)
                                watcher.DesktopCleaner.GrantExemption(onePath);

                            UndoBar.RestoreFromRecycleBin(new List<string> { onePath });
                        }
                        catch (Exception ex) { Ui.LogError(ex, "撤销桌面移除"); }
                    };

                    // 「保留」：先加名单，再登记豁免，再还原
                    Action keep = delegate
                    {
                        try
                        {
                            if (settings != null)
                            {
                                DesktopKeep.Add(settings.DesktopKeepTargets, onePath);
                                settings.Save();
                            }
                            if (watcher != null && watcher.DesktopCleaner != null)
                                watcher.DesktopCleaner.GrantExemption(onePath);

                            UndoBar.RestoreFromRecycleBin(new List<string> { onePath });
                        }
                        catch (Exception ex) { Ui.LogError(ex, "保留桌面快捷方式"); }
                    };

                    generalBar.Show("已移除桌面快捷方式：" + name, undo, keep);
                }
                else
                {
                    List<string> all = new List<string>(paths);
                    Action undo = delegate
                    {
                        try
                        {
                            if (watcher != null && watcher.DesktopCleaner != null)
                                foreach (string p in all) watcher.DesktopCleaner.GrantExemption(p);

                            UndoBar.RestoreFromRecycleBin(all);
                        }
                        catch (Exception ex) { Ui.LogError(ex, "撤销桌面移除"); }
                    };

                    generalBar.Show("已移除 " + removed.Count + " 个桌面快捷方式", undo);
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "显示桌面移除提示"); }
        }


        /// <summary>某个目标路径是否已在保留名单里。</summary>
        bool IsKeptOnDesktop(string targetPath)
        {
            try
            {
                if (settings == null || settings.DesktopKeepTargets == null) return false;
                string key = DesktopKeep.NormalizeKey(targetPath);
                if (string.IsNullOrEmpty(key)) return false;

                foreach (string k in settings.DesktopKeepTargets)
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            catch { return false; }
        }


        /// <summary>
        /// 「保留在桌面 / 取消保留在桌面」的切换。
        ///
        /// ── 关键顺序（需求补充第 1 条）──
        ///
        /// 加入保留时**先把目标路径写进名单，再创建桌面 .lnk**。
        /// 顺序反了的话，watcher 会在文件刚创建时把它当新快捷方式移走。
        /// </summary>
        void ToggleKeepOnDesktop(AppItem a, string targetPath, bool currentlyKept)
        {
            try
            {
                if (settings == null) return;
                if (settings.DesktopKeepTargets == null)
                    settings.DesktopKeepTargets = new List<string>();

                string key = DesktopKeep.NormalizeKey(targetPath);
                if (string.IsNullOrEmpty(key)) return;

                if (currentlyKept)
                {
                    // ---- 取消保留 ----
                    DesktopKeep.RemoveKey(settings.DesktopKeepTargets, key);
                    settings.Save();
                    Ui.Log("取消保留在桌面: " + targetPath);
                    return;
                }

                // ---- 加入保留 ----
                // ★ 第一步：先登记，再做任何文件操作
                DesktopKeep.AddKey(settings.DesktopKeepTargets, key);
                settings.Save();

                // ★ 第二步：桌面上没有就创建一个（此时已在名单里，watcher 不会移走）
                try
                {
                    if (!DesktopShortcutExists(targetPath))
                        CreateDesktopShortcut(a, targetPath);
                }
                catch (Exception ex) { Ui.LogError(ex, "创建桌面快捷方式"); }
            }
            catch (Exception ex) { Ui.LogError(ex, "切换保留在桌面"); }
        }


        /// <summary>桌面上是否已有指向该目标的快捷方式。</summary>
        bool DesktopShortcutExists(string targetPath)
        {
            try
            {
                string want = DesktopKeep.NormalizeKey(targetPath);
                if (string.IsNullOrEmpty(want)) return false;

                foreach (string dir in DesktopKeep.DesktopDirs())
                {
                    try
                    {
                        if (!Directory.Exists(dir)) continue;
                        foreach (string f in Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly))
                        {
                            try
                            {
                                string ext = (Path.GetExtension(f) ?? "").ToLowerInvariant();
                                if (ext != ".lnk") continue;

                                string t = DesktopKeep.ResolveTarget(f);
                                if (string.IsNullOrEmpty(t)) continue;
                                if (DesktopKeep.NormalizeKey(t) == want) return true;
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
                return false;
            }
            catch { return false; }
        }


        /// <summary>在用户桌面创建指向目标的 .lnk。</summary>
        void CreateDesktopShortcut(AppItem a, string targetPath)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrWhiteSpace(desktop)) return;

                string name = (a != null && !string.IsNullOrWhiteSpace(a.Name))
                    ? a.Name : Path.GetFileNameWithoutExtension(targetPath);
                if (string.IsNullOrWhiteSpace(name)) name = "快捷方式";

                // 去非法字符
                foreach (char c in Path.GetInvalidFileNameChars())
                    name = name.Replace(c, '_');

                string lnk = Path.Combine(desktop, name + ".lnk");

                // 已存在同名 → 加序号
                int n = 2;
                while (File.Exists(lnk))
                {
                    lnk = Path.Combine(desktop, name + " (" + n + ").lnk");
                    n++;
                    if (n > 99) break;
                }

                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;

                shell = Activator.CreateInstance(t);
                if (shell == null) return;

                shortcut = t.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell,
                    new object[] { lnk });
                if (shortcut == null) return;

                shortcut.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut,
                    new object[] { targetPath });
                shortcut.GetType().InvokeMember("Save",
                    System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);

                Ui.Log("已在桌面创建快捷方式: " + lnk);
            }
            catch (Exception ex) { Ui.LogError(ex, "创建桌面快捷方式"); }
            finally
            {
                try { if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut); } catch { }
                try { if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); } catch { }
            }
        }
    }
}
