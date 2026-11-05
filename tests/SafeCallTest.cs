using System;
using System.Threading;

namespace Clover
{
    static class SafeCallTest
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
            TestBackground();
            TestDeadlineSuccess();
            TestDeadlineDropsLateResult();

            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static void TestBackground()
        {
            ManualResetEvent done = new ManualResetEvent(false);
            bool ok = false;
            int value = 0;
            SafeCall.RunInBackground<int>(delegate { return 42; }, delegate(bool success, int result)
            {
                ok = success;
                value = result;
                done.Set();
            });
            Check(done.WaitOne(2000), "纯后台调用完成");
            Check(ok && value == 42, "纯后台调用返回真实结果", value.ToString());
            done.Close();
        }

        static void TestDeadlineSuccess()
        {
            ManualResetEvent done = new ManualResetEvent(false);
            bool ok = false;
            string value = null;
            SafeCall.RunWithDeadline<string>(delegate { return "ok"; }, 1000,
                delegate(bool success, string result)
                {
                    ok = success;
                    value = result;
                    done.Set();
                });
            Check(done.WaitOne(2000), "截止时间内调用完成");
            Check(ok && value == "ok", "截止时间内采用结果", value ?? "null");
            done.Close();
        }

        static void TestDeadlineDropsLateResult()
        {
            ManualResetEvent done = new ManualResetEvent(false);
            ManualResetEvent workerReturned = new ManualResetEvent(false);
            int callbacks = 0;
            bool callbackOk = true;
            int callbackValue = -1;

            SafeCall.RunWithDeadline<int>(delegate
            {
                Thread.Sleep(180);
                workerReturned.Set();
                return 99;
            }, 40, delegate(bool success, int result)
            {
                callbackOk = success;
                callbackValue = result;
                Interlocked.Increment(ref callbacks);
                done.Set();
            });

            Check(done.WaitOne(1000), "超时会及时回调");
            Check(!callbackOk && callbackValue == 0, "★超时回调 false/默认值",
                callbackOk + "/" + callbackValue);
            Check(workerReturned.WaitOne(1000), "底层阻塞调用允许自然结束");
            Thread.Sleep(80); // 给迟到路径足够时间，确认不会二次回调
            Check(callbacks == 1, "★迟到结果被丢弃且只回调一次", callbacks.ToString());

            done.Close();
            workerReturned.Close();
        }
    }
}
