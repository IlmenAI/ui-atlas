using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Recording.Windows;

public sealed partial class ManualRecordingSession
{
    private readonly object _fieldGate = new();
    private readonly FieldObservationTracker _fieldTracker = new();
    private readonly List<string> _fieldEvidence = [];
    private FrameObservation? _latestFieldFrame;
    private string? _lastFieldEvidenceHash;
    private long _fieldEvidenceBytes;
    private int _fieldObservationsFrozen;
    private bool _fieldEvidenceLimited;

    public event Action<FieldLearningSnapshot>? FieldObservationsChanged;

    public FieldLearningSnapshot FieldObservations
    {
        get { lock (_fieldGate) return FieldSnapshot(); }
    }

    public FrameObservation? LatestFieldObservation
    {
        get { lock (_fieldGate) return _latestFieldFrame; }
    }

    public (FieldLearningSnapshot Snapshot, FrameObservation? Frame) GetFieldObservationState()
    {
        lock (_fieldGate) return (FieldSnapshot(), _latestFieldFrame);
    }

    /// <summary>Skip busy capture periods; field discovery must not queue ahead of clicks.</summary>
    public async Task ObserveManualFieldsAsync(CancellationToken cancellationToken, bool includePaused = false)
    {
        if (_nativeCapture is null || !_startedCapture || _finalized || !FullRawObservations
            || Volatile.Read(ref _fieldObservationsFrozen) != 0 || (!includePaused && IsInputCapturePaused)) return;
        lock (_fieldGate) { if (_fieldEvidenceLimited) return; }
        if (includePaused) await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        else if (!await _captureGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            if (_finalized || Volatile.Read(ref _fieldObservationsFrozen) != 0) return;
            var frame = await _nativeCapture.ObserveFieldsAsync(_target, cancellationToken).ConfigureAwait(false);
            if (frame is not null) RememberFieldObservation(frame);
        }
        finally { _captureGate.Release(); }
    }

    public bool MarkFieldAtPoint(int x, int y, DateTimeOffset? clickObservedUtc = null)
    {
        FieldLearningSnapshot snapshot;
        lock (_fieldGate)
        {
            if (_latestFieldFrame is null || _fieldObservationsFrozen != 0 || _fieldEvidenceLimited) return false;
            // A click may replace the surface. Never nominate the replacement
            // control merely because it now occupies the same coordinates.
            if (clickObservedUtc is { } clicked && _latestFieldFrame.TimestampUtc > clicked) return false;
            if (_fieldTracker.MarkAtPoint(_latestFieldFrame, x, y) is null) return false;
            snapshot = _fieldTracker.Snapshot();
            RetainFieldEvidence(_latestFieldFrame, snapshot);
            snapshot = FieldSnapshot();
        }
        FieldObservationsChanged?.Invoke(snapshot);
        return true;
    }

    public void SetFieldDecision(string id, FieldReviewDecision decision)
    {
        FieldLearningSnapshot snapshot;
        lock (_fieldGate)
        {
            if (_fieldObservationsFrozen != 0 || !_fieldTracker.SetDecision(id, decision)) return;
            snapshot = FieldSnapshot();
        }
        FieldObservationsChanged?.Invoke(snapshot);
    }

    private void RememberFieldObservation(FrameObservation frame)
    {
        if (!FullRawObservations || Volatile.Read(ref _fieldObservationsFrozen) != 0) return;
        FieldLearningSnapshot snapshot;
        lock (_fieldGate)
        {
            if (_fieldObservationsFrozen != 0 || _fieldEvidenceLimited) return;
            if (_latestFieldFrame?.TimestampUtc > frame.TimestampUtc) return;
            _latestFieldFrame = frame;
            snapshot = _fieldTracker.Observe(frame);
            RetainFieldEvidence(frame, snapshot);
            snapshot = FieldSnapshot();
        }
        FieldObservationsChanged?.Invoke(snapshot);
    }

    private void RetainFieldEvidence(FrameObservation frame, FieldLearningSnapshot snapshot)
    {
        var evidence = FieldObservationEvidenceBuilder.Build(frame, snapshot, _target.ProcessId, _target.ProcessStartedUtc);
        if (evidence.ContentHash == _lastFieldEvidenceHash) return;
        // Keep the sample that reached the soft quota, including its derived changes, then stop.
        // Bounded projection guarantees the final sample fits the independent hard entry limit.
        _lastFieldEvidenceHash = evidence.ContentHash;
        _fieldEvidenceBytes += evidence.ByteCount;
        _fieldEvidence.Add(evidence.JsonLine);
        if (_fieldEvidence.Count >= RecordingContractLimits.MaxProviderObservations
            || _fieldEvidenceBytes >= RecordingContractLimits.MaxProviderBytes || evidence.Truncated)
            _fieldEvidenceLimited = true;
    }

    private FieldLearningSnapshot FieldSnapshot()
    {
        var snapshot = _fieldTracker.Snapshot();
        return snapshot with { Truncated = _fieldEvidenceLimited || snapshot.Truncated };
    }

    private void WriteFieldEvidence()
    {
        Interlocked.Exchange(ref _fieldObservationsFrozen, 1);
        lock (_fieldGate)
        {
            _writer.WriteText("raw/field-observations.jsonl", string.Join('\n', _fieldEvidence));
            _writer.WriteJson("derived/field-learning.json", FieldSnapshot());
            if (_fieldEvidenceLimited)
                _health.Add(new(DateTimeOffset.UtcNow, "fields", "observation-limit",
                    "Field observation reached its bounded evidence limit; earlier samples were retained.", true));
        }
    }
}
