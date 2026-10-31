using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 桌面保留名单测试：路径规范化、key 计算、名单增删、自动失效（Prune）。
    ///
    /// **用临时目录，不碰真实桌面。**
    /// </summary>
    static class DesktopKeepTest
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
            Console.WriteLine("=== 桌面保留名单测试 ===");
            Console.WriteLine();

            TestNormalize();
            TestNameKey();
            TestTargetExt();
            TestAddRemove();
            TestPrune();
            TestIsPublicDesktop();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 路径规范化 ----------
        static void TestNormalize()
        {
            Console.WriteLine("--- 1. NormalizeKey ---");

            Check(DesktopKeep.NormalizeKey(@"C:\A\B") == @"c:\a\b", "★大小写归一",
                DesktopKeep.NormalizeKey(@"C:\A\B"));
            Check(DesktopKeep.NormalizeKey("C:/A/B") == @"c:\a\b", "★正斜杠归一",
                DesktopKeep.NormalizeKey("C:/A/B"));
            Check(DesktopKeep.NormalizeKey(@"C:\A\B\") == @"c:\a\b", "★去尾部反斜杠",
                DesktopKeep.NormalizeKey(@"C:\A\B\"));
            Check(DesktopKeep.NormalizeKey(@"C:\A\B\\") == @"c:\a\b", "去多个尾部反斜杠",
                DesktopKeep.NormalizeKey(@"C:\A\B\\"));

            // 盘符根保留反斜杠
            Check(DesktopKeep.NormalizeKey(@"C:\") == @"c:\", "★盘符根保留",
                DesktopKeep.NormalizeKey(@"C:\"));

            // 混用分隔符
            Check(DesktopKeep.NormalizeKey(@"C:\A/B\C") == @"c:\a\b\c", "★混用分隔符",
                DesktopKeep.NormalizeKey(@"C:\A/B\C"));

            // 空 / null
            Check(DesktopKeep.NormalizeKey(null) == "", "null → 空");
            Check(DesktopKeep.NormalizeKey("") == "", "空串 → 空");
            Check(DesktopKeep.NormalizeKey("   ") == "", "空白 → 空");

            // 两种写法必须归一成同一个 key（这是需求 3.1 的核心）
            string a = DesktopKeep.NormalizeKey(@"C:\Program Files\App\a.exe");
            string b = DesktopKeep.NormalizeKey(@"c:/program files/app/a.exe/");
            Check(a == b, "★等价写法归一一致", a + " vs " + b);

            Console.WriteLine();
        }

        // ---------- 2. NameKey ----------
        static void TestNameKey()
        {
            Console.WriteLine("--- 2. NameKey ---");

            Check(DesktopKeep.NameKey(@"C:\Desktop\某网站.url") == "name:某网站.url",
                "★文件名 key", DesktopKeep.NameKey(@"C:\Desktop\某网站.url"));
            Check(DesktopKeep.NameKey(@"C:\Desktop\ABC.URL") == "name:abc.url",
                "★大写扩展名归一", DesktopKeep.NameKey(@"C:\Desktop\ABC.URL"));
            Check(DesktopKeep.NameKey(null) == "", "null → 空");

            Console.WriteLine();
        }

        // ---------- 3. 扩展名过滤 ----------
        static void TestTargetExt()
        {
            Console.WriteLine("--- 3. IsTargetExt（只处理 .lnk / .url）---");

            Check(DesktopCleaner.IsTargetExt(@"C:\a.lnk"), "★.lnk 是目标");
            Check(DesktopCleaner.IsTargetExt(@"C:\a.url"), "★.url 是目标");
            Check(DesktopCleaner.IsTargetExt(@"C:\a.LNK"), "★大写 .LNK 是目标");
            Check(DesktopCleaner.IsTargetExt(@"C:\a.URL"), "大写 .URL 是目标");

            // 其余一律不碰
            Check(!DesktopCleaner.IsTargetExt(@"C:\a.txt"), "★.txt 不处理");
            Check(!DesktopCleaner.IsTargetExt(@"C:\a.exe"), "★.exe 不处理");
            Check(!DesktopCleaner.IsTargetExt(@"C:\a.docx"), "★.docx 不处理");
            Check(!DesktopCleaner.IsTargetExt(@"C:\a.jpg"), "★.jpg 不处理");
            Check(!DesktopCleaner.IsTargetExt(@"C:\folder"), "★无扩展名不处理");
            Check(!DesktopCleaner.IsTargetExt(null), "null 不处理");
            Check(!DesktopCleaner.IsTargetExt(""), "空串不处理");

            Console.WriteLine();
        }

        // ---------- 4. 名单增删 ----------
        static void TestAddRemove()
        {
            Console.WriteLine("--- 4. 名单增删 ---");

            string tmp = Path.Combine(Path.GetTempPath(),
                "dk-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmp);

                // 造一个假目标文件 + 指向它的 .url
                string target = Path.Combine(tmp, "app.exe");
                File.WriteAllText(target, "x");

                string urlFile = Path.Combine(tmp, "test.url");
                File.WriteAllText(urlFile,
                    "[InternetShortcut]\r\nURL=" + target + "\r\n",
                    System.Text.Encoding.Default);

                List<string> keep = new List<string>();

                // 未加入 → 不在名单
                Check(!DesktopKeep.IsKept(keep, urlFile), "★初始不在名单");

                // 加入
                bool added = DesktopKeep.Add(keep, urlFile);
                Check(added, "★加入成功");
                Check(keep.Count == 1, "名单 1 条", Convert.ToString(keep.Count));
                Check(DesktopKeep.IsKept(keep, urlFile), "★加入后在名单");

                // 重复加入 → 不重复
                bool again = DesktopKeep.Add(keep, urlFile);
                Check(!again, "★重复加入返回 false");
                Check(keep.Count == 1, "名单仍 1 条", Convert.ToString(keep.Count));

                // 移除
                bool removed = DesktopKeep.Remove(keep, urlFile);
                Check(removed, "★移除成功");
                Check(keep.Count == 0, "名单空", Convert.ToString(keep.Count));
                Check(!DesktopKeep.IsKept(keep, urlFile), "移除后不在名单");

                // AddKey / RemoveKey 直接操作
                List<string> keep2 = new List<string>();
                DesktopKeep.AddKey(keep2, @"c:\a\b.exe");
                Check(keep2.Count == 1, "AddKey 成功");
                Check(!DesktopKeep.AddKey(keep2, @"C:\A\B.EXE"), "★AddKey 大小写不敏感去重");
                DesktopKeep.RemoveKey(keep2, @"C:\a\B.exe");
                Check(keep2.Count == 0, "★RemoveKey 大小写不敏感");
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }

            Console.WriteLine();
        }

        // ---------- 5. 自动失效（Prune）----------
        static void TestPrune()
        {
            Console.WriteLine("--- 5. 自动失效（跟随手动删除）---");

            string tmp = Path.Combine(Path.GetTempPath(),
                "dkp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmp);

                // 两个目标文件
                string t1 = Path.Combine(tmp, "one.exe");
                string t2 = Path.Combine(tmp, "two.exe");
                File.WriteAllText(t1, "x");
                File.WriteAllText(t2, "x");

                // 对应两个 .url（模拟桌面上的快捷方式）
                string u1 = Path.Combine(tmp, "one.url");
                string u2 = Path.Combine(tmp, "two.url");
                File.WriteAllText(u1, "[InternetShortcut]\r\nURL=" + t1 + "\r\n", System.Text.Encoding.Default);
                File.WriteAllText(u2, "[InternetShortcut]\r\nURL=" + t2 + "\r\n", System.Text.Encoding.Default);

                List<string> keep = new List<string>();
                DesktopKeep.Add(keep, u1);
                DesktopKeep.Add(keep, u2);
                Check(keep.Count == 2, "★初始 2 条", Convert.ToString(keep.Count));

                // ★ 用户手动删掉 u1（模拟：文件消失）
                File.Delete(u1);

                // Prune 用的是真实桌面目录，这里测的是「key 集合比对」逻辑 ——
                // 直接构造一个只含 u2 的存活集合，验证 RemoveAll 行为
                string k1 = DesktopKeep.KeyOf(u1);   // 文件已删 → 走 NameKey
                string k2 = DesktopKeep.KeyOf(u2);
                Check(!string.IsNullOrEmpty(k1), "★已删文件的 key 仍可算出（name: 兜底）", k1);
                Check(!string.IsNullOrEmpty(k2), "存活文件的 key", k2);

                // 模拟 Prune 的判定：只剩 k2 存活
                HashSet<string> alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                alive.Add(k2);
                keep.RemoveAll(delegate(string k)
                { return !alive.Contains(k); });

                Check(keep.Count == 1, "★失效条目被清理", Convert.ToString(keep.Count));
                Check(keep[0] == k2, "留下的是存活的那个", keep[0]);
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }

            // 空名单不报错
            Check(DesktopKeep.Prune(null) == 0, "Prune(null) → 0");
            Check(DesktopKeep.Prune(new List<string>()) == 0, "Prune(空) → 0");

            Console.WriteLine();
        }

        // ---------- 6. 公共桌面判定 ----------
        static void TestIsPublicDesktop()
        {
            Console.WriteLine("--- 6. IsPublicDesktop ---");

            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (!string.IsNullOrEmpty(common))
            {
                string inPublic = Path.Combine(common, "x.lnk");
                Check(DesktopKeep.IsPublicDesktop(inPublic), "★公共桌面内 → true", inPublic);
            }
            else
            {
                Console.WriteLine("  (跳过：本机没有公共桌面目录)");
            }

            string user = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(user))
            {
                string inUser = Path.Combine(user, "x.lnk");
                Check(!DesktopKeep.IsPublicDesktop(inUser), "★用户桌面 → false", inUser);
            }

            Check(!DesktopKeep.IsPublicDesktop(null), "null → false");
            Check(!DesktopKeep.IsPublicDesktop(""), "空串 → false");

            Console.WriteLine();
        }
    }
}
