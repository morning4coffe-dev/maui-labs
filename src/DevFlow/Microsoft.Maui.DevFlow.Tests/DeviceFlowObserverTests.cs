using Microsoft.Maui.Cli.DevFlow.Flows;
using Microsoft.Maui.DevFlow.Devices;
using Microsoft.Maui.DevFlow.Testing;
// Cli.DevFlow.Flows declares its own flow type family (FlowCompatibility.cs), so the Testing types
// the replay seam is defined against must be named explicitly.
using MauiFlow = Microsoft.Maui.DevFlow.Testing.MauiFlow;
using FlowStep = Microsoft.Maui.DevFlow.Testing.FlowStep;
using FlowStepResult = Microsoft.Maui.DevFlow.Testing.FlowStepResult;

namespace Microsoft.Maui.DevFlow.Tests;

/// <summary>
/// The device layer's contribution to a flow replay: a recording of the run, and a cause for a
/// step that could not be actioned.
/// <para>
/// The behaviour worth defending is restraint. An explanation is only offered for failures a
/// foreign window could actually account for, because sending a reader to investigate a healthy
/// environment while the real cause — usually a selector — goes unexamined is worse than saying
/// nothing.
/// </para>
/// </summary>
public class DeviceFlowObserverTests
{
    private sealed class StubSurface : IDeviceSurface
    {
        public bool RecordingSupported { get; set; } = true;
        public string? Foreground { get; set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public string? StoppedPath { get; set; } = "/tmp/run.mp4";

        public Task<DeviceHostHealth> GetHealthAsync(CancellationToken ct = default) =>
            Task.FromResult(new DeviceHostHealth { Availability = DeviceHostAvailability.Available });
        public Task<IReadOnlyList<DeviceTarget>?> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeviceTarget>?>([]);
        public Task<DeviceTarget?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<DeviceTarget?>(null);
        public Task<DeviceOperationResult> BootAsync(string id, CancellationToken ct = default) => Task.FromResult(DeviceOperationResult.Ok());
        public Task<DeviceOperationResult> ShutdownAsync(string id, CancellationToken ct = default) => Task.FromResult(DeviceOperationResult.Ok());
        public Task<DeviceOperationResult> TapAsync(string id, DevicePoint p, CancellationToken ct = default) => Task.FromResult(DeviceOperationResult.Ok());
        public Task<byte[]?> ScreenshotAsync(string id, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

        public Task<DeviceOperationResult> StartRecordingAsync(string id, int timeoutSeconds = 180, CancellationToken ct = default)
        {
            StartCalls++;
            return Task.FromResult(RecordingSupported
                ? DeviceOperationResult.Ok()
                : DeviceOperationResult.Unsupported("screen recording"));
        }

        public Task<DeviceRecordingResult> StopRecordingAsync(string id, CancellationToken ct = default)
        {
            StopCalls++;
            return Task.FromResult(new DeviceRecordingResult(StoppedPath is not null, StoppedPath));
        }

        public Task<string?> DescribeForegroundAsync(string id, string? appPackageId = null, CancellationToken ct = default) =>
            Task.FromResult(Foreground);
    }

    private static FlowStepResult Failed(string kind) =>
        new() { Ok = false, FailureKind = kind, Error = "step failed" };

    [Fact]
    public async Task RecordsTheRun_AndStopsWhenItEnds()
    {
        // A failed test that ships with video of its own failure is the most useful artifact a
        // replay can produce: the interesting moment has passed by the time anyone reads a report.
        var surface = new StubSurface();
        string? recorded = null;
        var observer = new DeviceFlowObserver(surface, "ios:A1B2", onRecording: p => recorded = p);

        var handle = await observer.BeginRunAsync(new MauiFlow(), CancellationToken.None);
        Assert.NotNull(handle);
        await handle!.DisposeAsync();

        Assert.Equal(1, surface.StartCalls);
        Assert.Equal(1, surface.StopCalls);
        Assert.Equal("/tmp/run.mp4", recorded);
    }

    [Fact]
    public async Task ADeviceThatCannotRecordDoesNotBlockTheRun()
    {
        var surface = new StubSurface { RecordingSupported = false };
        var observer = new DeviceFlowObserver(surface, "ios:A1B2");

        var handle = await observer.BeginRunAsync(new MauiFlow(), CancellationToken.None);

        Assert.Null(handle);
        Assert.Equal(0, surface.StopCalls);
    }

    [Fact]
    public async Task ExplainsAFailureAForeignWindowCouldCause()
    {
        var surface = new StubSurface { Foreground = "A system permission dialog" };
        var observer = new DeviceFlowObserver(surface, "ios:A1B2");

        var cause = await observer.ExplainFailureAsync(
            new MauiFlow(), new FlowStep(), Failed("not_visible"), CancellationToken.None);

        Assert.NotNull(cause);
        Assert.Contains("permission dialog", cause, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StaysSilentWhenTheAppIsFrontmost()
    {
        // Nothing was covering the app, so the failure is the app's own. Inventing environmental
        // context here would be a confident lie.
        var surface = new StubSurface { Foreground = null };
        var observer = new DeviceFlowObserver(surface, "ios:A1B2");

        var cause = await observer.ExplainFailureAsync(
            new MauiFlow(), new FlowStep(), Failed("not_visible"), CancellationToken.None);

        Assert.Null(cause);
    }

    [Theory]
    [InlineData("not_found")]
    [InlineData("ambiguous")]
    [InlineData("validation")]
    public async Task DoesNotBlameTheEnvironmentForAnAuthoringProblem(string kind)
    {
        // A selector that matched nothing, or matched too much, is not something a dialog caused.
        // Offering one would send the reader to investigate an environment that was fine.
        var surface = new StubSurface { Foreground = "A system permission dialog" };
        var observer = new DeviceFlowObserver(surface, "ios:A1B2");

        var cause = await observer.ExplainFailureAsync(
            new MauiFlow(), new FlowStep(), Failed(kind), CancellationToken.None);

        Assert.Null(cause);
    }

    [Fact]
    public async Task NeverExplainsASuccessfulStep()
    {
        var surface = new StubSurface { Foreground = "A share sheet" };
        var observer = new DeviceFlowObserver(surface, "ios:A1B2");

        var cause = await observer.ExplainFailureAsync(
            new MauiFlow(), new FlowStep(), new FlowStepResult { Ok = true }, CancellationToken.None);

        Assert.Null(cause);
    }

    [Theory]
    [InlineData("not_visible", true)]
    [InlineData("not_actionable", true)]
    [InlineData("timeout", true)]
    [InlineData("stale_element", true)]
    [InlineData("not_found", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ClassifiesWhichFailuresAForegroundCouldExplain(string? kind, bool explainable)
    {
        var result = new FlowStepResult { Ok = false, FailureKind = kind };

        Assert.Equal(explainable, DeviceFlowObserver.IsExplainableByForeground(result));
    }

    [Fact]
    public async Task DelegatesEvidenceCaptureToTheInnerObserver()
    {
        // A decorator, not a replacement: the existing evidence path keeps working untouched.
        var inner = new RecordingCapture();
        var observer = new DeviceFlowObserver(new StubSurface(), "ios:A1B2", inner);

        await observer.CaptureOnFailureAsync(
            new MauiFlow(), new FlowStep(), Failed("not_visible"), CancellationToken.None);

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task WorksWithNoInnerObserver()
    {
        var observer = new DeviceFlowObserver(new StubSurface(), "ios:A1B2");

        var exception = await Record.ExceptionAsync(() => observer.CaptureOnFailureAsync(
            new MauiFlow(), new FlowStep(), Failed("not_visible"), CancellationToken.None));

        Assert.Null(exception);
    }

    private sealed class RecordingCapture : IFlowReplayEvidenceCapture
    {
        public int Calls { get; private set; }

        public Task CaptureOnFailureAsync(MauiFlow flow, FlowStep failedStep, FlowStepResult result, CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void TheSeamDefaultsToDoingNothing()
    {
        // An existing implementer that predates these members must keep compiling and keep
        // behaving identically, which is why they are defaulted rather than required.
        IFlowReplayEvidenceCapture legacy = new RecordingCapture();

        Assert.Null(legacy.BeginRunAsync(new MauiFlow(), CancellationToken.None).Result);
        Assert.Null(legacy.ExplainFailureAsync(new MauiFlow(), new FlowStep(), Failed("not_visible"), CancellationToken.None).Result);
    }
}
