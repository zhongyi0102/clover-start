using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 隐藏应用功能的测试。
    ///
    /// 覆盖三件事：
    ///   1. 存储层：hide/show/clear、路径大小写、损坏回退
    ///   2. 目录层：隐藏项不进 ByCategory，但仍能从 HiddenItems() 取回
    ///   3. 边界：隐藏最后一个后「隐藏的项目」应为空、隐藏不该影响其它条目
    /// </summary>
    static class HiddenAppsTest
    {
        static int failed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name + (cond || detail.Length == 0 ? "" : "  [" + detail + "]"));
            if (!cond) failed++;
        }

        static void Eq(string got, string want, string name)
        {
            Check(got == want, name, "实际=" + got + " 期望=" + want);
        }

        static string NewRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "clover-hidden-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            return root;
        }

        /// <summary>
        /// 写一份 apps.json。注意：Catalog.ReadInto 会跳过**磁盘上不存在**的路径
        /// （`if (!File.Exists(a.Path)) continue`），所以这里必须真的建出那些文件，
        /// 否则清单会是空的。这是测试的硬约束，不是实现的问题。
        /// </summary>
        static void WriteApps(string root, string[][] rows)
        {
            List<object> arr = new List<object>();
            foreach (string[] r in rows)
            {
                // 用 root 下的真实文件，保证 File.Exists 成立
                string real = Path.Combine(root, r[1]);
                Directory.CreateDirectory(Path.GetDirectoryName(real));
                File.WriteAllText(real, "x", new UTF8Encoding(false));

                Dictionary<string, object> m = new Dictionary<string, object>();
                m["name"] = r[0];
                m["path"] = real;
                m["category"] = r[2];
                arr.Add(m);
            }
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["order"] = new List<string>();
            d["apps"] = arr;
            System.Web.Script.Serialization.JavaScriptSerializer ser =
                new System.Web.Script.Serialization.JavaScriptSerializer();
            File.WriteAllText(Path.Combine(root, "config", "apps.json"),
                ser.Serialize(d), new UTF8Encoding(false));
        }

        /// <summary>取上一步 WriteApps 为某个短名建出的真实路径。</summary>
        static string P(string root, string shortName)
        {
            return Path.Combine(root, shortName);
        }

        static void Main()
        {
            // ================= 1. 存储层基本操作 =================
            {
                string root = NewRoot();
                try
                {
                    HiddenApps h = HiddenApps.Load(root);
                    Check(h.Count == 0, "新目录为空");
                    Check(!h.IsHidden(@"C:\a.exe"), "未隐藏时 IsHidden=false");

                    Check(h.Hide(@"C:\a.exe"), "Hide 新条目返回 true");
                    Check(h.IsHidden(@"C:\a.exe"), "Hide 后 IsHidden=true");
                    Check(!h.Hide(@"C:\a.exe"), "重复 Hide 返回 false");

                    // 路径大小写不敏感（Windows 上同一个文件可能大小写不同）
                    Check(h.IsHidden(@"C:\A.EXE"), "路径大小写不敏感");

                    Check(h.Show(@"C:\a.exe"), "Show 已隐藏的返回 true");
                    Check(!h.IsHidden(@"C:\a.exe"), "Show 后 IsHidden=false");
                    Check(!h.Show(@"C:\a.exe"), "重复 Show 返回 false");

                    Check(!h.Hide(""), "空路径不隐藏");
                    Check(!h.IsHidden(""), "空路径 IsHidden=false");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 2. 落盘 + 重新加载保持 =================
            {
                string root = NewRoot();
                try
                {
                    HiddenApps h = HiddenApps.Load(root);
                    h.Hide(@"C:\x\one.exe");
                    h.Hide(@"C:\x\two.exe");

                    HiddenApps re = HiddenApps.Load(root);
                    Check(re.Count == 2, "落盘后重新加载保持 2 条", "实际 " + re.Count);
                    Check(re.IsHidden(@"C:\x\one.exe"), "第 1 条保持");
                    Check(re.IsHidden(@"C:\x\two.exe"), "第 2 条保持");

                    // Clear 清空全部
                    Check(re.Clear() == 2, "Clear 返回清掉的数量");
                    Check(re.Count == 0, "Clear 后为空");
                    Check(HiddenApps.Load(root).Count == 0, "Clear 落盘后仍为空");
                    Check(re.Clear() == 0, "空表再 Clear 返回 0");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 3. 损坏文件回退 =================
            {
                string root = NewRoot();
                try
                {
                    string p = HiddenApps.PathOf(root);

                    File.WriteAllText(p, "{{{ 不是 JSON", new UTF8Encoding(false));
                    HiddenApps broken = HiddenApps.Load(root);
                    Check(broken != null && broken.Count == 0, "坏 JSON 回退为空");
                    Check(!broken.IsHidden(@"C:\anything.exe"), "坏 JSON 下什么都不隐藏");

                    File.WriteAllText(p, "", new UTF8Encoding(false));
                    Check(HiddenApps.Load(root).Count == 0, "空文件回退为空");

                    File.WriteAllText(p, "{\"version\":1}", new UTF8Encoding(false));
                    Check(HiddenApps.Load(root).Count == 0, "缺 paths 字段回退为空");

                    File.WriteAllText(p, "{\"paths\":[1,2,3]}", new UTF8Encoding(false));
                    HiddenApps nums = HiddenApps.Load(root);
                    Check(nums.Count == 3, "非字符串元素被 Convert.ToString 后保留", "实际 " + nums.Count);

                    File.WriteAllText(p, "{\"paths\":[\"\",\"C:\\\\ok.exe\"]}", new UTF8Encoding(false));
                    HiddenApps part = HiddenApps.Load(root);
                    Check(part.Count == 1, "空字符串被跳过，只留有效项", "实际 " + part.Count);
                    Check(part.IsHidden(@"C:\ok.exe"), "有效项可查");

                    // 损坏后仍能写入（自愈）
                    part.Hide(@"C:\new.exe");
                    Check(HiddenApps.Load(root).Count == 2, "损坏后写入仍能保存");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 4. 目录层：隐藏项不进 ByCategory =================
            {
                string root = NewRoot();
                try
                {
                    WriteApps(root, new string[][]
                    {
                        new string[] { "Alpha", "a.exe", "办公" },
                        new string[] { "Beta",  "b.exe", "办公" },
                        new string[] { "Gamma", "g.exe", "开发" },
                    });

                    Catalog c1 = Catalog.Load(root);
                    Check(c1.ByCategory("办公").Count == 2, "未隐藏时「办公」有 2 个");
                    Check(c1.ByCategory("").Count == 3, "未隐藏时「全部」有 3 个");
                    Check(c1.HiddenItems().Count == 0, "未隐藏时隐藏列表为空");

                    HiddenApps.Load(root).Hide(P(root, "a.exe"));

                    Catalog c2 = Catalog.Load(root);
                    Check(c2.ByCategory("办公").Count == 1, "隐藏后「办公」变 1 个", "实际 " + c2.ByCategory("办公").Count);
                    Check(c2.ByCategory("").Count == 2, "隐藏后「全部」变 2 个", "实际 " + c2.ByCategory("").Count);

                    // 关键：隐藏项仍在 Apps 里，能从 HiddenItems 取回
                    Check(c2.Apps.Count == 3, "隐藏项仍留在 Apps 里（总数不变）", "实际 " + c2.Apps.Count);
                    Check(c2.HiddenItems().Count == 1, "HiddenItems 能取回隐藏项");
                    Eq(c2.HiddenItems()[0].Name, "Alpha", "HiddenItems 返回的就是被隐藏的那个");

                    // includeHidden=true 时不排除
                    Check(c2.ByCategory("办公", true).Count == 2, "ByCategory(cat,true) 包含隐藏项");
                    Check(c2.ByCategory("", true).Count == 3, "ByCategory('',true) 包含隐藏项");

                    // 隐藏标记确实打到条目上
                    bool flagged = false;
                    foreach (AppItem a in c2.Apps) if (a.Name == "Alpha" && a.Hidden) flagged = true;
                    Check(flagged, "被隐藏条目的 Hidden 标记为 true");

                    // 不该误伤别的条目
                    foreach (AppItem a in c2.Apps)
                    {
                        if (a.Name == "Beta" && a.Hidden) { Check(false, "Beta 不该被隐藏"); break; }
                        if (a.Name == "Gamma" && a.Hidden) { Check(false, "Gamma 不该被隐藏"); break; }
                    }
                    Check(true, "隐藏一个不影响其它条目");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 5. ActiveCategories 不含全隐藏的分类 =================
            {
                string root = NewRoot();
                try
                {
                    WriteApps(root, new string[][]
                    {
                        new string[] { "Alpha", "a.exe", "办公" },
                        new string[] { "Gamma", "g.exe", "开发" },
                    });

                    HiddenApps.Load(root).Hide(P(root, "a.exe"));

                    Catalog c = Catalog.Load(root);
                    List<string> active = c.ActiveCategories();
                    bool hasOffice = false, hasDev = false;
                    foreach (string s in active)
                    {
                        if (s == "办公") hasOffice = true;
                        if (s == "开发") hasDev = true;
                    }
                    Check(!hasOffice, "整类被隐藏后不出现在分类栏");
                    Check(hasDev, "还有可见项的分类照常出现");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 6. 全部隐藏后再取消 =================
            {
                string root = NewRoot();
                try
                {
                    WriteApps(root, new string[][]
                    {
                        new string[] { "Only", "only.exe", "其他" },
                    });

                    HiddenApps h = HiddenApps.Load(root);
                    h.Hide(P(root, "only.exe"));
                    Catalog c1 = Catalog.Load(root);
                    Check(c1.HiddenItems().Count == 1, "唯一应用被隐藏后 HiddenItems=1");
                    Check(c1.ByCategory("").Count == 0, "此时「全部」为空");
                    Check(c1.ActiveCategories().Count == 0, "此时没有活跃分类");

                    h.Show(P(root, "only.exe"));
                    Catalog c2 = Catalog.Load(root);
                    Check(c2.HiddenItems().Count == 0, "取消隐藏后 HiddenItems=0");
                    Check(c2.ByCategory("").Count == 1, "取消隐藏后「全部」恢复 1 个");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 7. 和「隐藏分类」并存 =================
            {
                string root = NewRoot();
                try
                {
                    WriteApps(root, new string[][]
                    {
                        new string[] { "A", "a.exe", "办公" },
                        new string[] { "B", "b.exe", "开发" },
                        new string[] { "C", "c.exe", "开发" },
                    });

                    HiddenApps.Load(root).Hide(P(root, "b.exe"));

                    Catalog c = Catalog.Load(root);
                    // 先隐藏分类「开发」（整类消失），再确认单个隐藏也还在
                    c.HideCategories(new List<string>(new string[] { "开发" }));
                    Check(c.ByCategory("开发").Count == 0, "整类隐藏后该分类为空");

                    // 办公不受影响
                    Check(c.ByCategory("办公").Count == 1, "另一个分类不受影响");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
