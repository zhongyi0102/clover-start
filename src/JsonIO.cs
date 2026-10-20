using System;
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
