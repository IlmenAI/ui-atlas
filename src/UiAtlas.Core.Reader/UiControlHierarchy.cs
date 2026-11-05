namespace UiAtlas.Core.Reader;

/// <summary>A surface-local forest retaining observed control parents and search ancestors.</summary>
public static class UiControlHierarchy
{
    public static IReadOnlyDictionary<string, string> Parents(
        IReadOnlyList<UiMapControlView> controls, string query = "", string? revealedId = null)
    {
        var byId = controls.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var parents = controls.ToDictionary(c => c.Id,
            c => byId.ContainsKey(c.ParentControlId) && c.ParentControlId != c.Id ? c.ParentControlId : "",
            StringComparer.Ordinal);
        // Malformed legacy graphs must remain inspectable, without cyclic WPF ownership.
        var complete = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in parents.Keys.Order(StringComparer.Ordinal))
        {
            var path = new HashSet<string>(StringComparer.Ordinal);
            var current = id;
            while (current.Length > 0 && !complete.Contains(current))
            {
                path.Add(current);
                var parent = parents[current];
                if (path.Contains(parent)) { parents[current] = ""; break; }
                current = parent;
            }
            complete.UnionWith(path);
        }
        if (string.IsNullOrWhiteSpace(query)) return parents;
        var included = new HashSet<string>(StringComparer.Ordinal);
        foreach (var control in controls.Where(c => c.Id == revealedId ||
                     UiInspectionPresentation.Matches(c, query)))
        {
            var id = control.Id;
            while (id.Length > 0 && included.Add(id)) id = parents[id];
        }
        return parents.Where(pair => included.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }
}
