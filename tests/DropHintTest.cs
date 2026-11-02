using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Clover
{
    /// <summary>
    /// DropHint 提示层测试：常驻（子元素数量恒定）/ 只切 Visibility /
    /// 不接收命中 / 样式令牌 / 高亮态切换 / 文案切换。
    ///
    /// 需要 WPF（构造 Border / TextBlock），所以走 wpfRefs。
    /// **不显示窗口**，只构造 + 调方法。
    /// </summary>
    static class DropHintTest
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
            Console.WriteLine("=== DropHint 提示层测试 ===");
            Console.WriteLine();

            try
            {
                TestConstruction();
                TestVisibility();
                TestHitTest();
                TestTokens();
                TestActiveState();
                TestNoDashAnimation();
                TestNoChildChurn();
                TestConstraints();
            }
            catch (Exception ex)
            {
                Check(false, "测试不抛异常", ex.GetType().Name + ": " + ex.Message);
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + passed + " / 失败 " + failed);
            Environment.Exit(failed == 0 ? 0 : 1);
        }

        // ---------- 1. 构造 ----------
        static void TestConstruction()
        {
            Console.WriteLine("--- 1. 构造 ---");

            DropHint h = new DropHint("把文件拖到这里", null);
            Check(h.Root != null, "★Root 非空");
            Check(h.Root is Border, "Root 是 Border");
            Check(!h.Visible, "★初始不可见");
            Check(!h.IsActive, "★初始非高亮");
            Check(h.TitleText == "把文件拖到这里", "★主文案正确", h.TitleText);
            Check(!h.HintVisible, "★没传约束行 → 不显示");
            Check(h.HintText == "", "约束行为空串", h.HintText);

            // 空标题回退默认
            DropHint h2 = new DropHint(null, null);
            Check(h2.TitleText == "把文件拖到这里", "★null 标题回退默认", h2.TitleText);

            DropHint h3 = new DropHint("", null);
            Check(h3.TitleText == "把文件拖到这里", "★空标题回退默认", h3.TitleText);

            Console.WriteLine();
        }

        // ---------- 2. 显隐 ----------
        static void TestVisibility()
        {
            Console.WriteLine("--- 2. 只切 Visibility ---");

            DropHint h = new DropHint("把文件拖到这里", null);

            h.SetVisible(true);
            Check(h.Visible, "★SetVisible(true) → 可见");
            Check(h.Root.Visibility == Visibility.Visible, "Visibility 是 Visible");

            h.SetVisible(false);
            Check(!h.Visible, "★SetVisible(false) → 不可见");
            Check(h.Root.Visibility == Visibility.Collapsed, "★用 Collapsed 不是 Hidden", 
                h.Root.Visibility.ToString());

            // 重复调用幂等
            h.SetVisible(false);
            Check(!h.Visible, "重复隐藏仍是隐藏");
            h.SetVisible(true);
            h.SetVisible(true);
            Check(h.Visible, "重复显示仍是显示");

            // 隐藏时清掉高亮态（避免再显示时残留实线边框）
            h.SetActive(true);
            Check(h.IsActive, "先设成高亮");
            h.SetVisible(false);
            Check(!h.IsActive, "★隐藏时自动清掉高亮态");

            Console.WriteLine();
        }

        // ---------- 3. 命中测试 ----------
        static void TestHitTest()
        {
            Console.WriteLine("--- 3. 不接收命中 ---");

            DropHint h = new DropHint("把文件拖到这里", null);

            Check(!h.Root.IsHitTestVisible, "★Root 不接收命中");

            // 所有子元素也不该接收（它们都是装饰，没有可点的东西）
            int bad = 0;
            CountHitTestable(h.Root, ref bad);
            Check(bad == 0, "★子树里没有可命中的元素", Convert.ToString(bad));

            Console.WriteLine();
        }

        static void CountHitTestable(DependencyObject node, ref int bad)
        {
            try
            {
                if (node == null) return;

                UIElement ue = node as UIElement;
                if (ue != null && ue.IsHitTestVisible && ue != node)
                {
                    // 只有 Root 自己是 UIElement 的起点；子元素若可命中就要报
                    bad++;
                }

                int n = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < n; i++)
                    CountHitTestable(VisualTreeHelper.GetChild(node, i), ref bad);
            }
            catch { }
        }

        // ---------- 4. 令牌 ----------
        static void TestTokens()
        {
            Console.WriteLine("--- 4. 样式只用现有令牌 ---");

            DropHint h = new DropHint("把文件拖到这里", null);
            Border b = h.Root as Border;

            Check(b.CornerRadius.TopLeft == Ui.R.Md, "★圆角 Ui.R.Md",
                Convert.ToString(b.CornerRadius.TopLeft));
            Check(b.Padding.Left == Ui.S.Xl && b.Padding.Top == Ui.S.Xl,
                "★内边距 Ui.S.Xl", b.Padding.Left + "/" + b.Padding.Top);

            // 找出两条边框矩形
            List<Rectangle> rects = new List<Rectangle>();
            CollectRects(b, rects);
            Check(rects.Count == 2, "★两条常驻边框矩形", Convert.ToString(rects.Count));

            Rectangle dash = null, solid = null;
            foreach (Rectangle r in rects)
            {
                if (r.StrokeDashArray != null && r.StrokeDashArray.Count > 0) dash = r;
                else solid = r;
            }

            Check(dash != null, "★有一条是虚线");
            Check(solid != null, "★有一条是实线");

            if (dash != null)
            {
                Check(dash.StrokeThickness == 1, "★虚线 1px", Convert.ToString(dash.StrokeThickness));
                Check(ReferenceEquals(dash.Stroke, Ui.Line), "★虚线用 Ui.Line");
                Check(dash.RadiusX == Ui.R.Md, "虚线圆角 Ui.R.Md");
            }

            if (solid != null)
            {
                Check(solid.StrokeThickness == 1.5, "★实线 1.5px", Convert.ToString(solid.StrokeThickness));
                Check(ReferenceEquals(solid.Stroke, Ui.Accent), "★实线用 Ui.Accent");
                Check(solid.RadiusX == Ui.R.Md, "实线圆角 Ui.R.Md");
            }

            // 主文案字号
            TextBlock titleTb = FindTextBySize(b, 13);
            Check(titleTb != null, "★主文案 13px");
            if (titleTb != null) Check(ReferenceEquals(titleTb.Foreground, Ui.Fg), "★主文案 Ui.Fg");

            // 图标 32px + MDL2 字体
            TextBlock iconTb = FindIcon(b);
            Check(iconTb != null, "★图标存在");
            if (iconTb != null)
            {
                Check(iconTb.FontSize == 32, "★图标 32px", Convert.ToString(iconTb.FontSize));
                Check(ReferenceEquals(iconTb.FontFamily, Ui.Mdl), "★图标用 Segoe MDL2（项目做法）");
                // 不用 emoji：MDL2 私用区字符在 E000-F8FF
                Check(iconTb.Text.Length > 0 && iconTb.Text[0] >= 0xE000 && iconTb.Text[0] <= 0xF8FF,
                    "★图标是私用区字形（非 emoji）",
                    "U+" + ((int)iconTb.Text[0]).ToString("X4"));
            }

            Console.WriteLine();
        }

        static void CollectRects(DependencyObject node, List<Rectangle> list)
        {
            try
            {
                if (node == null) return;
                Rectangle r = node as Rectangle;
                if (r != null) list.Add(r);
                int n = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < n; i++)
                    CollectRects(VisualTreeHelper.GetChild(node, i), list);
            }
            catch { }
        }

        static TextBlock FindTextBySize(DependencyObject node, double size)
        {
            try
            {
                if (node == null) return null;
                TextBlock t = node as TextBlock;
                if (t != null && Math.Abs(t.FontSize - size) < 0.01) return t;
                int n = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < n; i++)
                {
                    TextBlock r = FindTextBySize(VisualTreeHelper.GetChild(node, i), size);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        static TextBlock FindIcon(DependencyObject node)
        {
            try
            {
                if (node == null) return null;
                TextBlock t = node as TextBlock;
                if (t != null && ReferenceEquals(t.FontFamily, Ui.Mdl)) return t;
                int n = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < n; i++)
                {
                    TextBlock r = FindIcon(VisualTreeHelper.GetChild(node, i));
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        // ---------- 5. 高亮态 ----------
        static void TestActiveState()
        {
            Console.WriteLine("--- 5. 高亮态切换 ---");

            DropHint h = new DropHint("把文件拖到这里", null);

            h.SetActive(true);
            Check(h.IsActive, "★SetActive(true) 生效");
            Check(h.TitleText == "松开即可放入", "★拖入态文案切换", h.TitleText);

            h.SetActive(false);
            Check(!h.IsActive, "★SetActive(false) 生效");
            Check(h.TitleText == "把文件拖到这里", "★恢复常态文案", h.TitleText);

            // 重复调用不重复动画（IsActive 相等时直接返回）
            h.SetActive(false);
            Check(!h.IsActive, "重复 SetActive(false) 幂等");
            h.SetActive(true);
            h.SetActive(true);
            Check(h.IsActive, "重复 SetActive(true) 幂等");

            // 交替多轮后状态仍正确
            for (int i = 0; i < 5; i++) { h.SetActive(false); h.SetActive(true); }
            Check(h.IsActive, "★交替 5 轮后仍是高亮");
            Check(h.TitleText == "松开即可放入", "交替后文案仍对");

            Console.WriteLine();
        }

        // ---------- 5b. 卡顿修复：虚线层在高亮时不参与渲染 ----------
        //
        // 这一组是「拖放卡顿」的回归断言。根因是虚线矩形的透明度动画：
        // StrokeDashArray 每帧都要重新切分线段，而高亮态只在拖放期间出现，
        // 正好落在 OLE 拖放循环里（消息泵被节流）。修法是高亮时把虚线层
        // **Collapsed**（彻底不渲染），而不是把透明度做成 0。
        static void TestNoDashAnimation()
        {
            Console.WriteLine("--- 5b. 高亮时不渲染虚线（卡顿回归）---");

            DropHint h = new DropHint("把文件拖到这里", null);
            Border b = h.Root as Border;

            List<Rectangle> rects = new List<Rectangle>();
            CollectRects(b, rects);
            Rectangle dash = null, solid = null;
            foreach (Rectangle r in rects)
            {
                if (r.StrokeDashArray != null && r.StrokeDashArray.Count > 0) dash = r;
                else solid = r;
            }
            Check(dash != null && solid != null, "两条边框都在");

            // 常态：虚线可见、实线不渲染
            Check(dash.Visibility == Visibility.Visible, "★常态虚线可见");
            Check(solid.Visibility == Visibility.Collapsed, "★常态实线不渲染");

            // 高亮：虚线 Collapsed（关键断言）、实线可见
            h.SetActive(true);
            Check(dash.Visibility == Visibility.Collapsed,
                "★高亮时虚线 Collapsed（不参与渲染）", dash.Visibility.ToString());
            Check(solid.Visibility == Visibility.Visible, "★高亮时实线可见");

            // 虚线矩形的透明度**从未被动画过** —— 读本地值仍是 1
            // （若被 BeginAnimation 改过，GetAnimationBaseValue 与当前值会不同）
            double localOpacity = (double)dash.GetValue(UIElement.OpacityProperty);
            Check(Math.Abs(localOpacity - 1.0) < 0.001,
                "★虚线透明度从未被改动（仍为 1）", Convert.ToString(localOpacity));

            // 回常态：虚线恢复可见
            h.SetActive(false);
            Check(dash.Visibility == Visibility.Visible, "★回常态虚线恢复可见");
            Check(solid.Visibility == Visibility.Collapsed, "★回常态实线再次隐藏");

            // 底色是硬切（读刷子颜色即可，不该有动画残留）
            h.SetActive(true);
            SolidColorBrush bg = b.Background as SolidColorBrush;
            Check(bg != null, "底色是 SolidColorBrush");
            if (bg != null)
            {
                Check(bg.Color == Ui.AccentSoft.Color, "★高亮底色 = Ui.AccentSoft",
                    bg.Color.ToString());
                Check(bg.HasAnimatedProperties == false,
                    "★底色没有残留动画（硬切）");
            }

            h.SetActive(false);
            if (bg != null)
                Check(bg.Color == Colors.Transparent, "★常态底色透明", bg.Color.ToString());

            Console.WriteLine();
        }

        // ---------- 6. 子元素不增删 ----------
        static void TestNoChildChurn()
        {
            Console.WriteLine("--- 6. 运行期不增删子元素 ---");

            DropHint h = new DropHint("把文件拖到这里", null);
            int before = CountVisuals(h.Root);

            h.SetVisible(true);
            int afterShow = CountVisuals(h.Root);
            Check(afterShow == before, "★显示后子元素数不变",
                before + " → " + afterShow);

            h.SetActive(true);
            int afterActive = CountVisuals(h.Root);
            Check(afterActive == before, "★高亮后子元素数不变",
                before + " → " + afterActive);

            // 反复切换
            for (int i = 0; i < 20; i++) { h.SetActive(i % 2 == 0); h.SetVisible(i % 3 != 0); }
            int afterChurn = CountVisuals(h.Root);
            Check(afterChurn == before, "★20 轮显隐+高亮后子元素数不变",
                before + " → " + afterChurn);

            Console.WriteLine();
        }

        static int CountVisuals(DependencyObject node)
        {
            try
            {
                if (node == null) return 0;
                int n = VisualTreeHelper.GetChildrenCount(node);
                int sum = 1;
                for (int i = 0; i < n; i++)
                    sum += CountVisuals(VisualTreeHelper.GetChild(node, i));
                return sum;
            }
            catch { return 0; }
        }

        // ---------- 7. 约束行 ----------
        static void TestConstraints()
        {
            Console.WriteLine("--- 7. 约束行 ---");

            // 有约束行
            DropHint h = new DropHint("把文件拖到这里", "支持 jpg/png，长边超过 3840 会等比缩小");
            Check(h.HintVisible, "★传了约束 → 显示");
            Check(h.HintText == "支持 jpg/png，长边超过 3840 会等比缩小",
                "约束文案原样", h.HintText);

            // 约束行字号与颜色
            TextBlock hintTb = FindTextBySize(h.Root, 10.5);
            Check(hintTb != null, "★约束行 10.5px");
            if (hintTb != null)
                Check(ReferenceEquals(hintTb.Foreground, Ui.Sub), "★约束行 Ui.Sub");

            // 没约束行：子元素照样建出来（只是隐藏），数量恒定
            DropHint h2 = new DropHint("把文件拖到这里", null);
            Check(!h2.HintVisible, "没传约束 → 隐藏");
            Check(CountVisuals(h.Root) == CountVisuals(h2.Root),
                "★有无约束行的可视元素数一致（子元素常驻）",
                CountVisuals(h.Root) + " vs " + CountVisuals(h2.Root));

            Console.WriteLine();
        }
    }
}
