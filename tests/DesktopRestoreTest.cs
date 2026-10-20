using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// desktop-backup 还原测试：
    ///   - 重名时加后缀，绝不覆盖
    ///   - 还原成功的从 backup 移除
    ///   - 失败的保留原地、不重试
    ///   - 超过 200 个只警告不删除
    ///
    /// 直接测 AppWatcher.RestoreLinks / MakeUniqueDest 这两个静态纯文件逻辑，
    /// 不依赖 WPF 面板（通过链接 src 里 AppWatcher 的同名静态方法）。
    /// </summary>
    static class DesktopRestoreTest
    {
        static int failed = 0;

        static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name + (cond || detail.Length == 0 ? "" : "  [" + detail + "]"));
            if (!cond) failed++;
        }

        static string NewDir(string tag)
        {
            string d = Path.Combine(Path.GetTempPath(), "clover-restore-" + tag + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        static void Main()
        {
            // ---------- 1. 重名不覆盖 ----------
            string backup = NewDir("bak");
            string desktop = NewDir("desk");
            try
            {
                File.WriteAllText(Path.Combine(backup, "微信.lnk"), "from-backup", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(desktop, "微信.lnk"), "existing-desktop", new UTF8Encoding(false));

                string dest = Clover.AppWatcher.MakeUniqueDest(desktop, "微信.lnk");
                Check(Path.GetFileName(dest) != "微信.lnk", "重名时生成不同的目标文件名", Path.GetFileName(dest));
                Check(Path.GetFileName(dest).StartsWith("微信-还原"), "目标名带「-还原N」后缀", Path.GetFileName(dest));
                Check(File.Exists(Path.Combine(desktop, "微信.lnk")), "原桌面文件仍在");

                int[] r = Clover.AppWatcher.RestoreLinks(backup, desktop, delegate { });
                Check(r[0] == 1 && r[1] == 0, "还原成功 1 个", r[0] + "/" + r[1]);

                // 原文件内容没被覆盖
                string original = File.ReadAllText(Path.Combine(desktop, "微信.lnk"), Encoding.UTF8);
                Check(original == "existing-desktop", "原桌面文件内容未被覆盖", original);

                // 还原出来的那个是备份内容
                string restored = File.ReadAllText(dest, Encoding.UTF8);
                Check(restored == "from-backup", "还原出来的文件是备份内容", restored);

                // backup 里已移除
                Check(!File.Exists(Path.Combine(backup, "微信.lnk")), "还原后从 backup 移除");
            }
            finally
            {
                try { Directory.Delete(backup, true); } catch { }
                try { Directory.Delete(desktop, true); } catch { }
            }

            // ---------- 2. 无重名时保持原文件名 ----------
            string b2 = NewDir("bak2");
            string d2 = NewDir("desk2");
            try
            {
                File.WriteAllText(Path.Combine(b2, "QQ.lnk"), "qq", new UTF8Encoding(false));
                int[] r = Clover.AppWatcher.RestoreLinks(b2, d2, delegate { });
                Check(r[0] == 1, "无重名时还原成功");
                Check(File.Exists(Path.Combine(d2, "QQ.lnk")), "无重名时保持原文件名");
            }
            finally
            {
                try { Directory.Delete(b2, true); } catch { }
                try { Directory.Delete(d2, true); } catch { }
            }

            // ---------- 3. 多个同名备份依次加后缀，互不覆盖 ----------
            string b3 = NewDir("bak3");
            string d3 = NewDir("desk3");
            try
            {
                // 桌面已有一个，备份里再放两个同名
                File.WriteAllText(Path.Combine(d3, "钉钉.lnk"), "d0", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(b3, "钉钉.lnk"), "d1", new UTF8Encoding(false));

                int[] r1 = Clover.AppWatcher.RestoreLinks(b3, d3, delegate { });
                File.WriteAllText(Path.Combine(b3, "钉钉.lnk"), "d2", new UTF8Encoding(false));
                int[] r2 = Clover.AppWatcher.RestoreLinks(b3, d3, delegate { });

                Check(r1[0] == 1 && r2[0] == 1, "两轮还原各成功 1 个", r1[0] + "/" + r2[0]);

                int lnkCount = Directory.GetFiles(d3, "*.lnk").Length;
                Check(lnkCount == 3, "桌面最终有 3 个文件（原 1 + 还原 2）", "实际 " + lnkCount);

                // 三个内容各不相同 = 没有互相覆盖
                HashSet<string> contents = new HashSet<string>();
                foreach (string f in Directory.GetFiles(d3, "*.lnk"))
                    contents.Add(File.ReadAllText(f, Encoding.UTF8));
                Check(contents.Count == 3, "三个文件内容互不相同（未覆盖）", contents.Count + " 种");
            }
            finally
            {
                try { Directory.Delete(b3, true); } catch { }
                try { Directory.Delete(d3, true); } catch { }
            }

            // ---------- 4. 失败不重试：锁定目标文件 ----------
            string b4 = NewDir("bak4");
            string d4 = NewDir("desk4");
            FileStream locked = null;
            try
            {
                File.WriteAllText(Path.Combine(b4, "锁定.lnk"), "x", new UTF8Encoding(false));
                // 独占打开一个同名目标（MakeUniqueDest 会避开它，所以这里制造"移动必然失败"的场景：
                // 把 backup 文件本身锁定为只读/独占）
                locked = new FileStream(Path.Combine(b4, "锁定.lnk"), FileMode.Open, FileAccess.Read, FileShare.None);

                int logged = 0;
                int[] r = Clover.AppWatcher.RestoreLinks(b4, d4, delegate { logged++; });
                Check(r[0] == 0, "文件被占用时还原失败（成功数 0）", r[0].ToString());
                Check(r[1] == 1, "失败数记为 1");
                Check(File.Exists(Path.Combine(b4, "锁定.lnk")), "失败的文件保留在 backup 里");
                Check(logged > 0, "失败有写日志（不静默）", "日志条数 " + logged);
            }
            finally
            {
                if (locked != null) { try { locked.Close(); } catch { } }
                try { Directory.Delete(b4, true); } catch { }
                try { Directory.Delete(d4, true); } catch { }
            }

            // ---------- 5. 空 backup / 不存在目录：安全返回 ----------
            string b5 = NewDir("bak5");
            string d5 = NewDir("desk5");
            try
            {
                int[] r = Clover.AppWatcher.RestoreLinks(b5, d5, delegate { });
                Check(r[0] == 0 && r[1] == 0, "空 backup 返回 0/0");

                int[] r2 = Clover.AppWatcher.RestoreLinks(Path.Combine(b5, "不存在"), d5, delegate { });
                Check(r2[0] == 0 && r2[1] == 0, "backup 目录不存在时返回 0/0");
            }
            finally
            {
                try { Directory.Delete(b5, true); } catch { }
                try { Directory.Delete(d5, true); } catch { }
            }

            // ---------- 6. 只处理 .lnk，其他类型不动 ----------
            string b6 = NewDir("bak6");
            string d6 = NewDir("desk6");
            try
            {
                File.WriteAllText(Path.Combine(b6, "a.lnk"), "lnk", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(b6, "note.txt"), "txt", new UTF8Encoding(false));
                int[] r = Clover.AppWatcher.RestoreLinks(b6, d6, delegate { });
                Check(r[0] == 1, "只还原了 .lnk");
                Check(File.Exists(Path.Combine(b6, "note.txt")), "非 .lnk 文件留在 backup 不动");
                Check(!File.Exists(Path.Combine(d6, "note.txt")), "非 .lnk 没有搬到桌面");
            }
            finally
            {
                try { Directory.Delete(b6, true); } catch { }
                try { Directory.Delete(d6, true); } catch { }
            }

            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            Environment.Exit(failed == 0 ? 0 : 1);
        }
    }
}
