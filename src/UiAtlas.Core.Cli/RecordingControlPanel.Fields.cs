using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Cli;

internal sealed partial class RecordingControlPanel
{
    private FieldLearningSnapshot _fieldSnapshot = FieldLearningSnapshot.Empty;
    private Button? _fieldsExpandButton, _markFieldButton, _reviewFieldsButton;
    private TextBlock? _fieldsHeading, _fieldMarkHint;
    private Border? _fieldDetails, _fieldReviewShell;
    private StackPanel? _fieldRows, _fieldReviewRows;
    private TextBlock? _fieldReviewHeading, _fieldReviewSubtitle;
    private Button? _keepFieldsButton;
    private readonly Dictionary<string, bool> _fieldReviewSelection = new(StringComparer.Ordinal);
    private bool _fieldsExpanded, _fieldMarking, _fieldReviewVisible, _fieldReviewWasCompact, _fieldReviewRecordingComplete;
    private TaskCompletionSource? _fieldReviewCompletion;

    internal event Action? MarkFieldRequested;
    internal event Action? FieldReviewRequested;
    internal event Action<string, FieldReviewDecision>? FieldDecisionChanged;
    internal event Action? FieldReviewClosed;

    internal void RequestFieldReviewCommand() => _commands.Writer.TryWrite("REVIEW_FIELDS");

    internal void SetFieldObservations(FieldLearningSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var copy = snapshot with { Fields = snapshot.Fields.ToArray() };
        Post(() =>
        {
            _fieldSnapshot = copy;
            RefreshFieldPanel();
            if (_fieldReviewVisible) RefreshFieldReview();
        });
    }

    internal void SetFieldMarking(bool armed) => Post(() =>
    {
        _fieldMarking = armed;
        RefreshFieldPanel();
    });

    /// <summary>The caller pauses capture before opening a review of a live session.</summary>
    internal Task ShowFieldReview(bool recordingComplete = false)
    {
        var dispatcher = _dispatcher;
        if (_windowClosed || dispatcher is null || dispatcher.HasShutdownStarted) return Task.CompletedTask;
        return dispatcher.InvokeAsync(() =>
        {
            if (_windowClosed) return Task.CompletedTask;
            if (_fieldReviewVisible) return _fieldReviewCompletion?.Task ?? Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _fieldReviewCompletion = completion;
            _fieldReviewWasCompact = _isCompactCollapsed;
            _fieldReviewRecordingComplete = recordingComplete;
            _isCompactCollapsed = false;
            _fieldReviewSelection.Clear();
            _fieldReviewVisible = true;
            RefreshFieldReview();
            UpdateShellVisibility();
            return completion.Task;
        }).Task.Unwrap();
    }

    private void CloseFieldReview()
    {
        if (!_fieldReviewVisible) return;
        _fieldReviewVisible = false;
        _isCompactCollapsed = _fieldReviewWasCompact;
        UpdateShellVisibility();
        _fieldReviewCompletion?.TrySetResult();
        _fieldReviewCompletion = null;
        FieldReviewClosed?.Invoke();
    }

    private void CompleteFieldReviewOnClose()
    {
        _fieldReviewVisible = false;
        _fieldReviewCompletion?.TrySetResult();
        _fieldReviewCompletion = null;
    }

