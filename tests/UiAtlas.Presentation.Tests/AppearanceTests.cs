using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace UiAtlas.Presentation.Tests;

[CollectionDefinition("Appearance", DisableParallelization = true)]
public sealed class AppearanceCollection;

[Collection("Appearance")]
public sealed class AppearanceTests
{
    [Fact]
    public void PreferencesValidateAndRoundTripWithoutTouchingOtherSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "appearance-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "appearance.json");
            var other = Path.Combine(directory, "settings.json");
            File.WriteAllText(other, "retained unrelated settings");
            var store = new AppearancePreferenceStore(path);
            Assert.Equal(new(), store.Load());
            store.Save(new("aero", "dark"));
            Assert.Equal(new("aero", "dark"), store.Load());
            store.Save(new("unrecognized", "invalid"));
            Assert.Equal(new(), store.Load());
            File.WriteAllText(path, "{\"schemaVersion\":1,\"themeId\":\"ilmen\",\"colorMode\":\"unknown\"}");
            Assert.Equal(new("ilmen", "light"), store.Load());
            File.WriteAllText(path, "{\"schemaVersion\":99,\"themeId\":\"aero\",\"colorMode\":\"dark\"}");
            Assert.Equal(new(), store.Load());
            File.WriteAllText(path, "{interrupted");
            Assert.Equal(new(), store.Load());
            Assert.Equal("retained unrelated settings", File.ReadAllText(other));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void EveryPaletteResolvesNativeResourcesAndEditableControlTemplates()
    {
        Sta(() =>
        {
            foreach (var (id, _) in AppearancePreference.Themes)
            foreach (var mode in new[] { "light", "dark" })
            {
                var window = new Window { Width = 650, Height = 420, ShowActivated = false, ShowInTaskbar = false, Left = -10000 };
                try
                {
                    window.Resources.MergedDictionaries.Add(Appearance.CreateResources(new(id, mode), false));
                    var entry = new TextBox { Text = "Retained draft" };
                    var combo = new ComboBox { IsEditable = true, ItemsSource = new[] { "First", "Second" }, Text = "Custom entry" };
                    Appearance.Bind(window, ("Foreground", "InkBrush"), ("Background", "CanvasBrush"), ("FontFamily", "BodyFontFamily"));
                    var body = new StackPanel { Margin = new Thickness(24) };
                    body.Children.Add(Appearance.Bind(new Border { Padding = new Thickness(12), Child = Appearance.Bind(new TextBlock { Text = $"{id} · {mode}", FontSize = 22 }, ("Foreground", "ChromeInkBrush")) }, ("Background", "ChromeBrush")));
                    body.Children.Add(entry); body.Children.Add(combo);
                    body.Children.Add(new Button { Content = "Create", Style = (Style)window.FindResource("PrimaryActionButtonStyle") });
                    body.Children.Add(new CheckBox { Content = "Keep draft", IsChecked = true });
                    body.Children.Add(new DataGrid { ItemsSource = new[] { new { Evidence = "Read only", Version = 3 } }, IsReadOnly = true });
                    window.Content = body;
                    window.Show();
                    // The process-wide Window Loaded hook may have applied the persisted theme.
                    // Replace it here so each iteration exercises precisely this palette.
                    window.Resources.MergedDictionaries.Add(Appearance.CreateResources(new(id, mode), false));
                    window.UpdateLayout();
                    foreach (var key in new[] { "CanvasBrush", "PanelBrush", "InkBrush", "AccentBrush", "AccentTextBrush", "SelectionInkBrush", "FocusBrush", "WarningBrush", "ErrorBrush", "SuccessBrush", "ChromeBrush", "DisabledInkBrush" })
                        Assert.IsAssignableFrom<Brush>(window.FindResource(key));
                    Assert.IsType<FontFamily>(window.FindResource("BodyFontFamily"));
                    Assert.IsType<Thickness>(window.FindResource("ControlPadding"));
                    Assert.IsType<CornerRadius>(window.FindResource("ControlCornerRadius"));
                    Assert.NotNull(entry.Template.FindName("PART_ContentHost", entry));
                    Assert.NotNull(combo.Template.FindName("PART_EditableTextBox", combo));
                    Assert.Equal("Custom entry", combo.Text);
                    if (id is "plex" or "aero") Assert.IsType<LinearGradientBrush>(window.FindResource("ChromeBrush"));
                    var hc = Appearance.CreateResources(new(id, mode), true);
                    Assert.Same(SystemColors.WindowBrush, hc["CanvasBrush"]);
                    Assert.Same(SystemColors.HighlightTextBrush, hc["SelectionInkBrush"]);
                    if (Environment.GetEnvironmentVariable("UIATLAS_APPEARANCE_QA_DIR") is { Length: > 0 } directory)
                    {
                        Directory.CreateDirectory(directory);
                        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(Path.Combine(directory, $"{id}-{mode}.png")); encoder.Save(stream);
                    }
                }
                finally { window.Close(); }
            }
        });
    }

