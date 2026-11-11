using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml;

namespace Clover
{
    /// <summary>
    /// 开机自启统一入口。
    ///
    /// 新方案使用“当前用户登录时”计划任务，避免 Explorer 对启动文件夹项目的
    /// 分批延迟。旧版快捷方式仍被识别，并且只有在新任务注册、回读、校验全部
    /// 成功后才删除；若计划任务不可用，则保留或创建旧快捷方式作为兼容兜底。
    /// </summary>
    static class Autostart
    {
        internal const string TaskName = "CloverStart";
        internal const string LegacyShortcutName = "四叶草启动器.lnk";
        static readonly object Gate = new object();

        internal sealed class TaskInfo
        {
            public bool Exists;
            public bool Enabled;
            public bool HasLogonTrigger;
            public bool HasExecAction;
            public string ExecutablePath;
            public string Arguments;
            public string WorkingDirectory;
            public bool PolicyValid = true;
        }

        internal sealed class TaskPlan
        {
            public string ExecutablePath;
            public string WorkingDirectory;
            public string UserId;
            public bool LogonTrigger;
            public int DelaySeconds;
            public bool RunElevated;
            public bool StartWhenAvailable;
            public bool DisallowStartIfOnBatteries;
            public bool StopIfGoingOnBatteries;
            public bool UnlimitedExecutionTime;
            public bool IgnoreNewInstance;
            public int Priority;
        }

        internal interface IPlatform
        {
            TaskInfo ReadTask();
            void RegisterTask(TaskPlan plan);
            void DeleteTask();
            bool LegacyShortcutExists();
            void CreateLegacyShortcut(string executablePath, string workingDirectory);
            void DeleteLegacyShortcut();
        }

        internal sealed class State
        {
            public bool ScheduledTaskValid;
            public bool LegacyShortcutExists;
            public string Error;

            public bool Enabled
            {
                get { return ScheduledTaskValid || LegacyShortcutExists; }
            }

            public string Mode
            {
                get
                {
                    if (ScheduledTaskValid) return "登录计划任务（快速）";
                    if (LegacyShortcutExists) return "启动文件夹（兼容）";
                    return "未启用";
                }
            }
        }

        internal sealed class Result
        {
            public bool Success;
            public bool Changed;
            public bool UsingScheduledTask;
            public bool UsingLegacyFallback;
            public string Message;
        }

        public static string ExecutablePath(string baseDir)
        {
            try
            {
                string appExe = Path.Combine(baseDir ?? "", "app", "CloverStart.exe");
                if (File.Exists(appExe)) return Path.GetFullPath(appExe);

                string current = null;
                try
                {
                    current = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                }
                catch { }
                if (!string.IsNullOrEmpty(current)) return Path.GetFullPath(current);
            }
            catch { }
            return Path.Combine(baseDir ?? "", "CloverStart.exe");
        }

