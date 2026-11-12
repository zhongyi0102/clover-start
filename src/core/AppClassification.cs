using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>三类入口。首次分类存量；新入口默认用户类并持久保留，显式选择优先。</summary>
    class AppClassification
    {
        readonly Dictionary<string, AppRole> roles = new Dictionary<string, AppRole>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, object> unknown = new Dictionary<string, object>();
        readonly string path;
        bool initialized, readable = true;

        public AppClassification(string baseDir)
        {
            path = Path.Combine(baseDir, "config", "app-classification.json");
            try
            {
                string json = JsonIO.Read(path);
                if (string.IsNullOrEmpty(json))
                {
                    if (File.Exists(path)) throw new InvalidDataException("分类配置不可读取");
                    return;
                }
                Dictionary<string, object> data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json);
                object value;
                if (data == null) throw new InvalidDataException("分类数据为空");
                initialized = data.TryGetValue("initialized", out value) && Convert.ToBoolean(value);
                if (data.TryGetValue("roles", out value))
                {
                    Dictionary<string, object> saved = value as Dictionary<string, object>;
                    if (saved == null) throw new InvalidDataException("分类映射损坏");
                    foreach (KeyValuePair<string, object> pair in saved)
                    {
                        AppRole role;
                        if (Enum.TryParse<AppRole>(Convert.ToString(pair.Value), true, out role) && Enum.IsDefined(typeof(AppRole), role))
                            roles[HiddenApps.Normalize(pair.Key)] = role;
                    }
                }
                foreach (KeyValuePair<string, object> pair in data)
                    if (pair.Key != "version" && pair.Key != "initialized" && pair.Key != "roles") unknown[pair.Key] = pair.Value;
            }
            catch (Exception ex) { readable = false; Log.Warn("分类配置损坏，保留原文件：" + ex.Message); }
        }

        static bool Contains(IEnumerable<string> paths, string path)
        {
            if (paths == null) return false;
            string key = HiddenApps.Normalize(path);
            foreach (string candidate in paths) if (HiddenApps.Normalize(candidate) == key) return true;
            return false;
        }

        public bool NeedsBaseline { get { return !initialized && readable; } }

        public void SeedExisting(string name, string appPath)
        {
            if (!NeedsBaseline || string.IsNullOrWhiteSpace(appPath)) return;
            string key = HiddenApps.Normalize(appPath);
            if (!roles.ContainsKey(key)) roles[key] = Guess(name, appPath);
        }

        public void Apply(List<AppItem> apps, Settings settings, HiddenApps hidden)
        {
            if (apps == null) return;
            bool changed = false;
            foreach (AppItem app in apps)
            {
                if (app == null || string.IsNullOrWhiteSpace(app.Path)) continue;
                string key = HiddenApps.Normalize(app.Path);
                AppRole role;
                if (!roles.TryGetValue(key, out role))
                {
                    // 无法辨别是新安装还是新发现，统一按新入口优待，不使用“未启动”降级。
                    role = initialized || !readable ? AppRole.User : Guess(app.Name, app.Path);
                    roles[key] = role;
                    changed = true;
                }
                if (settings != null)
                {
                    if (Contains(settings.ComputerAppRoles, app.Path)) role = AppRole.Computer;
                    if (Contains(settings.AuxiliaryAppRoles, app.Path) || Contains(settings.LowValueMarked, app.Path)) role = AppRole.LowValue;
                    if (Contains(settings.ActiveAppRoles, app.Path) || Contains(settings.LowValueUnmarked, app.Path)) role = AppRole.User;
                }
                if (hidden != null && hidden.IsException(app.Path)) role = AppRole.User;
                app.Hidden = hidden != null && hidden.IsHidden(app.Path);
                // 仅迁移此前自动/建议收起的明确电脑入口。手动、批量隐藏和可选界面保留。
                if (app.Hidden && role == AppRole.Computer && hidden != null)
                {
                    HideReason reason = hidden.ReasonOf(app.Path);
                    if (reason.Type == HideReason.Kind.Automatic || reason.Type == HideReason.Kind.Suggested)
                    { hidden.Show(app.Path); app.Hidden = false; }
                }
                app.Role = role;
                app.RoleDetail = role == AppRole.Computer ? "电脑或其他软件调用；仅搜索显示"
                    : role == AppRole.LowValue ? "命令行、配套配置、维护或待确认入口" : "用户用的";
                app.ExcludedEntry = IsNonAppEntry(app.Name, app.Path);
                app.LowValue = role == AppRole.LowValue;
                app.LowValueReason = app.LowValue ? LowValueReason.ManualMarked : LowValueReason.None;
                app.LowValueDetail = app.LowValue ? app.RoleDetail : "";
            }
            if (!initialized) { initialized = true; changed = true; }
            if (changed && readable) Save();
        }

        public void Set(AppItem app, AppRole role)
        {
            if (app == null || string.IsNullOrWhiteSpace(app.Path)) return;
            roles[HiddenApps.Normalize(app.Path)] = role;
            if (readable) Save();
        }

        public AppRole RoleOf(string path)
        {
            AppRole role;
            return roles.TryGetValue(HiddenApps.Normalize(path), out role) ? role : AppRole.User;
        }

        void Save()
        {
            Dictionary<string, object> data = new Dictionary<string, object>(unknown);
            Dictionary<string, object> saved = new Dictionary<string, object>();
            foreach (KeyValuePair<string, AppRole> entry in roles) saved[entry.Key] = entry.Value.ToString();
            data["version"] = 1; data["initialized"] = initialized; data["roles"] = saved;
            JsonIO.WriteAtomicIfChanged(path, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(data));
        }

        static bool Match(string name, string pattern)
        { return Regex.IsMatch(name ?? "", pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }

        static string Target(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || AppPaths.IsShellPath(path)) return path ?? "";
            return path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutResolver.Resolve(path) ?? path : path;
        }

        public static bool IsNonAppEntry(string name, string path)
        {
            string target = Target(path);
            string ext = Path.GetExtension(target).ToLowerInvariant();
            string exe = Path.GetFileNameWithoutExtension(target);
            if (Match(name, @"\A(?:卸载|Uninstall(?:\s|\z))")
                || Match(exe, @"\A(?:unins|uninst|uninstall)|(?:-uninstall|uninstaller)\z")) return true;
            if (Directory.Exists(target)) return true;
            foreach (string fileExt in new[] { ".chm", ".txt", ".pdf", ".html", ".htm", ".ico", ".dll", ".msi", ".msix", ".appx" })
                if (ext == fileExt) return true;
            if (ext == ".exe" && (target.IndexOf(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) >= 0
                || target.IndexOf(@"\Windows\Installer\", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            // 游戏协议 .url 仍是应用；网页/文档由桌面内容规则判断。
            if (ext == ".url" && DesktopContentRules.Classify(target) == DesktopContentKind.NonApplication) return true;
            return false;
        }

        public static AppRole Guess(string name, string path)
        {
            string n = (name ?? "").Trim();
            string target = Target(path);
            string exe = Path.GetFileNameWithoutExtension(target);
            // 只匹配具体身份，避免 pythonw 启动的 Spyder/桌宠和 update.exe 的传输界面误伤。
            if (Match(n, @"\A(?:Python(?:\s+\d+\.\d+(?:\.\d+)?)?(?:\s*\((?:32|64)[ -]bit\))?|Python Launcher|Node\.js|Java(?:\s+\d+(?:\.\d+)*(?:\s+Update\s+\d+)?)?(?:\s*\((?:32|64)[ -]bit\))?|Java SE Development(?: Kit)?(?:\s+\d+(?:\.\d+)*(?:\s+Update\s+\d+)?)?(?:\s*\((?:32|64)[ -]bit\))?)\z")
                || Match(n, @"\A(?:Apple Mobile Device Support|MySQL Router(?:\s+[\d.]+)?|SnoreToast|AO-SemanticRouter|Bonjour|AntiCheatExpert|Npcap)\z")
                || Match(n, @"\A(?:Application Verifier(?:\s*\((?:WOW|X64|X86)\))?|Windows App Cert Kit|Debuggable Package Manager|SDK Configuration)\z")
                || Match(n, @"\A(?:x64|x86|x64_x86|x86_x64) (?:Native|Cross) Tools(?: Command Prompt)?(?: for VS \d{4})?\z")
                || Match(n, @"\A(?:Microsoft Visual C\+\+.*(?:Runtime|Redistributable).*|Microsoft Edge WebView2 Runtime|Windows SDK.*|Windows Software Development Kit.*|.*(?:VideoExtension|VideoExtensions|ImageExtension|ImageExtensions))\z")
                || Match(exe, @"\A(?:AppleMobileDeviceService|mysqlrouter|mysqld|redis-server|SnoreToast|OfficeClickToRun|mysql-secret-store-windows-credential|wetype_server|ChromeDriver|licensemanagershellext|msoadfsb|msoasb|sdxhelper|SKYPESERVER|ShellMcpServers\.Packaging|WindowsPackageManagerServer|claude-ssh-askpass|claude-ssh-proxy)\z"))
                return AppRole.Computer;

            if (Match(n, @"\A(?:IDLE(?:\s*\(Python[^)]*\))?|Anaconda Navigator(?:\s*\([^)]*\))?|Deno|Pandoc(?:\s+[\d.]+)?|yt-dlp|FFmpeg(?: for yt-dlp)?|GitHub CLI|RipGrep(?: MSVC)?|Rustup:.*|ngrok|WSL(?: Settings)?|Ubuntu|终端|命令面板|Xagent|配置工具|Office 语言首选项|AMD Software|Lenovo Hotkeys|微信输入法|kuaixcrlespc-64)\z")
                || Match(n, @"\A(?:MySQL Installer.*|Visual Studio Installer|Microsoft Visual Studio Installer|DirectX Repair|HP .*|传真.*向导|扫描.*向导|数字传真.*向导|配置 Java|检查更新|获取帮助|入门|Windows 工具|Windows 安全中心|Windows 备份)\z")
                || Match(n, @"\A(?:Dolby Access|Family|HP Smart|混合现实门户|电影和电视|单击以执行)\z")
                || Match(n, @"\A(?:HP Scan|Git(?: Bash| CMD| CLI)?|Python .*Module Docs.*|Tesseract-OCR.*|Microsoft Windows Application Compatibility Fix Database|vs_CoreEditorFonts)\z")
                || Match(exe, @"\A(?:deno|pandoc|yt-dlp|ffmpeg|ffprobe|gh|rg|rustup|rustc|cargo|ngrok|wsl|mongosh|mysql|redis-cli|tesseract|ollama|winget|wt|javaws|IEDIAGCMD|msoxmled|SystemInformer|TabTip|wabmig|GetHelp)\z")
                || Catalog.IsToolLike(n.ToLowerInvariant())
                || (Catalog.IsSystemTool(n.ToLowerInvariant()) && !Match(n, @"\A(?:File Explorer|Control Panel|Run|Remote Desktop Connection)\z")))
                return AppRole.LowValue;
            return AppRole.User;
        }
    }
}
