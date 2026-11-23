using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace UiAtlas.Presentation;

public static class PaneVisibilityButton
{
    public static ToggleButton Create(bool leftPane, string toolTip)
    {
        var button = Appearance.Bind(new ToggleButton
        {
            Content = CreatePaneVisibilityIcon(leftPane),
            Width = 35,
            Height = 30,
            Margin = new Thickness(0, 0, 0, 3),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = toolTip,
            Focusable = false,
            Template = PaneToggleButtonTemplate()
        },("Foreground", "InkBrush"),("BorderBrush", "LineBrush"),("Background", "PanelBrush"));
        button.Checked += (_, _) =>
        {
            Appearance.Bind(button, ("Background", "AccentBrush"));
            Appearance.Bind(button, ("BorderBrush", "LineBrush"));
            Appearance.Bind(button, ("Foreground", "AccentTextBrush"));
        };
        button.Unchecked += (_, _) =>
        {
            Appearance.Bind(button, ("Background", "PanelBrush"));
            Appearance.Bind(button, ("BorderBrush", "LineBrush"));
            Appearance.Bind(button, ("Foreground", "InkBrush"));
        };
        System.Windows.Automation.AutomationProperties.SetName(button, toolTip);
        return button;
    }

    private static FrameworkElement CreatePaneVisibilityIcon(bool leftPane)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(leftPane ? "M3,3H17V17H3Z M8,3V17" : "M3,3H17V17H3Z M12,3V17"),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent
        };
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ToggleButton), 1)
        });
        return new Viewbox { Width = 17, Height = 17, Stretch = Stretch.Uniform, Child = path };
    }

    private static ControlTemplate PaneToggleButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.BorderBrush))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        border.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(Control.BorderThickness))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(ContentControl.Content))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = border };
        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new System.Windows.DynamicResourceExtension("AccentBrush")));
        checkedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, new System.Windows.DynamicResourceExtension("LineBrush")));
        checkedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, new System.Windows.DynamicResourceExtension("AccentTextBrush")));
        template.Triggers.Add(checkedTrigger);
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.84));
        template.Triggers.Add(hoverTrigger);
        return template;
    }
}
