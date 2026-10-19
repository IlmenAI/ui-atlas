using Google.Protobuf;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

public sealed partial class ManualRecordingSession
{
    private readonly NativeRecorderCapture? _nativeCapture;
    public bool UsesNativeRecorder => _nativeCapture is not null;

    private async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectNativeAutomationAsync(CancellationToken cancellationToken)
    {
        var frame = await ObserveNativeFrameAsync("native-discovery", new FrameCaptureOptions(), false, cancellationToken).ConfigureAwait(false);
        return (frame.Automation, frame.AutomationTimedOut, frame.AutomationStatus);
    }

    private async Task<FrameObservation> ObserveNativeFrameAsync(string trigger, FrameCaptureOptions options,
        bool persist, CancellationToken cancellationToken)
    {
        RevalidateTarget();
        var episode = await _nativeCapture!.CaptureAsync(_target, cancellationToken, RawCaptureProgress,
            started => { lock (_stateGate) { DrainInputUnsafe(); return _events.Any(input => input.TimestampUtc > started
                && input.Kind is InputEventKind.PointerDown or InputEventKind.KeyDown or InputEventKind.Wheel); } },
            (capture, token) => CaptureScreenshotAsync(capture, token)).ConfigureAwait(false);
        RevalidateTarget();
        var sequence = persist ? Interlocked.Increment(ref _frameSequence) : 0;
        var observation = NativeRecorderCapture.Project(episode, _target, sequence, trigger, options);
        if (!persist) return observation;
        var pixels = episode.SensorPackets.Single(packet => packet.Screenshot is not null).Screenshot.ImageBlob.ToByteArray();
        lock (_stateGate)
        {
            if (_finalized) throw new OperationCanceledException("Recording finalized during native capture.");
            DrainInputUnsafe();
            _writer.WriteBytes(observation.NativeEpisodeEntry!, episode.ToByteArray());
            _writer.WriteBytes(observation.FrameEntry, pixels);
            foreach (var plane in episode.WindowImages.Where(image => !image.Png.IsEmpty))
            {
                var reference = observation.WindowImages!.Single(image => image.Hwnd.ToString() == plane.WindowHandleEvidence);
                _writer.WriteBytes(reference.Entry!, plane.Png.ToByteArray());
            }
            _writer.WriteJson($"raw/observations/frame-{sequence:D6}.json", observation);
            _latestScreenshotPng = pixels; _latestScreenshotSequence = sequence;
            _hasPartialCapture |= observation.AutomationStatus != "ok";
            _frames.Add(new(observation.Sequence, observation.TimestampUtc, observation.Trigger, observation.EpisodeSequence,
                observation.CapturePhase, observation.AutomationStatus, StructuralFingerprint(observation)));
            _frameObservations.Add(observation);
        }
        return observation;
    }
}
