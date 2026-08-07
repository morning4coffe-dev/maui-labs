namespace Microsoft.Maui.DevFlow.Devices;

/// <summary>
/// The device layer: what DevFlow can observe and control *around* a running app, as opposed to
/// the in-app agent which sees only what MAUI drew.
/// <para>
/// Implementations must never throw for an unsupported operation. Callers gate on
/// <see cref="DeviceTarget.Capabilities"/> and receive a <see cref="DeviceOperationResult"/>
/// describing why something was refused, so that an absent or partial backend degrades visibly
/// instead of erroring.
/// </para>
/// </summary>
public interface IDeviceSurface
{
    /// <summary>Whether a device host is present and answering. Cheap and safe to call often.</summary>
    Task<DeviceHostHealth> GetHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// All virtual devices known to the host.
    /// <para>
    /// Returns <c>null</c> when the devices could not be enumerated at all, which is deliberately
    /// distinct from an empty list meaning "enumerated, none present". Collapsing the two would
    /// make a transport failure look like the user having deleted their last emulator.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<DeviceTarget>?> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>One device by its provider-qualified id, or <c>null</c> when it is unknown.</summary>
    Task<DeviceTarget?> GetAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>Boots a device and waits until it is ready to be driven.</summary>
    Task<DeviceOperationResult> BootAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>Powers a device off without erasing or deleting it.</summary>
    Task<DeviceOperationResult> ShutdownAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>Taps at a point in device points.</summary>
    Task<DeviceOperationResult> TapAsync(string deviceId, DevicePoint point, CancellationToken cancellationToken = default);

    /// <summary>Captures a PNG screenshot, or <c>null</c> when the device cannot produce one.</summary>
    Task<byte[]?> ScreenshotAsync(string deviceId, CancellationToken cancellationToken = default);

    // ── Environment ────────────────────────────────────────────────────────────────────────────
    // These have default implementations so a backend can adopt them incrementally and a new
    // operation never breaks an existing surface. Refusing by default is the safe direction: a
    // caller that treats "unsupported" as "done" would run in the wrong environment, so the
    // precondition applier stops on a refusal rather than continuing.

    /// <summary>Grants or denies an app permission. State is <c>granted</c> or <c>denied</c>.</summary>
    Task<DeviceOperationResult> SetPermissionAsync(
        string deviceId, string permission, string state, CancellationToken cancellationToken = default)
        => Task.FromResult(DeviceOperationResult.Unsupported("changing permissions"));

    /// <summary>Sets or, when <paramref name="location"/> is null, clears the simulated location.</summary>
    Task<DeviceOperationResult> SetLocationAsync(
        string deviceId, DeviceLocation? location, CancellationToken cancellationToken = default)
        => Task.FromResult(DeviceOperationResult.Unsupported("simulating location"));

    /// <summary>Sets the network condition, such as <c>online</c> or <c>offline</c>.</summary>
    Task<DeviceOperationResult> SetNetworkAsync(
        string deviceId, string condition, CancellationToken cancellationToken = default)
        => Task.FromResult(DeviceOperationResult.Unsupported("changing network conditions"));

    /// <summary>Sets the battery level as a percentage from 0 to 100.</summary>
    Task<DeviceOperationResult> SetBatteryAsync(
        string deviceId, int percentage, CancellationToken cancellationToken = default)
        => Task.FromResult(DeviceOperationResult.Unsupported("setting the battery level"));

    /// <summary>Rotates the display to one of <see cref="DeviceOrientations"/>.</summary>
    Task<DeviceOperationResult> RotateAsync(
        string deviceId, string orientation, CancellationToken cancellationToken = default)
        => Task.FromResult(DeviceOperationResult.Unsupported("rotation"));

    /// <summary>Starts a bounded screen recording. The artifact path is returned by the stop call.</summary>
    Task<DeviceOperationResult> StartRecordingAsync(
        string deviceId, int timeoutSeconds = 180, CancellationToken cancellationToken = default)
        => Task.FromResult(DeviceOperationResult.Unsupported("screen recording"));

    /// <summary>Stops the active recording and returns the path of the finished file, if any.</summary>
    Task<DeviceRecordingResult> StopRecordingAsync(
        string deviceId, CancellationToken cancellationToken = default)
        => Task.FromResult(new DeviceRecordingResult(false, null, "This device does not support screen recording."));

    /// <summary>
    /// Describes what currently owns the screen, when it is not the app under test.
    /// <para>
    /// Returns <c>null</c> when the app is frontmost or the device cannot tell. This is the signal
    /// that turns a "not visible" failure into a cause: a permission dialog, a share sheet, or the
    /// soft keyboard is invisible to the app's own visual tree.
    /// </para>
    /// </summary>
    Task<string?> DescribeForegroundAsync(
        string deviceId, string? appPackageId = null, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}

/// <summary>The outcome of stopping a device recording.</summary>
/// <param name="Success">Whether a recording was finished.</param>
/// <param name="Path">Absolute path of the finished file.</param>
/// <param name="Reason">Why no recording was produced.</param>
public sealed record DeviceRecordingResult(bool Success, string? Path, string? Reason = null);

/// <summary>
/// The outcome of a device operation. Refusal is a normal, describable result rather than an
/// exception, because "this platform cannot do that" is expected on most machines.
/// </summary>
public sealed record DeviceOperationResult
{
    public bool Success { get; init; }

    /// <summary>Why the operation did not succeed, phrased for a human.</summary>
    public string? Reason { get; init; }

    /// <summary>The device record after the operation, when the backend returned one.</summary>
    public DeviceTarget? Device { get; init; }

    public static DeviceOperationResult Ok(DeviceTarget? device = null) =>
        new() { Success = true, Device = device };

    public static DeviceOperationResult Failed(string reason) =>
        new() { Success = false, Reason = reason };

    /// <summary>The standard refusal when no device host is installed.</summary>
    public static DeviceOperationResult NoHost() =>
        Failed("No device host is installed, so device-level control is unavailable.");

    /// <summary>The standard refusal when the device exists but cannot do this.</summary>
    public static DeviceOperationResult Unsupported(string capability) =>
        Failed($"This device does not support {capability}.");
}
