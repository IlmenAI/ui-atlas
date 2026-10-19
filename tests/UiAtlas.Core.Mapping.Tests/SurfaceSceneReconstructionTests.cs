using Google.Protobuf;
using UIMaps.Perception.Core;
using UIMaps.Protocol.V1;
using Xunit;

namespace UiAtlas.Core.Mapping.Tests;

public sealed class SurfaceSceneReconstructionTests
{
    [Fact]
    public void PopupBranchReportedThroughMainWindowMovesWithoutChangingRawEvidence()
    {
        var episode = Scene();
        var bytes = episode.ToByteArray();
        var objects = Build(episode, new());
        var popup = Window(objects, "200");
        var item = Control(objects, "item");
        Assert.Equal(popup.WorldObjectId, Property(item, "sceneOwnerSurfaceRef"));
        Assert.Equal(Window(objects, "100").WorldObjectId, Property(item, "sceneReportedSurfaceRef"));
        Assert.Equal(Control(objects, "menu").WorldObjectId, item.ParentWorldObjectId);
        Assert.Equal(popup.WorldObjectId, Control(objects, "menu").ParentWorldObjectId);
        Assert.Equal("visible", Property(item, "sceneVisibility"));
        Assert.Equal(bytes, episode.ToByteArray());
    }

    [Fact]
    public void GeometryOverlapOnlyOccludesAndPartialVisibilityIsClipped()
    {
        var episode = Scene();
        Nodes(episode).Add(Node("under", "", "Main button", "Button", "", Rect(450, 210, 100, 30)));
        var objects = Build(episode, new());
        var under = Control(objects, "under");
        Assert.Equal(Window(objects, "100").WorldObjectId, Property(under, "sceneOwnerSurfaceRef"));
        Assert.Empty(Property(under, "scenePresentedOn"));
        Assert.Equal(Window(objects, "200").WorldObjectId, Property(under, "sceneOccludedBy"));
        Assert.Equal("partially-visible", Property(under, "sceneVisibility"));
        Assert.Equal("450,210,50,30", Property(under, "sceneVisibleRegion"));
    }

    [Fact]
    public void StableGalleryItemKeepsOwnerAndIsPresentedOnPopup()
    {
        var reconstruction = new SurfaceSceneReconstructor();
        var before = Scene(false);
        Nodes(before).Add(Node("normal-before", "", "Normal", "ListItem", "", Rect(520, 210, 100, 30), 55));
        Build(before, reconstruction);
        var expanded = Scene();
        Nodes(expanded).Add(Node("normal-after", "menu", "Normal", "ListItem", "NetUIGalleryButton", Rect(520, 216, 100, 30), 55));
        var objects = Build(expanded, reconstruction);
        var normal = Control(objects, "normal-after");
        Assert.Equal(Window(objects, "100").WorldObjectId, Property(normal, "sceneOwnerSurfaceRef"));
        Assert.Equal(Window(objects, "200").WorldObjectId, Property(normal, "scenePresentedOn"));
        Assert.Empty(Property(normal, "sceneOccludedBy"));
        // The popup can remain open for several observations. Continuity retains ownership,
        // while all content and geometry still come from each new observation.
        var repeated = Build(expanded.Clone(), reconstruction);
        Assert.Equal(Window(repeated, "100").WorldObjectId, Property(Control(repeated, "normal-after"), "sceneOwnerSurfaceRef"));
        Assert.Equal(Window(repeated, "200").WorldObjectId, Property(Control(repeated, "normal-after"), "scenePresentedOn"));
        Nodes(expanded).Remove(Nodes(expanded).Single(node => node.AccessibilityEvidenceId == "normal-after"));
        Assert.DoesNotContain(Build(expanded, reconstruction), value => value.Name == "Normal");
    }

    [Fact]
    public void ReusedHwndDoesNotCarryControlsAndDifferentContentHasDifferentIdentity()
    {
        var reconstruction = new SurfaceSceneReconstructor();
        var first = Build(Scene(), reconstruction);
        var next = Scene();
        Nodes(next).Single(node => node.AccessibilityEvidenceId == "item").Name = "Different command";
        var second = Build(next, reconstruction);
        Assert.NotEqual(Property(Window(first, "200"), "sceneSurfaceKey"), Property(Window(second, "200"), "sceneSurfaceKey"));
        Assert.DoesNotContain(second, value => value.Name == "Popup command");
        Assert.DoesNotContain(Build(Scene(false), reconstruction), value => Property(value, "surfaceClass") == "RawPopupWindow");
    }

