using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Tests;

public sealed class GraphSemanticsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(120)]
    public void StreamingHashMatchesThePreviousCanonicalFormat(int count)
    {
        IReadOnlyList<GraphNode> nodes = Enumerable.Range(0, count).Select(i => Node(i)).ToArray();
        IReadOnlyList<GraphEdge> edges = Enumerable.Range(0, count).Select(i => new GraphEdge(
            "edge-" + i, "contains", "app", "node-" + i, [new("reason", "\"quoted\"\n<>&")],
            [new("capture", i + 1, "raw/observations/frame.json", null)])).ToArray();
        var previous = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { nodes, edges }, JsonDefaults.Options)))).ToLowerInvariant();

        Assert.Equal(previous, GraphSemantics.ComputeHash(nodes, edges));
    }

    [Fact]
    public void HashStillDetectsContentAndCollectionOrderChanges()
    {
        GraphNode[] nodes = [Node(0), Node(1)];
        GraphEdge[] edges = [new("e1", "contains", "app", "node-0", [], []),
            new("e2", "contains", "app", "node-1", [], [])];
        var original = GraphSemantics.ComputeHash(nodes, edges);
        Assert.NotEqual(original, GraphSemantics.ComputeHash(nodes.Reverse().ToArray(), edges));
        Assert.NotEqual(original, GraphSemantics.ComputeHash(nodes, edges.Reverse().ToArray()));
        Assert.NotEqual(original, GraphSemantics.ComputeHash([nodes[0] with { Label = "changed" }, nodes[1]], edges));
        Assert.NotEqual(original, GraphSemantics.ComputeHash(nodes, [edges[0] with { ToId = "other" }, edges[1]]));
        Assert.Equal(original, GraphSemantics.ComputeHash(nodes, edges));
    }

    [Fact]
    public void HashingDoesNotAllocateGraphSizedBuffers()
    {
        // >40 MB of canonical JSON from a small resident fixture. The old string
        // plus UTF-8 copies allocate hundreds of MB; the stream must stay bounded.
        var node = Node(0) with { Label = new string('x', 4096) };
        var nodes = Enumerable.Repeat(node, 10_000).ToArray();
        GraphSemantics.ComputeHash([node], []); // Warm serializer metadata and pooled buffers.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var hash = GraphSemantics.ComputeHash(nodes, []);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(64, hash.Length);
        Assert.True(allocated < 8 * 1024 * 1024, $"Hashing allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void RecorderRecoversFromOverflowWithoutCatchingFatalFailures()
    {
        var method = typeof(UiAtlas.Core.Cli.Program).GetMethod("IsRecorderWorkflowRecoverable",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        bool Recoverable(Exception exception) => (bool)method.Invoke(null, [exception])!;
        Assert.True(Recoverable(new OverflowException("graph too large")));
        Assert.True(Recoverable(new InvalidDataException("limit exceeded")));
        Assert.False(Recoverable(new OutOfMemoryException()));
        Assert.False(Recoverable(new StackOverflowException()));
    }

    private static GraphNode Node(int i) => new("node-" + i, GraphNodeKind.Control, "app", "key-" + i,
        "Résumé Ελληνικά 中文 \U0001F4CA \"quoted\"\r\n<>&", [new("data", "value", true)],
        [new("capture", i + 1, "raw/observations/frame.json", new(-10, 20, 30, 40), "raw/frames/frame.png")],
        [new("capture", i + 1, new("runtime", "parent", "id", "Control", "Button", "Class",
            new(-10, 20, 30, 40), true, false, "Win32", 123, ["Invoke"], HasKeyboardFocus: true,
            QueryWindowHwnd: 123, ScopeStatus: "verified"), 123, "same-window", true)]);
}
