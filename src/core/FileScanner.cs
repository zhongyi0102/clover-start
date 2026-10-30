using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 「文件」标签的目录列举器。**只读、不递归、不碰文件内容**。
    ///
    /// ── 边界（为什么这么严）──
    ///
    ///   · 不递归：只列一层。用户点文件夹是在资源管理器里打开，不在面板里钻下去。
    ///   · 不读内容：只取元数据（名字/大小/时间/属性）。读内容会触发
    ///     OneDrive 云文件下载 —— 用户只是想看看文件夹里有什么，不该被
    ///     悄悄拉下几个 GB。
    ///   · 不监视：没有 FileSystemWatcher。切到标签时读一次，30 秒内再切回用缓存。
    ///   · 不移动/删除/重命名：本类只有 Directory.GetFiles/GetDirectories 和
    ///     FileInfo/DirectoryInfo 的只读属性。
    ///
    /// ── 上限 ──
    ///
    ///   枚举 5000 项就停（防止在巨大目录上卡死界面线程）；
    ///   排序后最多返回 500 项（再多用户也翻不动）。
    ///   两种情况都会在返回结果里标记，界面负责显示提示。
    /// </summary>
    public static class FileScanner
    {
        /// <summary>枚举上限：数到这里就停，不再往下读目录项。</summary>
        public const int MaxEnumerate = 5000;

        /// <summary>返回上限：排序后最多给这么多条。</summary>
        public const int MaxReturn = 500;

        /// <summary>排序方式。</summary>
        public enum SortKey
        {
            /// <summary>修改时间降序（默认，最新的在最前）。</summary>
            Modified = 0,
            /// <summary>名称（逐字拼音，文件夹在前）。</summary>
            Name,
            /// <summary>大小（降序）。</summary>
            Size,
            /// <summary>类型（扩展名，同类按名称）。</summary>
            Type,
        }

        /// <summary>一条目录项。</summary>
        public class Entry
        {
            /// <summary>显示名（含扩展名）。</summary>
            public string Name = "";

            /// <summary>完整路径。</summary>
            public string Path = "";

            /// <summary>是不是文件夹。</summary>
            public bool IsFolder;

            /// <summary>修改时间（本地时间）。读不出时为 DateTime.MinValue。</summary>
            public DateTime Modified = DateTime.MinValue;

            /// <summary>大小（字节）。文件夹恒为 0。</summary>
            public long Size;

            /// <summary>扩展名（小写，含点）。文件夹为空串。</summary>
            public string Ext = "";

            public override string ToString()
            {
                return (IsFolder ? "[D] " : "[F] ") + Name + "  " + Size + "B";
            }
        }

        /// <summary>一次列举的结果。</summary>
        public class Result
        {
            /// <summary>条目列表（已排序、已截断）。</summary>
            public List<Entry> Items = new List<Entry>();

            /// <summary>枚举是否被 MaxEnumerate 截断（目录过大）。</summary>
            public bool EnumerateTruncated;

            /// <summary>返回是否被 MaxReturn 截断（仅显示前 N 项）。</summary>
            public bool ReturnTruncated;

            /// <summary>错误信息。null = 没出错。</summary>
            public string Error;

            /// <summary>枚举到的原始条目数（过滤前）。</summary>
            public int ScannedCount;

            /// <summary>过滤后、截断前的条目数。</summary>
            public int FilteredCount;

            /// <summary>给界面用的提示行（null = 不需要提示）。</summary>
            public string Notice
            {
                get
                {
                    if (!string.IsNullOrEmpty(Error)) return Error;
                    if (EnumerateTruncated)
                        return "目录过大，只扫描了前 " + MaxEnumerate + " 项";
                    if (ReturnTruncated)
                        return "仅显示前 " + MaxReturn + " 项（共 " + FilteredCount + " 项）";
                    return null;
                }
            }
        }

        // ========== 列举 ==========

        /// <summary>
        /// 列举一个目录（不递归）。永远不抛异常 —— 失败写进 Result.Error。
        /// </summary>
        /// <param name="dirPath">目录路径（会先归一化）</param>
        /// <param name="sort">排序方式</param>
        public static Result List(string dirPath, SortKey sort)
        {
            Result r = new Result();

            try
            {
                string norm;
                if (!FileFolders.TryNormalize(dirPath, out norm))
                {
                    r.Error = "路径无效";
                    return r;
                }

                if (!Directory.Exists(norm))
                {
                    r.Error = "目录不存在";
                    return r;
                }

                List<Entry> all = new List<Entry>();
                bool truncated = false;

                // ---- 枚举子目录 ----
                //
                // 用 EnumerateDirectories/EnumerateFiles 而不是 GetDirectories/GetFiles：
                // 后者一次性把整目录读进数组，几万项的目录会直接吃掉几十 MB 内存。
                // Enumerate 是流式的，我们数到 MaxEnumerate 就 break。
                try
                {
                    foreach (string d in Directory.EnumerateDirectories(norm))
                    {
                        if (all.Count >= MaxEnumerate) { truncated = true; break; }
                        Entry e = MakeEntry(d, true);
                        if (e != null) all.Add(e);
                    }
                }
                catch (UnauthorizedAccessException) { throw; }   // 整目录无权限 → 交给外层
                catch (DirectoryNotFoundException) { throw; }
                catch { }   // 枚举到一半出错：保留已拿到的，继续列文件

                // ---- 枚举文件 ----
                if (!truncated)
                {
                    try
                    {
                        foreach (string f in Directory.EnumerateFiles(norm))
                        {
                            if (all.Count >= MaxEnumerate) { truncated = true; break; }
                            Entry e = MakeEntry(f, false);
                            if (e != null) all.Add(e);
                        }
                    }
                    catch (UnauthorizedAccessException) { throw; }
                    catch (DirectoryNotFoundException) { throw; }
                    catch { }
                }

                r.ScannedCount = all.Count;
                r.EnumerateTruncated = truncated;

                // ---- 排序 ----
                Sort(all, sort);

                r.FilteredCount = all.Count;

                // ---- 截断 ----
                if (all.Count > MaxReturn)
                {
                    all.RemoveRange(MaxReturn, all.Count - MaxReturn);
                    r.ReturnTruncated = true;
                }

                r.Items = all;
            }
            catch (UnauthorizedAccessException)
            {
                r.Error = "没有访问权限";
                r.Items = new List<Entry>();
            }
            catch (DirectoryNotFoundException)
            {
                r.Error = "目录不存在";
                r.Items = new List<Entry>();
            }
            catch (Exception ex)
            {
                r.Error = "读取失败: " + ex.Message;
                r.Items = new List<Entry>();
            }

            return r;
        }

        /// <summary>
        /// 构造一条 Entry。**被过滤规则排除时返回 null**。
        ///
        /// 单个文件出错（属性读不出、路径太长）只跳过该项，不影响整批。
        /// </summary>
        static Entry MakeEntry(string fullPath, bool isFolder)
        {
            try
            {
                string name = System.IO.Path.GetFileName(fullPath);
                if (string.IsNullOrEmpty(name)) return null;

                // ---- 过滤规则 ----
                if (ShouldSkip(name, isFolder, fullPath)) return null;

                Entry e = new Entry();
                e.Name = name;
                e.Path = fullPath;
                e.IsFolder = isFolder;

                if (isFolder)
                {
                    // 文件夹不读大小（算目录大小要递归，违反"不递归"）
                    e.Size = 0;
                    e.Ext = "";
                    try { e.Modified = new DirectoryInfo(fullPath).LastWriteTime; } catch { }
                }
                else
                {
                    e.Ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
                    try
                    {
                        FileInfo fi = new FileInfo(fullPath);
                        // **只读元数据**：Length / LastWriteTime 都是目录项里的缓存值，
                        // 不会触发 OneDrive 云文件下载（那需要真的打开文件流）。
                        e.Size = fi.Length;
                        e.Modified = fi.LastWriteTime;
                    }
                    catch
                    {
                        // 单个文件属性读不出（权限/被占用）→ 跳过这一项
                        return null;
                    }
                }

                return e;
            }
            catch
            {
                // 路径过长 / 非法字符 / 其他 → 跳过
                return null;
            }
        }

        /// <summary>
        /// 该不该跳过这个条目。
        ///
        /// 规则（按开销从低到高）：
        ///   1. 名字：.lnk / desktop.ini / 以 ~$ 开头 / 临时下载后缀
        ///   2. 属性：隐藏或系统
        /// </summary>
        static bool ShouldSkip(string name, bool isFolder, string fullPath)
        {
            try
            {
                string lower = name.ToLowerInvariant();

                // ---- 1. 名字规则 ----
                if (lower.EndsWith(".lnk")) return true;          // 快捷方式不显示
                if (lower == "desktop.ini") return true;          // 系统桌面配置
                if (name.StartsWith("~$")) return true;           // Office 临时文件
                if (lower.EndsWith(".tmp")) return true;
                if (lower.EndsWith(".crdownload")) return true;   // Chrome 未下完
                if (lower.EndsWith(".part")) return true;         // Firefox 未下完
                if (lower.EndsWith(".download")) return true;     // 通用未下完

                // ---- 2. 属性规则 ----
                //
                // 用 FileAttributes 判断：Hidden 或 System 都跳过。
                // 注意：不能只看名字（如 "System Volume Information"），
                // 属性才是权威来源。
                FileAttributes attr;
                if (isFolder)
                {
                    try { attr = new DirectoryInfo(fullPath).Attributes; }
                    catch { return false; }   // 读不出属性 → 不因此跳过（保守）
                }
                else
                {
                    try { attr = File.GetAttributes(fullPath); }
                    catch { return false; }
                }

                if ((attr & FileAttributes.Hidden) != 0) return true;
                if ((attr & FileAttributes.System) != 0) return true;

                return false;
            }
            catch { return false; }
        }

        // ========== 排序 ==========

        /// <summary>
        /// 排序。**文件夹始终排在文件前**，然后按指定方式比较。
        /// </summary>
        public static void Sort(List<Entry> list, SortKey sort)
        {
            if (list == null || list.Count < 2) return;

            try
            {
                list.Sort(delegate(Entry x, Entry y)
                {
                    // 文件夹在前（这条优先于任何排序方式）
                    if (x.IsFolder != y.IsFolder) return x.IsFolder ? -1 : 1;

                    int c = 0;
                    switch (sort)
                    {
                        case SortKey.Name:
                            c = CompareName(x, y);
                            break;

                        case SortKey.Size:
                            // 大文件在前；文件夹之间按名字（它们大小都是 0）
                            c = y.Size.CompareTo(x.Size);
                            if (c == 0) c = CompareName(x, y);
                            break;

                        case SortKey.Type:
                            c = string.Compare(x.Ext, y.Ext, StringComparison.OrdinalIgnoreCase);
                            if (c == 0) c = CompareName(x, y);
                            break;

                        case SortKey.Modified:
                        default:
                            // 新的在前
                            c = y.Modified.CompareTo(x.Modified);
                            if (c == 0) c = CompareName(x, y);
                            break;
                    }

                    if (c != 0) return c;
                    // 最后用路径兜底，保证顺序稳定（不会每次刷新都跳）
                    return string.Compare(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);
                });
            }
            catch { }
        }

        /// <summary>按名称比较（逐字拼音键 + Ordinal，与「全部」同一套）。</summary>
        static int CompareName(Entry x, Entry y)
        {
            int c = string.Compare(
                Pinyin.SortKey(x.Name), Pinyin.SortKey(y.Name), StringComparison.Ordinal);
            if (c != 0) return c;
            return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        }

        // ========== 过滤（搜索）==========

        /// <summary>
        /// 按搜索词过滤。空查询返回原列表（不复制）。
        ///
        /// 复用 Pinyin.Matches：支持中文、英文、全拼、首字母。
        /// </summary>
        public static List<Entry> Filter(List<Entry> items, string query)
        {
            if (items == null) return new List<Entry>();
            if (string.IsNullOrWhiteSpace(query)) return items;

            string q = query.Trim();
            List<Entry> hit = new List<Entry>();
            foreach (Entry e in items)
            {
                if (e == null) continue;
                try
                {
                    if (Pinyin.Matches(e.Name, q)) hit.Add(e);
                }
                catch { }
            }
            return hit;
        }

        // ========== 格式化辅助（界面用，测试也可断言）==========

        /// <summary>大小转可读文本（B / KB / MB / GB）。</summary>
        public static string FormatSize(long bytes)
        {
            try
            {
                if (bytes < 0) return "-";
                if (bytes < 1024) return bytes + " B";
                double kb = bytes / 1024.0;
                if (kb < 1024) return kb.ToString("0.#") + " KB";
                double mb = kb / 1024.0;
                if (mb < 1024) return mb.ToString("0.#") + " MB";
                double gb = mb / 1024.0;
                return gb.ToString("0.##") + " GB";
            }
            catch { return "-"; }
        }

        /// <summary>修改时间转可读文本。</summary>
        public static string FormatTime(DateTime t)
        {
            try
            {
                if (t == DateTime.MinValue) return "";
                DateTime now = DateTime.Now;

                // 今天 → 只显示时间；今年 → 月日 + 时间；更早 → 完整日期
                if (t.Date == now.Date) return t.ToString("HH:mm");
                if (t.Year == now.Year) return t.ToString("MM-dd HH:mm");
                return t.ToString("yyyy-MM-dd");
            }
            catch { return ""; }
        }
    }
}
