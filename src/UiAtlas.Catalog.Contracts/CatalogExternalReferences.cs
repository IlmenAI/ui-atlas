using System.Security.Cryptography;
using System.Text.Json;

namespace UiAtlas.Catalog;

// A local retention root, never execution authority or a report of business truth.
public sealed record CatalogExternalReference(string ConsumerId, string MapId, string RevisionRef, string CanonicalPayloadHash);

/// <summary>All external reference writers, permanent map deletion and generation pruning
/// share this lease. Registration callers must validate exact retained bytes while holding it.
/// There is intentionally no unpin API: uncertain, closed and offline consumers remain roots.</summary>
public sealed class CatalogExternalReferenceLease : IDisposable
{
    private const int MaximumEntries = 10000, MaximumBytes = 8 * 1024 * 1024;
    private readonly FileStream lease;
    private readonly string path;
    private bool disposed;
    private sealed record Envelope(int Version, CatalogExternalReference[] References, string Sha256);
    private CatalogExternalReferenceLease(string root)
    {
        root = Path.GetFullPath(root); Check(root); Directory.CreateDirectory(root); Check(root);
        var owner = Path.Combine(root, "semantic-references.lock"); Check(owner);
        lease = new(owner, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        path = Path.Combine(root, "semantic-references.json");
    }
    public static CatalogExternalReferenceLease Acquire(string root) => new(root);
    public CatalogExternalReference[] Read()
    {
        ObjectDisposedException.ThrowIf(disposed, this); return ReadAt(path);
    }
    public static CatalogExternalReference[] ReadSnapshot(string root) => ReadAt(Path.Combine(Path.GetFullPath(root), "semantic-references.json"));
    private static CatalogExternalReference[] ReadAt(string path)
    {
        Check(path); if (!File.Exists(path)) return [];
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumBytes) throw new InvalidDataException("Retained catalog references exceed their byte bound; cleanup is deferred.");
        var value = JsonSerializer.Deserialize<Envelope>(input) ?? throw new InvalidDataException("Retained catalog references are empty; cleanup is deferred.");
        if (value.Version != 1 || value.References is null || value.References.Length > MaximumEntries || value.Sha256 != Digest(value.References))
            throw new InvalidDataException("Retained catalog reference format or digest differs; cleanup is deferred.");
        foreach (var reference in value.References) Validate(reference);
        if (value.References.Select(r => r.ConsumerId).Distinct(StringComparer.Ordinal).Count() != value.References.Length)
            throw new InvalidDataException("Retained consumer identities are ambiguous; cleanup is deferred.");
        return value.References;
    }
    public void RetainValidated(CatalogExternalReference reference)
    {
        ObjectDisposedException.ThrowIf(disposed, this); Validate(reference); var prior = Read();
        if (prior.SingleOrDefault(p => p.ConsumerId == reference.ConsumerId) is { } existing)
        { if (existing != reference) throw new InvalidDataException("This retained consumer identity already pins different exact bytes."); return; }
        if (prior.Length >= MaximumEntries) throw new InvalidDataException("Retained catalog reference capacity is exhausted; no root was discarded.");
        var values = prior.Append(reference).OrderBy(r => r.ConsumerId, StringComparer.Ordinal).ToArray();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, values, Digest(values)));
        if (payload.Length > MaximumBytes) throw new InvalidDataException("Retained catalog references exceed their byte bound.");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; Check(temp);
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(payload); stream.Flush(true); }
        Check(path); File.Move(temp, path, true);
    }
    public void DemandUnreferenced(string mapId)
    {
        if (Read().Any(r => string.Equals(r.MapId, mapId, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw new InvalidOperationException("This map is retained by an external semantic declaration or collection reference. Its exact history must remain available; archiving does not release that evidence.");
    }
    public static string ConsumerIdentity(string scope, string report, string requestDigest)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { scope, report, requestDigest }))).ToLowerInvariant();
    private static string Digest(CatalogExternalReference[] values) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values)));
    private static void Validate(CatalogExternalReference value)
    {
        const string prefix = "uimaps:map-revision:sha256:";
        if (value is null || value.ConsumerId is not { Length: 64 } || value.ConsumerId.Any(c => !char.IsAsciiHexDigit(c))
            || string.IsNullOrWhiteSpace(value.MapId) || value.MapId.Length > 200 || value.MapId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.MapId is "." or ".."
            || value.CanonicalPayloadHash is not { Length: 64 } || value.CanonicalPayloadHash.Any(c => !char.IsAsciiHexDigit(c))
            || value.RevisionRef != prefix + value.CanonicalPayloadHash)
            throw new InvalidDataException("Retained catalog references require exact bounded consumer, map and revision identities.");
    }
    private static void Check(string candidate)
    {
        for (var current = Path.GetFullPath(candidate); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Retained catalog references do not follow redirected paths.");
    }
    public void Dispose() { if (disposed) return; disposed = true; lease.Dispose(); }
}
