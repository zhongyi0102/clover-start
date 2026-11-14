using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Clover
{
    /// <summary>One keyboard-accessible button chrome for all existing WPF surfaces.</summary>
    static class UiButtons
    {
        static readonly Dictionary<double, ControlTemplate> templates = new Dictionary<double, ControlTemplate>();

        public static ControlTemplate Template(double radius)
        {
            ControlTemplate cached;
            if (templates.TryGetValue(radius, out cached)) return cached;
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory chrome = new FrameworkElementFactory(typeof(Border), "ButtonChrome");
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            Bind(chrome, Border.BackgroundProperty, "Background");
            Bind(chrome, Border.BorderBrushProperty, "BorderBrush");
            Bind(chrome, Border.BorderThicknessProperty, "BorderThickness");
            Bind(chrome, Border.PaddingProperty, "Padding");
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            Bind(content, ContentPresenter.HorizontalAlignmentProperty, "HorizontalContentAlignment");
            Bind(content, ContentPresenter.VerticalAlignmentProperty, "VerticalContentAlignment");
            chrome.AppendChild(content);
            FrameworkElementFactory layers = new FrameworkElementFactory(typeof(Grid));
            layers.AppendChild(chrome);
            // 焦点描边独立叠加，不改变普通边框、Padding 或内容可用空间。
            FrameworkElementFactory ring = new FrameworkElementFactory(typeof(Border), "FocusRing");
            ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            ring.SetBinding(Border.BorderBrushProperty, new Binding { Source = Ui.Accent });
            ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
            ring.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
            ring.SetValue(UIElement.IsHitTestVisibleProperty, false);
            layers.AppendChild(ring); template.VisualTree = layers;
            Trigger hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, new Binding { Source = Ui.Accent }, "ButtonChrome"));
            template.Triggers.Add(hover);
            Trigger focus = new Trigger { Property = Button.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, new Binding { Source = Ui.Accent }, "ButtonChrome"));
            focus.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "FocusRing"));
            template.Triggers.Add(focus);
            Trigger pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.OpacityProperty, 0.82, "ButtonChrome"));
            template.Triggers.Add(pressed);
            Trigger disabled = new Trigger { Property = Button.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Border.OpacityProperty, 0.45, "ButtonChrome"));
            template.Triggers.Add(disabled);
            templates[radius] = template;
            return template;
        }

        static void Bind(FrameworkElementFactory target, DependencyProperty property, string path)
        {
            target.SetBinding(property, new Binding(path)
            { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        }
    }
}