    private Border BuildFieldsPanel()
    {
        var stack = new StackPanel();
        var header = new DockPanel { LastChildFill = true };
        _markFieldButton = FieldButton("Mark a field", () => MarkFieldRequested?.Invoke(), false, "AtlasRecorderMarkField");
        _markFieldButton.Padding = new Thickness(FieldSize(6), FieldSize(3), FieldSize(6), FieldSize(3));
        _markFieldButton.FontSize = FieldSize(10);
        _markFieldButton.MinHeight = FieldSize(23);
        _markFieldButton.ToolTip = "Point out a field, then edit it yourself. Press again to cancel marking.";
        DockPanel.SetDock(_markFieldButton, Dock.Right);
        header.Children.Add(_markFieldButton);
        _fieldsHeading = FieldText("⌄  Fields", 12, Ink);
        _fieldsHeading.FontWeight = FontWeights.SemiBold;
        _fieldsExpandButton = new Button
        {
            Content = _fieldsHeading, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand, ToolTip = "Show suggested fields and observed changes", Focusable = true
        };
        AutomationProperties.SetAutomationId(_fieldsExpandButton, "AtlasRecorderFields");
        AutomationProperties.SetName(_fieldsExpandButton, "Show field observations");
        _fieldsExpandButton.Click += (_, _) => { _fieldsExpanded = !_fieldsExpanded; RefreshFieldPanel(); };
        header.Children.Add(_fieldsExpandButton);
        stack.Children.Add(header);
        _fieldMarkHint = FieldText("Click a field in the app. Edit it as usual.", 10, Accent);
        _fieldMarkHint.Margin = new Thickness(0, FieldSize(3), 0, 0);
        _fieldMarkHint.Visibility = Visibility.Collapsed;
        stack.Children.Add(_fieldMarkHint);

        _fieldRows = new StackPanel();
        var details = new StackPanel();
        details.Children.Add(new ScrollViewer
        {
            Content = _fieldRows, MaxHeight = FieldSize(75),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        _reviewFieldsButton = FieldLink("Review changes", () => FieldReviewRequested?.Invoke(), "AtlasRecorderReviewFields");
        details.Children.Add(_reviewFieldsButton);
        _fieldDetails = new Border { Child = details, Margin = new Thickness(0, FieldSize(4), 0, 0), Visibility = Visibility.Collapsed };
        stack.Children.Add(_fieldDetails);
        var panel = new Border
        {
            Child = stack, BorderBrush = Divider, BorderThickness = new Thickness(0, FieldSize(1), 0, 0),
            Padding = new Thickness(0, FieldSize(5), 0, 0), Margin = new Thickness(0, FieldSize(4), 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(225, 249, 249, 250))
        };
        RefreshFieldPanel();
        return panel;
    }

    private void RefreshFieldPanel()
    {
        if (_fieldsHeading is null || _fieldRows is null || _fieldDetails is null) return;
        var fields = _fieldSnapshot.Fields;
        _fieldsHeading.Text = (_fieldsExpanded ? "⌃  " : "⌄  ") + FieldObservationPresentation.Summary(fields);
        _fieldDetails.Visibility = _fieldsExpanded ? Visibility.Visible : Visibility.Collapsed;
        if (_markFieldButton is not null)
        {
            _markFieldButton.Content = _fieldMarking ? "Cancel marking" : "Mark a field";
            _markFieldButton.IsEnabled = _fieldMarking ||
                _currentMode == RecordingPanelMode.Active && !_autoPassActive && !_recordingControlsLocked;
            AutomationProperties.SetName(_markFieldButton, _fieldMarking ? "Cancel field marking" : "Mark a field");
        }
        if (_fieldMarkHint is not null) _fieldMarkHint.Visibility = _fieldMarking ? Visibility.Visible : Visibility.Collapsed;
        if (_reviewFieldsButton is not null) _reviewFieldsButton.IsEnabled = fields.Count != 0 &&
            _currentMode == RecordingPanelMode.Active && !_autoPassActive && !_recordingControlsLocked;
        _fieldRows.Children.Clear();
        var shown = fields.Where(field => field.IsCurrent || field.State == FieldObservationState.ChangeObserved || field.IsManual)
            .OrderByDescending(field => field.State == FieldObservationState.ChangeObserved)
            .ThenByDescending(field => field.IsCurrent).Take(30).ToArray();
        if (shown.Length == 0)
            _fieldRows.Children.Add(FieldText("Edit fields as usual. Mark one if we missed it.", 10, Muted));
        foreach (var field in shown) _fieldRows.Children.Add(BuildFieldSummaryRow(field));
        if (_fieldSnapshot.Truncated) _fieldRows.Children.Add(FieldText("Showing a limited set of fields.", 10, Muted));
    }

    private FrameworkElement BuildFieldSummaryRow(FieldObservation field)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(FieldSize(25)) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var symbol = FieldText(FieldObservationPresentation.Symbol(field.Kind), 13, Ink);
        symbol.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRowSpan(symbol, 2);
        grid.Children.Add(symbol);
        var name = FieldText(field.Label, 11, Ink);
        name.FontWeight = FontWeights.SemiBold;
        name.TextWrapping = TextWrapping.NoWrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(name, 1); grid.Children.Add(name);
        var status = FieldText(FieldObservationPresentation.Status(field), 10,
            field.State == FieldObservationState.ChangeObserved ? Accent : Muted);
        status.Margin = new Thickness(FieldSize(4), 0, 0, 0);
        Grid.SetColumn(status, 2); grid.Children.Add(status);
        if (field.State == FieldObservationState.ChangeObserved)
        {
            var value = FieldText(FieldObservationPresentation.ChangeText(field), 10, Muted);
            value.TextWrapping = TextWrapping.NoWrap;
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(value, 1); Grid.SetColumnSpan(value, 2); Grid.SetRow(value, 1);
            grid.Children.Add(value);
        }
        return new Border
        {
            Child = grid, Padding = new Thickness(FieldSize(4)), CornerRadius = new CornerRadius(FieldSize(4)),
            Background = field.State == FieldObservationState.ChangeObserved ? Brush("#EAF4FF") : Brushes.Transparent,
            ToolTip = $"{field.Label} · {FieldObservationPresentation.Kind(field.Kind)}\n{FieldObservationPresentation.ChangeText(field)}"
        };
    }

    private Border BuildFieldReviewShell()
    {
        var grid = new Grid { Width = RecorderShellContentWidth, Height = ShellContentHeight(RecorderCardHeight) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(BottomNavHeight) });
        var wave = BuildDecorativeWavePanel();
        wave.Margin = new Thickness(-IdlePopupUnderlaySideBleed, 0, -IdlePopupUnderlaySideBleed, 0);
        Grid.SetRow(wave, 1); Grid.SetRowSpan(wave, 2); grid.Children.Add(wave);

        var header = new StackPanel { Margin = new Thickness(FieldSize(10), FieldSize(22), FieldSize(10), FieldSize(8)) };
        var title = new DockPanel();
        var back = FieldLink("‹", CloseFieldReview, "AtlasRecorderFieldReviewBack");
        back.FontSize = FieldSize(23); back.Margin = new Thickness(0, 0, FieldSize(8), 0);
        title.Children.Add(back);
        var titleText = FieldText("Field changes", 19, Ink); titleText.FontWeight = FontWeights.SemiBold;
        title.Children.Add(titleText); header.Children.Add(title);
        _fieldReviewSubtitle = FieldText("Recording complete", 11, Muted); header.Children.Add(_fieldReviewSubtitle);
        _fieldReviewHeading = FieldText("Observed field changes", 17, Accent);
        _fieldReviewHeading.FontWeight = FontWeights.SemiBold;
        _fieldReviewHeading.Margin = new Thickness(0, FieldSize(4), 0, FieldSize(3));
        header.Children.Add(_fieldReviewHeading);
        header.Children.Add(FieldText("Review what changed while you worked.", 11, Muted));
        grid.Children.Add(header);

        _fieldReviewRows = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = _fieldReviewRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(FieldSize(10), 0, FieldSize(5), FieldSize(8))
        };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(FieldSize(10), 0, FieldSize(10), FieldSize(12)) };
        footer.Children.Add(new Border
        {
            Padding = new Thickness(FieldSize(8)), Background = Brush("#EAF4FF"),
            BorderBrush = Brush("#B9D9FF"), BorderThickness = new Thickness(FieldSize(1)), CornerRadius = new CornerRadius(FieldSize(6)),
            Child = FieldText("Keep these observations with the map. Finding a field again still needs a separate check.", 10, Ink)
        });
        var actions = new Grid { Margin = new Thickness(0, FieldSize(8), 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
        _keepFieldsButton = FieldButton("Keep fields", KeepReviewedFields, true, "AtlasRecorderKeepFields");
        _keepFieldsButton.Margin = new Thickness(0, 0, FieldSize(8), 0);
        actions.Children.Add(_keepFieldsButton);
        var later = FieldButton("Later", CloseFieldReview, false, "AtlasRecorderFieldsLater");
        Grid.SetColumn(later, 1); actions.Children.Add(later); footer.Children.Add(actions);
        Grid.SetRow(footer, 2); grid.Children.Add(footer);

        var windows = BottomNavButton(CreateWindowTargetIcon, "Windows", () => ReturnFromFieldReview(RecorderView.Windows), "Back to recorder windows");
        var maps = BottomNavButton(CreateMapIcon, "Maps", () => ReturnFromFieldReview(RecorderView.Maps), "Back to recorded maps");
        var export = BottomNavButton(CreateExportIcon, "Export", () => ReturnFromFieldReview(RecorderView.Library), "Back to export options");
        var navigation = BuildBottomNavigation(windows, maps, export);
        Grid.SetRow(navigation, 3); grid.Children.Add(navigation);
        return BuildRecorderShell(grid, RecorderFrameHeight, RecorderCardHeight, "Compact panel");
    }

    private void ReturnFromFieldReview(RecorderView view)
    {
        CloseFieldReview();
        if (_currentMode is RecordingPanelMode.PreStart or RecordingPanelMode.MapReady) SetCurrentView(view);
    }

    private void RefreshFieldReview()
    {
        if (_fieldReviewRows is null) return;
        var fields = _fieldSnapshot.Fields;
        var changed = fields.Count(field => field.State == FieldObservationState.ChangeObserved);
        if (_fieldReviewSubtitle is not null) _fieldReviewSubtitle.Text =
            !_fieldReviewRecordingComplete && _currentMode is RecordingPanelMode.Active or RecordingPanelMode.Paused
                ? "Recording paused for review" : "Recording complete";
        if (_fieldReviewHeading is not null) _fieldReviewHeading.Text = changed == 0 ? "No changes observed yet" :
            $"{changed} {(changed == 1 ? "change" : "changes")} observed";
        _fieldReviewRows.Children.Clear();
        foreach (var field in fields.OrderByDescending(field => field.State == FieldObservationState.ChangeObserved)
                     .ThenByDescending(field => field.IsManual))
        {
            if (!_fieldReviewSelection.ContainsKey(field.Id))
                _fieldReviewSelection[field.Id] = FieldObservationPresentation.InitiallySelected(field);
            _fieldReviewRows.Children.Add(BuildFieldReviewRow(field));
        }
        if (fields.Count == 0) _fieldReviewRows.Children.Add(FieldText("No field observations in this recording.", 12, Muted));
        if (_fieldSnapshot.Truncated) _fieldReviewRows.Children.Add(FieldText("The field observation limit was reached. This is a partial set.", 10, Muted));
        if (_keepFieldsButton is not null) _keepFieldsButton.IsEnabled = fields.Count != 0;
    }

    private FrameworkElement BuildFieldReviewRow(FieldObservation field)
    {
        var content = new StackPanel();
        var title = new TextBlock
        {
            Text = field.Label, FontSize = FieldSize(12), FontWeight = FontWeights.SemiBold, Foreground = Ink,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
        };
        content.Children.Add(title);
        content.Children.Add(FieldText(FieldObservationPresentation.Kind(field.Kind) + " · " + FieldObservationPresentation.Status(field), 10,
            field.State == FieldObservationState.ChangeObserved ? Accent : Muted));
        content.Children.Add(FieldText(FieldObservationPresentation.ChangeText(field), 11, Ink));
        var details = new StackPanel();
        foreach (var change in field.PropertyChanges)
            details.Children.Add(FieldText(FieldObservationPresentation.ChangeDetail(field, change), 10, Muted));
        if (field.UnchangedProperties.Count > 0)
            details.Children.Add(FieldText("Observed unchanged: " + string.Join(", ", field.UnchangedProperties.Select(FieldObservationPresentation.PropertyName)), 10, Muted));
        details.Children.Add(FieldText("This describes this recording. It does not prove the field will be found next time.", 10, Muted));
        var expander = new Expander
        {
            Header = "What changed?", Content = details, FontSize = FieldSize(10), Foreground = Muted,
            Margin = new Thickness(0, FieldSize(3), 0, 0)
        };
        content.Children.Add(expander);
        var selected = new CheckBox
        {
            IsChecked = _fieldReviewSelection[field.Id], Content = content, FontSize = FieldSize(12),
            VerticalContentAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(FieldSize(5), 0, 0, 0), ToolTip = "Keep this field observation"
        };
        AutomationProperties.SetName(selected, "Keep " + field.Label);
        AutomationProperties.SetAutomationId(selected, "AtlasFieldReview_" + field.Id);
        selected.Checked += (_, _) => _fieldReviewSelection[field.Id] = true;
        selected.Unchecked += (_, _) => _fieldReviewSelection[field.Id] = false;
        return new Border
        {
            Child = selected, Margin = new Thickness(0, 0, FieldSize(5), FieldSize(5)), Padding = new Thickness(FieldSize(8)),
            Background = Brush("#FCFCFD"), BorderBrush = Divider, BorderThickness = new Thickness(FieldSize(1)),
            CornerRadius = new CornerRadius(FieldSize(7))
        };
    }

    private void KeepReviewedFields()
    {
        foreach (var field in _fieldSnapshot.Fields)
        {
            var decision = _fieldReviewSelection.GetValueOrDefault(field.Id) ? FieldReviewDecision.Keep : FieldReviewDecision.Later;
            FieldDecisionChanged?.Invoke(field.Id, decision);
        }
        CloseFieldReview();
    }

    private static double FieldSize(double actualPixels) => actualPixels / RecorderContentScale;

    private static TextBlock FieldText(string text, double fontSize, Brush foreground) => new()
    {
        Text = text, FontSize = FieldSize(fontSize), Foreground = foreground,
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
    };

    private static Button FieldButton(string text, Action click, bool primary, string automationId)
    {
        var button = new Button
        {
            Content = text, FontSize = FieldSize(12), FontWeight = FontWeights.SemiBold,
            Foreground = primary ? Brushes.White : Accent, Background = primary ? Accent : Brushes.White,
            BorderBrush = primary ? Accent : Brush("#C5D1E2"), BorderThickness = new Thickness(FieldSize(1)),
            MinHeight = FieldSize(30), Padding = new Thickness(FieldSize(10), FieldSize(5), FieldSize(10), FieldSize(5)),
            Template = RoundedButtonTemplate(new CornerRadius(FieldSize(6))), Cursor = Cursors.Hand
        };
        AutomationProperties.SetAutomationId(button, automationId); AutomationProperties.SetName(button, text);
        button.Click += (_, _) => click();
        return button;
    }

    private static Button FieldLink(string text, Action click, string automationId)
    {
        var button = new Button
        {
            Content = text, FontSize = FieldSize(11), Foreground = Accent, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0, FieldSize(4), 0, FieldSize(4)),
            HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand
        };
        AutomationProperties.SetAutomationId(button, automationId); AutomationProperties.SetName(button, text);
        button.Click += (_, _) => click();
        return button;
    }
}
