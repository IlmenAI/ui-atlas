using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Automation;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Desktop;

public sealed partial class ExplorerWindow
{
    private string? _parameterReviewMode;
    private MapParameterizationDocument? _parameterDocument;
    private IReadOnlyList<ParameterProposal> _parameterProposals = [];
    private IReadOnlyDictionary<string, EffectiveParameterDecision> _effectiveParameters =
        new Dictionary<string, EffectiveParameterDecision>();
    private ParameterProposal? _selectedParameter;
    private GridVariationRule? _parameterPreviewRule;
    private string? _parameterError;
    private string? _parameterStoragePath;
    private string? _parameterGraphStoragePath;
    private bool _savingParameters;
    private readonly List<TreeViewItem> _parameterNameGroups = [];
    private readonly TextBlock _parameterSummary = new() { VerticalAlignment = VerticalAlignment.Center };

    private UIElement BuildParameterToolbar()
    {
        var bar = new WrapPanel { Margin = new Thickness(20, 0, 20, 8) };
        bar.Children.Add(Text("Data & parameter review", 13, FontWeights.SemiBold, Ink,
            verticalAlignment: VerticalAlignment.Center));
        Add("Find parameters", async () => { await LoadParameterReviewAsync(); EnterParameterReview("Parameters"); });
        Add("Parameters", () => EnterParameterReview("Parameters"));
        Add("Data grids", () => EnterParameterReview("Data grids"));
        Add("Add selected element", AddSelectedParameter);
        Add("Back to map", () => { ExitParameterReview(); RefreshAll(); });
        bar.Children.Add(_parameterSummary);
        return bar;

        void Add(string title, Action action)
        {
            var b = ReviewButton(title); b.Click += (_, _) => action(); bar.Children.Add(b);
        }
    }

    private async Task LoadParameterReviewAsync()
    {
        if (_model is null || _graphPath is null) return;
        var model = _model; var path = _graphPath; var version = _graphLoadVersion;
        _parameterError = null;
        try
        {
            var loaded = await Task.Run(() =>
            {
                var storagePath = MapParameterizationStore.PathFor(path);
                if (_parameterGraphStoragePath is not null && storagePath != _parameterGraphStoragePath)
                    throw new IOException("Map generation changed. Reopen the map before reviewing parameters.");
                var document = MapParameterizationStore.LoadFile(storagePath, model.Graph.Metadata.EffectiveLogicalMapId);
                IReadOnlyList<ParameterGridSource> grids = [];
                if (AtlasMapWorkspace.Exists(path))
                    grids = AtlasParameterizationAdapter.LoadGrids(AtlasMapWorkspace.Open(path), model.Graph);
                var session = System.IO.Path.ChangeExtension(path, ".session.json");
                if (File.Exists(session))
                {
                    var retainedIds = grids.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
                    grids = grids.Concat((LogicalMapSessionStore.Load(session).DataGrids ?? [])
                        .Where(g => !retainedIds.Contains(g.GridId)).Select(g =>
                            new ParameterGridSource(g.GridId, "unbound-grid:" + g.GridId, "", g.DisplayName,
                                g, System.IO.Path.GetFullPath(g.Review.ImagePath, System.IO.Path.GetDirectoryName(session)!), false))).ToArray();
                }
                if (storagePath != MapParameterizationStore.PathFor(path)) throw new IOException("Map changed during review loading. Reload the map.");
                return (Document: document, Proposals: ParameterizationAnalysis.Build(model, grids, document.Proposals), StoragePath: storagePath, Grids: grids);
            });
            if (version != _graphLoadVersion) return;
            _parameterDocument = loaded.Document; _parameterProposals = loaded.Proposals;
            _inspectionGridSources = loaded.Grids;
            _inspectionRenderedGridKey = null;
            _parameterStoragePath = loaded.StoragePath;
            _selectedParameter = _parameterProposals.FirstOrDefault(p => p.Id == _selectedParameter?.Id);
            _parameterSummary.Text = $"{loaded.Proposals.Count(p => !p.IsGrid)} parameters · {loaded.Proposals.Count(p => p.IsGrid)} grids";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            if (version != _graphLoadVersion) return;
            _parameterDocument = null; _parameterProposals = []; _selectedParameter = null;
            _inspectionGridSources = []; _inspectionRenderedGridKey = null;
            _parameterError = ex.Message;
            _parameterSummary.Text = "Review unavailable · " + ex.Message;
        }
        if (_parameterReviewMode is not null) RefreshParameterReview();
        else { RefreshInspectionHierarchyGrids(); RefreshInspectionPreview(); }
    }

