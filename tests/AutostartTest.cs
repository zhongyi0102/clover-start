using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>快速登录计划任务、自启迁移与兼容回退的纯逻辑测试。</summary>
    static class AutostartTest
    {
        static int failed;

        static void Check(bool ok, string name, string detail = "")
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name
                + (ok || detail.Length == 0 ? "" : "  [" + detail + "]"));
            if (!ok) failed++;
        }

        sealed class FakePlatform : Autostart.IPlatform
        {
            public Autostart.TaskInfo Task = new Autostart.TaskInfo();
            public bool Legacy;
            public bool RegisterFails;
            public bool DeleteTaskFails;
            public bool DeleteLegacyFails;
            public bool CreateLegacyFails;
            public Autostart.TaskPlan LastPlan;
            public readonly List<string> Events = new List<string>();

            public Autostart.TaskInfo ReadTask()
            {
                Events.Add("read-task");
                return Task;
            }

            public void RegisterTask(Autostart.TaskPlan plan)
            {
                Events.Add("register-task");
                LastPlan = plan;
                if (RegisterFails) throw new InvalidOperationException("task blocked");
                Task = new Autostart.TaskInfo
                {
                    Exists = true,
                    Enabled = true,
                    HasLogonTrigger = true,
                    HasExecAction = true,
                    ExecutablePath = plan.ExecutablePath,
                    WorkingDirectory = plan.WorkingDirectory
                };
            }

            public void DeleteTask()
            {
                Events.Add("delete-task");
                if (DeleteTaskFails) throw new IOException("task locked");
                Task = new Autostart.TaskInfo();
            }

            public bool LegacyShortcutExists()
            {
                Events.Add("read-legacy");
                return Legacy;
            }

            public void CreateLegacyShortcut(string executablePath, string workingDirectory)
            {
                Events.Add("create-legacy");
                if (CreateLegacyFails) throw new IOException("shortcut blocked");
                Legacy = true;
            }

            public void DeleteLegacyShortcut()
            {
                Events.Add("delete-legacy");
                if (DeleteLegacyFails) throw new IOException("shortcut locked");
                Legacy = false;
            }
        }

        static int IndexOf(List<string> values, string value)
        {
            for (int i = 0; i < values.Count; i++)
                if (values[i] == value) return i;
            return -1;
        }

        static void Main()
        {
            string dir = Path.Combine(Path.GetTempPath(), "clover-autostart-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, "CloverStart.exe");
            File.WriteAllText(exe, "test");

            try
            {
                // 1. 空状态
                FakePlatform empty = new FakePlatform();
                Autostart.State s0 = Autostart.Inspect(exe, empty);
                Check(!s0.Enabled, "默认未启用");
                Check(s0.Mode == "未启用", "默认状态文案明确");

                // 2. 从旧快捷方式迁移为计划任务
                FakePlatform migrate = new FakePlatform();
                migrate.Legacy = true;
                Autostart.Result m = Autostart.MigrateLegacy(exe, migrate);
                Check(m.Success && m.UsingScheduledTask, "旧快捷方式迁移成功");
                Check(!migrate.Legacy, "迁移成功后旧快捷方式已删除");
                Check(migrate.Task.Exists, "迁移后登录计划任务存在");
                Check(IndexOf(migrate.Events, "register-task") >= 0
                    && IndexOf(migrate.Events, "delete-legacy") > IndexOf(migrate.Events, "register-task"),
                    "★先注册新任务，再删除旧快捷方式");

                Autostart.TaskPlan plan = migrate.LastPlan;
                Check(plan != null && plan.LogonTrigger, "触发器是用户登录");
                Check(plan != null && plan.DelaySeconds == 0, "登录触发零延迟");
                Check(plan != null && !plan.RunElevated, "使用普通权限，不弹 UAC");
                Check(plan != null && !plan.DisallowStartIfOnBatteries
                    && !plan.StopIfGoingOnBatteries, "电池供电不阻止启动或运行");
                Check(plan != null && plan.UnlimitedExecutionTime, "不设置 72 小时运行上限");
                Check(plan != null && plan.IgnoreNewInstance, "重复触发时忽略新实例");
                Check(plan != null && plan.Priority == 4, "登录负载下使用正常任务优先级");
                Check(plan != null && string.Equals(plan.ExecutablePath, exe,
                    StringComparison.OrdinalIgnoreCase), "任务指向当前程序");
                Check(plan != null && string.Equals(plan.WorkingDirectory, dir,
                    StringComparison.OrdinalIgnoreCase), "任务工作目录正确");

                string xml = "<?xml version=\"1.0\"?><Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
                    + "<Principals><Principal><LogonType>InteractiveToken</LogonType></Principal></Principals>"
                    + "<Settings><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"
                    + "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable>"
                    + "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>"
                    + "<Priority>4</Priority></Settings><Triggers><LogonTrigger /></Triggers>"
                    + "<Actions><Exec><Command>" + exe + "</Command><WorkingDirectory>" + dir
                    + "</WorkingDirectory></Exec></Actions></Task>";
                Autostart.TaskInfo parsed = Autostart.ParseTaskXml(xml);
                Check(parsed.HasLogonTrigger && parsed.HasExecAction, "真实任务 XML 能识别登录触发与执行动作");
                Check(parsed.PolicyValid, "真实任务 XML 的快速策略校验通过");
                Check(string.Equals(parsed.ExecutablePath, exe, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(parsed.WorkingDirectory, dir, StringComparison.OrdinalIgnoreCase),
                    "真实任务 XML 正确读取命令与工作目录");

                Autostart.TaskInfo delayed = Autostart.ParseTaskXml(
                    xml.Replace("<LogonTrigger />", "<LogonTrigger><Delay>PT30S</Delay></LogonTrigger>"));
                Check(!delayed.PolicyValid, "带 30 秒延迟的旧任务会被判定需要修复");

                // 3. 计划任务失败时必须保住旧方式
                FakePlatform preserve = new FakePlatform();
                preserve.Legacy = true;
                preserve.RegisterFails = true;
                Autostart.Result p = Autostart.MigrateLegacy(exe, preserve);
                Check(p.Success, "迁移失败不破坏开机自启");
                Check(preserve.Legacy, "★新任务失败时旧快捷方式原样保留");
                Check(IndexOf(preserve.Events, "delete-legacy") < 0,
                    "失败路径绝不删除旧快捷方式");

                FakePlatform legacyLocked = new FakePlatform();
                legacyLocked.Legacy = true;
                legacyLocked.DeleteLegacyFails = true;
                Autostart.Result lockedMigration = Autostart.MigrateLegacy(exe, legacyLocked);
                Check(lockedMigration.Success && legacyLocked.Legacy,
                    "旧快捷方式暂时删不掉时仍保持自启");
                Check(legacyLocked.Task.Exists,
                    "旧快捷方式删除失败不回滚已验证的新任务");

                // 4. 首次启用时任务计划不可用，自动创建兼容快捷方式
                FakePlatform fallback = new FakePlatform();
                fallback.RegisterFails = true;
                Autostart.Result f = Autostart.Enable(exe, fallback);
                Check(f.Success && f.UsingLegacyFallback, "计划任务不可用时启用兼容模式");
                Check(fallback.Legacy, "兼容快捷方式已创建");

                // 5. 两种方式都失败时如实报告失败
                FakePlatform blocked = new FakePlatform();
                blocked.RegisterFails = true;
                blocked.CreateLegacyFails = true;
                Autostart.Result b = Autostart.Enable(exe, blocked);
                Check(!b.Success, "两种自启方式都失败时返回失败");
                Check(!blocked.Legacy, "失败后不伪造启用状态");

                // 6. 错路径/禁用任务不能冒充当前版本已启用；再次启用会修复
                FakePlatform stale = new FakePlatform();
                stale.Task = new Autostart.TaskInfo
                {
                    Exists = true, Enabled = true, HasLogonTrigger = true, HasExecAction = true,
                    ExecutablePath = Path.Combine(dir, "old.exe")
                };
                Check(!Autostart.Inspect(exe, stale).ScheduledTaskValid,
                    "旧路径任务不算当前版本已启用");
                Autostart.Result repaired = Autostart.Enable(exe, stale);
                Check(repaired.Success && repaired.UsingScheduledTask, "启用操作会修复旧路径任务");
                Check(string.Equals(stale.Task.ExecutablePath, exe, StringComparison.OrdinalIgnoreCase),
                    "修复后指向当前程序");

                stale.Task.WorkingDirectory = Path.Combine(dir, "wrong");
                Check(!Autostart.Inspect(exe, stale).ScheduledTaskValid,
                    "错误工作目录不会被误判为有效任务");
                Check(Autostart.Enable(exe, stale).Success
                    && string.Equals(stale.Task.WorkingDirectory, dir, StringComparison.OrdinalIgnoreCase),
                    "启用操作会修复工作目录");

                stale.Task.PolicyValid = false;
                Check(!Autostart.Inspect(exe, stale).ScheduledTaskValid,
                    "旧任务策略不符合快速模式时会重建");
                Check(Autostart.Enable(exe, stale).Success && stale.Task.PolicyValid,
                    "启用操作会修复任务策略");

                stale.Task.Arguments = "--reset-all";
                Check(!Autostart.Inspect(exe, stale).ScheduledTaskValid,
                    "带维护参数的任务不冒充正常自启");
                Check(Autostart.Enable(exe, stale).Success
                    && string.IsNullOrEmpty(stale.Task.Arguments), "启用操作会清除异常启动参数");

                stale.Task.Enabled = false;
                Check(!Autostart.Inspect(exe, stale).Enabled, "禁用的计划任务不算已启用");

                // 7. 关闭时同时清理新旧两种方式
                FakePlatform both = new FakePlatform();
                both.Legacy = true;
                both.Task = new Autostart.TaskInfo
                {
                    Exists = true, Enabled = true, HasLogonTrigger = true, HasExecAction = true,
                    ExecutablePath = exe
                };
                Autostart.Result off = Autostart.Disable(both);
                Check(off.Success && off.Changed, "关闭开机自启成功");
                Check(!both.Task.Exists && !both.Legacy, "关闭时新旧入口都被清理");

                // 8. 删除失败不能谎报成功
                FakePlatform locked = new FakePlatform();
                locked.Task.Exists = true;
                locked.DeleteTaskFails = true;
                Autostart.Result lockResult = Autostart.Disable(locked);
                Check(!lockResult.Success, "计划任务删除失败会报告失败");
                Check(locked.Task.Exists, "删除失败的任务仍可被复核出来");

                // 9. 已是快速任务时幂等，不重复注册
                FakePlatform stable = new FakePlatform();
                stable.Task = new Autostart.TaskInfo
                {
                    Exists = true, Enabled = true, HasLogonTrigger = true, HasExecAction = true,
                    ExecutablePath = exe, WorkingDirectory = dir
                };
                Autostart.Result same = Autostart.Enable(exe, stable);
                Check(same.Success && !same.Changed, "重复启用保持幂等");
                Check(IndexOf(stable.Events, "register-task") < 0, "有效任务不重复注册");
                Check(Autostart.Inspect(exe, stable).Mode == "登录计划任务（快速）",
                    "快速模式状态文案准确");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }

            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
