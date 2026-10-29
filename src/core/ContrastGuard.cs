using System;
using System.Collections.Generic;

namespace Clover
{
    /// <summary>
    /// 文字对比度保底：算出「需要叠多厚的压暗/提亮层」才能让文字达标。
    ///
    /// **只做计算，不做渲染。** 渲染接线在下一步（GlassBackdrop 的 guardLayer）。
    ///
    /// ── 为什么需要它 ──
    ///
    /// 毛玻璃把壁纸透上来，背景亮度就跟着壁纸跑：
    ///   · 深色主题 + 纯白壁纸 → 背景被提亮到 #5C6A6E，浅字压不住（实测 Sub 2.69）
    ///   · 浅色主题 + 纯黑壁纸 → 背景被压暗，深字压不住（实测 Sub 1.47）
    /// 任何**固定**参数都救不了，只能运行时按实际背景算补偿。
    ///
    /// ── 算法 ──
    ///
    /// 1. 在降采样图上，按四类文字所在区域分别取亮度分位：
    ///    · 深色主题（浅字）：取 **p90**（最亮处最危险）
    ///    · 浅色主题（深字）：取 **p10**（最暗处最危险）
    /// 2. 对每个区域算当前对比度，取「正文 4.5 / 次要 3.0」中要求更严的那个
    /// 3. 二分搜索**最小** alpha（深色主题叠黑、浅色主题叠白），使最差区域刚好达标
    /// 4. alpha 上限 0.85 —— 到顶仍不达标就用上限并记录，不把面板做成实心
    /// 5. 迟滞：对比度在「阈值 ± 0.2」内时保持上次 alpha，避免来回跳
    ///
    /// ── 性能 ──
    ///
    /// 全程在降采样图（默认 64×40）上算，不碰全尺寸位图。
    /// 只在背景变化（换壁纸 / 切模式 / 切预设 / 松手）时调，拖动滑块用降采样预览值。
    /// </summary>
    static class ContrastGuard
    {
        // ---- 阈值（WCAG）----
        public const double BodyThreshold = 4.5;     // 正文：磁贴名 / 搜索框 / 分类标签
        public const double SubThreshold = 3.0;      // 次要：底部提示

        // ---- 迟滞 ----
        public const double Hysteresis = 0.2;

        // ---- alpha 上限（不实心化面板）----
        public const double MaxAlpha = 0.85;

        /// <summary>降采样图尺寸。64×40 足够定位区域，又够快（约 2500 像素）。</summary>
        public const int SampleW = 64;
        public const int SampleH = 40;

        /// <summary>
        /// 文字区域（按面板归一化坐标 0-1）。
        ///
        /// 数值来源（PanelWindow.Ui.cs 的实际布局）：
        ///   · 面板总高 = 64(header) + 内容(*) + 32(footer)
        ///   · header 里搜索框占中间一大段
        ///   · 分类栏在 header 下方，紧贴内容区顶部
        ///   · 磁贴名称在磁贴底部（网格中部偏下）
        ///   · footer 32px 在最底部
        ///
        /// 用归一化坐标而不是像素：面板会缩放（DPI 变化），归一化后不用重算。
        /// </summary>
        public class Region
        {
            public string Name;
            public double X, Y, W, H;      // 归一化 0-1
            public double Threshold;        // 该区域要求的阈值
        }

        static readonly Region[] Regions = new Region[]
        {
            // 搜索框：header 中间，占宽度约 40%-70%
            new Region { Name = "搜索框",   X = 0.42, Y = 0.02, W = 0.28, H = 0.07, Threshold = BodyThreshold },
            // 分类标签栏：header 下沿到内容区顶部
            new Region { Name = "分类标签", X = 0.06, Y = 0.10, W = 0.88, H = 0.06, Threshold = BodyThreshold },
            // 磁贴名称：网格中部（磁贴行在垂直方向的中段）
            new Region { Name = "磁贴名称", X = 0.08, Y = 0.40, W = 0.84, H = 0.28, Threshold = BodyThreshold },
            // 底部提示：footer 32px
            new Region { Name = "底部提示", X = 0.06, Y = 0.94, W = 0.88, H = 0.05, Threshold = SubThreshold },
        };

