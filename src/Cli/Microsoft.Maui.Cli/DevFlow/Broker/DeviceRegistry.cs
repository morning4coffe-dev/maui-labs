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

    private readonly IDeviceSurface? _fixedSurface;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MobileCanvasDeviceSurface _mobileCanvas = new();

    private IReadOnlyList<DeviceTarget> _cached = [];
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Bumped whenever device state is deliberately changed. A refresh that started before a
    /// mutation must not publish its now-stale result, so it compares the generation it captured
    /// against the current one before committing to the cache.
    /// </summary>
    private long _generation;

    public DeviceRegistry(IDeviceSurface? surface = null, TimeProvider? timeProvider = null)
    {
        _fixedSurface = surface;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The device backend, resolved per call.
    /// <para>
    /// The broker is long-lived and the device host is a separate product that starts, idles out,
    /// and restarts independently. Choosing a backend once at construction would pin the broker to
    /// "no device layer" for its entire lifetime whenever it happened to start first — which is
    /// the ordinary sequence, since almost any <c>maui devflow</c> command starts the broker.
    /// </para>
    /// <para>
    /// The Mobile Canvas surface already re-reads the host's state file on every call and reports
    /// <see cref="DeviceHostAvailability.Absent"/> when there is none, so it is safe to use
    /// unconditionally.
    /// </para>
    /// </summary>
    private IDeviceSurface Surface => _fixedSurface ?? _mobileCanvas;

    /// <summary>Whether a device host is present and answering.</summary>
    public Task<DeviceHostHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        Surface.GetHealthAsync(cancellationToken);

    /// <summary>
    /// All known devices, cached briefly.
    /// <para>
    /// A failure to enumerate returns the previous result rather than an empty list: devices
    /// blinking out of the Inspector because a host was momentarily busy is a worse experience
    /// than briefly stale state. A successful enumeration that finds nothing is cached normally,
    /// so shutting down the last emulator is reflected rather than masked.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<DeviceTarget>> ListAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && !IsStale())
            return _cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while we waited for the gate.
            if (!forceRefresh && !IsStale())
                return _cached;

            var generation = Interlocked.Read(ref _generation);
            var devices = await Surface.ListAsync(cancellationToken).ConfigureAwait(false);

            // Null means enumeration failed. Serve the last good answer and retry on the next
            // call, but stamp the timestamp anyway so a persistently broken host does not turn
            // every request into a fresh attempt.
            if (devices is null)
            {
                _cachedAt = _time.GetUtcNow();
                return _cached;
            }

            // A boot or shutdown landed while this refresh was in flight, so what we just read is
            // already stale. Publishing it would hide the change for a full cache lifetime.
            if (Interlocked.Read(ref _generation) != generation)
                return devices;

            _cached = devices;
            _cachedAt = _time.GetUtcNow();
            return devices;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsStale() => _time.GetUtcNow() - _cachedAt >= CacheLifetime;

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

    /// <summary>
    /// The mutation-lease key that governs a device.
    /// <para>
    /// Leases are keyed per agent, but a device tap can happen when no agent exists at all —
    /// before the app launches, or after it crashes — so a device needs a key of its own. When an
    /// app <em>is</em> paired, its agent's key is used, so a device-level tap and a
    /// <c>maui_tap</c> contend for the same lease. That is the point: both mutate the same screen,
    /// and two independent locks would let two sessions drive one device believing each had
    /// exclusive control.
    /// </para>
    /// </summary>
    public async Task<string> ResolveLeaseKeyAsync(
        string deviceId,
        IEnumerable<AgentRegistration> agents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agents);

        var paired = await ListPairedAsync(agents, cancellationToken: cancellationToken).ConfigureAwait(false);
        var match = paired.FirstOrDefault(p =>
            string.Equals(p.Device.Id, deviceId, StringComparison.OrdinalIgnoreCase));

        return match?.AgentId ?? DeviceLeaseKey(deviceId);
    }

    /// <summary>The synthetic lease key for a device with no app running inside it.</summary>
    public static string DeviceLeaseKey(string deviceId) => $"device:{deviceId}";

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
        Surface.ScreenshotAsync(deviceId, cancellationToken);

    /// <summary>
    /// Runs an operation that changes device state and invalidates the cache, so the next read
    /// reflects a boot or shutdown rather than the state from before it.
    /// <para>
    /// The generation is bumped as well as the timestamp: a refresh already in flight would
    /// otherwise commit its pre-mutation result with a fresh timestamp and hide the change for a
    /// full cache lifetime, even from the caller that just received success.
    /// </para>
    /// </summary>
    private async Task<DeviceOperationResult> Mutate(Func<IDeviceSurface, Task<DeviceOperationResult>> operation)
    {
        var result = await operation(Surface).ConfigureAwait(false);
        if (result.Success)
        {
            Interlocked.Increment(ref _generation);
            _cachedAt = DateTimeOffset.MinValue;
        }

        return result;
    }
}
