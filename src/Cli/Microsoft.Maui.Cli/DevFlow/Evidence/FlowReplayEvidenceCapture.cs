using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;
using System.Security.Cryptography;

namespace Microsoft.Maui.Cli.DevFlow.Evidence;

/// <summary>Optional CLI adapter for failure-only workflow evidence. The Flows layer only knows its callback contract.</summary>
internal sealed class FlowReplayEvidenceCapture : IFlowRunEvidenceCapture
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
    public MauiFlowRunEvidenceContext? CapturedRunContext { get; private set; }
    public MauiFlowArtifactReference? CapturedArtifact { get; private set; }

    public async Task CaptureOnFailureAsync(
        MauiFlow flow,
        FlowStep failedStep,
        FlowStepResult result,
        CancellationToken cancellationToken)
    {
        if (CapturedPath is not null)
            return;
        var captured = await CaptureAsync(flow, null, cancellationToken);
        if (captured.Ok)
            SetCaptured(captured, null);
    }

    public async Task CaptureOnRunFailureAsync(
        MauiFlowRunEvidenceContext context,
        CancellationToken cancellationToken)
    {
        if (CapturedPath is not null)
            return;
        CapturedRunContext = context;
        var captured = await CaptureAsync(context.Flow, context, cancellationToken);
        if (captured.Ok)
            SetCaptured(captured, context);
    }

    private Task<EvidenceCaptureResult> CaptureAsync(
        MauiFlow flow,
        MauiFlowRunEvidenceContext? context,
        CancellationToken cancellationToken)
        => EvidenceCapture.CaptureAsync(_client, new EvidenceRequest
        {
            // Screenshots remain an explicit opt-in. Flow failure evidence is metadata/tree/log
            // oriented by default and follows the normal redaction policy.
            IncludeScreenshot = false,
            OutputPath = _outputPath,
            ProjectHint = _projectHint,
            Source = _source,
            WorkflowMarkdown = FlowMarkdown.Serialize(flow),
            FlowRun = context is null ? null : new EvidenceFlowRunLink
            {
                RunId = context.Report.RunId,
                FailedStepId = context.Report.DivergenceStepId,
                FailureCode = context.Report.Failure?.Code,
                ReportDigest = context.ReportDigest,
                ReportPath = context.ReportPath,
                ReportReference = context.ReportPath is null
                    ? $"run:{context.Report.RunId}"
                    : $"flow-run:{context.Report.RunId}",
                CaptureCompleteness = "failure-only-redacted",
            }
        }, cancellationToken);

    private void SetCaptured(EvidenceCaptureResult captured, MauiFlowRunEvidenceContext? context)
    {
        CapturedPath = captured.Path;
        if (string.IsNullOrWhiteSpace(captured.Path))
            return;

        string? digest = null;
        try
        {
            digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(captured.Path))).ToLowerInvariant();
        }
        catch
        {
            // The evidence path itself remains useful even if a concurrent cleanup prevented a
            // post-write digest read.
        }
        CapturedArtifact = new MauiFlowArtifactReference
        {
            ArtifactId = $"evidence-{context?.Report.RunId ?? "replay"}",
            Kind = "mauitrace",
            Path = captured.Path,
            Digest = digest,
            MediaType = "application/vnd.maui.evidence+zip",
            Redacted = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }
}
