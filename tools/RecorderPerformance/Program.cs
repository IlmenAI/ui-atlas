using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;
using UIMaps.Protocol.V1;

if (args.Length == 3 && args[0] == "--inspect-provider")
{
    var packet = AccessibilityPacket.Parser.ParseFrom(File.ReadAllBytes(args[1]));
    using var destination = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write);
    using var writer = new StreamWriter(destination);
    writer.Write(JsonFormatter.Default.Format(packet));
    Console.WriteLine("Provider evidence decoded to " + args[2]);
    return 0;
}
DpiAwareBenchmark.Initialize();
if (args.Length > 0 && args[0] == "--providers") return await ProviderComparison.RunAsync(args);

if (args.Length == 0 || args[0] != "--capture")
{
    Console.WriteLine("After desktop unlock: --capture --hwnd DECIMAL --pid PID --provider PATH --output NEW_DIRECTORY [--samples 20] [--profiles mapping-checkpoint,capture-optimized] [--timeout-seconds 45]");
    Console.WriteLine("Provider attribution: --providers --hwnd DECIMAL --pid PID --provider PATH --output NEW_DIRECTORY [--samples 3] [--timeout-seconds 60]. Compares uia and uia-cached under the same independent deadline; does not alter profiles.");
    return 0;
}
var options = new Dictionary<string, string>(StringComparer.Ordinal);
if (args.Length % 2 != 1) throw new ArgumentException("Every option needs one value.");
var allowed = new HashSet<string> { "--hwnd", "--pid", "--provider", "--output", "--samples", "--profiles", "--timeout-seconds" };
for (var i = 1; i < args.Length; i += 2)
{
    if (!allowed.Contains(args[i])) throw new ArgumentException("Unknown option: " + args[i]);
    options.Add(args[i], args[i + 1]);
}
var window = long.Parse(options["--hwnd"]);
var expectedPid = int.Parse(options["--pid"]);
var provider = Path.GetFullPath(options["--provider"]);
var output = Path.GetFullPath(options["--output"]);
var count = options.TryGetValue("--samples", out var number) ? int.Parse(number) : 20;
var timeoutSeconds = options.TryGetValue("--timeout-seconds", out var timeout) ? int.Parse(timeout) : 45;
var profiles = options.GetValueOrDefault("--profiles", "mapping-checkpoint,capture-optimized").Split(',');
if (count is < 1 or > 100 || timeoutSeconds is < 5 or > 900) throw new ArgumentOutOfRangeException(nameof(count));
if (profiles.Length is < 1 or > 3 || profiles.Distinct().Count() != profiles.Length ||
    profiles.Any(p => p is not ("mapping-checkpoint" or "capture-optimized" or "deep-survey")))
    throw new ArgumentException("Select one to three distinct supported comparison profiles.");
if (!File.Exists(provider)) throw new FileNotFoundException("Provider missing.", provider);
if (Directory.Exists(output)) throw new IOException("Use a new evidence directory.");
var target = WindowCatalog.Resolve(window);
if (target.ProcessId != expectedPid) throw new InvalidOperationException("The window no longer belongs to the specified target process.");
Directory.CreateDirectory(output);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var stores = new Dictionary<string, RecordingProfileSettingsStore>();
foreach (var id in profiles)
{
    var store = new RecordingProfileSettingsStore(Path.Combine(output, id + ".settings.json"));
    store.Save(new(id)); stores[id] = store;
}
var rows = new List<object>();
var failures = 0;
var binaryHashes = new[] { provider, typeof(NativeRecorderCapture).Assembly.Location,
    typeof(UIMaps.Recorder.Windows.RecorderPolicy).Assembly.Location, typeof(SensorEpisodeBundle).Assembly.Location }
    .Distinct().Select(path => new { path, sha256 = Hash(File.ReadAllBytes(path)) }).ToArray();
