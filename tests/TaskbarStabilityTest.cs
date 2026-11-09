using System;
using System.Windows;

namespace Clover
{
    /// <summary>
    /// 任务栏稳定性压测：不操作真实 Explorer，只压生产状态机与生产几何函数。
    /// </summary>
    static class TaskbarStabilityTest
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
            Console.WriteLine("=== 任务栏稳定性与混合 DPI 压测 ===");
            TestEventStormSingleFlight();
            TestGenerationRejectsStaleResult();
            TestResetInvalidatesInFlightGeneration();
            TestRetryBound();
            TestRapidFailureThenSuccess();
            TestBottomNegativeMonitor150();
            TestTopTaskbar125();
            TestLeftAndRightTaskbars();
            TestFullEdgeInference();
            TestAutoHideWorkArea();
            TestCalibrationAndSourceLabels();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static void TestEventStormSingleFlight()
        {
            TaskbarProbeCoordinator c = new TaskbarProbeCoordinator(2);
            c.NotifyChange();
            long first;
            Check(c.TryStart(out first), "事件风暴前能启动首个探测");

            for (int i = 0; i < 500; i++) c.NotifyChange();
            long shouldNotStart;
            Check(!c.TryStart(out shouldNotStart), "500 次密集事件期间仍只有一个探测在跑");

            TaskbarProbeCoordinator.Completion stale = c.Complete(first, true);
            Check(stale.Superseded && stale.Rerun && !stale.Apply,
                "事件风暴使旧结果过期且只请求一次补跑");

            long latest;
            Check(c.TryStart(out latest), "合并后只启动一次最新代探测");
            Check(latest == c.LatestGeneration && latest > first,
                "补跑使用最新 generation", latest + " / " + c.LatestGeneration);
            TaskbarProbeCoordinator.Completion done = c.Complete(latest, true);
            Check(done.Apply && !done.Rerun && !c.Pending && !c.Running,
                "最新代成功后状态机完全安静");
        }

        static void TestGenerationRejectsStaleResult()
        {
            TaskbarProbeCoordinator c = new TaskbarProbeCoordinator(2);
            c.NotifyChange();
            long oldGeneration;
            c.TryStart(out oldGeneration);
            c.NotifyChange();

            TaskbarProbeCoordinator.Completion old = c.Complete(oldGeneration, true);
            Check(!old.Apply, "旧 generation 的成功结果不能提交");
            Check(old.Rerun && c.Pending, "旧结果完成后保留最新一代 pending");
        }

        static void TestResetInvalidatesInFlightGeneration()
        {
            TaskbarProbeCoordinator c = new TaskbarProbeCoordinator(2);
            c.NotifyChange();
            long beforeReset;
            c.TryStart(out beforeReset);

            c.Reset();
            c.NotifyChange();
            long afterReset;
            c.TryStart(out afterReset);
            Check(afterReset > beforeReset,
                "Explorer 重建后 generation 仍单调递增", beforeReset + " -> " + afterReset);

            TaskbarProbeCoordinator.Completion stale = c.Complete(beforeReset, true);
            Check(stale.Superseded && !stale.Apply,
                "Reset 前迟到结果不能完成 Reset 后的新探测");

            TaskbarProbeCoordinator.Completion current = c.Complete(afterReset, true);
            Check(current.Apply, "Reset 后当前 generation 仍能正常提交");
        }

        static void TestRetryBound()
        {
            TaskbarProbeCoordinator c = new TaskbarProbeCoordinator(2);
            c.NotifyChange();
            int starts = 0;
            int retries = 0;
            for (int i = 0; i < 5; i++)
            {
                long generation;
                if (!c.TryStart(out generation)) break;
                starts++;
                TaskbarProbeCoordinator.Completion r = c.Complete(generation, false);
                if (r.Retry) retries++;
            }
            Check(starts == 3, "初次失败 + 两次短重试后停止", starts.ToString());
            Check(retries == 2 && !c.Pending && !c.Running,
                "失败重试硬上限为 2", retries.ToString());
        }

