using System;

namespace Clover
{
    /// <summary>
    /// 扫描调度：用内置 C# 扫描开始菜单（不再依赖 PowerShell 脚本），
    /// 然后清理死链接、补充识别便携软件。
    /// </summary>
    static class AppScan
    {
        public static bool Run(string baseDir)
        {
            bool ok;
            try
            {
                ok = NativeScanner.Scan(baseDir);
            }
            catch (Exception ex)
            {
                Log.Hook(ex, "扫描开始菜单");
                ok = false;
            }

            if (ok)
            {
                try { NativeScanner.FilterDeadLinks(baseDir); }
                catch (Exception ex) { Log.Hook(ex, "清理死链接"); }

                try { PortableScanner.Merge(baseDir); }
                catch (Exception ex) { Log.Hook(ex, "便携软件识别"); }
            }
            return ok;
        }
    }
}
