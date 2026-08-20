using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Agent.IntegrationTests.Fixtures;

/// <summary>
/// macOS-hosted iOS Simulator lifecycle. Reset is uninstall/install plus a test-build-only seed;
/// it never targets a physical device or user app data.
/// </summary>
public sealed class iOSSimulatorFixture : AppFixtureBase, IPlatformFlowTestLifecycle
{
    const string PackageId = "com.companyname.mauitodo";

    string? _simulatorUdid;
    bool _weBootedSimulator;
    string? _appBundleId;
    PlatformBuildResult? _build;
    MauiFlowResetResult? _reset;
    PlatformSeedResult? _seed;
    PlatformLaunchResult? _launch;
    PlatformAgentIdentity? _agent;
    bool _requiresInstall;

    public override string Platform => "ios";
    public override bool SupportsFlowLifecycle => true;
    internal override IPlatformFlowTestLifecycle FlowLifecycle => this;
    protected override bool CanReuseExistingAgent => false;

    protected override async Task InitializePlatformAsync()
    {
        var (udid, alreadyBooted) = await FindOrBootSimulatorAsync();
        _simulatorUdid = udid;
        _weBootedSimulator = !alreadyBooted;

        await BuildAsync();
        await InstallAsync();
        await HardResetAsync(new PlatformFlowResetRequest { Strategy = "uninstall-reinstall" });
        await InstallAsync();
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
        if (_weBootedSimulator && _simulatorUdid is not null)
        {
            try { await RunProcessAsync("xcrun", $"simctl shutdown {_simulatorUdid}", timeoutSeconds: 15); } catch { }
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
                "net10.0-ios",
                $"-p:_DeviceTarget=simulator -p:RuntimeIdentifier={GetSimulatorRuntimeIdentifier()} " +
                $"-p:MauiDevFlowPort={AgentPort} -p:DevFlowIntegrationTest=true");
            var appBundle = FindSimulatorAppBundle();
            _appBundleId = await ReadBundleIdAsync(appBundle).ConfigureAwait(false);
            build = new PlatformBuildResult
            {
                ArtifactPath = appBundle,
                AppBuildFingerprint = await AppleFlowLifecycleSupport
                    .ComputeFileFingerprintAsync(Path.Combine(appBundle, "Info.plist"), cancellationToken)
                    .ConfigureAwait(false),
                PackageId = _appBundleId,
            };
        });

        _build = build ?? throw PlatformFlowLifecycleException.Infrastructure(
            "The iOS Simulator build did not produce an app bundle.");
        return _build;
    }

    internal async Task<PlatformInstallResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var build = _build ?? await BuildAsync(cancellationToken).ConfigureAwait(false);
        var simulator = RequireSimulator();
        await RunProcessCheckedAsync("xcrun", $"simctl install {simulator} \"{build.ArtifactPath}\"", timeoutSeconds: 180);
        _requiresInstall = false;
        return new PlatformInstallResult
        {
            PackageId = build.PackageId,
            ArtifactPath = build.ArtifactPath,
            Reinstalled = true,
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
            strategy is not "uninstall-reinstall" and not "uninstall")
        {
            throw PlatformFlowLifecycleException.Precondition(
                "iOS Simulator requires the uninstall-reinstall hard-reset strategy.");
        }

        _agent ??= await TryGetAgentIdentityAsync().ConfigureAwait(false);
        var simulator = RequireSimulator();
        var bundleId = _appBundleId ?? (_build?.PackageId ?? PackageId);
        try { await RunProcessAsync("xcrun", $"simctl terminate {simulator} {bundleId}", timeoutSeconds: 15); } catch { }

        var uninstall = await RunProcessAsync("xcrun", $"simctl uninstall {simulator} {bundleId}", timeoutSeconds: 60);
        if (uninstall.ExitCode != 0 &&
            !uninstall.Stdout.Contains("not installed", StringComparison.OrdinalIgnoreCase) &&
            !uninstall.Stderr.Contains("not installed", StringComparison.OrdinalIgnoreCase))
        {
            throw PlatformFlowLifecycleException.Infrastructure("iOS Simulator uninstall failed before a clean flow attempt.");
        }

        _requiresInstall = true;
        _reset = new MauiFlowResetResult
        {
            Requested = true,
            Succeeded = true,
            AppStateSucceeded = true,
            BackendTestDataSucceeded = true,
            Strategy = "uninstall-reinstall",
            ResetIdentity = "ios-simulator-uninstall-reinstall-v1",
            Reference = new MauiFlowResetReference
            {
                Strategy = "uninstall-reinstall",
                Scope = "ios-simulator-app-container",
                Version = "1",
                EvidenceReference = "ios-simulator-lifecycle",
            },
            Outcome = new MauiFlowResetOutcome
            {
                Requested = true,
                Succeeded = true,
                AppStateSucceeded = true,
                BackendTestDataSucceeded = true,
                CompletedAt = DateTimeOffset.UtcNow,
                EvidenceReference = "ios-simulator-lifecycle",
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
        if (_requiresInstall)
            await InstallAsync(cancellationToken).ConfigureAwait(false);

        var previous = _agent ?? await TryGetAgentIdentityAsync().ConfigureAwait(false);
        var simulator = RequireSimulator();
        var bundleId = _appBundleId ?? (_build?.PackageId ?? PackageId);
        var launch = await RunProcessCheckedAsync(
            "xcrun",
            $"simctl launch --terminate-running-process {simulator} {bundleId}",
            new Dictionary<string, string>
            {
                ["SIMCTL_CHILD_DEVFLOW_TEST_PORT"] = AgentPort.ToString(CultureInfo.InvariantCulture),
                ["SIMCTL_CHILD_DEVFLOW_INTEGRATION_TEST_SEED"] = "devflow-sample-v1",
            },
            timeoutSeconds: 90).ConfigureAwait(false);
        var processId = TryParseSimulatorProcessId(launch);
        _launch = new PlatformLaunchResult
        {
            PackageId = bundleId,
            ProcessId = processId,
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
        var build = _build ?? throw PlatformFlowLifecycleException.Precondition("The iOS app has not been built.");
        var seed = _seed ?? request.Seed;
        var agent = _agent ?? throw PlatformFlowLifecycleException.Precondition("A new iOS agent instance has not been verified.");
        var status = await Client.GetStatusAsync().ConfigureAwait(false)
            ?? throw PlatformFlowLifecycleException.Infrastructure("The iOS DevFlow agent stopped responding during checkpoint verification.");
        if (!string.Equals(status.App?.PackageId, build.PackageId, StringComparison.Ordinal))
        {
            throw PlatformFlowLifecycleException.Precondition(
                $"Expected package '{build.PackageId}', observed '{status.App?.PackageId ?? "<none>"}'.");
        }
        if (_launch?.ProcessId is { } expectedPid && status.App?.ProcessId != expectedPid)
        {
            throw PlatformFlowLifecycleException.Precondition(
                $"Expected iOS process {expectedPid}, observed {status.App?.ProcessId?.ToString() ?? "<none>"}.");
        }

        var state = await new SampleIntegrationTestControlClient(Client).GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(seed.ProcessInstanceId, state.ProcessInstanceId, StringComparison.Ordinal))
            throw PlatformFlowLifecycleException.Precondition("The iOS seed endpoint belongs to a different app process.");
        AppleFlowLifecycleSupport.EnsureExpectedFingerprint("app build", build.AppBuildFingerprint, build.AppBuildFingerprint);
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
            Orientation = "simulator",
            DisplayProfile = $"ios-simulator:{RequireSimulator()}",
        };
        AppleFlowLifecycleSupport.EnsureObserved("route", observed.Route, "iOS");
        AppleFlowLifecycleSupport.EnsureObserved("theme", observed.Theme, "iOS");
        var expected = AppleFlowLifecycleSupport.MergeCheckpoint(request.Expected, observed);
        AppleFlowLifecycleSupport.EnsureCheckpointMatches(expected, observed, "iOS");

        return new PlatformCheckpointVerification
        {
            Expected = expected,
            Observed = observed,
            Target = new MauiFlowRunTarget
            {
                TargetId = agent.StableId,
                Platform = Platform,
                DeviceId = RequireSimulator(),
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
                    EvidenceReference = "ios-simulator-lifecycle-preflight",
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
            "ios",
            new Dictionary<string, string?>
            {
                ["reason"] = request.Reason,
                ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["simulatorIdDigest"] = _simulatorUdid is null
                    ? null
                    : $"sha256:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_simulatorUdid))).ToLowerInvariant()}",
                ["buildFingerprint"] = _build?.AppBuildFingerprint,
                ["agentInstanceId"] = _agent?.StableId,
                ["seedFingerprint"] = _seed?.SeedFingerprint,
                ["backendStateFingerprint"] = _seed?.BackendStateFingerprint,
                ["xcodeExitCode"] = xcode.ExitCode.ToString(CultureInfo.InvariantCulture),
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_simulatorUdid is not null && _appBundleId is not null)
            await RunProcessAsync("xcrun", $"simctl terminate {_simulatorUdid} {_appBundleId}", timeoutSeconds: 15);
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

    async Task<(string Udid, bool AlreadyBooted)> FindOrBootSimulatorAsync()
    {
        var versionPattern = Environment.GetEnvironmentVariable("DEVFLOW_TEST_IOS_VERSION");
        var json = await RunProcessCheckedAsync("xcrun", "simctl list devices --json");
        using var doc = JsonDocument.Parse(json);
        var candidates = new List<(string Udid, string Name, string Runtime, string State)>();
        foreach (var runtime in doc.RootElement.GetProperty("devices").EnumerateObject())
        {
            if (!runtime.Name.Contains("iOS", StringComparison.OrdinalIgnoreCase))
                continue;
            var osVersion = ExtractOsVersion(runtime.Name);
            if (osVersion is null || (!string.IsNullOrWhiteSpace(versionPattern) && !MatchesVersionPattern(osVersion, versionPattern)))
                continue;

            foreach (var device in runtime.Value.EnumerateArray())
            {
                var available = !device.TryGetProperty("isAvailable", out var flag) || flag.GetBoolean();
                var name = device.GetProperty("name").GetString() ?? string.Empty;
                if (!available || !name.Contains("iPhone", StringComparison.OrdinalIgnoreCase))
                    continue;
                candidates.Add((
                    device.GetProperty("udid").GetString() ?? string.Empty,
                    name,
                    osVersion,
                    device.GetProperty("state").GetString() ?? string.Empty));
            }
        }
        if (candidates.Count == 0)
            throw PlatformFlowLifecycleException.Infrastructure("No available iPhone simulator matched the requested iOS runtime.");
        var selected = candidates
            .OrderByDescending(candidate => ExtractIPhoneModelNumber(candidate.Name))
            .ThenByDescending(candidate => candidate.Runtime)
            .First();
        if (!string.Equals(selected.State, "Booted", StringComparison.OrdinalIgnoreCase))
            await RunProcessCheckedAsync("xcrun", $"simctl boot {selected.Udid}", timeoutSeconds: 60);
        await RunProcessCheckedAsync("xcrun", $"simctl bootstatus {selected.Udid} -b", timeoutSeconds: 180);
        return (selected.Udid, string.Equals(selected.State, "Booted", StringComparison.OrdinalIgnoreCase));
    }

    static string GetSimulatorRuntimeIdentifier() =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "iossimulator-arm64" : "iossimulator-x64";

    static string FindSimulatorAppBundle()
    {
        var binDir = Path.Combine(GetSampleBuildOutputRoot(), "net10.0-ios", GetSimulatorRuntimeIdentifier());
        var app = Directory.Exists(binDir)
            ? Directory.GetDirectories(binDir, "*.app", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        return app ?? throw PlatformFlowLifecycleException.Infrastructure($"No iOS simulator app bundle was found under '{binDir}'.");
    }

    static async Task<string> ReadBundleIdAsync(string appBundlePath)
    {
        var plist = Path.Combine(appBundlePath, "Info.plist");
        var result = await RunProcessAsync("/usr/libexec/PlistBuddy", $"-c \"Print :CFBundleIdentifier\" \"{plist}\"", timeoutSeconds: 15);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
            throw PlatformFlowLifecycleException.Infrastructure("The iOS app bundle did not expose CFBundleIdentifier.");
        return result.Stdout.Trim();
    }

    async Task<PlatformAgentIdentity?> TryGetAgentIdentityAsync()
    {
        try { return PlatformAgentIdentity.FromStatus(await Client.GetStatusAsync().ConfigureAwait(false)); }
        catch { return null; }
    }

    string RequireSimulator() => _simulatorUdid
        ?? throw PlatformFlowLifecycleException.Infrastructure("The iOS Simulator lifecycle has not been initialized.");

    static int? TryParseSimulatorProcessId(string output)
    {
        var match = Regex.Match(output, @":\s*(\d+)\s*$");
        return match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var pid) ? pid : null;
    }

    static int ExtractIPhoneModelNumber(string name)
    {
        var match = Regex.Match(name, @"iPhone\s+(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    static string? ExtractOsVersion(string runtimeId)
    {
        var match = Regex.Match(runtimeId, @"iOS[- ](\d+)[- ](\d+)");
        if (match.Success)
            return $"{match.Groups[1].Value}.{match.Groups[2].Value}";
        match = Regex.Match(runtimeId, @"iOS[- ](\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    static bool MatchesVersionPattern(string version, string pattern)
        => Regex.IsMatch(version, "^" + Regex.Escape(pattern).Replace("x", @"\d+") + "$", RegexOptions.IgnoreCase);
}
