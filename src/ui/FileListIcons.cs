using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Media;

namespace Clover
{
    /// <summary>文件类型图标的有界缓存与单个 STA 队列；同类型只提取一次。</summary>
    static class FileListIcons
    {
        internal const int Capacity = 96;
        static readonly object gate = new object();
        static readonly BoundedLruCache<string, ImageSource> cache =
            new BoundedLruCache<string, ImageSource>(Capacity, StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, List<Action<ImageSource>>> waiting =
            new Dictionary<string, List<Action<ImageSource>>>(StringComparer.OrdinalIgnoreCase);
        static readonly Queue<string> queue = new Queue<string>();
        static bool running;

        internal static string Key(string extension, bool folder)
        {
            if (folder) return "folder";
            string ext = (extension ?? "").Trim().ToLowerInvariant();
            if (ext.Length > 0 && ext[0] != '.') ext = "." + ext;
            return "type:" + ext;
        }

        internal static bool HasOwnIcon(string extension)
        {
            string key = Key(extension, false);
            return key == "type:.exe" || key == "type:.ico" || key == "type:.msi"
                || key == "type:.url" || key == "type:.appref-ms";
        }

        internal static bool TryGet(string key, out ImageSource source)
        { lock (gate) return cache.TryGetValue(key, out source); }

        // 未命中时仅排队，不在 UI 线程查询 Shell；命中可同步回调。
        internal static void GetAsync(string key, Action<ImageSource> completed)
        {
            ImageSource source;
            lock (gate)
            {
                if (!cache.TryGetValue(key, out source))
                {
                    List<Action<ImageSource>> callbacks;
                    if (!waiting.TryGetValue(key, out callbacks))
                    {
                        callbacks = new List<Action<ImageSource>>();
                        waiting[key] = callbacks;
                        queue.Enqueue(key);
                    }
                    if (completed != null) callbacks.Add(completed);
                    if (!running)
                    {
                        Thread worker = new Thread(Run);
                        worker.IsBackground = true;
                        worker.Name = "CloverStart.FileTypeIcons";
                        worker.SetApartmentState(ApartmentState.STA);
                        running = true;
                        worker.Start();
                    }
                    return;
                }
            }
            if (completed != null) completed(source);
        }

        static void Run()
        {
            while (true)
            {
                string key;
                lock (gate)
                {
                    if (queue.Count == 0) { running = false; return; }
                    key = queue.Dequeue();
                }
                ImageSource source = null;
                try { source = IconCache.GetFileType(key.StartsWith("type:") ? key.Substring(5) : "", key == "folder"); }
                catch { }
                List<Action<ImageSource>> callbacks;
                lock (gate)
                {
                    cache.Set(key, source);
                    callbacks = waiting[key];
                    waiting.Remove(key);
                }
                foreach (Action<ImageSource> callback in callbacks)
                {
                    try { callback(source); } catch { }
                }
            }
        }

        internal static int CountForTest { get { lock (gate) return cache.Count; } }
    }
}
