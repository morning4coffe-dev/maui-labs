using System.Text.Json;
using Microsoft.Maui.DevFlow.Driver;

namespace Microsoft.Maui.Cli.DevFlow.Evidence;

/// <summary>
/// The reads an evidence capture is allowed to perform. Keeping this narrow is part of the
/// privacy contract: there is no preferences, secure-storage, geolocation, or file-content read
/// available to the builder at all.
/// </summary>
internal interface IEvidenceDataSource
{
    Task<AgentStatus?> GetStatusAsync(CancellationToken ct);
    Task<JsonElement> GetCapabilitiesAsync(CancellationToken ct);
    Task<List<ElementInfo>> GetTreeAsync(CancellationToken ct);
    Task<DiagnosticProblemBatch> GetProblemsAsync(int limit, CancellationToken ct);
    Task<LayoutDiagnosticsReport?> GetLayoutDiagnosticsAsync(int maxElements, CancellationToken ct);
    Task<string> GetLogsAsync(int limit, CancellationToken ct);
    Task<List<NetworkRequest>> GetNetworkAsync(int limit, CancellationToken ct);
    Task<JsonElement> GetPlatformInfoAsync(string endpoint, CancellationToken ct);
    Task<byte[]?> GetScreenshotAsync(CancellationToken ct);
}

/// <summary>Adapts a live <see cref="AgentClient"/> to <see cref="IEvidenceDataSource"/>.</summary>
internal sealed class AgentEvidenceDataSource(AgentClient client) : IEvidenceDataSource
{
    public Task<AgentStatus?> GetStatusAsync(CancellationToken ct) => client.GetStatusAsync().WaitAsync(ct);

    public Task<JsonElement> GetCapabilitiesAsync(CancellationToken ct) => client.GetCapabilitiesAsync().WaitAsync(ct);

    public Task<List<ElementInfo>> GetTreeAsync(CancellationToken ct) => client.GetTreeAsync().WaitAsync(ct);

    public Task<DiagnosticProblemBatch> GetProblemsAsync(int limit, CancellationToken ct)
        => client.GetDiagnosticProblemsAsync(limit).WaitAsync(ct);

    public Task<LayoutDiagnosticsReport?> GetLayoutDiagnosticsAsync(int maxElements, CancellationToken ct)
        => client.GetLayoutDiagnosticsAsync(elementId: null, window: null, maxElements: maxElements).WaitAsync(ct);

    public Task<string> GetLogsAsync(int limit, CancellationToken ct) => client.GetLogsAsync(limit).WaitAsync(ct);

    public Task<List<NetworkRequest>> GetNetworkAsync(int limit, CancellationToken ct)
        => client.GetNetworkRequestsAsync(limit).WaitAsync(ct);

    public Task<JsonElement> GetPlatformInfoAsync(string endpoint, CancellationToken ct)
        => client.GetPlatformInfoAsync(endpoint).WaitAsync(ct);

    public Task<byte[]?> GetScreenshotAsync(CancellationToken ct) => client.ScreenshotAsync().WaitAsync(ct);
}
