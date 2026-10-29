using UIMaps.Protocol.V1;
using UIMaps.Recorder.ProviderProbe;
using UIMaps.Recorder.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class UiaCaptureTraversalTests
{
    [Fact]
    public void CompleteBreadthFirstWalkPreservesParentsAndActualChildCounts()
    {
        var root = new Node(1, new Node(2, new Node(4)), new Node(3));
        var packet = Walk(root);

        Assert.Equal(new[] { "1", "2", "3", "4" }, packet.Nodes.Select(x => x.Name));
        var byName = packet.Nodes.ToDictionary(x => x.Name);
        Assert.Equal(byName["1"].AccessibilityEvidenceId, byName["2"].ParentEvidenceId);
        Assert.Equal(byName["2"].AccessibilityEvidenceId, byName["4"].ParentEvidenceId);
        Assert.Equal(2, byName["1"].ChildCountObserved);
        Assert.Equal(2, byName["1"].ChildCountIncluded);
        Assert.Equal(HealthState.Complete, packet.Health.State);
        Assert.Empty(packet.TraversalFrontier);
    }

    [Fact]
    public void NodeCeilingBoundsSiblingQueriesAndQueuedReferences()
    {
        var root = new Node(1, Enumerable.Range(2, 10_000).Select(x => new Node(x)).ToArray());
        var siblingQueries = 0;
        var packet = Walk(root, maxNodes: 4, next: node => { siblingQueries++; return Next(root, node); });

        Assert.Equal(4, packet.Nodes.Count);
        Assert.Equal(2, siblingQueries);
        Assert.Equal(3, packet.Nodes[0].ChildCountObserved);
        Assert.Equal(3, packet.Nodes[0].ChildCountIncluded);
        Assert.True(packet.NodeLimited);
        Assert.Contains(packet.TraversalFrontier, item => item.StopReason == "per_window_node_limit");
    }

    [Fact]
    public void DepthLimitObservesOnlyOneChildToAccountForUnexpandedSubtree()
    {
        var root = new Node(1, new Node(2, new Node(3), new Node(4)));
        var siblingQueries = 0;
        var packet = Walk(root, maxDepth: 0, next: node => { siblingQueries++; return Next(root, node); });

        var retained = Assert.Single(packet.Nodes);
        Assert.Equal(1, retained.ChildCountObserved);
        Assert.Equal(0, retained.ChildCountIncluded);
        Assert.Equal(0, siblingQueries);
        Assert.True(packet.DepthLimited);
        Assert.Contains(packet.TraversalFrontier, item => item.ParentEvidenceId == retained.AccessibilityEvidenceId && item.StopReason == "depth_limit");
    }

    [Fact]
    public void DeadlineReturnsObservedPrefixWithoutClaimingQueuedChildrenWereIncluded()
    {
        var root = new Node(1, new Node(2), new Node(3));
        var time = true;
        var packet = Walk(root, next: node => { time = false; return Next(root, node); }, hasTime: () => time);

        var retained = Assert.Single(packet.Nodes);
        Assert.Equal(2, retained.ChildCountObserved);
        Assert.Equal(0, retained.ChildCountIncluded);
        Assert.True(packet.TimeLimited);
        Assert.Contains(packet.TraversalFrontier, item => item.StopReason == "cooperative_deadline");
        Assert.Equal(HealthState.Partial, packet.Health.State);
    }

    [Fact]
    public void CachedRootCanBeRetainedAfterAcquisitionUsedRemainingTime()
    {
        var packet = Walk(new Node(1, new Node(2)), first: _ => throw new InvalidOperationException("No time for navigation."), hasTime: () => false);
        Assert.Single(packet.Nodes);
        Assert.True(packet.TimeLimited);
    }

    [Fact]
    public void NavigationFailureIsExplicitPartialAndDoesNotErasePriorNodes()
    {
        var packet = Walk(new Node(1), first: _ => throw new InvalidOperationException("Injected UIA failure."));
        Assert.Single(packet.Nodes);
        Assert.Equal(HealthState.Partial, packet.Health.State);
        Assert.Contains(packet.TraversalFrontier, item => item.StopReason == "child_enumeration_failed");
    }

    [Fact]
    public void RepeatedRuntimeIdentityDoesNotCreateDanglingOrInventedChild()
    {
        var root = new Node(1, new Node(2), new Node(2));
        var packet = Walk(root);
        Assert.Equal(2, packet.Nodes.Count);
        Assert.Equal(2, packet.Nodes[0].ChildCountObserved);
        Assert.Equal(1, packet.Nodes[0].ChildCountIncluded);
        Assert.Contains(packet.TraversalFrontier, item => item.StopReason == "cycle_detected");
        Assert.Equal(HealthState.Partial, packet.Health.State);
    }

    private static AccessibilityPacket Walk(Node root, int maxNodes = 100, int maxDepth = 8,
        Func<Node, Node?>? first = null, Func<Node, Node?>? next = null, Func<bool>? hasTime = null) =>
        UiaCaptureTraversal.Capture(root, "window", maxNodes, maxDepth,
            (node, id, parent) => new AccessibilityNodeRecord { AccessibilityEvidenceId = id, ParentEvidenceId = parent,
                WindowEvidenceId = "window", Name = node.Id.ToString(), RuntimeIdEvidence = { node.Id }, Health = ProtocolHealth.Complete() },
            first ?? (node => node.Children.FirstOrDefault()), next ?? (node => Next(root, node)), hasTime ?? (() => true));

    private static Node? Next(Node root, Node node)
    {
        var index = Array.IndexOf(root.Children, node);
        if (index >= 0) return index + 1 < root.Children.Length ? root.Children[index + 1] : null;
        foreach (var child in root.Children)
            if (Contains(child, node)) return Next(child, node);
        return null;
    }
    private static bool Contains(Node root, Node node) => ReferenceEquals(root, node) || root.Children.Any(child => Contains(child, node));
    private sealed class Node(int id, params Node[] children)
    {
        public int Id { get; } = id;
        public Node[] Children { get; } = children;
    }
}
