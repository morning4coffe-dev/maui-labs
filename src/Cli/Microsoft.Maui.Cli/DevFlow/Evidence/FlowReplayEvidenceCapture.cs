using Microsoft.Maui.Cli.DevFlow.Flows;
using Microsoft.Maui.DevFlow.Driver;

namespace Microsoft.Maui.Cli.DevFlow.Evidence;

/// <summary>Optional CLI adapter for failure-only workflow evidence. The Flows layer only knows its callback contract.</summary>
internal sealed class FlowReplayEvidenceCapture : IFlowReplayEvidenceCapture
{
    private readonly AgentClient _client;
    private readonly string? _outputPath;
    private readonly string? _projectHint;
    private readonly string _source;

    public FlowReplayEvidenceCapture(AgentClient client, string? outputPath, string? projectHint, string source)
    {
        _client = client;
        _outputPath = outputPath;
        _projectHint = projectHint;
        _source = source;
    }

    public string? CapturedPath { get; private set; }

    public async Task CaptureOnFailureAsync(
        MauiFlow flow,
        FlowStep failedStep,
        FlowStepResult result,
        CancellationToken cancellationToken)
    {
        if (CapturedPath is not null)
            return;
        var captured = await EvidenceCapture.CaptureAsync(_client, new EvidenceRequest
        {
            // Screenshots remain an explicit opt-in. Flow failure evidence is metadata/tree/log
            // oriented by default and follows the normal redaction policy.
            IncludeScreenshot = false,
            OutputPath = _outputPath,
            ProjectHint = _projectHint,
            Source = _source,
            WorkflowMarkdown = FlowMarkdown.Serialize(flow)
        }, cancellationToken);
        if (captured.Ok)
            CapturedPath = captured.Path;
    }
}
