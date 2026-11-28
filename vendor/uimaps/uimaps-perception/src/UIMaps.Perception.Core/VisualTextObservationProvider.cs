using System.Security.Cryptography;
using System.Text;
using UIMaps.Perception.Core.Grid;
using UIMaps.Protocol.V1;

namespace UIMaps.Perception.Core;

public sealed class VisualTextObservationProvider(IGridOcrProvider? ocrProvider = null)
{
    private readonly IGridOcrProvider _ocrProvider = ocrProvider ?? new WindowsOcrProvider();

    public async Task<IReadOnlyList<Observation>> ReadAsync(
        SensorEpisodeBundle bundle,
        DateTimeOffset published,
        CancellationToken cancellationToken)
    {
        var screenshotPacket = bundle.SensorPackets.Single(packet => packet.StreamKind == SensorStreamKind.Screenshot);
        var screenshot = screenshotPacket.Screenshot;
        var census = bundle.SensorPackets.Single(packet => packet.WindowTopology is not null).WindowTopology;
        var windows = census.Windows.Where(window => window.IsVisible && !window.IsMinimized &&
            !window.IsCloakedOrUnavailable && window.BoundsPx is { Width: > 0, Height: > 0 }).ToArray();
        var accessibility = bundle.SensorPackets.Where(packet => packet.Accessibility is not null)
            .SelectMany(packet => AccessibilityEvidence.Nodes(packet.Accessibility)).ToArray();
        var result = new List<Observation>();
        var independent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plane in bundle.WindowImages.Where(p => p.Status == "ok" && !p.Png.IsEmpty))
        {
            var window = windows.FirstOrDefault(w => w.WindowHandleEvidence == plane.WindowHandleEvidence && w.BoundsPx.Equals(plane.BoundsPx));
            if (window is null) continue;
            independent.Add(window.WindowEvidenceId);
            var interval = screenshotPacket.CaptureInterval.Clone();
            if (DateTimeOffset.TryParse(interval.EndedAtUtc, out var anchor) && DateTimeOffset.TryParse(plane.StartedAtUtc, out var start)
                && DateTimeOffset.TryParse(plane.EndedAtUtc, out var end) && end >= start)
            {
                var frequency = bundle.ClockMetadata.MonotonicFrequencyHz;
                var anchorTicks = interval.EndedAtMonotonicTicks;
                interval.StartedAtMonotonicTicks = anchorTicks + (long)((start - anchor).TotalSeconds * frequency);
                interval.EndedAtMonotonicTicks = anchorTicks + (long)((end - anchor).TotalSeconds * frequency);
                interval.EndedAtUtc = plane.EndedAtUtc;
            }
            await ReadImage(plane.Png, plane.BoundsPx, plane.BoundsPx.Width, plane.BoundsPx.Height, interval,
                "window", bounds => Contains(window.BoundsPx, bounds) ? window : null).ConfigureAwait(false);
        }
        // A process-scoped census omits other applications even if its own traversal completed.
        // Desktop OCR needs a complete z-order to attribute all the pixels in each word and phrase.
        if (windows.Any(w => !independent.Contains(w.WindowEvidenceId)) && !census.WindowLimited &&
            census.Health?.State == HealthState.Complete && census.WindowsObserved == census.Windows.Count &&
            bundle.ObservationConsistency?.Status == "stable" && !screenshot.ImageBlob.IsEmpty && screenshot.WidthPx > 0 && screenshot.HeightPx > 0)
        {
            WindowObservationRecord? VisibleOwner(RectPx bounds)
            {
                if (screenshot.ProtectedOrMissingRegions.Any(r => Intersects(r.BoundsPx, bounds))) return null;
                var overlapping = windows.Where(w => Intersects(w.BoundsPx, bounds)).OrderBy(w => w.ZOrder).ToArray();
                return overlapping.FirstOrDefault() is { } front && Contains(front.BoundsPx, bounds) &&
                    !independent.Contains(front.WindowEvidenceId) && (overlapping.Length == 1 || overlapping[1].ZOrder > front.ZOrder)
                    ? front : null;
            }
            await ReadImage(screenshot.ImageBlob, bundle.DisplayDescriptor.BoundsPx, screenshot.WidthPx, screenshot.HeightPx,
                screenshotPacket.CaptureInterval, "desktop", VisibleOwner).ConfigureAwait(false);
        }
        return result;

