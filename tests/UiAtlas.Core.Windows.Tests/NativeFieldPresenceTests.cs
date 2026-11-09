using System.Runtime.InteropServices;
using Google.Protobuf;
using UIMaps.Protocol.V1;
using UIMaps.Recorder.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class NativeFieldPresenceTests
{
    [Fact]
    public void MergingTwoUnreadValuesDoesNotCreateEmptyEvidence()
    {
        var merged = Merge(Node("uia"), Node("native"));

        Assert.False(merged.HasValueEvidence);
        Assert.False(merged.HasSelectionEvidence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RealEmptyOrWhitespaceValueIsNotReplacedByFallback(string value)
    {
        var preferred = Node("uia");
        preferred.ValueEvidence = value;
        preferred.SelectionEvidence = value;
        var fallback = Node("native");
        fallback.ValueEvidence = "stale fallback";
        fallback.SelectionEvidence = "stale selection";

        var merged = Merge(preferred, fallback);

        Assert.True(merged.HasValueEvidence);
        Assert.Equal(value, merged.ValueEvidence);
        Assert.True(merged.HasSelectionEvidence);
        Assert.Equal(value, merged.SelectionEvidence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("observed fallback")]
    public void ExplicitSafeFallbackPreservesItsPresence(string value)
    {
        var fallback = Node("native");
        fallback.ValueEvidence = value;
        fallback.SelectionEvidence = value;

        var merged = Merge(Node("uia"), fallback);

        Assert.True(merged.HasValueEvidence);
        Assert.Equal(value, merged.ValueEvidence);
        Assert.True(merged.HasSelectionEvidence);
        Assert.Equal(value, merged.SelectionEvidence);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, null)]
    [InlineData(null, null)]
    public void UnknownPrivacyDoesNotAuthorizeFallbackContent(bool? targetPassword, bool? fallbackPassword)
    {
        var target = Node("uia");
        var fallback = Node("native");
        SetPassword(target, targetPassword);
        SetPassword(fallback, fallbackPassword);
        fallback.ValueEvidence = "unverified value";
        fallback.SelectionEvidence = "unverified selection";

        var merged = Merge(target, fallback);

        Assert.False(merged.HasValueEvidence);
        Assert.False(merged.HasSelectionEvidence);
        Assert.Equal(targetPassword.HasValue, merged.HasIsPassword);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PositivePasswordEvidenceClearsMergedContent(bool targetPassword, bool fallbackPassword)
    {
        var target = Node("uia");
        target.IsPassword = targetPassword;
        target.ValueEvidence = "private value";
        target.SelectionEvidence = "private selection";
        target.ToggleState = 1;
        var fallback = Node("native");
        fallback.IsPassword = fallbackPassword;
        fallback.ValueEvidence = "private fallback";

        var merged = Merge(target, fallback);

        Assert.True(merged.IsPassword);
        Assert.False(merged.HasValueEvidence);
        Assert.False(merged.HasSelectionEvidence);
        Assert.False(merged.HasToggleState);
    }

    [Theory]
    [InlineData("Edit", "", 0L, false, false)]
    [InlineData("ComboBox", "", 0L, false, false)]
    [InlineData("Button", "Save", 0L, false, false)]
    [InlineData("Edit", "private", 32L, false, true)]
    [InlineData("Edit", "observed", 0L, true, false)]
    [InlineData("ComboBox", "   ", 0L, true, false)]
    public void NativeCaptionFallbackDoesNotInventEmptyOrNonFieldValues(
        string className, string text, long style, bool available, bool password)
    {
        var packet = Packet();
        var fact = new NativeWindowFact(200, 100, className, text,
            new RectPx { X = 10, Y = 10, Width = 100, Height = 25 }, true, true, 1, style, 0);

        NativeAccessibilitySupplement.Build(packet, [fact], 100, "window", 10);

        var node = Assert.Single(packet.Nodes);
        Assert.Equal(available, node.HasValueEvidence);
        Assert.Equal(password, node.IsPassword);
        if (available) Assert.Equal(text, node.ValueEvidence);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("com-failure")]
    [InlineData("invalid")]
    public void FailedMsaaStateDoesNotClaimKnownNonPasswordOrWritable(string failure)
    {
        var node = Node("msaa");
        node.IsReadOnly = false;

        NativeMsaaProvider.ReadState(node, () => failure switch
        {
            "missing" => null,
            "com-failure" => throw new COMException("Unavailable fixture"),
            _ => "not a state"
        });

        Assert.False(node.HasIsPassword);
        Assert.False(node.HasIsReadOnly);
        Assert.Equal(1, node.PropertyCoverage.Attempted);
        Assert.Equal(0, node.PropertyCoverage.Read);
        Assert.Equal(1, node.PropertyCoverage.Failed + node.PropertyCoverage.Unsupported);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0x40, false, true)]
    [InlineData(0x20000000, true, false)]
    public void ExplicitMsaaStateRetainsFalseAndProtectionFlags(int state, bool password, bool readOnly)
    {
        var node = Node("msaa");

        NativeMsaaProvider.ReadState(node, () => state);

        Assert.True(node.HasIsPassword);
        Assert.Equal(password, node.IsPassword);
        Assert.True(node.HasIsReadOnly);
        Assert.Equal(readOnly, node.IsReadOnly);
        Assert.Equal(1, node.PropertyCoverage.Read);
        Assert.Equal(0, node.PropertyCoverage.Failed);
    }

    private static AccessibilityNodeRecord Merge(AccessibilityNodeRecord target, AccessibilityNodeRecord fallback)
    {
        var uia = Packet(target);
        var native = Packet(fallback);
        var beforeUia = uia.ToByteArray();
        var beforeNative = native.ToByteArray();
        var result = AccessibilityPacketMerger.Merge(uia, native, 10);
        Assert.Equal(beforeUia, uia.ToByteArray());
        Assert.Equal(beforeNative, native.ToByteArray());
        return Assert.Single(result.Nodes);
    }

    private static AccessibilityNodeRecord Node(string id) => new()
    {
        AccessibilityEvidenceId = id, WindowEvidenceId = "window", NativeWindowHandleEvidence = "200",
        ControlType = "Edit", ClassName = "Edit", IsPassword = false,
        BoundsPx = new RectPx { X = 10, Y = 10, Width = 100, Height = 25 }
    };

    private static AccessibilityPacket Packet(params AccessibilityNodeRecord[] nodes)
    {
        var packet = new AccessibilityPacket { Health = ProtocolHealth.Complete() };
        packet.Nodes.AddRange(nodes);
        return packet;
    }

    private static void SetPassword(AccessibilityNodeRecord node, bool? password)
    {
        if (password is { } value) node.IsPassword = value;
        else node.ClearIsPassword();
    }
}
