using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>桌面上的一个项目（文件或文件夹）。</summary>
    class DesktopItem
    {
        public string Name;
        public string Path;
        public bool IsFolder;
        /// <summary>小写扩展名（含点）；文件夹为空串。</summary>
        public string Ext;
        /// <summary>分组名（程序 / 文件夹 / 文档 / 图片 / 视频 / 压缩包 / 其他）。</summary>
        public string Group;
        public long Size;
        public DateTime Modified;
    }

    /// <summary>
    /// 虚拟桌面：读取桌面上的文件与文件夹，供面板显示。
    ///
    /// **核心原则：只读，不动真实桌面。**
    /// 用户提"桌面美化"，但真正难受的是"桌面乱"。虚拟桌面让他在面板里
    /// 看到按类型分好组的桌面内容，真实桌面可以完全不管 —— 零风险。
    ///
    /// 与方案 B（把文件移到分类子文件夹）的区别：
    ///   · B 会改变磁盘上的真实位置，别的程序引用桌面路径会失效
    ///   · 这里只是"在面板里多一个视图"，删掉这个功能桌面毫无影响
    ///
    /// 同时读用户桌面和公共桌面（Public\Desktop）——
    /// 很多安装程序把快捷方式放到公共桌面，只读用户桌面会漏。
    /// </summary>
    static class DesktopItems
    {
        /// <summary>分组顺序（界面上按这个顺序排）。</summary>
        public static readonly string[] GroupOrder = new string[]
        {
            "程序", "文件夹", "文档", "图片", "视频", "压缩包", "其他"
        };

        /// <summary>
        /// 读取桌面全部项目。
        ///
        /// 不抛异常：任何目录读不到就跳过（权限、路径不存在都算正常）。
        /// 按"分组 → 名字"排序，让界面拿到就是有序的。
        /// </summary>
        public static List<DesktopItem> Load()
        {
            List<DesktopItem> list = new List<DesktopItem>();
            try
            {
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                string[] dirs = new string[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                };

                foreach (string dir in dirs)
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

                    // 文件
                    try
                    {
                        foreach (string f in Directory.EnumerateFiles(dir))
                        {
                            try
                            {
                                if (IsSystemJunk(Path.GetFileName(f), f)) continue;
                                if (!seen.Add(f)) continue;
                                list.Add(Make(f, false));
                            }
                            catch { }
                        }
                    }
                    catch { }

                    // 文件夹
                    try
                    {
                        foreach (string d in Directory.EnumerateDirectories(dir))
                        {
                            try
                            {
                                if (IsSystemJunk(Path.GetFileName(d), d)) continue;
                                if (!seen.Add(d)) continue;
                                list.Add(Make(d, true));
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                Sort(list);
            }
            catch (Exception ex)
            {
                Log.Warn("读取桌面失败: " + ex.Message);
            }
            return list;
        }

        /// <summary>
        /// 桌面上的**系统/临时文件**，不该显示给用户。
        ///
        /// 实测这台机器的桌面有 8 项，其中 3 项是噪音：
        ///   · desktop.ini（用户桌面 + 公共桌面各一个）—— 系统配置，隐藏文件
        ///   · ~$README.docx / ~$thon大作业.docx —— Word 打开文档时的锁文件
        /// 用户不会想"整理"这些，它们出现在面板里只会让人困惑
        /// （尤其 ~$ 开头的，看起来像乱码）。
        /// </summary>
        static bool IsSystemJunk(string fileName, string fullPath)
        {
            try
            {
                if (string.IsNullOrEmpty(fileName)) return true;

                string lower = fileName.ToLowerInvariant();

                // 系统配置文件
                if (lower == "desktop.ini") return true;
                if (lower == "thumbs.db") return true;

                // Office / WPS 临时锁文件（~$ 开头）
                if (lower.StartsWith("~$")) return true;

                // Office 临时文件（~ 开头，.tmp 结尾）
                if (lower.StartsWith("~") && lower.EndsWith(".tmp")) return true;

                // 隐藏 + 系统属性的文件一律跳过
                try
                {
                    FileAttributes attr = File.GetAttributes(fullPath);
                    if ((attr & FileAttributes.System) != 0) return true;
                }
                catch { }

                return false;
            }
            catch { return false; }
        }

        static DesktopItem Make(string path, bool isFolder)
        {
            DesktopItem it = new DesktopItem();
            it.Path = path;
            it.IsFolder = isFolder;
            it.Name = isFolder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
            it.Ext = isFolder ? "" : Path.GetExtension(path).ToLowerInvariant();
            it.Group = GroupOf(it.Ext, isFolder);

            try
            {
                if (isFolder)
                {
                    DirectoryInfo di = new DirectoryInfo(path);
                    it.Modified = di.LastWriteTime;
                    it.Size = 0;   // 文件夹不算大小（递归统计太慢）
                }
                else
                {
                    FileInfo fi = new FileInfo(path);
                    it.Modified = fi.LastWriteTime;
                    it.Size = fi.Length;
                }
            }
            catch { }

            return it;
        }

        /// <summary>按扩展名分组。</summary>
        static string GroupOf(string ext, bool isFolder)
        {
            if (isFolder) return "文件夹";

            switch (ext)
            {
                case ".lnk":
                case ".exe":
                case ".url":
                case ".appref-ms":
                case ".bat":
                case ".cmd":
                    return "程序";

                case ".txt": case ".md": case ".pdf": case ".doc": case ".docx":
                case ".xls": case ".xlsx": case ".ppt": case ".pptx":
                case ".csv": case ".rtf":
                    return "文档";

                case ".png": case ".jpg": case ".jpeg": case ".gif": case ".bmp":
                case ".webp": case ".svg": case ".ico": case ".psd":
                    return "图片";

                case ".mp4": case ".avi": case ".mkv": case ".mov": case ".wmv":
                case ".flv": case ".webm": case ".mp3": case ".wav": case ".flac":
                    return "视频";

                case ".zip": case ".rar": case ".7z": case ".tar": case ".gz":
                case ".iso":
                    return "压缩包";

                default:
                    return "其他";
            }
        }

        static void Sort(List<DesktopItem> list)
        {
            try
            {
                list.Sort(delegate(DesktopItem a, DesktopItem b)
                {
                    int ga = Array.IndexOf(GroupOrder, a.Group);
                    int gb = Array.IndexOf(GroupOrder, b.Group);
                    if (ga < 0) ga = GroupOrder.Length;
                    if (gb < 0) gb = GroupOrder.Length;
                    if (ga != gb) return ga.CompareTo(gb);
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
            }
            catch { }
        }

        /// <summary>统计某个分组的数量（界面显示分组标题用）。</summary>
        public static int CountGroup(List<DesktopItem> list, string group)
        {
            if (list == null) return 0;
            int n = 0;
            foreach (DesktopItem it in list)
            {
                if (string.Equals(it.Group, group, StringComparison.Ordinal)) n++;
            }
            return n;
        }

        /// <summary>文件大小的人类可读形式。</summary>
        public static string FormatSize(long bytes)
        {
            try
            {
                if (bytes <= 0) return "";
                if (bytes < 1024) return bytes + " B";
                if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
                if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.#") + " MB";
                return (bytes / 1024.0 / 1024 / 1024).ToString("0.##") + " GB";
            }
            catch { return ""; }
        }
    }
}
