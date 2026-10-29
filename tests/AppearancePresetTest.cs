using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 毛玻璃预设测试。
    ///
    /// 覆盖用户要求的全部项：
    ///   · 预设参数映射正确
    ///   · 切换预设后所有参数都生效
    ///   · 修改单个参数后进入 custom
    ///   · 保存 → 重载 → 一致
    ///   · 恢复预设默认值有效
    ///   · 旧配置兼容
    ///   · 非法 preset / 越界数值安全回退
    ///   · 预设之间确实存在参数与视觉差异
    ///   · glassOpacity 边界（80 → 0.60，100 → 0.50）
    /// </summary>
    static class AppearancePresetTest
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

        [STAThread]
        static void Main()
        {
            Console.WriteLine("=== 毛玻璃预设测试 ===");
            Console.WriteLine();

            TestPresetTable();
            TestApplyPreset();
            TestMarkCustom();
            TestSaveReload();
            TestRestoreDefaults();
            TestOldConfig();
            TestInvalidPreset();
            TestPresetDifferences();
            TestOpacityBoundary();
            TestCustomDoesNotOverwrite();
            TestThemeHandling();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 预设表本身 ----------
        static void TestPresetTable()
        {
            Console.WriteLine("--- 1. 预设表 ---");

            AppearancePresets.Preset[] all = AppearancePresets.AllPresets;
            Check(all.Length == 6, "共 6 个预设", Convert.ToString(all.Length));
            Check(AppearancePresets.BuiltinCount == 5, "其中 5 个内置（不含 custom）",
                Convert.ToString(AppearancePresets.BuiltinCount));

            // custom 必须第一个（界面顺序）
            Check(all[0].IsCustom, "第一个是 custom");

            // 每个预设 Key / DisplayName 非空且唯一
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (AppearancePresets.Preset p in all)
            {
                Check(!string.IsNullOrEmpty(p.Key), "预设 Key 非空：" + p.DisplayName);
                Check(!string.IsNullOrEmpty(p.DisplayName), "预设 DisplayName 非空：" + p.Key);
                Check(keys.Add(p.Key), "Key 唯一：" + p.Key);
                Check(names.Add(p.DisplayName), "DisplayName 唯一：" + p.DisplayName);
            }

            // 内置预设的参数必须在合法范围
            foreach (AppearancePresets.Preset p in all)
            {
                if (p.IsCustom) continue;
                Check(p.Opacity >= 0 && p.Opacity <= 100, p.Key + " opacity 在 0-100",
                    Convert.ToString(p.Opacity));
                Check(p.Blur >= 0 && p.Blur <= 30, p.Key + " blur 在 0-30",
                    Convert.ToString(p.Blur));
                Check(p.TintStrength >= 0 && p.TintStrength <= 100, p.Key + " tintStrength 在 0-100");
                Check(p.Brightness >= -50 && p.Brightness <= 50, p.Key + " brightness 在 -50~50");
                Check(p.Tint == null || p.Tint.Length == 0 || Settings.IsHexColor(p.Tint),
                    p.Key + " tint 是合法颜色或空", p.Tint ?? "null");
                Check(p.Accent == null || p.Accent.Length == 0 || Settings.IsHexColor(p.Accent),
                    p.Key + " accent 是合法颜色或空", p.Accent ?? "null");
                Check(p.Theme == "keep" || p.Theme == "dark" || p.Theme == "light",
                    p.Key + " theme 是 keep/dark/light", p.Theme);
            }
            Console.WriteLine();
        }

        // ---------- 2. 应用预设后参数完全等于表 ----------
        static void TestApplyPreset()
        {
            Console.WriteLine("--- 2. 应用预设 → 参数等于表 ---");

            foreach (AppearancePresets.Preset p in AppearancePresets.AllPresets)
            {
                if (p.IsCustom) continue;

                Settings s = new Settings();
                // 先设成一堆无关值，确保 Apply 会全部覆盖
                s.GlassOpacity = 1; s.GlassBlur = 1.5; s.GlassTint = "#111111";
                s.GlassTintStrength = 1; s.GlassBrightness = 1; s.GlassAccent = "#222222";

                AppearancePresets.Apply(p, s);

                Check(s.GlassPreset == p.Key, p.Key + "：preset 名写入", s.GlassPreset);
                Check(s.GlassOpacity == p.Opacity, p.Key + "：opacity = " + p.Opacity,
                    Convert.ToString(s.GlassOpacity));
                Check(Math.Abs(s.GlassBlur - p.Blur) < 0.0001, p.Key + "：blur = " + p.Blur,
                    Convert.ToString(s.GlassBlur));
                Check((s.GlassTint ?? "") == (p.Tint ?? ""), p.Key + "：tint", s.GlassTint ?? "null");
                Check(s.GlassTintStrength == p.TintStrength, p.Key + "：tintStrength = " + p.TintStrength);
                Check(s.GlassBrightness == p.Brightness, p.Key + "：brightness = " + p.Brightness);
                Check((s.GlassAccent ?? "") == (p.Accent ?? ""), p.Key + "：accent", s.GlassAccent ?? "null");
            }
            Console.WriteLine();
        }

        // ---------- 3. 手动改参数 → custom ----------
        static void TestMarkCustom()
        {
            Console.WriteLine("--- 3. 手动改参数 → custom ---");

            Settings s = new Settings();
            AppearancePresets.Apply(AppearancePresets.Find("frosted"), s);
            Check(s.GlassPreset == "frosted", "先应用磨砂玻璃");

            // 模拟用户拖了 blur 滑块
            s.GlassBlur = 18.0;
            AppearancePresets.MarkCustom(s);

            Check(s.GlassPreset == "custom", "★改参数后 preset 变 custom", s.GlassPreset);
            Check(Math.Abs(s.GlassBlur - 18.0) < 0.0001, "★修改后的值被保留（不被覆盖）",
                Convert.ToString(s.GlassBlur));
            Check(s.GlassOpacity == 75, "其他参数保持磨砂玻璃的值（opacity=75）",
                Convert.ToString(s.GlassOpacity));

            // 已经是 custom 时再调 MarkCustom 不报错
            AppearancePresets.MarkCustom(s);
            Check(s.GlassPreset == "custom", "重复 MarkCustom 幂等");
            Console.WriteLine();
        }

        // ---------- 4. 保存 → 重载 → 一致 ----------
        static void TestSaveReload()
        {
            Console.WriteLine("--- 4. 保存 → 重载 → 一致 ---");

            string root = NewRoot();
            try
            {
                Settings s = Settings.Load(root);
                AppearancePresets.Apply(AppearancePresets.Find("liquid"), s);
                s.Save();

                Settings s2 = Settings.Load(root);
                Check(s2.GlassPreset == "liquid", "★重载后 preset 保持 liquid", s2.GlassPreset);
                Check(s2.GlassOpacity == 45, "重载后 opacity=45", Convert.ToString(s2.GlassOpacity));
                Check(Math.Abs(s2.GlassBlur - 4.0) < 0.0001, "重载后 blur=4",
                    Convert.ToString(s2.GlassBlur));
                Check(s2.GlassBrightness == 8, "重载后 brightness=8", Convert.ToString(s2.GlassBrightness));
                Check(s2.GlassTintStrength == 6, "重载后 tintStrength=6");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 5. 恢复预设默认值 ----------
        static void TestRestoreDefaults()
        {
            Console.WriteLine("--- 5. 恢复预设默认值 ---");

            // 5a. 当前是磨砂玻璃、参数被改过 → 恢复到磨砂玻璃的原始值
            Settings s = new Settings();
            s.GlassPreset = "frosted";
            s.GlassOpacity = 12;      // 被改乱
            s.GlassBlur = 1.0;
            s.GlassBrightness = -40;

            AppearancePresets.Preset restored = AppearancePresets.RestoreDefaults(s);
            Check(restored != null && restored.Key == "frosted", "恢复到磨砂玻璃",
                restored == null ? "null" : restored.Key);
            Check(s.GlassOpacity == 75, "opacity 恢复为 75", Convert.ToString(s.GlassOpacity));
            Check(Math.Abs(s.GlassBlur - 25.0) < 0.0001, "blur 恢复为 25");
            Check(s.GlassBrightness == 5, "brightness 恢复为 5");

            // 5b. 当前是 custom → 回退到经典毛玻璃
            Settings s2 = new Settings();
            s2.GlassPreset = "custom";
            s2.GlassOpacity = 3;
            AppearancePresets.Preset r2 = AppearancePresets.RestoreDefaults(s2);
            Check(r2 != null && r2.Key == "classic", "★custom 时回退到经典毛玻璃",
                r2 == null ? "null" : r2.Key);
            Check(s2.GlassOpacity == 80, "opacity = 经典毛玻璃的 80");

            // 5c. 恢复默认不改 Theme
            Settings s3 = new Settings();
            s3.Theme = "light";
            s3.GlassPreset = "dark";      // dark 预设本来要切深色
            AppearancePresets.RestoreDefaults(s3);
            Check(s3.Theme == "light", "★恢复默认不动全局主题", s3.Theme);
            Console.WriteLine();
        }

        // ---------- 6. 旧配置兼容 ----------
        static void TestOldConfig()
        {
            Console.WriteLine("--- 6. 旧配置兼容 ---");

            // 6a. 第 1 个提交之前的配置：只有 glassOpacity / glassMode
            string root = NewRoot();
            try
            {
                File.WriteAllText(Path.Combine(root, "config", "settings.json"),
                    "{\"theme\":\"dark\",\"glassOpacity\":80,\"glassMode\":\"fixed\"}",
                    new UTF8Encoding(false));

                Settings s = Settings.Load(root);
                Check(s.GlassPreset == "custom", "★旧配置 → preset 默认 custom", s.GlassPreset);
                Check(Math.Abs(s.GlassBlur - 10.0) < 0.0001, "旧配置 → blur 默认 10");
                Check(s.GlassOpacity == 80, "旧配置 → opacity 保持 80");
            }
            finally { Clean(root); }

            // 6b. 未知 preset 名
            root = NewRoot();
            try
            {
                File.WriteAllText(Path.Combine(root, "config", "settings.json"),
                    "{\"glassPreset\":\"nonexistent-preset\"}", new UTF8Encoding(false));
                Settings s = Settings.Load(root);
                Check(s.GlassPreset == "custom", "★未知 preset → 回退 custom", s.GlassPreset);
            }
            finally { Clean(root); }

            // 6c. 大小写
            root = NewRoot();
            try
            {
                File.WriteAllText(Path.Combine(root, "config", "settings.json"),
                    "{\"glassPreset\":\"FROSTED\"}", new UTF8Encoding(false));
                Settings s = Settings.Load(root);
                Check(s.GlassPreset == "frosted", "★大小写不敏感（FROSTED → frosted）", s.GlassPreset);
            }
            finally { Clean(root); }

            // 6d. 空串
            root = NewRoot();
            try
            {
                File.WriteAllText(Path.Combine(root, "config", "settings.json"),
                    "{\"glassPreset\":\"\"}", new UTF8Encoding(false));
                Settings s = Settings.Load(root);
                Check(s.GlassPreset == "custom", "空串 → 回退 custom", s.GlassPreset);
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 7. 非法 preset ----------
        static void TestInvalidPreset()
        {
            Console.WriteLine("--- 7. 非法 preset 与 null 安全 ---");

            Check(AppearancePresets.Find(null) == null, "Find(null) = null");
            Check(AppearancePresets.Find("") == null, "Find(\"\") = null");
            Check(AppearancePresets.Find("   ") == null, "Find(空白) = null");
            Check(AppearancePresets.Find("nope") == null, "Find(不存在) = null");

            Check(AppearancePresets.FindOrCustom(null) != null, "FindOrCustom(null) 不返回 null");
            Check(AppearancePresets.FindOrCustom(null).IsCustom, "FindOrCustom(null) = custom");
            Check(AppearancePresets.FindOrCustom("nope").IsCustom, "FindOrCustom(不存在) = custom");

            Check(!AppearancePresets.IsValidKey(null), "IsValidKey(null) = false");
            Check(!AppearancePresets.IsValidKey("nope"), "IsValidKey(不存在) = false");
            Check(AppearancePresets.IsValidKey("custom"), "IsValidKey(custom) = true");
            Check(AppearancePresets.IsValidKey("CLASSIC"), "IsValidKey(大小写不敏感) = true");

            // Apply(null) 不抛
            bool threw = false;
            try { AppearancePresets.Apply(null, new Settings()); } catch { threw = true; }
            Check(!threw, "Apply(null, s) 不抛异常");
            threw = false;
            try { AppearancePresets.Apply(AppearancePresets.Find("classic"), null); } catch { threw = true; }
            Check(!threw, "Apply(p, null) 不抛异常");
            Console.WriteLine();
        }

        // ---------- 8. 预设之间确实有差异 ----------
        static void TestPresetDifferences()
        {
            Console.WriteLine("--- 8. 预设之间确实有差异（参数层面）---");

            List<AppearancePresets.Preset> builtins = new List<AppearancePresets.Preset>();
            foreach (AppearancePresets.Preset p in AppearancePresets.AllPresets)
                if (!p.IsCustom) builtins.Add(p);

            // 两两对比：至少 2 个维度明显不同
            for (int i = 0; i < builtins.Count; i++)
            {
                for (int j = i + 1; j < builtins.Count; j++)
                {
                    AppearancePresets.Preset a = builtins[i], b = builtins[j];
                    int diffs = 0;
                    if (Math.Abs(a.Opacity - b.Opacity) >= 5) diffs++;
                    if (Math.Abs(a.Blur - b.Blur) >= 2) diffs++;
                    if (!string.Equals(a.Tint ?? "", b.Tint ?? "", StringComparison.OrdinalIgnoreCase)) diffs++;
                    if (Math.Abs(a.TintStrength - b.TintStrength) >= 5) diffs++;
                    if (Math.Abs(a.Brightness - b.Brightness) >= 5) diffs++;
                    if (!string.Equals(a.Accent ?? "", b.Accent ?? "", StringComparison.OrdinalIgnoreCase)) diffs++;

                    Check(diffs >= 2, "★" + a.Key + " vs " + b.Key + "：至少 2 个维度不同",
                        "实际 " + diffs + " 个");
                }
            }
            Console.WriteLine();
        }

        // ---------- 9. glassOpacity 边界 ----------
        static void TestOpacityBoundary()
        {
            Console.WriteLine("--- 9. glassOpacity → 面板底色 alpha ---");

            // 复刻 PanelWindow.ApplyGlassTint 的映射公式
            Check(Math.Abs(AlphaOf(0) - 1.00) < 0.001, "opacity=0 → alpha=1.00（纯色）",
                AlphaOf(0).ToString("F2"));
            Check(Math.Abs(AlphaOf(50) - 0.75) < 0.001, "opacity=50 → alpha=0.75",
                AlphaOf(50).ToString("F2"));
            Check(Math.Abs(AlphaOf(80) - 0.60) < 0.001, "★opacity=80 → alpha=0.60（老配置不变）",
                AlphaOf(80).ToString("F2"));
            Check(Math.Abs(AlphaOf(100) - 0.50) < 0.001, "★opacity=100 → alpha=0.50（改后终于更透）",
                AlphaOf(100).ToString("F2"));

            // 单调性：opacity 越大 alpha 越小
            double prev = AlphaOf(0);
            bool monotonic = true;
            for (int g = 5; g <= 100; g += 5)
            {
                double cur = AlphaOf(g);
                if (cur > prev + 0.0001) { monotonic = false; break; }
                prev = cur;
            }
            Check(monotonic, "★opacity 增大 → alpha 单调递减");

            // 下限 0.40
            Check(AlphaOf(100) >= 0.40 - 0.0001, "alpha 不低于 0.40 下限",
                AlphaOf(100).ToString("F2"));
            Console.WriteLine();
        }

        /// <summary>复刻 PanelWindow.ApplyGlassTint 的 alpha 映射（下限 0.40）。</summary>
        static double AlphaOf(int g)
        {
            if (g <= 0) return 1.0;
            double alpha = 1.0 - (g / 100.0) * 0.50;
            if (alpha < 0.40) alpha = 0.40;
            return alpha;
        }

        // ---------- 10. custom 不覆盖用户参数 ----------
        static void TestCustomDoesNotOverwrite()
        {
            Console.WriteLine("--- 10. 选 custom 不覆盖用户参数 ---");

            Settings s = new Settings();
            s.GlassPreset = "classic";
            s.GlassOpacity = 33;
            s.GlassBlur = 7.5;
            s.GlassTint = "#ABCDEF";
            s.GlassTintStrength = 22;
            s.GlassBrightness = -13;
            s.GlassAccent = "#123456";

            AppearancePresets.Preset custom = AppearancePresets.Find("custom");
            AppearancePresets.Apply(custom, s);

            Check(s.GlassPreset == "custom", "preset 变 custom");
            Check(s.GlassOpacity == 33, "★opacity 未被覆盖", Convert.ToString(s.GlassOpacity));
            Check(Math.Abs(s.GlassBlur - 7.5) < 0.0001, "★blur 未被覆盖");
            Check(s.GlassTint == "#ABCDEF", "★tint 未被覆盖", s.GlassTint);
            Check(s.GlassTintStrength == 22, "★tintStrength 未被覆盖");
            Check(s.GlassBrightness == -13, "★brightness 未被覆盖");
            Check(s.GlassAccent == "#123456", "★accent 未被覆盖");
            Console.WriteLine();
        }

        // ---------- 11. theme 处理 ----------
        static void TestThemeHandling()
        {
            Console.WriteLine("--- 11. 预设对全局主题的处理 ---");

            // dark 预设 → 切深色
            Settings s = new Settings();
            s.Theme = "light";
            AppearancePresets.Apply(AppearancePresets.Find("dark"), s);
            Check(s.Theme == "dark", "★深色玻璃 → Theme=dark", s.Theme);

            // light 预设 → 切浅色
            Settings s2 = new Settings();
            s2.Theme = "dark";
            AppearancePresets.Apply(AppearancePresets.Find("light"), s2);
            Check(s2.Theme == "light", "★浅色玻璃 → Theme=light", s2.Theme);

            // 其他预设 → 不改主题
            foreach (string k in new string[] { "classic", "frosted", "liquid" })
            {
                Settings s3 = new Settings();
                s3.Theme = "system";
                AppearancePresets.Apply(AppearancePresets.Find(k), s3);
                Check(s3.Theme == "system", "★" + k + " → Theme 不变（keep）", s3.Theme);
            }

            // custom → 不改主题
            Settings s4 = new Settings();
            s4.Theme = "dark";
            AppearancePresets.Apply(AppearancePresets.Find("custom"), s4);
            Check(s4.Theme == "dark", "custom → Theme 不变", s4.Theme);
            Console.WriteLine();
        }

        // ========== 辅助 ==========

        static string NewRoot()
        {
            string d = Path.Combine(Path.GetTempPath(), "preset-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
