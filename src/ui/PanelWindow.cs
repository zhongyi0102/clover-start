using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>
    /// 主面板。为便于排查，按职责拆成 partial 文件：
    ///   PanelWindow.cs         窗口生命周期、显示/隐藏、布局存取、启动应用
    ///   PanelWindow.Ui.cs      控件搭建（BuildUi 及通用小控件）
    ///   PanelWindow.Nav.cs     左侧分类、搜索、应用磁贴渲染与拖拽排序
    ///   PanelWindow.Tasks.cs   左侧“任务栏”列表（当前打开的窗口）
    ///   PanelWindow.Data.cs    目录/配置加载、刷新、对外接口
    /// </summary>
    partial class PanelWindow : Window
    {
        string baseDir;
        TriggerWindow trigger;
        AppWatcher watcher;
        Settings settings;
        Catalog catalog;
        string current = "";
        DateTime suppressDeactivate = DateTime.MinValue;
        /// <summary>程序内部清空搜索框时置 true，避免触发一次多余的全量重渲染。</summary>
        bool searchSuppress;
        /// <summary>面板是否至少被真正激活过一次；从未激活时不要自动收起。</summary>
        bool everActivated;
        string layoutPath;
        /// <summary>上次浏览的分类（重启后恢复到它，见 config\last-category.json）。</summary>
        string lastCatPath;
        Border searchBorder;
        Border closeBtn;
        UsageStore usage;
        List<string> hiddenCats;
        bool taskMenuOpen;
        MiniTaskbarWindow miniBar;
        bool showingCommon;
        Dictionary<string, List<string>> customOrders = new Dictionary<string, List<string>>();
        List<AppItem> renderedApps = new List<AppItem>();
        string orderPath;
        Border rootBorder;
        Border minBtn;
        Border maxBtn;
        bool expanded;
        double edgeMargin = 16;
        StackPanel taskStack;
        DispatcherTimer taskTimer;

        StackPanel navStack;
        WrapPanel tilePanel;
        TextBox searchBox;
        TextBlock searchHint;
        TextBlock footerCount;

        /// <summary>
        /// 侧栏所在列（折叠时改它的宽度）。由 BuildUi 赋值。
        /// 折叠用 ColumnDefinition 的宽度动画，不碰 navGrid 的 Visibility——
        /// 后者会把内容整棵丢掉，展开时还得重建。
        /// </summary>
        ColumnDefinition navColumn;
        /// <summary>侧栏容器（折叠时整体隐藏内部内容）。</summary>
        Grid navGrid;
        /// <summary>分类列表的滚动容器（折叠时隐藏）。</summary>
        ScrollViewer navScroll;
        /// <summary>侧栏下半部分（任务栏列表，折叠时隐藏）。</summary>
        Grid taskGrid;
        /// <summary>侧栏收起/展开的切换按钮。</summary>
        Border navToggle;
        /// <summary>侧栏是否已收起。</summary>
        bool navCollapsed;

        /// <summary>磁贴区的滚动容器（记住滚动位置要用）。</summary>
        ScrollViewer contentScroll;

        /// <summary>
        /// 各分类的滚动偏移（分类名 -> 垂直偏移）。
        /// 按分类分别记：切走再回来还在原处，而不是全局共用一个值。
        /// </summary>
        Dictionary<string, double> scrollOffsets = new Dictionary<string, double>();

        /// <summary>
        /// 程序自己要恢复滚动位置时置 true，避免那次位移被当成"用户滚动"记下来。
        /// 不设它的话：恢复动作本身会覆盖掉刚读到的值，多切几次就漂了。
        /// </summary>
        bool suppressScrollRecord;

        List<string> navCats = new List<string>();
        List<Border> navItems = new List<Border>();
        List<Border> navBars = new List<Border>();
        List<TextBlock> navNames = new List<TextBlock>();
        List<TextBlock> navCounts = new List<TextBlock>();

        Rect normalRect = Rect.Empty;

        /// <summary>用户手工指定的分类覆盖表（右键「移到分类」写入）。</summary>
        CategoryOverrides overrides;

        /// <summary>被隐藏的应用清单（右键「从面板隐藏」写入）。</summary>
        HiddenApps hiddenApps;

        /// <summary>分类的顺序与显示名（右键分类「重命名 / 上移下移」写入）。</summary>
        Categories categories;

        /// <summary>
        /// 「隐藏的项目」虚拟分类的标识。
        /// 用不可见字符开头，确保和任何真实分类名都不会撞。
        /// </summary>
        public const string HiddenCategory = "\u0001hidden";

        public PanelWindow(string baseDir, TriggerWindow trigger, Settings settings)
        {
            this.baseDir = baseDir;
            this.trigger = trigger;
            this.settings = settings ?? Settings.Load(baseDir);
            this.overrides = CategoryOverrides.Load(baseDir);
            this.hiddenApps = HiddenApps.Load(baseDir);
            this.categories = Categories.Load(baseDir);
            catalog = Catalog.Load(baseDir);
            usage = UsageStore.Load(baseDir);
            hiddenCats = this.settings.HiddenCategories;
            catalog.ApplyUsage(usage);
            catalog.HideCategories(hiddenCats);
            layoutPath = Path.Combine(baseDir, "config", "layout.json");
            lastCatPath = Path.Combine(baseDir, "config", "last-category.json");
            orderPath = Path.Combine(baseDir, "config", "order.json");
            LoadCustomOrders();
            LoadLastCategory();

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 640;
            MinHeight = 440;
            ShowInTaskbar = false;
            Topmost = true;
            Title = "四叶草面板";
            Width = 912;
            Height = 632;
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (!LoadLayout()) PositionPanel();
            try
            {
                BuildUi();
            }
            catch (Exception ex)
            {
                Ui.LogError(ex, "构建面板界面");
            }

            Deactivated += delegate(object s, EventArgs e)
            {
                Ui.Log("Deactivated active=" + IsActive + " everActivated=" + everActivated
                    + " suppress=" + (DateTime.Now < suppressDeactivate) + " menu=" + taskMenuOpen);
                if (taskMenuOpen) return;
                if (DateTime.Now < suppressDeactivate) return;
                if (trigger != null && trigger.IsActive) return;

                // Show()/Activate() 期间可能先派发一次 Deactivated，紧接着又 Activated。
                // 延迟到本调度周期之后再确认，避免面板“刚弹出就被自己收起来”。
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    try
                    {
                        if (!IsVisible) return;
                        if (taskMenuOpen) return;
                        if (DateTime.Now < suppressDeactivate) return;
                        if (trigger != null && trigger.IsActive) return;
                        // 从来没被激活过（例如前台激活被系统拒绝）就不要自动收起，
                        // 否则面板会在用户还没看到时自己消失。
                        if (!everActivated) return;
                        if (IsActive) return; // 已经重新激活，不收起
                        HidePanel();
                    }
                    catch (Exception ex)
                    {
                        Ui.LogError(ex, "自动收起面板");
                    }
                }));
            };
            Activated += delegate
            {
                everActivated = true;
                Ui.Log("Activated");
                // 面板被激活时刷新前台窗口缓存：此刻鼠标通常还没移到任务栏列表上，
                // 先把缓存更新好，划过时高亮就是对的（避免读到最多 1.5 秒前的旧值）。
                RefreshActiveCache();
            };
            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape)
                {
                    HidePanel();
                    e.Handled = true;
                    return;
                }

                // 搜索框里：方向键交给光标移动，只在上下/回车时接管，
                // 这样输入文字时按左右键不会跳磁贴选中。
                bool inSearch = searchBox != null && searchBox.IsKeyboardFocusWithin;
                switch (e.Key)
                {
                    case Key.Right:
                    case Key.Left:
                        if (inSearch) return;
                        if (MoveSelection(e.Key)) e.Handled = true;
                        return;
                    case Key.Up:
                    case Key.Down:
                    case Key.Home:
                    case Key.End:
                        if (MoveSelection(e.Key)) e.Handled = true;
                        return;
                    case Key.Enter:
                        if (LaunchSelected()) e.Handled = true;
                        return;
                }
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr h = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(h, -20, Native.GetWindowLong(h, -20) | 0x80);
            HwndSource.FromHwnd(h).AddHook(OnResizeHitTest);
        }

        IntPtr OnResizeHitTest(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != 0x0084) return IntPtr.Zero;
            try
            {
                long lp = lParam.ToInt64();
                short sx = (short)(lp & 0xFFFF);
                short sy = (short)((lp >> 16) & 0xFFFF);
                if (expanded) return IntPtr.Zero;
                Point p = PointFromScreen(new Point(sx, sy));
                double m = edgeMargin;
                const double band = 4;
                double w = Width, h = Height;
                bool nearL = p.X >= m - band && p.X <= m + band;
                bool nearR = p.X >= w - m - band && p.X <= w - m + band;
                bool nearT = p.Y >= m - band && p.Y <= m + band;
                bool nearB = p.Y >= h - m - band && p.Y <= h - m + band;
                bool inX = p.X >= m - band && p.X <= w - m + band;
                bool inY = p.Y >= m - band && p.Y <= h - m + band;
                int ht = 0;
                if (nearL && nearT) ht = 13;
                else if (nearR && nearT) ht = 14;
                else if (nearL && nearB) ht = 16;
                else if (nearR && nearB) ht = 17;
                else if (nearL && inY) ht = 10;
                else if (nearR && inY) ht = 11;
                else if (nearT && inX) ht = 12;
                else if (nearB && inX) ht = 15;
                if (ht != 0)
                {
                    handled = true;
                    return new IntPtr(ht);
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        void PositionPanel()
        {
            Rect wa = SystemParameters.WorkArea;
            // 面板左缘对齐开始按钮右侧（开始按钮 0-55），开始按钮保持可见可点
            Left = 55 + 6 - 16;
            Top = wa.Bottom - Height + 10;
            if (Top < 0) Top = 0;
        }

        public void ShowPanel()
        {
            // 抑制窗口必须在 Show() 之前武装：Show() 会同步派发 Deactivated，
            // 之前放在 Show() 之后赋值，导致第一次 Deactivated 完全绕过抑制、面板秒收。
            suppressDeactivate = DateTime.Now.AddMilliseconds(600);

            // 清空搜索框会触发 TextChanged -> OnSearchChanged -> RenderApps，
            // 所以先解除事件、清空、再重挂，避免每次打开面板都强制重建全部磁贴。
            if (searchBox != null && searchBox.Text.Length > 0)
            {
                searchSuppress = true;
                try { searchBox.Text = ""; }
                finally { searchSuppress = false; }
            }
            if (searchHint != null) searchHint.Visibility = Visibility.Visible;

            // 先刷前台窗口缓存（不受枚举限流影响），再决定要不要全量刷新任务列表。
            // RefreshTaskList 有 800ms 最小间隔，可能整段被跳过；那样缓存的句柄就是旧的，
            // 面板刚打开时划过任务栏会高亮错行。
            RefreshActiveCache();
            RefreshTaskList();
            // 只有在内容可能变化时才重建磁贴：反复开关面板不必每次销毁/重建
            // 113 个控件（会持续推高内存，实测 30 次开关涨 100+ MB）。
            RenderAppsIfNeeded();

            if (!IsVisible)
            {
                Opacity = 0;
                Show();
            }
            Activate();
            IntPtr h = new WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero) Native.SetForegroundWindow(h);

            // 复用同一个 reveal 动画对象，避免每次显示都新建 DoubleAnimation
            if (showAnim == null)
            {
                showAnim = new DoubleAnimation(1, TimeSpan.FromMilliseconds(150));
            }
            showAnim.From = 0;
            BeginAnimation(OpacityProperty, showAnim);

            // 轻微上移：淡入的同时从下方 8px 升到原位，让面板"活"一下。
            //
            // 用 rootBorder 的 RenderTransform 而不是改 Top/Margin：
            // RenderTransform 是布局之后应用的渲染变换，**不会触发布局重排**，
            // 也不会和新窗口位置打架（面板位置由 layout.json 记住，不能动）。
            // showShift 已由 BuildUi 接好，这里只跑动画。
            if (showShift != null)
            {
                if (showShiftAnim == null)
                {
                    showShiftAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
                }
                showShiftAnim.From = ShowShiftPx;
                showShift.BeginAnimation(TranslateTransform.YProperty, showShiftAnim);
            }

            if (searchBox != null) searchBox.Focus();
        }

        /// <summary>弹出时面板从下方升起的距离（像素）。</summary>
        const double ShowShiftPx = 8;

        DoubleAnimation showAnim;
        /// <summary>面板整体的位移变换（弹出/收起动画用），由 BuildUi 建立。</summary>
        TranslateTransform showShift;
        DoubleAnimation showShiftAnim;
        DoubleAnimation hideShiftAnim;

        public void HidePanel()
        {
            Ui.Log("HidePanel, visible=" + IsVisible);
            if (!IsVisible) return;
            SaveLayout();
            // 复用单个动画对象，避免每次隐藏都新建 DoubleAnimation + Clock
            // （实测反复开关面板会造成托管堆持续增长）。
            if (hideAnim == null)
            {
                hideAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
                hideAnim.Completed += delegate
                {
                    BeginAnimation(OpacityProperty, null);
                    Hide();
                };
            }
            hideAnim.From = Opacity;
            BeginAnimation(OpacityProperty, hideAnim);

            // 收起时向下方滑出一点，与弹出的上移对称（观感上"缩回去"而不是"凭空消失"）。
            //
            // 注意：目标值是 +ShowShiftPx（往下），不是 0 —— 静止位置本来就是 0，
            // 动画到 0 等于什么都没做。滑走停在下方，下次 ShowPanel 再从 +ShowShiftPx
            // 动画回 0，两边首尾相接。
            if (showShift != null)
            {
                if (hideShiftAnim == null)
                {
                    hideShiftAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
                }
                hideShiftAnim.From = showShift.Y;
                hideShiftAnim.To = ShowShiftPx;
                showShift.BeginAnimation(TranslateTransform.YProperty, hideShiftAnim);
            }
        }

        DoubleAnimation hideAnim;

        void ToggleExpand()
        {
            try
            {
                if (!expanded)
                {
                    normalRect = new Rect(Left, Top, Width, Height);
                    expanded = true;
                    edgeMargin = 0;
                    rootBorder.Margin = new Thickness(0);
                    rootBorder.CornerRadius = new CornerRadius(0);
                    Rect wa = SystemParameters.WorkArea;
                    Left = wa.Left;
                    Top = wa.Top;
                    Width = wa.Width;
                    Height = wa.Height;
                    maxBtn.Child = MakeGlyph("\uE923");
                    Ui.Log("expand -> " + Width + "x" + Height);
                }
                else
                {
                    expanded = false;
                    edgeMargin = 16;
                    rootBorder.Margin = new Thickness(16);
                    rootBorder.CornerRadius = new CornerRadius(16);
                    Left = normalRect.X;
                    Top = normalRect.Y;
                    Width = normalRect.Width;
                    Height = normalRect.Height;
                    maxBtn.Child = MakeGlyph("\uE922");
                    Ui.Log("restore -> " + Left + "," + Top + " " + Width + "x" + Height);
                }
            }
            catch { }
        }

        bool LoadLayout()
        {
            try
            {
                if (!File.Exists(layoutPath)) return false;
                System.Web.Script.Serialization.JavaScriptSerializer ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                string layoutJson = JsonIO.Read(layoutPath);
                if (string.IsNullOrEmpty(layoutJson)) return false;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(layoutJson);
                double l = Convert.ToDouble(d["left"]);
                double t = Convert.ToDouble(d["top"]);
                double w = Convert.ToDouble(d["width"]);
                double h = Convert.ToDouble(d["height"]);
                if (w < MinWidth || h < MinHeight) return false;
                Width = w;
                Height = h;
                Left = l;
                Top = t;
                ClampToScreen();
                return true;
            }
            catch { return false; }
        }

        void SaveLayout()
        {
            try
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["left"] = expanded ? normalRect.X : Left;
                d["top"] = expanded ? normalRect.Y : Top;
                d["width"] = expanded ? normalRect.Width : Width;
                d["height"] = expanded ? normalRect.Height : Height;
                System.Web.Script.Serialization.JavaScriptSerializer ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                JsonIO.WriteAtomic(layoutPath, ser.Serialize(d));
            }
            catch { }
        }

        void ClampToScreen()
        {
            double sw = SystemParameters.PrimaryScreenWidth;
            double sh = SystemParameters.PrimaryScreenHeight;
            if (Left < -60) Left = 0;
            if (Top < 0) Top = 0;
            if (Left > sw - 120) Left = sw - 120;
            if (Top > sh - 120) Top = sh - 120;
            if (Width > sw + 40) Width = sw + 40;
        }

        void Launch(AppItem a)
        {
            Ui.Log("Launch: " + a.Name + " path=" + a.Path);
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(a.Path);
                psi.UseShellExecute = true;
                Process.Start(psi);
                if (usage != null) usage.Record(a.Path);
                a.Usage++;
                a.LastUsed = DateTime.UtcNow.Ticks;
                HidePanel();
            }
            catch (Exception ex)
            {
                Ui.Log("Launch error: " + ex.Message);
                MessageBox.Show("无法启动 " + a.Name + "\r\n" + ex.Message, "四叶草面板");
            }
        }
    }
}
