using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 「外观…」设置窗口：预设选择 + 参数滑块 + 颜色选择。
    ///
    /// **设计约束**
    ///
    /// 1. **不引入第三方控件**：按钮与色板用原生 Button，滑块用原生 Slider。
    /// 2. **拖动实时预览，松手才写盘**：
    ///    · 拖动中：只改 settings 内存值 + 调 panel.ApplyGlassSettingPreview()
    ///      （降采样 + 短防抖，几毫秒一次）
    ///    · 松手（Thumb.DragCompleted）或关窗：一次原子写盘 + 全尺寸重算
    ///    这样拖 100 次不会写 100 次盘。
    /// 3. **窗口跟随主题**：所有颜色走 Ui.* 刷子（未冻结，改 .Color 自动重绘）。
    /// 4. **任何异常都回退纯色**：出错只写日志，不让面板打不开。
    /// </summary>
    static class AppearanceDialog
    {
        /// <summary>预设按钮的尺寸。</summary>
        const int PresetBtnW = 88;
        const int PresetBtnH = 30;

        /// <summary>
        /// 打开外观窗口。owner 为空时也能开（独立窗口）。
        /// </summary>
        /// <param name="owner">父窗口（用于居中）</param>
        /// <param name="settings">当前设置（**直接改这个对象**）</param>
        /// <param name="panel">面板（用于实时预览；可为 null）</param>
        /// <param name="onCommit">写盘并全尺寸重算的回调（松手/关窗时调）</param>
        public static void Show(Window owner, string baseDir, Settings settings, PanelWindow panel, Action onCommit)
        {
            try
            {
                Window dlg = new Window();
                dlg.Title = "外观";
                dlg.Width = DefaultW;
                dlg.Height = DefaultH;
                dlg.FontFamily = Ui.YaHei;
                // 与另外两个长期配置窗口统一使用项目标题栏，只保留「关闭」。
                // 四边四角拖拽由 WindowChrome + WM_NCHITTEST 钩子保留。
                dlg.Background = Ui.Bg;
                // 四边保留缩放热区，不再叠加内外两套 ResizeGrip。
                dlg.ResizeMode = ResizeMode.CanResize;
                dlg.MinWidth = 360;
                dlg.MinHeight = 300;
                // 最大不超过屏幕工作区。初值按主屏算，窗口移动/尺寸变化时
                // 再按所在屏幕重算（多显示器时上限跟着走）。
                ApplyWorkAreaLimits(dlg);
                // 不占任务栏（这是配置对话框，一闪而过）。
                dlg.ShowInTaskbar = false;
                dlg.WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen;
                if (owner != null) dlg.Owner = owner;

                // 位置记忆：有记录就恢复（此时不要再 CenterOwner）
                // ── 关键：不允许以最大化状态恢复 ──
                //
                // 历史记录里可能有 "maximized": true（早先拖到屏幕顶边被 Aero Snap
                // 最大化后关窗就存下来了）。最大化窗口在 Windows 里本来就不能
                // 拖边缘缩放 —— 四边四角全失效，且尺寸等于整个工作区。
                if (WindowPos.Attach(baseDir, WindowPos.Appearance, dlg))
                {
                    dlg.WindowStartupLocation = WindowStartupLocation.Manual;
                    if (dlg.WindowState == WindowState.Maximized)
                    {
                        dlg.WindowState = WindowState.Normal;
                        Ui.Log("外观窗口记录为最大化，已强制还原（最大化下无法拖边缩放）");
                    }
                }

                // 记录里可能存着撑满工作区的尺寸（最大化时存下来的），收回到合理大小。
                ClampToReasonableSize(dlg);

                // ---- 预览防抖 ----
                //
                // 拖动滑块会连发几十次 ValueChanged。每次都全尺寸重算会卡，
                // 所以：
                //   · 拖动中：120ms 防抖 + 降采样预览（panel 内部处理）
                //   · 松手：立刻全尺寸重算
                DispatcherTimer previewTimer = new DispatcherTimer();
                previewTimer.Interval = TimeSpan.FromMilliseconds(120);
                bool previewPending = false;

                Action doPreview = delegate
                {
                    try
                    {
                        if (panel != null) panel.ApplyGlassPreview();
                    }
                    catch (Exception ex) { Ui.LogError(ex, "外观预览"); }
                };

                previewTimer.Tick += delegate
                {
                    previewTimer.Stop();
                    if (previewPending) { previewPending = false; doPreview(); }
                };

                Action schedulePreview = delegate
                {
                    previewPending = true;
                    previewTimer.Stop();
                    previewTimer.Start();
                };

                Action commit = delegate
                {
                    try
                    {
                        previewTimer.Stop();
                        previewPending = false;
                        if (settings != null) settings.Save();
                        if (panel != null) panel.ApplyGlassSetting();
                        if (onCommit != null) onCommit();
                    }
                    catch (Exception ex) { Ui.LogError(ex, "外观提交"); }
                };

                // ---- 根容器 ----
                Border root = new Border();
                root.Background = Ui.Bg;

                Grid layout = new Grid();
                layout.RowDefinitions.Add(new RowDefinition());   // 标题
                layout.RowDefinitions.Add(new RowDefinition());   // 内容
                layout.RowDefinitions.Add(new RowDefinition());   // 底部
                layout.RowDefinitions[0].Height = GridLength.Auto;
                layout.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
                layout.RowDefinitions[2].Height = GridLength.Auto;

                // ---- 内容（可滚动）----
                ScrollViewer scroll = new ScrollViewer();
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                scroll.Margin = new Thickness(Ui.S.Xl, Ui.S.Sm, Ui.S.Xl, Ui.S.Sm);

                StackPanel body = new StackPanel();
                scroll.Content = body;
                Grid.SetRow(scroll, 1);
                layout.Children.Add(scroll);

                // ===== 主题（三选一，原右键菜单「主题 ▸」迁移至此）=====
                body.Children.Add(SectionLabel("主题"));

                TextBlock themeHint = Ui.T("跟随系统时，Windows 切深浅色本程序会自动跟随", 10.5, Ui.Sub);
                themeHint.Margin = new Thickness(0, 2, 0, 0);
                body.Children.Add(themeHint);

                StackPanel themeRow = new StackPanel();
                themeRow.Orientation = Orientation.Horizontal;
                themeRow.Margin = new Thickness(0, Ui.S.Sm, 0, Ui.S.Lg);

                Ui.ThemeMode curTheme = Ui.ParseTheme(settings.Theme);
                List<Button> themeChips = new List<Button>();

                foreach (Ui.ThemeMode m in new Ui.ThemeMode[]
                    { Ui.ThemeMode.System, Ui.ThemeMode.Light, Ui.ThemeMode.Dark })
                {
                    Ui.ThemeMode mm = m;
                    bool on = (mm == curTheme);

                    Button chip = MakeChip(Ui.ThemeDisplayName(mm), null);
                    chip.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
                    SetChipActive(chip, on);

                    themeChips.Add(chip);

                    chip.Click += delegate
                    {
                        try
                        {
                            settings.Theme = Ui.ThemeToString(mm);
                            settings.Save();
                            Ui.ApplyTheme(mm);
                            Ui.Log("主题 -> " + Ui.ThemeDisplayName(mm));

                            // 重画所有用旧主题颜色的地方
                            if (panel != null) { try { panel.ApplyTheme(); } catch { } }
                            if (onCommit != null) { try { onCommit(); } catch { } }

                            // 刷新三个胶囊的选中态
                            int idx = 0;
                            foreach (Ui.ThemeMode each in new Ui.ThemeMode[]
                                { Ui.ThemeMode.System, Ui.ThemeMode.Light, Ui.ThemeMode.Dark })
                            {
                                Button bc = themeChips[idx++];
                                bool sel = (each == mm);
                                SetChipActive(bc, sel);
                            }
                        }
                        catch (Exception ex) { Ui.LogError(ex, "切换主题"); }
                    };

                    themeRow.Children.Add(chip);
                }

                body.Children.Add(themeRow);

                // Manual settings are preserved; the entry switches mode rather than
                // presenting a disabled expander that looks clickable.
                StackPanel manualBody = body;
                manualBody.Visibility = Visibility.Collapsed;
                Button manualSettings = MakeChip("手动调整", null);
                manualSettings.HorizontalAlignment = HorizontalAlignment.Left;
                manualSettings.Margin = new Thickness(0, Ui.S.Md, 0, Ui.S.Sm);
                body = new StackPanel();
                scroll.Content = body;
                TextBlock heading = Ui.T("让背景与内容更协调", 16, Ui.Fg, FontWeights.SemiBold);
                heading.Margin = new Thickness(0, Ui.S.Md, 0, Ui.S.Sm);
                body.Children.Add(heading);
                CheckBox autoMode = new CheckBox { Content = "自动适配壁纸", Foreground = Ui.Fg,
                    IsChecked = settings != null && settings.AutoAppearance,
                    FontSize = 13, MinHeight = 36, VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, Ui.S.Sm, 0, Ui.S.Sm) };
                body.Children.Add(autoMode);
                TextBlock autoHint = Ui.T("自动选择明暗、配色和文字保护。无需逐项调节，原手动参数仍保留。", 12, Ui.Sub);
                autoHint.TextWrapping = TextWrapping.Wrap;
                body.Children.Add(autoHint);
                TextBlock autoStatus = Ui.T("", 12, Ui.Sub);
                autoStatus.Margin = new Thickness(0, Ui.S.Sm, 0, Ui.S.Md);
                autoStatus.TextWrapping = TextWrapping.Wrap;
                body.Children.Add(autoStatus);
                Action fitContent = null;
                Button reset = null;
                bool? lastAutomatic = null;
                Action refreshAutomatic = delegate
                {
                    bool automatic = settings != null && settings.AutoAppearance;
                    autoMode.IsChecked = automatic;
                    manualBody.Visibility = automatic ? Visibility.Collapsed : Visibility.Visible;
                    manualSettings.Content = automatic ? "手动调整…" : "返回自动适配";
                    manualSettings.ToolTip = automatic ? "切换到手动模式，使用之前保留的参数" : "恢复自动适配，不覆盖手动参数";
                    if (reset != null) reset.Content = automatic ? "重新适配" : "恢复默认";
                    autoStatus.Text = panel == null ? (automatic ? "自动适配已开启" : "手动调整 · 原参数已保留")
                        : panel.AutomaticAppearanceDescription;
                    if (lastAutomatic != automatic)
                    {
                        lastAutomatic = automatic;
                        if (fitContent != null) fitContent();
                    }
                };
                autoMode.Click += delegate
                {
                    if (settings != null) settings.AutoAppearance = autoMode.IsChecked == true;
                    commit();
                    refreshAutomatic();
                };
                manualSettings.Click += delegate
                {
                    if (settings != null) settings.AutoAppearance = !settings.AutoAppearance;
                    commit();
                    refreshAutomatic();
                };

                // ===== 预设 =====
                manualBody.Children.Add(SectionLabel("预设"));

                WrapPanel presetRow = new WrapPanel();
                presetRow.Margin = new Thickness(0, Ui.S.Sm, 0, Ui.S.Lg);
                manualBody.Children.Add(presetRow);

                // 预设按钮的选中态要跟着 settings 变 → 存下来便于刷新
                Dictionary<string, Button> presetButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);

                // refreshAll 要在预设按钮之前声明（预设点击回调里会用到）
                Action refreshAll = null;

                Action refreshPresetButtons = null;
                refreshPresetButtons = delegate
                {
                    try
                    {
                        string cur = settings == null ? AppearancePresets.Custom : settings.GlassPreset;
                        foreach (KeyValuePair<string, Button> kv in presetButtons)
                        {
                            bool on = string.Equals(kv.Key, cur, StringComparison.OrdinalIgnoreCase);
                            SetChipActive(kv.Value, on);
                        }
                    }
                    catch { }
                };

                foreach (AppearancePresets.Preset p in AppearancePresets.AllPresets)
                {
                    AppearancePresets.Preset preset = p;
                    Button b = MakeChip(preset.DisplayName, null);
                    b.Width = PresetBtnW;
                    b.Height = PresetBtnH;
                    b.Margin = new Thickness(0, 0, Ui.S.Sm, Ui.S.Sm);
                    b.Cursor = Cursors.Hand;
                    b.Background = Ui.SearchBg;
                    b.BorderBrush = Ui.Line;
                    b.BorderThickness = new Thickness(1);
                    b.ToolTip = preset.Description;

                    b.Click += delegate
                    {
                        try
                        {
                            if (preset.IsCustom)
                            {
                                // 选「自定义」：只改名字，不动参数（用户明确要求）
                                if (settings != null) settings.GlassPreset = AppearancePresets.Custom;
                                refreshPresetButtons();
                                commit();
                                return;
                            }

                            AppearancePresets.Apply(preset, settings);

                            // 预设改了 theme 时要让窗口跟着变
                            if (settings != null && !string.IsNullOrEmpty(settings.Theme))
                                Ui.ApplyTheme(Ui.ParseTheme(settings.Theme));

                            refreshAll();
                            commit();
                        }
                        catch (Exception ex) { Ui.LogError(ex, "应用预设"); }
                    };

                    presetRow.Children.Add(b);
                    presetButtons[preset.Key] = b;
                }

                // ===== 滑块 =====
                manualBody.Children.Add(SectionLabel("参数"));

                // 用一个列表存「从 settings 刷新 UI」的动作
                List<Action> refreshers = new List<Action>();


                // ---- 浓度（glassOpacity 0-100）----
                manualBody.Children.Add(MakeSlider("壁纸透出", 0, 100, 1,
                    delegate { return settings == null ? 0 : settings.GlassOpacity; },
                    delegate(double v)
                    {
                        if (settings == null) return;
                        settings.GlassOpacity = (int)Math.Round(v);
                        AppearancePresets.MarkCustom(settings);
                    },
                    delegate(double v) { return ((int)Math.Round(v)).ToString() + "%"; },
                    "面板底色盖住壁纸的程度。0 = 关闭玻璃（纯色），越大越透出壁纸",
                    schedulePreview, commit, refreshPresetButtons, refreshers));

                // ---- 模糊（glassBlur 0-30）----
                manualBody.Children.Add(MakeSlider("模糊", 0, 30, 0.5,
                    delegate { return settings == null ? 0 : settings.GlassBlur; },
                    delegate(double v)
                    {
                        if (settings == null) return;
                        settings.GlassBlur = Math.Round(v, 1);
                        AppearancePresets.MarkCustom(settings);
                    },
                    delegate(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); },
                    "底图的模糊程度。固定图模式下差异较小，跟随桌面模式更明显",
                    schedulePreview, commit, refreshPresetButtons, refreshers));

                // ---- 染色强度（glassTintStrength 0-100）----
                manualBody.Children.Add(MakeSlider("染色强度", 0, 100, 1,
                    delegate { return settings == null ? 0 : settings.GlassTintStrength; },
                    delegate(double v)
                    {
                        if (settings == null) return;
                        settings.GlassTintStrength = (int)Math.Round(v);
                        AppearancePresets.MarkCustom(settings);
                    },
                    delegate(double v) { return ((int)Math.Round(v)).ToString() + "%"; },
                    "染色层的浓度。与「浓度」不同：那个管透出多少壁纸，这个管颜色多重",
                    schedulePreview, commit, refreshPresetButtons, refreshers));

                // ---- 亮度（glassBrightness -50~+50）----
                manualBody.Children.Add(MakeSlider("亮度", -50, 50, 1,
                    delegate { return settings == null ? 0 : settings.GlassBrightness; },
                    delegate(double v)
                    {
                        if (settings == null) return;
                        settings.GlassBrightness = (int)Math.Round(v);
                        AppearancePresets.MarkCustom(settings);
                    },
                    delegate(double v)
                    {
                        int i = (int)Math.Round(v);
                        return (i > 0 ? "+" : "") + i.ToString();
                    },
                    "正值提亮、负值压暗（作用在模糊后的底图上）",
                    schedulePreview, commit, refreshPresetButtons, refreshers));

                // ---- 背景暗度（glassDarkness 0~60）----
                //
                // 壁纸偏亮时文字看不清 —— 在背景之上叠一层半透明深色。
                // 只改一个 Opacity 值，不重算毛玻璃，拖滑块即时见效。
                manualBody.Children.Add(MakeSlider("背景暗度", 0, 60, 1,
                    delegate { return settings == null ? 0 : settings.GlassDarkness; },
                    delegate(double v)
                    {
                        if (settings == null) return;
                        settings.GlassDarkness = Math.Round(v);
                    },
                    delegate(double v)
                    {
                        int i = (int)Math.Round(v);
                        return i == 0 ? "关" : (i + "%");
                    },
                    "在背景上叠一层深色遮罩，壁纸偏亮时调大它让文字更清楚（所有背景模式通用）",
                    schedulePreview,
                    commit, refreshPresetButtons, refreshers));

                // ===== 颜色 =====
                manualBody.Children.Add(SectionLabel("颜色"));

                // 染色色板
                manualBody.Children.Add(MakeColorRow("染色", settings,
                    delegate { return settings == null ? "" : settings.GlassTint; },
                    delegate(string hex)
                    {
                        if (settings == null) return;
                        settings.GlassTint = hex ?? "";
                        AppearancePresets.MarkCustom(settings);
                    },
                    "空 = 用当前强调色",
                    null,   // 染色不需要额外说明
                    schedulePreview, commit, refreshPresetButtons, refreshers));

                // 界面强调色板
                //
                // 名字带「界面」前缀，因为它**不只影响面板背景**：
                // glassAccent → Ui.SetAccentOverride → 全局 Ui.Accent 刷子，
                // 按钮、选中态、胶囊、搜索框焦点都跟着变。
                // 用户在这里改，是整个界面变色，不是只改玻璃染色。
                manualBody.Children.Add(MakeColorRow("界面强调色", settings,
                    delegate { return settings == null ? "" : settings.GlassAccent; },
                    delegate(string hex)
                    {
                        if (settings == null) return;
                        settings.GlassAccent = hex ?? "";
                        AppearancePresets.MarkCustom(settings);

                        // 强调色立刻生效（Ui.Accent 是全局未冻结刷子）
                        byte r, g, b;
                        if (!string.IsNullOrEmpty(hex) && Settings.TryParseHexColor(hex, out r, out g, out b))
                            Ui.SetAccentOverride(Color.FromRgb(r, g, b));
                        else
                            Ui.SetAccentOverride(null);
                    },
                    "空 = 用主题默认强调色",
                    "影响整个界面的强调色，包括按钮、选中状态等；不只影响面板背景。",
                    schedulePreview, commit, refreshPresetButtons, refreshers));

                // ===== 背景模式 =====
                body.Children.Add(SectionLabel("背景"));

                WrapPanel modeRow = new WrapPanel();
                modeRow.Margin = new Thickness(0, Ui.S.Sm, 0, Ui.S.Sm);
                body.Children.Add(modeRow);

                Button bScreen = null, bCustom = null, bWe = null;
                TextBlock customPathText = null;
                Button btnChange = null;
                Grid customRow = null;

                // 拖放提示的当前文案（空 = 没有提示，refreshMode 按文件名显示）。
                // 用局部变量而不是字段：AppearanceDialog 是 static class，
                // 而提示的生命周期只在这一个窗口内。
                string customNoticeText = "";

                Action refreshMode = null;
                refreshMode = delegate
                {
                    try
                    {
                        string mode = settings == null ? "screen" : (settings.GlassMode ?? "screen");
                        bool screen = string.Equals(mode, "screen", StringComparison.OrdinalIgnoreCase);
                        bool custom = string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase);
                        bool we = string.Equals(mode, "we", StringComparison.OrdinalIgnoreCase);

                        SetChipActive(bScreen, screen);
                        SetChipActive(bCustom, custom);
                        SetChipActive(bWe, we);

                        if (customPathText != null)
                        {
                            // 有拖放提示正在显示时不要覆盖它 —— 提示刚说完
                            // 「已设为背景：x.png」，refreshMode 立刻改回文件名
                            // 会让用户看不到反馈。
                            if (!string.IsNullOrEmpty(customNoticeText))
                            {
                                customPathText.Text = customNoticeText;
                                customPathText.Foreground = Ui.AccentText;
                            }
                            else
                            {
                                string f = BackgroundStore.Find(baseDir);
                                customPathText.Text = custom ? "自定义图片 · " + (string.IsNullOrEmpty(f)
                                    ? "尚未选择图片"
                                    : System.IO.Path.GetFileName(f) + "  ·  " + FormatSize(f))
                                    : we ? "WE 壁纸 · " + (string.IsNullOrEmpty(settings.WeWallpaperTitle) ? "尚未选择壁纸" : settings.WeWallpaperTitle)
                                    : "使用当前桌面壁纸";
                                customPathText.Foreground = Ui.Sub;
                                customPathText.ToolTip = custom ? f : we ? settings.WeWallpaperTitle : "桌面壁纸更换后自动跟随";
                            }
                        }
                        if (btnChange != null)
                        {
                            btnChange.Visibility = custom || we ? Visibility.Visible : Visibility.Collapsed;
                            btnChange.Content = we ? "更换壁纸…" : "更换图片…";
                        }
                    }
                    catch { }
                };


                bScreen = MakeChip("跟随桌面", delegate
                {
                    customNoticeText = "";
                    if (settings != null) settings.GlassMode = "screen";
                    commit();
                    refreshMode();
                    refreshAutomatic();
                });
                bScreen.ToolTip = "自动模式直接读取桌面壁纸并保护文字；手动模式沿用桌面取景";
                modeRow.Children.Add(bScreen);

                bCustom = MakeChip("自定义图片", delegate
                {
                    try
                    {
                        // Selecting an existing source switches to it. Selecting the
                        // already active source is a shortcut to replacing its image.
                        if (!BackgroundStore.Exists(baseDir) || settings.GlassMode == "custom")
                        {
                            if (!DoImportCustom(dlg, baseDir, panel)) return;
                        }
                        customNoticeText = "";
                        if (settings != null) settings.GlassMode = "custom";
                        InvalidateCustomCache(panel);
                        commit();
                        refreshMode();
                        refreshAutomatic();
                    }
                    catch (Exception ex) { Ui.LogError(ex, "切换到自定义背景"); }
                });
                bCustom.ToolTip = "导入你自己的图片作为面板背景（jpg/png/bmp/gif/webp/tif）";
                modeRow.Children.Add(bCustom);

                // ---- WE 壁纸（引用模式，与前三项互斥）----
                Action chooseWe = delegate
                {
                    try
                    {
                        WallpaperPickerDialog picker = new WallpaperPickerDialog(
                            dlg, baseDir, settings, panel, delegate
                            {
                                // 选好后 picker 已写好 settings（含 GlassMode = "we"）。
                                // 不吞异常：让选择窗口显示“应用失败”，避免假成功。
                                if (panel != null) panel.ApplyGlassSetting();
                                customNoticeText = "";
                                commit();
                                refreshMode();
                                refreshAutomatic();
                            });
                        picker.Show();
                    }
                    catch (Exception ex) { Ui.LogError(ex, "打开壁纸选择窗口"); }
                };
                bWe = MakeChip("WE 壁纸", delegate
                {
                    if (settings.GlassMode == "we" || string.IsNullOrEmpty(settings.WeWallpaperId)) { chooseWe(); return; }
                    settings.GlassMode = "we";
                    customNoticeText = "";
                    commit(); refreshMode(); refreshAutomatic();
                });
                bWe.ToolTip = "选择独立的图片或视频，不影响桌面壁纸；可手动指定 WE 目录";
                modeRow.Children.Add(bWe);
                foreach (UIElement item in modeRow.Children)
                    ((FrameworkElement)item).Margin = new Thickness(0, 0, Ui.S.Sm, Ui.S.Sm);

                // ---- 当前来源与更换操作；切换来源不删除已导入图片 ----
                customRow = new Grid();
                customRow.ColumnDefinitions.Add(new ColumnDefinition());
                customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                customRow.Margin = new Thickness(0, 0, 0, Ui.S.Sm);

                customPathText = Ui.T("", 12, Ui.Sub);
                customPathText.VerticalAlignment = VerticalAlignment.Center;
                customPathText.TextWrapping = TextWrapping.Wrap;
                customRow.Children.Add(customPathText);

                btnChange = MakeChip("更换", delegate
                {
                    try
                    {
                        if (settings.GlassMode == "we") { chooseWe(); return; }
                        if (!DoImportCustom(dlg, baseDir, panel)) return;
                        customNoticeText = "";
                        if (settings != null) settings.GlassMode = "custom";
                        InvalidateCustomCache(panel);
                        commit();
                        refreshMode(); refreshAutomatic();
                    }
                    catch (Exception ex) { Ui.LogError(ex, "更换自定义背景"); }
                });
                btnChange.Margin = new Thickness(Ui.S.Sm, 0, 0, 0);
                Grid.SetColumn(btnChange, 1);
                customRow.Children.Add(btnChange);

                body.Children.Add(customRow);

                // ---- 自定义背景的放置提示层（常驻）----
                //
                // 复用 DropHint（不新建第二套提示控件），样式令牌一个不改。
                // 约束行由 DropSpec.BackgroundHintText() 从常量生成，
                // 格式列表和 3840 都取自 core，与真正导入时的判定同源。
                //
                // ★ 只挂在「自定义图片」这一块，不是整个窗口：
                //   窗口还有主题、毛玻璃参数、WE 壁纸等区域，全窗铺一个
                //   提示层会让用户以为"往哪拖都行"。
                DropHint customDropHint = new DropHint("把文件拖到这里",
                    DropSpec.BackgroundHintText());
                customDropHint.Root.Margin = new Thickness(0, 0, 0, Ui.S.Lg);
                body.Children.Add(customDropHint.Root);
                body.Children.Add(manualSettings);
                body.Children.Add(manualBody);

                // ---- 拖放接线（只做高亮 + Drop 后校验，不接管任何现有事件）----
                //
                // 窗口级而不是 chip 级：拖文件时鼠标可能落在提示层、chip、
                // 或空白处，挂窗口级才能稳定收到 Enter/Leave。
                // 高亮只驱动 customDropHint，不影响其它控件。
                dlg.AllowDrop = true;

                // 本窗口自己的提示位。
                //
                // ★ 为什么不用主面板的 toastHost：本窗口是模态（ShowDialog），
                //   toastHost 挂在 PanelWindow 上，被模态窗整个挡住 —— 用户看不到。
                //   所以在这里把提示写进本窗口的 customPathText（就在 chip 下方，
                //   正是用户刚操作过的位置）。文案仍由 DropSpec.BuildSummary 生成，
                //   不另写一套措辞。
                Action<string> showCustomNotice = delegate(string text)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(text)) return;
                        customNoticeText = text;

                        if (customPathText == null) return;
                        customPathText.Text = text;
                        customPathText.Foreground = Ui.AccentText;
                        if (fitContent != null) fitContent();
                    }
                    catch { }
                };

                dlg.DragEnter += delegate(object s, DragEventArgs e)
                {
                    try
                    {
                        if (!HasFileDrop(e)) return;
                        customDropHint.SetActive(true);
                        e.Effects = DragDropEffects.Copy;
                        e.Handled = true;
                    }
                    catch (Exception ex) { Ui.LogError(ex, "背景图 DragEnter"); }
                };

                dlg.DragOver += delegate(object s, DragEventArgs e)
                {
                    try
                    {
                        if (!HasFileDrop(e)) return;
                        // 不重复 SetActive：DragOver 高频触发，反复启停动画会闪
                        e.Effects = DragDropEffects.Copy;
                        e.Handled = true;
                    }
                    catch { }
                };

                dlg.DragLeave += delegate(object s, DragEventArgs e)
                {
                    try
                    {
                        // 不信任事件本身：拖过子元素边界时会派发假 Leave。
                        // 以光标是否真在窗口内为准。
                        if (IsCursorInsideWindow(dlg)) { e.Handled = true; return; }
                        customDropHint.SetActive(false);
                    }
                    catch (Exception ex) { Ui.LogError(ex, "背景图 DragLeave"); }
                };

                dlg.Drop += delegate(object s, DragEventArgs e)
                {
                    try
                    {
                        customDropHint.SetActive(false);

                        string[] paths = null;
                        try { paths = e.Data.GetData(DataFormats.FileDrop) as string[]; }
                        catch { }
                        e.Handled = true;
                        if (paths == null || paths.Length == 0) return;

                        // ---- 走 DropSpec 校验（与真正导入同一份规则）----
                        DropSpec spec = DropSpec.ForBackground();
                        DropSpec.Validation v = spec.Validate(paths);

                        if (!v.AnyAccepted)
                        {
                            // 全被拒：复用步骤 2 的 BuildSummary 生成文案
                            string msg = DropSpec.BuildSummary(0, v.RejectedItems);
                            if (!string.IsNullOrEmpty(msg))
                            {
                                // 模态窗里没有 toastHost（它在主面板上，被本窗挡住），
                                // 所以写到本窗口自己的提示位（customPathText 上方）。
                                showCustomNotice(msg);
                                Ui.Log("背景图拖入被拒: " + msg);
                            }
                            return;
                        }

                        // 只取第一个（背景图 MaxFiles = 1）
                        string picked = v.Accepted[0];
                        string err;
                        string saved = BackgroundStore.Import(baseDir, picked, out err);

                        if (string.IsNullOrEmpty(saved))
                        {
                            MessageBox.Show(dlg,
                                "无法读取该图片。\r\n\r\n" + (err ?? "未知原因"),
                                AppBrand.Text("四叶草 · 背景图片"),
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        if (settings != null) settings.GlassMode = "custom";
                        InvalidateCustomCache(panel);
                        refreshMode();
                        commit();
                        refreshAutomatic();
                        showCustomNotice("已设为背景：" + System.IO.Path.GetFileName(saved));
                        Ui.Log("拖入背景图: " + picked + " → " + saved);
                    }
                    catch (Exception ex) { Ui.LogError(ex, "背景图 Drop"); }
                };

                refreshMode();



                // ===== 文字清晰保底 =====
                manualBody.Children.Add(SectionLabel("文字清晰"));

                Button guardChip = null;
                TextBlock guardHint = null;
                Action refreshGuard = null;
                refreshGuard = delegate
                {
                    try
                    {
                        bool on = settings == null || settings.TextContrastGuard;
                        if (guardChip != null)
                        {
                            SetChipActive(guardChip, on);
                            guardChip.Content = on ? "文字保护已开启" : "文字保护已关闭";
                        }
                        // 触发时显示灰色小字（提示用户背景被自动补偿了）
                        if (guardHint != null)
                        {
                            double alpha = panel == null ? 0 : panel.CurrentGuardAlpha;
                            if (on && alpha > 0.001)
                            {
                                bool dark = Ui.AppliedTheme == Ui.ThemeMode.Dark;
                                guardHint.Text = "已自动" + (dark ? "加深" : "提亮")
                                    + "以保证文字清晰（补偿 " + (alpha * 100).ToString("F0") + "%）";
                                guardHint.Visibility = Visibility.Visible;
                            }
                            else
                            {
                                guardHint.Visibility = Visibility.Collapsed;
                            }
                        }
                    }
                    catch { }
                };

                guardChip = MakeChip("文字清晰保底", delegate
                {
                    try
                    {
                        if (settings != null) settings.TextContrastGuard = !settings.TextContrastGuard;
                        refreshGuard();
                        commit();
                    }
                    catch (Exception ex) { Ui.LogError(ex, "切换文字保底"); }
                });
                guardChip.ToolTip = "背景太亮/太暗时，自动在文字区域叠一层极淡的压暗或提亮，让文字达到 WCAG 对比度";
                guardChip.HorizontalAlignment = HorizontalAlignment.Left;
                guardChip.Margin = new Thickness(0, Ui.S.Sm, 0, 0);
                manualBody.Children.Add(guardChip);

                guardHint = Ui.T("", 10.5, Ui.Sub);
                guardHint.Margin = new Thickness(0, Ui.S.Xs, 0, Ui.S.Lg);
                guardHint.Visibility = Visibility.Collapsed;
                manualBody.Children.Add(guardHint);

                refreshers.Add(refreshGuard);
                refreshers.Add(delegate
                {
                    Ui.ThemeMode selected = Ui.ParseTheme(settings.Theme);
                    Ui.ThemeMode[] modes = { Ui.ThemeMode.System, Ui.ThemeMode.Light, Ui.ThemeMode.Dark };
                    for (int i = 0; i < modes.Length; i++) SetChipActive(themeChips[i], modes[i] == selected);
                });
                refreshers.Add(refreshMode);
                refreshers.Add(refreshAutomatic);
                refreshAutomatic();
                DispatcherTimer automaticStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                automaticStatusTimer.Tick += delegate { refreshAutomatic(); };
                automaticStatusTimer.Start();
                dlg.Closed += delegate { automaticStatusTimer.Stop(); };

                // ---- 统一的「从 settings 刷新全部 UI」----
                refreshAll = delegate
                {
                    try
                    {
                        foreach (Action a in refreshers) { try { a(); } catch { } }
                        refreshPresetButtons();
                    }
                    catch { }
                };

                // 首次刷新
                refreshAll();

                // ===== 底部按钮 =====
                Grid footer = new Grid();
                footer.ColumnDefinitions.Add(new ColumnDefinition());
                footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                footer.Margin = new Thickness(Ui.S.Xl, Ui.S.Sm, Ui.S.Xl, Ui.S.Lg);
                StackPanel footerStart = new StackPanel { Orientation = Orientation.Horizontal };
                TextBlock savedHint = Ui.T("修改即时生效", 11, Ui.Sub);
                savedHint.VerticalAlignment = VerticalAlignment.Center;
                savedHint.Margin = new Thickness(Ui.S.Md, 0, 0, 0);

                reset = MakeChip("恢复默认", delegate
                {
                    try
                    {
                        if (settings != null && settings.AutoAppearance)
                        {
                            if (panel != null) panel.RecalculateAutomaticAppearance();
                            refreshAutomatic();
                            return;
                        }
                        AppearancePresets.RestoreDefaults(settings);
                        refreshAll();
                        commit();
                    }
                    catch (Exception ex) { Ui.LogError(ex, "恢复默认"); }
                });
                reset.HorizontalAlignment = HorizontalAlignment.Left;
                reset.ToolTip = "自动模式重新适配当前背景；手动模式恢复当前预设的参数";
                footerStart.Children.Add(reset);
                footerStart.Children.Add(savedHint);
                footer.Children.Add(footerStart);

                Button done = MakeChip("完成", delegate { dlg.Close(); });
                done.Background = Ui.Accent;
                done.Foreground = Brushes.White;
                done.MinWidth = 76;
                Grid.SetColumn(done, 1);
                footer.Children.Add(done);

                Grid.SetRow(footer, 2);
                layout.Children.Add(footer);

                // Compact by default; custom import help only belongs to that source.
                customDropHint.Root.Visibility = settings != null && settings.GlassMode == "custom"
                    ? Visibility.Visible : Visibility.Collapsed;
                Action oldRefreshMode = refreshMode;
                refreshMode = delegate
                {
                    oldRefreshMode();
                    customDropHint.Root.Visibility = settings != null && settings.GlassMode == "custom"
                        ? Visibility.Visible : Visibility.Collapsed;
                    if (fitContent != null) fitContent();
                };
                // refreshers captured the old delegate earlier; add the final source
                // visibility refresh as well for reset/import callbacks.
                refreshers.Add(refreshMode);
                root.Child = layout;
                DialogChrome.UseCloseOnlyTitleBar(dlg, root);
                // Keep position/width memory, but derive height from current content.
                // This intentionally replaces obsolete tall automatic-mode records.
                DispatcherOperation fitOperation = null;
                fitContent = delegate
                {
                    if (fitOperation != null && fitOperation.Status == DispatcherOperationStatus.Pending) return;
                    fitOperation = dlg.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
                    {
                        if (!dlg.IsVisible) return;
                        double bodyWidth = Math.Max(1, dlg.ActualWidth - Ui.S.Xl * 2 - 20);
                        body.Measure(new Size(bodyWidth, double.PositiveInfinity));
                        double contentHeight = body.DesiredSize.Height + footer.ActualHeight
                            + 36 + scroll.Margin.Top + scroll.Margin.Bottom + footer.Margin.Top + footer.Margin.Bottom + 4;
                        // Long manual forms should scroll instead of filling a tall
                        // monitor; automatic mode only occupies its natural height.
                        double heightLimit = Math.Min(dlg.MaxHeight, 760);
                        dlg.Height = Math.Max(dlg.MinHeight, Math.Min(heightLimit, contentHeight));
                        if (settings != null && settings.AutoAppearance) scroll.ScrollToTop();
                    }));
                };
                dlg.Loaded += delegate { fitContent(); autoMode.Focus(); };
                dlg.SizeChanged += delegate(object sender, SizeChangedEventArgs e)
                {
                    savedHint.Visibility = dlg.ActualWidth < 440 ? Visibility.Collapsed : Visibility.Visible;
                    if (e.WidthChanged) fitContent();
                };
                refreshAutomatic();

                // ---- 边缘拖拽 ----
                //
                // 无边框窗口（WindowStyle.None）没有系统非客户区，
                // 而且 root 有 14px 外边距 —— 边缘那一圈是空白的透明区，
                // 里面没有任何 WPF 元素，WPF 热区铺不到那里。
                //
                // 所以在 WM_NCHITTEST 里统一判断：命中窗口最外圈 6px 就返回
                // HTLEFT/HTRIGHT/HTTOP/HTBOTTOM/四角，系统自动接管缩放与光标。
                AttachResizeHook(dlg);

                // ---- 跨屏后重算上限 ----
                //
                // 用户把窗口拖到另一块显示器时，MaxWidth/MaxHeight 要跟着变。
                // 用延迟合并，避免拖动过程中反复计算。
                DispatcherTimer limitTimer = new DispatcherTimer();
                limitTimer.Interval = TimeSpan.FromMilliseconds(120);
                limitTimer.Tick += delegate
                {
                    limitTimer.Stop();
                    double previousMaxHeight = dlg.MaxHeight;
                    ApplyWorkAreaLimits(dlg);
                    if (dlg.MaxHeight != previousMaxHeight) fitContent();
                };
                Action scheduleLimits = delegate
                {
                    limitTimer.Stop();
                    limitTimer.Start();
                };
                dlg.LocationChanged += delegate { scheduleLimits(); };
                dlg.SizeChanged += delegate { scheduleLimits(); };
                dlg.Closed += delegate
                {
                    limitTimer.Stop();
                    if (fitOperation != null && fitOperation.Status == DispatcherOperationStatus.Pending) fitOperation.Abort();
                };

                // ---- 关窗：写盘一次 ----
                dlg.Closing += delegate
                {
                    try
                    {
                        previewTimer.Stop();
                        if (settings != null) settings.Save();
                        if (panel != null) panel.ApplyGlassSetting();
                    }
                    catch (Exception ex) { Ui.LogError(ex, "外观关窗"); }
                };

                dlg.PreviewKeyDown += delegate(object s, KeyEventArgs e)
                {
                    if (e.Key == Key.Escape) { dlg.Close(); e.Handled = true; }
                };

                DialogChrome.Attach(dlg);
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "打开外观窗口");
                // 兜底：至少不要让面板打不开
                try
                {
                    if (panel != null) panel.ApplyGlassSetting();
                }
                catch { }
            }
        }

        // ========== 小控件 ==========

        static TextBlock SectionLabel(string text)
        {
            TextBlock t = Ui.T(text, 12, Ui.Sub, FontWeights.SemiBold);
            t.Margin = new Thickness(0, Ui.S.Md, 0, 0);
            return t;
        }

        /// <summary>
        /// 把窗口的最大尺寸限制到它**所在那块**显示器的工作区。
        ///
        /// ── 为什么按所在屏幕算 ──
        ///
        /// 主屏 2560 宽、副屏 1920 宽时，固定用主屏尺寸当上限，
        /// 窗口拖到副屏就能超出边界。这里用 WinForms 的 Screen，
        /// 从窗口中心点找所在屏幕；找不到就退回主屏。
        ///
        /// ── 为什么要减一点余量 ──
        ///
        /// 工作区已经排除了任务栏，但无边框窗口的阴影/圆角仍会贴边，
        /// 留 16px 让视觉上不"顶死"屏幕。
        /// </summary>
        /// <summary>默认打开尺寸（记录无效时用）。</summary>
        const double DefaultW = 520;
        const double DefaultH = 340;

        /// <summary>
        /// 把恢复出来的尺寸收回到合理范围。
        ///
        /// 记录里可能存着最大化时的尺寸（= 整个工作区）。那种尺寸一打开就满屏，
        /// 而且看起来"像最大化"，用户会以为窗口不能缩放。
        /// 超过工作区 80% 就回落到默认大小。
        /// </summary>
        static void ClampToReasonableSize(Window w)
        {
            try
            {
                if (w == null) return;

                double maxW = (w.MaxWidth > 0 && !double.IsInfinity(w.MaxWidth))
                    ? w.MaxWidth : SystemParameters.WorkArea.Width;
                double maxH = (w.MaxHeight > 0 && !double.IsInfinity(w.MaxHeight))
                    ? w.MaxHeight : SystemParameters.WorkArea.Height;

                double limitW = Math.Max(w.MinWidth, maxW * 0.8);
                double limitH = Math.Max(w.MinHeight, maxH * 0.8);

                if (w.Width <= limitW && w.Height <= limitH) return;

                w.Width = Math.Min(Math.Max(DefaultW, w.MinWidth), limitW);
                w.Height = Math.Min(Math.Max(DefaultH, w.MinHeight), limitH);

                Ui.Log("外观窗口记录尺寸过大，已回落到 " + w.Width + "x" + w.Height);
            }
            catch { }
        }

        static void ApplyWorkAreaLimits(Window w)
        {
            try
            {
                if (w == null) return;

                double left = w.Left, top = w.Top;
                double width = w.Width > 0 ? w.Width : w.MinWidth;
                double height = w.Height > 0 ? w.Height : w.MinHeight;

                double cx = left + width / 2.0;
                double cy = top + height / 2.0;

                System.Drawing.Rectangle wa = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                try
                {
                    foreach (System.Windows.Forms.Screen s in System.Windows.Forms.Screen.AllScreens)
                    {
                        System.Drawing.Rectangle r = s.WorkingArea;
                        if (cx >= r.Left && cx <= r.Right && cy >= r.Top && cy <= r.Bottom)
                        {
                            wa = r;
                            break;
                        }
                    }
                }
                catch { }

                // WPF 用设备无关单位，需要按主屏比例换算一次。
                double scaleX = 1.0, scaleY = 1.0;
                try
                {
                    double sw = SystemParameters.PrimaryScreenWidth;
                    double sh = SystemParameters.PrimaryScreenHeight;
                    System.Drawing.Rectangle pw = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                    if (pw.Width > 0 && sw > 0) scaleX = sw / pw.Width;
                    if (pw.Height > 0 && sh > 0) scaleY = sh / pw.Height;
                }
                catch { }

                const double Margin = 16;
                double maxW = wa.Width * scaleX - Margin;
                double maxH = wa.Height * scaleY - Margin;

                // 上限不能小于下限，否则 WPF 会抛异常
                if (maxW < w.MinWidth) maxW = w.MinWidth;
                if (maxH < w.MinHeight) maxH = w.MinHeight;

                w.MaxWidth = maxW;
                w.MaxHeight = maxH;
            }
            catch { }
        }

        /// <summary>
        /// 在 WM_NCHITTEST 里实现四边四角缩放。
        ///
        /// ── 为什么必须走钩子，不能用 WPF 热区 ──
        ///
        /// WindowStyle.None 的窗口没有系统非客户区；同时 root 有 14px 外边距，
        /// 窗口最外那一圈是纯透明空白，没有任何 WPF 元素。
        /// WPF 命中测试在那里什么都不返回，最终落到 HTCLIENT ——
        /// 所以既没有缩放光标，按下也不会开始缩放。
        ///
        /// 实测证据（改之前）：style 含 WS_THICKFRAME、ResizeMode=CanResizeWithGrip，
        /// 但四边四角的 WM_NCHITTEST 全部返回 1 (HTCLIENT)。
        ///
        /// ── 判定 ──
        ///
        /// 用屏幕坐标和窗口矩形比较，命中最外圈 6px 就返回对应 HT 码。
        /// 不依赖任何子元素布局，边距/阴影怎么改都仍然有效。
        /// </summary>
        static void AttachResizeHook(Window w)
        {
            try
            {
                if (w == null) return;

                w.SourceInitialized += delegate
                {
                    try
                    {
                        IntPtr h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                        if (h == IntPtr.Zero) return;
                        System.Windows.Interop.HwndSource src =
                            System.Windows.Interop.HwndSource.FromHwnd(h);
                        if (src != null) src.AddHook(ResizeHitTest);
                    }
                    catch { }
                };
            }
            catch { }
        }

        /// <summary>窗口最外圈多宽算缩放区（设备无关单位）。</summary>
        const double ResizeBand = 6;

        static IntPtr ResizeHitTest(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_NCHITTEST = 0x0084;
            if (msg != WM_NCHITTEST) return IntPtr.Zero;

            try
            {
                long lp = lParam.ToInt64();
                int sx = (short)(lp & 0xFFFF);
                int sy = (short)((lp >> 16) & 0xFFFF);

                Native.RECT r;
                if (!Native.GetWindowRectPhysical(hwnd, out r)) return IntPtr.Zero;

                System.Windows.Interop.HwndSource src =
                    System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                if (src == null || src.RootVisual == null) return IntPtr.Zero;

                System.Windows.Point p = src.RootVisual.PointFromScreen(
                    new System.Windows.Point(sx, sy));

                FrameworkElement fe = src.RootVisual as FrameworkElement;
                double w = fe != null ? fe.ActualWidth : (r.Right - r.Left);
                double h = fe != null ? fe.ActualHeight : (r.Bottom - r.Top);

                if (w <= 0 || h <= 0) return IntPtr.Zero;

                // ── 让路给项目标题栏 ──
                //
                // 项目标题栏顶部 36px 内含关闭按钮和拖动区域。若这里返回
                // HTTOP/HTRIGHT，会吞掉按钮点击。所以：
                //   1. 顶部（y < 标题栏高）不由这个兼容钩子接管；
                //   2. 其余三边只在窗口最外圈 6px 接管。
                double titleBarH = 36;
                double relY = p.Y;
                double relX = p.X;
                double band = ResizeBand;

                bool left   = relX <= band && relY > titleBarH;
                bool right  = relX >= w - band && relY > titleBarH;
                bool top    = false;   // 顶部由 WindowChrome 处理
                bool bottom = relY >= h - band;
                // 只有点落在「边缘带 ∩ 透明边距区」才接管缩放；
                // 否则会把贴边控件（如标题栏关闭按钮）的点击也吞掉。

                int ht = 0;
                if (left && top) ht = Native.HTTOPLEFT;
                else if (left && bottom) ht = Native.HTBOTTOMLEFT;
                else if (right && bottom) ht = Native.HTBOTTOMRIGHT;
                else if (left) ht = Native.HTLEFT;
                else if (right) ht = Native.HTRIGHT;
                else if (top) ht = Native.HTTOP;
                else if (bottom) ht = Native.HTBOTTOM;

                if (ht == 0) return IntPtr.Zero;

                handled = true;
                return new IntPtr(ht);
            }
            catch { }

            return IntPtr.Zero;
        }

        /// <summary>设置胶囊的选中态（选中 = 强调色底 + 白字）。</summary>
        static void SetChipActive(Button chip, bool on)
        {
            try
            {
                if (chip == null) return;
                chip.Background = on ? Ui.AccentSoft : Ui.SearchBg;
                chip.BorderBrush = on ? Ui.Accent : Ui.Line;
                chip.Foreground = on ? Ui.AccentText : Ui.Fg;
                System.Windows.Automation.AutomationProperties.SetItemStatus(chip, on ? "已选择" : "未选择");
            }
            catch { }
        }

        /// <summary>文件大小显示（KB / MB）。</summary>
        static string FormatSize(string path)
        {
            try
            {
                long len = new System.IO.FileInfo(path).Length;
                if (len >= 1024 * 1024) return (len / 1024.0 / 1024.0).ToString("0.0") + " MB";
                if (len >= 1024) return (len / 1024.0).ToString("0") + " KB";
                return len + " B";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 拉起文件选择 + 导入。
        ///
        /// 导入失败（格式不支持 / 解码失败 / 写盘失败）→ 弹提示，**不改动现有设置**。
        /// 成功 → 返回 true，调用方负责切到 custom 模式并刷新。
        /// </summary>
        sealed class FileDialogOwner : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }
            public FileDialogOwner(Window owner)
            { Handle = new System.Windows.Interop.WindowInteropHelper(owner).Handle; }
        }

        static bool DoImportCustom(Window owner, string baseDir, PanelWindow panel)
        {
            try
            {
                string picked = null;
                using (System.Windows.Forms.OpenFileDialog dlg = new System.Windows.Forms.OpenFileDialog())
                {
                    dlg.Title = "选择背景图片";
                    dlg.Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff"
                        + "|所有文件|*.*";
                    dlg.CheckFileExists = true;
                    dlg.Multiselect = false;

                    FileDialogOwner fileOwner = new FileDialogOwner(owner);
                    if (dlg.ShowDialog(fileOwner) != System.Windows.Forms.DialogResult.OK) return false;
                    picked = dlg.FileName;
                }

                if (string.IsNullOrWhiteSpace(picked)) return false;

                string err;
                string saved = BackgroundStore.Import(baseDir, picked, out err);

                if (string.IsNullOrEmpty(saved))
                {
                    MessageBox.Show("无法读取该图片。\r\n\r\n" + (err ?? "未知原因")
                        + "\r\n\r\n支持的格式：jpg / png / bmp / gif / webp / tif",
                        AppBrand.Text("四叶草 · 背景图片"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "导入背景图片");
                try
                {
                    MessageBox.Show("导入失败：" + ex.Message,
                        AppBrand.Text("四叶草 · 背景图片"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
                return false;
            }
        }

        /// <summary>让 GlassBackdrop 丢弃已缓存的自定义图（导入/清除后必须调）。</summary>
        static void InvalidateCustomCache(PanelWindow panel)
        {
            try
            {
                if (panel != null) panel.InvalidateCustomBackground();
            }
            catch { }
        }

        /// <summary>这次拖拽里有没有文件（只看存在性，不读内容 —— DragOver 高频）。</summary>
        static bool HasFileDrop(DragEventArgs e)
        {
            try
            {
                return e != null && e.Data != null
                    && e.Data.GetDataPresent(DataFormats.FileDrop);
            }
            catch { return false; }
        }

        /// <summary>
        /// 光标是否真的在窗口矩形内（屏幕坐标）。
        ///
        /// 为什么需要：拖过子元素边界时系统会派发假的 DragLeave。
        /// 只信事件会让高亮在拖过 chip 时闪一下。
        /// </summary>
        static bool IsCursorInsideWindow(Window w)
        {
            try
            {
                if (w == null) return false;

                Native.POINT p;
                if (!Native.GetCursorPos(out p)) return false;

                Native.RECT r;
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                if (!Native.GetWindowRectPhysical(hwnd, out r)) return false;

                return p.X >= r.Left && p.X <= r.Right
                    && p.Y >= r.Top && p.Y <= r.Bottom;
            }
            catch { return false; }
        }

        static Button MakeChip(string label, Action onClick)
        {
            Button b = new Button();
            b.Height = 36;
            b.Template = MakeAppearanceButtonTemplate();
            b.HorizontalContentAlignment = HorizontalAlignment.Center;
            b.VerticalContentAlignment = VerticalAlignment.Center;
            b.Padding = new Thickness(14, 0, 14, 0);
            b.Cursor = Cursors.Hand;
            b.Background = Ui.SearchBg;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(1);

            b.Content = label;
            b.FontFamily = Ui.YaHei;
            b.FontSize = 12;
            b.Foreground = Ui.Fg;
            b.Click += delegate { if (onClick != null) onClick(); };
            return b;
        }

        static ControlTemplate MakeAppearanceButtonTemplate()
        {
            return UiButtons.Template(Ui.R.Sm);
        }

        /// <summary>
        /// 一行「标签 + 滑块 + 数值」。
        /// </summary>
        static FrameworkElement MakeSlider(
            string label, double min, double max, double step,
            Func<double> getter, Action<double> setter,
            Func<double, string> formatter, string tooltip,
            Action schedulePreview, Action commit,
            Action refreshPresetButtons, List<Action> refreshers)
        {
            Grid row = new Grid();
            row.Margin = new Thickness(0, Ui.S.Sm, 0, 0);
            row.ColumnDefinitions.Add(new ColumnDefinition());                          // 标签
            row.ColumnDefinitions.Add(new ColumnDefinition());                          // 滑块
            row.ColumnDefinitions.Add(new ColumnDefinition());                          // 数值
            row.ColumnDefinitions[0].Width = new GridLength(64);
            row.ColumnDefinitions[2].Width = new GridLength(52);

            TextBlock lab = Ui.T(label, 12, Ui.Fg);
            lab.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(lab, 0);
            row.Children.Add(lab);

            Slider sl = new Slider();
            sl.Minimum = min;
            sl.Maximum = max;
            sl.SmallChange = step;
            sl.LargeChange = step * 4;
            sl.TickFrequency = step;
            sl.IsSnapToTickEnabled = step >= 1;
            sl.VerticalAlignment = VerticalAlignment.Center;
            sl.ToolTip = tooltip;
            System.Windows.Automation.AutomationProperties.SetName(sl, label);
            Grid.SetColumn(sl, 1);
            row.Children.Add(sl);

            TextBlock val = Ui.T("", 12, Ui.Sub);
            val.VerticalAlignment = VerticalAlignment.Center;
            val.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(val, 2);
            row.Children.Add(val);

            bool updating = false;

            sl.ValueChanged += delegate
            {
                if (updating) return;
                try
                {
                    val.Text = formatter(sl.Value);
                    setter(sl.Value);
                    if (refreshPresetButtons != null) refreshPresetButtons();
                    if (schedulePreview != null) schedulePreview();
                }
                catch (Exception ex) { Ui.LogError(ex, "外观滑块"); }
            };

            // 松手：立刻全尺寸重算 + 写盘
            sl.AddHandler(Thumb.DragCompletedEvent,
                new DragCompletedEventHandler(delegate { if (commit != null) commit(); }));
            // 键盘/点击轨道也要提交（DragCompleted 不触发）
            sl.MouseLeftButtonUp += delegate { if (commit != null) commit(); };
            sl.KeyUp += delegate { if (commit != null) commit(); };

            // 刷新器：从 settings 读回值（不触发 ValueChanged 的副作用）
            refreshers.Add(delegate
            {
                try
                {
                    updating = true;
                    sl.Value = getter();
                    val.Text = formatter(sl.Value);
                }
                finally { updating = false; }
            });

            return row;
        }

        /// <summary>
        /// 一行「标签 + 色板 + #RRGGBB 输入框 + 清除」。
        ///
        /// 色板是原生 Button（不引入取色控件）。
        /// </summary>
        static FrameworkElement MakeColorRow(
            string label, Settings settings,
            Func<string> getter, Action<string> setter,
            string tooltip,
            string hint,          // 可选：标签下方的灰色说明（null = 不显示）
            Action schedulePreview, Action commit,
            Action refreshPresetButtons, List<Action> refreshers)
        {
            StackPanel wrap = new StackPanel();
            wrap.Margin = new Thickness(0, Ui.S.Sm, 0, 0);

            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions[0].Width = new GridLength(64);
            row.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);

            TextBlock lab = Ui.T(label, 12, Ui.Fg);
            lab.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(lab, 0);
            row.Children.Add(lab);

            WrapPanel swatches = new WrapPanel();
            Grid.SetColumn(swatches, 1);
            row.Children.Add(swatches);

            // 一排预设色 + 「清除」
            string[] palette = new string[]
            {
                "",          // 空 = 用默认
                "#172A30", "#4FC3D9", "#388C48", "#7FA8B8",
                "#DCE4E8", "#FFFFFF", "#FF8800", "#C0392B",
                "#8E44AD", "#2C3E50", "#000000"
            };

            List<Button> chips = new List<Button>();
            TextBox input = null;
            Action refreshChips = null;
            refreshChips = delegate
            {
                try
                {
                    string cur = (getter() ?? "").ToUpperInvariant();
                    foreach (Button c in chips)
                    {
                        string tag = c.Tag as string;
                        string tagU = (tag ?? "").ToUpperInvariant();
                        bool on = tagU == cur;
                        c.BorderBrush = on ? Ui.Accent : Ui.Line;
                        c.BorderThickness = new Thickness(on ? 2 : 1);
                        System.Windows.Automation.AutomationProperties.SetItemStatus(c, on ? "已选择" : "未选择");
                    }
                }
                catch { }
            };

            foreach (string hex in palette)
            {
                string value = hex;
                Button chip = MakeChip("", null);
                chip.Width = 26;
                chip.Height = 26;
                chip.Padding = new Thickness(0);
                chip.Margin = new Thickness(0, 0, Ui.S.Sm, Ui.S.Sm);
                chip.Cursor = Cursors.Hand;
                chip.Tag = value;
                chip.BorderBrush = Ui.Line;
                chip.BorderThickness = new Thickness(1);
                chip.ToolTip = string.IsNullOrEmpty(value) ? "清除（用默认）" : value;
                System.Windows.Automation.AutomationProperties.SetName(chip, label + " " + chip.ToolTip);

                if (string.IsNullOrEmpty(value))
                {
                    // 「清除」用斜线图标表示
                    chip.Background = Ui.SearchBg;
                    TextBlock x = Ui.T("\uE711", 9, Ui.Sub);
                    x.FontFamily = Ui.Mdl;
                    x.HorizontalAlignment = HorizontalAlignment.Center;
                    x.VerticalAlignment = VerticalAlignment.Center;
                    chip.Content = x;
                }
                else
                {
                    byte r, g, b;
                    if (Settings.TryParseHexColor(value, out r, out g, out b))
                        chip.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
                    else
                        chip.Background = Ui.SearchBg;
                }

                chip.Click += delegate
                {
                    try
                    {
                        setter(value);
                        if (input != null) input.Text = value;
                        refreshChips();
                        if (refreshPresetButtons != null) refreshPresetButtons();
                        if (schedulePreview != null) schedulePreview();
                        if (commit != null) commit();
                    }
                    catch (Exception ex) { Ui.LogError(ex, "选颜色"); }
                };

                swatches.Children.Add(chip);
                chips.Add(chip);
            }

            // 手动输入 #RRGGBB
            input = new TextBox();
            input.Width = 96;
            input.Height = 26;
            input.FontFamily = Ui.YaHei;
            input.FontSize = 11.5;
            input.Foreground = Ui.Fg;
            input.Background = Ui.SearchBg;
            input.BorderBrush = Ui.Line;
            input.BorderThickness = new Thickness(1);
            input.Padding = new Thickness(6, 0, 6, 0);
            input.VerticalContentAlignment = VerticalAlignment.Center;
            input.ToolTip = tooltip;
            System.Windows.Automation.AutomationProperties.SetName(input, label + "十六进制颜色");
            input.Margin = new Thickness(0, 0, Ui.S.Sm, Ui.S.Sm);

            TextBlock colorError = Ui.T("请输入 #RRGGBB 格式的颜色，或留空使用默认值。", 11, Ui.Sub);
            colorError.TextWrapping = TextWrapping.Wrap;
            colorError.Margin = new Thickness(64, 0, 0, Ui.S.Sm);
            colorError.Visibility = Visibility.Collapsed;
            input.TextChanged += delegate { colorError.Visibility = Visibility.Collapsed; };
            // 输入框回车/失焦时应用；错误保留输入并就地解释，不静默丢弃。
            Action applyInput = delegate
            {
                try
                {
                    string text = (input.Text ?? "").Trim();
                    if (text.Length > 0 && !text.StartsWith("#")) text = "#" + text;
                    if (text.Length > 0 && !Settings.IsHexColor(text))
                    {
                        colorError.Visibility = Visibility.Visible;
                        return;
                    }
                    colorError.Visibility = Visibility.Collapsed;
                    if (string.Equals(text, getter() ?? "", StringComparison.OrdinalIgnoreCase)) return;
                    setter(text);
                    refreshChips();
                    if (refreshPresetButtons != null) refreshPresetButtons();
                    if (schedulePreview != null) schedulePreview();
                    if (commit != null) commit();
                }
                catch (Exception ex) { Ui.LogError(ex, "输入颜色"); }
            };
            input.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { applyInput(); e.Handled = true; }
            };
            input.LostFocus += delegate { applyInput(); };
            swatches.Children.Add(input);

            wrap.Children.Add(row);
            wrap.Children.Add(colorError);

            // 可选说明（灰色小字，复用 Ui.Sub 样式）
            if (!string.IsNullOrEmpty(hint))
            {
                TextBlock hintText = Ui.T(hint, 10.5, Ui.Sub);
                hintText.Margin = new Thickness(64, 2, 0, 0);   // 与标签列对齐
                hintText.TextWrapping = TextWrapping.Wrap;
                wrap.Children.Add(hintText);
            }

            // 刷新器
            refreshers.Add(delegate
            {
                try
                {
                    string cur = getter() ?? "";
                    input.Text = cur;
                    refreshChips();
                }
                catch { }
            });

            return wrap;
        }
    }
}
