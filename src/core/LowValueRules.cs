using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 「低价值条目」识别规则。
    ///
    /// ── 什么是低价值 ──
    ///
    /// 卸载器、帮助文档、许可协议、更新日志、语言设置……这些不是"要用的应用"，
    /// 但在开始菜单里和真应用平铺在一起，把「全部」页挤得很长。
    /// 识别出来后**沉底 + 变淡**，而不是隐藏 —— 用户偶尔还是要找它们。
    ///
    /// ── 规则集中在这个文件 ──
    ///
    /// 所有关键词、扩展名、排除逻辑都在这里，增删规则只改这一处。
    ///
    /// ── 不误伤 ──
    ///
    /// 关键约束：**只匹配快捷方式名称，并排除「可执行文件名 == 应用名」的主程序**。
    /// 否则 `Geek Uninstaller`（本身就是个卸载工具，名字含 Uninstall）
    /// 会被误判为低价值。
    /// </summary>
    public static class LowValueRules
    {
        /// <summary>
        /// 名称关键词。命中任一 → 低价值。
        ///
        /// **不区分大小写**。中英文并列写，方便对照维护。
        /// 加规则就往这里加，不要改判定逻辑。
        /// </summary>
        public static readonly string[] NameKeywords = new string[]
        {
            // 卸载
            "卸载", "Uninstall", "卸载程序", "Uninstaller", "Remove",

            // 帮助 / 说明
            "帮助", "Help", "说明", "Readme", "ReadMe", "手册", "Manual",
            "文档", "Documentation", "Docs", "指南", "Guide", "教程", "Tutorial",

            // 更新日志
            "更新日志", "Release Notes", "ReleaseNotes", "更新说明", "Changelog",
            "Change Log", "What's New", "新功能介绍",

            // 许可
            "许可", "License", "Licence", "EULA", "授权", "协议", "Agreement",
            "Terms", "使用条款",

            // 修复
            "修复", "Repair", "修复工具", "Fix",

            // 语言
            "语言首选项", "Language Preferences", "Language", "语言设置", "语言",
        };

        /// <summary>
        /// 目标扩展名。快捷方式指向这些 → 低价值（网址链接、说明书）。
        ///
        /// 不含 `.exe` / `.lnk`：那些是真正的程序入口。
        /// </summary>
        public static readonly string[] TargetExtensions = new string[]
        {
            ".url",   // 网址链接
            ".txt",   // 纯文本说明
            ".pdf",   // PDF 手册
            ".chm",   // Windows 帮助文件
            ".html",  // 本地网页
            ".htm",
        };

        /// <summary>
        /// 判定一条应用是否低价值。
        /// </summary>
        /// <param name="name">应用显示名</param>
        /// <param name="targetPath">快捷方式目标路径（可为 null）</param>
        /// <param name="exeFileName">目标可执行文件名（不含路径，可为 null）</param>
        public static bool IsLowValue(string name, string targetPath, string exeFileName)
        {
            try
            {
                // ---- 1. 失效快捷方式：目标不存在 ----
                //
                // 但「商店应用」没有文件路径（Path 是 shell:AppsFolder\...），
                // 那种不算失效 —— 由调用方保证 targetPath 为 null 时跳过这一条。
                if (!string.IsNullOrEmpty(targetPath))
                {
                    bool exists = false;
                    try { exists = File.Exists(targetPath) || Directory.Exists(targetPath); }
                    catch { exists = false; }

                    if (!exists) return true;   // 目标不存在的失效快捷方式
                }

                // ---- 2. 目标是 .url / .txt / .pdf / .chm / .html ----
                if (!string.IsNullOrEmpty(targetPath))
                {
                    string ext = "";
                    try { ext = (Path.GetExtension(targetPath) ?? "").ToLowerInvariant(); }
                    catch { }

                    foreach (string e in TargetExtensions)
                    {
                        if (ext == e) return true;
                    }
                }

                // ---- 3. 名称关键词 ----
                if (string.IsNullOrWhiteSpace(name)) return false;

                // ★ 不误伤：如果「可执行文件名（去扩展名）== 应用名」，
                //   说明这是个主程序本身（如 Geek Uninstaller 的 geek.exe），
                //   名称里的 Uninstall 只是产品名的一部分，不是"卸载入口"。
                if (IsMainExecutable(name, exeFileName)) return false;

                foreach (string k in NameKeywords)
                {
                    if (string.IsNullOrEmpty(k)) continue;
                    if (name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }

                return false;
            }
            catch
            {
                return false;   // 判定失败一律当作正常应用（宁可漏判，不可误伤）
            }
        }

        /// <summary>
        /// 可执行文件名（去扩展名）是否与应用名一致 —— 一致则视为主程序，不做关键词判定。
        ///
        /// 归一化：去掉空格、连字符、下划线、点，转小写。
        /// 这样 `Geek Uninstaller` ↔ `geek.exe` 会被判为**不一致**（产品名比 exe 名长），
        /// 而 `Notepad` ↔ `notepad.exe` 一致。
        ///
        /// 注意：这里**不做**"包含"判断 —— `Geek Uninstaller` 的 exe 是 `geek.exe`，
        /// 名字里含 geek 但 exe 名不含 uninstaller，所以不会被误判为主程序。
        /// 真正的保护来自「应用名本身是否就是个工具名」这个语义判断，
        /// 由调用方通过 NameKeywords 是否命中来间接体现。
        /// </summary>
        static bool IsMainExecutable(string name, string exeFileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(exeFileName)) return false;

                string exe = Normalize(exeFileName);
                string n = Normalize(name);
                if (exe.Length == 0 || n.Length == 0) return false;

                // 完全一致 → 主程序
                if (string.Equals(exe, n, StringComparison.OrdinalIgnoreCase)) return true;

                // 应用名 = exe 名 + 少量后缀（如 "Notepad ++" vs "notepad"）→ 也算主程序
                // 但只在 exe 名足够长（≥4 字符）时才放宽，避免 "QQ" 这种短名误伤
                if (exe.Length >= 4 && n.StartsWith(exe, StringComparison.OrdinalIgnoreCase))
                    return true;

                return false;
            }
            catch { return false; }
        }

        /// <summary>归一化：去空格/连字符/下划线/点，转小写。</summary>
        static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == ' ' || c == '-' || c == '_' || c == '.') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 从完整路径取可执行文件名（不含扩展名）。取不出返回 null。
        /// </summary>
        public static string ExeNameOf(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return null;
                string n = Path.GetFileNameWithoutExtension(path);
                return string.IsNullOrWhiteSpace(n) ? null : n;
            }
            catch { return null; }
        }
    }
}
