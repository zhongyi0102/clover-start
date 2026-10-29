using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Clover
{
    /// <summary>
    /// .lnk 快捷方式解析（带缓存）。
    ///
    /// **为什么需要这个类**：
    /// 开始菜单里 112/119 条是 `.lnk` 路径，而注册表给的是真实 exe 路径。
    /// 不解析 lnk 就无法按路径去重 —— 同一个程序会以两个名字出现两次
    /// （`7-Zip File Manager.lnk` 和 `D:\...\7zFM.exe`）。
    ///
    /// 放在 core 层而不是复用 ui 层的 PanelWindow.ResolveShortcutTarget：
    /// core 不能依赖 ui（build.ps1 会单独编译 core 来验证这一点）。
    ///
    /// 与 RunningApps 的关系：
    /// RunningApps 有自己的 targetCache，但它是**私有**的、
    /// 且语义是"这个应用在不在运行"。这里需要的是"给我 lnk 的目标路径"，
    /// 用途不同、生命周期不同，所以独立一份缓存。
    /// 两份缓存的代价可接受：AppDedup 只在扫描时跑一次。
    ///
    /// 缓存策略：
    ///   · key = lnk 完整路径（忽略大小写）
    ///   · value = 目标路径；null 表示解析失败（也缓存，避免反复尝试 COM）
    /// </summary>
    static class ShortcutResolver
    {
        /// <summary>lnk 路径 → 目标路径（null = 解析失败）。</summary>
        static readonly Dictionary<string, string> cache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static readonly object gate = new object();

        /// <summary>统计：实际解析次数（未命中缓存）。</summary>
        static int realResolves;

        /// <summary>统计：缓存命中次数。</summary>
        static int cacheHits;

        /// <summary>实际解析次数（COM 调用）。</summary>
        public static int RealResolves { get { lock (gate) { return realResolves; } } }

        /// <summary>缓存命中次数。</summary>
        public static int CacheHits { get { lock (gate) { return cacheHits; } } }

        /// <summary>缓存条目数。</summary>
        public static int CacheCount { get { lock (gate) { return cache.Count; } } }

        /// <summary>
        /// 解析 .lnk 的目标路径。失败返回 null。
        ///
        /// **不做任何猜测**：解析不出来就是 null，调用方保留原有逻辑
        /// （用户要求"解析失败时不要强行合并"）。
        /// </summary>
        public static string Resolve(string lnkPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(lnkPath)) return null;
                string key = lnkPath.Trim();

                lock (gate)
                {
                    string cached;
                    if (cache.TryGetValue(key, out cached))
                    {
                        cacheHits++;
                        return cached;
                    }
                }

                string target = ResolveCore(key);

                lock (gate)
                {
                    cache[key] = target;
                    realResolves++;
                }
                return target;
            }
            catch { return null; }
        }

        static string ResolveCore(string lnkPath)
        {
            try
            {
                if (!File.Exists(lnkPath)) return null;

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;

                object shell = Activator.CreateInstance(shellType);
                if (shell == null) return null;

                object sc = shellType.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                if (sc == null) return null;

                object tp = sc.GetType().InvokeMember("TargetPath",
                    BindingFlags.GetProperty, null, sc, null);
                string s = tp as string;

                // WScript.Shell 对无效快捷方式返回空串而不是 null，统一成 null
                if (string.IsNullOrWhiteSpace(s)) return null;
                return s.Trim();
            }
            catch { return null; }
        }

        /// <summary>
        /// 解析并返回"去重用的路径键"。
        ///
        /// 与 Resolve 的区别：
        ///   · .lnk → 解析出的目标 exe 路径（解析失败返回 null）
        ///   · 其他（.exe / shell 路径）→ 原样归一化
        ///
        /// 返回 null 表示"这个条目的路径不适合当去重键"
        /// （比如 lnk 解析失败），调用方应退回名称匹配。
        /// </summary>
        public static string ResolveDedupKey(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return null;

                string p = path.Trim().Trim('"').Trim();

                // Shell 路径（商店应用）：直接归一化，不需要解析
                if (AppPaths.IsShellPath(p)) return AppPaths.Normalize(p);

                // .lnk：解析出目标
                if (p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string target = Resolve(p);
                    if (string.IsNullOrEmpty(target)) return null;   // 解析失败 → 不用路径去重
                    // 目标必须是文件（有些 lnk 指向文件夹或 URL）
                    if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        return null;
                    try { if (!File.Exists(target)) return null; } catch { return null; }
                    return AppPaths.Normalize(target);
                }

                // 其他（.exe 等）：直接归一化
                return AppPaths.Normalize(p);
            }
            catch { return null; }
        }

        /// <summary>清空缓存（重新扫描时调用）。</summary>
        public static void Clear()
        {
            lock (gate)
            {
                cache.Clear();
                realResolves = 0;
                cacheHits = 0;
            }
        }
    }
}
