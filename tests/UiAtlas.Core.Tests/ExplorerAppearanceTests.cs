using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using UiAtlas.Core.Desktop;
using UiAtlas.Core.Build;
using UiAtlas.Core.Recording.Windows;
using UiAtlas.Presentation;
using static UiAtlas.Core.Tests.MapperOverlayWindowTests;

namespace UiAtlas.Core.Tests;

[Collection("Mapper desktop")]
public sealed class ExplorerAppearanceTests
{
    [Fact]
    public void EditorAndSettingsKeepReadableTextTabsDraftsAndLayoutAcrossAllThemes()
    {
        Sta(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "explorer-appearance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var previousPath = Environment.GetEnvironmentVariable("UIMAPS_APPEARANCE_SETTINGS");
            var previous = Appearance.Current;
            Environment.SetEnvironmentVariable("UIMAPS_APPEARANCE_SETTINGS", Path.Combine(directory, "appearance.json"));
            var editor = new ExplorerWindow(null) { Left = -10000, Width = 1320, Height = 820, ShowActivated = false, ShowInTaskbar = false };
            var settings = new AtlasSettingsWindow(new(Path.Combine(directory, "profiles.json")), new(Path.Combine(directory, "build.json")))
                { Left = -10000, ShowActivated = false };
            try
            {
                var tree = Field<TreeView>(editor, "_hierarchy");
                var header = (FrameworkElement)typeof(ExplorerWindow).GetMethod("HierarchyItemHeader", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, ["Retained selected surface", Brushes.Blue, FontWeights.SemiBold, null, null])!;
                var item = new TreeViewItem { Header = header, IsSelected = true }; tree.Items.Add(item);
                editor.Show(); settings.Show(); Pump(40);
                var tabs = Descendants<TabControl>(settings).Single(); tabs.SelectedIndex = 1;
                settings.UpdateLayout();
                var nodes = Field<TextBox>(settings, "_nodes"); nodes.Text = "123456"; nodes.Select(2, 3);
                var image = Field<Image>(editor, "_appMapImage");
                var retained = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 }, 8);
                image.Source = retained;
                var loads = 0; header.Loaded += (_, _) => loads++;
                foreach (var (id, _) in AppearancePreference.Themes)
                foreach (var mode in new[] { "light", "dark" })
                {
                    Appearance.SetPreference(new(id, mode)); editor.UpdateLayout(); settings.UpdateLayout(); Pump(10);
                    CheckNativeCaption(editor, mode); CheckNativeCaption(settings, mode);
                    foreach (var field in new[] { "_title", "_appMapTitle", "_topologyTitle", "_loadingTitle" })
                        Assert.Same(editor.FindResource("InkBrush"), Field<TextBlock>(editor, field).Foreground);
                    Assert.Same(editor.FindResource("InkBrush"), Descendants<TextBlock>(header).Single().Foreground);
                    Assert.Same(retained, image.Source); Assert.Same(item, tree.SelectedItem); Assert.Equal(0, loads);
                    Assert.Equal("123456", nodes.Text); Assert.Equal(2, nodes.SelectionStart); Assert.Equal(3, nodes.SelectionLength);
                    Assert.Equal(1, tabs.SelectedIndex);
                    var selected = Assert.IsType<TabItem>(tabs.SelectedItem);
                    var frame = Assert.IsType<Border>(selected.Template.FindName("HeaderFrame", selected));
                    Assert.Same(settings.FindResource("AccentSelectionBrush"), frame.Background);
                    Assert.Same(settings.FindResource("SelectionInkBrush"), selected.Foreground);
                    Capture(editor, id + "-" + mode + "-editor"); Capture(settings, id + "-" + mode + "-atlas-settings");
                }
                var layout = Field<Button>(editor, "_layoutToggleButton");
                layout.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); editor.UpdateLayout();
                Assert.False(Field<bool>(editor, "_sideBySideLayout")); Assert.Same(retained, image.Source);
                Capture(editor, "stacked-editor");
                layout.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.True(Field<bool>(editor, "_sideBySideLayout"));
                // Dismissing the themed confirmation must leave the unsaved map limits intact.
                settings.Dispatcher.BeginInvoke(() =>
                {
                    var dialog = settings.OwnedWindows.Cast<Window>().Single(); dialog.UpdateLayout();
                    var no = Descendants<Button>(dialog).Single(b => Equals(b.Content, "No"));
                    Assert.True(no.IsCancel); Assert.True(no.IsDefault);
                    Capture(dialog, "unsaved-settings"); no.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                settings.Close(); Assert.True(settings.IsVisible); Assert.Equal("123456", nodes.Text);
                Assert.False(File.Exists(Path.Combine(directory, "build.json")));
            }
            finally
            {
                typeof(AtlasSettingsWindow).GetField("_buildDirty", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settings, false);
                settings.Close(); editor.Close(); Appearance.SetPreference(previous);
                Environment.SetEnvironmentVariable("UIMAPS_APPEARANCE_SETTINGS", previousPath);
            }
        });
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static void CheckNativeCaption(Window window, string mode)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return;
        Assert.Equal(0, DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 20, out var dark, sizeof(int)));
        Assert.Equal(!SystemParameters.HighContrast && mode == "dark" ? 1 : 0, dark);
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("UIATLAS_APPEARANCE_QA_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory); window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }
}
