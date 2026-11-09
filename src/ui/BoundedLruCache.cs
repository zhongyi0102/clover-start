using System;
using System.Collections.Generic;

namespace Clover
{
    /// <summary>
    /// 线程安全的有界 LRU 缓存。
    ///
    /// 图标属于小对象但生命周期很长；普通 Dictionary 会让“看过一次”的图标
    /// 永远留在进程里。这里把容量约束做成一个独立类型，AppIcons 与 IconCache
    /// 共用同一套淘汰语义，避免两层缓存各自实现后逐渐漂移。
    /// </summary>
    sealed class BoundedLruCache<TKey, TValue>
    {
        sealed class Entry
        {
            public TKey Key;
            public TValue Value;
        }

        readonly object gate = new object();
        readonly int capacity;
        readonly Dictionary<TKey, LinkedListNode<Entry>> map;
        readonly LinkedList<Entry> order = new LinkedList<Entry>();

        public BoundedLruCache(int capacity, IEqualityComparer<TKey> comparer)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException("capacity");
            this.capacity = capacity;
            map = new Dictionary<TKey, LinkedListNode<Entry>>(
                comparer ?? EqualityComparer<TKey>.Default);
        }

        public int Capacity { get { return capacity; } }

        public int Count
        {
            get { lock (gate) return map.Count; }
        }

        /// <summary>命中即提升为最近使用；value 允许为 null。</summary>
        public bool TryGetValue(TKey key, out TValue value)
        {
            lock (gate)
            {
                LinkedListNode<Entry> node;
                if (!map.TryGetValue(key, out node))
                {
                    value = default(TValue);
                    return false;
                }

                order.Remove(node);
                order.AddLast(node);
                value = node.Value.Value;
                return true;
            }
        }

        public void Set(TKey key, TValue value)
        {
            lock (gate)
            {
                LinkedListNode<Entry> existing;
                if (map.TryGetValue(key, out existing))
                {
                    existing.Value.Value = value;
                    order.Remove(existing);
                    order.AddLast(existing);
                    return;
                }

                Entry entry = new Entry { Key = key, Value = value };
                LinkedListNode<Entry> node = order.AddLast(entry);
                map[key] = node;

                while (map.Count > capacity)
                {
                    LinkedListNode<Entry> oldest = order.First;
                    if (oldest == null) break;
                    order.RemoveFirst();
                    map.Remove(oldest.Value.Key);
                }
            }
        }

        public bool Remove(TKey key)
        {
            lock (gate)
            {
                LinkedListNode<Entry> node;
                if (!map.TryGetValue(key, out node)) return false;
                map.Remove(key);
                order.Remove(node);
                return true;
            }
        }

        /// <summary>返回稳定快照，调用方可安全地边遍历边 Remove。</summary>
        public List<TKey> KeysSnapshot()
        {
            lock (gate) return new List<TKey>(map.Keys);
        }

        public void Clear()
        {
            lock (gate)
            {
                map.Clear();
                order.Clear();
            }
        }
    }
}
