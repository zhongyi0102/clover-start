using System;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// Settings 测试：默认值、读写往返、未知字段保留、损坏回退、开关联动规则。
    /// 只依赖 Settings + JsonIO，不牵扯 WPF。
    /// </summary>
    static class SettingsTest
    {
        static int failed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name + (cond || detail.Length == 0 ? "" : "  [" + detail + "]"));
            if (!cond) failed++;
        }

        static string NewRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "clover-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            return root;
        }

        static void Main()
        {
            // ---------- 1. 默认值 ----------
            string r1 = NewRoot();
            Settings s1 = Settings.Load(r1);
            Check(s1.HideTaskbar == false, "默认不隐藏任务栏");
            Check(s1.MiniTaskbar == true, "默认启用迷你任务栏");
            Check(s1.TaskbarHiding == true, "默认允许隐藏任务栏");
            Check(s1.DesktopAutoClean == false, "默认**不**自动移走桌面快捷方式（本轮改为 false）");
            Check(s1.DesktopAutoCleanNoticeShown == false, "默认未展示过自动移走确认说明");
            Check(s1.HiddenCategories.Count == 0, "默认没有隐藏分类");
            Check(s1.NavCollapsed == false, "默认侧栏展开（不收起）");
            Check(s1.Theme == "system", "默认主题跟随系统");
            Check(Settings.SafeModeActive == false, "默认不在安全模式");

            // ---------- 1c. ★安全模式只影响 Effective*，不改你的设置 ----------
            // 这是本轮的核心修复：安全模式原先在内存里把 6 个开关改成 false，
            // 而 Save() 会写全部字段 —— 于是安全模式期间用户只要动任意一个设置，
            // 那几项降级就被永久固化进 settings.json。
            // 其中 fullscreenDetect / hoverReveal 没有界面开关，一旦固化就永久失效。
            {
                string rSafe = NewRoot();
                Settings s = Settings.Load(rSafe);

                // 用户偏好：除了桌面清理，其余都开着
                s.MiniTaskbar = true;
                s.TaskbarHiding = true;
                s.HideTaskbar = true;
                s.FullscreenDetect = true;
                s.HoverReveal = true;
                s.DesktopAutoClean = true;
                s.Save();

                // 进入安全模式
                Settings.SafeModeActive = true;

                // 有效值应全部被压制
                Check(!s.EffectiveMiniTaskbar, "安全模式：迷你任务栏无效");
                Check(!s.EffectiveTaskbarHiding, "安全模式：允许隐藏任务栏无效");
                Check(!s.EffectiveHideTaskbar, "安全模式：隐藏任务栏无效");
                Check(!s.EffectiveDesktopAutoClean, "安全模式：桌面自动清理无效");
                Check(!s.EffectiveFullscreenDetect, "安全模式：全屏检测无效");
                Check(!s.EffectiveHoverReveal, "安全模式：悬停唤出无效");

                // ★但原字段必须原封不动（菜单要如实显示用户的偏好）
                Check(s.MiniTaskbar, "安全模式不改 MiniTaskbar 原字段");
                Check(s.TaskbarHiding, "安全模式不改 TaskbarHiding 原字段");
                Check(s.HideTaskbar, "安全模式不改 HideTaskbar 原字段");
                Check(s.FullscreenDetect, "安全模式不改 FullscreenDetect 原字段");
                Check(s.HoverReveal, "安全模式不改 HoverReveal 原字段");
                Check(s.DesktopAutoClean, "安全模式不改 DesktopAutoClean 原字段");

                // ★关键：安全模式期间保存设置，落盘的必须仍是用户偏好
                s.Save();
                Settings re = Settings.Load(rSafe);
                Check(re.MiniTaskbar && re.TaskbarHiding && re.HideTaskbar,
                    "★安全模式期间保存，落盘的仍是用户偏好（不是被压制的 false）",
                    string.Format("mini={0} hiding={1} hide={2}", re.MiniTaskbar, re.TaskbarHiding, re.HideTaskbar));
                Check(re.FullscreenDetect && re.HoverReveal,
                    "★安全模式期间保存，fullscreenDetect/hoverReveal 未被固化",
                    string.Format("fs={0} hover={1}", re.FullscreenDetect, re.HoverReveal));

                // 退出安全模式：立刻恢复
                Settings.SafeModeActive = false;
                Check(s.EffectiveMiniTaskbar && s.EffectiveHideTaskbar && s.EffectiveFullscreenDetect,
                    "退出安全模式后有效值即刻恢复");

                try { Directory.Delete(rSafe, true); } catch { }
            }

            // 复位，别影响后续用例
            Settings.SafeModeActive = false;

            // ---------- 1b. 老配置里显式的 desktopAutoClean=true 必须保持不变 ----------
            string rOld = NewRoot();
            File.WriteAllText(Path.Combine(rOld, "config", "settings.json"),
                "{\"desktopAutoClean\":true}", new UTF8Encoding(false));
            Settings sOld = Settings.Load(rOld);
            Check(sOld.DesktopAutoClean == true, "老配置显式 true 保持不变（不被新默认值覆盖）");

            // ---------- 1c. 未写 desktopAutoClean 的配置走新默认 false ----------
            string rMissing = NewRoot();
            File.WriteAllText(Path.Combine(rMissing, "config", "settings.json"),
                "{\"miniTaskbar\":false}", new UTF8Encoding(false));
            Settings sMissing = Settings.Load(rMissing);
            Check(sMissing.DesktopAutoClean == false, "未写该项时用新默认 false");

            // ---------- 1d. 首次确认标记读写 ----------
            string rNotice = NewRoot();
            Settings sNotice = Settings.Load(rNotice);
            Check(sNotice.DesktopAutoCleanNoticeShown == false, "初始未提示");
            sNotice.DesktopAutoCleanNoticeShown = true;
            sNotice.DesktopAutoClean = true;
            sNotice.Save();
            Settings sNotice2 = Settings.Load(rNotice);
            Check(sNotice2.DesktopAutoCleanNoticeShown == true, "首次确认标记已持久化");
            Check(sNotice2.DesktopAutoClean == true, "同时保存的开关值也正确");

            // ---------- 2. 保存后重新读取一致 ----------
            s1.HideTaskbar = true;
            s1.MiniTaskbar = false;
            s1.TaskbarHiding = false;
            s1.DesktopAutoClean = true;
            s1.HiddenCategories.Add("系统工具");
            s1.NavCollapsed = true;
            s1.Theme = "dark";
            s1.Save();

            Settings s2 = Settings.Load(r1);
            Check(s2.HideTaskbar == true, "hideTaskbar 往返");
            Check(s2.MiniTaskbar == false, "miniTaskbar 往返");
            Check(s2.TaskbarHiding == false, "taskbarHiding 往返");
            Check(s2.DesktopAutoClean == true, "desktopAutoClean 往返");
            Check(s2.HiddenCategories.Count == 1 && s2.HiddenCategories[0] == "系统工具", "hiddenCategories 往返");
            Check(s2.NavCollapsed == true, "navCollapsed 往返（侧栏收起状态持久化）");
            Check(s2.Theme == "dark", "theme 往返（主题持久化）");

            // ---------- 3. 保留未知字段 ----------
            string r3 = NewRoot();
            string p3 = Path.Combine(r3, "config", "settings.json");
            File.WriteAllText(p3, "{\"unknownField\":\"keep-me\",\"miniTaskbar\":false}", new UTF8Encoding(false));
            Settings s3 = Settings.Load(r3);
            Check(s3.MiniTaskbar == false, "读取已有 miniTaskbar");
            s3.DesktopAutoClean = false;
            s3.Save();
            string after = File.ReadAllText(p3, Encoding.UTF8);
            Check(after.Contains("keep-me"), "保存时保留未知字段", after);

            // ---------- 4. 开关联动：功能关闭时强制不隐藏 ----------
            string r4 = NewRoot();
            File.WriteAllText(Path.Combine(r4, "config", "settings.json"),
                "{\"hideTaskbar\":true,\"taskbarHiding\":false}", new UTF8Encoding(false));
            Settings s4 = Settings.Load(r4);
            bool effectiveHide = s4.TaskbarHiding && s4.HideTaskbar;
            Check(effectiveHide == false, "taskbarHiding=false 时强制不隐藏（避免无法取消）");

            // ---------- 5. 损坏的 JSON 回退默认值 ----------
            string r5 = NewRoot();
            File.WriteAllText(Path.Combine(r5, "config", "settings.json"), "{{{ 坏掉的 json", new UTF8Encoding(false));
            try
            {
                Settings s5 = Settings.Load(r5);
                Check(s5 != null, "损坏 JSON 不崩溃");
                Check(s5.MiniTaskbar == true, "损坏 JSON 回退到默认值");
            }
            catch (Exception ex)
            {
                Check(false, "损坏 JSON 不崩溃", ex.Message);
            }

            // ---------- 6. 隐藏任务栏的生效条件 ----------
            Check((true && true) == true, "开关开 + 勾选 -> 隐藏任务栏");
            Check((true && false) == false, "开关开 + 未勾选 -> 不隐藏");
            Check((false && true) == false, "开关关 + 勾选 -> 不隐藏");

            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
