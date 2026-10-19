using UIMaps.Protocol.V3;

namespace UIMaps.Map.Core;

public static class ControlDetectionPolicyNormalizer
{
    public const string DefaultDataGridShapePolicy = "tabular-distinct-top-header-v1";

    public static AppliedControlDetectionPolicy Normalize(ControlDetectionPolicy? requested)
    {
        var supplied = requested is not null;
        var original = requested?.Clone() ?? new ControlDetectionPolicy
        {
            AutomaticDataGridDetectionEnabled = true,
            AutomaticDataGridShapePolicy = DefaultDataGridShapePolicy,
        };
        var effective = original.Clone();
        if (!supplied) effective.AutomaticDataGridDetectionEnabled = true;
        if (!effective.VisualControlDetectionEnabled)
            effective.OcrOnVisualControlsEnabled = false;
        if (!string.Equals(effective.AutomaticDataGridShapePolicy, DefaultDataGridShapePolicy, StringComparison.Ordinal))
        {
            effective.AutomaticDataGridShapePolicy = DefaultDataGridShapePolicy;
            effective.AutomaticDataGridDetectionEnabled = false;
        }
        if (string.IsNullOrWhiteSpace(effective.AutomaticDataGridShapePolicy))
            effective.AutomaticDataGridShapePolicy = DefaultDataGridShapePolicy;

        var result = new AppliedControlDetectionPolicy
        {
            Requested = original,
            Effective = effective,
        };
        if (original.OcrOnVisualControlsEnabled && !original.VisualControlDetectionEnabled)
            result.Limitations.Add("OCR on visual controls was forced Off because visual control detection is Off.");
        if (requested is not null
            && !string.IsNullOrWhiteSpace(requested.AutomaticDataGridShapePolicy)
            && !string.Equals(requested.AutomaticDataGridShapePolicy, DefaultDataGridShapePolicy, StringComparison.Ordinal))
            result.Limitations.Add("The unknown automatic DataGrid shape policy failed closed; automatic detection is Off for this attachment.");
        return result;
    }
}
