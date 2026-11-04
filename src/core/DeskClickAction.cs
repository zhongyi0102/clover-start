using System;

namespace Clover
{
    /// <summary>
    /// 「桌面文件」页里鼠标按下该做什么。
    ///
    /// ── 为什么抽成纯逻辑 ──
    ///
    /// `DesktopView.OnCanvasDown` 里有三条互斥路径：双击打开、框选、拖动。
    /// 判定顺序错了会出现很难查的 bug：
    ///
    ///   · 先判拖动再判双击 → 第二次按下被捕获鼠标，抬起时走 OnCanvasUp
    ///     又重算一次选择，双击变成"两次单击"
    ///   · 双击时没排除 Ctrl/Shift → 用户 Ctrl+双击 想加选，结果打开了文件
    ///
    /// 这些规则写成纯函数就能穷举断言，不用真的点鼠标。
    ///
    /// ── 不做什么 ──
    ///
    /// 不碰 WPF 事件、不知道鼠标坐标、不打开任何文件。
    /// </summary>
    public static class DeskClickAction
    {
        /// <summary>鼠标按下后该走的路径。</summary>
        public enum Action
        {
            /// <summary>空白处 → 开始框选。</summary>
            BandSelect = 0,

            /// <summary>图标上双击 → 打开。</summary>
            Open,

            /// <summary>图标上单击 → 选中并准备拖动。</summary>
            Drag,
        }

        /// <summary>
        /// 判定这次按下该做什么。
        /// </summary>
        /// <param name="hitItem">按下的位置有没有命中图标</param>
        /// <param name="clickCount">WPF 的 <c>MouseButtonEventArgs.ClickCount</c></param>
        /// <param name="shift">是否按着 Shift</param>
        /// <param name="ctrl">是否按着 Ctrl</param>
        public static Action Resolve(bool hitItem, int clickCount, bool shift, bool ctrl)
        {
            // 空白处永远是框选，不管点了几下
            if (!hitItem) return Action.BandSelect;

            // 图标上：双击才打开，且**不能带修饰键**。
            //
            // 为什么要排除修饰键：Ctrl+点击 是"切换选中"、Shift+点击 是
            // "范围选中"，这两个是高频操作。如果 Ctrl+双击也打开文件，
            // 用户想加选两个文件时会被意外打开第一个 —— 那是干扰。
            if (clickCount == 2 && !shift && !ctrl) return Action.Open;

            // 其余（单击 / 三击 / 带修饰键的双击）都按拖动准备处理：
            // 选择逻辑由调用方按 shift/ctrl 决定，这里只给方向。
            return Action.Drag;
        }
    }
}
