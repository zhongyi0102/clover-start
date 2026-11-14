using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
    /// 单列纯色设置行、左侧名称说明与右侧原生开关；诊断详情默认折叠。
    /// 深浅色跟随主题（所有颜色走 Ui.* 刷子），修改即时生效。
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
        TextBlock statusValue;
        TextBlock hoverHint;
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
            dlg.Title = AppBrand.Text("四叶草 · 设置");
            dlg.Width = 580;
            dlg.Height = 740;
            dlg.FontFamily = Ui.YaHei;
            dlg.UseLayoutRounding = true;
            dlg.WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen;
            dlg.Owner = owner;
            dlg.Background = Ui.Bg;
            dlg.ResizeMode = ResizeMode.CanResize;
            // 不占任务栏（这是配置对话框，一闪而过）。
            // 配合 DialogChrome.Attach 去掉最小化按钮 ——
            // 否则最小化后任务栏没图标，窗口再也找不回来。
            dlg.ShowInTaskbar = false;
            dlg.MinWidth = 420;
            dlg.MinHeight = 400;

            // 位置记忆：有记录就恢复（此时不要再 CenterOwner），没记录保持居中
            if (WindowPos.Attach(baseDir, WindowPos.Settings, dlg))
                dlg.WindowStartupLocation = WindowStartupLocation.Manual;
            ApplyWorkAreaLimits();
            dlg.LocationChanged += delegate { ApplyWorkAreaLimits(); };

            scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.Padding = new Thickness(Ui.S.Xl, Ui.S.Lg, Ui.S.Xl, Ui.S.Xl);
            scroll.Name = "SettingsScroll";
            scroll.Foreground = Ui.Sub;
            scroll.Template = SettingsScrollTemplate();

            StackPanel col = new StackPanel();
            col.Orientation = Orientation.Vertical;

            // ---------- 常规 ----------
            StackPanel generalHeader = SectionHeader("常规", null);
            generalHeader.Margin = new Thickness(0, 0, 0, Ui.S.Sm);
            sectionAnchors[Section.General] = generalHeader;
            col.Children.Add(generalHeader);

            chkFullscreen = MakeCheck("全屏时隐藏悬浮按钮",
                "看视频、玩游戏时不遮挡全屏画面。",
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

            chkHover = MakeCheck("鼠标移到按钮位置时临时显示",
                null,
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
            chkHover.Margin = new Thickness(Ui.S.Lg, 0, 0, 0);
            col.Children.Add(chkHover);
            hoverHint = Ui.T("", 12, Ui.Sub);
            hoverHint.TextWrapping = TextWrapping.Wrap;
            hoverHint.Margin = new Thickness(Ui.S.Lg + Ui.S.Sm, 0, Ui.S.Sm, Ui.S.Xs);
            col.Children.Add(hoverHint);

            chkAutostart = MakeCheck("开机自启",
                "登录 Windows 后自动启动归位。",
                AutostartEnabled(),
                delegate(bool on)
                {
                    if (AutostartEnabled() != on) ToggleAutostart();
                });
            chkAutostart.Margin = new Thickness(0, Ui.S.Xs, 0, 0);
            col.Children.Add(chkAutostart);

            // ---------- 任务栏 ----------
            StackPanel taskbarHeader = SectionHeader("任务栏", null);
            sectionAnchors[Section.Taskbar] = taskbarHeader;
            col.Children.Add(taskbarHeader);

            chkMiniTaskbar = MakeCheck("迷你任务栏",
                "在面板左下角显示已加入任务栏的窗口。",
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
                "由归位接管位置；关闭开关或退出即可恢复。",
                settings.HideTaskbar,
                delegate(bool on) { SetHideTaskbar(on); });

            // 安全模式 / 功能被关时禁用
            if (!settings.EffectiveTaskbarHiding)
            {
                chkHideTaskbar.IsEnabled = false;
                chkHideTaskbar.ToolTip = Settings.SafeModeActive
                    ? "安全模式下已禁用（避免程序异常时系统任务栏回不来）"
                    : "「允许隐藏系统任务栏」已关闭，当前不可用";
                TextBlock reason = Ui.T(chkHideTaskbar.ToolTip.ToString(), 12, Ui.Sub);
                reason.TextWrapping = TextWrapping.Wrap;
                reason.Margin = new Thickness(Ui.S.Sm, 0, Ui.S.Sm, Ui.S.Xs);
                col.Children.Add(chkHideTaskbar);
                col.Children.Add(reason);
            }
            else col.Children.Add(chkHideTaskbar);

            // ---------- 桌面收纳 ----------
            StackPanel deskHeader = SectionHeader("桌面收纳",
                "收纳内容保存在此目录，可更换存放位置。");
            sectionAnchors[Section.DeskFiles] = deskHeader;
            col.Children.Add(deskHeader);

            Grid deskRow = InfoRow("存放目录", out deskRootValue,
                delegate { CopyText(deskRootValue.Text); });
            col.Children.Add(deskRow);

            WrapPanel deskBtns = new WrapPanel();
            deskBtns.Margin = new Thickness(0, Ui.S.Xs, 0, 0);

            Button changeBtn = SmallButton("更改目录…", delegate { ChangeDeskRoot(); });
            changeBtn.Margin = new Thickness(0, 0, Ui.S.Sm, Ui.S.Sm);
            deskBtns.Children.Add(changeBtn);

            Button openBtn = SmallButton("打开目录", delegate { OpenDeskRoot(); });
            openBtn.Margin = new Thickness(0, 0, Ui.S.Sm, Ui.S.Sm);
            deskBtns.Children.Add(openBtn);

            Button resetBtn = SmallButton("恢复默认", delegate { ResetDeskRoot(); });
            resetBtn.Background = Brushes.Transparent;
            resetBtn.BorderThickness = new Thickness(0);
            resetBtn.Margin = new Thickness(0, 0, 0, Ui.S.Sm);
            deskBtns.Children.Add(resetBtn);

            col.Children.Add(deskBtns);

            // ---------- 维护 ----------
            StackPanel maintHeader = SectionHeader("维护与诊断", null);
            sectionAnchors[Section.Maintenance] = maintHeader;
            col.Children.Add(maintHeader);

            statusValue = Ui.T("读取运行状态…", 13, Ui.Fg);
            statusValue.TextWrapping = TextWrapping.Wrap;
            statusValue.Margin = new Thickness(0, 0, 0, Ui.S.Sm);
            col.Children.Add(statusValue);

            Expander details = new Expander();
            details.Name = "SettingsDiagnostics";
            details.Header = "诊断详情";
            details.Foreground = Ui.Fg;
            details.FontSize = 13;
            details.IsExpanded = false;
            details.Template = DiagnosticsTemplate();
            System.Windows.Automation.AutomationProperties.SetName(details, "诊断详情");
            StackPanel diagnosticRows = new StackPanel();
            diagnosticRows.Margin = new Thickness(Ui.S.Lg, Ui.S.Sm, 0, 0);

            // 任务栏定位
            Grid locRow = InfoRow("任务栏定位", out locationValue,
                delegate { CopyText(locationValue.Text); });
            locationValue.TextWrapping = TextWrapping.Wrap;
            locationValue.TextTrimming = TextTrimming.None;
            diagnosticRows.Children.Add(locRow);

            // 运行状态
            Grid healthRow = InfoRow("运行状态", out healthValue,
                delegate { CopyText(healthValue.Text); });
            healthValue.TextWrapping = TextWrapping.Wrap;
            healthValue.TextTrimming = TextTrimming.None;
            diagnosticRows.Children.Add(healthRow);
            details.Content = diagnosticRows;
            col.Children.Add(details);

            // 复制全部
            Button copyAll = SmallButton("复制全部诊断信息", delegate { CopyAllDiagnostics(); });
            copyAll.HorizontalAlignment = HorizontalAlignment.Left;
            copyAll.Margin = new Thickness(0, Ui.S.Sm, 0, 0);
            col.Children.Add(copyAll);

            col.Children.Add(new Border
            {
                Height = 1,
                Background = Ui.Line,
                Margin = new Thickness(0, Ui.S.Lg, 0, Ui.S.Md)
            });

            Button rollback = SmallButton("撤销系统改动", delegate { DoRollback(); });
            rollback.HorizontalAlignment = HorizontalAlignment.Left;
            rollback.Margin = new Thickness(0, Ui.S.Xs, 0, 0);
            col.Children.Add(rollback);

            TextBlock rollbackHint = Ui.T(
                "恢复系统任务栏，移除自启等系统改动。保留设置和应用清单，执行前需确认。",
                12, Ui.Sub);
            rollbackHint.TextWrapping = TextWrapping.Wrap;
            rollbackHint.Margin = new Thickness(0, Ui.S.Sm, 0, 0);
            col.Children.Add(rollbackHint);

            scroll.Content = col;

            // 设置窗口不占任务栏，与“外观”“整理与隐藏”共用只含「关闭」的
            // 项目标题栏；仍可拖动和从边缘调整大小。
            Grid body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.Children.Add(scroll);
            Border footer = new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(Ui.S.Xl, Ui.S.Md, Ui.S.Xl, Ui.S.Md) };
            footer.Child = Ui.T("修改即时生效", 12, Ui.Sub);
            Grid.SetRow(footer, 1);
            body.Children.Add(footer);
            DialogChrome.UseCloseOnlyTitleBar(dlg, body);

            // Esc 关闭
            dlg.PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { dlg.Close(); e.Handled = true; }
            };

            dlg.Loaded += delegate
            {
                ApplyWorkAreaLimits();
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
            sp.Margin = new Thickness(0, Ui.S.Xl, 0, Ui.S.Sm);

            TextBlock t = Ui.T(title, 16, Ui.Fg);
            t.FontWeight = FontWeights.SemiBold;
            sp.Children.Add(t);

            if (!string.IsNullOrEmpty(sub))
            {
                TextBlock d = Ui.T(sub, 12, Ui.Sub);
                d.TextWrapping = TextWrapping.Wrap;
                d.Margin = new Thickness(0, 2, 0, 0);
                sp.Children.Add(d);
            }

            return sp;
        }

        static CheckBox MakeCheck(string label, string tip, bool isChecked, Action<bool> onToggle)
        {
            CheckBox c = new CheckBox();
            StackPanel text = new StackPanel();
            TextBlock title = Ui.T(label, 13, Ui.Fg);
            title.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(title);
            if (!string.IsNullOrEmpty(tip))
            {
                TextBlock hint = Ui.T(tip, 12, Ui.Sub);
                hint.TextWrapping = TextWrapping.Wrap;
                hint.Margin = new Thickness(0, Ui.S.Xs, 0, 0);
                text.Children.Add(hint);
            }
            c.Content = text;
            c.IsChecked = isChecked;
            c.Foreground = Ui.Fg;
            c.FontFamily = Ui.YaHei;
            c.FontSize = 13;
            c.MinHeight = 44;
            c.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            c.Padding = new Thickness(Ui.S.Sm);
            c.Cursor = Cursors.Hand;
            c.Template = SettingToggleTemplate();
            System.Windows.Automation.AutomationProperties.SetName(c, label);
            System.Windows.Automation.AutomationProperties.SetHelpText(c, tip ?? "");
            if (!string.IsNullOrEmpty(tip)) c.ToolTip = tip;
            c.Checked += delegate { try { if (onToggle != null) onToggle(true); } catch (Exception ex) { Ui.LogError(ex, "设置开关"); } };
            c.Unchecked += delegate { try { if (onToggle != null) onToggle(false); } catch (Exception ex) { Ui.LogError(ex, "设置开关"); } };
            return c;
        }

        Grid InfoRow(string label, out TextBlock valueText, Action onCopy)
        {
            Grid g = new Grid();
            g.Margin = new Thickness(0, Ui.S.Xs, 0, Ui.S.Sm);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock l = Ui.T(label, 12, Ui.Sub);
            l.Margin = new Thickness(0, 0, 0, Ui.S.Xs);
            l.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(l, 0);
            g.Children.Add(l);

            TextBlock v = Ui.T("—", 13, Ui.Fg);
            v.TextTrimming = TextTrimming.CharacterEllipsis;
            v.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(v, 1);
            g.Children.Add(v);
            valueText = v;

            Button copy = SmallButton("复制", onCopy);
            copy.Background = Brushes.Transparent;
            copy.BorderThickness = new Thickness(0);
            System.Windows.Automation.AutomationProperties.SetName(copy, "复制" + label);
            copy.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
            Grid.SetColumn(copy, 1);
            Grid.SetRow(copy, 1);
            g.Children.Add(copy);

            return g;
        }

        static Button SmallButton(string label, Action onClick)
        {
            Button b = new Button();
            b.Template = UiButtons.Template(Ui.R.Sm);
            b.Padding = new Thickness(Ui.S.Md, Ui.S.Xs, Ui.S.Md, Ui.S.Xs);
            b.MinHeight = 32;
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Cursor = Cursors.Hand;
            b.Background = Ui.SearchBg;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(1);
            b.HorizontalContentAlignment = HorizontalAlignment.Center;
            b.VerticalContentAlignment = VerticalAlignment.Center;

            TextBlock t = Ui.T(label, 12, Ui.Fg);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Content = t;
            System.Windows.Automation.AutomationProperties.SetName(b, label);

            b.Click += delegate
            {
                try { if (onClick != null) onClick(); }
                catch (Exception ex) { Ui.LogError(ex, "设置按钮"); }
            };
            return b;
        }

        // 保留原生 CheckBox 的鼠标、Space 和 TogglePattern 语义，只调整布局。
        static ControlTemplate SettingToggleTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(CheckBox));
            FrameworkElementFactory layers = new FrameworkElementFactory(typeof(Grid));
            FrameworkElementFactory chrome = new FrameworkElementFactory(typeof(Border), "SettingChrome");
            chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(Ui.R.Sm));
            chrome.SetBinding(Border.PaddingProperty, new Binding("Padding")
                { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            FrameworkElementFactory row = new FrameworkElementFactory(typeof(DockPanel));
            FrameworkElementFactory track = new FrameworkElementFactory(typeof(Border), "ToggleTrack");
            track.SetValue(DockPanel.DockProperty, Dock.Right);
            track.SetValue(FrameworkElement.WidthProperty, 38.0);
            track.SetValue(FrameworkElement.HeightProperty, 22.0);
            track.SetValue(FrameworkElement.MarginProperty, new Thickness(Ui.S.Lg, 0, 0, 0));
            track.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            track.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
            track.SetBinding(Border.BackgroundProperty, new Binding { Source = Ui.SearchBg });
            track.SetBinding(Border.BorderBrushProperty, new Binding { Source = Ui.Sub });
            track.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            FrameworkElementFactory thumb = new FrameworkElementFactory(typeof(Border), "ToggleThumb");
            thumb.SetValue(FrameworkElement.WidthProperty, 14.0);
            thumb.SetValue(FrameworkElement.HeightProperty, 14.0);
            thumb.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 0, 3, 0));
            thumb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            thumb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            thumb.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            thumb.SetBinding(Border.BackgroundProperty, new Binding { Source = Ui.Sub });
            track.AppendChild(thumb);
            row.AppendChild(track);
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            row.AppendChild(content);
            chrome.AppendChild(row);
            layers.AppendChild(chrome);
            FrameworkElementFactory focus = new FrameworkElementFactory(typeof(Border), "SettingFocus");
            focus.SetValue(Border.CornerRadiusProperty, new CornerRadius(Ui.R.Sm));
            focus.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            focus.SetBinding(Border.BorderBrushProperty, new Binding { Source = Ui.Accent });
            focus.SetValue(UIElement.IsHitTestVisibleProperty, false);
            focus.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
            layers.AppendChild(focus);
            template.VisualTree = layers;

            Trigger on = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true };
            on.Setters.Add(new Setter(Border.BackgroundProperty, new Binding { Source = Ui.Accent }, "ToggleTrack"));
            on.Setters.Add(new Setter(Border.BorderBrushProperty, new Binding { Source = Ui.Accent }, "ToggleTrack"));
            on.Setters.Add(new Setter(Border.BackgroundProperty, new Binding { Source = Ui.AccentText }, "ToggleThumb"));
            on.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right, "ToggleThumb"));
            template.Triggers.Add(on);
            Trigger hover = new Trigger { Property = CheckBox.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new Binding { Source = Ui.Hover }, "SettingChrome"));
            template.Triggers.Add(hover);
            Trigger focused = new Trigger { Property = CheckBox.IsKeyboardFocusedProperty, Value = true };
            focused.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "SettingFocus"));
            template.Triggers.Add(focused);
            Trigger pressed = new Trigger { Property = CheckBox.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, new Binding { Source = Ui.Pressed }, "SettingChrome"));
            template.Triggers.Add(pressed);
            Trigger disabled = new Trigger { Property = CheckBox.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "SettingChrome"));
            template.Triggers.Add(disabled);
            return template;
        }

        static void BindParent(FrameworkElementFactory target, DependencyProperty property, string path)
        {
            target.SetBinding(property, new Binding(path)
                { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        }

        static ControlTemplate SettingsScrollTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(ScrollViewer));
            FrameworkElementFactory root = new FrameworkElementFactory(typeof(DockPanel));
            FrameworkElementFactory bar = new FrameworkElementFactory(typeof(ScrollBar), "PART_VerticalScrollBar");
            bar.SetValue(DockPanel.DockProperty, Dock.Right);
            bar.SetValue(ScrollBar.OrientationProperty, Orientation.Vertical);
            bar.SetValue(FrameworkElement.WidthProperty, 12.0);
            BindParent(bar, UIElement.VisibilityProperty, "ComputedVerticalScrollBarVisibility");
            BindParent(bar, ScrollBar.MaximumProperty, "ScrollableHeight");
            BindParent(bar, ScrollBar.ViewportSizeProperty, "ViewportHeight");
            bar.SetBinding(ScrollBar.ValueProperty, new Binding("VerticalOffset")
                { Mode = BindingMode.OneWay, RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            // Track 不实现 IAddChild，不能用 FrameworkElementFactory.AppendChild。
            // XAML 的属性元素把三个原生部件明确接到 Track，仍由 ScrollViewer 处理滚动。
            ControlTemplate barTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ScrollBar}'>
  <Track x:Name='PART_Track' Orientation='Vertical' IsDirectionReversed='True'>
    <Track.DecreaseRepeatButton>
      <RepeatButton Command='{x:Static ScrollBar.PageUpCommand}' Focusable='False'>
        <RepeatButton.Template><ControlTemplate TargetType='{x:Type RepeatButton}'>
          <Border Background='Transparent'/>
        </ControlTemplate></RepeatButton.Template>
      </RepeatButton>
    </Track.DecreaseRepeatButton>
    <Track.Thumb>
      <Thumb MinHeight='28'>
        <Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'>
          <Border x:Name='Grip' Margin='3,2' CornerRadius='3'
                  Background='{Binding Foreground, RelativeSource={RelativeSource AncestorType={x:Type ScrollViewer}}}' Opacity='0.5'/>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Grip' Property='Opacity' Value='1'/></Trigger>
            <Trigger Property='IsDragging' Value='True'><Setter TargetName='Grip' Property='Opacity' Value='1'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Thumb.Template>
      </Thumb>
    </Track.Thumb>
    <Track.IncreaseRepeatButton>
      <RepeatButton Command='{x:Static ScrollBar.PageDownCommand}' Focusable='False'>
        <RepeatButton.Template><ControlTemplate TargetType='{x:Type RepeatButton}'>
          <Border Background='Transparent'/>
        </ControlTemplate></RepeatButton.Template>
      </RepeatButton>
    </Track.IncreaseRepeatButton>
  </Track>
</ControlTemplate>");
            bar.SetValue(Control.TemplateProperty, barTemplate);
            root.AppendChild(bar);
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ScrollContentPresenter), "PART_ScrollContentPresenter");
            BindParent(presenter, ContentPresenter.ContentProperty, "Content");
            BindParent(presenter, FrameworkElement.MarginProperty, "Padding");
            presenter.SetValue(UIElement.ClipToBoundsProperty, true);
            root.AppendChild(presenter);
            template.VisualTree = root;
            return template;
        }

        static ControlTemplate DiagnosticsTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(Expander));
            FrameworkElementFactory stack = new FrameworkElementFactory(typeof(StackPanel));
            FrameworkElementFactory toggle = new FrameworkElementFactory(typeof(ToggleButton), "HeaderSite");
            toggle.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            toggle.SetValue(FrameworkElement.MinHeightProperty, 32.0);
            toggle.SetBinding(Control.ForegroundProperty, new Binding { Source = Ui.Fg });
            toggle.SetValue(Control.PaddingProperty, new Thickness(Ui.S.Xs));
            toggle.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
            toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsExpanded")
                { Mode = BindingMode.TwoWay, RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            BindParent(toggle, ContentControl.ContentProperty, "Header");
            toggle.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "诊断详情");
            ControlTemplate headerTemplate = new ControlTemplate(typeof(ToggleButton));
            FrameworkElementFactory chrome = new FrameworkElementFactory(typeof(Border), "HeaderChrome");
            chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(Ui.R.Sm));
            chrome.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            chrome.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            BindParent(chrome, Border.PaddingProperty, "Padding");
            FrameworkElementFactory row = new FrameworkElementFactory(typeof(DockPanel));
            FrameworkElementFactory arrow = new FrameworkElementFactory(typeof(TextBlock), "Arrow");
            arrow.SetValue(TextBlock.TextProperty, "\u203A");
            arrow.SetBinding(TextBlock.ForegroundProperty, new Binding { Source = Ui.Sub });
            arrow.SetValue(TextBlock.FontSizeProperty, 16.0);
            arrow.SetValue(FrameworkElement.WidthProperty, 18.0);
            arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            row.AppendChild(arrow);
            FrameworkElementFactory label = new FrameworkElementFactory(typeof(ContentPresenter));
            label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            row.AppendChild(label); chrome.AppendChild(row);
            headerTemplate.VisualTree = chrome;
            Trigger expanded = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            expanded.Setters.Add(new Setter(TextBlock.TextProperty, "\u2304", "Arrow"));
            headerTemplate.Triggers.Add(expanded);
            Trigger hover = new Trigger { Property = ToggleButton.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new Binding { Source = Ui.Hover }, "HeaderChrome"));
            headerTemplate.Triggers.Add(hover);
            Trigger focus = new Trigger { Property = ToggleButton.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, new Binding { Source = Ui.Accent }, "HeaderChrome"));
            headerTemplate.Triggers.Add(focus);
            Trigger pressed = new Trigger { Property = ToggleButton.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, new Binding { Source = Ui.Pressed }, "HeaderChrome"));
            headerTemplate.Triggers.Add(pressed);
            toggle.SetValue(Control.TemplateProperty, headerTemplate);
            stack.AppendChild(toggle);
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter), "DetailsContent");
            content.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            stack.AppendChild(content);
            template.VisualTree = stack;
            Trigger show = new Trigger { Property = Expander.IsExpandedProperty, Value = true };
            show.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "DetailsContent"));
            template.Triggers.Add(show);
            return template;
        }

        void ApplyWorkAreaLimits()
        {
            try
            {
                double scaleX = SystemParameters.PrimaryScreenWidth / System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
                double scaleY = SystemParameters.PrimaryScreenHeight / System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(dlg).Handle;
                System.Windows.Forms.Screen monitor = hwnd != IntPtr.Zero
                    ? System.Windows.Forms.Screen.FromHandle(hwnd)
                    : (dlg.WindowStartupLocation == WindowStartupLocation.Manual
                        && !double.IsNaN(dlg.Left) && !double.IsNaN(dlg.Top)
                        ? System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
                            (int)((dlg.Left + dlg.Width / 2) / scaleX), (int)((dlg.Top + dlg.Height / 2) / scaleY)))
                        : (owner != null
                            ? System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(owner).Handle)
                            : System.Windows.Forms.Screen.PrimaryScreen));
                System.Drawing.Rectangle area = monitor.WorkingArea;
                double maxWidth = Math.Max(1, area.Width * scaleX - Ui.S.Lg);
                double maxHeight = Math.Max(1, area.Height * scaleY - Ui.S.Lg);
                dlg.MinWidth = Math.Min(420, maxWidth);
                dlg.MinHeight = Math.Min(400, maxHeight);
                dlg.MaxWidth = maxWidth;
                dlg.MaxHeight = maxHeight;
                if (dlg.Width > maxWidth) dlg.Width = maxWidth;
                if (dlg.Height > maxHeight) dlg.Height = maxHeight;
            }
            catch (Exception ex) { Ui.LogError(ex, "设置窗口工作区限制"); }
        }

        // ========== 逻辑 ==========

        bool AutostartEnabled()
        {
            try
            {
                return Autostart.Inspect(Autostart.ExecutablePath(baseDir)).Enabled;
            }
            catch { return false; }
        }

        void ToggleAutostart()
        {
            try
            {
                Autostart.Result result = owner != null
                    ? owner.ToggleAutostartPublic()
                    : (AutostartEnabled()
                        ? Autostart.Disable()
                        : Autostart.Enable(Autostart.ExecutablePath(baseDir)));
                // 菜单与窗口同步：重读真实状态
                if (chkAutostart != null)
                {
                    bool on = AutostartEnabled();
                    chkAutostart.IsChecked = on;
                }
                if (!result.Success) Ui.Log("开机自启切换失败：" + result.Message);
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
                        AppBrand.Text("隐藏系统任务栏后，四叶草会接管任务栏位置。\r\n\r\n")
                        + "如何恢复：\r\n"
                        + "  · 关闭本窗口里的「隐藏系统任务栏」开关\r\n"
                        + AppBrand.Text("  · 或右键四叶草 →「退出」\r\n")
                        + "  · 或运行 app\\CloverStart.exe --reset-all\r\n\r\n"
                        + "继续吗？",
                        AppBrand.Text("四叶草 · 隐藏系统任务栏"),
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
                    locationValue.Text = BuildLocationDisplay();
                if (healthValue != null)
                    healthValue.Text = BuildHealthSummary();
                if (statusValue != null)
                {
                    bool required = owner != null ? owner.HideTaskbar : settings.EffectiveHideTaskbar;
                    statusValue.Text = Settings.SafeModeActive ? "安全模式 · 系统接管已停用"
                        : (required ? "系统接管中 · 防护" + ((Guard.IsRunning || WatchdogRunning()) ? "运行中" : "正在启动")
                            : "运行中 · 系统接管未启用");
                }

                // 同步两个与菜单共用的开关
                if (chkAutostart != null) chkAutostart.IsChecked = AutostartEnabled();
                if (chkMiniTaskbar != null) chkMiniTaskbar.IsChecked = settings.MiniTaskbar;
                if (chkHideTaskbar != null) chkHideTaskbar.IsChecked = settings.HideTaskbar;
                if (deskRootValue != null)
                {
                    deskRootValue.Text = CurrentDeskRoot();
                    deskRootValue.ToolTip = deskRootValue.Text;
                }
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
                    MessageBox.Show(dlg, "主面板还没准备好，稍后再试。", AppBrand.Text("四叶草 · 桌面收纳"),
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
                    MessageBox.Show(dlg, pre.Message, AppBrand.Text("四叶草 · 桌面收纳"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string err = panel.ChangeDeskRoot(dlg, picked);
                if (err == null) return;            // 用户取消了三选一，不必提示

                if (err.Length == 0)
                {
                    RefreshValues();
                    MessageBox.Show(dlg, "存放目录已改为：\r\n" + CurrentDeskRoot(),
                        AppBrand.Text("四叶草 · 桌面收纳"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(dlg, err, AppBrand.Text("四叶草 · 桌面收纳"),
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
                    MessageBox.Show(dlg, "已经是默认目录了。", AppBrand.Text("四叶草 · 桌面收纳"),
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
                    MessageBox.Show(dlg, pre.Message, AppBrand.Text("四叶草 · 桌面收纳"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string err = panel.ChangeDeskRoot(dlg, target);
                if (err == null) return;

                if (err.Length == 0)
                {
                    RefreshValues();
                    MessageBox.Show(dlg, "存放目录已改为：\r\n" + CurrentDeskRoot(),
                        AppBrand.Text("四叶草 · 桌面收纳"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(dlg, err, AppBrand.Text("四叶草 · 桌面收纳"),
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
                    fbd.Description = "选择「桌面收纳」的存放目录（所有收纳桌面会建在它下面）";
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
                    AppBrand.Text("四叶草 · 桌面收纳"), MessageBoxButton.OK, MessageBoxImage.Warning);
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

                TaskbarGeometry.Snapshot snapshot = TaskbarGeometry.Current;
                string src = TaskbarGeometry.SourceDisplayName(snapshot.Source);
                string calibration = snapshot.TaskButtonsLocated
                    ? "应用按钮组已校准"
                    : (snapshot.StartLocated ? "开始按钮已校准" : "等待精确校准");

                return edgeName + "，" + Math.Round(TaskbarGeometry.Thickness) + " px"
                    + "（" + src + "，" + calibration + "）";
            }
            catch { return "读取失败"; }
        }

        /// <summary>维护区折叠详情中的任务栏定位信息。</summary>
        string BuildLocationDisplay()
        {
            try
            {
                Rect actual = owner != null ? owner.ActualWindowRect() : TaskbarGeometry.CloverRect();
                string source = owner != null ? owner.PositionSource : "任务栏几何估算（窗口未连接）";
                string calibrated = TriggerWindow.LastLayoutCalibrated == DateTime.MinValue
                    ? "尚未成功"
                    : TriggerWindow.LastLayoutCalibrated.ToString("HH:mm:ss");
                return BuildLocationSummary()
                    + "\r\n实际窗口 " + FmtRect(actual)
                    + "\r\n" + source + "；最后校准 " + calibrated;
            }
            catch { return "读取失败"; }
        }

        string BuildHealthSummary()
        {
            try
            {
                int crashes = ReadCrashCount();
                bool safe = Settings.SafeModeActive;
                bool required = owner != null ? owner.HideTaskbar : settings.EffectiveHideTaskbar;
                bool wd = Guard.IsRunning || WatchdogRunning();

                return "崩溃 " + crashes + " 次"
                    + (safe ? "（安全模式）" : "")
                    + "，看门狗" + (!required ? "按需待机" : (wd ? "运行中" : "正在启动"));
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
                sb.AppendLine(AppBrand.Text("四叶草 · 诊断信息"));
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
                Rect c = owner != null ? owner.ActualWindowRect() : TaskbarGeometry.CloverRect();
                string source = owner != null ? owner.PositionSource : "任务栏几何估算（窗口未连接）";
                string calibrated = TriggerWindow.LastLayoutCalibrated == DateTime.MinValue
                    ? "尚未成功"
                    : TriggerWindow.LastLayoutCalibrated.ToString("yyyy-MM-dd HH:mm:ss");
                return "  " + BuildLocationSummary() + "\r\n"
                    + "  DPI 缩放：" + TaskbarGeometry.ScaleFactor().ToString("0.##") + "\r\n"
                    + "  开始按钮格：" + FmtRect(s) + "\r\n"
                    + AppBrand.Text("  四叶草实际窗口：") + FmtRect(c) + "\r\n"
                    + "  按钮定位来源：" + source + "\r\n"
                    + "  最后按钮组校准：" + calibrated;
            }
            catch { return "  读取失败"; }
        }

        string BuildHealthDetail()
        {
            try
            {
                bool required = owner != null ? owner.HideTaskbar : settings.EffectiveHideTaskbar;
                string watchdog = !required ? "按需待机（仅隐藏系统任务栏时运行）"
                    : ((Guard.IsRunning || WatchdogRunning()) ? "运行中" : "正在启动");
                return "  " + BuildHealthSummary() + "\r\n"
                    + "  安全模式：" + (Settings.SafeModeActive ? "是" : "否") + "\r\n"
                    + "  看门狗：" + watchdog;
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
                MessageBox.Show(dlg, "诊断信息已复制到剪贴板。", AppBrand.Text("四叶草 · 设置"),
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
                string hint = on ? "移开鼠标后再次隐藏。" : "需先开启“全屏时隐藏悬浮按钮”，此选项的设置会保留。";
                if (hoverHint != null) hoverHint.Text = hint;
                System.Windows.Automation.AutomationProperties.SetHelpText(chkHover, hint);
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
            try { DialogChrome.Attach(dlg); dlg.ShowDialog(); }
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