    [Fact]
    public void SamePopupContentAcrossNewHandlesHasOneFamilyButSimultaneousInstancesStaySeparate()
    {
        var first = Build(Scene(), new());
        var episode = Scene();
        Windows(episode).Single(window => window.WindowHandleEvidence == "200").WindowHandleEvidence = "201";
        var next = Build(episode, new());
        Assert.Equal(Property(Window(first, "200"), "sceneSurfaceKey"), Property(Window(next, "201"), "sceneSurfaceKey"));
        var clone = Windows(episode).Single(window => window.WindowHandleEvidence == "201").Clone();
        clone.WindowHandleEvidence = "202"; clone.WindowEvidenceId = "second-popup";
        Windows(episode).Add(clone);
        var concurrent = Build(episode, new());
        Assert.NotEqual(Property(Window(concurrent, "201"), "sceneSurfaceKey"), Property(Window(concurrent, "202"), "sceneSurfaceKey"));
        Assert.Contains("ambiguous", Property(Control(concurrent, "item"), "sceneOwnerReason"));
    }

    [Fact]
    public void LegacyWindowScopeRemainsStableWhilePopupWithUnknownProcessStaysObservationScoped()
    {
        var first = Scene();
        Windows(first).ToList().ForEach(window => window.ProcessInstanceEvidence = "");
        var second = first.Clone(); second.EpisodeId = "next-observation";
        var original = Build(first, new()); var next = Build(second, new());
        Assert.Equal(Property(Window(original, "100"), "sceneSurfaceKey"), Property(Window(next, "100"), "sceneSurfaceKey"));
        Assert.NotEqual(Property(Window(original, "200"), "sceneSurfaceKey"), Property(Window(next, "200"), "sceneSurfaceKey"));
    }

    [Fact]
    public void ForeignProcessPopupCannotClaimMainWindowBranch()
    {
        var episode = Scene();
        Windows(episode)[1].ProcessInstanceEvidence = "different-process";
        var objects = Build(episode, new());
        Assert.Equal(Window(objects, "100").WorldObjectId, Property(Control(objects, "item"), "sceneOwnerSurfaceRef"));
    }

    [Fact]
    public void VisibleEmptyPopupIsRetainedAndHiddenParentSuppressesDescendants()
    {
        var episode = Scene();
        Nodes(episode).Single(node => node.AccessibilityEvidenceId == "menu").IsOffscreen = true;
        var objects = Build(episode, new());
        Assert.Equal("True", Property(Window(objects, "200"), "sceneVisible"));
        Assert.Equal("offscreen-or-unavailable", Property(Control(objects, "item"), "sceneVisibility"));
        Assert.Empty(Property(Control(objects, "item"), "sceneVisibleRegion"));
    }

    [Fact]
    public void OversizedPopupContainerAndPartiallyVisibleItemAreClippedToRecordedSurface()
    {
        var episode = Scene();
        Nodes(episode).Add(Node("group", "menu", "Category", "Group", "NetUIGalleryCategoryContainer", Rect(500, 200, 250, 700)));
        Nodes(episode).Add(Node("clipped", "group", "Last item", "ListItem", "", Rect(510, 490, 200, 30)));
        var objects = Build(episode, new());
        Assert.Equal(Window(objects, "200").WorldObjectId, Property(Control(objects, "group"), "sceneOwnerSurfaceRef"));
        Assert.Equal("510,490,200,10", Property(Control(objects, "clipped"), "sceneVisibleRegion"));
    }

    [Fact]
    public void MissingSourceOwnerStaysUnresolvedInsteadOfUsingFirstWindow()
    {
        var episode = Scene();
        var node = Node("orphan", "", "Orphan", "Button", "", Rect(50, 50, 20, 20));
        node.WindowEvidenceId = "unknown-window"; Nodes(episode).Add(node);
        var objects = Build(episode, new());
        var orphan = Control(objects, "orphan");
        Assert.Empty(Property(orphan, "sceneOwnerSurfaceRef"));
        Assert.Equal("unresolved", Property(orphan, "sceneVisibility"));
    }