        /// <summary>全部区域（诊断用）。</summary>
        public static Region[] AllRegions { get { return Regions; } }

        // ========== 色彩计算 ==========

        /// <summary>sRGB 相对亮度（WCAG 2.x）。</summary>
        public static double RelativeLuminance(double r, double g, double b)
        {
            double R = Channel(r), G = Channel(g), B = Channel(b);
            return 0.2126 * R + 0.7152 * G + 0.0722 * B;
        }

        static double Channel(double v)
        {
            double c = v / 255.0;
            if (c < 0) c = 0;
            if (c > 1) c = 1;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        /// <summary>WCAG 对比度（1.0 ~ 21.0）。</summary>
        public static double Contrast(double r1, double g1, double b1, double r2, double g2, double b2)
        {
            double l1 = RelativeLuminance(r1, g1, b1);
            double l2 = RelativeLuminance(r2, g2, b2);
            double hi = Math.Max(l1, l2), lo = Math.Min(l1, l2);
            return (hi + 0.05) / (lo + 0.05);
        }

        // ========== 主入口 ==========

        /// <summary>一次计算的完整结果（诊断与测试用）。</summary>
        public class Result
        {
            /// <summary>需要的叠加层 alpha（0 = 不需要）。</summary>
            public double Alpha;

            /// <summary>叠加色：深色主题叠黑、浅色主题叠白。</summary>
            public bool OverlayIsBlack;

            /// <summary>补偿前最差区域的对比度。</summary>
            public double WorstBefore;

            /// <summary>补偿后最差区域的对比度（= 刚好达标，或到上限仍不达标）。</summary>
            public double WorstAfter;

            /// <summary>最差区域名（诊断用）。</summary>
            public string WorstRegion;

            /// <summary>是否撞到 alpha 上限（撞到说明补偿不够）。</summary>
            public bool HitCeiling;

            /// <summary>是否因迟滞而复用了上次 alpha。</summary>
            public bool UsedHysteresis;

            public override string ToString()
            {
                return string.Format("alpha={0:F3} {1} before={2:F2} after={3:F2} worst={4}{5}{6}",
                    Alpha,
                    OverlayIsBlack ? "黑" : "白",
                    WorstBefore, WorstAfter, WorstRegion,
                    HitCeiling ? " [上限]" : "",
                    UsedHysteresis ? " [迟滞]" : "");
            }
        }

        /// <summary>
        /// 计算需要的补偿 alpha。
        /// </summary>
        /// <param name="samples">降采样图的 BGRA 像素（长度 = w*h*4）</param>
        /// <param name="w">采样图宽</param>
        /// <param name="h">采样图高</param>
        /// <param name="darkTheme">深色主题 = true（浅字、叠黑）；浅色主题 = false（深字、叠白）</param>
        /// <param name="fgR">正文色 R</param>
        /// <param name="fgG">正文色 G</param>
        /// <param name="fgB">正文色 B</param>
        /// <param name="subR">次要文字色 R</param>
        /// <param name="subG">次要文字色 G</param>
        /// <param name="subB">次要文字色 B</param>
        /// <param name="previousAlpha">上一次的 alpha（用于迟滞；传 0 表示无历史）</param>
        public static Result Compute(byte[] samples, int w, int h, bool darkTheme,
            double fgR, double fgG, double fgB,
            double subR, double subG, double subB,
            double previousAlpha)
        {
            Result res = new Result();
            res.OverlayIsBlack = darkTheme;

            try
            {
                if (samples == null || w <= 0 || h <= 0 || samples.Length < w * h * 4)
                {
                    res.Alpha = 0;
                    return res;
                }

                // ---- 1. 逐区域算「最危险」的背景色 ----
                //
                // 深色主题（浅字）→ 最亮处最危险 → p90
                // 浅色主题（深字）→ 最暗处最危险 → p10
                double worstRatio = double.MaxValue;
                string worstName = null;

                foreach (Region rg in Regions)
                {
                    double[] bg = RegionPercentileColor(samples, w, h, rg, darkTheme ? 0.90 : 0.10);
                    if (bg == null) continue;

                    // 该区域的文字色：正文用 fg，次要（底部提示）用 sub
                    bool isSub = rg.Threshold < BodyThreshold;
                    double tr = isSub ? subR : fgR;
                    double tg = isSub ? subG : fgG;
                    double tb = isSub ? subB : fgB;

                    double ratio = Contrast(tr, tg, tb, bg[0], bg[1], bg[2]);

                    // 归一化到「相对阈值」：ratio/threshold 越小越危险
                    // 这样正文 4.5 和次要 3.0 可以一起比较
                    double normalized = ratio / rg.Threshold;
                    if (normalized < worstRatio)
                    {
                        worstRatio = normalized;
                        worstName = rg.Name;
                        res.WorstBefore = ratio;
                    }
                }

                res.WorstRegion = worstName ?? "?";

                if (worstName == null)
                {
                    res.Alpha = 0;
                    res.WorstAfter = res.WorstBefore;
                    return res;
                }

                // ---- 2. 迟滞：已经在阈值 ±0.2 内就保持上次 ----
                //
                // 为什么需要：背景在临界点附近抖动时（拖动窗口、壁纸微变），
                // alpha 会 0 → 0.1 → 0 来回跳，看起来像闪烁。
                double worstThreshold = ThresholdOfRegion(worstName);
                if (previousAlpha > 0 && Math.Abs(res.WorstBefore - worstThreshold) <= Hysteresis)
                {
                    res.Alpha = previousAlpha;
                    res.UsedHysteresis = true;
                    res.WorstAfter = res.WorstBefore;
                    return res;
                }

                // ---- 3. 已经达标 → alpha = 0 ----
                if (res.WorstBefore >= worstThreshold)
                {
                    res.Alpha = 0;
                    res.WorstAfter = res.WorstBefore;
                    return res;
                }

                // ---- 4. 二分搜索最小 alpha ----
                //
                // 叠加层是纯黑（深色主题）或纯白（浅色主题），alpha 越大越接近实心。
                // 目标：让最差区域的对比度刚好达到阈值。
                res.Alpha = SolveAlpha(samples, w, h, darkTheme,
                    fgR, fgG, fgB, subR, subG, subB, worstThreshold, out res.HitCeiling);
                // WorstAfter 转回「真实对比度」便于阅读：归一化比值 × 最差区域阈值
                res.WorstAfter = WorstAfterAlpha(samples, w, h, darkTheme,
                    fgR, fgG, fgB, subR, subG, subB, res.Alpha) * worstThreshold;
            }
            catch (Exception ex)
            {
                // 异常时回退 alpha=0（不做补偿），并记录
                Log.Warn("对比度保底计算失败，已回退不补偿: " + ex.Message);
                res.Alpha = 0;
            }
            return res;
        }

        /// <summary>
        /// 计算**哪些区域**需要补偿，以及各自需要的 alpha。
        ///
        /// **为什么按区域算而不是整面板一刀切**：
        /// 整面板压暗/提亮会毁掉毛玻璃和液态玻璃的视觉（用户明确要求不能这样）。
        /// 只有文字所在的那几条带需要补偿，其余部分保持原样。
        ///
        /// 返回每个需要补偿的区域（Alpha > 0），以及叠加色。
        /// </summary>
        public static List<RegionNeed> ComputeRegions(byte[] samples, int w, int h, bool darkTheme,
            double fgR, double fgG, double fgB,
            double subR, double subG, double subB)
        {
            List<RegionNeed> needs = new List<RegionNeed>();
            try
            {
                if (samples == null || w <= 0 || h <= 0 || samples.Length < w * h * 4) return needs;

                foreach (Region rg in Regions)
                {
                    double[] bg = RegionPercentileColor(samples, w, h, rg, darkTheme ? 0.90 : 0.10);
                    if (bg == null) continue;

                    bool isSub = rg.Threshold < BodyThreshold;
                    double tr = isSub ? subR : fgR;
                    double tg = isSub ? subG : fgG;
                    double tb = isSub ? subB : fgB;

                    double ratio = Contrast(tr, tg, tb, bg[0], bg[1], bg[2]);
                    if (ratio >= rg.Threshold) continue;   // 这个区域已达标，不需要补偿

                    // 该区域自己的最小 alpha（二分）
                    double lo = 0, hi = MaxAlpha;
                    bool hitCeil = false;
                    if (SingleRegionWorst(samples, w, h, rg, darkTheme, tr, tg, tb, MaxAlpha) < 1.0)
                    {
                        hitCeil = true;
                        needs.Add(new RegionNeed { Region = rg, Alpha = MaxAlpha, IsBlack = darkTheme, HitCeiling = true });
                        Log.Warn("区域 " + rg.Name + " 到上限仍不达标，用上限 " + MaxAlpha.ToString("F2"));
                        continue;
                    }
                    for (int k = 0; k < 12; k++)
                    {
                        double mid = (lo + hi) / 2;
                        if (SingleRegionWorst(samples, w, h, rg, darkTheme, tr, tg, tb, mid) >= 1.0) hi = mid;
                        else lo = mid;
                    }
                    needs.Add(new RegionNeed { Region = rg, Alpha = hi, IsBlack = darkTheme, HitCeiling = hitCeil });
                }
            }
            catch (Exception ex)
            {
                Log.Warn("区域补偿计算失败，已回退不补偿: " + ex.Message);
                needs.Clear();
            }
            return needs;
        }

        /// <summary>单个区域在给定 alpha 下的归一化对比度（≥1.0 达标）。</summary>
        static double SingleRegionWorst(byte[] samples, int w, int h, Region rg, bool darkTheme,
            double tr, double tg, double tb, double alpha)
        {
            double[] bg = RegionPercentileColor(samples, w, h, rg, darkTheme ? 0.90 : 0.10);
            if (bg == null) return 1.0;
            double ov = darkTheme ? 0 : 255;
            double r = bg[0] * (1 - alpha) + ov * alpha;
            double g = bg[1] * (1 - alpha) + ov * alpha;
            double b = bg[2] * (1 - alpha) + ov * alpha;
            return Contrast(tr, tg, tb, r, g, b) / rg.Threshold;
        }

        /// <summary>一个区域的补偿需求。</summary>
        public class RegionNeed
        {
            public Region Region;
            public double Alpha;
            public bool IsBlack;      // true = 叠黑（深色主题）；false = 叠白
            public bool HitCeiling;
        }

        /// <summary>区域对应的阈值。</summary>
        static double ThresholdOfRegion(string name)
        {
            foreach (Region rg in Regions)
                if (rg.Name == name) return rg.Threshold;
            return BodyThreshold;
        }

        /// <summary>
        /// 二分搜索最小 alpha，使最差区域刚好达标。
        ///
        /// 单调性：alpha 越大，背景越接近纯黑/纯白，对比度单调上升
        /// （深色主题叠黑→背景变暗→浅字对比度升；浅色主题叠白→背景变亮→深字对比度升）。
        /// 所以可以二分。
        /// </summary>
        static double SolveAlpha(byte[] samples, int w, int h, bool darkTheme,
            double fgR, double fgG, double fgB,
            double subR, double subG, double subB,
            double threshold, out bool hitCeiling)
        {
            hitCeiling = false;

            // 先试上限：如果上限都不够，直接用上限
            double atMax = WorstAfterAlpha(samples, w, h, darkTheme, fgR, fgG, fgB, subR, subG, subB, MaxAlpha);
            if (atMax < 1.0)   // 归一化比值 < 1.0 = 不达标
            {
                hitCeiling = true;
                Log.Warn(string.Format(
                    "对比度保底到上限 {0:F2} 仍不达标（最差相对阈值 {1:F2} < 1.0，目标阈值 {2:F2}）",
                    MaxAlpha, atMax, threshold));
                return MaxAlpha;
            }

            // 二分：找最小的 alpha 使 worst >= threshold
            double lo = 0, hi = MaxAlpha;
            for (int i = 0; i < 12; i++)   // 12 次 → 精度 MaxAlpha/4096 ≈ 0.0002
            {
                double mid = (lo + hi) / 2;
                double v = WorstAfterAlpha(samples, w, h, darkTheme, fgR, fgG, fgB, subR, subG, subB, mid);
                if (v >= 1.0) hi = mid;   // 归一化比值 ≥ 1.0 = 达标
                else lo = mid;
            }
            return hi;
        }

        /// <summary>给定 alpha 叠加后，最差区域的对比度。</summary>
        static double WorstAfterAlpha(byte[] samples, int w, int h, bool darkTheme,
            double fgR, double fgG, double fgB,
            double subR, double subG, double subB,
            double alpha)
        {
            double worst = double.MaxValue;

            foreach (Region rg in Regions)
            {
                double[] bg = RegionPercentileColor(samples, w, h, rg, darkTheme ? 0.90 : 0.10);
                if (bg == null) continue;

                // 叠加：纯黑或纯白
                double ov = darkTheme ? 0 : 255;
                double r = bg[0] * (1 - alpha) + ov * alpha;
                double g = bg[1] * (1 - alpha) + ov * alpha;
                double b = bg[2] * (1 - alpha) + ov * alpha;

                bool isSub = rg.Threshold < BodyThreshold;
                double tr = isSub ? subR : fgR;
                double tg = isSub ? subG : fgG;
                double tb = isSub ? subB : fgB;

                double ratio = Contrast(tr, tg, tb, r, g, b);
                // 归一化后取最差（不同区域阈值不同，必须归一化才能一起比）
                double normalized = ratio / rg.Threshold;
                if (normalized < worst) worst = normalized;
            }

            // **返回归一化比值**（1.0 = 刚好全部达标）。
            // 调用方必须拿它跟 **1.0** 比，不能跟 4.5 / 3.0 比 ——
            // 之前这里跟原始阈值比，导致「补偿后反而更差」的假象。
            return worst;
        }

        /// <summary>
        /// 取区域内的亮度分位色。
        ///
        /// 为什么不取平均：平均会把「亮的一半 + 暗的一半」抹平成中灰，
        /// 而实际文字压在**最亮**（或最暗）那块上时才是最危险的。
        /// 分位数能抓住这个极端。
        /// </summary>
        static double[] RegionPercentileColor(byte[] samples, int w, int h, Region rg, double percentile)
        {
            try
            {
                int x0 = (int)(rg.X * w), x1 = (int)((rg.X + rg.W) * w);
                int y0 = (int)(rg.Y * h), y1 = (int)((rg.Y + rg.H) * h);
                if (x0 < 0) x0 = 0;
                if (y0 < 0) y0 = 0;
                if (x1 > w) x1 = w;
                if (y1 > h) y1 = h;
                if (x1 <= x0 || y1 <= y0) return null;

                List<double> lums = new List<double>();
                List<int> idxs = new List<int>();
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        int i = (y * w + x) * 4;
                        // BGRA
                        double b = samples[i], g = samples[i + 1], r = samples[i + 2];
                        lums.Add(RelativeLuminance(r, g, b));
                        idxs.Add(i);
                    }
                }
                if (lums.Count == 0) return null;

                // 按亮度排序，取分位
                double[] arr = lums.ToArray();
                int[] order = new int[arr.Length];
                for (int i = 0; i < order.Length; i++) order[i] = i;
                Array.Sort(arr, order);

                int k = (int)Math.Round(percentile * (arr.Length - 1));
                if (k < 0) k = 0;
                if (k >= arr.Length) k = arr.Length - 1;

                int pixel = idxs[order[k]];
                return new double[] { samples[pixel + 2], samples[pixel + 1], samples[pixel] };
            }
            catch { return null; }
        }
    }
}
