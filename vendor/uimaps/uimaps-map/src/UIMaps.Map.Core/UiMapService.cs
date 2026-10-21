using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Google.Protobuf;
using UIMaps.Map.Core.Grid;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V2_1;
using UIMaps.Protocol.V3;

namespace UIMaps.Map.Core;

public sealed partial class UiMapService
{
    private readonly UiMapStore _store;
    private readonly UiMapConstructionEngine _construction;
    private readonly StableMapRefAllocator _allocator;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, Stack<UiMapDraft>> _undo = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Stack<UiMapDraft>> _redo = new(StringComparer.Ordinal);
    private readonly object _reviewHistoryGate = new();

    public UiMapService(
        string dataDirectory,
        UiMapConstructionEngine? construction = null,
        StableMapRefAllocator? allocator = null,
        Func<DateTimeOffset>? clock = null)
    {
        _store = new UiMapStore(dataDirectory);
        _allocator = allocator ?? new StableMapRefAllocator();
        _construction = construction ?? new UiMapConstructionEngine(_allocator);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public MapCapabilitiesResponse GetCapabilities() => new()
    {
        ServiceVersion = "0.9.0",
        SchemaVersion = "3.3",
        Features =
        {
            "mapping_sessions_v3",
            "mapping_workspace_snapshot_import_v1",
            "snapshot_survey_lineage",
            "whole_application_graph",
            "application_member_windows_v1",
            "complete_disposition_ledger",
            "review_commands",
            "sqlite_durable_store",
            "immutable_revisions",
            "immutable_hash_revisions",
            "hosted_data_grid",
            "map_library",
            "map_library_delete",
            "map_library_archive",
            "map_library_restore",
            "map_library_readiness_v1",
            "compatibility_report",
            "safe_import_export",
            "no_target_input",
            "guided_recording_correlation_v3_1",
            "observed_transitions_v3_1",
            "source_surface_exposure_v3_3",
            "contextual_menu_visual_equivalence_v3_3",
            "stable_grid_host_authoring_v3_4",
            "observation_scoped_grid_identification_v3_4",
            "hosted_grid_knowledge_query_v3_4",
            "hosted_grid_metadata_query_v3_4",
            "observed_grid_content_query_v3_4",
            "evidence_stream_selection_v3_4",
            "visual_entity_hit_selection_v3_4",
            "confirmed_grid_ocr_v3_4",
            "organized_source_alignment_v3_4",
            "control_detection_policy_v3_5",
            "automatic_tabular_grid_detection_v3_5",
            "selected_grid_visible_data_v3_5",
            "scene_zoom_pan_v3_5",
        },
    };

    public CreateMappingSessionResponse CreateSession(CreateMappingSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId))
            throw new UiMapException("MAP_DRAFT_COMMAND_INVALID", "A request id is required.");
        ValidatePrivacy(request.PrivacyScope);
        var now = Now();
        var sessionRef = _allocator.Allocate("mapping-session");
        var draft = _construction.CreateDraft(sessionRef, request.Target, now, request.BaseMapRevisionRef);
        var session = new MappingSessionDescriptor
        {
            MappingSessionRef = sessionRef,
            Status = MappingSessionStatus.MappingSessionReady,
            Target = request.Target.Clone(),
            PrivacyScope = request.PrivacyScope.Clone(),
            MapDraftRef = draft.MapDraftRef,
            BaseMapRevisionRef = request.BaseMapRevisionRef,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
            StatusMessage = "Ready to attach an immutable Snapshot or Survey observation.",
            NotAuthorization = true,
        };
        UpdateSessionEtag(session);
        _store.SaveSessionAndDraft(session, draft);
        Audit("mapping-session-created", sessionRef, request.PrivacyScope.AcknowledgedBy, now, "Mapping Session created for one selected application.");
        return new CreateMappingSessionResponse { Session = session.Clone(), Draft = draft.Clone() };
    }

    public AttachObservationResponse AttachObservation(AttachObservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = _store.LoadSession(request.MappingSessionRef);
        var existingDraft = _store.LoadDraft(session.MapDraftRef);
        if (request.RecordingContext is not null
            && !string.IsNullOrWhiteSpace(request.RecordingContext.ActionEventRef)
            && existingDraft.Interactions.Any(value => string.Equals(
                value.ActionEventRef,
                request.RecordingContext.ActionEventRef,
                StringComparison.Ordinal)))
        {
            return new AttachObservationResponse { Session = session.Clone(), Draft = existingDraft.Clone() };
        }
        RequireSessionEtag(session, request.ExpectedEtag);
        if (session.Status is MappingSessionStatus.MappingSessionCancelled or MappingSessionStatus.MappingSessionFailed)
            throw new UiMapException("MAP_SESSION_TERMINAL", "The Mapping Session cannot accept new observations.");
        var hasSnapshot = request.Snapshot is not null && request.Snapshot.WorldObjects.Count > 0;
        var hasSurvey = request.Survey is not null && request.Survey.WorldObjects.Count > 0;
        if (hasSnapshot == hasSurvey)
            throw new UiMapException("MAP_SOURCE_INVALID", "Attach exactly one immutable Snapshot or Survey publication.");

        var now = Now();
        session.Status = MappingSessionStatus.MappingSessionProcessing;
        session.StatusMessage = "Validating source identity and constructing candidate graph.";
        session.Version++;
        session.UpdatedAt = now;
        UpdateSessionEtag(session);
        var draft = existingDraft;
        _store.SaveSessionAndDraft(session, draft);

        var source = BuildSource(session.Target, request, now);
        source.AppliedControlDetectionPolicy = ControlDetectionPolicyNormalizer.Normalize(request.ControlDetectionPolicy);
        var objects = hasSnapshot
            ? request.Snapshot!.WorldObjects.Select(x => x.Clone()).ToList()
            : request.Survey!.WorldObjects.Select(x => x.WorldObject.Clone()).ToList();
        try
        {
            var reviewedGridMap = request.ReviewedGridMap;
            if ((reviewedGridMap is null || reviewedGridMap.DataGrids.Count == 0)
                && request.ReviewedGridAnalysis is not null
                && request.ReviewedGridAnalysis.GridCandidates.Count > 0)
                reviewedGridMap = BuildReviewedGridMap(session, request, now);
            if (reviewedGridMap is not null) source.GridMapRevisionRef = reviewedGridMap.MapRevisionRef;
            _construction.Attach(draft, session.Target, source, objects, reviewedGridMap,
                request.SurfaceVisualFingerprints, source.AppliedControlDetectionPolicy.Effective, now);
            if (reviewedGridMap is not null && !string.IsNullOrWhiteSpace(request.GridHostControlRef))
            {
                var promoted = draft.Entities.Where(value => value.EntityKind == MapEntityKind.MapDataGrid
                    && (value.HostedDataGrid?.SourceObservationSetRef == source.SourceObservationSetRef
                        || value.EvidenceLinks.Any(link => link.SourceObservationSetRef == source.SourceObservationSetRef))).ToList();
                if (promoted.Count == 0 || promoted.All(value => value.ParentEntityRef != request.GridHostControlRef))
                    throw new UiMapException("MAP_GRID_HOST_CONTROL_MISMATCH",
                        "The reviewed grid candidate did not resolve beneath the explicitly selected stable host Control.");
            }
            if (request.RecordingContext is not null)
                _construction.CorrelateRecordingObservation(draft, source, request.RecordingContext, now);
            session.Status = MappingSessionStatus.MappingSessionPartial;
            session.StatusMessage = "Candidate graph is durable and requires review before publication.";
            session.AttachedSourceCount = draft.Sources.Count;
            session.BlockingReviewCount = draft.ReviewItems.Count(x => x.Blocking && x.ReviewState == ReviewState.ReviewUnreviewed);
            session.Version++;
            session.UpdatedAt = now;
            UpdateSessionEtag(session);
            _store.SaveSessionAndDraft(session, draft);
            Audit("source-attached", session.MappingSessionRef, session.PrivacyScope.AcknowledgedBy, now, $"Attached source {source.SourceObservationSetRef}; content omitted from audit.");
            return new AttachObservationResponse { Session = session.Clone(), Draft = draft.Clone() };
        }
        catch
        {
            session.Status = MappingSessionStatus.MappingSessionFailed;
            session.StatusMessage = "Source processing failed; previously durable work remains available.";
            session.Version++;
            session.UpdatedAt = now;
            UpdateSessionEtag(session);
            _store.SaveSessionAndDraft(session, draft);
            throw;
        }
    }

    public MappingSessionDescriptor GetSession(string sessionRef) => _store.LoadSession(sessionRef).Clone();
    public UiMapDraft GetDraft(string draftRef) => _store.LoadDraft(draftRef).Clone();
    public UiMapRevision GetRevision(string revisionRef) => _store.LoadRevision(revisionRef).Clone();

    public UpdateEvidenceStreamSelectionResponse UpdateEvidenceStreamSelection(UpdateEvidenceStreamSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.Selections.Count == 0)
            throw new UiMapException("MAP_EVIDENCE_STREAM_SELECTION_INVALID", "A request id and at least one explicit evidence-stream selection are required.");
        var session = _store.LoadSession(request.MappingSessionRef);
        RequireSessionEtag(session, request.ExpectedEtag);
        var draft = _store.LoadDraft(session.MapDraftRef);
        var duplicate = request.Selections.GroupBy(value => value.StreamKind).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null || request.Selections.Any(value => value.StreamKind == EvidenceStreamKind.Unspecified
                || value.Eligibility == EvidenceStreamEligibility.Unspecified))
            throw new UiMapException("MAP_EVIDENCE_STREAM_SELECTION_INVALID", "Every evidence stream must have one explicit eligibility value.");
        var visualOcrEligible = draft.Sources.Any(source => source.AppliedControlDetectionPolicy?.Effective is
            { VisualControlDetectionEnabled: true, OcrOnVisualControlsEnabled: true });
        if (!visualOcrEligible && request.Selections.Any(value => value.StreamKind == EvidenceStreamKind.EvidenceStreamVisualOcr
                && value.Eligibility == EvidenceStreamEligibility.EvidenceStreamEnabled))
            throw new UiMapException("MAP_VISUAL_CONTROL_NOT_ELIGIBLE", "Visual/OCR may be inspect-only or disabled at session scope. It becomes eligible only inside an explicit Data Grid identification request.");

        var now = Now();
        foreach (var selection in request.Selections)
        {
            var stored = selection.Clone();
            if (string.IsNullOrWhiteSpace(stored.SavedScope)) stored.SavedScope = "mapping-session";
            if (string.IsNullOrWhiteSpace(stored.SelectedAt)) stored.SelectedAt = now;
            var index = draft.EvidenceStreamSelections.ToList().FindIndex(value => value.StreamKind == stored.StreamKind);
            if (index >= 0) draft.EvidenceStreamSelections[index] = stored;
            else draft.EvidenceStreamSelections.Add(stored);
        }
        draft.Version++;
        UiMapConstructionEngine.UpdateEtag(draft, now);
        session.Version++;
        session.UpdatedAt = now;
        session.StatusMessage = "Evidence-stream presentation policy updated; retained evidence and dispositions were not changed.";
        UpdateSessionEtag(session);
        _store.SaveSessionAndDraft(session, draft);
        Audit("evidence-stream-selection-updated", session.MappingSessionRef,
            request.Selections.Select(value => value.SelectedBy).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "reviewer",
            now, "Presentation/authoring policy updated; source evidence remained immutable.");
        return new UpdateEvidenceStreamSelectionResponse { Session = session.Clone(), Draft = draft.Clone() };
    }

    public UpsertDataGridHostDeclarationResponse UpsertGridHostDeclaration(UpsertDataGridHostDeclarationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.Declaration is null
            || request.Declaration.AssertionKind == GridHostAssertionKind.Unspecified)
            throw new UiMapException("MAP_GRID_DECLARATION_CONFLICT", "A request id and explicit grid-host assertion are required.");
        var session = _store.LoadSession(request.MappingSessionRef);
        RequireSessionEtag(session, request.ExpectedEtag);
        var draft = _store.LoadDraft(session.MapDraftRef);
        var host = draft.Entities.SingleOrDefault(value => value.MapEntityRef == request.Declaration.HostControlRef);
        if (host?.EntityKind != MapEntityKind.MapControl)
            throw new UiMapException("MAP_GRID_HOST_CONTROL_REQUIRED", "The declaration must target one stable Map Control.");
        var (surfaceRef, variantRef) = ResolveSurfaceClosure(host, draft.Entities);
        if (string.IsNullOrWhiteSpace(surfaceRef))
            throw new UiMapException("MAP_GRID_HOST_CONTROL_OUT_OF_SCOPE", "The selected Control does not belong to a mapped Surface/variant closure.");

        var now = Now();
        DataGridHostDeclaration stored;
        if (!string.IsNullOrWhiteSpace(request.Declaration.DeclarationRef))
        {
            stored = draft.GridHostDeclarations.SingleOrDefault(value => value.DeclarationRef == request.Declaration.DeclarationRef)
                ?? throw new UiMapException("MAP_GRID_DECLARATION_NOT_FOUND", "The requested grid-host declaration does not exist in this draft.");
            if (request.ExpectedDeclarationVersion != stored.DeclarationVersion)
                throw new UiMapException("MAP_GRID_DECLARATION_CONFLICT", "The grid-host declaration changed; reload before applying another review decision.");
            stored.AssertionKind = request.Declaration.AssertionKind;
            stored.ReviewState = request.Declaration.ReviewState == ReviewState.Unspecified ? ReviewState.ReviewAccepted : request.Declaration.ReviewState;
            stored.ReviewerIdentity = request.Declaration.ReviewerIdentity;
            stored.ReviewedAt = now;
            stored.UpdatedAt = now;
            stored.DeclarationVersion++;
        }
        else
        {
            stored = request.Declaration.Clone();
            stored.DeclarationRef = _allocator.Allocate("grid-host-declaration");
            stored.MappingSessionRef = session.MappingSessionRef;
            stored.MapDraftRef = draft.MapDraftRef;
            stored.ApplicationRef = draft.ApplicationRef;
            stored.SurfaceRef = surfaceRef;
            stored.SurfaceVariantRefs.Clear();
            if (!string.IsNullOrWhiteSpace(variantRef)) stored.SurfaceVariantRefs.Add(variantRef);
            stored.DeclarationVersion = 1;
            stored.ReviewState = stored.AssertionKind == GridHostAssertionKind.GridHostPossible
                ? ReviewState.ReviewUnreviewed : ReviewState.ReviewAccepted;
            stored.CreatedAt = now;
            stored.UpdatedAt = now;
            if (stored.ReviewState == ReviewState.ReviewAccepted) stored.ReviewedAt = now;
            draft.GridHostDeclarations.Add(stored);
        }

        BindDeclarationEvidence(stored, host, draft, surfaceRef);
        if (stored.AssertionKind == GridHostAssertionKind.GridHostPresent
            && string.IsNullOrWhiteSpace(stored.DataGridRef)
            && !stored.Limitations.Contains("Declared-only hosted grid; identification and schema remain unknown.", StringComparer.Ordinal))
            stored.Limitations.Add("Declared-only hosted grid; identification and schema remain unknown.");
        draft.Version++;
        UiMapConstructionEngine.UpdateEtag(draft, now);
        session.Version++;
        session.UpdatedAt = now;
        session.Status = MappingSessionStatus.MappingSessionPartial;
        session.StatusMessage = $"Grid-host assertion {stored.AssertionKind} stored for {host.DisplayName}.";
        UpdateSessionEtag(session);
        _store.SaveSessionAndDraft(session, draft);
        Audit("grid-host-declaration-upserted", stored.DeclarationRef, stored.ReviewerIdentity, now,
            $"Grid-host assertion {stored.AssertionKind} stored; no target input was available.");
        return new UpsertDataGridHostDeclarationResponse
        {
            Session = session.Clone(), Draft = draft.Clone(), Declaration = stored.Clone(),
        };
    }

    public ResolveGridHostObservationResponse ResolveGridHostObservation(ResolveGridHostObservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var draft = _store.LoadDraft(request.MapDraftRef);
        if (!string.Equals(draft.ApplicationRef, request.ApplicationRef, StringComparison.Ordinal))
            throw new UiMapException("MAP_GRID_HOST_CONTROL_OUT_OF_SCOPE", "The selected Application does not match this draft.");
        var host = draft.Entities.SingleOrDefault(value => value.MapEntityRef == request.HostControlRef);
        if (host?.EntityKind != MapEntityKind.MapControl)
            throw new UiMapException("MAP_GRID_HOST_CONTROL_REQUIRED", "Select one stable Map Control before identifying a Data Grid.");
        var (surfaceRef, variantRef) = ResolveSurfaceClosure(host, draft.Entities);
        if (!string.Equals(surfaceRef, request.SurfaceRef, StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(request.SurfaceVariantRef)
                && !string.Equals(variantRef, request.SurfaceVariantRef, StringComparison.Ordinal)))
            throw new UiMapException("MAP_GRID_HOST_SURFACE_MISMATCH", "The host Control is outside the selected Surface/variant context.");
        var source = draft.Sources.SingleOrDefault(value => value.SourceObservationSetRef == request.SourceObservationSetRef)
            ?? throw new UiMapException("MAP_GRID_HOST_SOURCE_MISSING", "The selected source observation is not attached to this draft.");
        var evidence = host.EvidenceLinks.Where(value => value.SourceObservationSetRef == source.SourceObservationSetRef).ToList();
        if (evidence.Count == 0)
            throw new UiMapException("MAP_GRID_HOST_SOURCE_MISSING", "The selected Control has no direct evidence in this observation.");
        if (evidence.Count != 1)
            throw new UiMapException("MAP_GRID_HOST_SOURCE_AMBIGUOUS", "The selected Control has multiple direct source objects in this observation.");
        var link = evidence[0];
        var surface = source.SurfaceObservations.SingleOrDefault(value =>
            value.SourceWorldObjectRef == link.SourceSurfaceWorldObjectRef
            && value.ResultingMapSurfaceRef == surfaceRef);
        if (surface is null)
            throw new UiMapException("MAP_GRID_HOST_SURFACE_MISMATCH", "The Control evidence does not resolve to the selected source Surface.");
        if (surface.Exposure == SurfaceExposure.OccludedBySameApplication)
            throw new UiMapException("MAP_GRID_HOST_CONTROL_OUT_OF_SCOPE", "An occluded-only source Surface cannot be selected for grid authoring.");
        if (link.BoundsPx is not { Width: > 0, Height: > 0 })
            throw new UiMapException("MAP_GRID_HOST_SOURCE_MISSING", "The selected Control has no defensible visual bounds in this observation.");
        var binding = new GridHostObservationBinding
        {
            BindingRef = _allocator.Allocate("grid-host-binding"),
            MapDraftRef = draft.MapDraftRef,
            ApplicationRef = draft.ApplicationRef,
            SurfaceRef = surfaceRef,
            SurfaceVariantRef = variantRef,
            HostControlRef = host.MapEntityRef,
            SourceObservationSetRef = source.SourceObservationSetRef,
            SourceHostWorldObjectRef = link.SourceObjectRef,
            SourceSurfaceWorldObjectRef = link.SourceSurfaceWorldObjectRef,
            SourcePublicationRef = source.PublicationRef,
            ScreenshotRef = source.PublicationRef,
            ScreenshotHash = source.SourcePayloadHash,
            HostBoundsPx = link.BoundsPx.Clone(),
        };
        binding.BindingHash = HashText(binding.ToByteArray());
        return new ResolveGridHostObservationResponse { Binding = binding };
    }

    public PromoteHostedGridCandidateResponse PromoteHostedGridCandidate(PromoteHostedGridCandidateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.CandidateRef)
            || request.Analysis is null)
            throw new UiMapException("MAP_GRID_PROMOTION_INVALID", "Promotion requires an explicit retained analysis and candidate ref.");
        var session = _store.LoadSession(request.MappingSessionRef);
        RequireSessionEtag(session, request.ExpectedSessionEtag);
        var draft = _store.LoadDraft(session.MapDraftRef);
        var declaration = draft.GridHostDeclarations.SingleOrDefault(value => value.DeclarationRef == request.DeclarationRef)
            ?? throw new UiMapException("MAP_GRID_DECLARATION_NOT_FOUND", "The selected grid-host declaration does not exist.");
        if (declaration.DeclarationVersion != request.ExpectedDeclarationVersion)
            throw new UiMapException("MAP_GRID_DECLARATION_CONFLICT", "The grid-host declaration changed; reload before promotion.");
        if (declaration.AssertionKind != GridHostAssertionKind.GridHostPresent)
            throw new UiMapException("MAP_GRID_PROMOTION_INVALID", "Only an accepted 'hosts grid' declaration can be promoted.");
        var host = draft.Entities.SingleOrDefault(value => value.MapEntityRef == declaration.HostControlRef)
            ?? throw new UiMapException("MAP_GRID_HOST_CONTROL_REQUIRED", "The declared host Control is missing.");
        if (HasUsableStructuredList(host, draft))
            throw new UiMapException("MAP_GRID_STRUCTURED_COLLECTION_AVAILABLE", "A usable structured List already exists under this host; visual reconstruction abstained.");
        if (!string.Equals(declaration.SourceObservationSetRef, request.SourceObservationSetRef, StringComparison.Ordinal))
            throw new UiMapException("MAP_GRID_HOST_SOURCE_MISSING", "Promotion must use the declaration's exact source observation.");
        var candidate = request.Analysis.GridCandidates.SingleOrDefault(value => value.CandidateRef == request.CandidateRef)
            ?? throw new UiMapException("MAP_GRID_CANDIDATE_NOT_FOUND", "The selected candidate is not present in the retained analysis.");
        if (request.Analysis.ReviewedImageSchema is { } reviewedImage)
        {
            UIMaps.Protocol.ReviewedGridImageSchemaValidation.Validate(reviewedImage);
            if (!reviewedImage.Columns.SequenceEqual(candidate.ColumnCandidates))
                throw new UiMapException("MAP_GRID_SCHEMA_INVALID", "Promoted columns differ from the reviewed physical schema.");
        }
        var existingGrids = draft.Entities.Where(value => value.EntityKind == MapEntityKind.MapDataGrid
                && (string.Equals(value.MapEntityRef, declaration.DataGridRef, StringComparison.Ordinal)
                    || string.Equals(value.HostedDataGrid?.DeclarationRef, declaration.DeclarationRef, StringComparison.Ordinal)))
            .DistinctBy(value => value.MapEntityRef, StringComparer.Ordinal)
            .ToList();
        if (existingGrids.Count > 1)
            throw new UiMapException("MAP_GRID_REPAIR_AMBIGUOUS", "More than one reviewed Data Grid is bound to this declaration; header repair abstained.");
        if (existingGrids.Count == 1)
            return RepairHostedGridCandidate(session, draft, declaration, host, existingGrids[0], candidate, request);

        var now = Now();
        var gridRef = _allocator.Allocate("map-data-grid");
        var grid = new MapEntity
        {
            MapEntityRef = gridRef,
            EntityKind = MapEntityKind.MapDataGrid,
            ApplicationRef = draft.ApplicationRef,
            ParentEntityRef = host.MapEntityRef,
            DisplayName = string.IsNullOrWhiteSpace(request.ReviewedGridName) ? $"{host.DisplayName} Data Grid" : request.ReviewedGridName.Trim(),
            Role = "DataGrid",
            Applicable = true,
            ReviewState = ReviewState.ReviewAccepted,
            HostedDataGrid = new HostedDataGridDescriptor
            {
                HostControlRef = host.MapEntityRef,
                GridMapRevisionRef = "uimaps:grid-map-revision:sha256:" + HashText(request.Analysis.ToByteArray()),
                SourceGridRef = candidate.CandidateRef,
                ExpectedRelativeBounds = candidate.BoundsPx?.Clone(),
                DeclarationRef = declaration.DeclarationRef,
                KnowledgeStatus = HostedGridKnowledgeStatus.HostedGridKnowledgePresent,
                IdentificationStatus = GridIdentificationStatus.GridIdentificationReviewed,
                SchemaStatus = candidate.ColumnCandidates.Count > 0 ? GridSchemaStatus.GridSchemaReviewed : GridSchemaStatus.GridSchemaPartial,
                SourceObservationSetRef = request.SourceObservationSetRef,
                SourceAnalysisRef = request.Analysis.AnalysisRef,
                SourceCandidateRef = candidate.CandidateRef,
                ReviewedImageSchema = request.Analysis.ReviewedImageSchema?.Clone(),
            },
        };
        grid.HostedDataGrid.ProviderCapabilities.Add(GridProviderCapabilityKind.GridProviderVisualReconstruction);
        grid.HostedDataGrid.ApplicableSurfaceVariantRefs.AddRange(request.ApplicableSurfaceVariantRefs.Count > 0
            ? request.ApplicableSurfaceVariantRefs
            : declaration.SurfaceVariantRefs);
        var hostEvidence = host.EvidenceLinks.SingleOrDefault(value => value.SourceObservationSetRef == request.SourceObservationSetRef);
        if (hostEvidence is not null)
        {
            var link = hostEvidence.Clone();
            link.EvidenceLinkRef = _allocator.Allocate("map-evidence");
            link.SourceObjectRef = candidate.CandidateRef;
            link.EvidenceFamily = "reviewed-grid-identification";
            link.BoundsPx = candidate.BoundsPx?.Clone();
            link.Limitations.Clear();
            grid.EvidenceLinks.Add(link);
        }
        draft.Entities.Add(grid);
        draft.Relationships.Add(new MapRelationship
        {
            MapRelationshipRef = _allocator.Allocate("map-relationship"),
            RelationshipKind = MapRelationshipKind.MapHostsDataGrid,
            FromEntityRef = host.MapEntityRef,
            ToEntityRef = gridRef,
            ReviewState = ReviewState.ReviewAccepted,
        });

        var columnRefs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidateColumn in candidate.ColumnCandidates.OrderBy(value => value.Ordinal))
        {
            var columnRef = _allocator.Allocate("map-grid-column");
            columnRefs[candidateColumn.CandidateRef] = columnRef;
            var displayName = GridColumnHeaderResolver.Resolve(request.Analysis, candidateColumn);
            grid.HostedDataGrid.ExpectedHeaders.Add(displayName);
            draft.Entities.Add(new MapEntity
            {
                MapEntityRef = columnRef,
                EntityKind = MapEntityKind.MapGridColumn,
                ApplicationRef = draft.ApplicationRef,
                ParentEntityRef = gridRef,
                DisplayName = displayName,
                Role = "GridColumn",
                Applicable = true,
                ReviewState = ReviewState.ReviewAccepted,
                ReviewedOrder = candidateColumn.Ordinal,
            });
            draft.Relationships.Add(new MapRelationship
            {
                MapRelationshipRef = _allocator.Allocate("map-relationship"),
                RelationshipKind = MapRelationshipKind.MapHasColumn,
                FromEntityRef = gridRef,
                ToEntityRef = columnRef,
                ReviewState = ReviewState.ReviewAccepted,
            });
        }

        ApplyExplorationReceipt(grid.HostedDataGrid, request, gridRef, host.MapEntityRef);

        MarkGridInternalControls(draft, host.MapEntityRef, gridRef, candidate.BoundsPx);
        declaration.DataGridRef = gridRef;
        declaration.GridRegionRef = request.Analysis.RegionRef;
        declaration.ReviewState = ReviewState.ReviewAccepted;
        declaration.ReviewerIdentity = request.ReviewerIdentity;
        declaration.ReviewedAt = now;
        declaration.UpdatedAt = now;
        declaration.DeclarationVersion++;

        var observation = BuildHostedGridObservation(draft, declaration, grid, candidate, request.Analysis, columnRefs);
        draft.HostedGridObservations.Add(observation);
        draft.Version++;
        UiMapConstructionEngine.UpdateEtag(draft, now);
        session.Version++;
        session.UpdatedAt = now;
        session.Status = MappingSessionStatus.MappingSessionPartial;
        session.StatusMessage = $"Reviewed Data Grid '{grid.DisplayName}' promoted under {host.DisplayName}.";
        UpdateSessionEtag(session);
        _store.SaveSessionAndDraft(session, draft);
        Audit("hosted-grid-promoted", gridRef, request.ReviewerIdentity, now,
            "A reviewed retained candidate was promoted; no target input was available.");
        var response = new PromoteHostedGridCandidateResponse
        {
            Session = session.Clone(), Draft = draft.Clone(), Declaration = declaration.Clone(), DataGridRef = gridRef,
        };
        response.GridColumnRefs.AddRange(columnRefs.Values);
        return response;
    }

    private PromoteHostedGridCandidateResponse RepairHostedGridCandidate(
        MappingSessionDescriptor session,
        UiMapDraft draft,
        DataGridHostDeclaration declaration,
        MapEntity host,
        MapEntity grid,
        UIMaps.Protocol.V2_1.DataGridCandidate candidate,
        PromoteHostedGridCandidateRequest request)
    {
        var candidateColumns = candidate.ColumnCandidates.OrderBy(value => value.Ordinal).ToList();
        var existingColumns = draft.Entities.Where(value => value.EntityKind == MapEntityKind.MapGridColumn
                && string.Equals(value.ParentEntityRef, grid.MapEntityRef, StringComparison.Ordinal))
            .OrderBy(value => value.ReviewedOrder)
            .ToList();
        if (candidateColumns.Select(value => value.Ordinal).Distinct().Count() != candidateColumns.Count
            || existingColumns.Select(value => value.ReviewedOrder).Distinct().Count() != existingColumns.Count
            || !candidateColumns.Select(value => value.Ordinal).SequenceEqual(Enumerable.Range(0, candidateColumns.Count))
            || !existingColumns.Select(value => value.ReviewedOrder).SequenceEqual(Enumerable.Range(0, existingColumns.Count))
            || candidateColumns.Count < existingColumns.Count)
            throw new UiMapException("MAP_GRID_SCHEMA_REPAIR_CONFLICT", "The reviewed Data Grid schema is not a monotonic ordinal extension of the retained schema; repair made no changes.");

        var hostRelationships = draft.Relationships.Where(value => value.RelationshipKind == MapRelationshipKind.MapHostsDataGrid
                && string.Equals(value.FromEntityRef, host.MapEntityRef, StringComparison.Ordinal)
                && string.Equals(value.ToEntityRef, grid.MapEntityRef, StringComparison.Ordinal))
            .ToList();
        if (hostRelationships.Count != 1)
            throw new UiMapException("MAP_GRID_REPAIR_RELATIONSHIP_INVALID", "Header repair requires exactly one existing HOSTS_DATA_GRID relationship.");

        var descriptor = grid.HostedDataGrid ??= new HostedDataGridDescriptor();
        descriptor.HostControlRef = host.MapEntityRef;
        descriptor.GridMapRevisionRef = "uimaps:grid-map-revision:sha256:" + HashText(request.Analysis.ToByteArray());
        descriptor.SourceGridRef = candidate.CandidateRef;
        descriptor.ExpectedRelativeBounds = candidate.BoundsPx?.Clone();
        descriptor.DeclarationRef = declaration.DeclarationRef;
        descriptor.KnowledgeStatus = HostedGridKnowledgeStatus.HostedGridKnowledgePresent;
        descriptor.IdentificationStatus = GridIdentificationStatus.GridIdentificationReviewed;
        descriptor.SchemaStatus = candidateColumns.Count > 0 ? GridSchemaStatus.GridSchemaReviewed : GridSchemaStatus.GridSchemaPartial;
        descriptor.SourceObservationSetRef = request.SourceObservationSetRef;
        descriptor.SourceAnalysisRef = request.Analysis.AnalysisRef;
        descriptor.SourceCandidateRef = candidate.CandidateRef;
        descriptor.ReviewedImageSchema = request.Analysis.ReviewedImageSchema?.Clone();
        if (!descriptor.ProviderCapabilities.Contains(GridProviderCapabilityKind.GridProviderVisualReconstruction))
            descriptor.ProviderCapabilities.Add(GridProviderCapabilityKind.GridProviderVisualReconstruction);
        foreach (var variantRef in request.ApplicableSurfaceVariantRefs)
            if (!descriptor.ApplicableSurfaceVariantRefs.Contains(variantRef, StringComparer.Ordinal))
                descriptor.ApplicableSurfaceVariantRefs.Add(variantRef);

        descriptor.ExpectedHeaders.Clear();
        var columnRefs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < candidateColumns.Count; index++)
        {
            var candidateColumn = candidateColumns[index];
            var displayName = GridColumnHeaderResolver.Resolve(request.Analysis, candidateColumn);
            MapEntity column;
            if (index < existingColumns.Count)
            {
                column = existingColumns[index];
                column.DisplayName = displayName;
                column.Role = "GridColumn";
                column.Applicable = true;
                column.ReviewState = ReviewState.ReviewAccepted;
                column.ReviewedOrder = candidateColumn.Ordinal;
            }
            else
            {
                column = new MapEntity
                {
                    MapEntityRef = _allocator.Allocate("map-grid-column"),
                    EntityKind = MapEntityKind.MapGridColumn,
                    ApplicationRef = draft.ApplicationRef,
                    ParentEntityRef = grid.MapEntityRef,
                    DisplayName = displayName,
                    Role = "GridColumn",
                    Applicable = true,
                    ReviewState = ReviewState.ReviewAccepted,
                    ReviewedOrder = candidateColumn.Ordinal,
                };
                draft.Entities.Add(column);
                existingColumns.Add(column);
                draft.Relationships.Add(new MapRelationship
                {
                    MapRelationshipRef = _allocator.Allocate("map-relationship"),
                    RelationshipKind = MapRelationshipKind.MapHasColumn,
                    FromEntityRef = grid.MapEntityRef,
                    ToEntityRef = column.MapEntityRef,
                    ReviewState = ReviewState.ReviewAccepted,
                });
            }
            descriptor.ExpectedHeaders.Add(displayName);
            columnRefs[candidateColumn.CandidateRef] = column.MapEntityRef;
        }

        ApplyExplorationReceipt(descriptor, request, grid.MapEntityRef, host.MapEntityRef);

        if (!string.IsNullOrWhiteSpace(request.ReviewedGridName)) grid.DisplayName = request.ReviewedGridName.Trim();
        grid.ReviewState = ReviewState.ReviewAccepted;
        var hostEvidence = host.EvidenceLinks.SingleOrDefault(value => value.SourceObservationSetRef == request.SourceObservationSetRef);
        if (hostEvidence is not null && grid.EvidenceLinks.All(value => value.SourceObjectRef != candidate.CandidateRef))
        {
            var link = hostEvidence.Clone();
            link.EvidenceLinkRef = _allocator.Allocate("map-evidence");
            link.SourceObjectRef = candidate.CandidateRef;
            link.EvidenceFamily = "reviewed-grid-identification";
            link.BoundsPx = candidate.BoundsPx?.Clone();
            link.Limitations.Clear();
            grid.EvidenceLinks.Add(link);
        }

        var now = Now();
        MarkGridInternalControls(draft, host.MapEntityRef, grid.MapEntityRef, candidate.BoundsPx);
        declaration.DataGridRef = grid.MapEntityRef;
        declaration.GridRegionRef = request.Analysis.RegionRef;
        declaration.ReviewState = ReviewState.ReviewAccepted;
        declaration.ReviewerIdentity = request.ReviewerIdentity;
        declaration.ReviewedAt = now;
        declaration.UpdatedAt = now;
        declaration.DeclarationVersion++;
        if (candidate.VisibleRowCandidates.Count > 0)
            draft.HostedGridObservations.Add(BuildHostedGridObservation(
                draft, declaration, grid, candidate, request.Analysis, columnRefs));
        draft.Version++;
        UiMapConstructionEngine.UpdateEtag(draft, now);
        session.Version++;
        session.UpdatedAt = now;
        session.Status = MappingSessionStatus.MappingSessionPartial;
        session.StatusMessage = $"Reviewed Data Grid '{grid.DisplayName}' schema repaired or extended from retained observation evidence.";
        UpdateSessionEtag(session);
        _store.SaveSessionAndDraft(session, draft);
        Audit("hosted-grid-schema-repaired", grid.MapEntityRef, request.ReviewerIdentity, now,
            "Reviewed headers were corrected and newly discovered suffix columns were appended; stable existing refs were preserved.");
        var response = new PromoteHostedGridCandidateResponse
        {
            Session = session.Clone(),
            Draft = draft.Clone(),
            Declaration = declaration.Clone(),
            DataGridRef = grid.MapEntityRef,
        };
        response.GridColumnRefs.AddRange(existingColumns.Select(value => value.MapEntityRef));
        return response;
    }

    public GetHostedDataGridKnowledgeResponse GetHostedDataGridKnowledge(GetHostedDataGridKnowledgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var graph = LoadGraph(request.MapRef);
        var grids = graph.Entities.Where(value => value.EntityKind == MapEntityKind.MapDataGrid
                && value.HostedDataGrid?.HostControlRef == request.HostControlRef
                && (string.IsNullOrWhiteSpace(request.SurfaceVariantRef)
                    || value.HostedDataGrid.ApplicableSurfaceVariantRefs.Count == 0
                    || value.HostedDataGrid.ApplicableSurfaceVariantRefs.Contains(request.SurfaceVariantRef, StringComparer.Ordinal)))
            .ToList();
        var assertions = graph.Declarations.Where(value => value.HostControlRef == request.HostControlRef
                && (string.IsNullOrWhiteSpace(request.SurfaceVariantRef)
                    || value.SurfaceVariantRefs.Count == 0
                    || value.SurfaceVariantRefs.Contains(request.SurfaceVariantRef, StringComparer.Ordinal)))
            .ToList();
        var response = new GetHostedDataGridKnowledgeResponse { NotAuthorization = true };
        response.DataGridRefs.AddRange(grids.Select(value => value.MapEntityRef));
        response.Assertions.AddRange(assertions.Select(value => value.Clone()));
        response.Status = grids.Count > 0 || assertions.Any(value => value.AssertionKind == GridHostAssertionKind.GridHostPresent)
            ? HostedGridKnowledgeStatus.HostedGridKnowledgePresent
            : assertions.Any(value => value.AssertionKind == GridHostAssertionKind.GridHostAbsent)
                ? HostedGridKnowledgeStatus.HostedGridKnowledgeAbsent
                : HostedGridKnowledgeStatus.HostedGridKnowledgeUnknown;
        if (response.Status == HostedGridKnowledgeStatus.HostedGridKnowledgeUnknown)
            response.Limitations.Add("No accepted present/absent assertion exists for this stable host and variant scope.");
        return response;
    }

    public GetHostedDataGridMetadataResponse GetHostedDataGridMetadata(GetHostedDataGridMetadataRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var graph = LoadGraph(request.MapRef);
        var grid = graph.Entities.SingleOrDefault(value => value.MapEntityRef == request.DataGridRef
            && value.EntityKind == MapEntityKind.MapDataGrid)
            ?? throw new UiMapException("MAP_GRID_NOT_FOUND", "The requested Data Grid is not present in this Map.");
        var response = new GetHostedDataGridMetadataResponse
        {
            DataGrid = grid.Clone(),
            HostRelationship = graph.Relationships.SingleOrDefault(value => value.RelationshipKind == MapRelationshipKind.MapHostsDataGrid
                && value.ToEntityRef == grid.MapEntityRef)?.Clone(),
            MapPayloadHash = graph.PayloadHash,
            NotAuthorization = true,
        };
        response.Columns.AddRange(graph.Entities.Where(value => value.EntityKind == MapEntityKind.MapGridColumn
            && value.ParentEntityRef == grid.MapEntityRef).OrderBy(value => value.ReviewedOrder).Select(value => value.Clone()));
        return response;
    }

    public GetObservedDataGridMetadataResponse GetObservedDataGridMetadata(GetObservedDataGridMetadataRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var draft = _store.LoadDraft(request.MapDraftRef);
        var observation = draft.HostedGridObservations.SingleOrDefault(value => value.GridObservationRef == request.GridObservationRef)
            ?? throw new UiMapException("MAP_GRID_OBSERVATION_NOT_FOUND", "The requested retained Data Grid observation was not found.");
        return new GetObservedDataGridMetadataResponse { Observation = observation.Clone() };
    }

    public GetObservedDataGridContentsResponse GetObservedDataGridContents(GetObservedDataGridContentsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ReadScope == GridReadScope.Unspecified)
            throw new UiMapException("MAP_GRID_READ_SCOPE_REQUIRED", "An explicit read scope is required.");
        var observation = GetObservedDataGridMetadata(new GetObservedDataGridMetadataRequest
        {
            MapDraftRef = request.MapDraftRef, GridObservationRef = request.GridObservationRef,
        }).Observation;
        var response = new GetObservedDataGridContentsResponse
        {
            GridObservationRef = observation.GridObservationRef,
            RequestedScope = request.ReadScope,
            EffectiveScope = request.ReadScope == GridReadScope.GridReadRetainedProviderSnapshot
                ? GridReadScope.GridReadVisibleViewport : request.ReadScope,
            NotAuthorization = true,
        };
        response.Coverage.AddRange(observation.Coverage);
        if (response.EffectiveScope != GridReadScope.GridReadMetadataOnly)
            response.Rows.AddRange(observation.DataGridInstance?.VisibleRows.Select(value => value.Clone()) ?? []);
        if (request.ReadScope == GridReadScope.GridReadRetainedProviderSnapshot
            && !observation.Coverage.Contains(GridCoverageKind.GridCoverageProviderEnumerated))
            response.Limitations.Add("Provider-wide enumeration was not retained; returning the visible viewport only.");
        response.Limitations.AddRange(observation.Limitations);
        return response;
    }

    public ListMapLibraryResponse ListLibrary(ListMapLibraryRequest request)
    {
        request ??= new ListMapLibraryRequest();
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 100 : request.PageSize, 1, 500);
        var offset = DecodeOffset(request.ContinuationToken);
        var query = request.Query?.Trim() ?? "";
        var allSessions = _store.ListSessions();
        var sessions = allSessions
            .Where(x => string.IsNullOrWhiteSpace(query)
                || x.Target.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.Target.ApplicationRef.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.MappingSessionRef.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var response = new ListMapLibraryResponse();
        var archivedRefs = _store.ListArchivedLibraryEntryRefs();
        var readySelections = _store.ListReadyMapSelections();
        var entries = new List<MapLibraryEntry>();
        foreach (var session in sessions)
        {
            var target = session.Target ?? throw new UiMapException("MAP_SESSION_TARGET_REQUIRED", "A stored Mapping Session has no target application identity.");
            var applicationVersion = target.ApplicationVersion?.Trim() ?? string.Empty;
            var entry = new MapLibraryEntry
            {
                ApplicationRef = target.ApplicationRef,
                ApplicationDisplayName = target.DisplayName,
                ApplicationVersion = applicationVersion,
                MappingSessionRef = session.MappingSessionRef,
                SessionStatus = session.Status,
                MapDraftRef = session.MapDraftRef,
                LatestMapRevisionRef = session.PublishedMapRevisionRef,
                UpdatedAt = session.UpdatedAt,
                BlockingReviewCount = session.BlockingReviewCount,
                Warnings = { session.Warnings },
                Archived = archivedRefs.Contains(session.MappingSessionRef),
                IsReady = !string.IsNullOrWhiteSpace(session.PublishedMapRevisionRef)
                    && readySelections.TryGetValue((target.ApplicationRef, applicationVersion), out var selectedRevisionRef)
                    && string.Equals(selectedRevisionRef, session.PublishedMapRevisionRef, StringComparison.Ordinal),
            };
            entries.Add(entry);
        }
        var sessionRevisionRefs = allSessions.Select(x => x.PublishedMapRevisionRef).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.Ordinal);
        entries.AddRange(_store.ListRevisions().Where(x => !sessionRevisionRefs.Contains(x.MapRevisionRef))
            .Where(x => string.IsNullOrWhiteSpace(query)
                || x.Target.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.ApplicationRef.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.MapRevisionRef.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(revision => new MapLibraryEntry
            {
                ApplicationRef = revision.ApplicationRef,
                ApplicationDisplayName = revision.Target.DisplayName,
                ApplicationVersion = revision.Target?.ApplicationVersion?.Trim() ?? string.Empty,
                SessionStatus = MappingSessionStatus.MappingSessionMapReady,
                LatestMapRevisionRef = revision.MapRevisionRef,
                UpdatedAt = revision.PublishedAt,
                Archived = archivedRefs.Contains(revision.MapRevisionRef),
                IsReady = readySelections.TryGetValue((revision.ApplicationRef, revision.Target?.ApplicationVersion?.Trim() ?? string.Empty), out var selectedRevisionRef)
                    && string.Equals(selectedRevisionRef, revision.MapRevisionRef, StringComparison.Ordinal),
                Warnings = { "Imported immutable revision; no local Mapping Session draft is attached." },
            }));
        if (!request.IncludeArchived) entries = entries.Where(value => !value.Archived).ToList();
        response.Entries.AddRange(entries.OrderByDescending(x => x.UpdatedAt, StringComparer.Ordinal).ThenBy(x => x.LatestMapRevisionRef, StringComparer.Ordinal).Skip(offset).Take(pageSize));
        if (offset + response.Entries.Count < entries.Count) response.ContinuationToken = Convert.ToBase64String(BitConverter.GetBytes(offset + response.Entries.Count));
        return response;
    }

    public ArchiveMapLibraryEntriesResponse ArchiveLibraryEntries(ArchiveMapLibraryEntriesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.ArchivedBy))
            throw new UiMapException("MAP_LIBRARY_ARCHIVE_INVALID", "A request id and archiving identity are required.");
        var now = Now();
        var response = _store.ArchiveLibraryEntries(request.Entries, request.ArchivedBy, now);
        foreach (var subjectRef in response.ArchivedMappingSessionRefs.Concat(response.ArchivedMapRevisionRefs))
            Audit("map-library-entry-archived", subjectRef, request.ArchivedBy, now, "A Map Library entry was archived without deleting its artifacts.");
        return response;
    }

    public RestoreMapLibraryEntriesResponse RestoreLibraryEntries(RestoreMapLibraryEntriesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.RestoredBy))
            throw new UiMapException("MAP_LIBRARY_RESTORE_INVALID", "A request id and restoring identity are required.");
        var now = Now();
        var response = _store.RestoreLibraryEntries(request.Entries, now);
        foreach (var subjectRef in response.RestoredMappingSessionRefs.Concat(response.RestoredMapRevisionRefs))
            Audit("map-library-entry-restored", subjectRef, request.RestoredBy, now, "An archived Map Library entry was restored to the active library.");
        return response;
    }

    public SetMapLibraryReadinessResponse SetLibraryReadiness(SetMapLibraryReadinessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.ChangedBy) || request.Entry is null)
            throw new UiMapException("MAP_LIBRARY_READINESS_INVALID", "A request id, library entry, and acting identity are required.");

        string revisionRef;
        if (!string.IsNullOrWhiteSpace(request.Entry.MappingSessionRef))
        {
            var session = _store.LoadSession(request.Entry.MappingSessionRef);
            revisionRef = session.PublishedMapRevisionRef;
        }
        else
        {
            revisionRef = request.Entry.LatestMapRevisionRef;
        }
        if (string.IsNullOrWhiteSpace(revisionRef))
            throw new UiMapException("MAP_LIBRARY_READY_REVISION_REQUIRED", "Publish the Map draft before selecting it as Ready.");

        var revision = _store.LoadRevision(revisionRef);
        var now = Now();
        var response = _store.SetLibraryReadiness(revision, request.Ready, request.ChangedBy, now);
        Audit(
            request.Ready ? "map-library-entry-marked-ready" : "map-library-entry-marked-draft",
            revision.MapRevisionRef,
            request.ChangedBy,
            now,
            request.Ready ? "The Map revision was selected as Ready for its application version." : "The Map revision's Ready designation was cleared.");
        return response;
    }

    public DeleteMapLibraryEntriesResponse DeleteLibraryEntries(DeleteMapLibraryEntriesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.DeletedBy))
            throw new UiMapException("MAP_LIBRARY_DELETE_INVALID", "A request id and deleting identity are required.");
        var response = _store.DeleteLibraryEntries(request.Entries);
        foreach (var subjectRef in response.DeletedMappingSessionRefs.Concat(response.DeletedMapRevisionRefs))
            Audit("map-library-entry-deleted", subjectRef, request.DeletedBy, Now(), "A selected Map Library entry was permanently deleted.");
        return response;
    }

    public ApplyReviewCommandResponse ApplyReviewCommand(ReviewCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.RequestId) || string.IsNullOrWhiteSpace(command.ReviewedBy))
            throw new UiMapException("MAP_DRAFT_COMMAND_INVALID", "Request id and reviewer identity are required.");
        var current = _store.LoadDraft(command.MapDraftRef);
        RequireDraftEtag(current, command.ExpectedEtag);
        EnsureReviewHistory(current.MapDraftRef);
        var now = Now();
        var before = current.Clone();
        var affectedRefs = command.CommandKind == ReviewCommandKind.ReviewAcceptAllBlocking
            ? current.ReviewItems.Where(x => x.Blocking && x.ReviewState == ReviewState.ReviewUnreviewed)
                .Select(x => x.SubjectRef).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
            : command.CommandKind == ReviewCommandKind.ReviewSetAlias && command.Aliases.Count > 0
                ? command.Aliases.Select(value => value.FromEntityRef).Distinct(StringComparer.Ordinal).ToList()
                : string.IsNullOrWhiteSpace(command.SubjectRef) ? [] : new List<string> { command.SubjectRef };

        if (command.CommandKind == ReviewCommandKind.ReviewUndo)
            current = RestoreSnapshot(current, _undo, _redo, "MAP_DRAFT_UNDO_UNAVAILABLE");
        else if (command.CommandKind == ReviewCommandKind.ReviewRedo)
            current = RestoreSnapshot(current, _redo, _undo, "MAP_DRAFT_REDO_UNAVAILABLE");
        else
        {
            ApplyCommand(current, command, now);
            _undo.GetOrAdd(current.MapDraftRef, _ => new Stack<UiMapDraft>()).Push(before);
            _redo.GetOrAdd(current.MapDraftRef, _ => new Stack<UiMapDraft>()).Clear();
        }

        current.Version = before.Version + 1;
        UiMapConstructionEngine.UpdateEtag(current, now);
        var commandRef = _allocator.Allocate("review-command");
        var result = new ReviewCommandResult
        {
            ReviewCommandRef = commandRef,
            MapDraftRef = current.MapDraftRef,
            Etag = current.Etag,
            Version = current.Version,
            UndoAvailable = _undo.TryGetValue(current.MapDraftRef, out var undo) && undo.Count > 0,
        };
        result.AffectedRefs.AddRange(affectedRefs);
        var session = _store.LoadSession(current.MappingSessionRef);
        session.BlockingReviewCount = current.ReviewItems.Count(x => x.Blocking && x.ReviewState == ReviewState.ReviewUnreviewed);
        session.Status = session.BlockingReviewCount == 0 ? MappingSessionStatus.MappingSessionMapReady : MappingSessionStatus.MappingSessionPartial;
        session.StatusMessage = session.BlockingReviewCount == 0
            ? "Draft review is complete and ready for validation."
            : "Draft contains blocking review items.";
        session.Version++;
        session.UpdatedAt = now;
        UpdateSessionEtag(session);
        _store.CommitReview(
            session,
            current,
            commandRef,
            command,
            result,
            _undo.GetOrAdd(current.MapDraftRef, _ => new Stack<UiMapDraft>()),
            _redo.GetOrAdd(current.MapDraftRef, _ => new Stack<UiMapDraft>()),
            now);
        Audit("review-command", current.MapDraftRef, command.ReviewedBy, now, $"{command.CommandKind} applied to {command.SubjectRef}.");
        return new ApplyReviewCommandResponse { Result = result, Draft = current.Clone() };
    }

    public MapValidationReport ValidateDraft(string draftRef, string expectedEtag)
    {
        var draft = _store.LoadDraft(draftRef);
        RequireDraftEtag(draft, expectedEtag);
        var report = UiMapValidator.Validate(draft, _allocator.Allocate("validation-run"), Now());
        _store.SaveValidation(report);
        Audit("draft-validated", draftRef, "map-service", report.CompletedAt, $"Draft validation completed with {report.Findings.Count} finding(s).");
        return report.Clone();
    }

    public PublishMapDraftResponse Publish(PublishMapDraftRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.PublishedBy))
            throw new UiMapException("MAP_DRAFT_COMMAND_INVALID", "Request id and publisher identity are required.");
        var draft = _store.LoadDraft(request.MapDraftRef);
        RequireDraftEtag(draft, request.ExpectedEtag);
        var validation = _store.LoadValidation(request.ValidationRunRef);
        if (!validation.PublicationEligible || validation.DraftEtag != draft.Etag)
            throw new UiMapException("MAP_VALIDATION_STALE", "Publication requires a successful validation of the exact current draft.");
        var session = _store.LoadSession(draft.MappingSessionRef);
        if (session.BlockingReviewCount != 0)
            throw new UiMapException("MAP_VALIDATION_BLOCKED", "Blocking review items remain.");

        var now = Now();
        var revision = new UiMapRevision
        {
            SchemaVersion = "3.3",
            ApplicationRef = draft.ApplicationRef,
            Target = session.Target.Clone(),
            ValidationRunRef = validation.ValidationRunRef,
            PublishedBy = request.PublishedBy,
            PublishedAt = now,
            Immutable = true,
            NotAuthorization = true,
        };
        if (!string.IsNullOrWhiteSpace(draft.BaseMapRevisionRef)) revision.ParentRevisionRefs.Add(draft.BaseMapRevisionRef);
        revision.Sources.AddRange(draft.Sources.Select(x => x.Clone()));
        revision.Entities.AddRange(draft.Entities.Where(x => x.ReviewState != ReviewState.ReviewRejected).Select(x => x.Clone()));
        var included = revision.Entities.Select(x => x.MapEntityRef).ToHashSet(StringComparer.Ordinal);
        var reviewedInteractions = draft.Interactions
            .Where(x => x.ReviewState is ReviewState.ReviewAccepted or ReviewState.ReviewCorrected)
            .OrderBy(x => x.SequenceNumber).ThenBy(x => x.InteractionRef, StringComparer.Ordinal).ToList();
        var includedInteractionRefs = reviewedInteractions.Select(x => x.InteractionRef).ToHashSet(StringComparer.Ordinal);
        revision.Relationships.AddRange(draft.Relationships.Where(x => included.Contains(x.FromEntityRef) && included.Contains(x.ToEntityRef)
            && (string.IsNullOrWhiteSpace(x.InteractionRef) || includedInteractionRefs.Contains(x.InteractionRef))).Select(x => x.Clone()));
        revision.DispositionSummary.AddRange(draft.Dispositions.Select(x => x.Clone()));
        revision.Interactions.AddRange(reviewedInteractions.Select(x => x.Clone()));
        revision.GridHostDeclarations.AddRange(draft.GridHostDeclarations.Where(value =>
            value.ReviewState is ReviewState.ReviewAccepted or ReviewState.ReviewCorrected).Select(value => value.Clone()));
        revision.EvidenceStreamSelections.AddRange(draft.EvidenceStreamSelections.Select(value => value.Clone()));
        revision.Limitations.AddRange(draft.Limitations);
        UiMapCanonicalizer.Sort(revision);
        revision.CanonicalPayloadHash = UiMapStore.Hash(UiMapCanonicalizer.SerializeCanonical(revision));
        revision.MapRevisionRef = "uimaps:map-revision:sha256:" + revision.CanonicalPayloadHash;
        _store.SaveRevision(revision);

        session.Status = MappingSessionStatus.MappingSessionMapReady;
        session.PublishedMapRevisionRef = revision.MapRevisionRef;
        session.StatusMessage = "Immutable UI Map revision published.";
        session.Version++;
        session.UpdatedAt = now;
        UpdateSessionEtag(session);
        _store.SaveSessionAndDraft(session, draft);
        Audit("map-published", revision.MapRevisionRef, request.PublishedBy, now, "Immutable UI Map revision published; no target action authorized.");
        return new PublishMapDraftResponse { Revision = revision.Clone(), Session = session.Clone() };
    }

    public MappingSessionDescriptor SetSessionStatus(string sessionRef, string expectedEtag, MappingSessionStatus status, string message)
    {
        var session = _store.LoadSession(sessionRef);
        RequireSessionEtag(session, expectedEtag);
        var current = session.Status;
        if (status == MappingSessionStatus.MappingSessionPaused
            && current is not (MappingSessionStatus.MappingSessionReady or MappingSessionStatus.MappingSessionPartial or MappingSessionStatus.MappingSessionMapReady))
            throw new UiMapException("MAP_SESSION_INVALID_STATE", "Only a ready, partial, or Map-ready session can be paused.");
        if (status == MappingSessionStatus.MappingSessionReady && current != MappingSessionStatus.MappingSessionPaused)
            throw new UiMapException("MAP_SESSION_INVALID_STATE", "Only a paused session can be resumed.");
        if (status == MappingSessionStatus.MappingSessionCancelled
            && current is MappingSessionStatus.MappingSessionCancelled or MappingSessionStatus.MappingSessionFailed)
            throw new UiMapException("MAP_SESSION_INVALID_STATE", "A terminal Mapping Session cannot be cancelled again.");
        if (status is not (MappingSessionStatus.MappingSessionPaused or MappingSessionStatus.MappingSessionCancelled or MappingSessionStatus.MappingSessionReady))
            throw new UiMapException("MAP_SESSION_INVALID_STATE", "Only pause, cancel, or resume transitions are accepted by this operation.");
        session.Status = status == MappingSessionStatus.MappingSessionReady && current == MappingSessionStatus.MappingSessionPaused
            ? !string.IsNullOrWhiteSpace(session.PublishedMapRevisionRef) || (session.AttachedSourceCount > 0 && session.BlockingReviewCount == 0)
                ? MappingSessionStatus.MappingSessionMapReady
                : session.AttachedSourceCount > 0 ? MappingSessionStatus.MappingSessionPartial : MappingSessionStatus.MappingSessionReady
            : status;
        session.StatusMessage = message;
        session.Version++;
        session.UpdatedAt = Now();
        UpdateSessionEtag(session);
        _store.SaveSessionAndDraft(session, _store.LoadDraft(session.MapDraftRef));
        Audit("mapping-session-status", sessionRef, session.PrivacyScope.AcknowledgedBy, session.UpdatedAt, $"Mapping Session status changed to {status}.");
        return session.Clone();
    }

    public CompatibilityReport Compare(string oldRevisionRef, string newRevisionRef)
    {
        var oldRevision = _store.LoadRevision(oldRevisionRef);
        var newRevision = _store.LoadRevision(newRevisionRef);
        if (!string.Equals(oldRevision.ApplicationRef, newRevision.ApplicationRef, StringComparison.Ordinal))
            throw new UiMapException("MAP_SESSION_TARGET_MISMATCH", "Compatibility comparison requires revisions for the same application.");
        var report = UiMapCompatibility.Compare(oldRevision, newRevision, _allocator.Allocate("compatibility-report"), Now());
        _store.SaveCompatibility(report);
        return report.Clone();
    }

    public ExportMapRevisionResponse Export(string revisionRef, bool includeSensitiveEvidence)
    {
        var revision = _store.LoadRevision(revisionRef);
        if (includeSensitiveEvidence)
            throw new UiMapException("MAP_EXPORT_EVIDENCE_SCOPE_REQUIRED", "Phase 3 export does not inline sensitive source evidence.");
        var bytes = revision.ToByteArray();
        return new ExportMapRevisionResponse
        {
            PackageBytes = ByteString.CopyFrom(bytes),
            PackageHash = UiMapStore.Hash(bytes),
            Warnings = { "Source screenshots and raw evidence are excluded; exact source refs and limitations remain." },
        };
    }

    public ImportMapRevisionResponse Import(ByteString packageBytes, bool commit)
    {
        if (packageBytes is null || packageBytes.Length == 0 || packageBytes.Length > 128 * 1024 * 1024)
            throw new UiMapException("MAP_IMPORT_PACKAGE_INVALID", "The Map package is empty or exceeds the import limit.");
        UiMapRevision revision;
        try { revision = UiMapRevision.Parser.ParseFrom(packageBytes); }
        catch (InvalidProtocolBufferException ex) { throw new UiMapException("MAP_IMPORT_PACKAGE_INVALID", "The Map package is malformed.", false, ex); }
        var response = new ImportMapRevisionResponse();
        var hash = UiMapStore.Hash(UiMapCanonicalizer.SerializeCanonical(revision));
        if (!revision.Immutable || !revision.NotAuthorization
            || revision.CanonicalPayloadHash != hash
            || revision.MapRevisionRef != "uimaps:map-revision:sha256:" + hash)
        {
            response.Findings.Add(new ValidationFinding { Severity = ValidationSeverity.ValidationError, Code = "MAP_REVISION_INTEGRITY_FAILED", Message = "Imported revision integrity or safety marker is invalid." });
            return response;
        }
        response.Valid = true;
        response.MapRevisionRef = revision.MapRevisionRef;
        if (commit)
        {
            _store.SaveRevision(revision);
            response.Committed = true;
            Audit("map-imported", revision.MapRevisionRef, "local-user", Now(), "Hash-valid immutable Map revision imported without source evidence.");
        }
        return response;
    }

    private void ApplyCommand(UiMapDraft draft, ReviewCommand command, string now)
    {
        var entity = draft.Entities.FirstOrDefault(x => x.MapEntityRef == command.SubjectRef);
        var interaction = draft.Interactions.FirstOrDefault(x => x.InteractionRef == command.SubjectRef);
        switch (command.CommandKind)
        {
            case ReviewCommandKind.ReviewAcceptEntity:
                if (entity is null) throw new UiMapException("MAP_GRAPH_REF_KIND_INVALID", "The entity to accept was not found.");
                AcceptEntity(draft, entity);
                break;
            case ReviewCommandKind.ReviewRejectEntity:
                if (entity is null || entity.EntityKind is MapEntityKind.MapApplication or MapEntityKind.MapApplicationState)
                    throw new UiMapException("MAP_DRAFT_COMMAND_INVALID", "The requested entity cannot be rejected.");
                RejectEntityTree(draft, entity.MapEntityRef, command.Reason);
                break;
            case ReviewCommandKind.ReviewRenameEntity:
                if (entity is null || string.IsNullOrWhiteSpace(command.Value))
                    throw new UiMapException("MAP_DRAFT_COMMAND_INVALID", "Rename requires an existing entity and non-empty display name.");
                entity.DisplayName = command.Value.Trim();
                entity.Assertions.Add(new MapAssertion
                {
                    AssertionRef = _allocator.Allocate("map-assertion"),
                    SubjectRef = entity.MapEntityRef,
                    Predicate = "displayName",
                    Value = entity.DisplayName,
                    EpistemicStatus = EpistemicStatus.EpistemicReviewed,
                    ReviewState = ReviewState.ReviewCorrected,
                    Confidence = 1,
                    ReviewedBy = command.ReviewedBy,
                    ReviewedAt = now,
                });
                break;
            case ReviewCommandKind.ReviewSetAlias:
                var identity = new UIMaps.Protocol.AtlasIdentityEquivalence(draft.Entities, draft.Relationships);
                var confirmations = command.Aliases.Count > 0 ? command.Aliases.ToArray()
                    : [new ReviewedMapAlias { FromEntityRef = command.SubjectRef, ToEntityRef = command.SecondaryRef, Reason = command.Reason }];
                if (confirmations.Length > 4096 || confirmations.Select(value => value.FromEntityRef).Distinct(StringComparer.Ordinal).Count() != confirmations.Length)
                    throw new UiMapException("MAP_ALIAS_INVALID", "Alias review exceeds its bound or repeats a source identity.");
                var identities = draft.Entities.ToDictionary(value => value.MapEntityRef, StringComparer.Ordinal);
                var additions = new List<MapRelationship>();
                foreach (var confirmation in confirmations)
                {
                    if (!identities.TryGetValue(confirmation.FromEntityRef, out var source)
                        || !identities.TryGetValue(confirmation.ToEntityRef, out var canonical)
                        || !identity.Compatible(source, canonical) || string.IsNullOrWhiteSpace(confirmation.Reason))
                        throw new UiMapException("MAP_ALIAS_INVALID", "Alias requires reviewed controls in corresponding paths of different variants and a review reason.");
                    var alias = new MapRelationship { MapRelationshipRef = _allocator.Allocate("map-relationship"),
                        RelationshipKind = MapRelationshipKind.MapAliasOf, FromEntityRef = source.MapEntityRef,
                        ToEntityRef = canonical.MapEntityRef, ReviewState = ReviewState.ReviewAccepted };
                    alias.Limitations.Add($"Reviewed by {command.ReviewedBy} at {now}: {confirmation.Reason}");
                    additions.Add(alias);
                }
                _ = new UIMaps.Protocol.AtlasIdentityEquivalence(draft.Entities, draft.Relationships.Concat(additions));
                draft.Relationships.AddRange(additions);
                break;
            case ReviewCommandKind.ReviewClearAlias:
                foreach (var prior in draft.Relationships.Where(value => value.RelationshipKind == MapRelationshipKind.MapAliasOf && value.FromEntityRef == command.SubjectRef))
                    prior.ReviewState = ReviewState.ReviewRejected;
                break;
            case ReviewCommandKind.ReviewSetParent:
                if (entity is null || string.IsNullOrWhiteSpace(command.SecondaryRef)
                    || draft.Entities.All(x => x.MapEntityRef != command.SecondaryRef))
                    throw new UiMapException("MAP_DRAFT_COMMAND_INVALID", "Set parent requires existing child and parent refs.");
                entity.ParentEntityRef = command.SecondaryRef;
                var containment = draft.Relationships.FirstOrDefault(x => x.ToEntityRef == entity.MapEntityRef
                    && x.RelationshipKind is MapRelationshipKind.MapContains or MapRelationshipKind.MapHostsDataGrid or MapRelationshipKind.MapHasColumn);
                if (containment is not null) containment.FromEntityRef = command.SecondaryRef;
                break;
            case ReviewCommandKind.ReviewSetDisposition:
                var disposition = draft.Dispositions.FirstOrDefault(x => x.SourceWorldObjectRef == command.SubjectRef);
                if (disposition is null || !Enum.TryParse<DispositionKind>(command.Value, true, out var kind))
                    throw new UiMapException("MAP_DRAFT_COMMAND_INVALID", "Set disposition requires an existing source object and valid disposition kind.");
                disposition.DispositionKind = kind;
                disposition.Explanation = command.Reason;
                disposition.ReviewState = ReviewState.ReviewCorrected;
                break;
            case ReviewCommandKind.ReviewAcceptInteraction:
                if (interaction is null) throw new UiMapException("MAP_GRAPH_REF_KIND_INVALID", "The observed interaction to accept was not found.");
                AcceptInteraction(draft, interaction);
                break;
            case ReviewCommandKind.ReviewRejectInteraction:
                if (interaction is null) throw new UiMapException("MAP_GRAPH_REF_KIND_INVALID", "The observed interaction to reject was not found.");
                interaction.ReviewState = ReviewState.ReviewRejected;
                foreach (var relationship in draft.Relationships.Where(x => x.InteractionRef == interaction.InteractionRef))
                    relationship.ReviewState = ReviewState.ReviewRejected;
                ResolveReviewItems(draft, interaction.InteractionRef, ReviewState.ReviewRejected);
                break;
            case ReviewCommandKind.ReviewAcceptAllBlocking:
                var blockingRefs = draft.ReviewItems
                    .Where(x => x.Blocking && x.ReviewState == ReviewState.ReviewUnreviewed)
                    .Select(x => x.SubjectRef).Distinct(StringComparer.Ordinal).ToList();
                var entityRefs = draft.Entities.Select(x => x.MapEntityRef).ToHashSet(StringComparer.Ordinal);
                var interactionRefs = draft.Interactions.Select(x => x.InteractionRef).ToHashSet(StringComparer.Ordinal);
                if (blockingRefs.Any(x => !entityRefs.Contains(x) && !interactionRefs.Contains(x)))
                    throw new UiMapException("MAP_GRAPH_REF_KIND_INVALID", "A blocking review subject was neither a Map entity nor an observed interaction.");
                foreach (var subjectRef in blockingRefs)
                {
                    var blockingEntity = draft.Entities.FirstOrDefault(x => x.MapEntityRef == subjectRef);
                    if (blockingEntity is not null) AcceptEntity(draft, blockingEntity);
                    else AcceptInteraction(draft, draft.Interactions.Single(x => x.InteractionRef == subjectRef));
                }
                break;
            default:
                throw new UiMapException("MAP_DRAFT_COMMAND_INVALID", "The review command is not supported by this Phase 3 implementation.");
        }
    }

    private static void AcceptEntity(UiMapDraft draft, MapEntity entity)
    {
        entity.ReviewState = ReviewState.ReviewAccepted;
        foreach (var matcher in entity.Matchers) matcher.ReviewState = ReviewState.ReviewAccepted;
        foreach (var relationship in draft.Relationships.Where(x => x.FromEntityRef == entity.MapEntityRef || x.ToEntityRef == entity.MapEntityRef))
            relationship.ReviewState = ReviewState.ReviewAccepted;
        ResolveReviewItems(draft, entity.MapEntityRef, ReviewState.ReviewAccepted);
        if (entity.EntityKind == MapEntityKind.MapSurface)
        {
            foreach (var variant in draft.Entities.Where(x => x.ParentEntityRef == entity.MapEntityRef
                         && x.EntityKind == MapEntityKind.MapSurfaceVariant && x.ReviewState == ReviewState.ReviewUnreviewed))
                variant.ReviewState = ReviewState.ReviewAccepted;
        }
    }

    private static void AcceptInteraction(UiMapDraft draft, ObservedMapInteraction interaction)
    {
        interaction.ReviewState = ReviewState.ReviewAccepted;
        foreach (var relationship in draft.Relationships.Where(x => x.InteractionRef == interaction.InteractionRef))
            relationship.ReviewState = ReviewState.ReviewAccepted;
        ResolveReviewItems(draft, interaction.InteractionRef, ReviewState.ReviewAccepted);
    }

    private static void ResolveReviewItems(UiMapDraft draft, string subjectRef, ReviewState state)
    {
        foreach (var item in draft.ReviewItems.Where(x => x.SubjectRef == subjectRef)) item.ReviewState = state;
    }

    private static void RejectEntityTree(UiMapDraft draft, string rootRef, string reason)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal) { rootRef };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var entity in draft.Entities.Where(x => refs.Contains(x.ParentEntityRef)))
                changed |= refs.Add(entity.MapEntityRef);
        }
        foreach (var entity in draft.Entities.Where(x => refs.Contains(x.MapEntityRef))) entity.ReviewState = ReviewState.ReviewRejected;
        foreach (var item in draft.ReviewItems.Where(x => refs.Contains(x.SubjectRef))) item.ReviewState = ReviewState.ReviewRejected;
        foreach (var declaration in draft.GridHostDeclarations.Where(x => refs.Contains(x.DataGridRef)))
            declaration.ReviewState = ReviewState.ReviewRejected;
        foreach (var disposition in draft.Dispositions.Where(x => x.ResultingMapEntityRefs.Any(refs.Contains)))
        {
            disposition.DispositionKind = DispositionKind.DispositionExcluded;
            disposition.ReasonCode = "reviewer_excluded";
            disposition.Explanation = string.IsNullOrWhiteSpace(reason) ? "The reviewer excluded this candidate." : reason;
            disposition.ReviewState = ReviewState.ReviewCorrected;
            disposition.ResultingMapEntityRefs.Clear();
        }
    }

    private static UiMapDraft RestoreSnapshot(
        UiMapDraft current,
        ConcurrentDictionary<string, Stack<UiMapDraft>> source,
        ConcurrentDictionary<string, Stack<UiMapDraft>> destination,
        string errorCode)
    {
        var stack = source.GetOrAdd(current.MapDraftRef, _ => new Stack<UiMapDraft>());
        lock (stack)
        {
            if (stack.Count == 0) throw new UiMapException(errorCode, "No review command is available for this operation.");
            destination.GetOrAdd(current.MapDraftRef, _ => new Stack<UiMapDraft>()).Push(current.Clone());
            return stack.Pop().Clone();
        }
    }

    private void EnsureReviewHistory(string draftRef)
    {
        if (_undo.ContainsKey(draftRef) && _redo.ContainsKey(draftRef)) return;
        lock (_reviewHistoryGate)
        {
            if (_undo.ContainsKey(draftRef) && _redo.ContainsKey(draftRef)) return;
            var persisted = _store.LoadReviewStacks(draftRef);
            _undo[draftRef] = persisted.Undo;
            _redo[draftRef] = persisted.Redo;
        }
    }

    private static SourceObservationSet BuildSource(TargetApplicationIdentity target, AttachObservationRequest request, string now)
    {
        var snapshot = request.Snapshot;
        var survey = request.Survey;
        var isSnapshot = snapshot is not null && snapshot.WorldObjects.Count > 0;
        var payload = isSnapshot ? snapshot!.ToByteArray() : survey!.ToByteArray();
        var source = new SourceObservationSet
        {
            SourceObservationSetRef = "uimaps:source-observation-set:" + Guid.NewGuid().ToString("N"),
            ApplicationRef = target.ApplicationRef,
            AcquisitionSemantic = isSnapshot ? AcquisitionSemantic.AcquisitionSnapshot : AcquisitionSemantic.AcquisitionSurvey,
            PublicationRef = string.IsNullOrWhiteSpace(request.PublicationRef)
                ? isSnapshot ? $"uimaps:world-state:{snapshot!.PerceptionEpoch}:{snapshot.WorldStateVersion}" : survey!.PublicationId
                : request.PublicationRef,
            EpisodeId = isSnapshot ? snapshot!.EpisodeId : survey!.EpisodeId,
            PerceptionEpoch = isSnapshot ? snapshot!.PerceptionEpoch : survey!.PerceptionEpoch,
            WorldStateVersion = isSnapshot ? snapshot!.WorldStateVersion : survey!.SurveyVersion,
            WorldStateHash = UiMapStore.Hash(payload),
            SimultaneityGroupRef = string.IsNullOrWhiteSpace(request.SimultaneityGroupRef) ? "uimaps:simultaneity:" + Guid.NewGuid().ToString("N") : request.SimultaneityGroupRef,
            CapturedAt = isSnapshot ? snapshot!.CapturedAt : survey!.EndedAt,
            AttachedAt = now,
            SourcePayloadHash = UiMapStore.Hash(payload),
            GridMapRevisionRef = request.ReviewedGridMap?.MapRevisionRef ?? "",
        };
        var values = isSnapshot ? snapshot!.WorldObjects : survey!.WorldObjects.Select(x => x.WorldObject);
        source.EvidenceRefs.AddRange(values.Select(x => x.WorldObjectId));
        if (isSnapshot && snapshot!.Health?.State != HealthState.Complete)
            source.CoverageLimitations.Add("Snapshot health was not complete; absence claims require separate coverage.");
        if (!isSnapshot && survey!.ConvergenceState != ConvergenceState.ConvergenceConverged)
            source.CoverageLimitations.Add($"Survey convergence was {survey.ConvergenceState}; the Map is not exhaustive.");
        return source;
    }

    private static DataGridMapPackage BuildReviewedGridMap(MappingSessionDescriptor session, AttachObservationRequest request, string now)
    {
        if (string.IsNullOrWhiteSpace(request.GridHostWorldObjectRef))
            throw new UiMapException("MAP_GRAPH_GRID_HOST_INVALID", "Attaching reviewed grid analysis requires the selected host World Object ref.");
        var analysis = request.ReviewedGridAnalysis;
        var candidate = analysis.GridCandidates.OrderByDescending(x => x.GridConfidence).ThenBy(x => x.CandidateRef, StringComparer.Ordinal).First();
        var package = new DataGridMapPackage
        {
            SchemaVersion = "2.1",
            AppRef = session.Target.ApplicationRef,
            CreatedAt = now,
            PublishedAt = now,
            PublisherIdentity = session.PrivacyScope.AcknowledgedBy,
        };
        package.ApplicationMatchers.Add("application-ref:" + session.Target.ApplicationRef);
        var surfaceRef = "uimaps:grid-source-surface:" + Guid.NewGuid().ToString("N");
        package.Surfaces.Add(new MappedSurface
        {
            SurfaceRef = surfaceRef,
            ApplicationRef = session.Target.ApplicationRef,
            SurfaceVariantRefs = { "uimaps:grid-source-variant:" + Guid.NewGuid().ToString("N") },
            Matchers = { "mapping-session:" + session.MappingSessionRef },
        });
        var grid = new MappedDataGrid
        {
            DataGridRef = "uimaps:reviewed-grid:" + Guid.NewGuid().ToString("N"),
            SurfaceRef = surfaceRef,
            DisplayName = "Reviewed Data Grid",
            ExpectedRelativeBounds = candidate.BoundsPx?.Clone(),
            MatcherPolicyRef = "uimaps:grid-matcher:phase3-v1",
            SourceEvidenceRefs = { request.GridHostWorldObjectRef, candidate.CandidateRef },
            ReviewerAssertions = { "reviewed-in-mapping-session:" + session.MappingSessionRef },
        };
        grid.SurfaceVariantRefs.AddRange(package.Surfaces[0].SurfaceVariantRefs);
        foreach (var column in candidate.ColumnCandidates.OrderBy(x => x.Ordinal))
        {
            var name = GridColumnHeaderResolver.Resolve(analysis, column);
            grid.ExpectedHeaderSet.Add(name);
            grid.Columns.Add(new MappedGridColumn
            {
                GridColumnRef = "uimaps:grid-column:" + Guid.NewGuid().ToString("N"),
                DataGridRef = grid.DataGridRef,
                Ordinal = column.Ordinal,
                CanonicalDisplayName = name,
                ExpectedWidthRatio = column.WidthRatio,
                HeaderMatchers = { name },
                SourceEvidenceRefs = { column.CandidateRef },
            });
        }
        package.DataGrids.Add(grid);
        package.EvidenceManifest.AddRange([request.GridHostWorldObjectRef, candidate.CandidateRef]);
        package.CanonicalPayloadHash = DataGridMapStore.HashCanonical(package);
        package.MapRevisionRef = "uimaps:grid-map-revision:sha256:" + package.CanonicalPayloadHash;
        return package;
    }

    private static void ValidatePrivacy(PrivacyScopeAcknowledgement scope)
    {
        if (scope is null || string.IsNullOrWhiteSpace(scope.ScopeVersion) || string.IsNullOrWhiteSpace(scope.ScopeHash)
            || string.IsNullOrWhiteSpace(scope.AcknowledgedBy) || !scope.ProtectedValuesExcluded || !scope.TargetInputProhibited)
            throw new UiMapException("MAP_SESSION_SCOPE_NOT_ACKNOWLEDGED", "Affirmative privacy scope, protected-value exclusion, and target-input prohibition are required.");
    }

    private static (string SurfaceRef, string SurfaceVariantRef) ResolveSurfaceClosure(
        MapEntity entity,
        IEnumerable<MapEntity> entities)
    {
        var byRef = entities.ToDictionary(value => value.MapEntityRef, StringComparer.Ordinal);
        var current = entity;
        var surfaceRef = "";
        var variantRef = "";
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(current.MapEntityRef))
        {
            if (current.EntityKind == MapEntityKind.MapSurfaceVariant) variantRef = current.MapEntityRef;
            if (current.EntityKind == MapEntityKind.MapSurface)
            {
                surfaceRef = current.MapEntityRef;
                break;
            }
            if (string.IsNullOrWhiteSpace(current.ParentEntityRef)
                || !byRef.TryGetValue(current.ParentEntityRef, out current!))
                break;
        }
        return (surfaceRef, variantRef);
    }

    private static void BindDeclarationEvidence(
        DataGridHostDeclaration declaration,
        MapEntity host,
        UiMapDraft draft,
        string surfaceRef)
    {
        declaration.ApplicationRef = draft.ApplicationRef;
        declaration.SurfaceRef = surfaceRef;
        declaration.HostControlRef = host.MapEntityRef;
        declaration.EvidenceLinkRefs.Clear();
        if (string.IsNullOrWhiteSpace(declaration.SourceObservationSetRef)) return;

        var links = host.EvidenceLinks.Where(value => string.Equals(
            value.SourceObservationSetRef,
            declaration.SourceObservationSetRef,
            StringComparison.Ordinal)).ToList();
        if (links.Count == 0)
            throw new UiMapException("MAP_GRID_HOST_SOURCE_MISSING", "The selected host Control has no direct evidence in the selected observation.");
        if (links.Count != 1)
            throw new UiMapException("MAP_GRID_HOST_SOURCE_AMBIGUOUS", "The selected host Control has multiple direct objects in the selected observation.");
        var link = links[0];
        var source = draft.Sources.SingleOrDefault(value => value.SourceObservationSetRef == declaration.SourceObservationSetRef)
            ?? throw new UiMapException("MAP_GRID_HOST_SOURCE_MISSING", "The selected observation is not attached to the draft.");
        if (!source.SurfaceObservations.Any(value => value.SourceWorldObjectRef == link.SourceSurfaceWorldObjectRef
            && value.ResultingMapSurfaceRef == surfaceRef))
            throw new UiMapException("MAP_GRID_HOST_SURFACE_MISMATCH", "The selected host evidence belongs to a different source Surface.");
        declaration.SourceHostWorldObjectRef = link.SourceObjectRef;
        declaration.EvidenceLinkRefs.Add(link.EvidenceLinkRef);
    }

    private GraphSnapshot LoadGraph(string mapRef)
    {
        if (string.IsNullOrWhiteSpace(mapRef))
            throw new UiMapException("MAP_REF_REQUIRED", "A draft or revision ref is required.");
        try
        {
            var draft = _store.LoadDraft(mapRef);
            return new GraphSnapshot(draft.Entities, draft.Relationships, draft.GridHostDeclarations,
                UiMapCanonicalizer.HashDraft(draft));
        }
        catch (UiMapException)
        {
            var revision = _store.LoadRevision(mapRef);
            return new GraphSnapshot(revision.Entities, revision.Relationships, revision.GridHostDeclarations,
                revision.CanonicalPayloadHash);
        }
    }

    private static bool HasUsableStructuredList(MapEntity host, UiMapDraft draft)
    {
        var descendants = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(host.MapEntityRef);
        while (queue.TryDequeue(out var parentRef))
        {
            foreach (var child in draft.Entities.Where(value => value.ParentEntityRef == parentRef))
                if (descendants.Add(child.MapEntityRef)) queue.Enqueue(child.MapEntityRef);
        }
        return draft.Entities.Where(value => descendants.Contains(value.MapEntityRef)
                && value.EntityKind == MapEntityKind.MapControl)
            .Any(value => value.Role.Contains("list", StringComparison.OrdinalIgnoreCase)
                && value.EvidenceLinks.Any(link => link.EvidenceFamily.Contains("accessibility", StringComparison.OrdinalIgnoreCase)));
    }

    private static void MarkGridInternalControls(UiMapDraft draft, string hostRef, string gridRef, UIMaps.Protocol.V1.RectPx? gridBounds)
    {
        if (gridBounds is not { Width: > 0, Height: > 0 }) return;
        var descendants = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(hostRef);
        while (queue.TryDequeue(out var parentRef))
        {
            foreach (var child in draft.Entities.Where(value => value.ParentEntityRef == parentRef
                         && value.EntityKind == MapEntityKind.MapControl))
                if (descendants.Add(child.MapEntityRef)) queue.Enqueue(child.MapEntityRef);
        }
        foreach (var control in draft.Entities.Where(value => descendants.Contains(value.MapEntityRef)))
        {
            var contained = control.EvidenceLinks.Any(value => MostlyContained(value.BoundsPx, gridBounds));
            if (!contained) continue;
            control.GridInternalDataGridRef = gridRef;
            if (!control.Limitations.Contains("Retained as source evidence beneath the reviewed Data Grid; hidden as a separate Organized Control.", StringComparer.Ordinal))
                control.Limitations.Add("Retained as source evidence beneath the reviewed Data Grid; hidden as a separate Organized Control.");
        }
    }

    private HostedDataGridObservation BuildHostedGridObservation(
        UiMapDraft draft,
        DataGridHostDeclaration declaration,
        MapEntity grid,
        UIMaps.Protocol.V2_1.DataGridCandidate candidate,
        UIMaps.Protocol.V2_1.GridAnalysisResult analysis,
        IReadOnlyDictionary<string, string> columnRefs)
    {
        var source = draft.Sources.Single(value => value.SourceObservationSetRef == declaration.SourceObservationSetRef);
        var instance = new UIMaps.Protocol.V2_1.DataGridInstance
        {
            InstanceRef = _allocator.Allocate("data-grid-instance"),
            DataGridRef = grid.MapEntityRef,
            SurfaceRef = declaration.SurfaceRef,
            SurfaceVariantRef = declaration.SurfaceVariantRefs.FirstOrDefault() ?? "",
            WorldStateVersion = source.WorldStateVersion,
            CurrentBoundsPx = candidate.BoundsPx?.Clone(),
            Confidence = candidate.GridConfidence,
            EvidenceAvailable = true,
        };
        instance.ObservedHeaders.AddRange(grid.HostedDataGrid.ExpectedHeaders);
        instance.ObservedColumns.AddRange(columnRefs.Values);
        foreach (var rowCandidate in candidate.VisibleRowCandidates.OrderBy(value => value.VisibleOrdinal))
        {
            var row = new UIMaps.Protocol.V2_1.VisibleGridRowInstance
            {
                InstanceRef = _allocator.Allocate("visible-grid-row"),
                VisibleOrdinal = rowCandidate.VisibleOrdinal,
                AssociationConfidence = rowCandidate.Confidence,
                Lineage = rowCandidate.Lineage?.Clone(),
            };
            row.RowKeyHypotheses.AddRange(rowCandidate.RowKeyHypotheses);
            foreach (var cellCandidate in rowCandidate.CellCandidates)
            {
                var cell = new UIMaps.Protocol.V2_1.VisibleGridCellInstance
                {
                    GridColumnRef = columnRefs.GetValueOrDefault(cellCandidate.ColumnCandidateRef, ""),
                    OriginalText = cellCandidate.OriginalText,
                    NormalizedText = cellCandidate.NormalizedText,
                    BoundsPx = cellCandidate.BoundsPx?.Clone(),
                    Confidence = cellCandidate.Confidence,
                    Lineage = cellCandidate.Lineage?.Clone(),
                };
                row.VisibleCells.Add(cell);
            }
            instance.VisibleRows.Add(row);
        }
        var observation = new HostedDataGridObservation
        {
            GridObservationRef = _allocator.Allocate("hosted-grid-observation"),
            MapPayloadHash = UiMapCanonicalizer.HashDraft(draft),
            DataGridRef = grid.MapEntityRef,
            HostControlRef = declaration.HostControlRef,
            ApplicationRef = draft.ApplicationRef,
            SurfaceRef = declaration.SurfaceRef,
            SurfaceVariantRef = declaration.SurfaceVariantRefs.FirstOrDefault() ?? "",
            SourceObservationSetRef = declaration.SourceObservationSetRef,
            HostSourceWorldObjectRef = declaration.SourceHostWorldObjectRef,
            AnalysisRef = analysis.AnalysisRef,
            DataGridInstance = instance,
            NotAuthorization = true,
        };
        observation.ProviderCapabilities.Add(GridProviderCapabilityKind.GridProviderVisualReconstruction);
        observation.Coverage.Add(GridCoverageKind.GridCoverageVisibleOnly);
        if (grid.HostedDataGrid.ExplorationReceipt is { } exploration)
        {
            observation.Coverage.Add(GridCoverageKind.GridCoverageCompleteForClaimedScope);
            observation.Limitations.AddRange(exploration.Limitations);
        }
        if (candidate.UnresolvedSubregions.Count > 0 || analysis.UnresolvedSubregions.Count > 0)
        {
            observation.Coverage.Add(GridCoverageKind.GridCoveragePartial);
            observation.Limitations.Add("Unresolved subregions remain outside the promoted visible structure.");
        }
        var hashClone = observation.Clone();
        hashClone.ObservationHash = "";
        observation.ObservationHash = HashText(hashClone.ToByteArray());
        return observation;
    }

    private static void ApplyExplorationReceipt(
        HostedDataGridDescriptor descriptor,
        PromoteHostedGridCandidateRequest request,
        string dataGridRef,
        string hostControlRef)
    {
        descriptor.ExplorationReceipt = null;
        if (request.ExplorationReceipt is null) return;

        var receipt = request.ExplorationReceipt.Clone();
        if (string.IsNullOrWhiteSpace(receipt.ExplorationRef)
            || string.IsNullOrWhiteSpace(receipt.ExploredBy)
            || !DateTimeOffset.TryParse(receipt.ExploredAt, out _)
            || receipt.AcceptedViewports <= 0
            || receipt.ObservedRowCount < 0
            || !receipt.NotAuthorization
            || !receipt.ReachedTop || !receipt.ReachedBottom
            || !receipt.ReachedLeft || !receipt.ReachedRight
            || !receipt.RestorationSucceeded
            || !receipt.Coverage.Contains(GridCoverageKind.GridCoverageCompleteForClaimedScope)
            || !string.Equals(receipt.HostControlRef, hostControlRef, StringComparison.Ordinal)
            || !string.Equals(receipt.SourceObservationSetRef, request.SourceObservationSetRef, StringComparison.Ordinal)
            || !string.Equals(receipt.SourceAnalysisRef, request.Analysis.AnalysisRef, StringComparison.Ordinal)
            || !receipt.ObservedHeaders.SequenceEqual(descriptor.ExpectedHeaders, StringComparer.Ordinal))
            throw new UiMapException("MAP_GRID_EXPLORATION_INVALID",
                "A full-grid exploration receipt must prove all four reachable edges, successful restoration, and the exact promoted header schema.");

        receipt.DataGridRef = dataGridRef;
        receipt.HostControlRef = hostControlRef;
        var orderedCoverage = receipt.Coverage.Order().ToArray();
        receipt.Coverage.Clear();
        receipt.Coverage.AddRange(orderedCoverage);
        var orderedLimitations = receipt.Limitations.Order(StringComparer.Ordinal).ToArray();
        receipt.Limitations.Clear();
        receipt.Limitations.AddRange(orderedLimitations);
        receipt.ReceiptHash = string.Empty;
        receipt.ReceiptHash = HashText(receipt.ToByteArray());
        descriptor.ExplorationReceipt = receipt;
    }

    private static bool MostlyContained(UIMaps.Protocol.V1.RectPx? value, UIMaps.Protocol.V1.RectPx container)
    {
        if (value is not { Width: > 0, Height: > 0 }) return false;
        var left = Math.Max(value.X, container.X);
        var top = Math.Max(value.Y, container.Y);
        var right = Math.Min(value.X + value.Width, container.X + container.Width);
        var bottom = Math.Min(value.Y + value.Height, container.Y + container.Height);
        var overlap = (long)Math.Max(0, right - left) * Math.Max(0, bottom - top);
        return overlap >= (long)value.Width * value.Height * 0.8;
    }

    private static string HashText(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private sealed record GraphSnapshot(
        IEnumerable<MapEntity> Entities,
        IEnumerable<MapRelationship> Relationships,
        IEnumerable<DataGridHostDeclaration> Declarations,
        string PayloadHash);

    private static void RequireSessionEtag(MappingSessionDescriptor session, string expected)
    {
        if (!string.Equals(session.Etag, expected, StringComparison.Ordinal))
            throw new UiMapException("MAP_DRAFT_ETAG_CONFLICT", "The Mapping Session changed; reload before continuing.", true);
    }

    private static void RequireDraftEtag(UiMapDraft draft, string expected)
    {
        if (!string.Equals(draft.Etag, expected, StringComparison.Ordinal))
            throw new UiMapException("MAP_DRAFT_ETAG_CONFLICT", "The Map draft changed; reload before continuing.", true);
    }

    private static void UpdateSessionEtag(MappingSessionDescriptor session)
    {
        var clone = session.Clone();
        clone.Etag = "";
        session.Etag = UiMapStore.Hash(clone.ToByteArray());
    }

    private static int DecodeOffset(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return 0;
        try
        {
            var bytes = Convert.FromBase64String(token);
            return bytes.Length == sizeof(int) ? Math.Max(0, BitConverter.ToInt32(bytes)) : 0;
        }
        catch (FormatException) { return 0; }
    }

    private string Now() => _clock().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private void Audit(string kind, string subjectRef, string principal, string createdAt, string safeSummary) =>
        _store.AppendAudit(_allocator.Allocate("audit"), kind, subjectRef, string.IsNullOrWhiteSpace(principal) ? "local-user" : principal, createdAt, safeSummary);
}

