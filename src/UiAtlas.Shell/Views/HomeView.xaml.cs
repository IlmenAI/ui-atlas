using System.Windows;
using System.Windows.Controls;

namespace UIMaps.Application.Views;

public partial class HomeView : UserControl
{
    public HomeView(string heading)
    {
        InitializeComponent();
        HeadingText.Text = heading;
    }

    public event EventHandler? OpenUiMapRequested;

    public string Status
    {
        get => StatusText.Text;
        set => StatusText.Text = value;
    }

    private void OpenUiMap_Click(object sender, RoutedEventArgs e) => OpenUiMapRequested?.Invoke(this, EventArgs.Empty);
}