    [Fact]
    public void ReconstructingRetainedPublicationPreservesOriginalOwnershipAndIsIdempotent()
    {
        var episode = Scene(); var objects = Build(episode, new());
        var first = objects.Select(value => value.ToByteArray()).ToArray();
        new SurfaceSceneReconstructor().Apply(episode, objects);
        Assert.Equal(first, objects.Select(value => value.ToByteArray()).ToArray(), ByteArrayComparer.Instance);
        Assert.Equal(Window(objects, "100").WorldObjectId, Property(Control(objects, "item"), "sceneReportedSurfaceRef"));
    }

    [Fact]
    public void UnknownTimingIsQualifiedRatherThanPromotedToStable()
    {
        var episode = Scene(); episode.ObservationConsistency.Status = "changed";
        Assert.All(Build(episode, new()).Where(value => value.ObjectKind == WorldObjectKind.WindowObject),
            window => Assert.Equal("changed", Property(window, "sceneAlignment")));
    }

    [Fact]
    public void NamedPopupKeepsFamilyAcrossCoverageChangesAndDifferentTitlesStaySeparate()
    {
        var episode = Scene(); Windows(episode)[1].Title = "Shape Fill";
        var first = Build(episode, new());
        var next = episode.Clone(); Nodes(next).RemoveAt(Nodes(next).Count - 1);
        var second = Build(next, new());
        Assert.Equal(Property(Window(first, "200"), "sceneSurfaceKey"), Property(Window(second, "200"), "sceneSurfaceKey"));
        Assert.NotEqual(Property(Window(first, "200"), "sceneContentFingerprint"), Property(Window(second, "200"), "sceneContentFingerprint"));
        Windows(next)[1].Title = "Shape Outline";
        Assert.NotEqual(Property(Window(first, "200"), "sceneSurfaceKey"), Property(Window(Build(next, new()), "200"), "sceneSurfaceKey"));
        var other = Windows(episode)[1].Clone(); other.WindowEvidenceId = "other"; other.WindowHandleEvidence = "300";
        other.BoundsPx.X += 300; Windows(episode).Add(other);
        var concurrent = Build(episode, new());
        Assert.NotEqual(Property(Window(concurrent, "200"), "sceneSurfaceKey"), Property(Window(concurrent, "300"), "sceneSurfaceKey"));
    }

    [Fact]
    public void NativeAccessibilityRootNamesAnUntitledMenuIndependentlyOfItsChildren()
    {
        var episode = Scene();
        var root = Node("native-menu", "", "Change Case", "Menu", "Net UI Tool Window", Windows(episode)[1].BoundsPx.Clone());
        root.WindowEvidenceId = "popup"; root.NativeWindowHandleEvidence = "200"; Nodes(episode).Add(root);
        var first = Build(episode, new());
        Nodes(episode).Remove(Nodes(episode).Single(n => n.AccessibilityEvidenceId == "item"));
        var second = Build(episode, new());
        Assert.Equal(Property(Window(first, "200"), "sceneSurfaceKey"), Property(Window(second, "200"), "sceneSurfaceKey"));
        Assert.Equal("native-accessibility-root-and-owner", Property(Window(second, "200"), "sceneFamilyEvidence"));
    }

    [Fact]
    public void TooltipReportedUnderMainWindowGetsItsOwnSurfaceWithoutChangingRawEvidence()
    {
        var episode = Scene(); var bytes = episode.ToByteArray();
        Windows(episode)[1].ClassName = "OfficeTooltip"; Windows(episode)[1].Title = "Helpful text";
        Nodes(episode).Clear();
        Nodes(episode).Add(Node("tip", "", "Helpful text", "Pane", "OfficeTooltip", Windows(episode)[1].BoundsPx.Clone()));
        bytes = episode.ToByteArray();
        var objects = Build(episode, new());
        Assert.Equal("RawTooltipWindow", Property(Window(objects, "200"), "surfaceClass"));
        Assert.Equal(Window(objects, "200").WorldObjectId, Property(Control(objects, "tip"), "sceneOwnerSurfaceRef"));
        Assert.Equal("visible", Property(Control(objects, "tip"), "sceneVisibility"));
        Assert.Equal(bytes, episode.ToByteArray());
        Windows(episode)[1].Title = "Different tooltip";
        Assert.NotEqual(Property(Window(objects, "200"), "sceneSurfaceKey"), Property(Window(Build(episode, new()), "200"), "sceneSurfaceKey"));
    }

