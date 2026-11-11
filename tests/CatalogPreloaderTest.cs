using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Clover
{
    static class CatalogPreloaderTest
    {
        static int passed;
        static int failed;

        static void Check(bool value, string name)
        {
            Console.WriteLine((value ? "PASS  " : "FAIL  ") + name);
            if (value) passed++; else failed++;
        }

        static void Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "CloverCatalogWarm." + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "config"));
                File.WriteAllText(Path.Combine(root, "config", "settings.json"), "{}", new UTF8Encoding(false));
                File.WriteAllText(Migration.MarkerPath(root), "{}", new UTF8Encoding(false));
                File.WriteAllText(CategoryRules.PathOf(root), CategoryRules.DefaultTableText(), new UTF8Encoding(false));
                WriteApps(root, "甲应用");

                CatalogPreloader.Invalidate();
                CatalogPreloader.WarmAsync(root);
                Check(SpinWait.SpinUntil(delegate { return !CatalogPreloader.IsWarming; }, 5000),
                    "后台预热在截止时间内完成");
                Catalog first = CatalogPreloader.TryTake(root);
                Check(first != null && first.ContainsName("甲应用"), "稳定输入命中预热快照");
                Check(CatalogPreloader.TryTake(root) == null, "快照只领取一次，不共享可变对象");

                CatalogPreloader.WarmAsync(root);
                SpinWait.SpinUntil(delegate { return !CatalogPreloader.IsWarming; }, 5000);
                Thread.Sleep(20);
                WriteApps(root, "乙应用");
                Check(CatalogPreloader.TryTake(root) == null, "输入文件变化自动使旧快照失效");

                Catalog fallback = CatalogPreloader.TakeOrLoad(root);
                Check(fallback != null && fallback.ContainsName("乙应用"), "失效后安全回退同步加载最新数据");

                ManualResetEvent firstStarted = new ManualResetEvent(false);
                ManualResetEvent secondStarted = new ManualResetEvent(false);
                ManualResetEvent releaseFirst = new ManualResetEvent(false);
                ManualResetEvent releaseSecond = new ManualResetEvent(false);
                int workers = 0;
                try
                {
                    CatalogPreloader.Invalidate();
                    CatalogPreloader.SetBeforeLoadForTest(delegate
                    {
                        int n = Interlocked.Increment(ref workers);
                        if (n == 1)
                        {
                            firstStarted.Set();
                            releaseFirst.WaitOne(5000);
                        }
                        else if (n == 2)
                        {
                            secondStarted.Set();
                            releaseSecond.WaitOne(5000);
                        }
                    });

                    CatalogPreloader.WarmAsync(root);
                    Check(firstStarted.WaitOne(2000), "旧一代 Catalog 预热已开始");
                    CatalogPreloader.Invalidate();
                    CatalogPreloader.WarmAsync(root);
                    Check(secondStarted.WaitOne(2000), "失效后新一代 Catalog 预热已开始");

                    releaseFirst.Set();
                    Thread.Sleep(80);
                    Check(CatalogPreloader.IsWarming,
                        "★旧 worker 完成不能清掉新 worker 的忙碌状态");

                    releaseSecond.Set();
                    Check(SpinWait.SpinUntil(delegate { return !CatalogPreloader.IsWarming; }, 5000),
                        "新一代 Catalog 预热能正常收尾");
                    Catalog raced = CatalogPreloader.TryTake(root);
                    Check(raced != null && raced.ContainsName("乙应用"),
                        "★最终只领取新一代 Catalog 快照");
                }
                finally
                {
                    releaseFirst.Set();
                    releaseSecond.Set();
                    SpinWait.SpinUntil(delegate { return !CatalogPreloader.IsWarming; }, 1000);
                    CatalogPreloader.SetBeforeLoadForTest(null);
                    firstStarted.Close();
                    secondStarted.Close();
                    releaseFirst.Close();
                    releaseSecond.Close();
                }
            }
            finally
            {
                CatalogPreloader.Invalidate();
                try { Directory.Delete(root, true); } catch { }
            }

            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static void WriteApps(string root, string name)
        {
            string target = Path.Combine(root, name + ".exe");
            File.WriteAllText(target, "x", new UTF8Encoding(false));
            Dictionary<string, object> app = new Dictionary<string, object>();
            app["name"] = name;
            app["path"] = target;
            app["category"] = "其他";
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["order"] = Catalog.DefaultOrder;
            data["apps"] = new object[] { app };
            JavaScriptSerializer ser = new JavaScriptSerializer();
            File.WriteAllText(Path.Combine(root, "config", "apps.json"), ser.Serialize(data), new UTF8Encoding(false));
        }
    }
}
