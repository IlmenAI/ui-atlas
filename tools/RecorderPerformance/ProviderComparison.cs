using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using UiAtlas.Core.Recording.Windows;
using UIMaps.Protocol.V1;
using UIMaps.Recorder.Windows;

internal static class ProviderComparison
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length % 2 != 1) throw new ArgumentException("Every option needs one value.");
        var allowed = new HashSet<string> { "--hwnd", "--pid", "--provider", "--output", "--samples", "--timeout-seconds", "--operations" };
        var options = new Dictionary<string, string>();
        for (var i = 1; i < args.Length; i += 2)
        {
            if (!allowed.Contains(args[i])) throw new ArgumentException("Unknown option: " + args[i]);
            options.Add(args[i], args[i + 1]);
        }
        var hwnd = long.Parse(options["--hwnd"]);
        var pid = int.Parse(options["--pid"]);
        var provider = Path.GetFullPath(options["--provider"]);
        var output = Path.GetFullPath(options["--output"]);
        var count = int.Parse(options.GetValueOrDefault("--samples", "3"));
        var seconds = int.Parse(options.GetValueOrDefault("--timeout-seconds", "60"));
        if (count is < 1 or > 30 || seconds is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(count));
        if (!File.Exists(provider)) throw new FileNotFoundException("Provider missing", provider);
        if (Directory.Exists(output)) throw new IOException("Use a new evidence directory.");
        var target = WindowCatalog.Resolve(hwnd);
        if (target.ProcessId != pid) throw new InvalidOperationException("Target process/window mismatch.");
        Directory.CreateDirectory(output);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var runner = new ProviderProcessRunner(provider);
        var observations = new List<object>();
        var failures = 0;
        var operations = options.GetValueOrDefault("--operations", "uia,uia-cached").Split(',');
        if (operations.Length != 2 || operations.Distinct().Count() != 2 || operations.Any(x => x is not ("uia" or "uia-cached" or "msaa" or "msaa-root-reuse")))
            throw new ArgumentException("Select two distinct supported provider operations.");
        for (var iteration = 0; iteration < count && !stop.IsCancellationRequested; iteration++)
        {
            foreach (var operation in iteration % 2 == 0 ? operations : operations.Reverse())
            {
                if (stop.IsCancellationRequested) break;
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var packet = await runner.RunAsync(new ProbeRequest(operation, 0, 0, 0, 0, hwnd,
                        "performance-native-window", 10_000, 64, 0), AccessibilityPacket.Parser,
                        TimeSpan.FromSeconds(seconds), stop.Token);
                    var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var artifact = $"{iteration:D3}-{operation}.pb";
                    File.WriteAllBytes(Path.Combine(output, artifact), packet.ToByteArray());
                    var identities = packet.Nodes.Select(n => Hash(JsonSerializer.Serialize(new {
                        n.AutomationId, n.Name, n.ControlType, n.ClassName,
                        bounds = n.BoundsPx is null ? null : new[] { n.BoundsPx.X, n.BoundsPx.Y, n.BoundsPx.Width, n.BoundsPx.Height }
                    }))).Distinct().Order().ToArray();
                    var ids = packet.Nodes.Select(n => n.AccessibilityEvidenceId).ToHashSet();
                    var row = new { iteration, operation, outcome = "returned", elapsedMs, artifact,
                        health = packet.Health.State.ToString(), nodes = packet.Nodes.Count,
                        packet.TimeLimited, packet.NodeLimited, packet.DepthLimited,
                        frontier = packet.TraversalFrontier.Count,
                        missingParents = packet.Nodes.Count(n => n.ParentEvidenceId.Length > 0 && !ids.Contains(n.ParentEvidenceId)),
                        properties = new { attempted = packet.Nodes.Sum(n => n.PropertyCoverage?.Attempted ?? 0),
                            read = packet.Nodes.Sum(n => n.PropertyCoverage?.Read ?? 0), failed = packet.Nodes.Sum(n => n.PropertyCoverage?.Failed ?? 0) },
                        controls = identities };
                    observations.Add(row);
                    Console.WriteLine($"{iteration:D3} {operation}: {elapsedMs:F1} ms; {row.nodes} nodes; {row.health}; {row.frontier} frontier(s)");
                }
                catch (Exception error)
                {
                    failures++;
                    observations.Add(new { iteration, operation, outcome = "failed", elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        error = error.GetType().Name, code = error is RecorderException recorder ? recorder.Code : null });
                    Console.WriteLine($"{iteration:D3} {operation}: {error.GetType().Name}");
                }
                var results = Path.Combine(output, "provider-results.json");
                File.WriteAllText(results + ".tmp", JsonSerializer.Serialize(new { schema = "provider-comparison/1", pid, hwnd,
                    target.ProcessStartedUtc, provider, providerSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(provider))),
                    deadlineSeconds = seconds, maxNodes = 10_000, maxDepth = 64,
                    limitations = "Provider-only attribution including process/IPC overhead. No capture profile edits, input hooks, screenshots, settling, persistence pipeline or temporal consistency verification. Static fixture identity/ancestry checks do not prove application-wide equivalence.", observations },
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(results + ".tmp", results, true);
            }
        }
        return stop.IsCancellationRequested ? 130 : failures == 0 ? 0 : 1;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
