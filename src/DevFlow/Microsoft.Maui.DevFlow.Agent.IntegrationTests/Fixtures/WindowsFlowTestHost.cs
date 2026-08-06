using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Agent.IntegrationTests.Fixtures;

/// <summary>
/// Fixture-owned Windows flow host. It owns only lifecycle and admission facts, then invokes the
/// one public <see cref="MauiFlowRunner"/> implementation for every semantic action.
/// </summary>
internal sealed class WindowsFlowTestHost : IAsyncDisposable
{
    readonly IPlatformFlowTestLifecycle _lifecycle;
    readonly IMauiFlowDriver _driver;
    readonly AgentClient? _agentClient;
    bool _disposed;

    public WindowsFlowTestHost(WindowsFixture fixture)
        : this(
            fixture ?? throw new ArgumentNullException(nameof(fixture)),
            new AgentClientMauiFlowDriver(fixture.Client),
            fixture.Client)
    {
    }

    internal WindowsFlowTestHost(
        IPlatformFlowTestLifecycle lifecycle,
        IMauiFlowDriver driver,
        AgentClient? agentClient = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _agentClient = agentClient;
    }

    public async Task<WindowsFlowRunResult> RunAsync(
        MauiFlow flow,
        MauiTestPlan plan,
        WindowsFlowRunRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(plan);

        request ??= new WindowsFlowRunRequest();
        var runId = string.IsNullOrWhiteSpace(request.RunId)
            ? $"windows-{Guid.NewGuid():N}"
            : request.RunId;
        var artifactRoot = request.ArtifactRoot ?? Path.Combine(
            AppFixtureBase.FindRepoRoot(),
            "artifacts",
            "TestResults",
            "devflow-flow",
            "windows");
        var artifacts = new List<MauiFlowArtifactReference>();
        PlatformCheckpointVerification? verification = null;
        MauiFlowResetResult? reset = null;

        try
        {
            ValidateStaticCompatibility(flow, plan);
            await _lifecycle.BuildAsync(cancellationToken).ConfigureAwait(false);
            await _lifecycle.InstallAsync(cancellationToken).ConfigureAwait(false);
            reset = await _lifecycle.HardResetAsync(request.Reset, cancellationToken).ConfigureAwait(false);
            if (string.Equals(reset.Strategy, "uninstall-reinstall", StringComparison.Ordinal))
                await _lifecycle.InstallAsync(cancellationToken).ConfigureAwait(false);

            var launch = await _lifecycle.LaunchAsync(cancellationToken).ConfigureAwait(false);
            var ready = await _lifecycle.WaitForExpectedAgentAsync(new PlatformAgentExpectation
            {
                PackageId = launch.PackageId,
                ExpectedProcessId = launch.ProcessId,
                PreviousAgent = launch.PreviousAgent,
                Timeout = request.AgentReadyTimeout,
            }, cancellationToken).ConfigureAwait(false);
            ValidateRuntimeCapabilities(ready.Status, plan);

            var seed = await _lifecycle.SeedAsync(request.Seed, cancellationToken).ConfigureAwait(false);
            verification = await _lifecycle.VerifyCheckpointAsync(new PlatformCheckpointRequest
            {
                Expected = request.ExpectedCheckpoint ?? new MauiFlowCheckpoint { Route = "//native" },
                Reset = reset,
                Seed = seed,
            }, cancellationToken).ConfigureAwait(false);

            // The runner receives host-verified reset, checkpoint, and independent-oracle facts.
            // It owns validation, strict selector resolution, replay safety, and report semantics.
            using var retryScope = new NoMutatingRetryScope(_agentClient);
            var runner = new MauiFlowRunner(_driver, new MauiFlowRunnerOptions
            {
                RunId = runId,
                Target = verification.Target,
                Reset = reset,
                Plan = plan,
                RunContext = verification.RunContext,
                ArtifactRoot = artifactRoot,
            }, request.FailureEvidenceCapture);
            var report = await runner.RunAsync(flow, cancellationToken).ConfigureAwait(false);
            var diagnostics = await CaptureDiagnosticsAsync(
                artifactRoot,
                runId,
                "flow-complete",
                cancellationToken).ConfigureAwait(false);
            artifacts.AddRange(diagnostics);
            AppendArtifactsAndPersist(report, artifactRoot, diagnostics);
            artifacts.AddRange(report.Artifacts);
            return new WindowsFlowRunResult(report, artifacts);
        }
        catch (PlatformFlowLifecycleException ex)
        {
            var diagnostics = await CaptureDiagnosticsAsync(
                artifactRoot,
                runId,
                ex.Message,
                CancellationToken.None).ConfigureAwait(false);
            artifacts.AddRange(diagnostics);
            var report = CreateLifecycleFailureReport(flow, plan, runId, reset, verification, ex, artifacts);
            Persist(report, artifactRoot);
            artifacts.AddRange(report.Artifacts);
            return new WindowsFlowRunResult(report, artifacts);
        }
        catch (Exception ex)
        {
            var diagnostics = await CaptureDiagnosticsAsync(
                artifactRoot,
                runId,
                ex.Message,
                CancellationToken.None).ConfigureAwait(false);
            artifacts.AddRange(diagnostics);
            var failure = PlatformFlowLifecycleException.Infrastructure(
                $"Windows lifecycle failed unexpectedly: {ex.Message}",
                ex);
            var report = CreateLifecycleFailureReport(flow, plan, runId, reset, verification, failure, artifacts);
            Persist(report, artifactRoot);
            artifacts.AddRange(report.Artifacts);
            return new WindowsFlowRunResult(report, artifacts);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            await _lifecycle.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await _lifecycle.DisposeAsync().ConfigureAwait(false);
        }
    }