    private void EnterParameterReview(string mode)
    {
        if (_model is null) return;
        _parameterReviewMode = mode;
        _inspectionLevel = UiUnderstandingLevel.SemanticWorld;
        _propertiesColumn.Width = new GridLength(370);
        _topologyCanvas.MinWidth = 320;
        foreach (var button in _viewModeButtons) button.Value.IsEnabled = button.Key == "Window";
        _selectedParameter = null; _parameterPreviewRule = null;
        SetLevel(UiUnderstandingLevel.SemanticWorld);
    }

    private void ExitParameterReview()
    {
        _parameterReviewMode = null; _selectedParameter = null; _parameterPreviewRule = null;
        _propertiesColumn.Width = new GridLength(285); _topologyCanvas.MinWidth = 1180;
        _topologyCanvas.VerticalAlignment = VerticalAlignment.Stretch;
        foreach (var button in _viewModeButtons.Values) button.IsEnabled = true;
    }

    private void RefreshParameterReview()
    {
        if (_model is null) return;
        _effectiveParameters = ParameterDecisionReuse.Resolve(_parameterProposals, _parameterDocument?.Decisions ?? []);
        var items = _parameterProposals.Where(p => p.IsGrid == (_parameterReviewMode == "Data grids") &&
            (p.DisplayName.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase) ||
             p.ParameterName.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();
        _synchronizing = true;
        _hierarchy.Items.Clear(); _hierarchyItems.Clear(); _parameterNameGroups.Clear();
        var controls = _model.LayerFor(UiUnderstandingLevel.SemanticWorld).Controls.ToDictionary(c => c.Id);
        foreach (var hostItems in items.GroupBy(p => p.SurfaceId))
        {
            var surface = _model.LayerFor(UiUnderstandingLevel.SemanticWorld).Surfaces.FirstOrDefault(s => s.Id == hostItems.Key);
            var host = Group(surface?.DisplayName ?? "Host unresolved", true);
            _hierarchy.Items.Add(host);
            foreach (var category in hostItems.GroupBy(p => (p.ParameterName, p.PropertyName, p.Scope)))
            {
                var categoryItem = Group($"{ParameterGroupLabel(category.Key.ParameterName)} · {category.Count()}", true);
                categoryItem.ToolTip = category.Key.PropertyName + " · " + category.Key.Scope;
                host.Items.Add(categoryItem);
                foreach (var named in category.GroupBy(p => p.DisplayName, StringComparer.Ordinal)
                             .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var parent = categoryItem;
                    if (named.Count() > 1)
                    {
                        parent = Group($"{named.Key} · {named.Count()} controls", false);
                        parent.Tag = named.First();
                        parent.ToolTip = "Grouped for navigation. Select an individual recorded control to review its rule.";
                        categoryItem.Items.Add(parent);
                        _parameterNameGroups.Add(parent);
                    }
                    var occurrence = 0;
                    foreach (var p in named)
                    {
                        var state = ParameterizationAnalysis.State(p, DecisionFor(p));
                        controls.TryGetValue(p.TargetId, out var control);
                        var frames = control?.Evidence.Select(e => e.FrameSequence).Distinct().Order().ToArray() ?? [];
                        var identity = $"{control?.CanonicalKind ?? (p.IsGrid ? "Mapped grid" : "Unresolved control")} · " +
                            (frames.Length == 0 ? "source unavailable" : "frame " + string.Join(", ", frames.Take(4)) + (frames.Length > 4 ? "…" : ""));
                        var title = named.Count() > 1 ? $"{++occurrence}. {identity}" : $"{p.DisplayName} · {identity}";
                        var label = new StackPanel();
                        label.Children.Add(Text(title, 12, FontWeights.SemiBold, Ink, textWrapping: TextWrapping.Wrap));
                        label.Children.Add(Text($"{StateLabel(state)} · {(p.IsGrid ? GridLabel(RuleFor(p).Mode) : p.PropertyName)}",
                            11, FontWeights.Normal, StateBrush(state), textWrapping: TextWrapping.Wrap));
                        var item = new TreeViewItem { Header = label, Tag = p, Margin = new Thickness(0, 4, 0, 4),
                            ToolTip = $"{p.DisplayName}\n{identity}\nControl: {p.TargetId}" };
                        AutomationProperties.SetName(item, p.DisplayName + " · " + title);
                        parent.Items.Add(item); _hierarchyItems[p.Id] = item;
                        if (p.IsGrid)
                        {
                            item.Items.Add(new TreeViewItem { Header = p.Schema is null ? "No mapped schema" : $"Mapped columns: {p.Schema.Columns.Count}", Tag = p });
                            item.Items.Add(new TreeViewItem { Header = "Rows · variable collection", Tag = p });
                        }
                    }
                }
            }
        }
        _synchronizing = false;
        _topologyTitle.Text = _model.ApplicationDisplayName + " — Data & parameter review";
        _topologyLevel.Text = "Semantic World";
        _topologySummary.Text = $"{items.Length} {(_parameterReviewMode == "Data grids" ? "data regions" : "parameter proposals")} · Authoring knowledge";
        _appMapSummary.Text = "Captured evidence · parameter highlights";
        var next = items.FirstOrDefault(p => p.Id == _selectedParameter?.Id) ?? items.FirstOrDefault();
        if (next is not null) SelectParameterProposal(next);
        else
        {
            _selectedParameter = null; _properties.Children.Clear(); _topologyCanvas.Children.Clear();
            _appMapImage.Source = null; _appMapOverlay.Children.Clear();
            _properties.Children.Add(ReviewText(_parameterError ??
                "No proposals in this view. Find parameters, or return to the map, select an element and use Add selected element."));
        }

        static TreeViewItem Group(string title, bool expanded)
        {
            var item = new TreeViewItem { Header = Text(title, 12, FontWeights.SemiBold, Muted,
                textWrapping: TextWrapping.Wrap), IsExpanded = expanded };
            AutomationProperties.SetName(item, title);
            return item;
        }
    }

    private static string ParameterGroupLabel(string name) => name switch
    {
        "sheet_name" => "Worksheet names",
        "workbook_name" => "Workbook names",
        "document_name" => "Document names",
        "control_value" => "Control values",
        "grid_data" or "table_data" => "Data regions",
        _ => name.Replace('_', ' ')
    };

    private void SelectParameterProposal(ParameterProposal proposal)
    {
        if (_model is null) return;
        _selectedParameter = proposal; _parameterPreviewRule = RuleFor(proposal);
        var layer = _model.LayerFor(UiUnderstandingLevel.SemanticWorld);
        _selectedControl = layer.Controls.FirstOrDefault(c => c.Id == proposal.TargetId);
        _selectedSurface = layer.Surfaces.FirstOrDefault(s => s.Id == proposal.SurfaceId);
        _selectedSurfaceScope = _selectedSurface is null ? [] : [_selectedSurface];
        _inspectionLevel = UiUnderstandingLevel.SemanticWorld;
        _selectedVariant = _selectedControl is not null && _selectedSurface is not null
            ? ParameterReviewPresentation.Variant(_selectedControl, _model.VariantsFor([_selectedSurface], _selectedControl), layer.Controls)
            : _selectedSurface?.Variants.FirstOrDefault();
        SetViewMode("Window");
        _synchronizing = true;
        foreach (var group in _parameterNameGroups)
            group.IsExpanded = group.Items.OfType<TreeViewItem>().Any(item => (item.Tag as ParameterProposal)?.Id == proposal.Id);
        RevealHierarchyItem(proposal.Id);
        _synchronizing = false;
        if (_selectedSurface is not null) BuildVariants(_selectedSurface);
        _resetAppMapZoomOnNextRender = true;
        RenderAppMap();
        DrawParameterGraph();
        ShowParameterProperties();
    }

    private ParameterDecision? DecisionFor(ParameterProposal p) =>
        _effectiveParameters.GetValueOrDefault(p.Id)?.Decision
        ?? _parameterDocument?.Decisions.FirstOrDefault(d => d.ProposalId == p.Id);
    private GridVariationRule RuleFor(ParameterProposal p) => DecisionFor(p)?.GridRule ?? new();
    private static string StateLabel(ParameterReviewState state) => state switch
    {
        ParameterReviewState.NeedsEvidence => "Need evidence",
        _ => state.ToString()
    };
    private static string GridLabel(GridVariationMode mode) =>
        mode == GridVariationMode.FullyVariable ? "Fully variable" : "According to schema";
    private static Brush StateBrush(ParameterReviewState state) => state switch
    {
        ParameterReviewState.Accepted => Brush("#087F78"),
        ParameterReviewState.Denied => Muted,
        ParameterReviewState.NeedsEvidence => Brush("#A56408"),
        _ => Violet
    };
    private static TextBlock ReviewText(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ink, FontSize = 12, Margin = new Thickness(0, 3, 0, 7) };
    private static Button ReviewButton(string title) =>
        new() { Content = title, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(6, 3, 0, 3),
            Background = Brushes.White, BorderBrush = UiBorder, BorderThickness = new Thickness(1), Foreground = Ink,
            Template = RoundedButtonTemplate(6) };

    private void DrawParameterGraph()
    {
        _topologyCanvas.Children.Clear();
        if (_selectedParameter is not { } p) return;
        var state = ParameterizationAnalysis.State(p, DecisionFor(p));
        var width = Math.Max(320, Math.Min(700, _topologyScrollViewer?.ActualWidth ?? 400) - 20);
        var twoColumns = width >= 620;
        var cardWidth = twoColumns ? (width - 45) / 2 : width - 30;
        CardAt("HOST / UI IDENTITY", p.HostBound ? "Recorded host retained\n" + p.Scope : "Host association unresolved\nRetained grid evidence remains inspectable", 0, Blue);
        CardAt(p.IsGrid ? "DATA REGION" : "PARAMETER", (DecisionFor(p)?.ParameterName ?? p.ParameterName) +
            "\n" + StateLabel(state), 1, StateBrush(state));
        CardAt(p.IsGrid && (_parameterPreviewRule ?? RuleFor(p)).Mode == GridVariationMode.AccordingToSchema
                ? "REVIEWED STRUCTURE" : "VARIATION RULE",
            p.IsGrid ? (_parameterPreviewRule ?? RuleFor(p)).Mode == GridVariationMode.FullyVariable
                ? "Contents, data headers and occupied dimensions may change."
                : $"{p.Schema?.Columns.Count ?? 0} physical columns · {(p.Schema?.IsComplete == true ? "complete width" : "partial width")}\nValues and row count follow the reviewed rule."
                : p.PropertyName + " may vary within:\n" + (DecisionFor(p)?.Scope ?? p.Scope), 2, Violet);
        CardAt("EVIDENCE / LIMITS", p.IsGrid ? p.Coverage + "\nTypes and row keys unverified."
            : string.Join("\n", p.Evidence.Select(e => e.Kind).Distinct()), 3, Muted);
        _topologyCanvas.Width = width;
        _topologyCanvas.Height = twoColumns ? 305 : 595;
        _topologyCanvas.VerticalAlignment = VerticalAlignment.Top;
        QueueTopologyZoomBoundsUpdate(resetToFit: true);

        void CardAt(string title, string value, int index, Brush accent)
        {
            var x = twoColumns && index % 2 == 1 ? cardWidth + 30 : 15;
            var y = 15 + (twoColumns ? index / 2 : index) * 145;
            var content = new StackPanel();
            content.Children.Add(Text(title, 11, FontWeights.Bold, accent));
            content.Children.Add(ReviewText(value));
            var card = new Border { Child = content, Width = cardWidth, MinHeight = 130, Padding = new Thickness(12),
                Background = Brushes.White, BorderBrush = accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7) };
            card.MouseLeftButtonUp += (_, _) => SelectParameterProposal(p);
            Canvas.SetLeft(card, x); Canvas.SetTop(card, y); _topologyCanvas.Children.Add(card);
        }
    }