    [Fact]
    public void MatchingTooltipRoleQualifiesGenericWindowButUnrelatedTooltipDoesNot()
    {
        var episode = Scene();
        var tip = Node("tip", "", "Hint", "ToolTip", "", Windows(episode)[1].BoundsPx.Clone());
        tip.NativeWindowHandleEvidence = "200"; Nodes(episode).Add(tip);
        Assert.Equal("RawTooltipWindow", Property(Window(Build(episode, new()), "200"), "surfaceClass"));
        tip.NativeWindowHandleEvidence = "999";
        Assert.Equal("RawPopupWindow", Property(Window(Build(episode, new()), "200"), "surfaceClass"));
    }

    [Fact]
    public void NestedPopupReportedThroughParentPopupMovesToItsOwnNativeSurface()
    {
        var episode = Scene(); var nested = Windows(episode)[1].Clone();
        nested.WindowEvidenceId = "nested"; nested.WindowHandleEvidence = "300";
        nested.OwnerChainEvidence.Clear(); nested.OwnerChainEvidence.Add("200");
        nested.BoundsPx = Rect(750, 200, 200, 200); nested.ZOrder = 4; Windows(episode).Add(nested);
        var item = Node("texture", "", "Texture", "Menu", "NetUIToolWindow", nested.BoundsPx.Clone());
        item.WindowEvidenceId = "popup"; Nodes(episode).Add(item);
        var objects = Build(episode, new());
        Assert.Equal(Window(objects, "300").WorldObjectId, Property(Control(objects, "texture"), "sceneOwnerSurfaceRef"));
    }

