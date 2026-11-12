using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace Clover
{
    public enum DesktopContentKind { NonApplication, Application, Unknown }

    /// <summary>按真实身份区分应用与桌面内容；无法识别时不自动搬动。</summary>
    public static class DesktopContentRules
    {
        public static DesktopContentKind Classify(string path)
        {
            return Classify(path, 0);
        }

        static DesktopContentKind Classify(string path, int depth)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return DesktopContentKind.Unknown;
                if (depth > 4) return DesktopContentKind.Unknown;
                if (Directory.Exists(path)) return DesktopContentKind.NonApplication;
                string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
                if (ext == ".lnk")
                {
                    string target = ShortcutProperty(path, "TargetPath");
                    if (string.IsNullOrWhiteSpace(target)) return DesktopContentKind.Unknown;
                    if (Directory.Exists(target)) return DesktopContentKind.NonApplication;
                    if (!File.Exists(target)) return DesktopContentKind.Unknown;
                    string arguments = ShortcutProperty(path, "Arguments");
                    if (HasDocumentArgument(arguments)) return DesktopContentKind.NonApplication;
                    return Classify(target, depth + 1);
                }
                if (ext == ".url")
                {
                    if (!File.Exists(path)) return DesktopContentKind.Unknown;
                    foreach (string line in File.ReadAllLines(path))
                    {
                        if (!line.Trim().StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) continue;
                        Uri uri;
                        if (!Uri.TryCreate(line.Trim().Substring(4).Trim(), UriKind.Absolute, out uri))
                            return DesktopContentKind.Unknown;
                        if (uri.Scheme == "http" || uri.Scheme == "https") return DesktopContentKind.NonApplication;
                        if (uri.Scheme == "steam" || uri.Scheme == "ms-windows-store") return DesktopContentKind.Application;
                        return DesktopContentKind.Unknown;
                    }
                    return DesktopContentKind.Unknown;
                }
                if (ext == ".msi" || ext == ".msix" || ext == ".appx") return DesktopContentKind.NonApplication;
                if (ext == ".exe")
                {
                    // 仅采用明确的安装程序身份；模糊情况留原处供用户确认。
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (string.Equals(name, "setup", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("-setup", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("-installer", StringComparison.OrdinalIgnoreCase))
                        return DesktopContentKind.NonApplication;
                    return DesktopContentKind.Application;
                }
                foreach (string appExt in new string[] { ".com", ".scr", ".pif", ".appref-ms", ".msc", ".cpl" })
                    if (ext == appExt) return DesktopContentKind.Application;
                return DesktopContentKind.NonApplication;
            }
            catch { return DesktopContentKind.Unknown; }
        }

        public static string ShortcutProperty(string path, string property)
        {
            object shell = null, shortcut = null;
            try
            {
                if (!File.Exists(path)) return null;
                Type type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) return null;
                shell = Activator.CreateInstance(type);
                shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                return Convert.ToString(shortcut.GetType().InvokeMember(property, BindingFlags.GetProperty, null, shortcut, null));
            }
            catch { return null; }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }

        // 只接受明确的文档/网页参数，不能把任意应用命令参数当成文档。
        public static bool HasDocumentArgument(string arguments)
        {
            foreach (string token in ArgumentTokens(arguments))
            {
                Uri uri;
                if (Uri.TryCreate(token, UriKind.Absolute, out uri)
                    && (uri.Scheme == "http" || uri.Scheme == "https")) return true;
                string ext;
                try { ext = Path.GetExtension(token).ToLowerInvariant(); } catch { continue; }
                if (Array.IndexOf(new[] { ".doc", ".docx", ".docm", ".ppt", ".pptx", ".pptm",
                    ".xls", ".xlsx", ".xlsm", ".csv", ".pdf", ".txt", ".rtf", ".html", ".htm" }, ext) < 0) continue;
                // 包括已失效的文档入口：保留它，绝不能当应用删除。
                if (token.IndexOf(':') >= 0 || token.IndexOf('\\') >= 0 || token.IndexOf('/') >= 0
                    || !token.StartsWith("-", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        static IEnumerable<string> ArgumentTokens(string arguments)
        {
            bool quoted = false;
            System.Text.StringBuilder token = new System.Text.StringBuilder();
            foreach (char ch in arguments ?? "")
            {
                if (ch == '"') { quoted = !quoted; continue; }
                if (char.IsWhiteSpace(ch) && !quoted)
                {
                    if (token.Length > 0) { yield return token.ToString(); token.Length = 0; }
                }
                else token.Append(ch);
            }
            if (token.Length > 0) yield return token.ToString();
        }

        public static string ApplicationIdentity(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return null;
                if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return path.Trim().ToLowerInvariant();
                string target = path, args = "";
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    target = ShortcutProperty(path, "TargetPath");
                    args = ShortcutProperty(path, "Arguments") ?? "";
                    if (string.IsNullOrWhiteSpace(target) || HasDocumentArgument(args)) return null;
                }
                if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                {
                    if (Classify(path) != DesktopContentKind.Application) return null;
                    foreach (string line in File.ReadAllLines(path))
                        if (line.Trim().StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return "url:" + line.Trim().Substring(4).Trim();
                    return null;
                }
                if (!File.Exists(target)) return null;
                return Path.GetFullPath(target).Replace('/', '\\').TrimEnd('\\').ToLowerInvariant() + "\n" + args.Trim();
            }
            catch { return null; }
        }

        public static bool IsTemporary(string path)
        {
            string name = Path.GetFileName(path) ?? "";
            string ext = Path.GetExtension(path) ?? "";
            return name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith("~", StringComparison.Ordinal)
                || ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".part", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".crdownload", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".download", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>交接前后核对入口内容；失败时返回空，调用方必须保留源。</summary>
        public static string Fingerprint(string path)
        {
            try
            {
                using (FileStream stream = File.OpenRead(path))
                using (System.Security.Cryptography.SHA256 hash = System.Security.Cryptography.SHA256.Create())
                    return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
            }
            catch { return null; }
        }
    }
}
