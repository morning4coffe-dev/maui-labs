using Microsoft.Maui.DevFlow.TestAgent.Protocol;

namespace Microsoft.Maui.DevFlow.TestAgent.Host;

/// <summary>
/// Host-owned operation transport. It deliberately exposes no selector, replay, plan, repair, or
/// source policy; <c>MauiFlowRunner</c> remains the sole semantic engine above this adapter.
/// </summary>
public interface IAppleTestAgentTransport
{
    AppleTestAgentSession Session { get; }
    AppleTestAgentCommandReceipt? LastReceipt { get; }
    IReadOnlyList<AppleTestAgentArtifactReference> CompletedArtifacts { get; }

    Task<AppleTestAgentOperationCompletion> SendAsync(
        string operation,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken cancellationToken = default);

    Task<AppleTestAgentCommandReceipt?> CancelAsync(
        string commandId,
        string? reason = null,
        CancellationToken cancellationToken = default);
}
