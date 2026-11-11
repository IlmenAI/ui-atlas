using System.Windows;

namespace UiAtlas.Core.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Light.xaml")
        });
        var evidenceIndex = Array.IndexOf(args, "--evidence");
        var graphTokens = evidenceIndex >= 0 ? args[..evidenceIndex] : args;
        var evidenceTokens = evidenceIndex >= 0 && evidenceIndex + 1 < args.Length
            ? args[(evidenceIndex + 1)..]
            : [];
        var graphPath = JoinPathTokens(graphTokens);
        var evidencePath = JoinPathTokens(evidenceTokens);
        using var instance = graphPath is null ? null : new MapViewerInstance(graphPath);
        try { if (instance?.ActivateExisting() == true) return; }
        catch (Exception error) when (error is System.IO.IOException or TimeoutException)
        {
            MessageBox.Show("This map is already opening. Switch to its viewer window or retry after it closes. " + error.Message, "UI Map viewer");
            return;
        }
        Window window = graphPath is not null && System.IO.Directory.Exists(graphPath)
            ? new MapReviewWindow(graphPath) : new ExplorerWindow(graphPath, evidencePath);
        instance?.Listen(window);
        app.Run(window);
    }

    private static string? JoinPathTokens(ReadOnlySpan<string> tokens)
    {
        if (tokens.Length == 0) return null;
        var value = string.Join(" ", tokens.ToArray()).Trim();
        return value.Length == 0 ? null : value.Trim('"');
    }
}
