using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;

namespace Microsoft.Maui.DevFlow.Testing;

/// <summary>Limits applied before a report is serialized or persisted.</summary>
public sealed class MauiFlowRunReportLimits
{
    public int MaxEvents { get; set; } = 128;
    public int MaxSteps { get; set; } = 2_000;
    public int MaxActionabilityAttemptsPerStep { get; set; } = 64;
    public int MaxAssertionsPerStep { get; set; } = 64;
    public int MaxArtifacts { get; set; } = 64;
    public int MaxTextLength { get; set; } = 512;
    public int MaxJsonBytes { get; set; } = 1_048_576;
}

/// <summary>The outcome of an atomic report write.</summary>
public sealed class MauiFlowRunReportWriteResult
{
    public bool Ok { get; init; }
    public string? Path { get; init; }
    public string? Digest { get; init; }
    public long Bytes { get; init; }
    public string? Error { get; init; }
}

/// <summary>A small schema-shaped validation result for reports produced without a JSON-schema runtime.</summary>
public sealed class MauiFlowRunReportValidation
{
    public List<string> Errors { get; } = [];
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Source-generated serialization, deterministic digests, bounds, and atomic persistence for
/// <see cref="MauiFlowRunReport"/>. The persisted document follows flow-run-report-v1.
/// </summary>
public static class MauiFlowRunReportSerializer
{
    public const string FileName = "flow-run.json";

    public static byte[] SerializeToUtf8Bytes(MauiFlowRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ApplyLimits(report, new MauiFlowRunReportLimits());
        return JsonSerializer.SerializeToUtf8Bytes(report, MauiTestingJsonContext.Default.MauiFlowRunReport);
    }

    public static string ComputeDigest(MauiFlowRunReport report)
        => Convert.ToHexString(SHA256.HashData(SerializeToUtf8Bytes(report))).ToLowerInvariant();

    public static string ComputeFlowDigest(MauiFlow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var element = JsonSerializer.SerializeToElement(flow, MauiFlowJsonContext.Default.MauiFlow);
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        WriteCanonicalJson(writer, element);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    /// <summary>Writes <c>&lt;artifactRoot&gt;/&lt;runId&gt;/flow-run.json</c> atomically.</summary>
    public static MauiFlowRunReportWriteResult WriteAtomic(
        MauiFlowRunReport report,
        string artifactRoot,
        MauiFlowRunReportLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(artifactRoot))
            throw new ArgumentException("An artifact root is required.", nameof(artifactRoot));

        var runId = MauiFlowReportRedactor.SafeFileSegment(report.RunId) ?? "run";
        string root;
        try
        {
            root = Path.GetFullPath(artifactRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new MauiFlowRunReportWriteResult { Error = "The artifact root is not a valid path." };
        }

        var target = Path.Combine(root, runId, FileName);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            ApplyLimits(report, limits ?? new MauiFlowRunReportLimits());
            report.ReportPath = target;
            var identityDigest = ComputeDigest(report);
            // This is a stable report identity computed before its self-reference is added. A
            // literal SHA-256 of a JSON document cannot contain its own final SHA-256 value.
            report.ReportDigest = identityDigest;
            if (!report.Artifacts.Any(static artifact =>
                    string.Equals(artifact.Kind, "flow-run-report", StringComparison.Ordinal)))
            {
                report.Artifacts.Add(new MauiFlowArtifactReference
                {
                    ArtifactId = $"flow-run-{runId}",
                    Kind = "flow-run-report",
                    Path = target,
                    Digest = identityDigest,
                    MediaType = "application/json",
                    Redacted = true,
                    CreatedAt = report.EndedAt ?? DateTimeOffset.UtcNow,
                });
            }

            ApplyLimits(report, limits ?? new MauiFlowRunReportLimits());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                report,
                MauiTestingJsonContext.Default.MauiFlowRunReport);
            if (bytes.Length > (limits ?? new MauiFlowRunReportLimits()).MaxJsonBytes)
                return new MauiFlowRunReportWriteResult { Error = "The bounded report still exceeds the JSON size limit." };

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, overwrite: true);

