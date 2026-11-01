using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// 虚拟桌面的存储：每个自定义标签对应磁盘上一个真实文件夹，
    /// 外加一份「图标坐标」清单。
    ///
    /// ── 职责边界 ──
    ///
    /// 只管「文件夹在哪」和「每个文件图标放在哪」。
    /// 不搬文件、不读图标、不画界面 —— 那些分别是 FileOps / ShellIcons / DesktopView 的事。
    ///
    /// ── 存储 ──
    ///
    /// 坐标存在 `config\virtual-desks.json`：
    /// <code>
    /// {
    ///   "root": "E:\\CloverStart\\config\\desks",
    ///   "desks": {
    ///     "工作": {
    ///       "items": [
    ///         { "name": "报告.docx", "x": 40,  "y": 30 },
    ///         { "name": "预算.xlsx", "x": 140, "y": 30 }
    ///       ]
    ///     }
    ///   }
    /// }
    /// </code>
    ///
    /// 为什么用「文件名」而不是完整路径做键：桌面文件夹是用户可能整体搬走的，
    /// 存绝对路径一搬就全失效；存文件名，换目录也还认得出。
    ///
    /// ── 安全 ──
    ///
    /// 文件名是**外部数据**（用户可能手动改磁盘上的文件名）。所有用到的地方
    /// 都必须过 `IsSafeName`：拒绝路径分隔符、`..`、绝对路径。
    /// </summary>
    public class VirtualDesks
    {
        /// <summary>一个图标的坐标（设备无关单位，相对内容区左上角）。</summary>
        public class Pos
        {
            public double X;
            public double Y;
        }

        /// <summary>一个虚拟桌面。</summary>
        public class Desk
        {
            /// <summary>标签名（同时也是文件夹名）。</summary>
            public string Name = "";

            /// <summary>文件名 → 坐标。</summary>
            public Dictionary<string, Pos> Items =
                new Dictionary<string, Pos>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>所有虚拟桌面，按标签名索引。</summary>
        public Dictionary<string, Desk> Desks =
            new Dictionary<string, Desk>(StringComparer.OrdinalIgnoreCase);

        /// <summary>存放根目录（设置里可改）。</summary>
        public string Root = "";

        string configPath;

        public VirtualDesks(string baseDir)
        {
            try
            {
                if (!string.IsNullOrEmpty(baseDir))
                    configPath = Path.Combine(baseDir, "config", "virtual-desks.json");
            }
            catch { configPath = null; }
        }

        // ========== 根目录 ==========

        /// <summary>
        /// 默认存放根目录：程序目录下的 `desks`（与 config 同级）。
        ///
        /// 不用 `D:\四叶草\桌面` 那种绝对路径：程序可能被放到任何盘、
        /// 任何目录，写死盘符换台机器就找不到。
        /// </summary>
        public static string DefaultRoot(string baseDir)
        {
            try
            {
                if (string.IsNullOrEmpty(baseDir)) return "";
                return Path.Combine(baseDir, "desks");
            }
            catch { return ""; }
        }

        /// <summary>某个虚拟桌面的真实文件夹路径。</summary>
        public string FolderOf(string deskName)
        {
            try
            {
                if (string.IsNullOrEmpty(Root)) return null;
                if (!IsSafeName(deskName)) return null;
                return Path.Combine(Root, deskName);
            }
            catch { return null; }
        }

        // ========== 读 ==========

        public void Load()
        {
            Desks = new Dictionary<string, Desk>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath)) return;

                string json = JsonIO.Read(configPath);
                if (string.IsNullOrEmpty(json)) return;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return;

                object rv;
                if (d.TryGetValue("root", out rv) && rv != null)
                    Root = Convert.ToString(rv);

                object dv;
                if (!d.TryGetValue("desks", out dv) || dv == null) return;

                Dictionary<string, object> dm = dv as Dictionary<string, object>;
                if (dm == null) return;

                foreach (KeyValuePair<string, object> kv in dm)
                {
                    string name = kv.Key;
                    if (!IsSafeName(name)) continue;

                    Desk desk = new Desk();
                    desk.Name = name;

                    Dictionary<string, object> one = kv.Value as Dictionary<string, object>;
                    if (one != null)
                    {
                        object iv;
                        if (one.TryGetValue("items", out iv) && iv != null)
                        {
                            // JsonIO.AsArray 能正确处理 ArrayList（JavaScriptSerializer 的返回类型）
                            foreach (object o in JsonIO.AsArray(iv))
                            {
                                Dictionary<string, object> item = o as Dictionary<string, object>;
                                if (item == null) continue;

                                object nv;
                                if (!item.TryGetValue("name", out nv) || nv == null) continue;

                                string fn = Convert.ToString(nv);
                                if (!IsSafeName(fn)) continue;

                                double x = 0, y = 0;
                                ReadNum(item, "x", ref x);
                                ReadNum(item, "y", ref y);

                                desk.Items[fn] = new Pos { X = x, Y = y };
                            }
                        }
                    }

                    Desks[name] = desk;
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("读取 virtual-desks.json 失败，按空处理: " + ex.Message); } catch { }
                Desks = new Dictionary<string, Desk>(StringComparer.OrdinalIgnoreCase);
            }
        }

        static void ReadNum(Dictionary<string, object> d, string key, ref double v)
        {
            try
            {
                object o;
                if (d == null || !d.TryGetValue(key, out o) || o == null) return;

                if (o is int) { v = (int)o; return; }
                if (o is long) { v = (long)o; return; }
                if (o is double) { v = (double)o; return; }

                double parsed;
                if (double.TryParse(Convert.ToString(o), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed)) v = parsed;
            }
            catch { }
        }

        // ========== 写 ==========

        public void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(configPath)) return;

                Dictionary<string, object> root = new Dictionary<string, object>();
                root["root"] = Root ?? "";

                Dictionary<string, object> dm = new Dictionary<string, object>();
                foreach (KeyValuePair<string, Desk> kv in Desks)
                {
                    Desk desk = kv.Value;
                    if (desk == null || !IsSafeName(desk.Name)) continue;

                    List<Dictionary<string, object>> arr = new List<Dictionary<string, object>>();
                    foreach (KeyValuePair<string, Pos> it in desk.Items)
                    {
                        if (!IsSafeName(it.Key)) continue;
                        Dictionary<string, object> one = new Dictionary<string, object>();
                        one["name"] = it.Key;
                        one["x"] = Math.Round(it.Value.X, 1);
                        one["y"] = Math.Round(it.Value.Y, 1);
                        arr.Add(one);
                    }

                    Dictionary<string, object> body = new Dictionary<string, object>();
                    body["items"] = arr.ToArray();
                    dm[desk.Name] = body;
                }
                root["desks"] = dm;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                JsonIO.WriteAtomic(configPath, ser.Serialize(root));
            }
            catch (Exception ex)
            {
                try { Log.Warn("保存 virtual-desks.json 失败: " + ex.Message); } catch { }
            }
        }

        // ========== 桌面管理 ==========

        /// <summary>
        /// 取（或建）一个虚拟桌面对象，并在磁盘上建出对应文件夹。
        ///
        /// 返回 null 表示名字非法或目录建不出来。
        /// </summary>
        public Desk EnsureDesk(string name)
        {
            try
            {
                if (!IsSafeName(name)) return null;
                if (string.IsNullOrEmpty(Root)) return null;

                Desk desk;
                if (!Desks.TryGetValue(name, out desk) || desk == null)
                {
                    desk = new Desk();
                    desk.Name = name;
                    Desks[name] = desk;
                }

                string dir = FolderOf(name);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                return desk;
            }
            catch (Exception ex)
            {
                try { Log.Warn("创建虚拟桌面失败: " + ex.Message); } catch { }
                return null;
            }
        }

        /// <summary>
        /// 删除一个虚拟桌面的**记录**（不动磁盘文件）。
        ///
        /// 磁盘文件由调用方决定：放回真实桌面 / 保留在原文件夹。
        /// </summary>
        public void ForgetDesk(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) return;
                Desks.Remove(name);
                Save();
            }
            catch { }
        }

        /// <summary>把标签名改掉，同时把磁盘文件夹一起改名。</summary>
        public string RenameDesk(string oldName, string newName)
        {
            try
            {
                if (!IsSafeName(oldName) || !IsSafeName(newName)) return "名称不合法。";
                if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) return null;
                if (Desks.ContainsKey(newName)) return "已经有同名标签了。";

                Desk desk;
                if (!Desks.TryGetValue(oldName, out desk) || desk == null) return "找不到这个标签。";

                string oldDir = FolderOf(oldName);
                string newDir = FolderOf(newName);

                // 文件夹存在就一起改名（不存在则只改记录）
                if (!string.IsNullOrEmpty(oldDir) && Directory.Exists(oldDir))
                {
                    if (!string.IsNullOrEmpty(newDir) && Directory.Exists(newDir))
                        return "目标文件夹已存在，无法改名。";

                    try { Directory.Move(oldDir, newDir); }
                    catch (Exception ex) { return "文件夹改名失败：" + ex.Message; }
                }

                Desks.Remove(oldName);
                desk.Name = newName;
                Desks[newName] = desk;
                Save();
                return null;
            }
            catch (Exception ex) { return "改名失败：" + ex.Message; }
        }

        // ========== 坐标 ==========

        /// <summary>取坐标；没有记录返回 false。</summary>
        public bool TryGetPos(string deskName, string fileName, out double x, out double y)
        {
            x = 0; y = 0;
            try
            {
                Desk desk;
                if (string.IsNullOrEmpty(deskName)) return false;
                if (!Desks.TryGetValue(deskName, out desk) || desk == null) return false;
                if (!IsSafeName(fileName)) return false;

                Pos p;
                if (!desk.Items.TryGetValue(fileName, out p) || p == null) return false;

                x = p.X; y = p.Y;
                return true;
            }
            catch { return false; }
        }

        /// <summary>记录一个图标的坐标（不自动写盘，调用方批量改完再 Save）。</summary>
        public void SetPos(string deskName, string fileName, double x, double y)
        {
            try
            {
                if (string.IsNullOrEmpty(deskName)) return;
                if (!IsSafeName(fileName)) return;

                Desk desk;
                if (!Desks.TryGetValue(deskName, out desk) || desk == null) return;

                if (x < 0) x = 0;
                if (y < 0) y = 0;

                desk.Items[fileName] = new Pos { X = x, Y = y };
            }
            catch { }
        }

        /// <summary>去掉一个已不存在文件的坐标记录。</summary>
        public void RemovePos(string deskName, string fileName)
        {
            try
            {
                Desk desk;
                if (string.IsNullOrEmpty(deskName)) return;
                if (!Desks.TryGetValue(deskName, out desk) || desk == null) return;
                if (string.IsNullOrEmpty(fileName)) return;
                desk.Items.Remove(fileName);
            }
            catch { }
        }

        // ========== 安全 ==========

        /// <summary>
        /// 文件名 / 标签名是否安全。
        ///
        /// 这些名字会拼进路径，所以必须拒绝：空、路径分隔符、`..`、
        /// 绝对路径特征、以及 Windows 保留字符。
        /// </summary>
        public static bool IsSafeName(string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return false;
                if (name.Length > 64) return false;
                if (name == "." || name == "..") return false;

                // 路径分隔符 / 盘符 / 保留字符
                if (name.IndexOf('\\') >= 0) return false;
                if (name.IndexOf('/') >= 0) return false;
                if (name.IndexOf(':') >= 0) return false;
                if (name.IndexOfAny(new char[] { '*', '?', '"', '<', '>', '|' }) >= 0) return false;

                // 控制字符
                foreach (char c in name)
                {
                    if (c < 32) return false;
                }

                // 首尾空格 / 点（Windows 会自动裁掉，导致名字对不上）
                if (name != name.Trim()) return false;
                if (name.EndsWith(".", StringComparison.Ordinal)) return false;

                return true;
            }
            catch { return false; }
        }
    }
}
