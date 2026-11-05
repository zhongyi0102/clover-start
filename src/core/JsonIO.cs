using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>JSON 文件读写：先写临时文件再替换（防断电写空），并保留 .bak，读取时自动回退 .bak。</summary>
    static class JsonIO
    {
        public enum ReadSource
        {
            None,
            Primary,
            Backup,
        }

        /// <summary>一次 JSON 读取的结果。旧调用可继续使用 <see cref="Read"/>。</summary>
        public sealed class ReadResult
        {
            public string Text;
            public ReadSource Source;
            public string PrimaryError;
            public string BackupError;

            public bool Success { get { return Source != ReadSource.None && Text != null; } }
            public bool UsedBackup { get { return Source == ReadSource.Backup; } }
        }

        /// <summary>一次原子写的结果。返回值可被旧调用安全忽略。</summary>
        public sealed class WriteResult
        {
            public bool Success;
            public string Path;
            public bool UsedCopyFallback;
            public Exception Error;

            public string ErrorMessage
            {
                get { return Error == null ? "" : Error.GetType().Name + ": " + Error.Message; }
            }
        }

        /// <summary>
        /// 日志回调。JsonIO 被许多最小测试单独编译，不能直接依赖 Log.cs，
        /// 因此由程序入口注入；未接线时保持空操作。
        /// </summary>
        public static Action<string> Warn = delegate { };

        /// <summary>每日配置备份保留天数。</summary>
        const int KeepDailyBackups = 7;

        static readonly object backupGate = new object();
        static string lastBackupDay;

        public static WriteResult WriteAtomic(string path, string text)
        {
            WriteResult result = new WriteResult();
            result.Path = path;
            string tmp = string.IsNullOrEmpty(path) ? null : path + ".tmp";
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    throw new ArgumentException("写入路径不能为空", "path");

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                File.WriteAllText(tmp, text ?? "", new UTF8Encoding(false));

                if (!File.Exists(path))
                {
                    File.Move(tmp, path);
                    BackupDaily(path);
                    result.Success = true;
                    return result;
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
                        result.UsedCopyFallback = true;
                        try { File.Delete(tmp); } catch { }
                    }
                }

                BackupDaily(path);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Error = ex;
                try { if (!string.IsNullOrEmpty(tmp) && File.Exists(tmp)) File.Delete(tmp); } catch { }
                try { Warn("保存 JSON 失败: " + path + " -> " + result.ErrorMessage); } catch { }
            }
            return result;
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

        /// <summary>
        /// 读取合法 JSON。主文件为空、不可读或语法损坏时自动尝试 `.bak`。
        /// 过去这里只判断“非空”，半截 JSON 会挡住有效备份；现在两层都必须
        /// 通过完整语法校验才会返回。
        /// </summary>
        public static ReadResult ReadDetailed(string path)
        {
            ReadResult result = new ReadResult();
            string text;
            string error;

            if (TryReadValid(path, out text, out error))
            {
                result.Text = text;
                result.Source = ReadSource.Primary;
                return result;
            }
            result.PrimaryError = error;

            if (TryReadValid(path + ".bak", out text, out error))
            {
                result.Text = text;
                result.Source = ReadSource.Backup;
                try
                {
                    Warn("JSON 主文件不可用，已从备份读取: " + path
                        + (string.IsNullOrEmpty(result.PrimaryError) ? "" : "（" + result.PrimaryError + "）"));
                }
                catch { }
                return result;
            }
            result.BackupError = error;

            // 文件完全不存在是正常的首次运行，不刷告警；确实存在但两份都坏才记录。
            try
            {
                bool primaryExists = !string.IsNullOrEmpty(path) && File.Exists(path);
                bool backupExists = !string.IsNullOrEmpty(path) && File.Exists(path + ".bak");
                if (primaryExists || backupExists)
                {
                    Warn("JSON 读取失败，主文件与备份均不可用: " + path
                        + "（主: " + (result.PrimaryError ?? "未知")
                        + "；备份: " + (result.BackupError ?? "未知") + "）");
                }
            }
            catch { }

            return result;
        }

        public static string Read(string path)
        {
            return ReadDetailed(path).Text;
        }

        static bool TryReadValid(string path, out string text, out string error)
        {
            text = null;
            error = null;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    error = "文件不存在";
                    return false;
                }

                string s = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(s))
                {
                    error = "内容为空";
                    return false;
                }

                string validationError;
                if (!JsonValidator.IsValid(s, out validationError))
                {
                    error = "JSON 损坏: " + validationError;
                    return false;
                }

                text = s;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 小型严格 JSON 语法校验器。故意不依赖 System.Web.Extensions：JsonIO.cs
        /// 会被若干最小测试单文件编译，新增程序集依赖会破坏那条构建路径。
        /// 它只判断语法完整性，具体字段形状仍由各配置类负责。
        /// </summary>
        sealed class JsonValidator
        {
            readonly string text;
            int pos;
            string error;

            JsonValidator(string text) { this.text = text ?? ""; }

            public static bool IsValid(string text, out string error)
            {
                JsonValidator p = new JsonValidator(text);
                p.SkipWhite();
                bool ok = p.Value();
                p.SkipWhite();
                if (ok && p.pos != p.text.Length)
                {
                    p.Fail("末尾存在多余内容");
                    ok = false;
                }
                error = p.error;
                return ok;
            }

            bool Value()
            {
                SkipWhite();
                if (pos >= text.Length) return Fail("缺少值");
                char c = text[pos];
                if (c == '{') return Object();
                if (c == '[') return Array();
                if (c == '"') return String();
                if (c == 't') return Literal("true");
                if (c == 'f') return Literal("false");
                if (c == 'n') return Literal("null");
                if (c == '-' || (c >= '0' && c <= '9')) return Number();
                return Fail("无法识别的值");
            }

            bool Object()
            {
                pos++; // {
                SkipWhite();
                if (Take('}')) return true;
                while (true)
                {
                    SkipWhite();
                    if (!String()) return false;
                    SkipWhite();
                    if (!Take(':')) return Fail("对象键后缺少冒号");
                    if (!Value()) return false;
                    SkipWhite();
                    if (Take('}')) return true;
                    if (!Take(',')) return Fail("对象成员之间缺少逗号");
                }
            }

            bool Array()
            {
                pos++; // [
                SkipWhite();
                if (Take(']')) return true;
                while (true)
                {
                    if (!Value()) return false;
                    SkipWhite();
                    if (Take(']')) return true;
                    if (!Take(',')) return Fail("数组元素之间缺少逗号");
                }
            }

            bool String()
            {
                if (!Take('"')) return Fail("对象键必须是字符串");
                while (pos < text.Length)
                {
                    char c = text[pos++];
                    if (c == '"') return true;
                    if (c < 0x20) return Fail("字符串含控制字符");
                    if (c != '\\') continue;
                    if (pos >= text.Length) return Fail("字符串转义不完整");
                    char e = text[pos++];
                    if (e == '"' || e == '\\' || e == '/' || e == 'b'
                        || e == 'f' || e == 'n' || e == 'r' || e == 't') continue;
                    if (e != 'u') return Fail("字符串含非法转义");
                    for (int i = 0; i < 4; i++)
                    {
                        if (pos >= text.Length || !IsHex(text[pos++]))
                            return Fail("Unicode 转义不完整");
                    }
                }
                return Fail("字符串未闭合");
            }

            bool Number()
            {
                if (Take('-') && pos >= text.Length) return Fail("数字不完整");
                if (Take('0'))
                {
                    if (pos < text.Length && char.IsDigit(text[pos]))
                        return Fail("数字不能有前导零");
                }
                else
                {
                    if (!Digits()) return Fail("数字整数部分缺失");
                }

                if (Take('.'))
                {
                    if (!Digits()) return Fail("数字小数部分缺失");
                }

                if (pos < text.Length && (text[pos] == 'e' || text[pos] == 'E'))
                {
                    pos++;
                    if (pos < text.Length && (text[pos] == '+' || text[pos] == '-')) pos++;
                    if (!Digits()) return Fail("数字指数部分缺失");
                }
                return true;
            }

            bool Digits()
            {
                int start = pos;
                while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9') pos++;
                return pos > start;
            }

            bool Literal(string value)
            {
                if (pos + value.Length > text.Length) return Fail("字面量不完整");
                for (int i = 0; i < value.Length; i++)
                    if (text[pos + i] != value[i]) return Fail("非法字面量");
                pos += value.Length;
                return true;
            }

            void SkipWhite()
            {
                while (pos < text.Length)
                {
                    char c = text[pos];
                    if (c != ' ' && c != '\t' && c != '\r' && c != '\n') break;
                    pos++;
                }
            }

            bool Take(char c)
            {
                if (pos >= text.Length || text[pos] != c) return false;
                pos++;
                return true;
            }

            bool Fail(string message)
            {
                if (error == null) error = message + "（位置 " + pos + "）";
                return false;
            }

            static bool IsHex(char c)
            {
                return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')
                    || (c >= 'A' && c <= 'F');
            }
        }
    }
}
