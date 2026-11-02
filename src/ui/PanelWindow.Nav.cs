using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Clover
{
    /// <summary>左侧分类导航、搜索过滤、应用磁贴渲染与拖拽排序。</summary>
    partial class PanelWindow
    {
        /// <summary>键盘选中的应用磁贴（null 表示没有选中）。</summary>
        Border selectedTile;
        /// <summary>键盘选中项在 renderedApps 中的下标，-1 表示没有选中。</summary>
        int selectedIndex = -1;

        /// <summary>
        /// 悬停时图标放大的比例（纯视觉，不改布局）。
        ///
        /// 1.12 → 1.25：参考 GlassBoard 的 Dock 悬停（它用 1.0→1.6）。
        /// 磁贴是 96px 宽的网格项，不像 Dock 条那样有"把邻居挤开"的空间，
        /// 1.6 会明显盖住相邻磁贴的图标。1.25 是"看得出放大但不越界"的平衡点。
        /// </summary>
        const double IconHoverScale = 1.25;

        /// <summary>
        /// 悬停时图标**上浮**的像素数。
        ///
        /// 这是 Dock 最标志性的移动效果（GlassBoard 用 -5px × MotionLevel）。
        /// 配合放大一起做，才像"图标被拎起来了"；
        /// 只放大不上浮会显得"在原地胀大"，比较闷。
        ///
        /// 用负值是往上（WPF 的 Y 轴向下为正）。
        /// </summary>
        const double IconLiftPx = 4.0;

        /// <summary>
        /// 点击时的**弹跳**高度与时长。
        ///
        /// GlassBoard 的做法：0 → -7px → 0，420ms，
        /// 上去用 EaseOut（快起慢收）、回来用 EaseIn（慢起快收）。
        /// 这里用 -6px / 350ms：幅度略小、时长略短，
        /// 因为磁贴是"启动应用"的高频操作，反馈要快、不能拖。
        /// </summary>
        const double IconBouncePx = 6.0;
        const int IconBounceMs = 350;

        /// <summary>
        /// 拖拽让位/落入的动画时长（毫秒）。
        ///
        /// 200ms 是需求区间 150~250 的中间值：
        ///   · 150 太快 —— 磁贴像被"弹"开，看不出是滑动
        ///   · 250 太慢 —— 快速拖动时让位跟不上鼠标，显得拖沓
        /// 缓动用 CubicEase + EaseOut（快起慢收），这是"丝滑"的标准配方。
        /// </summary>
        const int DragShiftMs = 200;

        /// <summary>磁贴图标边长。
        ///
        /// 32 → 36：原先图标只占磁贴宽度 33%，偏小、显得空旷；
        /// Windows 开始菜单的图标占比约 40%，36/96 = 38% 更接近那个观感。
        /// 磁贴本身尺寸不变（96x90），所以排版、拖动换位、滚动记忆都不受影响。
        ///
        /// public：验收测试直接引用它来认图标，而不是再硬编码一个 32 ——
        /// 否则每次调尺寸都要同步改测试，很容易漏。
        /// </summary>
        public const double IconSizePx = 36;

        /// <summary>磁贴尺寸（供验收测试引用，避免硬编码漂移）。</summary>
        public const double TileWidthPx = 96;
        public const double TileHeightPx = 90;

        /// <summary>
        /// 图标悬停动画，**冻结后跨所有磁贴复用**。
        ///
        /// 为什么不每块磁贴新建：一次渲染 100+ 块磁贴，每块各建 2 个动画对象
        /// 会让托管堆持续增长（这个项目踩过同样的坑，见 ShowPanel 里复用
        /// showAnim/hideAnim 的注释）。冻结的动画是不可变的，可以安全共享。
        /// </summary>
        static readonly DoubleAnimation IconZoomIn = FrozenScale(1.0, IconHoverScale, 120);
        static readonly DoubleAnimation IconZoomOut = FrozenScale(IconHoverScale, 1.0, 120);

        /// <summary>悬停上浮 / 复位（起点终点都明确，可冻结共享）。</summary>
        static readonly DoubleAnimation IconLiftUp = FrozenShift(0.0, -IconLiftPx, 160);
        static readonly DoubleAnimation IconLiftDown = FrozenShift(-IconLiftPx, 0.0, 160);

        /// <summary>
        /// 点击弹跳（关键帧：0 → -6px → 0）。
        ///
        /// 为什么用 DoubleAnimationUsingKeyFrames 而不是两个动画串起来：
        /// 关键帧能保证"上去再回来"是一个连续动作，
        /// 中间不会被别的动画打断；而且只占一个动画槽，
        /// 磁贴被复用（滚动重建）时不会残留半截动画。
        /// </summary>
        static DoubleAnimationUsingKeyFrames BuildBounce()
        {
            DoubleAnimationUsingKeyFrames a = new DoubleAnimationUsingKeyFrames();
            a.Duration = TimeSpan.FromMilliseconds(IconBounceMs);

            CubicEase up = new CubicEase();
            up.EasingMode = EasingMode.EaseOut;
            up.Freeze();

            CubicEase down = new CubicEase();
            down.EasingMode = EasingMode.EaseIn;
            down.Freeze();

            a.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(0.0)));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(-IconBouncePx, KeyTime.FromPercent(0.45), up));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0), down));
            a.Freeze();
            return a;
        }

        static readonly DoubleAnimationUsingKeyFrames IconBounce = BuildBounce();

        /// <summary>
        /// 位移动画（起点终点明确，可冻结共享）。
        ///
        /// 与 FrozenScale 分开是因为缓动不同：
        /// 缩放和上浮都用 EaseOut（快起慢收，跟手），
        /// 但位移的"回位"用 EaseOut 也合适 —— 所以共用 FrozenCubic。
        /// </summary>
        static DoubleAnimation FrozenShift(double from, double to, int ms)
        {
            DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            a.From = from;
            a.EasingFunction = FrozenCubic();
            a.Freeze();
            return a;
        }

        /// <summary>
        /// 缩放动画。**起点与终点都明确**，所以可以冻结后跨所有磁贴共享。
        ///
        /// 加 EaseOut 缓动：线性缩放在鼠标划过密集磁贴时显得"一顿一顿"，
        /// 快起慢收则跟手感更顺。（不用 BackEase/ElasticEase —— 回弹会显得轻浮，
        /// 且高频划过时容易晕。）
        /// </summary>
        static DoubleAnimation FrozenScale(double from, double to, int ms)
        {
            DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            a.From = from;
            a.EasingFunction = FrozenCubic();
            a.Freeze();
            return a;
        }

        /// <summary>共享一个冻结的缓动对象（缓动本身不可变，可安全复用）。</summary>
        static CubicEase frozenEase;
        static CubicEase FrozenCubic()
        {
            if (frozenEase == null)
            {
                CubicEase e = new CubicEase();
                e.EasingMode = EasingMode.EaseOut;
                e.Freeze();
                frozenEase = e;
            }
            return frozenEase;
        }

        /// <summary>
        /// 设置键盘选中项。index 越界或 -1 表示清空选中。
        /// scrollIntoView 为 true 时把磁贴滚进可视区域（方向键导航用）。
        /// </summary>
        void SelectTile(int index, bool scrollIntoView)
        {
            try
            {
                if (tilePanel == null) return;

                // 先恢复旧选中项的外观
                if (selectedTile != null)
                {
                    selectedTile.Background = Brushes.Transparent;
                    selectedTile.BorderThickness = new Thickness(0);
                }
                selectedTile = null;
                selectedIndex = -1;

                if (index < 0 || index >= renderedApps.Count) return;
                if (index >= tilePanel.Children.Count) return;
                Border tile = tilePanel.Children[index] as Border;
                if (tile == null) return;

                selectedTile = tile;
                selectedIndex = index;
                tile.Background = Ui.AccentSoft;
                tile.BorderBrush = Ui.Accent;
                tile.BorderThickness = new Thickness(2);

                if (scrollIntoView) tile.BringIntoView();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "设置磁贴选中");
            }
        }

        /// <summary>方向键导航。返回 true 表示已处理（调用方应把事件标记为 Handled）。</summary>
        bool MoveSelection(Key key)
        {
            if (renderedApps == null || renderedApps.Count == 0) return false;

            // WrapPanel 当前一行放几个磁贴；拿不到布局信息时按 8 列估算
            int cols = 8;
            try
            {
                double w = tilePanel.ActualWidth;
                if (w > 0)
                {
                    double unit = 96 + 4; // 磁贴宽 + 左右 margin
                    int c = (int)Math.Floor(w / unit);
                    if (c >= 1) cols = c;
                }
            }
            catch { }

            int n = renderedApps.Count;
            int i = selectedIndex;

            switch (key)
            {
                case Key.Right:
                    i = (i < 0) ? 0 : i + 1;
                    break;
                case Key.Left:
                    i = (i < 0) ? 0 : i - 1;
                    break;
                case Key.Down:
                    i = (i < 0) ? 0 : i + cols;
                    break;
                case Key.Up:
                    i = (i < 0) ? 0 : i - cols;
                    break;
                case Key.Home:
                    i = 0;
                    break;
                case Key.End:
                    i = n - 1;
                    break;
                default:
                    return false;
            }

            if (i < 0) i = 0;
            if (i > n - 1) i = n - 1;
            SelectTile(i, true);
            return true;
        }

        /// <summary>回车启动当前选中的磁贴；没有选中时启动第一个。返回是否已处理。</summary>
        bool LaunchSelected()
        {
            try
            {
                if (renderedApps == null || renderedApps.Count == 0) return false;
                int i = selectedIndex;
                if (i < 0 || i >= renderedApps.Count) i = 0;
                Launch(renderedApps[i]);
                return true;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "回车启动选中应用");
                return false;
            }
        }

        void RebuildNav()
        {
            navStack.Children.Clear();
            navCats.Clear();
            navItems.Clear();
            navBars.Clear();
            navNames.Clear();
            navCounts.Clear();

            // ★ 只保留两个标签：「常用」「全部」。
            //
            // 为什么砍掉其余（办公/开发/…/桌面/收藏夹/壁纸/隐藏的项目）：
            // 分类越多，找应用时越要在标签之间来回切；而「全部」配上
            // 拼音搜索已经能覆盖绝大多数场景。实测 119 个应用平铺后
            // 一屏能看 40+ 个，翻两屏就到头了。
            //
            // 注意：Catalog / CategoryRules / CategoryOverrides / Categories
            // 这些**类和数据都保留**（下一阶段再清理），只是界面不再展示。
            List<string> cats = new List<string>();
            cats.Add("常用");
            cats.Add("");

            // 第三个标签「文件」（showFilesTab 关闭时不加）
            //
            // **这里不算数量**：文件数要读磁盘，而 RebuildNav 每次切换分类
            // 都会跑。算数量会让每次点标签都卡一下 —— 所以文件标签不显示计数。
            bool hasFilesTab = settings == null || settings.ShowFilesTab;
            if (hasFilesTab) cats.Add(FilesCategory);

            // 默认内置「虚拟桌面」：始终存在，不需要用户点 + 创建。
            // 放在「文件」之后、所有额外自定义桌面之前。
            cats.Add(VirtualDesktopCategory);

            // 用户自定义标签（+ 号建的），排在内置「虚拟桌面」之后
            if (customTabs != null)
            {
                foreach (NavTabs.Tab t in customTabs.Items)
                {
                    if (t == null || string.IsNullOrEmpty(t.Name)) continue;
                    if (cats.Contains(t.Name)) continue;
                    cats.Add(t.Name);
                }
            }

            foreach (string cat in cats)
            {
                string c = cat;
                // 文件与虚拟桌面标签不显示应用数量
                int count = (c == FilesCategory || IsDeskTab(c)) ? -1
                          : (c == "常用" ? CommonApps().Count
                          : (catalog.Apps.Count - catalog.HiddenItems().Count));

                // 显示名
                string label = c == "" ? "全部" : c;   // 空串 → 全部，其余用内部名

                // 分类项：横向胶囊按钮
                Border item = new Border();
                item.Height = 30;
                item.CornerRadius = new CornerRadius(item.Height / 2);
                item.Margin = new Thickness(0, 2, 4, 2);
                item.Padding = new Thickness(10, 0, 10, 0);
                item.Background = Brushes.Transparent;
                item.Cursor = Cursors.Hand;

                Grid g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition());
                // 左侧竖条已废弃，但保留元素（宽度 0）——
                // navBars 列表被 UpdateNav 直接索引，删掉会破坏下标对应。
                g.ColumnDefinitions[0].Width = new GridLength(0);
                g.ColumnDefinitions[1].Width = GridLength.Auto;

                Border bar = new Border();
                bar.Width = 0;
                bar.Height = 0;
                bar.Visibility = Visibility.Collapsed;
                Grid.SetColumn(bar, 0);
                g.Children.Add(bar);

                TextBlock name = new TextBlock();
                name.Text = label;
                name.FontFamily = Ui.YaHei;
                name.FontSize = 12;
                name.Foreground = Ui.Fg;
                name.VerticalAlignment = VerticalAlignment.Center;
                name.Margin = new Thickness(0);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(name, 1);
                g.Children.Add(name);

                TextBlock cnt = new TextBlock();
                cnt.Text = count < 0 ? "" : count.ToString();
                cnt.Visibility = count < 0 ? Visibility.Collapsed : Visibility.Visible;
                cnt.FontFamily = Ui.YaHei;
                cnt.FontSize = 10.5;
                cnt.Foreground = Ui.Sub;
                cnt.VerticalAlignment = VerticalAlignment.Center;
                cnt.Margin = new Thickness(5, 0, 0, 0);
                Grid.SetColumn(cnt, 1);
                g.Children.Add(cnt);

                item.Child = g;
                item.MouseEnter += delegate { if (current != c) item.Background = Ui.Hover; };
                item.MouseLeave += delegate { if (current != c) item.Background = Brushes.Transparent; };
                item.MouseLeftButtonUp += delegate
                {
                    // 先把当前分类的滚动位置存下来（必须在改 current 之前）
                    SaveScrollOffset();
                    SwitchCategory(c);
                };

                // 右键：只有额外自定义桌面能改名/删除；内置四个不给菜单
                if (NavTabs.IsReserved(c) || c == "")
                {
                    item.ToolTip = null;
                }
                else
                {
                    item.ToolTip = "右键：重命名 / 删除";
                    item.MouseRightButtonUp += delegate(object s2, MouseButtonEventArgs e2)
                    {
                        e2.Handled = true;
                        ShowTabMenu(c, item);
                    };
                }

                navStack.Children.Add(item);
                navCats.Add(c);
                navItems.Add(item);
                navBars.Add(bar);
                navNames.Add(name);
                navCounts.Add(cnt);
            }

            UpdateNav();

            // 「+」新建标签：排在所有标签最后
            navStack.Children.Add(MakeAddTabButton());
        }

        /// <summary>
        /// 标签栏末尾的圆形「+」：新建标签。
        ///
        /// 样式对齐「文件」页来源行末尾那个 +：圆形（CornerRadius = 高度/2）、
        /// 半透明浅灰底、悬停变深。
        /// </summary>
        Border MakeAddTabButton()
        {
            Border b = new Border();
            b.Width = 30;
            b.Height = 30;
            b.CornerRadius = new CornerRadius(b.Height / 2);
            b.Margin = new Thickness(0, 2, 4, 2);
            b.Cursor = Cursors.Hand;
            b.Background = Ui.SearchBg;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(1);
            b.ToolTip = "新建桌面";

            TextBlock t = new TextBlock();
            t.Text = "+";
            t.FontFamily = Ui.YaHei;
            t.FontSize = 15;
            t.Foreground = Ui.Sub;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = Ui.SearchBg; };
            b.MouseLeftButtonUp += delegate { PromptNewTab(); };
            return b;
        }

        /// <summary>新建标签：弹小输入框，回车确认。</summary>
        /// <summary>自定义标签的右键菜单：重命名 / 删除。</summary>
        void ShowTabMenu(string name, Border anchor)
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
                    bool moved = !string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir)
                        && (string.IsNullOrEmpty(oldDir) || !Directory.Exists(oldDir));
                    if (!moved)
                    {
                        Ui.Log("旧标签「" + legacy + "」迁移后核对失败：保留旧标签记录，请检查 " + targetDir);
                        continue;
                    }

                    if (asCustom)
                    {
                        err = customTabs.Remove(legacy);
                        if (err != null)
                        {
                            Ui.Log("旧标签记录迁移失败（文件已迁到桌面文件）: " + err);
                            continue;
                        }
                    }

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
                if (virtualDesks != null) return;
                virtualDesks = new VirtualDesks(baseDir);
                virtualDesks.Load();
                if (string.IsNullOrEmpty(virtualDesks.Root))
                    virtualDesks.Root = VirtualDesks.DefaultRoot(baseDir);
            }
            catch (Exception ex) { Ui.LogError(ex, "初始化虚拟桌面存储"); }
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

        void UpdateNav()
        {
            for (int i = 0; i < navItems.Count; i++)
            {
                bool sel = navCats[i] == current;
                // 胶囊的选中态用**实色强调底 + 白字**（目标图就是这样）：
                // 竖排列表时代用的是"淡色底 + 左侧竖条"，那套在横条上不成立 ——
                // 横条没有左边缘可言，竖条只会悬在胶囊里很怪。
                navItems[i].Background = sel ? Ui.Accent : Brushes.Transparent;
                navBars[i].Visibility = Visibility.Collapsed;   // 竖条已废弃
                navNames[i].Foreground = sel ? Brushes.White : Ui.Fg;
                navNames[i].FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal;
                if (i < navCounts.Count && navCounts[i] != null)
                {
                    navCounts[i].Foreground = sel ? Brushes.White : Ui.Sub;
                    navCounts[i].Opacity = sel ? 0.85 : 1.0;
                }
            }
        }

        // 分类右键菜单（重命名 / 上移 / 下移）已随分类系统下线移除。
        // 界面只剩「常用」「全部」两个虚拟标签，它们不提供改名/排序。
        // Categories.cs 因此失去全部调用方，已一并删除。

        void OnSearchChanged()
        {
            // 程序自己清空搜索框时不重建（打开面板的场景）
            if (searchSuppress) return;
            if (searchHint != null)
                searchHint.Visibility = searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderApps();
        }

        List<AppItem> CommonApps()
        {
            List<AppItem> list = new List<AppItem>();
            foreach (AppItem a in catalog.Apps)
            {
                if (a.Usage > 0) list.Add(a);
            }
            list.Sort(RenderCompare);
            if (list.Count > 12) list = list.GetRange(0, 12);
            return list;
        }


        void SyncRunningDot(AppItem a, Grid iconBox)
        {
            try
            {
                if (a == null || iconBox == null) return;

                bool shouldShow = RunningApps.IsRunningCached(a.Path);

                // 末尾是不是已经有圆点了
                Border existing = null;
                int n = iconBox.Children.Count;
                if (n > 0) existing = iconBox.Children[n - 1] as Border;
                bool hasDot = existing != null && Math.Abs(existing.Width - 8) < 0.1;

                if (shouldShow == hasDot) return;   // 状态没变，不碰

                if (shouldShow)
                {
                    Border dot = new Border();
                    dot.Width = 8;
                    dot.Height = 8;
                    dot.CornerRadius = new CornerRadius(4);   // 正圆
                    dot.Background = Ui.Accent;
                    dot.HorizontalAlignment = HorizontalAlignment.Right;
                    dot.VerticalAlignment = VerticalAlignment.Top;
                    // 略微外扩到图标边框上，像"贴在角上"而不是"压在图标里"
                    dot.Margin = new Thickness(0, -1, -1, 0);
                    dot.ToolTip = a.Name + " 正在运行";
                    iconBox.Children.Add(dot);
                }
                else
                {
                    iconBox.Children.Remove(existing);
                }
            }
            catch { }
        }

        /// <summary>
        /// 就地刷新所有已画磁贴的小圆点。
        ///
        /// 由任务栏刷新定时器（1.5 秒）调用。两件事会需要它：
        ///   1. 后台预热完成后，缓存从"没热"变成"热"，原先没画上的点要补上
        ///   2. 面板开着期间应用启动了/退出了
        /// 只查表 + 增删一个小 Border，不触发布局、不重建磁贴。
        /// </summary>
        void RefreshRunningDots()
        {
            try
            {
                if (tileIconBoxes.Count == 0) return;
                if (renderedApps == null) return;
                if (!IsVisible) return;   // 面板没显示，没人看，省掉

                RunningApps.Refresh();   // 内部 1.5 秒节流

                foreach (AppItem a in renderedApps)
                {
                    if (a == null) continue;
                    Grid box;
                    if (!tileIconBoxes.TryGetValue(a.Path, out box)) continue;
                    SyncRunningDot(a, box);
                }
            }
            catch { }
        }

        /// <summary>
        /// 快速启动按钮：图标 + 名字（横排，比磁贴小）。
        ///
        /// 单独做而不复用 MakeTile：磁贴是 96x90 的竖排大块，
        /// <summary>当前已渲染内容的签名，用于判断是否真的需要重建磁贴。</summary>
        string renderedSig = "";

        /// <summary>
        /// 内容没变就不重建磁贴。避免“每次打开面板都销毁并重建全部控件”，
        /// 那是重复开关面板时内存持续上涨的主因。
        /// </summary>
        void RenderAppsIfNeeded()
        {
            try
            {
                string sig = CurrentContentSig();
                if (sig == renderedSig) return;
                RenderApps();
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

        /// <summary>当前界面内容的签名（分类 + 搜索词 + 应用数 + 自定义排序长度）。</summary>
        string CurrentContentSig()
        {
            try
            {
                string q = searchBox == null ? "" : searchBox.Text.Trim();
                int orderCount = 0;
                List<string> order;
                if (customOrders.TryGetValue(current, out order) && order != null) orderCount = order.Count;
                // 隐藏数进签名：隐藏/取消隐藏会改变内容，但 Apps.Count 不变，
                // 不带上它的话「按需渲染」会误判成"没变化"而跳过重建。
                int hiddenCount = catalog == null ? 0 : catalog.HiddenItems().Count;
                return current + "\u0001" + q + "\u0001" + catalog.Apps.Count +
                       "\u0001" + orderCount + "\u0001" + hiddenCount;
            }
            catch
            {
                return "";
            }
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
        void RestoreScrollOffset()        {
            try
            {
                if (contentScroll == null) return;

                double target;
                if (!scrollOffsets.TryGetValue(current, out target) || target <= 0) return;

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

        /// <summary>
        /// 渲染「文件」标签：隐藏磁贴与搜索结果，显示 filesPanel。
        ///
        /// **搜索隔离**：文件标签下搜索框只过滤当前来源的文件名，
        /// 不调 searchResults.Search（那是全局应用搜索，会弹出一堆应用）。
        /// </summary>
        /// <summary>
        /// 内容区三个互斥层。**同一时间只能有一个可见**。
        ///
        /// 为什么用枚举而不是三个 bool：三层各自 Visible/Collapsed 的组合有 8 种，
        /// 手动维护必然漏 —— 加「文件」标签时就漏了把 filesPanel 设回 Collapsed，
        /// 结果切回「常用」后文件页还压在上面。用枚举 + 单一入口，
        /// 新增层时只需在这里加一个 case，不可能漏。
        /// </summary>
        enum ContentLayer
        {
            /// <summary>应用磁贴（常用 / 全部）。</summary>
            Tiles,
            /// <summary>全局搜索结果列表。</summary>
            SearchResults,
            /// <summary>「文件」标签面板。</summary>
            Files,
            /// <summary>虚拟桌面图标层（自定义标签）。</summary>
            Desks,
        }

        /// <summary>
        /// <summary>
        /// 切换分类（常用 / 全部 / 文件 / 虚拟桌面 / 额外桌面）。**所有切换入口都走这里**，
        /// 不要各自写 `current = x; RenderApps();` —— 那样必然漏掉状态隔离。
        ///
        /// 做三件事：
        ///   1. 让离开的标签释放自己的瞬时状态（文件标签清选择/过滤/缓存）
        ///   2. 清空搜索框 —— 否则在文件页搜过 "abc"，切到「常用」会用 "abc" 过滤应用
        ///   3. 重建标签选中态 + 渲染新标签
        /// </summary>
        void SwitchCategory(string cat)
        {
            try
            {
                string next = cat ?? "";
                bool wasFiles = (current == FilesCategory);
                bool willFiles = (next == FilesCategory);

                // ---- 1. 离开的标签释放状态 ----
                if (wasFiles && !willFiles && filesView != null)
                    filesView.OnLeave();

                // ---- 2. 清空搜索框（跨标签不共用搜索词）----
                //   searchSuppress 防止 TextChanged 触发一次多余渲染
                if (searchBox != null && searchBox.Text.Length > 0)
                {
                    searchSuppress = true;
                    try { searchBox.Text = ""; }
                    finally { searchSuppress = false; }
                    if (searchHint != null) searchHint.Visibility = Visibility.Visible;
                }

                // ---- 3. 切换 + 渲染 ----
                current = next;
                SaveLastCategory();
                UpdateNav();

                // 进入文件标签：重置瞬时状态，从干净状态开始
                if (willFiles && filesView != null)
                    filesView.OnEnter();

                RenderApps();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "切换分类");
            }
        }

        void ShowContentLayer(ContentLayer layer)

        {
            try
            {
                if (tilePanel != null)
                    tilePanel.Visibility = layer == ContentLayer.Tiles
                        ? Visibility.Visible : Visibility.Collapsed;

                if (searchResultPanel != null)
                    searchResultPanel.Visibility = layer == ContentLayer.SearchResults
                        ? Visibility.Visible : Visibility.Collapsed;

                if (filesPanel != null)
                    filesPanel.Visibility = layer == ContentLayer.Files
                        ? Visibility.Visible : Visibility.Collapsed;

                if (deskPanel != null)
                    deskPanel.Visibility = layer == ContentLayer.Desks
                        ? Visibility.Visible : Visibility.Collapsed;

                if (deskCanvas != null)
                    deskCanvas.Visibility = layer == ContentLayer.Desks
                        ? Visibility.Visible : Visibility.Collapsed;

                if (deskOverlay != null)
                    deskOverlay.Visibility = layer == ContentLayer.Desks
                        ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "切换内容层");
            }
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
        /// 重建「桌面文件」页顶部的按钮行。
        ///
        /// 按钮：收纳桌面 / 放回桌面 / 撤销上一批。
        /// 撤销按钮只在有历史时出现。
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

                DeskUndo.Batch latest = deskUndo == null ? null : deskUndo.Latest;
                if (latest != null)
                {
                    string label = "撤销「" + latest.Action + "」(" + latest.Entries.Count + ")";
                    deskToolRow.Children.Add(MakeDeskToolButton(
                        label, "把上一批文件移回原位置", delegate { UndoLastBatch(); }));
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "重建桌面工具行"); }
        }

        Border MakeDeskToolButton(string label, string tip, Action onClick)
        {
            Border b = new Border();
            b.Height = 26;
            b.CornerRadius = new CornerRadius(b.Height / 2);
            b.Padding = new Thickness(12, 0, 12, 0);
            b.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
            b.Cursor = Cursors.Hand;
            b.Background = Ui.SearchBg;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(1);
            b.ToolTip = tip;

            TextBlock t = Ui.T(label, 11.5, Ui.Sub);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = Ui.SearchBg; };
            b.MouseLeftButtonUp += delegate { if (onClick != null) onClick(); };
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

                if (r.AnyMoved && deskUndo != null)
                    deskUndo.Push("收纳桌面", r.Moved);

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
                    + VirtualDesktopCategory + "」", 14, Ui.Fg, FontWeights.SemiBold);
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

                Border cancel = MakeDeskToolButton("取消", "不做任何改动", delegate { dlg.Close(); });

                Border confirm = MakeDeskToolButton("确认收纳",
                    "把这些文件移进当前桌面文件夹", delegate { ok = true; dlg.Close(); });
                confirm.Background = Ui.Accent;
                TextBlock ct = confirm.Child as TextBlock;
                if (ct != null) ct.Foreground = Brushes.White;

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

                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                DeskCollector.MoveResult r = DeskCollector.PutBack(paths, desktop);

                if (r.AnyMoved && deskUndo != null)
                    deskUndo.Push("放回桌面", r.Moved);

                ReportMoveResult("放回桌面", r, desktop);
                RefreshDeskAfterMove();
            }
            catch (Exception ex) { Ui.LogError(ex, "放回桌面"); }
        }

        /// <summary>撤销最近一批。</summary>
        void UndoLastBatch()
        {
            try
            {
                if (deskUndo == null) return;

                DeskUndo.Batch latest = deskUndo.Latest;
                if (latest == null) return;

                if (!TextPromptDialog.Confirm(this, "撤销",
                    "把上一批（" + latest.Action + "，" + latest.Entries.Count
                    + " 个文件）移回原位置？\r\n\r\n原位置已被占用的文件会跳过，不会覆盖。",
                    "撤销")) return;

                DeskUndo.UndoResult r = deskUndo.UndoLatest();

                string msg = "已撤销 " + r.Restored + " 个文件。";
                if (!r.AllOk)
                {
                    msg += "\r\n\r\n" + r.Failed.Count + " 个未能撤销：";
                    for (int i = 0; i < r.Failed.Count && i < 5; i++)
                    {
                        msg += "\r\n  · " + r.Failed[i];
                        if (i < r.Reasons.Count && !string.IsNullOrEmpty(r.Reasons[i]))
                            msg += "（" + r.Reasons[i] + "）";
                    }
                }

                MessageBox.Show(this, msg, "四叶草 · 撤销",
                    MessageBoxButton.OK,
                    r.AllOk ? MessageBoxImage.Information : MessageBoxImage.Warning);

                RefreshDeskAfterMove();
            }
            catch (Exception ex) { Ui.LogError(ex, "撤销上一批"); }
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
                ShowContentLayer(ContentLayer.Desks);
                if (searchResults != null) searchResults.Clear();

                if (deskCanvas == null || deskOverlay == null) return;

                // 首次：建存储与视图（复用统一初始化）
                EnsureVirtualDesks();

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
            }
            catch (Exception ex) { Ui.LogError(ex, "渲染虚拟桌面"); }
        }

        void RenderFilesTab()
        {
            try
            {
                // 三者互斥：只显示文件层
                ShowContentLayer(ContentLayer.Files);
                if (searchResults != null) searchResults.Clear();

                // 首次创建 FilesTabView
                if (filesView == null && filesPanel != null)
                {
                    filesView = new FilesTabView(this, settings, baseDir,
                        filesChipRow, filesExtRow, filesListPanel, filesNotice,
                        filesUndoHost);
                }

                if (filesView != null)
                {
                    // 搜索词只传给文件视图做本地过滤
                    string q = searchBox == null ? "" : searchBox.Text.Trim();
                    filesView.ApplyFilter(q);
                    filesView.Refresh(false);
                }
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "渲染文件标签");
            }
        }

        void RenderAppsCore()
        {
            // ---- 「文件」标签：走完全独立的渲染路径 ----
            //
            // 文件标签与应用磁贴是两套东西：
            //   · 磁贴：AppItem、可拖动排序、有运行中圆点
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

            tilePanel.Children.Clear();
            // 磁贴被丢掉了，旧的图标容器引用（指向已移除的控件）也要一起清，
            // 否则 RefreshRunningDots 会去改已经不存在的控件
            tileIconBoxes.Clear();

            string q = searchBox == null ? "" : searchBox.Text.Trim();
            showingCommon = (q.Length == 0 && current == "常用");
            bool showingHidden = false;   // 「隐藏的项目」标签已移除


            List<AppItem> list;

            // ---- 系统级搜索（开始菜单 + 注册表 + 磁盘）----
            //
            // 与下面的"应用清单过滤"是两件事，同时进行：
            //   · 应用清单过滤：已扫描的 119 个应用，按拼音/中英文匹配
            //   · 系统级搜索：全系统的程序与文件，Instant + Deep 两级
            // 用户看到的是两部分结果，应用磁贴在先（更快、更常用），
            // 系统搜索结果在下面（更全、但可能是文件）。
            // 两者在同一个 Grid 里，必须**互斥显示**。
            //
            // 第一版只把 searchResultPanel 设 Visible、忘了把 tilePanel 设 Collapsed，
            // 结果两者叠在一起渲染 —— 搜索结果的文字直接压在磁贴上，
            // 用户看到的就是"文字互相重叠、糊成一片"。
            //
            // 搜索时同时隐藏快速启动区：它是"常用应用"的快捷入口，
            // 用户已经在搜索了，那排按钮只是占地方。
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
                    if (Pinyin.Matches(a.Name, q))
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
            renderedApps = new List<AppItem>(list);

            // 刷新"哪些应用在运行"，供磁贴右上角的小圆点用。
            //
            // RunningApps.Refresh 内部有 1.5 秒节流（枚举 300+ 进程约 50-75 ms），
            // 而建磁贴只用 IsRunningCached（只读缓存，不解析 lnk）——
            // 首次解析 112 个快捷方式要 239 ms，那代价放在后台预热里做。
            RunningApps.Refresh();

            // 重渲染会丢弃旧的 Border 对象，选中状态必须一起清掉（否则 selectedTile 指向已移除的控件）
            selectedTile = null;
            selectedIndex = -1;
            foreach (AppItem a in list)
                tilePanel.Children.Add(MakeTile(a));

            // 空状态：只在**没有系统搜索结果**时才显示。
            //
            // 为什么要这个判断：搜索时"应用清单里没有"不等于"全系统没有"。
            // 实测搜「报告」时，119 个已扫描应用里确实一个都不匹配，
            // 但磁盘上找到了 4 个 .docx —— 此时若显示"没有找到「报告」"
            // 就与下方真实结果自相矛盾。
            bool hasSystemResults = searchResultPanel != null
                && searchResultPanel.Visibility == Visibility.Visible
                && searchResultPanel.Children.Count > 0;

            if (list.Count == 0 && !(q.Length > 0 && hasSystemResults))
            {
                string tip, sub, glyph;
                if (q.Length > 0)
                {
                    glyph = "\uE721";                                   // 放大镜
                    tip = "没有找到「" + q + "」";
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
                    sub = "右键四叶草 →「重新扫描应用」可以重新识别";
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

            // 底部计数：隐藏项不该算进"应用总数"，否则数字对不上眼前的磁贴
            int visible = catalog.Apps.Count - catalog.HiddenItems().Count;
            // 搜索时计数改成"已找到多少条系统结果" ——
            // 原先显示"共 61 个应用"，但页面显示的是搜索结果，数字与内容无关。
            // SearchResultList 暴露 ShownCount；系统搜索还没出结果时显示"搜索中"。
            if (searching)
            {
                int n = searchResults != null ? searchResults.ShownCount : 0;
                footerCount.Text = n > 0
                    ? "找到 " + n + " 条结果"
                    : "搜索中…";
            }
            else
            {
                if (showingHidden)
                {
                    footerCount.Text = "共 " + catalog.HiddenItems().Count + " 个隐藏项 · 右键可取消隐藏";
                }
                else
                {
                    // ★ 「已隐藏 M 个」是可点的：点它打开「整理与隐藏」窗口并定位到标签二
                    int hiddenCount = catalog.HiddenItems().Count;
                    if (hiddenCount > 0)
                    {
                        footerCount.Inlines.Clear();

                        Run head = new Run("共 " + visible + " 个应用，");
                        head.Foreground = Ui.Sub;
                        footerCount.Inlines.Add(head);

                        Hyperlink link = new Hyperlink();
                        link.Inlines.Add(new Run("已隐藏 " + hiddenCount + " 个"));
                        link.Foreground = Ui.Accent;
                        link.TextDecorations = null;
                        link.Cursor = System.Windows.Input.Cursors.Hand;
                        link.ToolTip = "点击查看并恢复已隐藏的应用";
                        link.Click += delegate
                        {
                            try { ShowHiddenItems(); }
                            catch (Exception ex) { Ui.LogError(ex, "打开整理与隐藏窗口"); }
                        };
                        footerCount.Inlines.Add(link);
                    }
                    else
                    {
                        footerCount.Inlines.Clear();
                        footerCount.Text = "共 " + visible + " 个应用";
                    }
                }
            }

            // 内容重建完再恢复滚动位置（放在最后，确保布局已经反映了新的磁贴集合）
            RestoreScrollOffset();

        }


        /// <summary>
        /// 「全部」的列表：所有可见应用，按名称排序。
        ///
        /// 排序用 Pinyin.SortKey（逐字拼音键）+ StringComparison.Ordinal：
        ///   · 中文按拼音逐字比较（阿 &lt; 波 &lt; 次）
        ///   · 英文转小写后比较（大小写不敏感）
        ///   · 数字/符号开头的排最前
        ///   · Ordinal 保证与系统区域设置无关，结果稳定可重现
        ///
        /// 为什么不用 RenderCompare（按使用次数）：
        /// 「常用」已经承担了"按使用频率"的角色，「全部」应该是
        /// 可预测的字典序 —— 用户找某个应用时能按名字直接定位。
        /// </summary>
        /// <summary>
        /// 「全部」页底部统计：共 N 个应用，其中 M 个低价值。
        ///
        /// 只在「全部」页显示低价值数量 —— 「常用」页不受沉底影响，不显示。
        /// </summary>
        string BuildAllStats()
        {
            try
            {
                int total = 0, low = 0;
                foreach (AppItem a in catalog.Apps)
                {
                    if (a == null || a.Hidden) continue;
                    total++;
                    if (a.LowValue) low++;
                }

                if (low > 0)
                    return "共 " + total + " 个应用，其中 " + low + " 个低价值";
                return "共 " + total + " 个应用";
            }
            catch { return ""; }
        }
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
                    if (a == null || a.Hidden) continue;

                    string key = a.Path ?? "";
                    if (key.Length > 0 && !seenPath.Add(key)) continue;   // 已见过同路径 → 跳过

                    list.Add(a);
                }

                // ---- 2. 标记低价值 ----
                foreach (AppItem a in list)
                    a.LowValue = IsLowValueApp(a);

                // ---- 3. 排序：低价值整体沉底，各自内部保持字典序 ----
                list.Sort(delegate(AppItem x, AppItem y)
                {
                    // 低价值在后（这一条优先于名称排序）
                    if (x.LowValue != y.LowValue) return x.LowValue ? 1 : -1;

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

        /// <summary>
        /// 判定一个应用是否低价值。**手动标记优先于自动规则**：
        ///   · 在 LowValueUnmarked 里 → 一律不低价值（最高优先）
        ///   · 在 LowValueMarked 里   → 一律低价值
        ///   · 否则走 LowValueRules.IsLowValue 自动判定
        /// </summary>
        bool IsLowValueApp(AppItem a)
        {
            try
            {
                if (a == null || string.IsNullOrEmpty(a.Path)) return false;

                if (settings != null)
                {
                    if (settings.LowValueUnmarked != null)
                    {
                        foreach (string p in settings.LowValueUnmarked)
                            if (string.Equals(p, a.Path, StringComparison.OrdinalIgnoreCase)) return false;
                    }

                    if (settings.LowValueMarked != null)
                    {
                        foreach (string p in settings.LowValueMarked)
                            if (string.Equals(p, a.Path, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }

                // 商店应用没有真实文件路径，不做「失效」判定
                string target = a.Path;
                if (!string.IsNullOrEmpty(a.Aumid)) target = null;

                return LowValueRules.IsLowValue(a.Name, target, LowValueRules.ExeNameOf(target));
            }
            catch { return false; }
        }

        /// <summary>
        /// 启动时校正分类：last-category.json 里可能记着已被删除的标签
        /// （办公 / 开发 / 桌面 / 壁纸 / 收藏夹 / 隐藏的项目 等）。
        ///
        /// 遇到不存在的分类一律回退到「常用」，不能报错也不能显示空白。
        /// 在 BuildUi 之后、首次渲染之前调用。
        /// </summary>
        // ========== 测试辅助（生产代码不调）==========
        //
        // AcceptanceTest 要验证标签行为，但 RebuildNav / current / LoadLastCategory
        // 都是 private。加这几个 internal 方法让测试能驱动它们，
        // 比反射稳（反射遇到重命名就静默失效）。

        /// <summary>测试用：临时开关「文件」标签并重建标签栏。</summary>
        internal void TestSetShowFilesTab(bool on)
        {
            try
            {
                if (settings != null) settings.ShowFilesTab = on;
                RebuildNav();
            }
            catch (Exception ex) { Ui.LogError(ex, "测试：设置 showFilesTab"); }
        }

        /// <summary>测试用：切到某个分类。</summary>
        internal void TestSwitchCategory(string cat)
        {
            try
            {
                // 走生产路径，保证测试覆盖与真实切换一致
                SwitchCategory(cat);
            }
            catch (Exception ex) { Ui.LogError(ex, "测试：切换分类"); }
        }

        /// <summary>测试用：写入 last-category.json 并重读。</summary>
        internal void TestSetLastCategory(string cat)
        {
            try
            {
                if (string.IsNullOrEmpty(lastCatPath)) return;
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["category"] = cat ?? "";
                System.Web.Script.Serialization.JavaScriptSerializer ser =
                    new System.Web.Script.Serialization.JavaScriptSerializer();
                JsonIO.WriteAtomic(lastCatPath, ser.Serialize(d));
            }
            catch (Exception ex) { Ui.LogError(ex, "测试：写 last-category"); }
        }

        /// <summary>测试用：重读 last-category.json 并校正分类。</summary>
        internal void TestReloadLastCategory()
        {
            try
            {
                // 模拟「last-category.json 记着一个已不存在的标签」：
                // 直接把 current 设成那个值，再走 NormalizeCurrentCategory。
                // （不去调 LoadLastCategory —— 它遇到未知分类是「什么都不做」，
                //   current 保持调用前的值，那样测不出回退。）
                current = "不存在的分类XYZ";
                NormalizeCurrentCategory();
                UpdateNav();
            }
            catch (Exception ex) { Ui.LogError(ex, "测试：重读 last-category"); }
        }


        /// <summary>测试用：把按键转给文件视图，返回是否被消费。</summary>
        internal bool TestFilesHandleKey(Key key, ModifierKeys mods)
        {
            try
            {
                if (filesView == null) return false;
                return filesView.HandleKey(key, mods);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "测试：文件键盘");
                return false;
            }
        }

        /// <summary>测试用：预览是否打开。</summary>
        internal bool TestFilesPreviewOpen()
        {
            try { return filesView != null && filesView.IsPreviewOpen; }
            catch { return false; }
        }

        /// <summary>测试用：关预览。</summary>
        internal void TestFilesClosePreview()
        {
            try { if (filesView != null) filesView.ClosePreview(); }
            catch { }
        }

        /// <summary>测试用：文件列表里当前选中的项数。</summary>
        internal int TestFilesSelectedCount()
        {
            try { return filesView == null ? 0 : filesView.TestSelectedCount(); }
            catch { return 0; }
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
                List<AppItem> list = AllAppsSorted();
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

        /// <summary>测试用：设置/取消低价值标记。</summary>
        internal void TestSetLowValue(AppItem a, bool low)
        {
            try { SetLowValue(a, low); }
            catch { }
        }

        /// <summary>测试用：读当前分类。</summary>
        /// <summary>测试用：清空 file-folders.json（模拟"没有任何来源"）。</summary>
        internal void TestClearFileFolders()
        {
            try
            {
                string p = FileFolders.PathOf(baseDir);
                if (File.Exists(p)) File.Delete(p);
                if (File.Exists(p + ".bak")) File.Delete(p + ".bak");
                if (filesView != null) filesView.ReloadFolders();
            }
            catch (Exception ex) { Ui.LogError(ex, "测试：清空 file-folders"); }
        }

        /// <summary>测试用：添加一个来源目录。</summary>
        internal void TestAddFileFolder(string path)
        {
            try
            {
                FileFolders f = FileFolders.Load(baseDir);
                f.Add(path);
                if (filesView != null) filesView.ReloadFolders();
            }
            catch (Exception ex) { Ui.LogError(ex, "测试：添加来源"); }
        }

        /// <summary>测试用：读文件标签的提示文字。</summary>
        internal string TestFilesNotice()
        {
            try { return filesNotice == null ? "" : (filesNotice.Text ?? ""); }
            catch { return ""; }
        }

        /// <summary>测试用：文件列表行数。</summary>
        internal int TestFilesRowCount()
        {
            try { return filesListPanel == null ? 0 : filesListPanel.Children.Count; }
            catch { return 0; }
        }

        /// <summary>测试用：来源胶囊数。</summary>
        internal int TestFilesChipCount()
        {
            try { return filesChipRow == null ? 0 : filesChipRow.Children.Count; }
            catch { return 0; }
        }

        /// <summary>
        /// 测试用：把失效路径塞进来源多选，返回 NormalizeSource 之后的有效来源列表。
        /// 多选语义下：失效路径会被剔除；全被剔除后为空 = 全选。
        /// </summary>
        internal List<string> TestNormalizeFilesSource(string bogusPath)
        {
            List<string> result = new List<string>();
            try
            {
                if (filesView == null)
                {
                    string old = current;
                    current = FilesCategory;
                    RenderApps();
                    current = old;
                }
                if (filesView == null) return result;

                // 只留失效路径 → ReloadFolders 触发 NormalizeSource 把它剔除
                if (settings != null)
                {
                    settings.FilesSources = new List<string>();
                    settings.FilesSources.Add(bogusPath);
                }
                filesView.TestSetSources(new List<string> { bogusPath });
                filesView.ReloadFolders();
                result = filesView.TestGetSources();
                return result;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "测试：修正来源多选");
                return result;
            }
        }

        /// <summary>测试用：设置搜索框文字（不触发渲染）。</summary>
        internal void TestSetSearchText(string text)
        {
            try
            {
            }
            catch (Exception ex) { Ui.LogError(ex, "测试：设置搜索文字"); }
        }

        /// <summary>测试用：全局搜索结果面板是否可见。</summary>
        internal bool TestSearchResultVisible()
        {
            try
            {
                return searchResultPanel != null
                    && searchResultPanel.Visibility == Visibility.Visible;
            }
            catch { return false; }
        }

        /// <summary>测试用：文件面板是否可见。</summary>
        internal bool TestFilesPanelVisible()
        {
            try
            {
                return filesPanel != null && filesPanel.Visibility == Visibility.Visible;
            }
            catch { return false; }
        }

        /// <summary>测试用：桌面页是否可见。</summary>
        internal bool TestDeskPanelVisible()
        {
            try { return deskPanel != null && deskPanel.Visibility == Visibility.Visible; }
            catch { return false; }
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

        /// <summary>测试用：读搜索框文字。</summary>
        internal string TestSearchText()
        {
            try { return searchBox == null ? "" : (searchBox.Text ?? ""); }
            catch { return ""; }
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

        /// <summary>测试用：读当前分类。</summary>
        internal string TestCurrentCategory()
        {
            try { return current ?? ""; }
            catch { return ""; }
        }

        void NormalizeCurrentCategory()
        {
            try
            {
                // 只有「常用」和「全部」是合法值
                // 合法值：常用 / 全部(空串) / 文件（且 showFilesTab 开着）
                if (current == "常用" || current == "") return;

                if (current == FilesCategory)
                {
                    // showFilesTab 被关掉时，「文件」不再是合法标签 → 回退常用
                    if (settings != null && !settings.ShowFilesTab)
                    {
                        Ui.Log("「文件」标签已关闭，回退到常用");
                        current = "常用";
                        SaveLastCategory();
                    }
                    return;
                }

                // 默认内置「虚拟桌面」永远合法；目录不在就自动重建。
                if (current == VirtualDesktopCategory)
                {
                    try
                    {
                        EnsureVirtualDesks();
                        if (virtualDesks != null) virtualDesks.EnsureDesk(VirtualDesktopCategory);
                    }
                    catch { }
                    return;
                }

                // 自定义标签（虚拟桌面）：只要还在 nav-tabs.json 里就算合法。
                //
                // 不能把它当成「已不存在的分类」：它不对应 catalog 分类，
                // 对应的是磁盘文件夹。目录不在就自动建，不要回退到「常用」。
                if (customTabs != null && customTabs.FindIndex(current) >= 0)
                {
                    // 目录丢了（被手删 / 换盘）→ 重建空目录，标签仍然有效
                    try
                    {
                        if (virtualDesks == null)
                        {
                            virtualDesks = new VirtualDesks(baseDir);
                            virtualDesks.Load();
                        }
                        if (string.IsNullOrEmpty(virtualDesks.Root))
                            virtualDesks.Root = VirtualDesks.DefaultRoot(baseDir);

                        virtualDesks.EnsureDesk(current);
                    }
                    catch { }
                    return;
                }

                Ui.Log("分类「" + current + "」已不存在，回退到常用");
                current = "常用";
                SaveLastCategory();
            }
            catch { }
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
                if (a.Source == AppPaths.SourceStore && !string.IsNullOrEmpty(name))
                    return name + " ·商店";
                return name;
            }
            catch { return a == null ? "" : (a.Name ?? ""); }
        }

        Border MakeTile(AppItem a)
        {
            Border b = new Border();
            b.Width = TileWidthPx;
            b.Height = TileHeightPx;
            b.CornerRadius = new CornerRadius(Ui.R.Sm);
            b.Margin = new Thickness(2);
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;
            b.ToolTip = DisplayNameOf(a);

            // ★ 低价值条目变淡（沉底排序在 AllAppsSorted 里做）
            // 0.45 足够看出"这个不太重要"，又不至于看不清名字
            if (a != null && a.LowValue) b.Opacity = 0.45;

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

            FrameworkElement iconVisual;

            ImageSource icon = AppIcons.Get(a.Path);
            if (icon != null)
            {
                Image img = new Image();
                img.Source = icon;
                img.Width = IconSizePx;
                img.Height = IconSizePx;
                img.HorizontalAlignment = HorizontalAlignment.Center;
                img.VerticalAlignment = VerticalAlignment.Center;
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                iconVisual = img;
            }
            else
            {
                Border ph = new Border();
                ph.Width = IconSizePx;
                ph.Height = IconSizePx;
                ph.CornerRadius = new CornerRadius(Ui.R.Sm);
                ph.Background = Ui.AccentSoft;
                ph.HorizontalAlignment = HorizontalAlignment.Center;
                ph.VerticalAlignment = VerticalAlignment.Center;
                string initial = string.IsNullOrEmpty(a.Name) ? "?" : a.Name.Substring(0, 1);
                TextBlock init = Ui.T(initial, 14.5, Ui.AccentText, FontWeights.SemiBold);
                init.HorizontalAlignment = HorizontalAlignment.Center;
                init.VerticalAlignment = VerticalAlignment.Center;
                ph.Child = init;
                iconVisual = ph;
            }

            iconVisual.RenderTransformOrigin = new Point(0.5, 0.5);
            iconVisual.RenderTransform = iconTrans;

            // 图标外面套一层 Grid，好在右上角叠"运行中"小圆点。
            // 用叠加而不是改图标本身：图标来自系统缓存、可能被多块磁贴共享，
            // 不能往上画东西。
            Grid iconBox = new Grid();
            iconBox.Width = IconSizePx;
            iconBox.Height = IconSizePx;
            iconBox.HorizontalAlignment = HorizontalAlignment.Center;
            iconBox.Margin = new Thickness(0, 0, 0, Ui.S.Xs);
            iconBox.Children.Add(iconVisual);

            // 记下容器，供 RefreshRunningDots 就地增删小圆点
            tileIconBoxes[a.Path] = iconBox;

            // 建磁贴时只读缓存（IsRunningCached 不解析 lnk）：
            // 首次解析 112 个快捷方式要 239 ms，那代价必须留在后台预热里。
            // 缓存还没热的话这里先不画点，等 RefreshRunningDots 补上。
            SyncRunningDot(a, iconBox);

            sp.Children.Add(iconBox);

            TextBlock name = new TextBlock();
            name.Text = DisplayNameOf(a);
            name.FontFamily = Ui.YaHei;
            name.FontSize = 11.5;
            name.Foreground = Ui.Fg;
            name.TextWrapping = TextWrapping.Wrap;
            name.TextAlignment = TextAlignment.Center;
            name.LineHeight = 15;
            name.MaxWidth = 88;

            Border nameClip = new Border();
            nameClip.MaxHeight = 30;
            nameClip.ClipToBounds = true;
            nameClip.Child = name;
            nameClip.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(nameClip);

            b.Child = sp;
            Point downPt = new Point();
            bool tileDrag = false;
            b.MouseEnter += delegate
            {
                if (!ReferenceEquals(b, selectedTile)) b.Background = Ui.Hover;
                // 悬停：放大 + 上浮（参考 GlassBoard 的 Dock 悬停）。
                // 只动 RenderTransform，不触发布局重排。
                iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, IconZoomIn);
                iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, IconZoomIn);
                iconLift.BeginAnimation(TranslateTransform.YProperty, IconLiftUp);
            };
            b.MouseLeave += delegate
            {
                if (!ReferenceEquals(b, selectedTile)) b.Background = Brushes.Transparent;
                iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, IconZoomOut);
                iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, IconZoomOut);
                iconLift.BeginAnimation(TranslateTransform.YProperty, IconLiftDown);
            };
            b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                downPt = e.GetPosition(tilePanel);
                tileDrag = false;
                // 按下态：补全"静止/悬停/按下"三态。
                // 点一个启动稍慢的应用时，用户能看到"按下去了"，
                // 不会怀疑是没点中。
                if (!ReferenceEquals(b, selectedTile)) b.Background = Ui.Pressed;

                // 点击弹跳：0 → -6px → 0。
                // 与悬停上浮共用 TranslateTransform.Y，所以会覆盖悬停状态 ——
                // 这是有意的：弹跳结束后图标停在上浮位置（鼠标还在磁贴上），
                // 视觉上连贯。若弹跳后回到 0 再上浮，会看到"抖两下"。
                iconLift.BeginAnimation(TranslateTransform.YProperty, IconBounce);

                // 鼠标点击磁贴时，键盘选中跟随到它，方向键从当前位置继续
                SelectTile(renderedApps.IndexOf(a), false);
                try { b.CaptureMouse(); } catch { }
            };
            b.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                Point p = e.GetPosition(tilePanel);

                // 拖动阈值 6px：低于这个距离算点击（避免手抖变成拖动）
                if (!tileDrag && (Math.Abs(p.X - downPt.X) > 6 || Math.Abs(p.Y - downPt.Y) > 6))
                {
                    tileDrag = true;
                    b.Opacity = 0.25;          // 原位磁贴变淡，当"占位幽灵"
                    b.Cursor = Cursors.SizeAll;
                    // 拖动时取消悬停放大，否则原位磁贴又大又淡，很怪
                    iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, IconZoomOut);
                    iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, IconZoomOut);
                    iconLift.BeginAnimation(TranslateTransform.YProperty, IconLiftDown);

                    // 抓取点：鼠标相对磁贴左上角的偏移，
                    // 这样浮层不会在开始拖动瞬间"跳"到鼠标中心
                    Point local = e.GetPosition(b);
                    dragGrabOffset = local;

                    BeginDragVisual(a);
                    ShowDragGhost(a, p);
                    Ui.Log("DragStart " + a.Name);
                }

                if (tileDrag)
                {
                    // 浮层跟随鼠标（每帧只改 Transform，不碰布局）
                    MoveDragGhost(p);
                    // 落点计算 + 让位动画
                    UpdateDragPreview(a, p);
                }
            };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                try { b.ReleaseMouseCapture(); } catch { }
                b.Opacity = 1.0;
                b.Cursor = Cursors.Hand;
                // 松开：回到悬停态（鼠标还在磁贴上），而不是透明 ——
                // 否则刚点完会闪一下，像"点完就灭了"
                if (!ReferenceEquals(b, selectedTile)) b.Background = Ui.Hover;
                if (tileDrag)
                {
                    tileDrag = false;
                    // 顺序很重要：先取 insertIndex（要用它算落点），
                    // 再让浮层飞过去，最后提交 + 重建。
                    //
                    // 第一版写反了 —— EndDragVisual 把 insertIndex 重置成 -1，
                    // CommitDrag 拿到的永远是 -1，拖拽完全不生效（实测日志
                    // "CommitDrag ... target=-1" 而 DragPreview 明明算出了 idx=2）。
                    int target = insertIndex;

                    // 收起插入标记和让位位移，但**保留浮层**做落入动画
                    EndDragVisualKeepGhost();

                    // 浮层平滑飞到目标格 → 完成后提交并重建
                    DropDragGhost(a, target);
                }
                else
                {
                    Launch(a);
                }
            };
            b.MouseRightButtonUp += delegate { ShowTileMenu(a); };
            return b;
        }

        /// <summary>
        /// 拖拽开始：把整个 tilePanel 的磁贴位置快照下来，
        /// 供"实时让位"计算每个磁贴该偏移多少。
        /// </summary>
        void BeginDragVisual(AppItem dragged)
        {
            try
            {
                if (tilePanel == null) return;
                draggedApp = dragged;
                if (insertCanvas != null) insertCanvas.Visibility = Visibility.Visible;

                // 磁贴实际列数 = 面板宽度 / 磁贴宽度
                tileColumns = Math.Max(1, (int)(tilePanel.ActualWidth / (TileWidthPx + 4)));
            }
            catch { }
        }

        /// <summary>
        /// 创建拖动浮层（幽灵磁贴）：图标 + 名字 + 放大 + 投影。
        ///
        /// 为什么复制一份而不是直接移动原磁贴：
        /// 原磁贴是 WrapPanel 的子元素，移动它会触发整个网格重排
        /// （其他磁贴全部跳位，卡顿且闪烁）。复制到 Canvas 上绝对定位，
        /// 完全不碰布局 —— 这是所有成熟拖拽实现的通行做法。
        ///
        /// 放大 1.08 + 投影：这是"拿起来了"的视觉暗示。
        /// 不给放大会显得浮层和原位磁贴一样大，看不出哪块被拖。
        /// </summary>
        void ShowDragGhost(AppItem a, Point p)
        {
            try
            {
                if (insertCanvas == null) return;
                HideDragGhost();

                Border ghost = new Border();
                ghost.Width = TileWidthPx;
                ghost.Height = TileHeightPx;
                ghost.CornerRadius = new CornerRadius(Ui.R.Sm);
                ghost.Background = Ui.Bg;          // 实体背景，否则透出下面的磁贴
                ghost.IsHitTestVisible = false;    // 不挡鼠标事件
                ghost.Opacity = 0.96;

                // 投影：拖动时的"悬浮"感
                DropShadowEffect sh = new DropShadowEffect();
                sh.BlurRadius = 18;
                sh.ShadowDepth = 4;
                sh.Direction = 270;
                sh.Opacity = 0.42;
                sh.Color = Colors.Black;
                sh.RenderingBias = RenderingBias.Performance;
                ghost.Effect = sh;

                // 内容：图标 + 名字（与 MakeTile 同构，但不带事件）
                StackPanel sp = new StackPanel();
                sp.VerticalAlignment = VerticalAlignment.Center;

                ImageSource icon = AppIcons.Get(a.Path);
                if (icon != null)
                {
                    Image img = new Image();
                    img.Source = icon;
                    img.Width = IconSizePx;
                    img.Height = IconSizePx;
                    img.HorizontalAlignment = HorizontalAlignment.Center;
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    sp.Children.Add(img);
                }
                else
                {
                    Border ph = new Border();
                    ph.Width = IconSizePx;
                    ph.Height = IconSizePx;
                    ph.CornerRadius = new CornerRadius(Ui.R.Sm);
                    ph.Background = Ui.AccentSoft;
                    ph.HorizontalAlignment = HorizontalAlignment.Center;
                    string initial = string.IsNullOrEmpty(a.Name) ? "?" : a.Name.Substring(0, 1);
                    TextBlock init = Ui.T(initial, 14.5, Ui.AccentText, FontWeights.SemiBold);
                    init.HorizontalAlignment = HorizontalAlignment.Center;
                    init.VerticalAlignment = VerticalAlignment.Center;
                    ph.Child = init;
                    sp.Children.Add(ph);
                }

                TextBlock name = new TextBlock();
                name.Text = DisplayNameOf(a);
                name.FontFamily = Ui.YaHei;
                name.FontSize = 11.5;
                name.Foreground = Ui.Fg;
                name.TextWrapping = TextWrapping.Wrap;
                name.TextAlignment = TextAlignment.Center;
                name.LineHeight = 15;
                name.MaxWidth = 88;
                name.Margin = new Thickness(0, Ui.S.Xs, 0, 0);

                Border clip = new Border();
                clip.MaxHeight = 30;
                clip.ClipToBounds = true;
                clip.Child = name;
                clip.HorizontalAlignment = HorizontalAlignment.Center;
                sp.Children.Add(clip);

                ghost.Child = sp;

                // 位移用 TranslateTransform（每帧改 X/Y 不触发重排）
                dragGhostPos = new TranslateTransform(0, 0);
                ghost.RenderTransform = dragGhostPos;

                // 缩放用 RenderTransformOrigin 中心放大
                dragGhostScale = new ScaleTransform(1.08, 1.08);
                ghost.RenderTransformOrigin = new Point(0.5, 0.5);
                TransformGroup tg = new TransformGroup();
                tg.Children.Add(dragGhostScale);
                tg.Children.Add(dragGhostPos);
                ghost.RenderTransform = tg;

                insertCanvas.Children.Add(ghost);
                insertCanvas.Visibility = Visibility.Visible;
                dragGhost = ghost;

                // ★ 把插入标记提到最上层：浮层后加入，会盖住标记。
                // 实测拖动时只看到浮层、看不到竖条标记 —— 就是层叠顺序问题。
                // 移除再添加 = 移到 Children 末尾（WPF 的 z-order 就是列表顺序）。
                if (insertMarker != null && insertCanvas.Children.Contains(insertMarker))
                {
                    insertCanvas.Children.Remove(insertMarker);
                    insertCanvas.Children.Add(insertMarker);
                }

                // 初始位置：让鼠标正好落在"抓取点"上（不跳）
                Canvas.SetLeft(ghost, 0);
                Canvas.SetTop(ghost, 0);
                dragGhostPos.X = p.X - dragGrabOffset.X;
                dragGhostPos.Y = p.Y - dragGrabOffset.Y;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "创建拖动浮层");
            }
        }

        /// <summary>浮层跟随鼠标（每帧调用，只改 Transform）。</summary>
        void MoveDragGhost(Point p)
        {
            try
            {
                if (dragGhost == null || dragGhostPos == null) return;
                dragGhostPos.X = p.X - dragGrabOffset.X;
                dragGhostPos.Y = p.Y - dragGrabOffset.Y;
            }
            catch { }
        }

        /// <summary>移除浮层。</summary>
        void HideDragGhost()
        {
            try
            {
                if (dragGhost != null && insertCanvas != null)
                {
                    insertCanvas.Children.Remove(dragGhost);
                }
                dragGhost = null;
                dragGhostScale = null;
                dragGhostPos = null;
            }
            catch { }
        }

        /// <summary>
        /// 拖动中：更新插入标记位置 + **让磁贴实时滑开**（GlassBoard 的 livePreview）。
        ///
        /// 落点用**网格坐标**（列/行）算，不遍历子元素边界。
        /// 原实现用 `Mouse.GetPosition` 逐个判断 `p.X >= tl.X && p.X <= tl.X+width`，
        /// 有两个致命问题：
        ///   · 空状态时 tilePanel.Children[0] 是 StackPanel 不是磁贴，index 对不上
        ///   · 鼠标落在磁贴间隙时所有判断都失败 → target = -1（实测日志确实如此）
        ///
        /// 让位动画（这才是"丝滑"的关键）：
        /// 拖动时把 idx 之后的所有磁贴往右推一格，空出落点位置；
        /// 用 TranslateTransform + 200ms EaseOut 滑过去，而不是瞬间跳。
        /// 松手后 RenderApps 重建，位移归零。
        /// </summary>
        void UpdateDragPreview(AppItem dragged, Point p)
        {
            try
            {
                if (tilePanel == null || insertCanvas == null || insertMarker == null) return;
                if (renderedApps == null || renderedApps.Count == 0) return;

                double cellW = TileWidthPx + 4;    // 磁贴宽 + 左右 Margin
                double cellH = TileHeightPx + 4;
                int cols = Math.Max(1, tileColumns);

                // 用浮层的**中心**判断落点，而不是鼠标位置 ——
                // 鼠标可能抓在磁贴边角，用中心更符合"这块磁贴会掉到哪"的直觉
                double cx = p.X - dragGrabOffset.X + TileWidthPx / 2.0;
                double cy = p.Y - dragGrabOffset.Y + TileHeightPx / 2.0;

                int col = (int)Math.Floor(Math.Max(0, cx) / cellW);
                int row = (int)Math.Floor(Math.Max(0, cy) / cellH);
                if (col < 0) col = 0;
                if (col >= cols) col = cols - 1;
                if (row < 0) row = 0;

                int idx = row * cols + col;
                if (idx > renderedApps.Count) idx = renderedApps.Count;
                if (idx == insertIndex) return;    // 没变化就不重画，省开销

                int prev = insertIndex;
                insertIndex = idx;

                // ---- 插入标记位置（胶囊竖条）----
                double mx = col * cellW - 2;
                double my = row * cellH + 6;
                Canvas.SetLeft(insertMarker, mx);
                Canvas.SetTop(insertMarker, my);
                insertMarker.Height = Math.Max(24, TileHeightPx - 12);
                insertMarker.Visibility = Visibility.Visible;

                // ---- 磁贴让位（丝滑的核心）----
                AnimateTilesForInsert(idx, prev);
            }
            catch { }
        }

        /// <summary>
        /// 让 idx 之后的磁贴往右滑一格，给落点腾位置。
        ///
        /// 实现方式：给每个磁贴的**内容**挂一个 TranslateTransform，
        /// 按"是否需要让位"设偏移量，用 120ms EaseOut 动画过渡。
        /// 为什么动画作用在内容而不是 Border 本身：
        /// Border 是 WrapPanel 的直接子元素，动它会影响布局测量；
        /// 内容用 RenderTransform 则完全不碰布局，只是视觉上滑过去。
        ///
        /// 为什么不真的改 Children 顺序做"真实占位"：
        /// 拖动过程中每改一次顺序都要重建磁贴（重新取图标 = 卡顿），
        /// 而视觉上"滑开"已经足够说明落点，松手时一次性提交即可。
        /// </summary>
        void AnimateTilesForInsert(int idx, int prev)
        {
            try
            {
                if (tilePanel == null || renderedApps == null) return;

                int from = renderedApps.IndexOf(draggedApp);
                double shift = TileWidthPx + 4;   // 一格的宽度

                for (int i = 0; i < renderedApps.Count && i < tilePanel.Children.Count; i++)
                {
                    Border b = tilePanel.Children[i] as Border;
                    if (b == null) continue;

                    // 拖动项自己不动（它跟着鼠标，由 Opacity 表示）
                    if (i == from) continue;

                    // 让位规则：
                    //   拖动项往前移（from < idx）→ [from, idx) 的磁贴左移一格
                    //   拖动项往后移（from > idx）→ [idx, from) 的磁贴右移一格
                    double dx = 0;
                    if (from >= 0 && from < idx && i >= from && i < idx) dx = -shift;
                    else if (from >= 0 && from > idx && i >= idx && i < from) dx = shift;

                    FrameworkElement content = b.Child as FrameworkElement;
                    if (content == null) continue;

                    TranslateTransform tt = content.RenderTransform as TranslateTransform;
                    if (tt == null || tt.IsFrozen)
                    {
                        tt = new TranslateTransform();
                        content.RenderTransform = tt;
                    }

                    // 目标位置没变就不动它（避免每帧重启动画）
                    double cur = tt.X;
                    if (Math.Abs(cur - dx) < 0.5) continue;

                    DoubleAnimation anim = new DoubleAnimation(dx, TimeSpan.FromMilliseconds(DragShiftMs));
                    anim.EasingFunction = FrozenCubic();
                    tt.BeginAnimation(TranslateTransform.XProperty, anim);
                }
            }
            catch { }
        }

        /// <summary>当前被拖动的磁贴（让位计算要跳过它）。</summary>
        AppItem draggedApp;

        /// <summary>拖拽结束：收起标记，把让位产生的位移平滑归零。</summary>
        void EndDragVisual()
        {
            try
            {
                insertIndex = -1;
                draggedApp = null;
                if (insertMarker != null) insertMarker.Visibility = Visibility.Collapsed;
                if (insertCanvas != null) insertCanvas.Visibility = Visibility.Collapsed;
                HideDragGhost();

                // 让位位移归零。
                // 为什么要动画而不是直接设 0：松手后 RenderApps 会重建磁贴，
                // 但在"取消拖拽"（落点无效）的情况下不重建 ——
                // 那时如果位移瞬间归零，会看到磁贴"啪"地跳回原位。
                // 用 200ms 滑回去，与让位时的动画对称。
                ResetTileShifts();
            }
            catch { }
        }

        /// <summary>
        /// 拖拽结束但**保留浮层**：用于"平滑落入"动画。
        ///
        /// 与 EndDragVisual 的区别：不清 dragGhost、不隐藏 Canvas，
        /// 因为接下来要让浮层飞到目标格再消失。
        /// </summary>
        void EndDragVisualKeepGhost()
        {
            try
            {
                insertIndex = -1;
                draggedApp = null;
                if (insertMarker != null) insertMarker.Visibility = Visibility.Collapsed;
                // 注意：不调 HideDragGhost()、不隐藏 insertCanvas
                ResetTileShifts();
            }
            catch { }
        }

        /// <summary>把所有磁贴的让位位移平滑归零。</summary>
        void ResetTileShifts()
        {
            try
            {
                if (tilePanel == null) return;
                foreach (UIElement u in tilePanel.Children)
                {
                    Border b = u as Border;
                    if (b == null) continue;
                    FrameworkElement content = b.Child as FrameworkElement;
                    if (content == null) continue;
                    TranslateTransform tt = content.RenderTransform as TranslateTransform;
                    if (tt == null) continue;
                    if (Math.Abs(tt.X) < 0.5) continue;

                    DoubleAnimation back = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(DragShiftMs));
                    back.EasingFunction = FrozenCubic();
                    tt.BeginAnimation(TranslateTransform.XProperty, back);
                }
            }
            catch { }
        }

        /// <summary>
        /// 松手后：浮层平滑飞到目标格，落地后提交排序并重建列表。
        ///
        /// 为什么要有这一步：如果松手瞬间直接 RenderApps()，
        /// 浮层消失 + 磁贴重建是同一帧发生的，视觉上是"啪"地跳过去。
        /// 让浮层先飞 200ms 到目标位置，用户看到的是"图标落进格子里"，
        /// 之后重建时磁贴已经在正确位置，看不出切换。
        ///
        /// 落点无效（target &lt; 0）时直接取消，浮层飞回原位。
        /// </summary>
        void DropDragGhost(AppItem a, int target)
        {
            try
            {
                if (dragGhost == null || dragGhostPos == null || insertCanvas == null)
                {
                    // 没有浮层（异常情况）：直接提交
                    HideDragGhost();
                    if (insertCanvas != null) insertCanvas.Visibility = Visibility.Collapsed;
                    CommitDrag(a, target);
                    return;
                }

                Border ghost = dragGhost;

                // 目标格的中心坐标（tilePanel 坐标系）
                double cellW = TileWidthPx + 4;
                double cellH = TileHeightPx + 4;
                int cols = Math.Max(1, tileColumns);
                double dstX = 0, dstY = 0;
                if (target >= 0 && renderedApps != null)
                {
                    int t = target;
                    if (t > renderedApps.Count) t = renderedApps.Count;
                    int col = t % cols;
                    int row = t / cols;
                    dstX = col * cellW + 2;    // +2 = 磁贴 Margin
                    dstY = row * cellH + 2;
                }
                else
                {
                    // 落点无效：飞回原位（用当前浮层位置，等于原地淡出）
                    dstX = dragGhostPos.X;
                    dstY = dragGhostPos.Y;
                }

                CubicEase ease = FrozenCubic();

                // 位置：200ms 飞到目标格
                DoubleAnimation ax = new DoubleAnimation(dstX, TimeSpan.FromMilliseconds(DragShiftMs));
                ax.EasingFunction = ease;
                DoubleAnimation ay = new DoubleAnimation(dstY, TimeSpan.FromMilliseconds(DragShiftMs));
                ay.EasingFunction = ease;

                // 缩放：从 1.08 回落到 1.0（"放下"的手感）
                if (dragGhostScale != null)
                {
                    DoubleAnimation sx = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(DragShiftMs));
                    sx.EasingFunction = ease;
                    DoubleAnimation sy = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(DragShiftMs));
                    sy.EasingFunction = ease;
                    dragGhostScale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
                    dragGhostScale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
                }

                // 阴影淡出
                DropShadowEffect sh = ghost.Effect as DropShadowEffect;
                if (sh != null)
                {
                    DoubleAnimation fade = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(DragShiftMs));
                    fade.EasingFunction = ease;
                    sh.BeginAnimation(DropShadowEffect.OpacityProperty, fade);
                }

                // 落地后：移除浮层 → 提交排序 → 重建
                ay.Completed += delegate
                {
                    try
                    {
                        HideDragGhost();
                        if (insertCanvas != null) insertCanvas.Visibility = Visibility.Collapsed;
                        CommitDrag(a, target);
                    }
                    catch { }
                };

                dragGhostPos.BeginAnimation(TranslateTransform.XProperty, ax);
                dragGhostPos.BeginAnimation(TranslateTransform.YProperty, ay);
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "拖动落入动画");
                HideDragGhost();
                CommitDrag(a, target);
            }
        }

        /// <summary>
        /// 提交拖拽：按插入下标重排 renderedApps，写入 customOrders 并重渲染。
        ///
        /// target 由调用方传入（在 EndDragVisual 重置之前取好的值）。
        /// </summary>
        void CommitDrag(AppItem a, int target)
        {
            try
            {
                int from = renderedApps == null ? -1 : renderedApps.IndexOf(a);
                Ui.Log("CommitDrag " + a.Name + " from=" + from + " target=" + target);

                if (target < 0) return;
                if (from < 0) return;
                if (from == target) return;

                List<AppItem> newOrder = new List<AppItem>(renderedApps);
                AppItem moved = newOrder[from];
                newOrder.RemoveAt(from);

                // 目标下标是"移除前"的位置，移除后要修正：
                // 若目标在源之后，删掉源会让目标前移一位
                if (target > from) target--;
                if (target < 0) target = 0;
                if (target > newOrder.Count) target = newOrder.Count;

                newOrder.Insert(target, moved);

                List<string> paths = new List<string>();
                foreach (AppItem it in newOrder) paths.Add(it.Path);
                customOrders[current] = paths;
                SaveCustomOrders();
                RenderApps();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "提交拖拽");
            }
        }

        /// <summary>当前磁贴区的列数（拖动落点计算用）。</summary>
        int tileColumns = 1;

        void DropTile(AppItem a)
        {
            try
            {
                if (tilePanel == null || renderedApps == null || renderedApps.Count == 0) return;
                Point p = Mouse.GetPosition(tilePanel);
                int target = -1;
                int n = Math.Min(renderedApps.Count, tilePanel.Children.Count);
                for (int i = 0; i < n; i++)
                {
                    FrameworkElement fe = tilePanel.Children[i] as FrameworkElement;
                    if (fe == null) continue;
                    Point tl = fe.TranslatePoint(new Point(0, 0), tilePanel);
                    if (p.X >= tl.X && p.X <= tl.X + fe.ActualWidth && p.Y >= tl.Y && p.Y <= tl.Y + fe.ActualHeight)
                    {
                        target = i;
                        break;
                    }
                }
                int from = renderedApps.IndexOf(a);
                Ui.Log("DropTile " + a.Name + " from=" + from + " target=" + target + " n=" + n);
                if (target < 0) return;
                if (from < 0 || from == target) return;
                List<AppItem> newOrder = new List<AppItem>(renderedApps);
                AppItem moved = newOrder[from];
                newOrder.RemoveAt(from);
                if (target > newOrder.Count) target = newOrder.Count;
                newOrder.Insert(target, moved);
                List<string> paths = new List<string>();
                foreach (AppItem it in newOrder) paths.Add(it.Path);
                customOrders[current] = paths;
                SaveCustomOrders();
                RenderApps();
            }
            catch { }
        }

        /// <summary>
        /// 手动标记 / 取消低价值。
        ///
        /// 两个列表互斥：标记时从 Unmarked 里移除、加入 Marked；反之亦然。
        /// 优先级：Unmarked（强制不高）> Marked（强制低）> 自动规则。
        /// </summary>
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

                // 提示条宿主：面板底部的 undoHost（和文件页共用样式）
                if (undoBarForDesktop == null && filesUndoHost != null)
                {
                    undoBarForDesktop = new UndoBar(filesUndoHost);
                    undoBarForDesktop.OnFocusBack = delegate
                    {
                        try { if (tilePanel != null) tilePanel.Focus(); } catch { }
                    };
                }
                if (undoBarForDesktop == null) return;

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

                    undoBarForDesktop.Show("已移除桌面快捷方式：" + name, undo, keep);
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

                    undoBarForDesktop.Show("已移除 " + removed.Count + " 个桌面快捷方式", undo);
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "显示桌面移除提示"); }
        }
        /// <summary>桌面移除提示条（与文件页分开，避免互相覆盖）。</summary>
        UndoBar undoBarForDesktop;
        /// <summary>
        /// 单个隐藏后的轻提示：「已隐藏：xxx　[撤销]」。
        ///
        /// 复用 UndoBar（不抢焦点、5 秒消失、点按钮后焦点回到列表）。
        /// 撤销 = 取消隐藏。
        /// </summary>
        void ShowHiddenTip(AppItem a)
        {
            try
            {
                if (a == null) return;

                if (undoBarForDesktop == null && filesUndoHost != null)
                {
                    undoBarForDesktop = new UndoBar(filesUndoHost);
                    undoBarForDesktop.OnFocusBack = delegate
                    {
                        try { if (tilePanel != null) tilePanel.Focus(); } catch { }
                    };
                }
                if (undoBarForDesktop == null) return;

                AppItem captured = a;
                undoBarForDesktop.Show("已隐藏：" + (a.Name ?? ""), delegate
                {
                    try { SetAppHidden(captured, false); }
                    catch (Exception ex) { Ui.LogError(ex, "撤销隐藏"); }
                });
            }
            catch (Exception ex) { Ui.LogError(ex, "显示隐藏提示"); }
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
        void SetLowValue(AppItem a, bool low)
        {
            try
            {
                if (a == null || settings == null || string.IsNullOrEmpty(a.Path)) return;

                if (settings.LowValueMarked == null) settings.LowValueMarked = new List<string>();
                if (settings.LowValueUnmarked == null) settings.LowValueUnmarked = new List<string>();

                // 先从两个列表里都清掉（避免互斥冲突）
                settings.LowValueMarked.RemoveAll(delegate(string p)
                { return string.Equals(p, a.Path, StringComparison.OrdinalIgnoreCase); });
                settings.LowValueUnmarked.RemoveAll(delegate(string p)
                { return string.Equals(p, a.Path, StringComparison.OrdinalIgnoreCase); });

                if (low) settings.LowValueMarked.Add(a.Path);
                else settings.LowValueUnmarked.Add(a.Path);

                settings.Save();

                // 立即重算 + 重渲染（排序会变）
                a.LowValue = low;
                RenderApps();
            }
            catch (Exception ex) { Ui.LogError(ex, "设置低价值标记"); }
        }
        void ShowTileMenu(AppItem a)
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
                hide.ToolTip = "藏起来，不在任何分类里显示；可在「隐藏的项目」里找回";
                hide.Click += delegate { SetAppHidden(a, true); };
                cm.Items.Add(hide);
            }

            // ---- 从常用移除（只在「常用」分类出现）----
            if (showingCommon)
            {
                cm.Items.Add(new Separator());

                MenuItem rm = new MenuItem();
                rm.Header = "从常用移除";
                rm.IsEnabled = a.Usage > 0;
                rm.Click += delegate
                {
                    if (usage != null) usage.Remove(a.Path);
                    a.Usage = 0;
                    a.LastUsed = 0;
                    RebuildNav();
                    RenderApps();
                };
                cm.Items.Add(rm);
            }

            // ---- 低价值标记（手动纠正，优先于自动规则）----
            //
            // 只作用于「全部」页：常用页是"你常用的"，不该有沉底概念。
            if (!showingCommon)
            {
                cm.Items.Add(new Separator());

                if (a.LowValue)
                {
                    MenuItem unmark = new MenuItem();
                    unmark.Header = "取消低价值标记";
                    unmark.ToolTip = "恢复正常排序与不透明度";
                    unmark.Click += delegate { SetLowValue(a, false); };
                    cm.Items.Add(unmark);
                }
                else
                {
                    MenuItem mark = new MenuItem();
                    mark.Header = "标记为低价值";
                    mark.ToolTip = "排到最后并变淡";
                    mark.Click += delegate { SetLowValue(a, true); };
                    cm.Items.Add(mark);
                }
            }

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
                            if (miniBar == null) return;
                            foreach (TaskWindow w in captured) miniBar.Add(w.Hwnd);
                            Ui.Log("钉到迷你任务栏: " + a.Name + " (" + captured.Count + " 个窗口)");
                        }
                        catch (Exception ex) { Ui.LogError(ex, "钉到迷你任务栏"); }
                    };
                }

                cm.Items.Add(pin);
            }

            taskMenuOpen = true;
            cm.Closed += delegate { taskMenuOpen = false; };
            cm.Placement = PlacementMode.MousePoint;
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
        /// 写入 hidden-apps.json，标记内存中的条目，然后刷新界面。
        /// </summary>
        void SetAppHidden(AppItem a, bool hidden)
        {
            try
            {
                if (a == null || string.IsNullOrEmpty(a.Path)) return;
                if (hiddenApps == null) hiddenApps = HiddenApps.Load(baseDir);

                bool changed = hidden ? hiddenApps.Hide(a.Path) : hiddenApps.Show(a.Path);
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

                RebuildNav();
                RenderApps();

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
                    MessageBox.Show("这个应用没有可定位的路径。", "四叶草面板");
                    return;
                }

                string arg;
                string err;
                if (!TryBuildExplorerArgs(path, out arg, out err))
                {
                    MessageBox.Show(err, "四叶草面板");
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
                    MessageBox.Show("无法打开文件位置：" + ex.Message, "四叶草面板");
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
