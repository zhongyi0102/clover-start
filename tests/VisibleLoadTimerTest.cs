using System;

namespace Clover
{
    static class VisibleLoadTimerTest
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
            VisibleLoadTimer timer = new VisibleLoadTimer();
            int first = timer.Begin();
            Check(timer.Running, "开始显示周期后正在计时");
            timer.Cancel();
            Check(!timer.Running, "面板收起立即取消计时");

            long elapsed;
            Check(!timer.TryComplete(first, out elapsed), "隐藏前排队的旧回调不能完成计时");

            int second = timer.Begin();
            Check(second != first, "下次显示使用新 generation");
            Check(!timer.TryComplete(first, out elapsed), "旧 generation 不能结束新显示周期");
            Check(timer.TryComplete(second, out elapsed), "当前显示周期可正常完成");
            Check(elapsed >= 0 && !timer.Running, "完成后返回非负耗时并停止");

            timer.Cancel();
            timer.Cancel();
            Check(!timer.Running, "重复取消幂等且不抛异常");

            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
