using System.IO;

namespace UIMaps.Application.Core;

internal static class RunningApplicationMatcher
{
    internal static RecordableApplicationTarget? Find(
        MapApplicationPresentation application,
        IEnumerable<RecordableApplicationTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(application);
        return Find(application.ExecutableName, targets);
    }

    internal static RecordableApplicationTarget? Find(
        string executableName,
        IEnumerable<RecordableApplicationTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var executable = NormalizeExecutable(executableName);
        if (string.IsNullOrWhiteSpace(executable)) return null;
        return targets.FirstOrDefault(target => string.Equals(
            NormalizeExecutable(target.ExecutableName), executable, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeExecutable(string value)
    {
        var fileName = Path.GetFileName((value ?? string.Empty).Trim());
        return string.Equals(Path.GetExtension(fileName), ".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName;
    }
}
