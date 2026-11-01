using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 虚拟桌面存储测试：名称安全 / 坐标读写 / 桌面增删改名 / 持久化 / 容错。
    ///
    /// **全部用临时目录，不碰真实 config 与 desks。**
    /// </summary>
    static class VirtualDesksTest
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
            Console.WriteLine("=== 虚拟桌面存储测试 ===");
            Console.WriteLine();

            try
            {
                TestSafeName();
                TestDefaultRoot();
                TestPositions();
                TestEnsureAndForget();
                TestRenameDesk();
                TestPersistence();
                TestCorruptFile();
            }
            catch (Exception ex)
            {
                Check(false, "测试不抛异常", ex.GetType().Name + ": " + ex.Message);
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        static string NewRoot()
        {
            string d = Path.Combine(Path.GetTempPath(), "vdesk-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(d, "config"));
            return d;
        }

        static void Clean(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }

        // ---------- 1. 名称安全 ----------
        static void TestSafeName()
        {
            Console.WriteLine("--- 1. 名称安全 ---");

            Check(VirtualDesks.IsSafeName("工作"), "中文名通过");
            Check(VirtualDesks.IsSafeName("work-2024"), "英文数字横线通过");
            Check(VirtualDesks.IsSafeName("a b"), "含空格通过");

            // ---- 攻击向量 ----
            Check(!VirtualDesks.IsSafeName(".."), "★拒绝 ..");
            Check(!VirtualDesks.IsSafeName("."), "★拒绝 .");
            Check(!VirtualDesks.IsSafeName("a\\b"), "★拒绝反斜杠");
            Check(!VirtualDesks.IsSafeName("a/b"), "★拒绝正斜杠");
            Check(!VirtualDesks.IsSafeName("C:name"), "★拒绝冒号");
            Check(!VirtualDesks.IsSafeName("a*b"), "★拒绝星号");
            Check(!VirtualDesks.IsSafeName("a?b"), "★拒绝问号");
            Check(!VirtualDesks.IsSafeName("a|b"), "★拒绝竖线");
            Check(!VirtualDesks.IsSafeName("a<b"), "★拒绝尖括号");

            Check(!VirtualDesks.IsSafeName(""), "拒绝空串");
            Check(!VirtualDesks.IsSafeName(null), "拒绝 null");
            Check(!VirtualDesks.IsSafeName("   "), "拒绝全空白");
            Check(!VirtualDesks.IsSafeName(" a"), "★拒绝首空格");
            Check(!VirtualDesks.IsSafeName("a "), "★拒绝尾空格");
            Check(!VirtualDesks.IsSafeName("a."), "★拒绝尾点");

            string longName = new string('x', 80);
            Check(!VirtualDesks.IsSafeName(longName), "★拒绝超长名", Convert.ToString(longName.Length));

            Console.WriteLine();
        }

        // ---------- 2. 默认根目录 ----------
        static void TestDefaultRoot()
        {
            Console.WriteLine("--- 2. 默认根目录 ---");

            string r = VirtualDesks.DefaultRoot(@"C:\App");
            Check(r == @"C:\App\desks", "★默认在程序目录下 desks", r);

            Check(VirtualDesks.DefaultRoot(null) == "", "null → 空");
            Check(VirtualDesks.DefaultRoot("") == "", "空 → 空");

            Console.WriteLine();
        }

        // ---------- 3. 坐标 ----------
        static void TestPositions()
        {
            Console.WriteLine("--- 3. 坐标读写 ---");

            string root = NewRoot();
            try
            {
                VirtualDesks v = new VirtualDesks(root);
                v.Load();
                v.Root = VirtualDesks.DefaultRoot(root);

                VirtualDesks.Desk d = v.EnsureDesk("工作");
                Check(d != null, "★建桌面成功");

                double x, y;
                Check(!v.TryGetPos("工作", "a.docx", out x, out y), "初始没有坐标");

                v.SetPos("工作", "a.docx", 40, 30);
                Check(v.TryGetPos("工作", "a.docx", out x, out y), "★写入后可读");
                Check(Math.Abs(x - 40) < 0.01 && Math.Abs(y - 30) < 0.01,
                    "★坐标值正确", x + "," + y);

                // 负数钳到 0
                v.SetPos("工作", "b.docx", -50, -20);
                v.TryGetPos("工作", "b.docx", out x, out y);
                Check(x == 0 && y == 0, "★负数坐标钳到 0", x + "," + y);

                // 非法文件名
                v.SetPos("工作", "..", 10, 10);
                Check(!v.TryGetPos("工作", "..", out x, out y), "★非法文件名不记录");

                // 删除坐标
                v.RemovePos("工作", "a.docx");
                Check(!v.TryGetPos("工作", "a.docx", out x, out y), "★删除后读不到");

                // 不存在的桌面
                Check(!v.TryGetPos("不存在", "a.docx", out x, out y), "不存在的桌面 → false");

                Console.WriteLine();
            }
            finally { Clean(root); }
        }

        // ---------- 4. 桌面增删 ----------
        static void TestEnsureAndForget()
        {
            Console.WriteLine("--- 4. 桌面增删 ---");

            string root = NewRoot();
            try
            {
                VirtualDesks v = new VirtualDesks(root);
                v.Load();
                v.Root = VirtualDesks.DefaultRoot(root);

                VirtualDesks.Desk d = v.EnsureDesk("学习");
                Check(d != null, "建桌面");
                Check(Directory.Exists(v.FolderOf("学习")), "★磁盘文件夹已建出", v.FolderOf("学习"));

                // 重复 Ensure 幂等
                VirtualDesks.Desk d2 = v.EnsureDesk("学习");
                Check(ReferenceEquals(d, d2), "★重复 Ensure 返回同一对象");

                // 非法名
                Check(v.EnsureDesk("..") == null, "★非法名 → null");
                Check(v.EnsureDesk("") == null, "空名 → null");

                // Forget 只删记录，不动文件
                string f = Path.Combine(v.FolderOf("学习"), "keep.txt");
                File.WriteAllText(f, "x");
                v.ForgetDesk("学习");
                Check(!v.Desks.ContainsKey("学习"), "★Forget 删掉记录");
                Check(File.Exists(f), "★Forget 不动磁盘文件");

                Console.WriteLine();
            }
            finally { Clean(root); }
        }

        // ---------- 5. 桌面改名 ----------
        static void TestRenameDesk()
        {
            Console.WriteLine("--- 5. 桌面改名 ---");

            string root = NewRoot();
            try
            {
                VirtualDesks v = new VirtualDesks(root);
                v.Load();
                v.Root = VirtualDesks.DefaultRoot(root);

                v.EnsureDesk("旧名");
                string f = Path.Combine(v.FolderOf("旧名"), "a.txt");
                File.WriteAllText(f, "x");
                v.SetPos("旧名", "a.txt", 12, 34);

                string e = v.RenameDesk("旧名", "新名");
                Check(e == null, "★改名成功", e ?? "");
                Check(v.Desks.ContainsKey("新名"), "新名在");
                Check(!v.Desks.ContainsKey("旧名"), "旧名不在");
                Check(Directory.Exists(v.FolderOf("新名")), "★文件夹跟着改名");
                Check(File.Exists(Path.Combine(v.FolderOf("新名"), "a.txt")), "★文件还在");

                double x, y;
                Check(v.TryGetPos("新名", "a.txt", out x, out y), "★坐标跟着走");

                // 改成已存在的名字
                v.EnsureDesk("另一个");
                Check(v.RenameDesk("新名", "另一个") != null, "★重名被拒绝");

                // 非法名
                Check(v.RenameDesk("新名", "..") != null, "★非法名被拒绝");

                // 改名不存在
                Check(v.RenameDesk("不存在", "x") != null, "改名不存在的被拒绝");

                Console.WriteLine();
            }
            finally { Clean(root); }
        }

        // ---------- 6. 持久化 ----------
        static void TestPersistence()
        {
            Console.WriteLine("--- 6. 持久化 ---");

            string root = NewRoot();
            try
            {
                VirtualDesks v = new VirtualDesks(root);
                v.Load();
                v.Root = VirtualDesks.DefaultRoot(root);
                v.EnsureDesk("工作");
                v.SetPos("工作", "a.docx", 100, 200);
                v.SetPos("工作", "b.xlsx", 300, 400);
                v.Save();

                string file = Path.Combine(root, "config", "virtual-desks.json");
                Check(File.Exists(file), "★文件已写出");

                VirtualDesks v2 = new VirtualDesks(root);
                v2.Load();
                Check(v2.Root == v.Root, "★root 已持久化", v2.Root);
                Check(v2.Desks.ContainsKey("工作"), "★桌面已持久化");

                double x, y;
                Check(v2.TryGetPos("工作", "a.docx", out x, out y), "★坐标已持久化");
                Check(Math.Abs(x - 100) < 0.01 && Math.Abs(y - 200) < 0.01,
                    "★坐标值正确", x + "," + y);

                Console.WriteLine();
            }
            finally { Clean(root); }
        }

        // ---------- 7. 容错 ----------
        static void TestCorruptFile()
        {
            Console.WriteLine("--- 7. 容错 ---");

            string root = NewRoot();
            try
            {
                string file = Path.Combine(root, "config", "virtual-desks.json");

                File.WriteAllText(file, "{ 坏 JSON", new UTF8Encoding(false));
                VirtualDesks v = new VirtualDesks(root);
                v.Load();
                Check(v.Desks.Count == 0, "★坏 JSON → 空，不抛异常");

                File.WriteAllText(file, "{\"root\":\"X:\\\\d\",\"desks\":{}}", new UTF8Encoding(false));
                v.Load();
                Check(v.Root == @"X:\d", "root 读到");

                // 含非法桌面名 → 跳过
                File.WriteAllText(file,
                    "{\"root\":\"R\",\"desks\":{\"..\":{\"items\":[]},\"正常\":{\"items\":[]}}}",
                    new UTF8Encoding(false));
                v.Load();
                Check(!v.Desks.ContainsKey(".."), "★非法桌面名被跳过");
                Check(v.Desks.ContainsKey("正常"), "正常桌面保留");

                // 含非法文件名 → 跳过
                File.WriteAllText(file,
                    "{\"root\":\"R\",\"desks\":{\"d\":{\"items\":[{\"name\":\"..\",\"x\":1,\"y\":2},{\"name\":\"ok.txt\",\"x\":5,\"y\":6}]}}}",
                    new UTF8Encoding(false));
                v.Load();
                double x, y;
                Check(!v.TryGetPos("d", "..", out x, out y), "★非法文件名被跳过");
                Check(v.TryGetPos("d", "ok.txt", out x, out y), "★正常文件名保留");

                Console.WriteLine();
            }
            finally { Clean(root); }
        }
    }
}
