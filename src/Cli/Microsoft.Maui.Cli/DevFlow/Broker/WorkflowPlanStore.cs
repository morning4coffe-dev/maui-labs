using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.Cli.DevFlow.Broker;

/// <summary>
/// Bounded local persistence for non-executable plan sidecars and explicitly committed flow
/// bundles. The Testing package owns contracts; this host-only service owns workspace I/O.
/// </summary>
internal sealed class WorkflowPlanStore
{
    internal const long MaxFileBytes = 1_048_576;
    internal const int MaxFiles = 100;
    internal const int MaxRepairHistoryEntries = 256;
    internal const long MaxRepairHistoryBytes = 1_048_576;
    internal const int MaxRepairHistoryLineBytes = 16_384;

    private static readonly ConcurrentDictionary<string, object> TransactionGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string _projectRoot;
    private readonly string _workflowRoot;

    internal WorkflowPlanStore(string projectRoot)
    {
        _projectRoot = Path.GetFullPath(projectRoot ?? throw new ArgumentNullException(nameof(projectRoot)));
        _workflowRoot = Path.Combine(_projectRoot, "maui-tests");
    }

    internal string ProjectRoot => _projectRoot;
    internal string WorkflowRoot => _workflowRoot;

    internal WorkflowPlanStoreResult List()
    {
        if (!TryEnsureWorkflowRoot(create: false, out var error))
        {
            return Directory.Exists(_workflowRoot)
                ? WorkflowPlanStoreResult.Failure("workspace-unsafe", error!)
                : WorkflowPlanStoreResult.Success();
        }

        try
        {
            var entries = Directory.EnumerateFiles(_workflowRoot, "*", SearchOption.TopDirectoryOnly)
                .Where(static path => string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase))
                .Select(static path => new FileInfo(path))
                .Where(static info => (info.Attributes & FileAttributes.ReparsePoint) == 0 && info.Length <= MaxFileBytes)
                .OrderByDescending(static info => info.LastWriteTimeUtc)
                .ThenBy(static info => info.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxFiles)
                .Select(info =>
                {
                    var sidecar = SidecarPath(info.Name);
                    var hasPlan = File.Exists(sidecar) && !IsReparsePoint(sidecar);
                    return new WorkflowPlanListItem
                    {
                        Name = info.Name,
                        SidecarName = Path.GetFileName(sidecar),
                        HasPlan = hasPlan,
                        Size = info.Length,
                        ModifiedAt = info.LastWriteTimeUtc,
                    };
                })
                .ToArray();
            return WorkflowPlanStoreResult.Success() with { Items = entries };
        }
        catch (Exception)
        {
            return WorkflowPlanStoreResult.Failure("workspace-read-failed", "Could not list project workflow tests.");
        }
    }

    internal WorkflowPlanStoreResult Load(string? flowName)
    {
        if (!TryReadSnapshot(flowName, out var snapshot, out var result))
            return result;
        return WorkflowPlanStoreResult.Success() with
        {
            Snapshot = snapshot,
            Errors = result.Errors,
            Warnings = result.Warnings,
        };
    }

    internal WorkflowPlanStoreResult Validate(
        string? flowName,
        string? markdown,
        string? planJson,
        bool requireSidecar = false)
    {
        if (!TryReadSnapshot(flowName, out var committed, out var readResult))
            return readResult;

        if (!TryParseFlow(markdown, out var draftFlow, out var canonicalMarkdown, out var errors, out var warnings))
        {
            return WorkflowPlanStoreResult.Failure("flow-invalid", "The flow draft is invalid.") with
            {
                Errors = errors,
                Warnings = warnings,
            };
        }

        ValidateAuthoringFlow(draftFlow!, committed!.Flow, errors, warnings);
        var digest = MauiFlowRunReportSerializer.ComputeFlowDigest(draftFlow!);
        MauiTestPlan? plan = null;
        string? canonicalPlan = null;
        if (!string.IsNullOrWhiteSpace(planJson))
        {
            if (!TryPreparePlan(
                planJson,
                committed.Name!,
                digest,
                committed.Plan?.PlanId,
                committed.Plan?.Revision,
                out plan,
                out canonicalPlan,
                out var planErrors,
                out var planWarnings))
            {
                errors.AddRange(planErrors);
                warnings.AddRange(planWarnings);
            }
            else
            {
                warnings.AddRange(planWarnings);
                AddPlanCoverageWarnings(draftFlow!, plan!, warnings);
            }
        }
        else if (requireSidecar)
        {
            errors.Add("A plan sidecar is required before this flow can be committed.");
        }

        return WorkflowPlanStoreResult.Success() with
        {
            Snapshot = new WorkflowAuthoringSnapshot
            {
                Name = committed.Name,
                Markdown = canonicalMarkdown,
                Flow = draftFlow,
                FlowDigest = digest,
                PlanJson = canonicalPlan,
                Plan = plan,
                PlanDigest = canonicalPlan is null ? null : ComputeDigest(canonicalPlan),
            },
            Errors = errors,
            Warnings = warnings,
        };
    }

    internal WorkflowPlanStoreResult Save(WorkflowPlanSaveRequest request)
    {
        if (!TryReadSnapshot(request.FlowName, out var current, out var readResult))
            return readResult;

        lock (TransactionGates.GetOrAdd(_projectRoot, static _ => new object()))
        {
            if (!TryReadSnapshot(request.FlowName, out current, out readResult))
                return readResult;
            if (!MatchesExpectedRevision(current!, request, out var stale))
                return stale;

            if (!TryPreparePlan(
                request.PlanJson,
                current!.Name!,
                current.FlowDigest!,
                current.Plan?.PlanId,
                current.Plan?.Revision,
                out var plan,
                out var canonicalPlan,
                out var errors,
                out var warnings))
            {
                return WorkflowPlanStoreResult.Failure("plan-invalid", "The plan draft is invalid.") with
                {
                    Errors = errors,
                    Warnings = warnings,
                };
            }

            var path = SidecarPath(current.Name!);
            if (!WriteBundle([(path, canonicalPlan!)], out var writeError))
                return WorkflowPlanStoreResult.Failure("write-failed", writeError!);

            return WorkflowPlanStoreResult.Success() with
            {
                Snapshot = current with
                {
                    Plan = plan,
                    PlanJson = canonicalPlan,
                    PlanDigest = ComputeDigest(canonicalPlan!),
                },
                Warnings = warnings,
            };
        }
    }

    internal WorkflowPlanStoreResult Commit(WorkflowBundleCommitRequest request)
    {
        if (!TryReadCommitBaseline(request.FlowName, out var current, out var readResult))
            return readResult;

        lock (TransactionGates.GetOrAdd(_projectRoot, static _ => new object()))
        {
            if (!TryReadCommitBaseline(request.FlowName, out current, out readResult))
                return readResult;
            if (!MatchesExpectedRevision(current!, request, out var stale))
                return stale;

            if (!TryParseFlow(request.Markdown, out var flow, out var canonicalMarkdown, out var errors, out var warnings))
            {
                return WorkflowPlanStoreResult.Failure("flow-invalid", "The flow draft is invalid.") with
                {
                    Errors = errors,
                    Warnings = warnings,
                };
            }

            ValidateAuthoringFlow(flow!, current!.Flow, errors, warnings);
            var flowDigest = MauiFlowRunReportSerializer.ComputeFlowDigest(flow!);
            if (!TryPreparePlan(
                request.PlanJson,
                current.Name!,
                flowDigest,
                current.Plan?.PlanId,
                current.Plan?.Revision,
                out var plan,
                out var canonicalPlan,
                out var planErrors,
                out var planWarnings))
            {
                errors.AddRange(planErrors);
                warnings.AddRange(planWarnings);
            }
            else
            {
                warnings.AddRange(planWarnings);
                AddPlanCoverageWarnings(flow!, plan!, warnings);
            }

            if (errors.Count > 0)
            {
                return WorkflowPlanStoreResult.Failure("bundle-invalid", "The flow or plan draft is invalid.") with
                {
                    Errors = errors,
                    Warnings = warnings,
                };
            }

            if (!WriteBundle(
                [
                    (FlowPath(current.Name!), canonicalMarkdown!),
                    (SidecarPath(current.Name!), canonicalPlan!),
                ],
                out var writeError))
            {
                return WorkflowPlanStoreResult.Failure("write-failed", writeError!);
            }

            return WorkflowPlanStoreResult.Success() with
            {
                Snapshot = new WorkflowAuthoringSnapshot
                {
                    Name = current.Name,
                    Markdown = canonicalMarkdown,
                    Flow = flow,
                    FlowDigest = flowDigest,
                    Plan = plan,
                    PlanJson = canonicalPlan,
                    PlanDigest = ComputeDigest(canonicalPlan!),
                },
                Warnings = warnings,
            };
        }
    }

    /// <summary>
    /// Persists a human-approved selector-only repair through a digest/revision compare-and-swap.
    /// The flow, plan sidecar, and redacted hash-linked history are committed together.
    /// </summary>
    internal WorkflowRepairFlowApplyResult ApplySelectorRepair(WorkflowRepairFlowApplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var proposal = request.Proposal;
        if (!HasRepairBaseline(proposal, out var error))
            return WorkflowRepairFlowApplyResult.Failure("repair-baseline-invalid", error!);
        var validProposal = proposal!;
        var baseFlow = validProposal.BaseFlow!;

        return PersistSelectorRepair(
            validProposal,
            request.ExpectedFlowDigest ?? baseFlow.Digest,
            request.ExpectedFlowRevision ?? baseFlow.Revision,
            validProposal.ProposedSelector,
            verifyPatch: true,
            MauiFlowRepairOutcomeStates.Applied,
            request.Reviewer,
            request.GrantDigest,
            request.ValidationRunIds,
            [],
            request.RecordedAt ?? DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Persists a new revision restoring the selector that predated an applied repair. It never
    /// rewrites the failed run or prior repair history.
    /// </summary>
    internal WorkflowRepairFlowApplyResult RollbackSelectorRepair(WorkflowRepairFlowRollbackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var proposal = request.Proposal;
        if (!HasRepairBaseline(proposal, out var error) || proposal!.OldSelector is null)
        {
            return WorkflowRepairFlowApplyResult.Failure(
                "rollback-baseline-invalid",
                error ?? "The repair proposal has no prior selector.");
        }

        return PersistSelectorRepair(
            proposal,
            request.ExpectedAppliedFlowDigest,
            request.ExpectedAppliedFlowRevision,
            proposal.OldSelector,
            verifyPatch: false,
            MauiFlowRepairOutcomeStates.Reverted,
            request.Reviewer,
            request.GrantDigest,
            [],
            request.VerificationRunIds,
            request.RecordedAt ?? DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Appends a bounded redacted lifecycle event without modifying the flow. Proposal, preview,
    /// approval, validation, rejection, and verification transitions use this path; apply and
    /// rollback use the bundle transaction above so their flow revision and history line commit
    /// together.
    /// </summary>
    internal WorkflowRepairHistoryAppendResult AppendRepairHistory(WorkflowRepairHistoryAppendRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Proposal?.BaseFlow?.Path is not { Length: > 0 } flowName)
        {
            return WorkflowRepairHistoryAppendResult.Failure(
                "repair-baseline-invalid",
                "The repair proposal has no canonical flow path.");
        }

        if (!TryReadSnapshot(flowName, out var current, out var readResult))
        {
            return WorkflowRepairHistoryAppendResult.Failure(
                readResult.Code ?? "flow-read-failed",
                readResult.Error ?? "The repair flow could not be loaded.");
        }

        lock (TransactionGates.GetOrAdd(_projectRoot, static _ => new object()))
        {
            if (!TryReadSnapshot(flowName, out current, out readResult))
            {
                return WorkflowRepairHistoryAppendResult.Failure(
                    readResult.Code ?? "flow-read-failed",
                    readResult.Error ?? "The repair flow could not be loaded.");
            }
            var identity = ReadFlowIdentity(current!.Flow!);
            var flowId = identity.FlowId ?? request.Proposal.BaseFlow.FlowId ??
                CreateFlowId(current.Name!, request.Proposal.BaseFlow.Digest ?? current.FlowDigest!);
            if (!TryAppendRepairHistory(
                    flowId,
                    new WorkflowRepairHistoryEvent
                    {
                        Proposal = request.Proposal,
                        State = request.State,
                        NewFlowRevision = request.NewFlowRevision,
                        RollbackRevision = request.RollbackRevision,
                        Reviewer = request.Reviewer,
                        GrantDigest = request.GrantDigest,
                        ValidationRunIds = request.ValidationRunIds,
                        VerificationRunIds = request.VerificationRunIds,
                        ReasonCode = request.ReasonCode,
                        RecordedAt = request.RecordedAt ?? DateTimeOffset.UtcNow,
                    },
                    out var historyPath,
                    out var history,
                    out var historyError))
            {
                return WorkflowRepairHistoryAppendResult.Failure(
                    "repair-history-invalid",
                    historyError!);
            }
            if (!WriteRepairBundle([(historyPath!, history!)], out var writeError))
            {
                return WorkflowRepairHistoryAppendResult.Failure("write-failed", writeError!);
            }
            return WorkflowRepairHistoryAppendResult.Success(historyPath!);
        }
    }

    private WorkflowRepairFlowApplyResult PersistSelectorRepair(
        MauiFlowRepairProposal proposal,
        string? expectedDigest,
        int? expectedRevision,
        FlowSelector? selector,
        bool verifyPatch,
        string state,
        string? reviewer,
        string? grantDigest,
        IReadOnlyList<string> validationRunIds,
        IReadOnlyList<string> verificationRunIds,
        DateTimeOffset recordedAt)
    {
        if (selector is null || !int.TryParse(proposal.SourceStepId, out var stepSequence))
        {
            return WorkflowRepairFlowApplyResult.Failure(
                "repair-selector-invalid",
                "The repair proposal has no valid source step or selector.");
        }

        if (!TryReadSnapshot(proposal.BaseFlow!.Path, out var current, out var readResult))
        {
            return WorkflowRepairFlowApplyResult.Failure(
                readResult.Code ?? "flow-read-failed",
                readResult.Error ?? "The current flow could not be loaded.");
        }

        lock (TransactionGates.GetOrAdd(_projectRoot, static _ => new object()))
        {
            if (!TryReadSnapshot(proposal.BaseFlow.Path, out current, out readResult))
            {
                return WorkflowRepairFlowApplyResult.Failure(
                    readResult.Code ?? "flow-read-failed",
                    readResult.Error ?? "The current flow could not be loaded.");
            }

            var currentIdentity = ReadFlowIdentity(current!.Flow!);
            if (!string.Equals(current.FlowDigest, expectedDigest, StringComparison.Ordinal) ||
                currentIdentity.Revision != expectedRevision ||
                (!string.IsNullOrWhiteSpace(proposal.BaseFlow.FlowId) &&
                 !string.Equals(currentIdentity.FlowId, proposal.BaseFlow.FlowId, StringComparison.Ordinal)))
            {
                return WorkflowRepairFlowApplyResult.Failure(
                    "stale",
                    "The current flow digest, flow identity, or revision changed before persistence.",
                    stale: true);
            }

            var patch = verifyPatch
                ? MauiFlowRepairPatchBuilder.ApplyVerified(current.Flow, proposal)
                : MauiFlowRepairPatchBuilder.Build(current.Flow, stepSequence, selector);
            if (!patch.Ok || patch.PatchedFlow is null)
            {
                return WorkflowRepairFlowApplyResult.Failure(
                    verifyPatch ? "repair-patch-invalid" : "rollback-patch-invalid",
                    patch.Error ?? "The selector-only patch could not be verified.");
            }

            var flowId = currentIdentity.FlowId ?? proposal.BaseFlow.FlowId ??
                CreateFlowId(current.Name!, current.FlowDigest!);
            var nextRevision = (currentIdentity.Revision ?? 0) + 1;
            SetFlowIdentity(patch.PatchedFlow, flowId, nextRevision);
            var markdown = FlowMarkdown.ReplaceAuthoritativePayload(current.Markdown, patch.PatchedFlow);
            if (markdown is null)
            {
                return WorkflowRepairFlowApplyResult.Failure(
                    "flow-markdown-invalid",
                    "The authoritative payload could not be replaced without discarding flow prose.");
            }

            var digest = MauiFlowRunReportSerializer.ComputeFlowDigest(patch.PatchedFlow);
            if (!TryUpdatePlanForRepair(
                    current,
                    flowId,
                    nextRevision,
                    digest,
                    out var planJson,
                    out var planError))
            {
                return WorkflowRepairFlowApplyResult.Failure("plan-update-failed", planError!);
            }

            if (!TryAppendRepairHistory(
                    flowId,
                    new WorkflowRepairHistoryEvent
                    {
                        Proposal = proposal,
                        State = state,
                        NewFlowRevision = nextRevision,
                        RollbackRevision = state == MauiFlowRepairOutcomeStates.Reverted ? nextRevision : null,
                        Reviewer = reviewer,
                        GrantDigest = grantDigest,
                        ValidationRunIds = validationRunIds,
                        VerificationRunIds = verificationRunIds,
                        ReasonCode = state,
                        RecordedAt = recordedAt,
                    },
                    out var historyPath,
                    out var history,
                    out var historyError))
            {
                return WorkflowRepairFlowApplyResult.Failure("repair-history-invalid", historyError!);
            }

            var files = new List<(string Path, string Content)>
            {
                (FlowPath(current.Name!), markdown),
                (historyPath!, history!),
            };
            if (planJson is not null)
                files.Add((SidecarPath(current.Name!), planJson));
            if (!WriteRepairBundle(files, out var writeError))
                return WorkflowRepairFlowApplyResult.Failure("write-failed", writeError!);

            return WorkflowRepairFlowApplyResult.Success(flowId, nextRevision, digest, historyPath!);
        }
    }

    private static bool HasRepairBaseline(MauiFlowRepairProposal? proposal, out string? error)
    {
        error = null;
        if (proposal?.BaseFlow is null ||
            string.IsNullOrWhiteSpace(proposal.BaseFlow.Path) ||
            string.IsNullOrWhiteSpace(proposal.BaseFlow.Digest) ||
            string.IsNullOrWhiteSpace(proposal.SourceStepId))
        {
            error = "The repair proposal has no complete base flow path, digest, and source step.";
            return false;
        }
        return true;
    }

    internal WorkflowPlanStoreResult Diff(
        string? flowName,
        string? markdown,
        string? planJson)
    {
        if (!TryReadSnapshot(flowName, out var committed, out var readResult))
            return readResult;

        var validation = Validate(flowName, markdown, planJson, requireSidecar: false);
        if (!validation.Ok || validation.Snapshot is null)
            return validation;

        var flowDiff = DeterministicDiff(
            $"committed/{committed!.Name}",
            $"draft/{committed.Name}",
            FlowMarkdown.Serialize(committed.Flow!),
            validation.Snapshot.Markdown!);
        var planDiff = string.IsNullOrWhiteSpace(planJson)
            ? string.Empty
            : DeterministicDiff(
                $"committed/{Path.GetFileName(SidecarPath(committed.Name!))}",
                $"draft/{Path.GetFileName(SidecarPath(committed.Name!))}",
                committed.PlanJson ?? string.Empty,
                validation.Snapshot.PlanJson ?? string.Empty);
        return validation with { Diff = string.Join(Environment.NewLine, new[] { flowDiff, planDiff }.Where(static value => !string.IsNullOrEmpty(value))) };
    }

    private static WorkflowFlowIdentity ReadFlowIdentity(MauiFlow flow)
    {
        var extensions = flow.ExtensionData;
        return new WorkflowFlowIdentity(
            ReadExtensionString(extensions, "flowId"),
            ReadExtensionInt(extensions, "revision"));
    }

    private static void SetFlowIdentity(MauiFlow flow, string flowId, int revision)
    {
        flow.ExtensionData ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        flow.ExtensionData["flowId"] = JsonSerializer.SerializeToElement(flowId);
        flow.ExtensionData["revision"] = JsonSerializer.SerializeToElement(revision);
    }

    private static string? ReadExtensionString(
        Dictionary<string, JsonElement>? extensions,
        string name)
        => extensions is not null &&
           extensions.TryGetValue(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadExtensionInt(
        Dictionary<string, JsonElement>? extensions,
        string name)
        => extensions is not null &&
           extensions.TryGetValue(name, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var number)
            ? number
            : null;

    private static string CreateFlowId(string flowName, string digest)
        => "flow_" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(flowName + "\u001f" + digest))).ToLowerInvariant()[..24];

    private static bool TryUpdatePlanForRepair(
        WorkflowAuthoringSnapshot current,
        string flowId,
        int flowRevision,
        string flowDigest,
        out string? updatedPlanJson,
        out string? error)
    {
        updatedPlanJson = null;
        error = null;
        if (string.IsNullOrWhiteSpace(current.PlanJson))
            return true;

        try
        {
            var root = JsonNode.Parse(current.PlanJson) as JsonObject;
            if (root is null)
            {
                error = "The plan sidecar is not a JSON object.";
                return false;
            }

            root["revision"] = Math.Max(1, (current.Plan?.Revision ?? 0) + 1);
            var flow = root["flow"] as JsonObject ?? new JsonObject();
            flow["path"] = current.Name;
            flow["flowId"] = flowId;
            flow["revision"] = flowRevision;
            flow["digest"] = flowDigest;
            root["flow"] = flow;
            updatedPlanJson = CanonicalizeJson(root, indented: true);
            var validation = MauiTestPlanValidator.ValidateJson(updatedPlanJson, out var plan);
            if (validation.Errors.Count > 0 || plan is null)
            {
                error = validation.Errors.FirstOrDefault() ?? "The updated plan sidecar is invalid.";
                updatedPlanJson = null;
                return false;
            }
            return true;
        }
        catch (JsonException)
        {
            error = "The plan sidecar could not be canonicalized for the new flow revision.";
            return false;
        }
    }

    private bool TryAppendRepairHistory(
        string flowId,
        WorkflowRepairHistoryEvent repairEvent,
        out string? historyPath,
        out string? content,
        out string? error)
    {
        historyPath = null;
        content = null;
        error = null;
        if (!TryEnsureRepairHistoryRoot(out var historyRoot, out error))
            return false;
        if (!IsSafeFlowId(flowId))
        {
            error = "The flow identity is not valid for a repair history path.";
            return false;
        }

        historyPath = Path.Combine(historyRoot!, flowId + ".repair-history.jsonl");
        var existing = string.Empty;
        if (File.Exists(historyPath))
        {
            if (!TryReadSafeFile(historyPath, out existing, out error))
                return false;
            existing ??= string.Empty;
            if (Encoding.UTF8.GetByteCount(existing) > MaxRepairHistoryBytes)
            {
                error = "The bounded repair history is too large to append safely.";
                return false;
            }
        }

        var lines = existing.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (lines.Count >= MaxRepairHistoryEntries)
        {
            error = "The bounded repair history has reached its entry limit.";
            return false;
        }

        var previousHash = ValidateRepairHistoryChain(lines, out error);
        if (error is not null)
            return false;

        var node = CreateRepairHistoryNode(repairEvent, lines.Count + 1, previousHash);
        var material = CanonicalizeJson(node, indented: false);
        node["hash"] = "sha256:" + HashRepair(material);
        var line = CanonicalizeJson(node, indented: false);
        if (Encoding.UTF8.GetByteCount(line) > MaxRepairHistoryLineBytes)
        {
            error = "The repair history entry exceeds its bounded size.";
            return false;
        }

        content = existing;
        if (!string.IsNullOrEmpty(content) && !content.EndsWith('\n'))
            content += "\n";
        content += line + "\n";
        if (Encoding.UTF8.GetByteCount(content) > MaxRepairHistoryBytes)
        {
            error = "Appending this repair history entry would exceed the bounded history size.";
            content = null;
            return false;
        }
        return true;
    }

    private static string? ValidateRepairHistoryChain(IReadOnlyList<string> lines, out string? error)
    {
        error = null;
        string? previous = null;
        foreach (var line in lines)
        {
            try
            {
                var node = JsonNode.Parse(line) as JsonObject;
                var actual = node?["hash"]?.GetValue<string>();
                var declaredPrevious = node?["previousHash"]?.GetValue<string>();
                if (node is null ||
                    !string.Equals(declaredPrevious, previous, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(actual))
                {
                    error = "The existing repair history hash chain is invalid.";
                    return null;
                }

                node.Remove("hash");
                var expected = "sha256:" + HashRepair(CanonicalizeJson(node, indented: false));
                if (!FixedEquals(actual, expected))
                {
                    error = "The existing repair history hash chain failed verification.";
                    return null;
                }
                previous = actual;
            }
            catch (JsonException)
            {
                error = "The existing repair history contains invalid JSON.";
                return null;
            }
            catch (InvalidOperationException)
            {
                error = "The existing repair history has an invalid safe projection.";
                return null;
            }
        }
        return previous;
    }

    private static JsonObject CreateRepairHistoryNode(
        WorkflowRepairHistoryEvent repairEvent,
        int sequence,
        string? previousHash)
    {
        var proposal = repairEvent.Proposal;
        var candidate = proposal.Candidate;
        var node = new JsonObject
        {
            ["schema"] = 1,
            ["sequence"] = sequence,
            ["at"] = repairEvent.RecordedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["previousHash"] = previousHash,
            ["proposalId"] = SafeHistoryToken(proposal.ProposalId),
            ["proposalRevision"] = proposal.Revision,
            ["state"] = SafeHistoryState(repairEvent.State),
            ["sourceRunId"] = SafeHistoryToken(proposal.SourceRunId),
            ["sourceStepId"] = SafeHistoryToken(proposal.SourceStepId),
            ["sourceFailureCode"] = SafeHistoryToken(proposal.SourceFailureCode),
            ["baseFlowDigest"] = SafeHistoryDigest(proposal.BaseFlow?.Digest),
            ["patchDigest"] = SafeHistoryDigest(proposal.PatchDigest),
            ["trust"] = SafeHistoryToken(proposal.Trust),
            ["reviewerDigest"] = string.IsNullOrWhiteSpace(repairEvent.Reviewer)
                ? null
                : "sha256:" + HashRepair(repairEvent.Reviewer!),
            ["grantDigest"] = SafeHistoryDigest(repairEvent.GrantDigest),
            ["newFlowRevision"] = repairEvent.NewFlowRevision,
            ["rollbackRevision"] = repairEvent.RollbackRevision,
            ["reasonCode"] = SafeHistoryToken(repairEvent.ReasonCode),
            ["candidate"] = new JsonObject
            {
                ["candidateId"] = string.IsNullOrWhiteSpace(candidate?.CandidateId)
                    ? null
                    : "sha256:" + HashRepair(candidate.CandidateId!),
                ["kind"] = SafeHistoryCandidateCode(candidate?.SelectorDescriptor.Kind),
                ["score"] = candidate?.Score ?? candidate?.Scores.DeterministicRankScore,
                ["calibration"] = SafeHistoryToken(candidate?.CalibrationStatus ?? candidate?.Calibration.State),
                ["riskFlags"] = ToSafeHistoryCandidateCodeArray(candidate?.RiskFlags),
                ["originCodes"] = ToSafeHistoryCandidateCodeArray(candidate?.OriginCodes),
            },
            ["validationRunIds"] = ToSafeHistoryArray(repairEvent.ValidationRunIds),
            ["verificationRunIds"] = ToSafeHistoryArray(repairEvent.VerificationRunIds),
        };
        return node;
    }

    private static JsonArray ToSafeHistoryArray(IEnumerable<string>? values)
    {
        var result = new JsonArray();
        foreach (var value in (values ?? []).Where(static value => !string.IsNullOrWhiteSpace(value))
                     .Take(32)
                     .Select(SafeHistoryToken)
                     .Where(static value => value is not null))
        {
            result.Add(value);
        }
        return result;
    }

    private static JsonArray ToSafeHistoryCandidateCodeArray(IEnumerable<string>? values)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "app-automation-id",
            "stable-item-key",
            "native-automation-identity",
            "role-type-ancestor",
            "source-topology",
            "exact-text",
            "virtualization",
            "stale-source",
            "platform-divergence",
            "localization",
            "ambiguity",
        };
        var result = new JsonArray();
        foreach (var value in (values ?? []).Where(static value => !string.IsNullOrWhiteSpace(value)).Take(32))
        {
            var trimmed = value.Trim();
            result.Add(allowed.Contains(trimmed)
                ? trimmed
                : "sha256:" + HashRepair(trimmed));
        }
        return result;
    }

    private static string? SafeHistoryCandidateCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "automation-id",
            "stable-item-key",
            "native-automation-id",
            "role-type-ancestor",
            "source-topology",
            "exact-text",
        };
        var trimmed = value.Trim();
        return allowed.Contains(trimmed) ? trimmed : "sha256:" + HashRepair(trimmed);
    }

    private bool TryEnsureRepairHistoryRoot(out string? root, out string? error)
    {
        root = null;
        error = null;
        if (!TryEnsureWorkflowRoot(create: true, out error))
            return false;
        try
        {
            root = Path.Combine(_workflowRoot, ".devflow");
            if (!Directory.Exists(root))
                Directory.CreateDirectory(root);
            if (IsReparsePoint(root) || PathContainsReparsePoint(_workflowRoot, root))
            {
                error = "The repair history directory cannot be a symbolic link or reparse point.";
                root = null;
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = "The repair history directory could not be resolved safely.";
            root = null;
            return false;
        }
    }

    private bool WriteRepairBundle(IReadOnlyList<(string Path, string Content)> files, out string? error)
    {
        if (!TryEnsureRepairHistoryRoot(out _, out error))
            return false;
        return WriteBundle(files, IsExpectedRepairBundlePath, out error);
    }

    private bool IsExpectedRepairBundlePath(string path)
    {
        if (IsExpectedTopLevelPath(path))
            return true;
        try
        {
            var full = Path.GetFullPath(path);
            var historyRoot = Path.Combine(_workflowRoot, ".devflow");
            return IsUnderRoot(full, historyRoot) &&
                string.Equals(Path.GetDirectoryName(full), historyRoot, PathComparison) &&
                full.EndsWith(".repair-history.jsonl", StringComparison.OrdinalIgnoreCase) &&
                IsSafeFlowId(Path.GetFileNameWithoutExtension(
                    Path.GetFileNameWithoutExtension(full)));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSafeFlowId(string? flowId)
        => flowId is { Length: > 0 and <= 128 } &&
           flowId.All(static character =>
               char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static string? SafeHistoryToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return "sha256:" + HashRepair(value.Trim());
    }

    private static string? SafeHistoryDigest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        var hex = trimmed.StartsWith("sha256:", StringComparison.Ordinal)
            ? trimmed["sha256:".Length..]
            : trimmed;
        return hex.Length == 64 &&
               hex.All(static character =>
                   character is >= '0' and <= '9' or
                       >= 'a' and <= 'f' or
                       >= 'A' and <= 'F')
            ? trimmed
            : "sha256:" + HashRepair(trimmed);
    }

    private static string SafeHistoryState(string state)
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            MauiFlowRepairOutcomeStates.Proposed,
            MauiFlowRepairOutcomeStates.Previewed,
            MauiFlowRepairOutcomeStates.Approved,
            MauiFlowRepairOutcomeStates.Applying,
            MauiFlowRepairOutcomeStates.Applied,
            MauiFlowRepairOutcomeStates.Verified,
            MauiFlowRepairOutcomeStates.Rejected,
            MauiFlowRepairOutcomeStates.Stale,
            MauiFlowRepairOutcomeStates.ApprovalExpired,
            MauiFlowRepairOutcomeStates.VerificationFailed,
            MauiFlowRepairOutcomeStates.RollbackRequired,
            MauiFlowRepairOutcomeStates.Reverted,
            MauiFlowRepairOutcomeStates.RollbackFailed,
        };
        return known.Contains(state) ? state : "sha256:" + HashRepair(state);
    }

    private static string HashRepair(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedEquals(string first, string second)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(first),
            Encoding.UTF8.GetBytes(second));

    private bool TryReadSnapshot(
        string? flowName,
        out WorkflowAuthoringSnapshot? snapshot,
        out WorkflowPlanStoreResult result)
    {
        snapshot = null;
        if (!TryValidateFlowName(flowName, out var name, out var nameError))
        {
            result = WorkflowPlanStoreResult.Failure("flow-name-invalid", nameError!);
            return false;
        }
        if (!TryEnsureWorkflowRoot(create: false, out var rootError))
        {
            result = WorkflowPlanStoreResult.Failure("workspace-unsafe", rootError!);
            return false;
        }

        var flowPath = FlowPath(name!);
        if (!TryReadSafeFile(flowPath, out var markdown, out var fileError))
        {
            result = WorkflowPlanStoreResult.Failure("flow-not-found", fileError!);
            return false;
        }

        if (!TryParseFlow(markdown, out var flow, out _, out var errors, out var warnings))
        {
            result = WorkflowPlanStoreResult.Failure("flow-invalid", "The committed flow is invalid.") with
            {
                Errors = errors,
                Warnings = warnings,
            };
            return false;
        }

        var flowDigest = MauiFlowRunReportSerializer.ComputeFlowDigest(flow!);
        MauiTestPlan? plan = null;
        string? planJson = null;
        string? planDigest = null;
        var sidecarPath = SidecarPath(name!);
        if (File.Exists(sidecarPath))
        {
            if (!TryReadSafeFile(sidecarPath, out planJson, out var sidecarError))
            {
                result = WorkflowPlanStoreResult.Failure("plan-read-failed", sidecarError!);
                return false;
            }

            var validation = MauiTestPlanValidator.ValidateJson(planJson, out plan);
            errors.AddRange(validation.Errors);
            warnings.AddRange(validation.Warnings);
            if (plan?.Flow is not null)
            {
                if (!string.Equals(plan.Flow.Path, name, StringComparison.Ordinal))
                    errors.Add("The plan sidecar flow.path does not match its canonical Markdown flow.");
                if (!string.Equals(plan.Flow.Digest, flowDigest, StringComparison.Ordinal))
                    warnings.Add("The plan sidecar references an older flow digest and must be explicitly reviewed before saving.");
            }
            planDigest = ComputeDigest(planJson!);
            if (errors.Count > 0)
            {
                result = WorkflowPlanStoreResult.Failure(
                    "plan-invalid",
                    "The committed plan sidecar does not satisfy test-plan-v1.") with
                {
                    Errors = errors,
                    Warnings = warnings,
                };
                return false;
            }
        }

        snapshot = new WorkflowAuthoringSnapshot
        {
            Name = name!,
            Markdown = markdown,
            Flow = flow,
            FlowDigest = flowDigest,
            Plan = plan,
            PlanJson = planJson,
            PlanDigest = planDigest,
        };
        result = WorkflowPlanStoreResult.Success() with { Warnings = warnings, Errors = errors };
        return true;
    }

    private bool TryReadCommitBaseline(
        string? flowName,
        out WorkflowAuthoringSnapshot? snapshot,
        out WorkflowPlanStoreResult result)
    {
        snapshot = null;
        if (!TryValidateFlowName(flowName, out var name, out var nameError))
        {
            result = WorkflowPlanStoreResult.Failure("flow-name-invalid", nameError!);
            return false;
        }
        if (!TryEnsureWorkflowRoot(create: true, out var rootError))
        {
            result = WorkflowPlanStoreResult.Failure("workspace-unsafe", rootError!);
            return false;
        }
        if (File.Exists(FlowPath(name!)))
            return TryReadSnapshot(name, out snapshot, out result);

        snapshot = new WorkflowAuthoringSnapshot { Name = name };
        result = WorkflowPlanStoreResult.Success();
        return true;
    }

    private bool MatchesExpectedRevision(
        WorkflowAuthoringSnapshot current,
        WorkflowPlanSaveRequest request,
        out WorkflowPlanStoreResult result)
    {
        var hasCommittedFlow = !string.IsNullOrWhiteSpace(current.FlowDigest);
        var flowMatches = !hasCommittedFlow ||
            (!string.IsNullOrWhiteSpace(request.ExpectedFlowDigest) &&
             string.Equals(request.ExpectedFlowDigest, current.FlowDigest, StringComparison.Ordinal));
        var revisionMatches = current.Plan is null
            ? request.ExpectedPlanRevision is null or 0
            : request.ExpectedPlanRevision is not null &&
              request.ExpectedPlanRevision == current.Plan.Revision;
        var digestMatches = current.Plan is null
            ? string.IsNullOrWhiteSpace(request.ExpectedPlanDigest)
            : !string.IsNullOrWhiteSpace(request.ExpectedPlanDigest) &&
              string.Equals(request.ExpectedPlanDigest, current.PlanDigest, StringComparison.Ordinal);
        if (flowMatches && revisionMatches && digestMatches)
        {
            result = WorkflowPlanStoreResult.Success();
            return true;
        }

        if (request.ConfirmOverwrite)
        {
            result = WorkflowPlanStoreResult.Success();
            return true;
        }

        result = WorkflowPlanStoreResult.Failure(
            "stale",
            "The committed flow or plan changed. Reload it, or explicitly confirm overwriting the stale revision.") with
        {
            Stale = true,
            RequiresOverwriteConfirmation = true,
            Snapshot = current,
        };
        return false;
    }

    private static bool TryParseFlow(
        string? markdown,
        out MauiFlow? flow,
        out string? canonicalMarkdown,
        out List<string> errors,
        out List<string> warnings)
    {
        flow = null;
        canonicalMarkdown = null;
        errors = [];
        warnings = [];
        if (string.IsNullOrWhiteSpace(markdown))
        {
            errors.Add("Flow Markdown is required.");
            return false;
        }
        if (Encoding.UTF8.GetByteCount(markdown) > MaxFileBytes)
        {
            errors.Add("Flow Markdown exceeds the 1 MB size limit.");
            return false;
        }

        var parsed = FlowMarkdown.Parse(markdown);
        if (!parsed.Ok || parsed.Flow is null)
        {
            errors.Add(parsed.Error ?? "Could not parse flow Markdown.");
            return false;
        }

        flow = parsed.Flow;
        var validation = MauiFlowValidator.Validate(flow);
        errors.AddRange(validation.Errors);
        warnings.AddRange(validation.Warnings);
        canonicalMarkdown = FlowMarkdown.Serialize(flow);
        return errors.Count == 0;
    }

    private static void ValidateAuthoringFlow(
        MauiFlow flow,
        MauiFlow? committed,
        List<string> errors,
        List<string> warnings)
    {
        var expectedSequence = 1;
        foreach (var step in flow.Steps)
        {
            if (step.Seq != expectedSequence)
                errors.Add($"step {step.Seq}: steps must use a contiguous sequence starting at 1.");
            expectedSequence++;
            if (step.Label is { Length: > 512 })
                errors.Add($"step {step.Seq}: label is too long.");
            if (step.Intent is { Length: > 2_000 })
                errors.Add($"step {step.Seq}: intent is too long.");

            ValidateSelector(
                EffectiveSelector(step),
                FindCommittedSelector(committed, step.Seq),
                $"step {step.Seq}",
                errors,
                warnings);
            foreach (var assertion in step.Asserts ?? [])
            {
                ValidateSelector(
                    assertion.Selector,
                    FindCommittedAssertionSelector(committed, step.Seq, assertion.Kind, assertion.Name),
                    $"step {step.Seq} assertion {assertion.Kind}",
                    errors,
                    warnings);
            }
        }
    }

    private static FlowSelector? FindCommittedSelector(MauiFlow? flow, int sequence)
        => flow?.Steps.FirstOrDefault(step => step.Seq == sequence) is { } step
            ? EffectiveSelector(step)
            : null;

    private static FlowSelector? EffectiveSelector(FlowStep step)
        => step.Args?.Selector is { IsEmpty: false } selector ? selector : step.Target;

    private static FlowSelector? FindCommittedAssertionSelector(
        MauiFlow? flow,
        int sequence,
        string? kind,
        string? name)
        => flow?.Steps.FirstOrDefault(step => step.Seq == sequence)?.Asserts?
            .FirstOrDefault(assertion =>
                string.Equals(assertion.Kind, kind, StringComparison.Ordinal) &&
                string.Equals(assertion.Name, name, StringComparison.Ordinal))?.Selector;

    private static void ValidateSelector(
        FlowSelector? selector,
        FlowSelector? committed,
        string context,
        List<string> errors,
        List<string> warnings)
    {
        if (selector is null || selector.IsEmpty)
            return;

        var forms = 0;
        if (!string.IsNullOrWhiteSpace(selector.AutomationId)) forms++;
        if (!string.IsNullOrWhiteSpace(selector.Text)) forms++;
        if (!string.IsNullOrWhiteSpace(selector.Id)) forms++;
        if (selector.TypeIndex is not null ||
            (string.Equals(selector.SelectorKind, "typeIndex", StringComparison.Ordinal) &&
             !string.IsNullOrWhiteSpace(selector.Type) && selector.Index is not null))
        {
            forms++;
        }
        if (forms != 1)
            errors.Add($"{context}: exactly one active selector is required.");
        if (selector.MatchCount is not null && selector.MatchCount != 1)
            errors.Add($"{context}: selector must resolve exactly one element; it currently reports {selector.MatchCount} matches.");
        if (string.Equals(selector.Quality, "ambiguous", StringComparison.OrdinalIgnoreCase))
            errors.Add($"{context}: ambiguous selectors cannot be saved.");

        var rawRuntimeId = !string.IsNullOrWhiteSpace(selector.Id) &&
            string.IsNullOrWhiteSpace(selector.AutomationId) &&
            string.IsNullOrWhiteSpace(selector.Text) &&
            selector.TypeIndex is null;
        var unchangedRawRuntimeId = rawRuntimeId &&
            committed is not null &&
            string.Equals(selector.Id, committed.Id, StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(committed.AutomationId) &&
            string.IsNullOrWhiteSpace(committed.Text) &&
            committed.TypeIndex is null;
        if (rawRuntimeId && !unchangedRawRuntimeId)
            errors.Add($"{context}: a raw runtime ID cannot be promoted into an authored selector.");
        else if (rawRuntimeId)
            warnings.Add($"{context}: retained legacy runtime-ID selector is fragile and should be replaced with a durable selector.");
        else if (FlowSelector.IsFragile(selector))
            warnings.Add($"{context}: selector is fragile.");
    }

    private static bool TryPreparePlan(
        string? planJson,
        string flowName,
        string flowDigest,
        string? existingPlanId,
        int? existingRevision,
        out MauiTestPlan? plan,
        out string? canonicalPlan,
        out List<string> errors,
        out List<string> warnings)
    {
        plan = null;
        canonicalPlan = null;
        errors = [];
        warnings = [];
        if (string.IsNullOrWhiteSpace(planJson))
        {
            errors.Add("Plan JSON is required.");
            return false;
        }
        if (Encoding.UTF8.GetByteCount(planJson) > MaxFileBytes)
        {
            errors.Add("Plan JSON exceeds the 1 MB size limit.");
            return false;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(planJson) as JsonObject;
        }
        catch (JsonException ex)
        {
            errors.Add($"Invalid plan JSON: {ex.Message}");
            return false;
        }
        if (root is null)
        {
            errors.Add("Plan JSON must be an object.");
            return false;
        }

        root["schema"] = 1;
        var planId = root["planId"]?.GetValue<string>();
        root["planId"] = string.IsNullOrWhiteSpace(planId)
            ? existingPlanId ?? "plan_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant()
            : planId.Trim();
        root["revision"] = Math.Max(1, (existingRevision ?? 0) + 1);
        var flow = root["flow"] as JsonObject ?? new JsonObject();
        flow["path"] = flowName;
        flow["digest"] = flowDigest;
        root["flow"] = flow;

        canonicalPlan = CanonicalizeJson(root, indented: true);
        var validation = MauiTestPlanValidator.ValidateJson(canonicalPlan, out plan);
        errors.AddRange(validation.Errors);
        warnings.AddRange(validation.Warnings);
        if (plan?.Flow is not null)
        {
            if (!string.Equals(plan.Flow.Path, flowName, StringComparison.Ordinal))
                errors.Add("flow.path must reference the canonical Markdown filename.");
            if (!string.Equals(plan.Flow.Digest, flowDigest, StringComparison.Ordinal))
                errors.Add("flow.digest must match the canonical flow digest.");
        }
        return errors.Count == 0;
    }

    private static void AddPlanCoverageWarnings(MauiFlow flow, MauiTestPlan plan, List<string> warnings)
    {
        var linked = new HashSet<string>(
            flow.Steps
                .SelectMany(static step => step.AcceptanceCriterionIds ?? [])
                .Where(static id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.Ordinal);
        foreach (var criterion in plan.AcceptanceCriteria.Where(static criterion => criterion.Required))
        {
            if (!string.IsNullOrWhiteSpace(criterion.CriterionId) && !linked.Contains(criterion.CriterionId))
                warnings.Add($"Plan coverage gap: required acceptance criterion '{criterion.CriterionId}' is not linked from any flow step.");
        }
    }

    private bool TryEnsureWorkflowRoot(bool create, out string? error)
    {
        error = null;
        try
        {
            if (!Directory.Exists(_projectRoot))
            {
                error = "The project root is unavailable.";
                return false;
            }
            if (IsReparsePoint(_projectRoot))
            {
                error = "The project root cannot be a symbolic link or reparse point.";
                return false;
            }
            if (!Directory.Exists(_workflowRoot))
            {
                if (!create)
                {
                    error = "The project maui-tests directory does not exist.";
                    return false;
                }
                Directory.CreateDirectory(_workflowRoot);
            }
            if (IsReparsePoint(_workflowRoot) || PathContainsReparsePoint(_projectRoot, _workflowRoot))
            {
                error = "The project workflow directory cannot be a symbolic link or reparse point.";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = "The project workflow directory could not be resolved safely.";
            return false;
        }
    }

    private bool TryReadSafeFile(string path, out string? content, out string? error)
    {
        content = null;
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = "The selected workflow artifact no longer exists.";
                return false;
            }
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                PathContainsReparsePoint(_workflowRoot, path))
            {
                error = "Workflow artifacts reached through symbolic links or reparse points cannot be loaded.";
                return false;
            }
            if (info.Length > MaxFileBytes)
            {
                error = "Workflow artifacts larger than 1 MB cannot be loaded.";
                return false;
            }
            content = File.ReadAllText(path, Encoding.UTF8);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            error = "Could not read the workflow artifact.";
            return false;
        }
    }

    private bool WriteBundle(IReadOnlyList<(string Path, string Content)> files, out string? error)
        => WriteBundle(files, IsExpectedTopLevelPath, out error);

    private bool WriteBundle(
        IReadOnlyList<(string Path, string Content)> files,
        Func<string, bool> isExpectedPath,
        out string? error)
    {
        error = null;
        if (!TryEnsureWorkflowRoot(create: true, out error))
            return false;

        var staged = new List<StagedFile>(files.Count);
        var preserveBackups = false;
        try
        {
            foreach (var (path, content) in files)
            {
                if (!isExpectedPath(path) ||
                    (File.Exists(path) && (IsReparsePoint(path) || PathContainsReparsePoint(_workflowRoot, path))))
                {
                    error = "Refusing to write through a symbolic link, reparse point, or non-canonical path.";
                    return false;
                }
                if (Encoding.UTF8.GetByteCount(content) > MaxFileBytes)
                {
                    error = "The workflow bundle exceeds the 1 MB per-file limit.";
                    return false;
                }

                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.Write(content);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                staged.Add(new StagedFile(path, temporary));
            }

            foreach (var item in staged)
            {
                if (!File.Exists(item.Path))
                    continue;
                item.BackupPath = item.Path + "." + Guid.NewGuid().ToString("N") + ".bak";
                File.Move(item.Path, item.BackupPath);
            }
            foreach (var item in staged)
            {
                File.Move(item.TemporaryPath, item.Path);
                item.Committed = true;
            }
            foreach (var item in staged)
            {
                if (item.BackupPath is not null)
                    File.Delete(item.BackupPath);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var restored = RollBack(staged);
            preserveBackups = !restored;
            error = restored
                ? "The flow and plan were not saved; the previous committed bundle was restored."
                : "The bundle write failed and the previous revision could not be fully restored.";
            return false;
        }
        finally
        {
            foreach (var item in staged)
            {
                try { File.Delete(item.TemporaryPath); } catch { }
                try
                {
                    if (!preserveBackups && item.BackupPath is not null && File.Exists(item.BackupPath))
                        File.Delete(item.BackupPath);
                }
                catch { }
            }
        }
    }

    private static bool RollBack(IEnumerable<StagedFile> staged)
    {
        var restored = true;
        foreach (var item in staged.Reverse())
        {
            try
            {
                if (item.Committed && File.Exists(item.Path))
                    File.Delete(item.Path);
                if (item.BackupPath is not null && File.Exists(item.BackupPath))
                    File.Move(item.BackupPath, item.Path);
            }
            catch
            {
                restored = false;
            }
        }
        return restored;
    }

    private string FlowPath(string flowName) => Path.Combine(_workflowRoot, flowName);

    private string SidecarPath(string flowName)
        => Path.Combine(_workflowRoot, Path.GetFileNameWithoutExtension(flowName) + ".maui-plan.json");

    private bool IsExpectedTopLevelPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return IsUnderRoot(full, _workflowRoot) &&
                string.Equals(Path.GetDirectoryName(full), _workflowRoot, PathComparison) &&
                (full.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                 full.EndsWith(".maui-plan.json", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool TryValidateFlowName(string? value, out string? name, out string? error)
    {
        name = value?.Trim();
        error = null;
        if (string.IsNullOrWhiteSpace(name) ||
            name.Length > 255 ||
            name.IndexOfAny(['\\', '/']) >= 0 ||
            !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) ||
            !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            error = "name must be a top-level Markdown filename.";
            return false;
        }
        return true;
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !string.IsNullOrWhiteSpace(relative) &&
            !relative.Equals("..", PathComparison) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison) &&
            !Path.IsPathRooted(relative);
    }

    private static bool PathContainsReparsePoint(string root, string path)
    {
        var current = Path.GetFullPath(root);
        var target = Path.GetFullPath(path);
        if (!IsUnderRoot(target, current) && !string.Equals(target, current, PathComparison))
            return true;
        var relative = Path.GetRelativePath(current, target);
        foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current))
            {
                if (IsReparsePoint(current))
                    return true;
            }
        }
        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static string ComputeDigest(string json)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalizeJson(json, indented: false)))).ToLowerInvariant();

    private static string CanonicalizeJson(JsonNode node, bool indented)
        => CanonicalizeJson(node.ToJsonString(), indented);

    private static string CanonicalizeJson(string json, bool indented)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
        {
            WriteCanonicalJson(writer, document.RootElement);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
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

    private static string DeterministicDiff(string oldName, string newName, string before, string after)
    {
        var oldLines = before.ReplaceLineEndings("\n").Split('\n');
        var newLines = after.ReplaceLineEndings("\n").Split('\n');
        if (oldLines.SequenceEqual(newLines, StringComparer.Ordinal))
            return string.Empty;

        const int maxLines = 4_096;
        if (oldLines.Length > maxLines || newLines.Length > maxLines)
        {
            return $"--- {oldName}\n+++ {newName}\n@@ files differ; deterministic line diff omitted because it exceeds {maxLines} lines @@";
        }

        var lengths = new int[oldLines.Length + 1, newLines.Length + 1];
        for (var oldIndex = oldLines.Length - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = newLines.Length - 1; newIndex >= 0; newIndex--)
            {
                lengths[oldIndex, newIndex] = string.Equals(oldLines[oldIndex], newLines[newIndex], StringComparison.Ordinal)
                    ? lengths[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(lengths[oldIndex + 1, newIndex], lengths[oldIndex, newIndex + 1]);
            }
        }

        var builder = new StringBuilder()
            .Append("--- ").Append(oldName).Append('\n')
            .Append("+++ ").Append(newName).Append('\n')
            .Append("@@\n");
        var i = 0;
        var j = 0;
        while (i < oldLines.Length || j < newLines.Length)
        {
            if (i < oldLines.Length && j < newLines.Length &&
                string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal))
            {
                builder.Append(' ').AppendLine(oldLines[i]);
                i++;
                j++;
            }
            else if (j < newLines.Length && (i == oldLines.Length || lengths[i, j + 1] >= lengths[i + 1, j]))
            {
                builder.Append('+').AppendLine(newLines[j++]);
            }
            else
            {
                builder.Append('-').AppendLine(oldLines[i++]);
            }
        }
        return builder.ToString();
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed class StagedFile
    {
        public StagedFile(string path, string temporaryPath)
        {
            Path = path;
            TemporaryPath = temporaryPath;
        }

        public string Path { get; }
        public string TemporaryPath { get; }
        public string? BackupPath { get; set; }
        public bool Committed { get; set; }
    }
}

