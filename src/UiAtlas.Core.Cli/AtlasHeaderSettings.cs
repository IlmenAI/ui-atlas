using System.Windows;
using UiAtlas.Presentation;

namespace UiAtlas.Core.Cli;

internal static class AtlasHeaderSettings
{
    internal static async Task OpenAsync(Window owner)
    {
        if (Environment.GetEnvironmentVariable(AtlasSettingsNavigation.EnvironmentKey) is not null)
        {
            owner.Topmost = false;
            if (owner.Owner is { } parent) parent.Topmost = false;
            if (!await AtlasSettingsNavigation.RequestAsync())
                MessageBox.Show(owner, "MapLock could not be reached. Open Settings → Recording in its main window.", "Header reading settings");
            return;
        }
        new RecorderSettingsWindow { Owner = owner }.ShowDialog();
    }
}
