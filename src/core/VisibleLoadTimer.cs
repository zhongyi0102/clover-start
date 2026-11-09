using System;
using System.Diagnostics;

namespace Clover
{
    /// <summary>
    /// 只统计一次“可见加载周期”的计时器。隐藏/关闭会取消当前代次；旧回调即使迟到，
    /// 也不能完成下一次显示的新计时，避免把后台隐藏时间算进首屏性能。
    /// </summary>
    sealed class VisibleLoadTimer
    {
        Stopwatch watch;
        int generation;

        public bool Running { get { return watch != null; } }
        public int Generation { get { return generation; } }

        public int Begin()
        {
            unchecked
            {
                generation++;
                if (generation <= 0) generation = 1;
            }
            watch = Stopwatch.StartNew();
            return generation;
        }

        public void Cancel()
        {
            unchecked
            {
                generation++;
                if (generation <= 0) generation = 1;
            }
            if (watch != null) watch.Stop();
            watch = null;
        }

        public bool TryComplete(int expectedGeneration, out long elapsedMilliseconds)
        {
            elapsedMilliseconds = 0;
            if (watch == null || expectedGeneration != generation) return false;
            watch.Stop();
            elapsedMilliseconds = watch.ElapsedMilliseconds;
            watch = null;
            return true;
        }
    }
}
