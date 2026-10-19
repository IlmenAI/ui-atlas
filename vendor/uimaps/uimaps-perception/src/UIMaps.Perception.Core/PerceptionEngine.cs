using Google.Protobuf;
using UIMaps.Protocol.V1;

namespace UIMaps.Perception.Core;

public sealed class PerceptionEngine
{
    private readonly PerceptionPolicy _policy;
    private readonly PacketValidator _validator;
    private readonly ObservationNormalizer _normalizer = new();
    private readonly VisualTextObservationProvider _visualText;
    private readonly FrameSynchronizer _synchronizer;
    private readonly WorldObjectBuilder _objectBuilder;
    private readonly SurfaceSceneReconstructor _sceneReconstructor = new();
    private readonly TrackAssociator _associator = new(new AssociationPolicyV1());
    private readonly SemaphoreSlim _publisher = new(1, 1);
    private readonly SortedDictionary<ulong, byte[]> _snapshots = [];
    private readonly Dictionary<string, byte[]> _publications = new(StringComparer.Ordinal);
    private readonly object _waiterLock = new();
    private readonly List<TaskCompletionSource<IngestResult>> _waiters = [];
    private ulong _version;
    private ulong _surveyVersion;

    public PerceptionEngine(PerceptionPolicy? policy = null, VisualTextObservationProvider? visualText = null)
    {
        _policy = policy ?? PerceptionPolicy.Default;
        _visualText = visualText ?? new VisualTextObservationProvider();
        _validator = new PacketValidator(_policy);
        _synchronizer = new FrameSynchronizer(_policy);
        _objectBuilder = new WorldObjectBuilder(_policy);
        Epoch = Guid.NewGuid().ToString("N");
    }

    public string Epoch { get; }
    public ulong LatestVersion => Volatile.Read(ref _version);

