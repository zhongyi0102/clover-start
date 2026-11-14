using System;
using System.Collections.Generic;

namespace Clover
{
    /// <summary>
    /// Bounded, pure-logic wallpaper analysis. Population/chroma ranking and tonal
    /// roles follow Material's approach; extraction is a small RGB histogram, not
    /// the full HCT quantizer. Neutral fallback follows the Dusky/Matugen fix.
    /// Runtime values never write to Settings. BGRA input must exclude UI text.
    /// </summary>
    static class WallpaperAppearance
    {
        public const double TextThreshold = 4.5;
        const double SampleSafetyThreshold = 5.2; // grain/scaling/8-bit compositing margin
        public const int SampleWidth = 64, SampleHeight = 40;

        public sealed class Scheme
        {
            public bool Dark, Neutral;
            public uint Seed, Surface, Foreground, Secondary, Accent, AccentText, AccentSoft;
            public double SurfaceAlpha, Blur, Texture, MeanTone, ProtectionCost;
            public int Opacity;
            public string Description
            {
                get { return (Dark ? "深色" : "浅色") + " · " + (Neutral ? "中性配色" : "壁纸配色") + " · 文字保护已开启"; }
            }
        }

        sealed class Bucket { public int Count; public double R, G, B, Chroma; }

        public static Scheme Analyze(byte[] pixels, int width, int height, Scheme previous, bool dynamic)
        {
            if (pixels == null || width <= 0 || height <= 0 ||
                (long)width * height > 65536 || pixels.Length < (long)width * height * 4)
                return previous ?? Build(null, 0, 0, false, true, 0x808080, 0, 50, dynamic);

            Dictionary<int, Bucket> buckets = new Dictionary<int, Bucket>();
            double toneSum = 0, chromaSum = 0, texture = 0;
            int count = 0, edges = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    if (pixels[i + 3] < 240) continue;
                    double r = pixels[i + 2], g = pixels[i + 1], b = pixels[i];
                    double chroma = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                    double tone = Tone(r, g, b);
                    count++; toneSum += tone; chromaSum += chroma;
                    int key = ((int)r >> 4) << 8 | ((int)g >> 4) << 4 | (int)b >> 4;
                    Bucket bucket;
                    if (!buckets.TryGetValue(key, out bucket)) { bucket = new Bucket(); buckets.Add(key, bucket); }
                    bucket.Count++; bucket.R += r; bucket.G += g; bucket.B += b; bucket.Chroma += chroma;
                    if (x > 0 && pixels[i - 1] >= 240)
                    { texture += Math.Abs(tone - Tone(pixels[i - 2], pixels[i - 3], pixels[i - 4])); edges++; }
                }
            if (count == 0) return previous ?? Analyze(null, 0, 0, null, dynamic);
            double meanTone = toneSum / count;
            bool neutral = chromaSum / count < 12;
            uint seed = 0x808080;
            double best = -1;
            if (!neutral)
                foreach (Bucket bucket in buckets.Values)
                {
                    double population = (double)bucket.Count / count, chroma = bucket.Chroma / bucket.Count;
                    double r = bucket.R / bucket.Count, g = bucket.G / bucket.Count, b = bucket.B / bucket.Count;
                    double tone = Tone(r, g, b);
                    if (population < 0.01 || chroma < 18 || tone < 12 || tone > 92) continue;
                    double score = population * (0.6 + Math.Min(chroma, 100) / 100);
                    if (score > best) { best = score; seed = Rgb(r, g, b); }
                }
            if (best < 0) neutral = true;
            texture = edges == 0 ? 0 : Math.Min(1, texture / edges / 25);
            Scheme light = Build(pixels, width, height, false, neutral, seed, texture, meanTone, dynamic);
            Scheme dark = Build(pixels, width, height, true, neutral, seed, texture, meanTone, dynamic);
            Scheme next = dark.ProtectionCost < light.ProtectionCost ? dark : light;
            if (previous != null)
            {
                Scheme same = previous.Dark ? dark : light;
                // Dynamic media keeps one theme: per-frame black/white inversion is
                // distracting. Static changes need a meaningful advantage to switch.
                if (dynamic || same.ProtectionCost <= next.ProtectionCost + 0.10) next = same;
                if (next.Dark == previous.Dark && next.Neutral == previous.Neutral &&
                    ColorDistance(next.Seed, previous.Seed) < 28)
                {
                    next.Seed = previous.Seed;
                    FillAccent(next);
                }
                else if (dynamic && next.Dark == previous.Dark)
                {
                    // Ambient color changes slowly; text safety is independent of it.
                    next.Seed = Mix(previous.Seed, next.Seed, 0.18);
                    FillAccent(next);
                }
                if (dynamic) next.SurfaceAlpha = previous.SurfaceAlpha;
            }
            return next;
        }