            return new MauiFlowRunReportWriteResult
            {
                Ok = true,
                Path = target,
                Digest = identityDigest,
                Bytes = bytes.LongLength,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new MauiFlowRunReportWriteResult
            {
                Error = MauiFlowReportRedactor.SafeMessage(ex.Message) ?? "The report could not be written.",
            };
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    /// <summary>Validates the required v1 shape before a host publishes a report.</summary>
    public static MauiFlowRunReportValidation Validate(MauiFlowRunReport? report)
    {
        var result = new MauiFlowRunReportValidation();
        if (report is null)
        {
            result.Errors.Add("Report is required.");
            return result;
        }
        if (report.Schema != 1)
            result.Errors.Add("schema must be 1.");
        if (string.IsNullOrWhiteSpace(report.RunId))
            result.Errors.Add("runId is required.");
        if (string.IsNullOrWhiteSpace(report.FlowDigest))
            result.Errors.Add("flowDigest is required.");
        if (report.Outcome?.Terminal == true && report.EndedAt is null)
            result.Errors.Add("A terminal report requires endedAt.");
        if (report.Outcome?.Terminal == true && string.IsNullOrWhiteSpace(report.Outcome.Status))
            result.Errors.Add("A terminal report requires an outcome status.");
        if (report.Events.Count > new MauiFlowRunReportLimits().MaxEvents)
            result.Errors.Add("events exceeds the v1 bound.");
        if (report.Steps.Count > new MauiFlowRunReportLimits().MaxSteps)
            result.Errors.Add("steps exceeds the v1 bound.");
        if (report.Outcome?.Verified == true && report.ReplayEligibility?.RunVerificationAllowed != true)
            result.Errors.Add("A verified run requires successful independent-oracle eligibility.");
        if (report.Failure?.RepairEligible == true && report.ReplayEligibility?.RepairEligibility != true)
            result.Errors.Add("A repair-eligible failure requires replay repair eligibility.");
        if (report.ReplayEligibility is not null &&
            !string.Equals(report.SideEffectPolicy, report.ReplayEligibility.SideEffectPolicy, StringComparison.Ordinal))
        {
            result.Errors.Add("sideEffectPolicy must match replayEligibility.sideEffectPolicy.");
        }
        return result;
    }

    public static void ApplyLimits(MauiFlowRunReport report, MauiFlowRunReportLimits limits)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(limits);

        limits.MaxEvents = Math.Max(1, limits.MaxEvents);
        limits.MaxSteps = Math.Max(1, limits.MaxSteps);
        limits.MaxActionabilityAttemptsPerStep = Math.Max(1, limits.MaxActionabilityAttemptsPerStep);
        limits.MaxAssertionsPerStep = Math.Max(1, limits.MaxAssertionsPerStep);
        limits.MaxArtifacts = Math.Max(1, limits.MaxArtifacts);
        limits.MaxTextLength = Math.Max(32, limits.MaxTextLength);
        limits.MaxJsonBytes = Math.Max(4_096, limits.MaxJsonBytes);

        SanitizeReport(report, limits.MaxTextLength);
        Trim(report.Events, limits.MaxEvents, report, "events", "The event limit was reached.");
        Trim(report.Steps, limits.MaxSteps, report, "steps", "The step-attempt limit was reached.");
        Trim(report.Artifacts, limits.MaxArtifacts, report, "artifacts", "The artifact-reference limit was reached.");

        foreach (var step in report.Steps)
        {
            Trim(step.Actionability, limits.MaxActionabilityAttemptsPerStep, report, "actionability",
                "An actionability-attempt limit was reached.");
            Trim(step.Assertions, limits.MaxAssertionsPerStep, report, "assertions",
                "An assertion-result limit was reached.");
            Trim(step.Artifacts, limits.MaxArtifacts, report, "step-artifacts",
                "A step artifact-reference limit was reached.");
            BoundStepText(step, limits.MaxTextLength);
        }

        foreach (var item in report.Events)
            item.Message = MauiFlowReportRedactor.SafeMessage(item.Message, limits.MaxTextLength);
        report.Outcome?.Let(outcome => outcome.Summary = MauiFlowReportRedactor.SafeMessage(outcome.Summary, limits.MaxTextLength));
        report.Outcome?.Let(outcome => outcome.VerificationReason = MauiFlowReportRedactor.SafeMessage(outcome.VerificationReason, limits.MaxTextLength));
        if (report.Failure is not null)
            report.Failure.Message = MauiFlowReportRedactor.SafeMessage(report.Failure.Message, limits.MaxTextLength);
        report.Verification?.Let(value => value.Reason = MauiFlowReportRedactor.SafeMessage(value.Reason, limits.MaxTextLength));
        report.TruncationReason = MauiFlowReportRedactor.SafeMessage(report.TruncationReason, limits.MaxTextLength);

        // The normal cardinality and text caps leave ample headroom. If a caller supplied
        // hostile extension data or unusually large identifiers, degrade to a concise report
        // rather than writing more than the v1 one-megabyte budget.
        if (JsonSerializer.SerializeToUtf8Bytes(report, MauiTestingJsonContext.Default.MauiFlowRunReport).Length >
            limits.MaxJsonBytes)
        {
            report.Events = report.Events.Take(1).ToList();
            foreach (var step in report.Steps)
            {
                step.Actionability = step.Actionability.Take(1).ToList();
                step.Assertions = step.Assertions.Take(1).ToList();
                step.Artifacts = step.Artifacts.Take(1).ToList();
                step.ExtensionData = null;
            }
            ClearExtensionData(report);
            report.Truncated = true;
            AddOmission(report, "report-size", "The report was reduced to satisfy the JSON size limit.", null);
        }
    }

