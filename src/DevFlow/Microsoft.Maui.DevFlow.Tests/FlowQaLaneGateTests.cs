using Xunit;

// The gate source is shared into this project (see the linked FlowQaLaneGate.cs compile item in
// Microsoft.Maui.DevFlow.Tests.csproj) because Arcade never executes the integration-test project
// that owns it. The namespace matches the shared source so the internal gate stays reachable.
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

    // The four attributes are the only part of the gate the suites actually consume. A mis-wire
    // (say WindowsFlowQaFactAttribute asking the AppKit lane) compiles cleanly and then silently
    // skips or silently runs the wrong lane, so pin each attribute to its own lane.
    static string? SkipFor(FlowQaLaneReadiness readiness) => readiness.IsEnabled ? null : readiness.Reason;

    [Fact]
    public void AndroidFlowPilotFact_SkipsExactlyWhenTheAndroidLaneIsDisabled()
        => Assert.Equal(SkipFor(FlowQaLaneGate.AndroidFlowPilot()), new AndroidFlowPilotFactAttribute().Skip);

    [Fact]
    public void WindowsFlowQaFact_SkipsExactlyWhenTheWindowsLaneIsDisabled()
        => Assert.Equal(SkipFor(FlowQaLaneGate.WindowsFlowQa()), new WindowsFlowQaFactAttribute().Skip);

    [Fact]
    public void AppKitFlowQaFact_SkipsExactlyWhenTheAppKitLaneIsDisabled()
        => Assert.Equal(SkipFor(FlowQaLaneGate.AppKitFlowQa()), new AppKitFlowQaFactAttribute().Skip);

    [Fact]
    public void AppleFlowQaFact_SkipsExactlyWhenTheAppleLaneIsDisabled()
        => Assert.Equal(SkipFor(FlowQaLaneGate.AppleFlowQa()), new AppleFlowQaFactAttribute().Skip);

    // Every lane reason is distinct on every host, which is what makes the four assertions above
    // able to tell the lanes apart rather than all matching the same message.
    [Fact]
    public void EveryDisabledLaneReportsItsOwnReason()
    {
        var reasons = new[]
        {
            FlowQaLaneGate.AndroidFlowPilot(_ => null).Reason,
            FlowQaLaneGate.WindowsFlowQa(_ => null, isWindowsHost: false).Reason,
            FlowQaLaneGate.AppKitFlowQa(_ => null, isMacOSHost: false).Reason,
            FlowQaLaneGate.AppleFlowQa(_ => null, isMacOSHost: false).Reason,
            FlowQaLaneGate.WindowsFlowQa(_ => null, isWindowsHost: true).Reason,
            FlowQaLaneGate.AppKitFlowQa(_ => null, isMacOSHost: true).Reason,
            FlowQaLaneGate.AppleFlowQa(_ => null, isMacOSHost: true).Reason,
        };

        Assert.All(reasons, reason => Assert.NotEqual("", reason));
        Assert.Equal(reasons.Length, reasons.Distinct(StringComparer.Ordinal).Count());
    }
}
