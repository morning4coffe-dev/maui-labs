using Microsoft.Maui.DevFlow.Devices;

namespace Microsoft.Maui.Cli.DevFlow.Broker;

/// <summary>
/// A device and the app agent, if any, running inside it.
/// </summary>
/// <param name="Device">The device as the host reports it.</param>
/// <param name="AgentId">The paired agent's broker id, or null when nothing is running on it.</param>
/// <param name="AgentPort">The paired agent's port, so a caller can go straight to it.</param>
/// <param name="MatchConfidence">How the pairing was established.</param>
public sealed record PairedDevice(
    DeviceTarget Device,
    string? AgentId,
    int? AgentPort,
    DeviceMatchConfidence MatchConfidence);

/// <summary>
/// The broker's view of the device layer.
/// <para>
/// The broker is deliberately the single front door. Hosts never talk to a device host directly:
/// two background daemons is fine, but two front doors is what produces duplicate device pickers,
/// duplicate lifecycles, and two competing ideas of which device is selected — which is exactly
/// the "two tools stapled together" outcome this integration exists to avoid.
/// </para>
/// <para>
/// Enumerating devices shells out to platform tooling, so results are cached briefly. The cache is
/// short enough that a device booting or shutting down is noticed promptly, and long enough that a
/// polling Inspector does not spawn a process per refresh.
/// </para>
/// </summary>
public sealed class DeviceRegistry
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(3);

    private readonly IDeviceSurface _surface;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<DeviceTarget> _cached = [];
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private bool _everSucceeded;

    public DeviceRegistry(IDeviceSurface? surface = null, TimeProvider? timeProvider = null)
    {
        _surface = surface ?? CreateDefaultSurface();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Chooses a device backend. Discovery is file-based, so this never launches anything: a
    /// broker that starts on a machine with no device host must not spawn a daemon to find out.
    /// </summary>
    private static IDeviceSurface CreateDefaultSurface() =>
        MobileCanvasHost.IsPresent()
            ? new MobileCanvasDeviceSurface()
            : new NullDeviceSurface();

    /// <summary>Whether a device host is present and answering.</summary>
    public Task<DeviceHostHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        _surface.GetHealthAsync(cancellationToken);

    /// <summary>
    /// All known devices, cached briefly.
    /// <para>
    /// A transient enumeration failure returns the previous result rather than an empty list:
    /// devices blinking out of the Inspector because a host was momentarily busy is a worse
    /// experience than briefly stale state.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<DeviceTarget>> ListAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _time.GetUtcNow() - _cachedAt < CacheLifetime)
            return _cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while we waited for the gate.
            if (!forceRefresh && _time.GetUtcNow() - _cachedAt < CacheLifetime)
                return _cached;

            var devices = await _surface.ListAsync(cancellationToken).ConfigureAwait(false);

            if (devices.Count == 0 && _everSucceeded && _cached.Count > 0)
                return _cached;

            _cached = devices;
            _cachedAt = _time.GetUtcNow();
            if (devices.Count > 0)
                _everSucceeded = true;

            return devices;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Pairs every known device with the agent running inside it.
    /// <para>
    /// This is what makes selecting an app also select its device. An agent that reports no
    /// device identity — every desktop app — simply pairs with nothing.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<PairedDevice>> ListPairedAsync(
        IEnumerable<AgentRegistration> agents,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agents);

        var devices = await ListAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        if (devices.Count == 0)
            return [];

        var candidates = agents
            .Where(a => !string.IsNullOrWhiteSpace(a.DeviceId))
            .Select(a => (Agent: a, Identity: DeviceIdentity.Parse(a.DeviceId)))
            .Where(x => !x.Identity.IsEmpty)
            .ToList();

        var paired = new List<PairedDevice>(devices.Count);
        foreach (var device in devices)
        {
            AgentRegistration? bestAgent = null;
            var bestConfidence = DeviceMatchConfidence.None;
            var ambiguous = false;

            foreach (var (agent, identity) in candidates)
            {
                var confidence = DeviceIdentityMatcher.Match(identity, device);
                if (confidence == DeviceMatchConfidence.None)
                    continue;

                if (confidence > bestConfidence)
                {
                    bestAgent = agent;
                    bestConfidence = confidence;
                    ambiguous = false;
                }
                else if (confidence == bestConfidence)
                {
                    ambiguous = true;
                }
            }

            // Two apps claiming the same device means our identity signals are weaker than
            // assumed. Reporting no pairing is safer than attributing the device to the wrong
            // app, because every coordinate that followed would target the wrong window.
            if (ambiguous)
            {
                bestAgent = null;
                bestConfidence = DeviceMatchConfidence.None;
            }

            paired.Add(new PairedDevice(device, bestAgent?.Id, bestAgent?.Port, bestConfidence));
        }

        return paired;
    }

    /// <summary>
    /// Finds the device one agent is running on, or <c>null</c> when it is not on a recognisable
    /// virtual device.
    /// </summary>
    public async Task<DeviceTarget?> FindForAgentAsync(
        AgentRegistration agent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var identity = DeviceIdentity.Parse(agent.DeviceId);
        if (identity.IsEmpty)
            return null;

        var devices = await ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return DeviceIdentityMatcher.FindBest(identity, devices);
    }

    /// <summary>Boots a device and waits until it can be driven.</summary>
    public Task<DeviceOperationResult> BootAsync(string deviceId, CancellationToken cancellationToken = default) =>
        Mutate(surface => surface.BootAsync(deviceId, cancellationToken));

    /// <summary>Powers a device off without erasing or deleting it.</summary>
    public Task<DeviceOperationResult> ShutdownAsync(string deviceId, CancellationToken cancellationToken = default) =>
        Mutate(surface => surface.ShutdownAsync(deviceId, cancellationToken));

    /// <summary>Taps at a point in device points.</summary>
    public Task<DeviceOperationResult> TapAsync(string deviceId, DevicePoint point, CancellationToken cancellationToken = default) =>
        Mutate(surface => surface.TapAsync(deviceId, point, cancellationToken));

    /// <summary>Captures a device screenshot, or <c>null</c> when unavailable.</summary>
    public Task<byte[]?> ScreenshotAsync(string deviceId, CancellationToken cancellationToken = default) =>
        _surface.ScreenshotAsync(deviceId, cancellationToken);

    /// <summary>
    /// Runs an operation that changes device state and invalidates the cache, so the next read
    /// reflects a boot or shutdown rather than the state from before it.
    /// </summary>
    private async Task<DeviceOperationResult> Mutate(Func<IDeviceSurface, Task<DeviceOperationResult>> operation)
    {
        var result = await operation(_surface).ConfigureAwait(false);
        if (result.Success)
            _cachedAt = DateTimeOffset.MinValue;

        return result;
    }
}
