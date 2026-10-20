using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 「打开文件所在位置」的路径解析测试。
    ///
    /// 被测方法：PanelWindow.TryBuildExplorerArgs（纯逻辑，不弹窗、不启动进程）
    ///   - 存在的文件 → /select,"路径"
    ///   - 文件没了但目录还在 → "目录"
    ///   - 都不存在 → false + 提示
    ///   - .lnk → 先解析真实目标
    /// </summary>
    static class OpenFolderTest
    {
        static int failed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name + (cond || detail.Length == 0 ? "" : "  [" + detail + "]"));
            if (!cond) failed++;
        }

        static string NewDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "clover-openfolder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        static void Main()
        {
            // ---------- 1. 存在的 .exe → /select ----------
            string dir = NewDir();
            try
            {
                string exe = Path.Combine(dir, "demo.exe");
                File.WriteAllText(exe, "x", new UTF8Encoding(false));

                string args, err;
                bool ok = PanelWindow.TryBuildExplorerArgs(exe, out args, out err);
                Check(ok, "存在的 .exe 返回 true");
                Check(args == "/select,\"" + exe + "\"", "参数是 /select,\"路径\"", args ?? "null");
                Check(err == null, "成功时 err 为 null");

                // ---------- 2. 不存在的文件，但目录还在 → 打开目录 ----------
                string gone = Path.Combine(dir, "已卸载.exe");
                ok = PanelWindow.TryBuildExplorerArgs(gone, out args, out err);
                Check(ok, "文件不存在但目录还在：返回 true");
                Check(args == "\"" + dir + "\"", "参数是目录", args ?? "null");

                // ---------- 3. 目录也不存在 → false + 提示 ----------
                string nowhere = Path.Combine(dir, "不存在的子目录", "x.exe");
                ok = PanelWindow.TryBuildExplorerArgs(nowhere, out args, out err);
                Check(!ok, "目录也不存在：返回 false");
                Check(args == null, "失败时 args 为 null");
                Check(!string.IsNullOrEmpty(err), "失败时给出提示文本");

                // ---------- 4. 空路径 / null ----------
                ok = PanelWindow.TryBuildExplorerArgs("", out args, out err);
                Check(!ok, "空路径返回 false");
                Check(!string.IsNullOrEmpty(err), "空路径有提示");

                ok = PanelWindow.TryBuildExplorerArgs(null, out args, out err);
                Check(!ok, "null 路径返回 false");

                // ---------- 5. 大小写不敏感识别 .lnk ----------
                // 注意：伪造的 .lnk 本身是"存在的文件"，解析目标失败后会退回定位 .lnk 自身，
                // 所以走 /select 分支（这是正确行为：文件存在就选中它）。
                string fakeLnk = Path.Combine(dir, "FAKE.LNK");
                File.WriteAllText(fakeLnk, "not a real lnk", new UTF8Encoding(false));
                ok = PanelWindow.TryBuildExplorerArgs(fakeLnk, out args, out err);
                Check(ok, "无效 .lnk（文件本身存在）返回 true");
                Check(args == "/select,\"" + fakeLnk + "\"",
                    "无效 .lnk 定位到 .lnk 自身", args ?? "null");

                // 解析伪造 .lnk 应返回 null（不是有效快捷方式）
                Check(PanelWindow.ResolveShortcutTarget(fakeLnk) == null,
                    "伪造 .lnk 解析目标返回 null", PanelWindow.ResolveShortcutTarget(fakeLnk) ?? "null");

                // ---------- 6. 真实 .lnk：解析到目标 ----------
                string realExe = Path.Combine(dir, "real.exe");
                File.WriteAllText(realExe, "x", new UTF8Encoding(false));
                string lnk = Path.Combine(dir, "real.lnk");
                bool lnkOk = TryCreateShortcut(lnk, realExe);
                if (lnkOk)
                {
                    string resolved = PanelWindow.ResolveShortcutTarget(lnk);
                    Check(resolved != null && resolved.Equals(realExe, StringComparison.OrdinalIgnoreCase),
                        "真实 .lnk 能解析出目标", resolved ?? "null");

                    ok = PanelWindow.TryBuildExplorerArgs(lnk, out args, out err);
                    Check(ok, "真实 .lnk 返回 true");
                    Check(args == "/select,\"" + realExe + "\"",
                        "真实 .lnk 定位到目标文件（不是 lnk 自身）", args ?? "null");
                }
                else
                {
                    Console.WriteLine("      跳过真实 .lnk 测试（WScript.Shell 不可用）");
                }

                // ---------- 7. ResolveShortcutTarget 对无效输入不抛异常 ----------
                Check(PanelWindow.ResolveShortcutTarget(null) == null, "解析 null 返回 null");
                Check(PanelWindow.ResolveShortcutTarget("") == null, "解析空串返回 null");
                Check(PanelWindow.ResolveShortcutTarget(Path.Combine(dir, "不存在.lnk")) == null,
                    "解析不存在的 .lnk 返回 null");            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }

            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static bool TryCreateShortcut(string lnkPath, string targetPath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;
                object shell = Activator.CreateInstance(shellType);
                object sc = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                sc.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { targetPath });
                sc.GetType().InvokeMember("Save",
                    System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                return File.Exists(lnkPath);
            }
            catch
            {
                return false;
            }
        }
    }
}
