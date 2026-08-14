using Microsoft.Maui.Cli.DevFlow.Android;
using Microsoft.Maui.Cli.Models;
using Microsoft.Maui.Cli.Providers.Android;

namespace Microsoft.Maui.Cli.DevFlow.Execution;

internal sealed class AndroidFlowExecutionAdapter : IFlowExecutionPlatformAdapter
{
    private readonly IAndroidProvider _androidProvider;
    private readonly IAndroidAppDeployment _deployment;
    private readonly IAndroidFlowPortManager _portManager;

    public AndroidFlowExecutionAdapter(
        IAndroidProvider androidProvider,
        IAndroidAppDeployment deployment,
        IAndroidFlowPortManager portManager)
    {
        _androidProvider = androidProvider ?? throw new ArgumentNullException(nameof(androidProvider));
        _deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
        _portManager = portManager ?? throw new ArgumentNullException(nameof(portManager));
    }

    internal static readonly FlowExecutionPlatformDescriptor PlatformDescriptor = new()
    {
        Platform = "android",
        DisplayName = "Android",
        CommandAliases = ["android"],
        FlowPlatformAliases = ["android"],
        AgentPlatformAliases = ["android"],
        TargetFrameworkPlatformIdentifiers = ["android"],
        CandidateArtifactTypes = ["apk"],
        UnsupportedArtifactTypes = ["aab"],
        UnsupportedArtifactCode = "android-aab-unsupported",
        UnsupportedArtifactMessage = "Android flow run v1 supports installable APK artifacts only; AAB distribution bundles are not deployable.",
    };

    public FlowExecutionPlatformDescriptor Descriptor => PlatformDescriptor;

    public void ValidateHost()
    {
    }

    public string? GetDefaultRuntimeIdentifier() => null;

    public async Task<FlowExecutionPlatformPreflight> PreflightAsync(
        FlowExecutionPlatformPreflightRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAndroidArtifact(request.Artifact);
        var device = await ResolveDeviceAsync(request.DeviceSerial, cancellationToken).ConfigureAwait(false);
        return new FlowExecutionPlatformPreflight
        {
            Device = device,
            DeviceSerial = device.Id,
            PackageId = request.Artifact.LaunchIdentity!,
        };
    }

