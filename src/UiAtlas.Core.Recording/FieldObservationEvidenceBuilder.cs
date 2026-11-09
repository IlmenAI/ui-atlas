using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording;

public sealed record RetainedFieldObservation(string JsonLine, string ContentHash, int ByteCount, bool Truncated);

/// <summary>Bounded field samples with stable deduplication and the identity evidence needed for review.</summary>
public static class FieldObservationEvidenceBuilder
{
    private const int MaxControls = 512;
    private const int MaxAncestors = 2_048;
    private static readonly JsonSerializerOptions CompactJson = new(JsonDefaults.Options) { WriteIndented = false };

    public static RetainedFieldObservation Build(
        FrameObservation frame, FieldLearningSnapshot snapshot, int processId, DateTimeOffset processStartedUtc)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(snapshot);
        var truncated = false;
        var byReference = frame.Automation.GroupBy(control => (Window(control), control.RuntimeId))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var samples = new List<ControlSample>();
        var ancestors = new Dictionary<(long, string, string), AncestorSample>();
        foreach (var field in snapshot.Fields.Where(field => field.IsCurrent).OrderBy(field => field.Id, StringComparer.Ordinal))
        {
            if (samples.Count >= MaxControls) { truncated = true; break; }
            if (!byReference.TryGetValue((field.WindowHwnd, field.ControlRuntimeId), out var matches)) continue;
            var control = matches.FirstOrDefault(value => (value.ProviderId ?? value.FrameworkId) == field.ProviderId);
            if (control is null) continue;
            var safeContent = control.IsPassword == false;
            samples.Add(new(field.Id, field.IdentityBasis, Safe(control),
                safeContent && control.ValueAvailable && control.Value is not null ? Hash(control.Value) : null,
                safeContent && control.SelectionAvailable && control.SelectionValue is not null ? Hash(control.SelectionValue) : null));
            var cursor = control;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var depth = 0; !string.IsNullOrEmpty(cursor.ParentRuntimeId) && depth < 64; depth++)
            {
                if (!visited.Add(cursor.RuntimeId) ||
                    !byReference.TryGetValue((field.WindowHwnd, cursor.ParentRuntimeId), out var parents)) break;
                var parent = parents.FirstOrDefault(value => (value.ProviderId ?? value.FrameworkId) == field.ProviderId);
                if (parent is null) break;
                var key = (field.WindowHwnd, parent.ProviderId ?? "", parent.RuntimeId);
                if (!ancestors.ContainsKey(key))
                {
                    if (ancestors.Count >= MaxAncestors) { truncated = true; break; }
                    ancestors.Add(key, new(field.WindowHwnd, Clip(parent.RuntimeId, 512), Clip(parent.ParentRuntimeId, 512),
                        Clip(parent.AutomationId, 512), Clip(parent.ControlType, 256), Clip(parent.ClassName, 512),
                        Clip(parent.ProviderId ?? parent.FrameworkId, 128), NullableClip(parent.ProviderRuntimeId, 512)));
                }
                cursor = parent;
                if (depth == 63 && !string.IsNullOrEmpty(cursor.ParentRuntimeId)) truncated = true;
            }
        }
        var ancestorValues = ancestors.Values.OrderBy(value => value.WindowHwnd)
            .ThenBy(value => value.ProviderId, StringComparer.Ordinal).ThenBy(value => value.ProviderRuntimeId, StringComparer.Ordinal)
            .ThenBy(value => value.AutomationId, StringComparer.Ordinal).ThenBy(value => value.ControlType, StringComparer.Ordinal)
            .ThenBy(value => value.ClassName, StringComparer.Ordinal).ToArray();
        var observedWindows = (frame.ScopedWindows ?? []).Select(window => window.Hwnd)
            .Concat(frame.ObservedWindowHwnds ?? []).Append(frame.Window.Hwnd).Distinct().Order().ToArray();
        // Evidence references are per-acquisition; they must not make unchanged samples appear different.
        var semantic = JsonSerializer.Serialize(new
        {
            frame.AutomationStatus, frame.AutomationTimedOut, ObservedWindows = observedWindows,
            Controls = samples.Select(sample => sample with
            { Observation = sample.Observation with { RuntimeId = "", ParentRuntimeId = "" } }),
            Ancestors = ancestorValues.Select(ancestor => ancestor with { RuntimeId = "", ParentRuntimeId = "" }),
            Truncated = truncated
        }, CompactJson);
        var line = JsonSerializer.Serialize(new
        {
            frame.Sequence, frame.TimestampUtc, frame.Trigger, frame.AutomationStatus, frame.AutomationTimedOut,
            frame.ObservationScope, ObservedWindows = observedWindows, ProcessId = processId, ProcessStartedUtc = processStartedUtc,
            Controls = samples, Ancestors = ancestorValues, Truncated = truncated
        }, CompactJson);
        // 512 controls and 2,048 identity-only ancestors, with every text/pattern list bounded below,
        // remain below the existing 512MB per-entry hard limit even with worst-case JSON escaping.
        return new(line, Hash(semantic), Encoding.UTF8.GetByteCount(line), truncated);

        long Window(AutomationObservation control) => control.QueryWindowHwnd is > 0 ? control.QueryWindowHwnd.Value :
            control.WindowHwnd > 0 ? control.WindowHwnd : frame.Window.Hwnd;
        string Clip(string value, int limit)
        {
            if (value.Length <= limit) return value;
            truncated = true;
            return value[..limit];
        }
        string? NullableClip(string? value, int limit) => value is null ? null : Clip(value, limit);
        AutomationObservation Safe(AutomationObservation control)
        {
            var safe = control.IsPassword == false;
            if (control.SupportedPatterns?.Count > 32) truncated = true;
            return control with
            {
                RuntimeId = Clip(control.RuntimeId, 512), ParentRuntimeId = Clip(control.ParentRuntimeId, 512),
                AutomationId = Clip(control.AutomationId, 512), Name = safe ? Clip(control.Name, 4096) : "",
                ControlType = Clip(control.ControlType, 256), ClassName = Clip(control.ClassName, 512),
                FrameworkId = Clip(control.FrameworkId, 128), WindowHwnd = Window(control),
                SupportedPatterns = control.SupportedPatterns?.Take(32).Select(pattern => Clip(pattern, 128)).ToArray(),
                ToggleState = safe ? NullableClip(control.ToggleState, 32) : null,
                ExpandCollapseState = NullableClip(control.ExpandCollapseState, 32),
                VisualRole = null, OcrText = null, VisualGroupId = null,
                ScopeStatus = NullableClip(control.ScopeStatus, 128),
                Value = safe && control.ValueAvailable ? NullableClip(control.Value, 4096) : null,
                ValueAvailable = safe && control.ValueAvailable && control.Value is not null,
                ProviderRuntimeId = NullableClip(control.ProviderRuntimeId, 512), ProviderId = NullableClip(control.ProviderId, 128),
                SelectionValue = safe && control.SelectionAvailable ? NullableClip(control.SelectionValue, 4096) : null,
                SelectionAvailable = safe && control.SelectionAvailable && control.SelectionValue is not null
            };
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private sealed record ControlSample(string FieldId, string IdentityBasis, AutomationObservation Observation,
        string? ValueSha256, string? SelectionSha256);
    private sealed record AncestorSample(long WindowHwnd, string RuntimeId, string ParentRuntimeId, string AutomationId,
        string ControlType, string ClassName, string ProviderId, string? ProviderRuntimeId);
}
