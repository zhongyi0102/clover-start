using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Threading;

namespace Clover
{
    static class ResourceStressTest
    {
        [STAThread]
        static void Main(string[] args)
        {
            int cycles = 100;
            if (args.Length > 0) int.TryParse(args[0], out cycles);
            if (cycles < 20) cycles = 20;
            string report = args.Length > 1 ? args[1] : "resource-stress.json";
            string root = Path.Combine(Path.GetTempPath(), "CloverStress." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Migration.MarkerPath(root), "{}", new UTF8Encoding(false));
            File.WriteAllText(CategoryRules.PathOf(root), CategoryRules.DefaultTableText(), new UTF8Encoding(false));

            List<ResourceSnapshot> samples = new List<ResourceSnapshot>();
            bool pass = false;
            string reason = "未执行";
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                // Ui.ReducedMotion 是按系统设置/环境变量计算的只读属性。
                // 压力测试关闭动画，既缩短 100 轮耗时，也避免仍在运行的动画
                // 把瞬时对象误算成泄漏；不能为了测试给生产属性增加 setter。
                Environment.SetEnvironmentVariable("CLOVER_REDUCE_MOTION", "1");
                Settings settings = Settings.Load(root);
                TriggerWindow trigger = new TriggerWindow(root, settings);
                PanelWindow panel = new PanelWindow(root, trigger, settings);
                panel.Show();
                Pump(100);
                panel.Hide();

                for (int i = 0; i < cycles; i++)
                {
                    panel.ShowPanel();
                    Pump(18);
                    panel.HidePanel();
                    Pump(18);
                    samples.Add(ResourceSnapshot.Capture());
                }

                int warmIndex = Math.Min(samples.Count - 1, Math.Max(9, cycles / 3));
                ResourceSnapshot warm = samples[warmIndex];
                ResourceSnapshot end = samples[samples.Count - 1];
                pass = ResourceSnapshot.IsConverging(warm, end, samples.Count - warmIndex, out reason);
                panel.Close();
                trigger.Close();
            }
            finally
            {
                Dictionary<string, object> rootJson = new Dictionary<string, object>();
                rootJson["generatedAt"] = DateTime.Now.ToString("o");
                rootJson["cycles"] = cycles;
                rootJson["passed"] = pass;
                rootJson["reason"] = reason;
                rootJson["samples"] = samples;
                JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                File.WriteAllText(report, ser.Serialize(rootJson), new UTF8Encoding(false));
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine((pass ? "PASS  " : "FAIL  ") + "资源曲线: " + reason);
            Console.WriteLine("报告: " + report);
            Environment.Exit(pass ? 0 : 1);
        }

        static void Pump(int ms)
        {
            DispatcherFrame frame = new DispatcherFrame();
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
