using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// AppDedup 测试：多来源合并、路径匹配、AUMID、同名不同程序、空值容错。
    ///
    /// 全部用**构造的假数据**，不读真实注册表、不碰 shell:AppsFolder，
    /// 所以在任何机器上结果都一样。
    /// </summary>
    static class DedupTest
    {
        static int failed = 0;
        static int passed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            if (cond) { passed++; Console.WriteLine("PASS  " + name); }
            else
            {
                failed++;
                Console.WriteLine("FAIL  " + name + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
            }
        }

        static void Main()
        {
            Console.WriteLine("=== AppDedup 测试 ===");
            Console.WriteLine();

            TestPathMerge();
            TestNamePublisherMerge();
            TestSameNameDifferentApps();
            TestStoreSeparate();
            TestEmptyAndNull();
            TestDeterminism();
            TestRegistryInfoFill();

            // 新增（本阶段）：lnk 解析 + 名称兜底严格化
            TestLnkResolvesToSameExe();
            TestSameNameDifferentExe();
            TestNameFallbackBothNoPublisher();
            TestLnkResolveFailureKeepsEntry();
            TestShortcutResolverCache();
            TestUninstallEntry();
            TestInstallerPath();
            TestSameSourceNotMerged();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 路径相同 → 合并 ----------
        static void TestPathMerge()
        {
            Console.WriteLine("--- 1. 路径相同合并 ---");

            List<AppItem> start = new List<AppItem>();
            start.Add(Make("微信", @"D:\Program Files\Tencent\Weixin\Weixin.exe", AppPaths.SourceStartMenu));

            List<RegistryScanner.Entry> reg = new List<RegistryScanner.Entry>();
            reg.Add(MakeReg("微信", @"D:\Program Files\Tencent\Weixin\Weixin.exe", "3.9.0", "Tencent"));

            AppDedup.Stats st = new AppDedup.Stats();
            List<AppItem> result = AppDedup.Merge(start, reg, null, st);

            Check(result.Count == 1, "同路径合并为 1 条", "实际 " + result.Count);
            Check(st.MergedByPath == 1, "统计: 按路径合并 1", "实际 " + st.MergedByPath);
            Check(result[0].Name == "微信", "保留开始菜单的名称", result[0].Name);
            Check(result[0].Version == "3.9.0", "补上注册表版本", result[0].Version);
            Check(result[0].Publisher == "Tencent", "补上注册表发布者", result[0].Publisher);
            Check(result[0].Source == AppPaths.SourceStartMenu, "来源保持开始菜单", result[0].Source);
            Console.WriteLine();
        }

        // ---------- 2. 路径不同但名称+发布者相同 → 合并 ----------
        static void TestNamePublisherMerge()
        {
            Console.WriteLine("--- 2. 名称+发布者合并 ---");

            List<AppItem> start = new List<AppItem>();
            // 开始菜单是 .lnk 解析出的一个路径
            AppItem a = Make("Ditto", @"C:\Program Files\Ditto\Ditto.exe", AppPaths.SourceStartMenu);
            a.Publisher = "Ditto";
            start.Add(a);

            List<RegistryScanner.Entry> reg = new List<RegistryScanner.Entry>();
            // 注册表是另一个路径（比如升级后位置变了）
            reg.Add(MakeReg("Ditto", @"C:\Program Files\Ditto\Ditto64.exe", "3.24", "Ditto"));

            AppDedup.Stats st = new AppDedup.Stats();
            List<AppItem> result = AppDedup.Merge(start, reg, null, st);

            Check(result.Count == 1, "名称+发布者相同合并为 1 条", "实际 " + result.Count);
            Check(st.MergedByNamePublisher == 1, "统计: 按名称+发布者合并 1", "实际 " + st.MergedByNamePublisher);
            Console.WriteLine();
        }

        // ---------- 3. 同名但不同程序 → 不合并 ----------
        static void TestSameNameDifferentApps()
        {
            Console.WriteLine("--- 3. 同名不同程序不合并 ---");

            List<AppItem> start = new List<AppItem>();
            AppItem a = Make("Helper", @"C:\A\Helper.exe", AppPaths.SourceStartMenu);
            a.Publisher = "CompanyA";
            start.Add(a);

            List<RegistryScanner.Entry> reg = new List<RegistryScanner.Entry>();
            // 同名，但发布者不同、路径不同 → 是两个程序
            reg.Add(MakeReg("Helper", @"C:\B\Helper.exe", "1.0", "CompanyB"));

            AppDedup.Stats st = new AppDedup.Stats();
            List<AppItem> result = AppDedup.Merge(start, reg, null, st);

            Check(result.Count == 2, "同名不同发布者保留 2 条", "实际 " + result.Count);
            Check(st.MergedByNamePublisher == 0, "统计: 未按名称合并", "实际 " + st.MergedByNamePublisher);
            Console.WriteLine();
        }

        // ---------- 4. 商店应用不跨来源合并 ----------
        static void TestStoreSeparate()
        {
            Console.WriteLine("--- 4. shell:AppsFolder 条目分两类处理 ---");

            // A. 真商店应用：不跨来源合并，各留一条
            {
                List<AppItem> start = new List<AppItem>();
                start.Add(Make("计算器", @"C:\Windows\System32\calc.exe", AppPaths.SourceStartMenu));

                List<StoreAppScanner.Entry> store = new List<StoreAppScanner.Entry>();
                StoreAppScanner.Entry s = new StoreAppScanner.Entry();
                s.DisplayName = "计算器";
                s.Aumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
                s.LaunchPath = AppPaths.FromAumid(s.Aumid);
                s.IsRealStore = true;
                store.Add(s);

                AppDedup.Stats st = new AppDedup.Stats();
                List<AppItem> result = AppDedup.Merge(start, null, store, st);

                Check(result.Count == 2, "真商店：与传统版各留一条", "实际 " + result.Count);
                bool hasStore = false, hasMenu = false;
                foreach (AppItem x in result)
                {
                    if (x.Source == AppPaths.SourceStore) hasStore = true;
                    if (x.Source == AppPaths.SourceStartMenu) hasMenu = true;
                }
                Check(hasStore && hasMenu, "真商店：来源是 Store + StartMenu");
            }

            // B. 非真商店（如 Chrome，AUMID 是字面量）：Catalog 已有同名条目 → 跳过
            {
                List<AppItem> start = new List<AppItem>();
                start.Add(Make("Google Chrome", @"C:\Program Files\Chrome\chrome.exe", AppPaths.SourceStartMenu));

                List<StoreAppScanner.Entry> store = new List<StoreAppScanner.Entry>();
                StoreAppScanner.Entry s = new StoreAppScanner.Entry();
                s.DisplayName = "Google Chrome";
                s.Aumid = "Chrome";                       // 字面量，不是包族名
                s.LaunchPath = AppPaths.FromAumid(s.Aumid);
                s.IsRealStore = false;
                store.Add(s);

                AppDedup.Stats st = new AppDedup.Stats();
                List<AppItem> result = AppDedup.Merge(start, null, store, st);

                Check(result.Count == 1, "★非商店 AUMID + 已有同名条目 → 跳过", "实际 " + result.Count);
                Check(st.SkippedStoreDuplicate == 1, "统计: 非商店重复 1", "实际 " + st.SkippedStoreDuplicate);
                Check(result[0].Source == AppPaths.SourceStartMenu, "保留的是开始菜单那条", result[0].Source);
            }

            // C. 非真商店 + Catalog 没有对应条目 → 收，但不加 ·商店
            {
                List<StoreAppScanner.Entry> store = new List<StoreAppScanner.Entry>();
                StoreAppScanner.Entry s = new StoreAppScanner.Entry();
                s.DisplayName = "运行";
                s.Aumid = "Microsoft.Windows.Shell.RunDialog";
                s.LaunchPath = AppPaths.FromAumid(s.Aumid);
                s.IsRealStore = false;
                store.Add(s);

                AppDedup.Stats st = new AppDedup.Stats();
                List<AppItem> result = AppDedup.Merge(null, null, store, st);

                Check(result.Count == 1, "非商店 AUMID + 无对应条目 → 收录", "实际 " + result.Count);
                if (result.Count == 1)
                {
                    Check(result[0].Source == AppPaths.SourceAppsFolder,
                        "★来源是 AppsFolder（不是 Store，界面不加 ·商店）", result[0].Source);
                    Check(result[0].Source != AppPaths.SourceStore, "★不会被标成商店应用");
                }
            }

            // D. LooksLikePackageAumid 判定
            {
                Check(StoreAppScanner.LooksLikePackageAumid("Claude_pzs8sxrjxfjjc!Claude"),
                    "Claude_pzs8sxrjxfjjc!Claude → 真商店");
                Check(StoreAppScanner.LooksLikePackageAumid("MSTeams_8wekyb3d8bbwe!MSTeams"),
                    "MSTeams_8wekyb3d8bbwe!MSTeams → 真商店");
                Check(StoreAppScanner.LooksLikePackageAumid("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
                    "计算器 → 真商店");
                Check(!StoreAppScanner.LooksLikePackageAumid("Chrome"), "★Chrome → 不是真商店");
                Check(!StoreAppScanner.LooksLikePackageAumid("Microsoft.Office.EXCEL.EXE.15"),
                    "★Microsoft.Office.EXCEL.EXE.15 → 不是真商店");
                Check(!StoreAppScanner.LooksLikePackageAumid("Microsoft.Windows.ControlPanel"),
                    "★控制面板 → 不是真商店");
                Check(!StoreAppScanner.LooksLikePackageAumid(""), "空串 → 不是");
                Check(!StoreAppScanner.LooksLikePackageAumid(null), "null → 不是");
            }
            Console.WriteLine();
        }

        // ---------- 5. 空值 / null 容错 ----------
        static void TestEmptyAndNull()
        {
            Console.WriteLine("--- 5. 空值与 null 容错 ---");

            AppDedup.Stats st = new AppDedup.Stats();

            // 全 null
            List<AppItem> r1 = AppDedup.Merge(null, null, null, st);
            Check(r1 != null && r1.Count == 0, "全 null 返回空列表不抛异常");

            // 列表里有 null 元素
            List<AppItem> start = new List<AppItem>();
            start.Add(null);
            start.Add(Make("", @"C:\a.exe", AppPaths.SourceStartMenu));      // 空名称
            start.Add(Make("OK", @"C:\ok.exe", AppPaths.SourceStartMenu));

            AppDedup.Stats st2 = new AppDedup.Stats();
            List<AppItem> r2 = AppDedup.Merge(start, null, null, st2);
            Check(r2.Count == 1, "null 和空名称被丢弃，只留 1 条", "实际 " + r2.Count);
            Check(st2.DroppedNoName == 2, "统计: 丢弃无名称 2", "实际 " + st2.DroppedNoName);

            // 注册表条目 null / 不可启动
            List<RegistryScanner.Entry> reg = new List<RegistryScanner.Entry>();
            reg.Add(null);
            RegistryScanner.Entry unlaunchable = new RegistryScanner.Entry();
            unlaunchable.DisplayName = "运行库";
            unlaunchable.Launchable = false;
            reg.Add(unlaunchable);

            AppDedup.Stats st3 = new AppDedup.Stats();
            List<AppItem> r3 = AppDedup.Merge(null, reg, null, st3);
            Check(r3.Count == 0, "不可启动条目不进最终列表", "实际 " + r3.Count);
            Check(st3.DroppedUnlaunchable == 1, "统计: 丢弃不可启动 1", "实际 " + st3.DroppedUnlaunchable);
            Console.WriteLine();
        }

        // ---------- 6. 确定性：同样输入两次结果相同 ----------
        static void TestDeterminism()
        {
            Console.WriteLine("--- 6. 结果确定性 ---");

            string sig1 = RunAndSign();
            string sig2 = RunAndSign();
            Check(sig1 == sig2, "两次运行结果完全一致（不依赖字典枚举顺序）");
            Console.WriteLine();
        }

        static string RunAndSign()
        {
            List<AppItem> start = new List<AppItem>();
            start.Add(Make("阿波", @"C:\a.exe", AppPaths.SourceStartMenu));
            start.Add(Make("微信", @"C:\w.exe", AppPaths.SourceStartMenu));
            start.Add(Make("Steam", @"C:\s.exe", AppPaths.SourceStartMenu));

            List<RegistryScanner.Entry> reg = new List<RegistryScanner.Entry>();
            reg.Add(MakeReg("波", @"C:\b.exe", "1", "P1"));
            reg.Add(MakeReg("次", @"C:\c.exe", "2", "P2"));

            AppDedup.Stats st = new AppDedup.Stats();
            List<AppItem> result = AppDedup.Merge(start, reg, null, st);

            StringBuilder sb = new StringBuilder();
            foreach (AppItem a in result) sb.Append(a.Name).Append('|').Append(a.Path).Append(';');
            return sb.ToString();
        }

        // ---------- 7. 注册表信息补充 ----------
        static void TestRegistryInfoFill()
        {
            Console.WriteLine("--- 7. 注册表信息补充 ---");

            List<AppItem> start = new List<AppItem>();
            AppItem a = Make("TestApp", @"C:\t\test.exe", AppPaths.SourceStartMenu);
            start.Add(a);   // 没有任何 Version/Publisher/InstallDate

            List<RegistryScanner.Entry> reg = new List<RegistryScanner.Entry>();
            RegistryScanner.Entry r = MakeReg("TestApp", @"C:\t\test.exe", "2.5.1", "TestCorp");
            r.InstallDate = "20260101";
            r.InstallLocation = @"C:\t";
            reg.Add(r);

            AppDedup.Stats st = new AppDedup.Stats();
            List<AppItem> result = AppDedup.Merge(start, reg, null, st);

            Check(result.Count == 1, "合并为 1 条");
            Check(result[0].Version == "2.5.1", "版本已补", result[0].Version);
            Check(result[0].Publisher == "TestCorp", "发布者已补", result[0].Publisher);
            Check(result[0].InstallDate == "20260101", "安装日期已补", result[0].InstallDate);
            Check(result[0].InstallLocation == @"C:\t", "安装目录已补", result[0].InstallLocation);

            // 已有值不被覆盖
            List<AppItem> start2 = new List<AppItem>();
            AppItem b = Make("TestApp2", @"C:\t2\test.exe", AppPaths.SourceStartMenu);
            b.Version = "已有版本";
            start2.Add(b);

            List<RegistryScanner.Entry> reg2 = new List<RegistryScanner.Entry>();
            reg2.Add(MakeReg("TestApp2", @"C:\t2\test.exe", "注册表版本", "TestCorp"));

            AppDedup.Stats st2 = new AppDedup.Stats();
            List<AppItem> r2 = AppDedup.Merge(start2, reg2, null, st2);
            Check(r2[0].Version == "已有版本", "已有版本不被覆盖", r2[0].Version);
            Console.WriteLine();
        }

        // ---------- 8. lnk 与目标 exe 相同 → 合并 ----------
        //
        // 这是本阶段最核心的新能力：开始菜单存的是 .lnk，
        // 注册表给的是真实 exe 路径，必须解析后才能匹配上。
        //
        // 用临时目录造真实的 .lnk（WScript.Shell COM 创建），
        // 如果环境不支持 COM 就跳过（不判失败）。
        static void TestLnkResolvesToSameExe()
        {
            Console.WriteLine("--- 8. lnk 解析后路径匹配 ---");

            string dir = NewTempDir("dedup-lnk");
            string exe = Path.Combine(dir, "MyApp.exe");
            string lnk = Path.Combine(dir, "MyApp.lnk");
            try
            {
                File.WriteAllText(exe, "fake");

                if (!TryCreateShortcut(lnk, exe))
                {
                    Console.WriteLine("      跳过：WScript.Shell 不可用");
                    Console.WriteLine();
                    return;
                }

                // 确认解析能力可用
                string resolved = ShortcutResolver.Resolve(lnk);
                if (string.IsNullOrEmpty(resolved))
                {
                    Console.WriteLine("      跳过：lnk 解析返回空（COM 行为差异）");
                    Console.WriteLine();
                    return;
                }

                List<AppItem> start = new List<AppItem>();
                start.Add(Make("MyApp", lnk, AppPaths.SourceStartMenu));

                List<RegistryScanner.Entry> reg = new List<RegistryScanner.Entry>();
                reg.Add(MakeReg("MyApp", exe, "1.0", "TestCorp"));

                AppDedup.Stats st = new AppDedup.Stats();
                List<AppItem> result = AppDedup.Merge(start, reg, null, st);

                Check(result.Count == 1, "lnk 与目标 exe 合并为 1 条", "实际 " + result.Count);
                Check(st.MergedByPath == 1, "统计: 按路径合并 1", "实际 " + st.MergedByPath);
                Check(result[0].Version == "1.0", "合并后补上版本", result[0].Version);

                // 关键：原始路径不能被改（否则启动行为变了）
                Check(result[0].Path == lnk, "原始启动路径保持不变（仍是 .lnk）", result[0].Path);
            }
            finally { CleanDir(dir); }
            Console.WriteLine();
        }

        // ---------- 9. 不同目录同名 exe 不应仅因名称合并 ----------
        static void TestSameNameDifferentExe()
        {
            Console.WriteLine("--- 9. 同名但 exe 名不同不合并 ---");

            List<AppItem> start = new List<AppItem>();
            start.Add(Make("Tool", @"C:\A\ToolLauncher.exe", AppPaths.SourceStartMenu));

            List<RegistryScanner.Entry> reg = new List<RegistryScanner.Entry>();
            // 同名 "Tool"，但 exe 名不同（ToolLauncher vs ToolCore）→ 不同程序
            reg.Add(MakeReg("Tool", @"C:\B\ToolCore.exe", "1.0", ""));   // 无发布者

            AppDedup.Stats st = new AppDedup.Stats();
            List<AppItem> result = AppDedup.Merge(start, reg, null, st);

            Check(result.Count == 2, "同名但 exe 名不同保留 2 条", "实际 " + result.Count);
            Check(st.MergedByNameOnly == 0, "统计: 名称兜底未合并", "实际 " + st.MergedByNameOnly);
            Console.WriteLine();
        }

        // ---------- 10. 双方无发布者 + 名称相同 + exe 名相同 → 合并 ----------
        static void TestNameFallbackBothNoPublisher()
        {
            Console.WriteLine("--- 10. 名称兜底（双方无发布者 + 同 exe 名）---");

            List<AppItem> start = new List<AppItem>();
            AppItem a = Make("Clash V-Ninja", @"C:\X\clash-ninja.exe", AppPaths.SourceStartMenu);
            a.Publisher = "";   // 无发布者
            start.Add(a);

            List<RegistryScanner.Entry> reg = new List<RegistryScanner.Entry>();
            // 同名、同 exe 名、无发布者、路径不同 → 应合并
            reg.Add(MakeReg("Clash V-Ninja", @"D:\Y\clash-ninja.exe", "1.0", ""));

            AppDedup.Stats st = new AppDedup.Stats();
            List<AppItem> result = AppDedup.Merge(start, reg, null, st);

            Check(result.Count == 1, "三条证据一致时合并为 1 条", "实际 " + result.Count);
            Check(st.MergedByNameOnly == 1, "统计: 名称兜底合并 1", "实际 " + st.MergedByNameOnly);
            Console.WriteLine();
        }

        // ---------- 11. lnk 解析失败时保留原条目 ----------
        static void TestLnkResolveFailureKeepsEntry()
        {
            Console.WriteLine("--- 11. lnk 解析失败不丢条目 ---");

            List<AppItem> start = new List<AppItem>();
            // 指向不存在的 lnk → 解析必然失败
            string fakeLnk = Path.Combine(Path.GetTempPath(), "不存在的快捷方式_" + Guid.NewGuid() + ".lnk");
            start.Add(Make("GhostApp", fakeLnk, AppPaths.SourceStartMenu));

            AppDedup.Stats st = new AppDedup.Stats();
            List<AppItem> result = AppDedup.Merge(start, null, null, st);

            Check(result.Count == 1, "解析失败的条目仍保留", "实际 " + result.Count);
            Check(result[0].Path == fakeLnk, "原始路径未被修改", result[0].Path);
            Check(result[0].Name == "GhostApp", "名称未被修改", result[0].Name);
            Console.WriteLine();
        }

        // ---------- 12. ShortcutResolver 缓存复用 ----------
        static void TestShortcutResolverCache()
        {
            Console.WriteLine("--- 12. lnk 解析缓存复用 ---");

            string dir = NewTempDir("dedup-cache");
            string exe = Path.Combine(dir, "CacheApp.exe");
            string lnk = Path.Combine(dir, "CacheApp.lnk");
            try
            {
                File.WriteAllText(exe, "fake");
                if (!TryCreateShortcut(lnk, exe))
                {
                    Console.WriteLine("      跳过：WScript.Shell 不可用");
                    Console.WriteLine();
                    return;
                }

                ShortcutResolver.Clear();
                int beforeReal = ShortcutResolver.RealResolves;

                string r1 = ShortcutResolver.Resolve(lnk);
                int afterFirst = ShortcutResolver.RealResolves;
                string r2 = ShortcutResolver.Resolve(lnk);
                int afterSecond = ShortcutResolver.RealResolves;

                if (string.IsNullOrEmpty(r1))
                {
                    Console.WriteLine("      跳过：解析返回空");
                    Console.WriteLine();
                    return;
                }

                Check(afterFirst == beforeReal + 1, "首次解析计入 RealResolves");
                Check(afterSecond == afterFirst, "第二次命中缓存，不再解析");
                Check(r1 == r2, "两次结果一致");
                Check(ShortcutResolver.CacheHits >= 1, "缓存命中计数增加");
            }
            finally { CleanDir(dir); }
            Console.WriteLine();
        }

        // ---------- 10. 卸载入口判定（IsUninstallEntry）----------
        //
        // 用户明确要求：只拦「卸载」，不拦「安装器/更新器」。
        // Windows Update / Visual Studio Installer 这类用户要用的入口不能被误拦。
        static void TestUninstallEntry()
        {
            Console.WriteLine("--- 10. 卸载入口判定 ---");

            // 应该被拦
            Check(PortableScanner.IsUninstallEntry("卸载微信", null), "★「卸载微信」被拦");
            Check(PortableScanner.IsUninstallEntry("Uninstall Mineradio", null), "★「Uninstall xxx」被拦");
            Check(PortableScanner.IsUninstallEntry("Steam", @"D:\Program Files\steam\uninstall.exe"),
                "★目标 uninstall.exe 被拦（Steam 的注册表条目）");
            Check(PortableScanner.IsUninstallEntry("Anaconda3", @"D:\ProgramData\anaconda3\Uninstall-Anaconda3.exe"),
                "★Uninstall-Anaconda3.exe 被拦");
            Check(PortableScanner.IsUninstallEntry("x", @"C:\a\unins000.exe"), "unins000.exe 被拦");
            Check(PortableScanner.IsUninstallEntry("x", @"C:\a\uninst.exe"), "uninst.exe 被拦");

            // 不该被拦（用户要用的入口）
            Check(!PortableScanner.IsUninstallEntry("Visual Studio Installer", @"C:\VS\vs_installer.exe"),
                "★Visual Studio Installer 不被拦");
            Check(!PortableScanner.IsUninstallEntry("Windows Update", null),
                "★Windows Update 不被拦");
            Check(!PortableScanner.IsUninstallEntry("Geek Uninstaller", @"D:\geek\geek.exe"),
                "★Geek Uninstaller 不被拦（文件名 geek.exe，名字不以「卸载/Uninstall」开头）");
            Check(!PortableScanner.IsUninstallEntry("MySQL Installer - Community",
                @"C:\Program Files (x86)\MySQL\MySQL Installer for Windows\MySQLInstaller.exe"),
                "★MySQL Installer 不被拦（名字不以「卸载」开头，exe 不以 unins 开头）");
            Check(!PortableScanner.IsUninstallEntry("Windows PowerShell", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"),
                "普通程序不被拦");
            Check(!PortableScanner.IsUninstallEntry(null, null), "null 不拦");
            Check(!PortableScanner.IsUninstallEntry("", ""), "空串不拦");
            Console.WriteLine();
        }

        // ---------- 11. RegistryScanner 安装包路径排除（B2）----------
        static void TestInstallerPath()
        {
            Console.WriteLine("--- 11. 安装包路径排除 ---");

            // 应该被排除
            Check(RegistryScanner.IsInstallerPath(
                @"C:\Users\32532\AppData\Local\Package Cache\{1da2e09b-199c-4def-9a99-93a8c1b8ddf2}\python-3.11.9-amd64.exe"),
                "★Package Cache 里的 python-3.11.9-amd64.exe 被排除");
            Check(RegistryScanner.IsInstallerPath(@"C:\Windows\Installer\{GUID}\setup.exe"),
                "Windows\\Installer 下的被排除");
            Check(RegistryScanner.IsInstallerPath(@"D:\a\app-x64.exe"), "*-x64.exe 被排除");
            Check(RegistryScanner.IsInstallerPath(@"D:\a\app-amd64.exe"), "*-amd64.exe 被排除");
            Check(RegistryScanner.IsInstallerPath(@"D:\a\setup.exe"), "setup.exe 被排除");
            Check(RegistryScanner.IsInstallerPath(@"D:\a\uninstall.exe"), "uninstall.exe 被排除");
            Check(RegistryScanner.IsInstallerPath(@"D:\a\Uninstall-Anaconda3.exe"), "Uninstall-Anaconda3.exe 被排除");

            // 不该被排除
            Check(!RegistryScanner.IsInstallerPath(@"D:\Program Files\Microsoft VS Code\Code.exe"),
                "★正常的 Code.exe 不排除");
            Check(!RegistryScanner.IsInstallerPath(@"C:\Program Files\Google\Chrome\Application\chrome.exe"),
                "★正常的 chrome.exe 不排除");
            Check(!RegistryScanner.IsInstallerPath(@"D:\Program Files\7-Zip\7zFM.exe"),
                "正常的 7zFM.exe 不排除");
            Check(!RegistryScanner.IsInstallerPath(null), "null 不排除");
            Console.WriteLine();
        }

        // ---------- 12. 同来源多入口不被合并（固化核对结论）----------
        static void TestSameSourceNotMerged()
        {
            Console.WriteLine("--- 12. 同来源多入口不被合并 ---");

            // 实测场景：Anaconda Prompt / Command Prompt / Node.js command prompt
            // 三个 .lnk 都指向 cmd.exe，但它们必须各自保留（不同用途的入口）。
            string cmd = @"C:\Windows\System32\cmd.exe";
            List<AppItem> start = new List<AppItem>();
            start.Add(Make("Anaconda Prompt", cmd, AppPaths.SourceStartMenu));
            start.Add(Make("Command Prompt", cmd, AppPaths.SourceStartMenu));
            start.Add(Make("Node.js command prompt", cmd, AppPaths.SourceStartMenu));

            AppDedup.Stats st = new AppDedup.Stats();
            List<AppItem> result = AppDedup.Merge(start, null, null, st);

            Check(result.Count == 3, "★同来源 3 个入口指向同一 exe → 全部保留",
                "实际 " + result.Count);
            Check(st.MergedByPath == 0, "统计: 同来源不按路径合并", "实际 " + st.MergedByPath);
            Console.WriteLine();
        }

        // ---------- 辅助 ----------

        static string NewTempDir(string tag)
        {
            string d = Path.Combine(Path.GetTempPath(), tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        static void CleanDir(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
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
                if (sc == null) return false;
                Type scType = sc.GetType();
                scType.InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { targetPath });
                scType.InvokeMember("Save",
                    System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                return File.Exists(lnkPath);
            }
            catch { return false; }
        }

        static AppItem Make(string name, string path, string source)
        {
            AppItem a = new AppItem();
            a.Name = name;
            a.Path = path;
            a.Source = source;
            a.Launchable = true;
            return a;
        }

        static RegistryScanner.Entry MakeReg(string name, string launchPath, string version, string publisher)
        {
            RegistryScanner.Entry r = new RegistryScanner.Entry();
            r.DisplayName = name;
            r.LaunchPath = launchPath;
            r.Launchable = true;
            r.Version = version;
            r.Publisher = publisher;
            return r;
        }
    }
}
