using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Agent.IntegrationTests.Fixtures;

/// <summary>
/// Fixture-owned lifecycle for the official WinUI Windows MAUI target. State reset is deliberately
/// limited to the Debug integration-test process and its test-only extension; it never removes a
/// user profile, package data directory, or arbitrary application data.
/// </summary>
public sealed class WindowsFixture : AppFixtureBase, IPlatformFlowTestLifecycle
{
    const string PackageId = "com.companyname.mauitodo";
    const string TargetFramework = "net10.0-windows10.0.19041.0";
    const string ResetIdentity = "windows-safe-test-relaunch-v1";

    Process? _appProcess;
    int? _ownedProcessId;
    readonly WindowsDesktopSessionLaunchGate _desktopSessionLaunchGate;
    PlatformBuildResult? _build;
    MauiFlowResetResult? _reset;
    PlatformSeedResult? _seed;
    PlatformLaunchResult? _launch;
    PlatformAgentIdentity? _agent;
    WindowsDesktopSessionAdmission? _lastDesktopSessionAdmission;

    public WindowsFixture()
        : this(
            new WindowsDesktopSessionAdmissionProbe(),
            new ProcessStartWindowsWinUiProcessStarter())
    {
    }

    internal WindowsFixture(
        IWindowsDesktopSessionAdmissionProbe desktopSessionAdmissionProbe,
        IWindowsWinUiProcessStarter processStarter)
    {
        _desktopSessionLaunchGate = new WindowsDesktopSessionLaunchGate(
            desktopSessionAdmissionProbe,
            processStarter);
    }

    public override string Platform => "windows";
    public override bool SupportsFlowLifecycle => true;
    internal override IPlatformFlowTestLifecycle FlowLifecycle => this;
    protected override bool CanReuseExistingAgent => false;

    internal WindowsFlowTestHost CreateFlowTestHost() => new(this);

