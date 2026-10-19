using System.IO;
using System.Windows;
using UiAtlas.Shell;

namespace UiAtlas.Launcher;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var workspace = LauncherPreferences.ParseWorkspace(args);
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/UiAtlas.Shell;component/ShellTheme.xaml") });
            var window = new ShellWindow(new AtlasShellHost(workspace));
            window.Loaded += CenterOnDisplay;
            app.Run(window);
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "UI Atlas", MessageBoxButton.OK, MessageBoxImage.Error);
            return 2;
        }
    }

    private static void CenterOnDisplay(object sender, RoutedEventArgs args)
    {
        var window = (Window)sender;
        window.Loaded -= CenterOnDisplay;
        UIMaps.Application.Core.DisplayWindowPlacement.CenterWindow(window);
    }
}
