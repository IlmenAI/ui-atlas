using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;

namespace UiAtlas.Presentation;

/// <summary>Scoped companion theme shared by MapLock replay and Atlas mapping tools.</summary>
public static class AtlasFluent
{
    public const string Blue = "#1683FF", Ink = "#17243A", Muted = "#697A92", Amber = "#AE6900";
    public static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    public static void Apply(Window window)
    {
        Appearance.Apply(window);
        window.FontSize = 13;
        window.UseLayoutRounding = true; window.SnapsToDevicePixels = true;
    }

    public static TextBlock Text(string value, double size = 13, string color = Ink) => Appearance.Bind(new TextBlock
    {
        Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 4), VerticalAlignment = VerticalAlignment.Center
    }, ("Foreground", ColorResource(color)), ("FontFamily", size >= 18 ? "HeadingFontFamily" : "BodyFontFamily"));

    public static string ColorResource(string color) => color.ToUpperInvariant() switch
    {
        "#1683FF" or "#0870D9" => "AccentBrush",
        "#697A92" or "#64748B" => "MutedInkBrush",
        "#AE6900" => "WarningBrush",
        "#B42318" or "#DC2626" or "#B91C1C" => "ErrorBrush",
        "#15803D" or "#047857" or "#15803A" => "SuccessBrush",
        _ => "InkBrush"
    };

    public static Button Button(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label, MinHeight = 38, Padding = new Thickness(14, 8, 14, 8), Cursor = Cursors.Hand };
        button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "PrimaryActionButtonStyle" : "SecondaryActionButtonStyle");
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action(); return button;
    }

    public static Border Card(UIElement child, string background = "#FFFFFF") => Appearance.Bind(new Border
    {
        Child = child, BorderThickness = new Thickness(1), Padding = new Thickness(14), Margin = new Thickness(0, 6, 0, 6)
    }, ("Background", background == "#FFFFFF" ? "PanelBrush" : "SoftBrush"), ("BorderBrush", "LineBrush"), ("CornerRadius", "PanelCornerRadius"));

    public static Border Badge(string label, bool attention = false) => Appearance.Bind(new Border
    {
        Child = Appearance.Bind(Text(label, 12), ("Foreground", attention ? "WarningBrush" : "InfoBrush")), CornerRadius = new CornerRadius(16),
        BorderThickness = new Thickness(1), Padding = new Thickness(12, 1, 12, 1),
        HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 8)
    }, ("Background", attention ? "WarningSurfaceBrush" : "InfoSurfaceBrush"), ("BorderBrush", attention ? "WarningBrush" : "InfoBrush"));

    public static Border Shell(UIElement child) => Appearance.Bind(new Border
    {
        Child = child, BorderThickness = new Thickness(1),
        Margin = new Thickness(8)
    }, ("Background", "CanvasBrush"), ("BorderBrush", "LineBrush"), ("CornerRadius", "PanelCornerRadius"), ("Effect", "WindowShadow"));

    public static FrameworkElement Header(string title, Action? close = null, MouseButtonEventHandler? drag = null)
    {
        var row = global::UiAtlas.Presentation.Appearance.Bind(new DockPanel { Margin = new Thickness(0, 0, 0, 10) }, ("Background", "TransparentBrush"));
        if (close is not null)
        {
            var button = Button("×", close); button.MinHeight = 30; button.Width = 30; button.Height = 30;
            button.Template = (ControlTemplate)XamlReader.Parse("""
              <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
                  <Border x:Name="Ring" CornerRadius="50" Background="{DynamicResource PanelBrush}" BorderBrush="{DynamicResource LineBrush}" BorderThickness="1">
                    <Path x:Name="CloseIcon" Data="M 1,1 L 11,11 M 11,1 L 1,11" Width="12" Height="12" Stretch="None"
                        Stroke="{TemplateBinding Foreground}" StrokeThickness="1.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                        HorizontalAlignment="Center" VerticalAlignment="Center" IsHitTestVisible="False"/>
                  </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Ring" Property="BorderBrush" Value="{DynamicResource WindowCloseHoverBrush}"/><Setter TargetName="CloseIcon" Property="Stroke" Value="{DynamicResource WindowCloseInkBrush}"/><Setter TargetName="Ring" Property="Background" Value="{DynamicResource WindowCloseHoverBrush}"/></Trigger>
                  <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Ring" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/><Setter TargetName="Ring" Property="BorderThickness" Value="2"/></Trigger>
                  <Trigger Property="IsPressed" Value="True"><Setter TargetName="Ring" Property="Background" Value="{DynamicResource WindowClosePressedBrush}"/><Setter TargetName="Ring" Property="BorderBrush" Value="{DynamicResource WindowClosePressedBrush}"/><Setter TargetName="CloseIcon" Property="Stroke" Value="{DynamicResource WindowCloseInkBrush}"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
              """);
            button.Padding = new Thickness(0); button.FontSize = 17; button.SetResourceReference(Control.ForegroundProperty, "MutedInkBrush"); button.ToolTip = "Close " + title;
            AutomationProperties.SetName(button, "Close " + title); DockPanel.SetDock(button, Dock.Right); row.Children.Add(button);
        }
        var brand = Text("▰", 19, Blue); brand.Margin = new Thickness(0, 0, 9, 0);
        DockPanel.SetDock(brand, Dock.Left); row.Children.Add(brand);
        row.Children.Add(Text(title, 13, Muted));
        if (drag is not null) row.MouseLeftButtonDown += drag;
        return row;
    }

    public static Border Identity(string title, string subtitle, double headingSize = 19)
    {
        var content = new StackPanel(); var heading = Text(title, headingSize, headingSize <= 14 ? Muted : Ink);
        heading.FontWeight = headingSize <= 14 ? FontWeights.Normal : FontWeights.SemiBold;
        content.Children.Add(heading); content.Children.Add(Text(subtitle, 12, Muted));
        // A restrained Atlas ribbon. It is decorative and never enters evidence or input routing.
        var grid = new Grid { ClipToBounds = true }; grid.Children.Add(content);
        var ribbon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,45 C90,5 160,90 360,12 L360,25 C160,105 90,20 0,58 Z"),

            Stretch = Stretch.Fill, Height = 52, Width = 230, HorizontalAlignment = HorizontalAlignment.Right,
            IsHitTestVisible = false, Opacity = .5 };
        ribbon.SetResourceReference(Shape.FillProperty, "AccentSelectionBrush");
        Panel.SetZIndex(ribbon, -1); grid.Children.Add(ribbon);
        return new Border { Child = grid, Padding = new Thickness(0, 3, 0, 8) };
    }

}
