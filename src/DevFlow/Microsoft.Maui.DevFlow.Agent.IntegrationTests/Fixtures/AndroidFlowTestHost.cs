using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Agent.IntegrationTests.Fixtures;

/// <summary>
/// Fixture-owned Android flow path. It prepares host/device state, then invokes the one public
/// <see cref="MauiFlowRunner"/> implementation without reimplementing flow semantics.
/// </summary>
internal sealed class AndroidFlowTestHost : IAsyncDisposable
{
    /// <summary>Route the harness verifies when a plan declares no checkpoint route.</summary>
    internal const string DefaultCheckpointRoute = "//native";

    readonly IPlatformFlowTestLifecycle _lifecycle;
    readonly IMauiFlowDriver _driver;
    readonly AgentClient? _agentClient;
    bool _disposed;

    public AndroidFlowTestHost(AndroidEmulatorFixture fixture)
        : this(
            fixture ?? throw new ArgumentNullException(nameof(fixture)),
            new AgentClientMauiFlowDriver(fixture.Client),
            fixture.Client)
    {
    }

    internal AndroidFlowTestHost(
        IPlatformFlowTestLifecycle lifecycle,
        IMauiFlowDriver driver,
        AgentClient? agentClient = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _agentClient = agentClient;
    }

    public async Task<AndroidFlowRunResult> RunAsync(
        MauiFlow flow,
        MauiTestPlan plan,
        AndroidFlowRunRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(plan);

        request ??= new AndroidFlowRunRequest();
        var runId = string.IsNullOrWhiteSpace(request.RunId)
            ? $"android-{Guid.NewGuid():N}"
            : request.RunId;
        var artifactRoot = request.ArtifactRoot ?? Path.Combine(
            AppFixtureBase.FindRepoRoot(),
            "artifacts",
            "TestResults",
            "devflow-flow",
            "android");
        var artifacts = new List<MauiFlowArtifactReference>();
        PlatformCheckpointVerification? verification = null;
        MauiFlowResetResult? reset = null;

        try
        {
            await _lifecycle.BuildAsync(cancellationToken).ConfigureAwait(false);
            await _lifecycle.InstallAsync(cancellationToken).ConfigureAwait(false);
            reset = await _lifecycle.HardResetAsync(request.Reset, cancellationToken).ConfigureAwait(false);
            if (string.Equals(reset.Strategy, "uninstall-reinstall", StringComparison.Ordinal))
                await _lifecycle.InstallAsync(cancellationToken).ConfigureAwait(false);

            var launch = await _lifecycle.LaunchAsync(cancellationToken).ConfigureAwait(false);
            await _lifecycle.WaitForExpectedAgentAsync(new PlatformAgentExpectation
            {
                PackageId = launch.PackageId,
                ExpectedProcessId = launch.ProcessId,
                PreviousAgent = launch.PreviousAgent,
                Timeout = request.AgentReadyTimeout,
            }, cancellationToken).ConfigureAwait(false);
            var seed = await _lifecycle.SeedAsync(request.Seed, cancellationToken).ConfigureAwait(false);
            verification = await _lifecycle.VerifyCheckpointAsync(new PlatformCheckpointRequest
            {
                Expected = request.ExpectedCheckpoint ?? new MauiFlowCheckpoint { Route = DefaultCheckpointRoute },
                Reset = reset,
                Seed = seed,
            }, cancellationToken).ConfigureAwait(false);

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
            var diagnostics = await CaptureDiagnosticsAsync(artifactRoot, runId, "flow-complete", cancellationToken).ConfigureAwait(false);
            artifacts.AddRange(diagnostics);
            AppendArtifactsAndPersist(report, artifactRoot, diagnostics);
            artifacts.AddRange(report.Artifacts);
            return new AndroidFlowRunResult(report, artifacts);
        }
        catch (PlatformFlowLifecycleException ex)
        {
            var diagnostics = await CaptureDiagnosticsAsync(
                artifactRoot,
                runId,
                AndroidLifecycleDiagnosticRedactor.Sanitize(
                    ex.Message,
                    AndroidFixtureInitializationDiagnostics.MaxSafeErrorTextCharacters),
                CancellationToken.None).ConfigureAwait(false);
            artifacts.AddRange(diagnostics);
            var report = CreateLifecycleFailureReport(flow, plan, runId, reset, verification, ex, artifacts);
            Persist(report, artifactRoot);
            artifacts.AddRange(report.Artifacts);
            return new AndroidFlowRunResult(report, artifacts);
        }
        catch (Exception ex)
        {
            var safeError = AndroidLifecycleDiagnosticRedactor.Sanitize(
                ex.Message,
                AndroidFixtureInitializationDiagnostics.MaxSafeErrorTextCharacters);
            var diagnostics = await CaptureDiagnosticsAsync(
                artifactRoot,
                runId,
                safeError,
                CancellationToken.None).ConfigureAwait(false);
            artifacts.AddRange(diagnostics);
            var failure = PlatformFlowLifecycleException.Infrastructure(
                $"Android lifecycle failed unexpectedly: {safeError}",
                ex);
            var report = CreateLifecycleFailureReport(flow, plan, runId, reset, verification, failure, artifacts);
            Persist(report, artifactRoot);
            artifacts.AddRange(report.Artifacts);
            return new AndroidFlowRunResult(report, artifacts);
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
            // Diagnostics must not hide the primary lifecycle failure.
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
        var now = DateTimeOffset.UtcNow;
        var safeMessage = AndroidLifecycleDiagnosticRedactor.Sanitize(
            exception.Message,
            AndroidFixtureInitializationDiagnostics.MaxSafeErrorTextCharacters);
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
                Summary = safeMessage,
                Terminal = true,
                Verified = false,
                VerificationReason = "Replay did not start because Android lifecycle preconditions failed.",
            },
            Verification = new MauiFlowRunVerification
            {
                Verified = false,
                Reason = "Replay did not start because Android lifecycle preconditions failed.",
                CheckedAt = now,
            },
            Failure = new MauiFlowFailure
            {
                FailureId = $"failure-{runId}",
                Class = isInfrastructure ? MauiFlowFailureClasses.Infrastructure : MauiFlowFailureClasses.PreconditionUnsatisfied,
                Code = isInfrastructure ? MauiFlowFailureClasses.Infrastructure : MauiFlowFailureClasses.PreconditionUnsatisfied,
                Category = isInfrastructure ? "infrastructure" : "precondition",
                Phase = "android-lifecycle",
                Retryable = false,
                RepairEligible = false,
                Message = safeMessage,
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
                    Message = safeMessage,
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
                Reason = write.Error ?? "The Android host could not persist the run report.",
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

internal sealed class AndroidFlowRunRequest
{
    public string? RunId { get; init; }
    public string? ArtifactRoot { get; init; }
    /// <summary>Optional redacted, failure-only evidence capture supplied by an integration host.</summary>
    public IFlowReplayEvidenceCapture? FailureEvidenceCapture { get; init; }
    public PlatformFlowResetRequest Reset { get; init; } = new();
    public PlatformFlowSeedRequest Seed { get; init; } = new();
    public MauiFlowCheckpoint? ExpectedCheckpoint { get; init; }
    public TimeSpan AgentReadyTimeout { get; init; } = TimeSpan.FromSeconds(120);
}

internal sealed class AndroidFlowRunResult
{
    public AndroidFlowRunResult(MauiFlowRunReport report, IReadOnlyList<MauiFlowArtifactReference> artifacts)
    {
        Report = report;
        Artifacts = artifacts;
    }

    public MauiFlowRunReport Report { get; }
    public IReadOnlyList<MauiFlowArtifactReference> Artifacts { get; }
}
