using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// 窗口位置记忆：记住设置 / 整理与隐藏 / 外观三个窗口的位置与尺寸。
    ///
    /// ── 存哪里 ──
    ///
    /// `config\window-positions.json`，一个文件存所有窗口：
    ///
    /// <code>
    /// {
    ///   "settings":  { "left": 100, "top": 80, "width": 500, "height": 640, "maximized": false },
    ///   "organize":  { "left": 300, "top": 120, "width": 560, "height": 700, "maximized": false },
    ///   "appearance":{ "left": 420, "top": 100, "width": 420, "height": 620, "maximized": false }
    /// }
    /// </code>
    ///
    /// ── 为什么只记三个窗口 ──
    ///
    /// 确认框 / 输入框 / 批量预览是一次性小弹窗，记忆位置反而怪
    /// （下次出现时可能在屏幕角落）。只有长期使用的设置类窗口值得记。
    ///
    /// ── 关键约束 ──
    ///
    ///   1. **最大化时存 RestoreBounds** —— WPF 的 Left/Top/Width/Height
    ///      在最大化状态下是屏幕尺寸，直接存下来下次还原就是满屏。
    ///      必须用 `RestoreBounds` 取"还原后的框"，另存 `maximized` 标记。
    ///   2. **读回时和 MinWidth/MinHeight 取较大值** —— 防止旧记录把窗口压得过小。
    ///   3. **多显示器越界纠正** —— 用窗口标题栏是否落在**任意一块**显示器工作区判断，
    ///      而不是只对主屏判断。换显示器后跑到屏外就拉回最近的屏幕。
    ///   4. **损坏 / 缺字段 → 整体回退 CenterOwner**，不报错、不弹窗。
    ///   5. **写入失败静默** —— 只读目录、被占用都只记日志。
    ///   6. **保存时先读整个文件，只改自己那一项再写回** —— 三个窗口不互相覆盖。
    ///   7. **DPI**：WPF 的 Left/Top/Width/Height 是设备无关单位，直接存取。
    /// </summary>
    public static class WindowPos
    {
        /// <summary>窗口标识（配置文件里的 key）。</summary>
        public const string Settings = "settings";
        public const string Organize = "organize";
        public const string Appearance = "appearance";

        /// <summary>标题栏最少要有多少像素落在屏幕内，才算"还看得见"。</summary>
        const double MinVisiblePx = 80;

        static string PathOf(string baseDir)
        {
            return System.IO.Path.Combine(baseDir, "config", "window-positions.json");
        }

        // ========== 读取 ==========

        /// <summary>
        /// 读整个文件成字典。文件不存在 / 损坏 → 返回空字典（不抛异常）。
        /// </summary>
        static Dictionary<string, object> ReadAll(string baseDir)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            try
            {
                string path = PathOf(baseDir);
                if (!File.Exists(path)) return result;

                string json = JsonIO.Read(path);
                if (string.IsNullOrEmpty(json)) return result;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return result;
                return d;
            }
            catch (Exception ex)
            {
                try { Log.Warn("读取 window-positions.json 失败，回退默认位置: " + ex.Message); } catch { }
                return new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// 把一个窗口恢复到上次的位置与尺寸。
        ///
        /// 返回 true = 恢复成功（调用方不要再设 CenterOwner）；
        /// 返回 false = 无记录或记录无效（调用方保持 CenterOwner）。
        /// </summary>
        public static bool Restore(string baseDir, string key, Window w)
        {
            try
            {
                if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(key) || w == null) return false;

                Dictionary<string, object> all = ReadAll(baseDir);

                object raw;
                if (!all.TryGetValue(key, out raw) || raw == null) return false;

                Dictionary<string, object> d = raw as Dictionary<string, object>;
                if (d == null) return false;

                // ---- 逐字段读，缺任何一个都整体回退 ----
                double left, top, width, height;
                if (!TryNum(d, "left", out left)) return false;
                if (!TryNum(d, "top", out top)) return false;
                if (!TryNum(d, "width", out width)) return false;
                if (!TryNum(d, "height", out height)) return false;

                // ---- 和最小尺寸取较大值 ----
                double minW = w.MinWidth > 0 ? w.MinWidth : 0;
                double minH = w.MinHeight > 0 ? w.MinHeight : 0;
                if (width < minW) width = minW;
                if (height < minH) height = minH;

                // 非有限值一律拒绝
                if (double.IsNaN(width) || double.IsInfinity(width)) return false;
                if (double.IsNaN(height) || double.IsInfinity(height)) return false;
                if (double.IsNaN(left) || double.IsInfinity(left)) return false;
                if (double.IsNaN(top) || double.IsInfinity(top)) return false;

                // ---- 越界纠正 ----
                ClampToVisible(left, top, width, height, out left, out top);

                // 先设尺寸再设位置（WPF 里顺序不影响，但这样布局更稳）
                w.Width = width;
                w.Height = height;
                w.Left = left;
                w.Top = top;

                // ---- 最大化标记 ----
                bool maximized = false;
                object mv;
                if (d.TryGetValue("maximized", out mv) && mv != null)
                {
                    try { maximized = Convert.ToBoolean(mv); } catch { maximized = false; }
                }
                if (maximized) w.WindowState = WindowState.Maximized;

                return true;
            }
            catch (Exception ex)
            {
                try { Log.Warn("恢复窗口位置失败: " + key + " -> " + ex.Message); } catch { }
                return false;
            }
        }

        // ========== 写入 ==========

        /// <summary>
        /// 保存一个窗口的位置与尺寸。
        ///
        /// **先读入整个文件，只改自己那一项再写回** —— 三个窗口不互相覆盖。
        /// 写入失败静默（只记日志）。
        /// </summary>
        public static void Save(string baseDir, string key, Window w)
        {
            try
            {
                if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(key) || w == null) return;

                // ---- 取"还原后的框" ----
                //
                // 最大化时 Left/Top/Width/Height 是屏幕尺寸，直接存下来
                // 下次还原就是满屏。必须用 RestoreBounds。
                Rect bounds;
                bool maximized = (w.WindowState == WindowState.Maximized);

                if (maximized)
                {
                    bounds = w.RestoreBounds;
                    // RestoreBounds 在从未还原过时可能是 Empty
                    if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                        return;   // 拿不到有效还原框 → 不写，保留旧记录
                }
                else
                {
                    bounds = new Rect(w.Left, w.Top, w.Width, w.Height);
                    if (bounds.Width <= 0 || bounds.Height <= 0) return;
                }

                // 非有限值拒绝
                if (double.IsNaN(bounds.X) || double.IsInfinity(bounds.X)) return;
                if (double.IsNaN(bounds.Y) || double.IsInfinity(bounds.Y)) return;
                if (double.IsNaN(bounds.Width) || double.IsInfinity(bounds.Width)) return;
                if (double.IsNaN(bounds.Height) || double.IsInfinity(bounds.Height)) return;

                // ---- 读全量，改自己那一项 ----
                Dictionary<string, object> all = ReadAll(baseDir);

                Dictionary<string, object> mine = new Dictionary<string, object>();
                mine["left"] = Math.Round(bounds.X, 1);
                mine["top"] = Math.Round(bounds.Y, 1);
                mine["width"] = Math.Round(bounds.Width, 1);
                mine["height"] = Math.Round(bounds.Height, 1);
                mine["maximized"] = maximized;

                all[key] = mine;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                JsonIO.WriteAtomic(PathOf(baseDir), ser.Serialize(all));
            }
            catch (Exception ex)
            {
                // 只读目录 / 被占用 → 静默，只记一条日志
                try { Log.Warn("保存窗口位置失败: " + key + " -> " + ex.Message); } catch { }
            }
        }

        // ========== 越界纠正 ==========

        /// <summary>
        /// 把窗口位置纠正到"标题栏至少有一部分在**任意一块**显示器的工作区内"。
        ///
        /// ── 为什么不能用主屏判断 ──
        ///
        /// 用户可能在副屏上开窗口。只对 `SystemParameters.PrimaryScreenWidth`
        /// 判断的话，副屏上的窗口会被误判成越界、被硬拉回主屏。
        ///
        /// ── 判断标准 ──
        ///
        /// 取窗口顶部 30px 那条带（标题栏大致高度）作为"可抓取区域"，
        /// 只要有 ≥80px 宽落在某块屏幕的工作区内就算可见。
        /// 否则把窗口拉回**离它最近**的那块屏幕的左上角。
        /// </summary>
        public static void ClampToVisible(double left, double top, double width, double height,
            out double outLeft, out double outTop)
        {
            outLeft = left;
            outTop = top;

            try
            {
                Screen[] screens = Screen.AllScreens;
                if (screens == null || screens.Length == 0)
                {
                    // 拿不到显示器信息 → 用 WPF 主屏兜底
                    double sw = SystemParameters.PrimaryScreenWidth;
                    double sh = SystemParameters.PrimaryScreenHeight;
                    if (outLeft < -60) outLeft = 0;
                    if (outTop < 0) outTop = 0;
                    if (outLeft > sw - MinVisiblePx) outLeft = sw - MinVisiblePx;
                    if (outTop > sh - MinVisiblePx) outTop = sh - MinVisiblePx;
                    return;
                }

                // ---- 标题栏可抓取区域 ----
                double barLeft = left;
                double barRight = left + Math.Max(width, MinVisiblePx);
                double barTop = top;
                double barBottom = top + 30;   // 标题栏大致高度

                // ---- 是否落在某块屏幕内 ----
                foreach (Screen s in screens)
                {
                    try
                    {
                        System.Drawing.Rectangle wa = s.WorkingArea;

                        // 水平重叠宽度
                        double ovLeft = Math.Max(barLeft, wa.Left);
                        double ovRight = Math.Min(barRight, wa.Right);
                        double ovW = ovRight - ovLeft;

                        // 垂直是否与工作区有交集
                        bool vHit = barBottom > wa.Top && barTop < wa.Bottom;

                        if (ovW >= MinVisiblePx && vHit) return;   // 可见 → 不动
                    }
                    catch { }
                }

                // ---- 都不可见 → 拉回最近的那块屏幕 ----
                Screen nearest = null;
                double bestDist = double.MaxValue;

                foreach (Screen s in screens)
                {
                    try
                    {
                        System.Drawing.Rectangle wa = s.WorkingArea;
                        double cx = wa.Left + wa.Width / 2.0;
                        double cy = wa.Top + wa.Height / 2.0;
                        double dx = left - cx;
                        double dy = top - cy;
                        double dist = dx * dx + dy * dy;
                        if (dist < bestDist) { bestDist = dist; nearest = s; }
                    }
                    catch { }
                }

                if (nearest == null) nearest = screens[0];

                System.Drawing.Rectangle target = nearest.WorkingArea;

                // 尽量保留相对位置，但保证完全落在工作区内
                double nl = left;
                double nt = top;

                if (nl < target.Left) nl = target.Left + 20;
                if (nt < target.Top) nt = target.Top + 20;
                if (nl + width > target.Right) nl = target.Right - width - 20;
                if (nt + height > target.Bottom) nt = target.Bottom - height - 20;

                // 二次兜底：窗口比屏幕还大时贴左上
                if (nl < target.Left) nl = target.Left;
                if (nt < target.Top) nt = target.Top;

                outLeft = nl;
                outTop = nt;

                try { Log.Info("窗口位置越界，已拉回屏幕: " + target.ToString()); } catch { }
            }
            catch (Exception ex)
            {
                try { Log.Warn("纠正窗口位置失败: " + ex.Message); } catch { }
            }
        }

        // ========== 辅助 ==========

        /// <summary>从字典里读一个数字（支持 int / double / string）。</summary>
        static bool TryNum(Dictionary<string, object> d, string key, out double v)
        {
            v = 0;
            try
            {
                object o;
                if (d == null || !d.TryGetValue(key, out o) || o == null) return false;

                if (o is int) { v = (int)o; return true; }
                if (o is long) { v = (long)o; return true; }
                if (o is double) { v = (double)o; return true; }
                if (o is decimal) { v = (double)(decimal)o; return true; }

                string s = Convert.ToString(o);
                if (string.IsNullOrWhiteSpace(s)) return false;

                double parsed;
                if (!double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed)) return false;

                v = parsed;
                return true;
            }
            catch { return false; }
        }

        // ========== 便捷封装 ==========

        /// <summary>
        /// 给窗口套上"打开时恢复、关闭时保存"。
        ///
        /// 用法：构造完窗口、设好 MinWidth/MinHeight 之后调一次。
        /// 返回 true 表示恢复成功（调用方不要再设 CenterOwner）。
        /// </summary>
        public static bool Attach(string baseDir, string key, Window w)
        {
            bool restored = false;
            try
            {
                restored = Restore(baseDir, key, w);
            }
            catch { restored = false; }

            try
            {
                w.Closing += delegate
                {
                    try { Save(baseDir, key, w); }
                    catch (Exception ex)
                    {
                        try { Log.Warn("关闭时保存窗口位置失败: " + ex.Message); } catch { }
                    }
                };
            }
            catch { }

            return restored;
        }
    }
}
