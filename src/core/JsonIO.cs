using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>JSON 文件读写：先写临时文件再替换（防断电写空），并保留 .bak，读取时自动回退 .bak。</summary>
    static class JsonIO
    {
        /// <summary>每日配置备份保留天数。</summary>
        const int KeepDailyBackups = 7;

        static readonly object backupGate = new object();
        static string lastBackupDay;

        public static void WriteAtomic(string path, string text)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string tmp = path + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));

                if (!File.Exists(path))
                {
                    File.Move(tmp, path);
                    BackupDaily(path);
                    return;
                }

                try
                {
                    File.Replace(tmp, path, path + ".bak", true);
                }
                catch
                {
                    try { File.Delete(path + ".bak"); } catch { }
                    try
                    {
                        File.Replace(tmp, path, path + ".bak", true);
                    }
                    catch
                    {
                        File.Copy(tmp, path, true);
                        try { File.Delete(tmp); } catch { }
                    }
                }

                BackupDaily(path);
            }
            catch { }
        }

        /// <summary>
        /// 每天第一次写某个配置文件时，把它的当前内容存一份到 config\backup\yyyyMMdd\。
        /// 用「当天是否已备份」判断，所以同一天多次写只留最早的一份快照，不会刷爆磁盘。
        /// </summary>
        static void BackupDaily(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                string cfgDir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(cfgDir)) return;
                // 只备份 config 目录下的顶层 JSON（不含 backup 子目录自身）
                if (!string.Equals(Path.GetFileName(cfgDir), "config", StringComparison.OrdinalIgnoreCase)) return;

                string day = DateTime.Now.ToString("yyyyMMdd");
                string fileName = Path.GetFileName(path);

                lock (backupGate)
                {
                    string key = day + "|" + path;
                    if (string.Equals(key, lastBackupDay, StringComparison.OrdinalIgnoreCase)) return;
                    lastBackupDay = key;

                    string destDir = Path.Combine(cfgDir, "backup", day);
                    string dest = Path.Combine(destDir, fileName);
                    if (File.Exists(dest)) return; // 今天已经备份过

                    Directory.CreateDirectory(destDir);
                    if (File.Exists(path)) File.Copy(path, dest, false);

                    PruneOldBackups(Path.Combine(cfgDir, "backup"));
                }
            }
            catch { }
        }

        /// <summary>只保留最近 N 天的备份目录。</summary>
        static void PruneOldBackups(string backupRoot)
        {
            try
            {
                if (!Directory.Exists(backupRoot)) return;
                string[] dirs = Directory.GetDirectories(backupRoot);
                if (dirs.Length <= KeepDailyBackups) return;

                Array.Sort(dirs, StringComparer.Ordinal); // yyyyMMdd 字典序即时间序
                int drop = dirs.Length - KeepDailyBackups;
                for (int i = 0; i < drop; i++)
                {
                    try { Directory.Delete(dirs[i], true); } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// 把 JSON 值读成真正的数组。**非数组一律返回空数组**（而不是 null），
        /// 所以可以直接写 `foreach (object o in JsonIO.AsArray(v))` 而不会空引用。
        ///
        /// 为什么需要它：`JavaScriptSerializer` 反序列化后，数组的运行时类型是
        /// **`System.Collections.ArrayList`**（不是 `object[]`），
        /// 而**字符串也是 `IEnumerable`** —— 遍历它得到的是**字符**。
        /// 于是下面这种写法在文件被手改成 `"order":"办公"` 时会出诡异结果：
        ///
        ///     foreach (object o in (IEnumerable)v) ...   // '办'、'公' 被当成两个元素
        ///
        /// 汉字被逐字拆成"分类名"，比英文更能暴露这个问题。
        /// 统一用这个助手读数组型字段，避免各处重复踩。
        /// </summary>
        public static object[] AsArray(object v)
        {
            // 字符串必须排除：它也是 IEnumerable，但按字符遍历毫无意义
            if (v == null || v is string) return new object[0];

            System.Collections.IEnumerable e = v as System.Collections.IEnumerable;
            if (e == null) return new object[0];

            // object[] 直接用（某些路径下会是这个类型）
            object[] asArr = v as object[];
            if (asArr != null) return asArr;

            // JavaScriptSerializer 走的是这条路：ArrayList
            List<object> list = new List<object>();
            foreach (object o in e) list.Add(o);
            return list.ToArray();
        }

        /// <summary>
        /// 把 JSON 值读成字符串数组。非数组（含字符串、数字、null）一律返回空数组。
        /// 这是读数组型字段的**安全写法**，替代直接 foreach IEnumerable。
        /// </summary>
        public static string[] AsStringArray(object v)
        {
            if (v == null || v is string) return new string[0];

            List<string> list = new List<string>();
            foreach (object o in AsArray(v))
            {
                if (o == null) continue;
                string s = Convert.ToString(o);
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
            return list.ToArray();
        }

        public static string Read(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    string s = File.ReadAllText(path, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            catch { }

            try
            {
                if (File.Exists(path + ".bak"))
                {
                    string s = File.ReadAllText(path + ".bak", Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            catch { }

            return null;
        }
    }
}
