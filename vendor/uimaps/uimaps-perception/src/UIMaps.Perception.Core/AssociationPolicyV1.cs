using UIMaps.Protocol.V1;

namespace UIMaps.Perception.Core;

public sealed class AssociationPolicyV1
{
    public (int Score, IReadOnlyList<string> Evidence) Score(WorldObject current, WorldObject previous, string? currentParentTrack)
    {
        if (current.ObjectKind != previous.ObjectKind) return (int.MinValue, []);
        var evidence = new List<string>();
        var score = 0;
        var currentProcess = Property(current, "processName");
        var previousProcess = Property(previous, "processName");
        if (!string.IsNullOrEmpty(currentProcess) || !string.IsNullOrEmpty(previousProcess))
        {
            if (!EqualsNormalized(currentProcess, previousProcess)) return (int.MinValue, []);
            score += 40;
            evidence.Add("same_process");
        }
        if (!string.IsNullOrEmpty(currentParentTrack) && string.Equals(currentParentTrack, previous.ParentWorldObjectId, StringComparison.Ordinal))
        {
            score += 30;
            evidence.Add("same_parent_track");
        }
        if (EqualsNormalized(current.Role, previous.Role)) { score += 20; evidence.Add("same_role"); }
        else if (current.ObjectKind == WorldObjectKind.AccessibilityElement) return (int.MinValue, []);
        var currentAutomation = Property(current, "automationId");
        var previousAutomation = Property(previous, "automationId");
        if (!string.IsNullOrEmpty(currentAutomation) && EqualsNormalized(currentAutomation, previousAutomation)) { score += 45; evidence.Add("same_automation_id"); }
        if (!string.IsNullOrEmpty(current.Name) && EqualsNormalized(current.Name, previous.Name)) { score += 20; evidence.Add("same_name"); }
        var geometry = GeometrySimilarity(current.BoundsPx, previous.BoundsPx);
        score += geometry;
        if (geometry >= 10) evidence.Add("compatible_geometry");
        if (geometry == 20 && current.ObjectKind is WorldObjectKind.Display or WorldObjectKind.VisualSurface)
        {
            score += 20;
            evidence.Add("same_global_surface");
        }
        return (score, evidence);
    }

    public bool IsConfident(int bestScore, int? nextScore) => bestScore >= 45 && (nextScore is null || bestScore - nextScore.Value >= 15);

    private static string Property(WorldObject value, string key) =>
        value.Properties.Concat(value.ProcessEvidence).FirstOrDefault(x => x.Key == key)?.Value ?? string.Empty;

    private static bool EqualsNormalized(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().Normalize().ToUpperInvariant();

    private static int GeometrySimilarity(RectPx left, RectPx right)
    {
        var unionLeft = Math.Min(left.X, right.X);
        var unionTop = Math.Min(left.Y, right.Y);
        var unionRight = Math.Max(left.X + left.Width, right.X + right.Width);
        var unionBottom = Math.Max(left.Y + left.Height, right.Y + right.Height);
        var intersectionLeft = Math.Max(left.X, right.X);
        var intersectionTop = Math.Max(left.Y, right.Y);
        var intersectionRight = Math.Min(left.X + left.Width, right.X + right.Width);
        var intersectionBottom = Math.Min(left.Y + left.Height, right.Y + right.Height);
        var intersection = Math.Max(0, intersectionRight - intersectionLeft) * (long)Math.Max(0, intersectionBottom - intersectionTop);
        var union = Math.Max(1, unionRight - unionLeft) * (long)Math.Max(1, unionBottom - unionTop);
        return (int)Math.Round(20d * intersection / union);
    }
}