    private static void BoundStepText(MauiFlowStepAttempt step, int maxTextLength)
    {
        step.Intent = MauiFlowReportRedactor.SafeMessage(step.Intent, maxTextLength);
        step.TargetResolution?.Let(value =>
        {
            value.Message = MauiFlowReportRedactor.SafeMessage(value.Message, maxTextLength);
            value.FinalResolution = MauiFlowReportRedactor.SafeMessage(value.FinalResolution, maxTextLength);
        });
        foreach (var actionability in step.Actionability)
            actionability.Message = MauiFlowReportRedactor.SafeMessage(actionability.Message, maxTextLength);
        foreach (var assertion in step.Assertions)
            assertion.Message = MauiFlowReportRedactor.SafeMessage(assertion.Message, maxTextLength);
    }

    private static void SanitizeReport(MauiFlowRunReport report, int maxTextLength)
    {
        report.RunId = MauiFlowReportRedactor.SafeIdentifier(report.RunId);
        report.FlowId = MauiFlowReportRedactor.SafeIdentifier(report.FlowId);
        report.FlowDigest = MauiFlowReportRedactor.SafeIdentifier(report.FlowDigest);
        report.LegacyFlowIdentity = MauiFlowReportRedactor.SafeIdentifier(report.LegacyFlowIdentity);
        report.ReportDigest = MauiFlowReportRedactor.SafeIdentifier(report.ReportDigest);
        report.ReportPath = MauiFlowReportRedactor.SafeMessage(report.ReportPath, maxTextLength);
        report.SideEffectPolicy = MauiFlowReportRedactor.SafeIdentifier(report.SideEffectPolicy);
        SanitizeTarget(report.Target);
        SanitizeCheckpoint(report.Reset);
        SanitizePreconditions(report.Preconditions);
        SanitizeCompensator(report.Compensator);
        SanitizeOracles(report.BusinessOracles, maxTextLength);
        SanitizeEligibility(report.ReplayEligibility, maxTextLength);

        foreach (var eventItem in report.Events)
        {
            eventItem.Kind = MauiFlowReportRedactor.SafeIdentifier(eventItem.Kind);
            eventItem.StepId = MauiFlowReportRedactor.SafeIdentifier(eventItem.StepId);
            eventItem.Data = null;
        }

        foreach (var step in report.Steps)
        {
            step.StepId = MauiFlowReportRedactor.SafeIdentifier(step.StepId);
            step.Action = MauiFlowReportRedactor.SafeIdentifier(step.Action);
            step.Selector = MauiFlowReportRedactor.SanitizeSelector(step.Selector);
            SanitizeSelectorRequest(step.SelectorRequest);
            step.CommandId = MauiFlowReportRedactor.SafeIdentifier(step.CommandId);
            step.ActionDigest = MauiFlowReportRedactor.SafeIdentifier(step.ActionDigest);
            step.AcknowledgementState = MauiFlowReportRedactor.SafeIdentifier(step.AcknowledgementState);
            step.CompletionCertainty = MauiFlowReportRedactor.SafeIdentifier(step.CompletionCertainty);
            step.FailureClass = MauiFlowReportRedactor.SafeIdentifier(step.FailureClass);
            step.TargetResolution?.Let(value =>
            {
                value.Status = MauiFlowReportRedactor.SafeIdentifier(value.Status);
                value.ElementId = MauiFlowReportRedactor.SafeIdentifier(value.ElementId);
            });
            step.CandidateSummary?.Let(value =>
            {
                value.Types = value.Types
                    .Take(8)
                    .Select(static type => MauiFlowReportRedactor.SafeIdentifier(type) ?? "element")
                    .ToList();
                value.Final = MauiFlowReportRedactor.SafeMessage(value.Final, maxTextLength);
            });
            SanitizeCheckpoint(step.ExpectedCheckpoint);
            SanitizeCheckpoint(step.ObservedCheckpoint);
            step.Dispatch?.Let(value =>
            {
                value.CommandId = MauiFlowReportRedactor.SafeIdentifier(value.CommandId);
                value.ActionDigest = MauiFlowReportRedactor.SafeIdentifier(value.ActionDigest);
                value.AcknowledgementState = MauiFlowReportRedactor.SafeIdentifier(value.AcknowledgementState);
                value.CompletionCertainty = MauiFlowReportRedactor.SafeIdentifier(value.CompletionCertainty);
            });
            foreach (var assertion in step.Assertions)
            {
                assertion.Kind = MauiFlowReportRedactor.SafeIdentifier(assertion.Kind);
                assertion.ExpectedDisclosure = NormalizeDisclosure(assertion.ExpectedDisclosure, assertion.Expected);
                assertion.ActualDisclosure = NormalizeDisclosure(assertion.ActualDisclosure, assertion.Actual);
                assertion.Expected = assertion.ExpectedDisclosure.Value;
                assertion.Actual = assertion.ActualDisclosure.Value;
            }
            foreach (var actionability in step.Actionability)
            {
                actionability.Kind = MauiFlowReportRedactor.SafeIdentifier(actionability.Kind);
                actionability.Outcome = MauiFlowReportRedactor.SafeIdentifier(actionability.Outcome);
            }
            SanitizeArtifacts(step.Artifacts, maxTextLength);
        }

        if (report.Failure is not null)
        {
            report.Failure.FailureId = MauiFlowReportRedactor.SafeIdentifier(report.Failure.FailureId);
            report.Failure.Class = MauiFlowReportRedactor.SafeIdentifier(report.Failure.Class);
            report.Failure.Code = MauiFlowReportRedactor.SafeIdentifier(report.Failure.Code);
            report.Failure.Category = MauiFlowReportRedactor.SafeIdentifier(report.Failure.Category);
            report.Failure.Phase = MauiFlowReportRedactor.SafeIdentifier(report.Failure.Phase);
            report.Failure.LegacyKind = MauiFlowReportRedactor.SafeIdentifier(report.Failure.LegacyKind);
            report.Failure.StepId = MauiFlowReportRedactor.SafeIdentifier(report.Failure.StepId);
            SanitizeArtifacts(report.Failure.Artifacts, maxTextLength);
        }
        SanitizeArtifacts(report.Artifacts, maxTextLength);
    }

