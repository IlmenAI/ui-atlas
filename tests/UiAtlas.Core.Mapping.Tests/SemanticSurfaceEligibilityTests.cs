using Google.Protobuf;
using UIMaps.Map.Core;
using UIMaps.Protocol.V1;
using UIMaps.Protocol.V3;
using Xunit;

namespace UiAtlas.Core.Mapping.Tests;

public sealed class SemanticSurfaceEligibilityTests
{
    private const string Process = "pid:42:started:2026-10-23T00:00:00.0000000Z";
    private const string Time = "2026-10-23T00:01:00Z";
    private static TargetApplicationIdentity Target => new()
    {
        ApplicationRef = "app:test", DisplayName = "Fixture", ExecutableName = "fixture.exe",
        ProcessIdentityEvidence = { Process }, WindowIdentityEvidence = { "100", "10" }
    };

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 200)]
    [InlineData(200, 0)]
    [InlineData(-1, 200)]
    public void NonvisualOwnerContextIsRetainedButCannotBecomeASurface(int width, int height)
    {
        var engine = new UiMapConstructionEngine();
        var draft = engine.CreateDraft("session", Target, Time);
        var owner = Window("owner", "10", "Progress", width, height);
        // A native owner can also be represented below a visible window in the
        // perceived tree. Descendant scope must not bypass surface eligibility.
        owner.ParentWorldObjectId = "main";
        var main = Window("main", "100", "Arrival", 800, 600, "10");
        var booking = Window("booking", "200", "Booking", 500, 400, "100");
        var helper = Window("helper", "300", "Owned helper", 300, 200, "10");
        helper.ProcessInstanceEvidence = "pid:99:started:2026-10-23T00:00:00Z";
        helper.Properties.Single(p => p.Key == "processName").Value = "helper";
        var objects = new[] { owner, main, booking, helper, new WorldObject
        {
            WorldObjectId = "owner-child", ParentWorldObjectId = "owner",
            ObjectKind = WorldObjectKind.AccessibilityElement, Name = "Owner context",
            BoundsPx = new RectPx { Width = 10, Height = 10 }
        } };
        var bytes = objects.Select(value => value.ToByteArray()).ToArray();

        engine.Attach(draft, Target, Source("first"), objects, null, Time);

        Assert.Equal(new[] { "Arrival", "Booking", "Owned helper" }, draft.Entities.Where(e => e.EntityKind == MapEntityKind.MapSurface)
            .Select(e => e.DisplayName).Order().ToArray());
        Assert.Contains(draft.Dispositions, d => d.SourceWorldObjectRef == "owner" && d.ReasonCode == "window_not_observable");
        Assert.DoesNotContain(draft.Entities, e => e.DisplayName == "Owner context");
        for (var i = 0; i < objects.Length; i++) Assert.Equal(bytes[i], objects[i].ToByteArray());
        Assert.Equal("10", main.Properties.Single(p => p.Key == "ownerWindowHandleEvidence").Value);
    }

    [Fact]
    public void VisibleImageOnlyToolWindowIsNotExcludedByNameClassOrLackOfControls()
    {
        var engine = new UiMapConstructionEngine();
        var draft = engine.CreateDraft("session", Target, Time);
        var window = Window("tool", "100", "Progress", 1, 1);
        window.Properties.Add(new StringProperty { Key = "isToolWindow", Value = "True" });
        engine.Attach(draft, Target, Source("first"), [window], null, Time);
        Assert.Equal("Progress", Assert.Single(draft.Entities, e => e.EntityKind == MapEntityKind.MapSurface).DisplayName);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("minimized")]
    [InlineData("cloaked")]
    [InlineData("zero-area")]
    [InlineData("closed")]
    public void PreviouslyObservedWindowsKeepTheirSurfaceAndEvidence(string laterState)
    {
        var engine = new UiMapConstructionEngine();
        var draft = engine.CreateDraft("session", Target, Time);
        var main = Window("main", "100", "Arrival", 800, 600);
        var popup = Window("popup", "200", "Legitimate popup", 200, 100, "100");
        engine.Attach(draft, Target, Source("first"), [main, popup], null, Time);
        var original = draft.Entities.Single(e => e.EntityKind == MapEntityKind.MapSurface && e.DisplayName == "Legitimate popup").Clone();
        if (laterState == "hidden") popup.Properties.Single(p => p.Key == "isVisible").Value = "False";
        if (laterState == "minimized") popup.Properties.Add(new StringProperty { Key = "isMinimized", Value = "True" });
        if (laterState == "cloaked") popup.Properties.Add(new StringProperty { Key = "isCloakedOrUnavailable", Value = "True" });
        if (laterState == "zero-area") popup.BoundsPx = new RectPx();
        engine.Attach(draft, Target, Source("second"), laterState == "closed" ? [main] : [main, popup], null, Time);
        var retained = draft.Entities.Single(e => e.MapEntityRef == original.MapEntityRef);
        Assert.Equal(original.ToByteArray(), retained.ToByteArray());
        Assert.Equal(2, draft.Entities.Count(e => e.EntityKind == MapEntityKind.MapSurface));
    }

    private static SourceObservationSet Source(string id) => new()
    {
        SourceObservationSetRef = id, ApplicationRef = "app:test", PublicationRef = "publication:" + id,
        AcquisitionSemantic = AcquisitionSemantic.AcquisitionSnapshot
    };

    private static WorldObject Window(string id, string hwnd, string title, int width, int height, string owner = "") => new()
    {
        WorldObjectId = id, ObjectKind = WorldObjectKind.WindowObject, Name = title, Role = "window",
        TrackId = "track:" + id, ProcessInstanceEvidence = Process,
        BoundsPx = new RectPx { Width = width, Height = height },
        Properties =
        {
            new StringProperty { Key = "processName", Value = "fixture" },
            new StringProperty { Key = "windowHandleEvidence", Value = hwnd },
            new StringProperty { Key = "ownerWindowHandleEvidence", Value = owner },
            new StringProperty { Key = "className", Value = "ProOwnerWin" },
            new StringProperty { Key = "isVisible", Value = "True" }
        }
    };
}