    protected override async Task InitializePlatformAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw PlatformFlowLifecycleException.Infrastructure(
                "The Windows MAUI fixture requires a Windows host.");
        }

        await BuildAsync().ConfigureAwait(false);
        await InstallAsync().ConfigureAwait(false);
        await HardResetAsync(new PlatformFlowResetRequest()).ConfigureAwait(false);
        await LaunchAsync().ConfigureAwait(false);
        await WaitForExpectedAgentAsync(new PlatformAgentExpectation
        {
            PackageId = PackageId,
            ExpectedProcessId = _launch?.ProcessId,
            PreviousAgent = _launch?.PreviousAgent,
        }).ConfigureAwait(false);
        await SeedAsync(new PlatformFlowSeedRequest()).ConfigureAwait(false);
        await VerifyCheckpointAsync(new PlatformCheckpointRequest
        {
            Expected = new MauiFlowCheckpoint { Route = "//native" },
            Reset = _reset!,
            Seed = _seed!,
        }).ConfigureAwait(false);
    }

    protected override async Task DisposePlatformAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _appProcess?.Dispose();
            _appProcess = null;
            _ownedProcessId = null;
        }
    }

    internal async Task<PlatformBuildResult> BuildAsync(CancellationToken cancellationToken = default)
    {
        if (_build is { } cached && File.Exists(cached.ArtifactPath))
            return cached;

        PlatformBuildResult? build = null;
        await WithBuildLockAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectPath = GetSampleProjectPath();
            await BuildSampleAsync(
                projectPath,
                TargetFramework,
                $"-p:MauiDevFlowPort={AgentPort} -p:DevFlowIntegrationTest=true").ConfigureAwait(false);

            var executable = FindExecutable();
            build = new PlatformBuildResult
            {
                ArtifactPath = executable,
                AppBuildFingerprint = await ComputeFileFingerprintAsync(executable, cancellationToken).ConfigureAwait(false),
                PackageId = PackageId,
            };
        }).ConfigureAwait(false);

        _build = build ?? throw PlatformFlowLifecycleException.Infrastructure(
            "The Windows integration-test build did not produce an executable.");
        return _build;
    }

    internal async Task<PlatformInstallResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var build = _build ?? await BuildAsync(cancellationToken).ConfigureAwait(false);
        if (!File.Exists(build.ArtifactPath))
        {
            throw PlatformFlowLifecycleException.Infrastructure(
                "The Windows integration-test executable is no longer available.");
        }

        // The sample is intentionally unpackaged (WindowsPackageType=None). There is no package
        // install or package-data deletion step; launching this exact instrumented executable is
        // the install boundary for this fixture.
        return new PlatformInstallResult
        {
            PackageId = PackageId,
            ArtifactPath = build.ArtifactPath,
            Reinstalled = false,
        };
    }

    internal async Task<MauiFlowResetResult> HardResetAsync(
        PlatformFlowResetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _agent ??= await TryGetAgentIdentityAsync(cancellationToken).ConfigureAwait(false);
        await StopOwnedProcessAsync(cancellationToken).ConfigureAwait(false);

        _reset = new MauiFlowResetResult
        {
            Requested = true,
            Succeeded = true,
            AppStateSucceeded = true,
            // DevFlow.Sample has no external backend. App-process reset is not evidence that an
            // external effect was reset, so the contract remains deliberately false.
            BackendTestDataSucceeded = false,
            Strategy = "safe-test-relaunch",
            ResetIdentity = request.Requirement?.ResetIdentity ?? ResetIdentity,
            SeedFingerprint = request.Requirement?.SeedFingerprint,
            BackendStateFingerprint = request.Requirement?.BackendStateFingerprint,
            Reference = request.Requirement?.Reference ?? new MauiFlowResetReference
            {
                Strategy = "safe-test-relaunch",
                ResetId = ResetIdentity,
                Scope = "owned-integration-test-process-memory",
                Version = "1",
            },
            Outcome = new MauiFlowResetOutcome
            {
                Requested = true,
                Succeeded = true,
                AppStateSucceeded = true,
                BackendTestDataSucceeded = false,
                CompletedAt = DateTimeOffset.UtcNow,
                Message = "Only the fixture-owned Debug integration-test process was terminated; no user data was deleted.",
            },
            Message = "Windows test-only process reset completed.",
        };
        return _reset;
    }

    internal async Task<PlatformSeedResult> SeedAsync(
        PlatformFlowSeedRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var retries = Client.TransientFailureRetryCount;
        var retryMutations = Client.RetryMutatingRequests;
        Client.TransientFailureRetryCount = 0;
        Client.RetryMutatingRequests = false;
        try
        {
            var state = await new SampleIntegrationTestControlClient(Client)
                .SeedAsync(request.SeedId, cancellationToken)
                .ConfigureAwait(false);
            EnsureExpectedFingerprint("seed", request.ExpectedSeedFingerprint, state.SeedFingerprint);
            EnsureExpectedFingerprint("backend state", request.ExpectedBackendStateFingerprint, state.BackendStateFingerprint);

            _seed = new PlatformSeedResult
            {
                SeedId = state.SeedId,
                SeedFingerprint = state.SeedFingerprint,
                BackendStateFingerprint = state.BackendStateFingerprint,
                StateFingerprint = state.StateFingerprint,
                ProcessInstanceId = state.ProcessInstanceId,
                AppStateSeed = new MauiFlowAppStateSeedFingerprint
                {
                    SeedId = state.SeedId,
                    Fingerprint = state.SeedFingerprint,
                    Version = "1",
                    Source = "sample-integration-test-extension",
                },
                BackendTestDataSeed = new MauiFlowBackendTestDataSeedFingerprint
                {
                    SeedId = state.SeedId,
                    Fingerprint = state.BackendStateFingerprint,
                    Dataset = "none",
                    Version = "1",
                    Source = "sample-no-external-backend",
                },
                StateOracle = new MauiIndependentBusinessOracleResult
                {
                    OracleId = "sample-integration-state",
                    Succeeded = true,
                    Independent = true,
                    ObservedAt = DateTimeOffset.UtcNow,
                    EvidenceReference = "sample-test-state",
                    Message = "The Debug-only sample extension returned a deterministic, non-sensitive state fingerprint.",
                },
            };

            if (_reset is not null)
            {
                _reset.SeedFingerprint = _seed.SeedFingerprint;
                _reset.BackendStateFingerprint = _seed.BackendStateFingerprint;
                _reset.AppStateSeed = _seed.AppStateSeed;
                _reset.BackendTestDataSeed = _seed.BackendTestDataSeed;
            }

            return _seed;
        }
        finally
        {
            Client.TransientFailureRetryCount = retries;
            Client.RetryMutatingRequests = retryMutations;
        }
    }

    internal async Task<PlatformLaunchResult> LaunchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var desktopSessionAdmission = _desktopSessionLaunchGate.Admit();
        _lastDesktopSessionAdmission = desktopSessionAdmission;
        await CaptureWindowsSessionDiagnosticAsync(
            desktopSessionAdmission,
            artifactRoot: null,
            CancellationToken.None).ConfigureAwait(false);
        if (!desktopSessionAdmission.IsAllowed)
            throw WindowsDesktopSessionLaunchGate.CreateRejectionException(desktopSessionAdmission);

        var build = _build ?? await BuildAsync(cancellationToken).ConfigureAwait(false);
        var previous = _agent ?? await TryGetAgentIdentityAsync(cancellationToken).ConfigureAwait(false);
        await StopOwnedProcessAsync(cancellationToken).ConfigureAwait(false);

        var start = new ProcessStartInfo(build.ArtifactPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(build.ArtifactPath) ?? Environment.CurrentDirectory,
        };
        start.Environment["DEVFLOW_TEST_PORT"] = AgentPort.ToString(CultureInfo.InvariantCulture);
        start.Environment["DEVFLOW_INTEGRATION_TEST_SEED"] = IntegrationTestSeedId;
        start.Environment["DEVFLOW_TEST_WINDOWS_LIFECYCLE"] = "1";

        _appProcess = _desktopSessionLaunchGate.Start(start, desktopSessionAdmission);
        _ownedProcessId = _appProcess.Id;
        _launch = new PlatformLaunchResult
        {
            PackageId = PackageId,
            ProcessId = _ownedProcessId,
            PreviousAgent = previous,
        };
        return _launch;
    }

    internal async Task<PlatformAgentReadyResult> WaitForExpectedAgentAsync(
        PlatformAgentExpectation expectation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readiness = PlatformAgentReadiness.WaitForExpectedAsync(
            _ => Client.GetStatusAsync(),
            expectation,
            cancellationToken: linkedCancellation.Token);

        while (!readiness.IsCompleted)
        {
            if (_appProcess is { HasExited: true } exitedProcess)
            {
                linkedCancellation.Cancel();
                try
                {
                    await readiness.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The process-exit diagnostic below is more actionable than the poll cancel.
                }
                catch
                {
                    // Preserve the exact owned-process exit fact below.
                }

                var exitCode = TryGetExitCode(exitedProcess);
                await CaptureProcessExitDiagnosticsAsync(exitCode, CancellationToken.None).ConfigureAwait(false);
                throw PlatformFlowLifecycleException.Infrastructure(
                    $"The fixture-owned Windows app process {_ownedProcessId} exited before a new process-scoped DevFlow agent became ready (exit code {exitCode?.ToString(CultureInfo.InvariantCulture) ?? "<unknown>"}).");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        var ready = await readiness.ConfigureAwait(false);
        _agent = ready.Agent;
        return ready;
    }

    internal async Task<PlatformCheckpointVerification> VerifyCheckpointAsync(
        PlatformCheckpointRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var build = _build ?? throw PlatformFlowLifecycleException.Precondition("The Windows app has not been built.");
        var seed = _seed ?? request.Seed;
        var agent = _agent ?? throw PlatformFlowLifecycleException.Precondition(
            "A process-scoped Windows DevFlow agent has not been verified.");

        var status = await Client.GetStatusAsync().ConfigureAwait(false)
            ?? throw PlatformFlowLifecycleException.Infrastructure(
                "The Windows DevFlow agent stopped responding during checkpoint verification.");
        if (!string.Equals(status.Device?.Platform, Platform, StringComparison.OrdinalIgnoreCase))
        {
            throw PlatformFlowLifecycleException.Precondition(
                $"Expected Windows agent platform, observed '{status.Device?.Platform ?? "<none>"}'.");
        }
        if (!string.Equals(status.App?.PackageId, PackageId, StringComparison.Ordinal))
        {
            throw PlatformFlowLifecycleException.Precondition(
                $"Expected package '{PackageId}', observed '{status.App?.PackageId ?? "<none>"}'.");
        }
        if (status.App?.ProcessId != agent.ProcessId || status.App.ProcessId != _ownedProcessId)
        {
            throw PlatformFlowLifecycleException.Precondition(
                $"The agent process does not match the fixture-owned process. Expected {_ownedProcessId?.ToString() ?? "<none>"}, observed {status.App?.ProcessId?.ToString() ?? "<none>"}.");
        }
        EnsureObserved("app version", status.App?.Version);
        EnsureObserved("app build", status.App?.Build);
        await EnsureOwnedExecutableMatchesBuildAsync(build, cancellationToken).ConfigureAwait(false);

        var state = await new SampleIntegrationTestControlClient(Client).GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(seed.ProcessInstanceId, state.ProcessInstanceId, StringComparison.Ordinal))
        {
            throw PlatformFlowLifecycleException.Precondition(
                "The test-only state endpoint belongs to a different Windows app process than the seeded instance.");
        }
        EnsureExpectedFingerprint("seed", seed.SeedFingerprint, state.SeedFingerprint);
        EnsureExpectedFingerprint("backend state", seed.BackendStateFingerprint, state.BackendStateFingerprint);

        var observed = new MauiFlowCheckpoint
        {
            AppBuildFingerprint = build.AppBuildFingerprint,
            AgentInstanceId = agent.StableId,
            SeedFingerprint = state.SeedFingerprint,
            BackendStateFingerprint = state.BackendStateFingerprint,
            Route = status.Route,
            Window = status.Window,
            Modal = status.Modal,
            Locale = status.Locale,
            Theme = status.Theme,
            Orientation = status.Orientation,
            DisplayProfile = status.DisplayProfile,
        };
        EnsureObserved("route", observed.Route);
        EnsureObserved("locale", observed.Locale);
        EnsureObserved("theme", observed.Theme);
        EnsureObserved("orientation", observed.Orientation);
        EnsureObserved("display profile", observed.DisplayProfile);
        var expected = MergeCheckpoint(request.Expected, observed);
        var mismatches = CompareCheckpoint(expected, observed);
        if (mismatches.Count > 0)
        {
            throw PlatformFlowLifecycleException.Precondition(
                $"Windows replay preconditions did not match: {string.Join("; ", mismatches)}");
        }

        var target = new MauiFlowRunTarget
        {
            TargetId = agent.StableId,
            Platform = Platform,
            DeviceId = Environment.MachineName,
            DeviceProfile = observed.DisplayProfile,
            AppId = PackageId,
            AppBuildFingerprint = build.AppBuildFingerprint,
            AgentId = status.Agent?.Name,
            AgentInstanceId = agent.StableId,
            Locale = observed.Locale,
            Theme = observed.Theme,
            Orientation = observed.Orientation,
            DisplayProfile = observed.DisplayProfile,
        };
        var context = new MauiFlowRunContext
        {
            Intent = MauiFlowReplayIntents.OrdinaryReplay,
            Preconditions = new MauiFlowReplayPreconditions
            {
                Expected = expected,
                Observed = observed,
                CheckedAt = DateTimeOffset.UtcNow,
                EvidenceReference = "windows-lifecycle-preflight",
            },
            Reset = request.Reset,
            BusinessOracles = seed.StateOracle is null ? [] : [seed.StateOracle],
        };
        return new PlatformCheckpointVerification
        {
            Expected = expected,
            Observed = observed,
            Target = target,
            RunContext = context,
        };
    }

    internal async Task<PlatformHostDiagnostics> CaptureHostDiagnosticsAsync(
        PlatformDiagnosticsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = EnsureRepositoryArtifactRoot(request.ArtifactRoot);
        var runDirectory = Path.Combine(root, SanitizeFileName(request.RunId));
        Directory.CreateDirectory(runDirectory);
        var desktopSessionAdmission = _lastDesktopSessionAdmission ?? _desktopSessionLaunchGate.Admit();
        _lastDesktopSessionAdmission = desktopSessionAdmission;
        var sessionDiagnosticPath = await CaptureWindowsSessionDiagnosticAsync(
            desktopSessionAdmission,
            root,
            cancellationToken).ConfigureAwait(false);

        var facts = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["reason"] = request.Reason,
            ["hostOs"] = Environment.OSVersion.VersionString,
            ["hostCulture"] = CultureInfo.CurrentCulture.Name,
            ["machine"] = Environment.MachineName,
            ["packageId"] = PackageId,
            ["agentPort"] = AgentPort.ToString(CultureInfo.InvariantCulture),
            ["ownedProcessId"] = _ownedProcessId?.ToString(CultureInfo.InvariantCulture),
            ["buildFingerprint"] = _build?.AppBuildFingerprint,
            ["agentInstanceId"] = _agent?.StableId,
            ["seedFingerprint"] = _seed?.SeedFingerprint,
            ["backendStateFingerprint"] = _seed?.BackendStateFingerprint,
            ["resetIdentity"] = _reset?.ResetIdentity,
        };
        try
        {
            var status = await Client.GetStatusAsync().ConfigureAwait(false);
            facts["agentProcessId"] = status?.App?.ProcessId?.ToString(CultureInfo.InvariantCulture);
            facts["route"] = RedactRoute(status?.Route);
            facts["window"] = status?.Window;
            facts["modal"] = status?.Modal;
            facts["locale"] = status?.Locale;
            facts["theme"] = status?.Theme;
            facts["orientation"] = status?.Orientation;
            facts["displayProfile"] = status?.DisplayProfile;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            facts["agentStatus"] = $"{ex.GetType().Name}: unavailable";
        }

        var path = Path.Combine(runDirectory, "windows-host-diagnostics.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);

        var diagnostics = new PlatformHostDiagnostics();
        if (!string.IsNullOrWhiteSpace(sessionDiagnosticPath) && File.Exists(sessionDiagnosticPath))
        {
            diagnostics.Artifacts.Add(new MauiFlowArtifactReference
            {
                ArtifactId = $"windows-session-{SanitizeFileName(request.RunId)}",
                Kind = "windows-session-diagnostics",
                Path = sessionDiagnosticPath,
                Digest = await ComputeFileFingerprintAsync(sessionDiagnosticPath, cancellationToken).ConfigureAwait(false),
                MediaType = "application/json",
                Redacted = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        diagnostics.Artifacts.Add(new MauiFlowArtifactReference
        {
            ArtifactId = $"windows-host-diagnostics-{SanitizeFileName(request.RunId)}",
            Kind = "host-diagnostics",
            Path = path,
            Digest = await ComputeFileFingerprintAsync(path, cancellationToken).ConfigureAwait(false),
            MediaType = "application/json",
            Redacted = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        return diagnostics;
    }

    internal async Task StopAsync(CancellationToken cancellationToken = default)
        => await StopOwnedProcessAsync(cancellationToken).ConfigureAwait(false);

    Task<PlatformBuildResult> IPlatformFlowTestLifecycle.BuildAsync(CancellationToken cancellationToken)
        => BuildAsync(cancellationToken);

    Task<PlatformInstallResult> IPlatformFlowTestLifecycle.InstallAsync(CancellationToken cancellationToken)
        => InstallAsync(cancellationToken);

    Task<MauiFlowResetResult> IPlatformFlowTestLifecycle.HardResetAsync(
        PlatformFlowResetRequest request,
        CancellationToken cancellationToken)
        => HardResetAsync(request, cancellationToken);

    Task<PlatformSeedResult> IPlatformFlowTestLifecycle.SeedAsync(
        PlatformFlowSeedRequest request,
        CancellationToken cancellationToken)
        => SeedAsync(request, cancellationToken);

    Task<PlatformLaunchResult> IPlatformFlowTestLifecycle.LaunchAsync(CancellationToken cancellationToken)
        => LaunchAsync(cancellationToken);

    Task<PlatformAgentReadyResult> IPlatformFlowTestLifecycle.WaitForExpectedAgentAsync(
        PlatformAgentExpectation expectation,
        CancellationToken cancellationToken)
        => WaitForExpectedAgentAsync(expectation, cancellationToken);

    Task<PlatformCheckpointVerification> IPlatformFlowTestLifecycle.VerifyCheckpointAsync(
        PlatformCheckpointRequest request,
        CancellationToken cancellationToken)
        => VerifyCheckpointAsync(request, cancellationToken);

    Task<PlatformHostDiagnostics> IPlatformFlowTestLifecycle.CaptureHostDiagnosticsAsync(
        PlatformDiagnosticsRequest request,
        CancellationToken cancellationToken)
        => CaptureHostDiagnosticsAsync(request, cancellationToken);

    Task IPlatformFlowTestLifecycle.StopAsync(CancellationToken cancellationToken)
        => StopAsync(cancellationToken);

    async ValueTask IAsyncDisposable.DisposeAsync()
        => await StopAsync().ConfigureAwait(false);

    async Task StopOwnedProcessAsync(CancellationToken cancellationToken)
    {
        var process = _appProcess;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                if (_ownedProcessId is null || process.Id != _ownedProcessId.Value)
                {
                    throw PlatformFlowLifecycleException.Precondition(
                        "Refusing to terminate a Windows process that is not owned by this fixture.");
                }

                process.Kill(entireProcessTree: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (PlatformFlowLifecycleException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw PlatformFlowLifecycleException.Infrastructure(
                $"The fixture-owned Windows process {_ownedProcessId} did not exit within 15 seconds.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw PlatformFlowLifecycleException.Infrastructure(
                $"The fixture-owned Windows process {_ownedProcessId} could not be terminated.",
                ex);
        }
        finally
        {
            process.Dispose();
            _appProcess = null;
            _ownedProcessId = null;
        }
    }

    async Task<PlatformAgentIdentity?> TryGetAgentIdentityAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return PlatformAgentIdentity.FromStatus(await Client.GetStatusAsync().ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return null;
        }
    }

    async Task EnsureOwnedExecutableMatchesBuildAsync(
        PlatformBuildResult build,
        CancellationToken cancellationToken)
    {
        var process = _appProcess;
        if (process is null || _ownedProcessId is null || process.HasExited || process.Id != _ownedProcessId.Value)
        {
            throw PlatformFlowLifecycleException.Precondition(
                "The Windows test process is no longer owned by this fixture.");
        }

        string? executablePath;
        try
        {
            executablePath = process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw PlatformFlowLifecycleException.Infrastructure(
                "The fixture could not inspect the owned Windows process executable.",
                ex);
        }
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !string.Equals(
                Path.GetFullPath(executablePath),
                Path.GetFullPath(build.ArtifactPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw PlatformFlowLifecycleException.Precondition(
                "The Windows agent process was not launched from the fixture-built executable.");
        }

        var currentFingerprint = await ComputeFileFingerprintAsync(executablePath, cancellationToken).ConfigureAwait(false);
        EnsureExpectedFingerprint("Windows executable", build.AppBuildFingerprint, currentFingerprint);
    }

    static MauiFlowCheckpoint MergeCheckpoint(MauiFlowCheckpoint requested, MauiFlowCheckpoint observed)
        => new()
        {
            AppBuildFingerprint = requested.AppBuildFingerprint ?? observed.AppBuildFingerprint,
            AgentInstanceId = requested.AgentInstanceId ?? observed.AgentInstanceId,
            SeedFingerprint = requested.SeedFingerprint ?? observed.SeedFingerprint,
            BackendStateFingerprint = requested.BackendStateFingerprint ?? observed.BackendStateFingerprint,
            Route = requested.Route ?? observed.Route,
            Window = requested.Window ?? observed.Window,
            Modal = requested.Modal ?? observed.Modal,
            Locale = requested.Locale ?? observed.Locale,
            Theme = requested.Theme ?? observed.Theme,
            Orientation = requested.Orientation ?? observed.Orientation,
            DisplayProfile = requested.DisplayProfile ?? observed.DisplayProfile,
            CollectionItemKey = requested.CollectionItemKey ?? observed.CollectionItemKey,
        };

    static List<string> CompareCheckpoint(MauiFlowCheckpoint expected, MauiFlowCheckpoint observed)
    {
        var mismatches = new List<string>();
        AddMismatch(mismatches, "app build", expected.AppBuildFingerprint, observed.AppBuildFingerprint);
        AddMismatch(mismatches, "agent instance", expected.AgentInstanceId, observed.AgentInstanceId);
        AddMismatch(mismatches, "seed", expected.SeedFingerprint, observed.SeedFingerprint);
        AddMismatch(mismatches, "backend state", expected.BackendStateFingerprint, observed.BackendStateFingerprint);
        AddMismatch(mismatches, "route", expected.Route, observed.Route);
        AddMismatch(mismatches, "window", expected.Window, observed.Window);
        AddMismatch(mismatches, "modal", expected.Modal, observed.Modal);
        AddMismatch(mismatches, "locale", expected.Locale, observed.Locale);
        AddMismatch(mismatches, "theme", expected.Theme, observed.Theme);
        AddMismatch(mismatches, "orientation", expected.Orientation, observed.Orientation);
        AddMismatch(mismatches, "display", expected.DisplayProfile, observed.DisplayProfile);
        return mismatches;
    }

    static void AddMismatch(List<string> mismatches, string name, string? expected, string? observed)
    {
        if (!string.IsNullOrWhiteSpace(expected) &&
            !string.Equals(expected, observed, StringComparison.Ordinal))
        {
            mismatches.Add($"{name} expected '{expected}', observed '{observed ?? "<none>"}'");
        }
    }

    static void EnsureExpectedFingerprint(string kind, string? expected, string? observed)
    {
        if (!string.IsNullOrWhiteSpace(expected) &&
            !string.Equals(expected, observed, StringComparison.Ordinal))
        {
            throw PlatformFlowLifecycleException.Precondition(
                $"Expected {kind} fingerprint '{expected}', observed '{observed ?? "<none>"}'.");
        }
    }

    static void EnsureObserved(string kind, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw PlatformFlowLifecycleException.Precondition($"Windows checkpoint did not provide {kind}.");
    }

    static async Task<string> ComputeFileFingerprintAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    static string FindExecutable()
    {
        var binDir = GetSampleBuildOutputRoot();
        var candidates = Directory.GetFiles(binDir, "DevFlow.Sample.exe", SearchOption.AllDirectories)
            .Where(path => path.Contains(TargetFramework, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw PlatformFlowLifecycleException.Infrastructure(
                $"No Debug integration-test DevFlow.Sample.exe was found under '{binDir}'.");
        }

        return candidates[0];
    }

    static string EnsureRepositoryArtifactRoot(string path)
    {
        var repositoryRoot = Path.GetFullPath(FindRepoRoot());
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(repositoryRoot, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw PlatformFlowLifecycleException.Precondition(
                "Windows flow diagnostics must remain under the repository artifact root.");
        }

        return fullPath;
    }

    async Task<string?> CaptureWindowsSessionDiagnosticAsync(
        WindowsDesktopSessionAdmission admission,
        string? artifactRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(artifactRoot)
                ? ResolveWindowsDiagnosticArtifactRoot()
                : EnsureRepositoryArtifactRoot(artifactRoot);
            return await WindowsDesktopSessionDiagnostics.WriteAsync(root, admission, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // The admission result remains authoritative if writing supplementary evidence fails.
            return null;
        }
    }

    static string ResolveWindowsDiagnosticArtifactRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("DEVFLOW_FLOW_QA_ARTIFACT_ROOT");
        return string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(FindRepoRoot(), "artifacts", "TestResults", "devflow-integration", "windows")
            : EnsureRepositoryArtifactRoot(configuredRoot);
    }

    async Task CaptureProcessExitDiagnosticsAsync(int? exitCode, CancellationToken cancellationToken)
    {
        try
        {
            var root = ResolveWindowsDiagnosticArtifactRoot();
            var directory = Path.Combine(root, "host-diagnostics");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"windows-process-exit-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{_ownedProcessId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.json");
            var facts = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["event"] = "fixture-owned-process-exited-before-agent-ready",
                ["processId"] = _ownedProcessId?.ToString(CultureInfo.InvariantCulture),
                ["exitCode"] = exitCode?.ToString(CultureInfo.InvariantCulture),
                ["exitCodeHex"] = exitCode is { } code ? $"0x{unchecked((uint)code):X8}" : null,
                ["executableName"] = _build is null ? null : Path.GetFileName(_build.ArtifactPath),
                ["buildFingerprint"] = _build?.AppBuildFingerprint,
                ["agentPort"] = AgentPort.ToString(CultureInfo.InvariantCulture),
                ["resetIdentity"] = _reset?.ResetIdentity,
            };
            WindowsDesktopSessionDiagnostics.AddProcessExitFacts(
                facts,
                _lastDesktopSessionAdmission);
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A failed best-effort host diagnostic must not obscure the process exit reason.
        }
    }

    static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "run" : sanitized[..Math.Min(sanitized.Length, 96)];
    }

    static string? RedactRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
            return route;

        var end = route.IndexOfAny(['?', '#']);
        return end >= 0 ? route[..end] : route;
    }

    const string IntegrationTestSeedId = "devflow-sample-v1";
}
