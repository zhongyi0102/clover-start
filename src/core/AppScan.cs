using System;
using System.Collections.Generic;

namespace Clover
{
    /// <summary>
    /// 扫描调度：用内置 C# 扫描开始菜单（不再依赖 PowerShell 脚本），
    /// 然后清理死链接、补充识别便携软件，最后扫注册表与商店应用。
    ///
    /// **三个数据源各写各的文件**（用户要求 C1）：
    ///   · apps.json          ← NativeScanner（本方法的唯一写入者，职责不变）
    ///   · registry-apps.json ← RegistryScanner
    ///   · store-apps.json    ← StoreAppScanner
    /// 合并发生在 Catalog.Load（去重 + 排序），不在这里做。
    ///
    /// 为什么合并放在 Load 而不在这里：
    ///   · Load 每次都会跑，保证界面看到的一致状态
    ///   · 用户手改 JSON 后也能立刻反映
    ///   · 三份文件独立，某个坏了只丢那一份（用户要求 C4）
    /// </summary>
    static class AppScan
    {
        /// <summary>扫描前后真正影响目录内容的三个文件。</summary>
        static readonly string[] CatalogFiles = new string[]
        {
            "apps.json", "registry-apps.json", "store-apps.json"
        };

        public static bool Run(string baseDir)
        {
            return RunDetailed(baseDir).Success;
        }

        /// <summary>
        /// 扫描并报告目录内容是否真的变化。比较内容而不是修改时间，兼容旧扫描器
        /// 仍写 updated 字段的情况；调用方据此决定是否刷新 WPF 控件树。
        /// </summary>
        public static ScanResult RunDetailed(string baseDir)
        {
            DateTime started = DateTime.UtcNow;
            Dictionary<string, string> before = Snapshot(baseDir);
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

            // ---- 新增来源 1：注册表 Uninstall ----
            //
            // 独立 try：注册表读不到（权限/异常）不影响开始菜单的结果。
            // 耗时约 100-2000ms（首次冷缓存慢，之后热缓存快）。
            try
            {
                RegistryScanner.Stats rs = new RegistryScanner.Stats();
                System.Collections.Generic.List<RegistryScanner.Entry> reg = RegistryScanner.Scan(rs);
                RegistryScanner.Save(baseDir, reg, rs);
                Log.Diagnostic("注册表扫描完成，得到 " + reg.Count + " 条");
            }
            catch (Exception ex)
            {
                Log.Hook(ex, "扫描注册表");
            }

            // ---- 新增来源 2：商店应用（UWP）----
            //
            // 独立 try：Shell COM 不可用（精简系统/权限）时只丢商店来源。
            // 内部有 8 秒超时保护，不会卡死扫描。
            try
            {
                StoreAppScanner.Stats ss = new StoreAppScanner.Stats();
                System.Collections.Generic.List<StoreAppScanner.Entry> store = StoreAppScanner.Scan(ss);
                StoreAppScanner.Save(baseDir, store, ss);
                Log.Diagnostic("商店扫描完成，得到 " + store.Count + " 条");
            }
            catch (Exception ex)
            {
                Log.Hook(ex, "扫描商店应用");
            }

            int duration = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            return new ScanResult(ok, Changed(before, Snapshot(baseDir)), duration);
        }

        static Dictionary<string, string> Snapshot(string baseDir)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in CatalogFiles)
            {
                string path = System.IO.Path.Combine(baseDir ?? "", "config", name);
                try { result[name] = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null; }
                catch { result[name] = null; }
            }
            return result;
        }

        static bool Changed(Dictionary<string, string> before, Dictionary<string, string> after)
        {
            foreach (string name in CatalogFiles)
            {
                string a, b;
                before.TryGetValue(name, out a);
                after.TryGetValue(name, out b);
                if (!string.Equals(NormalizeGeneratedJson(a), NormalizeGeneratedJson(b),
                    StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // 兼容旧版本生成文件：updated/stats 只用于诊断，不应把相同目录误判为内容变化。
        internal static string NormalizeGeneratedJson(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try
            {
                System.Web.Script.Serialization.JavaScriptSerializer ser =
                    new System.Web.Script.Serialization.JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> root = ser.Deserialize<Dictionary<string, object>>(text);
                if (root == null) return text;
                root.Remove("updated");
                root.Remove("stats");
                return ser.Serialize(root);
            }
            catch { return text; }
        }
    }
}
