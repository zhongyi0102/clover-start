using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Clover
{
    /// <summary>
    /// 主面板目录预热：四叶草按钮首帧之后在后台完成 Catalog.Load，首次打开
    /// 面板时按输入文件的「长度 + 修改时间」领取结果。任何输入变化都会让旧结果
    /// 自动失效；没有命中时安全退回同步加载，不改变用户数据格式。
    /// </summary>
    static class CatalogPreloader
    {
        sealed class Cached
        {
            public string BaseDir;
            public string Signature;
            public Catalog Value;
        }

        static readonly object gate = new object();
        static Cached cached;
        static string warmingKey;
        static int generation;
        static Action beforeLoadForTest;

        static readonly string[] Inputs = new string[]
        {
            "apps.json", "manual-apps.json", "registry-apps.json", "store-apps.json",
            "category-overrides.json", "hidden-apps.json", "settings.json",
            "migration-category-hidden.json", "category-rules.tsv"
        };

        public static void WarmAsync(string baseDir)
        {
            if (string.IsNullOrEmpty(baseDir)) return;
            string full;
            try { full = Path.GetFullPath(baseDir); }
            catch { return; }

            string signature = InputSignature(full);
            string key = full + "\n" + signature;
            int ownGeneration;
            lock (gate)
            {
                if (cached != null
                    && string.Equals(cached.BaseDir, full, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(cached.Signature, signature, StringComparison.Ordinal)) return;
                if (string.Equals(warmingKey, key, StringComparison.OrdinalIgnoreCase)) return;
                // 新 key 代表输入或目录已变化。提升 generation，让仍在运行的旧 worker
                // 既不能覆盖新快照，也不能清掉新 worker 的忙碌状态。
                unchecked { generation++; }
                warmingKey = key;
                ownGeneration = generation;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                Stopwatch sw = Stopwatch.StartNew();
                try
                {
                    Catalog value = null;
                    string stable = null;
                    // 首次 Catalog.Load 可能导出默认规则表/完成一次性迁移。若输入因此
                    // 变化，再读一次，保证缓存代表一个稳定快照。
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        Action beforeLoad;
                        lock (gate) { beforeLoad = beforeLoadForTest; }
                        if (beforeLoad != null) beforeLoad();
                        string before = InputSignature(full);
                        value = Catalog.Load(full);
                        string after = InputSignature(full);
                        if (string.Equals(before, after, StringComparison.Ordinal))
                        {
                            stable = after;
                            break;
                        }
                    }

                    lock (gate)
                    {
                        if (ownGeneration == generation && value != null && stable != null)
                        {
                            cached = new Cached { BaseDir = full, Signature = stable, Value = value };
                        }
                    }
                    Log.Diagnostic("Catalog 后台预热完成，用时 " + sw.ElapsedMilliseconds
                        + "ms" + (stable == null ? "（输入持续变化，未缓存）" : ""));
                }
                catch (Exception ex)
                {
                    Log.Warn("Catalog 后台预热失败，首次打开将同步加载: " + ex.Message);
                }
                finally
                {
                    lock (gate)
                    {
                        // Invalidate 后可能已经启动同 key 的新一代预热；旧 worker
                        // 不能仅凭 key 相同就把新 worker 的忙碌状态清掉。
                        if (ownGeneration == generation
                            && string.Equals(warmingKey, key, StringComparison.OrdinalIgnoreCase))
                            warmingKey = null;
                    }
                }
            });
        }

        /// <summary>领取预热结果；每份对象只交给一个 PanelWindow，避免共享可变状态。</summary>
        public static Catalog TakeOrLoad(string baseDir)
        {
            Catalog hit = TryTake(baseDir);
            if (hit != null)
            {
                Log.Diagnostic("主面板命中 Catalog 预热快照");
                return hit;
            }
            Stopwatch sw = Stopwatch.StartNew();
            Catalog loaded = Catalog.Load(baseDir);
            Log.Diagnostic("主面板同步加载 Catalog，用时 " + sw.ElapsedMilliseconds + "ms");
            return loaded;
        }

        internal static Catalog TryTake(string baseDir)
        {
            if (string.IsNullOrEmpty(baseDir)) return null;
            string full;
            try { full = Path.GetFullPath(baseDir); }
            catch { return null; }
            string signature = InputSignature(full);
            lock (gate)
            {
                if (cached == null) return null;
                if (!string.Equals(cached.BaseDir, full, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(cached.Signature, signature, StringComparison.Ordinal))
                {
                    cached = null;
                    return null;
                }
                Catalog result = cached.Value;
                cached = null;
                return result;
            }
        }

        public static void Invalidate()
        {
            lock (gate)
            {
                unchecked { generation++; }
                cached = null;
                warmingKey = null;
            }
        }

        internal static bool IsWarming
        {
            get { lock (gate) { return warmingKey != null; } }
        }

        /// <summary>测试接缝：在 Catalog.Load 前阻塞 worker，覆盖失效代次竞态。</summary>
        internal static void SetBeforeLoadForTest(Action hook)
        {
            lock (gate) { beforeLoadForTest = hook; }
        }

        internal static string InputSignature(string baseDir)
        {
            StringBuilder sb = new StringBuilder();
            string cfg = Path.Combine(baseDir ?? "", "config");
            foreach (string name in Inputs)
            {
                AppendFileStamp(sb, Path.Combine(cfg, name));
                AppendFileStamp(sb, Path.Combine(cfg, name + ".bak"));
            }
            return sb.ToString();
        }

        static void AppendFileStamp(StringBuilder sb, string path)
        {
            sb.Append(Path.GetFileName(path)).Append('=');
            try
            {
                FileInfo fi = new FileInfo(path);
                if (fi.Exists) sb.Append(fi.Length).Append('@').Append(fi.LastWriteTimeUtc.Ticks);
                else sb.Append('-');
            }
            catch { sb.Append('?'); }
            sb.Append(';');
        }
    }
}
