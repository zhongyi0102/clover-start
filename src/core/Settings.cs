using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// config\settings.json 的统一读写入口。
    /// 之前 TriggerWindow 自己读 hideTaskbar、PanelWindow 自己读 hiddenCategories，
    /// 各写各的容易互相覆盖；现在集中在这里，保存时会保留未知字段。
    /// </summary>
    class Settings
    {
        /// <summary>隐藏系统任务栏（当前状态）。默认 false。</summary>
        public bool HideTaskbar = false;

        /// <summary>是否启用“迷你任务栏”功能（左下角显示窗口按钮）。默认 true。</summary>
        public bool MiniTaskbar = true;

        /// <summary>是否允许“隐藏系统任务栏”这个功能。默认 true。</summary>
        public bool TaskbarHiding = true;

        /// <summary>
        /// 是否自动把桌面快捷方式移到 desktop-backup。
        /// **默认 false**（不主动动用户桌面）；老配置里显式写了 true 的保持 true。
        /// </summary>
        public bool DesktopAutoClean = false;

        /// <summary>
        /// 是否已经给用户看过「自动移走桌面快捷方式」的确认说明。
        /// 首次勾选时弹一次，之后不再重复弹。
        /// </summary>
        public bool DesktopAutoCleanNoticeShown = false;

        /// <summary>是否启用悬停唤出（全屏时鼠标移到按钮位置临时显示）。默认 true。</summary>
        public bool HoverReveal = true;

        /// <summary>是否启用全屏检测（全屏时自动隐藏悬浮按钮）。默认 true。</summary>
        public bool FullscreenDetect = true;

        /// <summary>强制安全模式：不隐藏任务栏、不启用迷你任务栏、不移动桌面快捷方式、不做全屏钩子。</summary>
        public bool SafeMode = false;

        /// <summary>要隐藏的分类（「系统工具」默认隐藏）。</summary>
        public List<string> HiddenCategories = new List<string>();

        /// <summary>
        /// 侧栏是否已收起。默认 false（展开）。
        ///
        /// 放进 settings.json 而不是单独一个文件：它和 miniTaskbar 一样是
        /// "用户对界面的一个持久偏好"，没必要为它多一个配置文件。
        /// </summary>
        public bool NavCollapsed = false;

        /// <summary>
        /// 主题：light / dark / system。默认 system（跟随 Windows）。
        ///
        /// 存字符串而不是枚举，避免 core 层依赖界面层的类型；
        /// 非法值统一按 system 处理（见 Ui.ParseTheme）。
        /// </summary>
        public string Theme = "system";

        /// <summary>
        /// 本次运行是否处于安全模式。
        ///
        /// **只在运行时生效，不写回 settings.json。**
        ///
        /// 为什么不直接改各个开关的值：`Save()` 会写**全部字段**，
        /// 而安全模式原本是在内存里把 6 个开关改成 false。于是安全模式期间
        /// 用户只要动任意一个设置（换主题、开关别的功能），
        /// 那 6 项降级就会被**永久固化进 settings.json**。
        /// 其中 `fullscreenDetect` / `hoverReveal` 没有界面开关，
        /// 一旦被固化就永久失效，只能手改 JSON。
        ///
        /// 现在改成：用户的偏好原封不动留在文件里，安全模式只让
        /// `Effective*` 系列返回 false，用户一退出安全模式立刻恢复原样。
        /// </summary>
        public static bool SafeModeActive = false;

        // ---- 安全模式感知的有效值 ----
        // 所有**消费方**（窗口、悬浮条、监听器）都应读这些，而不是直接读字段，
        // 否则安全模式挡不住。菜单显示则读原字段（见 TriggerWindow 的说明）。

        public bool EffectiveMiniTaskbar
        {
            get { return MiniTaskbar && !SafeModeActive; }
        }

        public bool EffectiveTaskbarHiding
        {
            get { return TaskbarHiding && !SafeModeActive; }
        }

        /// <summary>实际是否该隐藏任务栏（总开关 + 当前状态 + 安全模式）。</summary>
        public bool EffectiveHideTaskbar
        {
            get { return TaskbarHiding && HideTaskbar && !SafeModeActive; }
        }

        public bool EffectiveDesktopAutoClean
        {
            get { return DesktopAutoClean && !SafeModeActive; }
        }

        public bool EffectiveFullscreenDetect
        {
            get { return FullscreenDetect && !SafeModeActive; }
        }

        public bool EffectiveHoverReveal
        {
            get { return HoverReveal && !SafeModeActive; }
        }

        string path;

        /// <summary>
        /// 警告输出钩子。默认接到 ErrorLog（WPF 程序里由 Program 设置），
        /// 测试环境可以不设，避免 Settings 强依赖 WPF 的 ErrorLog。
        /// </summary>
        public static Action<string> Warn = delegate { };

        public static Settings Load(string baseDir)
        {
            Settings s = new Settings();
            s.path = Path.Combine(baseDir, "config", "settings.json");
            try
            {
                string json = JsonIO.Read(s.path);
                if (string.IsNullOrEmpty(json)) return s;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return s;

                object v;
                if (d.TryGetValue("hideTaskbar", out v) && v != null) s.HideTaskbar = Convert.ToBoolean(v);
                if (d.TryGetValue("miniTaskbar", out v) && v != null) s.MiniTaskbar = Convert.ToBoolean(v);
                if (d.TryGetValue("taskbarHiding", out v) && v != null) s.TaskbarHiding = Convert.ToBoolean(v);
                if (d.TryGetValue("desktopAutoClean", out v) && v != null) s.DesktopAutoClean = Convert.ToBoolean(v);
                if (d.TryGetValue("desktopAutoCleanNoticeShown", out v) && v != null) s.DesktopAutoCleanNoticeShown = Convert.ToBoolean(v);
                if (d.TryGetValue("hoverReveal", out v) && v != null) s.HoverReveal = Convert.ToBoolean(v);
                if (d.TryGetValue("fullscreenDetect", out v) && v != null) s.FullscreenDetect = Convert.ToBoolean(v);
                if (d.TryGetValue("safeMode", out v) && v != null) s.SafeMode = Convert.ToBoolean(v);
                if (d.TryGetValue("navCollapsed", out v) && v != null) s.NavCollapsed = Convert.ToBoolean(v);
                if (d.TryGetValue("theme", out v) && v != null) s.Theme = Convert.ToString(v);
                if (d.TryGetValue("hiddenCategories", out v) && v != null)
                {
                    // 循环变量名不能叫 s —— 它是外层的 Settings 对象
                    foreach (string cat in JsonIO.AsStringArray(v)) s.HiddenCategories.Add(cat);
                }
            }
            catch (Exception ex)
            {
                Warn("读取 settings.json 失败，使用默认设置: " + ex.Message);
            }
            return s;
        }

        /// <summary>写回 settings.json；保留文件里已有的其它字段，不覆盖别人写的内容。</summary>
        public void Save()
        {
            try
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                // 先读回现有内容，保留未知字段
                try
                {
                    string oldJson = JsonIO.Read(path);
                    if (!string.IsNullOrEmpty(oldJson))
                    {
                        JavaScriptSerializer ser0 = new JavaScriptSerializer();
                        Dictionary<string, object> old = ser0.Deserialize<Dictionary<string, object>>(oldJson);
                        if (old != null) d = old;
                    }
                }
                catch { }

                d["hideTaskbar"] = HideTaskbar;
                d["miniTaskbar"] = MiniTaskbar;
                d["taskbarHiding"] = TaskbarHiding;
                d["desktopAutoClean"] = DesktopAutoClean;
                d["desktopAutoCleanNoticeShown"] = DesktopAutoCleanNoticeShown;
                d["hoverReveal"] = HoverReveal;
                d["fullscreenDetect"] = FullscreenDetect;
                d["safeMode"] = SafeMode;
                d["navCollapsed"] = NavCollapsed;
                d["theme"] = Theme;
                d["hiddenCategories"] = HiddenCategories;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                JsonIO.WriteAtomic(path, ser.Serialize(d));
            }
            catch (Exception ex)
            {
                Warn("保存 settings.json 失败: " + ex.Message);
            }
        }
    }
}
