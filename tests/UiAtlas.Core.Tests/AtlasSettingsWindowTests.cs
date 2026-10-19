using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UiAtlas.Core.Build;
using UiAtlas.Core.Recording.Windows;
using UIMaps.Recorder.Windows;
using static UiAtlas.Core.Tests.MapperOverlayWindowTests;

namespace UiAtlas.Core.Tests;

[Collection("Mapper desktop")]
public sealed class AtlasSettingsWindowTests
{
    [Fact]
    public void ProfileEditsSurviveNavigationSaveReopenAndResolveIntoTheCapturePolicy() => Sta(() =>
    {
        using var temp = new TempDirectory();
        var profiles = new RecordingProfileSettingsStore(Path.Combine(temp.Path, "profiles.json"));
        var builds = new MapBuildSettingsStore(Path.Combine(temp.Path, "builds.json"));
        profiles.Save(new("mapping-checkpoint"));
        var window = new AtlasSettingsWindow(profiles, builds);
        window.Show(); Pump(20);
        try
        {
            var selected = Combo(window, "Profile to edit");
            Assert.Equal("mapping-checkpoint", selected.SelectedValue);
            Descendants<CheckBox>(window).Single().IsChecked = true;
            Entry(window, "Maximum nodes per window").Text = "12000";
            Entry(window, "Maximum nodes per capture").Text = "60000";
            selected.SelectedValue = "balanced";
            selected.SelectedValue = "mapping-checkpoint";
            Assert.Equal("12000", Entry(window, "Maximum nodes per window").Text);
            Click(window, "Save recording profiles");
            var reopened = profiles.Load();
            var policy = new CapturePolicyResolver(new CaptureProfileCatalogService()).Resolve(
                RecordingProfileSettingsStore.Request(reopened), RecordingProfileSettingsStore.Consumer);
            Assert.Equal(12000, policy.MaxNodesPerWindow); Assert.Equal(60000, policy.MaxNodesPerEpisode);
            Assert.Contains(policy.ProviderBudgets, budget => budget.ProviderId == "msaa" && budget.MaxNodes == 12000);
            RenderIfRequested(window, "settings-recording-profiles.png");
            Descendants<TabControl>(window).Single().SelectedIndex = 1; Pump(10);
            Entry(window, "Maximum map nodes").Text = "350000";
            Entry(window, "Maximum map edges").Text = "750000";
            Click(window, "Save build settings"); Assert.Equal(new MapBuildSettings(350000, 750000), builds.Load());
            RenderIfRequested(window, "settings-map-building.png");
        }
        finally { window.Close(); }
        var second = new AtlasSettingsWindow(profiles, builds);
        try
        {
            second.Show(); Pump(10);
            Assert.Equal("12000", Entry(second, "Maximum nodes per window").Text);
            Combo(second, "Profile for new recordings").SelectedValue = "fast";
            Click(second, "Save recording profiles"); Assert.Equal("fast", profiles.Load().ActiveProfileId);
            Click(second, "Restore profile defaults"); Click(second, "Save recording profiles");
            Assert.Empty(profiles.Load().Overrides!);
            Descendants<TabControl>(second).Single().SelectedIndex = 1; Pump(10);
            Assert.Equal("750000", Entry(second, "Maximum map edges").Text);
            Click(second, "Restore build defaults"); Click(second, "Save build settings"); Assert.Equal(new(), builds.Load());
        }
        finally { second.Close(); }
    });

