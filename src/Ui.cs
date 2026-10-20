using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Clover
{
    static class Ui
    {
        public static readonly Color BgColor = Color.FromArgb(255, 251, 250, 248);
        public static readonly SolidColorBrush Bg = Freeze(new SolidColorBrush(BgColor));
        public static readonly SolidColorBrush White = Freeze(new SolidColorBrush(Colors.White));
        public static readonly SolidColorBrush Line = Freeze(new SolidColorBrush(Color.FromRgb(226, 223, 218)));
        public static readonly SolidColorBrush Fg = Freeze(new SolidColorBrush(Color.FromRgb(32, 32, 32)));
        public static readonly SolidColorBrush Sub = Freeze(new SolidColorBrush(Color.FromRgb(120, 118, 114)));
        public static readonly SolidColorBrush Hover = Freeze(new SolidColorBrush(Color.FromRgb(243, 241, 237)));
        public static readonly SolidColorBrush SearchBg = Freeze(new SolidColorBrush(Color.FromRgb(242, 240, 236)));
        public static readonly SolidColorBrush Accent = Freeze(new SolidColorBrush(Color.FromRgb(56, 140, 72)));
        public static readonly SolidColorBrush AccentSoft = Freeze(new SolidColorBrush(Color.FromRgb(232, 243, 233)));
        public static readonly SolidColorBrush AccentText = Freeze(new SolidColorBrush(Color.FromRgb(30, 105, 45)));
        public static readonly FontFamily YaHei = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
        public static readonly FontFamily Mdl = new FontFamily("Segoe MDL2 Assets");

        static SolidColorBrush Freeze(SolidColorBrush b)
        {
            b.Freeze();
            return b;
        }

        public static TextBlock T(string text, double size, Brush color)
        {
            return T(text, size, color, FontWeights.Normal);
        }

        public static TextBlock T(string text, double size, Brush color, FontWeight weight)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.FontFamily = YaHei;
            t.FontSize = size;
            t.Foreground = color;
            t.FontWeight = weight;
            t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }

        public static DropShadowEffect Shadow(double blur, double opacity, double depth)
        {
            DropShadowEffect e = new DropShadowEffect();
            e.BlurRadius = blur;
            e.ShadowDepth = depth;
            e.Opacity = opacity;
            e.Color = Color.FromRgb(0, 0, 0);
            return e;
        }

        public static void Log(string msg)
        {
            try
            {
                string p = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clover.log");
                System.IO.File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>只输出到 clover.log，同时把异常细节写进 error.log。</summary>
        public static void LogError(Exception ex, string where)
        {
            Log(where + ": " + (ex == null ? "(null)" : ex.Message));
            ErrorLog.Hook(ex, where);
        }

        /// <summary>读取图片；失败（文件被删、损坏、权限不足）时返回 null，不抛异常拖垮窗口构造。</summary>
        public static BitmapImage LoadImage(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
                BitmapImage b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.UriSource = new Uri(path, UriKind.Absolute);
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("读取图片失败: " + path + " -> " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 加载四叶草图标。单文件 exe 场景下没有外部 res\ 目录，
        /// 所以优先读 <paramref name="externalPath"/>，读不到就用**嵌入资源**兜底。
        /// 两条路都失败时返回 null（调用方显示首字占位，不崩）。
        /// </summary>
        public static BitmapImage LoadCloverIcon(string externalPath)
        {
            BitmapImage img = LoadImage(externalPath);
            if (img != null) return img;

            // 兜底：编译时嵌入的程序集资源（见 build.ps1 的 /resource 参数）
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (System.IO.Stream s = asm.GetManifestResourceStream("CloverStart.clover.png"))
                {
                    if (s == null)
                    {
                        // 资源名可能带目录前缀，模糊匹配一次
                        foreach (string name in asm.GetManifestResourceNames())
                        {
                            if (name.EndsWith("clover.png", StringComparison.OrdinalIgnoreCase))
                            {
                                using (System.IO.Stream s2 = asm.GetManifestResourceStream(name))
                                {
                                    if (s2 != null) return DecodeStream(s2);
                                }
                            }
                        }
                        return null;
                    }
                    return DecodeStream(s);
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Warn("读取嵌入图标失败: " + ex.Message);
                return null;
            }
        }

        static BitmapImage DecodeStream(System.IO.Stream stream)
        {
            BitmapImage b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.StreamSource = stream;
            b.EndInit();
            b.Freeze();
            return b;
        }
    }
}