internal record WorkflowPlanSaveRequest
{
    public string? FlowName { get; init; }
    public string? Markdown { get; init; }
    public string? PlanJson { get; init; }
    public int? ExpectedPlanRevision { get; init; }
    public string? ExpectedPlanDigest { get; init; }
    public string? ExpectedFlowDigest { get; init; }
    public bool ConfirmOverwrite { get; init; }
}

internal sealed record WorkflowBundleCommitRequest : WorkflowPlanSaveRequest
{
}

internal sealed record WorkflowAuthoringSnapshot
{
    public string? Name { get; init; }
    public string? Markdown { get; init; }
    public MauiFlow? Flow { get; init; }
    public string? FlowDigest { get; init; }
    public string? PlanJson { get; init; }
    public MauiTestPlan? Plan { get; init; }
    public string? PlanDigest { get; init; }
}

internal sealed record WorkflowPlanListItem
{
    public string? Name { get; init; }
    public string? SidecarName { get; init; }
    public bool HasPlan { get; init; }
    public long Size { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
}

internal sealed record WorkflowPlanStoreResult
{
    public bool Ok { get; init; }
    public string? Code { get; init; }
    public string? Error { get; init; }
    public bool Stale { get; init; }
    public bool RequiresOverwriteConfirmation { get; init; }
    public WorkflowAuthoringSnapshot? Snapshot { get; init; }
    public IReadOnlyList<WorkflowPlanListItem> Items { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? Diff { get; init; }

    public static WorkflowPlanStoreResult Success() => new() { Ok = true };
    public static WorkflowPlanStoreResult Failure(string code, string error) => new()
    {
        Ok = false,
        Code = code,
        Error = error,
    };
}

internal sealed class WorkflowRepairFlowApplyRequest
{
    public MauiFlowRepairProposal? Proposal { get; init; }
    public string? ExpectedFlowDigest { get; init; }
    public int? ExpectedFlowRevision { get; init; }
    public string? Reviewer { get; init; }
    public string? GrantDigest { get; init; }
    public IReadOnlyList<string> ValidationRunIds { get; init; } = [];
    public DateTimeOffset? RecordedAt { get; init; }
}

internal sealed class WorkflowRepairFlowRollbackRequest
{
    public MauiFlowRepairProposal? Proposal { get; init; }
    public string? ExpectedAppliedFlowDigest { get; init; }
    public int? ExpectedAppliedFlowRevision { get; init; }
    public string? Reviewer { get; init; }
    public string? GrantDigest { get; init; }
    public IReadOnlyList<string> VerificationRunIds { get; init; } = [];
    public DateTimeOffset? RecordedAt { get; init; }
}

internal sealed class WorkflowRepairFlowApplyResult
{
    public bool Ok { get; private init; }
    public bool Stale { get; private init; }
    public string? Code { get; private init; }
    public string? Error { get; private init; }
    public string? FlowId { get; private init; }
    public int? FlowRevision { get; private init; }
    public string? FlowDigest { get; private init; }
    public string? HistoryPath { get; private init; }

