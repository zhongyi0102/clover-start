using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;

namespace Clover
{
    /// <summary>
    /// 应用图标获取（需要 WPF）。
    ///
    /// 从 Native 里拆出来：Native 现在是纯 P/Invoke（core 层），
    /// 而 ImageSource 属于 WPF，不能出现在 core 层。
    ///
    /// 实际实现走 IconCache（内存 + 磁盘 PNG 缓存，key 含文件修改时间），
    /// 这里只保留一层进程内字典，避免同一路径反复算 key。
    /// </summary>
    static class AppIcons
    {
        sealed class PendingLoad
        {
            public string Path;
            public int Generation;
        }

        static readonly object Gate = new object();
        static readonly object LoadGate = new object();
        internal const int MemoryCacheCapacity = 96;
        static readonly BoundedLruCache<string, ImageSource> Cache =
            new BoundedLruCache<string, ImageSource>(MemoryCacheCapacity,
                StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, List<Action<ImageSource>>> Waiters =
            new Dictionary<string, List<Action<ImageSource>>>(StringComparer.OrdinalIgnoreCase);
        static readonly Queue<PendingLoad> Queue = new Queue<PendingLoad>();
        static bool workerRunning;
        static int generation;

        public static ImageSource Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            ImageSource cached;
            lock (Gate)
            {
                if (Cache.TryGetValue(path, out cached)) return cached;
            }

            // 拖动浮层等交互路径仍允许同步读取。与异步工作线程共用
            // LoadGate，避免同一个缓存文件被两个线程同时提取/写入。
            ImageSource result;
            lock (LoadGate) result = IconCache.Get(path);
            lock (Gate) Cache.Set(path, result);
            return result;
        }

        /// <summary>
        /// 只查进程内缓存，不碰磁盘、Shell 或快捷方式解析。
        /// 返回 true 时 image 可以为 null（表示此前已确认取不到图标）。
        /// </summary>
        public static bool TryGetCached(string path, out ImageSource image)
        {
            image = null;
            if (string.IsNullOrEmpty(path)) return false;
            lock (Gate) return Cache.TryGetValue(path, out image);
        }

        /// <summary>
        /// 在单个 STA 后台工作队列中读取图标。相同路径的并发请求会合并；
        /// 缓存命中时原线程立即回调，未命中时在工作线程回调，调用方负责
        /// 切回自己的 Dispatcher。
        /// </summary>
        public static void GetAsync(string path, Action<ImageSource> completed)
        {
            if (string.IsNullOrEmpty(path))
            {
                if (completed != null) completed(null);
                return;
            }

            ImageSource cached = null;
            bool hit = false;
            lock (Gate)
            {
                // 查询与登记必须在同一把锁里完成。若分成两段，工作线程可能
                // 恰好在两段之间写入缓存并移除 Waiters，调用方随后又把同一路径
                // 排一次，造成偶发的重复磁盘/Shell I/O。
                hit = Cache.TryGetValue(path, out cached);
                if (!hit)
                {
                    List<Action<ImageSource>> callbacks;
                    if (!Waiters.TryGetValue(path, out callbacks))
                    {
                        callbacks = new List<Action<ImageSource>>();
                        Waiters[path] = callbacks;
                        Queue.Enqueue(new PendingLoad { Path = path, Generation = generation });
                    }
                    if (completed != null) callbacks.Add(completed);

                    if (!workerRunning) StartWorkerLocked();
                }
            }

            if (hit && completed != null) completed(cached);
        }

        /// <summary>
        /// Shell 图标与快捷方式解析会用到 COM；固定 STA 比 ThreadPool 的 MTA
        /// 更符合 Shell API 的线程模型，也避免部分机器上商店应用一直停在占位图。
        /// 调用方已持有 Gate。
        /// </summary>
        static void StartWorkerLocked()
        {
            workerRunning = true;
            try
            {
                Thread worker = new Thread(RunWorker);
                worker.IsBackground = true;
                worker.Name = "CloverStart.IconLoader";
                worker.SetApartmentState(ApartmentState.STA);
                worker.Start();
            }
            catch
            {
                workerRunning = false;
                throw;
            }
        }

        static void RunWorker()
        {
            while (true)
            {
                PendingLoad work;
                lock (Gate)
                {
                    if (Queue.Count == 0)
                    {
                        workerRunning = false;
                        return;
                    }
                    work = Queue.Dequeue();
                }

                ImageSource result = null;
                try
                {
                    lock (LoadGate) result = IconCache.Get(work.Path);
                }
                catch { }

                List<Action<ImageSource>> callbacks = null;
                lock (Gate)
                {
                    // Clear() 之后到达的旧结果不能重新污染新缓存。
                    if (work.Generation == generation)
                    {
                        Cache.Set(work.Path, result);
                        Waiters.TryGetValue(work.Path, out callbacks);
                        Waiters.Remove(work.Path);
                    }
                }

                if (callbacks == null) continue;
                foreach (Action<ImageSource> callback in callbacks)
                {
                    try { if (callback != null) callback(result); }
                    catch { }
                }
            }
        }

        /// <summary>清掉进程内缓存（重新扫描后调用，让改了图标的程序重新取图）。</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                generation++;
                Cache.Clear();
                Queue.Clear();
                Waiters.Clear();
            }
        }

        internal static int CachedCountForTest
        {
            get { lock (Gate) return Cache.Count; }
        }
    }
}
