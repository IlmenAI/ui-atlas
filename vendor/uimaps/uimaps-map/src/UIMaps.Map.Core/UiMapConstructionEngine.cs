using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V2_1;
using UIMaps.Protocol.V3;

namespace UIMaps.Map.Core;

public sealed class UiMapConstructionEngine(StableMapRefAllocator? allocator = null)
{
    private readonly StableMapRefAllocator _allocator = allocator ?? new();

    public UiMapDraft CreateDraft(string sessionRef, TargetApplicationIdentity target, string createdAt, string baseRevisionRef = "")
    {
        ValidateTarget(target);
        var applicationRef = string.IsNullOrWhiteSpace(target.ApplicationRef) ? _allocator.Allocate("app") : target.ApplicationRef;
        var draft = new UiMapDraft
        {
            MapDraftRef = _allocator.Allocate("map-draft"),
            MappingSessionRef = sessionRef,
            ApplicationRef = applicationRef,
            Version = 1,
            BaseMapRevisionRef = baseRevisionRef ?? "",
            UpdatedAt = createdAt,
        };
        var application = new MapEntity
        {
            MapEntityRef = applicationRef,
            EntityKind = MapEntityKind.MapApplication,
            ApplicationRef = applicationRef,
            DisplayName = target.DisplayName,
            Role = "application",
            Applicable = true,
            ReviewState = ReviewState.ReviewAccepted,
            StructurallyNecessary = true,
        };
        application.Matchers.AddRange(target.ProcessIdentityEvidence.Select(value => Matcher("processIdentity", "value", value, 1, true, false, ReviewState.ReviewAccepted)));
        if (!string.IsNullOrWhiteSpace(target.ExecutableName))
            application.Matchers.Add(Matcher("process", "processName", Path.GetFileNameWithoutExtension(target.ExecutableName), 1, true, false, ReviewState.ReviewAccepted));
        draft.Entities.Add(application);
        var state = new MapEntity
        {
            MapEntityRef = _allocator.Allocate("application-state"),
            EntityKind = MapEntityKind.MapApplicationState,
            ApplicationRef = applicationRef,
            ParentEntityRef = applicationRef,
            DisplayName = "Default observed state",
            Role = "application-state",
            Applicable = true,
            ReviewState = ReviewState.ReviewAccepted,
            StructurallyNecessary = true,
        };
        draft.Entities.Add(state);
        draft.Relationships.Add(Relationship(MapRelationshipKind.MapHasState, applicationRef, state.MapEntityRef, ReviewState.ReviewAccepted));
        UpdateEtag(draft, createdAt);
        return draft;
    }

    public void Attach(
        UiMapDraft draft,
        TargetApplicationIdentity target,
        SourceObservationSet source,
        IReadOnlyList<WorldObject> worldObjects,
        DataGridMapPackage? reviewedGridMap,
        string updatedAt) => Attach(draft, target, source, worldObjects, reviewedGridMap, [], null, updatedAt);

    public void Attach(
        UiMapDraft draft,
        TargetApplicationIdentity target,
        SourceObservationSet source,
        IReadOnlyList<WorldObject> worldObjects,
        DataGridMapPackage? reviewedGridMap,
        IReadOnlyList<SurfaceVisualFingerprint> surfaceVisualFingerprints,
        string updatedAt) => Attach(draft, target, source, worldObjects, reviewedGridMap, surfaceVisualFingerprints, null, updatedAt);

    public void Attach(
        UiMapDraft draft,
        TargetApplicationIdentity target,
        SourceObservationSet source,
        IReadOnlyList<WorldObject> worldObjects,
        DataGridMapPackage? reviewedGridMap,
        IReadOnlyList<SurfaceVisualFingerprint> surfaceVisualFingerprints,
        ControlDetectionPolicy? controlDetectionPolicy,
        string updatedAt)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(source);
        if (draft.Sources.Any(x => x.SourceObservationSetRef == source.SourceObservationSetRef
            || (!string.IsNullOrWhiteSpace(source.PublicationRef) && x.PublicationRef == source.PublicationRef)))
            throw new UiMapException("MAP_SOURCE_ALREADY_ATTACHED", "The observation source is already attached to this draft.");
        ValidateTarget(target);
        if (!string.Equals(draft.ApplicationRef, source.ApplicationRef, StringComparison.Ordinal))
            throw new UiMapException("MAP_SESSION_TARGET_MISMATCH", "The observation source application does not match the Mapping Session.");

        var objects = worldObjects.Select(x => x.Clone()).ToList();
        var byId = objects.Where(x => !string.IsNullOrWhiteSpace(x.WorldObjectId))
            .GroupBy(x => x.WorldObjectId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var windows = objects.Where(x => x.ObjectKind == WorldObjectKind.WindowObject).ToList();
        var targetWindowMembership = ResolveApplicationMemberWindows(windows, target);
        var targetWindows = targetWindowMembership.Keys
            .OrderByDescending(x => MatchesSeedWindow(x, target))
            .ThenBy(x => x.WorldObjectId, StringComparer.Ordinal)
            .ToList();
        if (targetWindows.Count == 0)
            throw new UiMapException("MAP_SESSION_TARGET_MISMATCH", "No captured native window matched the selected target application.");

        var surfaceObservations = BuildSurfaceObservations(targetWindows, objects, byId, surfaceVisualFingerprints);
        source.SurfaceObservations.Clear();
        source.SurfaceObservations.AddRange(surfaceObservations.Values.Select(value => value.Clone()));
        var inScope = DescendantClosure(targetWindows, byId);
        var sourceToMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var activeVariantBySurface = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var value in objects.OrderBy(x => Depth(x, byId))
                     .ThenByDescending(x => x.ObjectKind == WorldObjectKind.WindowObject && MatchesSeedWindow(x, target))
                     .ThenBy(x => x.WorldObjectId, StringComparer.Ordinal))
        {
            if (!inScope.Contains(value.WorldObjectId))
            {
                var reason = value.ObjectKind == WorldObjectKind.WindowObject
                    ? ExcludedWindowReason(value, target, targetWindows)
                    : "outside_target_scope";
                AddDisposition(draft, source, value, DispositionKind.DispositionExcluded, reason, "The captured object is not an observable member of the selected target application.", [], ReviewState.ReviewAccepted);
                continue;
            }

            switch (value.ObjectKind)
            {
                case WorldObjectKind.WindowObject:
                {
                    var surfaceObservation = surfaceObservations[value];
                    var surface = FindOrCreateSurface(draft, source, value, surfaceObservation);
                    var variant = FindOrCreateVariant(draft, source, surface, value, objects, byId, surfaceObservation);
                    surfaceObservation.ResultingMapSurfaceRef = surface.MapEntityRef;
                    surfaceObservation.ResultingMapVariantRef = variant.MapEntityRef;
                    activeVariantBySurface[surface.MapEntityRef] = variant.MapEntityRef;
                    sourceToMap[value.WorldObjectId] = surface.MapEntityRef;
                    var membershipReason = targetWindowMembership.GetValueOrDefault(value, "mapped_surface_exact_process");
                    AddDisposition(draft, source, value, DispositionKind.DispositionMapped, membershipReason, "The application-member native window supports a distinct mapped Surface candidate.", [surface.MapEntityRef], surface.ReviewState);
                    break;
                }
                case WorldObjectKind.AccessibilityElement:
                {
                    var parentRef = ResolveControlParent(draft, sourceToMap, activeVariantBySurface, value, byId);
                    var visualEquivalent = FindEquivalentControl(draft, parentRef, value, "visual-ocr");
                    if (visualEquivalent is not null)
                    {
                        AddObservedProperties(visualEquivalent, source, value, "accessibility");
                        visualEquivalent.Role = SafeRole(value.Role);
                        sourceToMap[value.WorldObjectId] = visualEquivalent.MapEntityRef;
                        AddDisposition(draft, source, value, DispositionKind.DispositionMapped, "mapped_structured_control_visual_deduplicated",
                            "Structured accessibility evidence was merged into an equivalent visual Control identity without creating a duplicate.",
                            [visualEquivalent.MapEntityRef], visualEquivalent.ReviewState);
                        break;
                    }
                    var control = FindOrCreateControl(draft, source, value, parentRef);
                    sourceToMap[value.WorldObjectId] = control.MapEntityRef;
                    AddDisposition(draft, source, value, DispositionKind.DispositionMapped, "mapped_control", "The accessibility object is represented by a Control candidate.", [control.MapEntityRef], control.ReviewState);
                    break;
                }
                case WorldObjectKind.VisualText:
                {
                    if (controlDetectionPolicy is { VisualControlDetectionEnabled: true, OcrOnVisualControlsEnabled: true })
                    {
                        var parentRef = ResolveControlParent(draft, sourceToMap, activeVariantBySurface, value, byId);
                        var equivalent = FindEquivalentControl(draft, parentRef, value, "accessibility");
                        if (equivalent is not null)
                        {
                            AddEvidence(equivalent, source, value, "visual-ocr");
                            AddVisualTextFacts(equivalent, value);
                            AddDisposition(draft, source, value, DispositionKind.DispositionMergedAsEvidence, "visual_text_structured_control_deduplicated",
                                "OCR evidence matched an existing structured Control and was merged without creating a duplicate.",
                                [equivalent.MapEntityRef], ReviewState.ReviewAccepted);
                            break;
                        }
                        var control = FindOrCreateControl(draft, source, value, parentRef, "visual-ocr");
                        sourceToMap[value.WorldObjectId] = control.MapEntityRef;
                        AddDisposition(draft, source, value, DispositionKind.DispositionMapped, "mapped_visual_ocr_control",
                            "The attachment policy explicitly enabled bounded visual/OCR Control construction. The Control is non-actionable and has no fabricated structured patterns.",
                            [control.MapEntityRef], control.ReviewState);
                        break;
                    }
                    var supporting = ResolveNearestMappedAncestor(value, sourceToMap, byId) ?? draft.ApplicationRef;
                    AddEvidenceToEntity(draft, supporting, source, value, "visual-text");
                    AddDisposition(draft, source, value, DispositionKind.DispositionMergedAsEvidence, "visual_text_evidence_only",
                        "Local OCR is retained as inspect-only evidence. Phase 3.4 permits it to propose a Data Grid inside an explicit reviewed host region, but it cannot manufacture an ordinary Map Control.",
                        [supporting], ReviewState.ReviewAccepted);
                    break;
                }
                case WorldObjectKind.VisualSurface:
                {
                    var supporting = ResolveNearestMappedAncestor(value, sourceToMap, byId) ?? draft.ApplicationRef;
                    AddEvidenceToEntity(draft, supporting, source, value, "visual");
                    AddDisposition(draft, source, value, DispositionKind.DispositionMergedAsEvidence, "visual_support", "The visual object supports an existing mapped entity.", [supporting], ReviewState.ReviewAccepted);
                    break;
                }
                default:
                    AddDisposition(draft, source, value, DispositionKind.DispositionExcluded, "structural_context", "The object remains source context and is not a durable UI entity.", [], ReviewState.ReviewAccepted);
                    break;
            }
        }