    private static void SanitizeTarget(MauiFlowRunTarget? target)
    {
        if (target is null)
            return;
        target.TargetId = MauiFlowReportRedactor.SafeIdentifier(target.TargetId);
        target.Platform = MauiFlowReportRedactor.SafeIdentifier(target.Platform);
        target.DeviceId = MauiFlowReportRedactor.SafeIdentifier(target.DeviceId);
        target.DeviceProfile = MauiFlowReportRedactor.SafeIdentifier(target.DeviceProfile);
        target.AppId = MauiFlowReportRedactor.SafeIdentifier(target.AppId);
        target.AppBuildFingerprint = MauiFlowReportRedactor.SafeIdentifier(target.AppBuildFingerprint);
        target.AgentId = MauiFlowReportRedactor.SafeIdentifier(target.AgentId);
        target.AgentInstanceId = MauiFlowReportRedactor.SafeIdentifier(target.AgentInstanceId);
        target.Locale = MauiFlowReportRedactor.SafeIdentifier(target.Locale);
        target.Theme = MauiFlowReportRedactor.SafeIdentifier(target.Theme);
        target.Orientation = MauiFlowReportRedactor.SafeIdentifier(target.Orientation);
        target.DisplayProfile = MauiFlowReportRedactor.SafeIdentifier(target.DisplayProfile);
    }

