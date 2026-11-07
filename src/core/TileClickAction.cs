using System;

namespace Clover
{
    /// <summary>
    /// 「常用 / 全部」页里点一个应用磁贴，松开鼠标时该做什么。
    ///
    /// ── 为什么抽成纯逻辑 ──
    ///
    /// 磁贴同时承载三件事：启动应用、拖动排序、右键菜单。松开鼠标这一刻
    /// 有三条互斥出口，判定顺序错了就会很难查：
    ///
    ///   · 先判拖动再判点击 → 用户拖到别处松手，应用被顺带启动了
    ///   · 单击就启动 → 用户想拖动排序，手指一按就开了程序（旧实现就是这样）
    ///   · 三击也启动 → 手抖连点，一次操作开出两个窗口
    ///
    /// 写成纯函数就能把 24 种组合穷举断言，不用真的点鼠标。
    ///
    /// ── 交互约定 ──
    ///
    /// 磁贴是**双击启动**，与桌面图标、资源管理器保持一致：
    /// 单击只负责「选中」（键盘方向键从这里接着走），双击才启动。
    /// 这样拖动排序（按下 → 移动 → 松手）永远不会误启动应用。
    ///
    /// ── 不做什么 ──
    ///
    /// 不碰 WPF 事件、不知道鼠标坐标、不启动任何程序。
    /// </summary>
    public static class TileClickAction
    {
        /// <summary>松开鼠标后该走的路径。</summary>
        public enum Action
        {
            /// <summary>刚拖完 → 提交排序，不启动。</summary>
            Drop = 0,

            /// <summary>双击 → 启动应用。</summary>
            Launch,

            /// <summary>单击 → 只选中，不启动。</summary>
            Select,
        }

        /// <summary>
        /// 判定这次松手该做什么。
        /// </summary>
        /// <param name="dragged">
        /// 这次按下之后指针是否超过了拖动阈值（调用方的 <c>tileDrag</c>）。
        /// </param>
        /// <param name="clickCount">WPF 的 <c>MouseButtonEventArgs.ClickCount</c>。</param>
        public static Action Resolve(bool dragged, int clickCount)
        {
            // 拖动过就是排序，优先于任何点击判定。
            // 顺序不能反：拖完松手时 WPF 也可能报 ClickCount=2（拖得慢，
            // 两次按下落在系统双击间隔内），那时若先判点击就会"拖完顺手启动"。
            if (dragged) return Action.Drop;

            // 只认 ClickCount == 2。三击（3）不启动 —— 与桌面图标一致，
            // 避免手抖连点三次开出两个窗口。
            if (clickCount == 2) return Action.Launch;

            // 单击 / 三击 / 异常值（0、负数）一律只选中。
            return Action.Select;
        }
    }
}
