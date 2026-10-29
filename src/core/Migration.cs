using System;
using System.Collections.Generic;
using System.IO;

namespace Clover
{
    /// <summary>
    /// 一次性数据迁移：把「旧 hiddenCategories 隐藏的整类应用」转成「逐条隐藏」。
    ///
    /// **为什么需要这个迁移**
    ///
    /// 以前 `settings.json` 有 `hiddenCategories`（默认 `["系统工具"]`），
    /// 靠「隐藏整个分类」来减少噪音 —— 用户从没看见过这些应用。
    ///
    /// 分类体系移除后，这个机制消失。如果不做任何处理，
    /// 那 60+ 个被分类隐藏的应用会**一次性全冒出来**，面板突然变乱。
    ///
    /// 解决办法：把「分类隐藏」翻译成「逐条隐藏」——
    /// 读旧 `apps.json` 的 `category` 字段，找出属于旧 `hiddenCategories` 的应用，
    /// 把它们的路径并入 `hidden-apps.json`。
    ///
    /// **三条硬性要求**（用户明确指定）：
    ///   1. 必须在任何会重写 `apps.json` 的扫描之前完成
    ///      （扫描会重写 category 字段，之后就读不到了）
    ///   2. 幂等：重复运行结果不变
    ///   3. `hidden-apps.json` 读不出时**不要覆盖它**（宁可跳过迁移，不丢用户数据）
    ///
    /// **标记文件**：`config\migration-category-hidden.json`
    ///   记录「已迁移过」以及迁移了什么，方便排查。
    ///   但幂等性不依赖这个文件 —— 即使删掉它重跑，结果也一样
    ///   （因为 `HiddenApps.Hide` 对已存在的路径返回 false，不会重复写入）。
    /// </summary>
    static class Migration
    {
        /// <summary>标记文件路径。</summary>
        public static string MarkerPath(string baseDir)
        {
            return Path.Combine(baseDir, "config", "migration-category-hidden.json");
        }

        /// <summary>
        /// 迁移结果（写日志 / 测试断言用）。
        /// </summary>
        public class Result
        {
            /// <summary>是否实际做了迁移（false = 已迁移过 / 无需迁移 / 数据不全）</summary>
            public bool Performed;

            /// <summary>跳过原因（Performed=false 时有值）。</summary>
            public string SkipReason;

            /// <summary>
            /// 是否应该写标记文件。
            ///
            /// **只有两种情况写标记**：
            ///   · 迁移完成（Performed = true）
            ///   · 确认无事可做（旧配置里没有 hiddenCategories、apps.json 已无 category 字段、
            ///     没有条目属于旧隐藏分类 —— 这些是**终态**，重试也不会有不同结果）
            ///
            /// **临时错误不写**（读 settings / apps.json / hidden-apps.json 失败、异常）：
            /// 下次启动重试，避免一次偶发 IO 错误把迁移永久跳过。
            /// </summary>
            public bool ShouldWriteMarker;

            /// <summary>旧 hiddenCategories 里的分类名。</summary>
            public List<string> HiddenCategories = new List<string>();

            /// <summary>apps.json 里读到的总条目数。</summary>
            public int TotalApps;

            /// <summary>有 category 字段的条目数（0 说明是分类移除后的新格式）。</summary>
            public int AppsWithCategory;

            /// <summary>匹配到旧隐藏分类的条目数。</summary>
            public int Matched;

            /// <summary>实际新加入 hidden-apps.json 的路径数。</summary>
            public int NewlyHidden;

            /// <summary>已经在 hidden-apps.json 里、跳过的路径数。</summary>
            public int AlreadyHidden;

            public string Report()
            {
                if (!Performed)
                    return "分类隐藏迁移: 跳过（" + SkipReason + "）"
                         + (ShouldWriteMarker ? " [已记录，不再重试]" : " [临时问题，下次启动重试]");

                return "分类隐藏迁移: 旧隐藏分类 ["
                     + string.Join(", ", HiddenCategories.ToArray()) + "]"
                     + " | apps.json " + TotalApps + " 条（有分类 " + AppsWithCategory + "）"
                     + " | 匹配 " + Matched + " 条"
                     + " | 新隐藏 " + NewlyHidden + " 条"
                     + " | 已隐藏跳过 " + AlreadyHidden + " 条";
            }
        }

