using Microsoft.Maui.Cli.DevFlow.Broker;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed class RouteCheckpointStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "route-checkpoint-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Save_ReloadAndClear_UsesAtomicLocalState()
    {
        var store = new RouteCheckpointStore(_root);
        var saved = store.Save("agent", "session", "/orders?id=42", "Demo", "Windows", "Demo.csproj");

        var reloaded = new RouteCheckpointStore(_root).Get("agent", "session", connected: true);
        Assert.True(reloaded.HasCheckpoint);
        Assert.Equal(saved.Route, reloaded.Checkpoint!.Route);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));

        Assert.True(store.Clear("agent", "session"));
        Assert.False(store.Get("agent", "session").HasCheckpoint);
    }

    [Fact]
    public void Get_CorruptFile_WarnsAndStartsClean()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "route-checkpoints.v1.json"), "{broken");

        var status = new RouteCheckpointStore(_root).Get("agent");

        Assert.False(status.HasCheckpoint);
        Assert.NotNull(status.Warning);
        Assert.NotEmpty(Directory.EnumerateFiles(_root, "*.corrupt-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Get_ExpiredCheckpoint_PrunesIt()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "route-checkpoints.v1.json"),
            """{"schema":1,"checkpoints":[{"schema":1,"agentId":"agent","route":"/old","savedUtc":"2000-01-01T00:00:00+00:00"}]}""");

        var status = new RouteCheckpointStore(_root).Get("agent");

        Assert.False(status.HasCheckpoint);
    }

    [Fact]
    public void Save_CapsEntries()
    {
        var store = new RouteCheckpointStore(_root);
        for (var index = 0; index <= RouteCheckpointStore.MaxEntries; index++)
            store.Save("agent-" + index, null, "/route-" + index, null, null, null);

        Assert.False(store.Get("agent-0").HasCheckpoint);
        Assert.True(store.Get("agent-" + RouteCheckpointStore.MaxEntries).HasCheckpoint);
    }

    [Theory]
    [InlineData("/ok")]
    [InlineData("  /trimmed  ")]
    public void NormalizeRoute_ValidRoute_Accepts(string route)
        => Assert.True(RouteCheckpointStore.TryNormalizeRoute(route, out _, out _));

    [Fact]
    public void NormalizeRoute_ControlCharacter_Rejects()
        => Assert.False(RouteCheckpointStore.TryNormalizeRoute("/bad\nroute", out _, out _));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
