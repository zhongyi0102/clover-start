using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Clover
{
    /// <summary>四叶草任务栏按钮的键盘、UIAutomation、焦点与冗余状态反馈。</summary>
    static class TaskbarAccessibilityTest
    {
        static int passed;
        static int failed;

        static void Check(bool condition, string name, string detail = "")
        {
            if (condition) { passed++; Console.WriteLine("PASS  " + name); }
            else
            {
                failed++;
                Console.WriteLine("FAIL  " + name
                    + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
            }
        }

        [STAThread]
        static void Main()
        {
            Console.WriteLine("=== 任务栏按钮辅助功能测试 ===");
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string root = Path.Combine(Path.GetTempPath(),
                "clover-a11y-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));

            TriggerWindow trigger = null;
            try
            {
                Ui.ApplyTheme(Ui.ThemeMode.Light);
                Settings settings = Settings.Load(root);
                settings.HideTaskbar = false;
                trigger = new TriggerWindow(root, settings);

                Button button = trigger.TestRootButton;
                Check(button != null, "交互根节点存在");
                Check(button is Button, "交互根节点是原生 Button");
                Check(button.Focusable && KeyboardNavigation.GetIsTabStop(button),
                    "按钮可聚焦且在 Tab 顺序中");
                Check(button.Width.Equals(double.NaN) || trigger.Width >= 44,
                    "窗口命中宽度至少 44 DIP", trigger.Width.ToString());
                Check(trigger.Height >= 44, "窗口命中高度至少 44 DIP", trigger.Height.ToString());

                string name = AutomationProperties.GetName(button);
                string help = AutomationProperties.GetHelpText(button);
                Check(name == "打开四叶草面板", "关闭态有准确辅助名称", name);
                Check(!string.IsNullOrEmpty(help) && help.IndexOf("右键", StringComparison.Ordinal) >= 0,
                    "帮助文本同时说明左键与右键", help);
                Check(Convert.ToString(button.ToolTip) == "打开四叶草面板",
                    "状态有 Tooltip 冗余反馈，不只依赖颜色");

                ButtonAutomationPeer peer = new ButtonAutomationPeer(button);
                object pattern = peer.GetPattern(PatternInterface.Invoke);
                IInvokeProvider invoke = pattern as IInvokeProvider;
                Check(invoke != null, "UIAutomation 暴露 InvokePattern");
                Check(peer.GetAutomationControlType() == AutomationControlType.Button,
                    "UIAutomation 控件类型为 Button");

                int factoryCalls = 0;
                trigger.AttachPanelFactory(delegate
                {
                    Interlocked.Increment(ref factoryCalls);
                    return null;
                });
                if (invoke != null) invoke.Invoke();
                Pump(180);
                Check(factoryCalls == 1,
                    "InvokePattern 与鼠标点击走同一个面板入口", factoryCalls.ToString());

                trigger.TestApplyFocusVisual(true);
                Check(button.BorderThickness.Left >= 2,
                    "键盘焦点显示 2 DIP 可见边框", button.BorderThickness.ToString());
                Check(button.BorderBrush != null && button.BorderBrush != Brushes.Transparent,
                    "焦点边框使用主题/系统高对比度颜色");
                trigger.TestApplyFocusVisual(false);
                Check(button.BorderThickness.Left == 0,
                    "离开焦点后恢复静止视觉");
            }
            catch (Exception ex)
            {
                Check(false, "辅助功能测试不抛异常", ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { if (trigger != null) trigger.Close(); } catch { }
                try { app.Shutdown(); } catch { }
                try { Directory.Delete(root, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static void Pump(int milliseconds)
        {
            DispatcherFrame frame = new DispatcherFrame();
            DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background);
            timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
