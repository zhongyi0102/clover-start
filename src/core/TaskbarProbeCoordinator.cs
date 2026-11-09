using System;

namespace Clover
{
    /// <summary>
    /// 任务栏探测的纯状态机：合并密集失效、保证单飞、阻止旧代结果覆盖新布局，
    /// 并给失败重试设置硬上限。没有 WPF/Timer 依赖，可做高强度确定性压测。
    /// </summary>
    sealed class TaskbarProbeCoordinator
    {
        public struct Completion
        {
            public bool Apply;
            public bool Rerun;
            public bool Retry;
            public bool Superseded;
            public int RetryAttempt;
        }

        readonly int retryLimit;
        long latestGeneration;
        long activeGeneration;
        bool pending;
        bool running;
        int retryAttempt;

        public TaskbarProbeCoordinator(int retryLimit)
        {
            this.retryLimit = Math.Max(0, retryLimit);
        }

        public long LatestGeneration { get { return latestGeneration; } }
        public bool Pending { get { return pending; } }
        public bool Running { get { return running; } }
        public int RetryAttempt { get { return retryAttempt; } }

        public long NotifyChange()
        {
            unchecked
            {
                latestGeneration++;
                if (latestGeneration <= 0) latestGeneration = 1;
            }
            pending = true;
            retryAttempt = 0;
            return latestGeneration;
        }

        public bool TryStart(out long generation)
        {
            generation = 0;
            if (running || !pending) return false;

            pending = false;
            running = true;
            activeGeneration = latestGeneration;
            generation = activeGeneration;
            return true;
        }

        public Completion Complete(long generation, bool success)
        {
            Completion result = new Completion();
            if (!running || generation != activeGeneration)
            {
                result.Superseded = true;
                return result;
            }

            running = false;

            if (generation != latestGeneration)
            {
                pending = true;
                result.Superseded = true;
                result.Rerun = true;
                return result;
            }

            if (success)
            {
                retryAttempt = 0;
                result.Apply = true;
                return result;
            }

            if (retryAttempt < retryLimit)
            {
                retryAttempt++;
                pending = true;
                result.Retry = true;
                result.RetryAttempt = retryAttempt;
            }
            return result;
        }

        public void Reset()
        {
            // generation 必须单调前进，不能清零。否则旧探测 gen=1 尚未回调时，
            // Reset 后的新探测也可能再次拿到 gen=1，迟到旧结果会被误认为当前结果。
            unchecked
            {
                latestGeneration++;
                if (latestGeneration <= 0) latestGeneration = 1;
            }
            activeGeneration = 0;
            pending = false;
            running = false;
            retryAttempt = 0;
        }
    }
}
