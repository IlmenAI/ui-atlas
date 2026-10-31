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
        window.FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        window.FontSize = 13; window.Foreground = Brush(Ink);
        window.UseLayoutRounding = true; window.SnapsToDevicePixels = true;
        window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Resources));
    }

    public static TextBlock Text(string value, double size = 13, string color = Ink) => new()
    {
        Text = value, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 4), VerticalAlignment = VerticalAlignment.Center
    };

    public static Button Button(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label, MinHeight = 38, Padding = new Thickness(14, 8, 14, 8),
            Background = Brush(primary ? Blue : "#FFFFFF"), Foreground = primary ? Brushes.White : Brush(Ink),
            BorderBrush = Brush(primary ? Blue : "#D6DFEA"), Cursor = Cursors.Hand };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action(); return button;
    }

    public static Border Card(UIElement child, string background = "#FFFFFF") => new()
    {
        Child = child, Background = Brush(background), BorderBrush = Brush("#E5EBF3"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Margin = new Thickness(0, 6, 0, 6)
    };

    public static Border Badge(string label, bool attention = false) => new()
    {
        Child = Text(label, 12, attention ? Amber : "#0870D9"), CornerRadius = new CornerRadius(16),
        Background = Brush(attention ? "#FFF3DF" : "#E9F4FF"), BorderBrush = Brush(attention ? "#FFE2B1" : "#CDE5FF"),
        BorderThickness = new Thickness(1), Padding = new Thickness(12, 1, 12, 1),
        HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 8)
    };

    public static Border Shell(UIElement child) => new()
    {
        Child = child, Background = Brush("#F7FAFD"), BorderBrush = Brush("#CEDBEA"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(22), Margin = new Thickness(8),
        Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = .14, Color = Colors.SlateGray }
    };

    public static FrameworkElement Header(string title, Action? close = null, MouseButtonEventHandler? drag = null)
    {
        var row = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 10) };
        if (close is not null)
        {
            var button = Button("×", close); button.MinHeight = 30; button.Width = 30; button.Height = 30;
            button.Template = (ControlTemplate)XamlReader.Parse("""
              <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
                  <Border x:Name="Ring" CornerRadius="50" Background="#FFFFFF" BorderBrush="#DAE4F0" BorderThickness="1">
                    <Path x:Name="CloseIcon" Data="M 1,1 L 11,11 M 11,1 L 1,11" Width="12" Height="12" Stretch="None"
                        Stroke="{TemplateBinding Foreground}" StrokeThickness="1.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                        HorizontalAlignment="Center" VerticalAlignment="Center" IsHitTestVisible="False"/>
                  </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Ring" Property="BorderBrush" Value="#1683FF"/><Setter Property="Foreground" Value="#1683FF"/><Setter TargetName="Ring" Property="Background" Value="#EFF7FF"/></Trigger>
                  <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Ring" Property="BorderBrush" Value="#1683FF"/><Setter TargetName="Ring" Property="BorderThickness" Value="2"/></Trigger>
                  <Trigger Property="IsPressed" Value="True"><Setter TargetName="Ring" Property="Background" Value="#D8EBFF"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
              """);
            button.Padding = new Thickness(0); button.FontSize = 17; button.Foreground = Brush(Muted); button.ToolTip = "Close " + title;
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
            Fill = new LinearGradientBrush(Color.FromArgb(12, 22, 131, 255), Color.FromArgb(35, 22, 131, 255), 0),
            Stretch = Stretch.Fill, Height = 52, Width = 230, HorizontalAlignment = HorizontalAlignment.Right,
            IsHitTestVisible = false, Opacity = .5 };
        Panel.SetZIndex(ribbon, -1); grid.Children.Add(ribbon);
        return new Border { Child = grid, Padding = new Thickness(0, 3, 0, 8) };
    }

    private const string Resources = """
    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
      <Style TargetType="Button">
        <Setter Property="Background" Value="White"/><Setter Property="Foreground" Value="#17243A"/>
        <Setter Property="BorderBrush" Value="#D6DFEA"/><Setter Property="BorderThickness" Value="1"/>
        <Setter Property="Padding" Value="12,7"/><Setter Property="MinHeight" Value="32"/>
        <Setter Property="HorizontalContentAlignment" Value="Center"/><Setter Property="VerticalContentAlignment" Value="Center"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
          <Border x:Name="Frame" CornerRadius="8" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="True"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Opacity" Value="0.86"/></Trigger>
            <Trigger Property="IsPressed" Value="True"><Setter TargetName="Frame" Property="Opacity" Value="0.68"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="#004A9D"/><Setter TargetName="Frame" Property="BorderThickness" Value="2"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Frame" Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style TargetType="TextBox">
        <Setter Property="Padding" Value="10,8"/><Setter Property="Background" Value="White"/><Setter Property="Foreground" Value="#17243A"/>
        <Setter Property="BorderBrush" Value="#CFD9E6"/><Setter Property="BorderThickness" Value="1"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
          <Border x:Name="Frame" CornerRadius="8" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}">
            <ScrollViewer x:Name="PART_ContentHost"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="#1683FF"/><Setter TargetName="Frame" Property="BorderThickness" Value="2"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Frame" Property="Opacity" Value="0.45"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style TargetType="CheckBox">
        <Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox">
          <Border x:Name="Focus" Padding="3" CornerRadius="6" BorderThickness="1" BorderBrush="Transparent" Background="Transparent">
            <DockPanel>
              <Border x:Name="Track" DockPanel.Dock="Right" Margin="12,0,0,0" Width="36" Height="21" CornerRadius="11" Background="#A2B1C4" VerticalAlignment="Center">
                <Ellipse x:Name="Thumb" Width="15" Height="15" Margin="3" Fill="White" HorizontalAlignment="Left"/>
              </Border>
              <ContentPresenter VerticalAlignment="Center" RecognizesAccessKey="True"/>
            </DockPanel>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True"><Setter TargetName="Track" Property="Background" Value="#1683FF"/><Setter TargetName="Thumb" Property="HorizontalAlignment" Value="Right"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Focus" Property="BorderBrush" Value="#1683FF"/></Trigger>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Focus" Property="Background" Value="#EDF5FF"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Focus" Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style TargetType="ComboBox">
        <Setter Property="Padding" Value="10,5"/><Setter Property="MinHeight" Value="30"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
          <Border x:Name="ComboFrame" BorderBrush="#CFD9E6" BorderThickness="1" CornerRadius="7"><Grid>
            <ToggleButton Style="{x:Null}" HorizontalAlignment="Stretch" VerticalAlignment="Stretch" Focusable="False" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
              <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border x:Name="Surface" CornerRadius="7" Background="#FFFFFF"><Path Data="M 0,0 L 4,4 L 8,0" Stroke="#697A92" StrokeThickness="1.5" Width="8" Height="4" HorizontalAlignment="Right" Margin="0,0,10,0" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="#F0F7FF"/></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter Margin="10,4,28,4" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}" ContentStringFormat="{TemplateBinding SelectionBoxItemStringFormat}" IsHitTestVisible="False" VerticalAlignment="Center"/>
            <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" IsOpen="{TemplateBinding IsDropDownOpen}" Focusable="False">
              <Border Background="#FCFDFF" CornerRadius="10" BorderBrush="#D6E2F1" BorderThickness="1" Padding="4" Margin="0,4,0,0" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"><ScrollViewer MaxHeight="240"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer></Border>
            </Popup>
          </Grid></Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="ComboFrame" Property="BorderBrush" Value="#77B5FF"/></Trigger>
            <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="ComboFrame" Property="BorderBrush" Value="#1683FF"/><Setter TargetName="ComboFrame" Property="BorderThickness" Value="2"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style TargetType="ComboBoxItem">
        <Setter Property="Padding" Value="12,7"/><Setter Property="Margin" Value="0,2"/><Setter Property="Foreground" Value="#17243A"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem">
          <Border x:Name="Item" CornerRadius="6" Background="Transparent" Padding="{TemplateBinding Padding}"><Grid><Border x:Name="SelectedMark" Width="3" CornerRadius="2" Background="#1683FF" HorizontalAlignment="Left" Margin="-8,0,0,0" Visibility="Collapsed"/><ContentPresenter/></Grid></Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Item" Property="Background" Value="#F0F6FD"/></Trigger>
            <Trigger Property="IsSelected" Value="True"><Setter TargetName="Item" Property="Background" Value="#E7F2FF"/><Setter TargetName="SelectedMark" Property="Visibility" Value="Visible"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Item" Property="Background" Value="#DDECFF"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Item" Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style x:Key="AtlasScrollThumb" TargetType="Thumb">
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Thumb"><Border x:Name="Handle" Background="#A8B8CC" CornerRadius="4" Margin="3"/><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Handle" Property="Background" Value="#709BCB"/></Trigger><Trigger Property="IsDragging" Value="True"><Setter TargetName="Handle" Property="Background" Value="#1683FF"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
      </Style>
      <Style TargetType="ScrollBar">
        <Setter Property="Width" Value="12"/><Setter Property="MinHeight" Value="20"/><Setter Property="Background" Value="Transparent"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
          <Grid Background="Transparent"><Track x:Name="PART_Track" IsDirectionReversed="True" Orientation="{TemplateBinding Orientation}" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}" ViewportSize="{TemplateBinding ViewportSize}">
            <Track.DecreaseRepeatButton><RepeatButton x:Name="Before" Command="ScrollBar.PageUpCommand" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
            <Track.Thumb><Thumb x:Name="Handle" MinHeight="20" MinWidth="8" Style="{StaticResource AtlasScrollThumb}"/></Track.Thumb>
            <Track.IncreaseRepeatButton><RepeatButton x:Name="After" Command="ScrollBar.PageDownCommand" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
          </Track></Grid>
          <ControlTemplate.Triggers><Trigger Property="Orientation" Value="Horizontal"><Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False"/><Setter TargetName="Handle" Property="MinHeight" Value="8"/><Setter TargetName="Handle" Property="MinWidth" Value="20"/><Setter TargetName="Before" Property="Command" Value="ScrollBar.PageLeftCommand"/><Setter TargetName="After" Property="Command" Value="ScrollBar.PageRightCommand"/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate></Setter.Value></Setter>
        <Style.Triggers><Trigger Property="Orientation" Value="Horizontal"><Setter Property="Width" Value="Auto"/><Setter Property="Height" Value="12"/><Setter Property="MinHeight" Value="0"/><Setter Property="MinWidth" Value="20"/></Trigger></Style.Triggers>
      </Style>
      <Style TargetType="DataGrid"><Setter Property="BorderBrush" Value="#DCE5F0"/><Setter Property="BorderThickness" Value="1"/><Setter Property="RowHeight" Value="32"/><Setter Property="ColumnHeaderHeight" Value="32"/><Setter Property="HorizontalGridLinesBrush" Value="#EDF1F7"/><Setter Property="AlternatingRowBackground" Value="#FAFCFF"/></Style>
      <Style TargetType="DataGridColumnHeader"><Setter Property="Background" Value="#F0F5FB"/><Setter Property="Foreground" Value="#536780"/><Setter Property="Padding" Value="9,5"/><Setter Property="BorderThickness" Value="0"/><Setter Property="FontWeight" Value="SemiBold"/></Style>
      <Style TargetType="DataGridCell"><Setter Property="Padding" Value="8,4"/><Setter Property="BorderThickness" Value="0"/><Style.Triggers><Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="#E7F2FF"/><Setter Property="Foreground" Value="#17243A"/></Trigger></Style.Triggers></Style>
      <Style TargetType="ProgressBar"><Setter Property="Foreground" Value="#1683FF"/><Setter Property="Background" Value="#DFE9F5"/><Setter Property="BorderThickness" Value="0"/></Style>
    </ResourceDictionary>
    """;
}
