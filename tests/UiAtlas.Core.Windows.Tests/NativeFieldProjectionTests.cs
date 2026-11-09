using Google.Protobuf;
using UiAtlas.Core.Recording.Windows;
using UIMaps.Protocol.V1;

namespace UiAtlas.Core.Windows.Tests;

public sealed class NativeFieldProjectionTests
{
    [Fact]
    public void UnreadValueIsDifferentFromAnObservedEmptyValue()
    {
        var unread = Node();
        var empty = unread.Clone();
        empty.ValueEvidence = "";
        empty.SelectionEvidence = "";

        var unavailable = NativeRecorderCapture.ProjectControl(unread, 100);
        var observedEmpty = NativeRecorderCapture.ProjectControl(empty, 100);

        Assert.False(unavailable.ValueAvailable);
        Assert.Null(unavailable.Value);
        Assert.False(unavailable.SelectionAvailable);
        Assert.Null(unavailable.SelectionValue);
        Assert.True(observedEmpty.ValueAvailable);
        Assert.Equal("", observedEmpty.Value);
        Assert.True(observedEmpty.SelectionAvailable);
        Assert.Equal("", observedEmpty.SelectionValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public void UnknownOrPasswordContentIsNotProjected(bool? password)
    {
        var node = Node();
        if (password is { } value) node.IsPassword = value;
        else node.ClearIsPassword();
        node.ValueEvidence = "private text";
        node.SelectionEvidence = "private selection";
        node.ToggleState = 1;
        var original = node.ToByteArray();

        var projected = NativeRecorderCapture.ProjectControl(node, 100);

        Assert.Equal(password, projected.IsPassword);
        Assert.False(projected.ValueAvailable);
        Assert.Null(projected.Value);
        Assert.False(projected.SelectionAvailable);
        Assert.Null(projected.SelectionValue);
        Assert.Null(projected.ToggleState);
        Assert.Equal(original, node.ToByteArray());
    }

    [Fact]
    public void KnownNonPasswordContentPreservesEmptyAndFalseState()
    {
        var node = Node();
        node.IsReadOnly = false;
        node.ValueEvidence = "47";
        node.SelectionEvidence = "Reserved";
        node.ToggleState = 0;
        node.HasKeyboardFocus = true;
        node.IsSelected = true;

        var projected = NativeRecorderCapture.ProjectControl(node, 100);

        Assert.False(projected.IsPassword);
        Assert.False(projected.IsReadOnly);
        Assert.Equal("47", projected.Value);
        Assert.True(projected.ValueAvailable);
        Assert.Equal("Reserved", projected.SelectionValue);
        Assert.True(projected.SelectionAvailable);
        Assert.Equal("0", projected.ToggleState);
        Assert.True(projected.HasKeyboardFocus);
        Assert.True(projected.IsSelected);
    }

    [Theory]
    [InlineData(false, "Not selected")]
    [InlineData(true, "Selected")]
    public void RadioSelectionDistinguishesObservedFalseFromUnavailable(bool selected, string expected)
    {
        var node = Node();
        node.ControlType = "RadioButton";
        node.IsSelected = selected;

        var projected = NativeRecorderCapture.ProjectControl(node, 100);

        Assert.True(projected.SelectionAvailable);
        Assert.Equal(expected, projected.SelectionValue);
        Assert.False(projected.ValueAvailable);
        node.ClearIsSelected();
        Assert.False(NativeRecorderCapture.ProjectControl(node, 100).SelectionAvailable);
        node.IsSelected = selected;
        node.ClearIsPassword();
        Assert.False(NativeRecorderCapture.ProjectControl(node, 100).SelectionAvailable);
    }

    [Fact]
    public void MissingReadOnlyAndRuntimeEvidenceRemainUnknown()
    {
        var node = Node();
        node.RuntimeIdEvidence.Clear();

        var projected = NativeRecorderCapture.ProjectControl(node, null);

        Assert.Null(projected.IsReadOnly);
        Assert.Null(projected.ProviderRuntimeId);
        Assert.Equal(0, projected.WindowHwnd);
        Assert.Null(projected.QueryWindowHwnd);
        Assert.Equal("unverified", projected.ScopeStatus);
    }

    [Fact]
    public void ProviderRuntimeIdentityAndWindowScopeRemainSeparateFromPresentationIds()
    {
        var node = Node();

        var first = NativeRecorderCapture.ProjectControl(node, 100);
        var second = NativeRecorderCapture.ProjectControl(node, 200);

        Assert.Equal("evidence-node", first.RuntimeId);
        Assert.Equal("evidence-parent", first.ParentRuntimeId);
        Assert.Equal("42.-7.9", first.ProviderRuntimeId);
        Assert.Equal("uia-cached", first.ProviderId);
        Assert.Equal("reservation-number", first.AutomationId);
        Assert.Equal("ControlType.Edit", first.ControlType);
        Assert.Equal(100, first.WindowHwnd);
        Assert.Equal(100, first.QueryWindowHwnd);
        Assert.Equal(555, first.NativeWindowHwnd);
        Assert.Equal(first.ProviderRuntimeId, second.ProviderRuntimeId);
        Assert.NotEqual(first.QueryWindowHwnd, second.QueryWindowHwnd);
    }

    [Fact]
    public void PresentationBoundsContentWithoutChangingOriginalEvidence()
    {
        var node = Node();
        node.ValueEvidence = new string('v', 5000);
        node.SelectionEvidence = new string('s', 5000);
        var original = node.ToByteArray();

        var projected = NativeRecorderCapture.ProjectControl(node, 100);

        Assert.Equal(4096, projected.Value!.Length);
        Assert.Equal(4096, projected.SelectionValue!.Length);
        Assert.True(projected.ValueAvailable);
        Assert.True(projected.SelectionAvailable);
        Assert.Equal(original, node.ToByteArray());
    }

    [Fact]
    public void ExplicitDefaultPresenceSurvivesProtocolRoundTrip()
    {
        // The existing field numbers and wire types are retained. These exact
        // bytes encode false/empty observations, including the additive fields.
        byte[] bytes = [0x68, 0x00, 0x72, 0x00, 0xf0, 0x01, 0x00, 0xfa, 0x01, 0x00];
        var parsed = AccessibilityNodeRecord.Parser.ParseFrom(bytes);

        Assert.True(parsed.HasIsPassword);
        Assert.False(parsed.IsPassword);
        Assert.True(parsed.HasValueEvidence);
        Assert.Equal("", parsed.ValueEvidence);
        Assert.True(parsed.HasIsReadOnly);
        Assert.False(parsed.IsReadOnly);
        Assert.True(parsed.HasSelectionEvidence);
        Assert.Equal("", parsed.SelectionEvidence);
        Assert.Equal(bytes, parsed.ToByteArray());
        var projected = NativeRecorderCapture.ProjectControl(parsed, 100);
        Assert.True(projected.ValueAvailable);
        Assert.Equal("", projected.Value);
    }

    [Fact]
    public void LegacyDefaultOmissionDoesNotPretendAnUnreadValueWasEmpty()
    {
        // An old proto3 writer omitted false is_password and empty value_evidence.
        // A non-empty legacy value at field14 still parses, but lacks permission
        // to treat the unknown password status as an explicit non-password value.
        byte[] bytes = [0x72, 0x03, (byte)'o', (byte)'l', (byte)'d'];
        var parsed = AccessibilityNodeRecord.Parser.ParseFrom(bytes);

        Assert.False(parsed.HasIsPassword);
        Assert.True(parsed.HasValueEvidence);
        Assert.Equal("old", parsed.ValueEvidence);
        var projected = NativeRecorderCapture.ProjectControl(parsed, 100);
        Assert.Null(projected.IsPassword);
        Assert.False(projected.ValueAvailable);
        Assert.Null(projected.Value);

        var absent = AccessibilityNodeRecord.Parser.ParseFrom(Array.Empty<byte>());
        Assert.False(absent.HasIsPassword);
        Assert.False(absent.HasValueEvidence);
        Assert.False(absent.HasIsReadOnly);
        Assert.False(absent.HasSelectionEvidence);
    }

    private static AccessibilityNodeRecord Node()
    {
        var node = new AccessibilityNodeRecord
        {
            AccessibilityEvidenceId = "evidence-node", ParentEvidenceId = "evidence-parent",
            AutomationId = "reservation-number", Name = "Reservation number", ControlType = "Edit",
            ClassName = "Edit", ProviderId = "uia-cached", NativeWindowHandleEvidence = "555",
            IsEnabled = true, IsPassword = false,
            BoundsPx = new RectPx { X = 10, Y = 20, Width = 100, Height = 25 }
        };
        node.RuntimeIdEvidence.AddRange([42, -7, 9]);
        return node;
    }
}