        static Scheme Build(byte[] pixels, int width, int height, bool dark, bool neutral,
            uint seed, double texture, double meanTone, bool dynamic)
        {
            Scheme s = new Scheme();
            s.Dark = dark; s.Neutral = neutral; s.Seed = seed; s.Texture = texture; s.MeanTone = meanTone;
            s.Foreground = dark ? 0xF3F4F4u : 0x202020u;
            s.Secondary = dark ? 0xD8DCDDu : 0x454545u;
            s.Surface = AtTone(neutral ? 0x808080u : Mix(seed, 0x808080, 0.92), dark ? 10 : 97);
            s.Blur = Math.Round(texture * 2, 1);
            s.Opacity = 100;
            // Keep the image intact. Contrast protection belongs behind actual text,
            // not over icons and empty space. Missing sources still get a solid fallback.
            s.SurfaceAlpha = pixels == null ? 1 : 0.03 + texture * 0.05;
            if (pixels != null)
            {
                int count = 0;
                for (int i = 0; i < width * height * 4; i += 4)
                {
                    if (pixels[i + 3] < 240) continue;
                    uint background = Rgb(pixels[i + 2], pixels[i + 1], pixels[i]);
                    s.ProtectionCost += RequiredAlpha(background, s.Secondary, s.Surface); count++;
                }
                if (count > 0) s.ProtectionCost /= count;
            }
            FillAccent(s);
            return s;
        }

        /// <summary>Minimal local backing for one text rectangle, in sample coordinates.
        /// Dynamic text backings withstand unsampled opposite frames without dimming the image.</summary>
        public static double TextProtectionAlpha(byte[] pixels, int width, int height,
            int left, int top, int right, int bottom, uint foreground, uint surface,
            uint globalSurface, double globalAlpha, bool dynamic)
        {
            if (dynamic || pixels == null || width <= 0 || height <= 0 ||
                (long)width * height > 65536 || pixels.Length < (long)width * height * 4)
                return Math.Max(RequiredAlpha(0, foreground, surface), RequiredAlpha(0xFFFFFF, foreground, surface));
            double alpha = 0;
            left = Math.Max(0, left); top = Math.Max(0, top);
            right = Math.Min(width, right); bottom = Math.Min(height, bottom);
            for (int y = top; y < bottom; y++) for (int x = left; x < right; x++)
            {
                int i = (y * width + x) * 4;
                if (pixels[i + 3] < 240) continue;
                uint background = Rgb(pixels[i + 2], pixels[i + 1], pixels[i]);
                alpha = Math.Max(alpha, RequiredAlpha(Mix(background, globalSurface, globalAlpha), foreground, surface));
            }
            return alpha;
        }

        static void FillAccent(Scheme s)
        {
            // White labels already used across the project must remain readable.
            s.Accent = AtTone(s.Neutral ? 0x808080u : Mix(s.Seed, 0x808080, 0.25), 40);
            s.AccentSoft = Mix(s.Accent, s.Surface, s.Dark ? 0.82 : 0.90);
            s.AccentText = AtTone(s.Neutral ? 0x808080u : s.Seed, s.Dark ? 88 : 28);
        }

        public static double RequiredAlpha(uint background, uint foreground, uint surface)
        {
            if (Contrast(foreground, background) >= SampleSafetyThreshold) return 0;
            double low = 0, high = 1;
            for (int k = 0; k < 12; k++)
            {
                double mid = (low + high) / 2;
                if (Contrast(foreground, Mix(background, surface, mid)) >= SampleSafetyThreshold) high = mid;
                else low = mid;
            }
            return Math.Min(1, high + 1.0 / 255); // quantization safety margin
        }

        public static double Contrast(uint a, uint b)
        {
            double y1 = Luminance(a), y2 = Luminance(b);
            return (Math.Max(y1, y2) + 5) / (Math.Min(y1, y2) + 5);
        }
        static double Luminance(uint rgb)
        { return 0.2126 * MaterialColorMath.Linearized((rgb >> 16) & 255) +
            0.7152 * MaterialColorMath.Linearized((rgb >> 8) & 255) + 0.0722 * MaterialColorMath.Linearized(rgb & 255); }
        static double Tone(double r, double g, double b)
        { return MaterialColorMath.LstarFromY(0.2126 * MaterialColorMath.Linearized(r) +
            0.7152 * MaterialColorMath.Linearized(g) + 0.0722 * MaterialColorMath.Linearized(b)); }

        public static uint AtTone(uint rgb, double tone)
        {
            double target = MaterialColorMath.YFromLstar(tone), original = Luminance(rgb);
            double r = MaterialColorMath.Linearized((rgb >> 16) & 255);
            double g = MaterialColorMath.Linearized((rgb >> 8) & 255), b = MaterialColorMath.Linearized(rgb & 255);
            if (target <= original)
            { double f = original <= 0 ? 0 : target / original; r *= f; g *= f; b *= f; }
            else
            { double f = original >= 100 ? 0 : (target - original) / (100 - original); r += (100 - r) * f; g += (100 - g) * f; b += (100 - b) * f; }
            return Rgb(MaterialColorMath.Delinearized(r), MaterialColorMath.Delinearized(g), MaterialColorMath.Delinearized(b));
        }
        public static uint Mix(uint a, uint b, double amount)
        { double t = Math.Max(0, Math.Min(1, amount)); return Rgb(((a >> 16) & 255) * (1 - t) + ((b >> 16) & 255) * t,
            ((a >> 8) & 255) * (1 - t) + ((b >> 8) & 255) * t, (a & 255) * (1 - t) + (b & 255) * t); }
        static uint Rgb(double r, double g, double b)
        { return (uint)Math.Round(r) << 16 | (uint)Math.Round(g) << 8 | (uint)Math.Round(b); }
        static double ColorDistance(uint a, uint b)
        { return Math.Abs((int)((a >> 16) & 255) - (int)((b >> 16) & 255)) +
            Math.Abs((int)((a >> 8) & 255) - (int)((b >> 8) & 255)) + Math.Abs((int)(a & 255) - (int)(b & 255)); }
    }
}