using var targetProcess = Process.GetProcessById(expectedPid);
using var self = Process.GetCurrentProcess();
for (var repeat = 0; repeat < count && !cancellation.IsCancellationRequested; repeat++)
{
    // AB/BA alternation reduces drift. First iteration is retained, not silently discarded.
    foreach (var id in repeat % 2 == 0 ? profiles : profiles.Reverse())
    {
        if (cancellation.IsCancellationRequested) break;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var stages = new List<object>();
        var sampleStart = Stopwatch.GetTimestamp();
        var file = $"{repeat:D3}-{id}.mlrec";
        await using var session = new ManualRecordingSession(target, Path.Combine(output, file),
            null, null, null, provider, true, stores[id]) { FullRawObservations = true };
        session.RawCaptureProgress = stage => stages.Add(new { stage, elapsedMs = Stopwatch.GetElapsedTime(sampleStart).TotalMilliseconds });
        try
        {
            session.Start(explicitConsent: true);
            var startupMs = Stopwatch.GetElapsedTime(sampleStart).TotalMilliseconds;
            self.Refresh(); targetProcess.Refresh();
            var cpu = self.TotalProcessorTime; var targetCpu = targetProcess.TotalProcessorTime;
            var captureStart = Stopwatch.GetTimestamp();
            var frame = await session.CaptureAsync("performance-comparison", deadline.Token);
            var captureMs = Stopwatch.GetElapsedTime(captureStart).TotalMilliseconds;
            self.Refresh(); targetProcess.Refresh();
            var parentCpuMs = (self.TotalProcessorTime - cpu).TotalMilliseconds;
            var targetCpuMs = (targetProcess.TotalProcessorTime - targetCpu).TotalMilliseconds;
            var parentWorkingSetBytes = self.WorkingSet64; var targetWorkingSetBytes = targetProcess.WorkingSet64;
            var sealStart = Stopwatch.GetTimestamp();
            session.Complete();
            var sealMs = Stopwatch.GetElapsedTime(sealStart).TotalMilliseconds;
            using var bundle = RecordingBundle.Open(Path.Combine(output, file));
            var episodes = bundle.Entries.Where(p => p.EndsWith(".episode.pb", StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => SensorEpisodeBundle.Parser.ParseFrom(bundle.ReadBytes(p, CaptureSizeLimits.MaximumEpisodeBytes))).ToArray();
            var last = SensorEpisodeBundle.Parser.ParseFrom(bundle.ReadBytes(frame.NativeEpisodeEntry!, CaptureSizeLimits.MaximumEpisodeBytes));
            var packets = last.SensorPackets.Where(p => p.Accessibility is not null).ToArray();
            var nodes = packets.SelectMany(p => p.Accessibility.Nodes).ToArray();
            var providerOutcomes = episodes.SelectMany(e => e.SensorPackets)
                .Where(p => p.Accessibility is not null).SelectMany(p => p.Accessibility.ProviderAttempts).ToArray();
            var row = new {
                profile = id, iteration = repeat, firstIteration = repeat == 0, outcome = "captured",
                startupMs, captureMs, sealMs, parentCpuMs, targetCpuMs, parentWorkingSetBytes, targetWorkingSetBytes,
                artifact = file, artifactBytes = new FileInfo(Path.Combine(output, file)).Length,
                acquisitionAttempts = episodes.Length,
                health = last.Health.State.ToString(), consistency = last.ObservationConsistency?.Status,
                temporalConsistencyEstablished = last.ObservationConsistency?.Status == "stable",
                // This is a machine-checkable screen, not a substitute for required-control review.
                providerCoverageComplete = packets.Length > 0 && packets.All(p =>
                    p.Accessibility.Health.State == HealthState.Complete && p.Accessibility.TraversalFrontier.Count == 0) &&
                    providerOutcomes.All(p => p.Status == "completed"),
                windows = last.SensorPackets.Sum(p => p.WindowTopology?.Windows.Count ?? 0),
                nodes = nodes.Length, frontier = packets.Sum(p => p.Accessibility.TraversalFrontier.Count),
                controlSignatures = nodes.Select(n => Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
                    n.ProviderId, n.NativeWindowHandleEvidence, n.AutomationId, n.Name, n.ControlType, n.ClassName,
                    bounds = n.BoundsPx is null ? null : new[] { n.BoundsPx.X, n.BoundsPx.Y, n.BoundsPx.Width, n.BoundsPx.Height }
                })))).Distinct().Order().ToArray(),
                images = last.WindowImages.Select(p => new { p.Status, bytes = p.Png.Length }).ToArray(),
                providers = providerOutcomes.Select(p => new {
                    p.ProviderId, p.WorkerVersion, p.Status, p.NodesIncluded, p.FailureClass,
                    elapsedMs = DateTimeOffset.TryParse(p.StartedAt, out var a) && DateTimeOffset.TryParse(p.EndedAt, out var b)
                        ? (double?)(b - a).TotalMilliseconds : null
                }).ToArray(), stages
            };
            rows.Add(row);
            Console.WriteLine($"{repeat:D3} {id}: {captureMs:F1} ms; {row.nodes} nodes; {row.health}; {row.consistency}; {row.acquisitionAttempts} attempt(s)");
        }
        catch (Exception ex)
        {
            failures++;
            // Preserve completed observations on interruption; no missing capture is reported as fast success.
            try { session.Fail(); } catch (Exception sealError) { stages.Add(new { stage = "failure-seal", error = sealError.GetType().Name }); }
            rows.Add(new { profile = id, iteration = repeat, outcome = cancellation.IsCancellationRequested ? "cancelled" : "failed",
                elapsedMs = Stopwatch.GetElapsedTime(sampleStart).TotalMilliseconds, error = ex.GetType().Name,
                code = ex is UIMaps.Recorder.Windows.RecorderException recorder ? recorder.Code : null, stages });
            Console.WriteLine($"{repeat:D3} {id}: {ex.GetType().Name}");
        }
        var resultsPath = Path.Combine(output, "capture-results.json");
        File.WriteAllText(resultsPath + ".tmp", JsonSerializer.Serialize(new {
            schema = "map-capture-performance/3", targetPid = expectedPid, targetWindow = window,
            dpiAwareness = "per-monitor-v2", targetBoundsPx = target.Bounds,
            targetStartedUtc = target.ProcessStartedUtc, binaryHashes, timeoutSeconds,
            notes = "Actual ManualRecordingSession with FullRawObservations: captureMs includes settling, retry, acquisition, verification, projection and observation persistence. Startup and final archive sealing are separate. HUD rendering and child-worker CPU are excluded. User profile settings are never opened. Synthetic signature equality is a diagnostic only; review expected controls, ownership and ancestry.",
            observations = rows
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(resultsPath + ".tmp", resultsPath, true);
    }
}
return cancellation.IsCancellationRequested ? 130 : failures > 0 ? 1 : 0;

static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

internal static class DpiAwareBenchmark
{
    internal static void Initialize()
    {
        // Console entry points do not inherit the product UI's DPI manifest.
        // WindowCatalog and the pixel workers must use the same physical coordinates.
        _ = SetProcessDpiAwarenessContext(new nint(-4));
        if (!AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), new nint(-4)))
            throw new InvalidOperationException("Benchmark requires per-monitor-v2 DPI awareness before resolving window bounds.");
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
}
