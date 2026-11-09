using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording;

/// <summary>
/// Learns from observations only. It never writes to an application, treats coordinates as
/// identity, or turns a property difference into a matcher exclusion.
/// </summary>
public sealed class FieldObservationTracker
{
    private const int MaxFields = 512;
    private const int MaxChangesPerField = 64;
    private const int MaxPreviewLength = 2_048;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _fields = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastObservedUtc;
    private long _lastSequence;
    private bool _truncated;

    public FieldLearningSnapshot Observe(FrameObservation frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            ObserveCore(frame);
            return SnapshotCore();
        }
    }

    /// <summary>The point selects an observed control; it is never saved as its identity.</summary>
    public FieldObservation? MarkAtPoint(FrameObservation frame, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (_lastObservedUtc > frame.TimestampUtc) return null;
            ObserveCore(frame);
            var context = new ObservationContext(frame);
            var atPoint = context.Controls.Where(sample => Eligible(sample.Control) &&
                context.IsVisible(sample) && Contains(sample.Control.Bounds, x, y)).ToArray();
            var sample = atPoint.Where(sample => KindOf(sample.Control) is not null)
                .OrderBy(sample => Area(sample.Control.Bounds)).FirstOrDefault()
                ?? atPoint.Where(sample => sample.Control.ControlType.Split('.').Last() is not
                    ("Window" or "Pane" or "Group" or "ToolBar" or "MenuBar" or "TitleBar" or "StatusBar"))
                    .OrderBy(sample => Area(sample.Control.Bounds)).FirstOrDefault();
            if (sample is null) return null;

            var identity = context.Identity(sample);
            if (!_fields.TryGetValue(identity.Key, out var entry))
            {
                if (_fields.Count >= MaxFields) { _truncated = true; return null; }
                entry = new(identity, sample, KindOf(sample.Control) ?? FieldKind.Other, frame);
                _fields.Add(identity.Key, entry);
                ObserveEntry(entry, sample, frame);
            }
            entry.IsManual = true;
            return entry.Snapshot();
        }
    }

    public bool SetDecision(string id, FieldReviewDecision decision)
    {
        if (!Enum.IsDefined(decision)) return false;
        lock (_gate)
        {
            var entry = _fields.Values.FirstOrDefault(field => field.Id == id);
            if (entry is null) return false;
            entry.Decision = decision;
            return true;
        }
    }

    public bool Rename(string id, string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        lock (_gate)
        {
            var entry = _fields.Values.FirstOrDefault(field => field.Id == id);
            if (entry is null) return false;
            entry.Label = Preview(label.Trim(), 128);
            return true;
        }
    }

    public FieldLearningSnapshot Snapshot()
    {
        lock (_gate) return SnapshotCore();
    }

    private void ObserveCore(FrameObservation frame)
    {
        // Passive observations legitimately have Sequence=0. Their acquisition time distinguishes them.
        if (_lastObservedUtc > frame.TimestampUtc ||
            (_lastObservedUtc == frame.TimestampUtc && _lastSequence == frame.Sequence)) return;
        _lastObservedUtc = frame.TimestampUtc;
        _lastSequence = frame.Sequence;
        foreach (var field in _fields.Values) field.IsCurrent = false;
        var context = new ObservationContext(frame);
        var groups = context.Controls.Where(context.IsVisible)
            .Select(sample => (Sample: sample, Identity: context.Identity(sample)))
            .GroupBy(item => item.Identity.Key, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var first = group.First();
            var control = first.Sample.Control;
            if (group.Any(item => Protected(item.Sample.Control)))
            {
                // A later password classification must not leave this field in the learning report.
                _fields.Remove(group.Key);
                continue;
            }
            if (!Eligible(control) || group.Any(item => !Equivalent(control, item.Sample.Control))) continue;
            var kind = KindOf(control);
            if (!_fields.TryGetValue(group.Key, out var entry))
            {
                if (kind is null) continue;
                if (_fields.Count >= MaxFields) { _truncated = true; continue; }
                entry = new(first.Identity, first.Sample, kind.Value, frame);
                _fields.Add(group.Key, entry);
            }
            else if (kind is null && !entry.IsManual) continue;
            ObserveEntry(entry, first.Sample, frame);
        }

        if (frame.RawStreams is { Count: > 0 })
        {
            var coverage = frame.RawStreams.Where(stream => stream.Status == "ok" && !stream.Truncated && stream.TargetWindowHwnd is > 0)
                .Select(stream => (Window: stream.TargetWindowHwnd!.Value, Provider: stream.Source)).ToHashSet();
            foreach (var entry in _fields.Values.Where(entry => !entry.IsCurrent && coverage.Contains((entry.WindowHwnd, entry.ProviderId))))
                entry.ComparisonInterrupted = true;
        }
        else if (!frame.AutomationTimedOut && frame.AutomationStatus == "ok")
        {
            var fullyObservedWindows = new HashSet<long>((frame.ScopedWindows ?? []).Select(window => window.Hwnd))
                { frame.Window.Hwnd };
            fullyObservedWindows.UnionWith(frame.ObservedWindowHwnds ?? []);
            foreach (var entry in _fields.Values.Where(entry => !entry.IsCurrent && fullyObservedWindows.Contains(entry.WindowHwnd)))
                entry.ComparisonInterrupted = true;
        }

        // Ambiguous observation-only suggestions are replaced by the next observation, not accumulated
        // or quietly correlated by their screen location. Explicit user choices remain reviewable.
        foreach (var key in _fields.Where(pair => !pair.Value.IsCurrent && !pair.Value.StableIdentity &&
                     !pair.Value.IsManual && pair.Value.Decision == FieldReviewDecision.Undecided)
                 .Select(pair => pair.Key).ToArray())
            _fields.Remove(key);
    }

    private void ObserveEntry(Entry entry, Sample sample, FrameObservation frame)
    {
        var control = sample.Control;
        if (entry.ComparisonInterrupted)
        {
            // Runtime IDs can be recycled after a control disappears. Retain earlier evidence,
            // but do not compare a new instance with the old instance's last value.
            entry.Previous.Clear();
            entry.UnchangedProperties.Clear();
            entry.BeforeValue = null;
            entry.DisplayProperty = null;
            entry.ComparisonInterrupted = false;
        }
        entry.Bounds = control.Bounds;
        entry.ControlRuntimeId = control.RuntimeId;
        entry.IsCurrent = true;
        entry.LastFrameSequence = frame.Sequence;
        entry.LastObservedUtc = frame.TimestampUtc;
        entry.ValueAvailable = false;
        entry.AfterValue = null;

        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ControlType"] = control.ControlType,
            ["ClassName"] = control.ClassName,
            ["Bounds"] = FormattableString.Invariant($"{control.Bounds.X},{control.Bounds.Y},{control.Bounds.Width},{control.Bounds.Height}"),
            ["IsEnabled"] = control.IsEnabled ? "true" : "false"
        };
        // Missing password metadata is not permission to retain a potentially sensitive value/name.
        if (control.IsPassword == false)
        {
            properties["AutomationId"] = control.AutomationId;
            if (control.Name != "[redacted]") properties["Name"] = control.Name;
            if (control.ValueAvailable && control.Value is not null) properties["Value"] = control.Value;
            if (control.SelectionAvailable && control.SelectionValue is not null) properties["Selection"] = control.SelectionValue;
            if (control.ToggleState is not null) properties["ToggleState"] = control.ToggleState;
            var content = Content(control, entry.Kind);
            if (content is { } observed)
            {
                entry.ValueAvailable = true;
                if (entry.DisplayProperty != observed.Property) entry.BeforeValue = null;
                entry.DisplayProperty = observed.Property;
                entry.BeforeValue ??= Preview(observed.Value);
                entry.AfterValue = Preview(observed.Value);
            }
        }

        foreach (var (property, value) in properties)
        {
            var evidence = new PropertySample(Hash(value), Preview(value), frame.Sequence, frame.TimestampUtc);
            if (entry.Previous.TryGetValue(property, out var previous) && entry.StableIdentity)
            {
                if (previous.Hash == evidence.Hash)
                {
                    if (!entry.ChangedProperties.Contains(property)) entry.UnchangedProperties.Add(property);
                }
                else
                {
                    entry.ChangedProperties.Add(property);
                    entry.UnchangedProperties.Remove(property);
                    if (property is "Value" or "Selection" or "ToggleState")
                        entry.State = FieldObservationState.ChangeObserved;
                    if (entry.Changes.Count < MaxChangesPerField)
                        entry.Changes.Add(new(property, previous.Preview, evidence.Preview,
                            previous.Sequence, evidence.Sequence, previous.TimestampUtc, evidence.TimestampUtc));
                    else _truncated = true;
                }
            }
            // Unchanged samples may be deduplicated in the retained evidence. Keep the first
            // sample for this value so a later difference always points to retained provenance.
            if (!entry.Previous.TryGetValue(property, out var prior) || prior.Hash != evidence.Hash)
                entry.Previous[property] = evidence;
        }
    }

    private FieldLearningSnapshot SnapshotCore() => new(FieldLearningSnapshot.CurrentFormat,
        _fields.Values.OrderByDescending(field => field.IsCurrent)
            .ThenBy(field => field.FirstObservedUtc).ThenBy(field => field.Id, StringComparer.Ordinal)
            .Select(field => field.Snapshot()).ToArray(), _truncated);

    private static (string Property, string Value)? Content(AutomationObservation control, FieldKind kind)
    {
        if (kind == FieldKind.Toggle && control.ToggleState is not null) return ("ToggleState", control.ToggleState);
        if (kind is FieldKind.Selection or FieldKind.Toggle && control.SelectionAvailable && control.SelectionValue is not null)
            return ("Selection", control.SelectionValue);
        if (control.ValueAvailable && control.Value is not null) return ("Value", control.Value);
        if (control.SelectionAvailable && control.SelectionValue is not null) return ("Selection", control.SelectionValue);
        if (control.ToggleState is not null) return ("ToggleState", control.ToggleState);
        return null;
    }

    private static bool Eligible(AutomationObservation control) => !Protected(control) && control.IsReadOnly != true;

    private static bool Protected(AutomationObservation control) => control.IsPassword == true ||
        Regex.IsMatch(control.Name + " " + control.AutomationId,
            @"(?:password|passwd|passphrase|(?:api|access|secret)[\s_.-]*(?:key|token)|(?:^|[\s_.-])(?:cvv|cvc|pin)(?:$|[\s_.-]))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static FieldKind? KindOf(AutomationObservation control)
    {
        var type = control.ControlType.Split('.').Last();
        bool Has(string pattern) => control.SupportedPatterns?.Any(value =>
            value.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
            value.Equals(pattern + "Pattern", StringComparison.OrdinalIgnoreCase) ||
            value.Equals(pattern + "PatternIdentifiers.Pattern", StringComparison.OrdinalIgnoreCase)) == true;

        if (type is "CheckBox" or "RadioButton" || Has("Toggle")) return FieldKind.Toggle;
        if (type is "Calendar" or "DatePicker" or "DateTimePicker" ||
            control.ClassName.Contains("DateTimePick", StringComparison.OrdinalIgnoreCase)) return FieldKind.Date;
        if (type is "Spinner" or "Slider" || Has("RangeValue")) return FieldKind.Number;
        // SelectionItem describes a selectable row or navigation item, not an editable field.
        // Keep actual value/toggle capabilities below/above, and suggest selection inputs as a whole.
        if (type is "ComboBox" or "List" ||
            (Has("Selection") && type is not ("Tab" or "Tree" or "DataGrid" or "Table"))) return FieldKind.Selection;
        if (type is "Edit" or "Document" || Has("Value") || Has("Text")) return FieldKind.Text;
        return null;
    }

    private static bool Equivalent(AutomationObservation left, AutomationObservation right) =>
        left.ControlType == right.ControlType && left.ClassName == right.ClassName &&
        left.AutomationId == right.AutomationId && left.Name == right.Name &&
        left.IsPassword == right.IsPassword && left.IsReadOnly == right.IsReadOnly &&
        left.ValueAvailable == right.ValueAvailable && left.Value == right.Value &&
        left.SelectionAvailable == right.SelectionAvailable && left.SelectionValue == right.SelectionValue &&
        left.ToggleState == right.ToggleState && left.Bounds == right.Bounds;

    private static bool Contains(RectI bounds, int x, int y) => x >= bounds.X && y >= bounds.Y &&
        (long)x < (long)bounds.X + bounds.Width && (long)y < (long)bounds.Y + bounds.Height;
    private static long Area(RectI bounds) => (long)bounds.Width * bounds.Height;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Preview(string value, int limit = MaxPreviewLength) => value.Length <= limit ? value : value[..limit] + "…";
    private static string Component(string value) => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

    private sealed record PropertySample(string Hash, string Preview, long Sequence, DateTimeOffset TimestampUtc);
    private sealed record IdentityInfo(string Key, string Basis, bool Stable);
    private sealed record Sample(AutomationObservation Control, long WindowHwnd, string Provider);

    private sealed class Entry
    {
        public Entry(IdentityInfo identity, Sample sample, FieldKind kind, FrameObservation frame)
        {
            Id = "field-" + Hash(identity.Key)[..24];
            IdentityBasis = identity.Basis;
            StableIdentity = identity.Stable;
            var name = sample.Control.IsPassword == false ? sample.Control.Name : "";
            Label = Preview(!string.IsNullOrWhiteSpace(name) && name != "[redacted]" ? name :
                sample.Control.IsPassword == false && !string.IsNullOrWhiteSpace(sample.Control.AutomationId)
                    ? sample.Control.AutomationId : kind + " field", 128);
            Kind = kind;
            Bounds = sample.Control.Bounds;
            WindowHwnd = sample.WindowHwnd;
            ProviderId = sample.Provider;
            ControlRuntimeId = sample.Control.RuntimeId;
            FirstFrameSequence = frame.Sequence;
            FirstObservedUtc = frame.TimestampUtc;
        }

        public string Id { get; }
        public string Label { get; set; }
        public FieldKind Kind { get; }
        public RectI Bounds { get; set; }
        public FieldObservationState State { get; set; }
        public FieldReviewDecision Decision { get; set; }
        public bool IsManual { get; set; }
        public bool ValueAvailable { get; set; }
        public string? BeforeValue { get; set; }
        public string? AfterValue { get; set; }
        public string? DisplayProperty { get; set; }
        public List<FieldPropertyChange> Changes { get; } = [];
        public HashSet<string> UnchangedProperties { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ChangedProperties { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PropertySample> Previous { get; } = new(StringComparer.Ordinal);
        public string IdentityBasis { get; }
        public bool StableIdentity { get; }
        public long FirstFrameSequence { get; }
        public long LastFrameSequence { get; set; }
        public long WindowHwnd { get; }
        public string ProviderId { get; }
        public string ControlRuntimeId { get; set; }
        public bool IsCurrent { get; set; }
        public bool ComparisonInterrupted { get; set; }
        public DateTimeOffset FirstObservedUtc { get; }
        public DateTimeOffset LastObservedUtc { get; set; }

        public FieldObservation Snapshot() => new(Id, Label, Kind, Bounds, State, Decision, IsManual,
            ValueAvailable, BeforeValue, AfterValue, Changes.ToArray(),
            UnchangedProperties.Order(StringComparer.Ordinal).ToArray(), IdentityBasis,
            FirstFrameSequence, LastFrameSequence, WindowHwnd, ControlRuntimeId, IsCurrent,
            FirstObservedUtc, LastObservedUtc, ProviderId);
    }

    private sealed class ObservationContext
    {
        private readonly FrameObservation _frame;
        private readonly Dictionary<(long Window, string Provider, string Reference), Sample[]> _byReference;
        private readonly Dictionary<(long Window, string Provider, string Parent, string Segment), int> _siblingCounts;
        private readonly Dictionary<Sample, IdentityInfo> _identities = [];
        public IReadOnlyList<Sample> Controls { get; }

        public ObservationContext(FrameObservation frame)
        {
            _frame = frame;
            var windows = new HashSet<long>((frame.ScopedWindows ?? []).Select(window => window.Hwnd));
            windows.UnionWith(frame.ObservedWindowHwnds ?? []);
            windows.Add(frame.Window.Hwnd);
            Controls = frame.Automation.Where(control => control.ScopeStatus is not ("unverified" or "outside-target"))
                .Select(control => new Sample(control,
                    control.QueryWindowHwnd is > 0 ? control.QueryWindowHwnd.Value :
                    control.WindowHwnd > 0 ? control.WindowHwnd : frame.Window.Hwnd,
                    control.ProviderId ?? control.FrameworkId))
                .Where(sample => sample.WindowHwnd > 0 && windows.Contains(sample.WindowHwnd)).ToArray();
            _byReference = Controls.GroupBy(sample => (sample.WindowHwnd, sample.Provider, sample.Control.RuntimeId))
                .ToDictionary(group => group.Key, group => group.ToArray());
            _siblingCounts = Controls.GroupBy(sample =>
                    (sample.WindowHwnd, sample.Provider, sample.Control.ParentRuntimeId, Segment(sample.Control)))
                .ToDictionary(group => group.Key, group => group.Count());
        }

        public bool IsVisible(Sample sample)
        {
            var current = sample;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var depth = 0; depth < 64; depth++)
            {
                if (current.Control.IsOffscreen || !ControlGeometry.IsRenderable(current.Control.Bounds)) return false;
                if (string.IsNullOrEmpty(current.Control.ParentRuntimeId)) return true;
                if (!visited.Add(current.Control.RuntimeId)) return false;
                if (!_byReference.TryGetValue((current.WindowHwnd, current.Provider, current.Control.ParentRuntimeId), out var parents))
                    return true;
                if (parents.Length != 1) return true;
                current = parents[0];
            }
            return false;
        }

        public IdentityInfo Identity(Sample sample)
        {
            if (_identities.TryGetValue(sample, out var found)) return found;
            var scope = _frame.Window.ProcessId.ToString(CultureInfo.InvariantCulture) + "/" +
                sample.WindowHwnd.ToString(CultureInfo.InvariantCulture) + "/" + Component(sample.Provider);
            IdentityInfo identity;
            if (!string.IsNullOrWhiteSpace(sample.Control.ProviderRuntimeId))
                identity = new(scope + "/runtime/" + Component(sample.Control.ProviderRuntimeId),
                    "Provider runtime ID in this window; recording session only", true);
            else if (TryPath(sample, out var path))
                identity = new(scope + "/path/" + path,
                    "Unambiguous AutomationId path in this observed window; needs later validation", true);
            else
                identity = new(scope + "/observation/" + _frame.TimestampUtc.UtcTicks.ToString(CultureInfo.InvariantCulture) +
                    "/" + _frame.Sequence.ToString(CultureInfo.InvariantCulture) + "/" + Component(sample.Control.RuntimeId),
                    "Observation only; control identity could not be verified", false);
            _identities[sample] = identity;
            return identity;
        }

        private bool TryPath(Sample sample, out string path)
        {
            path = "";
            if (string.IsNullOrWhiteSpace(sample.Control.AutomationId)) return false;
            var segments = new List<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = sample;
            for (var depth = 0; depth < 64; depth++)
            {
                if (!visited.Add(current.Control.RuntimeId)) return false;
                var segment = Segment(current.Control);
                if (_siblingCounts[(current.WindowHwnd, current.Provider, current.Control.ParentRuntimeId, segment)] != 1)
                    return false;
                segments.Add(segment);
                if (string.IsNullOrEmpty(current.Control.ParentRuntimeId))
                {
                    segments.Reverse();
                    path = string.Join("/", segments);
                    return true;
                }
                if (!_byReference.TryGetValue((current.WindowHwnd, current.Provider, current.Control.ParentRuntimeId), out var parents)
                    || parents.Length != 1) return false;
                current = parents[0];
            }
            return false;
        }

        private static string Segment(AutomationObservation control) => Component(control.AutomationId) +
            Component(control.ControlType) + Component(control.ClassName);
    }
}
