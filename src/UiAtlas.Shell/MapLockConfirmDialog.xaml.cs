using System.Windows;

namespace UIMaps.Application;

public partial class MapLockConfirmDialog : Window
{
    private MapLockConfirmDialog(
        string title,
        string heading,
        string message,
        string detail,
        string confirmText)
    {
        InitializeComponent();
        Title = title;
        DialogTitle.Text = title;
        HeadingText.Text = heading;
        MessageText.Text = message;
        DetailText.Text = detail;
        ConfirmButton.Content = confirmText;
    }

    public static bool ShowConfirmation(
        Window? owner,
        string title,
        string heading,
        string message,
        string detail,
        string confirmText)
    {
        var dialog = new MapLockConfirmDialog(title, heading, message, detail, confirmText);
        if (owner is not null)
            dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
