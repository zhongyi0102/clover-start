using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Clover
{
    enum AppHideAction { Skip, Hide, Keep }

    sealed class AppHideChoice
    {
        public string Path;
        public AppHideAction Action;
    }

    sealed class AppHideSuggestion
    {
        public AppItem App;
        public string Group;
        public string Explanation;
        public bool DefaultHide;
    }

    /// <summary>
    /// 按具体入口识别配套项，不按整个软件家族匹配。
    /// 主动使用、固定与保留例外优先；启动过的入口默认暂不处理。
    /// </summary>
    static class AppHideSuggestions
    {
        static bool Matches(string name, string pattern)
        {
            return Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }

        static bool ContainsPath(IEnumerable<string> paths, string path)
        {
            if (paths == null) return false;
            string key = HiddenApps.Normalize(path);
            foreach (string p in paths)
                if (HiddenApps.Normalize(p) == key) return true;
            return false;
        }

        internal static AppHideSuggestion Explain(AppItem app)
        {
            if (app == null || string.IsNullOrWhiteSpace(app.Path)) return null;
            string name = (app.Name ?? "").Trim();
            string group = "", reason = "";
            bool defaultHide = false;
            if (Matches(name, @"\APython(?:\s+\d+\.\d+(?:\.\d+)?)?(?:\s*\((?:32|64)[ -]bit\))?\z")
                || Matches(name, @"\APython Launcher\z"))
            {
                group = "运行环境入口";
                reason = "Python 命令行运行入口，通常由脚本或开发工具调用；如果你直接打开它，请保留。";
                defaultHide = true;
            }
            else if (Matches(name, @"\AIDLE(?:\s*\(Python\s+\d+\.\d+(?:\.\d+)?(?:\s+(?:32|64)[ -]bit)?\))?\z"))
            {
                group = "可选配套界面";
                reason = "Python 自带的交互式编辑器，不是纯后台组件；只有不用 IDLE 时才隐藏。";
                defaultHide = true;
            }
            else if (Matches(name, @"\AAnaconda Navigator(?:\s*\([^()]+\))?\z"))
            {
                group = "可选配套界面";
                reason = "Anaconda 的图形管理入口；如果通过它管理环境或启动工具，请保留。";
                defaultHide = true;
            }
            else if (Matches(name, @"\ANode\.js\z")
                || Matches(name, @"\AJava(?:\s+\d+(?:\.\d+)*(?:\s+Update\s+\d+)?)?(?:\s*\((?:32|64)[ -]bit\))?\z")
                || Matches(name, @"\AJava SE Development(?: Kit)?(?:\s+\d+(?:\.\d+)*)?(?:\s*\((?:32|64)[ -]bit\))?\z"))
            {
                group = "运行环境入口";
                reason = "语言运行环境或开发套件入口；隐藏仅收起面板图标，不影响其他软件调用。";
                defaultHide = true;
            }
            else if (Matches(name, @"\AApplication Verifier(?:\s*\((?:WOW|X64|X86)\))?\z")
                || Matches(name, @"\AWindows App Cert Kit\z")
                || Matches(name, @"\A(?:Sample|Tools for) (?:Desktop|UWP) Apps\z"))
            {
                group = "开发配套入口";
                reason = "SDK 的示例、验证或配套工具；开发测试时可能需要，确认不用后再隐藏。";
                defaultHide = true;
            }
            else if (Matches(name, @"\APython\s+\d+\.\d+(?:\.\d+)?\s+Module Docs(?:\s*\((?:32|64)[ -]bit\))?\z")
                || Matches(name, @"\A(?:x64|x86|x64_x86|x86_x64) (?:Native|Cross) Tools(?: Command Prompt)?(?: for VS \d{4})?\z")
                || Matches(name, @"\A(?:SDK Configuration|Debuggable Package Manager|Reset Spyder Settings)\z"))
            {
                group = "开发配套入口";
                reason = "软件随附的文档、配置、示例或构建辅助入口；主应用仍保留显示。";
                defaultHide = true;
            }
            if (reason.Length == 0) return null;
            return new AppHideSuggestion { App = app, Group = group, Explanation = reason,
                DefaultHide = defaultHide && app.Usage <= 0 };
        }

        /// <summary>不用的明确配套项自动隐藏；有启动记录、主动角色、固定和例外均优先。</summary>
        internal static int ApplyAutomatic(IEnumerable<AppItem> apps, Settings settings, HiddenApps hidden)
        {
            if (hidden == null) return 0;
            int count = 0;
            foreach (AppHideSuggestion suggestion in Build(apps, settings, hidden))
            {
                // 手动归为辅助配套，不等于用户不用；只自动处理明确识别到的入口。
                if (!suggestion.DefaultHide) continue;
                if (!hidden.Hide(suggestion.App.Path, HideReason.Automatic(suggestion.Group))) continue;
                suggestion.App.Hidden = true;
                count++;
            }
            return count;
        }

        internal static List<AppHideSuggestion> Build(IEnumerable<AppItem> apps, Settings settings, HiddenApps hidden)
        {
            List<AppHideSuggestion> result = new List<AppHideSuggestion>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (apps == null) return result;
            foreach (AppItem app in apps)
            {
                if (app == null || app.Hidden || string.IsNullOrWhiteSpace(app.Path)) continue;
                if (hidden != null && (hidden.IsHidden(app.Path) || hidden.IsException(app.Path))) continue;
                if (settings != null && ContainsPath(settings.ActiveAppRoles, app.Path)) continue;
                AppHideSuggestion suggestion = Explain(app);
                if (suggestion == null && settings != null && ContainsPath(settings.AuxiliaryAppRoles, app.Path))
                    suggestion = new AppHideSuggestion { App = app, Group = "手动辅助配套",
                        Explanation = "你已归为辅助配套；确认不会直接打开它后，可隐藏。", DefaultHide = false };
                if (suggestion != null && seen.Add(HiddenApps.Normalize(app.Path))) result.Add(suggestion);
            }
            result.Sort(delegate(AppHideSuggestion x, AppHideSuggestion y)
            { return string.Compare(x.App.Name, y.App.Name, StringComparison.CurrentCultureIgnoreCase); });
            return result;
        }

        /// <summary>确认后重新检查最新候选；过期路径、保留项与不认识的动作都跳过。</summary>
        internal static int Apply(IEnumerable<AppItem> apps, Settings settings, HiddenApps hidden,
            IEnumerable<AppHideChoice> choices)
        {
            if (hidden == null || choices == null) return 0;
            Dictionary<string, AppHideAction> actions = new Dictionary<string, AppHideAction>(StringComparer.OrdinalIgnoreCase);
            foreach (AppHideChoice choice in choices)
            {
                if (choice == null || string.IsNullOrWhiteSpace(choice.Path)) continue;
                string key = HiddenApps.Normalize(choice.Path);
                AppHideAction previous;
                // 同一路径出现冲突时保留优先，避免重复输入误隐藏。
                if (!actions.TryGetValue(key, out previous) || previous != AppHideAction.Keep)
                    actions[key] = choice.Action;
            }
            int count = 0;
            foreach (AppHideSuggestion suggestion in Build(apps, settings, hidden))
            {
                AppHideAction action;
                if (!actions.TryGetValue(HiddenApps.Normalize(suggestion.App.Path), out action)) continue;
                if (action == AppHideAction.Keep) hidden.ShowWithException(suggestion.App.Path);
                else if (action == AppHideAction.Hide && hidden.Hide(suggestion.App.Path, HideReason.Suggested(suggestion.Group)))
                {
                    suggestion.App.Hidden = true;
                    count++;
                }
            }
            return count;
        }
    }
}
