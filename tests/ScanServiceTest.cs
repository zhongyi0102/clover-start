using System;
using System.Threading;

namespace Clover
{
    // ScanService 默认 runner 的编译占位；测试会在启动任何扫描前注入假 runner。
    static class AppScan
    {
        public static bool Run(string baseDir) { return true; }
    }

    static class ScanServiceTest
    {
        static int failed;
        static int passed;

        static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name
                + (cond || detail.Length == 0 ? "" : "  [" + detail + "]"));
            if (cond) passed++; else failed++;
        }

        static void Main()
        {
            TestInvalidRequest();
            TestPendingRerunCoalesces();

            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static void TestInvalidRequest()
        {
            Check(!ScanService.RunAsync(null, "空路径", null), "空路径不启动扫描");
            Check(!ScanService.Busy, "空路径后仍为空闲");
        }

        static void TestPendingRerunCoalesces()
        {
            ManualResetEvent firstStarted = new ManualResetEvent(false);
            ManualResetEvent releaseFirst = new ManualResetEvent(false);
            ManualResetEvent callbacksDone = new ManualResetEvent(false);
            int runs = 0;
            int active = 0;
            int maxActive = 0;
            int callbacks = 0;
            string secondDir = null;

            ScanService.SetRunnerForTest(delegate(string dir)
            {
                int now = Interlocked.Increment(ref active);
                int seen;
                do
                {
                    seen = maxActive;
                    if (now <= seen) break;
                }
                while (Interlocked.CompareExchange(ref maxActive, now, seen) != seen);

                int n = Interlocked.Increment(ref runs);
                if (n == 1)
                {
                    firstStarted.Set();
                    releaseFirst.WaitOne(3000);
                }
                else
                {
                    secondDir = dir;
                }
                Interlocked.Decrement(ref active);
                return true;
            });

            Action<bool> done = delegate(bool ok)
            {
                if (Interlocked.Increment(ref callbacks) == 3) callbacksDone.Set();
            };

            Check(ScanService.RunAsync("A", "首次", done), "首次请求已接受");
            Check(firstStarted.WaitOne(2000), "首次扫描已开始");
            Check(ScanService.Busy, "扫描期间 Busy=true");

            Check(ScanService.RunAsync("B", "开始菜单变化", done), "忙时请求进入 pending");
            Check(ScanService.RunAsync("C", "再次变化", done), "多次忙时请求继续合并");
            releaseFirst.Set();

            Check(callbacksDone.WaitOne(5000), "首次与合并请求的回调全部完成");
            SpinWait.SpinUntil(delegate { return !ScanService.Busy; }, 2000);

            Check(runs == 2, "★三次触发只执行首次 + 一次补跑", runs.ToString());
            Check(maxActive == 1, "★扫描严格串行", maxActive.ToString());
            Check(callbacks == 3, "★每个触发方都收到完成回调", callbacks.ToString());
            Check(secondDir == "C", "补跑采用最新目录状态", secondDir ?? "null");
            Check(!ScanService.Busy, "全部完成后 Busy=false");
            Check(ScanService.LastDone > DateTime.MinValue, "记录最后完成时间");

            firstStarted.Close();
            releaseFirst.Close();
            callbacksDone.Close();
            ScanService.ResetForTest();
        }
    }
}