    private static void SanitizeCheckpoint(MauiFlowResetResult? reset)
    {
        if (reset is null)
            return;
        reset.Strategy = MauiFlowReportRedactor.SafeIdentifier(reset.Strategy);
        reset.ResetIdentity = MauiFlowReportRedactor.SafeIdentifier(reset.ResetIdentity);
        reset.SeedFingerprint = MauiFlowReportRedactor.SafeIdentifier(reset.SeedFingerprint);
        reset.BackendStateFingerprint = MauiFlowReportRedactor.SafeIdentifier(reset.BackendStateFingerprint);
        reset.Message = MauiFlowReportRedactor.SafeMessage(reset.Message);
        if (reset.Reference is not null)
        {
            reset.Reference.Strategy = MauiFlowReportRedactor.SafeIdentifier(reset.Reference.Strategy);
            reset.Reference.ResetId = MauiFlowReportRedactor.SafeIdentifier(reset.Reference.ResetId);
            reset.Reference.Scope = MauiFlowReportRedactor.SafeIdentifier(reset.Reference.Scope);
            reset.Reference.Version = MauiFlowReportRedactor.SafeIdentifier(reset.Reference.Version);
            reset.Reference.EvidenceReference = MauiFlowReportRedactor.SafeMessage(reset.Reference.EvidenceReference);
        }
        SanitizeAppStateSeed(reset.AppStateSeed);
        SanitizeBackendSeed(reset.BackendTestDataSeed);
        if (reset.Outcome is not null)
        {
            reset.Outcome.EvidenceReference = MauiFlowReportRedactor.SafeMessage(reset.Outcome.EvidenceReference);
            reset.Outcome.Message = MauiFlowReportRedactor.SafeMessage(reset.Outcome.Message);
        }
    }

    private static void SanitizeCheckpoint(MauiFlowCheckpoint? checkpoint)
    {
        if (checkpoint is null)
            return;
        checkpoint.AppBuildFingerprint = MauiFlowReportRedactor.SafeIdentifier(checkpoint.AppBuildFingerprint);
        checkpoint.AgentInstanceId = MauiFlowReportRedactor.SafeIdentifier(checkpoint.AgentInstanceId);
        checkpoint.SeedFingerprint = MauiFlowReportRedactor.SafeIdentifier(checkpoint.SeedFingerprint);
        checkpoint.BackendStateFingerprint = MauiFlowReportRedactor.SafeIdentifier(checkpoint.BackendStateFingerprint);
        checkpoint.Route = MauiFlowReportRedactor.SafeRoute(checkpoint.Route);
        checkpoint.Window = MauiFlowReportRedactor.SafeIdentifier(checkpoint.Window);
        checkpoint.Modal = MauiFlowReportRedactor.SafeIdentifier(checkpoint.Modal);
        checkpoint.Locale = MauiFlowReportRedactor.SafeIdentifier(checkpoint.Locale);
        checkpoint.Theme = MauiFlowReportRedactor.SafeIdentifier(checkpoint.Theme);
        checkpoint.Orientation = MauiFlowReportRedactor.SafeIdentifier(checkpoint.Orientation);
        checkpoint.DisplayProfile = MauiFlowReportRedactor.SafeIdentifier(checkpoint.DisplayProfile);
        checkpoint.CollectionItemKey = MauiFlowReportRedactor.SafeIdentifier(checkpoint.CollectionItemKey);
    }

    private static void SanitizePreconditions(MauiFlowReplayPreconditions? preconditions)
    {
        if (preconditions is null)
            return;
        SanitizeCheckpoint(preconditions.Expected);
        SanitizeCheckpoint(preconditions.Observed);
        preconditions.EvidenceReference = MauiFlowReportRedactor.SafeMessage(preconditions.EvidenceReference);
    }

    private static void SanitizeAppStateSeed(MauiFlowAppStateSeedFingerprint? seed)
    {
        if (seed is null)
            return;
        seed.SeedId = MauiFlowReportRedactor.SafeIdentifier(seed.SeedId);
        seed.Fingerprint = MauiFlowReportRedactor.SafeIdentifier(seed.Fingerprint);
        seed.Version = MauiFlowReportRedactor.SafeIdentifier(seed.Version);
        seed.Source = MauiFlowReportRedactor.SafeIdentifier(seed.Source);
    }

    private static void SanitizeBackendSeed(MauiFlowBackendTestDataSeedFingerprint? seed)
    {
        if (seed is null)
            return;
        seed.SeedId = MauiFlowReportRedactor.SafeIdentifier(seed.SeedId);
        seed.Fingerprint = MauiFlowReportRedactor.SafeIdentifier(seed.Fingerprint);
        seed.Dataset = MauiFlowReportRedactor.SafeIdentifier(seed.Dataset);
        seed.Version = MauiFlowReportRedactor.SafeIdentifier(seed.Version);
        seed.Source = MauiFlowReportRedactor.SafeIdentifier(seed.Source);
    }

