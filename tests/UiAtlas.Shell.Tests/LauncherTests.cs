using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UiAtlas.Core.Storage;
using UiAtlas.Catalog;
using UiAtlas.Launcher;
using UiAtlas.Shell;
using UiAtlas.Shell.Catalog;
using UIMaps.Application.Core;
using UIMaps.Application.Views;
using Xunit;

namespace UiAtlas.Shell.Tests;

public sealed class LauncherTests
{
    [Fact]
    public void AtlasGroupsRecordingsWithoutChangingTheirAuthorityReferences()
    {
        var first = new MapsLibraryRecord(new UIMaps.Protocol.V3.MapLibraryEntry { ApplicationRef = "authority:first", MapDraftRef = "draft:first" }, null, null, null)
            { DisplayApplicationRef = "atlas:app:example" };
        var second = new MapsLibraryRecord(new UIMaps.Protocol.V3.MapLibraryEntry { ApplicationRef = "authority:second", MapDraftRef = "draft:second" }, null, null, null)
            { DisplayApplicationRef = "atlas:app:example" };
        var application = Assert.Single(MapsLibraryViewModel.BuildGroups([first, second]));
        Assert.Equal(2, application.Maps.Count);
        Assert.Equal("authority:first", first.Entry.ApplicationRef);
        Assert.Equal("authority:second", second.Entry.ApplicationRef);
    }
    [Fact]
    public void StartupPreferenceIsExplicitAndCommandOverrideIsTemporary()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-launcher-" + Guid.NewGuid().ToString("N"));
        try
        {
            var preferences = new LauncherPreferences(Path.Combine(root, "startup.json"));
            var catalog = new LocalArtifactCatalog(Path.Combine(root, "catalog"));
            Assert.Equal(ExperienceProfileId.Maps, preferences.Load());
            preferences.Save(ExperienceProfileId.Diagnostics);
            var host = new AtlasShellHost(catalog: catalog, preferences: preferences);
            Assert.Equal("diagnostics", host.StartupRoute);
            Assert.Equal("maps", new AtlasShellHost(ExperienceProfileId.Maps, catalog, preferences).StartupRoute);
            Assert.Equal(ExperienceProfileId.Diagnostics, preferences.Load());
            Assert.Throws<ArgumentException>(() => preferences.Save(ExperienceProfileId.Work));
            Assert.Throws<ArgumentException>(() => LauncherPreferences.ParseWorkspace(["--workspace", "work"]));
            Assert.Null(LauncherPreferences.ParseWorkspace([]));
            Assert.Equal(ExperienceProfileId.Diagnostics, LauncherPreferences.ParseWorkspace(["--workspace", "diagnostics"]));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AtlasCompositionShowsSharedWindowAndAllItsPagesWithoutMapLockServices()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "atlas-ui-" + Guid.NewGuid().ToString("N"));
            Application? app = null;
            ShellWindow? window = null;
            try
            {
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/UiAtlas.Shell;component/ShellTheme.xaml") });
                var host = new AtlasShellHost(catalog: new LocalArtifactCatalog(root), preferences: new LauncherPreferences(Path.Combine(root, "startup.json")));
                window = new ShellWindow(host) { ShowActivated = false };
                window.Show();
                Assert.True(window.IsVisible);
                Assert.Equal("UI Atlas", window.Title);
                Assert.Equal("maps", window.ActiveRoute);
                var selector = Assert.IsType<ComboBox>(window.FindName("ProfileSelector"));
                Assert.Equal(new[] { ExperienceProfileId.Maps, ExperienceProfileId.Diagnostics }, selector.Items.Cast<ExperienceProfileOption>().Select(value => value.Id));
                var page = Assert.IsType<ContentControl>(window.FindName("PageHost"));
                Assert.IsType<MapsView>(page.Content);
                selector.SelectedItem = selector.Items.Cast<ExperienceProfileOption>().Single(value => value.Id == ExperienceProfileId.Diagnostics);
                Assert.Equal("diagnostics", window.ActiveRoute);
                selector.SelectedItem = selector.Items.Cast<ExperienceProfileOption>().Single(value => value.Id == ExperienceProfileId.Maps);
                Assert.Equal("maps", window.ActiveRoute);
                window.Navigate("recordings");
                Assert.IsType<AtlasRecordingsView>(page.Content);
                window.Navigate("diagnostics");
                Assert.IsType<DiagnosticsHubView>(page.Content);
                window.Navigate("settings");
                var settingsView = Assert.IsType<AtlasSettingsView>(page.Content);
                var readiness = Assert.IsType<DockPanel>(settingsView.Content).Children.OfType<MapReadinessSettingsPanel>().Single();
                var toggle = Assert.IsType<StackPanel>(readiness.Content).Children.OfType<CheckBox>().Single();
                Assert.True(toggle.IsChecked);
                toggle.IsChecked = false;
                toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.False(new MapReadinessSettingsStore(root).Load().RequireReview);
                window.UpdateLayout();
                if (Environment.GetEnvironmentVariable("UIATLAS_SETTINGS_QA_DIR") is { Length: > 0 } qa)
                {
                    Directory.CreateDirectory(qa);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,
                        (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var image = File.Create(Path.Combine(qa, "atlas-readiness-setting.png"));
                    encoder.Save(image);
                }
                var reopened = new MapReadinessSettingsPanel(root);
                Assert.False(Assert.IsType<StackPanel>(reopened.Content).Children.OfType<CheckBox>().Single().IsChecked);
                toggle.IsChecked = true;
                toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.True(new MapReadinessSettingsStore(root).Load().RequireReview);
                window.Navigate("maps");
                Assert.Throws<ArgumentException>(() => window.Navigate("integrations"));
                Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name is "UIMaps.Application" or "UIMaps.LocalServices");
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
            catch (Exception error) { failure = error; }
            finally { window?.Close(); app?.Shutdown(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Shared shell did not complete startup and navigation.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