    static void ValidateStaticCompatibility(MauiFlow flow, MauiTestPlan plan)
    {
        if (!AllowsWindows(flow.Platform))
        {
            throw PlatformFlowLifecycleException.Capability(
                $"capability-missing: flow platform tag '{flow.Platform}' does not include windows.");
        }

        if (plan.RequiredPlatforms.Count > 0 &&
            !plan.RequiredPlatforms.Any(static platform =>
                string.Equals(platform, "windows", StringComparison.OrdinalIgnoreCase)))
        {
            throw PlatformFlowLifecycleException.Capability(
                "capability-missing: the test plan does not declare windows as a required platform.");
        }
    }

    static void ValidateRuntimeCapabilities(AgentStatus status, MauiTestPlan plan)
    {
        var capabilities = status.Capabilities;
        foreach (var requirement in plan.Requirements?.RequiredCapabilities ?? [])
        {
            if (!requirement.Required)
                continue;

            if (!IsCapabilityAvailable(requirement.Name, capabilities))
            {
                throw PlatformFlowLifecycleException.Capability(
                    $"capability-missing: required Windows capability '{requirement.Name ?? "<unnamed>"}' is unavailable.");
            }
        }
    }

    static bool IsCapabilityAvailable(string? capability, AgentCapabilities? available)
        => capability?.Trim() switch
        {
            "agent.ui" => available?.Ui == true,
            "agent.mutations" => available?.Mutations == true,
            "agent.workflowCommandLedger" => available?.WorkflowCommandLedger == true,
            "agent.webview" => available?.WebView == true,
            "agent.theme" => available?.Theme == true,
            _ => false,
        };

    static bool AllowsWindows(string? platformTag)
    {
        if (string.IsNullOrWhiteSpace(platformTag))
            return false;

        return platformTag.Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(static item =>
                string.Equals(item, "windows", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item, "all", StringComparison.OrdinalIgnoreCase));
    }