        /// <summary>
        /// 执行迁移。**必须在任何重写 apps.json 的扫描之前调用。**
        ///
        /// 调用点：`Catalog.Load` 的最早阶段（读 apps.json 之前）。
        /// 这样即使随后触发扫描重写 apps.json，迁移也已经拿到旧数据了。
        /// </summary>
        public static Result Run(string baseDir, Settings settings)
        {
            Result r = new Result();
            try
            {
                if (string.IsNullOrEmpty(baseDir))
                {
                    // baseDir 为空是调用方问题，下次可能不同 → 不写标记
                    r.SkipReason = "baseDir 为空";
                    r.ShouldWriteMarker = false;
                    return r;
                }

                // ---- 1. 已经迁移过？----
                if (File.Exists(MarkerPath(baseDir)))
                {
                    r.SkipReason = "已迁移过（标记文件存在）";
                    r.ShouldWriteMarker = false;   // 标记已存在，不用重写
                    return r;
                }

                // ---- 2. 旧 hiddenCategories 有内容吗？----
                //
                // **直接读原始 settings.json**，不经 Settings 类：
                // 本阶段删除了 Settings.HiddenCategories 字段，
                // 但老用户磁盘上的 settings.json 里可能还有这个键，
                // 迁移必须能读到它 —— 否则迁移永远「无事可做」，
                // 用户之前隐藏的整类应用会全部重新冒出来。
                // 读失败（ok=false）→ **不写标记**，下次启动重试。
                // 读成功但为空（ok=true, Count=0）→ 终态，写标记。
                bool cfgOk;
                List<string> hiddenCats = ReadHiddenCategoriesRaw(baseDir, out cfgOk);
                if (!cfgOk)
                {
                    // settings.json 还没就绪 / 损坏 / 字段类型错。
                    // 这时判定终态会让用户**永久失去迁移机会**，所以不写标记。
                    r.SkipReason = "settings.json 读不出（未就绪或损坏，下次重试）";
                    r.ShouldWriteMarker = false;
                    return r;
                }
                if (hiddenCats.Count == 0)
                {
                    r.SkipReason = "旧配置里没有 hiddenCategories（无需迁移）";
                    r.ShouldWriteMarker = true;    // 终态：这个用户不需要迁移
                    if (r.ShouldWriteMarker) WriteMarker(baseDir, r);
                    return r;
                }
                r.HiddenCategories = hiddenCats;

                // ---- 3. 读旧 apps.json 的 category 字段 ----
                string appsPath = Path.Combine(baseDir, "config", "apps.json");
                if (!File.Exists(appsPath))
                {
                    // apps.json 可能只是还没生成（首次运行）→ 下次重试
                    r.SkipReason = "apps.json 不存在";
                    r.ShouldWriteMarker = false;
                    return r;
                }

                List<KeyValuePair<string, string>> apps = ReadAppsWithCategory(appsPath, r);
                if (apps.Count == 0)
                {
                    // 解析失败 / IO 错误 → 下次重试，不写标记
                    r.SkipReason = "apps.json 读不出条目";
                    r.ShouldWriteMarker = false;
                    return r;
                }
                if (r.AppsWithCategory == 0)
                {
                    // 分类移除后重扫过的 apps.json 没有 category 字段 —— 无从迁移
                    r.SkipReason = "apps.json 已无 category 字段（分类移除后重扫过）";
                    r.ShouldWriteMarker = true;    // 终态：旧数据已经不存在
                    if (r.ShouldWriteMarker) WriteMarker(baseDir, r);
                    return r;
                }

                // ---- 4. 匹配 ----
                HashSet<string> catSet = new HashSet<string>(hiddenCats, StringComparer.Ordinal);
                List<string> matchedPaths = new List<string>();
                foreach (KeyValuePair<string, string> kv in apps)
                {
                    if (string.IsNullOrEmpty(kv.Value)) continue;
                    if (!catSet.Contains(kv.Value)) continue;
                    r.Matched++;
                    if (!string.IsNullOrEmpty(kv.Key)) matchedPaths.Add(kv.Key);
                }

                if (matchedPaths.Count == 0)
                {
                    r.SkipReason = "没有条目属于旧隐藏分类";
                    r.ShouldWriteMarker = true;    // 终态：这个用户没有需要迁移的条目
                    if (r.ShouldWriteMarker) WriteMarker(baseDir, r);
                    return r;
                }

                // ---- 5. 并入 hidden-apps.json ----
                //
                // 关键：hidden-apps.json 读不出时**不要覆盖它**。
                // HiddenApps.Load 读失败会返回空对象（Warn 已记录），
                // 此时如果直接 Hide + Save，会把用户原有的隐藏清单整个冲掉。
                //
                // 所以先探测：文件存在但解析失败 → 放弃迁移。
                HiddenApps hidden = HiddenApps.Load(baseDir);
                string hiddenPath = HiddenApps.PathOf(baseDir);
                if (File.Exists(hiddenPath) && hidden.Count == 0)
                {
                    // 文件存在却一条都读不出来，说明解析失败（或文件真的是空清单）
                    // 保守处理：只有文件内容非空才算"读不出"
                    string raw = JsonIO.Read(hiddenPath);
                    if (!string.IsNullOrEmpty(raw) && raw.IndexOf("\"paths\"", StringComparison.Ordinal) < 0)
                    {
                        r.SkipReason = "hidden-apps.json 读不出（已保留原文件，不覆盖）";
                        r.ShouldWriteMarker = false;   // 下次重试，别把用户数据永久跳过
                        return r;
                    }
                }

                foreach (string p in matchedPaths)
                {
                    if (hidden.IsHidden(p)) { r.AlreadyHidden++; continue; }
                    if (hidden.Hide(p)) r.NewlyHidden++;
                    else r.AlreadyHidden++;
                }

                r.Performed = true;
                r.ShouldWriteMarker = true;
                if (r.ShouldWriteMarker) WriteMarker(baseDir, r);
                Log.Info(r.Report());
                return r;
            }
            catch (Exception ex)
            {
                Log.Hook(ex, "分类隐藏迁移");
                r.SkipReason = "异常: " + ex.Message;
                r.ShouldWriteMarker = false;   // 异常是临时的，下次重试
                return r;
            }
        }

