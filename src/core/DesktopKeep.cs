using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 桌面保留名单：记录用户想留在桌面的快捷方式。
    ///
    /// ── key 规则 ──
    ///
    ///   · 能解析出目标 → 规范化目标路径，如 `c:\program files\...\chrome.exe`
    ///   · 解析不出（网址 / UWP）→ `name:文件名.url`
    ///
    /// 规范化（按需求 3.1：忽略大小写和 `\` `/` 差异）：
    ///   GetFullPath → 统一 `\` → 去尾部斜杠（盘符根保留）→ 小写
    ///
    /// ── 自动失效 ──
    ///
    /// 用户自己把保留名单里的快捷方式移入回收站（或永久删除）后，
    /// 该条目自动从名单移除 —— 否则名单会越积越长，且用户下次真的
    /// 需要它被自动清理时反而被"幽灵条目"保护。
    ///
    /// 判定方式见 `Prune`：名单里每条都反查它对应的快捷方式是否还在。
    /// 对不上任何现存快捷方式的条目，删掉。
    /// </summary>
    public static class DesktopKeep
    {
        /// <summary>解析不出目标时的 key 前缀。</summary>
        public const string NamePrefix = "name:";

        // ========== 路径规范化 ==========

        /// <summary>
        /// 规范化路径成保留名单的 key。
        ///
        /// 步骤：GetFullPath → 统一 `\` → 去尾部斜杠（盘符根保留）→ 小写。
        /// 失败时退化为原字符串的简单清理（不抛异常）。
        /// </summary>
        public static string NormalizeKey(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return "";

                string p = path.Trim();

                // 1. 统一分隔符（先做，GetFullPath 才能正确处理混用）
                p = p.Replace('/', '\\');

                // 2. 展开成绝对路径
                try { p = Path.GetFullPath(p); }
                catch { /* 非法路径：保持原样继续清理 */ }

                // 3. 去尾部斜杠，但盘符根（C:\）保留
                while (p.Length > 3 && p.EndsWith("\\", StringComparison.Ordinal))
                    p = p.Substring(0, p.Length - 1);

                // 4. 小写
                return p.ToLowerInvariant();
            }
            catch
            {
                try { return (path ?? "").Trim().Replace('/', '\\').ToLowerInvariant(); }
                catch { return ""; }
            }
        }

        /// <summary>构造「解析不出目标」时的 key。</summary>
        public static string NameKey(string lnkPath)
        {
            try
            {
                string n = Path.GetFileName(lnkPath);
                if (string.IsNullOrWhiteSpace(n)) return "";
                return NamePrefix + n.ToLowerInvariant();
            }
            catch { return ""; }
        }

        // ========== 目标解析 ==========

        /// <summary>
        /// 解析快捷方式的目标路径。
        ///
        ///   · `.lnk` → WScript.Shell 的 CreateShortcut().TargetPath
        ///   · `.url` → 读 `[InternetShortcut]` 段的 `URL=` 行
        ///
        /// 解析失败（UWP、损坏的 .lnk、无 URL 的 .url）→ 返回 null。
        /// </summary>
        public static string ResolveTarget(string lnkPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(lnkPath)) return null;
                if (!File.Exists(lnkPath)) return null;

                string ext = (Path.GetExtension(lnkPath) ?? "").ToLowerInvariant();

                if (ext == ".url") return ReadUrlTarget(lnkPath);
                if (ext == ".lnk") return ReadLnkTarget(lnkPath);

                return null;
            }
            catch { return null; }
        }

        /// <summary>读 `.url` 文件里的 URL。</summary>
        static string ReadUrlTarget(string urlPath)
        {
            try
            {
                foreach (string line in File.ReadAllLines(urlPath, Encoding.Default))
                {
                    string t = (line ?? "").Trim();
                    if (t.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                        return t.Substring(4).Trim();
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>读 `.lnk` 的 TargetPath（走 WScript.Shell COM）。</summary>
        static string ReadLnkTarget(string lnkPath)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;

                shell = Activator.CreateInstance(t);
                if (shell == null) return null;

                shortcut = t.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell,
                    new object[] { lnkPath });
                if (shortcut == null) return null;

                object target = shortcut.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.GetProperty, null, shortcut, null);

                string s = target as string;
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            catch { return null; }
            finally
            {
                try { if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut); } catch { }
                try { if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); } catch { }
            }
        }

        // ========== key 计算 ==========

        /// <summary>
        /// 给一个快捷方式算出它的保留名单 key。
        /// 能解析目标 → 目标路径 key；否则 → `name:文件名`。
        /// </summary>
        public static string KeyOf(string lnkPath)
        {
            try
            {
                string target = ResolveTarget(lnkPath);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    string k = NormalizeKey(target);
                    if (k.Length > 0) return k;
                }
                return NameKey(lnkPath);
            }
            catch { return ""; }
        }

        // ========== 名单操作 ==========

        /// <summary>某个快捷方式是否在保留名单里。</summary>
        public static bool IsKept(List<string> keepTargets, string lnkPath)
        {
            try
            {
                if (keepTargets == null || keepTargets.Count == 0) return false;

                string key = KeyOf(lnkPath);
                if (string.IsNullOrEmpty(key)) return false;

                foreach (string k in keepTargets)
                {
                    if (string.IsNullOrEmpty(k)) continue;
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>加入保留名单（去重）。返回是否新增。</summary>
        public static bool Add(List<string> keepTargets, string lnkPath)
        {
            try
            {
                if (keepTargets == null) return false;

                string key = KeyOf(lnkPath);
                if (string.IsNullOrEmpty(key)) return false;

                foreach (string k in keepTargets)
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return false;

                keepTargets.Add(key);
                return true;
            }
            catch { return false; }
        }

        /// <summary>按 key 直接加入（用于「保留在桌面」先登记后创建的场景）。</summary>
        public static bool AddKey(List<string> keepTargets, string key)
        {
            try
            {
                if (keepTargets == null || string.IsNullOrEmpty(key)) return false;
                foreach (string k in keepTargets)
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return false;
                keepTargets.Add(key);
                return true;
            }
            catch { return false; }
        }

        /// <summary>从保留名单移除。返回是否真的删掉了。</summary>
        public static bool Remove(List<string> keepTargets, string lnkPath)
        {
            try
            {
                if (keepTargets == null) return false;

                string key = KeyOf(lnkPath);
                if (string.IsNullOrEmpty(key)) return false;

                int before = keepTargets.Count;
                keepTargets.RemoveAll(delegate(string k)
                { return string.Equals(k, key, StringComparison.OrdinalIgnoreCase); });
                return keepTargets.Count != before;
            }
            catch { return false; }
        }

        /// <summary>按 key 直接移除。</summary>
        public static bool RemoveKey(List<string> keepTargets, string key)
        {
            try
            {
                if (keepTargets == null || string.IsNullOrEmpty(key)) return false;
                int before = keepTargets.Count;
                keepTargets.RemoveAll(delegate(string k)
                { return string.Equals(k, key, StringComparison.OrdinalIgnoreCase); });
                return keepTargets.Count != before;
            }
            catch { return false; }
        }

        // ========== 自动失效（跟随用户手动删除）==========

        /// <summary>
        /// 清理保留名单里**已经不存在于桌面**的条目。
        ///
        /// ── 为什么需要 ──
        ///
        /// 用户自己把保留名单里的快捷方式删了（移入回收站或永久删除），
        /// 名单里的 key 就成了幽灵条目。不清理的话：
        ///   · 名单越积越长，设置界面里一堆对不上任何文件的行
        ///   · 用户以后重新装同一个软件、桌面又出现同名快捷方式时，
        ///     会被这个幽灵 key 意外保护（用户早就删了，不该再保护）
        ///
        /// ── 判定方式 ──
        ///
        /// 扫两个桌面目录里现有的全部 .lnk/.url，算出它们的 key 集合。
        /// 名单里**不在这个集合里**的条目 → 删掉。
        ///
        /// 注意：只在**能成功枚举桌面**时才清理。如果两个桌面目录都读不出来
        /// （权限问题、目录不存在），一律不清理 —— 否则会把整个名单误删光。
        /// </summary>
        /// <returns>被移除的条目数</returns>
        public static int Prune(List<string> keepTargets)
        {
            try
            {
                if (keepTargets == null || keepTargets.Count == 0) return 0;

                List<string> desks = DesktopDirs();
                if (desks.Count == 0) return 0;

                // 收集现存快捷方式的 key
                HashSet<string> alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool anyReadable = false;

                foreach (string dir in desks)
                {
                    try
                    {
                        if (!Directory.Exists(dir)) continue;

                        string[] files;
                        try { files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly); }
                        catch { continue; }

                        anyReadable = true;   // 至少有一个目录能读

                        foreach (string f in files)
                        {
                            string ext = "";
                            try { ext = (Path.GetExtension(f) ?? "").ToLowerInvariant(); }
                            catch { continue; }

                            if (ext != ".lnk" && ext != ".url") continue;

                            string k = KeyOf(f);
                            if (!string.IsNullOrEmpty(k)) alive.Add(k);
                        }
                    }
                    catch { }
                }

                // ★ 一个目录都读不出来 → 不清理（避免误删整个名单）
                if (!anyReadable) return 0;

                int before = keepTargets.Count;
                keepTargets.RemoveAll(delegate(string k)
                {
                    if (string.IsNullOrEmpty(k)) return true;   // 空条目顺手清掉
                    return !alive.Contains(k);
                });

                int removed = before - keepTargets.Count;
                if (removed > 0)
                {
                    try { Log.Warn("保留名单：清理了 " + removed + " 个已不存在的条目"); }
                    catch { }
                }
                return removed;
            }
            catch { return 0; }
        }

        // ========== 桌面目录 ==========

        /// <summary>
        /// 两个桌面目录：用户桌面 + 公共桌面。
        ///
        /// 用 Environment.GetFolderPath 取，不硬编码路径
        /// （用户目录可能被重定向到 OneDrive）。
        /// </summary>
        public static List<string> DesktopDirs()
        {
            List<string> list = new List<string>();
            try
            {
                string user = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrWhiteSpace(user)) list.Add(user);
            }
            catch { }

            try
            {
                string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                if (!string.IsNullOrWhiteSpace(common) && !list.Contains(common)) list.Add(common);
            }
            catch { }

            return list;
        }

        /// <summary>这个路径是否在公共桌面（需要管理员权限）。</summary>
        public static bool IsPublicDesktop(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                if (string.IsNullOrWhiteSpace(common)) return false;

                string p = NormalizeKey(path);
                string c = NormalizeKey(common);
                return p.StartsWith(c + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
