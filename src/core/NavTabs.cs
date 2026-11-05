using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// 用户自定义桌面的持久化清单（主标签栏内置「常用 / 全部 / 文件 / 虚拟桌面」，
    /// 用户通过末尾「+」新建的额外桌面记录在这里）。
    ///
    /// ── 职责边界 ──
    ///
    /// 只负责「存哪些标签、什么顺序、叫什么名字」。
    /// 不决定标签里显示什么（当前实现里自定义标签等同于「全部」，见
    /// PanelWindow.RenderApps 的说明），也不碰分类规则。
    ///
    /// ── 存储 ──
    ///
    /// `config\nav-tabs.json`
    /// <code>
    /// { "tabs": [ { "name": "游戏" }, { "name": "开发" } ] }
    /// </code>
    ///
    /// 原子写 + `.bak`；读失败由 JsonIO 回退 `.bak`，再失败返回空列表（不抛异常）。
    ///
    /// ── 为什么不放进 settings.json ──
    ///
    /// 标签是**列表结构**（有顺序、可增删），塞进扁平配置要么拼字符串、
    /// 要么破坏「未知字段原样保留」那条约束。独立文件更干净。
    /// </summary>
    public class NavTabs
    {
        /// <summary>最多允许几个自定义标签。</summary>
        ///
        /// 上限的意义：标签栏是一行横向排列，超过 10 个会挤出面板宽度；
        /// 而且标签越多越难找，与「常用 + 搜索」的设计初衷相悖。
        public const int MaxTabs = 10;

        /// <summary>标签名最大长度。</summary>
        public const int MaxNameLength = 12;

        /// <summary>内置标签名，不允许用户新建同名、也不允许重命名成它们。</summary>
        public static readonly string[] Reserved =
            new string[] { "常用", "全部", "文件", "桌面文件", "虚拟桌面" };

        /// <summary>内置标签的新显示名；只阻止新建/重命名，不隐藏已有同名数据。</summary>
        static readonly string[] ReservedDisplay = new string[] { "文件库", "桌面收纳" };

        /// <summary>一个自定义标签。</summary>
        public class Tab
        {
            public string Name = "";

            public Tab() { }
            public Tab(string name) { Name = name ?? ""; }
        }

        /// <summary>当前清单（按显示顺序）。</summary>
        public List<Tab> Items = new List<Tab>();

        /// <summary>文件路径（构造时传入，避免每处都拼一遍）。</summary>
        string path;

        public NavTabs(string baseDir)
        {
            try
            {
                if (!string.IsNullOrEmpty(baseDir))
                    path = Path.Combine(baseDir, "config", "nav-tabs.json");
            }
            catch { path = null; }
        }

        // ========== 读 ==========

        /// <summary>
        /// 从磁盘加载。文件不存在 / 损坏 / 字段缺失 → 返回空清单（不抛异常）。
        /// </summary>
        public void Load()
        {
            Items = new List<Tab>();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

                string json = JsonIO.Read(path);
                if (string.IsNullOrEmpty(json)) return;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return;

                object raw;
                if (!d.TryGetValue("tabs", out raw) || raw == null) return;

                // JavaScriptSerializer 把 JSON 数组反序列化成
                // System.Collections.ArrayList（既不是 object[]，
                // 也不是 Dictionary[]）—— 用 as 强转会永远得到 null，
                // 表现为「写得出、读不回」。所以按 IEnumerable 遍历。
                System.Collections.IEnumerable arr = raw as System.Collections.IEnumerable;
                if (arr == null) return;

                foreach (object one in arr)
                {
                    Dictionary<string, object> item = one as Dictionary<string, object>;
                    if (item == null) continue;

                    object nv;
                    if (!item.TryGetValue("name", out nv) || nv == null) continue;

                    string name = Convert.ToString(nv);
                    name = SanitizeName(name);
                    if (name.Length == 0) continue;
                    if (IsReserved(name)) continue;      // 内置名不当作自定义标签
                    if (FindIndex(name) >= 0) continue;  // 去重
                    if (Items.Count >= MaxTabs) break;

                    Items.Add(new Tab(name));
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("读取 nav-tabs.json 失败，按空清单处理: " + ex.Message); } catch { }
                Items = new List<Tab>();
            }
        }

        // ========== 写 ==========

        /// <summary>写回磁盘（原子写 + .bak）。失败静默，只记日志。</summary>
        public void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;

                List<Dictionary<string, object>> arr = new List<Dictionary<string, object>>();
                foreach (Tab t in Items)
                {
                    if (t == null || string.IsNullOrEmpty(t.Name)) continue;
                    Dictionary<string, object> one = new Dictionary<string, object>();
                    one["name"] = t.Name;
                    arr.Add(one);
                }

                Dictionary<string, object> root = new Dictionary<string, object>();
                root["tabs"] = arr.ToArray();

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                JsonIO.WriteAtomic(path, ser.Serialize(root));
            }
            catch (Exception ex)
            {
                try { Log.Warn("保存 nav-tabs.json 失败: " + ex.Message); } catch { }
            }
        }

        // ========== 增删改 ==========

        /// <summary>
        /// 新建标签。成功返回 null，失败返回给用户看的原因。
        /// </summary>
        public string Add(string rawName)
        {
            try
            {
                string name = SanitizeName(rawName);

                if (name.Length == 0) return "标签名不能为空。";
                if (IsReservedForCreate(name)) return "「" + name + "」是内置标签，不能重复创建。";
                if (FindIndex(name) >= 0) return "已经有同名标签了。";
                if (Items.Count >= MaxTabs) return "最多 " + MaxTabs + " 个自定义标签，请先删掉一些。";

                Items.Add(new Tab(name));
                Save();
                return null;
            }
            catch (Exception ex)
            {
                return "新建失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 重命名。成功返回 null，失败返回原因。
        /// </summary>
        public string Rename(string oldName, string rawNewName)
        {
            try
            {
                int i = FindIndex(oldName);
                if (i < 0) return "找不到这个标签。";

                string name = SanitizeName(rawNewName);
                if (name.Length == 0) return "标签名不能为空。";
                if (IsReservedForCreate(name)) return "「" + name + "」是内置标签，不能使用。";

                int dup = FindIndex(name);
                if (dup >= 0 && dup != i) return "已经有同名标签了。";

                Items[i].Name = name;
                Save();
                return null;
            }
            catch (Exception ex)
            {
                return "重命名失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 删除。内置标签不允许删除。
        /// </summary>
        public string Remove(string name)
        {
            try
            {
                if (IsReserved(name)) return "内置标签不能删除。";

                int i = FindIndex(name);
                if (i < 0) return "找不到这个标签。";

                Items.RemoveAt(i);
                Save();
                return null;
            }
            catch (Exception ex)
            {
                return "删除失败：" + ex.Message;
            }
        }

        // ========== 查询 ==========

        /// <summary>按名字找下标（大小写不敏感），找不到返回 -1。</summary>
        public int FindIndex(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) return -1;
                for (int i = 0; i < Items.Count; i++)
                {
                    if (Items[i] == null) continue;
                    if (string.Equals(Items[i].Name, name, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            catch { }
            return -1;
        }

        /// <summary>是否内置标签名。</summary>
        public static bool IsReserved(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name)) return false;
                foreach (string r in Reserved)
                {
                    if (string.Equals(r, name, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        static bool IsReservedForCreate(string name)
        {
            if (IsReserved(name)) return true;
            try
            {
                foreach (string r in ReservedDisplay)
                    if (string.Equals(r, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 清洗标签名：去首尾空白、压缩内部连续空白、截断到上限。
        ///
        /// 不做「非法字符」过滤 —— 标签名只显示在界面上，不参与路径拼接。
        /// </summary>
        public static string SanitizeName(string raw)
        {
            try
            {
                if (string.IsNullOrEmpty(raw)) return "";

                string s = raw.Trim();

                // 内部连续空白压成一个空格（避免「游戏   中心」这种怪名字）
                System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
                bool lastSpace = false;
                foreach (char c in s)
                {
                    bool isSpace = char.IsWhiteSpace(c);
                    if (isSpace)
                    {
                        if (!lastSpace) sb.Append(' ');
                        lastSpace = true;
                    }
                    else
                    {
                        sb.Append(c);
                        lastSpace = false;
                    }
                }

                s = sb.ToString().Trim();
                if (s.Length > MaxNameLength) s = s.Substring(0, MaxNameLength);
                return s;
            }
            catch { return ""; }
        }
    }
}
