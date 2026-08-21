using System.Diagnostics;
using System.Globalization;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Agent.IntegrationTests.Fixtures;

/// <summary>
/// macOS-hosted Mac Catalyst lifecycle. The clean reset is a test-build-only seed plus a process
/// relaunch; it does not delete user directories, Keychain entries, or unrelated platform state.
/// </summary>
public sealed class MacCatalystFixture : AppFixtureBase, IPlatformFlowTestLifecycle
{
    const string PackageId = "com.companyname.mauitodo";

    Process? _appProcess;
    PlatformBuildResult? _build;
    MauiFlowResetResult? _reset;
    PlatformSeedResult? _seed;
    PlatformLaunchResult? _launch;
    PlatformAgentIdentity? _agent;

    public override string Platform => "maccatalyst";
    public override bool SupportsFlowLifecycle => true;
    internal override IPlatformFlowTestLifecycle FlowLifecycle => this;
    protected override bool CanReuseExistingAgent => false;

    protected override async Task InitializePlatformAsync()
    {
        await BuildAsync();
        await InstallAsync();
        await HardResetAsync(new PlatformFlowResetRequest { Strategy = "safe-test-relaunch" });
        await LaunchAsync();
        await WaitForExpectedAgentAsync(new PlatformAgentExpectation
        {
            PackageId = PackageId,
            ExpectedProcessId = _launch?.ProcessId,
            PreviousAgent = _launch?.PreviousAgent,
        });
        await SeedAsync(new PlatformFlowSeedRequest());
        await VerifyCheckpointAsync(new PlatformCheckpointRequest
        {
            Expected = new MauiFlowCheckpoint { Route = "//native" },
            Reset = _reset!,
            Seed = _seed!,
        });
    }

