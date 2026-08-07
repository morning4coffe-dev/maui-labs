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

    /// <summary>All virtual devices known to the host. Empty when no host is installed.</summary>
    Task<IReadOnlyList<DeviceTarget>> ListAsync(CancellationToken cancellationToken = default);

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
}

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
