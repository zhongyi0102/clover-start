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
    /// 1. **不引入第三方控件**：色板是手绘的一排 Border，滑块用原生 Slider。
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
        public static void Show(Window owner, Settings settings, PanelWindow panel, Action onCommit)
        {
            try
            {
                Window dlg = new Window();
                dlg.Title = "外观";
                dlg.Width = 420;
                dlg.Height = 620;
                dlg.WindowStyle = WindowStyle.None;
                dlg.AllowsTransparency = true;
                dlg.Background = Brushes.Transparent;
                dlg.ResizeMode = ResizeMode.NoResize;
                dlg.ShowInTaskbar = false;
                dlg.WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen;
                if (owner != null) dlg.Owner = owner;

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
                root.CornerRadius = new CornerRadius(Ui.R.Lg);
                root.Background = Ui.Bg;
                root.BorderBrush = Ui.Line;
                root.BorderThickness = new Thickness(1);
                root.Margin = new Thickness(14);
                try { root.Effect = Ui.PanelShadow(); } catch { }

                Grid layout = new Grid();
                layout.RowDefinitions.Add(new RowDefinition());   // 标题
                layout.RowDefinitions.Add(new RowDefinition());   // 内容
                layout.RowDefinitions.Add(new RowDefinition());   // 底部
                layout.RowDefinitions[0].Height = GridLength.Auto;
                layout.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
                layout.RowDefinitions[2].Height = GridLength.Auto;

                // ---- 标题（可拖动窗口）----
                Grid title = new Grid();
                title.Margin = new Thickness(Ui.S.Xl, Ui.S.Lg, Ui.S.Md, Ui.S.Sm);
                title.ColumnDefinitions.Add(new ColumnDefinition());
                title.ColumnDefinitions.Add(new ColumnDefinition());
                title.ColumnDefinitions[1].Width = GridLength.Auto;

                TextBlock titleText = Ui.T("外观", 15, Ui.Fg, FontWeights.SemiBold);
                titleText.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(titleText, 0);
                title.Children.Add(titleText);

                Border closeX = MakeIconButton("\uE711", delegate { dlg.Close(); });
                Grid.SetColumn(closeX, 1);
                title.Children.Add(closeX);

                title.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
                {
                    try { dlg.DragMove(); } catch { }
                };
                Grid.SetRow(title, 0);
                layout.Children.Add(title);

                // ---- 内容（可滚动）----
                ScrollViewer scroll = new ScrollViewer();
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                scroll.Margin = new Thickness(Ui.S.Xl, 0, Ui.S.Xl, 0);

                StackPanel body = new StackPanel();
                scroll.Content = body;
                Grid.SetRow(scroll, 1);
                layout.Children.Add(scroll);

                // ===== 预设 =====
                body.Children.Add(SectionLabel("预设"));

                WrapPanel presetRow = new WrapPanel();
                presetRow.Margin = new Thickness(0, Ui.S.Sm, 0, Ui.S.Lg);
                body.Children.Add(presetRow);

                // 预设按钮的选中态要跟着 settings 变 → 存下来便于刷新
                Dictionary<string, Border> presetButtons = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);

                // refreshAll 要在预设按钮之前声明（预设点击回调里会用到）
                Action refreshAll = null;

                Action refreshPresetButtons = null;
                refreshPresetButtons = delegate
                {
                    try
                    {
                        string cur = settings == null ? AppearancePresets.Custom : settings.GlassPreset;
                        foreach (KeyValuePair<string, Border> kv in presetButtons)
                        {
                            bool on = string.Equals(kv.Key, cur, StringComparison.OrdinalIgnoreCase);
                            kv.Value.Background = on ? Ui.Accent : Ui.SearchBg;
                            kv.Value.BorderBrush = on ? Ui.Accent : Ui.Line;
                            TextBlock tb = kv.Value.Child as TextBlock;
                            if (tb != null) tb.Foreground = on ? Brushes.White : Ui.Fg;
                        }
                    }
                    catch { }
                };

                foreach (AppearancePresets.Preset p in AppearancePresets.AllPresets)
                {
                    AppearancePresets.Preset preset = p;
                    Border b = new Border();
                    b.Width = PresetBtnW;
                    b.Height = PresetBtnH;
                    b.CornerRadius = new CornerRadius(b.Height / 2);
                    b.Margin = new Thickness(0, 0, Ui.S.Sm, Ui.S.Sm);
                    b.Cursor = Cursors.Hand;
                    b.Background = Ui.SearchBg;
                    b.BorderBrush = Ui.Line;
                    b.BorderThickness = new Thickness(1);
                    b.ToolTip = preset.Description;

                    TextBlock t = Ui.T(preset.DisplayName, 11.5, Ui.Fg);
                    t.HorizontalAlignment = HorizontalAlignment.Center;
                    t.VerticalAlignment = VerticalAlignment.Center;
                    b.Child = t;

                    b.MouseLeftButtonUp += delegate
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
                body.Children.Add(SectionLabel("参数"));

                // 用一个列表存「从 settings 刷新 UI」的动作
                List<Action> refreshers = new List<Action>();


                // ---- 浓度（glassOpacity 0-100）----
                body.Children.Add(MakeSlider("浓度", 0, 100, 1,
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
                body.Children.Add(MakeSlider("模糊", 0, 30, 0.5,
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
                body.Children.Add(MakeSlider("染色强度", 0, 100, 1,
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
                body.Children.Add(MakeSlider("亮度", -50, 50, 1,
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

                // ===== 颜色 =====
                body.Children.Add(SectionLabel("颜色"));

                // 染色色板
                body.Children.Add(MakeColorRow("染色", settings,
                    delegate { return settings == null ? "" : settings.GlassTint; },
                    delegate(string hex)
                    {
                        if (settings == null) return;
                        settings.GlassTint = hex ?? "";
                        AppearancePresets.MarkCustom(settings);
                    },
                    "空 = 用当前强调色",
                    schedulePreview, commit, refreshPresetButtons, refreshers));

                // 强调色板
                body.Children.Add(MakeColorRow("强调色", settings,
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
                    schedulePreview, commit, refreshPresetButtons, refreshers));

                // ===== 背景模式 =====
                body.Children.Add(SectionLabel("背景"));

                StackPanel modeRow = new StackPanel();
                modeRow.Orientation = Orientation.Horizontal;
                modeRow.Margin = new Thickness(0, Ui.S.Sm, 0, Ui.S.Lg);
                body.Children.Add(modeRow);

                Border bFixed = null, bScreen = null;
                Action refreshMode = null;
                refreshMode = delegate
                {
                    try
                    {
                        bool screen = settings != null
                            && string.Equals(settings.GlassMode, "screen", StringComparison.OrdinalIgnoreCase);
                        if (bFixed != null)
                        {
                            bFixed.Background = screen ? Ui.SearchBg : Ui.Accent;
                            ((TextBlock)bFixed.Child).Foreground = screen ? Ui.Fg : Brushes.White;
                        }
                        if (bScreen != null)
                        {
                            bScreen.Background = screen ? Ui.Accent : Ui.SearchBg;
                            ((TextBlock)bScreen.Child).Foreground = screen ? Brushes.White : Ui.Fg;
                        }
                    }
                    catch { }
                };

                bFixed = MakeChip("固定图", delegate
                {
                    if (settings != null) settings.GlassMode = "fixed";
                    refreshMode();
                    commit();
                });
                bFixed.ToolTip = "用 res\\background-cyangray.png 这张青灰玻璃图（观感稳定）";
                modeRow.Children.Add(bFixed);

                bScreen = MakeChip("跟随桌面", delegate
                {
                    if (settings != null) settings.GlassMode = "screen";
                    refreshMode();
                    commit();
                });
                bScreen.ToolTip = "截取桌面壁纸并模糊（透出真实桌面，但亮壁纸会干扰阅读）";
                modeRow.Children.Add(bScreen);

                refreshers.Add(refreshMode);

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
                StackPanel footer = new StackPanel();
                footer.Orientation = Orientation.Horizontal;
                footer.HorizontalAlignment = HorizontalAlignment.Right;
                footer.Margin = new Thickness(Ui.S.Xl, Ui.S.Sm, Ui.S.Xl, Ui.S.Lg);

                Border reset = MakeChip("恢复默认", delegate
                {
                    try
                    {
                        AppearancePresets.RestoreDefaults(settings);
                        refreshAll();
                        commit();
                    }
                    catch (Exception ex) { Ui.LogError(ex, "恢复默认"); }
                });
                reset.Margin = new Thickness(0, 0, Ui.S.Sm, 0);
                reset.ToolTip = "把参数恢复到当前预设的初始值；当前是自定义时回退到「经典毛玻璃」";
                footer.Children.Add(reset);

                Border done = MakeChip("完成", delegate { dlg.Close(); });
                done.Background = Ui.Accent;
                ((TextBlock)done.Child).Foreground = Brushes.White;
                footer.Children.Add(done);

                Grid.SetRow(footer, 2);
                layout.Children.Add(footer);

                root.Child = layout;
                dlg.Content = root;

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

        static Border MakeChip(string label, Action onClick)
        {
            Border b = new Border();
            b.Height = 28;
            b.CornerRadius = new CornerRadius(b.Height / 2);
            b.Padding = new Thickness(14, 0, 14, 0);
            b.Cursor = Cursors.Hand;
            b.Background = Ui.SearchBg;
            b.BorderBrush = Ui.Line;
            b.BorderThickness = new Thickness(1);

            TextBlock t = Ui.T(label, 11.5, Ui.Fg);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            b.MouseEnter += delegate { b.Opacity = 0.85; };
            b.MouseLeave += delegate { b.Opacity = 1.0; };
            b.MouseLeftButtonUp += delegate { if (onClick != null) onClick(); };
            return b;
        }

        static Border MakeIconButton(string glyph, Action onClick)
        {
            Border b = new Border();
            b.Width = 28;
            b.Height = 28;
            b.CornerRadius = new CornerRadius(Ui.R.Sm);
            b.Cursor = Cursors.Hand;
            b.Background = Brushes.Transparent;

            TextBlock t = new TextBlock();
            t.Text = glyph;
            t.FontFamily = Ui.Mdl;
            t.FontSize = 12;
            t.Foreground = Ui.Sub;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            b.MouseEnter += delegate { b.Background = Ui.Hover; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += delegate { if (onClick != null) onClick(); };
            return b;
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
        /// 色板是手绘的一排 Border（不引入取色控件）。
        /// </summary>
        static FrameworkElement MakeColorRow(
            string label, Settings settings,
            Func<string> getter, Action<string> setter,
            string tooltip,
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

            List<Border> chips = new List<Border>();
            Action refreshChips = null;
            refreshChips = delegate
            {
                try
                {
                    string cur = (getter() ?? "").ToUpperInvariant();
                    foreach (Border c in chips)
                    {
                        string tag = c.Tag as string;
                        string tagU = (tag ?? "").ToUpperInvariant();
                        bool on = tagU == cur;
                        c.BorderBrush = on ? Ui.Accent : Ui.Line;
                        c.BorderThickness = new Thickness(on ? 2 : 1);
                    }
                }
                catch { }
            };

            foreach (string hex in palette)
            {
                string value = hex;
                Border chip = new Border();
                chip.Width = 22;
                chip.Height = 22;
                chip.CornerRadius = new CornerRadius(Ui.R.Sm);
                chip.Margin = new Thickness(0, 0, Ui.S.Sm, Ui.S.Sm);
                chip.Cursor = Cursors.Hand;
                chip.Tag = value;
                chip.BorderBrush = Ui.Line;
                chip.BorderThickness = new Thickness(1);
                chip.ToolTip = string.IsNullOrEmpty(value) ? "清除（用默认）" : value;

                if (string.IsNullOrEmpty(value))
                {
                    // 「清除」用斜线图标表示
                    chip.Background = Ui.SearchBg;
                    TextBlock x = Ui.T("\uE711", 9, Ui.Sub);
                    x.FontFamily = Ui.Mdl;
                    x.HorizontalAlignment = HorizontalAlignment.Center;
                    x.VerticalAlignment = VerticalAlignment.Center;
                    chip.Child = x;
                }
                else
                {
                    byte r, g, b;
                    if (Settings.TryParseHexColor(value, out r, out g, out b))
                        chip.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
                    else
                        chip.Background = Ui.SearchBg;
                }

                chip.MouseLeftButtonUp += delegate
                {
                    try
                    {
                        setter(value);
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
            TextBox input = new TextBox();
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
            input.Margin = new Thickness(0, 0, Ui.S.Sm, Ui.S.Sm);

            // 输入框回车/失焦时应用
            Action applyInput = delegate
            {
                try
                {
                    string text = (input.Text ?? "").Trim();
                    if (text.Length > 0 && !text.StartsWith("#")) text = "#" + text;
                    if (text.Length > 0 && !Settings.IsHexColor(text))
                    {
                        // 格式错：恢复成当前值，不报错
                        input.Text = getter() ?? "";
                        return;
                    }
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
