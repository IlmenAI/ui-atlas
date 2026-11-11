using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Build;
using UiAtlas.Core.Recording.Windows;
using Xunit;

namespace UiAtlas.Core.Mapping.Tests;

public sealed class LogicalIdentitySettingsTests
{
    [Fact]
    public void SettingsSavesExperimentalModeAndRendersItsScope()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "identity-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            AtlasSettingsWindow? window = null;
            try
            {
                var store = new MapBuildSettingsStore(Path.Combine(directory, "build.json"));
                window = new(new RecordingProfileSettingsStore(Path.Combine(directory, "profiles.json")), store) { ShowInTaskbar = true };
                window.Show();
                var tabs = Descendants<TabControl>(window).Single(); tabs.SelectedIndex = 1; window.UpdateLayout();
                var checkbox = Descendants<CheckBox>(window).Single(c => c.Content?.ToString()?.Contains("Experimental") == true);
                Assert.False(checkbox.IsChecked);
                checkbox.IsChecked = true; checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Descendants<Button>(window).Single(b => b.Content?.ToString() == "Save build settings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(store.Load().ExperimentalControlIdentity);
                if (Environment.GetEnvironmentVariable("UIMAPS_IDENTITY_SETTINGS_IMAGE") is { Length: > 0 } image)
                {
                    window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    Directory.CreateDirectory(Path.GetDirectoryName(image)!); using var file = File.Create(image); encoder.Save(file);
                }
                window.Close(); window = new(new RecordingProfileSettingsStore(Path.Combine(directory, "profiles.json")), store);
                window.Show(); Descendants<TabControl>(window).Single().SelectedIndex = 1; window.UpdateLayout();
                Assert.True(Descendants<CheckBox>(window).Single(c => c.Content?.ToString()?.Contains("Experimental") == true).IsChecked);
            }
            catch (Exception error) { failure = error; }
            finally { window?.Close(); Directory.Delete(directory, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var value2 in Descendants<T>(child)) yield return value2;
        }
    }
}
