using System.Text.Json;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Mapping.Atlas;

public static class AtlasParameterizationAdapter
{
    public static IReadOnlyList<ParameterGridSource> LoadGrids(AtlasMapWorkspace workspace, UiKnowledgeGraph graph)
    {
        var result = new List<ParameterGridSource>();
        foreach (var entry in workspace.Index.GridReviews ?? [])
        {
            var path = workspace.Resolve(entry.ReviewFile);
            if (new FileInfo(path).Length > 16 * 1024 * 1024)
                throw new InvalidDataException("Grid review exceeds its size limit.");
            var review = JsonSerializer.Deserialize<SavedDataGridReview>(File.ReadAllBytes(path))
                ?? throw new InvalidDataException("Empty grid review.");
            if (review.GridId != entry.GridId) throw new InvalidDataException("Grid identity mismatch.");
            var hosts = graph.Nodes.Where(n => n.Properties.Any(p =>
                p.Name == "sourceGridRef" && p.Value == "atlas:grid:" + entry.GridId)).ToArray();
            var host = hosts.Length == 1 ? hosts[0] : null;
            result.Add(new(entry.GridId, host?.Id ?? "unbound-grid:" + entry.GridId,
                host?.Properties.FirstOrDefault(p => p.Name == "semanticSurfaceId")?.Value ?? "",
                entry.Name, review, workspace.Resolve(entry.ImageFile), host is not null));
        }
        return result;
    }

    public static byte[] ReadGridImage(ParameterProposal proposal)
    {
        if (proposal.ImagePath is null || proposal.ImageSha256 is null)
            throw new InvalidDataException("No retained grid image.");
        return AtlasGridReviewAdapter.ReadImage(proposal.ImagePath, proposal.ImageSha256);
    }
}