        AnnotateEvidenceSourceSurfaces(draft, source, byId);

        if (reviewedGridMap is not null && reviewedGridMap.DataGrids.Count > 0)
            AttachReviewedGrids(draft, source, reviewedGridMap, sourceToMap, byId);
        ReobserveHostedGrids(
            draft,
            source,
            sourceToMap,
            byId,
            schemaReanalyzed: reviewedGridMap is not null && reviewedGridMap.DataGrids.Count > 0);
        PopulateEvidenceStreamSummaries(draft, source);

        var undispositionedWindows = targetWindows.Where(window => !draft.Dispositions.Any(disposition =>
            disposition.SourceObservationSetRef == source.SourceObservationSetRef
            && disposition.SourceWorldObjectRef == window.WorldObjectId
            && disposition.DispositionKind == DispositionKind.DispositionMapped
            && disposition.ReasonCode.StartsWith("mapped_surface_", StringComparison.Ordinal))).ToList();
        if (undispositionedWindows.Count > 0)
            throw new UiMapException("MAP_MEMBER_WINDOW_UNACCOUNTED", "An accepted application-member window did not receive a mapped Surface disposition.");

        source.SurfaceObservations.Clear();
        source.SurfaceObservations.AddRange(surfaceObservations.Values.Select(value => value.Clone()));
        draft.Sources.Add(source.Clone());
        draft.Version++;
        UpdateEtag(draft, updatedAt);
    }

    private static void PopulateEvidenceStreamSummaries(UiMapDraft draft, SourceObservationSet source)
    {
        source.EvidenceStreamSummaries.Clear();
        var evidence = draft.Entities.SelectMany(entity => entity.EvidenceLinks.Select(link => (Entity: entity, Link: link)))
            .Where(value => string.Equals(value.Link.SourceObservationSetRef, source.SourceObservationSetRef, StringComparison.Ordinal))
            .ToList();
        foreach (var kind in new[]
                 {
                     EvidenceStreamKind.EvidenceStreamAccessibility,
                     EvidenceStreamKind.EvidenceStreamNative,
                     EvidenceStreamKind.EvidenceStreamScreenshotGeometry,
                     EvidenceStreamKind.EvidenceStreamVisualOcr,
                     EvidenceStreamKind.EvidenceStreamReviewedMapAssertion,
                 })
        {
            var matching = evidence.Where(value => EvidenceStream(value.Link.EvidenceFamily) == kind).ToList();
            var summary = new EvidenceStreamSummary
            {
                SourceObservationSetRef = source.SourceObservationSetRef,
                StreamKind = kind,
                EvidenceCount = matching.Select(value => value.Link.EvidenceLinkRef).Distinct(StringComparer.Ordinal).Count(),
                MappedEntityCount = kind == EvidenceStreamKind.EvidenceStreamVisualOcr
                    ? matching.Where(value => value.Entity.EntityKind == MapEntityKind.MapControl
                            && string.Equals(value.Link.EvidenceFamily, "visual-ocr", StringComparison.Ordinal))
                        .Select(value => value.Entity.MapEntityRef).Distinct(StringComparer.Ordinal).Count()
                    : matching.Select(value => value.Entity.MapEntityRef).Distinct(StringComparer.Ordinal).Count(),
            };
            if (kind == EvidenceStreamKind.EvidenceStreamVisualOcr)
                summary.Limitations.Add(source.AppliedControlDetectionPolicy?.Effective is { VisualControlDetectionEnabled: true, OcrOnVisualControlsEnabled: true }
                    ? "Eligible for bounded visual/OCR Control construction under the attachment's explicit policy; still non-actionable."
                    : "Inspect-only outside an explicit, host-contained Data Grid identification request.");
            source.EvidenceStreamSummaries.Add(summary);
        }
    }

    private static EvidenceStreamKind EvidenceStream(string family) => family switch
    {
        "accessibility" => EvidenceStreamKind.EvidenceStreamAccessibility,
        "native" or "surface-variant" or "data-grid-host-reobservation" => EvidenceStreamKind.EvidenceStreamNative,
        "visual" => EvidenceStreamKind.EvidenceStreamScreenshotGeometry,
        "visual-text" or "visual-ocr" => EvidenceStreamKind.EvidenceStreamVisualOcr,
        _ => EvidenceStreamKind.EvidenceStreamReviewedMapAssertion,
    };

    private MapEntity FindOrCreateSurface(
        UiMapDraft draft,
        SourceObservationSet source,
        WorldObject value,
        SourceSurfaceObservation surfaceObservation)
    {
        var stableKey = StableObjectKey(value);
        var candidates = draft.Entities.Where(x => x.EntityKind == MapEntityKind.MapSurface
            && x.Matchers.Any(m => m.Family == "stableObject" && m.ExpectedValue == stableKey)).ToList();
        var visualCandidates = surfaceObservation.ContextualMenu && !string.IsNullOrWhiteSpace(surfaceObservation.VisualFingerprint)
            ? draft.Entities.Where(x => x.EntityKind == MapEntityKind.MapSurface
                && x.Matchers.Any(m => m.Family == "surfaceKind" && m.Key == "kind" && m.ExpectedValue == "contextual-menu")
                && x.Matchers.Any(m => m.Family == "surfaceVisual"
                    && VisualFingerprintsEquivalent(m.Key, m.ExpectedValue,
                        surfaceObservation.VisualFingerprintAlgorithm, surfaceObservation.VisualFingerprint))
                && !x.EvidenceLinks.Any(e => e.SourceObservationSetRef == source.SourceObservationSetRef
                    && e.SourceObjectRef != value.WorldObjectId)).ToList()
            : [];
        var availableCandidates = candidates.Where(x => !x.EvidenceLinks.Any(e =>
            e.SourceObservationSetRef == source.SourceObservationSetRef && e.SourceObjectRef != value.WorldObjectId)).ToList();
        var trackCandidates = string.IsNullOrWhiteSpace(value.TrackId) ? [] : availableCandidates.Where(x =>
            x.Matchers.Any(m => m.Family == "track" && m.Key == "trackId" && m.ExpectedValue == value.TrackId)).ToList();
        var existing = visualCandidates.Count == 1
            ? visualCandidates[0]
            : trackCandidates.Count == 1 ? trackCandidates[0]
            : availableCandidates.Count == 1 ? availableCandidates[0] : null;
        if (existing is not null)
        {
            AddEvidence(existing, source, value, "native");
            AddSurfaceVisualMatchers(existing, surfaceObservation);
            return existing;
        }

        var surface = new MapEntity
        {
            MapEntityRef = _allocator.Allocate("surface"),
            EntityKind = MapEntityKind.MapSurface,
            ApplicationRef = draft.ApplicationRef,
            ParentEntityRef = draft.ApplicationRef,
            DisplayName = string.IsNullOrWhiteSpace(value.Name) ? "Application surface" : value.Name,
            Role = string.IsNullOrWhiteSpace(value.Role) ? "window" : value.Role,
            Applicable = true,
            ReviewState = ReviewState.ReviewUnreviewed,
            StructurallyNecessary = true,
        };
        AddObservedProperties(surface, source, value, "native");
        surface.Matchers.Add(Matcher("stableObject", "key", stableKey, .9, false, StableKeyIsVolatile(stableKey), ReviewState.ReviewUnreviewed));
        AddSurfaceVisualMatchers(surface, surfaceObservation);
        if (candidates.Count > 0)
        {
            const string limitation = "A same-signature application window was retained as a separate Surface because the evidence did not identify a unique prior Surface.";
            surface.Limitations.Add(limitation);
            if (!draft.Limitations.Contains(limitation, StringComparer.Ordinal)) draft.Limitations.Add(limitation);
        }
        draft.Entities.Add(surface);
        draft.Relationships.Add(Relationship(MapRelationshipKind.MapHasSurface, draft.ApplicationRef, surface.MapEntityRef, ReviewState.ReviewUnreviewed));

        var variant = new MapEntity
        {
            MapEntityRef = _allocator.Allocate("surface-variant"),
            EntityKind = MapEntityKind.MapSurfaceVariant,
            ApplicationRef = draft.ApplicationRef,
            ParentEntityRef = surface.MapEntityRef,
            DisplayName = "Default observed variant",
            Role = "surface-variant",
            Applicable = true,
            ReviewState = ReviewState.ReviewUnreviewed,
            StructurallyNecessary = true,
        };
        draft.Entities.Add(variant);
        draft.Relationships.Add(Relationship(MapRelationshipKind.MapHasVariant, surface.MapEntityRef, variant.MapEntityRef, ReviewState.ReviewUnreviewed));
        AddReviewItem(draft, surface.MapEntityRef, "surface", "Confirm the target-owned Surface and its default variant.", true);
        return surface;
    }

    private MapEntity FindOrCreateControl(
        UiMapDraft draft,
        SourceObservationSet source,
        WorldObject value,
        string parentRef,
        string evidenceFamily = "accessibility")
    {
        var stableKey = StableObjectKey(value);
        var candidates = draft.Entities.Where(x => x.EntityKind == MapEntityKind.MapControl
            && x.ParentEntityRef == parentRef
            && (x.Matchers.Any(m => m.Family == "stableObject" && m.ExpectedValue == stableKey)
                || (value.ObjectKind == WorldObjectKind.VisualText && !string.IsNullOrWhiteSpace(value.TrackId)
                    && x.Matchers.Any(m => m.Family == "track" && m.Key == "trackId" && m.ExpectedValue == value.TrackId)))
            && !x.EvidenceLinks.Any(e => e.SourceObservationSetRef == source.SourceObservationSetRef
                && e.SourceObjectRef != value.WorldObjectId)).ToList();
        var existing = SelectExistingControl(candidates, value);
        if (existing is not null)
        {
            AddEvidence(existing, source, value, evidenceFamily);
            AddNativeHostedContentLimitation(existing, value);
            AddVisualTextFacts(existing, value);
            return existing;
        }

        var control = new MapEntity
        {
            MapEntityRef = _allocator.Allocate("control"),
            EntityKind = MapEntityKind.MapControl,
            ApplicationRef = draft.ApplicationRef,
            ParentEntityRef = parentRef,
            DisplayName = string.IsNullOrWhiteSpace(value.Name) ? $"Unnamed {SafeRole(value.Role)}" : value.Name,
            Role = SafeRole(value.Role),
            Applicable = true,
            ReviewState = ReviewState.ReviewUnreviewed,
            StructurallyNecessary = string.IsNullOrWhiteSpace(value.Name),
        };
        AddObservedProperties(control, source, value, evidenceFamily);
        AddNativeHostedContentLimitation(control, value);
        AddVisualTextFacts(control, value);
        control.Matchers.Add(Matcher("stableObject", "key", stableKey, .9, false, StableKeyIsVolatile(stableKey), ReviewState.ReviewUnreviewed));
        draft.Entities.Add(control);
        draft.Relationships.Add(Relationship(MapRelationshipKind.MapContains, parentRef, control.MapEntityRef, ReviewState.ReviewUnreviewed));
        AddReviewItem(draft, control.MapEntityRef, "control", "Confirm this Control candidate or assign an alternative disposition.", true);
        return control;
    }

    private void AddVisualTextFacts(MapEntity control, WorldObject value)
    {
        if (value.ObjectKind != WorldObjectKind.VisualText) return;
        const string limitation = "Visual text is local OCR evidence and is not an actionable accessibility Control.";
        if (!control.Limitations.Contains(limitation, StringComparer.Ordinal)) control.Limitations.Add(limitation);
        const string structuredLimitation = "No accessibility or native identity/patterns are implied by visual/OCR evidence.";
        if (!control.Limitations.Contains(structuredLimitation, StringComparer.Ordinal)) control.Limitations.Add(structuredLimitation);
        if (!string.IsNullOrWhiteSpace(value.Name)
            && !control.Assertions.Any(assertion => assertion.Predicate == "visualText" && assertion.Value == value.Name))
        {
            control.Assertions.Add(Assertion(control.MapEntityRef, "visualText", value.Name,
                EpistemicStatus.EpistemicObserved, ReviewState.ReviewUnreviewed, value.Confidence?.Value ?? 0));
        }
    }

    private static MapEntity? FindEquivalentControl(UiMapDraft draft, string parentRef, WorldObject observed, string requiredEvidenceFamily)
    {
        if (observed.BoundsPx is null) return null;
        var normalized = NormalizeText(observed.Name);
        return draft.Entities.Where(entity => entity.EntityKind == MapEntityKind.MapControl
                && entity.ParentEntityRef == parentRef
                && entity.EvidenceLinks.Any(link => link.EvidenceFamily == requiredEvidenceFamily))
            .Where(entity => entity.EvidenceLinks.Any(link => OverlapOverMinimum(link.BoundsPx, observed.BoundsPx) >= .65))
            .Where(entity => normalized.Length == 0
                || NormalizeText(entity.DisplayName) == normalized
                || entity.Assertions.Any(assertion => assertion.Predicate == "name" && NormalizeText(assertion.Value) == normalized))
            .OrderBy(entity => entity.MapEntityRef, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string NormalizeText(string? value) =>
        string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static double OverlapOverMinimum(RectPx? left, RectPx? right)
    {
        if (left is null || right is null) return 0;
        var width = Math.Max(0, Math.Min(left.X + left.Width, right.X + right.Width) - Math.Max(left.X, right.X));
        var height = Math.Max(0, Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y));
        var minimum = Math.Min((long)left.Width * left.Height, (long)right.Width * right.Height);
        return minimum <= 0 ? 0 : width * (double)height / minimum;
    }

    private static void AddNativeHostedContentLimitation(MapEntity control, WorldObject value)
    {
        if (!string.Equals(Property(value, "providerId"), "native-hwnd", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Property(value, "hostedContentOpaque"), bool.TrueString, StringComparison.OrdinalIgnoreCase))
            return;
        const string limitation = "Native HWND host observed directly; owner-drawn descendants were not enumerated and remain unmodeled.";
        if (!control.Limitations.Contains(limitation, StringComparer.Ordinal)) control.Limitations.Add(limitation);
    }

    private static MapEntity? SelectExistingControl(IReadOnlyList<MapEntity> candidates, WorldObject value)
    {
        if (candidates.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(value.TrackId))
        {
            var trackMatches = candidates.Where(candidate => candidate.Matchers.Any(matcher =>
                matcher.Family == "track" && matcher.Key == "trackId" && matcher.ExpectedValue == value.TrackId)).ToList();
            if (trackMatches.Count == 1) return trackMatches[0];
        }

        if (!string.IsNullOrWhiteSpace(value.Name))
        {
            var nameMatches = candidates.Where(candidate =>
                string.Equals(candidate.DisplayName, value.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (nameMatches.Count == 1) return nameMatches[0];
            if (nameMatches.Count > 1) candidates = nameMatches;
        }

        if (value.BoundsPx is not null)
        {
            var bounds = $"{value.BoundsPx.X},{value.BoundsPx.Y},{value.BoundsPx.Width},{value.BoundsPx.Height}";
            var geometryMatches = candidates.Where(candidate => candidate.Matchers.Any(matcher =>
                matcher.Family == "geometry" && matcher.Key == "bounds" && matcher.ExpectedValue == bounds)).ToList();
            if (geometryMatches.Count == 1) return geometryMatches[0];
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private MapEntity FindOrCreateVariant(
        UiMapDraft draft,
        SourceObservationSet source,
        MapEntity surface,
        WorldObject window,
        IReadOnlyList<WorldObject> objects,
        IReadOnlyDictionary<string, WorldObject> byId,
        SourceSurfaceObservation surfaceObservation)
    {
        var signature = StructureSignature(window.WorldObjectId, objects, byId);
        var variants = draft.Entities.Where(x => x.EntityKind == MapEntityKind.MapSurfaceVariant && x.ParentEntityRef == surface.MapEntityRef).ToList();
        var visuallyEquivalent = surfaceObservation.ContextualMenu && !string.IsNullOrWhiteSpace(surfaceObservation.VisualFingerprint)
            ? variants.Where(x => x.Matchers.Any(m => m.Family == "surfaceVisual"
                && VisualFingerprintsEquivalent(m.Key, m.ExpectedValue,
                    surfaceObservation.VisualFingerprintAlgorithm, surfaceObservation.VisualFingerprint))).ToList()
            : [];
        var existing = visuallyEquivalent.Count == 1
            ? visuallyEquivalent[0]
            : variants.FirstOrDefault(x => x.Matchers.Any(m => m.Family == "structure" && m.Key == "signature" && m.ExpectedValue == signature));
        if (existing is not null)
        {
            if (!existing.Matchers.Any(m => m.Family == "structure" && m.Key == "signature" && m.ExpectedValue == signature))
                existing.Matchers.Add(Matcher("structure", "signature", signature, 1, true, false, ReviewState.ReviewUnreviewed));
            AddSurfaceVisualMatchers(existing, surfaceObservation);
            AddEvidence(existing, source, window, "surface-variant");
            return existing;
        }

        var uninitialized = variants.FirstOrDefault(x => x.Matchers.All(m => m.Family != "structure" || m.Key != "signature"));
        if (uninitialized is not null && uninitialized.EvidenceLinks.Count == 0)
        {
            uninitialized.Matchers.Add(Matcher("structure", "signature", signature, 1, true, false, ReviewState.ReviewUnreviewed));
            AddSurfaceVisualMatchers(uninitialized, surfaceObservation);
            AddEvidence(uninitialized, source, window, "surface-variant");
            LinkStateToVariant(draft, uninitialized.MapEntityRef);
            return uninitialized;
        }

        var variant = new MapEntity
        {
            MapEntityRef = _allocator.Allocate("surface-variant"),
            EntityKind = MapEntityKind.MapSurfaceVariant,
            ApplicationRef = draft.ApplicationRef,
            ParentEntityRef = surface.MapEntityRef,
            DisplayName = $"Observed variant {variants.Count + 1}",
            Role = "surface-variant",
            Applicable = true,
            ReviewState = ReviewState.ReviewUnreviewed,
            StructurallyNecessary = true,
        };
        variant.Matchers.Add(Matcher("structure", "signature", signature, 1, true, false, ReviewState.ReviewUnreviewed));
        AddSurfaceVisualMatchers(variant, surfaceObservation);
        AddEvidence(variant, source, window, "surface-variant");
        draft.Entities.Add(variant);
        draft.Relationships.Add(Relationship(MapRelationshipKind.MapHasVariant, surface.MapEntityRef, variant.MapEntityRef, ReviewState.ReviewUnreviewed));
        LinkStateToVariant(draft, variant.MapEntityRef);
        AddReviewItem(draft, variant.MapEntityRef, "surface-variant", "Confirm this distinct observed Surface structure as a variant.", true);
        return variant;
    }

    private void LinkStateToVariant(UiMapDraft draft, string variantRef)
    {
        var state = draft.Entities.First(x => x.EntityKind == MapEntityKind.MapApplicationState);
        if (!draft.Relationships.Any(x => x.RelationshipKind == MapRelationshipKind.MapIncludesVariant && x.FromEntityRef == state.MapEntityRef && x.ToEntityRef == variantRef))
            draft.Relationships.Add(Relationship(MapRelationshipKind.MapIncludesVariant, state.MapEntityRef, variantRef, ReviewState.ReviewUnreviewed));
    }

    private void AttachReviewedGrids(
        UiMapDraft draft,
        SourceObservationSet source,
        DataGridMapPackage gridMap,
        IReadOnlyDictionary<string, string> sourceToMap,
        IReadOnlyDictionary<string, WorldObject> byId)
    {
        foreach (var mappedGrid in gridMap.DataGrids)
        {
            var hostRef = mappedGrid.SourceEvidenceRefs.Select(reference => sourceToMap.GetValueOrDefault(reference))
                .FirstOrDefault(reference => !string.IsNullOrWhiteSpace(reference));
            if (string.IsNullOrWhiteSpace(hostRef))
                hostRef = FindHostByGeometry(draft, source, mappedGrid.ExpectedRelativeBounds, byId, sourceToMap);
            if (string.IsNullOrWhiteSpace(hostRef))
            {
                AddReviewItem(draft, mappedGrid.DataGridRef, "data-grid-host", "The reviewed Data Grid has no uniquely supported host Control.", true);
                continue;
            }
            var hostEntity = draft.Entities.FirstOrDefault(x => x.MapEntityRef == hostRef);
            if (hostEntity?.EntityKind != MapEntityKind.MapControl)
            {
                AddReviewItem(draft, mappedGrid.DataGridRef, "data-grid-host", "The reviewed Data Grid host must resolve to a mapped accessibility Control.", true);
                continue;
            }

            var normalizedHeaders = NormalizeHeaders(mappedGrid.ExpectedHeaderSet);
            var existingGrid = draft.Entities.FirstOrDefault(x => x.EntityKind == MapEntityKind.MapDataGrid
                && SameLogicalControl(draft, x.ParentEntityRef, hostRef)
                && NormalizeHeaders(x.HostedDataGrid?.ExpectedHeaders ?? []).SequenceEqual(normalizedHeaders, StringComparer.Ordinal));
            var hostSource = sourceToMap.FirstOrDefault(x => x.Value == hostRef).Key;
            if (existingGrid is not null)
            {
                if (!string.IsNullOrWhiteSpace(hostSource) && byId.TryGetValue(hostSource, out var reobservedHost))
                    AddEvidence(existingGrid, source, reobservedHost, "data-grid-host-reobservation");
                MarkGridInternals(draft, source.SourceObservationSetRef, hostRef, existingGrid.MapEntityRef, mappedGrid.ExpectedRelativeBounds);
                continue;
            }
            var conflictingGrid = draft.Entities.FirstOrDefault(x => x.EntityKind == MapEntityKind.MapDataGrid
                && SameLogicalControl(draft, x.ParentEntityRef, hostRef));
            if (conflictingGrid is not null)
            {
                AddReviewItem(draft, conflictingGrid.MapEntityRef, "data-grid-schema-conflict", "A reviewed grid on the same host Control has materially different headers and requires an explicit same-table decision.", true);
                continue;
            }

            var gridRef = _allocator.Allocate("data-grid");
            var grid = new MapEntity
            {
                MapEntityRef = gridRef,
                EntityKind = MapEntityKind.MapDataGrid,
                ApplicationRef = draft.ApplicationRef,
                ParentEntityRef = hostRef,
                DisplayName = string.IsNullOrWhiteSpace(mappedGrid.DisplayName) ? "Reviewed data grid" : mappedGrid.DisplayName,
                Role = "data-grid",
                Applicable = true,
                ReviewState = ReviewState.ReviewAccepted,
                HostedDataGrid = new HostedDataGridDescriptor
                {
                    HostControlRef = hostRef,
                    GridMapRevisionRef = gridMap.MapRevisionRef,
                    SourceGridRef = mappedGrid.DataGridRef,
                    ExpectedRelativeBounds = mappedGrid.ExpectedRelativeBounds?.Clone(),
                    KnowledgeStatus = HostedGridKnowledgeStatus.HostedGridKnowledgePresent,
                    IdentificationStatus = GridIdentificationStatus.GridIdentificationReviewed,
                    SchemaStatus = mappedGrid.Columns.Count > 0 && !mappedGrid.SchemaPartial
                        ? GridSchemaStatus.GridSchemaReviewed
                        : GridSchemaStatus.GridSchemaPartial,
                    SourceObservationSetRef = source.SourceObservationSetRef,
                },
            };
            grid.HostedDataGrid.ProviderCapabilities.Add(GridProviderCapabilityKind.GridProviderUnknown);
            grid.HostedDataGrid.ExpectedHeaders.AddRange(mappedGrid.ExpectedHeaderSet);
            grid.HostedDataGrid.Limitations.AddRange(mappedGrid.KnownLimitations);
            grid.Matchers.Add(Matcher("grid", "headers", string.Join('\u001f', mappedGrid.ExpectedHeaderSet.Order(StringComparer.OrdinalIgnoreCase)), 1, true, false, ReviewState.ReviewAccepted));
            draft.Entities.Add(grid);
            draft.Relationships.Add(Relationship(MapRelationshipKind.MapHostsDataGrid, hostRef, gridRef, ReviewState.ReviewAccepted));
            foreach (var mappedColumn in mappedGrid.Columns.OrderBy(x => x.Ordinal))
            {
                var column = new MapEntity
                {
                    MapEntityRef = _allocator.Allocate("grid-column"),
                    EntityKind = MapEntityKind.MapGridColumn,
                    ApplicationRef = draft.ApplicationRef,
                    ParentEntityRef = gridRef,
                    DisplayName = mappedColumn.CanonicalDisplayName,
                    Role = "grid-column",
                    Applicable = true,
                    ReviewState = ReviewState.ReviewAccepted,
                    ReviewedOrder = mappedColumn.Ordinal,
                };
                column.Matchers.AddRange(mappedColumn.HeaderMatchers.Select(value => Matcher("gridColumn", "header", value, 1, false, false, ReviewState.ReviewAccepted)));
                draft.Entities.Add(column);
                draft.Relationships.Add(Relationship(MapRelationshipKind.MapHasColumn, gridRef, column.MapEntityRef, ReviewState.ReviewAccepted));
            }
            MarkGridInternals(draft, source.SourceObservationSetRef, hostRef, gridRef, mappedGrid.ExpectedRelativeBounds);
        }
    }

    private static void MarkGridInternals(
        UiMapDraft draft,
        string sourceObservationSetRef,
        string hostControlRef,
        string dataGridRef,
        RectPx? gridBounds)
    {
        if (gridBounds is not { Width: > 0, Height: > 0 }) return;
        var entities = draft.Entities.Where(entity => !string.IsNullOrWhiteSpace(entity.MapEntityRef))
            .ToDictionary(entity => entity.MapEntityRef, StringComparer.Ordinal);
        foreach (var control in draft.Entities.Where(entity => entity.EntityKind == MapEntityKind.MapControl
                     && entity.MapEntityRef != hostControlRef
                     && IsDescendantOfEntity(entity, hostControlRef, entities)))
        {
            var boundedEvidence = control.EvidenceLinks.Where(evidence =>
                string.Equals(evidence.SourceObservationSetRef, sourceObservationSetRef, StringComparison.Ordinal)
                && evidence.BoundsPx is { Width: > 0, Height: > 0 }).ToList();
            if (boundedEvidence.Count == 0 || !boundedEvidence.All(evidence => MostlyContained(gridBounds, evidence.BoundsPx!))) continue;
            control.GridInternalDataGridRef = dataGridRef;
            const string limitation = "Presented beneath the reviewed DataGrid as retained grid-internal evidence; hidden only from Organized peer-Control hierarchy.";
            if (!control.Limitations.Contains(limitation, StringComparer.Ordinal)) control.Limitations.Add(limitation);
        }
    }

    private static bool IsDescendantOfEntity(
        MapEntity entity,
        string ancestorRef,
        IReadOnlyDictionary<string, MapEntity> entities)
    {
        var parentRef = entity.ParentEntityRef;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(parentRef) && visited.Add(parentRef))
        {
            if (string.Equals(parentRef, ancestorRef, StringComparison.Ordinal)) return true;
            if (!entities.TryGetValue(parentRef, out var parent)) return false;
            parentRef = parent.ParentEntityRef;
        }
        return false;
    }

    private static bool MostlyContained(RectPx outer, RectPx inner)
    {
        var left = Math.Max(outer.X, inner.X);
        var top = Math.Max(outer.Y, inner.Y);
        var right = Math.Min(outer.X + outer.Width, inner.X + inner.Width);
        var bottom = Math.Min(outer.Y + outer.Height, inner.Y + inner.Height);
        var intersection = (long)Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var area = (long)inner.Width * inner.Height;
        return area > 0 && intersection >= area * 0.8;
    }

    public void CorrelateRecordingObservation(UiMapDraft draft, SourceObservationSet resultSource, MapRecordingContext context, string updatedAt)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(resultSource);
        ArgumentNullException.ThrowIfNull(context);
        if (context.ObservationRole != MapObservationRole.MapObservationActionResult) return;
        if (string.IsNullOrWhiteSpace(context.RecordingSessionRef) || string.IsNullOrWhiteSpace(context.ActionEventRef)
            || string.IsNullOrWhiteSpace(context.PrecedingSourceObservationSetRef) || !context.UserPerformed || !context.NotAuthorization)
            throw new UiMapException("MAP_RECORDING_CONTEXT_INVALID", "Action-result attachment requires user-performed, non-authorizing before/action/after correlation evidence.");
        if (draft.Interactions.Any(x => x.ActionEventRef == context.ActionEventRef))
            throw new UiMapException("MAP_INTERACTION_ALREADY_ATTACHED", "The observed action is already correlated to this Map draft.");
        if (draft.Sources.All(x => x.SourceObservationSetRef != context.PrecedingSourceObservationSetRef))
            throw new UiMapException("MAP_RECORDING_PRECEDING_SOURCE_MISSING", "The preceding recording observation is not attached to this Map draft.");

        var sourceDisposition = draft.Dispositions.FirstOrDefault(x => x.SourceObservationSetRef == context.PrecedingSourceObservationSetRef
            && x.SourceWorldObjectRef == context.SourceWorldObjectRef
            && x.ResultingMapEntityRefs.Count == 1);
        var sourceEntityRef = sourceDisposition?.ResultingMapEntityRefs[0] ?? "";
        var precedingVariants = VariantsForSource(draft, context.PrecedingSourceObservationSetRef);
        var resultVariants = VariantsForSource(draft, resultSource.SourceObservationSetRef);
        var outcome = resultSource.CoverageLimitations.Count > 0
            ? MapInteractionOutcome.MapInteractionPartialResult
            : resultVariants.Count == 0
                ? MapInteractionOutcome.MapInteractionUnresolved
                : precedingVariants.ToHashSet(StringComparer.Ordinal).SetEquals(resultVariants)
                    ? MapInteractionOutcome.MapInteractionNoObservableStructuralChange
                    : MapInteractionOutcome.MapInteractionStateChanged;
        var interaction = new ObservedMapInteraction
        {
            InteractionRef = _allocator.Allocate("observed-interaction"),
            RecordingSessionRef = context.RecordingSessionRef,
            SequenceNumber = context.ObservationOrdinal,
            Kind = context.ActionKind,
            OccurredAt = context.ActionObservedAt,
            SourceObservationSetRef = context.PrecedingSourceObservationSetRef,
            ResultObservationSetRef = resultSource.SourceObservationSetRef,
            SourceWorldObjectRef = context.SourceWorldObjectRef,
            SourceMapEntityRef = sourceEntityRef,
            Outcome = outcome,
            ReviewState = ReviewState.ReviewUnreviewed,
            UserPerformed = true,
            NotAuthorization = true,
            ScreenX = context.ScreenX,
            ScreenY = context.ScreenY,
            WheelX = context.WheelX,
            WheelY = context.WheelY,
            ActionEventRef = context.ActionEventRef,
        };
        interaction.ResultSurfaceVariantRefs.AddRange(resultVariants);
        if (string.IsNullOrWhiteSpace(sourceEntityRef)) interaction.Limitations.Add("The pre-action source entity was not uniquely resolved.");
        if (resultVariants.Count == 0) interaction.Limitations.Add("No resulting Surface variant was supported strongly enough for correlation.");
        interaction.Limitations.AddRange(resultSource.CoverageLimitations);
        draft.Interactions.Add(interaction);
        if (!string.IsNullOrWhiteSpace(sourceEntityRef))
        {
            foreach (var resultVariantRef in resultVariants)
            {
                var relationship = Relationship(MapRelationshipKind.MapObservedTransition, sourceEntityRef, resultVariantRef, ReviewState.ReviewUnreviewed);
                relationship.InteractionRef = interaction.InteractionRef;
                draft.Relationships.Add(relationship);
            }
        }
        AddReviewItem(draft, interaction.InteractionRef, "observed-transition", "Review the attended before/action/after transition evidence. It is not a Workflow step or authorization.", true);
        draft.Version++;
        UpdateEtag(draft, updatedAt);
    }

    private void ReobserveHostedGrids(
        UiMapDraft draft,
        SourceObservationSet source,
        IReadOnlyDictionary<string, string> sourceToMap,
        IReadOnlyDictionary<string, WorldObject> byId,
        bool schemaReanalyzed)
    {
        foreach (var grid in draft.Entities.Where(x => x.EntityKind == MapEntityKind.MapDataGrid && x.HostedDataGrid is not null))
        {
            var hostSourceRef = sourceToMap.FirstOrDefault(x =>
                SameLogicalControl(draft, x.Value, grid.HostedDataGrid.HostControlRef)).Key;
            if (string.IsNullOrWhiteSpace(hostSourceRef) || !byId.TryGetValue(hostSourceRef, out var host)) continue;
            AddEvidence(grid, source, host, "data-grid-host-reobservation");
            if (!schemaReanalyzed)
            {
                const string limitation = "Schema was not re-analyzed for this checkpoint; host re-observation alone does not prove visible row or schema completeness.";
                if (!grid.Limitations.Contains(limitation)) grid.Limitations.Add(limitation);
            }
        }
    }

    private static List<string> VariantsForSource(UiMapDraft draft, string sourceRef) => draft.Entities
        .Where(x => x.EntityKind == MapEntityKind.MapSurfaceVariant && x.EvidenceLinks.Any(e => e.SourceObservationSetRef == sourceRef))
        .Select(x => x.MapEntityRef)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToList();

    private static List<string> NormalizeHeaders(IEnumerable<string> headers) => headers
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => x.Trim().ToUpperInvariant())
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToList();

    private static bool SameLogicalControl(UiMapDraft draft, string firstRef, string secondRef)
    {
        if (string.Equals(firstRef, secondRef, StringComparison.Ordinal)) return true;
        var first = draft.Entities.FirstOrDefault(value => value.MapEntityRef == firstRef && value.EntityKind == MapEntityKind.MapControl);
        var second = draft.Entities.FirstOrDefault(value => value.MapEntityRef == secondRef && value.EntityKind == MapEntityKind.MapControl);
        if (first is null || second is null) return false;
        var firstKeys = first.Matchers.Where(value => value.Family == "stableObject" && value.Key == "key")
            .Select(value => value.ExpectedValue).Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.Ordinal);
        return second.Matchers.Any(value => value.Family == "stableObject" && value.Key == "key" && firstKeys.Contains(value.ExpectedValue));
    }

    private string FindHostByGeometry(
        UiMapDraft draft,
        SourceObservationSet source,
        RectPx? gridBounds,
        IReadOnlyDictionary<string, WorldObject> byId,
        IReadOnlyDictionary<string, string> sourceToMap)
    {
        if (gridBounds is null || gridBounds.Width <= 0 || gridBounds.Height <= 0) return "";
        var candidates = byId.Values.Where(x => x.ObjectKind == WorldObjectKind.AccessibilityElement && Contains(x.BoundsPx, gridBounds))
            .OrderBy(x => Area(x.BoundsPx))
            .Select(x => sourceToMap.GetValueOrDefault(x.WorldObjectId))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidates.Count != 1) return "";
        var entity = draft.Entities.FirstOrDefault(x => x.MapEntityRef == candidates[0]);
        if (entity is not null) entity.Limitations.Add("Data Grid host was associated by reviewed geometry because direct source lineage was unavailable.");
        return candidates[0]!;
    }

    private void AddObservedProperties(MapEntity entity, SourceObservationSet source, WorldObject value, string family)
    {
        AddEvidence(entity, source, value, family);
        entity.Assertions.Add(Assertion(entity.MapEntityRef, "role", SafeRole(value.Role), EpistemicStatus.EpistemicObserved, ReviewState.ReviewUnreviewed, value.Confidence?.Value ?? 1));
        if (!string.IsNullOrWhiteSpace(value.Name))
            entity.Assertions.Add(Assertion(entity.MapEntityRef, "name", value.Name, EpistemicStatus.EpistemicObserved, ReviewState.ReviewUnreviewed, value.Confidence?.Value ?? 1));
        if (!string.IsNullOrWhiteSpace(value.TrackId))
            entity.Matchers.Add(Matcher("track", "trackId", value.TrackId, 1, false, true, ReviewState.ReviewUnreviewed));
        foreach (var property in value.Properties.Concat(value.ProcessEvidence))
        {
            if (string.IsNullOrWhiteSpace(property.Value)) continue;
            var stable = property.Key is "automationId" or "controlType" or "className" or "frameworkId" or "processName" or "applicationUserModelId" or "packageFamilyName";
            var volatileValue = property.Key is "name" or "value" or "processId" or "windowHandleEvidence" or "runtimeId";
            if (stable || volatileValue)
                entity.Matchers.Add(Matcher(family, property.Key, property.Value, stable ? .8 : .2, false, volatileValue, ReviewState.ReviewUnreviewed));
        }
        if (value.BoundsPx is not null)
            entity.Matchers.Add(Matcher("geometry", "bounds", $"{value.BoundsPx.X},{value.BoundsPx.Y},{value.BoundsPx.Width},{value.BoundsPx.Height}", .25, false, true, ReviewState.ReviewUnreviewed));
    }

    private void AddEvidenceToEntity(UiMapDraft draft, string entityRef, SourceObservationSet source, WorldObject value, string family)
    {
        var entity = draft.Entities.FirstOrDefault(x => x.MapEntityRef == entityRef);
        if (entity is not null) AddEvidence(entity, source, value, family);
    }

    private void AddEvidence(MapEntity entity, SourceObservationSet source, WorldObject value, string family)
    {
        if (entity.EvidenceLinks.Any(x => x.SourceObservationSetRef == source.SourceObservationSetRef && x.SourceObjectRef == value.WorldObjectId)) return;
        entity.EvidenceLinks.Add(new MapEvidenceLink
        {
            EvidenceLinkRef = _allocator.Allocate("map-evidence"),
            SourceObservationSetRef = source.SourceObservationSetRef,
            SourceArtifactRef = source.PublicationRef,
            SourceObjectRef = value.WorldObjectId,
            EvidenceFamily = family,
            ObservedAt = string.IsNullOrWhiteSpace(value.LastObservedAt) ? source.CapturedAt : value.LastObservedAt,
            SimultaneityGroupRef = source.SimultaneityGroupRef,
            BoundsPx = value.BoundsPx?.Clone(),
        });
    }

    private static void AnnotateEvidenceSourceSurfaces(
        UiMapDraft draft,
        SourceObservationSet source,
        IReadOnlyDictionary<string, WorldObject> byId)
    {
        foreach (var evidence in draft.Entities.SelectMany(entity => entity.EvidenceLinks).Where(evidence =>
                     string.Equals(evidence.SourceObservationSetRef, source.SourceObservationSetRef, StringComparison.Ordinal)
                     && string.IsNullOrWhiteSpace(evidence.SourceSurfaceWorldObjectRef)
                     && byId.ContainsKey(evidence.SourceObjectRef)))
        {
            evidence.SourceSurfaceWorldObjectRef = OwningWindowObjectRef(byId[evidence.SourceObjectRef], byId);
        }
    }

    private static string OwningWindowObjectRef(
        WorldObject value,
        IReadOnlyDictionary<string, WorldObject> byId)
    {
        var current = value;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(current.WorldObjectId))
        {
            if (current.ObjectKind == WorldObjectKind.WindowObject) return current.WorldObjectId;
            if (string.IsNullOrWhiteSpace(current.ParentWorldObjectId)
                || !byId.TryGetValue(current.ParentWorldObjectId, out current!)) break;
        }
        return "";
    }

    private string ResolveControlParent(
        UiMapDraft draft,
        IReadOnlyDictionary<string, string> sourceToMap,
        IReadOnlyDictionary<string, string> activeVariantBySurface,
        WorldObject value,
        IReadOnlyDictionary<string, WorldObject> byId)
    {
        if (!string.IsNullOrWhiteSpace(value.ParentWorldObjectId)
            && sourceToMap.TryGetValue(value.ParentWorldObjectId, out var direct))
        {
            var parent = draft.Entities.First(x => x.MapEntityRef == direct);
            if (parent.EntityKind == MapEntityKind.MapSurface)
                return activeVariantBySurface.GetValueOrDefault(parent.MapEntityRef)
                    ?? draft.Entities.First(x => x.EntityKind == MapEntityKind.MapSurfaceVariant && x.ParentEntityRef == parent.MapEntityRef).MapEntityRef;
            return direct;
        }
        var ancestor = ResolveNearestMappedAncestor(value, sourceToMap, byId);
        if (!string.IsNullOrWhiteSpace(ancestor))
        {
            var parent = draft.Entities.First(x => x.MapEntityRef == ancestor);
            if (parent.EntityKind == MapEntityKind.MapSurface)
                return activeVariantBySurface.GetValueOrDefault(parent.MapEntityRef)
                    ?? draft.Entities.First(x => x.EntityKind == MapEntityKind.MapSurfaceVariant && x.ParentEntityRef == parent.MapEntityRef).MapEntityRef;
            return ancestor;
        }
        return draft.ApplicationRef;
    }

    private static string? ResolveNearestMappedAncestor(
        WorldObject value,
        IReadOnlyDictionary<string, string> sourceToMap,
        IReadOnlyDictionary<string, WorldObject> byId)
    {
        var parentRef = value.ParentWorldObjectId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(parentRef) && visited.Add(parentRef))
        {
            if (sourceToMap.TryGetValue(parentRef, out var mapped)) return mapped;
            if (!byId.TryGetValue(parentRef, out var parent)) break;
            parentRef = parent.ParentWorldObjectId;
        }
        return null;
    }

    private void AddDisposition(
        UiMapDraft draft,
        SourceObservationSet source,
        WorldObject value,
        DispositionKind kind,
        string reason,
        string explanation,
        IEnumerable<string> entityRefs,
        ReviewState reviewState)
    {
        var disposition = new ConstructionDisposition
        {
            SourceWorldObjectRef = value.WorldObjectId,
            SourceObservationSetRef = source.SourceObservationSetRef,
            DispositionKind = kind,
            ReasonCode = reason,
            Explanation = explanation,
            ReviewState = reviewState,
        };
        disposition.ResultingMapEntityRefs.AddRange(entityRefs);
        draft.Dispositions.Add(disposition);
    }

    private void AddReviewItem(UiMapDraft draft, string subjectRef, string category, string message, bool blocking)
    {
        if (draft.ReviewItems.Any(x => x.SubjectRef == subjectRef && x.Category == category && x.ReviewState == ReviewState.ReviewUnreviewed)) return;
        draft.ReviewItems.Add(new ReviewItem
        {
            ReviewItemRef = _allocator.Allocate("review-item"),
            Category = category,
            Severity = blocking ? "blocking" : "warning",
            SubjectRef = subjectRef,
            Message = message,
            Blocking = blocking,
            ReviewState = ReviewState.ReviewUnreviewed,
        });
    }

    private MapRelationship Relationship(MapRelationshipKind kind, string from, string to, ReviewState state) => new()
    {
        MapRelationshipRef = _allocator.Allocate("map-relationship"),
        RelationshipKind = kind,
        FromEntityRef = from,
        ToEntityRef = to,
        ReviewState = state,
    };

    private MapMatcher Matcher(string family, string key, string value, double weight, bool hard, bool volatileValue, ReviewState state) => new()
    {
        MatcherRef = _allocator.Allocate("map-matcher"),
        Family = family,
        Key = key,
        ExpectedValue = value,
        Weight = weight,
        HardGate = hard,
        VolatileValue = volatileValue,
        ReviewState = state,
    };

    private MapAssertion Assertion(string subject, string predicate, string value, EpistemicStatus status, ReviewState review, double confidence) => new()
    {
        AssertionRef = _allocator.Allocate("map-assertion"),
        SubjectRef = subject,
        Predicate = predicate,
        Value = value,
        EpistemicStatus = status,
        ReviewState = review,
        Confidence = Math.Clamp(double.IsFinite(confidence) ? confidence : 0, 0, 1),
    };

    public static void UpdateEtag(UiMapDraft draft, string updatedAt)
    {
        draft.UpdatedAt = updatedAt;
        draft.Etag = UiMapCanonicalizer.HashDraft(draft);
    }

    public static string StableObjectKey(WorldObject value)
    {
        if (value.ObjectKind == WorldObjectKind.WindowObject)
        {
            var process = Property(value, "processName");
            var windowClass = Property(value, "className");
            var normalizedTitle = string.Join(' ', (value.Name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var owned = Property(value, "isOwnedWindow");
            if (!string.IsNullOrWhiteSpace(process) || !string.IsNullOrWhiteSpace(windowClass) || !string.IsNullOrWhiteSpace(normalizedTitle))
                return $"window:{process}:{windowClass}:{normalizedTitle}:owned:{owned}".ToUpperInvariant();
        }
        if (value.ObjectKind == WorldObjectKind.VisualText)
        {
            var anchor = Property(value, "visualTextAnchor");
            return $"visual-text:{SafeRole(value.Role)}:{anchor}".ToUpperInvariant();
        }
        var automation = UsableAutomationId(Property(value, "automationId"));
        if (!string.IsNullOrWhiteSpace(automation)) return $"automation:{SafeRole(value.Role)}:{automation}".ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(value.Name))
        {
            var controlType = Property(value, "controlType");
            var className = Property(value, "className");
            return $"fallback:{SafeRole(value.Role)}:{value.Name}:{controlType}:{className}:{value.BoundsPx?.Width ?? 0}:{value.BoundsPx?.Height ?? 0}".ToUpperInvariant();
        }
        if (!string.IsNullOrWhiteSpace(value.TrackId)) return "track:" + value.TrackId;
        return $"fallback:{SafeRole(value.Role)}:{value.Name}:{value.BoundsPx?.Width ?? 0}:{value.BoundsPx?.Height ?? 0}".ToUpperInvariant();
    }

    public static string StructureSignature(
        string windowRef,
        IReadOnlyList<WorldObject> objects,
        IReadOnlyDictionary<string, WorldObject> byId)
    {
        var parts = objects.Where(x => x.ObjectKind == WorldObjectKind.AccessibilityElement && IsDescendantOf(x, windowRef, byId))
            .Select(value =>
            {
                var automation = UsableAutomationId(Property(value, "automationId"));
                var controlType = Property(value, "controlType");
                var className = Property(value, "className");
                var framework = Property(value, "frameworkId");
                var parent = byId.GetValueOrDefault(value.ParentWorldObjectId);
                var parentIdentity = parent is null
                    ? string.Empty
                    : string.Join('\u001d', SafeRole(parent.Role), UsableAutomationId(Property(parent, "automationId")),
                        Property(parent, "controlType"), Property(parent, "className"), parent.Name);
                return string.Join('\u001f', SafeRole(value.Role), automation, controlType, className, framework,
                    value.Name, Property(value, "isOffscreen"), Property(value, "isEnabled"), parentIdentity).ToUpperInvariant();
            })
            .Order(StringComparer.Ordinal)
            .ToList();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001e', parts)))).ToLowerInvariant();
    }

    private static bool IsDescendantOf(WorldObject value, string ancestorRef, IReadOnlyDictionary<string, WorldObject> byId)
    {
        var parent = value.ParentWorldObjectId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(parent) && visited.Add(parent))
        {
            if (parent == ancestorRef) return true;
            if (!byId.TryGetValue(parent, out var next)) break;
            parent = next.ParentWorldObjectId;
        }
        return false;
    }

    public static bool MatchesTarget(WorldObject window, TargetApplicationIdentity target)
    {
        if (target.ProcessIdentityEvidence.Count == 0) return false;
        var evidence = window.ProcessEvidence.Concat(window.Properties).Select(x => x.Value)
            .Append(window.ProcessInstanceEvidence)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (target.ProcessIdentityEvidence.Count > 0
            && !target.ProcessIdentityEvidence.Any(expected => evidence.Contains(expected)))
            return false;
        if (!string.IsNullOrWhiteSpace(target.ExecutableName))
        {
            var process = Property(window, "processName");
            var executable = Property(window, "executablePath");
            if (!string.Equals(process, Path.GetFileNameWithoutExtension(target.ExecutableName), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(process, target.ExecutableName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(executable), target.ExecutableName, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    public static bool MatchesSeedWindow(WorldObject window, TargetApplicationIdentity target)
    {
        if (!MatchesTarget(window, target)) return false;
        if (target.WindowIdentityEvidence.Count > 0)
        {
            var windowEvidence = window.Properties.Concat(window.ProcessEvidence).Select(x => x.Value).Append(window.Name);
            if (!target.WindowIdentityEvidence.Any(expected => windowEvidence.Contains(expected, StringComparer.OrdinalIgnoreCase))) return false;
        }
        return true;
    }

    private static Dictionary<WorldObject, string> ResolveApplicationMemberWindows(
        IReadOnlyList<WorldObject> windows,
        TargetApplicationIdentity target)
    {
        var result = windows.Where(IsObservableWindowCandidate).Where(x => MatchesTarget(x, target))
            .ToDictionary(x => x, _ => "mapped_surface_exact_process");
        var changed = true;
        while (changed)
        {
            changed = false;
            var acceptedHandles = result.Keys.Select(x => Property(x, "windowHandleEvidence"))
                .Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var window in windows.Where(IsObservableWindowCandidate).Where(x => !result.ContainsKey(x)))
            {
                var owner = Property(window, "ownerWindowHandleEvidence");
                var rootOwner = Property(window, "rootOwnerWindowHandleEvidence");
                if ((!string.IsNullOrWhiteSpace(owner) && acceptedHandles.Contains(owner))
                    || (!string.IsNullOrWhiteSpace(rootOwner) && acceptedHandles.Contains(rootOwner)))
                {
                    result[window] = "mapped_surface_native_owner";
                    changed = true;
                }
            }
        }
        return result;
    }

    private static string ExcludedWindowReason(
        WorldObject window,
        TargetApplicationIdentity target,
        IReadOnlyList<WorldObject> acceptedWindows)
    {
        if (!IsObservableWindowCandidate(window)) return "window_not_observable";
        if (target.ProcessIdentityEvidence.Count > 0 && MatchesExecutable(window, target)
            && !target.ProcessIdentityEvidence.Contains(window.ProcessInstanceEvidence, StringComparer.OrdinalIgnoreCase))
            return "window_wrong_process_instance";
        var acceptedHandles = acceptedWindows.Select(x => Property(x, "windowHandleEvidence"))
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var owner = Property(window, "ownerWindowHandleEvidence");
        var rootOwner = Property(window, "rootOwnerWindowHandleEvidence");
        if (!string.IsNullOrWhiteSpace(owner) || !string.IsNullOrWhiteSpace(rootOwner))
            return acceptedHandles.Contains(owner) || acceptedHandles.Contains(rootOwner)
                ? "window_not_observable"
                : "window_unproven_ownership";
        return "window_outside_application_scope";
    }

    private static bool MatchesExecutable(WorldObject window, TargetApplicationIdentity target)
    {
        if (string.IsNullOrWhiteSpace(target.ExecutableName)) return false;
        var process = Property(window, "processName");
        var executable = Property(window, "executablePath");
        return string.Equals(process, Path.GetFileNameWithoutExtension(target.ExecutableName), StringComparison.OrdinalIgnoreCase)
            || string.Equals(process, target.ExecutableName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(executable), target.ExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsObservableWindowCandidate(WorldObject window)
    {
        var visible = Property(window, "isVisible");
        var minimized = Property(window, "isMinimized");
        var unavailable = Property(window, "isCloakedOrUnavailable");
        return !string.Equals(visible, bool.FalseString, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(minimized, bool.TrueString, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(unavailable, bool.TrueString, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> DescendantClosure(IReadOnlyList<WorldObject> roots, IReadOnlyDictionary<string, WorldObject> byId)
    {
        var result = roots.Select(x => x.WorldObjectId).ToHashSet(StringComparer.Ordinal);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var value in byId.Values)
            {
                if (!string.IsNullOrWhiteSpace(value.ParentWorldObjectId) && result.Contains(value.ParentWorldObjectId) && result.Add(value.WorldObjectId))
                    changed = true;
            }
        }
        return result;
    }

    private static int Depth(WorldObject value, IReadOnlyDictionary<string, WorldObject> byId)
    {
        var depth = 0;
        var parent = value.ParentWorldObjectId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(parent) && visited.Add(parent) && byId.TryGetValue(parent, out var ancestor))
        {
            depth++;
            parent = ancestor.ParentWorldObjectId;
        }
        return depth;
    }

    private static string Property(WorldObject value, string key) =>
        value.Properties.Concat(value.ProcessEvidence).FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))?.Value ?? "";

    private static bool Contains(RectPx? outer, RectPx? inner) => outer is not null && inner is not null
        && outer.X <= inner.X && outer.Y <= inner.Y
        && outer.X + outer.Width >= inner.X + inner.Width
        && outer.Y + outer.Height >= inner.Y + inner.Height;

    private static long Area(RectPx? value) => value is null ? long.MaxValue : checked((long)Math.Max(0, value.Width) * Math.Max(0, value.Height));

    private static string SafeRole(string role) => string.IsNullOrWhiteSpace(role) ? "control" : role.Trim();

    private static string UsableAutomationId(string value)
    {
        var normalized = value.Trim();
        return normalized.Length == 0
            || normalized.Equals("System.__ComObject", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("AutomationElement.NotSupported", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("NotSupported", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : normalized;
    }

    private void AddSurfaceVisualMatchers(MapEntity entity, SourceSurfaceObservation observation)
    {
        if (!observation.ContextualMenu || string.IsNullOrWhiteSpace(observation.VisualFingerprint)
            || string.IsNullOrWhiteSpace(observation.VisualFingerprintAlgorithm)) return;
        if (!entity.Matchers.Any(matcher => matcher.Family == "surfaceKind" && matcher.Key == "kind"
                && matcher.ExpectedValue == "contextual-menu"))
            entity.Matchers.Add(Matcher("surfaceKind", "kind", "contextual-menu", 1, true, false, ReviewState.ReviewUnreviewed));
        if (!entity.Matchers.Any(matcher => matcher.Family == "surfaceVisual"
                && matcher.Key == observation.VisualFingerprintAlgorithm
                && matcher.ExpectedValue == observation.VisualFingerprint))
            entity.Matchers.Add(Matcher("surfaceVisual", observation.VisualFingerprintAlgorithm,
                observation.VisualFingerprint, 1, false, false, ReviewState.ReviewUnreviewed));
    }

    private static Dictionary<WorldObject, SourceSurfaceObservation> BuildSurfaceObservations(
        IReadOnlyList<WorldObject> targetWindows,
        IReadOnlyList<WorldObject> objects,
        IReadOnlyDictionary<string, WorldObject> byId,
        IReadOnlyList<SurfaceVisualFingerprint> fingerprints)
    {
        var fingerprintByObject = fingerprints
            .Where(value => !string.IsNullOrWhiteSpace(value.SourceWorldObjectRef))
            .GroupBy(value => value.SourceWorldObjectRef, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var result = targetWindows.ToDictionary(window => window, window =>
        {
            var zOrderObserved = int.TryParse(Property(window, "zOrder"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var zOrder);
            fingerprintByObject.TryGetValue(window.WorldObjectId, out var fingerprint);
            return new SourceSurfaceObservation
            {
                SourceWorldObjectRef = window.WorldObjectId,
                Exposure = SurfaceExposure.Ambiguous,
                ZOrder = zOrder,
                ZOrderObserved = zOrderObserved,
                ContextualMenu = IsContextualMenuWindow(window, objects, byId),
                VisualFingerprintAlgorithm = fingerprint?.Algorithm ?? "",
                VisualFingerprint = fingerprint?.Fingerprint ?? "",
            };
        });

        if (targetWindows.Count == 1)
        {
            result[targetWindows[0]].Exposure = SurfaceExposure.Topmost;
            return result;
        }

        if (result.Values.All(value => value.ZOrderObserved))
        {
            var minimum = result.Values.Min(value => value.ZOrder);
            var topmost = result.Where(value => value.Value.ZOrder == minimum).Select(value => value.Key).ToList();
            foreach (var pair in result)
                pair.Value.Exposure = pair.Value.ZOrder == minimum
                    ? topmost.Count == 1 ? SurfaceExposure.Topmost : SurfaceExposure.Ambiguous
                    : SurfaceExposure.OccludedBySameApplication;
            return result;
        }

        var foreground = targetWindows.Where(window => string.Equals(Property(window, "isForeground"),
            bool.TrueString, StringComparison.OrdinalIgnoreCase)).ToList();
        if (foreground.Count == 1)
        {
            foreach (var pair in result)
                pair.Value.Exposure = ReferenceEquals(pair.Key, foreground[0])
                    ? SurfaceExposure.Topmost
                    : SurfaceExposure.OccludedBySameApplication;
        }
        return result;
    }

    private static bool IsContextualMenuWindow(
        WorldObject window,
        IReadOnlyList<WorldObject> objects,
        IReadOnlyDictionary<string, WorldObject> byId)
    {
        var className = Property(window, "className");
        if (string.Equals(className, "#32768", StringComparison.OrdinalIgnoreCase)) return true;
        if (IsMenuRole(window.Role)) return true;
        if (!string.Equals(Property(window, "isOwnedWindow"), bool.TrueString, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(Property(window, "ownerWindowHandleEvidence"))) return false;
        if (IsMenuRole(window.Name)) return true;
        var descendants = objects.Where(value => value.ObjectKind == WorldObjectKind.AccessibilityElement
                && IsDescendantOf(value, window.WorldObjectId, byId)).ToList();
        var menuDescendants = descendants.Where(value => IsMenuRole(value.Role)
            || IsMenuRole(Property(value, "controlType"))).ToList();
        if (menuDescendants.Count == 0) return false;
        var interactiveDescendants = descendants.Where(value => !IsStructuralMenuContainer(value)).ToList();
        return interactiveDescendants.Count > 0 && interactiveDescendants.All(value =>
            IsMenuRole(value.Role) || IsMenuRole(Property(value, "controlType")));
    }

    private static bool IsMenuRole(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Replace(" ", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal);
        return normalized.Equals("menu", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("menuitem", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("contextmenu", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("popupmenu", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStructuralMenuContainer(WorldObject value)
    {
        var role = string.IsNullOrWhiteSpace(value.Role) ? Property(value, "controlType") : value.Role;
        return role.Equals("pane", StringComparison.OrdinalIgnoreCase)
            || role.Equals("group", StringComparison.OrdinalIgnoreCase)
            || role.Equals("separator", StringComparison.OrdinalIgnoreCase)
            || role.Equals("text", StringComparison.OrdinalIgnoreCase)
            || role.Equals("window", StringComparison.OrdinalIgnoreCase);
    }

    public static bool VisualFingerprintsEquivalent(
        string firstAlgorithm,
        string firstFingerprint,
        string secondAlgorithm,
        string secondFingerprint)
    {
        if (!string.Equals(firstAlgorithm, secondAlgorithm, StringComparison.Ordinal)
            || !string.Equals(firstAlgorithm, "surface-phash-256-v1", StringComparison.Ordinal)
            || firstFingerprint.Length != 64 || secondFingerprint.Length != 64) return false;
        try
        {
            var left = Convert.FromHexString(firstFingerprint);
            var right = Convert.FromHexString(secondFingerprint);
            var distance = 0;
            for (var index = 0; index < left.Length; index++)
                distance += System.Numerics.BitOperations.PopCount((uint)(left[index] ^ right[index]));
            return distance <= 4;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool StableKeyIsVolatile(string value) =>
        value.StartsWith("track:", StringComparison.Ordinal)
        || value.StartsWith("fallback:", StringComparison.Ordinal)
        || value.StartsWith("VISUAL-TEXT:", StringComparison.Ordinal);

    private static void ValidateTarget(TargetApplicationIdentity target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(target.ApplicationRef) || string.IsNullOrWhiteSpace(target.DisplayName))
            throw new UiMapException("MAP_SESSION_TARGET_REQUIRED", "Application ref and display name are required.");
        if (target.ProcessIdentityEvidence.Count == 0 || target.ProcessIdentityEvidence.Any(string.IsNullOrWhiteSpace))
            throw new UiMapException("MAP_SESSION_TARGET_REQUIRED", "An exact process-instance identity is required for application membership.");
    }
}
