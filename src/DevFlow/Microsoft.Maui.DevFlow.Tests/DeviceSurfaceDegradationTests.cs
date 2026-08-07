using Microsoft.Maui.DevFlow.Devices;

namespace Microsoft.Maui.DevFlow.Tests;

/// <summary>
/// The device layer is optional. Most machines running DevFlow will not have a device host, and a
/// desktop MAUI app never has a virtual device at all. These tests pin the guarantee that its
/// absence is an ordinary, describable state rather than an error path.
/// </summary>
public class DeviceSurfaceDegradationTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"devflow-devices-{Guid.NewGuid():N}");

    public DeviceSurfaceDegradationTests() => MobileCanvasHost.HomeOverride = _home;

    public void Dispose()
    {
        MobileCanvasHost.HomeOverride = null;
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    private void WriteHostState(string json)
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "host.json"), json);
    }

    [Fact]
    public async Task NullSurface_ReportsUnavailableRatherThanThrowing()
    {
        var surface = NullDeviceSurface.Instance;

        var health = await surface.GetHealthAsync();

        Assert.False(health.Available);
        Assert.NotNull(health.Reason);
    }

    [Fact]
    public async Task NullSurface_ListsNoDevices()
    {
        Assert.Empty(await NullDeviceSurface.Instance.ListAsync());
    }

    [Fact]
    public async Task NullSurface_RefusesEveryOperationWithAReason()
    {
        var surface = NullDeviceSurface.Instance;

        var boot = await surface.BootAsync("anything");
        var shutdown = await surface.ShutdownAsync("anything");
        var tap = await surface.TapAsync("anything", new DevicePoint(1, 1));

        Assert.False(boot.Success);
        Assert.False(shutdown.Success);
        Assert.False(tap.Success);
        Assert.All([boot, shutdown, tap], r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
    }

    [Fact]
    public async Task NullSurface_ReturnsNoScreenshot()
    {
        Assert.Null(await NullDeviceSurface.Instance.ScreenshotAsync("anything"));
    }

    [Fact]
    public void HostDiscovery_FindsNothing_WhenNotInstalled()
    {
        Assert.Null(MobileCanvasHost.TryRead());
        Assert.False(MobileCanvasHost.IsPresent());
    }

    [Fact]
    public void HostDiscovery_FindsAnInstalledHost()
    {
        WriteHostState("""{"port":54321,"pid":42,"version":"0.1.6"}""");

        var state = MobileCanvasHost.TryRead();

        Assert.NotNull(state);
        Assert.Equal(54321, state!.Port);
        Assert.Equal("http://127.0.0.1:54321", state.BaseUrl);
    }

    [Fact]
    public void HostDiscovery_IgnoresAStateFileWithNoUsablePort()
    {
        WriteHostState("""{"pid":42}""");

        Assert.Null(MobileCanvasHost.TryRead());
    }

    [Fact]
    public void HostDiscovery_IgnoresAnOutOfRangePort()
    {
        WriteHostState("""{"port":99999}""");

        Assert.Null(MobileCanvasHost.TryRead());
    }

    [Fact]
    public void HostDiscovery_DegradesToNothing_ForMalformedJson()
    {
        // The file is owned by another product; a torn or foreign write must not throw into a
        // caller that was only asking whether device control was available.
        WriteHostState("{ not json");

        Assert.Null(MobileCanvasHost.TryRead());
    }

    [Fact]
    public void HostDiscovery_ToleratesUnknownFields()
    {
        WriteHostState("""{"port":54321,"somethingWeDoNotModel":{"nested":true}}""");

        Assert.NotNull(MobileCanvasHost.TryRead());
    }

    [Fact]
    public async Task MobileCanvasSurface_ReportsUnavailable_WhenNoHostIsInstalled()
    {
        using var surface = new MobileCanvasDeviceSurface(stateProvider: () => null);

        var health = await surface.GetHealthAsync();

        Assert.False(health.Available);
        Assert.Empty(await surface.ListAsync());
        Assert.Null(await surface.GetAsync("ios:A1B2"));
    }

    [Fact]
    public async Task MobileCanvasSurface_RefusesOperations_WhenNoHostIsInstalled()
    {
        using var surface = new MobileCanvasDeviceSurface(stateProvider: () => null);

        var result = await surface.BootAsync("ios:A1B2");

        Assert.False(result.Success);
        Assert.Contains("device host", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MobileCanvasSurface_ReportsNotResponding_WhenTheHostIsGone()
    {
        // A stale host.json outlives a crashed host. That is a normal state and must produce a
        // description, not an exception.
        var stale = new MobileCanvasHostState { Port = 1, Pid = 0 };
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(250) };
        using var surface = new MobileCanvasDeviceSurface(http, () => stale);

        var health = await surface.GetHealthAsync();

        Assert.False(health.Available);
        Assert.NotNull(health.Reason);
        Assert.Empty(await surface.ListAsync());
        Assert.Null(await surface.ScreenshotAsync("ios:A1B2"));
    }

    [Fact]
    public async Task MobileCanvasSurface_RejectsAnEmptyDeviceId()
    {
        using var surface = new MobileCanvasDeviceSurface(stateProvider: () => new MobileCanvasHostState { Port = 1 });

        var result = await surface.TapAsync("", new DevicePoint(1, 1));

        Assert.False(result.Success);
    }

    [Fact]
    public void CapabilitiesNone_HasEverythingDisabled()
    {
        var none = DeviceCapabilities.None;

        Assert.False(none.Tap);
        Assert.False(none.LiveStream);
        Assert.False(none.Screenshot);
        Assert.False(none.Recording);
        Assert.False(none.Boot);
    }

    [Fact]
    public void DeviceTarget_DefaultsToNoCapabilities()
    {
        // A device deserialised from a host that did not report capabilities must read as
        // incapable rather than as capable-by-default.
        Assert.False(new DeviceTarget().Capabilities.Tap);
        Assert.False(new DeviceTarget().IsBooted);
    }
}
