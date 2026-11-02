using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Clover
{
    /// <summary>
    /// 「设置」窗口：单页、纵向分区块。
    ///
    /// ── 为什么需要它 ──
    ///
    /// 右键菜单之前有 4 个级联子菜单（主题 / 任务栏 / 设置 / 维护与诊断），
    /// 配置项和动作混在一起，找一个开关要点三层。现在菜单只留动作 + 最常用的
    /// 两个开关，其余全收进这里。
    ///
    /// ── 风格 ──
    ///
    /// 直接复用「整理与隐藏」窗口：独立小窗、加粗小标题、灰色小字说明、
    /// 勾选项、圆角灰色小按钮。深浅色跟随主题（所有颜色走 Ui.* 刷子）。
    ///
    /// ── 单例 ──
    ///
    /// 重复点击「设置…」只激活已有窗口，不重复创建。
    ///
    /// ── 即时生效 ──
    ///
    /// 所有开关改动立即写盘并生效，没有「确定 / 应用」按钮。
    /// </summary>
    class SettingsWindow
    {
        // ========== 单例 ==========

        static SettingsWindow instance;

        /// <summary>定位到哪个区块。</summary>
        public enum Section
        {
            General = 0,
            Taskbar = 1,
            DeskFiles = 2,
            Maintenance = 3,
        }

        Window dlg;
        readonly TriggerWindow owner;
        readonly Settings settings;
        readonly PanelWindow panel;
        readonly string baseDir;

        CheckBox chkFullscreen;
        CheckBox chkHover;
        CheckBox chkAutostart;
        CheckBox chkHideTaskbar;
        CheckBox chkMiniTaskbar;

        TextBlock locationValue;
        TextBlock healthValue;
        TextBlock deskRootValue;
        ScrollViewer scroll;
        Section pendingSection = Section.General;

        /// <summary>
        /// 每个区块的锚点元素。滚动定位靠它在滚动内容里的实际位置，
        /// 而不是写死像素偏移 —— 前面加一行说明就会全错位。
        /// </summary>
        readonly Dictionary<Section, FrameworkElement> sectionAnchors =
            new Dictionary<Section, FrameworkElement>();

        // ========== 打开 ==========

        /// <summary>
        /// 打开设置窗口（单例）。已打开时只激活并滚到指定区块。
        /// </summary>
        public static void Open(TriggerWindow owner, Settings settings, PanelWindow panel,
            string baseDir, Section section)
        {
            try
            {
                if (instance != null && instance.dlg != null && instance.dlg.IsVisible)
                {
                    // 已有窗口 → 激活 + 定位，不重复创建
                    instance.pendingSection = section;
                    instance.dlg.Activate();
                    instance.ScrollToSection(section);
                    instance.RefreshValues();
                    return;
                }

                instance = new SettingsWindow(owner, settings, panel, baseDir, section);
                instance.ShowDialog();
                instance = null;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "打开设置窗口");
                instance = null;
            }
        }

        SettingsWindow(TriggerWindow owner, Settings settings, PanelWindow panel,
            string baseDir, Section section)
        {
            this.owner = owner;
            this.settings = settings;
            this.panel = panel;
            this.baseDir = baseDir;
            this.pendingSection = section;

            BuildUi();
        }

        // ========== 构建 ==========

        void BuildUi()
        {
            dlg = new Window();
            dlg.Title = "四叶草 · 设置";
            dlg.Width = 500;
            dlg.Height = 640;
            dlg.WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen;
            dlg.Owner = owner;
            dlg.Background = Ui.Bg;
            dlg.ResizeMode = ResizeMode.CanResize;
            dlg.ShowInTaskbar = false;
            dlg.MinWidth = 420;
            dlg.MinHeight = 400;

            // 位置记忆：有记录就恢复（此时不要再 CenterOwner），没记录保持居中
            if (WindowPos.Attach(baseDir, WindowPos.Settings, dlg))
                dlg.WindowStartupLocation = WindowStartupLocation.Manual;

            scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.Padding = new Thickness(Ui.S.Xl);

            StackPanel col = new StackPanel();
            col.Orientation = Orientation.Vertical;

            // ---------- 常规 ----------
            StackPanel generalHeader = SectionHeader("常规",
                "控制全屏应用运行时悬浮按钮的行为。");
            sectionAnchors[Section.General] = generalHeader;
            col.Children.Add(generalHeader);

            chkFullscreen = MakeCheck("全屏时隐藏悬浮按钮",
                "看视频、玩游戏（全屏）时自动隐藏四叶草按钮，不挡画面",
                settings.FullscreenDetect,
                delegate(bool on)
                {
                    settings.FullscreenDetect = on;
                    settings.Save();
                    OverlayGate.Enabled = settings.EffectiveFullscreenDetect;
                    OverlayGate.Refresh();
                    Ui.Log("全屏检测 -> " + on);
                    UpdateHoverEnabled();
                });
            col.Children.Add(chkFullscreen);

            chkHover = MakeCheck("全屏时鼠标移过去临时显示",
                "全屏时把鼠标移到按钮位置，按钮临时浮现，移开再隐藏",
                settings.HoverReveal,
                delegate(bool on)
                {
                    settings.HoverReveal = on;
                    settings.Save();
                    OverlayGate.HoverReveal = settings.EffectiveHoverReveal;
                    OverlayGate.Refresh();
                    Ui.Log("悬停唤出 -> " + on);
                });
            // 缩进子选项
            chkHover.Margin = new Thickness(22, 2, 0, 0);
            col.Children.Add(chkHover);

            chkAutostart = MakeCheck("开机自启",
                "开机时自动启动四叶草（与右键菜单状态同步）",
                AutostartEnabled(),
                delegate(bool on)
                {
                    if (AutostartEnabled() != on) ToggleAutostart();
                });
            chkAutostart.Margin = new Thickness(0, Ui.S.Md, 0, 0);
            col.Children.Add(chkAutostart);

            // ---------- 任务栏 ----------
            StackPanel taskbarHeader = SectionHeader("任务栏",
                "迷你任务栏显示「显示在任务栏」的窗口；隐藏系统任务栏后由四叶草接管位置。");
            sectionAnchors[Section.Taskbar] = taskbarHeader;
            col.Children.Add(taskbarHeader);

            chkMiniTaskbar = MakeCheck("迷你任务栏",
                "左下角显示“显示在任务栏”加入的窗口按钮；关闭后该功能整体停用",
                settings.MiniTaskbar,
                delegate(bool on)
                {
                    settings.MiniTaskbar = on;
                    settings.Save();
                    if (panel != null) panel.ApplyMiniTaskbarSetting();
                    Ui.Log("迷你任务栏开关 -> " + on);
                });
            col.Children.Add(chkMiniTaskbar);

            chkHideTaskbar = MakeCheck("隐藏系统任务栏",
                "隐藏后由四叶草接管。恢复方式：关闭本开关，或右键四叶草 →「退出」",
                settings.HideTaskbar,
                delegate(bool on) { SetHideTaskbar(on); });

            // 安全模式 / 功能被关时禁用
            if (!settings.EffectiveTaskbarHiding)
            {
                chkHideTaskbar.IsEnabled = false;
                chkHideTaskbar.ToolTip = Settings.SafeModeActive
                    ? "安全模式下已禁用（避免程序异常时系统任务栏回不来）"
                    : "「允许隐藏系统任务栏」已关闭，先在本窗口重新开启";
            }
            col.Children.Add(chkHideTaskbar);

            // ---------- 桌面文件 ----------
            StackPanel deskHeader = SectionHeader("桌面文件",
                "「桌面文件」标签把桌面上的东西收进一个真实文件夹。"
                + "存放目录可以改到别的盘，改完立即生效。");
            sectionAnchors[Section.DeskFiles] = deskHeader;
            col.Children.Add(deskHeader);

            Grid deskRow = InfoRow("存放目录", out deskRootValue,
                delegate { CopyText(deskRootValue.Text); });
            col.Children.Add(deskRow);

            StackPanel deskBtns = new StackPanel();
            deskBtns.Orientation = Orientation.Horizontal;
            deskBtns.Margin = new Thickness(0, Ui.S.Xs, 0, 0);

            Border changeBtn = SmallButton("更改…", delegate { ChangeDeskRoot(); });
            deskBtns.Children.Add(changeBtn);

            Border openBtn = SmallButton("打开目录", delegate { OpenDeskRoot(); });
            openBtn.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
            deskBtns.Children.Add(openBtn);

            Border resetBtn = SmallButton("恢复默认", delegate { ResetDeskRoot(); });
            resetBtn.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
            deskBtns.Children.Add(resetBtn);

            col.Children.Add(deskBtns);

            TextBlock deskHint = Ui.T(
                "更改时会问「搬过去」还是「只改指针」，并写明会搬多少个文件。"
                + "搬过去走核对式移动：全部成功才切换，失败自动搬回原处。",
                10.5, Ui.Sub);
            deskHint.TextWrapping = TextWrapping.Wrap;
            deskHint.Margin = new Thickness(0, Ui.S.Sm, 0, 0);
            col.Children.Add(deskHint);

            // ---------- 维护 ----------
            StackPanel maintHeader = SectionHeader("维护", "诊断信息与恢复");
            sectionAnchors[Section.Maintenance] = maintHeader;
            col.Children.Add(maintHeader);

            col.Children.Add(SubHeader("诊断信息"));

            // 任务栏定位
            Grid locRow = InfoRow("任务栏定位", out locationValue,
                delegate { CopyText(locationValue.Text); });
            col.Children.Add(locRow);

            // 运行状态
            Grid healthRow = InfoRow("运行状态", out healthValue,
                delegate { CopyText(healthValue.Text); });
            col.Children.Add(healthRow);

            // 复制全部
            Border copyAll = SmallButton("复制全部诊断信息", delegate { CopyAllDiagnostics(); });
            copyAll.HorizontalAlignment = HorizontalAlignment.Left;
            copyAll.Margin = new Thickness(0, Ui.S.Sm, 0, 0);
            col.Children.Add(copyAll);

            col.Children.Add(new Border
            {
                Height = 1,
                Background = Ui.Line,
                Margin = new Thickness(0, Ui.S.Lg, 0, Ui.S.Md)
            });

            col.Children.Add(SubHeader("出问题时可恢复到上次正常状态"));

            Border rollback = SmallButton("一键回滚", delegate { DoRollback(); });
            rollback.HorizontalAlignment = HorizontalAlignment.Left;
            rollback.Margin = new Thickness(0, Ui.S.Xs, 0, 0);
            col.Children.Add(rollback);

            TextBlock rollbackHint = Ui.T(
                "撤销本程序对系统做过的所有改动：任务栏可见性、开机自启、看门狗、"
                + "崩溃保护状态。你的设置和应用清单会保留。",
                10.5, Ui.Sub);
            rollbackHint.TextWrapping = TextWrapping.Wrap;
            rollbackHint.Margin = new Thickness(0, Ui.S.Sm, 0, 0);
            col.Children.Add(rollbackHint);

            scroll.Content = col;
            dlg.Content = scroll;

            // Esc 关闭
            dlg.PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { dlg.Close(); e.Handled = true; }
            };

            dlg.Loaded += delegate
            {
                RefreshValues();
                UpdateHoverEnabled();
                ScrollToSection(pendingSection);
            };
        }

        // ========== 控件工厂 ==========

        /// <summary>区块标题 + 灰色小字说明。返回 StackPanel（标题在上、说明在下）。</summary>
        static StackPanel SectionHeader(string title, string sub)
        {
            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Vertical;
            sp.Margin = new Thickness(0, Ui.S.Lg, 0, Ui.S.Sm);

            TextBlock t = Ui.T(title, 13, Ui.Fg);
            t.FontWeight = FontWeights.SemiBold;
            sp.Children.Add(t);

            if (!string.IsNullOrEmpty(sub))
            {
                TextBlock d = Ui.T(sub, 10.5, Ui.Sub);
                d.TextWrapping = TextWrapping.Wrap;
                d.Margin = new Thickness(0, 2, 0, 0);
                sp.Children.Add(d);
            }

            return sp;
        }

        static TextBlock SubHeader(string text)
        {
            TextBlock t = Ui.T(text, 11.5, Ui.Fg);
            t.FontWeight = FontWeights.SemiBold;
            t.Margin = new Thickness(0, Ui.S.Md, 0, Ui.S.Xs);
            return t;
        }

        static CheckBox MakeCheck(string label, string tip, bool isChecked, Action<bool> onToggle)
        {
            CheckBox c = new CheckBox();
            c.Content = label;
            c.IsChecked = isChecked;
            c.Foreground = Ui.Fg;
            c.FontSize = 12;
            c.Margin = new Thickness(0, 3, 0, 3);
            if (!string.IsNullOrEmpty(tip)) c.ToolTip = tip;
            c.Checked += delegate { try { if (onToggle != null) onToggle(true); } catch (Exception ex) { Ui.LogError(ex, "设置开关"); } };
            c.Unchecked += delegate { try { if (onToggle != null) onToggle(false); } catch (Exception ex) { Ui.LogError(ex, "设置开关"); } };
            return c;
        }

        Grid InfoRow(string label, out TextBlock valueText, Action onCopy)
        {
            Grid g = new Grid();
            g.Margin = new Thickness(0, 2, 0, 2);
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions[0].Width = new GridLength(88);
            g.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            g.ColumnDefinitions[2].Width = GridLength.Auto;

            TextBlock l = Ui.T(label + "：", 10.5, Ui.Sub);
            l.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(l, 0);
            g.Children.Add(l);

            TextBlock v = Ui.T("—", 10.5, Ui.Fg);
            v.TextTrimming = TextTrimming.CharacterEllipsis;
            v.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            valueText = v;

            Border copy = SmallButton("复制", onCopy);
            copy.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
            Grid.SetColumn(copy, 2);
            g.Children.Add(copy);

            return g;
        }

        static Border SmallButton(string label, Action onClick)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(10);
            b.Padding = new Thickness(10, 2, 10, 2);
            b.Cursor = Cursors.Hand;
            b.Background = Ui.SearchBg;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(1);

            TextBlock t = Ui.T(label, 10.5, Ui.Fg);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = Ui.SearchBg; };
            b.MouseLeftButtonUp += delegate
            {
                try { if (onClick != null) onClick(); }
                catch (Exception ex) { Ui.LogError(ex, "设置按钮"); }
            };
            return b;
        }

        // ========== 逻辑 ==========

        bool AutostartEnabled()
        {
            try
            {
                string lnk = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    "四叶草启动器.lnk");
                return File.Exists(lnk);
            }
            catch { return false; }
        }

        void ToggleAutostart()
        {
            try
            {
                if (owner != null) owner.ToggleAutostartPublic();
                // 菜单与窗口同步：重读真实状态
                if (chkAutostart != null)
                {
                    bool on = AutostartEnabled();
                    chkAutostart.IsChecked = on;
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "切换开机自启"); }
        }

        void SetHideTaskbar(bool on)
        {
            try
            {
                // 开启时弹一次确认，说明如何恢复
                if (on)
                {
                    MessageBoxResult r = MessageBox.Show(dlg,
                        "隐藏系统任务栏后，四叶草会接管任务栏位置。\r\n\r\n"
                        + "如何恢复：\r\n"
                        + "  · 关闭本窗口里的「隐藏系统任务栏」开关\r\n"
                        + "  · 或右键四叶草 →「退出」\r\n"
                        + "  · 或运行 app\\CloverStart.exe --reset-all\r\n\r\n"
                        + "继续吗？",
                        "四叶草 · 隐藏系统任务栏",
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                        MessageBoxResult.Cancel);   // 默认焦点在「取消」

                    if (r != MessageBoxResult.OK)
                    {
                        if (chkHideTaskbar != null) chkHideTaskbar.IsChecked = false;
                        return;
                    }
                }

                if (owner != null) owner.SetHideTaskbarPublic(on);

                if (chkHideTaskbar != null) chkHideTaskbar.IsChecked = on;
                RefreshValues();
            }
            catch (Exception ex) { Ui.LogError(ex, "切换隐藏系统任务栏"); }
        }

        /// <summary>刷新诊断文字。</summary>
        void RefreshValues()
        {
            try
            {
                if (locationValue != null)
                    locationValue.Text = BuildLocationSummary();
                if (healthValue != null)
                    healthValue.Text = BuildHealthSummary();

                // 同步两个与菜单共用的开关
                if (chkAutostart != null) chkAutostart.IsChecked = AutostartEnabled();
                if (chkMiniTaskbar != null) chkMiniTaskbar.IsChecked = settings.MiniTaskbar;
                if (chkHideTaskbar != null) chkHideTaskbar.IsChecked = settings.HideTaskbar;
                if (deskRootValue != null) deskRootValue.Text = CurrentDeskRoot();
            }
            catch (Exception ex) { Ui.LogError(ex, "刷新设置窗口"); }
        }

        // ========== 桌面存放目录 ==========

        /// <summary>当前存放目录；panel 没起来时回退到默认值。</summary>
        string CurrentDeskRoot()
        {
            try
            {
                if (panel != null)
                {
                    string r = panel.DeskRootPath();
                    if (!string.IsNullOrEmpty(r)) return r;
                }
                return VirtualDesks.DefaultRoot(baseDir);
            }
            catch { return ""; }
        }

        /// <summary>「更改…」：选目录 → 交给 panel 校验 / 盘点 / 三选一 / 迁移。</summary>
        void ChangeDeskRoot()
        {
            try
            {
                if (panel == null)
                {
                    MessageBox.Show(dlg, "主面板还没准备好，稍后再试。", "四叶草 · 桌面文件",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string picked = PickFolder(CurrentDeskRoot());
                if (string.IsNullOrEmpty(picked)) return;   // 用户取消

                // 提前校验一次：选错层级（比如选成了某个桌面文件夹本身）当场说清，
                // 不要等到三选一弹窗之后再报错。
                DeskRootChange.Check pre = DeskRootChange.Validate(picked, CurrentDeskRoot());
                if (!pre.Ok)
                {
                    MessageBox.Show(dlg, pre.Message, "四叶草 · 桌面文件",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string err = panel.ChangeDeskRoot(dlg, picked);
                if (err == null) return;            // 用户取消了三选一，不必提示

                if (err.Length == 0)
                {
                    RefreshValues();
                    MessageBox.Show(dlg, "存放目录已改为：\r\n" + CurrentDeskRoot(),
                        "四叶草 · 桌面文件", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(dlg, err, "四叶草 · 桌面文件",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    RefreshValues();
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "更改桌面存放目录"); }
        }

        /// <summary>「打开目录」：在资源管理器里打开当前存放根目录。</summary>
        void OpenDeskRoot()
        {
            try
            {
                string root = CurrentDeskRoot();
                if (string.IsNullOrEmpty(root)) return;

                if (!Directory.Exists(root))
                {
                    Directory.CreateDirectory(root);
                }
                System.Diagnostics.Process.Start("explorer.exe", "\"" + root + "\"");
            }
            catch (Exception ex) { Ui.LogError(ex, "打开桌面存放目录"); }
        }

        /// <summary>「恢复默认」：回到程序目录下的 desks（同样走三选一）。</summary>
        void ResetDeskRoot()
        {
            try
            {
                string def = VirtualDesks.DefaultRoot(baseDir);
                if (string.IsNullOrEmpty(def)) return;

                if (string.Equals(DeskRootChange.SafeFull(def),
                    DeskRootChange.SafeFull(CurrentDeskRoot()),
                    StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(dlg, "已经是默认目录了。", "四叶草 · 桌面文件",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                ChangeDeskRootTo(def);
            }
            catch (Exception ex) { Ui.LogError(ex, "恢复默认桌面存放目录"); }
        }

        /// <summary>与 ChangeDeskRoot 相同，但目标目录已确定（「恢复默认」用）。</summary>
        void ChangeDeskRootTo(string target)
        {
            try
            {
                if (panel == null) return;

                DeskRootChange.Check pre = DeskRootChange.Validate(target, CurrentDeskRoot());
                if (!pre.Ok)
                {
                    MessageBox.Show(dlg, pre.Message, "四叶草 · 桌面文件",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string err = panel.ChangeDeskRoot(dlg, target);
                if (err == null) return;

                if (err.Length == 0)
                {
                    RefreshValues();
                    MessageBox.Show(dlg, "存放目录已改为：\r\n" + CurrentDeskRoot(),
                        "四叶草 · 桌面文件", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(dlg, err, "四叶草 · 桌面文件",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    RefreshValues();
                }
            }
            catch (Exception ex) { Ui.LogError(ex, "更改桌面存放目录"); }
        }

        /// <summary>系统自带选目录对话框，从当前存放目录的位置打开。</summary>
        string PickFolder(string startAt)
        {
            try
            {
                using (System.Windows.Forms.FolderBrowserDialog fbd =
                    new System.Windows.Forms.FolderBrowserDialog())
                {
                    fbd.Description = "选择「桌面文件」的存放目录（所有桌面文件夹会建在它下面）";
                    fbd.ShowNewFolderButton = true;

                    try
                    {
                        if (!string.IsNullOrEmpty(startAt) && Directory.Exists(startAt))
                            fbd.SelectedPath = startAt;
                    }
                    catch { }

                    if (fbd.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
                    return fbd.SelectedPath;
                }
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "选目录对话框");
                MessageBox.Show(dlg, "打不开选目录对话框：" + ex.Message,
                    "四叶草 · 桌面文件", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
        }

        string BuildLocationSummary()
        {
            try
            {
                TaskbarGeometry.Refresh();

                string edgeName;
                switch (TaskbarGeometry.CurrentEdge)
                {
                    case TaskbarGeometry.Edge.Top: edgeName = "顶部"; break;
                    case TaskbarGeometry.Edge.Left: edgeName = "左侧"; break;
                    case TaskbarGeometry.Edge.Right: edgeName = "右侧"; break;
                    default: edgeName = "底部"; break;
                }

                string src = TaskbarGeometry.LastSource;
                if (src == "workArea") src = "工作区推断";
                else if (src == "trayWindow") src = "任务栏窗口矩形";
                else src = "回退估算（降级）";

                return edgeName + "，" + Math.Round(TaskbarGeometry.Thickness) + " px"
                    + "（" + src + (TaskbarGeometry.PreciseLocated ? "，已校准" : "，未校准") + "）";
            }
            catch { return "读取失败"; }
        }

        string BuildHealthSummary()
        {
            try
            {
                int crashes = ReadCrashCount();
                bool safe = Settings.SafeModeActive;
                bool wd = WatchdogRunning();

                return "崩溃 " + crashes + " 次"
                    + (safe ? "（安全模式）" : "")
                    + "，看门狗" + (wd ? "运行中" : "未运行");
            }
            catch { return "读取失败"; }
        }

        /// <summary>读崩溃计数（crash-guard.json 的 crashes 数组长度）。</summary>
        int ReadCrashCount()
        {
            try
            {
                string p = Path.Combine(baseDir, "config", "crash-guard.json");
                if (!File.Exists(p)) return 0;

                string json = JsonIO.Read(p);
                if (string.IsNullOrEmpty(json)) return 0;

                System.Web.Script.Serialization.JavaScriptSerializer ser =
                    new System.Web.Script.Serialization.JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return 0;

                object arr;
                if (!d.TryGetValue("crashes", out arr) || arr == null) return 0;

                int n = 0;
                foreach (object o in JsonIO.AsArray(arr)) n++;
                return n;
            }
            catch { return 0; }
        }

        bool WatchdogRunning()
        {
            try
            {
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
                {
                    try
                    {
                        if (string.Equals(p.ProcessName, "CloverStart", StringComparison.OrdinalIgnoreCase)
                            && p.Id != System.Diagnostics.Process.GetCurrentProcess().Id)
                            return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        string BuildAllDiagnostics()
        {
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("四叶草 · 诊断信息");
                sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine();
                sb.AppendLine("【任务栏定位】");
                sb.AppendLine(BuildLocationDetail());
                sb.AppendLine();
                sb.AppendLine("【运行状态】");
                sb.AppendLine(BuildHealthDetail());
                sb.AppendLine();
                sb.AppendLine("【设置】");
                sb.AppendLine("  主题：" + settings.Theme);
                sb.AppendLine("  开机自启：" + AutostartEnabled());
                sb.AppendLine("  迷你任务栏：" + settings.MiniTaskbar);
                sb.AppendLine("  隐藏系统任务栏：" + settings.HideTaskbar);
                sb.AppendLine("  全屏隐藏：" + settings.FullscreenDetect);
                sb.AppendLine("  悬停唤出：" + settings.HoverReveal);
                return sb.ToString();
            }
            catch (Exception ex) { return "生成失败: " + ex.Message; }
        }

        string BuildLocationDetail()
        {
            try
            {
                TaskbarGeometry.Refresh();
                Rect s = TaskbarGeometry.StartCell;
                Rect c = TaskbarGeometry.CloverRect();
                return "  " + BuildLocationSummary() + "\r\n"
                    + "  DPI 缩放：" + TaskbarGeometry.ScaleFactor().ToString("0.##") + "\r\n"
                    + "  开始按钮格：" + FmtRect(s) + "\r\n"
                    + "  四叶草按钮：" + FmtRect(c);
            }
            catch { return "  读取失败"; }
        }

        string BuildHealthDetail()
        {
            try
            {
                return "  " + BuildHealthSummary() + "\r\n"
                    + "  安全模式：" + (Settings.SafeModeActive ? "是" : "否") + "\r\n"
                    + "  看门狗：" + (WatchdogRunning() ? "运行中" : "未运行");
            }
            catch { return "  读取失败"; }
        }

        static string FmtRect(Rect r)
        {
            return "(" + Math.Round(r.X) + ", " + Math.Round(r.Y) + ") "
                + Math.Round(r.Width) + "×" + Math.Round(r.Height);
        }

        void CopyText(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return;
                Clipboard.SetText(text);
            }
            catch (Exception ex) { Ui.LogError(ex, "复制诊断信息"); }
        }

        void CopyAllDiagnostics()
        {
            try
            {
                string all = BuildAllDiagnostics();
                Clipboard.SetText(all);
                MessageBox.Show(dlg, "诊断信息已复制到剪贴板。", "四叶草 · 设置",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { Ui.LogError(ex, "复制全部诊断信息"); }
        }

        void DoRollback()
        {
            try
            {
                if (owner == null) return;
                owner.PromptRollbackPublic(dlg);
            }
            catch (Exception ex) { Ui.LogError(ex, "一键回滚"); }
        }

        void UpdateHoverEnabled()
        {
            try
            {
                if (chkHover == null || chkFullscreen == null) return;

                // 前者关闭时子选项置灰
                bool on = chkFullscreen.IsChecked == true;
                chkHover.IsEnabled = on;
                chkHover.Opacity = on ? 1.0 : 0.45;
            }
            catch { }
        }

        void ScrollToSection(Section section)
        {
            try
            {
                if (scroll == null) return;

                FrameworkElement anchor;
                if (!sectionAnchors.TryGetValue(section, out anchor) || anchor == null)
                {
                    scroll.ScrollToTop();
                    return;
                }

                if (section == Section.General)
                {
                    scroll.ScrollToTop();
                    return;
                }

                // 排到布局之后再滚。
                //
                // 为什么不能在 Loaded 里直接 BringIntoView：那时布局还没跑完，
                // 元素的最终位置未知，ScrollViewer 会把请求算成 0 —— 实测
                // 「点设置 ▸ 诊断」打开后仍停在顶部，看不到维护区块。
                dlg.Dispatcher.BeginInvoke(new Action(delegate { DoScrollTo(anchor); }),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch { }
        }

        /// <summary>按锚点在滚动内容里的实际位置滚过去（不依赖 BringIntoView 的时序）。</summary>
        void DoScrollTo(FrameworkElement anchor)
        {
            try
            {
                if (scroll == null || anchor == null) return;

                anchor.UpdateLayout();

                GeneralTransform t = anchor.TransformToAncestor(scroll);
                Point p = t.Transform(new Point(0, 0));

                double target = scroll.VerticalOffset + p.Y - Ui.S.Xs;
                if (target < 0) target = 0;

                scroll.ScrollToVerticalOffset(target);

                // 兜底：目标位置在视口里但被裁掉一点时，让 WPF 自己微调
                anchor.BringIntoView();
            }
            catch
            {
                try { if (anchor != null) anchor.BringIntoView(); } catch { }
            }
        }

        void ShowDialog()
        {
            try { dlg.ShowDialog(); }
            catch (Exception ex) { Ui.LogError(ex, "显示设置窗口"); }
        }

        // ========== 测试钩子 ==========

        /// <summary>
        /// 构造一个设置窗口并返回它（**不 ShowDialog**），供验收测试检查控件树。
        ///
        /// 为什么不直接 `SettingsWindow.Open`：那是模态阻塞的，无人值守跑不了。
        /// 为什么不反射私有构造：私有签名一改，测试就静默失效 —— 宁可多一个
        /// `internal` 钩子，让它跟着编译一起报错。
        /// </summary>
        internal static Window BuildForTest(TriggerWindow owner, Settings settings,
            PanelWindow panel, string baseDir, Section section)
        {
            try
            {
                SettingsWindow w = new SettingsWindow(owner, settings, panel, baseDir, section);
                return w.dlg;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "构造设置窗口（测试）");
                return null;
            }
        }
    }
}
