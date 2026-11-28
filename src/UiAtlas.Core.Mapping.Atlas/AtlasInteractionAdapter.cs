using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UIMaps.Protocol;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Mapping.Atlas;

/// <summary>Retains explicit attended before/action/after correlation without
/// guessing a provider owner, mouse button, wheel delta, or replayable action.</summary>
internal sealed class AtlasInteractionAdapter
{
    private readonly InteractionObservation[] _interactions;
    private readonly Dictionary<long, InputEvent> _inputs;
    private readonly string _bundleHash;
    private readonly RecordingBundle _bundle;
    private readonly string _workspace;
    public AtlasInteractionAdapter(RecordingBundle bundle, string bundleHash, string workspace)
    {
        _bundle = bundle;
        _workspace = workspace;
        _bundleHash = bundleHash;
        _interactions = ReadInteractions(bundle);
        _inputs = Read<InputEvent>(bundle, "raw/input-events.jsonl").ToDictionary(value => value.Sequence);
    }
    internal static InteractionObservation[] ReadInteractions(RecordingBundle bundle) =>
        Read<InteractionObservation>(bundle, "raw/interactions.jsonl");
    private static T[] Read<T>(RecordingBundle bundle, string entry) => bundle.Entries.Contains(entry)
        ? bundle.ReadText(entry).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<T>(line, JsonDefaults.Options) ?? throw new InvalidDataException("Invalid recording event.")).ToArray() : [];

    public MapRecordingContext? ForFrame(long frame, UiMapDraft draft, IReadOnlyList<AtlasMapSource> sources)
    {
        var candidates = _interactions.Where(value => value.Actor == InteractionActor.User
            && value.Gesture is InteractionGestureKind.Click or InteractionGestureKind.DoubleClick or InteractionGestureKind.RightClick or InteractionGestureKind.MiddleClick or InteractionGestureKind.Wheel
            && value.ResultFrameSequences.Contains(frame)
            && !draft.Interactions.Any(existing => existing.ActionEventRef == EventRef(value))).ToArray();
        // A single result cannot be assigned to competing actions. The complete
        // interaction stream is still retained in the original bundle.
        if (candidates.Length != 1) return null;
        var action = candidates[0];
        var before = sources.SingleOrDefault(source => source.BundleHash == _bundleHash && source.Frame == action.SourceFrameSequence);
        // The manual recorder retains the release sequence for an attended click.
        var input = action.InputSequences.Select(sequence => _inputs.GetValueOrDefault(sequence))
            .LastOrDefault(value => action.Gesture == InteractionGestureKind.Wheel
                ? value?.Kind == InputEventKind.Wheel : value?.Kind is InputEventKind.PointerUp or InputEventKind.PointerDown);
        if (before is null || input is null || draft.Sources.All(value => value.SourceObservationSetRef != before.SourceRef)) return null;
        return new MapRecordingContext
        {
            RecordingSessionRef = "atlas-recording:" + _bundleHash,
            ObservationOrdinal = checked((ulong)action.Sequence), ObservationRole = MapObservationRole.MapObservationActionResult,
            PrecedingSourceObservationSetRef = before.SourceRef, ActionEventRef = EventRef(action),
            ActionKind = action.Gesture switch
            {
                InteractionGestureKind.Wheel => MapInteractionKind.MapInteractionPointerWheel,
                InteractionGestureKind.DoubleClick => MapInteractionKind.MapInteractionPointerDoubleClick,
                _ => input.Button switch
                {
                    RecordedPointerButton.Primary => MapInteractionKind.MapInteractionPointerPrimary,
                    RecordedPointerButton.Secondary => MapInteractionKind.MapInteractionPointerSecondary,
                    RecordedPointerButton.Middle => MapInteractionKind.MapInteractionPointerMiddle,
                    _ => MapInteractionKind.Unspecified // Older recordings omit the button.
                }
            },
            ActionObservedAt = input.TimestampUtc.ToString("O"), ScreenX = input.X, ScreenY = input.Y,
            UserPerformed = true, NotAuthorization = true,
            SourceWorldObjectRef = ResolveSource(before, action.SourceControl, input),
        };
    }

    private string ResolveSource(AtlasMapSource before, AutomationObservation? selected, InputEvent input)
    {
        if (selected is null || string.IsNullOrEmpty(selected.ProviderId) || string.IsNullOrEmpty(selected.RuntimeId))
            return "";
        var bounds = selected.Bounds;
        if (input.X < bounds.X || input.X >= (long)bounds.X + bounds.Width ||
            input.Y < bounds.Y || input.Y >= (long)bounds.Y + bounds.Height)
            return "";
        var frame = _bundle.ReadJson<FrameObservation>($"raw/observations/frame-{before.Frame:D6}.json");
        if (string.IsNullOrEmpty(frame.NativeEpisodeEntry)) return "";
        // In native frames only, the projected RuntimeId is the provider node's
        // observation-local AccessibilityEvidenceId. Check the retained pre-action
        // record before resolving it in that observation's Perception publication.
        var matches = (frame.RawStreams ?? []).Where(stream => stream.EvidenceKind == "native-automation")
            .SelectMany(stream => stream.Controls)
            .Where(control => control.ProviderId == selected.ProviderId && control.RuntimeId == selected.RuntimeId)
            .Take(2).ToArray();
        if (matches.Length != 1 || matches[0].WindowHwnd != selected.WindowHwnd ||
            matches[0].Name != selected.Name || matches[0].AutomationId != selected.AutomationId ||
            matches[0].ControlType != selected.ControlType || matches[0].Bounds != bounds)
            return "";
        var publication = GetPublicationResponse.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(_workspace, before.PublicationFile)));
        var objects = publication.Snapshot?.WorldStateSnapshot.WorldObjects.AsEnumerable()
            ?? publication.Survey.WorldObjects.Select(item => item.WorldObject);
        var owners = objects.Where(obj => RawWorldControlFusion.Members(obj).Any(member =>
            member.Properties.Any(p => p.Key == "sourceAccessibilityEvidenceId" && p.Value == selected.RuntimeId) &&
            member.Properties.Any(p => p.Key == "providerId" && p.Value == selected.ProviderId)))
            .Take(2).ToArray();
        return owners.Length == 1 ? owners[0].WorldObjectId : "";
    }
    private string EventRef(InteractionObservation action) => "atlas-action:" + _bundleHash + ":" + action.Sequence;
}