    [Fact]
    public void SwitchingAndExternalRefreshKeepDraftSelectionAndWindowIdentity()
    {
        var previous = Environment.GetEnvironmentVariable("UIMAPS_APPEARANCE_SETTINGS");
        var path = Path.Combine(Path.GetTempPath(), "appearance-" + Guid.NewGuid().ToString("N") + ".json");
        Environment.SetEnvironmentVariable("UIMAPS_APPEARANCE_SETTINGS", path);
        try
        {
            Sta(() =>
            {
                var draft = new TextBox { Text = "Unsubmitted correction", SelectionStart = 3, SelectionLength = 5 };
                var selector = new ComboBox { IsEditable = true, ItemsSource = new[] { "Saved map 7", "Saved map 8" }, SelectedIndex = 1 };
                var panel = new StackPanel(); panel.Children.Add(draft); panel.Children.Add(selector);
                var settings = new AppearanceSettingsPanel(); panel.Children.Add(settings);
                var window = new Window { Content = panel, Width = 400, Height = 240, ShowActivated = false, ShowInTaskbar = false, Left = -10000 };
                Appearance.Apply(window); window.Show();
                try
                {
                    Appearance.Bind(draft, ("Foreground", "InkBrush"));
                    window.UpdateLayout();
                    var choices = settings.Children.OfType<WrapPanel>().Single().Children.OfType<StackPanel>()
                        .SelectMany(field => field.Children.OfType<ComboBox>()).ToArray();
                    choices[0].SelectedValue = "aero"; choices[1].SelectedIndex = 1;
                    Assert.Equal(new("aero", "dark"), new AppearancePreferenceStore(path).Load());
                    var editor = (TextBox)selector.Template.FindName("PART_EditableTextBox", selector);
                    editor.Select(2, 4);
                    foreach (var (id, _) in AppearancePreference.Themes)
                    foreach (var mode in new[] { "dark", "light" })
                    {
                        Appearance.SetPreference(new(id, mode)); window.UpdateLayout();
                        Assert.Equal(id, choices[0].SelectedValue);
                        Assert.Equal(mode == "dark" ? 1 : 0, choices[1].SelectedIndex);
                        Assert.Same(panel, window.Content);
                        Assert.Equal("Unsubmitted correction", draft.Text);
                        Assert.Equal(3, draft.SelectionStart); Assert.Equal(5, draft.SelectionLength);
                        Assert.Equal(1, selector.SelectedIndex);
                        Assert.Same(editor, selector.Template.FindName("PART_EditableTextBox", selector));
                        Assert.Equal(2, editor.SelectionStart); Assert.Equal(4, editor.SelectionLength);
                        Assert.Same(window.FindResource("InkBrush"), draft.Foreground);
                    }
                    // An independent process writes this same atomic store. Timer and activation both use Refresh.
                    new AppearancePreferenceStore(path).Save(new("ilmen", "dark"));
                    Appearance.Refresh();
                    Assert.Equal(new("ilmen", "dark"), Appearance.Current);
                    Assert.Same(window.FindResource("InkBrush"), draft.Foreground);
                }
                finally { window.Close(); }
                var closedResources = window.Resources.MergedDictionaries.Last();
                Appearance.SetPreference(new("fluent", "light"));
                Assert.Same(closedResources, window.Resources.MergedDictionaries.Last());
            });
        }
        finally { Environment.SetEnvironmentVariable("UIMAPS_APPEARANCE_SETTINGS", previous); File.Delete(path); }
    }

    [Fact]
    public async Task BroadcastConstructsResourcesOnEachOwningDispatcher()
    {
        var previous = Environment.GetEnvironmentVariable("UIMAPS_APPEARANCE_SETTINGS");
        var path = Path.Combine(Path.GetTempPath(), "appearance-dispatchers-" + Guid.NewGuid().ToString("N") + ".json");
        Environment.SetEnvironmentVariable("UIMAPS_APPEARANCE_SETTINGS", path);
        try
        {
            var ready = new TaskCompletionSource<Window>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Thread(() =>
            {
                try
                {
                    var companion = new Window { Title = "Appearance dispatcher fixture", Content = new TextBox { Text = "Retained companion draft" }, Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false, Left = -10000 };
                    Appearance.Apply(companion); companion.Show(); ready.SetResult(companion);
                    Dispatcher.Run();
                }
                catch (Exception error) { ready.TrySetException(error); }
            });
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
            var other = await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            try
            {
                Sta(() =>
                {
                    var primary = new Window(); Appearance.Apply(primary);
                    try
                    {
                        Appearance.SetPreference(new("plex", "dark"));
                        var local = (Brush)primary.FindResource("CanvasBrush");
                        var remote = other.Dispatcher.Invoke(() =>
                        {
                            Assert.Equal("Retained companion draft", ((TextBox)other.Content).Text);
                            var brush = (Brush)other.FindResource("CanvasBrush");
                            Assert.True(brush.CheckAccess());
                            return brush;
                        });
                        Assert.NotSame(local, remote);
                        Assert.True(local.CheckAccess());
                    }
                    finally { primary.Close(); }
                });
            }
            finally
            {
                other.Dispatcher.Invoke(() => { other.Close(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); });
                Assert.True(worker.Join(TimeSpan.FromSeconds(20)));
            }
        }
        finally { Environment.SetEnvironmentVariable("UIMAPS_APPEARANCE_SETTINGS", previous); File.Delete(path); }
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Native appearance check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
