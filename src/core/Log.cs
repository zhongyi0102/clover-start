using System;

namespace Clover
{
    /// <summary>
    /// 日志抽象层（属于 core 层，零 WPF 依赖）。
    ///
    /// 为什么需要它：纯逻辑层（扫描、配置、看门狗、崩溃保护…）不该依赖 WPF。
    /// 但它们都要写日志，而原来的 `Ui.Log` / `ErrorLog` 都在 UI 层，
    /// 于是产生了"底层反向依赖上层"——这正是分层要消除的东西。
    ///
    /// 做法：core 层只调用这里的委托；由 UI 层在启动最早期注入真实实现
    /// （见 Program.cs 的 Wire 调用）。这样 core 层**零 WPF 依赖**，
    /// 可以单独编译成一个 DLL，由编译器强制保证分层不被破坏。
    ///
    /// 默认实现是空操作：测试或尚未接线时不会崩，也不会刷屏。
    /// </summary>
    static class Log
    {
        /// <summary>普通信息（走 ErrorLog，写 error.log，级别 INFO）。</summary>
        public static Action<string> Info = delegate { };

        /// <summary>警告（写 error.log，级别 WARN）。</summary>
        public static Action<string> Warn = delegate { };

        /// <summary>异常（写 error.log，含完整堆栈）。</summary>
        public static Action<Exception, string> Hook = delegate { };

        /// <summary>
        /// 日志系统初始化（设置日志文件路径、注册异常钩子）。
        /// core 层的独立入口（看门狗模式、一键回滚）需要它，但实现属于 UI 层，
        /// 所以这里只留一个钩子，由 UI 层注入。
        /// </summary>
        public static Action<string> InitHook = delegate { };

        /// <summary>由 core 层调用：初始化日志。</summary>
        public static void Init(string baseDir)
        {
            InitHook(baseDir);
        }

        /// <summary>
        /// 一次性接线。由 UI 层的 Program 在启动最早期调用。
        /// 重复调用安全（覆盖同一批委托）。
        /// </summary>
        public static void Wire(Action<string> info, Action<string> warn,
            Action<Exception, string> hook, Action<string> init)
        {
            Info = info ?? delegate { };
            Warn = warn ?? delegate { };
            Hook = hook ?? delegate { };
            InitHook = init ?? delegate { };
        }
    }
}