    private static void SanitizeCompensator(MauiFlowCompensatorOutcome? outcome)
    {
        if (outcome is null)
            return;
        if (outcome.Compensator is not null)
        {
            outcome.Compensator.CompensatorId = MauiFlowReportRedactor.SafeIdentifier(outcome.Compensator.CompensatorId);
            outcome.Compensator.Description = MauiFlowReportRedactor.SafeMessage(outcome.Compensator.Description);
            outcome.Compensator.Scope = MauiFlowReportRedactor.SafeIdentifier(outcome.Compensator.Scope);
            outcome.Compensator.EvidenceKind = MauiFlowReportRedactor.SafeIdentifier(outcome.Compensator.EvidenceKind);
            outcome.Compensator.Reference = MauiFlowReportRedactor.SafeMessage(outcome.Compensator.Reference);
        }
        outcome.EvidenceReference = MauiFlowReportRedactor.SafeMessage(outcome.EvidenceReference);
        outcome.Message = MauiFlowReportRedactor.SafeMessage(outcome.Message);
    }

    private static void SanitizeOracles(
        IEnumerable<MauiIndependentBusinessOracleResult> results,
        int maxTextLength)
    {
        foreach (var result in results)
        {
            result.OracleId = MauiFlowReportRedactor.SafeIdentifier(result.OracleId);
            result.EvidenceReference = MauiFlowReportRedactor.SafeMessage(result.EvidenceReference, maxTextLength);
            result.Message = MauiFlowReportRedactor.SafeMessage(result.Message, maxTextLength);
        }
    }

    private static void SanitizeEligibility(
        MauiFlowReplayEligibilityDecision? decision,
        int maxTextLength)
    {
        if (decision is null)
            return;
        decision.SideEffectPolicy = MauiFlowReportRedactor.SafeIdentifier(decision.SideEffectPolicy) ??
            MauiFlowSideEffectPolicies.Unspecified;
        foreach (var reason in decision.Reasons)
        {
            reason.Code = MauiFlowReportRedactor.SafeIdentifier(reason.Code);
            reason.Message = MauiFlowReportRedactor.SafeMessage(reason.Message, maxTextLength);
            reason.Scope = MauiFlowReportRedactor.SafeIdentifier(reason.Scope);
        }
    }

    private static void SanitizeSelectorRequest(MauiFlowSelectorRequest? request)
    {
        if (request is null)
            return;
        request.Kind = MauiFlowReportRedactor.SafeIdentifier(request.Kind);
        request.Scope = MauiFlowReportRedactor.SafeIdentifier(request.Scope);
        request.Value = NormalizeDisclosure(request.Value, request.Value?.Value);
    }

    private static MauiFlowValueDisclosure NormalizeDisclosure(
        MauiFlowValueDisclosure? disclosure,
        string? rawValue)
    {
        if (disclosure is null)
            return MauiFlowReportRedactor.DescribeValue(rawValue);

        var normalized = MauiFlowReportRedactor.DescribeValue(
            disclosure.Value ?? rawValue,
            allowPlain: string.Equals(disclosure.State, "disclosed", StringComparison.Ordinal));
        if (disclosure.Value is null && rawValue is null)
        {
            normalized.State = disclosure.State is "disclosed" or "redacted" or "omitted"
                ? disclosure.State
                : "omitted";
            normalized.Type = MauiFlowReportRedactor.SafeIdentifier(disclosure.Type);
            normalized.Length = disclosure.Length;
            normalized.Digest = MauiFlowReportRedactor.SafeIdentifier(disclosure.Digest);
        }
        return normalized;
    }

    private static void SanitizeArtifacts(List<MauiFlowArtifactReference> artifacts, int maxTextLength)
    {
        foreach (var artifact in artifacts)
        {
            artifact.ArtifactId = MauiFlowReportRedactor.SafeIdentifier(artifact.ArtifactId);
            artifact.Kind = MauiFlowReportRedactor.SafeIdentifier(artifact.Kind);
            artifact.Path = MauiFlowReportRedactor.SafeMessage(artifact.Path, maxTextLength);
            artifact.Digest = MauiFlowReportRedactor.SafeIdentifier(artifact.Digest);
            artifact.MediaType = MauiFlowReportRedactor.SafeIdentifier(artifact.MediaType);
        }
    }

