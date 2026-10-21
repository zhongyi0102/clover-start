using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Clover
{
    /// <summary>
    /// 系统状态持久化：任何“改动系统状态”的操作，执行前先把原始状态写进
    /// config\system-state.json，还原后清掉。这样即使进程被强杀，下次启动也能
    /// 从磁盘知道“上次留下了什么没还原”。
    ///
    /// 设计要点：
    ///   - 写入必须“先落盘再动系统”，顺序不能反，否则崩溃窗口期状态丢失
    ///   - 标记文件存在 = 有未还原状态；删除 = 已还原干净
    ///   - 多个状态用字段表示，互不覆盖
    /// </summary>
    class SystemState
    {
        /// <summary>上次是否隐藏了系统任务栏（Shell_TrayWnd）。</summary>
        public bool TaskbarHidden;

        /// <summary>上次是否把任务栏改成了“全透明可点击”模式（WS_EX_LAYERED + alpha）。</summary>
        public bool TaskbarLayered;

        /// <summary>记录开始时间，便于排查“什么时候留下的”。</summary>
        public string Since = "";

        /// <summary>写入这条状态的版本，便于以后兼容。</summary>
        public string Version = "";

        static readonly object gate = new object();
        static string path;

        /// <summary>是否有任何未还原的系统状态。</summary>
        public bool Dirty
        {
            get { return TaskbarHidden || TaskbarLayered; }
        }

        public static void Init(string baseDir)
        {
            try { path = Path.Combine(baseDir, "config", "system-state.json"); } catch { }
        }

        public static string PathOf(string baseDir)
        {
            return Path.Combine(baseDir, "config", "system-state.json");
        }

        /// <summary>读取上次残留的状态；文件不存在或损坏都返回 null。</summary>
        public static SystemState Load(string baseDir)
        {
            try
            {
                string p = PathOf(baseDir);
                if (!File.Exists(p)) return null;
                string json = File.ReadAllText(p, System.Text.Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return null;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return null;

                SystemState s = new SystemState();
                object v;
                if (d.TryGetValue("taskbarHidden", out v) && v != null) s.TaskbarHidden = Convert.ToBoolean(v);
                if (d.TryGetValue("taskbarLayered", out v) && v != null) s.TaskbarLayered = Convert.ToBoolean(v);
                if (d.TryGetValue("since", out v) && v != null) s.Since = Convert.ToString(v);
                if (d.TryGetValue("version", out v) && v != null) s.Version = Convert.ToString(v);
                return s;
            }
            catch (Exception ex)
            {
                // 损坏的状态文件不能阻断启动：当成“没有残留”处理，并记一条
                Log.Warn("读取 system-state.json 失败（按无残留处理）: " + ex.Message);
                return null;
            }
        }

        /// <summary>把当前状态落盘。Dirty 为 false 时等价于删除标记。</summary>
        public void Save()
        {
            lock (gate)
            {
                try
                {
                    if (string.IsNullOrEmpty(path)) return;
                    if (!Dirty)
                    {
                        Clear();
                        return;
                    }

                    if (string.IsNullOrEmpty(Since)) Since = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    Version = BuildInfo.Version;

                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["taskbarHidden"] = TaskbarHidden;
                    d["taskbarLayered"] = TaskbarLayered;
                    d["since"] = Since;
                    d["version"] = Version;

                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    string json = ser.Serialize(d);

                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    // 用临时文件 + 替换，避免写到一半崩溃留下半个 JSON
                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
                catch (Exception ex)
                {
                    Log.Warn("写 system-state.json 失败: " + ex.Message);
                }
            }
        }

        /// <summary>清掉标记（指定目录）。</summary>
        public static void Clear(string baseDir)
        {
            lock (gate)
            {
                try
                {
                    string p = PathOf(baseDir);
                    if (File.Exists(p)) File.Delete(p);
                    string tmp = p + ".tmp";
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
                catch (Exception ex)
                {
                    Log.Warn("清除 system-state.json 失败: " + ex.Message);
                }
            }
        }

        /// <summary>清掉标记（用 Init 记住的路径）。</summary>
        public void Clear()
        {
            lock (gate)
            {
                try
                {
                    if (string.IsNullOrEmpty(path)) return;
                    if (File.Exists(path)) File.Delete(path);
                    string tmp = path + ".tmp";
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
                catch (Exception ex)
                {
                    Log.Warn("清除 system-state.json 失败: " + ex.Message);
                }
            }
        }
    }
}
