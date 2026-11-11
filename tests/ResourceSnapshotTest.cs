using System;

namespace Clover
{
    static class ResourceSnapshotTest
    {
        static int passed, failed;
        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
            if (ok) passed++; else failed++;
        }

        static void Main()
        {
            ResourceSnapshot now = ResourceSnapshot.Capture();
            Check(now.WorkingSetBytes > 0, "可读取工作集");
            Check(now.PrivateBytes > 0, "可读取私有内存");
            Check(now.Handles > 0 && now.Threads > 0, "可读取句柄与线程");

            ResourceSnapshot warm = Fake(100, 10, 100, 20, 30);
            ResourceSnapshot stable = Fake(120, 18, 120, 25, 35);
            string reason;
            Check(ResourceSnapshot.IsConverging(warm, stable, 50, out reason),
                "预热后小幅增长判为收敛");
            ResourceSnapshot leaking = Fake(180, 40, 400, 140, 180);
            Check(!ResourceSnapshot.IsConverging(warm, leaking, 50, out reason),
                "线性增长判为不收敛");
            ResourceSnapshot onePerCycle = Fake(120, 18, 150, 70, 80);
            Check(!ResourceSnapshot.IsConverging(warm, onePerCycle, 50, out reason),
                "★每轮增长一个句柄/GDI/USER 不误判为收敛");
            Check(!ResourceSnapshot.IsConverging(null, stable, 50, out reason),
                "缺失快照不误判通过");

            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static ResourceSnapshot Fake(long privateMb, long managedMb, int handles, int gdi, int user)
        {
            return new ResourceSnapshot
            {
                PrivateBytes = privateMb * 1024 * 1024,
                ManagedBytes = managedMb * 1024 * 1024,
                Handles = handles,
                GdiObjects = gdi,
                UserObjects = user,
            };
        }
    }
}