    public async Task<IngestResult> IngestAsync(SensorEpisodeBundle bundle, CancellationToken cancellationToken)
    {
        _validator.Validate(bundle);
        await _publisher.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var published = DateTimeOffset.UtcNow;
            var screenshot = bundle.SensorPackets.Single(x => x.StreamKind == SensorStreamKind.Screenshot);
            var anchor = screenshot.CaptureInterval.StartedAtMonotonicTicks + ((screenshot.CaptureInterval.EndedAtMonotonicTicks - screenshot.CaptureInterval.StartedAtMonotonicTicks) / 2);
            var observations = _normalizer.Normalize(bundle, anchor, published).Select(value => value.Clone()).ToList();
            Health? visualTextHealth = null;
            if (ShouldCaptureVisualText(bundle))
            {
                try
                {
                    observations.AddRange(await _visualText.ReadAsync(bundle, published, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    visualTextHealth = new Health { State = HealthState.Partial };
                    visualTextHealth.Issues.Add(new HealthIssue
                    {
                        State = HealthState.ProviderFailed,
                        Code = "PERCEPTION_VISUAL_TEXT_UNAVAILABLE",
                        SafeMessage = "Local visual-text supplementation was unavailable; native and accessibility evidence was retained.",
                        Source = "visual_text",
                    });
                }
            }
            if (bundle.EffectiveCapturePolicy?.CaptureSemantics == CaptureSemantics.Survey)
            {
                return PublishSurvey(bundle, observations, published, visualTextHealth);
            }
            var (frame, _) = _synchronizer.Synchronize(bundle, observations);
            var objects = _objectBuilder.Build(bundle, observations).Select(x => x.Clone()).ToList();
            _sceneReconstructor.Apply(bundle, objects);
            WorldStateSnapshot? previous;
            lock (_snapshots) previous = _snapshots.Count == 0 ? null : WorldStateSnapshot.Parser.ParseFrom(_snapshots.Last().Value);
            var tracks = _associator.Associate(objects, previous);
            var version = checked(_version + 1);
            var scene = new Scene
            {
                SceneId = Guid.NewGuid().ToString("N"),
                EpisodeId = bundle.EpisodeId,
                Kind = "diagnostic_display_capture",
                DisplayId = bundle.DisplayDescriptor.DisplayId,
                FrameId = frame.FrameId,
                Health = frame.Health.Clone(),
            };
            var snapshot = new WorldStateSnapshot
            {
                PerceptionEpoch = Epoch,
                WorldStateVersion = version,
                EpisodeId = bundle.EpisodeId,
                FrameId = frame.FrameId,
                Scene = scene,
                DisplayId = bundle.DisplayDescriptor.DisplayId,
                Health = AggregateHealth(frame.Health, objects.Select(x => x.Health)),
                CapturedAt = frame.CapturedAt,
                PublishedAt = published.ToString("O"),
                Frame = frame.Clone(),
            };
            snapshot.WorldObjects.AddRange(objects.Select(x => x.Clone()));
            snapshot.Tracks.AddRange(tracks.Select(x => x.Clone()));
            snapshot.Observations.AddRange(observations.Select(x => x.Clone()));
            snapshot.SourcePacketIds.AddRange(bundle.SensorPackets.Select(x => x.PacketId));
            lock (_snapshots)
            {
                _snapshots[version] = snapshot.ToByteArray();
                while (_snapshots.Count > _policy.SnapshotRetentionLimit) _snapshots.Remove(_snapshots.Keys.First());
            }
            Volatile.Write(ref _version, version);
            var publicationId = Guid.NewGuid().ToString("N");
            var publication = new SnapshotPublication
            {
                PublicationId = publicationId, PublicationKind = PublicationKind.Snapshot,
                EffectivePolicy = bundle.EffectiveCapturePolicy?.Clone(), CoverageReport = bundle.CoverageReport?.Clone(),
                WorldStateSnapshot = snapshot.Clone(), Health = snapshot.Health.Clone(),
            };
            lock (_publications) _publications[publicationId] = new GetPublicationResponse { Snapshot = publication }.ToByteArray();
            var result = new IngestResult { PerceptionEpoch = Epoch, WorldStateVersion = version, PublicationId = publicationId, PublicationKind = PublicationKind.Snapshot, Health = snapshot.Health.Clone() };
            TaskCompletionSource<IngestResult>[] waiters;
            lock (_waiterLock)
            {
                waiters = _waiters.ToArray();
                _waiters.Clear();
            }
            foreach (var waiter in waiters) waiter.TrySetResult(result.Clone());
            return result;
        }
        finally
        {
            _publisher.Release();
        }
    }

    public GetPublicationResponse GetPublication(string publicationId)
    {
        lock (_publications)
        {
            if (!_publications.TryGetValue(publicationId, out var bytes)) throw new PerceptionException("PERCEPTION_PUBLICATION_NOT_FOUND", "Perception publication is not retained.");
            return GetPublicationResponse.Parser.ParseFrom(bytes);
        }
    }

    private IngestResult PublishSurvey(
        SensorEpisodeBundle bundle,
        IReadOnlyList<Observation> observations,
        DateTimeOffset published,
        Health? visualTextHealth)
    {
        var objects = _objectBuilder.Build(bundle, observations).Select(x => x.Clone()).ToList();
        _sceneReconstructor.Apply(bundle, objects);
        var tracks = _associator.Associate(objects, null);
        var version = checked(_surveyVersion + 1);
        var publicationId = Guid.NewGuid().ToString("N");
        var objectHealth = objects.Select(x => x.Health).ToList();
        if (visualTextHealth is not null) objectHealth.Add(visualTextHealth);
        var survey = new SurveyScenePublication
        {
            PublicationId = publicationId, PublicationKind = PublicationKind.Survey,
            PerceptionEpoch = Epoch, SurveyVersion = version, EpisodeId = bundle.EpisodeId,
            SceneId = Guid.NewGuid().ToString("N"), DisplayId = bundle.DisplayDescriptor.DisplayId,
            StartedAt = bundle.StartedAtUtc, EndedAt = bundle.EndedAtUtc,
            EffectivePolicy = bundle.EffectiveCapturePolicy?.Clone(), CoverageReport = bundle.CoverageReport?.Clone(),
            ConvergenceState = bundle.CoverageReport?.ConvergenceState ?? ConvergenceState.Unspecified,
            Health = AggregateHealth(bundle.Health, objectHealth), PublishedAt = published.ToString("O"),
        };
        foreach (var value in objects)
        {
            var record = new SurveyObjectRecord { WorldObject = value.Clone(), FirstObservedAt = value.FirstObservedAt, LastObservedAt = value.LastObservedAt };
            foreach (var observation in observations.Where(x => value.Lineage.ObservationIds.Contains(x.ObservationId)))
                if (observation.SourceInterval is not null) record.ObservationIntervals.Add(observation.SourceInterval.Clone());
            survey.WorldObjects.Add(record);
        }
        survey.Tracks.AddRange(tracks.Select(x => x.Clone()));
        foreach (var observation in observations)
            survey.ObservationIntervals.Add(new SurveyObservationInterval { SourceRef = observation.SourceEvidenceId, WindowScopeRef = observation.WindowEvidenceId, Interval = observation.SourceInterval?.Clone() });
        survey.SegmentLineage.AddRange(bundle.SegmentDescriptors.Select(x => new SegmentLineage { SegmentId = x.SegmentId, SequenceNumber = x.SequenceNumber, Checksum = x.Checksum }));
        lock (_publications) _publications[publicationId] = new GetPublicationResponse { Survey = survey }.ToByteArray();
        Volatile.Write(ref _surveyVersion, version);
        return new IngestResult { PerceptionEpoch = Epoch, SurveyVersion = version, PublicationId = publicationId, PublicationKind = PublicationKind.Survey, Health = survey.Health.Clone() };
    }

    public WorldStateSnapshot Get(string epoch, ulong version)
    {
        if (!string.Equals(epoch, Epoch, StringComparison.Ordinal)) throw new PerceptionException("PERCEPTION_EPOCH_NOT_FOUND", "Perception epoch is not available.");
        lock (_snapshots)
        {
            if (!_snapshots.TryGetValue(version, out var bytes)) throw new PerceptionException("PERCEPTION_SNAPSHOT_NOT_FOUND", "World State version is not retained.");
            return WorldStateSnapshot.Parser.ParseFrom(bytes);
        }
    }

    public async Task<IngestResult> WaitForPublicationAsync(string epoch, ulong afterVersion, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(epoch) && !string.Equals(epoch, Epoch, StringComparison.Ordinal))
            throw new PerceptionException("PERCEPTION_EPOCH_NOT_FOUND", "Perception epoch is not available.");
        var current = LatestVersion;
        if (current > afterVersion)
        {
            var snapshot = Get(Epoch, current);
            return new IngestResult { PerceptionEpoch = Epoch, WorldStateVersion = current, Health = snapshot.Health.Clone() };
        }
        var waiter = new TaskCompletionSource<IngestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiterLock) _waiters.Add(waiter);
        using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
        try { return await waiter.Task.ConfigureAwait(false); }
        finally { lock (_waiterLock) _waiters.Remove(waiter); }
    }

    private static Health AggregateHealth(Health frame, IEnumerable<Health> objects)
    {
        var health = new Health { State = frame.State };
        health.Issues.AddRange(frame.Issues.Select(x => x.Clone()));
        health.Issues.AddRange(objects.SelectMany(x => x.Issues).Where(x => health.Issues.All(existing => existing.Code != x.Code)).Select(x => x.Clone()));
        if (health.Issues.Count > 0 && health.State == HealthState.Complete) health.State = HealthState.Partial;
        return health;
    }

    internal static bool ShouldCaptureVisualText(SensorEpisodeBundle bundle) =>
        bundle.EffectiveCapturePolicy?.CaptureSemantics == CaptureSemantics.Survey
        && string.Equals(bundle.EffectiveCapturePolicy.ResolvedProfileRef?.ProfileId, "deep-survey", StringComparison.OrdinalIgnoreCase)
        && bundle.EffectiveCapturePolicy.VisualControlDetectionEnabled
        && bundle.EffectiveCapturePolicy.OcrOnVisualControlsEnabled;
}
