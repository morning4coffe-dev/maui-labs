using Xunit;

namespace Microsoft.Maui.DevFlow.Agent.IntegrationTests;

/// <summary>
/// The QA lanes used to <c>return</c> early when disabled, which reported a pass for a lane that
/// never ran. These tests pin the replacement gate so a disabled lane is skipped with a reason.
/// </summary>
public class FlowQaLaneGateTests
{
    static Func<string, string?> Env(string variable, string? value)
        => name => string.Equals(name, variable, StringComparison.Ordinal) ? value : null;

    [Fact]
    public void AndroidFlowPilot_RequiresExplicitOptIn()
    {
        var readiness = FlowQaLaneGate.AndroidFlowPilot(Env(FlowQaLaneGate.AndroidFlowPilotVariable, null));

        Assert.False(readiness.IsEnabled);
        Assert.Contains(FlowQaLaneGate.AndroidFlowPilotVariable, readiness.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("1 ")]
    public void AndroidFlowPilot_OnlyExactlyOneEnablesTheLane(string value)
    {
        Assert.False(FlowQaLaneGate.AndroidFlowPilot(Env(FlowQaLaneGate.AndroidFlowPilotVariable, value)).IsEnabled);
    }

    [Fact]
    public void AndroidFlowPilot_EnabledWhenRequested()
    {
        var readiness = FlowQaLaneGate.AndroidFlowPilot(Env(FlowQaLaneGate.AndroidFlowPilotVariable, "1"));

        Assert.True(readiness.IsEnabled);
        Assert.Equal("", readiness.Reason);
    }

    [Fact]
    public void WindowsFlowQa_RequiresWindowsHostEvenWhenRequested()
    {
        var readiness = FlowQaLaneGate.WindowsFlowQa(
            Env(FlowQaLaneGate.WindowsFlowQaVariable, "1"),
            isWindowsHost: false);

        Assert.False(readiness.IsEnabled);
        Assert.Contains("Windows", readiness.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsFlowQa_RequiresOptInEvenOnWindows()
    {
        var readiness = FlowQaLaneGate.WindowsFlowQa(
            Env(FlowQaLaneGate.WindowsFlowQaVariable, null),
            isWindowsHost: true);

        Assert.False(readiness.IsEnabled);
        Assert.Contains(FlowQaLaneGate.WindowsFlowQaVariable, readiness.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsFlowQa_EnabledOnWindowsWhenRequested()
        => Assert.True(
            FlowQaLaneGate.WindowsFlowQa(Env(FlowQaLaneGate.WindowsFlowQaVariable, "1"), isWindowsHost: true)
                .IsEnabled);

    [Fact]
    public void AppKitFlowQa_RequiresMacOSHostEvenWhenRequested()
    {
        var readiness = FlowQaLaneGate.AppKitFlowQa(
            Env(FlowQaLaneGate.AppKitFlowQaVariable, "1"),
            isMacOSHost: false);

        Assert.False(readiness.IsEnabled);
        Assert.Contains("macOS", readiness.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AppKitFlowQa_EnabledOnMacOSWhenRequested()
        => Assert.True(
            FlowQaLaneGate.AppKitFlowQa(Env(FlowQaLaneGate.AppKitFlowQaVariable, "1"), isMacOSHost: true).IsEnabled);

    [Fact]
    public void AppleFlowQa_RequiresMacOSHostEvenWhenRequested()
    {
        var readiness = FlowQaLaneGate.AppleFlowQa(
            Env(FlowQaLaneGate.AppleFlowQaVariable, "1"),
            isMacOSHost: false);

        Assert.False(readiness.IsEnabled);
        Assert.Contains("macOS", readiness.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AppleFlowQa_EnabledOnMacOSWhenRequested()
        => Assert.True(
            FlowQaLaneGate.AppleFlowQa(Env(FlowQaLaneGate.AppleFlowQaVariable, "1"), isMacOSHost: true).IsEnabled);

    [Fact]
    public void LanesDoNotShareEnvironmentVariables()
    {
        var variables = new[]
        {
            FlowQaLaneGate.AndroidFlowPilotVariable,
            FlowQaLaneGate.WindowsFlowQaVariable,
            FlowQaLaneGate.AppKitFlowQaVariable,
            FlowQaLaneGate.AppleFlowQaVariable,
        };

        Assert.Equal(variables.Length, variables.Distinct(StringComparer.Ordinal).Count());
    }
}
