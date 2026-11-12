using System;
using System.Diagnostics;

namespace Clover
{
    static class LaunchThrottleTest
    {
        static int passed;
        static int failed;

        static void Check(bool condition, string name)
        {
            Console.WriteLine((condition ? "PASS  " : "FAIL  ") + name);
            if (condition) passed++; else failed++;
        }

        static void Main()
        {
            LaunchThrottle throttle = new LaunchThrottle(2000);
            long second = Stopwatch.Frequency;
            long start = 10 * second;

            Check(!throttle.IsBlocked("app-a", start), "首次启动允许");

            // 查询本身不占用冷却期；只有成功发出后才记账。
            Check(!throttle.IsBlocked("app-a", start + 1), "启动失败可立即重试");
            throttle.MarkSent("app-a", start);

            Check(throttle.IsBlocked("app-a", start + second), "同一应用 2 秒内拦截");
            Check(!throttle.IsBlocked("app-b", start + second), "不同应用互不影响");
            Check(!throttle.IsBlocked("app-a", start + 2 * second), "满 2 秒后允许");

            throttle.MarkSent("APP-A", start + 3 * second);
            Check(throttle.IsBlocked("app-a", start + 3 * second + 1), "应用键忽略大小写");
            Check(!throttle.IsBlocked(null, start), "空键不拦截");

            LaunchThrottle disabled = new LaunchThrottle(0);
            disabled.MarkSent("app-a", start);
            Check(!disabled.IsBlocked("app-a", start), "零窗口禁用拦截");

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
