using System.Text;
using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Tests;

public sealed class FieldObservationEvidenceTests
{
    [Fact]
    public void SemanticDeduplicationIgnoresNewEvidenceIdsAndKeepsValueChangeProvenance()
    {
        var tracker = new FieldObservationTracker();
        var first = Frame(1, Field("evidence-one", "same"));
        var initial = FieldObservationEvidenceBuilder.Build(first, tracker.Observe(first), 42, Epoch);
        var repeated = Frame(2, Field("evidence-two", "same"));
        var duplicate = FieldObservationEvidenceBuilder.Build(repeated, tracker.Observe(repeated), 42, Epoch);
        Assert.Equal(initial.ContentHash, duplicate.ContentHash);
        Assert.DoesNotContain('\n', duplicate.JsonLine);
        Assert.Equal(Encoding.UTF8.GetByteCount(duplicate.JsonLine), duplicate.ByteCount);

        var changed = Frame(3, Field("evidence-three", "changed"));
        var snapshot = tracker.Observe(changed);
        var change = Assert.Single(Assert.Single(snapshot.Fields).PropertyChanges, change => change.PropertyName == "Value");
        Assert.Equal(1, change.BeforeFrameSequence);
        Assert.Equal(first.TimestampUtc, change.BeforeObservedUtc);
        Assert.NotEqual(initial.ContentHash, FieldObservationEvidenceBuilder.Build(changed, snapshot, 42, Epoch).ContentHash);
    }

    [Fact]
    public void QueryWindowAndAncestorIdentityAreRetainedWithoutAncestorContent()
    {
        var tracker = new FieldObservationTracker();
        var parent = Field("parent", "parent-sensitive-value") with
        { ControlType = "ControlType.Pane", SupportedPatterns = [], AutomationId = "section", Name = "parent-sensitive-name" };
        var child = Field("child", "safe") with
        { WindowHwnd = 0, QueryWindowHwnd = 100, ParentRuntimeId = "parent", ProviderRuntimeId = null };
        var frame = Frame(1, parent, child);
        var evidence = FieldObservationEvidenceBuilder.Build(frame, tracker.Observe(frame), 42, Epoch);
        using var json = JsonDocument.Parse(evidence.JsonLine);
        Assert.Equal(1, json.RootElement.GetProperty("controls").GetArrayLength());
        var observed = json.RootElement.GetProperty("controls")[0].GetProperty("observation");
        Assert.Equal(100, observed.GetProperty("windowHwnd").GetInt64());
        Assert.Equal(1, json.RootElement.GetProperty("ancestors").GetArrayLength());
        Assert.Contains("section", evidence.JsonLine, StringComparison.Ordinal);
        Assert.DoesNotContain("parent-sensitive", evidence.JsonLine, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownPrivacyAndUnavailableContentsAreNotCopiedIntoFieldEvidence()
    {
        var tracker = new FieldObservationTracker();
        var unknown = Field("unknown", "unverified-private-value") with
        { IsPassword = null, Name = "unverified-private-name", OcrText = "unverified-private-ocr" };
        var unavailable = Field("unavailable", "unavailable-private-value") with
        { ValueAvailable = false, ProviderRuntimeId = "other-runtime", AutomationId = "other" };
        var frame = Frame(1, unknown, unavailable);
        var evidence = FieldObservationEvidenceBuilder.Build(frame, tracker.Observe(frame), 42, Epoch);
        Assert.DoesNotContain("private", evidence.JsonLine, StringComparison.Ordinal);
        Assert.False(evidence.Truncated);
    }

    [Fact]
    public void CompleteDisappearanceIsRetainedAsAnEmptyBoundary()
    {
        var tracker = new FieldObservationTracker();
        var visible = Frame(1, Field("visible", "before"));
        var initial = FieldObservationEvidenceBuilder.Build(visible, tracker.Observe(visible), 42, Epoch);
        var absent = Frame(2);
        var boundary = FieldObservationEvidenceBuilder.Build(absent, tracker.Observe(absent), 42, Epoch);
        Assert.NotEqual(initial.ContentHash, boundary.ContentHash);
        using var json = JsonDocument.Parse(boundary.JsonLine);
        Assert.Equal(0, json.RootElement.GetProperty("controls").GetArrayLength());
        Assert.Equal("ok", json.RootElement.GetProperty("automationStatus").GetString());
    }

    [Fact]
    public void OversizedValueIsBoundedAndItsFullDigestRetainsChangeEvidence()
    {
        var tracker = new FieldObservationTracker();
        var first = Frame(1, Field("field", new string('a', 5_000)));
        var evidence = FieldObservationEvidenceBuilder.Build(first, tracker.Observe(first), 42, Epoch);
        Assert.True(evidence.Truncated);
        using var json = JsonDocument.Parse(evidence.JsonLine);
        var item = json.RootElement.GetProperty("controls")[0];
        Assert.Equal(4096, item.GetProperty("observation").GetProperty("value").GetString()!.Length);
        Assert.Equal(64, item.GetProperty("valueSha256").GetString()!.Length);
        var later = Frame(2, Field("field-next", new string('a', 4_999) + "b"));
        var changed = FieldObservationEvidenceBuilder.Build(later, tracker.Observe(later), 42, Epoch);
        Assert.NotEqual(evidence.ContentHash, changed.ContentHash);
    }

    private static readonly DateTimeOffset Epoch = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static FrameObservation Frame(long sequence, params AutomationObservation[] controls) => new(
        sequence, Epoch.AddSeconds(sequence), "",
        new(100, 100, 42, "TestWindow", "Test", new(0, 0, 800, 600), true, true, false, false, 96),
        controls, false, "ok", "manual-field-observation");
    private static AutomationObservation Field(string id, string value) => new(
        id, "", "field", "Name", "ControlType.Edit", "Edit", new(10, 10, 120, 24), true, false, "Win32",
        WindowHwnd: 100, SupportedPatterns: ["Value"], IsPassword: false, IsReadOnly: false,
        Value: value, ValueAvailable: true, ProviderRuntimeId: "stable-runtime", ProviderId: "uia-cached");
}
