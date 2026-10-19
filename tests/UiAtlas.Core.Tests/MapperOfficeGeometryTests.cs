using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Tests;

public sealed class MapperOfficeGeometryTests
{
    // Physical HWND rectangles and MSAA records from the user's 175% recording.
    private static readonly RectI Client = new(11, 51, 876, 913);
    private static readonly RectI[] Anchors = [new(45, 164, 401, 29), new(475, 164, 191, 29), new(696, 164, 159, 29)];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FractionalMonitorScaleAlignsVirtualControlsAgainstThreeNativeEditAnchors(bool msaa)
    {
        var controls = Controls(msaa);
        var result = MapperMsaaGeometry.ProjectAnchoredClient(controls, 200, Client, .875, Anchors);
        Assert.Equal(new RectI(598, 904, 132, 47), result.Single(c => c.Name == "OK").Bounds);
        Assert.Equal(new RectI(37, 159, 415, 39), result.Single(c => c.Name == "Font").Bounds);
        Assert.Equal(new RectI(53, 489, 143, 34), result.Single(c => c.Name == "Strike").Bounds);
        Assert.Equal(new RectI(682, 1026, 151, 54), controls.Single(c => c.Name == "OK").Bounds);
        Assert.Equal(controls.Single(c => c.Name == "Close"), result.Single(c => c.Name == "Close"));
        Assert.Equal(controls.Single(c => c.Name == "Native edit"), result.Single(c => c.Name == "Native edit"));
        var again = MapperMsaaGeometry.ProjectAnchoredClient(result, 200, Client, .875, Anchors);
        Assert.Same(result, again); // Retained presentation evidence cannot be scaled twice.
    }

    [Theory]
    [InlineData("one-anchor")]
    [InlineData("ambiguous-anchor")]
    [InlineData("wrong-scale")]
    [InlineData("wrong-window")]
    [InlineData("physical")]
    [InlineData("invalid-scale")]
    public void ScalingRequiresUnambiguousSameWindowGeometry(string mode)
    {
        var controls = Controls(true);
        var anchors = Anchors;
        var scale = .875;
        if (mode == "one-anchor") anchors = [Anchors[0]];
        if (mode == "ambiguous-anchor") anchors = [..Anchors, Anchors[0] with { X = 44 }];
        if (mode == "wrong-scale") scale = .5;
        if (mode == "wrong-window") controls = controls.Select(c => c with { WindowHwnd = 300 }).ToArray();
        if (mode == "physical") controls = MapperMsaaGeometry.ProjectAnchoredClient(controls, 200, Client, scale, anchors).ToArray();
        if (mode == "invalid-scale") scale = double.NaN;
        Assert.Same(controls, MapperMsaaGeometry.ProjectAnchoredClient(controls, 200, Client, scale, anchors));
    }

    [Fact]
    public void MixedProvidersQualifyIndependentlyAndNativeChildSubtreeStaysPhysical()
    {
        var msaa = Controls(true);
        var physicalUia = MapperMsaaGeometry.ProjectAnchoredClient(Controls(false), 200, Client, .875, Anchors);
        AutomationObservation[] all = [..msaa, ..physicalUia];
        var result = MapperMsaaGeometry.ProjectAnchoredClient(all, 200, Client, .875, Anchors);
        foreach (var control in physicalUia) Assert.Equal(control, result.Single(c => c.RuntimeId == control.RuntimeId));
        Assert.Equal(new RectI(598, 904, 132, 47), result.Single(c => c.RuntimeId == "msaa-OK").Bounds);
    }

    private static AutomationObservation[] Controls(bool msaa)
    {
        var prefix = msaa ? "msaa-" : "uia-";
        var framework = msaa ? "UiAtlas.Msaa" : "";
        AutomationObservation Make(string name, string type, RectI bounds, string parent = "root") =>
            MapperHighlightTests.Control(prefix + name, type, bounds, framework) with
            { Name = name, ParentRuntimeId = parent.Length == 0 ? "" : prefix + parent, WindowHwnd = 200 };
        return [Make("root", "Window", Client, ""),
            Make("Font", "Edit", new(41,174,474,45)), Make("Style", "Edit", new(533,174,234,45)),
            Make("Size", "Edit", new(785,174,198,45)), Make("OK", "Button", new(682,1026,151,54)),
            Make("Strike", "CheckBox", new(59,552,163,38)),
            Make("title", "TitleBar", new(11,0,876,51)), Make("Close", "Button", new(810,0,77,51), "title"),
            Make("Native edit", "Edit", Anchors[0]) with { NativeWindowHwnd = 123 },
            Make("Native text", "Text", Anchors[0], "Native edit")];
    }
}
