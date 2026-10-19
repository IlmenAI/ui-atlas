using System.Globalization;
using System.Runtime.InteropServices;

namespace UIMaps.Recorder.Windows;

public static class ProviderPropertyValue
{
    public static string ToProtocolString(object? value, object? notSupportedSentinel = null)
    {
        if (value is null || ReferenceEquals(value, notSupportedSentinel) || Marshal.IsComObject(value))
            return string.Empty;
        return value is string text
            ? text
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public static string ToControlTypeName(object? value, object? notSupportedSentinel = null)
    {
        if (value is null || ReferenceEquals(value, notSupportedSentinel) || Marshal.IsComObject(value))
            return string.Empty;
        var programmaticName = value.GetType().GetProperty("ProgrammaticName")?.GetValue(value) as string;
        var text = string.IsNullOrWhiteSpace(programmaticName)
            ? ToProtocolString(value, notSupportedSentinel)
            : programmaticName;
        const string prefix = "ControlType.";
        return text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;
    }
}
