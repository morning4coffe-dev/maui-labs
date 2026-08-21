using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.Cli.DevFlow.Execution;
using Microsoft.Maui.DevFlow.Driver;
using Xunit;

namespace Microsoft.Maui.Cli.UnitTests;

/// <summary>
/// Readiness is not registration. These pin the difference, because conflating them made
/// <c>flow run</c> fail reproducibly on a healthy app that was merely slow to start.
/// </summary>
public class ExactAgentBindingReadinessTests
{
    private static ExactAgentBindingExpectation Expectation() => new()
    {
        SessionId = "session-1",
        TargetFramework = "net10.0-android",
        Platform = "android",
        PlatformAliases = ["android"],
        PackageId = "com.example.app",
    };

    private static AgentStatus Status(bool running) => new()
    {
        Running = running,
        App = new AppDescriptor { PackageId = "com.example.app" },
    };

    private static ExactAgentBindingResolver Resolver() => new(
        _ => Task.FromResult<AgentRegistration[]?>([]),
        pollInterval: TimeSpan.Zero);

    [Fact]
    public async Task WaitForLiveStatus_AgentThatBecomesReadyLate_Succeeds()
    {
        // The exact shape observed on a freshly installed Debug build: registered and forwarded,
        // but not yet answering "running" when the first probe lands.
        var probes = 0;
        var status = await Resolver().WaitForLiveStatusAsync(
            () =>
            {
                probes++;
                return Task.FromResult<AgentStatus?>(probes < 3 ? null : Status(running: true));
            },
            Expectation(),
            TimeSpan.FromSeconds(30));

        Assert.True(status.Running);
        Assert.True(probes >= 3);
    }

    [Fact]
    public async Task WaitForLiveStatus_AgentThatAnswersButNeverRuns_ReportsNotRunning()
    {
        var error = await Assert.ThrowsAsync<FlowExecutionException>(() =>
            Resolver().WaitForLiveStatusAsync(
                () => Task.FromResult<AgentStatus?>(Status(running: false)),
                Expectation(),
                TimeSpan.Zero));

        Assert.Equal("agent-not-running", error.Code);
    }

    [Fact]
    public async Task WaitForLiveStatus_AgentThatNeverAnswers_ReportsNotReachable()
    {
        // A distinct code, because "never answered" points at forwarding and ports while
        // "answered but not running" points at the app, and one message for both costs a
        // reproduction to tell apart.
        var error = await Assert.ThrowsAsync<FlowExecutionException>(() =>
            Resolver().WaitForLiveStatusAsync(
                () => Task.FromResult<AgentStatus?>(null),
                Expectation(),
                TimeSpan.Zero));

        Assert.Equal("agent-not-reachable", error.Code);
    }

    [Fact]
    public async Task WaitForLiveStatus_ReadThatThrows_IsTreatedAsNotYetReady()
    {
        var probes = 0;
        var status = await Resolver().WaitForLiveStatusAsync(
            () =>
            {
                probes++;
                if (probes < 3)
                    throw new HttpRequestException("connection refused");
                return Task.FromResult<AgentStatus?>(Status(running: true));
            },
            Expectation(),
            TimeSpan.FromSeconds(30));

        Assert.True(status.Running);
    }
}
