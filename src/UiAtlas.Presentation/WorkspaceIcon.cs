using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace UiAtlas.Presentation;

public sealed class WorkspaceIcon : Viewbox
{
    private readonly System.Windows.Shapes.Path _path = new()
    {
        StrokeThickness = 1.7, StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
    };

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string),
        typeof(WorkspaceIcon), new PropertyMetadata(null, (d, e) =>
            ((WorkspaceIcon)d)._path.SetResourceReference(System.Windows.Shapes.Path.DataProperty, "WorkspaceIcon." + e.NewValue)));

    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public WorkspaceIcon()
    {
        Width = Height = 16;
        IsHitTestVisible = false;
        var surface = new Canvas { Width = 24, Height = 24 };
        _path.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        surface.Children.Add(_path);
        Child = surface;
    }
}
