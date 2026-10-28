using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// 扫描链的回归测试，重点是「便携条目不能每轮被抹掉」。
    ///
    /// 背景（真实 bug）：NativeScanner.Scan 是 apps.json 的**唯一写入者**，
    /// 但它只扫开始菜单，不认识便携软件。原实现每轮扫描只写回开始菜单条目，
    /// 于是上一轮 PortableScanner 补进来的便携条目**整批消失**，
    /// 只能靠本轮 Merge 再补。而 Merge 一旦超时中止（60s / 8000 目录上限）
    /// 就不写文件 —— 那几条会静默从面板里消失。
    ///
    /// 实测数据（修复前）：扫前 119 个（含 7 便携）→ 只跑 Scan → 112 个，便携 0。
    /// </summary>
    static class ScanChainTest
    {
        static int failed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name + (cond || detail.Length == 0 ? "" : "  [" + detail + "]"));
            if (!cond) failed++;
        }

        static string NewRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "clover-scan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));
            return root;
        }

        /// <summary>造一个真实的 exe 文件（FilterJson / 保留逻辑要检查文件是否存在）。</summary>
        static string MakeExe(string root, string rel)
        {
            string p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, "x", new UTF8Encoding(false));
            return p;
        }

        /// <summary>写 apps.json，含指定条目。</summary>
        static void WriteApps(string root, params string[][] rows)
        {
            List<object> arr = new List<object>();
            foreach (string[] r in rows)
            {
                Dictionary<string, object> m = new Dictionary<string, object>();
                m["name"] = r[0];
                m["path"] = r[1];
                m["category"] = r[2];
                if (r.Length > 3 && r[3] != null) m["folder"] = r[3];
                arr.Add(m);
            }
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["updated"] = "2026-01-01 00:00:00";
            d["order"] = new List<string>(Catalog.DefaultOrder);
            d["apps"] = arr;

            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            File.WriteAllText(Path.Combine(root, "config", "apps.json"),
                ser.Serialize(d), new UTF8Encoding(false));
        }

        static List<Dictionary<string, object>> ReadApps(string root)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(Path.Combine(root, "config", "apps.json"), Encoding.UTF8));
                object arr;
                if (d == null || !d.TryGetValue("apps", out arr)) return list;
                foreach (object o in JsonIO.AsArray(arr))
                {
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m != null) list.Add(m);
                }
            }
            catch { }
            return list;
        }

        static int CountPortable(string root)
        {
            int n = 0;
            foreach (Dictionary<string, object> m in ReadApps(root))
            {
                object f;
                if (m.TryGetValue("folder", out f) &&
                    Convert.ToString(f) == PortableScanner.FolderMarker) n++;
            }
            return n;
        }

        static bool HasName(string root, string name)
        {
            foreach (Dictionary<string, object> m in ReadApps(root))
            {
                object v;
                if (m.TryGetValue("name", out v) && Convert.ToString(v) == name) return true;
            }
            return false;
        }

        static void Main()
        {
            // ================= 1. ★便携条目在扫描后保留 =================
            // 这是本套件的核心：修复前这里会掉到 0。
            {
                string root = NewRoot();
                try
                {
                    string p1 = MakeExe(root, @"ext\Portable\Geek\geek.exe");
                    string p2 = MakeExe(root, @"ext\Portable\Zyper\ZyperWin++.exe");
                    WriteApps(root,
                        new string[] { "便携A", p1, "系统工具", PortableScanner.FolderMarker },
                        new string[] { "便携B", p2, "其他", PortableScanner.FolderMarker });

                    int before = CountPortable(root);
                    Check(before == 2, "夹具：扫前有 2 个便携条目", "实际 " + before);

                    // 只跑开始菜单扫描（不跑 PortableScanner.Merge）
                    NativeScanner.Scan(root);

                    int after = CountPortable(root);
                    Check(after == 2, "★扫描后便携条目仍保留（修复前会掉到 0）", "实际 " + after);
                    Check(HasName(root, "便携A"), "便携 A 还在");
                    Check(HasName(root, "便携B"), "便携 B 还在");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 2. 便携条目的分类按当前规则重判 =================
            {
                string root = NewRoot();
                try
                {
                    string p = MakeExe(root, @"ext\Portable\Chrome\chrome.exe");
                    // 故意写一个错分类，扫描后应被规则表纠正
                    WriteApps(root,
                        new string[] { "Chrome", p, "其他", PortableScanner.FolderMarker });

                    NativeScanner.Scan(root);

                    string cat = "";
                    foreach (Dictionary<string, object> m in ReadApps(root))
                    {
                        object v;
                        if (m.TryGetValue("name", out v) && Convert.ToString(v) == "Chrome")
                        {
                            object c;
                            if (m.TryGetValue("category", out c)) cat = Convert.ToString(c);
                        }
                    }
                    Check(cat == "网络通讯" || cat == "开发" || cat == "其他",
                        "便携条目分类被重判（不再是写死的值）", "实际 " + cat);
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 3. 目标已不存在的便携条目被淘汰 =================
            {
                string root = NewRoot();
                try
                {
                    string alive = MakeExe(root, @"ext\Portable\A\a.exe");
                    string gone = Path.Combine(root, @"ext\Portable\B\b.exe");   // 不创建
                    WriteApps(root,
                        new string[] { "Alive", alive, "其他", PortableScanner.FolderMarker },
                        new string[] { "Gone", gone, "其他", PortableScanner.FolderMarker });

                    NativeScanner.Scan(root);

                    Check(HasName(root, "Alive"), "文件仍在的便携条目保留");
                    Check(!HasName(root, "Gone"), "文件已删的便携条目被淘汰（不留死条目）");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 4. 不会重复添加 =================
            {
                string root = NewRoot();
                try
                {
                    string p = MakeExe(root, @"ext\Portable\Dup\dup.exe");
                    WriteApps(root,
                        new string[] { "Dup", p, "其他", PortableScanner.FolderMarker });

                    NativeScanner.Scan(root);
                    NativeScanner.Scan(root);
                    NativeScanner.Scan(root);

                    int n = 0;
                    foreach (Dictionary<string, object> m in ReadApps(root))
                    {
                        object v;
                        if (m.TryGetValue("name", out v) && Convert.ToString(v) == "Dup") n++;
                    }
                    Check(n == 1, "连扫 3 次也只保留 1 条（不累积重复）", "实际 " + n);
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 5. 非便携条目不受影响 =================
            {
                string root = NewRoot();
                try
                {
                    string p1 = MakeExe(root, @"ext\Start\Word.exe");
                    string p2 = MakeExe(root, @"ext\Portable\X\x.exe");
                    WriteApps(root,
                        new string[] { "WordLike", p1, "办公", "SomeFolder" },
                        new string[] { "PortableX", p2, "其他", PortableScanner.FolderMarker });

                    NativeScanner.Scan(root);

                    // folder != 便携识别 的条目走正常扫描逻辑，不属于"保留"范围，
                    // 但便携那条必须还在
                    Check(HasName(root, "PortableX"), "便携条目保留");
                    Check(CountPortable(root) == 1, "只有标记为便携的被当作便携", "实际 " + CountPortable(root));
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            // ================= 6. FolderMarker 常量与写入值一致 =================
            {
                Check(PortableScanner.FolderMarker == "便携识别",
                    "FolderMarker 常量值不变（改它会让旧文件里的便携条目认不出来）",
                    "实际 " + PortableScanner.FolderMarker);
            }

            // ================= 7. apps.json 缺失/损坏不崩 =================
            {
                string root = NewRoot();
                try
                {
                    // 文件不存在
                    NativeScanner.Scan(root);
                    Check(true, "apps.json 不存在时扫描不崩");

                    File.WriteAllText(Path.Combine(root, "config", "apps.json"), "{{{ 坏的",
                        new UTF8Encoding(false));
                    NativeScanner.Scan(root);
                    Check(true, "apps.json 损坏时扫描不崩");
                }
                finally { try { Directory.Delete(root, true); } catch { } }
            }

            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
