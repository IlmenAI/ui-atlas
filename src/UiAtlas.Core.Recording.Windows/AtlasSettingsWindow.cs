using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Google.Protobuf;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UIMaps.Protocol.V1;

namespace UiAtlas.Core.Recording.Windows;

public sealed class AtlasSettingsWindow : Window
{
    private sealed record LimitField(string Name, Func<CaptureLimits, long> Read, Action<CaptureLimits, long> Write,
        long Minimum, long Maximum, bool UnlimitedInSurvey = false);
    private sealed record ProfileDraft(bool Custom, Dictionary<string, string> Values);
    private static readonly LimitField[] Fields = [
        new("Overall duration (ms)", l => l.OverallCaptureDeadlineMs, (l,v) => l.OverallCaptureDeadlineMs = v, 250, 86_400_000, true),
        new("Screenshot timeout (ms)", l => l.ScreenCaptureDeadlineMs, (l,v) => l.ScreenCaptureDeadlineMs = v, 100, 300_000),
        new("Window census timeout (ms)", l => l.WindowTopologyDeadlineMs, (l,v) => l.WindowTopologyDeadlineMs = v, 100, 300_000),
        new("Accessibility timeout per window (ms)", l => l.UiaPerWindowDeadlineMs, (l,v) => l.UiaPerWindowDeadlineMs = v, 100, 300_000),
        new("Maximum windows", l => l.MaxWindows, (l,v) => l.MaxWindows = (int)v, 1, 100_000, true),
        new("Maximum nodes per window", l => l.MaxNodesPerWindow, (l,v) => l.MaxNodesPerWindow = (int)v, 1, 2_000_000, true),
        new("Maximum nodes per capture", l => l.MaxNodesPerEpisode, (l,v) => l.MaxNodesPerEpisode = (int)v, 1, int.MaxValue, true),
        new("Maximum tree depth", l => l.MaxDepth, (l,v) => l.MaxDepth = (int)v, 1, 512, true),
        new("Concurrent accessibility workers", l => l.MaxConcurrentUiaWorkers, (l,v) => l.MaxConcurrentUiaWorkers = (int)v, 1, 16),
        new("Attempts per window", l => l.MaxAttempts, (l,v) => l.MaxAttempts = (int)v, 1, 5),
        new("Capture size limit (MiB)", l => l.MaxBytesPerEpisode / CaptureSizeLimits.MiB, (l,v) => l.MaxBytesPerEpisode = v * CaptureSizeLimits.MiB, 1, CaptureSizeLimits.MaximumEpisodeBytes / CaptureSizeLimits.MiB)
    ];
    private readonly RecordingProfileSettingsStore _profilesStore;
    private readonly MapBuildSettingsStore _buildStore;
    private readonly IReadOnlyList<CaptureProfileDescriptor> _profiles;
    private RecordingProfileSettings _savedProfiles = new();
    private readonly ComboBox _active = new(), _selected = new();
    private readonly CheckBox _custom = new() { Content = "Customize this profile", Margin = new(0, 12, 0, 8) };
    private readonly TextBlock _description = Label(""), _profileStatus = Label(""), _buildStatus = Label("");
    private readonly Dictionary<string, TextBox> _entries = [];
    private readonly Dictionary<string, ProfileDraft> _drafts = [];
    private readonly TextBox _nodes = Entry("Maximum map nodes"), _edges = Entry("Maximum map edges");
    private CaptureProfileDescriptor? _shown;
    private bool _loading, _profilesDirty, _buildDirty;