    [Fact]
    public void RetainedWorkerCommandsCompletePreferredRootOnlyTreeAndReplayDeterministically()
    {
        var episode = Scene();
        var retained = Build(episode, new());
        var packet = episode.SensorPackets.Single(p => p.Accessibility is not null).Accessibility;
        var response = new RawProviderResponse { WindowEvidenceId = "popup", Attempt = new() { ProviderId = "msaa", AttemptId = "msaa-1" }, Packet = new() };
        var root = Node("msaa-root", "", "", "Window", "", Rect(500, 200, 250, 300)); root.WindowEvidenceId = "popup"; root.ProviderId = "msaa";
        var item = Node("msaa-item", "msaa-root", "UPPERCASE", "MenuItem", "", Rect(510, 260, 200, 30)); item.WindowEvidenceId = "popup"; item.ProviderId = "msaa";
        response.Packet.Nodes.Add(root); response.Packet.Nodes.Add(item);
        var duplicate = item.Clone(); duplicate.AccessibilityEvidenceId = "duplicate"; response.Packet.Nodes.Add(duplicate);
        packet.RawProviderResponses.Add(response);
        var bytes = episode.ToByteArray();
        var completed = WorldObjectBuilder.CompleteAccessibility(episode, retained); new SurfaceSceneReconstructor().Apply(episode, completed);
        var command = Control(completed, "msaa-item");
        Assert.Equal("msaa", Property(command, "providerId"));
        Assert.Equal(Window(completed, "200").WorldObjectId, Property(command, "sceneOwnerSurfaceRef"));
        Assert.Single(completed, v => v.Name == "UPPERCASE");
        Assert.Equal(bytes, episode.ToByteArray());
        Assert.Equal(completed.Select(v => v.WorldObjectId), WorldObjectBuilder.CompleteAccessibility(episode, completed).Select(v => v.WorldObjectId));
        Assert.Equal(command.WorldObjectId, Control(WorldObjectBuilder.CompleteAccessibility(episode, retained), "msaa-item").WorldObjectId);
        Assert.Single(Build(episode, new()), v => v.Name == "UPPERCASE");
        Assert.DoesNotContain(Build(Scene(), new()), v => v.Name == "UPPERCASE");
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();
        public bool Equals(byte[]? a, byte[]? b) => a is not null && b is not null && a.SequenceEqual(b);
        public int GetHashCode(byte[] value) => value.Length;
    }
    private static WorldObject Window(IReadOnlyList<WorldObject> objects, string hwnd) => objects.Single(value => value.ObjectKind == WorldObjectKind.WindowObject && Property(value, "windowHandleEvidence") == hwnd);
    private static WorldObject Control(IReadOnlyList<WorldObject> objects, string evidence) => objects.Single(value => Property(value, "sourceAccessibilityEvidenceId") == evidence);
    private static string Property(WorldObject value, string key) => SurfaceSceneReconstructor.Property(value, key);
    private static IReadOnlyList<WorldObject> Build(SensorEpisodeBundle bundle, SurfaceSceneReconstructor reconstruction)
    {
        var observations = new ObservationNormalizer().Normalize(bundle, 105, DateTimeOffset.UnixEpoch);
        var objects = new WorldObjectBuilder(PerceptionPolicy.Default).Build(bundle, observations);
        reconstruction.Apply(bundle, objects);
        return objects;
    }
    private static Google.Protobuf.Collections.RepeatedField<WindowObservationRecord> Windows(SensorEpisodeBundle bundle) => bundle.SensorPackets.Single(packet => packet.WindowTopology is not null).WindowTopology.Windows;
    private static Google.Protobuf.Collections.RepeatedField<AccessibilityNodeRecord> Nodes(SensorEpisodeBundle bundle) => bundle.SensorPackets.Single(packet => packet.Accessibility is not null).Accessibility.Nodes;
    private static SensorEpisodeBundle Scene(bool popup = true)
    {
        var bundle = EpisodeFixture.Create();
        bundle.DisplayDescriptor.BoundsPx = Rect(0, 0, 1200, 900);
        bundle.ObservationConsistency = new() { Status = "stable" };
        var windows = Windows(bundle); windows.Clear();
        windows.Add(new WindowObservationRecord { WindowEvidenceId = "main", WindowHandleEvidence = "100", RootOwnerHandleEvidence = "100",
            ProcessInstanceEvidence = "process-1", ProcessId = 1, ProcessName = "Word", ClassName = "OpusApp", WindowStyle = 0x00C40000,
            BoundsPx = Rect(0, 0, 1000, 800), IntersectionWithDisplayPx = Rect(0, 0, 1000, 800), IsVisible = true, ZOrder = 10 });
        if (popup) windows.Add(new WindowObservationRecord { WindowEvidenceId = "popup", WindowHandleEvidence = "200", RootOwnerHandleEvidence = "100",
            OwnerChainEvidence = { "100" }, ProcessInstanceEvidence = "process-1", ProcessId = 1, ProcessName = "Word",
            ClassName = "Net UI Tool Window", WindowStyle = 0x80000000, IsToolWindow = true,
            BoundsPx = Rect(500, 200, 250, 300), IntersectionWithDisplayPx = Rect(500, 200, 250, 300), IsVisible = true, ZOrder = 5 });
        Nodes(bundle).Clear();
        Nodes(bundle).Add(Node("opener", "", "Colors", "MenuItem", "NetUIAnchor", Rect(500, 160, 80, 40), 1));
        if (popup)
        {
            Nodes(bundle).Add(Node("menu", "opener", "Colors", "Menu", "NetUIToolWindow", Rect(500, 200, 250, 300), 2));
            Nodes(bundle).Add(Node("item", "menu", "Popup command", "MenuItem", "NetUITWBtnMenuItem", Rect(510, 250, 200, 30), 3));
        }
        return bundle;
    }
    private static AccessibilityNodeRecord Node(string id, string parent, string name, string role, string cls, RectPx bounds, int runtime = 0) => new()
    {
        AccessibilityEvidenceId = id, ParentEvidenceId = parent, WindowEvidenceId = "main", Name = name, ControlType = role,
        LocalizedControlType = role, ClassName = cls, BoundsPx = bounds, ProviderId = "uia", IsEnabled = true,
        RuntimeIdEvidence = { runtime }, Health = new() { State = HealthState.Complete }
    };
    private static RectPx Rect(int x, int y, int width, int height) => new() { X = x, Y = y, Width = width, Height = height };
}
