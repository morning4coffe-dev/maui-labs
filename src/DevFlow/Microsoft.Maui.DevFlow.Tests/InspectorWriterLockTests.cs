using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.Cli.DevFlow.Inspector;

namespace Microsoft.Maui.DevFlow.Tests;

public class InspectorWriterLockTests
{
    [Fact]
    public void FirstLeaseAcquires_SecondIsDenied_ForceTakeoverTransfersOwnership()
    {
        var leases = new MutationLeaseRegistry();

        var first = leases.Control("agent", "claim", "A", "web", "Browser", force: false);
        Assert.True(first.YouHold);
        Assert.False(first.HeldByOther);

        var second = leases.Control("agent", "claim", "B", "mcp", "MCP", force: false);
        Assert.False(second.YouHold);
        Assert.True(second.HeldByOther);
        Assert.Equal("Browser", second.Label);

        var takeover = leases.Control("agent", "claim", "B", "mcp", "MCP", force: true);
        Assert.True(takeover.YouHold);
        Assert.Equal("MCP", takeover.Label);

        var oldHolder = leases.Control("agent", "status", "A", "web", "Browser", force: false);
        Assert.False(oldHolder.YouHold);
        Assert.True(oldHolder.HeldByOther);
    }

    [Fact]
    public void ReleaseAllowsAnotherLeaseToAcquire()
    {
        var leases = new MutationLeaseRegistry();
        Assert.True(leases.Control("agent", "claim", "A", "web", "Browser", false).YouHold);
        Assert.False(leases.Control("agent", "release", "A", "web", "Browser", false).YouHold);
        Assert.True(leases.Control("agent", "claim", "B", "cli", "CLI", false).YouHold);
    }

    [Fact]
    public async Task ExpiredLease_CanBeReclaimedByTheSameIdentity()
    {
        var leases = new MutationLeaseRegistry(leaseDurationMs: 1_000);
        Assert.True(leases.Control("agent", "claim", "A", "mcp", "MCP", false).YouHold);

        await Task.Delay(1_100);

        Assert.False(leases.Control("agent", "validate", "A", "mcp", "MCP", false).Allowed);
        Assert.True(leases.Control("agent", "claim", "A", "mcp", "MCP", false).YouHold);
    }

    [Theory]
    [InlineData("/api/tap", true)]
    [InlineData("/api/fill", true)]
    [InlineData("/api/setProperty", true)]
    [InlineData("/api/navigate", true)]
    [InlineData("/api/cdp/eval", true)]
    [InlineData("/api/flows/replay", true)]
    [InlineData("/api/flows/record/step", true)]
    [InlineData("/api/checkpoint", false)]
    [InlineData("/api/state", false)]
    [InlineData("/api/getProperty", false)]
    [InlineData("/api/logs", false)]
    [InlineData("/api/control", false)]
    public void IsMutation_GatesOnlyStateChangingPosts(string path, bool mutation)
        => Assert.Equal(mutation, InspectorServer.IsMutation(path));
}