    public AtlasSettingsWindow(RecordingProfileSettingsStore? profilesStore = null, MapBuildSettingsStore? buildStore = null,
        Action<Window>? openTableReading = null, bool embedded = false)
    {
        _profilesStore = profilesStore ?? new(); _buildStore = buildStore ?? new();
        _profiles = RecordingProfileSettingsStore.Profiles();
        Title = "UI Atlas settings"; Width = 860; Height = Math.Min(820, SystemParameters.WorkArea.Height - 32);
        MinWidth = 680; MinHeight = Math.Min(540, Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(247, 248, 252));
        Foreground = new SolidColorBrush(Color.FromRgb(37, 41, 54)); FontFamily = new("Segoe UI"); FontSize = 13;
        var root = new DockPanel { Margin = new(24), Background = Background };
        var heading = Label("Settings", 26); heading.FontWeight = FontWeights.SemiBold;
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var footer = new DockPanel { Margin = new(0, 16, 0, 0), LastChildFill = false }; DockPanel.SetDock(footer, Dock.Bottom);
        var close = Button("Close", Close); DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
        if (embedded) close.Visibility = Visibility.Collapsed;
        if (openTableReading is not null) footer.Children.Add(Button("Table reading…", () => openTableReading(this)));
        root.Children.Add(footer);
        var tabs = new TabControl { Margin = new(0, 12, 0, 0), Background = Brushes.White, Padding = new(16) };
        tabs.Items.Add(new TabItem { Header = "Recording profiles", Padding = new(16, 9, 16, 9), Content = BuildProfilesPage() });
        tabs.Items.Add(new TabItem { Header = "UI Map building", Padding = new(16, 9, 16, 9), Content = BuildMapPage() });
        root.Children.Add(tabs); Content = root;
        LoadSettings();
        _active.SelectionChanged += (_, _) => { if (!_loading) _profilesDirty = true; };
        _selected.SelectionChanged += (_, _) => { if (!_loading) { RememberDraft(); ShowProfile(); } };
        _custom.Checked += (_, _) => ChangedCustom(); _custom.Unchecked += (_, _) => ChangedCustom();
        _nodes.TextChanged += (_, _) => { if (!_loading) _buildDirty = true; };
        _edges.TextChanged += (_, _) => { if (!_loading) _buildDirty = true; };
        Closing += (_, args) => {
            if ((_profilesDirty || _buildDirty) && MessageBox.Show(this, "Discard your unsaved settings changes?", "Unsaved settings",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) args.Cancel = true;
        };
    }

    public bool ConfirmDiscard(Window owner) => !(_profilesDirty || _buildDirty)
        || MessageBox.Show(owner, "Leave Settings with unsaved changes? Your edits remain here until the app closes.", "Unsaved settings",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private UIElement BuildProfilesPage()
    {
        var panel = new StackPanel();
        panel.Children.Add(Label("Recording profiles", 21));
        panel.Children.Add(Label("Deep survey is the default for complete UI mapping coverage. Choose a profile for new recordings and edit its capture limits. A recording already in progress keeps its current settings."));
        panel.Children.Add(Label("Overrides are local to UI Atlas mapping tools, including mapping launched from MapLock. Shared Recorder defaults stay unchanged.", 12));
        panel.Children.Add(Label("Profile for new recordings"));
        ConfigureCombo(_active, "Profile for new recordings", _profiles.Where(p => p.DeprecationState == "active").ToArray()); panel.Children.Add(_active);
        panel.Children.Add(Label("Profile to edit")); ConfigureCombo(_selected, "Profile to edit", _profiles); panel.Children.Add(_selected);
        panel.Children.Add(_description); panel.Children.Add(_custom);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new GridLength(180) });
        foreach (var field in Fields)
        {
            var entry = Entry(field.Name); _entries.Add(field.Name, entry);
            entry.TextChanged += (_, _) => { if (!_loading) _profilesDirty = true; };
            AddRow(grid, Label(field.Name), entry);
        }
        panel.Children.Add(grid);
        panel.Children.Add(Label($"Capture size applies to each observation, including screenshots and accessibility data. Default: {CaptureSizeLimits.DefaultEpisodeBytes / CaptureSizeLimits.MiB} MiB; configurable from 1 to {CaptureSizeLimits.MaximumEpisodeBytes / CaptureSizeLimits.MiB} MiB. The same setting applies to UI mapping launched from MapLock. Deep survey and Mapping checkpoint include UI Automation and legacy MSAA controls. In Deep survey, an accessibility timeout of 0 waits for completion or cancellation.", 12));
        var actions = new WrapPanel { Margin = new(0, 12, 0, 0) };
        actions.Children.Add(Button("Save recording profiles", SaveProfiles));
        actions.Children.Add(Button("Restore profile defaults", () => {
            if (_shown is null) return;
            _drafts[_shown.ProfileId] = DefaultDraft(_shown); ShowProfile(); _profilesDirty = true;
            _profileStatus.Text = "Defaults restored in the editor. Save recording profiles to apply.";
        }));
        var page = new DockPanel();
        var footer = new StackPanel(); footer.Children.Add(actions); footer.Children.Add(_profileStatus);
        DockPanel.SetDock(footer, Dock.Bottom); page.Children.Add(footer); page.Children.Add(Scroll(panel));
        return page;
    }

