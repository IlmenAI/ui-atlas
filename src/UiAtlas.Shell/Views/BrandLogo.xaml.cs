using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UIMaps.Application.Views;

public partial class BrandLogo : UserControl
{
    public static readonly DependencyProperty WordmarkBrushProperty = DependencyProperty.Register(
        nameof(WordmarkBrush),
        typeof(System.Windows.Media.Brush),
        typeof(BrandLogo),
        new PropertyMetadata(System.Windows.Media.Brushes.White));

    public static readonly DependencyProperty WordmarkProperty = DependencyProperty.Register(
        nameof(Wordmark), typeof(string), typeof(BrandLogo), new PropertyMetadata("MAPLOCK"));
    public string Wordmark { get => (string)GetValue(WordmarkProperty); set => SetValue(WordmarkProperty, value); }

    public BrandLogo() => InitializeComponent();

    public System.Windows.Media.Brush WordmarkBrush
    {
        get => (System.Windows.Media.Brush)GetValue(WordmarkBrushProperty);
        set => SetValue(WordmarkBrushProperty, value);
    }
}
