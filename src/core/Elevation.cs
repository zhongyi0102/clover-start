using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 按需提权（不整体提权）。
    ///
    /// ── 设计原则 ──
    ///
    ///   1. **应用本身永远普通权限运行** —— 不加 `requireAdministrator` manifest。
    ///   2. **只有真的被拒绝时才提示提权** —— 捕获 `UnauthorizedAccessException`
    ///      或 `Win32Exception` 的「拒绝访问」才弹框，不预先提权、不自动 UAC。
    ///   3. **用户点「重试」才触发 UAC** —— 默认焦点在「取消」，防误触。
    ///   4. **用户在 UAC 里点「否」→ Win32Exception 1223（ERROR_CANCELLED）
    ///      → 静默当作取消**，不报错、不弹二次提示。
    ///
    /// ── 为什么删除要起子进程 ──
    ///
    /// 提权删除不能重启整个启动台（那会丢掉面板状态、还可能重复启动）。
    /// 改用 `cmd /c` 执行具体删除命令，主进程只等它退出。
    ///
    /// **注意：提权删除是永久删除** —— 回收站需要以原用户身份操作，
    /// 提权后 `SHFileOperation` 进的是管理员回收站或直接永久删。所以提示里必须写明。
    /// </summary>
    public static class Elevation
    {
        /// <summary>UAC 被用户取消的 Win32 错误码。</summary>
        public const int ErrorCancelled = 1223;

        /// <summary>日志回调（由 ui 层注入）。</summary>
        public static Action<string> Log = delegate { };

        // ========== 错误判定 ==========

        /// <summary>
        /// 这个异常是不是「权限不足」？
        ///
        /// 认三种：
        ///   · UnauthorizedAccessException
        ///   · Win32Exception 的 ERROR_ACCESS_DENIED (5)
        ///   · Win32Exception 的 ERROR_PRIVILEGE_NOT_HELD (1314)
        /// </summary>
        public static bool IsAccessDenied(Exception ex)
        {
            if (ex == null) return false;

            if (ex is UnauthorizedAccessException) return true;

            Win32Exception w = ex as Win32Exception;
            if (w != null)
            {
                if (w.NativeErrorCode == 5) return true;      // ERROR_ACCESS_DENIED
                if (w.NativeErrorCode == 1314) return true;   // ERROR_PRIVILEGE_NOT_HELD
            }

            // 内层异常也查一遍
            if (ex.InnerException != null) return IsAccessDenied(ex.InnerException);

            return false;
        }

        /// <summary>UAC 里点了「否」→ 静默当作取消。</summary>
        public static bool IsUserCancelled(Exception ex)
        {
            try
            {
                Win32Exception w = ex as Win32Exception;
                return w != null && w.NativeErrorCode == ErrorCancelled;
            }
            catch { return false; }
        }

        // ========== 提权启动 ==========

        /// <summary>
        /// 以管理员身份启动某个程序（runas）。
        ///
        /// 用户在 UAC 点「否」→ 返回 false，不抛异常。
        /// </summary>
        public static bool RunAs(string file, string arguments)
        {
            try
            {
                if (string.IsNullOrEmpty(file)) return false;
                if (!File.Exists(file) && !Directory.Exists(file)) return false;

                ProcessStartInfo psi = new ProcessStartInfo(file);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                if (!string.IsNullOrEmpty(arguments)) psi.Arguments = arguments;

                Process.Start(psi);
                Log("已请求提权启动: " + file);
                return true;
            }
            catch (Exception ex)
            {
                if (IsUserCancelled(ex))
                {
                    Log("用户取消了 UAC（启动）: " + file);
                    return false;   // 静默
                }
                Log("提权启动失败: " + file + " -> " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 以管理员身份打开某个文件（用默认程序，runas）。
        ///
        /// 与 RunAs 的区别只是语义：这里 file 可以是任意文档，
        /// 由 shell 按关联程序打开；但 runas 对非 exe 通常无效，
        /// 所以调用方应只在「用默认程序打开被拒绝」时用它。
        /// </summary>
        public static bool OpenAs(string file)
        {
            try
            {
                if (string.IsNullOrEmpty(file)) return false;

                ProcessStartInfo psi = new ProcessStartInfo(file);
                psi.UseShellExecute = true;
                psi.Verb = "runas";

                Process.Start(psi);
                Log("已请求提权打开: " + file);
                return true;
            }
            catch (Exception ex)
            {
                if (IsUserCancelled(ex))
                {
                    Log("用户取消了 UAC（打开）: " + file);
                    return false;
                }
                Log("提权打开失败: " + file + " -> " + ex.Message);
                return false;
            }
        }

        // ========== 提权删除 ==========

        /// <summary>
        /// 以管理员身份**永久删除**（不进回收站）。
        ///
        /// 起子进程执行 `cmd /c` 删除命令，不重启启动台。
        /// 路径经过 `QuoteArg` 转义，含空格/中文都能处理。
        ///
        /// 返回 true = 子进程退出码 0。
        /// </summary>
        public static bool DeleteAsAdmin(string path, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    error = "路径为空";
                    return false;
                }

                bool isDir = Directory.Exists(path);
                bool isFile = File.Exists(path);
                if (!isDir && !isFile)
                {
                    error = "路径不存在";
                    return false;
                }

                // ---- 构造删除命令 ----
                //
                // 目录：rd /s /q "path"
                // 文件：del /f /q "path"
                //
                // 为什么用 cmd 而不是直接 Process.Start(path, runas)：
                //   · 对目录，runas 打开的是资源管理器，不是删除
                //   · 需要明确执行「删除」这个动作
                //
                // ★ 转义：cmd 的引号规则与 .NET 不同。路径里的 " 要处理，
                //   而且末尾反斜杠 + 引号会破坏转义。用 QuoteCmdArg 处理。
                string cmd;
                if (isDir)
                    cmd = "rd /s /q " + QuoteCmdArg(path);
                else
                    cmd = "del /f /q " + QuoteCmdArg(path);

                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.Arguments = "/c " + cmd;
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                using (Process p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        error = "无法启动提权进程";
                        return false;
                    }
                    p.WaitForExit(60000);   // 最多等 60 秒

                    if (!p.HasExited)
                    {
                        error = "提权删除超时";
                        Log("提权删除超时: " + path);
                        return false;
                    }

                    int code = p.ExitCode;
                    if (code != 0)
                    {
                        error = "提权删除失败（退出码 " + code + "）";
                        Log(error + ": " + path);
                        return false;
                    }
                }

                Log("已提权永久删除: " + path);
                return true;
            }
            catch (Exception ex)
            {
                if (IsUserCancelled(ex))
                {
                    Log("用户取消了 UAC（删除）: " + path);
                    error = null;    // 静默，不算失败
                    return false;
                }
                error = ex.Message;
                Log("提权删除异常: " + path + " -> " + ex.Message);
                return false;
            }
        }

        // ========== 命令行转义 ==========

        /// <summary>
        /// 把一个路径转成 cmd.exe 能安全接受的形式。
        ///
        /// 规则（参照 `CommandLineToArgvW` + cmd 的双层解析）：
        ///   · 外层加双引号
        ///   · 内部的 `"` 转成 `\"`
        ///   · **末尾反斜杠要加倍** —— 否则 `"C:\dir\"` 里的 `\"` 会被当成转义引号，
        ///     导致路径被截断
        ///
        /// 例：
        ///   `C:\a b\x.txt`      → `"C:\a b\x.txt"`
        ///   `C:\dir\`           → `"C:\dir\\"`
        ///   `C:\a"b\x.txt`      → `"C:\a\"b\x.txt"`
        ///   `D:\中文 目录\`      → `"D:\中文 目录\\"`
        /// </summary>
        public static string QuoteCmdArg(string arg)
        {
            try
            {
                if (arg == null) return "\"\"";

                StringBuilder sb = new StringBuilder();
                sb.Append('"');

                int backslashes = 0;
                foreach (char c in arg)
                {
                    if (c == '\\')
                    {
                        backslashes++;
                        sb.Append(c);
                    }
                    else if (c == '"')
                    {
                        // 引号前的反斜杠要加倍，再补一个转义引号
                        sb.Append('\\', backslashes);
                        sb.Append("\\\"");
                        backslashes = 0;
                    }
                    else
                    {
                        backslashes = 0;
                        sb.Append(c);
                    }
                }

                // 结尾：反斜杠加倍（避免 "C:\dir\" 的 \" 被当转义）
                if (backslashes > 0) sb.Append('\\', backslashes);

                sb.Append('"');
                return sb.ToString();
            }
            catch { return "\"\""; }
        }
    }
}
