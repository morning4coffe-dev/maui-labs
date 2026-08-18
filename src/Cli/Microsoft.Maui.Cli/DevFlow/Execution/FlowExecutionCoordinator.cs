using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.Cli.DevFlow.Evidence;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.Cli.DevFlow.Execution;

internal sealed class FlowExecutionCoordinator : IFlowExecutionCoordinator
{
    private readonly CommittedFlowBundleLoader _bundleLoader;
    private readonly IAppArtifactResolver _artifactResolver;
    private readonly IReadOnlyList<IFlowExecutionPlatformAdapter> _platformAdapters;
    private readonly IFlowStateEvidenceProviderRegistry _stateEvidenceProviders;
    private readonly ExactAgentBindingResolver _agentBindingResolver;
    private readonly FlowRunReportWriter _reportWriter;
    private readonly JUnitFlowExecutionWriter _junitWriter;
    private readonly ExecutionManifestWriter _manifestWriter;
    private readonly ImmutableExecutionOutputWriter _outputWriter;
    private readonly Func<Task<int?>> _ensureBrokerRunningAsync;
    private readonly Func<string, int, AgentClient> _agentClientFactory;
    private readonly TimeProvider _clock;
    private readonly IAppSourceIdentityProvider _appSourceIdentityProvider;
    private readonly Func<string>? _agentSessionIdFactory;

    public FlowExecutionCoordinator(
        CommittedFlowBundleLoader bundleLoader,
        IAppArtifactResolver artifactResolver,
        IEnumerable<IFlowExecutionPlatformAdapter> platformAdapters,
        IFlowStateEvidenceProviderRegistry stateEvidenceProviders,
        ExactAgentBindingResolver agentBindingResolver,
        FlowRunReportWriter reportWriter,
        JUnitFlowExecutionWriter junitWriter,
        ExecutionManifestWriter manifestWriter,
        ImmutableExecutionOutputWriter outputWriter,
        Func<Task<int?>> ensureBrokerRunningAsync,
        Func<string, int, AgentClient>? agentClientFactory = null,
        TimeProvider? clock = null,
        IAppSourceIdentityProvider? appSourceIdentityProvider = null,
        Func<string>? agentSessionIdFactory = null)
    {
        _bundleLoader = bundleLoader ?? throw new ArgumentNullException(nameof(bundleLoader));
        _artifactResolver = artifactResolver ?? throw new ArgumentNullException(nameof(artifactResolver));
        _platformAdapters = (platformAdapters ?? throw new ArgumentNullException(nameof(platformAdapters))).ToArray();
        _stateEvidenceProviders = stateEvidenceProviders ?? throw new ArgumentNullException(nameof(stateEvidenceProviders));
        _agentBindingResolver = agentBindingResolver ?? throw new ArgumentNullException(nameof(agentBindingResolver));
        _reportWriter = reportWriter ?? throw new ArgumentNullException(nameof(reportWriter));
        _junitWriter = junitWriter ?? throw new ArgumentNullException(nameof(junitWriter));
        _manifestWriter = manifestWriter ?? throw new ArgumentNullException(nameof(manifestWriter));
        _outputWriter = outputWriter ?? throw new ArgumentNullException(nameof(outputWriter));
        _ensureBrokerRunningAsync = ensureBrokerRunningAsync ?? throw new ArgumentNullException(nameof(ensureBrokerRunningAsync));
        _agentClientFactory = agentClientFactory ?? ((host, port) => new AgentClient(host, port));
        _clock = clock ?? TimeProvider.System;
        _appSourceIdentityProvider = appSourceIdentityProvider ?? new NullAppSourceIdentityProvider();
        _agentSessionIdFactory = agentSessionIdFactory;
    }

