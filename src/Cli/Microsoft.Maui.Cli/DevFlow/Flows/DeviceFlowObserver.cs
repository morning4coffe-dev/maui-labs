using Microsoft.Maui.DevFlow.Devices;
using Microsoft.Maui.DevFlow.Testing;
// This namespace declares its own MauiFlow (FlowCompatibility.cs), which would otherwise shadow
// the Testing one the replay seam is defined against.
using TestingFlow = Microsoft.Maui.DevFlow.Testing.MauiFlow;
using TestingStep = Microsoft.Maui.DevFlow.Testing.FlowStep;
using TestingStepResult = Microsoft.Maui.DevFlow.Testing.FlowStepResult;

namespace Microsoft.Maui.Cli.DevFlow.Flows;

/// <summary>
/// Lets the device layer contribute to a flow replay: a recording of the run, and a cause for a
/// step that could not be actioned.
/// <para>
/// This is a decorator rather than a replacement. The existing evidence capture keeps doing
/// exactly what it did; this adds the two things the replay engine structurally cannot obtain,
/// because both require seeing <em>outside</em> the app process.
/// </para>
/// <para>
/// The dependency points inwards on purpose. <c>Microsoft.Maui.DevFlow.Testing</c> is a shipped
/// package and does not reference the device layer; it declares a seam, and the CLI — which owns
/// both — implements it. Adding a project reference the other way would have forced a
/// non-shipping assembly into a public package.
/// </para>
/// </summary>
public sealed class DeviceFlowObserver : IFlowReplayEvidenceCapture
{
    private readonly IFlowReplayEvidenceCapture? _inner;
    private readonly IDeviceSurface _devices;
    private readonly string _deviceId;
    private readonly string? _appPackageId;
    private readonly Action<string>? _onRecording;

    public DeviceFlowObserver(
        IDeviceSurface devices,
        string deviceId,
        IFlowReplayEvidenceCapture? inner = null,
        string? appPackageId = null,
        Action<string>? onRecording = null)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _deviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
        _inner = inner;
        _appPackageId = appPackageId;
        _onRecording = onRecording;
    }

    public Task CaptureOnFailureAsync(
        TestingFlow flow, TestingStep failedStep, TestingStepResult result, CancellationToken cancellationToken)
        => _inner?.CaptureOnFailureAsync(flow, failedStep, result, cancellationToken) ?? Task.CompletedTask;

    /// <summary>
    /// Starts recording the run. A failed test that ships with video of its own failure is the
    /// most useful artifact a replay can produce, because the interesting moment has already
    /// passed by the time anyone reads the report.
    /// </summary>
    public async Task<IAsyncDisposable?> BeginRunAsync(TestingFlow flow, CancellationToken cancellationToken)
    {
        var started = await _devices
            .StartRecordingAsync(_deviceId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // A device that cannot record is not a reason to refuse the run.
        return started.Success ? new RecordingHandle(this) : null;
    }

    /// <summary>
    /// Turns "not visible" into a reason. The replay engine can say a step could not be actioned;
    /// only something outside the app can say a permission dialog was covering it.
    /// </summary>
    public async Task<string?> ExplainFailureAsync(
        TestingFlow flow, TestingStep failedStep, TestingStepResult result, CancellationToken cancellationToken)
    {
        // Only ask when the failure is the kind a foreign window would explain. A selector that
        // matched nothing is an authoring problem, and blaming the environment for it would send
        // the reader somewhere useless.
        if (!IsExplainableByForeground(result))
            return null;

        var foreground = await _devices
            .DescribeForegroundAsync(_deviceId, _appPackageId, cancellationToken)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(foreground)
            ? null
            : $"{foreground} was covering the app, so the step could not reach its target.";
    }

    /// <summary>
    /// Whether something on top of the app could plausibly account for this failure.
    /// <para>
    /// A wrong explanation is worse than none: it sends the reader to investigate an environment
    /// that was fine while the real cause — usually a selector — goes unexamined.
    /// </para>
    /// </summary>
    internal static bool IsExplainableByForeground(TestingStepResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Ok)
            return false;

        var kind = result.FailureKind;
        if (string.IsNullOrWhiteSpace(kind))
            return false;

        return kind.Contains("visible", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("actionable", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("stale", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Stops the recording when the run ends, however it ends.</summary>
    private sealed class RecordingHandle(DeviceFlowObserver owner) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                var stopped = await owner._devices.StopRecordingAsync(owner._deviceId).ConfigureAwait(false);
                if (stopped.Success && !string.IsNullOrWhiteSpace(stopped.Path))
                    owner._onRecording?.Invoke(stopped.Path);
            }
            catch
            {
                // Finishing a recording must never change the run's outcome.
            }
        }
    }
}
