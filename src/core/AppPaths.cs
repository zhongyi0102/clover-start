using System;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 应用「路径」的形态判断与归一化。
    ///
    /// **为什么需要这个类**：
    /// 加了商店应用（UWP）之后，"路径"不再总是文件系统路径 ——
    /// 商店应用用的是 `shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App`
    /// 这种 Shell 命名空间路径。
    ///
    /// 凡是做 `File.Exists` / `Path.GetFullPath` / 死链清理 / 路径大小写比较的地方，
    /// 都必须先问一句"这是不是 Shell 路径"，否则商店应用会被当成死链清掉、
    /// 或者 Path.GetFullPath 直接抛异常。
    ///
    /// 全部判断集中在这里，避免同一个判断散落二十处、改一处漏一处。
    /// </summary>
    static class AppPaths
    {
        /// <summary>来源标识（写进 AppItem.Source 和 json 的 source 字段）。</summary>
        public const string SourceStartMenu = "StartMenu";
        public const string SourceRegistry = "Registry";
        public const string SourceStore = "Store";
        public const string SourcePortable = "Portable";

        /// <summary>商店应用路径的前缀。</summary>
        public const string ShellAppsPrefix = @"shell:AppsFolder\";

        /// <summary>
        /// 是否是 Shell 命名空间路径（非文件系统路径）。
        ///
        /// 目前只有商店应用会用，但判断写成通用形式：
        /// 任何 `xxx:` 开头且不是盘符的（如 `shell:`、`::{CLSID}`）都算。
        /// </summary>
        public static bool IsShellPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                string p = path.Trim();

                if (p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return true;
                if (p.StartsWith("::{", StringComparison.Ordinal)) return true;

                // 盘符路径（C:\...）不算
                if (p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0])) return false;

                // 其它带冒号的（如 ms-settings:）也算 Shell 路径
                int colon = p.IndexOf(':');
                return colon > 0 && colon < 8;
            }
            catch { return false; }
        }

        /// <summary>从商店应用路径里取出 AppUserModelID。</summary>
        public static string ExtractAumid(string shellPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(shellPath)) return null;
                if (!IsShellPath(shellPath)) return null;

                string p = shellPath.Trim();
                int idx = p.IndexOf(ShellAppsPrefix, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return null;
                string aumid = p.Substring(idx + ShellAppsPrefix.Length).Trim();
                return string.IsNullOrEmpty(aumid) ? null : aumid;
            }
            catch { return null; }
        }

        /// <summary>由 AppUserModelID 构造商店应用路径。</summary>
        public static string FromAumid(string aumid)
        {
            if (string.IsNullOrWhiteSpace(aumid)) return null;
            return ShellAppsPrefix + aumid.Trim();
        }

        /// <summary>
        /// 路径归一化（用于去重比较）。
        ///
        /// 规则：
        ///   · Shell 路径 → 原样小写（AUMID 大小写不敏感）
        ///   · 文件路径   → 去引号、Path.GetFullPath 展开、小写
        ///
        /// 失败时回退到"去引号 + 小写"，绝不抛异常。
        /// </summary>
        public static string Normalize(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return "";
                string p = path.Trim().Trim('"').Trim();

                if (IsShellPath(p)) return p.ToLowerInvariant();

                try { p = Path.GetFullPath(p); } catch { }
                return p.ToLowerInvariant();
            }
            catch { return (path ?? "").ToLowerInvariant(); }
        }

        /// <summary>
        /// 「路径」是否存在（文件 / Shell 命名空间）。
        ///
        /// Shell 路径无法用 File.Exists 验证 —— 直接当作"存在"，
        /// 让上层逻辑不要把它清掉。
        /// </summary>
        public static bool Exists(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                if (IsShellPath(path)) return true;
                return File.Exists(path);
            }
            catch { return false; }
        }

        /// <summary>
        /// 取用于显示的文件名（Shell 路径返回 AUMID 的最后一段）。
        /// </summary>
        public static string DisplayFileName(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return "";
                if (IsShellPath(path))
                {
                    string aumid = ExtractAumid(path);
                    if (!string.IsNullOrEmpty(aumid))
                    {
                        int bang = aumid.IndexOf('!');
                        return bang > 0 ? aumid.Substring(0, bang) : aumid;
                    }
                    return path;
                }
                return Path.GetFileName(path);
            }
            catch { return path ?? ""; }
        }
    }
}
