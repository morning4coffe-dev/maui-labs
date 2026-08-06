using System.Text.Json;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.TestAgent.Host;

/// <summary>
/// Produces a transport-independent projection of a canonical runner report. It is intentionally
/// narrow: timestamps, target identities, command IDs, and artifact paths are transport facts, not
/// flow semantics. The full report is retained separately.
/// </summary>
public static class MauiFlowReportParity
{
    public static string ComputeDigest(MauiFlowRunReport report)
        => Microsoft.Maui.DevFlow.TestAgent.Protocol.AppleTestAgentAuthenticator.ComputeDigest(
            System.Text.Encoding.UTF8.GetBytes(CreateNormalizedJson(report)));

    /// <summary>
    /// Creates the stable report projection shared by Android, Windows, and Apple QA fixtures.
    /// Transport timestamps, host paths, command IDs, and target process identities are excluded.
    /// </summary>
    public static string CreateNormalizedJson(MauiFlowRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var normalized = new
        {
            outcome = report.Outcome?.Status,
            terminal = report.Outcome?.Terminal,
            failure = report.Failure?.Code,
            divergence = report.DivergenceStepId,
            steps = report.Steps.Select(static step => new
            {
                step.Sequence,
                step.Action,
                step.FailureClass,
                assertions = step.Assertions.Select(static assertion => new
                {
                    assertion.Kind,
                    assertion.Passed,
                    assertion.Skipped,
                }),
            }),
        };
        return JsonSerializer.Serialize(normalized);
    }
}