        static void TestRapidFailureThenSuccess()
        {
            TaskbarProbeCoordinator c = new TaskbarProbeCoordinator(2);
            c.NotifyChange();
            long first;
            c.TryStart(out first);
            TaskbarProbeCoordinator.Completion failedOnce = c.Complete(first, false);
            Check(failedOnce.Retry && failedOnce.RetryAttempt == 1,
                "瞬时失败安排 1 秒级第一次重试");

            long retry;
            c.TryStart(out retry);
            TaskbarProbeCoordinator.Completion recovered = c.Complete(retry, true);
            Check(recovered.Apply && c.RetryAttempt == 0,
                "快速失败后成功会提交并清空重试计数");
        }

        static void TestBottomNegativeMonitor150()
        {
            Native.RECT monitor = R(-2560, 0, 0, 1440);
            Native.RECT taskbar = R(-2560, 1368, 0, 1440);
            Native.RECT start = R(-2560, 1368, -2476, 1440); // 56 DIP @ 150%
            TaskbarGeometry.Snapshot s = TaskbarGeometry.BuildSnapshotForTest(
                taskbar, monitor, 144, TaskbarGeometry.Edge.Bottom, start,
                -1800, true, false);

            Check(Width(s.CloverPx) == 66 && Height(s.CloverPx) == 72,
                "150% DPI 的四叶草物理尺寸为 66×72",
                Width(s.CloverPx) + "×" + Height(s.CloverPx));
            Check(s.CloverPx.Left < 0 && Contains(taskbar, s.CloverPx),
                "负坐标副屏上的四叶草保持在该任务栏内", Key(s.CloverPx));
            Check(s.WorkPx.Left == -2560 && s.WorkPx.Bottom == 1368,
                "负坐标副屏工作区按任务栏上沿截断", Key(s.WorkPx));

            Native.RECT mini = TaskbarGeometry.MiniBarRectForTest(s, 3);
            Check(Width(mini) == 198 && Height(mini) == 72,
                "150% DPI 迷你任务栏按 3 个按钮计算物理尺寸", Key(mini));
            Check(mini.Bottom == s.WorkPx.Bottom && mini.Left < 0,
                "底部迷你任务栏贴副屏工作区底边", Key(mini));
        }

        static void TestTopTaskbar125()
        {
            Native.RECT monitor = R(0, -1440, 2560, 0);
            Native.RECT taskbar = R(0, -1440, 2560, -1380);
            TaskbarGeometry.Snapshot s = TaskbarGeometry.BuildSnapshotForTest(
                taskbar, monitor, 120, TaskbarGeometry.Edge.Top, new Native.RECT(),
                900, true, false);

            Check(Width(s.CloverPx) == 55 && Height(s.CloverPx) == 60,
                "125% DPI 四叶草物理尺寸为 55×60", Key(s.CloverPx));
            Check(Contains(taskbar, s.CloverPx), "顶部任务栏定位保持在任务栏内", Key(s.CloverPx));
            Check(s.WorkPx.Top == -1380, "顶部任务栏工作区从任务栏下沿开始", Key(s.WorkPx));

            Native.RECT mini = TaskbarGeometry.MiniBarRectForTest(s, 2);
            Check(mini.Top == s.WorkPx.Top && Width(mini) == 110,
                "顶部迷你任务栏贴工作区上沿", Key(mini));
        }

