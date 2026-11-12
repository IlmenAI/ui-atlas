using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UIMaps.Protocol;

// Shared by native clarification and experimental map construction/matching.
public sealed record IdentityNameChange(int Index, string Original, string Prefix, string Suffix, bool Variable = true)
{
    public bool Matches(string value) => !Variable ? value == Original
        : value.Length >= Prefix.Length + Suffix.Length && value.StartsWith(Prefix, StringComparison.Ordinal)
            && value.EndsWith(Suffix, StringComparison.Ordinal);
    public string Canonical => Variable ? Prefix + "{content}" + Suffix : Original;
}
public sealed record IdentityAnchor(string Name, string ClassName, string AutomationId, string Type);
public sealed record ControlIdentityKnowledge(string Id, string Executable, IdentityAnchor[] Path,
    IdentityNameChange[] Changes, string Explanation, string EvidenceId, bool Enabled = true)
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string Serialize(ControlIdentityKnowledge rule) => JsonSerializer.Serialize(rule, Json);
    public static ControlIdentityKnowledge Parse(string json)
    {
        var rule = JsonSerializer.Deserialize<ControlIdentityKnowledge>(json, Json) ?? throw new InvalidDataException("Missing identity knowledge.");
        rule.Validate(); return rule;
    }
    public void Validate()
    {
        if (Id.Length is < 1 or > 128 || Executable.Length is < 1 or > 1024 || Path.Length is < 2 or > 128
            || Changes.Length is < 1 or > 32 || Explanation.Length is < 1 or > 16000 || EvidenceId.Length > 128
            || Changes.Select(c => c.Index).Distinct().Count() != Changes.Length)
            throw new InvalidDataException("Invalid identity knowledge bounds.");
        foreach (var node in Path)
            if (new[] { node.Name, node.ClassName, node.AutomationId, node.Type }.Any(s => s is null || s.Length > 2048))
                throw new InvalidDataException("Invalid identity anchor.");
        foreach (var change in Changes)
        {
            // Empty is an observed value too. An explicitly identified editable
            // leaf may later contain text without becoming a different control.
            var emptyFieldValue = change.Index == Path.Length - 1 && change.Original.Length == 0
                && change.Prefix == "" && change.Suffix == "" && Path[change.Index].Type == "edit"
                && Path[change.Index].AutomationId.Length > 0 && Path[change.Index].ClassName.Length > 0;
            if (change.Index < 0 || change.Index >= Path.Length || change.Original != Path[change.Index].Name
                || change.Prefix is null || change.Suffix is null || !change.Matches(change.Original)
                || change.Variable && change.Original.Length <= change.Prefix.Length + change.Suffix.Length && !emptyFieldValue)
                throw new InvalidDataException("The content rule does not describe the selected evidence.");
            var node = Path[change.Index];
            if (change.Variable && change.Prefix.Length + change.Suffix.Length == 0
                && node.AutomationId.Length == 0 && (change.Index != 0 || node.ClassName.Length == 0))
                throw new InvalidDataException("Variable text requires a stable control locator or owning window class.");
        }
    }
    public bool Applies(string executable, IReadOnlyList<IdentityAnchor> path)
    {
        if (!Enabled || !WindowsExecutableIdentity.ProcessName(Executable).Equals(WindowsExecutableIdentity.ProcessName(executable), StringComparison.OrdinalIgnoreCase)
            || path.Count != Path.Length) return false;
        for (var i = 0; i < path.Count; i++)
        {
            var a = path[i]; var b = Path[i];
            if (!a.ClassName.Equals(b.ClassName, StringComparison.OrdinalIgnoreCase)
                || !a.Type.Equals(b.Type, StringComparison.OrdinalIgnoreCase) || a.AutomationId != b.AutomationId) return false;
            var change = Changes.SingleOrDefault(c => c.Index == i);
            if (change is not null ? !change.Matches(a.Name) : a.Name != b.Name) return false;
        }
        return true;
    }
    public IEnumerable<IdentityNameChange> ChangesFor(string executable, IReadOnlyList<IdentityAnchor> path)
    {
        if (!Enabled || path.Count == 0 || !WindowsExecutableIdentity.ProcessName(Executable).Equals(WindowsExecutableIdentity.ProcessName(executable), StringComparison.OrdinalIgnoreCase)) yield break;
        // A reviewed window-title rule belongs to the surface, including its
        // other controls. Leaf/ancestor rules retain the complete inspected path.
        foreach (var change in Changes.Where(c => c.Index == 0))
            if (path[0].ClassName.Equals(Path[0].ClassName, StringComparison.OrdinalIgnoreCase)
                && path[0].Type.Equals(Path[0].Type, StringComparison.OrdinalIgnoreCase) && change.Matches(path[0].Name)) yield return change;
        if (Applies(executable, path)) foreach (var change in Changes.Where(c => c.Index > 0)) yield return change;
    }
}

public sealed class ControlIdentityKnowledgeStore(string? path = null)
{
    public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UiAtlas", "Core", "identity-knowledge.json");
    private readonly string _path = System.IO.Path.GetFullPath(path ?? DefaultPath);
    public ControlIdentityKnowledge[] Load()
    {
        if (!File.Exists(_path)) return [];
        if (new FileInfo(_path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Identity knowledge exceeds its storage limit.");
        var rules = JsonSerializer.Deserialize<ControlIdentityKnowledge[]>(File.ReadAllBytes(_path), ControlIdentityKnowledge.Json)
            ?? throw new InvalidDataException("Identity knowledge is empty.");
        if (rules.Length > 4096) throw new InvalidDataException("Too many identity corrections.");
        foreach (var rule in rules) rule.Validate();
        return rules;
    }
    public void Save(ControlIdentityKnowledge rule) { rule.Validate(); Update(all => all.Where(r => r.Id != rule.Id).Append(rule).ToArray()); }
    public void Disable(string id) => Update(all => all.Select(r => r.Id == id ? r with { Enabled = false } : r).ToArray());
    private void Update(Func<ControlIdentityKnowledge[], ControlIdentityKnowledge[]> amend)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())));
        using var mutex = new Mutex(false, "Local\\UiMaps.IdentityKnowledge." + key);
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) throw new IOException("Identity knowledge is busy."); }
        catch (AbandonedMutexException) { }
        try
        {
            var updated = amend(Load());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(updated, ControlIdentityKnowledge.Json);
            if (updated.Length > 4096 || bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("Identity knowledge limit reached.");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, _path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { mutex.ReleaseMutex(); }
    }
}
