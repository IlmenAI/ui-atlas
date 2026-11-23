using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UiAtlas.Presentation;

public static class SurfaceVariantSelector
{
    public static void Configure(ListBox selector)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel)); panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        selector.ItemsPanel = new ItemsPanelTemplate(panel);
        ScrollViewer.SetHorizontalScrollBarVisibility(selector, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(selector, ScrollBarVisibility.Disabled);
        selector.PreviewKeyDown += (_, e) =>
        {
            if (selector.Items.Count == 0 || e.Key is not (System.Windows.Input.Key.Left or System.Windows.Input.Key.Right)) return;
            selector.SelectedIndex = Math.Clamp(selector.SelectedIndex + (e.Key == System.Windows.Input.Key.Left ? -1 : 1), 0, Math.Max(0, selector.Items.Count - 1));
            selector.ScrollIntoView(selector.SelectedItem); e.Handled = true;
        };
        AutomationProperties.SetHelpText(selector, "Visible surface variants. Select a variant to inspect its recorded frames.");
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 5, 9, 5)));
        itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(1, 2, 1, 2)));
        itemStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12d));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, new System.Windows.DynamicResourceExtension("TransparentBrush")));
        itemStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new System.Windows.DynamicResourceExtension("TransparentBrush")));
        itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        itemStyle.Setters.Add(new Setter(Control.TemplateProperty, System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type ListBoxItem}">
                <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                    BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="7" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Stretch" VerticalAlignment="Center"/>
                </Border>
            </ControlTemplate>
            """)));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new System.Windows.DynamicResourceExtension("AccentSelectionBrush")));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, new System.Windows.DynamicResourceExtension("AccentBrush")));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, new System.Windows.DynamicResourceExtension("AccentBrush")));
        itemStyle.Triggers.Add(selected);
        var hovered = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hovered.Setters.Add(new Setter(Control.BorderBrushProperty, new System.Windows.DynamicResourceExtension("AccentBrush")));
        itemStyle.Triggers.Add(hovered);
        selector.ItemContainerStyle = itemStyle;
    }
}