public static class UiMapValidator
{
    public static MapValidationReport Validate(UiMapDraft draft, string validationRef, string completedAt)
    {
        var report = new MapValidationReport
        {
            ValidationRunRef = validationRef,
            MapDraftRef = draft.MapDraftRef,
            DraftEtag = draft.Etag,
            SourceObjectCount = draft.Dispositions
                .Select(x => (x.SourceObservationSetRef, x.SourceWorldObjectRef))
                .Distinct()
                .Count(),
            DispositionCount = draft.Dispositions.Count,
            MappedEntityCount = draft.Entities.Count(x => x.ReviewState != ReviewState.ReviewRejected),
            CompletedAt = completedAt,
            CanonicalPayloadHash = UiMapCanonicalizer.HashDraft(draft),
        };
        void Finding(ValidationSeverity severity, string code, string message, params string[] refs)
        {
            var finding = new ValidationFinding { Severity = severity, Code = code, Message = message };
            finding.AffectedRefs.AddRange(refs);
            report.Findings.Add(finding);
        }

        try { _ = new UIMaps.Protocol.AtlasIdentityEquivalence(draft.Entities, draft.Relationships); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException) { Finding(ValidationSeverity.ValidationError, "MAP_ALIAS_INVALID", error.Message); }

        var included = draft.Entities.Where(x => x.ReviewState != ReviewState.ReviewRejected).ToList();
        var refs = included.Select(x => x.MapEntityRef).ToList();
        if (included.Count(x => x.EntityKind == MapEntityKind.MapApplication) != 1)
            Finding(ValidationSeverity.ValidationError, "MAP_GRAPH_APPLICATION_INVALID", "Exactly one application root is required.", draft.ApplicationRef);
        if (refs.Count != refs.Distinct(StringComparer.Ordinal).Count())
            Finding(ValidationSeverity.ValidationError, "MAP_GRAPH_REF_DUPLICATE", "Map entity refs must be unique.");
        var refSet = refs.ToHashSet(StringComparer.Ordinal);
        foreach (var entity in included)
        {
            if (entity.ApplicationRef != draft.ApplicationRef)
                Finding(ValidationSeverity.ValidationError, "MAP_GRAPH_APPLICATION_SCOPE", "Every entity must belong to the draft application.", entity.MapEntityRef);
            if (!string.IsNullOrWhiteSpace(entity.ParentEntityRef) && !refSet.Contains(entity.ParentEntityRef))
                Finding(ValidationSeverity.ValidationError, "MAP_GRAPH_PARENT_MISSING", "Entity parent is outside the published graph.", entity.MapEntityRef, entity.ParentEntityRef);
            if (entity.EntityKind == MapEntityKind.MapDataGrid)
            {
                if (entity.HostedDataGrid is null || !refSet.Contains(entity.HostedDataGrid.HostControlRef)
                    || included.FirstOrDefault(x => x.MapEntityRef == entity.HostedDataGrid.HostControlRef)?.EntityKind != MapEntityKind.MapControl
                    || string.IsNullOrWhiteSpace(entity.HostedDataGrid.GridMapRevisionRef))
                    Finding(ValidationSeverity.ValidationError, "MAP_GRAPH_GRID_HOST_INVALID", "A hosted Data Grid requires a valid host and exact reviewed grid revision.", entity.MapEntityRef);
                else if (entity.HostedDataGrid.ProviderCapabilities.Contains(GridProviderCapabilityKind.GridProviderVisualReconstruction))
                {
                    var receipt = entity.HostedDataGrid.ExplorationReceipt;
                    if (entity.HostedDataGrid.ReviewedImageSchema is { } imageSchema)
                    {
                        try
                        {
                            UIMaps.Protocol.ReviewedGridImageSchemaValidation.Validate(imageSchema);
                            var labels = imageSchema.Columns.Select(column => GridColumnHeaderResolver.Resolve(new UIMaps.Protocol.V2_1.GridAnalysisResult(), column));
                            if (!labels.SequenceEqual(entity.HostedDataGrid.ExpectedHeaders, StringComparer.Ordinal))
                                throw new InvalidOperationException("Reviewed image schema differs from the mapped columns.");
                            Finding(ValidationSeverity.ValidationWarning, "MAP_GRID_CONTENT_NOT_CAPTURED",
                                "The full-width schema is reviewed. Full table content must be qualified independently during acquisition.", entity.MapEntityRef);
                        }
                        catch (InvalidOperationException error)
                        {
                            Finding(ValidationSeverity.ValidationError, "MAP_GRID_REVIEWED_IMAGE_INVALID", error.Message, entity.MapEntityRef);
                        }
                    }
                    else if (receipt is null)
                    {
                        Finding(ValidationSeverity.ValidationError, "MAP_GRID_EXPLORATION_REQUIRED",
                            "A non-native mapped Data Grid must be fully explored before publication.", entity.MapEntityRef);
                    }
                    else
                    {
                        var hashClone = receipt.Clone();
                        hashClone.ReceiptHash = string.Empty;
                        var receiptValid = !string.IsNullOrWhiteSpace(receipt.ExplorationRef)
                            && !string.IsNullOrWhiteSpace(receipt.ExploredBy)
                            && DateTimeOffset.TryParse(receipt.ExploredAt, out _)
                            && receipt.AcceptedViewports > 0
                            && receipt.ObservedRowCount >= 0
                            && receipt.NotAuthorization
                            && receipt.ReachedTop && receipt.ReachedBottom
                            && receipt.ReachedLeft && receipt.ReachedRight
                            && receipt.RestorationSucceeded
                            && receipt.Coverage.Contains(GridCoverageKind.GridCoverageCompleteForClaimedScope)
                            && string.Equals(receipt.DataGridRef, entity.MapEntityRef, StringComparison.Ordinal)
                            && string.Equals(receipt.HostControlRef, entity.HostedDataGrid.HostControlRef, StringComparison.Ordinal)
                            && string.Equals(receipt.SourceObservationSetRef, entity.HostedDataGrid.SourceObservationSetRef, StringComparison.Ordinal)
                            && string.Equals(receipt.SourceAnalysisRef, entity.HostedDataGrid.SourceAnalysisRef, StringComparison.Ordinal)
                            && receipt.ObservedHeaders.SequenceEqual(entity.HostedDataGrid.ExpectedHeaders, StringComparer.Ordinal)
                            && string.Equals(receipt.ReceiptHash, UiMapStore.Hash(hashClone.ToByteArray()), StringComparison.Ordinal);
                        if (!receiptValid)
                            Finding(ValidationSeverity.ValidationError, "MAP_GRID_EXPLORATION_INVALID",
                                "The saved Data Grid exploration receipt is stale, incomplete, or does not match the promoted schema.", entity.MapEntityRef, receipt.ExplorationRef);
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(entity.GridInternalDataGridRef)
                && (!refSet.Contains(entity.GridInternalDataGridRef)
                    || included.First(value => value.MapEntityRef == entity.GridInternalDataGridRef).EntityKind != MapEntityKind.MapDataGrid))
                Finding(ValidationSeverity.ValidationError, "MAP_GRAPH_GRID_INTERNAL_INVALID",
                    "A grid-internal annotation must reference one included Data Grid.", entity.MapEntityRef, entity.GridInternalDataGridRef);
        }
        var sourceRefs = draft.Sources.Select(x => x.SourceObservationSetRef).ToHashSet(StringComparer.Ordinal);
        foreach (var declaration in draft.GridHostDeclarations.Where(value => value.ReviewState != ReviewState.ReviewRejected))
        {
            var host = included.FirstOrDefault(value => value.MapEntityRef == declaration.HostControlRef);
            if (host?.EntityKind != MapEntityKind.MapControl)
                Finding(ValidationSeverity.ValidationError, "MAP_GRID_HOST_CONTROL_REQUIRED",
                    "A grid-host declaration must reference one included stable Control.", declaration.DeclarationRef, declaration.HostControlRef);
            if (!string.IsNullOrWhiteSpace(declaration.SourceObservationSetRef)
                && !sourceRefs.Contains(declaration.SourceObservationSetRef))
                Finding(ValidationSeverity.ValidationError, "MAP_GRID_HOST_SOURCE_MISSING",
                    "A grid-host declaration references an unattached source observation.", declaration.DeclarationRef, declaration.SourceObservationSetRef);
            if (declaration.AssertionKind == GridHostAssertionKind.GridHostAbsent
                && !string.IsNullOrWhiteSpace(declaration.DataGridRef))
                Finding(ValidationSeverity.ValidationError, "MAP_GRID_DECLARATION_CONFLICT",
                    "An accepted absent assertion cannot reference a Data Grid.", declaration.DeclarationRef, declaration.DataGridRef);
            if (!string.IsNullOrWhiteSpace(declaration.DataGridRef))
            {
                var grid = included.FirstOrDefault(value => value.MapEntityRef == declaration.DataGridRef);
                if (grid?.EntityKind != MapEntityKind.MapDataGrid
                    || grid.HostedDataGrid?.HostControlRef != declaration.HostControlRef)
                    Finding(ValidationSeverity.ValidationError, "MAP_GRID_DECLARATION_CONFLICT",
                        "A declaration's promoted Data Grid must remain under the declared host Control.", declaration.DeclarationRef, declaration.DataGridRef);
            }
        }
        foreach (var group in draft.GridHostDeclarations.Where(value => value.ReviewState == ReviewState.ReviewAccepted)
                     .GroupBy(value => (value.HostControlRef, VariantScope: string.Join('\u001f', value.SurfaceVariantRefs.Order(StringComparer.Ordinal)))))
            if (group.Select(value => value.AssertionKind).Distinct().Count() > 1)
                Finding(ValidationSeverity.ValidationError, "MAP_GRID_DECLARATION_CONFLICT",
                    "Conflicting accepted grid-host assertions exist for one stable host and variant scope.", group.Select(value => value.DeclarationRef).ToArray());
        var visualOcrEligibleForDraft = draft.Sources.Any(source => source.AppliedControlDetectionPolicy?.Effective is
            { VisualControlDetectionEnabled: true, OcrOnVisualControlsEnabled: true });
        if (draft.EvidenceStreamSelections.GroupBy(value => value.StreamKind).Any(group => group.Count() > 1)
            || draft.EvidenceStreamSelections.Any(value => value.StreamKind == EvidenceStreamKind.Unspecified
                || value.Eligibility == EvidenceStreamEligibility.Unspecified
                || (value.StreamKind == EvidenceStreamKind.EvidenceStreamVisualOcr
                    && value.Eligibility == EvidenceStreamEligibility.EvidenceStreamEnabled
                    && !visualOcrEligibleForDraft)))
            Finding(ValidationSeverity.ValidationError, "MAP_EVIDENCE_STREAM_SELECTION_INVALID",
                "Saved evidence-stream policy must be unique and explicit; visual/OCR requires an attachment policy that enabled visual detection and OCR-on-visual-controls.");
        foreach (var observation in draft.HostedGridObservations)
        {
            if (!observation.NotAuthorization || !refSet.Contains(observation.DataGridRef)
                || !refSet.Contains(observation.HostControlRef) || !sourceRefs.Contains(observation.SourceObservationSetRef))
                Finding(ValidationSeverity.ValidationError, "MAP_GRID_OBSERVATION_INVALID",
                    "A retained Data Grid observation requires valid Map/source refs and notAuthorization=true.", observation.GridObservationRef);
        }
        if (HasContainmentCycle(included))
            Finding(ValidationSeverity.ValidationError, "MAP_GRAPH_CONTAINMENT_CYCLE", "Map containment must be acyclic.");
        foreach (var relationship in draft.Relationships.Where(x => included.Any(y => y.MapEntityRef == x.FromEntityRef || y.MapEntityRef == x.ToEntityRef)))
        {
            if (!refSet.Contains(relationship.FromEntityRef) || !refSet.Contains(relationship.ToEntityRef))
                Finding(ValidationSeverity.ValidationError, "MAP_GRAPH_RELATIONSHIP_INVALID", "Relationship endpoints must be in the published graph.", relationship.MapRelationshipRef);
        }
        var dispositionKeys = draft.Dispositions.Select(x => (x.SourceObservationSetRef, x.SourceWorldObjectRef)).ToList();
        if (dispositionKeys.Count != dispositionKeys.Distinct().Count())
            Finding(ValidationSeverity.ValidationError, "MAP_GRAPH_DISPOSITION_DUPLICATE", "Every source object requires exactly one primary disposition.");
        if (draft.ReviewItems.Any(x => x.Blocking && x.ReviewState == ReviewState.ReviewUnreviewed))
            Finding(ValidationSeverity.ValidationError, "MAP_VALIDATION_BLOCKED", "Blocking review items remain.", draft.ReviewItems.Where(x => x.Blocking && x.ReviewState == ReviewState.ReviewUnreviewed).Select(x => x.SubjectRef).ToArray());
        var actionRefs = draft.Interactions.Select(x => x.ActionEventRef).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (actionRefs.Count != actionRefs.Distinct(StringComparer.Ordinal).Count())
            Finding(ValidationSeverity.ValidationError, "MAP_INTERACTION_ACTION_DUPLICATE", "Each observed action may support at most one Map interaction.");
        foreach (var interaction in draft.Interactions.Where(x => x.ReviewState != ReviewState.ReviewRejected))
        {
            if (!interaction.UserPerformed || !interaction.NotAuthorization
                || !sourceRefs.Contains(interaction.SourceObservationSetRef)
                || !sourceRefs.Contains(interaction.ResultObservationSetRef))
                Finding(ValidationSeverity.ValidationError, "MAP_INTERACTION_EVIDENCE_INVALID", "An observed interaction requires attached before/result sources and non-authorizing user-action evidence.", interaction.InteractionRef);
            if (interaction.ReviewState == ReviewState.ReviewUnreviewed)
                Finding(ValidationSeverity.ValidationError, "MAP_INTERACTION_REVIEW_REQUIRED", "Observed transitions require explicit acceptance or rejection before publication.", interaction.InteractionRef);
        }
        if (draft.Sources.Count == 0)
            Finding(ValidationSeverity.ValidationError, "MAP_SOURCE_REQUIRED", "At least one immutable source observation is required.");
        foreach (var limitation in draft.Sources.SelectMany(x => x.CoverageLimitations).Distinct(StringComparer.Ordinal))
            Finding(ValidationSeverity.ValidationWarning, "MAP_SOURCE_COVERAGE_PARTIAL", limitation);
        report.PublicationEligible = report.Findings.All(x => x.Severity != ValidationSeverity.ValidationError);
        return report;
    }

    private static bool HasContainmentCycle(IReadOnlyList<MapEntity> entities)
    {
        var parents = entities.Where(x => !string.IsNullOrWhiteSpace(x.ParentEntityRef))
            .ToDictionary(x => x.MapEntityRef, x => x.ParentEntityRef, StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            var current = entity.MapEntityRef;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (parents.TryGetValue(current, out var parent))
            {
                if (!visited.Add(current) || parent == entity.MapEntityRef) return true;
                current = parent;
            }
        }
        return false;
    }
}

public static class UiMapCompatibility
{
    public static CompatibilityReport Compare(UiMapRevision oldRevision, UiMapRevision newRevision, string reportRef, string completedAt)
    {
        var report = new CompatibilityReport
        {
            CompatibilityReportRef = reportRef,
            OldMapRevisionRef = oldRevision.MapRevisionRef,
            NewMapRevisionRef = newRevision.MapRevisionRef,
            CompletedAt = completedAt,
        };
        var current = newRevision.Entities.ToDictionary(x => x.MapEntityRef, StringComparer.Ordinal);
        foreach (var oldEntity in oldRevision.Entities.OrderBy(x => x.MapEntityRef, StringComparer.Ordinal))
        {
            if (current.TryGetValue(oldEntity.MapEntityRef, out var same))
            {
                report.Entries.Add(new CompatibilityEntry
                {
                    OldEntityRef = oldEntity.MapEntityRef,
                    NewEntityRefs = { same.MapEntityRef },
                    Status = same.EntityKind == oldEntity.EntityKind ? CompatibilityStatus.CompatibilityUnchanged : CompatibilityStatus.CompatibilityUnresolved,
                    Rationale = same.EntityKind == oldEntity.EntityKind ? "The stable ref and entity kind are unchanged." : "The stable ref was reused with an incompatible entity kind.",
                    AutomaticCarryForward = same.EntityKind == oldEntity.EntityKind,
                });
                continue;
            }
            var candidates = newRevision.Entities.Where(x => x.EntityKind == oldEntity.EntityKind
                && string.Equals(x.DisplayName, oldEntity.DisplayName, StringComparison.OrdinalIgnoreCase)).ToList();
            var entry = new CompatibilityEntry
            {
                OldEntityRef = oldEntity.MapEntityRef,
                Status = candidates.Count switch
                {
                    0 => CompatibilityStatus.CompatibilityRetired,
                    1 => CompatibilityStatus.CompatibilityReplaced,
                    _ => CompatibilityStatus.CompatibilitySplit,
                },
                Rationale = candidates.Count == 0
                    ? "No current entity with the reviewed name and kind was found; retirement requires consumer review."
                    : "Name/kind candidates are non-authoritative and require explicit consumer review.",
                AutomaticCarryForward = false,
            };
            entry.NewEntityRefs.AddRange(candidates.Select(x => x.MapEntityRef));
            report.Entries.Add(entry);
        }
        return report;
    }
}