        static void TestLeftAndRightTaskbars()
        {
            Native.RECT monitor = R(1920, 0, 3840, 1080);
            Native.RECT leftBar = R(1920, 0, 1980, 1080);
            TaskbarGeometry.Snapshot left = TaskbarGeometry.BuildSnapshotForTest(
                leftBar, monitor, 120, TaskbarGeometry.Edge.Left, new Native.RECT(),
                500, true, false);
            Check(left.WorkPx.Left == 1980 && Contains(leftBar, left.CloverPx),
                "左侧任务栏工作区和按钮位置正确", Key(left.CloverPx));

            Native.RECT leftMini = TaskbarGeometry.MiniBarRectForTest(left, 4);
            Check(Width(leftMini) == 55 && Height(leftMini) == 240,
                "竖任务栏迷你按钮纵向排列", Key(leftMini));
            Check(leftMini.Left == left.CloverPx.Left,
                "竖迷你条与四叶草沿 X 对齐", Key(leftMini));

            Native.RECT rightBar = R(3780, 0, 3840, 1080);
            TaskbarGeometry.Snapshot right = TaskbarGeometry.BuildSnapshotForTest(
                rightBar, monitor, 120, TaskbarGeometry.Edge.Right, new Native.RECT(),
                1040, true, false);
            Check(right.WorkPx.Right == 3780 && Contains(rightBar, right.CloverPx),
                "右侧任务栏工作区和越界夹取正确", Key(right.CloverPx));
        }

        static void TestFullEdgeInference()
        {
            Native.RECT monitor = R(-1920, -1080, 0, 0);
            Check(TaskbarGeometry.InferEdgeForTest(R(-1920, -48, 0, 0), monitor)
                    == TaskbarGeometry.Edge.Bottom,
                "铺满宽度的底部任务栏不会因同时贴左右边而误判");
            Check(TaskbarGeometry.InferEdgeForTest(R(-1920, -1080, 0, -1032), monitor)
                    == TaskbarGeometry.Edge.Top,
                "铺满宽度的顶部任务栏方向正确");
            Check(TaskbarGeometry.InferEdgeForTest(R(-1920, -1080, -1872, 0), monitor)
                    == TaskbarGeometry.Edge.Left,
                "铺满高度的左侧任务栏方向正确");
            Check(TaskbarGeometry.InferEdgeForTest(R(-48, -1080, 0, 0), monitor)
                    == TaskbarGeometry.Edge.Right,
                "铺满高度的右侧任务栏方向正确");
        }

        static void TestAutoHideWorkArea()
        {
            Native.RECT monitor = R(0, 0, 1920, 1080);
            Native.RECT taskbar = R(0, 1032, 1920, 1080);
            TaskbarGeometry.Snapshot s = TaskbarGeometry.BuildSnapshotForTest(
                taskbar, monitor, 96, TaskbarGeometry.Edge.Bottom, new Native.RECT(),
                500, true, true);
            Check(Key(s.WorkPx) == Key(monitor),
                "自动隐藏任务栏时工作区恢复完整显示器", Key(s.WorkPx));
        }

        static void TestCalibrationAndSourceLabels()
        {
            TaskbarGeometry.Snapshot startOnly = new TaskbarGeometry.Snapshot();
            startOnly.StartLocated = true;
            Check(TaskbarGeometry.HasPreciseCalibration(startOnly),
                "只有开始按钮结果时也属于精确校准");

            TaskbarGeometry.Snapshot buttonsOnly = new TaskbarGeometry.Snapshot();
            buttonsOnly.TaskButtonsLocated = true;
            Check(TaskbarGeometry.HasPreciseCalibration(buttonsOnly),
                "只有应用按钮组结果时也属于精确校准");

            Check(TaskbarGeometry.SourceDisplayName("appbar") == "AppBar 原生接口",
                "AppBar 来源不会误显示为回退估算");
            Check(TaskbarGeometry.SourceDisplayName("uia/task-buttons").IndexOf("应用按钮组", StringComparison.Ordinal) >= 0,
                "UIAutomation 应用按钮组来源有准确中文文案");
        }

        static Native.RECT R(int l, int t, int r, int b)
        {
            return new Native.RECT { Left = l, Top = t, Right = r, Bottom = b };
        }

        static int Width(Native.RECT r) { return r.Right - r.Left; }
        static int Height(Native.RECT r) { return r.Bottom - r.Top; }
        static bool Contains(Native.RECT outer, Native.RECT inner)
        {
            return inner.Left >= outer.Left && inner.Top >= outer.Top
                && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
        }
        static string Key(Native.RECT r)
        {
            return r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom;
        }
    }
}
