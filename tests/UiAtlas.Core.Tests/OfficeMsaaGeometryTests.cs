using Google.Protobuf;
using UIMaps.Perception.Core;
using UIMaps.Protocol.V1;

namespace UiAtlas.Core.Tests;

public sealed class OfficeMsaaGeometryTests
{
    [Fact]
    public void QualifiedClientScaleIsAppliedBeforeVisibilityWithoutChangingRawPacketsOrChrome()
    {
        var episode = Episode();
        var bytes = episode.ToByteArray();
        var corrections = OfficeMsaaGeometry.Resolve(episode);
        Assert.Equal(new RectPx { X = 3223, Y = 898, Width = 75, Height = 26 }, corrections["ok"].Bounds);
        Assert.DoesNotContain("client", corrections.Keys);
        Assert.DoesNotContain("close", corrections.Keys);
        Assert.DoesNotContain("uia", corrections.Keys);
        Assert.DoesNotContain("foreign", corrections.Keys);
        var world = new WorldObject { WorldObjectId = "ok", BoundsPx = corrections["ok"].ReportedBounds.Clone(),
            Properties = { new StringProperty { Key = "sourceAccessibilityEvidenceId", Value = "ok" } } };
        OfficeMsaaGeometry.Apply(episode, [world]);
        Assert.Equal(corrections["ok"].Bounds, world.BoundsPx);
        Assert.Equal("3558,1385,151,54", SurfaceSceneReconstructor.Property(world, "sceneReportedBounds"));
        OfficeMsaaGeometry.Apply(episode, [world]); // Reopening an already corrected publication must not scale twice.
        Assert.Equal(corrections["ok"].Bounds, world.BoundsPx);
        Assert.Single(world.Properties, property => property.Key == "sceneCoordinateCorrection");
        Assert.Equal(bytes, episode.ToByteArray());
    }

    [Theory]
    [InlineData("missing-native")]
    [InlineData("one-anchor")]
    [InlineData("foreign-anchors")]
    [InlineData("ambiguous-anchor")]
    [InlineData("already-physical")]
    [InlineData("other-window-class")]
    [InlineData("missing-dpi")]
    public void DpiOrOverflowAloneNeverAuthorizesCoordinateCorrection(string mode)
    {
        var episode = Episode();
        var packet = episode.SensorPackets.Single(packet => packet.Accessibility is not null).Accessibility;
        var topology = episode.SensorPackets.Single(packet => packet.WindowTopology is not null).WindowTopology.Windows[0];
        if (mode == "missing-native")
            foreach (var node in packet.Nodes.Where(node => node.ProviderId == "native-hwnd").ToArray()) packet.Nodes.Remove(node);
        if (mode == "one-anchor") packet.Nodes.Remove(packet.Nodes.Single(node => node.AccessibilityEvidenceId == "native2"));
        if (mode == "foreign-anchors")
            foreach (var node in packet.Nodes.Where(node => node.ProviderId == "native-hwnd")) node.WindowEvidenceId = "other";
        if (mode == "ambiguous-anchor")
        {
            var duplicate = packet.Nodes.Single(node => node.AccessibilityEvidenceId == "native1").Clone();
            duplicate.AccessibilityEvidenceId = "duplicate"; duplicate.BoundsPx.X++;
            packet.Nodes.Add(duplicate);
        }
        if (mode == "already-physical")
        {
            var corrections = OfficeMsaaGeometry.Resolve(episode);
            foreach (var node in packet.Nodes)
                if (corrections.TryGetValue(node.AccessibilityEvidenceId, out var correction)) node.BoundsPx = correction.Bounds;
        }
        if (mode == "other-window-class") topology.ClassName = "CustomDialog";
        if (mode == "missing-dpi") topology.ClearDpi();
        Assert.Empty(OfficeMsaaGeometry.Resolve(episode));
    }

    private static SensorEpisodeBundle Episode()
    {
        static RectPx Rect(int x, int y, int width, int height) => new() { X = x, Y = y, Width = width, Height = height };
        AccessibilityNodeRecord Node(string id, string parent, string type, RectPx bounds, string provider = "msaa", string window = "dialog") =>
            new() { AccessibilityEvidenceId = id, ParentEvidenceId = parent, WindowEvidenceId = window,
                ProviderId = provider, ControlType = type, BoundsPx = bounds, IsEnabled = true };
        var native1 = Node("native1", "", "Pane", Rect(2906, 475, 229, 17), "native-hwnd"); native1.ClassName = "EDTBX";
        var native2 = Node("native2", "", "Pane", Rect(3152, 475, 109, 17), "native-hwnd"); native2.ClassName = "EDTBX";
        return new SensorEpisodeBundle {
            DisplayDescriptor = new() { DpiX = 96, DpiY = 96 },
            SensorPackets = {
                new SensorPacket { WindowTopology = new() { Windows = {
                    new WindowObservationRecord { WindowEvidenceId = "dialog", ClassName = "bosa_sdm_XL9", Dpi = 192,
                        BoundsPx = Rect(2886, 381, 502, 552) } } } },
                new SensorPacket { Accessibility = new() { Nodes = {
                    Node("client", "", "Window", Rect(2887, 410, 501, 522)),
                    Node("font", "client", "Edit", Rect(2917, 533, 474, 45)),
                    Node("style", "client", "Edit", Rect(3409, 533, 234, 45)),
                    Node("ok", "client", "Button", Rect(3558, 1385, 151, 54)),
                    Node("close", "", "Button", Rect(3350, 381, 30, 28)),
                    Node("uia", "client", "Button", Rect(3223, 898, 75, 26), "uia"),
                    Node("foreign", "client", "Button", Rect(3558, 1385, 151, 54), window: "other"), native1, native2 } } }
            }
        };
    }
}
