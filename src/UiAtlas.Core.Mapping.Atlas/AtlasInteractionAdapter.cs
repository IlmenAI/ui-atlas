using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Mapping.Atlas;

/// <summary>Retains explicit attended before/action/after correlation without
/// guessing a provider owner, mouse button, wheel delta, or replayable action.</summary>
internal sealed class AtlasInteractionAdapter
{
    private readonly InteractionObservation[] _interactions;
    private readonly Dictionary<long, InputEvent> _inputs;
    private readonly string _bundleHash;
    public AtlasInteractionAdapter(RecordingBundle bundle, string bundleHash)
    {
        _bundleHash = bundleHash;
        _interactions = Read<InteractionObservation>(bundle, "raw/interactions.jsonl");
        _inputs = Read<InputEvent>(bundle, "raw/input-events.jsonl").ToDictionary(value => value.Sequence);
    }
    private static T[] Read<T>(RecordingBundle bundle, string entry) => bundle.Entries.Contains(entry)
        ? bundle.ReadText(entry).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<T>(line, JsonDefaults.Options) ?? throw new InvalidDataException("Invalid recording event.")).ToArray() : [];

    public MapRecordingContext? ForFrame(long frame, UiMapDraft draft, IReadOnlyList<AtlasMapSource> sources)
    {
        var candidates = _interactions.Where(value => value.Actor == InteractionActor.User
            && value.Gesture is InteractionGestureKind.Click or InteractionGestureKind.DoubleClick or InteractionGestureKind.Wheel
            && value.ResultFrameSequences.Contains(frame)
            && !draft.Interactions.Any(existing => existing.ActionEventRef == EventRef(value))).ToArray();
        // A single result cannot be assigned to competing actions. The complete
        // interaction stream is still retained in the original bundle.
        if (candidates.Length != 1) return null;
        var action = candidates[0];
        var before = sources.SingleOrDefault(source => source.BundleHash == _bundleHash && source.Frame == action.SourceFrameSequence);
        var input = action.InputSequences.Select(sequence => _inputs.GetValueOrDefault(sequence))
            .FirstOrDefault(value => value?.Kind is InputEventKind.PointerDown or InputEventKind.Wheel);
        if (before is null || input is null || draft.Sources.All(value => value.SourceObservationSetRef != before.SourceRef)) return null;
        return new MapRecordingContext
        {
            RecordingSessionRef = "atlas-recording:" + _bundleHash,
            ObservationOrdinal = checked((ulong)action.Sequence), ObservationRole = MapObservationRole.MapObservationActionResult,
            PrecedingSourceObservationSetRef = before.SourceRef, ActionEventRef = EventRef(action),
            // Atlas historically did not distinguish primary/secondary/middle
            // buttons or retain wheel deltas. Unspecified is the truthful value.
            ActionKind = action.Gesture == InteractionGestureKind.DoubleClick ? MapInteractionKind.MapInteractionPointerDoubleClick
                : action.Gesture == InteractionGestureKind.Wheel ? MapInteractionKind.MapInteractionPointerWheel : MapInteractionKind.Unspecified,
            ActionObservedAt = action.StartedUtc.ToString("O"), ScreenX = input.X, ScreenY = input.Y,
            UserPerformed = true, NotAuthorization = true,
            // Runtime IDs in merged Atlas records cannot uniquely select one
            // provider's WorldObject. UI Maps records an unresolved source.
            SourceWorldObjectRef = "",
        };
    }
    private string EventRef(InteractionObservation action) => "atlas-action:" + _bundleHash + ":" + action.Sequence;
}