        /// <summary>
        /// <summary>
        /// 从**原始 settings.json** 读旧 hiddenCategories 数组。
        ///
        /// 为什么不走 Settings 类：本阶段删掉了 Settings.HiddenCategories 字段，
        /// 但老用户磁盘上的 JSON 里可能还有这个键。迁移必须能读到它，
        /// 否则「隐藏整类」的历史数据就再也迁不出来。
        ///
        /// **必须区分「读到空」和「读不到」**（ok 参数）：
        ///
        ///   ok = false（读不到 → 本次**不写标记**，下次重试）
        ///     · settings.json 不存在（程序可能还没生成它）
        ///     · 文件读取失败 / IO 异常
        ///     · JSON 损坏 / 解析异常 / 根不是对象
        ///     · hiddenCategories 存在但类型不是数组
        ///
        ///   ok = true（读成功 → 可据此判定终态）
        ///     · 没有 hiddenCategories 键      → 列表为空
        ///     · hiddenCategories 是空数组      → 列表为空
        ///     · hiddenCategories 是非空数组    → 列表有值
        ///
        /// **为什么这个区分是必须的**：如果读失败也返回空列表，调用方会把它
        /// 当成「这个用户没有旧数据」→ 写标记 → 用户修复配置后再也不会迁移，
        /// 原本隐藏的整类应用全部重新冒出来。
        /// </summary>
        static List<string> ReadHiddenCategoriesRaw(string baseDir, out bool ok)
        {
            List<string> list = new List<string>();
            ok = false;
            try
            {
                string path = Path.Combine(baseDir, "config", "settings.json");

                // 文件不存在：可能是首次运行（还没生成），也可能被用户删了。
                // 两种情况都不该判定为「终态」—— 下次启动再看。
                if (!File.Exists(path)) return list;

                string json = JsonIO.Read(path);
                if (string.IsNullOrEmpty(json)) return list;   // 读失败

                System.Web.Script.Serialization.JavaScriptSerializer ser =
                    new System.Web.Script.Serialization.JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return list;                    // 根不是对象

                // 到这里 JSON 本体是好的 —— 无论有没有这个键，都算「读成功」
                ok = true;

                object arr;
                if (!d.TryGetValue("hiddenCategories", out arr)) return list;   // 没有键 → 空
                if (arr == null) return list;                                   // 值为 null → 空

                // 类型检查：必须是**数组**，否则视为读失败。
                //
                // JavaScriptSerializer 的实际行为（实测）：
                //   {"x":[]}        → System.Collections.ArrayList（空）
                //   {"x":["a"]}     → System.Collections.ArrayList
                //   {"x":"str"}     → System.String       ← 不是数组
                //   {"x":null}      → null                 ← 上面已处理
                //
                // 所以判断条件是「ArrayList 或 object[]」，**不能**用 `is IEnumerable`
                // —— string 也实现了 IEnumerable<char>，会把类型错误误判成合法。
                if (!(arr is System.Collections.ArrayList) && !(arr is object[]))
                {
                    ok = false;
                    Log.Warn("迁移: settings.hiddenCategories 不是数组（类型 "
                        + arr.GetType().Name + "），本次不判定终态");
                    return list;
                }

                foreach (object o in JsonIO.AsArray(arr))
                {
                    string c = o as string;
                    if (string.IsNullOrWhiteSpace(c)) continue;
                    list.Add(c.Trim());
                }
            }
            catch (Exception ex)
            {
                // 解析异常 → 读失败（ok 保持 false）
                ok = false;
                Log.Warn("迁移: 读原始 settings.json 的 hiddenCategories 失败: " + ex.Message);
            }
            return list;
        }

