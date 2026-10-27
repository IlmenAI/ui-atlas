using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class MapBuildSettingsTests
{
    [Fact]
    public void SqliteReadersShareTheMultiRecordingMapSizeLimit()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var path = Path.Combine(temp.Path, "multi-recording-size.db");
        SqliteGraphStore.Save(graph, path);
        // Trailing unused pages exercise the file-size guard without creating
        // hundreds of MiB of irrelevant synthetic node properties.
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
            file.SetLength(512L * 1024 * 1024 + 4096);
        Assert.Equal(graph.Nodes.Count, SqliteGraphStore.Load(path).Nodes.Count);
        Assert.Equal(graph.Nodes.Count, SqliteGraphStore.ReadSummary(path).NodeCount);
        Assert.Equal(graph.Metadata.SemanticHash, SqliteGraphStore.ReadCatalogMetadata(path).SemanticHash);
        Assert.NotEmpty(SqliteGraphStore.ReadLayerNodes(path, "semantic-world"));

        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
            file.SetLength(GraphContractLimits.MaxSqliteBytes + 1);
        Assert.Throws<InvalidDataException>(() => SqliteGraphStore.Load(path));
        Assert.Throws<InvalidDataException>(() => SqliteGraphStore.ReadSummary(path));
        Assert.Throws<InvalidDataException>(() => SqliteGraphStore.ReadCatalogMetadata(path));
        Assert.Throws<InvalidDataException>(() => SqliteGraphStore.ReadLayerNodes(path, "semantic-world"));
    }

    [Fact]
    public void BuildLimitsAreIndependentAndPersistWithoutAcceptingInvalidValues()
    {
        using var temp = new TempDirectory();
        var store = new MapBuildSettingsStore(Path.Combine(temp.Path, "build.json"));
        Assert.Equal(new(), store.Load());
        store.Save(new(250000, 600000)); Assert.Equal(new(250000, 600000), store.Load());
        Assert.Throws<InvalidDataException>(() => store.Save(new(0, 10)));
        Assert.Throws<InvalidDataException>(() => store.Save(new(10, GraphContractLimits.MaxEdges + 1)));
        Assert.Equal(new(250000, 600000), store.Load());
        var bundle = SyntheticBundleFactory.Create(temp.Path);
        var graph = new RecordingGraphBuilder(new()).Build(bundle);
        new RecordingGraphBuilder(new(graph.Nodes.Count, graph.Edges.Count)).Build(bundle);
        Assert.Contains("Settings > UI Map building", Assert.Throws<InvalidDataException>(() =>
            new RecordingGraphBuilder(new(graph.Nodes.Count - 1, graph.Edges.Count)).Build(bundle)).Message);
        Assert.Throws<InvalidDataException>(() => new RecordingGraphBuilder(new(graph.Nodes.Count, graph.Edges.Count - 1)).Build(bundle));
    }

    [Fact]
    public void MapsAboveTheOldNodeAndEdgeLimitRoundTripThroughSqlite()
    {
        using var temp = new TempDirectory();
        var evidence = new[] { new EvidenceRef("synthetic", 1, "raw/observations/frame-000001.json", null) };
        var properties = new[] { new GraphProperty("layer", "legacy-observed") };
        var nodes = new[] { new GraphNode("app", GraphNodeKind.Application, "", "app", "Synthetic", properties, []) }
            .Concat(Enumerable.Range(1, 100001).Select(i => new GraphNode("n" + i, GraphNodeKind.Control, "app", "n" + i, "Control", properties, evidence)))
            .OrderBy(n => n.Id, StringComparer.Ordinal).ToArray();
        var edges = nodes.Skip(1).Select((n, i) => new GraphEdge("e" + i, "contains", "app", n.Id, [], evidence))
            .OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
        var graph = new UiKnowledgeGraph(new(FormatVersions.Graph, FormatVersions.Tool, "large-synthetic", DateTimeOffset.UtcNow,
            "synthetic", GraphSemantics.ComputeHash(nodes, edges), FormatVersions.FullEvidenceProfile), nodes, edges);
        new MapBuildSettings(200000, 200000).CheckCounts(nodes.Length, edges.Length);
        Assert.Throws<InvalidDataException>(() => new MapBuildSettings().CheckCounts(nodes.Length, edges.Length));
        Assert.True(GraphValidator.Validate(graph).IsValid);
        var path = Path.Combine(temp.Path, "large.db"); SqliteGraphStore.Save(graph, path);
        var loaded = SqliteGraphStore.Load(path);
        Assert.Equal(nodes.Length, loaded.Nodes.Count); Assert.Equal(edges.Length, loaded.Edges.Count);
        Assert.Equal(graph.Metadata.SemanticHash, loaded.Metadata.SemanticHash);
        Assert.Equal(nodes.Length, SqliteGraphStore.ReadLayerNodes(path, "legacy-observed").Count);
    }
}
