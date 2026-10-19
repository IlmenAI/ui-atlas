using System.Security.Cryptography;
using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Build;

public static class GraphSemantics
{
    public static string ComputeHash(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        // Keep the exact canonical JSON bytes, but never require a whole-graph
        // UTF-16 string (or UTF-8 array). Large recordings exceed the serializer's
        // string transcoding limit even when the graph fits its node/edge limits.
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        JsonSerializer.Serialize(stream, new { nodes, edges }, JsonDefaults.Options);
        stream.FlushFinalBlock();
        return Convert.ToHexString(hash.Hash!).ToLowerInvariant();
    }
}
