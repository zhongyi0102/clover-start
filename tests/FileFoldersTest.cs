using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// FileFolders 测试：自定义文件夹清单的增删改查、路径校验、去重、上限、容错。
    ///
    /// **全部用临时目录**，不碰真实桌面或用户文件夹。
    /// 网络路径只做「判定为网络」的断言，不实际访问（那会很慢且依赖网络）。
    /// </summary>
    static class FileFoldersTest
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
            Console.WriteLine("=== FileFolders 测试 ===");
            Console.WriteLine();

            FileFolders.Warn = delegate(string m) { /* 测试里不打印告警，避免噪音 */ };

            TestEmptyAndMissing();
            TestAddValid();
            TestAddRejections();
            TestNormalize();
            TestDeduplication();
            TestMaxLimit();
            TestRename();
            TestRemove();
            TestAvailability();
            TestCorruptAndBak();
            TestAtomicWrite();
            TestNetworkDetection();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 空 / 文件不存在 ----------
        static void TestEmptyAndMissing()
        {
            Console.WriteLine("--- 1. 空清单 / 文件不存在 ---");

            string root = NewRoot();
            try
            {
                // 没有 file-folders.json
                FileFolders f = FileFolders.Load(root);
                Check(f != null, "Load 返回非 null");
                Check(f.Items.Count == 0, "★无文件时为空列表", Convert.ToString(f.Items.Count));

                // 保存空清单后再读
                f.Save();
                FileFolders f2 = FileFolders.Load(root);
                Check(f2.Items.Count == 0, "空清单往返仍为空");
                Check(File.Exists(FileFolders.PathOf(root)), "文件已创建");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 2. 添加合法目录 ----------
        static void TestAddValid()
        {
            Console.WriteLine("--- 2. 添加合法目录 ---");

            string root = NewRoot();
            try
            {
                string d1 = Path.Combine(root, "proj");
                string d2 = Path.Combine(root, "photos");
                Directory.CreateDirectory(d1);
                Directory.CreateDirectory(d2);

                FileFolders f = FileFolders.Load(root);
                FileFolders.AddResult r1 = f.Add(d1);
                Check(r1 == FileFolders.AddResult.Ok, "★添加合法目录返回 Ok", r1.ToString());
                Check(f.Items.Count == 1, "清单 1 条", Convert.ToString(f.Items.Count));
                Check(f.Items[0].Name == "proj", "★默认显示名 = 文件夹名", f.Items[0].Name);
                Check(f.Items[0].Path == d1, "路径正确", f.Items[0].Path);

                FileFolders.AddResult r2 = f.Add(d2);
                Check(r2 == FileFolders.AddResult.Ok, "添加第二个 Ok");
                Check(f.Items.Count == 2, "清单 2 条");

                // 持久化
                FileFolders f2 = FileFolders.Load(root);
                Check(f2.Items.Count == 2, "★重载后仍是 2 条", Convert.ToString(f2.Items.Count));
                Check(f2.Items[1].Name == "photos", "第二个名字正确", f2.Items[1].Name);
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 3. 拒绝非法路径 ----------
        static void TestAddRejections()
        {
            Console.WriteLine("--- 3. 拒绝非法路径 ---");

            string root = NewRoot();
            try
            {
                FileFolders f = FileFolders.Load(root);

                // 空
                Check(f.Add(null) == FileFolders.AddResult.InvalidPath, "★null → InvalidPath");
                Check(f.Add("") == FileFolders.AddResult.InvalidPath, "★空串 → InvalidPath");
                Check(f.Add("   ") == FileFolders.AddResult.InvalidPath, "★空白 → InvalidPath");

                // 不存在
                string ghost = Path.Combine(root, "not-exist-xyz");
                Check(f.Add(ghost) == FileFolders.AddResult.NotExist, "★不存在的目录 → NotExist",
                    f.Add(ghost).ToString());

                // 是文件不是目录
                string file = Path.Combine(root, "a.txt");
                File.WriteAllText(file, "x", new UTF8Encoding(false));
                Check(f.Add(file) == FileFolders.AddResult.NotDirectory, "★文件 → NotDirectory",
                    f.Add(file).ToString());

                // UNC
                Check(f.Add(@"\\server\share") == FileFolders.AddResult.NetworkPath, "★UNC → NetworkPath");
                Check(f.Add(@"\\192.168.1.1\c$") == FileFolders.AddResult.NetworkPath, "★UNC(IP) → NetworkPath");

                Check(f.Items.Count == 0, "★全部被拒绝后清单仍为空", Convert.ToString(f.Items.Count));
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 4. 路径归一化 ----------
        static void TestNormalize()
        {
            Console.WriteLine("--- 4. 路径归一化 ---");

            string norm;

            Check(FileFolders.TryNormalize(@"C:\a\b", out norm) && norm == @"C:\a\b", "普通路径不变", norm);
            Check(FileFolders.TryNormalize(@"C:/a/b", out norm) && norm == @"C:\a\b",
                "★正斜杠 → 反斜杠", norm);
            Check(FileFolders.TryNormalize(@"C:\a\b\", out norm) && norm == @"C:\a\b",
                "★去尾部斜杠", norm);
            Check(FileFolders.TryNormalize(@"C:\a\b\\\", out norm) && norm == @"C:\a\b",
                "多个尾部斜杠也去掉", norm);
            Check(FileFolders.TryNormalize(@"C:\", out norm) && norm == @"C:\",
                "★盘符根保留尾部斜杠", norm);
            Check(FileFolders.TryNormalize(@"  C:\a  ", out norm) && norm == @"C:\a",
                "首尾空格去掉", norm);
            Check(FileFolders.TryNormalize("\"C:\\a\"", out norm) && norm == @"C:\a",
                "去掉包裹的引号", norm);

            Check(!FileFolders.TryNormalize(null, out norm), "null → false");
            Check(!FileFolders.TryNormalize("", out norm), "空串 → false");
            Console.WriteLine();
        }

        // ---------- 5. 去重 ----------
        static void TestDeduplication()
        {
            Console.WriteLine("--- 5. 归一化后去重 ---");

            string root = NewRoot();
            try
            {
                string d = Path.Combine(root, "dup");
                Directory.CreateDirectory(d);

                FileFolders f = FileFolders.Load(root);
                Check(f.Add(d) == FileFolders.AddResult.Ok, "首次添加 Ok");

                Check(f.Add(d) == FileFolders.AddResult.Duplicate, "★完全相同 → Duplicate");
                Check(f.Add(d.ToUpperInvariant()) == FileFolders.AddResult.Duplicate,
                    "★大小写不同 → Duplicate", d.ToUpperInvariant());
                Check(f.Add(d + "\\") == FileFolders.AddResult.Duplicate, "★尾部斜杠 → Duplicate");
                Check(f.Add(d.Replace('\\', '/')) == FileFolders.AddResult.Duplicate,
                    "★正斜杠 → Duplicate", d.Replace('\\', '/'));
                Check(f.Add("  " + d + "  ") == FileFolders.AddResult.Duplicate, "首尾空格 → Duplicate");

                Check(f.Items.Count == 1, "★去重后只有 1 条", Convert.ToString(f.Items.Count));
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 6. 上限 ----------
        static void TestMaxLimit()
        {
            Console.WriteLine("--- 6. 上限 " + FileFolders.MaxFolders + " 个 ---");

            string root = NewRoot();
            try
            {
                FileFolders f = FileFolders.Load(root);

                for (int i = 0; i < FileFolders.MaxFolders; i++)
                {
                    string d = Path.Combine(root, "d" + i);
                    Directory.CreateDirectory(d);
                    FileFolders.AddResult r = f.Add(d);
                    if (r != FileFolders.AddResult.Ok)
                    {
                        Check(false, "第 " + (i + 1) + " 个应成功", r.ToString());
                        break;
                    }
                }
                Check(f.Items.Count == FileFolders.MaxFolders,
                    "★恰好 " + FileFolders.MaxFolders + " 条", Convert.ToString(f.Items.Count));

                // 第 11 个应被拒
                string extra = Path.Combine(root, "extra");
                Directory.CreateDirectory(extra);
                Check(f.Add(extra) == FileFolders.AddResult.TooMany, "★超过上限 → TooMany");
                Check(f.Items.Count == FileFolders.MaxFolders, "清单未被撑大", Convert.ToString(f.Items.Count));
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 7. 重命名 ----------
        static void TestRename()
        {
            Console.WriteLine("--- 7. 重命名 / 重名加序号 ---");

            string root = NewRoot();
            try
            {
                string d1 = Path.Combine(root, "alpha");
                string d2 = Path.Combine(root, "beta");
                Directory.CreateDirectory(d1);
                Directory.CreateDirectory(d2);

                FileFolders f = FileFolders.Load(root);
                f.Add(d1);
                f.Add(d2);

                // 改名
                Check(f.Rename(d1, "项目"), "★重命名成功");
                Check(f.Items[0].Name == "项目", "新名字生效", f.Items[0].Name);

                // 重名 → 自动加 (2)
                Check(f.Rename(d2, "项目"), "★改成同名也算成功");
                Check(f.Items[1].Name == "项目 (2)", "★重名自动加 (2)", f.Items[1].Name);

                // 再改一个同名 → (3)
                string d3 = Path.Combine(root, "gamma");
                Directory.CreateDirectory(d3);
                f.Add(d3);
                f.Rename(d3, "项目");
                Check(f.Items[2].Name == "项目 (3)", "★第三个重名加 (3)", f.Items[2].Name);

                // 空名 → 恢复默认
                Check(f.Rename(d1, ""), "空名也算成功");
                Check(f.Items[0].Name == "alpha", "★空名恢复默认（文件夹名）", f.Items[0].Name);

                // 改自己成原来的名字不该加序号
                Check(f.Rename(d2, "项目 (2)"), "改成自己当前的名字");
                Check(f.Items[1].Name == "项目 (2)", "★改自己不加序号", f.Items[1].Name);

                // 不存在的路径
                Check(!f.Rename(Path.Combine(root, "nope"), "x"), "不存在的路径 → false");

                // 持久化
                FileFolders f2 = FileFolders.Load(root);
                Check(f2.Items[0].Name == "alpha", "重载后名字保留", f2.Items[0].Name);
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 8. 移除不碰磁盘 ----------
        static void TestRemove()
        {
            Console.WriteLine("--- 8. 移除只删记录，不碰磁盘 ---");

            string root = NewRoot();
            try
            {
                string d = Path.Combine(root, "keep");
                Directory.CreateDirectory(d);
                string inside = Path.Combine(d, "inner.txt");
                File.WriteAllText(inside, "hello", new UTF8Encoding(false));

                FileFolders f = FileFolders.Load(root);
                f.Add(d);
                Check(f.Items.Count == 1, "先添加 1 条");

                Check(f.Remove(d), "★移除成功");
                Check(f.Items.Count == 0, "清单已空");
                Check(Directory.Exists(d), "★目录本身仍在");
                Check(File.Exists(inside), "★目录里的文件仍在");

                // 移除不存在的
                Check(!f.Remove(Path.Combine(root, "nope")), "移除不存在 → false");

                // 持久化
                FileFolders f2 = FileFolders.Load(root);
                Check(f2.Items.Count == 0, "重载后仍为空");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 9. 失效路径 ----------
        static void TestAvailability()
        {
            Console.WriteLine("--- 9. 路径失效 ---");

            string root = NewRoot();
            try
            {
                string d = Path.Combine(root, "vanish");
                Directory.CreateDirectory(d);

                FileFolders f = FileFolders.Load(root);
                f.Add(d);

                Check(FileFolders.IsAvailable(d), "存在 → IsAvailable true");

                // 删掉目录（模拟外置盘拔出 / 用户删除）
                Directory.Delete(d, true);

                Check(!FileFolders.IsAvailable(d), "★目录已删 → IsAvailable false");

                // 关键：记录必须保留
                FileFolders f2 = FileFolders.Load(root);
                Check(f2.Items.Count == 1, "★失效后记录仍保留（不自动删）", Convert.ToString(f2.Items.Count));
                Check(f2.Items[0].Path == d, "路径原样保留");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 10. 损坏 JSON / .bak 回退 ----------
        static void TestCorruptAndBak()
        {
            Console.WriteLine("--- 10. 损坏 JSON / .bak 回退 ---");

            // 10a. 主文件损坏且无 .bak → 空列表
            string root = NewRoot();
            try
            {
                string p = FileFolders.PathOf(root);
                File.WriteAllText(p, "{ 这不是 JSON", new UTF8Encoding(false));

                bool threw = false;
                FileFolders f = null;
                try { f = FileFolders.Load(root); } catch { threw = true; }

                Check(!threw, "★损坏 JSON 不抛异常");
                Check(f != null && f.Items.Count == 0, "★损坏 → 空列表",
                    f == null ? "null" : Convert.ToString(f.Items.Count));
            }
            finally { Clean(root); }

            // 10b. 主文件损坏但 .bak 完好 → 从 .bak 恢复
            root = NewRoot();
            try
            {
                string d = Path.Combine(root, "bak-test");
                Directory.CreateDirectory(d);

                // 先正常存一次（会生成 .bak）
                FileFolders f0 = FileFolders.Load(root);
                f0.Add(d);
                f0.Save();   // 第二次写会产生 .bak

                string p = FileFolders.PathOf(root);
                Check(File.Exists(p + ".bak"), "★.bak 已生成");

                // 把主文件写坏
                File.WriteAllText(p, "{ broken", new UTF8Encoding(false));

                FileFolders f1 = FileFolders.Load(root);
                Check(f1.Items.Count == 1, "★主文件损坏 → 从 .bak 恢复",
                    Convert.ToString(f1.Items.Count));
            }
            finally { Clean(root); }

            // 10c. 结构不对（items 不是数组）→ 空列表
            root = NewRoot();
            try
            {
                File.WriteAllText(FileFolders.PathOf(root),
                    "{\"items\":\"not-an-array\"}", new UTF8Encoding(false));
                FileFolders f = FileFolders.Load(root);
                Check(f.Items.Count == 0, "★items 类型错 → 空列表", Convert.ToString(f.Items.Count));
            }
            finally { Clean(root); }

            // 10d. items 里混入非法条目 → 跳过，保留合法
            root = NewRoot();
            try
            {
                File.WriteAllText(FileFolders.PathOf(root),
                    "{\"items\":[{\"path\":\"\"},{\"name\":\"x\"},{\"path\":\"C:\\\\valid\",\"name\":\"V\"}]}",
                    new UTF8Encoding(false));
                FileFolders f = FileFolders.Load(root);
                Check(f.Items.Count == 1, "★跳过无 path 的条目", Convert.ToString(f.Items.Count));
                if (f.Items.Count == 1)
                    Check(f.Items[0].Name == "V", "合法条目保留", f.Items[0].Name);
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 11. 原子写 ----------
        static void TestAtomicWrite()
        {
            Console.WriteLine("--- 11. 原子写 ---");

            string root = NewRoot();
            try
            {
                string d = Path.Combine(root, "atomic");
                Directory.CreateDirectory(d);

                FileFolders f = FileFolders.Load(root);
                f.Add(d);

                string p = FileFolders.PathOf(root);
                Check(File.Exists(p), "文件已创建");
                Check(!File.Exists(p + ".tmp"), "★没有残留 .tmp", "存在 .tmp 说明写中断了");

                string json = File.ReadAllText(p, Encoding.UTF8);
                Check(json.IndexOf("\"items\"", StringComparison.Ordinal) >= 0, "JSON 结构正确（有 items）");
                Check(json.IndexOf("\"path\"", StringComparison.Ordinal) >= 0, "有条目的 path");
                Check(json.IndexOf("\"name\"", StringComparison.Ordinal) >= 0, "有条目的 name");

                // 再存一次，应生成 .bak
                f.Save();
                Check(File.Exists(p + ".bak"), "★第二次写生成 .bak");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 12. 网络路径判定 ----------
        static void TestNetworkDetection()
        {
            Console.WriteLine("--- 12. 网络路径判定 ---");

            Check(FileFolders.IsNetworkPath(@"\\server\share"), "★UNC → true");
            Check(FileFolders.IsNetworkPath(@"\\192.168.1.1\c$"), "★UNC(IP) → true");
            Check(FileFolders.IsNetworkPath(@"\\?\C:\x"), "★\\\\?\\ 前缀 → true");

            // 本地盘不应判为网络
            string sysDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (!string.IsNullOrEmpty(sysDrive))
            {
                DriveInfo di = null;
                try { di = new DriveInfo(sysDrive); } catch { }
                if (di != null && di.DriveType == DriveType.Fixed)
                {
                    Check(!FileFolders.IsNetworkPath(sysDrive), "★本地固定盘 → false", sysDrive);
                    Check(!FileFolders.IsNetworkPath(Path.Combine(sysDrive, "Temp")), "本地子目录 → false");
                }
            }

            Check(!FileFolders.IsNetworkPath(null), "null → false");
            Check(!FileFolders.IsNetworkPath(""), "空串 → false");
            Console.WriteLine();
        }

        // ========== 辅助 ==========

        static string NewRoot()
        {
            string d = Path.Combine(Path.GetTempPath(), "ff-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            Directory.CreateDirectory(Path.Combine(d, "config"));
            return d;
        }

        static void Clean(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        }
    }
}