    private static void ClearExtensionData(MauiFlowRunReport report)
    {
        report.ExtensionData = null;
        report.Target?.Let(value => value.ExtensionData = null);
        report.Reset?.Let(value =>
        {
            value.ExtensionData = null;
            value.Reference?.Let(reference => reference.ExtensionData = null);
            value.AppStateSeed?.Let(seed => seed.ExtensionData = null);
            value.BackendTestDataSeed?.Let(seed => seed.ExtensionData = null);
            value.Outcome?.Let(outcome => outcome.ExtensionData = null);
        });
        report.Preconditions?.Let(value =>
        {
            value.ExtensionData = null;
            value.Expected?.Let(checkpoint => checkpoint.ExtensionData = null);
            value.Observed?.Let(checkpoint => checkpoint.ExtensionData = null);
        });
        report.Compensator?.Let(value =>
        {
            value.ExtensionData = null;
            value.Compensator?.Let(reference => reference.ExtensionData = null);
        });
        report.ReplayEligibility?.Let(value =>
        {
            value.ExtensionData = null;
            foreach (var reason in value.Reasons)
                reason.ExtensionData = null;
        });
        report.Verification?.Let(value => value.ExtensionData = null);
        foreach (var oracle in report.BusinessOracles)
            oracle.ExtensionData = null;
        report.Outcome?.Let(value => value.ExtensionData = null);
        report.Failure?.Let(value => value.ExtensionData = null);
        foreach (var eventItem in report.Events)
            eventItem.ExtensionData = null;
        foreach (var step in report.Steps)
        {
            step.ExtensionData = null;
            step.Selector?.Let(value => value.ExtensionData = null);
            step.SelectorRequest?.Let(value => value.ExtensionData = null);
            step.CandidateSummary?.Let(value => value.ExtensionData = null);
            step.TargetResolution?.Let(value => value.ExtensionData = null);
            step.Dispatch?.Let(value => value.ExtensionData = null);
            step.ExpectedCheckpoint?.Let(value => value.ExtensionData = null);
            step.ObservedCheckpoint?.Let(value => value.ExtensionData = null);
            foreach (var actionability in step.Actionability)
                actionability.ExtensionData = null;
            foreach (var assertion in step.Assertions)
            {
                assertion.ExtensionData = null;
                assertion.ExpectedDisclosure?.Let(value => value.ExtensionData = null);
                assertion.ActualDisclosure?.Let(value => value.ExtensionData = null);
            }
            foreach (var artifact in step.Artifacts)
                artifact.ExtensionData = null;
        }
        foreach (var artifact in report.Artifacts)
            artifact.ExtensionData = null;
        foreach (var omission in report.Omissions)
            omission.ExtensionData = null;
    }

    private static void Trim<T>(
        List<T> values,
        int maximum,
        MauiFlowRunReport report,
        string kind,
        string reason)
    {
        if (values.Count <= maximum)
            return;

        var omitted = values.Count - maximum;
        values.RemoveRange(maximum, omitted);
        report.Truncated = true;
        report.TruncationReason ??= reason;
        AddOmission(report, kind, reason, omitted);
    }

