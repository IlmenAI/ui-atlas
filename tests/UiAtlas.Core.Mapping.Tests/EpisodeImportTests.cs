using System.Text.Json;
using System.Diagnostics;
using Google.Protobuf;
using UiAtlas.Core.Cli;
using UIMaps.Map.Core;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V3;
using Xunit;

namespace UiAtlas.Core.Mapping.Tests;

public sealed class EpisodeImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atlas-episode-test-" + Guid.NewGuid().ToString("N"));
    private string Output => Path.Combine(_root, "imported");

    public EpisodeImportTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task SnapshotRetainsOriginalBytesAndReopensAnUnreviewedScopedDraft()
    {
        var episode = EpisodeFixture.Create();
        var input = Save(episode);
        var result = await Import(input);
        Assert.Equal(episode.ToByteArray(), File.ReadAllBytes(Path.Combine(Output, "episode.pb")));
        Assert.Equal(episode.ToByteArray(), File.ReadAllBytes(input));
        Assert.Equal("draft-review-required", result.Status);
        Assert.Equal("Snapshot", result.AcquisitionSemantic);
        Assert.True(result.NotAuthorization);
        Assert.True(result.RetainsFullEpisode);
        var publication = GetPublication();
        Assert.NotNull(publication.Snapshot);
        Assert.Null(publication.Survey);
        var map = new UiMapService(Path.Combine(Output, "map"));
        var draft = map.GetDraft(result.MapDraftRef);
        Assert.Equal(result.MappingSessionRef, draft.MappingSessionRef);
        Assert.Equal(AcquisitionSemantic.AcquisitionSnapshot, Assert.Single(draft.Sources).AcquisitionSemantic);
        Assert.Contains(draft.Entities, entity => entity.DisplayName == "Save" && entity.ReviewState == ReviewState.ReviewUnreviewed);
        Assert.DoesNotContain(draft.Entities, entity => entity.DisplayName.Contains("unrelated", StringComparison.Ordinal));
        Assert.True(result.BlockingReviewCount > 0);
        Assert.Empty(Directory.GetFiles(Path.Combine(Output, "map", "revisions"), "*", SearchOption.AllDirectories));
        Assert.Equal(result, JsonSerializer.Deserialize<UiMapsEpisodeImportResult>(File.ReadAllText(Path.Combine(Output, "manifest.json"))));
    }

    [Fact]
    public async Task SurveyKeepsItsIntervalsAndPartialCoverage()
    {
        var episode = EpisodeFixture.Create(survey: true);
        episode.Health.State = HealthState.Partial;
        episode.CoverageReport.ConvergenceState = ConvergenceState.Unspecified;
        episode.CoverageReport.WindowsCompleted = 0;
        episode.CoverageReport.WindowOutcomes[0].TerminalStatus = "partial";
        EpisodeFixture.Seal(episode);
        var result = await Import(Save(episode));
        Assert.Equal("Survey", result.AcquisitionSemantic);
        Assert.Equal("Partial", result.Health);
        var publication = GetPublication();
        Assert.Null(publication.Snapshot);
        Assert.NotEmpty(publication.Survey.ObservationIntervals);
        Assert.Equal(episode.StartedAtUtc, publication.Survey.StartedAt);
        Assert.Equal(episode.EndedAtUtc, publication.Survey.EndedAt);
        var draft = new UiMapService(Path.Combine(Output, "map")).GetDraft(result.MapDraftRef);
        var source = Assert.Single(draft.Sources);
        Assert.Equal(AcquisitionSemantic.AcquisitionSurvey, source.AcquisitionSemantic);
        Assert.NotEmpty(source.CoverageLimitations);
    }

    [Fact]
    public async Task ImportedDraftSupportsReviewPublishAndLosslessRevisionExport()
    {
        var result = await Import(Save(EpisodeFixture.Create()));
        var service = new UiMapService(Path.Combine(Output, "map"));
        var draft = service.GetDraft(result.MapDraftRef);
        var control = Assert.Single(draft.Entities, entity => entity.DisplayName == "Save");
        draft = service.ApplyReviewCommand(new ReviewCommand
        {
            RequestId = "rename", MapDraftRef = draft.MapDraftRef, ExpectedEtag = draft.Etag,
            CommandKind = ReviewCommandKind.ReviewRenameEntity, SubjectRef = control.MapEntityRef,
            Value = "Save document", ReviewedBy = "synthetic-reviewer", Reason = "Synthetic edit qualification",
        }).Draft;
        draft = service.ApplyReviewCommand(new ReviewCommand
        {
            RequestId = "accept", MapDraftRef = draft.MapDraftRef, ExpectedEtag = draft.Etag,
            CommandKind = ReviewCommandKind.ReviewAcceptAllBlocking,
            ReviewedBy = "synthetic-reviewer", Reason = "Explicit synthetic review",
        }).Draft;
        var validation = service.ValidateDraft(draft.MapDraftRef, draft.Etag);
        Assert.True(validation.PublicationEligible, string.Join("; ", validation.Findings.Select(finding => finding.Message)));
        var revision = service.Publish(new PublishMapDraftRequest
        {
            RequestId = "publish", MapDraftRef = draft.MapDraftRef, ExpectedEtag = draft.Etag,
            ValidationRunRef = validation.ValidationRunRef, PublishedBy = "synthetic-reviewer",
        }).Revision;
        var exported = service.Export(revision.MapRevisionRef, includeSensitiveEvidence: false);
        var receiver = new UiMapService(Path.Combine(_root, "receiver"));
        var imported = receiver.Import(exported.PackageBytes, commit: true);
        Assert.True(imported.Valid);
        Assert.True(imported.Committed);
        Assert.Equal(revision.ToByteArray(), receiver.GetRevision(revision.MapRevisionRef).ToByteArray());
        Assert.Contains(revision.Entities, entity => entity.DisplayName == "Save document");
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("password")]
    [InlineData("missing-process")]
    [InlineData("duplicate-window")]
    [InlineData("unavailable-window")]
    [InlineData("missing-geometry")]
    [InlineData("missing-health")]
    public async Task InvalidEvidenceIsRejectedBeforePublishingOutput(string scenario)
    {
        var episode = EpisodeFixture.Create();
        var window = episode.SensorPackets[1].WindowTopology.Windows[0];
        if (scenario == "tampered") window.Title = "Changed without resealing";
        if (scenario == "password")
        {
            episode.SensorPackets[2].Accessibility.Nodes[0].IsPassword = true;
            episode.SensorPackets[2].Accessibility.Nodes[0].ValueEvidence = "synthetic-secret";
        }
        if (scenario == "missing-process") window.ProcessInstanceEvidence = "";
        if (scenario == "duplicate-window") episode.SensorPackets[1].WindowTopology.Windows.Add(window.Clone());
        if (scenario == "unavailable-window") window.IsMinimized = true;
        if (scenario == "missing-geometry") window.IntersectionWithDisplayPx = null;
        if (scenario == "missing-health") episode.Health = null;
        if (scenario != "tampered") EpisodeFixture.Seal(episode);
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(Save(episode)));
        Assert.False(Directory.Exists(Output));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task RepeatedManifestEntryCannotHideAnUnverifiedPacket()
    {
        var episode = EpisodeFixture.Create();
        episode.SensorManifest.Entries[2] = episode.SensorManifest.Entries[1].Clone();
        episode.IntegrityMetadata.ManifestSha256 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(episode.SensorManifest.ToByteArray())).ToLowerInvariant();
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(Save(episode)));
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public async Task UnknownWindowDoesNotCreateAnEmptyMap()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new UiMapsEpisodeImporter().ImportAsync(
            Save(EpisodeFixture.Create()), "unknown", Output, true));
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public async Task ExistingOutputRemainsUntouched()
    {
        Directory.CreateDirectory(Output);
        var sentinel = Path.Combine(Output, "user-file");
        File.WriteAllText(sentinel, "preserve");
        await Assert.ThrowsAsync<IOException>(() => Import(Save(EpisodeFixture.Create())));
        Assert.Equal("preserve", File.ReadAllText(sentinel));
        Assert.Single(Directory.GetFiles(Output));
    }

    [Fact]
    public async Task FullEpisodeRetentionRequiresExplicitAcknowledgement()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new UiMapsEpisodeImporter().ImportAsync(
            Save(EpisodeFixture.Create()), "selected-window", Output, false));
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public async Task OversizedFileIsRejectedBeforeReadingPayload()
    {
        var input = Path.Combine(_root, "oversized.pb");
        using (var stream = File.Create(input)) stream.SetLength((long)UiMapsEpisodeImporter.MaximumEpisodeBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(input));
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public async Task MalformedProtobufIsRejected()
    {
        var input = Path.Combine(_root, "invalid.pb");
        File.WriteAllBytes(input, [255]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(input));
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public async Task CancellationDoesNotLeaveAnOutput()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UiMapsEpisodeImporter().ImportAsync(
            Save(EpisodeFixture.Create()), "selected-window", Output, true, new CancellationToken(true)));
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public async Task CliReturnsMachineReadableDraftAndRefusesImplicitSensitiveImport()
    {
        var input = Save(EpisodeFixture.Create());
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await UiMapsImportCommand.RunAsync([input, "--window", "selected-window", "--output", Output], output, error));
        Assert.False(Directory.Exists(Output));
        Assert.Equal(0, await UiMapsImportCommand.RunAsync([input, "--window", "selected-window", "--output", Output, "--allow-sensitive-evidence"], output, error));
        Assert.Equal("draft-review-required", JsonSerializer.Deserialize<UiMapsEpisodeImportResult>(output.ToString())!.Status);
    }

    private string Save(SensorEpisodeBundle episode)
    {
        var path = Path.Combine(_root, "episode.pb");
        File.WriteAllBytes(path, episode.ToByteArray());
        return path;
    }
    [Fact]
    public async Task NativeEpisodeCanBeReviewedAndPublishedThroughTheSameCliCommands()
    {
        var imported = await Import(Save(EpisodeFixture.Create()));
        var store = new EpisodeMappingReviewStore(Output);
        var draft = Assert.Single(store.Drafts);
        using var output = new StringWriter();
        Assert.Equal(4, await UiMapsWorkspaceCommand.RunAsync(["validate-drafts", Output], output));
        Assert.Equal(0, await UiMapsWorkspaceCommand.RunAsync(["review", Output, draft.MapDraftRef, "accept-all", "-",
            "--etag", draft.Etag, "--reviewer", "test"], output));
        Assert.Equal(0, await UiMapsWorkspaceCommand.RunAsync(["publish", Output, "--reviewer", "test"], output));
        Assert.NotEmpty(new EpisodeMappingReviewStore(Output).Service.GetSession(imported.MappingSessionRef).PublishedMapRevisionRef);
    }

    [Fact]
    public void PublicFoundationDoesNotContainPrivateWorkflowContractsOrApplicationCode()
    {
        foreach (var assembly in new[] { typeof(SensorEpisodeBundle).Assembly, typeof(UiMapService).Assembly,
            typeof(UIMaps.Perception.Core.PerceptionEngine).Assembly })
            Assert.DoesNotContain(assembly.GetTypes(), type => type.Namespace is { } ns &&
                (ns.StartsWith("UIMaps.Protocol.Workflow", StringComparison.Ordinal) || ns.StartsWith("UIMaps.Application", StringComparison.Ordinal)
                || ns.StartsWith("UIMaps.Workflow", StringComparison.Ordinal)));
    }
    private Task<UiMapsEpisodeImportResult> Import(string path) =>
        new UiMapsEpisodeImporter().ImportAsync(path, "selected-window", Output, true);
    private GetPublicationResponse GetPublication() =>
        GetPublicationResponse.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(Output, "perception.pb")));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Fact]
    public async Task CliProcessImportsFromAnIndependentWorkingDirectoryAndReportsInvalidInput()
    {
        var cli = Environment.GetEnvironmentVariable("UI_ATLAS_MAPPING_CLI_PATH")
            ?? typeof(UiMapsImportCommand).Assembly.Location;
        var input = Save(EpisodeFixture.Create());
        var successful = await RunCli(cli, input, Output);
        Assert.True(successful.ExitCode == 0, successful.Error);
        Assert.Equal("draft-review-required", JsonSerializer.Deserialize<UiMapsEpisodeImportResult>(successful.Output)!.Status);
        Assert.True(File.Exists(Path.Combine(Output, "map", "map.db")));
        Assert.False(Directory.Exists(Path.Combine(_root, "legacy-catalog")));

        File.WriteAllBytes(input, [255]);
        var rejected = await RunCli(cli, input, Path.Combine(_root, "invalid-output"));
        Assert.Equal(2, rejected.ExitCode);
        Assert.Contains("not a valid UI Maps sensor episode", rejected.Error, StringComparison.Ordinal);
        Assert.Empty(rejected.Output);
        Assert.False(Directory.Exists(Path.Combine(_root, "invalid-output")));
    }

    private async Task<(int ExitCode, string Output, string Error)> RunCli(string cli, string input, string destination)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _root,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { cli, "map", "import-episode", input, "--window", "selected-window", "--output", destination, "--allow-sensitive-evidence" })
            start.ArgumentList.Add(argument);
        start.Environment["UI-ATLAS_DATA_HOME"] = Path.Combine(_root, "legacy-catalog");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await output, await error);
    }
}
