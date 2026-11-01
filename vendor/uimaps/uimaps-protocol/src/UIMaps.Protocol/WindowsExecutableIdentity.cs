namespace UIMaps.Protocol;

/// <summary>Compares Windows process names without treating dots within a name as extensions.</summary>
public static class WindowsExecutableIdentity
{
    public static string ProcessName(string? value)
    {
        var name = (value ?? string.Empty).Trim().Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public static string ExecutableName(string? value)
    {
        var name = ProcessName(value);
        return name.Length == 0 ? name : name + ".exe";
    }
}