        /// <summary>
        /// 读 apps.json，返回 (path, category) 列表。
        /// 同时填充 r.TotalApps / r.AppsWithCategory。
        /// </summary>
        static List<KeyValuePair<string, string>> ReadAppsWithCategory(string appsPath, Result r)
        {
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
            try
            {
                string json = JsonIO.Read(appsPath);
                if (string.IsNullOrEmpty(json)) return list;

                System.Web.Script.Serialization.JavaScriptSerializer ser =
                    new System.Web.Script.Serialization.JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> d = ser.Deserialize<Dictionary<string, object>>(json);
                if (d == null) return list;

                object arr;
                if (!d.TryGetValue("apps", out arr) || arr == null) return list;

                foreach (object o in JsonIO.AsArray(arr))
                {
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m == null) continue;
                    r.TotalApps++;

                    string path = GetStr(m, "path");
                    string cat = GetStr(m, "category");
                    if (!string.IsNullOrEmpty(cat)) r.AppsWithCategory++;

                    if (string.IsNullOrEmpty(path)) continue;
                    list.Add(new KeyValuePair<string, string>(path, cat));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("迁移: 读 apps.json 失败: " + ex.Message);
            }
            return list;
        }

        static string GetStr(Dictionary<string, object> m, string key)
        {
            object v;
            if (m.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return "";
        }

        /// <summary>写标记文件（幂等：已存在就不重写）。</summary>
        static void WriteMarker(string baseDir, Result r)
        {
            try
            {
                string p = MarkerPath(baseDir);
                if (File.Exists(p)) return;

                Dictionary<string, object> d = new Dictionary<string, object>();
                d["version"] = 1;
                d["migratedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                d["performed"] = r.Performed;
                d["skipReason"] = r.SkipReason ?? "";
                d["hiddenCategories"] = r.HiddenCategories;
                d["totalApps"] = r.TotalApps;
                d["appsWithCategory"] = r.AppsWithCategory;
                d["matched"] = r.Matched;
                d["newlyHidden"] = r.NewlyHidden;
                d["alreadyHidden"] = r.AlreadyHidden;

                System.Web.Script.Serialization.JavaScriptSerializer ser =
                    new System.Web.Script.Serialization.JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                JsonIO.WriteAtomic(p, ser.Serialize(d));
            }
            catch (Exception ex)
            {
                Log.Warn("迁移: 写标记文件失败: " + ex.Message);
            }
        }
    }
}
