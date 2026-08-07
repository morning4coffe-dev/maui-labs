using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.DevFlow.Devices;

namespace Microsoft.Maui.DevFlow.Tests;

/// <summary>
/// How the mutation lease covers device-level input.
/// <para>
/// Leases are keyed per agent, but a device tap can happen when no agent exists — before launch,
/// or after a crash. This was the blocker that gated the Inspector work: without a key of its own
/// a device tap either could not be arbitrated at all, or would get an independent lock and let
/// two sessions drive one screen each believing it had exclusive control.
/// </para>
/// </summary>
public class DeviceLeaseKeyTests
{
    private sealed class OneDeviceSurface(DeviceTarget device) : IDeviceSurface
    {
        public Task<DeviceHostHealth> GetHealthAsync(CancellationToken ct = default) =>
            Task.FromResult(new DeviceHostHealth { Availability = DeviceHostAvailability.Available });
        public Task<IReadOnlyList<DeviceTarget>?> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeviceTarget>?>([device]);
        public Task<DeviceTarget?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<DeviceTarget?>(device);
        public Task<DeviceOperationResult> BootAsync(string id, CancellationToken ct = default) => Task.FromResult(DeviceOperationResult.Ok());
        public Task<DeviceOperationResult> ShutdownAsync(string id, CancellationToken ct = default) => Task.FromResult(DeviceOperationResult.Ok());
        public Task<DeviceOperationResult> TapAsync(string id, DevicePoint p, CancellationToken ct = default) => Task.FromResult(DeviceOperationResult.Ok());
        public Task<byte[]?> ScreenshotAsync(string id, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    }

    private static DeviceTarget Device() => new()
    {
        Id = "ios:A1B2",
        Name = "iPhone 16",
        NativeId = "A1B2",
        Udid = "A1B2",
        State = DeviceStates.Booted,
    };

    [Fact]
    public async Task UsesThePairedAgentsKey_SoAppAndDeviceInputContend()
    {
        // The point of the whole design: a device tap and a maui_tap on the same screen must
        // compete for one lease, not hold two independent ones.
        var registry = new DeviceRegistry(new OneDeviceSurface(Device()));
        var agents = new[]
        {
            new AgentRegistration { Id = "agent-42", Port = 9223, DeviceId = "platform=ios;udid=A1B2" },
        };

        var key = await registry.ResolveLeaseKeyAsync("ios:A1B2", agents);

        Assert.Equal("agent-42", key);
    }

    [Fact]
    public async Task FallsBackToADeviceScopedKey_WhenNoAppIsRunning()
    {
        // Before launch or after a crash there is no agent to key on, but the tap still has to be
        // arbitrated against other sessions driving the same device.
        var registry = new DeviceRegistry(new OneDeviceSurface(Device()));

        var key = await registry.ResolveLeaseKeyAsync("ios:A1B2", []);

        Assert.Equal("device:ios:A1B2", key);
    }

    [Fact]
    public async Task FallsBackToADeviceScopedKey_WhenTheAgentIsOnAnotherDevice()
    {
        var registry = new DeviceRegistry(new OneDeviceSurface(Device()));
        var agents = new[]
        {
            new AgentRegistration { Id = "elsewhere", Port = 9223, DeviceId = "platform=android;serial=emulator-5554" },
        };

        var key = await registry.ResolveLeaseKeyAsync("ios:A1B2", agents);

        Assert.Equal("device:ios:A1B2", key);
    }

    [Fact]
    public async Task FallsBackToADeviceScopedKey_ForAnUnknownDevice()
    {
        var registry = new DeviceRegistry(new OneDeviceSurface(Device()));

        var key = await registry.ResolveLeaseKeyAsync("ios:UNKNOWN", []);

        Assert.Equal("device:ios:UNKNOWN", key);
    }

    [Fact]
    public void DeviceScopedKeysAreNamespaced_SoTheyCannotCollideWithAnAgentId()
    {
        // Agent ids are hex digests, so a "device:" prefix cannot be mistaken for one.
        Assert.StartsWith("device:", DeviceRegistry.DeviceLeaseKey("anything"));
    }

    [Fact]
    public void ASharedKeyMeansASharedLease()
    {
        // Verifies the consequence rather than the plumbing: one holder on the shared key blocks
        // the other surface.
        var leases = new MutationLeaseRegistry();

        var claimed = leases.Control("agent-42", "claim", "lease-1", "inspector", "Inspector", force: false);
        var observed = leases.Control("agent-42", "status", leaseId: null, holderKind: null, label: null, force: false);

        Assert.True(claimed.YouHold);
        Assert.True(observed.HeldByOther);
    }
}
