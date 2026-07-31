namespace Microsoft.Maui.DevFlow.Agent.IntegrationTests.Fixtures;

/// <summary>
/// Represents a platform-specific fixture that manages the lifecycle
/// of the DevFlow sample app for integration testing.
/// </summary>
public interface IAppFixture : IAsyncLifetime
{
    Driver.AgentClient Client { get; }
    HttpClient Http { get; }
    int AgentPort { get; }
    string AgentBaseUrl { get; }
    string Platform { get; }

    /// <summary>
    /// Optional host-owned lifecycle implementation. Existing platform fixtures remain usable
    /// without flow orchestration; Android exposes this only from integration tooling.
    /// </summary>
    IPlatformFlowTestLifecycle? FlowLifecycle { get; }
}