    private static void AddOmission(MauiFlowRunReport report, string kind, string reason, int? count)
    {
        if (report.Omissions.Any(item => string.Equals(item.Kind, kind, StringComparison.Ordinal)))
            return;
        report.Omissions.Add(new MauiFlowReportOmission
        {
            Kind = kind,
            Reason = reason,
            Count = count,
        });
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonicalJson(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}

/// <summary>Privacy projection used by run reports before a host persists or publishes them.</summary>
public static class MauiFlowReportRedactor
{
    private static readonly Regex SensitiveAssignment = new(
        @"(?ix)
        \b(password|passcode|secret|token|api[_-]?key|authorization|cookie|credential|private[_-]?key|
        access[_-]?key|client[_-]?secret|refresh[_-]?token|pin|otp|cvv|ssn)\b
        \s*[:=]\s*([^\s,;]+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BearerToken = new(
        @"(?i)\bbearer\s+[a-z0-9._~+/\-=]{8,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static MauiFlowValueDisclosure DescribeValue(string? value, bool allowPlain = false)
    {
        if (value is null)
            return new MauiFlowValueDisclosure { State = "omitted" };

        var normalized = RemoveControls(value);
        var scalar = IsSafeScalar(normalized);
        var disclose = allowPlain && scalar && !FlowSecretReference.LooksSensitive(normalized);
        return new MauiFlowValueDisclosure
        {
            State = disclose ? "disclosed" : "redacted",
            Type = scalar ? "scalar" : "string",
            Length = normalized.Length,
            Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant(),
            Value = disclose ? normalized : null,
        };
    }

    public static string? SafeMessage(string? value, int maximumLength = 512)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var safe = RemoveControls(value);
        safe = SensitiveAssignment.Replace(safe, "$1=<redacted>");
        safe = BearerToken.Replace(safe, "Bearer <redacted>");
        if (FlowSecretReference.LooksSensitive(safe) || LooksOpaqueSecret(safe))
            safe = "<redacted>";
        if (safe.Length > maximumLength)
            safe = safe[..Math.Max(0, maximumLength - 12)] + "[truncated]";
        return safe;
    }

    public static string? SafeIdentifier(string? value, int maximumLength = 128)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = RemoveControls(value.Trim());
        if (normalized.Length > maximumLength)
            normalized = normalized[..maximumLength];
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '/'
                ? character
                : '_');
        return builder.ToString();
    }

    public static string? SafeFileSegment(string? value)
    {
        var safe = SafeIdentifier(value, 96);
        return string.IsNullOrWhiteSpace(safe)
            ? null
            : safe.Replace('/', '_').Replace(':', '_').Trim('_', '.');
    }

    public static string? SafeRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
            return null;
        var index = route.IndexOfAny(['?', '#']);
        return SafeMessage(index >= 0 ? route[..index] : route, 256);
    }

    public static FlowSelector? SanitizeSelector(FlowSelector? selector)
    {
        if (selector is null)
            return null;
        return new FlowSelector
        {
            AutomationId = SafeIdentifier(selector.AutomationId),
            Id = selector.Id is null ? null : "sha256:" + ShortDigest(selector.Id),
            TypeIndex = selector.TypeIndex is null ? null : new FlowTypeIndex
            {
                Type = SafeIdentifier(selector.TypeIndex.Type),
                Index = selector.TypeIndex.Index,
            },
            Type = SafeIdentifier(selector.Type),
            Index = selector.Index,
            SelectorKind = SafeIdentifier(selector.SelectorKind),
            MatchCount = selector.MatchCount,
            Quality = SafeIdentifier(selector.Quality),
            FragilityReasons = selector.FragilityReasons?
                .Take(8)
                .Select(static reason => SafeIdentifier(reason) ?? "unknown")
                .ToList(),
        };
    }

    public static MauiFlowSelectorRequest? DescribeSelector(FlowSelector? selector)
    {
        if (selector is null || selector.IsEmpty)
            return null;

        if (!string.IsNullOrEmpty(selector.AutomationId))
            return new MauiFlowSelectorRequest
            {
                Kind = "automationId",
                Value = DescribeValue(SafeIdentifier(selector.AutomationId), allowPlain: true),
            };
        if (!string.IsNullOrEmpty(selector.Text))
            return new MauiFlowSelectorRequest
            {
                Kind = "text",
                Value = DescribeValue(selector.Text),
            };
        if (selector.TypeIndex is not null ||
            (string.Equals(selector.SelectorKind, "typeIndex", StringComparison.Ordinal) && selector.Index is not null))
            return new MauiFlowSelectorRequest
            {
                Kind = "typeIndex",
                Value = DescribeValue(
                    $"{selector.TypeIndex?.Type ?? selector.Type}:{selector.TypeIndex?.Index ?? selector.Index}",
                    allowPlain: true),
            };
        return new MauiFlowSelectorRequest
        {
            Kind = "id",
            Value = DescribeValue(selector.Id),
        };
    }

    public static string DescribeElement(string? type, string? automationId, string? id)
    {
        var safeType = SafeIdentifier(type) ?? "element";
        var safeAutomationId = SafeIdentifier(automationId);
        return safeAutomationId is not null
            ? $"{safeType}[{safeAutomationId}]"
            : $"{safeType}[{ShortDigest(id)}]";
    }

    private static bool IsSafeScalar(string value)
        => bool.TryParse(value, out _) ||
           decimal.TryParse(value, System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture, out _) ||
           (value.StartsWith('#') && value.Length is 7 or 9 && value[1..].All(Uri.IsHexDigit));

    private static bool LooksOpaqueSecret(string value)
    {
        var compact = new string(value.Where(char.IsAsciiLetterOrDigit).ToArray());
        return !value.Any(char.IsWhiteSpace) &&
            compact.Length >= 20 &&
            compact.Any(char.IsUpper) &&
            compact.Any(char.IsLower);
    }

    private static string ShortDigest(string? value)
        => value is null
            ? "none"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];

    private static string RemoveControls(string value)
        => new(value.Where(static character =>
            !char.IsControl(character) &&
            char.GetUnicodeCategory(character) != UnicodeCategory.Format).ToArray());
}

internal static class MauiFlowReportExtensions
{
    public static void Let<T>(this T? value, Action<T> action)
        where T : class
    {
        if (value is not null)
            action(value);
    }
}
