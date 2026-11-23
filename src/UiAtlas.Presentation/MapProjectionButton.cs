using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UiAtlas.Presentation;

public static class MapProjectionButton
{
    public static Button Create(string mode, bool selected)
    {
        var button = new Button
        {
            Content = mode, Tag = mode, ToolTip = mode, Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(3, 0, 0, 0), Template = RoundedTemplate(8)
        };
        AutomationProperties.SetName(button, mode);
        SetSelected(button, selected);
        return button;
    }

    public static void SetSelected(Button button, bool selected) => Appearance.Bind(button,
        ("Background", selected ? "AccentSelectionBrush" : "PanelBrush"),
        ("Foreground", selected ? "AccentBrush" : "InkBrush"),
        ("BorderBrush", selected ? "AccentBrush" : "LineBrush"));

    public static ControlTemplate RoundedTemplate(double radius)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Control.Background))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Control.BorderBrush))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding(nameof(Control.BorderThickness))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding(nameof(Control.Padding))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding(nameof(ContentControl.Content))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        presenter.SetBinding(ContentPresenter.HorizontalAlignmentProperty, new System.Windows.Data.Binding(nameof(Control.HorizontalContentAlignment))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        presenter.SetBinding(ContentPresenter.VerticalAlignmentProperty, new System.Windows.Data.Binding(nameof(Control.VerticalContentAlignment))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.86));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.72));
        template.Triggers.Add(pressed);
        return template;
    }
}