    private void ShowParameterProperties()
    {
        if (_selectedParameter is not { } p) return;
        _properties.Children.Clear();
        var decision = DecisionFor(p); var docs = decision?.Documentation.ToList() ?? [];
        var state = ParameterizationAnalysis.State(p, decision);
        _properties.Children.Add(Section(p.IsGrid ? "Data grid variation" : "Parameter review"));
        _properties.Children.Add(ReviewText($"{StateLabel(state)} · {p.DisplayName}"));
        if (_effectiveParameters.TryGetValue(p.Id, out var effective) && effective.IsInherited)
            _properties.Children.Add(ReviewText("Confirmed through an equivalent control in this map.\n" +
                "Source confirmation: " + effective.SourceProposalId));
        if (p.ReuseKey.Length > 0)
            _properties.Children.Add(ReviewText("This definition also applies to equivalent structural roles in this map. " +
                "Explicit decisions on individual controls take precedence."));
        _properties.Children.Add(ReviewText($"Control: {_selectedControl?.CanonicalKind ?? (p.IsGrid ? "Mapped grid" : "Unresolved")}\nProperty: {p.PropertyName}"));
        if (decision is not null && decision.SourceFingerprint != p.Fingerprint)
            _properties.Children.Add(ReviewText("Source evidence changed. The earlier decision is retained but no longer applies; review again."));
        if (!p.HostBound) _properties.Children.Add(ReviewText("Host unresolved. Accepting this rule does not establish a correspondence to a live grid."));
        var name = Field("Parameter name", decision?.ParameterName ?? p.ParameterName);
        var type = Field("Value type / shape", decision?.ValueType ?? p.ValueType);
        var scope = Field("Scope", decision?.Scope ?? p.Scope);
        _properties.Children.Add(ReviewText("Property: " + p.PropertyName));
        var mode = new ComboBox { ItemsSource = new[] { "Fully variable", "According to schema" },
            SelectedIndex = RuleFor(p).Mode == GridVariationMode.FullyVariable ? 0 : 1, Margin = new Thickness(0, 3, 0, 7) };
        var rowCount = new CheckBox { Content = "Row count may change (including zero)", IsChecked = RuleFor(p).AllowRowCountChanges };
        var rowOrder = new CheckBox { Content = "Row order may change", IsChecked = RuleFor(p).AllowRowOrderChanges };
        var columns = new CheckBox { Content = "Additional / reordered columns allowed", IsChecked = RuleFor(p).AllowColumnChanges };
        if (p.IsGrid)
        {
            _properties.Children.Add(mode);
            var policy = new StackPanel(); policy.Children.Add(rowCount); policy.Children.Add(rowOrder); policy.Children.Add(columns);
            _properties.Children.Add(policy);
            var modeHint = ReviewText(""); _properties.Children.Add(modeHint);
            void UpdatePreview()
            {
                _parameterPreviewRule = ReadRule();
                policy.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
                modeHint.Text = mode.SelectedIndex == 0
                    ? "All contents may vary. Host and grid identity are retained."
                    : p.Schema is null ? "No reviewed schema is attached. Map and review this table's headers before accepting schema mode."
                    : "Reviewed column identities remain required. Missing or changed required columns require renewed review.";
                RenderAppMap(); DrawParameterGraph();
            }
            mode.SelectionChanged += (_, _) => UpdatePreview();
            foreach (var check in new[] { rowCount, rowOrder, columns })
                check.Click += (_, _) => UpdatePreview();
            UpdatePreview();
            _properties.Children.Add(ReviewText(p.Coverage));
            if (p.Schema is { } schema)
            {
                var schemaPanel = new StackPanel();
                schemaPanel.Children.Add(ReviewText($"Revision {schema.Revision}\n{(schema.IsComplete ? "Complete column schema" : "Partial column schema — acceptance does not complete it")}"));
                foreach (var c in schema.Columns.OrderBy(c => c.Ordinal))
                    schemaPanel.Children.Add(ReviewText($"{c.Ordinal}: {(string.IsNullOrEmpty(c.Label) ? "(blank)" : c.Label)}\nKey: {c.ColumnKey} · X {c.StartX}–{c.EndX}"));
                _properties.Children.Add(new Expander { Header = $"Mapped columns ({schema.Columns.Count})", Content = schemaPanel });
                _properties.Children.Add(ReviewText("Header review does not verify value types, unique record keys, status domains or row transcription."));
            }
            var subregion = ReviewButton("Use mapped table as subregion…");
            subregion.Click += (_, _) => AddMappedSubregion(p);
            _properties.Children.Add(subregion);
            if (p.ParentProposalId is not null) _properties.Children.Add(ReviewText("Refines parent region: " + p.ParentProposalId));
        }
        var evidencePanel = new StackPanel();
        foreach (var e in p.Evidence)
        {
            evidencePanel.Children.Add(ReviewText(e.Kind + "\n" + e.Summary + "\n" + e.Source +
                (e.Applicability is null ? "" : "\nApplies to: " + e.Applicability)));
            if (MapParameterizationStore.IsDocumentationUrl(e.Source))
            {
                var link = ReviewButton("Open source"); link.Click += (_, _) => OpenParameterSource(e.Source);
                evidencePanel.Children.Add(link);
            }
        }
        var docList = new StackPanel();
        void RefreshDocs()
        {
            docList.Children.Clear();
            foreach (var d in docs.ToArray())
            {
                docList.Children.Add(ReviewText(d.Title + "\n" + d.Excerpt + "\nApplies to: " + d.Applicability));
                var actions = new WrapPanel();
                var open = ReviewButton("Open source"); open.Click += (_, _) => OpenParameterSource(d.Url);
                var remove = ReviewButton("Remove"); remove.Click += (_, _) => { docs.Remove(d); RefreshDocs(); };
                actions.Children.Add(open); actions.Children.Add(remove); docList.Children.Add(actions);
            }
        }
        RefreshDocs(); evidencePanel.Children.Add(docList);
        var attach = ReviewButton("Attach documentation…");
        attach.Click += (_, _) => { var d = PromptDocumentation(); if (d is not null) { docs.Add(d); RefreshDocs(); } };
        evidencePanel.Children.Add(attach);
        _properties.Children.Add(new Expander { Header = "Evidence & documentation", IsExpanded = true, Content = evidencePanel });
        var note = Field("Review note", decision?.Note ?? "");
        var actionsBar = new WrapPanel();
        foreach (var action in new[] { ParameterReviewState.Accepted, ParameterReviewState.Denied, ParameterReviewState.NeedsEvidence })
        {
            var button = ReviewButton(action == ParameterReviewState.Accepted ? "Accept" :
                action == ParameterReviewState.Denied ? "Deny" : "Need evidence");
            button.IsEnabled = !_savingParameters && _parameterDocument is not null;
            button.Click += async (_, _) => await SaveParameterDecisionAsync(p, new(p.Id, p.Fingerprint, action,
                name.Text.Trim(), type.Text.Trim(), scope.Text.Trim(), p.IsGrid ? ReadRule() : null,
                docs.ToArray(), Environment.UserName, DateTimeOffset.UtcNow, note.Text));
            actionsBar.Children.Add(button);
        }
        _properties.Children.Insert(2, actionsBar);
        _properties.Children.Add(ReviewText(decision is null ? "Save a decision to retain these edits." :
            $"Reviewed by {decision.Reviewer} · {decision.ReviewedUtc.LocalDateTime:g}"));
        _properties.Children.Add(ReviewText("Authoring knowledge only. A denied proposal does not prove a constant. Execution and target resolution require separate validation."));
        if (_parameterError is not null) _properties.Children.Add(ReviewText("Could not save: " + _parameterError));

        TextBox Field(string label, string value)
        {
            _properties.Children.Add(ReviewText(label));
            var field = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 7), Padding = new Thickness(5),
                MaxLength = label == "Parameter name" ? 128 : 2048 };
            _properties.Children.Add(field); return field;
        }
        GridVariationRule ReadRule() => mode.SelectedIndex == 0 ? new() : new(GridVariationMode.AccordingToSchema,
            rowCount.IsChecked == true, rowOrder.IsChecked == true, columns.IsChecked == true);
    }

    private async Task SaveParameterDecisionAsync(ParameterProposal proposal, ParameterDecision decision)
    {
        if (_parameterDocument is null || _graphPath is null || _savingParameters) return;
        _savingParameters = true; _parameterError = null;
        var path = _graphPath; var version = _graphLoadVersion;
        var document = _parameterDocument with { Proposals = _parameterProposals,
            Decisions = _parameterDocument.Decisions.Where(d => d.ProposalId != proposal.Id).Append(decision).ToArray() };
        try
        {
            var storagePath = _parameterStoragePath;
            var saved = await Task.Run(() => MapParameterizationStore.Save(path, document, document.Revision, storagePath));
            if (version != _graphLoadVersion) return;
            _parameterDocument = saved;
            _status.Text = $"{StateLabel(decision.State)} saved · {decision.ParameterName} · revision {saved.Revision}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            if (version != _graphLoadVersion) return;
            _parameterError = ex.Message; _status.Text = "Parameter review was not saved: " + ex.Message;
        }
        finally
        {
            _savingParameters = false;
            if (version == _graphLoadVersion) RefreshParameterReview();
        }
    }

    private void AddSelectedParameter()
    {
        if (_parameterDocument is null || _model is null) { _status.Text = "Open a valid map first."; return; }
        var node = _selectedControl?.Source ?? _selectedSurface?.Source;
        if (node is null) { _status.Text = "Select an element in the map first."; return; }
        var property = PromptChoice("Add parameter proposal", "Choose the variable property of the selected element.",
            new[] { "Name", "Value", "AutomationId" });
        if (property is null) return;
        var p = ParameterizationAnalysis.Create(node, _selectedSurface?.Id ?? "",
            _selectedControl?.Bounds ?? _selectedSurface!.Bounds, property, "element_" + property.ToLowerInvariant(),
            "Text", "This element within its recorded host", false,
            [new("Mapper proposal", "Manually proposed; variation requires review.", "Selected KG element: " + node.Id)], true);
        _parameterProposals = _parameterProposals.Where(v => v.Id != p.Id).Append(p).ToArray();
        EnterParameterReview("Parameters"); SelectParameterProposal(p);
        _status.Text = "Proposal added locally. Save a review decision to retain it.";
    }

    private void AddMappedSubregion(ParameterProposal parent)
    {
        var choices = _parameterProposals.Where(p => p.IsGrid && p.Id != parent.Id && p.ParentProposalId is null &&
            p.Schema is not null && p.HostBound && parent.HostBound && p.SurfaceId == parent.SurfaceId &&
            p.Bounds.X >= parent.Bounds.X && p.Bounds.Y >= parent.Bounds.Y &&
            (long)p.Bounds.X + p.Bounds.Width <= (long)parent.Bounds.X + parent.Bounds.Width &&
            (long)p.Bounds.Y + p.Bounds.Height <= (long)parent.Bounds.Y + parent.Bounds.Height).ToArray();
        if (choices.Length == 0)
        {
            _status.Text = "No reviewed table lies within this grid. Map and review the inner table first; its exact bounds and schema are required.";
            MessageBox.Show(this, _status.Text, "Mapped subregion", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var titles = choices.Select(p => p.DisplayName + " · " + p.Id).ToArray();
        var selected = PromptChoice("Use mapped table as subregion", "Reuse an explicitly mapped table inside this region.", titles);
        if (selected is null) return;
        var source = choices[Array.IndexOf(titles, selected)];
        var proposal = source with { Id = "subregion:" + ParameterizationAnalysis.Fingerprint(parent.Id, source.Id)[..24],
            ParentProposalId = parent.Id, IsManual = true,
            Fingerprint = ParameterizationAnalysis.Fingerprint(parent.Fingerprint, source.Fingerprint),
            Evidence = source.Evidence.Append(new ParameterEvidence("Mapper association", "Explicit subregion of " + parent.DisplayName, source.Id)).ToArray() };
        _parameterProposals = _parameterProposals.Where(p => p.Id != proposal.Id).Append(proposal).ToArray();
        RefreshParameterReview(); SelectParameterProposal(proposal);
        _status.Text = "Subregion uses its mapped schema and bounds. Choose According to schema, then save the review.";
    }

    private string? PromptChoice(string title, string description, string[] choices)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(ReviewText(description));
        var select = new ComboBox { ItemsSource = choices, SelectedIndex = 0, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(select);
        var dialog = new Window { Title = title, Owner = this, Width = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        var ok = ReviewButton("Use selection"); ok.IsDefault = true; ok.Click += (_, _) => dialog.DialogResult = true;
        panel.Children.Add(ok);
        return dialog.ShowDialog() == true ? select.SelectedItem as string : null;
    }

    private ParameterDocumentation? PromptDocumentation()
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        var fields = new List<TextBox>();
        foreach (var label in new[] { "Source title", "HTTP(S) URL", "Supporting excerpt", "Product / version applicability" })
        {
            panel.Children.Add(ReviewText(label));
            var input = new TextBox { Padding = new Thickness(6), MaxLength = 2048 };
            fields.Add(input); panel.Children.Add(input);
        }
        var error = ReviewText(""); panel.Children.Add(error);
        var dialog = new Window { Owner = this, Title = "Documentation evidence", Width = 560,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        var save = ReviewButton("Attach source"); save.IsDefault = true;
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(fields[0].Text) || !MapParameterizationStore.IsDocumentationUrl(fields[1].Text.Trim()))
            { error.Text = "Enter a source title and a valid HTTP(S) URL without credentials."; return; }
            dialog.DialogResult = true;
        };
        panel.Children.Add(save);
        return dialog.ShowDialog() == true ? new(fields[0].Text.Trim(), fields[1].Text.Trim(), fields[2].Text, fields[3].Text) : null;
    }

    private void OpenParameterSource(string url)
    {
        if (!MapParameterizationStore.IsDocumentationUrl(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { _status.Text = "Could not open source: " + ex.Message; }
    }

    private bool RenderRetainedParameterGrid()
    {
        if (_selectedParameter is not { IsGrid: true, ImagePath: not null } p) return false;
        _appMapOverlay.Children.Clear(); _appMapImage.Source = null; _traceBannerHost.Visibility = Visibility.Collapsed;
        try
        {
            using var stream = new MemoryStream(AtlasParameterizationAdapter.ReadGridImage(p));
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            _appMapImage.Source = bitmap; _appMapImage.Visibility = Visibility.Visible; _appMapImage.Opacity = 1;
            _appMapViewport.Width = _appMapOverlay.Width = bitmap.PixelWidth;
            _appMapViewport.Height = _appMapOverlay.Height = bitmap.PixelHeight;
            _renderedViewportBounds = new(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
            var schema = (_parameterPreviewRule ?? RuleFor(p)).Mode == GridVariationMode.AccordingToSchema;
            var regionColor = ParameterizationAnalysis.State(p, DecisionFor(p)) == ParameterReviewState.Denied ? Muted : Violet;
            var header = p.HeaderBounds;
            if (schema && header is { Width: > 0, Height: > 0 })
            {
                ParameterRectangle(header, Brush("#087F78"), "Reviewed schema header", p);
                var y = Math.Clamp(header.Y + header.Height, 0, bitmap.PixelHeight);
                ParameterRectangle(new(0, y, bitmap.PixelWidth, bitmap.PixelHeight - y), regionColor, "Variable rows · values unverified", p);
            }
            else ParameterRectangle(new(0, 0, bitmap.PixelWidth, bitmap.PixelHeight), regionColor, "Fully variable contents", p);
            _appMapSummary.Text = $"Retained grid image · {GridLabel(schema ? GridVariationMode.AccordingToSchema : GridVariationMode.FullyVariable)} · {(p.HostBound ? "host bound" : "host unresolved")}";
            QueueAppMapZoomBoundsUpdate(resetToFit: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            _appMapViewport.Width = _appMapOverlay.Width = 700; _appMapViewport.Height = _appMapOverlay.Height = 260;
            var error = ReviewText("Retained grid image unavailable: " + ex.Message);
            error.Width = 640; Canvas.SetLeft(error, 20); Canvas.SetTop(error, 20); _appMapOverlay.Children.Add(error);
        }
        return true;
    }

    private void RenderParameterOverlays()
    {
        if (_parameterReviewMode is null || _selectedParameter is not { } p || _renderedViewportBounds is not { } viewport) return;
        var peers = _model?.LayerFor(UiUnderstandingLevel.SemanticWorld).Controls;
        FrameObservation? frame = null;
        if (_selectedVariant is not null && _evidence?.SessionIds.Contains(_selectedVariant.BundleId) == true)
        {
            try { frame = _evidence.ReadObservation(_selectedVariant.BundleId, _selectedVariant.FrameSequence); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { }
        }
        var regions = _appMapImage.Source is null ? [] : ParameterReviewPresentation.Regions(_selectedControl, _selectedVariant, peers, _model, frame);
        var localRegions = regions.Select(r => UiMapPresentation.ProjectToSurface(r, viewport)).OfType<RectI>().ToArray();
        var state = ParameterizationAnalysis.State(p, DecisionFor(p));
        foreach (var r in localRegions)
            ParameterRectangle(r, StateBrush(state), $"{p.DisplayName} · {p.PropertyName} · {StateLabel(state)}", p);
        var banner = new StackPanel();
        banner.Children.Add(ReviewText($"{p.DisplayName} · {_selectedControl?.CanonicalKind ?? "Control"} · {p.PropertyName}"));
        var duplicateOwner = _selectedControl is not null && _selectedVariant is not null &&
            ParameterReviewPresentation.HasMoreSpecificOwner(_selectedControl, _selectedVariant, peers);
        var message = duplicateOwner
            ? "This control was also recorded in its own child window. Select it under that surface; no box is drawn on the parent window."
            : localRegions.Length == 0 ? "Exact control location unavailable in this observation. No substitute container is highlighted."
            : $"Selected control outlined · frame {_selectedVariant?.FrameSequence} · {StateLabel(state)}";
        banner.Children.Add(ReviewText(message));
        _appMapSummary.Text = message;
        if (localRegions.Length > 0)
        {
            var focusBounds = new RectI(localRegions.Min(r => r.X), localRegions.Min(r => r.Y),
                localRegions.Max(r => r.X + r.Width) - localRegions.Min(r => r.X),
                localRegions.Max(r => r.Y + r.Height) - localRegions.Min(r => r.Y));
            var actions = new WrapPanel();
            var focus = ReviewButton("Focus selected control");
            focus.Click += (_, _) => FocusParameterBounds(focusBounds);
            var all = ReviewButton("Show full surface");
            all.Click += (_, _) => UpdateAppMapZoomBounds(resetToFit: true);
            actions.Children.Add(focus); actions.Children.Add(all); banner.Children.Add(actions);
            var variant = _selectedVariant;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (_parameterReviewMode is not null && _selectedParameter?.Id == p.Id && _selectedVariant == variant)
                    FocusParameterBounds(focusBounds);
            }));
        }
        _traceBannerHost.Content = banner; _traceBannerHost.Visibility = Visibility.Visible;
    }

    private void FocusParameterBounds(RectI bounds)
    {
        if (_appMapScrollViewer is not { } scroll) return;
        UpdateAppMapZoomBounds(resetToFit: false);
        var zoom = Math.Clamp(Math.Min(scroll.ViewportWidth / (bounds.Width + 140),
            scroll.ViewportHeight / (bounds.Height + 140)), _appMapMinZoom, 2);
        ApplyAppMapZoom(zoom); _appMapZoomPinnedToFit = false;
        scroll.UpdateLayout();
        scroll.ScrollToHorizontalOffset(Math.Max(0, (bounds.X + bounds.Width / 2d) * zoom - scroll.ViewportWidth / 2));
        scroll.ScrollToVerticalOffset(Math.Max(0, (bounds.Y + bounds.Height / 2d) * zoom - scroll.ViewportHeight / 2));
    }

    private void ParameterRectangle(RectI bounds, Brush color, string label, ParameterProposal proposal)
    {
        if (!ControlGeometry.IsRenderable(bounds)) return;
        var fill = color.Clone(); fill.Opacity = proposal.IsGrid ? .10 : .025;
        var rectangle = new Rectangle { Width = bounds.Width, Height = bounds.Height, Stroke = color,
            StrokeThickness = 3, Fill = fill, ToolTip = label, Tag = proposal.Id,
            Cursor = System.Windows.Input.Cursors.Hand };
        rectangle.MouseLeftButtonUp += (_, e) => { e.Handled = true; SelectParameterProposal(proposal); };
        Canvas.SetLeft(rectangle, bounds.X); Canvas.SetTop(rectangle, bounds.Y); _appMapOverlay.Children.Add(rectangle);
    }
}