    public static WorkflowRepairFlowApplyResult Success(
        string flowId,
        int revision,
        string digest,
        string historyPath) => new()
    {
        Ok = true,
        FlowId = flowId,
        FlowRevision = revision,
        FlowDigest = digest,
        HistoryPath = historyPath,
    };

    public static WorkflowRepairFlowApplyResult Failure(
        string code,
        string error,
        bool stale = false) => new()
    {
        Code = code,
        Error = error,
        Stale = stale,
    };
}

internal sealed class WorkflowRepairHistoryEvent
{
    public MauiFlowRepairProposal Proposal { get; init; } = new();
    public string State { get; init; } = "";
    public int? NewFlowRevision { get; init; }
    public int? RollbackRevision { get; init; }
    public string? Reviewer { get; init; }
    public string? GrantDigest { get; init; }
    public IEnumerable<string>? ValidationRunIds { get; init; }
    public IEnumerable<string>? VerificationRunIds { get; init; }
    public string? ReasonCode { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
}

internal sealed class WorkflowRepairHistoryAppendRequest
{
    public MauiFlowRepairProposal? Proposal { get; init; }
    public string State { get; init; } = "";
    public int? NewFlowRevision { get; init; }
    public int? RollbackRevision { get; init; }
    public string? Reviewer { get; init; }
    public string? GrantDigest { get; init; }
    public IReadOnlyList<string> ValidationRunIds { get; init; } = [];
    public IReadOnlyList<string> VerificationRunIds { get; init; } = [];
    public string? ReasonCode { get; init; }
    public DateTimeOffset? RecordedAt { get; init; }
}

internal sealed class WorkflowRepairHistoryAppendResult
{
    public bool Ok { get; private init; }
    public string? Code { get; private init; }
    public string? Error { get; private init; }
    public string? HistoryPath { get; private init; }

    public static WorkflowRepairHistoryAppendResult Success(string historyPath) => new()
    {
        Ok = true,
        HistoryPath = historyPath,
    };

    public static WorkflowRepairHistoryAppendResult Failure(string code, string error) => new()
    {
        Code = code,
        Error = error,
    };
}

internal readonly record struct WorkflowFlowIdentity(string? FlowId, int? Revision);