        async Task ReadImage(Google.Protobuf.ByteString png, RectPx imageBounds, int width, int height,
            CaptureInterval interval, string planeKind, Func<RectPx, WindowObservationRecord?> owner)
        {
            var hash = Convert.ToHexString(SHA256.HashData(png.Span)).ToLowerInvariant();
            var words = await _ocrProvider.ReadAsync(png.ToByteArray(), cancellationToken).ConfigureAwait(false);
            string Scope(GridOcrToken token)
            {
                var bounds = ToScreenBounds(token.Bounds, imageBounds, width, height);
                var window = owner(bounds);
                if (window is null) return string.Empty;
                var element = accessibility.Where(node => node.WindowEvidenceId == window.WindowEvidenceId && Contains(node.BoundsPx, bounds))
                    .MinBy(node => Area(node.BoundsPx));
                return element?.AccessibilityEvidenceId ?? window.WindowEvidenceId;
            }
            foreach (var phrase in GroupWords(words, Scope))
            {
                var bounds = ToScreenBounds(phrase.Bounds, imageBounds, width, height);
                var window = owner(bounds);
                if (window is null || DuplicatesAccessibility(phrase.Text, bounds, accessibility, window.WindowEvidenceId)) continue;
                var parent = accessibility
                    .Where(node => node.WindowEvidenceId == window.WindowEvidenceId && Contains(node.BoundsPx, bounds))
                    .OrderBy(node => Area(node.BoundsPx))
                    .FirstOrDefault();
                var relative = new RectPx
                {
                    X = Quantize(bounds.X - window.BoundsPx.X),
                    Y = Quantize(bounds.Y - window.BoundsPx.Y),
                    Width = Math.Max(4, Quantize(bounds.Width)),
                    Height = Math.Max(4, Quantize(bounds.Height)),
                };
                var evidenceId = "visual-text:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f',
                    bundle.EpisodeId, window.WindowEvidenceId, hash, phrase.ProviderId, bounds.X, bounds.Y, bounds.Width, bounds.Height, phrase.Text)))).ToLowerInvariant()[..24];
                var ended = DateTimeOffset.TryParse(interval.EndedAtUtc, out var parsed) ? parsed : published;
                var observation = new Observation
                {
                    ObservationId = Guid.NewGuid().ToString("N"),
                    EpisodeId = bundle.EpisodeId,
                    FrameCandidateId = bundle.EpisodeId,
                    ObservationKind = ObservationKind.VisualTextObservation,
                    BoundsPx = bounds,
                    Role = "visual text",
                    ObservedName = phrase.Text,
                    Health = new Health { State = HealthState.Complete },
                    Confidence = new Confidence { State = ConfidenceState.Computed, Value = phrase.Confidence, Rationale = "Local Windows OCR direct visual-text observation" },
                    Freshness = new Freshness
                    {
                        SourceInterval = interval.Clone(),
                        SkewFromAnchorMs = 0,
                        AgeAtPublicationMs = Math.Max(0, (published - ended).TotalMilliseconds),
                    },
                    Lineage = new Lineage { SourcePacketIds = { screenshotPacket.PacketId } },
                    ParentEvidenceId = parent?.AccessibilityEvidenceId ?? window.WindowEvidenceId,
                    SourceEvidenceId = evidenceId,
                    WindowEvidenceId = window.WindowEvidenceId,
                    ProcessInstanceEvidence = window.ProcessInstanceEvidence,
                    SourceInterval = interval.Clone(),
                };
                observation.Properties.Add(new StringProperty { Key = "providerId", Value = phrase.ProviderId });
                observation.Properties.Add(new StringProperty { Key = "providerVersion", Value = phrase.ProviderVersion });
                observation.Properties.Add(new StringProperty { Key = "visualTextAnchor", Value = $"{relative.X},{relative.Y},{relative.Width},{relative.Height}" });
                observation.Properties.Add(new StringProperty { Key = "sourceMethod", Value = "local-ocr" });
                observation.Properties.Add(new StringProperty { Key = "nonActionable", Value = bool.TrueString });
                observation.Properties.Add(new StringProperty { Key = "originalText", Value = phrase.Text });
                observation.Properties.Add(new StringProperty
                {
                    Key = "windowScopeRef",
                    Value = $"{window.ProcessInstanceEvidence}|hwnd:{window.WindowHandleEvidence}|root:{window.RootOwnerHandleEvidence}|class:{window.ClassName}",
                });
                observation.Properties.Add(new StringProperty { Key = "sourceImageKind", Value = planeKind });
                observation.Properties.Add(new StringProperty { Key = "sourceImageSha256", Value = hash });
                observation.Properties.Add(new StringProperty { Key = "sourceImageHwnd", Value = window.WindowHandleEvidence });
                observation.Properties.Add(new StringProperty { Key = "sourceImageScreenBounds", Value = $"{imageBounds.X},{imageBounds.Y},{imageBounds.Width},{imageBounds.Height}" });
                result.Add(observation);
            }
        }
    }

    internal static IReadOnlyList<VisualTextPhrase> GroupWords(
        IEnumerable<GridOcrToken> source,
        Func<GridOcrToken, string>? scopeSelector = null)
    {
        var spatiallyDistinct = new List<GridOcrToken>();
        foreach (var token in source.Where(token => !string.IsNullOrWhiteSpace(token.OriginalText)
                         && token.Bounds is { Width: > 0, Height: > 0 })
                     .OrderByDescending(token => token.Confidence)
                     .ThenByDescending(token => token.ProviderId.Contains("2x", StringComparison.OrdinalIgnoreCase))
                     .ThenByDescending(token => token.OriginalText.Length))
        {
            if (spatiallyDistinct.Any(existing => OverlapOverMinimum(existing.Bounds, token.Bounds) >= .72)) continue;
            spatiallyDistinct.Add(token);
        }

        scopeSelector ??= _ => string.Empty;
        var lines = new List<(string Scope, List<GridOcrToken> Tokens)>();
        foreach (var token in spatiallyDistinct.OrderBy(token => CenterY(token.Bounds)).ThenBy(token => token.Bounds.X))
        {
            var scope = scopeSelector(token);
            var lineIndex = lines.FindIndex(candidate => candidate.Scope == scope
                && Math.Abs(candidate.Tokens.Average(item => CenterY(item.Bounds)) - CenterY(token.Bounds))
                <= Math.Max(3, Math.Min(candidate.Tokens.Average(item => item.Bounds.Height), token.Bounds.Height) * .55));
            if (lineIndex < 0) lines.Add((scope, [token]));
            else lines[lineIndex].Tokens.Add(token);
        }

        var result = new List<VisualTextPhrase>();
        foreach (var (_, tokens) in lines)
        {
            var ordered = tokens.OrderBy(token => token.Bounds.X).ToList();
            var group = new List<GridOcrToken>();
            foreach (var token in ordered)
            {
                if (group.Count > 0)
                {
                    var previous = group[^1];
                    var gap = token.Bounds.X - (previous.Bounds.X + previous.Bounds.Width);
                    var threshold = Math.Max(5, (int)Math.Round(Math.Min(previous.Bounds.Height, token.Bounds.Height) * 1.5));
                    if (gap > threshold)
                    {
                        result.Add(Phrase(group));
                        group.Clear();
                    }
                }
                group.Add(token);
            }
            if (group.Count > 0) result.Add(Phrase(group));
        }
        return result.OrderBy(phrase => CenterY(phrase.Bounds)).ThenBy(phrase => phrase.Bounds.X).ToList();
    }

    private static bool Intersects(RectPx? a, RectPx b) => a is not null && a.X < b.X + (long)b.Width &&
        b.X < a.X + (long)a.Width && a.Y < b.Y + (long)b.Height && b.Y < a.Y + (long)a.Height;

    private static VisualTextPhrase Phrase(IReadOnlyList<GridOcrToken> words)
    {
        var left = words.Min(word => word.Bounds.X);
        var top = words.Min(word => word.Bounds.Y);
        var right = words.Max(word => word.Bounds.X + word.Bounds.Width);
        var bottom = words.Max(word => word.Bounds.Y + word.Bounds.Height);
        return new VisualTextPhrase(
            string.Join(' ', words.Select(word => word.OriginalText.Trim())),
            new RectPx { X = left, Y = top, Width = right - left, Height = bottom - top },
            words.Min(word => word.Confidence),
            string.Join('+', words.Select(word => word.ProviderId).Distinct(StringComparer.Ordinal)),
            string.Join('+', words.Select(word => word.ProviderVersion).Distinct(StringComparer.Ordinal)));
    }

    private static bool DuplicatesAccessibility(
        string text,
        RectPx bounds,
        IReadOnlyList<AccessibilityNodeRecord> nodes,
        string windowEvidenceId)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0) return true;
        return nodes.Any(node => node.WindowEvidenceId == windowEvidenceId
            && OverlapOverMinimum(bounds, node.BoundsPx) >= .45
            && (Normalize(node.Name) == normalized || Normalize(node.ValueEvidence) == normalized));
    }

    private static RectPx ToScreenBounds(RectPx value, RectPx display, int imageWidth, int imageHeight)
    {
        var scaleX = (double)display.Width / imageWidth;
        var scaleY = (double)display.Height / imageHeight;
        return new RectPx
        {
            X = display.X + (int)Math.Floor(value.X * scaleX),
            Y = display.Y + (int)Math.Floor(value.Y * scaleY),
            Width = Math.Max(1, (int)Math.Ceiling(value.Width * scaleX)),
            Height = Math.Max(1, (int)Math.Ceiling(value.Height * scaleY)),
        };
    }

    private static bool Contains(RectPx? outer, RectPx? inner) => outer is not null && inner is not null
        && outer.X <= inner.X && outer.Y <= inner.Y
        && outer.X + outer.Width >= inner.X + inner.Width
        && outer.Y + outer.Height >= inner.Y + inner.Height;

    private static long Area(RectPx? value) => value is null ? long.MaxValue : (long)value.Width * value.Height;
    private static double CenterY(RectPx value) => value.Y + value.Height / 2d;
    private static int Quantize(int value) => (int)Math.Round(value / 4d) * 4;
    private static string Normalize(string? value) => string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static double OverlapOverMinimum(RectPx? left, RectPx? right)
    {
        if (left is null || right is null) return 0;
        var width = Math.Max(0, Math.Min(left.X + left.Width, right.X + right.Width) - Math.Max(left.X, right.X));
        var height = Math.Max(0, Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y));
        var minimum = Math.Min((long)left.Width * left.Height, (long)right.Width * right.Height);
        return minimum <= 0 ? 0 : width * (double)height / minimum;
    }
}

public sealed record VisualTextPhrase(string Text, RectPx Bounds, double Confidence, string ProviderId, string ProviderVersion);