    public async Task<FlowExecutionPlatformSession> PrepareAndLaunchAsync(
        FlowExecutionPlatformRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAndroidArtifact(request.Artifact);
        var device = request.Preflight.Device;
        var reverse = await _portManager.EnsureAsync(new AndroidDevFlowForwardingRequest
        {
            AgentPorts = [],
            EnsureBrokerReverse = true,
            BrokerPort = request.BrokerPort,
            Repair = true,
            DeviceSerial = device.Id,
        }, cancellationToken).ConfigureAwait(false);
        if (!reverse.IsReady ||
            !string.Equals(reverse.SelectedSerial, device.Id, StringComparison.Ordinal))
        {
            if (reverse.BrokerReverseAdded)
                await TryRemoveReverseAsync(device.Id, request.BrokerPort).ConfigureAwait(false);
            throw FlowExecutionException.Infrastructure(
                "android-broker-reverse-failed",
                "The exact Android device could not establish the DevFlow broker reverse mapping.");
        }

        var state = new AndroidFlowExecutionSessionState
        {
            BrokerPort = request.BrokerPort,
            BrokerReverseAdded = reverse.BrokerReverseAdded,
        };
        AndroidAppDeploymentSession deployment;
        try
        {
            deployment = await _deployment.DeployAndLaunchAsync(new AndroidAppDeploymentRequest
            {
                DeviceSerial = device.Id,
                ApkPath = request.Artifact.Path,
                PackageId = request.Artifact.LaunchIdentity!,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (AndroidAppDeploymentException ex)
        {
            state.Deployment = ex.Session;
            throw new FlowExecutionPlatformLaunchException(
                ex.Failure,
                CreateSession(device, state));
        }
        catch
        {
            if (state.BrokerReverseAdded)
                await TryRemoveReverseAsync(device.Id, state.BrokerPort).ConfigureAwait(false);
            throw;
        }
        state.Deployment = deployment;
        return CreateSession(device, state);
    }

    private static FlowExecutionPlatformSession CreateSession(
        Device device,
        AndroidFlowExecutionSessionState state)
    {
        var deployment = state.Deployment ??
            throw new InvalidOperationException("Android deployment state is unavailable.");
        return new()
        {
            Device = device,
            DeviceSerial = device.Id,
            PackageId = deployment.PackageId,
            Platform = PlatformDescriptor.Platform,
            RuntimeKind = device.IsEmulator ? "emulator" : "physical",
            DeviceProfile = device.IsEmulator ? "emulator" : "physical",
            RequireAgentDeviceIdentity = true,
            ProcessId = deployment.ProcessId,
            InstalledByInvocation = deployment.InstalledByInvocation,
            LaunchedByInvocation = deployment.LaunchedByInvocation,
            State = state,
        };
    }

    public async Task EstablishAgentForwardingAsync(
        FlowExecutionPlatformSession session,
        int agentPort,
        int brokerPort,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.State is not AndroidFlowExecutionSessionState state)
        {
            throw FlowExecutionException.Infrastructure(
                "android-deployment-session-missing",
                "The Android deployment session state is unavailable.");
        }

        var report = await _portManager.EnsureAsync(new AndroidDevFlowForwardingRequest
        {
            AgentPorts = [agentPort],
            EnsureBrokerReverse = true,
            BrokerPort = brokerPort,
            Repair = true,
            DeviceSerial = session.DeviceSerial,
        }, cancellationToken).ConfigureAwait(false);
        state.BrokerReverseAdded |= report.BrokerReverseAdded;
        foreach (var port in report.AgentForwards.Where(static forward => forward.Added).Select(static forward => forward.Port))
            state.AgentForwardPortsAdded.Add(port);
        if (!report.IsReady ||
            !string.Equals(report.SelectedSerial, session.DeviceSerial, StringComparison.Ordinal) ||
            !report.AgentForwards.Any(forward => forward.Port == agentPort && forward.PresentAfter))
        {
            throw FlowExecutionException.Infrastructure(
                "android-agent-forward-failed",
                "The exact Android device could not establish the DevFlow agent forward mapping.");
        }
    }

    public async Task<FlowExecutionCleanupResult> CleanupAsync(
        FlowExecutionPlatformSession session,
        string cleanupPolicy,
        CancellationToken cancellationToken = default)
    {
        if (session.State is not AndroidFlowExecutionSessionState state ||
            state.Deployment is null)
        {
            return new FlowExecutionCleanupResult
            {
                Succeeded = false,
                DetailCode = "android-deployment-session-missing",
            };
        }

        AndroidAppDeploymentCleanupResult result;
        try
        {
            result = await _deployment.CleanupAsync(
                state.Deployment,
                cleanupPolicy,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            result = new AndroidAppDeploymentCleanupResult
            {
                Succeeded = false,
                DetailCode = "android-deployment-cleanup-exception",
            };
        }
        var mappingsRemoved = true;
        foreach (var port in state.AgentForwardPortsAdded.OrderBy(static port => port))
        {
            mappingsRemoved &= await TryRemoveForwardAsync(
                session.DeviceSerial,
                port,
                cancellationToken).ConfigureAwait(false);
        }
        if (state.BrokerReverseAdded)
        {
            mappingsRemoved &= await TryRemoveReverseAsync(
                session.DeviceSerial,
                state.BrokerPort,
                cancellationToken).ConfigureAwait(false);
        }
        return new FlowExecutionCleanupResult
        {
            Succeeded = result.Succeeded && mappingsRemoved,
            PackageStopped = result.PackageStopped,
            PackageUninstalled = result.PackageUninstalled,
            UninstallSkippedNotOwned = result.UninstallSkippedNotOwned,
            DetailCode = mappingsRemoved
                ? result.DetailCode
                : "android-port-mapping-cleanup-failed",
        };
    }

    internal static void ValidateAndroidArtifact(ResolvedAppArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!string.IsNullOrWhiteSpace(artifact.OwnedOutputRoot))
        {
            ExecutionPathSafety.ValidateConfinedArtifactPath(
                artifact.OwnedOutputRoot,
                artifact.Path);
        }
        if (!string.Equals(artifact.TargetPlatformIdentifier, "android", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(artifact.TargetRuntimeKind, "android", StringComparison.OrdinalIgnoreCase) ||
            !artifact.TargetFramework.Contains("-android", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(artifact.RuntimeIdentifier) &&
             !artifact.RuntimeIdentifier.StartsWith("android-", StringComparison.OrdinalIgnoreCase)))
        {
            throw FlowExecutionException.Invalid(
                "android-artifact-target-mismatch",
                "The resolved app artifact does not target Android.");
        }
        if (string.Equals(artifact.ArtifactType, "aab", StringComparison.OrdinalIgnoreCase))
        {
            throw FlowExecutionException.Unsupported(
                "android-aab-unsupported",
                "Android flow run v1 supports installable APK artifacts only; AAB distribution bundles are not deployable.");
        }
        if (!string.Equals(artifact.ArtifactType, "apk", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(artifact.Path), ".apk", StringComparison.OrdinalIgnoreCase))
        {
            throw FlowExecutionException.Unsupported(
                "android-artifact-unsupported",
                "Android flow run v1 supports APK artifacts only.");
        }
        if (!string.Equals(artifact.ArtifactContractVersion, "1", StringComparison.Ordinal) ||
            !string.Equals(artifact.ArtifactRole, "deployable", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(artifact.DeploymentModel, "package", StringComparison.OrdinalIgnoreCase) ||
            !artifact.Installable ||
            !artifact.Launchable)
        {
            throw FlowExecutionException.Unsupported(
                "android-artifact-not-deployable",
                "The AppProjectReference metadata does not describe an installable and launchable Android package.");
        }
        if (!string.Equals(artifact.LaunchIdentityKind, "android-package-name", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(artifact.LaunchIdentity) ||
            (!string.IsNullOrWhiteSpace(artifact.ApplicationId) &&
             !string.Equals(artifact.ApplicationId, artifact.LaunchIdentity, StringComparison.Ordinal)))
        {
            throw FlowExecutionException.Invalid(
                "android-launch-identity-invalid",
                "The AppProjectReference launch identity does not match the Android application identity.");
        }
    }

    private async Task<Device> ResolveDeviceAsync(
        string? requestedSerial,
        CancellationToken cancellationToken)
    {
        var online = (await _androidProvider.GetDevicesAsync(cancellationToken).ConfigureAwait(false))
            .Where(static device =>
                device.Platforms.Any(platform => string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase)) &&
                device.State is DeviceState.Connected or DeviceState.Booted)
            .ToArray();
        Device selected;
        if (!string.IsNullOrWhiteSpace(requestedSerial))
        {
            selected = online.SingleOrDefault(device =>
                    string.Equals(device.Id, requestedSerial, StringComparison.Ordinal))
                ?? throw FlowExecutionException.Invalid(
                    "android-device-not-found",
                    "The requested exact Android device serial is not connected and online.");
        }
        else
        {
            selected = online.Length switch
            {
                1 => online[0],
                0 => throw FlowExecutionException.Invalid(
                    "android-device-missing",
                    "No connected and online Android device was found."),
                _ => throw FlowExecutionException.Invalid(
                    "android-device-ambiguous",
                    "Multiple Android devices are online. Specify the exact serial with --device."),
            };
        }

        if (!selected.IsEmulator)
        {
            throw FlowExecutionException.Unsupported(
                "android-physical-device-unsupported",
                "Android flow run v1 refuses physical-device deployment because package and user ownership cannot be proven safely.");
        }
        return selected;
    }

    private async Task TryRemoveReverseAsync(string deviceSerial, int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _ = await TryRemoveReverseAsync(deviceSerial, port, timeout.Token).ConfigureAwait(false);
    }

    private async Task<bool> TryRemoveReverseAsync(
        string deviceSerial,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _portManager.RemoveReverseAsync(
                deviceSerial,
                port,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryRemoveForwardAsync(
        string deviceSerial,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _portManager.RemoveForwardAsync(
                deviceSerial,
                port,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class AndroidFlowExecutionSessionState
{
    public AndroidAppDeploymentSession? Deployment { get; set; }
    public required int BrokerPort { get; init; }
    public bool BrokerReverseAdded { get; set; }
    public HashSet<int> AgentForwardPortsAdded { get; } = [];
}