    protected override async Task DisposePlatformAsync()
    {
        try { await StopAsync(); } catch { }
        _appProcess?.Dispose();
        _appProcess = null;
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
                "net10.0-maccatalyst",
                $"-p:MauiDevFlowPort={AgentPort} -p:DevFlowIntegrationTest=true");
            var app = FindAppBundle();
            build = new PlatformBuildResult
            {
                ArtifactPath = app,
                AppBuildFingerprint = await AppleFlowLifecycleSupport
                    .ComputeFileFingerprintAsync(Path.Combine(app, "Contents", "Info.plist"), cancellationToken)
                    .ConfigureAwait(false),
                PackageId = PackageId,
            };
        });
        _build = build ?? throw PlatformFlowLifecycleException.Infrastructure(
            "The Mac Catalyst build did not produce an app bundle.");
        return _build;
    }

    internal async Task<PlatformInstallResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var build = _build ?? await BuildAsync(cancellationToken).ConfigureAwait(false);
        if (!Directory.Exists(build.ArtifactPath))
            throw PlatformFlowLifecycleException.Infrastructure("The Mac Catalyst app bundle is unavailable for launch.");
        return new PlatformInstallResult
        {
            PackageId = build.PackageId,
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
        var strategy = request.Strategy ?? request.Requirement?.Strategy;
        if (!string.IsNullOrWhiteSpace(strategy) &&
            strategy is not "safe-test-relaunch" and not "app-private-state")
        {
            throw PlatformFlowLifecycleException.Precondition(
                "Mac Catalyst requires the safe-test-relaunch reset strategy.");
        }
        _agent ??= await TryGetAgentIdentityAsync().ConfigureAwait(false);
        await StopAsync(cancellationToken).ConfigureAwait(false);

        _reset = new MauiFlowResetResult
        {
            Requested = true,
            Succeeded = true,
            AppStateSucceeded = true,
            BackendTestDataSucceeded = true,
            Strategy = "safe-test-relaunch",
            ResetIdentity = "maccatalyst-safe-test-relaunch-v1",
            Reference = new MauiFlowResetReference
            {
                Strategy = "safe-test-relaunch",
                Scope = "test-build-process-memory",
                Version = "1",
                EvidenceReference = "maccatalyst-test-seed-hook",
            },
            Outcome = new MauiFlowResetOutcome
            {
                Requested = true,
                Succeeded = true,
                AppStateSucceeded = true,
                BackendTestDataSucceeded = true,
                CompletedAt = DateTimeOffset.UtcNow,
                EvidenceReference = "maccatalyst-test-seed-hook",
            },
        };
        return _reset;
    }

    internal async Task<PlatformSeedResult> SeedAsync(
        PlatformFlowSeedRequest request,
        CancellationToken cancellationToken = default)
    {
        _seed = await AppleFlowLifecycleSupport.SeedAsync(Client, request, _reset, cancellationToken)
            .ConfigureAwait(false);
        return _seed;
    }

    internal async Task<PlatformLaunchResult> LaunchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var build = _build ?? await BuildAsync(cancellationToken).ConfigureAwait(false);
        var previous = _agent ?? await TryGetAgentIdentityAsync().ConfigureAwait(false);
        await StopAsync(cancellationToken).ConfigureAwait(false);

        var executable = FindExecutable(build.ArtifactPath);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["DEVFLOW_TEST_PORT"] = AgentPort.ToString(CultureInfo.InvariantCulture);
        start.Environment["DEVFLOW_INTEGRATION_TEST_SEED"] = "devflow-sample-v1";
        _appProcess = Process.Start(start)
            ?? throw PlatformFlowLifecycleException.Infrastructure("The Mac Catalyst app process could not be started.");
        _launch = new PlatformLaunchResult
        {
            PackageId = build.PackageId,
            ProcessId = _appProcess.Id,
            PreviousAgent = previous,
        };
        return _launch;
    }

    internal async Task<PlatformAgentReadyResult> WaitForExpectedAgentAsync(
        PlatformAgentExpectation expectation,
        CancellationToken cancellationToken = default)
    {
        var ready = await PlatformAgentReadiness.WaitForExpectedAsync(
            _ => Client.GetStatusAsync(),
            expectation,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _agent = ready.Agent;
        return ready;
    }

    internal async Task<PlatformCheckpointVerification> VerifyCheckpointAsync(
        PlatformCheckpointRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var build = _build ?? throw PlatformFlowLifecycleException.Precondition("The Mac Catalyst app has not been built.");
        var seed = _seed ?? request.Seed;
        var agent = _agent ?? throw PlatformFlowLifecycleException.Precondition("A new Mac Catalyst agent instance has not been verified.");
        var status = await Client.GetStatusAsync().ConfigureAwait(false)
            ?? throw PlatformFlowLifecycleException.Infrastructure("The Mac Catalyst DevFlow agent stopped responding during checkpoint verification.");
        if (!string.Equals(status.App?.PackageId, build.PackageId, StringComparison.Ordinal))
        {
            throw PlatformFlowLifecycleException.Precondition(
                $"Expected package '{build.PackageId}', observed '{status.App?.PackageId ?? "<none>"}'.");
        }
        if (_launch?.ProcessId is { } expectedPid && status.App?.ProcessId != expectedPid)
        {
            throw PlatformFlowLifecycleException.Precondition(
                $"Expected Mac Catalyst process {expectedPid}, observed {status.App?.ProcessId?.ToString() ?? "<none>"}.");
        }

        var state = await new SampleIntegrationTestControlClient(Client).GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(seed.ProcessInstanceId, state.ProcessInstanceId, StringComparison.Ordinal))
            throw PlatformFlowLifecycleException.Precondition("The Mac Catalyst seed endpoint belongs to a different app process.");
        AppleFlowLifecycleSupport.EnsureExpectedFingerprint("seed", seed.SeedFingerprint, state.SeedFingerprint);
        AppleFlowLifecycleSupport.EnsureExpectedFingerprint("backend state", seed.BackendStateFingerprint, state.BackendStateFingerprint);

        var theme = await Client.GetThemeAsync().ConfigureAwait(false);
        var observed = new MauiFlowCheckpoint
        {
            AppBuildFingerprint = build.AppBuildFingerprint,
            AgentInstanceId = agent.StableId,
            SeedFingerprint = state.SeedFingerprint,
            BackendStateFingerprint = state.BackendStateFingerprint,
            Route = status.Route,
            // See AndroidEmulatorFixture: a host that never reports window/modal makes any plan
            // declaring them permanently unsatisfiable rather than merely mismatched.
            Window = status.Window,
            Modal = status.Modal,
            Locale = CultureInfo.CurrentUICulture.Name,
            Theme = theme?.Theme.ToString().ToLowerInvariant(),
            Orientation = "desktop",
            DisplayProfile = "maccatalyst-desktop",
        };
        AppleFlowLifecycleSupport.EnsureObserved("route", observed.Route, "Mac Catalyst");
        AppleFlowLifecycleSupport.EnsureObserved("theme", observed.Theme, "Mac Catalyst");
        var expected = AppleFlowLifecycleSupport.MergeCheckpoint(request.Expected, observed);
        AppleFlowLifecycleSupport.EnsureCheckpointMatches(expected, observed, "Mac Catalyst");
        return new PlatformCheckpointVerification
        {
            Expected = expected,
            Observed = observed,
            Target = new MauiFlowRunTarget
            {
                TargetId = agent.StableId,
                Platform = Platform,
                DeviceProfile = observed.DisplayProfile,
                AppId = build.PackageId,
                AppBuildFingerprint = build.AppBuildFingerprint,
                PackageDigest = build.AppBuildFingerprint,
                AgentId = status.Agent?.Name,
                AgentInstanceId = agent.StableId,
                Locale = observed.Locale,
                Theme = observed.Theme,
                Orientation = observed.Orientation,
                DisplayProfile = observed.DisplayProfile,
            },
            RunContext = new MauiFlowRunContext
            {
                Intent = MauiFlowReplayIntents.OrdinaryReplay,
                Preconditions = new MauiFlowReplayPreconditions
                {
                    Expected = expected,
                    Observed = observed,
                    CheckedAt = DateTimeOffset.UtcNow,
                    EvidenceReference = "maccatalyst-lifecycle-preflight",
                },
                Reset = request.Reset,
                BusinessOracles = seed.StateOracle is null ? [] : [seed.StateOracle],
            },
        };
    }

    internal async Task<PlatformHostDiagnostics> CaptureHostDiagnosticsAsync(
        PlatformDiagnosticsRequest request,
        CancellationToken cancellationToken = default)
    {
        var xcode = await RunProcessAsync("xcodebuild", "-version", timeoutSeconds: 15).ConfigureAwait(false);
        return await AppleFlowLifecycleSupport.WriteDiagnosticsAsync(
            request.ArtifactRoot,
            request.RunId,
            "maccatalyst",
            new Dictionary<string, string?>
            {
                ["reason"] = request.Reason,
                ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["buildFingerprint"] = _build?.AppBuildFingerprint,
                ["agentInstanceId"] = _agent?.StableId,
                ["processId"] = _appProcess?.Id.ToString(CultureInfo.InvariantCulture),
                ["seedFingerprint"] = _seed?.SeedFingerprint,
                ["backendStateFingerprint"] = _seed?.BackendStateFingerprint,
                ["xcodeExitCode"] = xcode.ExitCode.ToString(CultureInfo.InvariantCulture),
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_appProcess is { HasExited: false })
        {
            _appProcess.Kill(entireProcessTree: true);
            try { await _appProcess.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        _appProcess?.Dispose();
        _appProcess = null;
    }

    Task<PlatformBuildResult> IPlatformFlowTestLifecycle.BuildAsync(CancellationToken cancellationToken) => BuildAsync(cancellationToken);
    Task<PlatformInstallResult> IPlatformFlowTestLifecycle.InstallAsync(CancellationToken cancellationToken) => InstallAsync(cancellationToken);
    Task<MauiFlowResetResult> IPlatformFlowTestLifecycle.HardResetAsync(PlatformFlowResetRequest request, CancellationToken cancellationToken) => HardResetAsync(request, cancellationToken);
    Task<PlatformSeedResult> IPlatformFlowTestLifecycle.SeedAsync(PlatformFlowSeedRequest request, CancellationToken cancellationToken) => SeedAsync(request, cancellationToken);
    Task<PlatformLaunchResult> IPlatformFlowTestLifecycle.LaunchAsync(CancellationToken cancellationToken) => LaunchAsync(cancellationToken);
    Task<PlatformAgentReadyResult> IPlatformFlowTestLifecycle.WaitForExpectedAgentAsync(PlatformAgentExpectation expectation, CancellationToken cancellationToken) => WaitForExpectedAgentAsync(expectation, cancellationToken);
    Task<PlatformCheckpointVerification> IPlatformFlowTestLifecycle.VerifyCheckpointAsync(PlatformCheckpointRequest request, CancellationToken cancellationToken) => VerifyCheckpointAsync(request, cancellationToken);
    Task<PlatformHostDiagnostics> IPlatformFlowTestLifecycle.CaptureHostDiagnosticsAsync(PlatformDiagnosticsRequest request, CancellationToken cancellationToken) => CaptureHostDiagnosticsAsync(request, cancellationToken);
    Task IPlatformFlowTestLifecycle.StopAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);
    ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;

    static string FindAppBundle()
    {
        var root = Path.Combine(GetSampleBuildOutputRoot(), "net10.0-maccatalyst");
        var app = Directory.Exists(root)
            ? Directory.GetDirectories(root, "*.app", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        return app ?? throw PlatformFlowLifecycleException.Infrastructure($"No Mac Catalyst app bundle was found under '{root}'.");
    }

    static string FindExecutable(string appBundlePath)
    {
        var directory = Path.Combine(appBundlePath, "Contents", "MacOS");
        var executable = Directory.Exists(directory)
            ? Directory.GetFiles(directory).FirstOrDefault(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
            : null;
        return executable ?? throw PlatformFlowLifecycleException.Infrastructure(
            "The Mac Catalyst app bundle did not contain a launchable executable.");
    }

    async Task<PlatformAgentIdentity?> TryGetAgentIdentityAsync()
    {
        try { return PlatformAgentIdentity.FromStatus(await Client.GetStatusAsync().ConfigureAwait(false)); }
        catch { return null; }
    }
}
