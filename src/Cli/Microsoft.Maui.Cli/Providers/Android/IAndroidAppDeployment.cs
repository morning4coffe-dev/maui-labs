namespace Microsoft.Maui.Cli.Providers.Android;

internal sealed record AndroidAppDeploymentRequest
{
    public required string DeviceSerial { get; init; }
    public required string ApkPath { get; init; }
    public required string PackageId { get; init; }
}

internal sealed record AndroidAppDeploymentSession
{
    public required string DeviceSerial { get; init; }
    public required string PackageId { get; init; }
    public required string Activity { get; init; }
    public int? ProcessId { get; init; }
    public bool PackageWasInstalledBefore { get; init; }
    public bool InstalledByInvocation { get; init; }
    public bool InstallAttempted { get; init; }
    public bool InstallationCompletionUnknown { get; init; }
    public bool LaunchedByInvocation { get; init; }
}

internal sealed record AndroidAppDeploymentCleanupResult
{
    public bool Succeeded { get; init; }
    public bool PackageStopped { get; init; }
    public bool PackageUninstalled { get; init; }
    public bool UninstallSkippedNotOwned { get; init; }
    public string? DetailCode { get; init; }
}

/// <summary>
/// Wipes the app's own persistent state and brings it back on its launcher activity. Only a caller
/// that installed the package may ask for this, which is why the session — not a package name — is
/// the request.
/// </summary>
internal sealed record AndroidAppDataResetResult
{
    public bool Succeeded { get; init; }

    /// <summary>Whether <c>pm clear</c> reported success for the exact package.</summary>
    public bool DataCleared { get; init; }

    /// <summary>Whether the package was relaunched on its recorded launcher activity.</summary>
    public bool Relaunched { get; init; }

    /// <summary>The process id observed after relaunch, when the package exposed one.</summary>
    public int? ProcessId { get; init; }

    public string? DetailCode { get; init; }
}

internal sealed class AndroidAppDeploymentException : Exception
{
    public AndroidAppDeploymentException(
        Microsoft.Maui.Cli.DevFlow.Execution.FlowExecutionException failure,
        AndroidAppDeploymentSession session)
        : base(failure.Message, failure)
    {
        Failure = failure;
        Session = session;
    }

    public Microsoft.Maui.Cli.DevFlow.Execution.FlowExecutionException Failure { get; }
    public AndroidAppDeploymentSession Session { get; }
}

internal interface IAndroidAppDeployment
{
    Task<AndroidAppDeploymentSession> DeployAndLaunchAsync(
        AndroidAppDeploymentRequest request,
        CancellationToken cancellationToken = default);

    Task<AndroidAppDeploymentCleanupResult> CleanupAsync(
        AndroidAppDeploymentSession session,
        string cleanupPolicy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the app's data and relaunches it. Refused unless this invocation installed and
    /// launched the package, so the reset can never wipe app state the caller does not own.
    /// </summary>
    Task<AndroidAppDataResetResult> ResetAppDataAndRelaunchAsync(
        AndroidAppDeploymentSession session,
        CancellationToken cancellationToken = default);
}
