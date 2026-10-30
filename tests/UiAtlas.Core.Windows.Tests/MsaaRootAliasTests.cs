using UIMaps.Protocol.V1;
using UIMaps.Recorder.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class MsaaRootAliasTests
{
    [Fact]
    public void SameAcquisitionAliasPreservesBothTreesWithIndependentAncestryAndValues()
    {
        var root = Node("root", "window", "Guest list"); root.ChildCountObserved = root.ChildCountIncluded = 1;
        var child = Node("child", "root", "Guest");
        var packet = new AccessibilityPacket(); packet.Nodes.Add(root); packet.Nodes.Add(child); packet.NodesObserved = 2;
        Assert.True(NativeMsaaProvider.TryAppendRootAlias(packet, new[] { root, child }, 4));
        Assert.Equal(4, packet.Nodes.Count); Assert.Equal(4, packet.NodesObserved);
        Assert.Equal(4, packet.Nodes.Select(x => x.AccessibilityEvidenceId).Distinct().Count());
        Assert.Equal("", packet.Nodes[2].ParentEvidenceId);
        Assert.Equal(packet.Nodes[2].AccessibilityEvidenceId, packet.Nodes[3].ParentEvidenceId);
        Assert.Equal("Guest", packet.Nodes[3].Name);
        packet.Nodes[3].Name = "Changed"; Assert.Equal("Guest", child.Name);
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("truncated")]
    [InlineData("failed_read")]
    [InlineData("missing_parent")]
    [InlineData("cycle")]
    [InlineData("duplicate_id")]
    public void UnprovenOrIncompleteSubtreeFallsBackWithoutAppendingAnything(string condition)
    {
        var root = Node("root", "window", "Root"); root.ChildCountObserved = root.ChildCountIncluded = 1;
        var child = Node("child", "root", "Child");
        var maximum = condition == "budget" ? 1 : 10;
        if (condition == "truncated") root.ChildCountObserved++;
        if (condition == "failed_read") child.PropertyCoverage.Failed = 1;
        if (condition == "missing_parent") child.ParentEvidenceId = "absent";
        if (condition == "cycle") child.ParentEvidenceId = "child";
        if (condition == "duplicate_id") child.AccessibilityEvidenceId = "root";
        var packet = new AccessibilityPacket();
        Assert.False(NativeMsaaProvider.TryAppendRootAlias(packet, new[] { root, child }, maximum));
        Assert.Empty(packet.Nodes); Assert.Equal(0, packet.NodesObserved);
    }

    private static AccessibilityNodeRecord Node(string id, string parent, string name) => new()
    {
        AccessibilityEvidenceId = id, ParentEvidenceId = parent, Name = name,
        Health = ProtocolHealth.Complete(), PropertyCoverage = new PropertyCoverage { Attempted = 5, Read = 5 }
    };
}
