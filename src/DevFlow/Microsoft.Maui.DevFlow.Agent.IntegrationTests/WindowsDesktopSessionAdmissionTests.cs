using System.Diagnostics;
using System.Text.Json;
using Microsoft.Maui.DevFlow.Agent.IntegrationTests.Fixtures;

namespace Microsoft.Maui.DevFlow.Agent.IntegrationTests;

public sealed class WindowsDesktopSessionAdmissionTests
{
    static readonly DateTimeOffset ObservationTime = new(2026, 8, 2, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ActiveUnlockedSession_IsAdmitted()
    {
        var probe = new WindowsDesktopSessionAdmissionProbe(
            static () => true,
            static () => 42,
            new FakeWtsSessionApi
            {
                ConnectionState = WindowsWtsConnectionState.Active,
                DesktopLockState = WindowsDesktopLockState.Unlocked,
            },
            static () => ObservationTime);

        var result = probe.Probe();

        Assert.True(result.IsAllowed);
        Assert.Equal(42, result.SessionId);
        Assert.Equal(WindowsWtsConnectionState.Active, result.WtsConnectionState);
        Assert.Equal(WindowsDesktopLockState.Unlocked, result.DesktopLockState);
        Assert.Equal(WindowsDesktopSessionAdmissionResult.Allowed, result.Result);
        Assert.Equal(ObservationTime, result.TimestampUtc);
        Assert.Equal("active-unlocked-desktop", result.Reason);
    }

    [Fact]
    public void DisconnectedSession_RejectsEvenWhenUserInteractiveWouldBeTrue_AndStartsNoProcess()
    {
        const bool userInteractiveSignal = true;
        Assert.True(userInteractiveSignal);

        var probe = new WindowsDesktopSessionAdmissionProbe(
            static () => true,
            static () => 42,
            new FakeWtsSessionApi
            {
                ConnectionState = WindowsWtsConnectionState.Disconnected,
                DesktopLockState = WindowsDesktopLockState.Unlocked,
            },
            static () => ObservationTime);
        var processStarter = new CountingProcessStarter();
        var gate = new WindowsDesktopSessionLaunchGate(probe, processStarter);

        var admission = gate.Admit();
        var exception = Assert.Throws<PlatformFlowLifecycleException>(
            () => gate.Start(new ProcessStartInfo("DevFlow.Sample.exe"), admission));

        Assert.Equal(PlatformFlowLifecycleFailureKind.Infrastructure, exception.Kind);
        Assert.Equal(WindowsDesktopSessionAdmissionResult.Rejected, admission.Result);
        Assert.Equal("wts-connection-state-disconnected", admission.Reason);
        Assert.Equal(0, processStarter.StartCalls);
    }

    [Fact]
    public void UnavailableSessionState_FailsClosedBeforeProcessStart()
    {
        var probe = new WindowsDesktopSessionAdmissionProbe(
            static () => true,
            static () => 42,
            new FakeWtsSessionApi { ConnectionStateAvailable = false },
            static () => ObservationTime);
        var processStarter = new CountingProcessStarter();
        var gate = new WindowsDesktopSessionLaunchGate(probe, processStarter);

        var admission = gate.Admit();
        var exception = Assert.Throws<PlatformFlowLifecycleException>(
            () => gate.Start(new ProcessStartInfo("DevFlow.Sample.exe"), admission));

        Assert.Equal(PlatformFlowLifecycleFailureKind.Infrastructure, exception.Kind);
        Assert.Equal(WindowsDesktopSessionAdmissionResult.Unavailable, admission.Result);
        Assert.Equal("wts-connection-state-unavailable", admission.Reason);
        Assert.Equal(0, processStarter.StartCalls);
    }

    [Fact]
    public async Task SessionDiagnostics_ContainBoundedRedactedAdmissionAndProcessExitFacts()
    {
        var root = Path.Combine(
            AppFixtureBase.FindRepoRoot(),
            "artifacts",
            "TestResults",
            "windows-session-diagnostics",
            Guid.NewGuid().ToString("N"));
        var admission = new WindowsDesktopSessionAdmission(
            42,
            WindowsWtsConnectionState.Disconnected,
            null,
            WindowsDesktopSessionAdmissionResult.Rejected,
            ObservationTime,
            "username=Alice raw quser output must never be written");
        try
        {
            var path = await WindowsDesktopSessionDiagnostics.WriteAsync(root, admission);

            Assert.EndsWith(
                Path.Combine("host-diagnostics", "windows-session.json"),
                path,
                StringComparison.OrdinalIgnoreCase);
            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("Alice", json, StringComparison.Ordinal);
            Assert.DoesNotContain("quser", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("username", json, StringComparison.OrdinalIgnoreCase);

            using var document = JsonDocument.Parse(json);
            var record = document.RootElement;
            Assert.Equal(42, record.GetProperty("sessionId").GetInt32());
            Assert.Equal("disconnected", record.GetProperty("wtsConnectionState").GetString());
            Assert.Equal("unavailable", record.GetProperty("desktopLockState").GetString());
            Assert.Equal("rejected", record.GetProperty("admissionResult").GetString());
            Assert.Equal(ObservationTime.ToString("O"), record.GetProperty("admissionTimestampUtc").GetString());
            Assert.Equal("redacted", record.GetProperty("reason").GetString());

            var processExitFacts = new Dictionary<string, string?>();
            WindowsDesktopSessionDiagnostics.AddProcessExitFacts(processExitFacts, admission);
            Assert.Equal("42", processExitFacts["sessionId"]);
            Assert.Equal("disconnected", processExitFacts["wtsConnectionState"]);
            Assert.Equal("rejected", processExitFacts["admissionResult"]);
            Assert.Equal(ObservationTime.ToString("O"), processExitFacts["admissionTimestampUtc"]);
            Assert.Equal("redacted", processExitFacts["admissionReason"]);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    sealed class FakeWtsSessionApi : IWindowsWtsSessionApi
    {
        public bool ConnectionStateAvailable { get; init; } = true;
        public bool DesktopLockStateAvailable { get; init; } = true;
        public WindowsWtsConnectionState ConnectionState { get; init; } = WindowsWtsConnectionState.Active;
        public WindowsDesktopLockState DesktopLockState { get; init; } = WindowsDesktopLockState.Unlocked;

        public bool TryGetConnectionState(int sessionId, out WindowsWtsConnectionState connectionState)
        {
            connectionState = ConnectionState;
            return ConnectionStateAvailable;
        }

        public bool TryGetDesktopLockState(int sessionId, out WindowsDesktopLockState desktopLockState)
        {
            desktopLockState = DesktopLockState;
            return DesktopLockStateAvailable;
        }
    }

    sealed class CountingProcessStarter : IWindowsWinUiProcessStarter
    {
        public int StartCalls { get; private set; }

        public Process? Start(ProcessStartInfo startInfo)
        {
            StartCalls++;
            return null;
        }
    }
}
