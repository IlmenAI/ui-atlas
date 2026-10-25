using Xunit;
using UIMaps.Perception.Core;
using UIMaps.Protocol.V1;

namespace UiAtlas.Core.Mapping.Tests;

public sealed class ScopedRuntimeTrackTests
{
    [Fact]
    public void OverlappingRibbonPanesKeepTheirOwnRuntimeIdentity()
    {
        var previous = new WorldStateSnapshot();
        previous.WorldObjects.AddRange(new[] { Pane("previous-a", "42,1", "track-a"), Pane("previous-b", "42,2", "track-b") });
        var current = Pane("current", "42,2");
        var track = Assert.Single(new TrackAssociator(new()).Associate([current], previous));
        Assert.Equal("track-b", track.TrackId);
        Assert.Equal(AssociationState.Matched, track.AssociationState);
        Assert.Contains("same_scoped_uia_runtime_identity", track.AssociationEvidence);
        Assert.DoesNotContain(current.Health.Issues, i => i.State == HealthState.IdentityAmbiguous);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("process")]
    [InlineData("window")]
    public void ProviderIdentityDoesNotAliasAnotherObjectOrOwner(string difference)
    {
        var old = Pane("old", "42,1", "old-track");
        var current = Pane("new", difference == "runtime" ? "42,2" : "42,1");
        if (difference == "process") current.ProcessInstanceEvidence = "other-process";
        if (difference == "window") current.WindowScopeRef = "other-window";
        var previous = new WorldStateSnapshot { WorldObjects = { old } };
        var track = Assert.Single(new TrackAssociator(new()).Associate([current], previous));
        Assert.Equal(AssociationState.New, track.AssociationState);
        Assert.NotEqual(old.TrackId, track.TrackId);
    }

    [Fact]
    public void DuplicatePriorRuntimeIdentityRemainsAmbiguous()
    {
        var previous = new WorldStateSnapshot { WorldObjects = { Pane("old-a", "42,1", "a"), Pane("old-b", "42,1", "b") } };
        var current = Pane("new", "42,1");
        var track = Assert.Single(new TrackAssociator(new()).Associate([current], previous));
        Assert.Equal(AssociationState.Ambiguous, track.AssociationState);
    }

    [Fact]
    public void DuplicateCurrentIdentityCannotConsumeTheSamePriorIdentityTwice()
    {
        var previous = new WorldStateSnapshot { WorldObjects = { Pane("old", "42,1", "a") } };
        var tracks = new TrackAssociator(new()).Associate([Pane("a", "42,1"), Pane("b", "42,1")], previous);
        Assert.All(tracks, track => Assert.Equal(AssociationState.Ambiguous, track.AssociationState));
    }

    [Fact]
    public void ABetterGeometricScoreDoesNotOverrideDuplicateProviderIdentity()
    {
        var other = Pane("old-b", "42,1", "b");
        other.Name = "Other pane"; other.BoundsPx.X = 9000;
        var previous = new WorldStateSnapshot { WorldObjects = { Pane("old-a", "42,1", "a"), other } };
        var track = Assert.Single(new TrackAssociator(new()).Associate([Pane("new", "42,1")], previous));
        Assert.Equal(AssociationState.Ambiguous, track.AssociationState);
    }

    private static WorldObject Pane(string id, string runtime, string track = "") => new()
    {
        WorldObjectId = id, TrackId = track, ObjectKind = WorldObjectKind.AccessibilityElement,
        Name = "Ribbon", Role = "Pane", ProcessInstanceEvidence = "word:instance", WindowScopeRef = "word:window",
        BoundsPx = new() { Width = 1000, Height = 200 }, Health = new() { State = HealthState.Complete },
        Properties = { new StringProperty { Key = "providerId", Value = "uia" }, new StringProperty { Key = "runtimeIdentityEvidence", Value = runtime },
            new StringProperty { Key = "className", Value = "NetUInetpane" } }
    };
}