    [Fact]
    public void InvalidInputDoesNotReplaceSavedSettingsAndDeepSurveyKeepsUnlimitedCoverage() => Sta(() =>
    {
        using var temp = new TempDirectory();
        var profiles = new RecordingProfileSettingsStore(Path.Combine(temp.Path, "profiles.json"));
        var builds = new MapBuildSettingsStore(Path.Combine(temp.Path, "builds.json"));
        profiles.Save(new("mapping-checkpoint")); builds.Save(new());
        var window = new AtlasSettingsWindow(profiles, builds);
        try
        {
            window.Show(); Pump(10);
            Descendants<CheckBox>(window).Single().IsChecked = true;
            Entry(window, "Maximum nodes per capture").Text = "10";
            Click(window, "Save recording profiles"); Assert.Null(profiles.Load().Overrides);
            Assert.Contains(Descendants<TextBlock>(window), t => t.Text.Contains("must be at least"));
            Click(window, "Restore profile defaults"); Click(window, "Save recording profiles");
            Combo(window, "Profile to edit").SelectedValue = "deep-survey";
            Descendants<CheckBox>(window).Single().IsChecked = true;
            Assert.Equal("Unlimited", Entry(window, "Maximum nodes per capture").Text);
            Assert.False(Entry(window, "Maximum nodes per capture").IsEnabled);
            Assert.True(Entry(window, "Attempts per window").IsEnabled);
            Click(window, "Restore profile defaults"); Click(window, "Save recording profiles");
            Descendants<TabControl>(window).Single().SelectedIndex = 1; Pump(10);
            Entry(window, "Maximum map nodes").Text = "0";
            Click(window, "Save build settings"); Assert.Equal(new(), builds.Load());
            Assert.Contains(Descendants<TextBlock>(window), t => t.Text.Contains("must be a whole number"));
            Click(window, "Restore build defaults"); Click(window, "Save build settings");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void DeepSurveyIsSelectedByDefaultAndCanSaveUnlimitedAccessibility() => Sta(() =>
    {
        using var temp = new TempDirectory();
        var profiles = new RecordingProfileSettingsStore(Path.Combine(temp.Path, "profiles.json"));
        var window = new AtlasSettingsWindow(profiles, new MapBuildSettingsStore(Path.Combine(temp.Path, "builds.json")));
        try
        {
            window.Show(); Pump(10);
            Assert.Equal("deep-survey", Combo(window, "Profile for new recordings").SelectedValue);
            Assert.Equal("deep-survey", Combo(window, "Profile to edit").SelectedValue);
            Assert.Equal("0", Entry(window, "Accessibility timeout per window (ms)").Text);
            Assert.Equal("256", Entry(window, "Capture size limit (MiB)").Text);
            Descendants<CheckBox>(window).Single().IsChecked = true;
            Entry(window, "Concurrent accessibility workers").Text = "2";
            Entry(window, "Capture size limit (MiB)").Text = "384";
            Click(window, "Save recording profiles");
            var policy = new CapturePolicyResolver(new CaptureProfileCatalogService()).Resolve(
                RecordingProfileSettingsStore.Request(profiles.Load()), RecordingProfileSettingsStore.Consumer);
            Assert.Equal(2, policy.SchedulingPolicy.GlobalConcurrency);
            Assert.Equal(384L * 1024 * 1024, policy.MaxBytesPerEpisode);
            Assert.Equal(Timeout.InfiniteTimeSpan, RecorderPolicy.FromEffective(policy).UiaPerWindowDeadline);
            Assert.Equal(0, policy.MaxNodesPerWindow);
            Combo(window, "Profile to edit").SelectedValue = "balanced";
            Combo(window, "Profile to edit").SelectedValue = "deep-survey";
            Assert.Equal("384", Entry(window, "Capture size limit (MiB)").Text);
            Click(window, "Restore profile defaults"); Click(window, "Save recording profiles");
            Assert.Equal("256", Entry(window, "Capture size limit (MiB)").Text);
        }
        finally { window.Close(); }
    });

    private static ComboBox Combo(Window window, string name) => Descendants<ComboBox>(window).Single(c => AutomationProperties.GetName(c) == name);
    private static TextBox Entry(Window window, string name) => Descendants<TextBox>(window).Single(c => AutomationProperties.GetName(c) == name);
    private static void Click(Window window, string label) => Descendants<Button>(window).Single(b => Equals(b.Content, label))
        .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    private static void RenderIfRequested(Window window, string file)
    {
        window.UpdateLayout(); Pump(20);
        var directory = Environment.GetEnvironmentVariable("UIATLAS_MAPPER_QA_DIR");
        if (!string.IsNullOrWhiteSpace(directory)) { Directory.CreateDirectory(directory); Render((FrameworkElement)window.Content, Path.Combine(directory, file)); }
    }
}
