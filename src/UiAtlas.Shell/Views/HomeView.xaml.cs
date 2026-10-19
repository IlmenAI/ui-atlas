using System.Windows;
using System.Windows.Controls;

namespace UIMaps.Application.Views;

public partial class HomeView : UserControl
{
    public HomeView(string heading, bool workSessionsAvailable = false)
    {
        InitializeComponent();
        HeadingText.Text = heading;
        AvailableWorkSessions.Visibility = workSessionsAvailable ? Visibility.Visible : Visibility.Collapsed;
        FutureWorkSessions.Visibility = workSessionsAvailable ? Visibility.Collapsed : Visibility.Visible;
    }

    public event EventHandler? OpenUiMapRequested;

    public string Status
    {
        get => StatusText.Text;
        set => StatusText.Text = value;
    }

    private void OpenUiMap_Click(object sender, RoutedEventArgs e) => OpenUiMapRequested?.Invoke(this, EventArgs.Empty);
}
