using Microsoft.Maui.Cli.DevFlow.Broker;
using System.Collections.Concurrent;

namespace Microsoft.Maui.DevFlow.Tests;

public class BrokerInspectorRoutingTests
{
    [Fact]
    public void ResolveInspectorAgent_ExactId_ReturnsAgent()
    {
        var expected = new object();
        var agents = new Dictionary<string, object>
        {
            ["android-agent"] = expected
        };

        var actual = BrokerServer.ResolveInspectorAgent(agents, "android-agent");

        Assert.Same(expected, actual);
    }

    [Fact]
    public void ResolveInspectorAgent_MissingId_DoesNotFallbackToSoleAgent()
    {
        var agents = new Dictionary<string, object>
        {
            ["windows-agent"] = new object()
        };

        var actual = BrokerServer.ResolveInspectorAgent(agents, "android-agent");

        Assert.Null(actual);
    }

    [Fact]
    public void ResolveInspectorAgent_DefaultId_ReturnsSoleAgent()
    {
        var expected = new object();
        var agents = new Dictionary<string, object>
        {
            ["windows-agent"] = expected
        };

        var actual = BrokerServer.ResolveInspectorAgent(agents, "default");

        Assert.Same(expected, actual);
    }

    [Fact]
    public void ResolveInspectorAgent_DefaultId_WithMultipleAgents_ReturnsNull()
    {
        var agents = new Dictionary<string, object>
        {
            ["windows-agent"] = new object(),
            ["android-agent"] = new object()
        };

        var actual = BrokerServer.ResolveInspectorAgent(agents, "default");

        Assert.Null(actual);
    }

    [Fact]
    public async Task ReplaceConnection_ConcurrentReplacements_ReturnOnlySupersededValues()
    {
        var initial = new object();
        var connections = new ConcurrentDictionary<string, object>();
        connections["agent"] = initial;
        var replacements = Enumerable.Range(0, 32).Select(_ => new object()).ToArray();

        var superseded = await Task.WhenAll(replacements.Select(replacement => Task.Run(() =>
            BrokerServer.ReplaceConnection(connections, "agent", replacement))));

        var current = connections["agent"];
        Assert.Contains(current, replacements);
        Assert.DoesNotContain(current, superseded);
        Assert.Equal(replacements.Length, superseded.Distinct().Count());
        Assert.Contains(initial, superseded);
    }
}