using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.Cli.DevFlow.Flows;
using Microsoft.Maui.Controls;
using Microsoft.Maui.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Driver;

namespace Microsoft.Maui.DevFlow.Tests;

public class BinaryCompatibilityTests
{
    [Fact]
    public void Pr397Apis_RetainTheirExactOriginalPublicClrSignatures()
    {
        // These are the member references emitted by consumers compiled against PR 397's
        // d255d19a baseline. Wider implementation overloads must not replace them.
        Assert.NotNull(typeof(VisualTreeWalker).GetMethod(
            nameof(VisualTreeWalker.WalkTree),
            [typeof(Application), typeof(int), typeof(int?)]));

        Assert.NotNull(typeof(BrokerServer).GetConstructor(
            [typeof(int), typeof(TimeSpan?), typeof(Action<string>)]));

        Assert.NotNull(typeof(FlowRecorder).GetConstructor(
            [typeof(string), typeof(string), typeof(string), typeof(string)]));

        Assert.NotNull(typeof(FlowReplayer).GetConstructor(
            [typeof(AgentClient), typeof(int), typeof(int)]));
    }
}
