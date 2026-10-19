using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Desktop;
using UiAtlas.Core.Mapping.Atlas;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;
using UiAtlas.Core.Storage;
using System.Text.Json;
using Google.Protobuf;
using Xunit;

namespace UiAtlas.Core.Mapping.Tests;

public sealed class LiveMappingTests
{
    [Fact]
    public async Task AttendedCaptureOfOwnedTestWindowBuildsMapsAndRendersTheExistingEditor()
    {
        var directory = Path.Combine(Path.GetTempPath(), "atlas-live-mapping-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ready = new TaskCompletionSource<(Window Window, long Hwnd)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var button = new Button { Content = "Save example", Width = 140, Height = 42, Margin = new Thickness(20) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "AtlasMappingSave");
            var table = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true, Height = 180,
                ItemsSource = new[] { new { Name = "Example A", Value = 1 }, new { Name = "Example B", Value = 2 } } };
            System.Windows.Automation.AutomationProperties.SetAutomationId(table, "AtlasMappingGrid");
            var content = new StackPanel(); content.Children.Add(button); content.Children.Add(table);
            var window = new Window { Title = "UI Atlas mapping qualification target", Width = 420, Height = 360,
                Left = 120, Top = 140, Content = content, ShowActivated = false, Topmost = true };
            window.Show();
            ready.SetResult((window, new WindowInteropHelper(window).Handle.ToInt64()));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        var target = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await target.Window.Dispatcher.InvokeAsync(() => target.Window.UpdateLayout(), DispatcherPriority.ContextIdle);
        // Show() precedes the compositor's window-opening animation. The fixture
        // must settle before asking the recorder to prove unchanged pixels.
        await Task.Delay(500);
        try
        {
            var recording = Path.Combine(directory, "live.mlrec");
            var cli = Environment.GetEnvironmentVariable("UI_ATLAS_MAPPING_CLI_PATH") ?? typeof(UiAtlas.Core.Cli.UiMapsImportCommand).Assembly.Location;
            await using (var session = new ManualRecordingSession(WindowCatalog.Resolve(target.Hwnd), recording, automation: new UiaWorkerClient(cli),
                nativeProviderPath: Path.Combine(Path.GetDirectoryName(cli)!, "native-recorder", "UIMaps.Recorder.ProviderProbe.dll")))
            {
                session.Start(true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var captured = await session.CaptureAsync("baseline", timeout.Token);
                session.Complete();
                // Retain failed capture evidence too: a failed stability check happens
                // before reconstruction and needs its before/after pixels for diagnosis.
                if (Environment.GetEnvironmentVariable("UI_ATLAS_VISUAL_EVIDENCE") is { Length: > 0 } captureEvidence)
                {
                    Directory.CreateDirectory(captureEvidence);
                    File.Copy(recording, Path.Combine(captureEvidence, "live-capture.mlrec"), true);
                }
                Assert.True(session.UsesNativeRecorder);
                Assert.NotNull(captured.NativeEpisodeEntry);
                Assert.True(captured.Acquisition?.StabilityStatus == "stable", captured.Acquisition?.DiagnosticCode);
                Assert.True(UiAtlas.Core.Reader.RawVariantPresentation.AllowsOverlay(captured));
                Assert.Contains(captured.WindowImages!, image => image.Status == "ok");
            }
            var recordingValidation = RecordingBundleValidator.Validate(recording);
            Assert.True(recordingValidation.IsValid, string.Join("; ", recordingValidation.Issues.Select(issue => issue.Code + ":" + issue.Message)));
            SavedDataGridReview gridReview;
            using (var retained = RecordingBundle.Open(recording))
            {
                var native = UIMaps.Protocol.V1.SensorEpisodeBundle.Parser.ParseFrom(retained.ReadBytes("raw/native/frame-000001.episode.pb"));
                Assert.Equal("uimaps-recorder", native.Producer);
                Assert.Equal("recorder-stopwatch-v1", native.ClockMetadata.ClockId);
                var longText = native.Clone();
                var longNode = longText.SensorPackets.First(packet => packet.Accessibility?.Nodes.Count > 0).Accessibility.Nodes[0];
                longNode.Name = new string('n', 5000); longNode.AutomationId = new string('a', 800);
                var presentation = NativeRecorderCapture.Project(longText, WindowCatalog.Resolve(target.Hwnd), 1, "fixture", new());
                Assert.All(presentation.Automation, value => { Assert.True(value.Name.Length <= 4096); Assert.True(value.AutomationId.Length <= 512); });
                Assert.Equal(5000, longNode.Name.Length);
                var frame = retained.ReadJson<FrameObservation>("raw/observations/frame-000001.json");
                Assert.DoesNotContain(frame.Automation, value => value.ScopeStatus == "verified");
                var host = Assert.Single(frame.Automation, value => value.AutomationId == "AtlasMappingGrid");
                var pixels = retained.ReadBytes(frame.FrameEntry);
                var imagePath = Path.Combine(directory, "reviewed-grid.png"); File.WriteAllBytes(imagePath, pixels);
                var imageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels)).ToLowerInvariant();
                var manifest = retained.ReadJson<RecordingManifest>("manifest.json");
                gridReview = new("test-grid", "Example table", new(new(manifest.Target.ProcessName, frame.Window.ClassName, frame.Window.Title, [],
                    new(host.ClassName, host.AutomationId)), "test", new(host.ClassName, host.AutomationId), host.Bounds, true,
                    manifest.SessionId, frame.Sequence, host.RuntimeId),
                    new("test-review", "test-grid", new("review-1", false, null,
                        [new("physical-0", 0, "Name", 0, 100), new("physical-100", 1, "Value", 100, 200)], 0, 25),
                        new(0, 0, 200, 25), imagePath, imageHash, "", GridCaptureStatus.Partial,
                        new(true, true, false, false, true, false, false, [], ["fixture-visible-scope"]),
                        new(GridRestorationStatus.SafelySkipped, "No input in this mapping fixture", false, false), [], [], DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
                var capturePath = Path.Combine(directory, "grid-capture.json");
                var capture = new CapturedGrid("fixture-capture", gridReview.GridId, gridReview.Review.Schema,
                    new(target.Hwnd, target.Hwnd, manifest.Target.ProcessId, manifest.Target.ProcessStartedUtc, target.Hwnd, frame.Window.Bounds, host.Bounds),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, GridCaptureStatus.Partial, GridAcquisitionStage.Verify, ["fixture-visible-scope"],
                    [new("tile-0001", imagePath, imageHash, DateTimeOffset.UtcNow, frame.Window.Bounds,
                        new(0, 0, 200, 25), new(0, 25, 200, 100), 0, 0, true, [])], [], [], gridReview.Review.Coverage,
                    gridReview.Review.Restoration, capturePath);
                File.WriteAllText(capturePath, JsonSerializer.Serialize(capture, JsonDefaults.Options));
                gridReview = gridReview with { Review = gridReview.Review with { CaptureManifestPath = capturePath } };
            }
            var mapPath = Path.Combine(directory, "live.db");
            var graph = await AtlasMapWorkspace.BuildAsync([recording], mapPath, gridReviews: [gridReview]);
            var workspace = AtlasMapWorkspace.Open(mapPath);
            Assert.Equal("mapped-schema", Assert.Single(workspace.Index.GridReviews!).Status);
            var mappedGrid = Assert.Single(workspace.Drafts.SelectMany(value => value.Entities), value => value.EntityKind == UIMaps.Protocol.V3.MapEntityKind.MapDataGrid);
            Assert.Equal(UIMaps.Protocol.V3.GridSchemaStatus.GridSchemaPartial, mappedGrid.HostedDataGrid.SchemaStatus);
            Assert.Equal(new[] { "Name", "Value" }, mappedGrid.HostedDataGrid.ExpectedHeaders);
            Assert.All(workspace.Drafts, draft => Assert.Empty(draft.HostedGridObservations));
            Assert.Contains(graph.Nodes, node => node.Label == "Name" && node.ParentId == mappedGrid.MapEntityRef);
            var now = DateTimeOffset.UtcNow;
            var prediction = new SpeculativePredictionState("prediction-only", null, "fixture", "future-click", "Unobserved dialog",
                AutoMappingWorkKind.DialogLauncher, "dialog", .5, 1, 1, "test hypothesis", SpeculativePredictionStatus.Predicted,
                gridReview.Context.SourceBundleId!, 1, null, null, now);
            LogicalMapSessionStore.Save(Path.ChangeExtension(mapPath, ".session.json"), new("ui-atlas.logical-map-session/1", "fixture", "test", now, now,
                [new(gridReview.Context.SourceBundleId!, recording)], SpeculativePlanning: SpeculativePlanningState.Empty(now) with { Predictions = [prediction] },
                DataGrids: [gridReview]));
            var requestId = Guid.NewGuid().ToString("N");
            UiAtlas.Core.Cli.AtlasRecordingHandoff.Complete(requestId, Path.Combine(directory, "result.json"), WindowCatalog.Resolve(target.Hwnd), mapPath);
            var transfer = UIMaps.Protocol.V3.ImportMappingWorkspaceRequest.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(directory, requestId + ".import.pb")));
            var destination = new UIMaps.Map.Core.UiMapService(Path.Combine(directory, "private-map-library"));
            var imported = destination.ImportWorkspace(transfer);
            Assert.Equal(workspace.Index.Sessions.Select(value => value.DraftRef), imported.MapDraftRefs);
            Assert.True(destination.ImportWorkspace(transfer).AlreadyPresent);
            Assert.Equal(mappedGrid, destination.GetDraft(imported.MapDraftRefs[0]).Entities.Single(value => value.MapEntityRef == mappedGrid.MapEntityRef));
            Assert.True(File.Exists(Path.Combine(directory, "private-map-library", "workspace-sources", imported.RetainedArchiveSha256 + ".atlasmap")));
            var portableMap = AtlasMapPackage.Import(transfer.SourceArchivePath, Path.Combine(directory, "portable"), true);
            var portable = AtlasMapWorkspace.Open(portableMap);
            Assert.Equal("mapped-schema", Assert.Single(portable.Index.GridReviews!).Status);
            Assert.True(File.Exists(portable.Resolve(Assert.Single(portable.Index.GridReviews!).ImageFile)));
            Assert.Contains(portable.Project().Nodes, node => node.ParentId == mappedGrid.MapEntityRef && node.Label == "Value");
            var editorSession = LogicalMapSessionStore.Load(Path.ChangeExtension(portableMap, ".session.json"));
            Assert.Equal(prediction, Assert.Single(editorSession.SpeculativePlanning!.Predictions));
            Assert.DoesNotContain(portable.Drafts.SelectMany(value => value.Entities), value => value.DisplayName == prediction.DisplayName);
            var portableGrid = Assert.Single(editorSession.DataGrids!);
            var retainedCapture = JsonSerializer.Deserialize<CapturedGrid>(File.ReadAllText(portableGrid.Review.CaptureManifestPath), JsonDefaults.Options)!;
            var tile = Assert.Single(retainedCapture.Tiles);
            Assert.False(Path.IsPathRooted(tile.PngPath));
            Assert.True(File.Exists(Path.GetFullPath(tile.PngPath, Path.GetDirectoryName(portableGrid.Review.CaptureManifestPath)!)));
            var transferEvidence = Environment.GetEnvironmentVariable("UI_ATLAS_HANDOFF_EVIDENCE");
            if (!string.IsNullOrEmpty(transferEvidence))
            {
                Directory.CreateDirectory(transferEvidence);
                var archive = Path.Combine(transferEvidence, "native-recording.atlasmap");
                File.Copy(transfer.SourceArchivePath, archive, true);
                transfer.SourceArchivePath = archive;
                File.WriteAllBytes(Path.Combine(transferEvidence, "import.pb"), transfer.ToByteArray());
            }
            await ExerciseCliHandoff(cli, target.Hwnd, directory);
            Assert.Contains(graph.Nodes, node => node.Kind == GraphNodeKind.Control && node.Label.Contains("Save example", StringComparison.Ordinal));
            static bool Layer(GraphNode node, string layer) => node.Properties.Any(property => property.Name == "layer" && property.Value == layer);
            foreach (var layer in new[] { "raw-data-streams", "raw-world", "semantic-world" })
                Assert.Contains(graph.Nodes, node => Layer(node, layer) && node.Label.Contains("Save example", StringComparison.Ordinal));
            var reader = new UiAtlas.Core.Reader.UiMappingReadModel(graph);
            foreach (var level in new[] { UiAtlas.Core.Reader.UiUnderstandingLevel.RawWorld, UiAtlas.Core.Reader.UiUnderstandingLevel.SemanticWorld })
                Assert.All(reader.VariantsFor(reader.LayerFor(level).Surfaces), variant => Assert.Equal("snapshot", variant.CaptureStatus));
            var rawStreams = graph.Nodes.Where(node => Layer(node, "raw-data-streams") && node.Kind == GraphNodeKind.Window).ToArray();
            Assert.NotEmpty(rawStreams);
            Assert.All(rawStreams, node => Assert.Contains(node.Properties, property => property.Name == "nativeEpisodeEntry" && property.Value == "raw/native/frame-000001.episode.pb"));
            var semantic = graph.Nodes.Where(node => Layer(node, "semantic-world") && node.Kind == GraphNodeKind.Control).ToArray();
            Assert.All(semantic, node => Assert.Contains(node.Properties, property => property.Name == "sourceRawControlId"
                && graph.Nodes.Any(raw => raw.Id == property.Value && Layer(raw, "raw-world"))));
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await target.Window.Dispatcher.InvokeAsync(async () =>
            {
                ExplorerWindow? editor = null;
                MapReviewWindow? review = null;
                MappingEvidenceWindow? evidence = null;
                try
                {
                    editor = new ExplorerWindow(mapPath, recording) { ShowActivated = false };
                    editor.Show();
                    await Task.Delay(1500);
                    Assert.Contains("UI Knowledge Graph Editor", ((TextBlock)FindTitle(editor)).Text);
                    review = new MapReviewWindow(mapPath) { ShowActivated = false };
                    review.Show(); review.UpdateLayout();
                    await Task.Delay(200);
                    var entities = (DataGrid)typeof(MapReviewWindow).GetField("_entities", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(review)!;
                    Assert.True(entities.ActualHeight >= 120, "Map entities must remain visible when review details are long.");
                    Assert.All(entities.Columns, column => Assert.True(column.ActualWidth >= 120, "Entity columns must be readable."));
                    evidence = new MappingEvidenceWindow(mapPath) { ShowActivated = false };
                    evidence.Show(); evidence.UpdateLayout();
                    var evidenceRoot = Environment.GetEnvironmentVariable("UI_ATLAS_VISUAL_EVIDENCE");
                    if (!string.IsNullOrEmpty(evidenceRoot))
                    {
                        Directory.CreateDirectory(evidenceRoot);
                        Render(editor, Path.Combine(evidenceRoot, "atlas-maps-editor.png"));
                        var buttons = (System.Collections.IDictionary)typeof(ExplorerWindow).GetField("_levelButtons", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor)!;
                        foreach (System.Collections.DictionaryEntry entry in buttons)
                        {
                            ((Button)entry.Value!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            editor.UpdateLayout();
                            Render(editor, Path.Combine(evidenceRoot, "atlas-native-" + entry.Key + ".png"));
                        }
                        Render(review, Path.Combine(evidenceRoot, "atlas-maps-review.png"));
                        Render(evidence, Path.Combine(evidenceRoot, "atlas-maps-source.png"));
                    }
                    rendered.SetResult();
                }
                catch (Exception ex) { rendered.SetException(ex); }
                finally { evidence?.Close(); review?.Close(); editor?.Close(); }
            });
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            target.Window.Dispatcher.Invoke(() => { target.Window.Close(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send); });
            thread.Join(TimeSpan.FromSeconds(10));
            Directory.Delete(directory, true);
        }
    }
    private static object FindTitle(ExplorerWindow editor) => typeof(ExplorerWindow)
        .GetField("_title", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor)!;

    private static async Task ExerciseCliHandoff(string cli, long hwnd, string root)
    {
        var directory = Path.Combine(root, "cli-handoff"); Directory.CreateDirectory(directory);
        var target = WindowCatalog.Resolve(hwnd);
        var requestId = Guid.NewGuid().ToString("N");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        foreach (var argument in new[] { cli, "record", "--hwnd", hwnd.ToString(), "--out", Path.Combine(directory, "recording.mlrec"),
            "--request-id", requestId, "--result", Path.Combine(directory, "result.json"), "--expected-process-id", target.ProcessId.ToString(),
            "--expected-process-start", target.ProcessStartedUtc.ToString("O") }) start.ArgumentList.Add(argument);
        start.Environment["UI-ATLAS_DATA_HOME"] = Path.Combine(directory, "catalog");
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) output.Enqueue(args.Data); };
        process.BeginOutputReadLine();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        try
        {
            await Invoke("AtlasRecorderStart");
            await Invoke("AtlasRecorderModeManual");
            while (!output.Any(line => line.Contains("Recording ready. Engine: UIMaps native", StringComparison.Ordinal)))
            {
                Assert.False(process.HasExited, string.Join("\n", output) + "\n" + (process.HasExited ? await errors : ""));
                await Task.Delay(100, deadline.Token);
            }
            await Invoke("AtlasRecorderFinish");
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, string.Join("\n", output) + "\n" + await errors);
            var receipt = System.Text.Json.JsonSerializer.Deserialize<UiAtlas.Core.Cli.AtlasRecordingReceipt>(File.ReadAllText(Path.Combine(directory, "result.json")))!;
            Assert.Equal(requestId, receipt.RequestId); Assert.Equal(hwnd, receipt.WindowHandle);
            var imported = UIMaps.Protocol.V3.ImportMappingWorkspaceRequest.Parser.ParseFrom(File.ReadAllBytes(receipt.ImportRequestPath));
            var library = new UIMaps.Map.Core.UiMapService(Path.Combine(directory, "library"));
            Assert.NotEmpty(library.ImportWorkspace(imported).MapDraftRefs);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }

        async Task Invoke(string automationId)
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var windows = System.Windows.Automation.AutomationElement.RootElement.FindAll(System.Windows.Automation.TreeScope.Children,
                    new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ProcessIdProperty, process.Id));
                foreach (System.Windows.Automation.AutomationElement window in windows)
                {
                    var button = window.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.AutomationIdProperty, automationId));
                    if (button is not null && button.Current.IsEnabled && !button.Current.IsOffscreen
                        && button.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out var pattern))
                    { ((System.Windows.Automation.InvokePattern)pattern).Invoke(); return; }
                }
                Assert.False(process.HasExited, string.Join("\n", output));
                await Task.Delay(100, deadline.Token);
            }
        }
    }
    private static void Render(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
