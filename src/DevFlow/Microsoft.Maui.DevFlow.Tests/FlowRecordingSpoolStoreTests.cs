using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.Cli.DevFlow.Flows;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed class FlowRecordingSpoolStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "flow-spool-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveRestoreAndDelete_PreservesActiveSnapshot()
    {
        var recorder = new FlowRecorder("resume", "Demo", "Android", null);
        recorder.AppendStep(FlowActions.Tap, new FlowSelector { AutomationId = "go" }, null, null, null, false, null);
        var store = new FlowRecordingSpoolStore(_root);
        const string id = "0123456789abcdef01234567";

        store.Save("stable-agent", "session", id, recorder);
        var restored = store.Restore();

        var spool = Assert.Single(restored);
        Assert.Equal("stable-agent", spool.AgentId);
        Assert.Equal(id, spool.RecordingId);
        Assert.Single(spool.Flow.Steps);
        store.Delete(id);
        Assert.Empty(store.Restore());
    }

    [Fact]
    public void Restore_CorruptSpool_QuarantinesAndWarns()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "0123456789abcdef01234567.json"), "{broken");
        var warnings = new List<string>();

        var restored = new FlowRecordingSpoolStore(_root, warning: warnings.Add).Restore();

        Assert.Empty(restored);
        Assert.NotEmpty(warnings);
        Assert.NotEmpty(Directory.EnumerateFiles(_root, "*.corrupt-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Restore_ExpiredSpool_DeletesIt()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "0123456789abcdef01234567.json"),
            """{"schema":1,"agentId":"agent","recordingId":"0123456789abcdef01234567","createdUtc":"2000-01-01T00:00:00+00:00","lastTouchedUtc":"2000-01-01T00:00:00+00:00","flow":{"schema":1,"name":"old","steps":[]}}""");

        var restored = new FlowRecordingSpoolStore(_root).Restore();

        Assert.Empty(restored);
        Assert.False(File.Exists(Path.Combine(_root, "0123456789abcdef01234567.json")));
    }

    [Fact]
    public void Save_PrunesToTheConfiguredFileCap()
    {
        var store = new FlowRecordingSpoolStore(_root);
        for (var index = 0; index < FlowRecordingSpoolStore.MaxFiles + 3; index++)
        {
            var id = index.ToString("x24");
            var recorder = new FlowRecorder($"flow-{index}", "Demo", "Windows", null);
            store.Save($"agent-{index}", null, id, recorder);
        }

        Assert.True(
            Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly).Count()
            <= FlowRecordingSpoolStore.MaxFiles);
    }

    [Fact]
    public void Coordinator_ReportsSpoolPersistenceFailureWithoutDroppingRecording()
    {
        var warnings = new List<string>();
        Directory.CreateDirectory(Path.GetDirectoryName(_root)!);
        File.WriteAllText(_root, "not a directory");
        var spools = new FlowRecordingSpoolStore(_root, warning: warnings.Add);
        var recordings = new FlowRecordingStore();
        var coordinator = new BrokerFlowCoordinator(recordings, spools);

        var result = coordinator.Start("agent", "flow", "Demo", "Windows", null);

        Assert.True(result.Ok);
        Assert.True(coordinator.Status("agent").Recording);
        Assert.Contains(warnings, warning => warning.Contains("Could not persist", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try { if (File.Exists(_root)) File.Delete(_root); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
