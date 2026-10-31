using System.Drawing;
using System.Runtime.InteropServices;
using Google.Protobuf;
using UIMaps.Protocol.V1;

namespace UIMaps.Recorder.Windows;

/// <summary>Qualifies a bounded mapping observation. Provider completeness and
/// temporal consistency are independent; Windows APIs are not an atomic snapshot.</summary>
public static class MappingObservationEvidence
{
    public static async Task CompleteAsync(SensorEpisodeBundle episode, IScreenCaptureProvider screen,
        IWindowTopologyProvider topology, Func<DateTimeOffset, bool> inputObserved, CancellationToken token)
    {
        var policy = RecorderPolicy.FromEffective(episode.EffectiveCapturePolicy);
        var windows = episode.SensorPackets.SelectMany(packet => packet.WindowTopology?.Windows ?? []).ToArray();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        long bytes = 0;
        foreach (var window in windows.Where(Visible))
        {
            var plane = new WindowPixelEvidence { WindowHandleEvidence = window.WindowHandleEvidence,
                BoundsPx = window.BoundsPx.Clone(), StartedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                Method = "windows-graphics-capture", Status = "unavailable" };
            try
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                    throw new PlatformNotSupportedException("Independent window images require Windows 10 2004 or later.");
                var remaining = TimeSpan.FromSeconds(5) - timer.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                var png = await NativeGraphicsCapture.CaptureWindowPngAsync((nint)long.Parse(window.WindowHandleEvidence),
                    remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                using var stream = new MemoryStream(png);
                using var image = new Bitmap(stream);
                if (image.Width != plane.BoundsPx.Width || image.Height != plane.BoundsPx.Height || IsBlank(image)
                    || png.Length > 16 * 1024 * 1024 || bytes + png.Length > 32 * 1024 * 1024)
                    throw new InvalidDataException("Independent window image cannot be qualified.");
                plane.Png = ByteString.CopyFrom(png); bytes += png.Length; plane.Status = "ok";
            }
            catch (OperationCanceledException) { throw; }
            // InvalidDataException is a SystemException, not an IOException. A
            // rejected image remains unavailable evidence, just like a failed
            // capture; it must not abort the other independently scoped windows.
            catch (Exception ex) when (ex is InvalidDataException or ExternalException or InvalidOperationException or TimeoutException
                or IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
            { plane.Status = ex is TimeoutException ? "timeout" : "unavailable"; }
            plane.EndedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            episode.WindowImages.Add(plane);
        }
        var consistency = new ObservationConsistency { Status = "unverified", Reason = "verification-unavailable" };
        try
        {
            var after = await topology.CaptureAsync(episode.DisplayDescriptor, policy, token).ConfigureAwait(false);
            var pixels = await screen.CaptureAsync(episode.DisplayDescriptor, policy, token).ConfigureAwait(false);
            consistency.VerificationWindows.AddRange(after.Windows);
            consistency.VerificationScreenshot = pixels;
            if (after.Health.State != HealthState.Complete || pixels.Health.State != HealthState.Complete)
                throw new RecorderException("RECORDER_VERIFICATION_INCOMPLETE", "Verification providers did not return complete evidence.");
            consistency.InputObserved = inputObserved(DateTimeOffset.Parse(episode.StartedAtUtc));
            consistency.TopologyMatched = ScopeMatches(windows, after.Windows);
            var before = episode.SensorPackets.Single(packet => packet.Screenshot is not null).Screenshot;
            consistency.PixelsMatched = pixels.Health.State == HealthState.Complete &&
                PixelsMatch(before.ImageBlob.ToByteArray(), pixels.ImageBlob.ToByteArray());
            consistency.Status = !consistency.InputObserved && consistency.TopologyMatched && consistency.PixelsMatched ? "stable" : "changed";
            consistency.Reason = consistency.InputObserved ? "input-during-observation" : !consistency.TopologyMatched ? "window-topology-changed"
                : !consistency.PixelsMatched ? "pixels-changed" : "matching-before-after-pixels-and-topology-no-input";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is RecorderException or IOException or ExternalException or ArgumentException)
        { consistency.Reason = "verification-unavailable:" + ex.GetType().Name; }
        consistency.CheckedAtUtc = DateTimeOffset.UtcNow.ToString("O");
        episode.ObservationConsistency = consistency;
        episode.EndedAtUtc = consistency.CheckedAtUtc;
        if (consistency.Status != "stable")
        {
            episode.EffectiveCapturePolicy.CaptureSemantics = CaptureSemantics.Survey;
            episode.EffectiveCapturePolicy.Adjustments.Add(new EffectivePolicyAdjustment { Code = "mapping_consistency_unverified",
                SafeDescription = "The retained mapping observation cannot establish temporal consistency; published as Survey evidence." });
        }
        if (episode.CalculateSize() > policy.MaxBundleSize)
            throw new RecorderException("RECORDER_CAPTURE_TOO_LARGE", "Mapping evidence exceeds the negotiated episode limit.");
    }

    private static bool Visible(WindowObservationRecord window) => window.IsVisible && !window.IsMinimized
        && !window.IsCloakedOrUnavailable && window.BoundsPx is { Width: > 0, Height: > 0 };

    public static bool ScopeMatches(IEnumerable<WindowObservationRecord> before, IEnumerable<WindowObservationRecord> after)
    {
        // Compare relative z-order; unrelated desktop windows may change absolute positions.
        static IEnumerable<string> Keys(IEnumerable<WindowObservationRecord> windows) => windows.Where(Visible)
            .OrderBy(window => window.ZOrder).Select(window => string.Join('|', window.WindowHandleEvidence,
                window.ProcessInstanceEvidence, window.ClassName, window.BoundsPx, window.Dpi, window.WindowStyle,
                window.WindowExStyle, window.RootOwnerHandleEvidence, string.Join(',', window.OwnerChainEvidence)));
        return Keys(before).SequenceEqual(Keys(after));
    }

    public static bool PixelsMatch(byte[] before, byte[] after)
    {
        using var firstStream = new MemoryStream(before); using var lastStream = new MemoryStream(after);
        using var first = new Bitmap(firstStream); using var last = new Bitmap(lastStream);
        if (first.Size != last.Size) return false;
        long changed = 0, samples = 0;
        // Ignore small rendering noise and caret blinking, not moving surfaces.
        var step = Math.Max(1, Math.Min(first.Width, first.Height) / 256);
        for (var y = 0; y < first.Height; y += step)
            for (var x = 0; x < first.Width; x += step)
            {
                var a = first.GetPixel(x, y); var b = last.GetPixel(x, y); samples++;
                if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 45) changed++;
            }
        return changed <= samples * .002;
    }

    private static bool IsBlank(Bitmap image)
    {
        for (var y = 0; y < image.Height; y += Math.Max(1, image.Height / 32))
            for (var x = 0; x < image.Width; x += Math.Max(1, image.Width / 32))
            { var pixel = image.GetPixel(x, y); if (pixel.R + pixel.G + pixel.B > 24) return false; }
        return true;
    }
}