    public async Task<FlowExecutionResult> RunAsync(
        FlowExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runId = CreateRunId();
        var agentSessionId = _agentSessionIdFactory is null
            ? CreateBuildScopedAgentSessionId(request)
            : _agentSessionIdFactory();
        if (string.IsNullOrWhiteSpace(agentSessionId) ||
            agentSessionId.Length > 64 ||
            agentSessionId.Any(static character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw FlowExecutionException.Infrastructure(
                "agent-session-id-invalid",
                "The execution host could not create a valid opaque agent session identity.");
        }
        var appSourceIdentity = new AppSourceIdentity();
        var outputDirectory = PrepareOutputDirectory(request.OutputDirectory, runId);
        var startedAt = _clock.GetUtcNow();
        var lifecycle = new MauiTestExecutionLifecycleFacts
        {
            StartedAt = startedAt,
            CleanupPolicy = request.CleanupPolicy,
        };

        CommittedFlowBundle? bundle = null;
        ResolvedAppArtifact? artifact = null;
        IFlowExecutionPlatformAdapter? adapter = null;
        FlowExecutionPlatformPreflight? platformPreflight = null;
        FlowExecutionPlatformSession? platformSession = null;
        FlowStateAdmission? stateAdmission = null;
        MauiFlowRunContext? runContext = null;
        MauiFlowRunReport? report = null;
        AgentStatus? liveStatus = null;
        AgentClient? client = null;
        FlowReplayEvidenceCapture? evidence = null;
        MauiFlowAppProcessEvidence? appProcess = null;
        var exitCategory = FlowExecutionExitCategories.InfrastructureFailure;
        var detailCode = "execution-not-started";
        var message = "The flow execution did not start.";
        var currentStage = "initialization";
        var infrastructurePhase = "execution";
        FlowExecutionDiagnosticsArtifact? diagnosticsArtifact = null;

        try
        {
            currentStage = "validate-request";
            ValidateRequest(request);
            adapter = SelectAdapter(_platformAdapters, request.Platform);
            adapter.ValidateHost();
            AddStage(lifecycle, currentStage, "passed", "request-valid", durationMs: 0);

            currentStage = "source-identity";
            appSourceIdentity = await StageAsync(
                lifecycle,
                currentStage,
                () => _appSourceIdentityProvider.ResolveAsync(request.ProjectPath, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            currentStage = "load-workflow";
            bundle = await StageAsync(
                lifecycle,
                currentStage,
                () => _bundleLoader.LoadAsync(request.FlowPath, request.PlanPath, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            currentStage = "validate-target";
            ValidateBundleTarget(bundle, adapter.Descriptor);
            AddStage(lifecycle, currentStage, "passed", "target-supported", durationMs: 0);

            currentStage = "device-admission";
            await StageAsync(
                lifecycle,
                currentStage,
                async () =>
                {
                    await adapter.ValidateDeviceAdmissionAsync(new FlowExecutionDeviceAdmissionRequest
                    {
                        DeclaredAppId = bundle.Flow.App,
                        DeviceSerial = request.DeviceSerial,
                    }, cancellationToken).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            currentStage = "resolve-artifact";
            artifact = await StageAsync(
                lifecycle,
                currentStage,
                () => _artifactResolver.ResolveAsync(new AppArtifactResolutionRequest
                {
                    ProjectPath = request.ProjectPath,
                    AgentSessionId = agentSessionId,
                    TargetFramework = request.TargetFramework,
                    Configuration = request.Configuration,
                    WorkDirectory = outputDirectory,
                    Platform = adapter.Descriptor.Platform,
                    TargetFrameworkPlatformIdentifiers = adapter.Descriptor.TargetFrameworkPlatformIdentifiers,
                    CandidateArtifactTypes = adapter.Descriptor.CandidateArtifactTypes,
                    UnsupportedArtifactTypes = adapter.Descriptor.UnsupportedArtifactTypes,
                    UnsupportedArtifactCode = adapter.Descriptor.UnsupportedArtifactCode,
                    UnsupportedArtifactMessage = adapter.Descriptor.UnsupportedArtifactMessage,
                    RuntimeIdentifier = adapter.GetDefaultRuntimeIdentifier(),
                }, cancellationToken),
                cancellationToken,
                ownedResource: true).ConfigureAwait(false);
            if (!string.Equals(artifact.AgentSessionId, agentSessionId, StringComparison.Ordinal))
            {
                throw FlowExecutionException.Infrastructure(
                    "artifact-agent-session-mismatch",
                    "The app artifact was not built for the invocation's opaque agent session identity.");
            }

            currentStage = "platform-preflight";
            platformPreflight = await StageAsync(
                lifecycle,
                currentStage,
                () => adapter.PreflightAsync(new FlowExecutionPlatformPreflightRequest
                {
                    Artifact = artifact,
                    DeviceSerial = request.DeviceSerial,
                }, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            currentStage = "preflight";
            stateAdmission = await StageAsync(
                lifecycle,
                currentStage,
                () => _stateEvidenceProviders.PrepareAsync(new FlowStateEvidenceRequest
                {
                    Plan = bundle.Plan,
                    Flow = bundle.Flow,
                    Artifact = artifact,
                }, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            runContext = stateAdmission.RunContext;

            currentStage = "broker-snapshot";
            var brokerPort = await _ensureBrokerRunningAsync().ConfigureAwait(false)
                ?? throw FlowExecutionException.Infrastructure(
                    "broker-start-failed",
                    "The DevFlow broker could not be started.");
            var snapshot = await StageAsync(
                lifecycle,
                currentStage,
                () => _agentBindingResolver.SnapshotAsync(brokerPort, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            currentStage = "platform-launch";
            platformSession = await StageAsync(
                lifecycle,
                currentStage,
                () => adapter.PrepareAndLaunchAsync(new FlowExecutionPlatformRequest
                {
                    Artifact = artifact,
                    Preflight = platformPreflight,
                    BrokerPort = brokerPort,
                }, cancellationToken),
                cancellationToken,
                ownedResource: true).ConfigureAwait(false);
            lifecycle.InstalledByInvocation = platformSession.InstalledByInvocation;
            lifecycle.LaunchedByInvocation = platformSession.LaunchedByInvocation;

            var expectation = new ExactAgentBindingExpectation
            {
                SessionId = artifact.AgentSessionId,
                TargetFramework = artifact.TargetFramework,
                Platform = adapter.Descriptor.Platform,
                PlatformAliases = adapter.Descriptor.AgentPlatformAliases,
                PackageId = platformSession.PackageId,
                DeviceSerial = platformSession.DeviceSerial,
                DeviceEmulatorId = platformSession.Device.EmulatorId,
                RequireDeviceIdentityMatch = platformSession.RequireAgentDeviceIdentity,
                ProcessId = platformSession.ProcessId,
            };
            currentStage = "bind-agent";
            var registration = await StageAsync(
                lifecycle,
                currentStage,
                () => _agentBindingResolver.WaitForNewAsync(
                    brokerPort,
                    snapshot,
                    expectation,
                    request.AgentWaitTimeout,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);

            currentStage = "agent-forward";
            await StageAsync(
                lifecycle,
                currentStage,
                async () =>
                {
                    await adapter.EstablishAgentForwardingAsync(
                        platformSession,
                        registration.Port,
                        brokerPort,
                        cancellationToken).ConfigureAwait(false);
                    return true;
                },
                cancellationToken,
                ownedResource: true).ConfigureAwait(false);

            client = _agentClientFactory(request.AgentHost, registration.Port);
            client.TransientFailureRetryCount = 2;
            client.RetryMutatingRequests = false;
            currentStage = "validate-agent";
            liveStatus = await StageAsync(
                lifecycle,
                currentStage,
                async () =>
                {
                    var status = await client.GetStatusAsync().ConfigureAwait(false);
                    ExactAgentBindingResolver.ValidateLiveStatus(status, expectation);
                    ValidatePlanRequirements(bundle.Plan, status!);
                    return status!;
                },
                cancellationToken).ConfigureAwait(false);

            currentStage = "verify-preconditions";
            await StageAsync(
                lifecycle,
                currentStage,
                () =>
                {
                    VerifyDeclaredPreconditions(bundle, runContext, liveStatus);
                    return Task.FromResult(true);
                },
                cancellationToken).ConfigureAwait(false);

            if (request.CaptureFailureEvidence)
            {
                evidence = new FlowReplayEvidenceCapture(
                    client,
                    Path.Combine(outputDirectory, "failure.mauitrace"),
                    Path.GetDirectoryName(bundle.FlowPath),
                    "flow-run",
                    request.CaptureFailureEvidenceScreenshot);
            }

            currentStage = "execute-flow";
            var runTarget = CreateRunTarget(
                platformSession,
                registration,
                artifact,
                liveStatus,
                appSourceIdentity);
            var runner = new MauiFlowRunner(
                new AgentClientMauiFlowDriver(client),
                new MauiFlowRunnerOptions
                {
                    RunId = runId,
                    FlowId = bundle.FlowId,
                    FlowRevision = bundle.FlowRevision,
                    FlowDigest = bundle.FlowDigest,
                    LegacyFlowIdentity = Path.GetFileName(bundle.FlowPath),
                    Plan = bundle.Plan,
                    RunContext = runContext,
                    ExpectedCheckpoint = runContext.Preconditions?.Expected,
                    Target = runTarget,
                    Clock = _clock,
                },
                evidence);
            var execution = await StageAsync(
                lifecycle,
                currentStage,
                () => runner.RunWithLegacyAsync(bundle.Flow, bundle.FlowPath, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            report = execution.Report;

            currentStage = "post-run-oracles";
            var executionEndedAt = report.EndedAt ?? _clock.GetUtcNow();
            var oracleResults = await StageAsync(
                lifecycle,
                currentStage,
                () => _stateEvidenceProviders.EvaluatePostRunAsync(
                    stateAdmission,
                    new FlowPostRunOracleEvaluationRequest
                    {
                        Plan = bundle.Plan,
                        Flow = bundle.Flow,
                        Artifact = artifact,
                        RunId = runId,
                        FlowDigest = bundle.FlowDigest,
                        DeviceIdentityFingerprint = runTarget.DeviceId!,
                        AppBuildFingerprint = runTarget.AppBuildFingerprint!,
                        PackageDigest = artifact.PackageDigest,
                        Platform = platformSession.Platform,
                        DeviceSerial = platformSession.DeviceSerial,
                        PackageId = platformSession.PackageId,
                        StartedAt = report.StartedAt ?? startedAt,
                        EndedAt = executionEndedAt,
                        EvaluationDeadline = _clock.GetUtcNow() + TimeSpan.FromMinutes(1),
                        Report = report,
                    },
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
            runContext.BusinessOracles = oracleResults.ToList();
            ApplyPostRunVerification(report, bundle.Plan, bundle.Flow, runContext, _clock.GetUtcNow());

            exitCategory = ClassifyReport(report);
            detailCode = report.Failure?.Code ?? exitCategory;
            // The replay's own summary ("Flow replay passed.") is true but, on its own, reads as a
            // contradiction next to ok:false. Lead with why the run is unverified and keep the
            // replay summary as context, so the operator sees an actionable reason rather than a
            // pass that failed.
            message = exitCategory == FlowExecutionExitCategories.Unverified
                ? ComposeUnverifiedMessage(report)
                : report.Outcome?.Summary ?? MessageForCategory(exitCategory);
        }
        catch (FlowExecutionPlatformLaunchException ex)
        {
            platformSession = ex.Session;
            lifecycle.InstalledByInvocation = platformSession.InstalledByInvocation;
            lifecycle.LaunchedByInvocation = platformSession.LaunchedByInvocation;
            exitCategory = ex.ExitCategory;
            detailCode = ex.Code;
            message = BoundMessage(ex.Message);
            diagnosticsArtifact = ex.DiagnosticsArtifact;
            infrastructurePhase = currentStage;
            AddFailedStageIfMissing(lifecycle, currentStage, detailCode);
        }
        catch (FlowExecutionException ex)
        {
            exitCategory = ex.ExitCategory;
            detailCode = ex.Code;
            message = BoundMessage(ex.Message);
            diagnosticsArtifact = ex.DiagnosticsArtifact;
            infrastructurePhase = currentStage;
            AddFailedStageIfMissing(lifecycle, currentStage, detailCode);
        }
        catch (OperationCanceledException)
        {
            var unknown = platformSession?.LaunchedByInvocation == true;
            exitCategory = unknown
                ? FlowExecutionExitCategories.UnknownCompletion
                : FlowExecutionExitCategories.InfrastructureFailure;
            detailCode = unknown ? "execution-cancelled-after-launch" : "execution-cancelled";
            message = unknown
                ? "Execution was cancelled after app launch; mutation completion cannot be proven."
                : "Execution was cancelled before the app was launched.";
            infrastructurePhase = currentStage;
            AddFailedStageIfMissing(lifecycle, currentStage, detailCode);
        }
        catch (Exception ex)
        {
            exitCategory = FlowExecutionExitCategories.InfrastructureFailure;
            detailCode = "unexpected-execution-error";
            message = $"Execution failed during {SafeCode(currentStage)}.";
            infrastructurePhase = currentStage;
            AddFailedStageIfMissing(lifecycle, currentStage, detailCode);
            Trace.WriteLine($"DevFlow flow run failed with {ex.GetType().Name}.");
        }
        finally
        {
            if (platformSession is not null && adapter is not null)
            {
                // The probe has to run before cleanup, because cleanup force-stops the app and
                // would erase the very evidence that separates a crash from a teardown.
                appProcess = await ProbeAppProcessAsync(
                    adapter,
                    platformSession,
                    startedAt).ConfigureAwait(false);
                var cleanupStopwatch = Stopwatch.StartNew();
                FlowExecutionCleanupResult cleanup;
                try
                {
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    cleanup = await adapter.CleanupAsync(
                        platformSession,
                        request.CleanupPolicy,
                        cleanupTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"DevFlow flow run cleanup failed with {ex.GetType().Name}.");
                    cleanup = new FlowExecutionCleanupResult
                    {
                        Succeeded = false,
                        DetailCode = "cleanup-exception",
                    };
                }
                cleanupStopwatch.Stop();
                lifecycle.CleanupCompleted = cleanup.Succeeded;
                AddStage(
                    lifecycle,
                    "cleanup",
                    cleanup.Succeeded ? "passed" : "failed",
                    cleanup.DetailCode ?? (cleanup.Succeeded ? "cleanup-complete" : "cleanup-failed"),
                    (long)cleanupStopwatch.Elapsed.TotalMilliseconds,
                    ownedResource: true);
                if (!cleanup.Succeeded)
                {
                    exitCategory = FlowExecutionExitCategories.InfrastructureFailure;
                    detailCode = cleanup.DetailCode ?? "cleanup-failed";
                    message = "The run reached a terminal execution outcome, but owned platform cleanup failed.";
                    infrastructurePhase = "cleanup";
                }
            }
            else
            {
                lifecycle.CleanupCompleted = true;
            }

            client?.Dispose();
            if (artifact?.OwnedOutputRoot is not null)
            {
                var retainForRunningDesktop = ShouldRetainOwnedArtifactRoot(
                    platformSession,
                    request.CleanupPolicy);
                var artifactCleanup = retainForRunningDesktop ||
                    MsBuildAppArtifactResolver.TryDeleteDirectory(artifact.OwnedOutputRoot);
                AddStage(
                    lifecycle,
                    "artifact-cleanup",
                    artifactCleanup ? "passed" : "failed",
                    retainForRunningDesktop
                        ? "artifact-retained-for-running-desktop"
                        : artifactCleanup
                            ? "artifact-cleanup-complete"
                            : "artifact-cleanup-failed",
                    durationMs: 0,
                    ownedResource: true);
                if (!artifactCleanup)
                {
                    lifecycle.CleanupCompleted = false;
                    exitCategory = FlowExecutionExitCategories.InfrastructureFailure;
                    detailCode = "artifact-cleanup-failed";
                    message = "The run reached a terminal execution outcome, but the owned build artifact directory could not be removed.";
                    infrastructurePhase = "artifact-cleanup";
                }
            }
            lifecycle.EndedAt = _clock.GetUtcNow();
        }

        report ??= CreateSyntheticReport(
            runId,
            bundle,
            artifact,
            platformSession,
            startedAt,
            lifecycle.EndedAt ?? _clock.GetUtcNow(),
            exitCategory,
            detailCode,
            message,
            appSourceIdentity,
            infrastructurePhase);
        report.AppProcess = appProcess;
        // Cleanup owns the outcome when the host's own teardown failed, so a crash never displaces
        // it; the evidence is still attached above so nothing is hidden.
        var priorFailure = report.Failure;
        var priorEventCount = report.Events.Count;
        var crashOwnsFailure = infrastructurePhase is not ("cleanup" or "artifact-cleanup") &&
            TryStampAppCrash(report, lifecycle.EndedAt ?? _clock.GetUtcNow());
        if (crashOwnsFailure)
        {
            var recomputed = ClassifyReport(report);
            // Only ever tighten: an infrastructure verdict becomes the app's fault once the crash
            // is proven. A fail-closed category (unknown-completion, unverified) is never relaxed,
            // because knowing the app died still does not prove the mutation completed.
            if (exitCategory == FlowExecutionExitCategories.InfrastructureFailure &&
                recomputed == FlowExecutionExitCategories.TestFailure)
            {
                exitCategory = recomputed;
                detailCode = report.Failure?.Code ?? detailCode;
                message = report.Failure?.Message ?? message;
            }
            else if (!string.Equals(recomputed, exitCategory, StringComparison.Ordinal))
            {
                // The category cannot move but the stamp would make the report disagree with it,
                // which reads downstream as a corrupt artifact rather than as a crash. Keep the
                // evidence in report.AppProcess and leave the attribution alone.
                report.Failure = priorFailure;
                report.Events.RemoveRange(priorEventCount, report.Events.Count - priorEventCount);
                crashOwnsFailure = false;
            }
        }
        if (exitCategory == FlowExecutionExitCategories.InfrastructureFailure &&
            !string.Equals(report.Outcome?.Status, MauiFlowRunOutcomes.InfrastructureError, StringComparison.Ordinal))
        {
            PromoteToInfrastructureFailure(
                report,
                detailCode,
                message,
                lifecycle.EndedAt ?? _clock.GetUtcNow(),
                infrastructurePhase);
        }
        report.ExtensionData ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        report.ExtensionData["exitCategory"] = JsonSerializer.SerializeToElement(exitCategory);
        // The report and the manifest have to agree, so drop the pointer once if the file is gone.
        if (diagnosticsArtifact is not null &&
            !File.Exists(Path.Combine(outputDirectory, diagnosticsArtifact.FileName)))
        {
            diagnosticsArtifact = null;
        }
        if (diagnosticsArtifact is not null)
            AddDiagnosticsArtifact(report, diagnosticsArtifact, _clock.GetUtcNow());
        // Re-evaluated last so the verdict reflects the artifacts and oracle results the run
        // actually ended with, not the ones the replay had produced when it finished.
        report.ExpectedEvidence = MauiFlowExpectedEvidenceEvaluator.Evaluate(
            bundle?.Flow,
            report,
            evidence?.CapturedEvidenceKinds);
        NormalizeArtifactPaths(report);

        var reportFile = _reportWriter.Create(report);
        var junitFile = _junitWriter.Create(report, exitCategory);
        var manifest = CreateManifest(
            runId,
            bundle,
            artifact,
            platformSession,
            liveStatus,
            lifecycle,
            report,
            exitCategory,
            reportFile,
            junitFile,
            evidence?.CapturedPath,
            appSourceIdentity,
            diagnosticsArtifact);
        var manifestFile = _manifestWriter.Create(manifest);
        await _outputWriter.WriteAsync(
            outputDirectory,
            [reportFile, junitFile, manifestFile],
            CancellationToken.None).ConfigureAwait(false);

        return new FlowExecutionResult
        {
            ExitCategory = exitCategory,
            Message = message,
            OutputDirectory = outputDirectory,
            ManifestPath = Path.Combine(outputDirectory, ExecutionManifestWriter.FileName),
            ReportPath = Path.Combine(outputDirectory, MauiFlowRunReportSerializer.FileName),
            JUnitPath = Path.Combine(outputDirectory, JUnitFlowExecutionWriter.FileName),
            EvidencePath = evidence?.CapturedPath,
            Report = report,
            Manifest = manifest,
        };
    }

    internal static void ApplyPostRunVerification(
        MauiFlowRunReport report,
        MauiTestPlan plan,
        MauiFlow flow,
        MauiFlowRunContext runContext,
        DateTimeOffset checkedAt)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(runContext);

        var decision = MauiFlowReplaySafetyEvaluator.EvaluateWithFlow(new MauiFlowRunRequest
        {
            Plan = plan,
            Context = runContext,
        }, flow);
        report.BusinessOracles = runContext.BusinessOracles.ToList();
        report.ReplayEligibility = decision;

        var executionPassed = string.Equals(
            report.Outcome?.Status,
            MauiFlowRunOutcomes.Passed,
            StringComparison.Ordinal);
        var verified = executionPassed && decision.RunVerificationAllowed;
        var reason = PostRunVerificationReason(executionPassed, verified, decision);
        report.Outcome ??= new MauiFlowRunOutcome();
        report.Outcome.Verified = verified;
        report.Outcome.VerificationReason = reason;
        report.Verification = new MauiFlowRunVerification
        {
            Verified = verified,
            Reason = reason,
            CheckedAt = checkedAt,
        };
    }

    internal static string ClassifyReport(MauiFlowRunReport report)
    {
        var failureClass = report.Failure?.Class ?? report.Failure?.Code;
        if (string.Equals(report.Outcome?.Status, MauiFlowRunOutcomes.Passed, StringComparison.Ordinal))
        {
            return report.Outcome?.Verified == true
                ? FlowExecutionExitCategories.Pass
                : FlowExecutionExitCategories.Unverified;
        }
        if (string.Equals(report.Outcome?.Status, MauiFlowRunOutcomes.UnknownCompletion, StringComparison.Ordinal) ||
            string.Equals(report.Outcome?.Status, MauiFlowRunOutcomes.Cancelled, StringComparison.Ordinal) ||
            string.Equals(report.Outcome?.Status, MauiFlowRunOutcomes.TimedOut, StringComparison.Ordinal) ||
        string.Equals(report.Outcome?.Status, MauiFlowRunOutcomes.LeaseLost, StringComparison.Ordinal) ||
        string.Equals(report.Outcome?.Status, MauiFlowRunOutcomes.Orphaned, StringComparison.Ordinal) ||
        failureClass is
            MauiFlowFailureClasses.UnknownCompletion or
            MauiFlowFailureClasses.LeaseConflict or
            MauiFlowFailureClasses.LeaseLost or
            MauiFlowFailureClasses.Cancelled or
            MauiFlowFailureClasses.Timeout)
        {
        return FlowExecutionExitCategories.UnknownCompletion;
        }
        // A proven app crash is attributed to the app, not to the harness. This runs after the
        // unknown-completion branch so knowing the app died never relaxes an unknown mutation, and
        // before the infrastructure branch so a crash is not filed as an environment problem and
        // silently retried.
        if (failureClass is MauiFlowFailureClasses.AppCrash)
            return FlowExecutionExitCategories.TestFailure;
        if (string.Equals(report.Outcome?.Status, MauiFlowRunOutcomes.InfrastructureError, StringComparison.Ordinal) ||
        failureClass is MauiFlowFailureClasses.Infrastructure or
            MauiFlowFailureClasses.Transport or
            MauiFlowFailureClasses.AgentDisconnected)
        {
            return FlowExecutionExitCategories.InfrastructureFailure;
        }
        if (failureClass is MauiFlowFailureClasses.FlowInvalid or
            MauiFlowFailureClasses.SchemaUnsupported or
            MauiFlowFailureClasses.CapabilityMissing or
            MauiFlowFailureClasses.PreconditionUnsatisfied or
            MauiFlowFailureClasses.ResetFailed)
        {
            return FlowExecutionExitCategories.InvalidConfiguration;
        }
        return FlowExecutionExitCategories.TestFailure;
    }

    private static string PostRunVerificationReason(
        bool executionPassed,
        bool verified,
        MauiFlowReplayEligibilityDecision decision)
    {
        if (verified)
        {
            return "Required scenario, acceptance-criterion, and independent business-oracle coverage verified the run.";
        }
        if (!executionPassed)
            return "The execution did not pass, so it was not independently verified.";

        var reasonCodes = decision.Reasons
            .Where(static reason => string.Equals(reason.Scope, "verification", StringComparison.Ordinal))
            .Select(static reason => SafeCode(reason.Code ?? "verification-unsatisfied"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static code => code, StringComparer.Ordinal)
            .ToArray();
        return reasonCodes.Length == 0
            ? "The execution passed, but independent verification requirements were not satisfied."
            : "The execution passed, but independent verification requirements were not satisfied: " +
              string.Join(", ", reasonCodes) + ".";
    }

    internal static IFlowExecutionPlatformAdapter SelectAdapter(
        IEnumerable<IFlowExecutionPlatformAdapter> adapters,
        string platform)
    {
        var materialized = adapters?.ToArray() ?? throw new ArgumentNullException(nameof(adapters));
        var matches = materialized
            .Where(adapter => adapter.Descriptor.MatchesCommand(platform))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw FlowExecutionException.Unsupported(
                "platform-unsupported",
                "The requested flow execution platform is unsupported. Supported values: " +
                string.Join(
                    ", ",
                    materialized
                        .SelectMany(static adapter => adapter.Descriptor.CommandAliases)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)) +
                "."),
            _ => throw FlowExecutionException.Invalid(
                "platform-adapter-ambiguous",
                "Multiple execution adapters are registered for the selected platform."),
        };
    }

    internal static bool ShouldRetainOwnedArtifactRoot(
        FlowExecutionPlatformSession? session,
        string cleanupPolicy)
        => session?.State is DesktopFlowExecutionSessionState &&
           session.LaunchedByInvocation &&
           string.Equals(
               cleanupPolicy,
               FlowExecutionCleanupPolicies.None,
               StringComparison.Ordinal);

    private static void ValidateRequest(FlowExecutionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectPath))
            throw FlowExecutionException.Invalid("project-path-missing", "Flow run requires --project <app.csproj>.");
        if (string.IsNullOrWhiteSpace(request.Platform))
            throw FlowExecutionException.Invalid("platform-missing", "Flow run requires a target platform.");
        if (!FlowExecutionCleanupPolicies.IsKnown(request.CleanupPolicy))
            throw FlowExecutionException.Invalid("cleanup-policy-invalid", "--cleanup must be none, stop, or uninstall.");
        if (!string.Equals(request.Configuration, "Debug", StringComparison.OrdinalIgnoreCase))
        {
            throw FlowExecutionException.Unsupported(
                "devflow-agent-debug-configuration-required",
                "Flow run v1 requires a Debug app build because the mutating DevFlow agent is disabled for non-Debug configurations.");
        }
        if (request.AgentWaitTimeout <= TimeSpan.Zero || request.AgentWaitTimeout > TimeSpan.FromMinutes(10))
            throw FlowExecutionException.Invalid("agent-timeout-invalid", "The agent wait timeout must be between 1 second and 10 minutes.");
        if (!string.Equals(request.AgentHost, "localhost", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.AgentHost, "127.0.0.1", StringComparison.Ordinal) &&
            !string.Equals(request.AgentHost, "::1", StringComparison.Ordinal))
        {
            throw FlowExecutionException.Unsupported(
                "remote-agent-host-unsupported",
                "Flow run binds only to a local broker and locally attached platform target.");
        }
    }

    internal static void ValidateBundleTarget(
        CommittedFlowBundle bundle,
        FlowExecutionPlatformDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(bundle.Flow.Platform) &&
            !descriptor.MatchesFlowPlatform(bundle.Flow.Platform))
        {
            throw FlowExecutionException.Invalid(
                "flow-platform-mismatch",
                $"The committed flow does not target {descriptor.DisplayName}.");
        }
        var requiredPlatforms = FlowPlatformTags.Parse(bundle.Plan.RequiredPlatforms);
        if (requiredPlatforms.Count > 0 &&
            !requiredPlatforms.Any(descriptor.MatchesFlowPlatform))
        {
            throw FlowExecutionException.Invalid(
                "plan-platform-mismatch",
                $"The committed plan does not include {descriptor.DisplayName} in its required platforms.");
        }
    }

    internal static void ValidatePlanRequirements(MauiTestPlan plan, AgentStatus status)
    {
        var available = WorkflowRunCoordinator.BuildAvailableCapabilities(status);
        available.Semantics.AddRange(
        [
            new MauiSupportedSemantic
            {
                Name = "canonical-flow-runner",
                Version = 1,
            },
            new MauiSupportedSemantic
            {
                Name = "stable-step-identity",
                Version = 1,
            },
        ]);
        var validation = MauiFlowRequirementValidator.Validate(plan.Requirements, available);
        if (validation.Errors.Count == 0)
            return;

        var failure = validation.Errors[0];
        throw FlowExecutionException.Unsupported(
            failure.Code,
            failure.Message);
    }

    /// <summary>
    /// Enforces the plan's declared checkpoint preconditions against the app's clean state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Admission runs before anything is deployed, so it cannot observe the app; a state evidence
    /// provider evaluating at that point can only declare the observation deferred, which is why a
    /// declared checkpoint used to be unsatisfiable and every plan therefore left it out. The first
    /// moment a clean-state checkpoint exists is immediately after the exact agent binding is
    /// validated and before the flow has taken a single action, which is where this runs.
    /// </para>
    /// <para>
    /// Both halves come from separate sources: expected from the committed plan, observed from the
    /// live agent. Nothing here derives one from the other, so the resulting
    /// <c>expectedCheckpoint</c> recorded on each step is a declaration the run was held to rather
    /// than a copy of what the run happened to see.
    /// </para>
    /// </remarks>
    private void VerifyDeclaredPreconditions(
        CommittedFlowBundle bundle,
        MauiFlowRunContext runContext,
        AgentStatus liveStatus)
    {
        ArgumentNullException.ThrowIfNull(liveStatus);
        FlowStateEvidenceProviderRegistry.ApplyDeclaredCheckpoint(runContext, bundle.Plan.Checkpoint);
        var preconditions = runContext.Preconditions;
        if (preconditions is null)
        {
            throw FlowExecutionException.Invalid(
                "precondition-context-missing",
                "The run context carries no preconditions to verify, so the declared checkpoint cannot be enforced.");
        }

        // The live reading only fills fields the agent can actually see. A provider that attested a
        // seed or a backend state at preflight owns those fields, so they are kept rather than
        // overwritten with the nulls CreateCheckpoint produces for them.
        var live = MauiFlowRunner.CreateCheckpoint(liveStatus);
        var observed = preconditions.Observed ?? new MauiFlowCheckpoint();
        observed.AppBuildFingerprint ??= live.AppBuildFingerprint;
        observed.AgentInstanceId ??= live.AgentInstanceId;
        observed.Route ??= live.Route;
        observed.Window ??= live.Window;
        observed.Modal ??= live.Modal;
        observed.Locale ??= live.Locale;
        observed.Theme ??= live.Theme;
        observed.Orientation ??= live.Orientation;
        observed.DisplayProfile ??= live.DisplayProfile;
        preconditions.Observed = observed;
        preconditions.ObservationDeferredUntilLaunch = null;
        preconditions.CheckedAt = _clock.GetUtcNow();

        SealExpectedRunIdentity(preconditions, observed);

        var decision = MauiFlowReplaySafetyEvaluator.EvaluateWithFlow(
            new MauiFlowRunRequest
            {
                Plan = bundle.Plan,
                Context = runContext,
            },
            bundle.Flow);
        if (decision.OrdinaryReplayAllowed)
            return;

        // The first blocking reason is the diagnosis, whatever it is named. Preferring a
        // "precondition"-prefixed code would report a checkpoint problem for refusals that are not
        // one, such as an unauthorized one-shot or an uncertain prior mutation.
        var blocking = decision.Reasons.FirstOrDefault(static reason => reason.Blocking == true);
        throw FlowExecutionException.Invalid(
            blocking?.Code ?? "precondition-checkpoint-unsatisfied",
            blocking?.Message ??
                "The app's observed clean state does not satisfy the plan's declared checkpoint preconditions.");
    }

    /// <summary>
    /// Records the run-identity facts the plan cannot declare in advance onto the expected
    /// checkpoint, once, after preconditions verified.
    /// <para>
    /// A plan author can meaningfully declare a route, a window, a locale, or a theme. They cannot
    /// declare the app build fingerprint, the agent instance id, or the seed and backend
    /// fingerprints a provider attests at preflight, because those are properties of the run about
    /// to happen. Repair eligibility nonetheless requires all of them
    /// (<c>checkpoint-app-build-expected-missing</c> and friends), so leaving them unset made every
    /// local run permanently repair-ineligible no matter how clean it was — the reason selector
    /// self-repair had never once been reachable from `flow run`.
    /// </para>
    /// <para>
    /// This is not weakening the gate. These fields are sealed from the state the run was already
    /// held to: preconditions verified first, and only facts the plan left unspecified are filled.
    /// Anything the plan did declare stays exactly as declared and is still compared, so a plan
    /// that pins a build or a seed continues to fail closed when the app does not match it.
    /// </para>
    /// </summary>
    private static void SealExpectedRunIdentity(
        MauiFlowReplayPreconditions preconditions,
        MauiFlowCheckpoint observed)
    {
        var expected = preconditions.Expected;
        if (expected is null)
            return;

        expected.AppBuildFingerprint ??= observed.AppBuildFingerprint;
        expected.AgentInstanceId ??= observed.AgentInstanceId;
        expected.SeedFingerprint ??= observed.SeedFingerprint;
        expected.BackendStateFingerprint ??= observed.BackendStateFingerprint;
        expected.Locale ??= observed.Locale;
        expected.Theme ??= observed.Theme;
        expected.Orientation ??= observed.Orientation;
        expected.DisplayProfile ??= observed.DisplayProfile;
    }

    private static MauiFlowRunTarget CreateRunTarget(
        FlowExecutionPlatformSession session,
        AgentRegistration registration,
        ResolvedAppArtifact artifact,
        AgentStatus? status,
        AppSourceIdentity appSourceIdentity)
        => new()
        {
            TargetId = registration.Id,
            Platform = session.Platform,
            DeviceId = Hash("device", session.DeviceSerial),
            DeviceProfile = session.DeviceProfile,
            AppId = session.PackageId,
            AppBuildFingerprint = Hash("app-build", artifact.PackageDigest + "\u001f" + (status?.App?.Build ?? "")),
            AppSourceFingerprint = appSourceIdentity.AppSourceFingerprint,
            PackageDigest = artifact.PackageDigest,
            NormalizedPayloadDigest = artifact.NormalizedPayloadDigest,
            AgentId = registration.Id,
            AgentInstanceId = registration.InstanceId,
            Locale = status?.Locale,
            Theme = status?.Theme,
            Orientation = status?.Orientation,
            DisplayProfile = status?.DisplayProfile,
        };

    private MauiTestExecutionManifest CreateManifest(
        string runId,
        CommittedFlowBundle? bundle,
        ResolvedAppArtifact? artifact,
        FlowExecutionPlatformSession? session,
        AgentStatus? liveStatus,
        MauiTestExecutionLifecycleFacts lifecycle,
        MauiFlowRunReport report,
        string exitCategory,
        ExecutionOutputFile reportFile,
        ExecutionOutputFile junitFile,
        string? evidencePath,
        AppSourceIdentity appSourceIdentity,
        FlowExecutionDiagnosticsArtifact? diagnosticsArtifact = null)
    {
        var manifest = new MauiTestExecutionManifest
        {
            ManifestId = "manifest-" + runId,
            RunId = runId,
            FlowId = report.FlowId ?? bundle?.FlowId,
            FlowRevision = report.FlowRevision ?? bundle?.FlowRevision,
            FlowDigest = report.FlowDigest ?? bundle?.FlowDigest,
            Host = CreateHostFacts(),
            Build = new MauiTestExecutionBuildFacts
            {
                RepositoryFingerprint = FindRepositoryFingerprint(artifact?.ProjectPath),
                ProjectFingerprint = artifact?.ProjectPath is null ? null : Hash("project", artifact.ProjectPath),
                SourceRevision = appSourceIdentity.SourceRevision,
                TargetFramework = artifact?.TargetFramework,
                Configuration = artifact?.Configuration,
                AppId = session?.PackageId ?? artifact?.ApplicationId,
                AppBuildFingerprint = artifact is null
                    ? null
                    : Hash("app-build", artifact.PackageDigest + "\u001f" + (liveStatus?.App?.Build ?? "")),
                AppSourceFingerprint = appSourceIdentity.AppSourceFingerprint,
                PackageDigest = artifact?.PackageDigest,
            },
            Device = session is null ? null : new MauiTestExecutionDeviceFacts
            {
                Platform = session.Platform,
                RuntimeKind = session.RuntimeKind,
                Profile = session.DeviceProfile,
                Architecture = session.Device.Architecture ?? session.Device.PlatformArchitecture,
                ApiLevel = string.Equals(session.Platform, "android", StringComparison.OrdinalIgnoreCase)
                    ? session.Device.Version
                    : null,
                OsVersion = session.Device.Version,
                FormFactor = session.Device.Idiom,
                Virtual = session.Device.IsEmulator,
                DeviceIdentityFingerprint = Hash("device", session.DeviceSerial),
            },
            Lifecycle = lifecycle,
            Outcome = new MauiTestExecutionOutcomeFacts
            {
                Status = report.Outcome?.Status ?? OutcomeStatusForCategory(exitCategory),
                ExitCategory = exitCategory,
                Terminal = true,
                Verified = exitCategory == FlowExecutionExitCategories.Pass,
                UnknownCompletion = exitCategory == FlowExecutionExitCategories.UnknownCompletion,
            },
            Artifacts =
            [
                ToManifestArtifact("flow-run", "semantic-report", "flow-run-report", "application/json", reportFile),
                ToManifestArtifact("junit", "test-report", "junit", "application/xml", junitFile),
            ],
        };
        if (!string.IsNullOrWhiteSpace(evidencePath) && File.Exists(evidencePath))
        {
            var info = new FileInfo(evidencePath);
            manifest.Artifacts.Add(new MauiTestExecutionArtifact
            {
                ArtifactId = "failure-evidence",
                Role = "failure-evidence",
                Kind = "mauitrace",
                MediaType = "application/vnd.maui.evidence+zip",
                Digest = ComputeFileDigest(evidencePath),
                SizeBytes = info.Length,
                RelativePath = Path.GetFileName(evidencePath),
                Redacted = true,
            });
        }
        if (diagnosticsArtifact is not null)
        {
            manifest.Artifacts.Add(new MauiTestExecutionArtifact
            {
                ArtifactId = "app-build-log",
                Role = "failure-diagnostics",
                Kind = diagnosticsArtifact.Kind,
                MediaType = diagnosticsArtifact.MediaType,
                Digest = diagnosticsArtifact.Digest,
                SizeBytes = diagnosticsArtifact.SizeBytes,
                RelativePath = diagnosticsArtifact.FileName,
                Redacted = true,
            });
        }

        try
        {
            var triage = MauiFlowTriageAnalyzer.Analyze(new MauiFlowTriageInput
            {
                Flow = bundle?.Flow,
                Report = report,
                Manifest = manifest,
                Plan = bundle?.Plan,
                ReplayEligibility = report.ReplayEligibility,
                ExpectedCheckpoint = report.Preconditions?.Expected,
                IsCurrentLocalRun = true,
            });
            manifest.TestIdentityFingerprint = triage.TestIdentityFingerprint;
            manifest.IncidentFingerprint = triage.IncidentFingerprint;
            manifest.OccurrenceFingerprint = triage.OccurrenceFingerprint;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"DevFlow triage fingerprinting failed with {ex.GetType().Name}.");
            var fingerprints = MauiFlowIncidentFingerprint.Create(bundle?.Flow, report, manifest);
            manifest.TestIdentityFingerprint = fingerprints.TestIdentityFingerprint;
            manifest.IncidentFingerprint = fingerprints.IncidentFingerprint;
            manifest.OccurrenceFingerprint = fingerprints.OccurrenceFingerprint;
        }
        return MauiTestExecutionManifestSerializer.CreateSafeProjection(manifest);
    }

    private static MauiTestExecutionArtifact ToManifestArtifact(
        string id,
        string role,
        string kind,
        string mediaType,
        ExecutionOutputFile file)
        => new()
        {
            ArtifactId = id,
            Role = role,
            Kind = kind,
            MediaType = mediaType,
            Digest = file.Digest,
            SizeBytes = file.SizeBytes,
            RelativePath = file.FileName,
            Redacted = true,
        };

    private static MauiTestExecutionHostFacts CreateHostFacts()
    {
        var os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsLinux() ? "linux"
            : "unknown";
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var runtime = Environment.Version.ToString();
        return new MauiTestExecutionHostFacts
        {
            Os = os,
            Architecture = architecture,
            Runtime = runtime,
            RunnerKind = "maui-cli",
            EnvironmentFingerprint = Hash("host", $"{os}\u001f{architecture}\u001f{runtime}"),
        };
    }

    private static MauiFlowRunReport CreateSyntheticReport(
        string runId,
        CommittedFlowBundle? bundle,
        ResolvedAppArtifact? artifact,
        FlowExecutionPlatformSession? session,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string exitCategory,
        string detailCode,
        string message,
        AppSourceIdentity appSourceIdentity,
        string failurePhase)
    {
        var failureClass = exitCategory switch
        {
            FlowExecutionExitCategories.InvalidConfiguration => MauiFlowFailureClasses.FlowInvalid,
            FlowExecutionExitCategories.Unsupported => MauiFlowFailureClasses.CapabilityMissing,
            FlowExecutionExitCategories.UnknownCompletion => MauiFlowFailureClasses.UnknownCompletion,
            _ => MauiFlowFailureClasses.Infrastructure,
        };
        return new MauiFlowRunReport
        {
            RunId = runId,
            FlowId = bundle?.FlowId ?? (bundle is null ? null : $"sha256:{bundle.FlowDigest}"),
            FlowRevision = bundle?.FlowRevision,
            FlowDigest = bundle?.FlowDigest,
            LegacyFlowIdentity = bundle is null ? null : Path.GetFileName(bundle.FlowPath),
            Target = new MauiFlowRunTarget
            {
                Platform = session?.Platform,
                DeviceId = session is null ? null : Hash("device", session.DeviceSerial),
                DeviceProfile = session?.DeviceProfile,
                AppId = session?.PackageId ?? artifact?.ApplicationId,
                AppBuildFingerprint = artifact is null
                    ? null
                    : Hash("app-build", artifact.PackageDigest + "\u001f"),
                AppSourceFingerprint = appSourceIdentity.AppSourceFingerprint,
                PackageDigest = artifact?.PackageDigest,
                NormalizedPayloadDigest = artifact?.NormalizedPayloadDigest,
            },
            SideEffectPolicy = bundle?.Plan.SideEffectPolicy,
            StartedAt = startedAt,
            EndedAt = endedAt,
            Outcome = new MauiFlowRunOutcome
            {
                Status = OutcomeStatusForCategory(exitCategory),
                Summary = message,
                Terminal = true,
                Verified = exitCategory == FlowExecutionExitCategories.Pass,
                VerificationReason = exitCategory == FlowExecutionExitCategories.Unverified
                    ? "Required independent business-oracle evidence is absent."
                    : null,
            },
            Verification = new MauiFlowRunVerification
            {
                Verified = exitCategory == FlowExecutionExitCategories.Pass,
                Reason = exitCategory == FlowExecutionExitCategories.Unverified
                    ? "Required independent business-oracle evidence is absent."
                    : null,
                CheckedAt = endedAt,
            },
            Failure = exitCategory is FlowExecutionExitCategories.Pass or FlowExecutionExitCategories.Unverified
                ? null
                : new MauiFlowFailure
                {
                    FailureId = "failure-" + runId,
                    Class = failureClass,
                    Code = detailCode,
                    Category = exitCategory,
                    Phase = SafeCode(failurePhase),
                    Retryable = exitCategory == FlowExecutionExitCategories.InfrastructureFailure,
                    RepairEligible = false,
                    Message = message,
                    At = endedAt,
                },
        };
    }

    private static void PromoteToInfrastructureFailure(
        MauiFlowRunReport report,
        string detailCode,
        string message,
        DateTimeOffset at,
        string phase)
    {
        var verificationReason = phase is "cleanup" or "artifact-cleanup"
            ? "Owned execution cleanup did not complete."
            : "Post-execution infrastructure processing did not complete.";
        report.ExtensionData ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        report.ExtensionData["primaryExecutionOutcome"] = CreatePrimaryExecutionOutcome(report);
        report.Events.Add(new MauiFlowRunEvent
        {
            Sequence = report.Events.Count == 0
                ? 1
                : report.Events.Max(static item => item.Sequence ?? 0) + 1,
            At = at,
            Kind = "secondary-infrastructure-failure",
            Message = message,
        });
        report.Outcome = new MauiFlowRunOutcome
        {
            Status = MauiFlowRunOutcomes.InfrastructureError,
            Summary = message,
            Terminal = true,
            Verified = false,
            VerificationReason = verificationReason,
        };
        report.Verification = new MauiFlowRunVerification
        {
            Verified = false,
            Reason = verificationReason,
            CheckedAt = at,
        };
        report.Failure = new MauiFlowFailure
        {
            FailureId = "failure-" + report.RunId,
            Class = MauiFlowFailureClasses.Infrastructure,
            Code = detailCode,
            Category = FlowExecutionExitCategories.InfrastructureFailure,
            Phase = phase,
            Retryable = true,
            RepairEligible = false,
            Message = message,
            At = at,
        };
    }

    private static async Task<MauiFlowAppProcessEvidence?> ProbeAppProcessAsync(
        IFlowExecutionPlatformAdapter adapter,
        FlowExecutionPlatformSession session,
        DateTimeOffset runStartedAt)
    {
        try
        {
            // Deliberately not linked to the run's token. A crashed or wedged app is a common cause
            // of the run being cancelled or deadlined, which is exactly when this evidence matters
            // most; teardown two lines earlier uses an independent token for the same reason.
            using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return await adapter.ProbeAppProcessAsync(
                new FlowExecutionAppProbeRequest
                {
                    Session = session,
                    RunStartedAt = runStartedAt,
                },
                probeTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A probe that cannot answer must not invent one. The absence of evidence is recorded
            // as an absence, so classification keeps whatever it already knew.
            Trace.WriteLine($"DevFlow app process probe failed with {ex.GetType().Name}.");
            return new MauiFlowAppProcessEvidence
            {
                Probed = true,
                ProbeError = "probe-failed",
            };
        }
    }

    /// <summary>
    /// Replaces the report's failure class with <c>app-crash</c> when, and only when, the attached
    /// process evidence proves the application died abnormally and the classifier agrees the crash
    /// outranks whatever class the run already carried.
    /// </summary>
    private static bool TryStampAppCrash(MauiFlowRunReport report, DateTimeOffset at)
    {
        var evidence = report.AppProcess;
        if (evidence?.ProvesAbnormalExit() != true)
            return false;
        if (string.Equals(report.Outcome?.Status, MauiFlowRunOutcomes.Passed, StringComparison.Ordinal))
            return false;

        var existing = report.Failure;
        var facts = new MauiFlowFailureFacts
        {
            FailureClass = existing?.Class,
            LegacyFailureKind = existing?.LegacyKind,
            CompletionCertain = report.Steps
                .Any(static step => string.Equals(step.CompletionCertainty, "unknown", StringComparison.Ordinal))
                ? false
                : null,
            AppProcessExited = evidence.ProcessExited,
            AppExitCode = evidence.ExitCode,
            AppExitReason = evidence.ExitReason,
            CrashLogPresent = evidence.CrashLogPresent,
        };
        var classification = MauiFlowFailureClassifier.Classify(facts);
        if (!string.Equals(classification.FailureClass, MauiFlowFailureClasses.AppCrash, StringComparison.Ordinal))
            return false;

        report.Failure = new MauiFlowFailure
        {
            FailureId = existing?.FailureId ?? "failure-" + report.RunId,
            Class = classification.FailureClass,
            Code = classification.Code,
            Category = classification.Category,
            Phase = classification.Phase,
            StepId = existing?.StepId,
            LegacyKind = existing?.LegacyKind,
            Retryable = classification.Retryable,
            RepairEligible = classification.RepairEligible,
            Message = "The app under test exited abnormally during execution.",
            At = existing?.At ?? at,
        };
        report.Events.Add(new MauiFlowRunEvent
        {
            Sequence = report.Events.Count == 0
                ? 1
                : report.Events.Max(static item => item.Sequence ?? 0) + 1,
            At = at,
            Kind = "app-process-exit",
            // The displaced class is kept here rather than dropped: the crash explains the verdict,
            // but the symptom the run actually hit is what an author needs to find the defect.
            Message = existing?.Class is { Length: > 0 } displaced
                ? $"The app process exited abnormally during this run; the run had reported {displaced}."
                : "The app process exited abnormally during this run.",
        });
        return true;
    }

    private static JsonElement CreatePrimaryExecutionOutcome(MauiFlowRunReport report)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("exitCategory", ClassifyReport(report));
            if (!string.IsNullOrWhiteSpace(report.Outcome?.Status))
                writer.WriteString("status", SafeCode(report.Outcome.Status));
            writer.WriteBoolean("verified", report.Outcome?.Verified == true);
            if (!string.IsNullOrWhiteSpace(report.Failure?.Class))
                writer.WriteString("failureClass", SafeCode(report.Failure.Class));
            if (!string.IsNullOrWhiteSpace(report.Failure?.Code))
                writer.WriteString("failureCode", SafeCode(report.Failure.Code));
            if (!string.IsNullOrWhiteSpace(report.Failure?.Phase))
                writer.WriteString("failurePhase", SafeCode(report.Failure.Phase));
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void AddDiagnosticsArtifact(
        MauiFlowRunReport report,
        FlowExecutionDiagnosticsArtifact diagnostics,
        DateTimeOffset at)
    {
        // The report message is bounded, so the operator-facing pointer to the full build log has to
        // travel as an artifact reference instead.
        report.Artifacts.Add(CreateReference(diagnostics, at));
        report.Failure?.Artifacts.Add(CreateReference(diagnostics, at));

        static MauiFlowArtifactReference CreateReference(
            FlowExecutionDiagnosticsArtifact diagnostics,
            DateTimeOffset at)
            => new()
            {
                ArtifactId = "app-build-log",
                Kind = diagnostics.Kind,
                Path = diagnostics.FileName,
                Digest = diagnostics.Digest,
                MediaType = diagnostics.MediaType,
                Redacted = true,
                CreatedAt = at,
            };
    }

    private static void NormalizeArtifactPaths(MauiFlowRunReport report)
    {
        Normalize(report.Artifacts);
        if (report.Failure is not null)
            Normalize(report.Failure.Artifacts);
        foreach (var step in report.Steps)
            Normalize(step.Artifacts);

        static void Normalize(IEnumerable<MauiFlowArtifactReference> artifacts)
        {
            foreach (var artifact in artifacts)
            {
                if (!string.IsNullOrWhiteSpace(artifact.Path) && Path.IsPathRooted(artifact.Path))
                    artifact.Path = Path.GetFileName(artifact.Path);
            }
        }
    }

    private static string OutcomeStatusForCategory(string exitCategory)
        => exitCategory switch
        {
            FlowExecutionExitCategories.Pass or FlowExecutionExitCategories.Unverified => MauiFlowRunOutcomes.Passed,
            FlowExecutionExitCategories.InfrastructureFailure => MauiFlowRunOutcomes.InfrastructureError,
            FlowExecutionExitCategories.UnknownCompletion => MauiFlowRunOutcomes.UnknownCompletion,
            _ => MauiFlowRunOutcomes.Failed,
        };

    internal static string ComposeUnverifiedMessage(MauiFlowRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var reason = report.Outcome?.VerificationReason ?? report.Verification?.Reason;
        var summary = report.Outcome?.Summary;
        var message = string.IsNullOrWhiteSpace(reason)
            ? MessageForCategory(FlowExecutionExitCategories.Unverified)
            : reason!;
        return string.IsNullOrWhiteSpace(summary) || message.Contains(summary!, StringComparison.Ordinal)
            ? message
            : $"{message} ({summary})";
    }

    private static string MessageForCategory(string exitCategory)        => exitCategory switch
        {
            FlowExecutionExitCategories.Pass => "The flow passed with required independent verification.",
            FlowExecutionExitCategories.Unverified => "The flow completed but is not independently verified.",
            FlowExecutionExitCategories.TestFailure => "The flow completed with a test failure.",
            FlowExecutionExitCategories.InvalidConfiguration => "The flow execution configuration is invalid.",
            FlowExecutionExitCategories.Unsupported => "The requested flow execution is unsupported.",
            FlowExecutionExitCategories.UnknownCompletion => "Flow mutation completion is unknown.",
            _ => "The flow failed because of execution infrastructure.",
        };

    private static string PrepareOutputDirectory(string? requestedPath, string runId)
    {
        var path = string.IsNullOrWhiteSpace(requestedPath)
            ? Path.Combine(Environment.CurrentDirectory, "artifacts", "devflow", runId)
            : requestedPath;
        return ExecutionPathSafety.PrepareNewOrEmptyDirectory(path);
    }

    private static async Task<T> StageAsync<T>(
        MauiTestExecutionLifecycleFacts lifecycle,
        string name,
        Func<Task<T>> action,
        CancellationToken cancellationToken,
        bool ownedResource = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await action().ConfigureAwait(false);
            stopwatch.Stop();
            AddStage(lifecycle, name, "passed", name + "-complete", (long)stopwatch.Elapsed.TotalMilliseconds, ownedResource);
            return result;
        }
        catch
        {
            stopwatch.Stop();
            AddStage(lifecycle, name, "failed", name + "-failed", (long)stopwatch.Elapsed.TotalMilliseconds, ownedResource);
            throw;
        }
    }

    private static void AddStage(
        MauiTestExecutionLifecycleFacts lifecycle,
        string name,
        string status,
        string detailCode,
        long durationMs,
        bool ownedResource = false)
        => lifecycle.Stages.Add(new MauiTestExecutionLifecycleStage
        {
            Sequence = lifecycle.Stages.Count + 1,
            Name = SafeCode(name),
            Status = SafeCode(status),
            DetailCode = SafeCode(detailCode),
            DurationMs = Math.Max(0, durationMs),
            OwnedResource = ownedResource,
        });

    private static void AddFailedStageIfMissing(
        MauiTestExecutionLifecycleFacts lifecycle,
        string name,
        string detailCode)
    {
        if (lifecycle.Stages.LastOrDefault()?.Name == SafeCode(name) &&
            lifecycle.Stages.Last().Status == "failed")
        {
            lifecycle.Stages.Last().DetailCode = SafeCode(detailCode);
            return;
        }
        AddStage(lifecycle, name, "failed", detailCode, 0);
    }

    private static string CreateRunId()
        => "run-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Derives the opaque agent session identity from the build inputs that select the app binary
    /// rather than from a per-invocation random value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session identity is compiled into the app: <c>Microsoft.Maui.DevFlow.Agent.targets</c>
    /// emits it as an <see cref="System.Reflection.AssemblyMetadataAttribute"/> and, on Android, as
    /// an <c>AndroidManifest.xml</c> <c>meta-data</c> overlay. A per-invocation random value therefore
    /// forced the app assembly to be recompiled on every <c>flow run</c>, and — because Roslyn's
    /// deterministic compilation hashes its inputs — changed the assembly MVID, the deterministic PE
    /// timestamp and the debug directory content id in addition to the embedded literal. Two runs of
    /// the same flow against the same commit could never produce the same app binary.
    /// </para>
    /// <para>
    /// The identity is derived from the full project path and nothing else, which is deliberately the
    /// same input <c>Microsoft.Maui.DevFlow.Agent.targets</c> uses for its own default session id
    /// (<c>dw</c> followed by the last 24 alphanumeric characters of the sanitised
    /// <c>$(MSBuildProjectFullPath)</c>). Separate worktrees, CI agents and developer machines
    /// therefore still produce distinct identities, while every spelling of one build — <c>Debug</c>
    /// versus <c>debug</c>, <c>android</c> versus <c>Android</c>, <c>ios</c> versus
    /// <c>ios-simulator</c>, an omitted versus an explicit <c>-f</c> — produces the same identity.
    /// Folding the artifact selectors into the hash instead would make an imported run and its local
    /// reproduction disagree whenever the two were spelled differently, which is the exact failure
    /// this method exists to remove.
    /// </para>
    /// <para>
    /// The path is lowercased unconditionally rather than only on case-insensitive hosts. Hashing the
    /// whole path is strictly more discriminating than the identity it replaces — the targets' own
    /// default collides for any two projects whose sanitised path tails agree over 24 alphanumeric
    /// characters — so case folding removes a strict subset of collisions that already exist in the
    /// default path, and it keeps one repository path from producing two identities depending on the
    /// host filesystem. Equivalence extends exactly as far as <see cref="Path.GetFullPath(string)"/>
    /// normalization plus case folding: a junction, a symbolic link, an 8.3 short name, a
    /// <c>\\?\</c> prefix, and a mapped drive versus its UNC spelling are each a distinct identity,
    /// as they are for <c>$(MSBuildProjectFullPath)</c>.
    /// </para>
    /// <para>
    /// Nothing about agent binding exactness is relaxed. The selectors dropped here are still matched
    /// independently and more precisely elsewhere: <see cref="ExactAgentBindingResolver"/> admits only
    /// agents that appear after launch relative to its pre-launch snapshot, requires the package id,
    /// target framework and platform to match, compares the process id whenever the expectation
    /// carries one, compares device identity when device identity matching is required, and refuses
    /// ambiguous candidates outright.
    /// </para>
    /// <para>
    /// One consequence is worth stating plainly. Because the identity is keyed on the absolute project
    /// path, an imported run produced on another machine and its local reproduction necessarily carry
    /// different identities, and the length-preserving neutralisation in
    /// <see cref="NormalizedPayloadDigest"/> removes the embedded literal but not the compiler outputs
    /// derived from it. A normalized payload digest is therefore comparable only between builds that
    /// share a session identity — that is, one project path on one machine. This change strictly
    /// improves that case, which previously could never agree, and does not create a cross-machine one.
    /// </para>
    /// <para>
    /// The result keeps the 36-character shape of the previous <c>"flow" + Guid("N")</c> value so the
    /// length-preserving session-id neutralisation in <see cref="NormalizedPayloadDigest"/> continues
    /// to apply, and is ASCII alphanumeric so it satisfies the agent session identity validation and
    /// survives the targets' <c>[^a-z0-9]+</c> sanitisation unchanged.
    /// </para>
    /// </remarks>
    internal static string CreateBuildScopedAgentSessionId(FlowExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var projectPath = request.ProjectPath ?? string.Empty;
        try
        {
            projectPath = Path.GetFullPath(projectPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            // Fall back to the caller-supplied spelling; resolution failures surface later with a
            // precise diagnostic and must not escape as an unstructured exception here.
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(projectPath.ToLowerInvariant()));
        return "flow" + Convert.ToHexStringLower(hash)[..32];
    }

    private static string BoundMessage(string message)
        => message.Length <= 512 ? message : message[..512];

    private static string SafeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";
        var safe = new string(value.Trim()
            .Select(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
                    ? character
                    : '-')
            .ToArray());
        return safe[..Math.Min(safe.Length, 128)];
    }

    private static string Hash(string domain, string value)
        => "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(domain + "\u001f" + value))).ToLowerInvariant();

    private static string? FindRepositoryFingerprint(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return null;
        var current = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(projectPath))!);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")))
            {
                return Hash("repository", current.FullName);
            }
            current = current.Parent;
        }
        return null;
    }

    private static string ComputeFileDigest(string path)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

}
