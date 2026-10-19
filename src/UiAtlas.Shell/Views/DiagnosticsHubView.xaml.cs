using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using UIMaps.Application.Core;

namespace UIMaps.Application.Views;

public sealed class ExploreApplicationRequestedEventArgs(RecordableApplicationTarget target) : EventArgs
{
    public RecordableApplicationTarget Target { get; } = target;
}

public partial class DiagnosticsHubView : UserControl
{
    private readonly IRecordableApplicationDiscovery _discovery;

    public DiagnosticsHubView() : this(new RecordableApplicationDiscovery()) { }

    internal DiagnosticsHubView(IRecordableApplicationDiscovery discovery, bool fullWorkspaceAvailable = true)
    {
        _discovery = discovery;
        InitializeComponent();
        if (!fullWorkspaceAvailable)
        {
            FullWorkspaceCard.Visibility = Visibility.Collapsed;
            ExploreDescription.Text = "Choose a visible window to inspect a native capture, provider health, and accessibility evidence.";
            InspectionDescription.Text = "Inspection is read-only. Capture starts only when you select Capture snapshot.";
        }
        Loaded += (_, _) => RefreshTargets();
    }

    public event EventHandler<ExploreApplicationRequestedEventArgs>? ExploreApplicationRequested;
    public event EventHandler? OpenFullDiagnosticsRequested;

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshTargets();

    internal void RefreshTargets()
    {
        var targets = _discovery.Discover();
        var view = new ListCollectionView(targets.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RecordableApplicationTarget.Category)));
        ApplicationsList.ItemsSource = view;
        ApplicationsList.SelectedIndex = targets.Count > 0 ? 0 : -1;
        EmptyState.Visibility = targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateExploreState();
    }

    private void ApplicationsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateExploreState();

    private void UpdateExploreState()
    {
        if (ExploreButton is not null)
            ExploreButton.IsEnabled = ApplicationsList.SelectedItem is RecordableApplicationTarget;
    }

    private void Explore_Click(object sender, RoutedEventArgs e)
    {
        if (ApplicationsList.SelectedItem is RecordableApplicationTarget target)
            ExploreApplicationRequested?.Invoke(this, new ExploreApplicationRequestedEventArgs(target));
    }

    private void OpenFullDiagnostics_Click(object sender, RoutedEventArgs e) =>
        OpenFullDiagnosticsRequested?.Invoke(this, EventArgs.Empty);
}
