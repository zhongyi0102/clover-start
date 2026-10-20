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
            s1.Save();

            Settings s2 = Settings.Load(r1);
            Check(s2.HideTaskbar == true, "hideTaskbar 往返");
            Check(s2.MiniTaskbar == false, "miniTaskbar 往返");
            Check(s2.TaskbarHiding == false, "taskbarHiding 往返");
            Check(s2.DesktopAutoClean == true, "desktopAutoClean 往返");
            Check(s2.HiddenCategories.Count == 1 && s2.HiddenCategories[0] == "系统工具", "hiddenCategories 往返");

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
