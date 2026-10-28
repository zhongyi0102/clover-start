using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace Clover
{
    /// <summary>
    /// 从图片里提取"主色调"（用于换壁纸后自动改强调色）。
    ///
    /// **为什么不用简单的平均色**：
    /// 一张风景壁纸的平均色往往是浑浊的灰褐（蓝天+绿地+暗部一平均就废了）。
    /// 用户感知的"主色"是**最有存在感的那种颜色**，不是算术平均。
    ///
    /// 做法（简化版的色彩量化）：
    ///   1. 缩到 64×64 采样（4096 个点，够统计又不慢）
    ///   2. 每像素转 HSV，**过滤掉低饱和度的灰/黑/白**（它们不构成"色调"）
    ///   3. 按色相（Hue）分 24 个桶（每桶 15°），统计每桶的加权像素数
    ///      权重 = 饱和度 × 明度 —— 又灰又暗的颜色不算"主色"
    ///   4. 取权重最高的桶，用桶内像素的平均 H/S/V 重建颜色
    ///   5. 把 S/V 钳制到适合当强调色的范围（太暗看不清、太亮刺眼）
    ///
    /// 全流程约 5-15 ms（64×64 = 4096 像素），可以在界面线程同步调用。
    /// </summary>
    static class DominantColor
    {
        /// <summary>
        /// 提取主色调。失败时返回 null（调用方保留原强调色）。
        /// </summary>
        /// <param name="imagePath">图片路径</param>
        /// <param name="forDarkTheme">
        /// true = 深色主题用（要更亮、更鲜艳，否则深底上显脏）
        /// false = 浅色主题用（要稍暗，否则白底上看不清）
        /// </param>
        public static Color? FromImage(string imagePath, bool forDarkTheme)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(imagePath)) return null;
                if (!System.IO.File.Exists(imagePath)) return null;

                using (Bitmap src = new Bitmap(imagePath))
                using (Bitmap small = new Bitmap(src, 64, 64))
                {
                    return Analyze(small, forDarkTheme);
                }
            }
            catch { return null; }
        }

        static Color? Analyze(Bitmap bmp, bool forDarkTheme)
        {
            const int Buckets = 24;              // 每桶 15°
            double[] weight = new double[Buckets];
            double[] sumH = new double[Buckets];
            double[] sumS = new double[Buckets];
            double[] sumV = new double[Buckets];
            int[] count = new int[Buckets];

            double totalWeight = 0;

            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    double h, s, v;
                    ToHsv(c, out h, out s, out v);

                    // 过滤低饱和（灰）和极暗/极亮（黑/白）——它们不构成"色调"
                    if (s < 0.15) continue;
                    if (v < 0.12) continue;
                    if (v > 0.96 && s < 0.25) continue;

                    int b = (int)(h / 360.0 * Buckets);
                    if (b < 0) b = 0;
                    if (b >= Buckets) b = Buckets - 1;

                    // 权重：饱和度 × 明度 —— 鲜艳且明亮的颜色更"有存在感"
                    double w = s * v;
                    weight[b] += w;
                    sumH[b] += h * w;
                    sumS[b] += s * w;
                    sumV[b] += v * w;
                    count[b]++;
                    totalWeight += w;
                }
            }

            // 没有足够的彩色像素（纯灰壁纸）：返回 null，保留原强调色
            if (totalWeight < 1.0) return null;

            int best = -1;
            double bestW = 0;
            for (int i = 0; i < Buckets; i++)
            {
                if (weight[i] > bestW) { bestW = weight[i]; best = i; }
            }
            if (best < 0 || weight[best] <= 0) return null;

            double hh = sumH[best] / weight[best];
            double ss = sumS[best] / weight[best];
            double vv = sumV[best] / weight[best];

            // ---- 钳制到"适合当强调色"的范围 ----
            //
            // 为什么必须钳制：壁纸主色可能非常暗（深蓝夜空）或非常淡（浅粉），
            // 直接拿来当强调色，在面板底色上要么看不清、要么刺眼。
            if (forDarkTheme)
            {
                // 深色主题：要亮、要饱和（深底上才跳得出来）
                if (ss < 0.45) ss = 0.45;
                if (ss > 0.90) ss = 0.90;
                if (vv < 0.70) vv = 0.70;
                if (vv > 0.95) vv = 0.95;
            }
            else
            {
                // 浅色主题：稍暗、中等饱和（白底上才够对比）
                if (ss < 0.45) ss = 0.45;
                if (ss > 0.85) ss = 0.85;
                if (vv < 0.42) vv = 0.42;
                if (vv > 0.62) vv = 0.62;
            }

            return FromHsv(hh, ss, vv);
        }

        // ---------- HSV ↔ RGB ----------

        static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;

            v = max;
            s = max <= 0 ? 0 : d / max;

            if (d <= 0) { h = 0; return; }

            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);

            if (h < 0) h += 360;
        }

        static Color FromHsv(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs(((h / 60) % 2) - 1));
            double m = v - c;

            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            int R = Clamp((r + m) * 255);
            int G = Clamp((g + m) * 255);
            int B = Clamp((b + m) * 255);
            return Color.FromArgb(255, R, G, B);
        }

        static int Clamp(double v)
        {
            int i = (int)Math.Round(v);
            if (i < 0) return 0;
            if (i > 255) return 255;
            return i;
        }

        /// <summary>
        /// 由主色派生"浅色强调底"（AccentSoft）和"强调文字色"（AccentText）。
        ///
        /// 规则：
        ///   · AccentSoft = 主色压到很低明度 + 低不透明度（深色主题）
        ///                  或很高明度（浅色主题）—— 用作选中背景
        ///   · AccentText = 主色提亮（深色主题）或压暗（浅色主题）—— 用作文字
        /// </summary>
        public static void DeriveAccents(Color accent, bool forDarkTheme,
                                          out Color soft, out Color text)
        {
            double h, s, v;
            ToHsv(accent, out h, out s, out v);

            if (forDarkTheme)
            {
                // 深色：soft 是"主色压暗 + 半透明"，text 是"主色提亮"
                soft = FromHsv(h, Math.Min(1.0, s * 1.1), Math.Max(0.10, v * 0.26));
                text = FromHsv(h, Math.Min(1.0, s * 0.85), Math.Min(1.0, v * 1.12));
            }
            else
            {
                // 浅色：soft 是"主色极淡"（近乎白），text 是"主色压暗"
                soft = FromHsv(h, Math.Max(0.08, s * 0.22), Math.Min(1.0, v * 1.45));
                text = FromHsv(h, Math.Min(1.0, s * 0.95), Math.Max(0.18, v * 0.55));
            }
        }

        /// <summary>把一个颜色转成 #RRGGBB 字符串（存 settings 用）。</summary>
        public static string ToHex(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }

        /// <summary>解析 #RRGGBB 或 #AARRGGBB。失败返回 null。</summary>
        public static Color? FromHex(string hex)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex)) return null;
                string s = hex.Trim();
                if (s.StartsWith("#")) s = s.Substring(1);

                if (s.Length == 6)
                {
                    int r = Convert.ToInt32(s.Substring(0, 2), 16);
                    int g = Convert.ToInt32(s.Substring(2, 2), 16);
                    int b = Convert.ToInt32(s.Substring(4, 2), 16);
                    return Color.FromArgb(255, r, g, b);
                }
                if (s.Length == 8)
                {
                    int a = Convert.ToInt32(s.Substring(0, 2), 16);
                    int r = Convert.ToInt32(s.Substring(2, 2), 16);
                    int g = Convert.ToInt32(s.Substring(4, 2), 16);
                    int b = Convert.ToInt32(s.Substring(6, 2), 16);
                    return Color.FromArgb(a, r, g, b);
                }
                return null;
            }
            catch { return null; }
        }
    }
}
