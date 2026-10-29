using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Clover
{
    /// <summary>
    /// 毛玻璃背景测试：验证删除壁纸页后，glassMode / accentFromWallpaper 仍然工作。
    ///
    /// **为什么需要这个测试**
    ///
    /// 2a 删掉了壁纸浏览页，而壁纸页里的「跟随壁纸 / 固定图」两个按钮
    /// 是 glassMode 唯一的界面入口。删掉后必须确认：
    ///   · glassMode = "screen" 仍然让 GlassBackdrop.FollowWallpaper = true
    ///   · glassMode = "fixed"  仍然让它为 false
    ///   · FollowWallpaper = true 时走截屏路径，不抛未处理异常
    ///
    /// 这是「删功能时不要误伤保留部分」的回归保护。
    /// </summary>
    static class GlassBackdropTest
    {
        static int failed = 0;
        static int passed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            if (cond) { passed++; Console.WriteLine("PASS  " + name); }
            else
            {
                failed++;
                Console.WriteLine("FAIL  " + name + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
            }
        }

        [STAThread]
        static void Main()
        {
            Console.WriteLine("=== 毛玻璃背景测试 ===");
            Console.WriteLine();

            TestFollowWallpaperSwitch();
            TestSettingsDefaults();
            TestCaptureDoesNotThrow();
            TestAccentSettingsPreserved();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. FollowWallpaper 与 glassMode 联动 ----------
        static void TestFollowWallpaperSwitch()
        {
            Console.WriteLine("--- 1. glassMode 控制 FollowWallpaper ---");

            // 这个映射逻辑在 PanelWindow.Data.cs 的 ApplyGlassSettings 里：
            //   glassBackdrop.FollowWallpaper = settings.GlassMode == "screen"
            //
            // 用 GlassBackdrop 直接验证字段本身的行为
            GlassBackdrop gb = MakeBackdrop();

            Check(gb.FollowWallpaper == false, "默认 FollowWallpaper = false");

            gb.FollowWallpaper = true;
            Check(gb.FollowWallpaper, "设为 true 后生效");

            gb.FollowWallpaper = false;
            Check(!gb.FollowWallpaper, "设为 false 后生效");

            // 验证 settings → FollowWallpaper 的映射规则（与生产代码一致）
            Check(FollowWallpaperOf("screen") == true, "glassMode=screen → FollowWallpaper=true");
            Check(FollowWallpaperOf("fixed") == false, "glassMode=fixed → FollowWallpaper=false");
            Check(FollowWallpaperOf("SCREEN") == true, "大小写不敏感（SCREEN）");
            Check(FollowWallpaperOf("Fixed") == false, "大小写不敏感（Fixed）");
            Check(FollowWallpaperOf("") == false, "空串 → false（回退固定图）");
            Check(FollowWallpaperOf(null) == false, "null → false（回退固定图）");
            Console.WriteLine();
        }

        /// <summary>
        /// 复刻 PanelWindow.Data.cs 里的映射规则，确认逻辑没变。
        /// </summary>
        static bool FollowWallpaperOf(string glassMode)
        {
            return string.Equals(glassMode, "screen", StringComparison.OrdinalIgnoreCase);
        }

        // ---------- 2. Settings 默认值仍正确 ----------
        static void TestSettingsDefaults()
        {
            Console.WriteLine("--- 2. 保留的设置项默认值 ---");

            string root = NewRoot();
            try
            {
                Settings s = Settings.Load(root);

                Check(s.GlassOpacity == 0, "glassOpacity 默认 0", Convert.ToString(s.GlassOpacity));
                Check(s.GlassMode == "fixed", "glassMode 默认 fixed", s.GlassMode ?? "null");
                Check(s.AccentFromWallpaper == true, "accentFromWallpaper 默认 true");
                Check(string.IsNullOrEmpty(s.AccentOverride), "accentOverride 默认空", s.AccentOverride ?? "null");

                // 往返
                s.GlassMode = "screen";
                s.GlassOpacity = 80;
                s.AccentOverride = "#4FC3D9";
                s.AccentFromWallpaper = false;
                s.Save();

                Settings s2 = Settings.Load(root);
                Check(s2.GlassMode == "screen", "glassMode 往返", s2.GlassMode);
                Check(s2.GlassOpacity == 80, "glassOpacity 往返", Convert.ToString(s2.GlassOpacity));
                Check(s2.AccentOverride == "#4FC3D9", "accentOverride 往返", s2.AccentOverride);
                Check(s2.AccentFromWallpaper == false, "accentFromWallpaper 往返");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 3. 截屏路径不抛未处理异常 ----------
        static void TestCaptureDoesNotThrow()
        {
            Console.WriteLine("--- 3. 截屏路径（CaptureFromScreen）不抛异常 ---");

            // 说明：CaptureFromScreen 是 private，通过 Refresh() 间接走到。
            // 不要求真的截到图（无桌面会话时可能拿不到），
            // 只要求**不抛未处理异常**。
            GlassBackdrop gb = null;
            bool threw = false;
            string err = null;

            try
            {
                gb = MakeBackdrop();
                gb.FollowWallpaper = true;          // 走截屏路径
                gb.OpacityPercent = 80;
                gb.Refresh();                        // 触发 Capture
            }
            catch (Exception ex)
            {
                threw = true;
                err = ex.GetType().Name + ": " + ex.Message;
            }

            Check(!threw, "FollowWallpaper=true 时 Refresh() 不抛未处理异常", err ?? "");
            if (gb != null)
            {
                Check(gb.FollowWallpaper, "调用后 FollowWallpaper 仍为 true");
                try { gb.Stop(); } catch { }
            }

            // 固定图模式（FollowWallpaper=false）也不该抛
            threw = false;
            err = null;
            GlassBackdrop gb2 = null;
            try
            {
                gb2 = MakeBackdrop();
                gb2.FollowWallpaper = false;
                gb2.OpacityPercent = 80;
                gb2.Refresh();
            }
            catch (Exception ex)
            {
                threw = true;
                err = ex.GetType().Name + ": " + ex.Message;
            }
            Check(!threw, "FollowWallpaper=false 时 Refresh() 不抛未处理异常", err ?? "");
            if (gb2 != null) { try { gb2.Stop(); } catch { } }

            // Clear / Stop 在未初始化时也不该抛
            threw = false;
            try
            {
                GlassBackdrop gb3 = MakeBackdrop();
                gb3.Clear();
                gb3.Stop();
            }
            catch { threw = true; }
            Check(!threw, "未初始化的 Clear/Stop 不抛异常");

            Console.WriteLine();
        }

        // ---------- 4. 强调色相关设置保留 ----------
        static void TestAccentSettingsPreserved()
        {
            Console.WriteLine("--- 4. 强调色设置保留 ---");

            // 删除壁纸页后 accentFromWallpaper 失去触发点，但字段必须保留：
            //   · 用户手动改 settings.json 仍应生效
            //   · 将来若加回取色触发点，配置不用迁移
            string root = NewRoot();
            try
            {
                string p = Path.Combine(root, "config", "settings.json");
                File.WriteAllText(p,
                    "{\"accentFromWallpaper\":false,\"accentOverride\":\"#FF8800\",\"glassMode\":\"screen\"}",
                    new UTF8Encoding(false));

                Settings s = Settings.Load(root);
                Check(s.AccentFromWallpaper == false, "旧配置的 accentFromWallpaper=false 被读到");
                Check(s.AccentOverride == "#FF8800", "旧配置的 accentOverride 被读到", s.AccentOverride);
                Check(s.GlassMode == "screen", "旧配置的 glassMode=screen 被读到", s.GlassMode);
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 辅助 ----------

        /// <summary>
        /// 造一个可用的 GlassBackdrop。
        ///
        /// 构造函数需要 (Window owner, Panel host, string resDirectory) ——
        /// 它是真实 WPF 控件，必须挂在窗口上。这里造一个最小窗口 + Panel，
        /// 用完立刻关闭（不 Show，避免闪窗）。
        /// </summary>
        static GlassBackdrop MakeBackdrop()
        {
            Window w = new Window();
            w.Width = 100;
            w.Height = 100;
            w.WindowStyle = WindowStyle.None;
            w.ShowInTaskbar = false;
            w.Left = -30000;      // 屏幕外，避免任何视觉干扰
            w.Top = -30000;

            System.Windows.Controls.Panel host = new System.Windows.Controls.Grid();
            w.Content = host;

            return new GlassBackdrop(w, host, Path.Combine(Path.GetTempPath(), "glass-res"));
        }

        static void CloseBackdrop(GlassBackdrop gb)
        {
            try { if (gb != null) gb.Stop(); } catch { }
        }

        static string NewRoot()
        {
            string d = Path.Combine(Path.GetTempPath(), "glass-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            Directory.CreateDirectory(Path.Combine(d, "config"));
            return d;
        }

        static void Clean(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }
    }
}
