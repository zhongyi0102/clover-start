using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 分类顺序与重命名的测试（config\categories.json）。
    ///
    /// 覆盖四件事：
    ///   1. 重命名只改显示名：内部名不变，所以 apps.json / 规则表 / 覆盖表都不受影响
    ///   2. 顺序：只调整已存在的分类相对次序，未提及的接在后面
    ///   3. 边界：上移到顶 / 下移到底 / 改回原名 / 空值
    ///   4. 健壮性：文件缺失或损坏时回退到默认（不崩、不阻断启动）
    /// </summary>
    static class CategoriesTest
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
            string root = Path.Combine(Path.GetTempPath(), "clover-cats-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            return root;
        }

        static void WriteApps(string root, string[][] rows)
        {
            List<object> arr = new List<object>();
            foreach (string[] r in rows)
            {
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

        static void Main()
        {
            // ================= 1. 基本读写 =================
            {
                string root = NewRoot();
                try
                {
                    Categories c = Categories.Load(root);
                    Check(c.RenameCount == 0, "新目录没有改名");
                    Check(c.OrderCount == 0, "新目录没有自定义顺序");
                    Eq(c.DisplayName("办公"), "办公", "未改名时显示原名");
                    Check(!c.IsRenamed("办公"), "未改名时 IsRenamed=false");
                    Eq(c.DisplayName(""), "", "空名原样返回");

                    Check(c.Rename("办公", "工作"), "Rename 返回 true");
                    Eq(c.DisplayName("办公"), "工作", "改名后显示新名");
                    Check(c.IsRenamed("办公"), "改名后 IsRenamed=true");
                    Check(!c.Rename("办公", "工作"), "改成同一个值返回 false");

                    // 改回去
                    Check(c.Rename("办公", null), "传 null 表示恢复原名");
                    Eq(c.DisplayName("办公"), "办公", "恢复后显示原名");
                    Check(!c.IsRenamed("办公"), "恢复后 IsRenamed=false");
                    Check(!c.Rename("办公", "办公"), "改成与内部名相同 = 不记录");
                    Check(!c.Rename("", "x"), "空内部名不记录");
                    Check(!c.Rename(null, "x"), "null 内部名不记录");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 2. 落盘 + 重新加载保持 =================
            {
                string root = NewRoot();
                try
                {
                    Categories c = Categories.Load(root);
                    c.Rename("影音娱乐", "游戏");
                    c.Rename("开发", "编程");

                    Categories re = Categories.Load(root);
                    Check(re.RenameCount == 2, "落盘后重新加载保持 2 条改名", "实际 " + re.RenameCount);
                    Eq(re.DisplayName("影音娱乐"), "游戏", "第 1 条保持");
                    Eq(re.DisplayName("开发"), "编程", "第 2 条保持");
                    Eq(re.DisplayName("办公"), "办公", "没改的仍是原名");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 3. 顺序：应用自定义顺序 =================
            {
                string root = NewRoot();
                try
                {
                    Categories c = Categories.Load(root);

                    // 用户想把「其他」挪到最前
                    Check(c.SaveOrder(new List<string>(new string[] { "其他", "办公", "开发" })), "SaveOrder 返回 true");
                    Check(!c.SaveOrder(new List<string>(new string[] { "其他", "办公", "开发" })), "相同顺序返回 false");

                    Categories re = Categories.Load(root);
                    List<string> cats = new List<string>(new string[]
                        { "办公", "开发", "网络通讯", "影音娱乐", "系统工具", "工具/命令行", "其他" });
                    re.ApplyOrder(cats);

                    Eq(cats[0], "其他", "「其他」被挪到最前");
                    Eq(cats[1], "办公", "「办公」第 2");
                    Eq(cats[2], "开发", "「开发」第 3");
                    // 未提及的保持原有相对次序接在后面
                    Eq(cats[3], "网络通讯", "未提及的分类接在后面（第 1 个）");
                    Eq(cats[4], "影音娱乐", "未提及的分类接在后面（第 2 个）");
                    Check(cats.Contains("工具/命令行"), "未提及的分类不会丢");
                    Check(cats.Count == 7, "分类总数不变", "实际 " + cats.Count);
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 4. 顺序：未知分类名不出乱子 =================
            {
                string root = NewRoot();
                try
                {
                    Categories c = Categories.Load(root);
                    // 手写文件里放了已不存在的分类名
                    c.SaveOrder(new List<string>(new string[] { "已卸载的旧分类", "开发", "办公" }));

                    List<string> cats = new List<string>(new string[] { "办公", "开发", "其他" });
                    c.ApplyOrder(cats);

                    Check(cats.Count == 3, "未知分类名不会凭空加进来", "实际 " + cats.Count);
                    Eq(cats[0], "开发", "存在的分类仍按保存顺序排（开发在前）");
                    Eq(cats[1], "办公", "存在的分类仍按保存顺序排（办公在后）");
                    Eq(cats[2], "其他", "未提及的接在最后");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 5. 上移 / 下移 =================
            {
                string root = NewRoot();
                try
                {
                    Categories c = Categories.Load(root);
                    List<string> cats = new List<string>(new string[] { "A", "B", "C" });

                    Check(c.MoveCategory(cats, "B", -1), "B 上移返回 true");
                    Eq(string.Join(",", cats.ToArray()), "B,A,C", "B 上移一格");

                    Check(c.MoveCategory(cats, "C", -1), "C 上移返回 true");
                    Eq(string.Join(",", cats.ToArray()), "B,C,A", "C 也上移一格");

                    Check(!c.MoveCategory(cats, "B", -1), "已在第 1 位再上移返回 false");
                    Eq(string.Join(",", cats.ToArray()), "B,C,A", "到头了不上移");

                    Check(!c.MoveCategory(cats, "A", +1), "已在最后再下移返回 false");
                    Eq(string.Join(",", cats.ToArray()), "B,C,A", "到底了不下移");

                    Check(!c.MoveCategory(cats, "不存在", -1), "未知分类返回 false");
                    Check(!c.MoveCategory(null, "B", -1), "null 列表返回 false");
                    Check(!c.MoveCategory(cats, "B", 0), "delta=0 返回 false");

                    // 移过之后要落盘
                    Categories re = Categories.Load(root);
                    Check(re.OrderCount == 3, "移动结果已落盘", "实际 " + re.OrderCount);
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 6. Clear =================
            {
                string root = NewRoot();
                try
                {
                    Categories c = Categories.Load(root);
                    c.Rename("办公", "工作");
                    c.SaveOrder(new List<string>(new string[] { "其他", "办公" }));
                    Check(c.Clear() == 3, "Clear 返回清掉的项数（1 改名 + 2 顺序）");

                    Categories re = Categories.Load(root);
                    Check(re.RenameCount == 0 && re.OrderCount == 0, "Clear 后都为 0");
                    Check(re.Clear() == 0, "空表再 Clear 返回 0");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 7. 损坏 / 异常输入回退 =================
            {
                string root = NewRoot();
                try
                {
                    string p = Categories.PathOf(root);

                    File.WriteAllText(p, "{{{ 不是 JSON", new UTF8Encoding(false));
                    Categories broken = Categories.Load(root);
                    Check(broken != null && broken.RenameCount == 0 && broken.OrderCount == 0,
                        "坏 JSON 回退为空（不崩）");
                    Eq(broken.DisplayName("办公"), "办公", "坏 JSON 下显示原名");

                    File.WriteAllText(p, "", new UTF8Encoding(false));
                    Check(Categories.Load(root).RenameCount == 0, "空文件回退为空");

                    File.WriteAllText(p, "{\"version\":1}", new UTF8Encoding(false));
                    Check(Categories.Load(root).RenameCount == 0, "缺字段回退为空");

                    // renames 不是字典
                    File.WriteAllText(p, "{\"renames\":[1,2,3]}", new UTF8Encoding(false));
                    Check(Categories.Load(root).RenameCount == 0, "renames 类型不对时回退为空");

                    // order 不是数组
                    File.WriteAllText(p, "{\"order\":\"办公\"}", new UTF8Encoding(false));
                    Check(Categories.Load(root).OrderCount == 0, "order 类型不对时回退为空");

                    // 空 key / 空 value / 改成同名 都被跳过
                    File.WriteAllText(p,
                        "{\"renames\":{\"\":\"x\",\"办公\":\"\",\"开发\":\"开发\",\"其他\":\"杂项\"}}",
                        new UTF8Encoding(false));
                    Categories part = Categories.Load(root);
                    Check(part.RenameCount == 1, "跳过空 key / 空 value / 改成同名", "实际 " + part.RenameCount);
                    Eq(part.DisplayName("其他"), "杂项", "有效改名被保留");

                    // 损坏后仍能写入
                    part.Rename("办公", "工作");
                    Check(Categories.Load(root).RenameCount == 2, "损坏后写入仍能保存");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 8. ★关键：改名不影响分类归属 =================
            // 内部名不变，所以 apps.json 里的 category、规则表、覆盖表都不用迁移。
            {
                string root = NewRoot();
                try
                {
                    WriteApps(root, new string[][]
                    {
                        new string[] { "Word",  "w.exe", "办公" },
                        new string[] { "Code",  "c.exe", "开发" },
                    });

                    // 先把「办公」改名
                    Categories.Load(root).Rename("办公", "我的工作");

                    Catalog cat = Catalog.Load(root);

                    // 归类仍按内部名「办公」工作
                    Check(cat.ByCategory("办公").Count == 1, "按内部名仍能取到该分类的应用",
                        "实际 " + cat.ByCategory("办公").Count);
                    Check(cat.ByCategory("我的工作").Count == 0, "用显示名取不到（显示名不是内部键）");

                    bool hasOffice = false;
                    foreach (string s in cat.ActiveCategories()) if (s == "办公") hasOffice = true;
                    Check(hasOffice, "ActiveCategories 仍返回内部名");

                    // 条目上的 Category 也没被改
                    foreach (AppItem a in cat.Apps)
                    {
                        if (a.Name == "Word" && a.Category != "办公")
                        {
                            Check(false, "条目的 Category 不该被改成显示名", "实际 " + a.Category);
                            break;
                        }
                    }
                    Check(true, "条目的 Category 保持内部名");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 9. Catalog.Load 会应用自定义顺序 =================
            {
                string root = NewRoot();
                try
                {
                    WriteApps(root, new string[][]
                    {
                        new string[] { "A", "a.exe", "办公" },
                        new string[] { "B", "b.exe", "开发" },
                        new string[] { "C", "c.exe", "其他" },
                    });

                    // 默认顺序下「办公」在「其他」前面
                    Catalog before = Catalog.Load(root);
                    int iOffice = before.Order.IndexOf("办公");
                    int iOther = before.Order.IndexOf("其他");
                    Check(iOffice >= 0 && iOther >= 0 && iOffice < iOther, "默认顺序：办公 在 其他 之前");

                    // 把「其他」挪到最前
                    Categories.Load(root).SaveOrder(new List<string>(new string[] { "其他", "办公", "开发" }));

                    Catalog after = Catalog.Load(root);
                    Eq(after.Order[0], "其他", "Catalog.Load 应用了自定义顺序");
                    Check(after.Order.IndexOf("办公") > 0, "「办公」不在首位了");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
