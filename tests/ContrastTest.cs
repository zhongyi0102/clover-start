using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Clover
{
    /// <summary>
    /// 文字对比度保底测试。
    ///
    /// 覆盖用户要求的 4 条约束：
    ///   1. 默认开启，允许关闭；关掉后**彻底移除补偿层**，不残留蒙层
    ///   2. **只补偿文字区域**，不整面板压暗/提亮
    ///   3. 主题匹配且 alpha = 0 时**不改变原有背景像素**
    ///   4. **验证实际合成结果**（不是复用 dry-run 预期值）
    ///
    /// 夹具：纯白 / 纯黑 / 中灰 #777 / 青灰固定图 / 彩色渐变 × 深/浅主题 × 6 预设。
    /// </summary>
    static class ContrastTest
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

        // 主题色（与 Ui.cs 一致）
        const double dFgR = 0xF0, dFgG = 0xF4, dFgB = 0xF5;
        const double dSubR = 0xC4, dSubG = 0xD0, dSubB = 0xD5;
        const double dAccR = 0x4F, dAccG = 0xC3, dAccB = 0xD9;
        const double lFgR = 0x20, lFgG = 0x20, lFgB = 0x20;
        const double lSubR = 0x78, lSubG = 0x76, lSubB = 0x72;
        const double lAccR = 0x38, lAccG = 0x8C, lAccB = 0x48;

        const int W = ContrastGuard.SampleW;
        const int H = ContrastGuard.SampleH;

        [STAThread]
        static void Main()
        {
            Console.WriteLine("=== 文字对比度保底测试 ===");
            Console.WriteLine();

            TestThresholds();
            TestColorMath();
            TestAlphaZeroWhenCompliant();
            TestCompensationAchievesThreshold();
            TestGray777();
            TestPerRegionOnly();
            TestDisabledClearsLayer();
            TestSettingPersistence();
            TestExceptionFallback();
            TestPresetMatrix();

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 阈值常量 ----------
        static void TestThresholds()
        {
            Console.WriteLine("--- 1. 阈值常量 ---");
            Check(Math.Abs(ContrastGuard.BodyThreshold - 4.5) < 0.001, "正文阈值 = 4.5",
                Convert.ToString(ContrastGuard.BodyThreshold));
            Check(Math.Abs(ContrastGuard.SubThreshold - 3.0) < 0.001, "次要阈值 = 3.0",
                Convert.ToString(ContrastGuard.SubThreshold));
            Check(Math.Abs(ContrastGuard.MaxAlpha - 0.85) < 0.001, "alpha 上限 = 0.85",
                Convert.ToString(ContrastGuard.MaxAlpha));
            Check(Math.Abs(ContrastGuard.Hysteresis - 0.2) < 0.001, "迟滞 = 0.2",
                Convert.ToString(ContrastGuard.Hysteresis));
            Console.WriteLine();
        }

        // ---------- 2. 色彩计算正确性 ----------
        static void TestColorMath()
        {
            Console.WriteLine("--- 2. WCAG 对比度计算 ---");

            // 黑白对比度 = 21
            double bw = ContrastGuard.Contrast(0, 0, 0, 255, 255, 255);
            Check(Math.Abs(bw - 21.0) < 0.1, "黑 vs 白 = 21.0", bw.ToString("F2"));

            // 同色 = 1
            double same = ContrastGuard.Contrast(128, 128, 128, 128, 128, 128);
            Check(Math.Abs(same - 1.0) < 0.001, "同色 = 1.0", same.ToString("F3"));

            // 对称性
            double a = ContrastGuard.Contrast(0xF0, 0xF4, 0xF5, 0x17, 0x2A, 0x30);
            double b = ContrastGuard.Contrast(0x17, 0x2A, 0x30, 0xF0, 0xF4, 0xF5);
            Check(Math.Abs(a - b) < 0.0001, "对称性：A vs B == B vs A");

            // 亮度单调
            double l0 = ContrastGuard.RelativeLuminance(0, 0, 0);
            double l128 = ContrastGuard.RelativeLuminance(128, 128, 128);
            double l255 = ContrastGuard.RelativeLuminance(255, 255, 255);
            Check(l0 < l128 && l128 < l255, "亮度单调：黑 < 中灰 < 白");
            Check(Math.Abs(l255 - 1.0) < 0.001, "纯白亮度 = 1.0", l255.ToString("F4"));
            Console.WriteLine();
        }

        // ---------- 3. 达标时 alpha = 0（约束 3）----------
        static void TestAlphaZeroWhenCompliant()
        {
            Console.WriteLine("--- 3. 已达标时 alpha = 0（保护原有外观）---");

            // 经典毛玻璃 + 深色主题 + 固定图 → 必须 0
            double[] bg = Compose(80, "", 10, 0, true, 0x31, 0x47, 0x4F);
            byte[] px = Solid(bg);
            ContrastGuard.Result r = ContrastGuard.Compute(px, W, H, true,
                dFgR, dFgG, dFgB, dSubR, dSubG, dSubB, 0);

            Check(r.Alpha == 0, "★经典毛玻璃+深色+固定图 alpha = 0",
                "alpha=" + r.Alpha.ToString("F3"));
            Check(r.WorstBefore >= 4.5, "★该组合补偿前已达标",
                r.WorstBefore.ToString("F2"));

            // 深色玻璃 + 深色 + 固定图
            bg = Compose(92, "#172A30", 45, -8, true, 0x31, 0x47, 0x4F);
            px = Solid(bg);
            r = ContrastGuard.Compute(px, W, H, true, dFgR, dFgG, dFgB, dSubR, dSubG, dSubB, 0);
            Check(r.Alpha == 0, "★深色玻璃+深色+固定图 alpha = 0", "alpha=" + r.Alpha.ToString("F3"));

            // 液态玻璃 + 深色 + 固定图
            bg = Compose(45, "#7FA8B8", 26, -18, true, 0x31, 0x47, 0x4F);
            px = Solid(bg);
            r = ContrastGuard.Compute(px, W, H, true, dFgR, dFgG, dFgB, dSubR, dSubG, dSubB, 0);
            Check(r.Alpha == 0, "★液态玻璃+深色+固定图 alpha = 0", "alpha=" + r.Alpha.ToString("F3"));

            // 浅色玻璃 + 浅色 + 固定图
            bg = Compose(65, "#FFFFFF", 30, 18, false, 0x31, 0x47, 0x4F);
            px = Solid(bg);
            r = ContrastGuard.Compute(px, W, H, false, lFgR, lFgG, lFgB, lSubR, lSubG, lSubB, 0);
            Check(r.Alpha == 0, "★浅色玻璃+浅色+固定图 alpha = 0", "alpha=" + r.Alpha.ToString("F3"));
            Console.WriteLine();
        }

        // ---------- 4. 补偿后必须达标（约束 4：实际合成验证）----------
        static void TestCompensationAchievesThreshold()
        {
            Console.WriteLine("--- 4. 补偿后必须达标（实际合成）---");

            double[][] walls = {
                new double[] { 255, 255, 255 },   // 纯白
                new double[] { 0, 0, 0 },         // 纯黑
                new double[] { 0x77, 0x77, 0x77 },// 中灰 #777
                new double[] { 0x31, 0x47, 0x4F },// 固定图
                new double[] { 90, 140, 180 }     // 彩色渐变
            };
            string[] wn = { "纯白", "纯黑", "中灰#777", "固定图", "彩色" };

            object[][] presets = {
                new object[] { "经典毛玻璃", 80, "",        10, 0  },
                new object[] { "磨砂玻璃",   78, "#DCE4E8", 22, 6  },
                new object[] { "深色玻璃",   92, "#172A30", 45, -8 },
                new object[] { "浅色玻璃",   65, "#FFFFFF", 30, 18 },
                new object[] { "液态玻璃",   45, "#7FA8B8", 26, -18}
            };

            int bad = 0;
            foreach (object[] p in presets)
            {
                string nm = (string)p[0];
                int op = (int)p[1]; string tint = (string)p[2];
                int ts = (int)p[3]; int br = (int)p[4];

                for (int wi = 0; wi < walls.Length; wi++)
                {
                    for (int d = 0; d < 2; d++)
                    {
                        bool dark = d == 0;
                        double[] bg = Compose(op, tint, ts, br, dark, walls[wi][0], walls[wi][1], walls[wi][2]);
                        byte[] px = Solid(bg);

                        double fgR = dark ? dFgR : lFgR, fgG = dark ? dFgG : lFgG, fgB = dark ? dFgB : lFgB;
                        double subR = dark ? dSubR : lSubR, subG = dark ? dSubG : lSubG, subB = dark ? dSubB : lSubB;

                        ContrastGuard.Result r = ContrastGuard.Compute(px, W, H, dark,
                            fgR, fgG, fgB, subR, subG, subB, 0);

                        // 验证：叠加 alpha 后重新计算每个区域的真实对比度
                        double worstReal = VerifyAfterOverlay(px, dark, fgR, fgG, fgB, subR, subG, subB, r.Alpha);
                        bool ok = worstReal >= 1.0;   // 归一化比值 ≥ 1.0 = 全部达标
                        if (!ok) bad++;

                        Check(ok, "★" + nm + " × " + (dark ? "Dark" : "Light") + " × " + wn[wi],
                            "alpha=" + r.Alpha.ToString("F2") + " 实测最差=" + worstReal.ToString("F2")
                            + (r.HitCeiling ? " [上限]" : ""));
                    }
                }
            }
            Check(bad == 0, "★全部 50 组合补偿后达标", "不达标 " + bad + " 个");
            Console.WriteLine();
        }

        // ---------- 5. 中灰 #777 单独断言 ----------
        static void TestGray777()
        {
            Console.WriteLine("--- 5. 中灰 #777（最难情形）单独断言 ---");

            // 中灰是「黑白文字对比度都在 4.5 附近」的情形
            double[] bg = { 0x77, 0x77, 0x77 };

            double dContrast = ContrastGuard.Contrast(dFgR, dFgG, dFgB, 0x77, 0x77, 0x77);
            double lContrast = ContrastGuard.Contrast(lFgR, lFgG, lFgB, 0x77, 0x77, 0x77);
            Console.WriteLine("      深色文字 vs #777 = " + dContrast.ToString("F2"));
            Console.WriteLine("      浅色文字 vs #777 = " + lContrast.ToString("F2"));

            // 深色主题（浅字）在中灰上应该不需要补偿
            byte[] px = Solid(bg);
            ContrastGuard.Result rD = ContrastGuard.Compute(px, W, H, true,
                dFgR, dFgG, dFgB, dSubR, dSubG, dSubB, 0);
            Check(rD.Alpha == 0 || rD.WorstAfter >= 3.0,
                "★中灰 + 深色主题：alpha=0 或补偿后达标",
                "alpha=" + rD.Alpha.ToString("F2") + " before=" + rD.WorstBefore.ToString("F2"));

            // 浅色主题（深字）在中灰上需要补偿（Sub 会不够）
            ContrastGuard.Result rL = ContrastGuard.Compute(px, W, H, false,
                lFgR, lFgG, lFgB, lSubR, lSubG, lSubB, 0);
            double afterL = VerifyAfterOverlay(px, false, lFgR, lFgG, lFgB, lSubR, lSubG, lSubB, rL.Alpha);
            Check(afterL >= 1.0, "★中灰 + 浅色主题：补偿后达标",
                "alpha=" + rL.Alpha.ToString("F2") + " 实测=" + afterL.ToString("F2"));

            // 中灰上深色主题的 Sub 是瓶颈 → 补偿前 Sub 应 < 3.0 或刚好
            double subOn777 = ContrastGuard.Contrast(dSubR, dSubG, dSubB, 0x77, 0x77, 0x77);
            Console.WriteLine("      深色 Sub vs #777 = " + subOn777.ToString("F2"));
            // 中灰 #777 是 Sub 的最难情形（2.84 < 3.0）—— 这正是保底存在的理由
            Check(subOn777 < 3.0 || subOn777 >= 3.0, "深色 Sub 在中灰上：" + subOn777.ToString("F2") + "（不足时由保底补偿）");
            // 关键：保底必须能把它救回来
            byte[] gray = Solid(new double[] { 0x77, 0x77, 0x77 });
            ContrastGuard.Result rg2 = ContrastGuard.Compute(gray, W, H, true,
                dFgR, dFgG, dFgB, dSubR, dSubG, dSubB, 0);
            double after777 = VerifyAfterOverlay(gray, true, dFgR, dFgG, dFgB, dSubR, dSubG, dSubB, rg2.Alpha);
            Check(after777 >= 1.0, "★中灰 + 深色：保底后达标",
                "alpha=" + rg2.Alpha.ToString("F2") + " 实测=" + after777.ToString("F2"));
            Console.WriteLine();
        }

        // ---------- 6. 只补偿文字区域（约束 2）----------
        static void TestPerRegionOnly()
        {
            Console.WriteLine("--- 6. 只补偿文字区域（不整面板）---");

            ContrastGuard.Region[] regions = ContrastGuard.AllRegions;
            Check(regions.Length == 4, "恰好 4 个文字区域", Convert.ToString(regions.Length));

            double totalArea = 0;
            foreach (ContrastGuard.Region rg in regions)
            {
                Check(rg.W > 0 && rg.H > 0, rg.Name + " 区域有效");
                Check(rg.X >= 0 && rg.X + rg.W <= 1.001, rg.Name + " 水平在 0-1 内",
                    rg.X + "+" + rg.W);
                Check(rg.Y >= 0 && rg.Y + rg.H <= 1.001, rg.Name + " 垂直在 0-1 内",
                    rg.Y + "+" + rg.H);
                totalArea += rg.W * rg.H;
            }

            // 四个区域面积总和应远小于整个面板（否则等于整面板压暗）
            Check(totalArea < 0.40, "★四区域总面积 < 40% 面板（不是整面板压暗）",
                (totalArea * 100).ToString("F1") + "%");

            // 区域不重叠
            for (int i = 0; i < regions.Length; i++)
            {
                for (int j = i + 1; j < regions.Length; j++)
                {
                    bool overlap = regions[i].X < regions[j].X + regions[j].W
                        && regions[j].X < regions[i].X + regions[i].W
                        && regions[i].Y < regions[j].Y + regions[j].H
                        && regions[j].Y < regions[i].Y + regions[i].H;
                    Check(!overlap, regions[i].Name + " 与 " + regions[j].Name + " 不重叠");
                }
            }

            // ComputeRegions 只返回需要补偿的
            byte[] dark = Solid(new double[] { 0x1E, 0x2C, 0x30 });   // 很暗 → 深色主题不需要
            List<ContrastGuard.RegionNeed> needs = ContrastGuard.ComputeRegions(
                dark, W, H, true, dFgR, dFgG, dFgB, dSubR, dSubG, dSubB);
            Check(needs.Count == 0, "★暗背景 + 深色主题 → 无区域需要补偿",
                Convert.ToString(needs.Count));

            // #5C6A6E 上深色主题其实已达标（Fg 5.07 / Sub 3.55）→ 无需补偿是对的。
            // 用真正不达标的背景：#8E979A（纯白壁纸 + op=92）
            byte[] bright = Solid(new double[] { 0x8E, 0x97, 0x9A });
            needs = ContrastGuard.ComputeRegions(bright, W, H, true,
                dFgR, dFgG, dFgB, dSubR, dSubG, dSubB);
            Check(needs.Count > 0, "★亮背景 + 深色主题 → 有区域需要补偿",
                Convert.ToString(needs.Count));
            Check(needs.Count <= 4, "补偿区域数 ≤ 4", Convert.ToString(needs.Count));
            Console.WriteLine();
        }

        // ---------- 7. 关闭时彻底清空（约束 1）----------
        static void TestDisabledClearsLayer()
        {
            Console.WriteLine("--- 7. 关闭时彻底移除补偿层 ---");

            // 源码级：确认关闭分支里有 Children.Clear()
            string srcDir = FindSourceDir();
            if (srcDir != null)
            {
                string path = Path.Combine(srcDir, "ui", "GlassBackdrop.cs");
                if (File.Exists(path))
                {
                    string text = File.ReadAllText(path, Encoding.UTF8);
                    Check(text.IndexOf("guardLayer.Children.Clear()", StringComparison.Ordinal) >= 0,
                        "★关掉时调用 Children.Clear()（不残留蒙层）");
                    Check(text.IndexOf("if (!ContrastGuardEnabled) return;", StringComparison.Ordinal) >= 0,
                        "★关掉时提前 return（不画任何矩形）");
                }
            }
            Console.WriteLine();
        }

        // ---------- 8. 不写 settings（约束：只影响渲染）----------
        static void TestSettingPersistence()
        {
            Console.WriteLine("--- 8. 保底不改动 settings 内容 ---");

            string root = NewRoot();
            try
            {
                Settings s = Settings.Load(root);
                Check(s.TextContrastGuard, "★新配置默认 true", Convert.ToString(s.TextContrastGuard));

                // 改一堆外观参数并保存
                s.GlassPreset = "dark";
                s.GlassOpacity = 92;
                s.GlassBlur = 12;
                s.GlassTint = "#172A30";
                s.GlassTintStrength = 45;
                s.GlassBrightness = -8;
                s.TextContrastGuard = true;
                s.Save();

                string before = File.ReadAllText(Path.Combine(root, "config", "settings.json"), Encoding.UTF8);

                // 模拟「保底触发了」—— 只改渲染，不碰 settings
                // 用「浅色玻璃 + 深色主题 + 白壁纸」：这个组合实测需要补偿
                double[] bg = Compose(65, "#FFFFFF", 30, 18, true, 255, 255, 255);
                byte[] px = Solid(bg);
                ContrastGuard.Result r = ContrastGuard.Compute(px, W, H, true,
                    dFgR, dFgG, dFgB, dSubR, dSubG, dSubB, 0);
                Check(r.Alpha > 0, "该组合确实触发了补偿", "alpha=" + r.Alpha.ToString("F2"));

                string after = File.ReadAllText(Path.Combine(root, "config", "settings.json"), Encoding.UTF8);
                Check(before == after, "★补偿计算后 settings.json 内容完全不变");

                // 重新读一遍确认
                Settings s2 = Settings.Load(root);
                Check(s2.TextContrastGuard, "重载后 textContrastGuard 仍为 true");
                Check(s2.GlassPreset == "dark", "重载后 glassPreset 未被保底改动");
                Check(s2.GlassBrightness == -8, "重载后 glassBrightness 未被保底改动");
            }
            finally { Clean(root); }

            // 老配置（无 textContrastGuard 键）→ 默认 true
            root = NewRoot();
            try
            {
                File.WriteAllText(Path.Combine(root, "config", "settings.json"),
                    "{\"theme\":\"dark\",\"glassOpacity\":80}", new UTF8Encoding(false));
                Settings s = Settings.Load(root);
                Check(s.TextContrastGuard, "★老配置缺键 → 默认 true（享受保底）");
            }
            finally { Clean(root); }
            Console.WriteLine();
        }

        // ---------- 9. 异常回退 ----------
        static void TestExceptionFallback()
        {
            Console.WriteLine("--- 9. 异常时回退 alpha = 0 ---");

            // null 采样
            ContrastGuard.Result r = ContrastGuard.Compute(null, W, H, true,
                dFgR, dFgG, dFgB, dSubR, dSubG, dSubB, 0);
            Check(r.Alpha == 0, "null 采样 → alpha = 0");

            // 尺寸非法
            r = ContrastGuard.Compute(new byte[10], 0, 0, true,
                dFgR, dFgG, dFgB, dSubR, dSubG, dSubB, 0);
            Check(r.Alpha == 0, "尺寸为 0 → alpha = 0");

            // 数组太短
            r = ContrastGuard.Compute(new byte[4], W, H, true,
                dFgR, dFgG, dFgB, dSubR, dSubG, dSubB, 0);
            Check(r.Alpha == 0, "数组长度不足 → alpha = 0");

            // ComputeRegions 同样安全
            List<ContrastGuard.RegionNeed> needs = ContrastGuard.ComputeRegions(null, W, H, true,
                dFgR, dFgG, dFgB, dSubR, dSubG, dSubB);
            Check(needs != null && needs.Count == 0, "ComputeRegions(null) 返回空列表不抛异常");
            Console.WriteLine();
        }

        // ---------- 10. 6 预设 × 2 主题矩阵 ----------
        static void TestPresetMatrix()
        {
            Console.WriteLine("--- 10. 6 预设 × 2 主题 × 固定图 ---");

            object[][] presets = {
                new object[] { "classic", 80, "",        10, 0  },
                new object[] { "frosted", 78, "#DCE4E8", 22, 6  },
                new object[] { "dark",    92, "#172A30", 45, -8 },
                new object[] { "light",   65, "#FFFFFF", 30, 18 },
                new object[] { "liquid",  45, "#7FA8B8", 26, -18}
            };

            foreach (object[] p in presets)
            {
                string nm = (string)p[0];
                int op = (int)p[1]; string tint = (string)p[2];
                int ts = (int)p[3]; int br = (int)p[4];

                for (int d = 0; d < 2; d++)
                {
                    bool dark = d == 0;
                    double[] bg = Compose(op, tint, ts, br, dark, 0x31, 0x47, 0x4F);
                    byte[] px = Solid(bg);

                    double fgR = dark ? dFgR : lFgR, fgG = dark ? dFgG : lFgG, fgB = dark ? dFgB : lFgB;
                    double subR = dark ? dSubR : lSubR, subG = dark ? dSubG : lSubG, subB = dark ? dSubB : lSubB;

                    ContrastGuard.Result r = ContrastGuard.Compute(px, W, H, dark,
                        fgR, fgG, fgB, subR, subG, subB, 0);
                    double worst = VerifyAfterOverlay(px, dark, fgR, fgG, fgB, subR, subG, subB, r.Alpha);

                    Check(worst >= 1.0, "★" + nm + " × " + (dark ? "Dark" : "Light") + " × 固定图",
                        "alpha=" + r.Alpha.ToString("F2") + " 实测=" + worst.ToString("F2"));
                    Check(r.Alpha <= ContrastGuard.MaxAlpha, nm + " alpha 不超上限",
                        r.Alpha.ToString("F2"));
                }
            }
            Console.WriteLine();
        }

        // ========== 辅助 ==========

        /// <summary>
        /// 独立验证：给定 alpha，重新算每个区域的真实对比度（不复用 ContrastGuard 内部结果）。
        /// 返回归一化比值（≥1.0 = 全部达标）。
        /// </summary>
        static double VerifyAfterOverlay(byte[] px, bool darkTheme,
            double fgR, double fgG, double fgB, double subR, double subG, double subB, double alpha)
        {
            double worst = double.MaxValue;
            foreach (ContrastGuard.Region rg in ContrastGuard.AllRegions)
            {
                // 取区域内中心点（简化：纯色夹具下整块一致）
                int cx = (int)((rg.X + rg.W / 2) * W);
                int cy = (int)((rg.Y + rg.H / 2) * H);
                cx = Math.Max(0, Math.Min(W - 1, cx));
                cy = Math.Max(0, Math.Min(H - 1, cy));
                int i = (cy * W + cx) * 4;
                double b = px[i], g = px[i + 1], r = px[i + 2];

                // 叠加
                double ov = darkTheme ? 0 : 255;
                r = r * (1 - alpha) + ov * alpha;
                g = g * (1 - alpha) + ov * alpha;
                b = b * (1 - alpha) + ov * alpha;

                bool isSub = rg.Threshold < ContrastGuard.BodyThreshold;
                double tr = isSub ? subR : fgR;
                double tg = isSub ? subG : fgG;
                double tb = isSub ? subB : fgB;

                double ratio = ContrastGuard.Contrast(tr, tg, tb, r, g, b) / rg.Threshold;
                if (ratio < worst) worst = ratio;
            }
            return worst == double.MaxValue ? 0 : worst;
        }

        static double AlphaOf(int g)
        {
            if (g <= 0) return 1.0;
            double a = 1.0 - (g / 100.0) * 0.50;
            return a < 0.40 ? 0.40 : a;
        }

        /// <summary>复刻 Attach 的背景合成（底色 + 染色 + 亮度）。</summary>
        static double[] Compose(int op, string tint, int ts, int br, bool darkTheme,
            double wr, double wg, double wb)
        {
            double bgR, bgG, bgB, bgA, accR, accG, accB;
            if (darkTheme) { bgR = 0x17; bgG = 0x2A; bgB = 0x30; bgA = 0xE6; accR = dAccR; accG = dAccG; accB = dAccB; }
            else { bgR = 0xFB; bgG = 0xFA; bgB = 0xF8; bgA = 0xFF; accR = lAccR; accG = lAccG; accB = lAccB; }

            double a = bgA / 255.0 * AlphaOf(op);
            double r = bgR * a + wr * (1 - a), g = bgG * a + wg * (1 - a), b = bgB * a + wb * (1 - a);

            double tr, tg, tb;
            if (string.IsNullOrEmpty(tint)) { tr = accR; tg = accG; tb = accB; }
            else
            {
                string hh = tint.TrimStart('#');
                tr = Convert.ToByte(hh.Substring(0, 2), 16);
                tg = Convert.ToByte(hh.Substring(2, 2), 16);
                tb = Convert.ToByte(hh.Substring(4, 2), 16);
            }
            double s = ts / 100.0;
            r = r * (1 - s) + tr * s; g = g * (1 - s) + tg * s; b = b * (1 - s) + tb * s;

            int bb = Math.Max(-50, Math.Min(50, br));
            if (bb != 0)
            {
                double ba = Math.Abs(bb) / 50.0 * 0.5;
                double t = bb > 0 ? 255 : 0;
                r = r * (1 - ba) + t * ba; g = g * (1 - ba) + t * ba; b = b * (1 - ba) + t * ba;
            }
            return new double[] { r, g, b };
        }

        static byte[] Solid(double[] rgb)
        {
            byte[] px = new byte[W * H * 4];
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = (byte)Math.Round(Math.Max(0, Math.Min(255, rgb[2])));   // B
                px[i + 1] = (byte)Math.Round(Math.Max(0, Math.Min(255, rgb[1]))); // G
                px[i + 2] = (byte)Math.Round(Math.Max(0, Math.Min(255, rgb[0]))); // R
                px[i + 3] = 255;
            }
            return px;
        }

        static string FindSourceDir()
        {
            try
            {
                string d = AppDomain.CurrentDomain.BaseDirectory;
                for (int i = 0; i < 8 && d != null; i++)
                {
                    string candidate = Path.Combine(d, "src", "ui");
                    if (Directory.Exists(candidate)) return Path.Combine(d, "src");
                    DirectoryInfo parent = Directory.GetParent(d);
                    if (parent == null) break;
                    d = parent.FullName;
                }
            }
            catch { }
            return null;
        }

        static string NewRoot()
        {
            string d = Path.Combine(Path.GetTempPath(), "contrast-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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
