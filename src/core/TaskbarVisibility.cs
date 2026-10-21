using System;
using System.Windows;

namespace Clover
{
    /// <summary>
    /// 系统状态改动的唯一入口。
    ///
    /// 所有会改变“系统级状态”的操作（隐藏任务栏、把任务栏改成透明可点击）都必须：
    ///   1. 先把原始状态写进 config\system-state.json（落盘）
    ///   2. 再真正改动系统
    ///   3. 还原时反向操作，最后清掉标记文件
    ///
    /// 这样进程被强杀 / 崩溃 / 被安全软件拦截后，下次启动能靠磁盘上的标记把系统救回来，
    /// 而不依赖内存里的变量。
    ///
    /// 所有方法都是幂等的，可以重复调用。
    /// </summary>
    static class TaskbarVisibility
    {
        const int SW_HIDE = 0;
        const int SW_SHOW = 5;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x00080000;

        static string baseDir;
        static SystemState state;

        /// <summary>当前实例记录的“我们改了哪些系统状态”。</summary>
        public static SystemState State
        {
            get { return state; }
        }

        public static void Init(string dir)
        {
            baseDir = dir;
            state = new SystemState();
        }

        static IntPtr Tray()
        {
            try { return Native.FindWindow("Shell_TrayWnd", null); }
            catch { return IntPtr.Zero; }
        }

        /// <summary>系统任务栏当前是否可见。</summary>
        public static bool IsTrayVisible()
        {
            try
            {
                IntPtr h = Tray();
                return h != IntPtr.Zero && Native.IsWindowVisible(h);
            }
            catch { return false; }
        }

        /// <summary>
        /// 隐藏 / 显示系统任务栏。隐藏前会先把原始状态落盘。
        /// </summary>
        public static void Apply(bool hide)
        {
            try
            {
                IntPtr tray = Tray();
                if (tray == IntPtr.Zero) return;

                if (hide)
                {
                    // 先落盘再动手：顺序不能反，否则崩溃窗口期状态丢失
                    if (state != null)
                    {
                        state.TaskbarHidden = true;
                        state.Save();
                    }
                    Native.ShowWindow(tray, SW_HIDE);
                    Log.Info("隐藏系统任务栏（已记录 system-state）");
                }
                else
                {
                    Native.ShowWindow(tray, SW_SHOW);
                    if (state != null && state.TaskbarHidden)
                    {
                        state.TaskbarHidden = false;
                        state.Save(); // Dirty 为 false 时会自动清掉文件
                    }
                    Log.Info("显示系统任务栏");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("切换系统任务栏显示失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 把任务栏临时改成“全透明但仍可点击”（用于在任务栏隐藏时点原生开始菜单）。
        /// 调用方必须在 finally 里调 RestoreTaskbarLayered()。
        /// </summary>
        public static bool MakeTaskbarLayered(out int originalExStyle)
        {
            originalExStyle = 0;
            try
            {
                IntPtr tray = Tray();
                if (tray == IntPtr.Zero) return false;

                originalExStyle = Native.GetWindowLong(tray, GWL_EXSTYLE);

                // 先落盘：记录“我们改过 EX 样式”，崩溃后靠它判断要不要还原
                if (state != null)
                {
                    state.TaskbarLayered = true;
                    state.Save();
                }

                Native.SetWindowLong(tray, GWL_EXSTYLE, originalExStyle | WS_EX_LAYERED);
                Native.SetLayeredWindowAttributes(tray, 0, 1, 2); // LWA_ALPHA, alpha=1/255
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("设置任务栏透明模式失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>恢复任务栏的 EX 样式（撤销 MakeTaskbarLayered）。</summary>
        public static void RestoreTaskbarLayered(int originalExStyle)
        {
            try
            {
                IntPtr tray = Tray();
                if (tray != IntPtr.Zero)
                {
                    Native.SetWindowLong(tray, GWL_EXSTYLE, originalExStyle);
                    // 顺带把 alpha 恢复正常（1 表示完全不透明）
                    Native.SetLayeredWindowAttributes(tray, 0, 255, 2);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("恢复任务栏 EX 样式失败: " + ex.Message);
            }
            finally
            {
                if (state != null && state.TaskbarLayered)
                {
                    state.TaskbarLayered = false;
                    state.Save();
                }
            }
        }

        /// <summary>
        /// 唯一的系统状态还原入口。所有退出/异常路径都调它。
        /// 幂等：重复调用无副作用。
        /// </summary>
        public static void RestoreSystemState(string reason)
        {
            try
            {
                IntPtr tray = Tray();
                if (tray != IntPtr.Zero)
                {
                    // 1) 任务栏 EX 样式：无论如何先恢复成正常（非 layered）
                    int ex = Native.GetWindowLong(tray, GWL_EXSTYLE);
                    if ((ex & WS_EX_LAYERED) != 0)
                    {
                        Native.SetWindowLong(tray, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
                        Log.Info("恢复任务栏 EX 样式 (" + reason + ")");
                    }

                    // 2) 任务栏可见性
                    if (!Native.IsWindowVisible(tray))
                    {
                        Native.ShowWindow(tray, SW_SHOW);
                        Log.Info("恢复系统任务栏可见 (" + reason + ")");
                    }
                }

                // 3) 清掉磁盘标记
                if (state != null)
                {
                    state.TaskbarHidden = false;
                    state.TaskbarLayered = false;
                    state.Clear();
                }
                else if (!string.IsNullOrEmpty(baseDir))
                {
                    SystemState.Clear(baseDir);
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("还原系统状态失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// 启动时清理上次残留：读 system-state.json，有标记就还原。
        /// 返回是否执行过还原（便于写日志）。
        /// </summary>
        public static bool RecoverLeftover(string dir, string reason)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) return false;
                SystemState leftover = SystemState.Load(dir);
                if (leftover == null || !leftover.Dirty) return false;

                Log.Info("发现上次未还原的系统状态（" + leftover.Since + "，来源 " + reason + "），开始还原");
                Log.Warn("检测到上次异常退出遗留的系统状态（" + leftover.Since + "）："
                    + "taskbarHidden=" + leftover.TaskbarHidden + " taskbarLayered=" + leftover.TaskbarLayered
                    + "，已自动还原");

                if (string.IsNullOrEmpty(baseDir)) baseDir = dir;
                if (state == null) state = leftover;

                RestoreSystemState("启动时清理残留");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("启动时清理残留状态失败: " + ex.Message);
                return false;
            }
        }
    }
}
