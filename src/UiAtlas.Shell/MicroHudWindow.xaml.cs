using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using UIMaps.Application.Core;

namespace UIMaps.Application;

public partial class MicroHudWindow : Window
{
    private static readonly nint HwndTopmost = new(-1);
    private const uint SwpShowWindow = 0x0040;

    private readonly Rectangle _displayBounds;
    private bool _positioned;

    internal event EventHandler? RecordUiMapRequested;

    internal MicroHudWindow(
        Rectangle displayBounds,
        ExperienceProfileDefinition profile)
    {
        _displayBounds = displayBounds;
        InitializeComponent();
        CommandsItemsControl.ItemsSource = profile.HudCommands;
        ContentRendered += MicroHudWindow_ContentRendered;
    }

    private void MicroHudWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (_positioned)
            return;
        _positioned = true;

        var dpi = VisualTreeHelper.GetDpi(this);
        var requestedSize = new System.Drawing.Size(
            Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY)));
        var placement = MicroHudPlacement.Calculate(_displayBounds, requestedSize);
        SetWindowPos(
            new WindowInteropHelper(this).Handle,
            HwndTopmost,
            placement.X,
            placement.Y,
            placement.Width,
            placement.Height,
            SwpShowWindow);
    }

    private void CommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string commandId })
            return;

        if (commandId == "hud.record-map")
        {
            RecordUiMapRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        MessageBox.Show(this, "This capability is still in development.", "Micro HUD",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MicroHudWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        Close();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