    private UIElement BuildMapPage()
    {
        var panel = new StackPanel();
        panel.Children.Add(Label("UI Map building", 21));
        panel.Children.Add(Label("Set the maximum size of the map produced from your recordings. These limits apply to the next build or rebuild, including extensions to an existing map."));
        var grid = new Grid { Margin = new(0, 16, 0, 12) }; grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new GridLength(180) });
        AddRow(grid, Label("Maximum nodes"), _nodes); AddRow(grid, Label("Maximum edges"), _edges); panel.Children.Add(grid);
        panel.Children.Add(Label("Nodes represent applications, windows, states, and controls. Edges represent their relationships. Counts include all map layers."));
        panel.Children.Add(Label("Range: 1–2,000,000 each. Default: 100,000 each. Larger maps need more memory and take longer to build and open."));
        panel.Children.Add(Label("If either limit is exceeded, the build stops with the required counts. The existing map and source recordings are kept. Lowering a limit does not prevent existing maps from opening."));
        var actions = new WrapPanel { Margin = new(0, 16, 0, 0) };
        actions.Children.Add(Button("Save build settings", () => {
            try {
                var value = new MapBuildSettings((int)Parse(_nodes.Text, "Maximum nodes", 1, GraphContractLimits.MaxNodes),
                    (int)Parse(_edges.Text, "Maximum edges", 1, GraphContractLimits.MaxEdges));
                _buildStore.Save(value); _buildDirty = false; _buildStatus.Text = "Build settings saved. The next map build will use these limits.";
            } catch (Exception ex) { _buildStatus.Text = "Could not save build settings: " + ex.Message; }
        }));
        actions.Children.Add(Button("Restore build defaults", () => { _nodes.Text = "100000"; _edges.Text = "100000"; _buildDirty = true;
            _buildStatus.Text = "Defaults restored in the editor. Save build settings to apply."; }));
        panel.Children.Add(actions); panel.Children.Add(_buildStatus); return Scroll(panel);
    }

    private void LoadSettings()
    {
        _loading = true;
        try { _savedProfiles = _profilesStore.Load(); }
        catch (Exception ex) { _profileStatus.Text = "Could not load recording settings: " + ex.Message + " Defaults are shown; save to replace the unreadable settings."; }
        _active.SelectedValue = _savedProfiles.ActiveProfileId; _selected.SelectedValue = _savedProfiles.ActiveProfileId;
        var build = new MapBuildSettings();
        try { build = _buildStore.Load(); }
        catch (Exception ex) { _buildStatus.Text = "Could not load build settings: " + ex.Message + " Defaults are shown; save to replace the unreadable settings."; }
        _nodes.Text = build.MaxNodes.ToString(CultureInfo.InvariantCulture); _edges.Text = build.MaxEdges.ToString(CultureInfo.InvariantCulture);
        ShowProfile(); _loading = false;
    }

    private static ProfileDraft DefaultDraft(CaptureProfileDescriptor profile) => Draft(profile.DefaultLimits, false);
    private static ProfileDraft Draft(CaptureLimits limits, bool custom) => new(custom,
        Fields.ToDictionary(f => f.Name, f => f.Read(limits).ToString(CultureInfo.InvariantCulture)));
    private void RememberDraft()
    {
        if (_shown is not null) _drafts[_shown.ProfileId] = new(_custom.IsChecked == true, _entries.ToDictionary(e => e.Key, e => e.Value.Text));
    }
    private void ShowProfile()
    {
        _loading = true;
        _shown = _selected.SelectedItem as CaptureProfileDescriptor;
        if (_shown is not null)
        {
            if (!_drafts.TryGetValue(_shown.ProfileId, out var draft)) draft = Draft(RecordingProfileSettingsStore.LimitsFor(_savedProfiles, _shown),
                _savedProfiles.Overrides?.ContainsKey(_shown.ProfileId) == true);
            _custom.IsChecked = draft.Custom;
            foreach (var field in Fields) _entries[field.Name].Text = _shown.ExhaustiveCoverage && field.UnlimitedInSurvey ? "Unlimited" : draft.Values[field.Name];
            _description.Text = _shown.Description + (_shown.ExhaustiveCoverage ? " Duration, window, node, and depth limits remain unlimited. Accessibility waits for completion by default; set its timeout to 0 for no deadline. Capture remains cancellable." : "")
                + (_shown.DeprecationState != "active" ? " This profile is disabled by the recorder feature flag." : "");
            UpdateEnabled();
        }
        _loading = false;
    }
    private void ChangedCustom() { if (!_loading) { _profilesDirty = true; UpdateEnabled(); } }
    private void UpdateEnabled()
    {
        foreach (var field in Fields) _entries[field.Name].IsEnabled = _custom.IsChecked == true && !(_shown?.ExhaustiveCoverage == true && field.UnlimitedInSurvey);
    }
    private void SaveProfiles()
    {
        RememberDraft();
        try
        {
            var overrides = new Dictionary<string, string>(_savedProfiles.Overrides ?? []);
            foreach (var pair in _drafts)
            {
                if (!pair.Value.Custom) { overrides.Remove(pair.Key); continue; }
                var profile = _profiles.Single(p => p.ProfileId == pair.Key); var limits = profile.DefaultLimits.Clone();
                foreach (var field in Fields)
                    field.Write(limits, profile.ExhaustiveCoverage && (field.UnlimitedInSurvey
                        || field.Name == "Accessibility timeout per window (ms)" && pair.Value.Values[field.Name].Trim() == "0")
                        ? 0 : Parse(pair.Value.Values[field.Name], profile.DisplayName + ": " + field.Name, field.Minimum, field.Maximum));
                if (!profile.ExhaustiveCoverage && limits.MaxNodesPerEpisode < limits.MaxNodesPerWindow)
                    throw new InvalidDataException(profile.DisplayName + ": maximum nodes per capture must be at least maximum nodes per window.");
                overrides[pair.Key] = JsonFormatter.Default.Format(limits);
            }
            var settings = new RecordingProfileSettings(_active.SelectedValue as string ?? "", overrides);
            _profilesStore.Save(settings); _savedProfiles = settings; _profilesDirty = false;
            _profileStatus.Text = "Recording profiles saved. New recordings will use " + _profiles.Single(p => p.ProfileId == settings.ActiveProfileId).DisplayName + ".";
        }
        catch (Exception ex) { _profileStatus.Text = "Could not save recording profiles: " + ex.Message; }
    }

    private static long Parse(string text, string name, long min, long max)
    {
        if (!long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            throw new InvalidDataException($"{name} must be a whole number from {min:N0} to {max:N0}.");
        return value;
    }
    private static void ConfigureCombo(ComboBox box, string name, IReadOnlyList<CaptureProfileDescriptor> profiles)
    {
        box.ItemsSource = profiles; box.DisplayMemberPath = nameof(CaptureProfileDescriptor.DisplayName); box.SelectedValuePath = nameof(CaptureProfileDescriptor.ProfileId);
        box.Padding = new(7); box.Margin = new(0, 2, 0, 8); AutomationProperties.SetName(box, name);
    }
    private static void AddRow(Grid grid, UIElement label, UIElement field)
    {
        var row = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(label, row); Grid.SetRow(field, row); Grid.SetColumn(field, 1); grid.Children.Add(label); grid.Children.Add(field);
    }
    private static TextBox Entry(string name)
    {
        var box = new TextBox { Padding = new(7, 5, 7, 5), Margin = new(0, 3, 0, 3) }; AutomationProperties.SetName(box, name); return box;
    }
    private static TextBlock Label(string text, double size = 13) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Margin = new(0, 6, 12, 6), VerticalAlignment = VerticalAlignment.Center };
    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new(14, 8, 14, 8), Margin = new(0, 0, 8, 0) };
        AutomationProperties.SetName(button, text); button.Click += (_, _) => action(); return button;
    }
    private static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new(0, 0, 12, 0) };
}
