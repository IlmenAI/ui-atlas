using System.Windows.Controls;

namespace UIMaps.Application.Views;

public partial class PlaceholderView : UserControl
{
    public PlaceholderView(string heading, string message, string glyph)
    {
        InitializeComponent();
        HeadingText.Text = heading;
        MessageText.Text = message;
        GlyphText.Text = glyph;
    }
}
