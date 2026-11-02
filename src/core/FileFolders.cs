using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// 「文件」标签的用户自定义文件夹清单。
    ///
    /// **职责边界**：只负责「存哪些目录」和「这些目录还能不能用」。
    /// 不列目录内容、不读文件、不监视变化 —— 那是 FileScanner 的事。
    ///
    /// **存储**：`config\file-folders.json`
    /// <code>
    /// { "items": [ { "path": "D:\\Projects", "name": "项目" } ] }
    /// </code>
    /// 原子写 + `.bak`；读失败时 JsonIO 自动回退 `.bak`，再失败返回空列表（不抛异常）。
    ///
    /// **为什么全部由用户手动添加、不内置桌面**：
    /// 内置来源会让「文件」标签在首次打开时就显示一堆用户没要求看的东西。
    /// 用户点了「+」并选了目录，才说明他确实想看那里的文件。
    /// </summary>
    public class FileFolders
    {
        /// <summary>最多允许添加几个文件夹。超过这个数，「+」提示上限而不是静默失败。</summary>
        public const int MaxFolders = DropSpec.FolderSourceMax;

        /// <summary>一条记录：目录路径 + 用户可见的显示名。</summary>
        public class Folder
        {
            /// <summary>绝对路径（已归一化）。</summary>
            public string Path = "";

            /// <summary>显示名。默认取文件夹名（盘符根取盘符），用户可改。</summary>
            public string Name = "";

            public Folder() { }

            public Folder(string path, string name)
            {
                Path = path ?? "";
                Name = name ?? "";
            }
        }

        /// <summary>添加结果码。</summary>
        public enum AddResult
        {
            /// <summary>添加成功。</summary>
            Ok = 0,
            /// <summary>路径为空或格式非法。</summary>
            InvalidPath,
            /// <summary>目录不存在。</summary>
            NotExist,
            /// <summary>路径存在但不是目录（是文件）。</summary>
            NotDirectory,
            /// <summary>UNC 路径（\\server\share）或映射的网络盘。</summary>
            NetworkPath,
            /// <summary>已经添加过（归一化后相同）。</summary>
            Duplicate,
            /// <summary>已达上限 MaxFolders。</summary>
            TooMany,
        }

        /// <summary>当前清单（只读快照语义：外部改它不会自动存盘，要调 Save）。</summary>
        public List<Folder> Items = new List<Folder>();

        /// <summary>告警回调（由 UI 层注入 ErrorLog.Warn，core 层不直接依赖）。</summary>
        public static Action<string> Warn = delegate { };

        string path;

        /// <summary>配置文件路径。</summary>
        public static string PathOf(string baseDir)
        {
            return System.IO.Path.Combine(baseDir, "config", "file-folders.json");
        }

        // ========== 读写 ==========

        /// <summary>
        /// 读配置。文件不存在 / 读不出 / JSON 损坏 → 返回空清单（不抛异常）。
        /// </summary>
        public static FileFolders Load(string baseDir)
        {
            FileFolders f = new FileFolders();
            try
            {
                f.path = PathOf(baseDir);
                if (string.IsNullOrEmpty(baseDir)) return f;

                // 为什么不用 JsonIO.Read 的 .bak 回退：
                // 它只判断「内容非空」，不校验 JSON 合法性 —— 主文件被写坏
                // （比如断电留下半截）时它照样返回那段坏内容，.bak 永远用不上。
                // 所以这里自己做两层：主文件解析失败 → 再读 .bak 试一次。
                List<Folder> parsed = ParseFile(f.path);
                if (parsed == null)
                {
                    parsed = ParseFile(f.path + ".bak");
                    if (parsed != null)
                        Warn("file-folders.json 主文件损坏，已从 .bak 恢复");
                }
                if (parsed != null) f.Items.AddRange(parsed);
            }
            catch (Exception ex)
            {
                Warn("读 file-folders.json 失败，已用空清单: " + ex.Message);
                f.Items.Clear();
            }
            return f;
        }

        /// <summary>
        /// 解析一个 JSON 文件，返回条目列表。
        ///
        /// 返回 null 表示「这个文件读不出/不是合法 JSON」—— 调用方据此决定要不要试 .bak。
        /// 返回空列表表示「文件是合法的，只是没有条目」。
        ///
        /// **这个区分很重要**：如果把「损坏」和「空」都返回空列表，
        /// 主文件一坏就永远回退不到 .bak。
        /// </summary>
        static List<Folder> ParseFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

                string json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return null;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return null;

                List<Folder> list = new List<Folder>();
                object arr;
                if (!d.TryGetValue("items", out arr) || arr == null) return list;   // 没有 items → 合法空

                foreach (object o in JsonIO.AsArray(arr))
                {
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m == null) continue;

                    string p = GetStr(m, "path");
                    if (string.IsNullOrWhiteSpace(p)) continue;

                    string n = GetStr(m, "name");
                    Folder item = new Folder(p.Trim(), string.IsNullOrWhiteSpace(n) ? "" : n.Trim());

                    // 显示名缺失时补默认值（老数据 / 手改漏写）
                    if (item.Name.Length == 0) item.Name = DefaultName(item.Path);

                    list.Add(item);
                }
                return list;
            }
            catch
            {
                return null;   // 解析失败 → 让调用方试 .bak
            }
        }

        /// <summary>存盘（原子写 + .bak）
        public void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;

                Dictionary<string, object> root = new Dictionary<string, object>();
                List<object> arr = new List<object>();
                foreach (Folder f in Items)
                {
                    if (f == null || string.IsNullOrWhiteSpace(f.Path)) continue;
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["path"] = f.Path;
                    m["name"] = f.Name ?? "";
                    arr.Add(m);
                }
                root["items"] = arr;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                JsonIO.WriteAtomic(path, ser.Serialize(root));
            }
            catch (Exception ex)
            {
                Warn("存 file-folders.json 失败: " + ex.Message);
            }
        }

        // ========== 增删改 ==========

        /// <summary>
        /// 添加一个目录。成功时已写入 Items 并自动存盘。
        ///
        /// 校验顺序（先便宜的检查，再碰磁盘）：
        ///   空 → 网络路径 → 归一化 → 存在性 → 是否目录 → 重复 → 上限
        /// </summary>
        public AddResult Add(string rawPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rawPath)) return AddResult.InvalidPath;

                string p = rawPath.Trim().Trim('"');
                if (p.Length == 0) return AddResult.InvalidPath;

                // 网络路径：UNC（\\server\share）或映射盘（DriveType.Network）
                if (IsNetworkPath(p)) return AddResult.NetworkPath;

                string norm;
                if (!TryNormalize(p, out norm)) return AddResult.InvalidPath;

                if (!Directory.Exists(norm))
                {
                    // 区分「不存在」和「是文件」
                    if (File.Exists(norm)) return AddResult.NotDirectory;
                    return AddResult.NotExist;
                }

                // 归一化后去重（大小写不敏感）
                foreach (Folder f in Items)
                {
                    if (f == null) continue;
                    if (string.Equals(f.Path, norm, StringComparison.OrdinalIgnoreCase))
                        return AddResult.Duplicate;
                }

                if (Items.Count >= MaxFolders) return AddResult.TooMany;

                Folder item = new Folder(norm, MakeUniqueName(DefaultName(norm)));
                Items.Add(item);
                Save();
                return AddResult.Ok;
            }
            catch (Exception ex)
            {
                Warn("添加文件夹失败: " + ex.Message);
                return AddResult.InvalidPath;
            }
        }

        /// <summary>移除一条记录（只删记录，不碰磁盘文件）。成功返回 true。</summary>
        public bool Remove(string rawPath)
        {
            try
            {
                string norm;
                if (!TryNormalize(rawPath, out norm)) return false;

                for (int i = 0; i < Items.Count; i++)
                {
                    Folder f = Items[i];
                    if (f == null) continue;
                    if (!string.Equals(f.Path, norm, StringComparison.OrdinalIgnoreCase)) continue;

                    Items.RemoveAt(i);
                    Save();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Warn("移除文件夹失败: " + ex.Message);
            }
            return false;
        }

        /// <summary>
        /// 改显示名。空名表示恢复默认（文件夹名 / 盘符）。
        /// 重名时自动加 (2)、(3)…。成功返回 true。
        /// </summary>
        public bool Rename(string rawPath, string newName)
        {
            try
            {
                string norm;
                if (!TryNormalize(rawPath, out norm)) return false;

                for (int i = 0; i < Items.Count; i++)
                {
                    Folder f = Items[i];
                    if (f == null) continue;
                    if (!string.Equals(f.Path, norm, StringComparison.OrdinalIgnoreCase)) continue;

                    string want = (newName ?? "").Trim();
                    if (want.Length == 0) want = DefaultName(f.Path);

                    // 重名检测要排除自己
                    f.Name = MakeUniqueNameExcept(want, f);
                    Save();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Warn("重命名文件夹失败: " + ex.Message);
            }
            return false;
        }

        /// <summary>
        /// 这个目录现在还能不能访问（目录被删 / 外置盘拔出 → false）。
        ///
        /// **不自动删记录**：外置盘可能只是暂时没插，删了用户就得重新添加。
        /// </summary>
        public static bool IsAvailable(string rawPath)
        {
            try
            {
                string norm;
                if (!TryNormalize(rawPath, out norm)) return false;
                return Directory.Exists(norm);
            }
            catch { return false; }
        }

        // ========== 路径处理 ==========

        /// <summary>
        /// 归一化路径：
        ///   · GetFullPath 展开相对路径和 ..
        ///   · 统一反斜杠（/ → \）
        ///   · 去掉尾部斜杠（但盘符根 D:\ 保留）
        ///   · 不做大小写转换（比较时用 OrdinalIgnoreCase）
        /// 失败返回 false。
        /// </summary>
        public static bool TryNormalize(string raw, out string normalized)
        {
            normalized = null;
            try
            {
                if (string.IsNullOrWhiteSpace(raw)) return false;

                string p = raw.Trim().Trim('"');
                if (p.Length == 0) return false;

                p = p.Replace('/', '\\');
                p = System.IO.Path.GetFullPath(p);

                // 去尾部斜杠，但 "D:\" 这种盘符根要保留
                if (p.Length > 3 && p.EndsWith("\\")) p = p.TrimEnd('\\');
                // 处理 "D:\" → 保留；"D:" → GetFullPath 会补成当前目录，不处理

                if (p.Length == 0) return false;
                normalized = p;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 是不是网络路径：
        ///   · UNC：以 \\ 开头
        ///   · 映射网络盘：DriveType == Network
        /// </summary>
        public static bool IsNetworkPath(string raw)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(raw)) return false;
                string p = raw.Trim().Trim('"');

                // UNC：\\server\share
                if (p.StartsWith("\\\\")) return true;

                // 映射网络盘：D:\xxx → 查 D: 的 DriveType
                if (p.Length >= 2 && p[1] == ':')
                {
                    string root = p.Substring(0, 2) + "\\";
                    try
                    {
                        DriveInfo di = new DriveInfo(root);
                        if (di.DriveType == DriveType.Network) return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 默认显示名：盘符根用盘符（"D:\" → "D 盘"），其余用最后一段目录名。
        /// </summary>
        public static string DefaultName(string normalizedPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(normalizedPath)) return "";

                string p = normalizedPath.TrimEnd('\\');
                if (p.Length == 0) return "";

                // 盘符根："D:" → "D 盘"
                if (p.Length == 2 && p[1] == ':') return p.Substring(0, 1).ToUpperInvariant() + " 盘";

                string last = System.IO.Path.GetFileName(p);
                if (!string.IsNullOrWhiteSpace(last)) return last;

                return p;
            }
            catch { return ""; }
        }

        /// <summary>生成不重名的显示名（重名加 (2)、(3)…）。</summary>
        string MakeUniqueName(string want)
        {
            return MakeUniqueNameExcept(want, null);
        }

        /// <summary>同上，但排除某一条（重命名自己时用）。</summary>
        string MakeUniqueNameExcept(string want, Folder except)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(want)) want = "文件夹";

                string candidate = want;
                int n = 2;
                while (true)
                {
                    bool clash = false;
                    foreach (Folder f in Items)
                    {
                        if (f == null || f == except) continue;
                        if (string.Equals(f.Name, candidate, StringComparison.OrdinalIgnoreCase))
                        {
                            clash = true;
                            break;
                        }
                    }
                    if (!clash) return candidate;

                    candidate = want + " (" + n + ")";
                    n++;
                    if (n > 1000) return candidate;   // 防御性上限
                }
            }
            catch { return want ?? ""; }
        }

        static string GetStr(Dictionary<string, object> m, string key)
        {
            object v;
            if (m.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return "";
        }
    }
}
