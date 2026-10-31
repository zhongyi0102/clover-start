using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;

namespace Clover
{
    /// <summary>
    /// 窗口位置记忆测试：存取往返、最大化还原、最小尺寸、越界纠正、损坏回退、多窗口互不覆盖。
    ///
    /// **全部用临时目录，不碰真实 config。**
    /// </summary>
    static class WindowPosTest
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
            Console.WriteLine("=== 窗口位置记忆测试 ===");
            Console.WriteLine();

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            try
            {
                TestRoundtrip();
                TestMaximized();
                TestMinSize();
                TestOffscreen();
                TestCorrupt();
                TestMultiWindow();
                TestMissingField();
            }
            catch (Exception ex)
            {
                Check(false, "测试不抛异常", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { app.Shutdown(); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static string NewRoot()
        {
            string d = Path.Combine(Path.GetTempPath(),
                "wp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(d, "config"));
            return d;
        }

        static string FileOf(string root)
        {
            return Path.Combine(root, "config", "window-positions.json");
        }

        /// <summary>造一个可测的窗口（不 Show，避免真弹窗）。</summary>
        static Window MakeWindow(double w, double h, double minW, double minH)
        {
            Window win = new Window();
            win.Width = w;
            win.Height = h;
            win.MinWidth = minW;
            win.MinHeight = minH;
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.ShowInTaskbar = false;
            // 不 Show：WPF 里未 Show 的窗口 Left/Top/Width/Height 仍可读写
            return win;
        }

        // ---------- 1. 存取往返 ----------
        static void TestRoundtrip()
        {
            Console.WriteLine("--- 1. 存取往返 ---");

            string root = NewRoot();
            try
            {
                Window a = MakeWindow(500, 640, 420, 400);
                a.Left = 120;
                a.Top = 90;

                WindowPos.Save(root, WindowPos.Settings, a);
                Check(File.Exists(FileOf(root)), "★保存后文件存在");

                string json = File.ReadAllText(FileOf(root), Encoding.UTF8);
                Check(json.IndexOf("settings", StringComparison.Ordinal) >= 0, "★文件含 settings 键");
                Check(json.IndexOf("left", StringComparison.Ordinal) >= 0, "含 left");
                Check(json.IndexOf("maximized", StringComparison.Ordinal) >= 0, "含 maximized");

                // 换一个新窗口读回
                Window b = MakeWindow(300, 300, 420, 400);
                bool ok = WindowPos.Restore(root, WindowPos.Settings, b);

                Check(ok, "★恢复返回 true");
                Check(Math.Abs(b.Left - 120) < 0.5, "★Left 恢复", b.Left.ToString("0.#"));
                Check(Math.Abs(b.Top - 90) < 0.5, "★Top 恢复", b.Top.ToString("0.#"));
                Check(Math.Abs(b.Width - 500) < 0.5, "★Width 恢复", b.Width.ToString("0.#"));
                Check(Math.Abs(b.Height - 640) < 0.5, "★Height 恢复", b.Height.ToString("0.#"));
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 2. 最大化 ----------
        static void TestMaximized()
        {
            Console.WriteLine("--- 2. 最大化关闭后还原尺寸正确 ---");

            string root = NewRoot();
            try
            {
                // 先正常存一次（建立 RestoreBounds 的基准）
                Window a = MakeWindow(500, 640, 420, 400);
                a.Left = 200;
                a.Top = 150;
                WindowPos.Save(root, WindowPos.Settings, a);

                // 模拟：窗口被最大化后关闭
                //
                // 未 Show 的窗口无法真的进最大化状态，这里直接构造记录验证读取路径：
                // 手写一条"最大化 + 还原框 500x640@(200,150)"的记录
                string json = "{"
                    + "\"settings\":{"
                    + "\"left\":200,\"top\":150,\"width\":500,\"height\":640,\"maximized\":true"
                    + "}}";
                File.WriteAllText(FileOf(root), json, new UTF8Encoding(false));

                Window b = MakeWindow(300, 300, 420, 400);
                bool ok = WindowPos.Restore(root, WindowPos.Settings, b);

                Check(ok, "★最大化记录能恢复");
                Check(Math.Abs(b.Width - 500) < 0.5,
                    "★还原尺寸 Width = 500（不是屏幕宽度）", b.Width.ToString("0.#"));
                Check(Math.Abs(b.Height - 640) < 0.5,
                    "★还原尺寸 Height = 640", b.Height.ToString("0.#"));
                Check(b.WindowState == WindowState.Maximized,
                    "★WindowState 恢复为 Maximized", b.WindowState.ToString());

                // 关键：尺寸不能是屏幕尺寸
                Check(b.Width < SystemParameters.PrimaryScreenWidth,
                    "★宽度小于屏幕宽度（没把全屏尺寸存下来）",
                    b.Width + " vs " + SystemParameters.PrimaryScreenWidth);
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 3. 最小尺寸 ----------
        static void TestMinSize()
        {
            Console.WriteLine("--- 3. 旧记录过小 → 取 MinWidth/MinHeight ---");

            string root = NewRoot();
            try
            {
                // 造一条"比最小尺寸还小"的记录
                string json = "{"
                    + "\"settings\":{"
                    + "\"left\":100,\"top\":100,\"width\":100,\"height\":80,\"maximized\":false"
                    + "}}";
                File.WriteAllText(FileOf(root), json, new UTF8Encoding(false));

                Window w = MakeWindow(500, 640, 420, 400);
                bool ok = WindowPos.Restore(root, WindowPos.Settings, w);

                Check(ok, "记录能读");
                Check(w.Width >= 420, "★Width 提升到 MinWidth 420", w.Width.ToString("0.#"));
                Check(w.Height >= 400, "★Height 提升到 MinHeight 400", w.Height.ToString("0.#"));
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 4. 越界纠正 ----------
        static void TestOffscreen()
        {
            Console.WriteLine("--- 4. 坐标在屏幕外 → 打开后完全可见 ---");

            string root = NewRoot();
            try
            {
                // 造一条远在屏幕外的记录
                string json = "{"
                    + "\"settings\":{"
                    + "\"left\":-9999,\"top\":-9999,\"width\":500,\"height\":640,\"maximized\":false"
                    + "}}";
                File.WriteAllText(FileOf(root), json, new UTF8Encoding(false));

                Window w = MakeWindow(500, 640, 420, 400);
                bool ok = WindowPos.Restore(root, WindowPos.Settings, w);

                Check(ok, "记录能读");
                Check(w.Left > -9999, "★Left 被纠正", w.Left.ToString("0.#"));
                Check(w.Top > -9999, "★Top 被纠正", w.Top.ToString("0.#"));

                // 标题栏必须落在某块屏幕内
                bool visible = false;
                try
                {
                    foreach (System.Windows.Forms.Screen s in System.Windows.Forms.Screen.AllScreens)
                    {
                        System.Drawing.Rectangle wa = s.WorkingArea;
                        double barLeft = w.Left;
                        double barRight = w.Left + Math.Max(w.Width, 80);
                        double barTop = w.Top;
                        double barBottom = w.Top + 30;

                        double ovW = Math.Min(barRight, wa.Right) - Math.Max(barLeft, wa.Left);
                        bool vHit = barBottom > wa.Top && barTop < wa.Bottom;

                        if (ovW >= 80 && vHit) { visible = true; break; }
                    }
                }
                catch { visible = w.Left >= 0 && w.Top >= 0; }

                Check(visible, "★纠正后标题栏在屏幕工作区内",
                    "(" + w.Left.ToString("0") + "," + w.Top.ToString("0") + ")");

                // 右边越界也要纠正
                string json2 = "{"
                    + "\"settings\":{"
                    + "\"left\":99999,\"top\":99999,\"width\":500,\"height\":640,\"maximized\":false"
                    + "}}";
                File.WriteAllText(FileOf(root), json2, new UTF8Encoding(false));

                Window w2 = MakeWindow(500, 640, 420, 400);
                WindowPos.Restore(root, WindowPos.Settings, w2);
                Check(w2.Left < 99999 && w2.Top < 99999, "★右下越界也被纠正",
                    "(" + w2.Left.ToString("0") + "," + w2.Top.ToString("0") + ")");
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 5. 损坏文件 ----------
        static void TestCorrupt()
        {
            Console.WriteLine("--- 5. 文件损坏 → 回退（不抛异常）---");

            string root = NewRoot();
            try
            {
                File.WriteAllText(FileOf(root), "{ 这不是合法 JSON ", new UTF8Encoding(false));

                Window w = MakeWindow(500, 640, 420, 400);
                bool threw = false;
                bool ok = false;
                try { ok = WindowPos.Restore(root, WindowPos.Settings, w); }
                catch { threw = true; }

                Check(!threw, "★损坏文件不抛异常");
                Check(!ok, "★损坏文件返回 false（调用方保持 CenterOwner）");

                // 空文件
                File.WriteAllText(FileOf(root), "", new UTF8Encoding(false));
                threw = false;
                ok = false;
                try { ok = WindowPos.Restore(root, WindowPos.Settings, w); }
                catch { threw = true; }
                Check(!threw && !ok, "空文件也不抛、返回 false");

                // 文件不存在
                File.Delete(FileOf(root));
                threw = false;
                ok = false;
                try { ok = WindowPos.Restore(root, WindowPos.Settings, w); }
                catch { threw = true; }
                Check(!threw && !ok, "★文件不存在不抛、返回 false");
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 6. 多窗口互不覆盖 ----------
        static void TestMultiWindow()
        {
            Console.WriteLine("--- 6. 三个窗口互不覆盖 ---");

            string root = NewRoot();
            try
            {
                Window s = MakeWindow(500, 640, 420, 400);
                s.Left = 100; s.Top = 80;
                WindowPos.Save(root, WindowPos.Settings, s);

                Window o = MakeWindow(560, 700, 460, 480);
                o.Left = 300; o.Top = 120;
                WindowPos.Save(root, WindowPos.Organize, o);

                Window ap = MakeWindow(420, 620, 0, 0);
                ap.Left = 420; ap.Top = 100;
                WindowPos.Save(root, WindowPos.Appearance, ap);

                // 三个 key 都在
                string json = File.ReadAllText(FileOf(root), Encoding.UTF8);
                Check(json.IndexOf("settings", StringComparison.Ordinal) >= 0, "★settings 键在");
                Check(json.IndexOf("organize", StringComparison.Ordinal) >= 0, "★organize 键在");
                Check(json.IndexOf("appearance", StringComparison.Ordinal) >= 0, "★appearance 键在");

                // 各自读回正确
                Window s2 = MakeWindow(300, 300, 0, 0);
                WindowPos.Restore(root, WindowPos.Settings, s2);
                Check(Math.Abs(s2.Left - 100) < 0.5, "★settings 位置正确", s2.Left.ToString("0.#"));

                Window o2 = MakeWindow(300, 300, 0, 0);
                WindowPos.Restore(root, WindowPos.Organize, o2);
                Check(Math.Abs(o2.Left - 300) < 0.5, "★organize 位置正确", o2.Left.ToString("0.#"));
                Check(Math.Abs(o2.Width - 560) < 0.5, "★organize 尺寸正确", o2.Width.ToString("0.#"));

                Window ap2 = MakeWindow(300, 300, 0, 0);
                WindowPos.Restore(root, WindowPos.Appearance, ap2);
                Check(Math.Abs(ap2.Left - 420) < 0.5, "★appearance 位置正确", ap2.Left.ToString("0.#"));
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        // ---------- 7. 缺字段 ----------
        static void TestMissingField()
        {
            Console.WriteLine("--- 7. 缺字段 → 整体回退 ---");

            string root = NewRoot();
            try
            {
                // 只有 left/top，缺 width/height
                string json = "{\"settings\":{\"left\":100,\"top\":80}}";
                File.WriteAllText(FileOf(root), json, new UTF8Encoding(false));

                Window w = MakeWindow(500, 640, 420, 400);
                w.Left = 777; w.Top = 777;
                bool ok = WindowPos.Restore(root, WindowPos.Settings, w);

                Check(!ok, "★缺字段返回 false");
                Check(Math.Abs(w.Left - 777) < 0.5,
                    "★缺字段时窗口位置未被改动", w.Left.ToString("0.#"));

                // 未知 key 不报错
                threw = false;
                try { WindowPos.Restore(root, "no-such-window", w); }
                catch { threw = true; }
                Check(!threw, "★未知 key 不抛异常");
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            Console.WriteLine();
        }

        static bool threw = false;
    }
}
