using System;
using System.Runtime.InteropServices;
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
        static readonly System.Collections.Generic.Dictionary<string, ImageSource> Cache =
            new System.Collections.Generic.Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            ImageSource cached;
            if (Cache.TryGetValue(path, out cached)) return cached;

            ImageSource result = IconCache.Get(path);
            Cache[path] = result;
            return result;
        }

        /// <summary>清掉进程内缓存（重新扫描后调用，让改了图标的程序重新取图）。</summary>
        public static void Clear()
        {
            Cache.Clear();
        }
    }
}
