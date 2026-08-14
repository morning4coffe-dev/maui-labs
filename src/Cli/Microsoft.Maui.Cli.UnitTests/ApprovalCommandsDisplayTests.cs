using Microsoft.Maui.Cli.DevFlow.Approvals;
using Xunit;

namespace Microsoft.Maui.Cli.UnitTests;

/// <summary>
/// The approval review surface prints text written by the very agent whose request is under review.
/// The broker bounds that text's length but does not strip control characters, so the CLI has to.
/// </summary>
public class ApprovalCommandsDisplayTests
{
    [Fact]
    public void Display_KeepsOrdinaryReviewTextIntact()
    {
        const string intent = "Tap the Save button on /orders/new, then assert the toast text.";

        Assert.Equal(intent, ApprovalCommands.Display(intent));
        Assert.Equal("actions=[run], sideEffects=[run], maxActions=1", ApprovalCommands.Display("actions=[run], sideEffects=[run], maxActions=1"));
        Assert.Equal(string.Empty, ApprovalCommands.Display(null));
        Assert.Equal(string.Empty, ApprovalCommands.Display(string.Empty));
    }

    [Fact]
    public void Display_CollapsesNewlinesSoAnAgentCannotForgeExtraReviewLines()
    {
        var forged = ApprovalCommands.Display(
            "read-only tree inspection\nKind             : exploration\nRequested scope  : actions=[tap]");

        Assert.DoesNotContain('\n', forged);
        Assert.DoesNotContain('\r', forged);
        Assert.Contains("read-only tree inspection", forged, StringComparison.Ordinal);
        Assert.Contains("exploration", forged, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_StripsAnsiControlSoAnAgentCannotEraseTheRealScopeLine()
    {
        var forged = ApprovalCommands.Display("harmless\u001b[2K\rKind : exploration");

        Assert.DoesNotContain('\u001b', forged);
        Assert.DoesNotContain('\r', forged);
        Assert.StartsWith("harmless", forged, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData('\u0000')]
    [InlineData('\u0007')]
    [InlineData('\u0008')]
    [InlineData('\u0009')]
    [InlineData('\u007f')]
    [InlineData('\u009b')]
    [InlineData('\u061c')]
    [InlineData('\u200b')]
    [InlineData('\u200e')]
    [InlineData('\u202e')]
    [InlineData('\u2066')]
    [InlineData('\u2028')]
    [InlineData('\u2029')]
    [InlineData('\ufeff')]
    public void Display_ReplacesEveryControlOrDirectionOverrideCodePoint(char hostile)
    {
        var rendered = ApprovalCommands.Display($"a{hostile}b");

        Assert.Equal("a\ufffdb", rendered);
    }

    [Fact]
    public void Display_ClampsRunawayTextToOneBoundedLine()
    {
        var rendered = ApprovalCommands.Display(new string('x', 5_000));

        Assert.Equal(243, rendered.Length);
        Assert.EndsWith("...", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_DoesNotSplitSurrogatePairsWhenClamping()
    {
        var rendered = ApprovalCommands.Display(string.Concat(Enumerable.Repeat("\U0001F600", 5_000)));

        Assert.EndsWith("...", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain('\ufffd', rendered);

        var clamped = rendered[..^3];
        for (var index = 0; index < clamped.Length; index++)
        {
            if (char.IsHighSurrogate(clamped[index]))
            {
                Assert.True(index + 1 < clamped.Length && char.IsLowSurrogate(clamped[index + 1]));
                index++;
                continue;
            }
            Assert.False(char.IsLowSurrogate(clamped[index]));
        }
    }
}