        public static string LegacyShortcutPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                LegacyShortcutName);
        }

        public static State Inspect(string executablePath)
        {
            lock (Gate)
            {
                return Inspect(executablePath, new WindowsPlatform());
            }
        }

        internal static State Inspect(string executablePath, IPlatform platform)
        {
            State state = new State();
            if (platform == null)
            {
                state.Error = "自启平台不可用";
                return state;
            }

            try
            {
                TaskInfo task = platform.ReadTask();
                state.ScheduledTaskValid = IsTaskValid(task, executablePath);
            }
            catch (Exception ex)
            {
                state.Error = FriendlyMessage(ex);
            }

            try
            {
                state.LegacyShortcutExists = platform.LegacyShortcutExists();
            }
            catch (Exception ex)
            {
                if (string.IsNullOrEmpty(state.Error)) state.Error = FriendlyMessage(ex);
            }
            return state;
        }

        public static Result Enable(string executablePath)
        {
            lock (Gate)
            {
                return Enable(executablePath, new WindowsPlatform());
            }
        }

        internal static Result Enable(string executablePath, IPlatform platform)
        {
            string exe;
            try
            {
                exe = NormalizePath(executablePath);
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                    return Failed("找不到程序文件，无法设置开机自启");
            }
            catch (Exception ex)
            {
                return Failed("程序路径无效：" + FriendlyMessage(ex));
            }

            string working = Path.GetDirectoryName(exe);
            State before = Inspect(exe, platform);
            bool hadLegacy = before.LegacyShortcutExists;

            try
            {
                if (!before.ScheduledTaskValid)
                    platform.RegisterTask(MakePlan(exe, working));

                TaskInfo verify = platform.ReadTask();
                if (!IsTaskValid(verify, exe))
                    throw new InvalidOperationException("计划任务回读校验失败");

                // 数据安全边界：必须先确认新任务可用，再删旧快捷方式。
                if (platform.LegacyShortcutExists()) platform.DeleteLegacyShortcut();
                if (platform.LegacyShortcutExists())
                    throw new IOException("旧启动快捷方式未能删除");

                return new Result
                {
                    Success = true,
                    Changed = !before.ScheduledTaskValid || hadLegacy,
                    UsingScheduledTask = true,
                    Message = "已启用快速开机自启"
                };
            }
            catch (Exception taskError)
            {
                // 计划任务不可用时，不让“开机自启”功能整体失效。已有旧快捷方式
                // 原样保留；原本没启用则创建一个旧式快捷方式作为兼容兜底。
                try
                {
                    if (!platform.LegacyShortcutExists())
                        platform.CreateLegacyShortcut(exe, working);
                    if (platform.LegacyShortcutExists())
                    {
                        return new Result
                        {
                            Success = true,
                            Changed = !hadLegacy,
                            UsingLegacyFallback = true,
                            Message = "快速自启不可用，已改用兼容模式：" + FriendlyMessage(taskError)
                        };
                    }
                }
                catch (Exception fallbackError)
                {
                    return Failed("设置开机自启失败：" + FriendlyMessage(taskError)
                        + "；兼容模式也失败：" + FriendlyMessage(fallbackError));
                }

                return Failed("设置开机自启失败：" + FriendlyMessage(taskError));
            }
        }

        public static Result Disable()
        {
            lock (Gate)
            {
                return Disable(new WindowsPlatform());
            }
        }

        internal static Result Disable(IPlatform platform)
        {
            bool changed = false;
            string errors = "";

            try
            {
                TaskInfo task = platform.ReadTask();
                if (task != null && task.Exists)
                {
                    platform.DeleteTask();
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                errors = "删除登录计划任务失败：" + FriendlyMessage(ex);
            }

            try
            {
                if (platform.LegacyShortcutExists())
                {
                    platform.DeleteLegacyShortcut();
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                if (errors.Length > 0) errors += "；";
                errors += "删除旧启动快捷方式失败：" + FriendlyMessage(ex);
            }

            try
            {
                TaskInfo remain = platform.ReadTask();
                if (remain != null && remain.Exists)
                {
                    if (errors.Length > 0) errors += "；";
                    errors += "登录计划任务仍然存在";
                }
                if (platform.LegacyShortcutExists())
                {
                    if (errors.Length > 0) errors += "；";
                    errors += "旧启动快捷方式仍然存在";
                }
            }
            catch (Exception ex)
            {
                if (errors.Length > 0) errors += "；";
                errors += "关闭后校验失败：" + FriendlyMessage(ex);
            }

            return new Result
            {
                Success = errors.Length == 0,
                Changed = changed,
                Message = errors.Length == 0 ? "已关闭开机自启" : errors
            };
        }

        public static Result MigrateLegacy(string executablePath)
        {
            lock (Gate)
            {
                return MigrateLegacy(executablePath, new WindowsPlatform());
            }
        }

        internal static Result MigrateLegacy(string executablePath, IPlatform platform)
        {
            try
            {
                if (!platform.LegacyShortcutExists())
                {
                    State state = Inspect(executablePath, platform);
                    return new Result
                    {
                        Success = true,
                        Changed = false,
                        UsingScheduledTask = state.ScheduledTaskValid,
                        Message = "无需迁移"
                    };
                }
            }
            catch (Exception ex)
            {
                return Failed("检查旧自启方式失败：" + FriendlyMessage(ex));
            }

            Result result = Enable(executablePath, platform);
            if (result.UsingLegacyFallback)
            {
                // 迁移失败但旧自启仍在，不算功能损坏；调用方可记录诊断并在下次重试。
                result.Success = true;
                result.Changed = false;
                result.Message = "旧自启方式暂未迁移：" + result.Message;
            }
            else if (result.Success)
            {
                result.Message = "已将旧启动快捷方式迁移为快速登录任务";
            }
            return result;
        }

        static TaskPlan MakePlan(string exe, string working)
        {
            string user = "";
            try { user = WindowsIdentity.GetCurrent().Name; } catch { }
            return new TaskPlan
            {
                ExecutablePath = exe,
                WorkingDirectory = working,
                UserId = user,
                LogonTrigger = true,
                DelaySeconds = 0,
                RunElevated = false,
                StartWhenAvailable = true,
                DisallowStartIfOnBatteries = false,
                StopIfGoingOnBatteries = false,
                UnlimitedExecutionTime = true,
                IgnoreNewInstance = true,
                Priority = 4
            };
        }

        internal static TaskInfo ParseTaskXml(string xml)
        {
            TaskInfo info = new TaskInfo { PolicyValid = false };
            if (string.IsNullOrWhiteSpace(xml)) return info;

            XmlDocument doc = new XmlDocument();
            doc.XmlResolver = null;
            doc.LoadXml(xml);

            XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");

            XmlNode logon = doc.SelectSingleNode("/t:Task/t:Triggers/t:LogonTrigger", ns);
            if (logon != null)
            {
                XmlNode enabled = logon.SelectSingleNode("t:Enabled", ns);
                info.HasLogonTrigger = enabled == null
                    || !string.Equals(enabled.InnerText.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            }

            XmlNode exec = doc.SelectSingleNode("/t:Task/t:Actions/t:Exec", ns);
            if (exec != null)
            {
                XmlNode command = exec.SelectSingleNode("t:Command", ns);
                XmlNode arguments = exec.SelectSingleNode("t:Arguments", ns);
                XmlNode working = exec.SelectSingleNode("t:WorkingDirectory", ns);
                info.ExecutablePath = command == null ? "" : command.InnerText.Trim();
                info.Arguments = arguments == null ? "" : arguments.InnerText.Trim();
                info.WorkingDirectory = working == null ? "" : working.InnerText.Trim();
                info.HasExecAction = !string.IsNullOrWhiteSpace(info.ExecutablePath);
            }

            info.PolicyValid = IsTaskPolicyValid(doc, ns, logon);
            return info;
        }

        static bool IsTaskPolicyValid(XmlDocument doc, XmlNamespaceManager ns, XmlNode logon)
        {
            if (doc == null || ns == null || logon == null) return false;

            XmlNode delay = logon.SelectSingleNode("t:Delay", ns);
            if (delay != null && !TextIs(delay, "PT0S")) return false;

            XmlNode runLevel = doc.SelectSingleNode("/t:Task/t:Principals/t:Principal/t:RunLevel", ns);
            if (runLevel != null && !TextIs(runLevel, "LeastPrivilege")) return false;

            return TextIs(doc.SelectSingleNode(
                       "/t:Task/t:Settings/t:DisallowStartIfOnBatteries", ns), "false")
                && TextIs(doc.SelectSingleNode(
                       "/t:Task/t:Settings/t:StopIfGoingOnBatteries", ns), "false")
                && TextIs(doc.SelectSingleNode(
                       "/t:Task/t:Settings/t:StartWhenAvailable", ns), "true")
                && TextIs(doc.SelectSingleNode(
                       "/t:Task/t:Settings/t:ExecutionTimeLimit", ns), "PT0S")
                && TextIs(doc.SelectSingleNode(
                       "/t:Task/t:Settings/t:MultipleInstancesPolicy", ns), "IgnoreNew")
                && TextIs(doc.SelectSingleNode(
                       "/t:Task/t:Settings/t:Priority", ns), "4");
        }

        static bool TextIs(XmlNode node, string expected)
        {
            return node != null && string.Equals(node.InnerText.Trim(), expected,
                StringComparison.OrdinalIgnoreCase);
        }

        static bool IsTaskValid(TaskInfo task, string executablePath)
        {
            if (task == null || !task.Exists || !task.Enabled
                || !task.HasLogonTrigger || !task.HasExecAction || !task.PolicyValid)
                return false;
            return PathsEqual(task.ExecutablePath, executablePath)
                && string.IsNullOrWhiteSpace(task.Arguments)
                && PathsEqual(task.WorkingDirectory, Path.GetDirectoryName(executablePath));
        }

        static bool PathsEqual(string a, string b)
        {
            try
            {
                string left = NormalizePath(a);
                string right = NormalizePath(b);
                if (left.Length == 0 || right.Length == 0) return false;
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static string NormalizePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string s = value.Trim().Trim('"');
            return Path.GetFullPath(s).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        static Result Failed(string message)
        {
            return new Result { Success = false, Message = message ?? "未知错误" };
        }

        static string FriendlyMessage(Exception ex)
        {
            Exception cur = ex;
            while (cur is TargetInvocationException && cur.InnerException != null)
                cur = cur.InnerException;
            return cur == null || string.IsNullOrEmpty(cur.Message) ? "未知错误" : cur.Message;
        }

        /// <summary>Windows Task Scheduler 2.0 + WScript.Shell 的真实实现。</summary>
        sealed class WindowsPlatform : IPlatform
        {
            const int TaskTriggerLogon = 9;
            const int TaskActionExec = 0;
            const int TaskCreateOrUpdate = 6;
            const int TaskLogonInteractiveToken = 3;
            const int TaskRunLevelLeastPrivilege = 0;
            const int TaskInstancesIgnoreNew = 2;

            public TaskInfo ReadTask()
            {
                object service = null;
                object root = null;
                object task = null;
                try
                {
                    Connect(out service, out root);
                    try
                    {
                        task = Call(root, "GetTask", TaskName);
                    }
                    catch (Exception ex)
                    {
                        if (IsNotFound(ex)) return new TaskInfo { Exists = false };
                        throw;
                    }

                    TaskInfo info = new TaskInfo();
                    info.Exists = true;
                    info.Enabled = Convert.ToBoolean(Get(task, "Enabled"));
                    TaskInfo parsed = ParseTaskXml(Convert.ToString(Get(task, "Xml")));
                    info.HasLogonTrigger = parsed.HasLogonTrigger;
                    info.HasExecAction = parsed.HasExecAction;
                    info.ExecutablePath = parsed.ExecutablePath;
                    info.Arguments = parsed.Arguments;
                    info.WorkingDirectory = parsed.WorkingDirectory;
                    info.PolicyValid = parsed.PolicyValid;
                    return info;
                }
                finally
                {
                    Release(ref task);
                    Release(ref root);
                    Release(ref service);
                }
            }

            public void RegisterTask(TaskPlan plan)
            {
                object service = null;
                object root = null;
                object definition = null;
                object registration = null;
                object principal = null;
                object settings = null;
                object triggers = null;
                object trigger = null;
                object actions = null;
                object action = null;
                object registered = null;
                try
                {
                    Connect(out service, out root);
                    definition = Call(service, "NewTask", 0);

                    registration = Get(definition, "RegistrationInfo");
                    Set(registration, "Description", "四叶草启动面板快速开机自启");
                    Set(registration, "Author", string.IsNullOrEmpty(plan.UserId) ? "CloverStart" : plan.UserId);

                    principal = Get(definition, "Principal");
                    if (!string.IsNullOrEmpty(plan.UserId)) Set(principal, "UserId", plan.UserId);
                    Set(principal, "LogonType", TaskLogonInteractiveToken);
                    Set(principal, "RunLevel", TaskRunLevelLeastPrivilege);

                    settings = Get(definition, "Settings");
                    Set(settings, "Enabled", true);
                    Set(settings, "Hidden", false);
                    Set(settings, "AllowDemandStart", true);
                    Set(settings, "StartWhenAvailable", plan.StartWhenAvailable);
                    Set(settings, "DisallowStartIfOnBatteries", plan.DisallowStartIfOnBatteries);
                    Set(settings, "StopIfGoingOnBatteries", plan.StopIfGoingOnBatteries);
                    Set(settings, "ExecutionTimeLimit", plan.UnlimitedExecutionTime ? "PT0S" : "PT72H");
                    Set(settings, "MultipleInstances", plan.IgnoreNewInstance ? TaskInstancesIgnoreNew : 0);
                    Set(settings, "Priority", plan.Priority);

                    triggers = Get(definition, "Triggers");
                    trigger = Call(triggers, "Create", TaskTriggerLogon);
                    Set(trigger, "Enabled", true);
                    if (!string.IsNullOrEmpty(plan.UserId)) Set(trigger, "UserId", plan.UserId);
                    Set(trigger, "Delay", plan.DelaySeconds <= 0 ? "PT0S" : "PT" + plan.DelaySeconds + "S");

                    actions = Get(definition, "Actions");
                    action = Call(actions, "Create", TaskActionExec);
                    Set(action, "Path", plan.ExecutablePath);
                    Set(action, "Arguments", "");
                    Set(action, "WorkingDirectory", plan.WorkingDirectory);

                    registered = Call(root, "RegisterTaskDefinition", TaskName, definition,
                        TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken, null);
                }
                finally
                {
                    Release(ref registered);
                    Release(ref action);
                    Release(ref actions);
                    Release(ref trigger);
                    Release(ref triggers);
                    Release(ref settings);
                    Release(ref principal);
                    Release(ref registration);
                    Release(ref definition);
                    Release(ref root);
                    Release(ref service);
                }
            }

            public void DeleteTask()
            {
                object service = null;
                object root = null;
                try
                {
                    Connect(out service, out root);
                    try { Call(root, "DeleteTask", TaskName, 0); }
                    catch (Exception ex) { if (!IsNotFound(ex)) throw; }
                }
                finally
                {
                    Release(ref root);
                    Release(ref service);
                }
            }

            public bool LegacyShortcutExists()
            {
                return File.Exists(LegacyShortcutPath());
            }

            public void CreateLegacyShortcut(string executablePath, string workingDirectory)
            {
                object shell = null;
                object shortcut = null;
                try
                {
                    Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                    if (shellType == null) throw new InvalidOperationException("WScript.Shell 不可用");
                    shell = Activator.CreateInstance(shellType);
                    shortcut = Call(shell, "CreateShortcut", LegacyShortcutPath());
                    Set(shortcut, "TargetPath", executablePath);
                    Set(shortcut, "WorkingDirectory", workingDirectory);
                    Set(shortcut, "IconLocation", executablePath + ",0");
                    Set(shortcut, "Description", "四叶草启动面板（兼容自启）");
                    Call(shortcut, "Save");
                }
                finally
                {
                    Release(ref shortcut);
                    Release(ref shell);
                }
            }

            public void DeleteLegacyShortcut()
            {
                string path = LegacyShortcutPath();
                if (File.Exists(path)) File.Delete(path);
            }

            static void Connect(out object service, out object root)
            {
                service = null;
                root = null;
                Type type = Type.GetTypeFromProgID("Schedule.Service");
                if (type == null) throw new InvalidOperationException("Windows 任务计划程序不可用");
                service = Activator.CreateInstance(type);
                Call(service, "Connect");
                root = Call(service, "GetFolder", "\\");
            }

            static object Call(object target, string member, params object[] args)
            {
                return target.GetType().InvokeMember(member, BindingFlags.InvokeMethod,
                    null, target, args);
            }

            static object Get(object target, string member)
            {
                return target.GetType().InvokeMember(member, BindingFlags.GetProperty,
                    null, target, null);
            }

            static void Set(object target, string member, object value)
            {
                target.GetType().InvokeMember(member, BindingFlags.SetProperty,
                    null, target, new object[] { value });
            }

            static bool IsNotFound(Exception ex)
            {
                Exception cur = ex;
                while (cur != null)
                {
                    // 同一个 HRESULT 在不同 Windows/CLR 组合下可能映射成
                    // COMException、FileNotFoundException，或再包一层
                    // TargetInvocationException。按 HRESULT 判断才稳定。
                    if (cur.HResult == unchecked((int)0x80070002)
                        || cur.HResult == unchecked((int)0x8004130F))
                        return true;
                    cur = cur.InnerException;
                }
                return false;
            }

            static void Release(ref object value)
            {
                object obj = value;
                value = null;
                if (obj == null) return;
                try
                {
                    if (Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj);
                }
                catch { }
            }
        }
    }
}
