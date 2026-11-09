using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace UiAtlas.Shell;

/// <summary>Shared destructive-action presentation; callers retain ownership of deletion.</summary>
public class DeleteConfirmationDialog : Window
{
    public DeleteConfirmationDialog(string kind, string name, string message, string? subtitle = null, string? detail = null)
    {
        Title = "Delete " + kind; Width = 500; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14; Background = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = kind == "UI Maps" ? "Delete these UI Maps?" : $"Delete this {kind}?", FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, subtitle is null ? 12 : 4) });
        if (subtitle is not null) panel.Children.Add(new TextBlock { Text = subtitle, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (detail is not null) panel.Children.Add(new TextBlock { Text = detail, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Name = "CancelButton", Content = "Cancel", IsCancel = true, IsDefault = true, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 10, 0) };
        var delete = new Button { Name = "DeleteButton", Content = "Delete " + kind, Padding = new Thickness(18, 8, 18, 8), Background = new SolidColorBrush(Color.FromRgb(180, 35, 24)), Foreground = Brushes.White };
        cancel.Click += (_, _) => DialogResult = false;
        delete.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(delete); panel.Children.Add(buttons);
        Loaded += (_, _) => cancel.Focus();
    }
    public static bool Confirm(Window? owner, string kind, string name, string message, string? subtitle = null, string? detail = null)
    {
        var dialog = new DeleteConfirmationDialog(kind, name, message, subtitle, detail);
        if (owner is not null) { dialog.Owner = owner; dialog.Icon = owner.Icon; }
        return dialog.ShowDialog() == true;
    }
}
