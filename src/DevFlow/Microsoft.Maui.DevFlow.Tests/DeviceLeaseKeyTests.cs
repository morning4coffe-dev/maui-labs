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
        // Distinct from the no-app case: here an app IS running and paired, but the caller asked
        // about a different device, so the agent's key must not be borrowed for it.
        var registry = new DeviceRegistry(new OneDeviceSurface(Device()));
        var agents = new[]
        {
            new AgentRegistration { Id = "agent-42", Port = 9223, DeviceId = "platform=ios;udid=A1B2" },
        };

        var key = await registry.ResolveLeaseKeyAsync("ios:UNKNOWN", agents);

        Assert.Equal("device:ios:UNKNOWN", key);
    }

    [Fact]
    public void DeviceScopedKeysAreNamespaced_SoTheyCannotCollideWithAnAgentId()
    {
        // Agent ids are hex digests, so a "device:" prefix cannot be mistaken for one.
        Assert.StartsWith("device:", DeviceRegistry.DeviceLeaseKey("anything"));
    }

    [Fact]
    public void TheHolderIsAdmitted_AndEveryoneElseIsRefused()
    {
        // The behaviour that matters, and the one an earlier version of this test missed: probing
        // without a lease id reports "held by other" even to the session that holds it, so a
        // presence check would refuse exactly the caller that should be allowed. Only passing the
        // caller's own lease id distinguishes them.
        var leases = new MutationLeaseRegistry();
        leases.Control("agent-42", "claim", "lease-holder", "inspector", "Inspector", force: false);

        var holder = leases.Control("agent-42", "validate", "lease-holder", null, null, force: false);
        var other = leases.Control("agent-42", "validate", "lease-other", null, null, force: false);
        var anonymous = leases.Control("agent-42", "validate", null, null, null, force: false);

        Assert.False(holder.HeldByOther);
        Assert.True(other.HeldByOther);
        Assert.True(anonymous.HeldByOther);
    }

    [Fact]
    public void AnUnclaimedKeyAdmitsTheFirstCaller()
    {
        // Before an app launches nobody has claimed the device key. "Nobody holds it" must not be
        // read as "nobody may drive it", or device control would be impossible in exactly the
        // situation it exists for.
        var leases = new MutationLeaseRegistry();

        var first = leases.Control(
            DeviceRegistry.DeviceLeaseKey("ios:A1B2"), "validate", "lease-1", null, null, force: false);

        Assert.False(first.HeldByOther);
    }
}
