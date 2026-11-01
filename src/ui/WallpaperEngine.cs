using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// Wallpaper Engine 壁纸目录发现与元数据解析。
    ///
    /// ── 只读 ──
    ///
    /// 本类**只读** WE 的目录，不修改、不移动、不删除它的任何文件，
    /// 不读它的配置，不调用它的命令行。
    ///
    /// ── 目录查找链 ──
    ///
    ///   1. 注册表 `HKCU\SOFTWARE\Valve\Steam` 的 `SteamPath`
    ///      （或 `HKLM\SOFTWARE\WOW6432Node\Valve\Steam` 的 `InstallPath`）
    ///   2. `steamapps\libraryfolders.vdf` 里列出的每个 Steam 库
    ///   3. 每个库下 `steamapps\workshop\content\431960\<壁纸ID>\`
    ///   4. 加上 WE 安装目录下 `projects\myprojects\`
    ///
    /// 431960 是 Wallpaper Engine 的 Steam AppID。
    ///
    /// ── 支持的类型 ──
    ///
    ///   · **video** → 主文件是 mp4，可直接播放
    ///   · **scene** → 素材打包在 `scene.pkg` 里（私有格式，无法解析），
    ///                只能用 `preview.*` 作**静态**背景
    ///
    /// `web` / `application` 不列出（前者要浏览器引擎，后者是 WE 自己的可执行程序）。
    ///
    /// ── 路径安全 ──
    ///
    /// `project.json` 里的 `file` / `preview` 是**外部数据**，必须校验：
    ///   · 拒绝绝对路径
    ///   · 拒绝含 `..` 的路径
    ///   · 解析后必须落在壁纸文件夹内
    /// 否则一个恶意/损坏的 project.json 就能让程序去读任意文件。
    /// </summary>
    public static class WallpaperEngine
    {
        /// <summary>Wallpaper Engine 的 Steam AppID。</summary>
        public const string AppId = "431960";

        /// <summary>可列出的类型（小写）。</summary>
        public static readonly string[] SupportedTypes = { "video", "scene" };

        // ========== 数据模型 ==========

        /// <summary>一个壁纸条目。</summary>
        public class Item
        {
            /// <summary>创意工坊 ID 或本地目录名。</summary>
            public string Id = "";

            /// <summary>标题（project.json 的 title）。</summary>
            public string Title = "";

            /// <summary>类型：video / scene（小写）。</summary>
            public string Type = "";

            /// <summary>壁纸文件夹绝对路径。</summary>
            public string Dir = "";

            /// <summary>主文件绝对路径（video 是 mp4；scene 为空）。</summary>
            public string MainFile = "";

            /// <summary>预览图绝对路径（preview.jpg / .gif / .png）。</summary>
            public string PreviewFile = "";

            /// <summary>主文件大小（字节）。</summary>
            public long MainSize = 0;

            /// <summary>预览图像素宽度（0 = 未知）。</summary>
            public int PreviewWidth = 0;

            /// <summary>预览图像素高度（0 = 未知）。</summary>
            public int PreviewHeight = 0;

            /// <summary>是否是 scene（只能静态预览）。</summary>
            public bool IsScene
            {
                get { return string.Equals(Type, "scene", StringComparison.OrdinalIgnoreCase); }
            }

            /// <summary>预览图分辨率是否偏低（小于 1280 宽）。</summary>
            public bool LowResPreview
            {
                get { return PreviewWidth > 0 && PreviewWidth < 1280; }
            }

            /// <summary>列表里显示的类型标签。</summary>
            public string TypeLabel
            {
                get
                {
                    if (IsScene) return "仅静态预览";
                    if (string.Equals(Type, "video", StringComparison.OrdinalIgnoreCase)) return "视频";
                    return Type;
                }
            }
        }

        /// <summary>扫描结果。</summary>
        public class ScanResult
        {
            public List<Item> Items = new List<Item>();
            public List<string> Roots = new List<string>();
            public string Error = null;

            /// <summary>找到几个可用壁纸目录。</summary>
            public int RootCount { get { return Roots == null ? 0 : Roots.Count; } }
        }

        /// <summary>
        /// 测试专用：临时覆盖根目录。生产环境始终为 null。
        /// 用于让集成测试驱动真实的 ResolveById → PanelWindow 背景调用链，
        /// 不依赖测试机器是否安装 Wallpaper Engine。
        /// </summary>
        internal static List<string> TestRootsOverride { get; set; }
        // ========== 目录查找 ==========

        /// <summary>
        /// 自动查找所有壁纸根目录（每个库里一个 workshop 目录 + myprojects）。
        ///
        /// 找不到返回空列表（调用方给"手动选择目录"）。
        /// </summary>
        public static List<string> FindRoots()
        {
            if (TestRootsOverride != null)
                return new List<string>(TestRootsOverride);

            List<string> roots = new List<string>();
            try
            {
                List<string> steamLibs = FindSteamLibraries();

                foreach (string lib in steamLibs)
                {
                    try
                    {
                        string ws = Path.Combine(lib,
                            "steamapps", "workshop", "content", AppId);
                        if (Directory.Exists(ws) && !roots.Contains(ws)) roots.Add(ws);
                    }
                    catch { }
                }

                // WE 安装目录下的 myprojects（本地自建壁纸）
                foreach (string lib in steamLibs)
                {
                    try
                    {
                        string we = Path.Combine(lib,
                            "steamapps", "common", "wallpaper_engine", "projects", "myprojects");
                        if (Directory.Exists(we) && !roots.Contains(we)) roots.Add(we);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("查找 Wallpaper Engine 目录失败: " + ex.Message); } catch { }
            }
            return roots;
        }

        /// <summary>找所有 Steam 库目录。</summary>
        static List<string> FindSteamLibraries()
        {
            List<string> libs = new List<string>();
            try
            {
                // ---- 1. 从注册表拿 Steam 主目录 ----
                string steamPath = null;
                steamPath = ReadReg(@"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam", "SteamPath");
                if (string.IsNullOrEmpty(steamPath))
                    steamPath = ReadReg(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
                if (string.IsNullOrEmpty(steamPath))
                    steamPath = ReadReg(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath");

                if (string.IsNullOrEmpty(steamPath)) return libs;

                // 注册表里可能是正斜杠（d:/program files/steam）
                steamPath = steamPath.Replace('/', '\\').TrimEnd('\\');
                if (!Directory.Exists(steamPath)) return libs;

                if (!libs.Contains(steamPath)) libs.Add(steamPath);

                // ---- 2. 解析 libraryfolders.vdf 拿其它库 ----
                string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (string p in ParseLibraryFolders(vdf))
                    {
                        string norm = p.Replace('/', '\\').TrimEnd('\\');
                        if (Directory.Exists(norm) && !libs.Contains(norm)) libs.Add(norm);
                    }
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("查找 Steam 库失败: " + ex.Message); } catch { }
            }
            return libs;
        }

        /// <summary>
        /// 从 libraryfolders.vdf 里抽出所有 `"path"  "D:\\..."` 的值。
        ///
        /// VDF 是 Valve 自有的键值格式，这里只需要 path 字段，
        /// 用正则足够（不值得为它写完整解析器）。
        /// </summary>
        public static List<string> ParseLibraryFolders(string vdfPath)
        {
            List<string> list = new List<string>();
            try
            {
                if (!File.Exists(vdfPath)) return list;

                string text = File.ReadAllText(vdfPath, Encoding.UTF8);

                // "path"		"D:\\Program Files\\steam"
                System.Text.RegularExpressions.MatchCollection ms =
                    System.Text.RegularExpressions.Regex.Matches(text,
                        "\"path\"\\s+\"([^\"]+)\"",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                foreach (System.Text.RegularExpressions.Match m in ms)
                {
                    string v = m.Groups[1].Value;
                    if (string.IsNullOrWhiteSpace(v)) continue;

                    // VDF 里的 \\ 是转义，还原成 \
                    v = v.Replace("\\\\", "\\");
                    if (!list.Contains(v)) list.Add(v);
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("解析 libraryfolders.vdf 失败: " + ex.Message); } catch { }
            }
            return list;
        }

        static string ReadReg(string key, string name)
        {
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(key, name, null);
                return v == null ? null : Convert.ToString(v);
            }
            catch { return null; }
        }

        // ========== 扫描 ==========

        /// <summary>
        /// 扫描指定根目录下的所有壁纸。
        ///
        /// 只列 video / scene；其它类型跳过。
        /// </summary>
        public static ScanResult Scan(List<string> roots)
        {
            ScanResult r = new ScanResult();
            try
            {
                if (roots == null || roots.Count == 0) return r;

                foreach (string root in roots)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                        r.Roots.Add(root);

                        foreach (string dir in Directory.GetDirectories(root))
                        {
                            try
                            {
                                Item it = ParseWallpaper(dir);
                                if (it != null) r.Items.Add(it);
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        try { Log.Warn("扫描壁纸目录失败: " + root + " -> " + ex.Message); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }
            return r;
        }

        /// <summary>自动查找 + 扫描。</summary>
        public static ScanResult ScanAuto()
        {
            return Scan(FindRoots());
        }

        // ========== 解析单个壁纸 ==========

        /// <summary>
        /// 解析一个壁纸文件夹。不可用（缺 project.json / 类型不支持 / 文件缺失）返回 null。
        /// </summary>
        public static Item ParseWallpaper(string dir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;

                string pj = Path.Combine(dir, "project.json");
                if (!File.Exists(pj)) return null;

                Dictionary<string, object> d = ReadJsonDict(pj);
                if (d == null) return null;

                string type = GetStr(d, "type").Trim().ToLowerInvariant();
                if (!IsSupportedType(type)) return null;

                Item it = new Item();
                it.Id = Path.GetFileName(dir.TrimEnd('\\'));
                it.Type = type;
                it.Dir = dir;
                it.Title = GetStr(d, "title");
                if (string.IsNullOrWhiteSpace(it.Title)) it.Title = it.Id;

                // ---- 主文件（仅 video）----
                if (type == "video")
                {
                    string f = GetStr(d, "file");
                    string safe = SafeCombine(dir, f);
                    if (safe == null) return null;       // 路径不安全
                    if (!File.Exists(safe)) return null; // 主文件缺失

                    it.MainFile = safe;
                    try { it.MainSize = new FileInfo(safe).Length; } catch { }
                }

                // ---- 预览图 ----
                string prev = GetStr(d, "preview");
                string prevSafe = SafeCombine(dir, prev);
                if (prevSafe != null && File.Exists(prevSafe))
                {
                    it.PreviewFile = prevSafe;
                    int pw, ph;
                    if (TryReadImageSize(prevSafe, out pw, out ph))
                    {
                        it.PreviewWidth = pw;
                        it.PreviewHeight = ph;
                    }
                }

                // scene 必须有预览图才能用（素材在 pkg 里读不出来）
                if (type == "scene" && string.IsNullOrEmpty(it.PreviewFile)) return null;

                return it;
            }
            catch { return null; }
        }

        /// <summary>类型是否受支持（忽略大小写）。</summary>
        public static bool IsSupportedType(string type)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(type)) return false;
                string t = type.Trim().ToLowerInvariant();
                foreach (string s in SupportedTypes)
                    if (t == s) return true;
                return false;
            }
            catch { return false; }
        }

        // ========== 按 ID 重新定位（引用模式，不复制文件）==========

        /// <summary>
        /// 定位结果：主文件路径 + 类型。
        /// </summary>
        public class Resolved
        {
            /// <summary>可直接读取的文件绝对路径（video 的 mp4 或 scene 的 preview）。</summary>
            public string File = "";

            /// <summary>壁纸文件夹绝对路径（提取 scene.pkg 用）。</summary>
            public string Dir = "";

            /// <summary>类型：video / scene。</summary>
            public string Type = "";

            /// <summary>失败原因（成功为 null）。</summary>
            public string Error = null;

            public bool Ok { get { return !string.IsNullOrEmpty(File) && FileExists(File); } }

            static bool FileExists(string p)
            {
                try { return !string.IsNullOrEmpty(p) && System.IO.File.Exists(p); }
                catch { return false; }
            }
        }

        /// <summary>
        /// 按「ID + 相对路径」在所有壁纸根目录里重新定位主文件。
        ///
        /// ── 为什么存相对路径 ──
        ///
        /// Steam 库可能换盘符、换目录、加新库。存绝对路径一旦变动就失效；
        /// 存「ID + 相对路径」，每次启动重新在找到的根目录里拼一次，稳得多。
        ///
        /// ── 安全 ──
        ///
        /// 相对路径同样过 `SafeCombine`（拒绝 `..` 和绝对路径）——
        /// 配置文件也可能被手改坏。
        /// </summary>
        public static Resolved ResolveById(string id, string relFile, string type)
        {
            Resolved r = new Resolved();
            try
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    r.Error = "没有选中壁纸";
                    return r;
                }

                r.Type = (type ?? "").Trim().ToLowerInvariant();

                foreach (string root in FindRoots())
                {
                    try
                    {
                        string dir = Path.Combine(root, id);
                        if (!Directory.Exists(dir)) continue;

                        // ---- 有相对路径 → 直接用（但仍要校验）----
                        if (!string.IsNullOrWhiteSpace(relFile))
                        {
                            string safe = SafeCombine(dir, relFile);
                            if (safe != null && File.Exists(safe))
                            {
                                r.File = safe;
                                r.Dir = dir;
                                return r;
                            }
                        }

                        // ---- 相对路径失效 → 重新解析 project.json ----
                        Item it = ParseWallpaper(dir);
                        if (it == null) continue;

                        if (it.IsScene)
                        {
                            if (!string.IsNullOrEmpty(it.PreviewFile))
                            {
                                r.File = it.PreviewFile;
                                r.Dir = dir;
                                r.Type = "scene";
                                return r;
                            }
                        }
                        else if (!string.IsNullOrEmpty(it.MainFile))
                        {
                            r.File = it.MainFile;
                                r.Dir = dir;
                            r.Type = "video";
                            return r;
                        }
                    }
                    catch { }
                }

                r.Error = "壁纸已不可用（目录不存在或文件缺失）";
                return r;
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                return r;
            }
        }

        /// <summary>取相对路径（相对壁纸文件夹），用于保存到配置。</summary>
        public static string RelativeTo(string wallpaperDir, string file)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(wallpaperDir) || string.IsNullOrWhiteSpace(file))
                    return "";

                string d = Path.GetFullPath(wallpaperDir).TrimEnd('\\') + "\\";
                string f = Path.GetFullPath(file);

                if (f.StartsWith(d, StringComparison.OrdinalIgnoreCase))
                    return f.Substring(d.Length);

                return "";
            }
            catch { return ""; }
        }

        // ========== 路径安全 ==========

        /// <summary>
        /// 把 project.json 里的相对路径安全地拼到壁纸目录下。
        ///
        /// **拒绝**：绝对路径、含 `..` 的路径、解析后跑到目录外的路径。
        /// 不安全返回 null。
        /// </summary>
        public static string SafeCombine(string baseDir, string rel)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(baseDir)) return null;
                if (string.IsNullOrWhiteSpace(rel)) return null;

                string r = rel.Trim().Replace('/', '\\');

                // 绝对路径直接拒绝
                if (r.Length >= 2 && r[1] == ':') return null;
                if (r.StartsWith("\\", StringComparison.Ordinal)) return null;

                // 含 .. 的路径段拒绝
                foreach (string seg in r.Split('\\'))
                    if (seg == "..") return null;

                string baseFull;
                try { baseFull = Path.GetFullPath(baseDir); }
                catch { return null; }

                string full;
                try { full = Path.GetFullPath(Path.Combine(baseFull, r)); }
                catch { return null; }

                // 最终必须落在 baseDir 内（防止符号链接等间接逃逸）
                string baseWithSep = baseFull.TrimEnd('\\') + "\\";
                if (!full.StartsWith(baseWithSep, StringComparison.OrdinalIgnoreCase)) return null;

                return full;
            }
            catch { return null; }
        }

        // ========== 辅助 ==========

        static Dictionary<string, object> ReadJsonDict(string path)
        {
            try
            {
                string json = JsonIO.Read(path);
                if (string.IsNullOrEmpty(json)) return null;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                return ser.Deserialize<Dictionary<string, object>>(json);
            }
            catch { return null; }
        }

        static string GetStr(Dictionary<string, object> d, string key)
        {
            try
            {
                object v;
                if (d == null || !d.TryGetValue(key, out v) || v == null) return "";
                return Convert.ToString(v) ?? "";
            }
            catch { return ""; }
        }

        /// <summary>读图片像素尺寸（只读头部，不解码整张图）。</summary>
        public static bool TryReadImageSize(string path, out int w, out int h)
        {
            w = 0;
            h = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

                // 用 BitmapFrame 读元数据，不加载像素
                using (FileStream fs = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite))
                {
                    BitmapFrameInfo info = BitmapFrameInfo.Read(fs);
                    if (info == null) return false;
                    w = info.PixelWidth;
                    h = info.PixelHeight;
                    return w > 0 && h > 0;
                }
            }
            catch { return false; }
        }

        /// <summary>图片尺寸信息（避免直接依赖 WPF 类型）。</summary>
        class BitmapFrameInfo
        {
            public int PixelWidth;
            public int PixelHeight;

            public static BitmapFrameInfo Read(Stream s)
            {
                try
                {
                    var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
                        s,
                        System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                        System.Windows.Media.Imaging.BitmapCacheOption.None);
                    if (frame == null) return null;

                    BitmapFrameInfo i = new BitmapFrameInfo();
                    i.PixelWidth = frame.PixelWidth;
                    i.PixelHeight = frame.PixelHeight;
                    return i;
                }
                catch { return null; }
            }
        }
    }
}
