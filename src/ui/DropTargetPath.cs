using System;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 解析系统拖放目标的实际目录路径（UI 层，允许使用 COM）。
    ///
    /// 桌面：直接返回用户桌面路径。
    /// 资源管理器：用 Shell.Application.Windows() 按 HWND 找当前窗口 LocationURL。
    /// 解析失败返回空串，调用方日志明确写「目标路径未知」，不猜路径。
    /// </summary>
    static class DropTargetPath
    {
        public static string Resolve(DropTarget.Kind kind, IntPtr rootHwnd)
        {
            try
            {
                if (kind == DropTarget.Kind.Desktop)
                    return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) ?? "";

                if (kind != DropTarget.Kind.Explorer || rootHwnd == IntPtr.Zero) return "";

                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return "";

                object shell = null;
                object windows = null;
                try
                {
                    shell = Activator.CreateInstance(shellType);
                    windows = shellType.InvokeMember("Windows",
                        System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                    if (windows == null) return "";

                    Type wt = windows.GetType();
                    object countObj = wt.InvokeMember("Count",
                        System.Reflection.BindingFlags.GetProperty, null, windows, null);
                    int count = Convert.ToInt32(countObj);

                    for (int i = 0; i < count; i++)
                    {
                        object w = null;
                        try
                        {
                            w = wt.InvokeMember("Item",
                                System.Reflection.BindingFlags.InvokeMethod, null, windows,
                                new object[] { i });
                            if (w == null) continue;

                            Type t = w.GetType();
                            object hwndObj = t.InvokeMember("HWND",
                                System.Reflection.BindingFlags.GetProperty, null, w, null);
                            long hwnd = Convert.ToInt64(hwndObj);
                            if (new IntPtr(hwnd) != rootHwnd) continue;

                            object urlObj = t.InvokeMember("LocationURL",
                                System.Reflection.BindingFlags.GetProperty, null, w, null);
                            string url = Convert.ToString(urlObj) ?? "";
                            string path = FileUrlToPath(url);
                            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
                        }
                        catch { }
                        finally
                        {
                            ReleaseCom(w);
                        }
                    }
                }
                finally
                {
                    ReleaseCom(windows);
                    ReleaseCom(shell);
                }
            }
            catch { }
            return "";
        }

        static string FileUrlToPath(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return "";
                Uri u;
                if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return "";
                if (!u.IsFile) return "";
                return u.LocalPath;
            }
            catch { return ""; }
        }

        static void ReleaseCom(object o)
        {
            try
            {
                if (o != null && System.Runtime.InteropServices.Marshal.IsComObject(o))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(o);
            }
            catch { }
        }
    }
}
