using UiAtlas.Catalog;
using UiAtlas.Shell.Catalog;
using Xunit;

namespace UiAtlas.Shell.Tests;

public sealed class RecordingLibraryTests
{
    private static readonly CatalogRecording[] Recordings =
    [
        new("word-old", DateTimeOffset.Parse("2026-10-21T12:00:00Z"), "complete", "WINWORD", 10, "draft-map"),
        new("excel-new", DateTimeOffset.Parse("2026-10-23T12:00:00Z"), "complete", "EXCEL", 20, null),
        new("word-archived", DateTimeOffset.Parse("2026-10-24T12:00:00Z"), "complete", "WINWORD", 30, null, true),
        new("broken", DateTimeOffset.Parse("2026-10-20T12:00:00Z"), "invalid", "EXCEL", 0, null),
    ];
    [Fact]
    public void SearchFiltersSortAndArchiveCompose()
    {
        Assert.Equal(new[] { "excel-new", "word-old", "broken" }, RecordingLibraryItem.Filter(Recordings, false, "", null, 0, 0).Select(item => item.Id));
        Assert.Equal("word-archived", Assert.Single(RecordingLibraryItem.Filter(Recordings, true, "word", "WINWORD", 2, 0)).Id);
        Assert.Equal("excel-new", Assert.Single(RecordingLibraryItem.Filter(Recordings, false, " NEW ", "EXCEL", 2, 0)).Id);
        Assert.Equal("broken", Assert.Single(RecordingLibraryItem.Filter(Recordings, false, "", null, 3, 0)).Id);
        Assert.Equal("broken", RecordingLibraryItem.Filter(Recordings, false, "", null, 0, 1)[0].Id);
        Assert.Empty(RecordingLibraryItem.Filter(Recordings, false, "word", "EXCEL", 0, 0));
    }
    [Fact]
    public void MapRelationshipDoesNotClaimReviewedReadiness()
    {
        var built = Assert.Single(RecordingLibraryItem.Filter(Recordings, false, "", null, 1, 0));
        Assert.Equal("Map built", built.Status);
        Assert.Equal("Invalid", new RecordingLibraryItem(Recordings[3]).Status);
    }
}
