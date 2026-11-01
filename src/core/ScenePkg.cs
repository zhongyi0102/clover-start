using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 从 Wallpaper Engine 的 `scene.pkg` 里提取真实贴图。
    ///
    /// ── 为什么需要它 ──
    ///
    /// scene 类壁纸的素材全部打包在 `scene.pkg` 里（私有格式）。之前只能拿
    /// 同目录的 `preview.*` 当静态背景 —— 那是创意工坊的**缩略图**，
    /// 实测 26 个 scene 里最大 1024×1024，最小的只有 160×160。
    /// 160px 铺到 1000px 宽的面板上要放大 6~7 倍，满屏马赛克。
    ///
    /// 而 `scene.pkg` 里的真实贴图是 2880×5120 级别 —— 高 32 倍。
    ///
    /// ── 格式（实测 WE 2.x，PKGV0024）──
    ///
    /// <code>
    /// 头部：
    ///   u32 magicLen | magicLen 字节 "PKGV0024\0" | u32 entryCount
    ///   然后每项：
    ///     u32 nameLen | nameLen 字节 name\0 | u32 offset | u32 length
    ///
    /// 偏移基准：**头部结束位置 + offset**
    ///
    /// .tex 文件内部（TEXV0005 / TEXI0001 / TEXB0004）：
    ///   实测 mip0 数据块直接是**标准 PNG**（89 50 4E 47 ...），
    ///   不需要解码 DXT/BC 等私有压缩 —— 扫到 PNG 签名后整段就是合法图片。
    /// </code>
    ///
    /// ── 安全 ──
    ///
    /// 这是**外部数据**：所有偏移/长度都做边界校验，任何异常都返回 null，
    /// 由调用方回退到 preview 缩略图。绝不因为一个坏包让面板打不开。
    ///
    /// 只读，不修改 WE 目录下任何文件。
    /// </summary>
    static class ScenePkg
    {
        /// <summary>PNG 签名。</summary>
        static readonly byte[] PngSig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>包内一个条目。</summary>
        class Entry
        {
            public string Name;
            public uint Offset;
            public uint Length;
        }

        /// <summary>
        /// 从壁纸目录里找 `scene.pkg`（大小写不敏感）。
        /// </summary>
        public static string FindPackage(string wallpaperDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(wallpaperDir)) return null;
                if (!Directory.Exists(wallpaperDir)) return null;

                foreach (string f in Directory.GetFiles(wallpaperDir))
                {
                    if (string.Equals(Path.GetFileName(f), "scene.pkg",
                        StringComparison.OrdinalIgnoreCase))
                        return f;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 解析包头，返回条目表。失败返回 null。
        /// </summary>
        static List<Entry> ReadEntries(byte[] b, out int headerEnd)
        {
            headerEnd = 0;
            try
            {
                if (b == null || b.Length < 16) return null;

                int p = 0;

                uint magicLen = BitConverter.ToUInt32(b, p);
                p += 4;
                if (magicLen == 0 || magicLen > 64 || p + (int)magicLen > b.Length) return null;

                string magic = Encoding.ASCII.GetString(b, p, (int)magicLen).TrimEnd('\0');
                p += (int)magicLen;

                // 只认 PKGV 开头；其它版本保守放弃（回退缩略图）
                if (!magic.StartsWith("PKGV", StringComparison.Ordinal)) return null;

                uint count = BitConverter.ToUInt32(b, p);
                p += 4;

                // 条目数上限：正常包 10~200 项，防坏数据撑爆内存
                if (count == 0 || count > 20000) return null;

                List<Entry> list = new List<Entry>((int)count);

                for (uint i = 0; i < count; i++)
                {
                    if (p + 4 > b.Length) return null;

                    uint nameLen = BitConverter.ToUInt32(b, p);
                    p += 4;
                    if (nameLen == 0 || nameLen > 4096 || p + (int)nameLen > b.Length) return null;

                    string name = Encoding.UTF8.GetString(b, p, (int)nameLen).TrimEnd('\0');
                    p += (int)nameLen;

                    if (p + 8 > b.Length) return null;

                    uint off = BitConverter.ToUInt32(b, p); p += 4;
                    uint len = BitConverter.ToUInt32(b, p); p += 4;

                    list.Add(new Entry { Name = name, Offset = off, Length = len });
                }

                headerEnd = p;
                return list;
            }
            catch { return null; }
        }

        /// <summary>
        /// 提取最大的一张真实贴图（PNG 字节）。失败返回 null。
        ///
        /// ── 选哪张 ──
        ///
        /// 优先 `materials/*.tex` 里**体积最大**的那个（通常是主背景）。
        /// `*_depth.tex` 是深度图（灰度），不能当背景，跳过。
        /// </summary>
        public static byte[] ExtractMainTexture(string packagePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(packagePath)) return null;
                if (!File.Exists(packagePath)) return null;

                byte[] b = File.ReadAllBytes(packagePath);
                int headerEnd;
                List<Entry> entries = ReadEntries(b, out headerEnd);
                if (entries == null) return null;

                // 挑最大的非 depth .tex
                Entry best = null;
                foreach (Entry e in entries)
                {
                    if (e == null || string.IsNullOrEmpty(e.Name)) continue;
                    if (!e.Name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.Name.IndexOf("_depth", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    if (best == null || e.Length > best.Length) best = e;
                }

                if (best == null) return null;
                return ExtractPng(b, headerEnd, best);
            }
            catch { return null; }
        }

        /// <summary>从一个条目里切出 PNG。</summary>
        static byte[] ExtractPng(byte[] b, int headerEnd, Entry e)
        {
            try
            {
                long startL = (long)headerEnd + e.Offset;
                long endL = startL + e.Length;

                // 边界校验：外部数据不可信
                if (startL < 0 || endL > b.Length || startL >= endL) return null;

                int start = (int)startL;
                int end = (int)endL;

                // 找 PNG 签名
                int pngStart = -1;
                for (int i = start; i + PngSig.Length <= end; i++)
                {
                    bool hit = true;
                    for (int k = 0; k < PngSig.Length; k++)
                    {
                        if (b[i + k] != PngSig[k]) { hit = false; break; }
                    }
                    if (hit) { pngStart = i; break; }
                }
                if (pngStart < 0) return null;

                // 找 IEND 结尾（"IEND" + 4 字节 CRC = 8 字节）
                int pngEnd = -1;
                for (int i = pngStart; i + 8 <= end; i++)
                {
                    if (b[i] == 0x49 && b[i + 1] == 0x45 && b[i + 2] == 0x4E && b[i + 3] == 0x44)
                    {
                        pngEnd = i + 8;
                        break;
                    }
                }
                if (pngEnd < 0 || pngEnd <= pngStart) return null;

                int size = pngEnd - pngStart;
                // 上限 64 MB：正常贴图 15~25 MB，防坏数据
                if (size <= 0 || size > 64 * 1024 * 1024) return null;

                byte[] png = new byte[size];
                Array.Copy(b, pngStart, png, 0, size);
                return png;
            }
            catch { return null; }
        }

        /// <summary>
        /// 提取并落盘成缓存文件，返回缓存路径。
        ///
        /// 为什么要落盘：贴图 15~25 MB，每次打开面板都重新解析 + 解码太慢。
        /// 缓存到 `config\background\scene-<id>.png`，下次直接读。
        /// 缓存文件名带源包的大小与时间戳哈希，WE 更新壁纸后自动失效。
        /// </summary>
        public static string ExtractToCache(string baseDir, string wallpaperId, string packagePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(baseDir) || string.IsNullOrWhiteSpace(packagePath))
                    return null;
                if (!File.Exists(packagePath)) return null;

                string dir = Path.Combine(baseDir, "config", "background");
                Directory.CreateDirectory(dir);

                FileInfo fi = new FileInfo(packagePath);
                string stamp = fi.Length + "-" + fi.LastWriteTimeUtc.Ticks;
                string safeId = SanitizeId(wallpaperId);
                string target = Path.Combine(dir, "scene-" + safeId + ".png");
                string stampFile = target + ".stamp";

                // 缓存命中：stamp 一致就直接用
                if (File.Exists(target) && File.Exists(stampFile))
                {
                    try
                    {
                        if (File.ReadAllText(stampFile).Trim() == stamp) return target;
                    }
                    catch { }
                }

                byte[] png = ExtractMainTexture(packagePath);
                if (png == null || png.Length == 0) return null;

                // 先写临时文件再原子替换，避免半成品被读到
                string tmp = target + ".importing-" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(tmp, png);

                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmp, target);
                }
                catch
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    return null;
                }

                try { File.WriteAllText(stampFile, stamp); } catch { }

                Log.Info("已从 scene.pkg 提取贴图: " + Path.GetFileName(target)
                    + "  " + (png.Length / 1024 / 1024) + " MB");
                return target;
            }
            catch (Exception ex)
            {
                try { Log.Warn("提取 scene.pkg 贴图失败: " + ex.Message); } catch { }
                return null;
            }
        }

        /// <summary>ID 只留字母数字，防路径穿越。</summary>
        static string SanitizeId(string id)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(id)) return "unknown";
                StringBuilder sb = new StringBuilder();
                foreach (char c in id)
                {
                    if (char.IsLetterOrDigit(c)) sb.Append(c);
                }
                string s = sb.ToString();
                return s.Length == 0 ? "unknown" : s;
            }
            catch { return "unknown"; }
        }
    }
}
