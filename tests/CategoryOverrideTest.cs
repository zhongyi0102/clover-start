using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 分类覆盖表测试。
    ///
    /// 覆盖三件事：
    ///   1. 优先级：覆盖表 > 关键词规则
    ///   2. 重新扫描后保持：重新 Load 后覆盖仍然生效
    ///   3. 文件损坏时回退到默认规则（不崩、不阻断启动）
    /// </summary>
    static class CategoryOverrideTest
    {
        static int failed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name + (cond || detail.Length == 0 ? "" : "  [" + detail + "]"));
            if (!cond) failed++;
        }

        static string NewRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "clover-catov-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            return root;
        }

        static void Main()
        {
            // ---------- 1. 基本读写 ----------
            string r1 = NewRoot();
            try
            {
                CategoryOverrides o = CategoryOverrides.Load(r1);
                Check(o.Count == 0, "新目录加载为空表");

                Check(o.Get(@"C:\x\a.exe") == null, "未设置时 Get 返回 null");

                Check(o.Set(@"C:\x\a.exe", "开发"), "Set 新条目返回 true（有变化）");
                Check(o.Get(@"C:\x\a.exe") == "开发", "Set 后能读到");

                Check(!o.Set(@"C:\x\a.exe", "开发"), "Set 相同值返回 false（无变化）");
                Check(o.Set(@"C:\x\a.exe", "网络通讯"), "Set 改值返回 true");
                Check(o.Get(@"C:\x\a.exe") == "网络通讯", "改值后读到新值");

                Check(o.Remove(@"C:\x\a.exe"), "Remove 已存在的返回 true");
                Check(o.Get(@"C:\x\a.exe") == null, "Remove 后读不到");
                Check(!o.Remove(@"C:\x\a.exe"), "Remove 不存在的返回 false");

                // ---------- 2. 落盘 + 重新加载保持 ----------
                o.Set(@"C:\x\b.exe", "影音娱乐");
                CategoryOverrides reloaded = CategoryOverrides.Load(r1);
                Check(reloaded.Get(@"C:\x\b.exe") == "影音娱乐", "落盘后重新加载仍保持");

                // ---------- 3. 路径大小写不敏感 ----------
                Check(reloaded.Get(@"C:\X\B.EXE") == "影音娱乐", "路径大小写不敏感");

                // ---------- 4. 应用到清单上（优先级高于关键词规则）----------
                // 模拟：关键词规则把这些判成某个分类，覆盖表要能改掉
                List<AppItem> apps = new List<AppItem>();
                apps.Add(new AppItem { Name = "HP Scan", Path = @"C:\x\b.exe", Category = "系统工具" });
                apps.Add(new AppItem { Name = "Word", Path = @"C:\x\word.exe", Category = "办公" });
                int changed = reloaded.Apply(apps);
                Check(changed == 1, "Apply 只改动被覆盖的那条", "改动 " + changed);
                Check(apps[0].Category == "影音娱乐", "覆盖表改掉了 HP Scan 的分类", apps[0].Category);
                Check(apps[1].Category == "办公", "没被覆盖的保持原分类", apps[1].Category);
            }
            finally
            {
                try { Directory.Delete(r1, true); } catch { }
            }

            // ---------- 5. 损坏文件回退到默认规则 ----------
            string r2 = NewRoot();
            try
            {
                string p = CategoryOverrides.PathOf(r2);

                // 5a. 非法 JSON
                File.WriteAllText(p, "{{{ 这不是合法 JSON", new UTF8Encoding(false));
                CategoryOverrides broken = null;
                try
                {
                    broken = CategoryOverrides.Load(r2);
                    Check(broken != null, "损坏 JSON 不抛异常");
                    Check(broken.Count == 0, "损坏 JSON 回退为空表", "实际 " + broken.Count);
                }
                catch (Exception ex)
                {
                    Check(false, "损坏 JSON 不抛异常", ex.Message);
                }

                // 5b. 空文件
                File.WriteAllText(p, "", new UTF8Encoding(false));
                CategoryOverrides empty = CategoryOverrides.Load(r2);
                Check(empty.Count == 0, "空文件回退为空表");

                // 5c. 结构合法但没有 overrides 字段
                File.WriteAllText(p, "{\"version\":1}", new UTF8Encoding(false));
                Check(CategoryOverrides.Load(r2).Count == 0, "缺 overrides 字段回退为空表");

                // 5d. overrides 值类型不对（不是字典）
                File.WriteAllText(p, "{\"overrides\":[1,2,3]}", new UTF8Encoding(false));
                Check(CategoryOverrides.Load(r2).Count == 0, "overrides 类型不对时回退为空表");

                // 5e. 空 key / 空 value 被跳过
                File.WriteAllText(p,
                    "{\"overrides\":{\"\":\"开发\",\"C:\\\\ok.exe\":\"\",\"C:\\\\good.exe\":\"办公\"}}",
                    new UTF8Encoding(false));
                CategoryOverrides partial = CategoryOverrides.Load(r2);
                Check(partial.Count == 1, "跳过空 key / 空 value，只保留有效项", "实际 " + partial.Count);
                Check(partial.Get(@"C:\good.exe") == "办公", "有效项被保留");

                // ---------- 6. 损坏后仍能正常写入（自愈）----------
                partial.Set(@"C:\new.exe", "开发");
                CategoryOverrides afterFix = CategoryOverrides.Load(r2);
                Check(afterFix.Get(@"C:\new.exe") == "开发", "损坏后写入仍能保存");
                Check(afterFix.Count == 2, "自愈后共 2 条", "实际 " + afterFix.Count);
            }
            finally
            {
                try { Directory.Delete(r2, true); } catch { }
            }

            // ---------- 7. Catalog.Load 会应用覆盖（端到端优先级）----------
            string r3 = NewRoot();
            try
            {
                // 造一个能被扫出来的假 .lnk？不行——Catalog 只读 apps.json/manual-apps.json。
                // 这里直接写 manual-apps.json（Catalog 会读它），再放覆盖表，验证优先级。
                string exePath = Path.Combine(r3, "fake.exe");
                File.WriteAllText(exePath, "x", new UTF8Encoding(false));

                string manual = "{\"apps\":[{\"name\":\"FakeApp\",\"path\":\"" +
                    exePath.Replace("\\", "\\\\") + "\",\"category\":\"其他\"}]}";
                File.WriteAllText(Path.Combine(r3, "config", "manual-apps.json"), manual, new UTF8Encoding(false));

                // 未加覆盖：保持 manual 里的分类
                Catalog c1 = Catalog.Load(r3);
                AppItem found1 = null;
                foreach (AppItem a in c1.Apps) if (a.Name == "FakeApp") found1 = a;
                Check(found1 != null, "manual-apps 条目被加载");
                Check(found1 != null && found1.Category == "其他", "无覆盖时保持原分类",
                    found1 == null ? "null" : found1.Category);

                // 加覆盖：分类应被改掉
                CategoryOverrides ov = CategoryOverrides.Load(r3);
                ov.Set(exePath, "开发");

                Catalog c2 = Catalog.Load(r3);
                AppItem found2 = null;
                foreach (AppItem a in c2.Apps) if (a.Name == "FakeApp") found2 = a;
                Check(found2 != null && found2.Category == "开发",
                    "Catalog.Load 应用了覆盖（优先级高于文件里的分类）",
                    found2 == null ? "null" : found2.Category);

                // 分类顺序里也要有被覆盖到的分类（保证侧栏能显示）
                Check(c2.Order.Contains("开发"), "覆盖后的分类出现在 Order 里");

                // 重新加载多次结果稳定（模拟"重新扫描后保持"）
                for (int i = 0; i < 3; i++)
                {
                    Catalog again = Catalog.Load(r3);
                    AppItem f = null;
                    foreach (AppItem a in again.Apps) if (a.Name == "FakeApp") f = a;
                    if (f == null || f.Category != "开发")
                    {
                        Check(false, "重复加载保持覆盖（第 " + (i + 1) + " 次）",
                            f == null ? "null" : f.Category);
                        break;
                    }
                    if (i == 2) Check(true, "重复加载 3 次保持覆盖");
                }

                // 恢复自动分类
                ov.Remove(exePath);
                Catalog c3 = Catalog.Load(r3);
                AppItem found3 = null;
                foreach (AppItem a in c3.Apps) if (a.Name == "FakeApp") found3 = a;
                Check(found3 != null && found3.Category == "其他",
                    "移除覆盖后回到默认分类",
                    found3 == null ? "null" : found3.Category);
            }
            finally
            {
                try { Directory.Delete(r3, true); } catch { }
            }

            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