    async Task<IReadOnlyList<MauiFlowArtifactReference>> CaptureDiagnosticsAsync(
        string artifactRoot,
        string runId,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var diagnostics = await _lifecycle.CaptureHostDiagnosticsAsync(new PlatformDiagnosticsRequest
            {
                ArtifactRoot = artifactRoot,
                RunId = runId,
                Reason = reason,
            }, cancellationToken).ConfigureAwait(false);
            return diagnostics.Artifacts;
        }
        catch
        {
            // Diagnostics must not hide the primary lifecycle result.
            return [];
        }
    }

    static MauiFlowRunReport CreateLifecycleFailureReport(
        MauiFlow flow,
        MauiTestPlan plan,
        string runId,
        MauiFlowResetResult? reset,
        PlatformCheckpointVerification? verification,
        PlatformFlowLifecycleException exception,
        IReadOnlyList<MauiFlowArtifactReference> artifacts)
    {
        var context = verification?.RunContext ?? new MauiFlowRunContext
        {
            Intent = MauiFlowReplayIntents.OrdinaryReplay,
            Reset = reset,
        };
        var eligibility = MauiFlowReplaySafetyEvaluator.Evaluate(new MauiFlowRunRequest
        {
            Plan = plan,
            Context = context,
        });
        var isInfrastructure = exception.Kind == PlatformFlowLifecycleFailureKind.Infrastructure;
        var isCapability = exception.Kind == PlatformFlowLifecycleFailureKind.Capability;
        var code = isInfrastructure
            ? MauiFlowFailureClasses.Infrastructure
            : isCapability
                ? MauiFlowFailureClasses.CapabilityMissing
                : MauiFlowFailureClasses.PreconditionUnsatisfied;
        var now = DateTimeOffset.UtcNow;
        return new MauiFlowRunReport
        {
            RunId = runId,
            FlowId = $"sha256:{MauiFlowRunReportSerializer.ComputeFlowDigest(flow)}",
            FlowDigest = MauiFlowRunReportSerializer.ComputeFlowDigest(flow),
            LegacyFlowIdentity = flow.Name,
            Target = verification?.Target,
            Reset = reset,
            Preconditions = context.Preconditions,
            SideEffectPolicy = eligibility.SideEffectPolicy,
            ReplayEligibility = eligibility,
            StartedAt = now,
            EndedAt = now,
            Outcome = new MauiFlowRunOutcome
            {
                Status = isInfrastructure ? MauiFlowRunOutcomes.InfrastructureError : MauiFlowRunOutcomes.Failed,
                Summary = exception.Message,
                Terminal = true,
                Verified = false,
                VerificationReason = "Replay did not start because Windows lifecycle admission failed.",
            },
            Verification = new MauiFlowRunVerification
            {
                Verified = false,
                Reason = "Replay did not start because Windows lifecycle admission failed.",
                CheckedAt = now,
            },
            Failure = new MauiFlowFailure
            {
                FailureId = $"failure-{runId}",
                Class = code,
                Code = code,
                Category = isInfrastructure ? "infrastructure" : isCapability ? "capability" : "precondition",
                Phase = "windows-lifecycle",
                Retryable = false,
                RepairEligible = false,
                Message = exception.Message,
                At = now,
                Artifacts = artifacts.ToList(),
            },
            Events =
            [
                new MauiFlowRunEvent
                {
                    Sequence = 1,
                    At = now,
                    Kind = "lifecycle-failed",
                    Message = exception.Message,
                },
            ],
            Artifacts = artifacts.ToList(),
        };
    }

    static void AppendArtifactsAndPersist(
        MauiFlowRunReport report,
        string artifactRoot,
        IReadOnlyList<MauiFlowArtifactReference> diagnostics)
    {
        foreach (var artifact in diagnostics)
        {
            if (!report.Artifacts.Any(existing =>
                    string.Equals(existing.ArtifactId, artifact.ArtifactId, StringComparison.Ordinal)))
            {
                report.Artifacts.Add(artifact);
            }
        }

        Persist(report, artifactRoot);
    }

    static void Persist(MauiFlowRunReport report, string artifactRoot)
    {
        var write = MauiFlowRunReportSerializer.WriteAtomic(report, artifactRoot);
        if (write.Ok)
        {
            report.ReportPath = write.Path;
            report.ReportDigest = write.Digest;
        }
        else
        {
            report.Truncated = true;
            report.Omissions.Add(new MauiFlowReportOmission
            {
                Kind = "report-artifact",
                Reason = write.Error ?? "The Windows host could not persist the run report.",
            });
        }
    }

    sealed class NoMutatingRetryScope : IDisposable
    {
        readonly AgentClient? _client;
        readonly bool _previous;

        public NoMutatingRetryScope(AgentClient? client)
        {
            _client = client;
            _previous = client?.RetryMutatingRequests ?? false;
            if (_client is not null)
                _client.RetryMutatingRequests = false;
        }

        public void Dispose()
        {
            if (_client is not null)
                _client.RetryMutatingRequests = _previous;
        }
    }
}

internal sealed class WindowsFlowRunRequest
{
    public string? RunId { get; init; }
    public string? ArtifactRoot { get; init; }
    public IFlowReplayEvidenceCapture? FailureEvidenceCapture { get; init; }
    public PlatformFlowResetRequest Reset { get; init; } = new();
    public PlatformFlowSeedRequest Seed { get; init; } = new();
    public MauiFlowCheckpoint? ExpectedCheckpoint { get; init; }
    public TimeSpan AgentReadyTimeout { get; init; } = TimeSpan.FromSeconds(120);
}

internal sealed class WindowsFlowRunResult
{
    public WindowsFlowRunResult(MauiFlowRunReport report, IReadOnlyList<MauiFlowArtifactReference> artifacts)
    {
        Report = report;
        Artifacts = artifacts;
    }

    public MauiFlowRunReport Report { get; }
    public IReadOnlyList<MauiFlowArtifactReference> Artifacts { get; }
}
