using Microsoft.Maui.Cli.Providers.Android;

namespace Microsoft.Maui.Cli.DevFlow.Execution;

/// <summary>
/// The Android lifecycle owner. It is constructed only by the host that actually installed and
/// launched the package, and it re-establishes that state with <c>am force-stop</c>,
/// <c>pm clear</c>, and a relaunch on the launcher activity the install already resolved.
/// </summary>
/// <remarks>
/// The fingerprints this owner reports are derived from what it applied — the package it installed,
/// the device it installed it on, and the seed it chose — never from anything the running app says
/// about itself. That is the whole reason a lifecycle owner can answer questions a broker cannot:
/// it is describing its own actions.
/// </remarks>
internal sealed class AndroidFlowLifecycleResetOwner : IFlowLifecycleResetOwner
{
    internal const string ResetStrategy = "android-clear-app-data";

    private readonly IAndroidAppDeployment _deployment;
    private readonly AndroidAppDeploymentSession _session;
    private readonly string _appBuildIdentity;
    private readonly string? _seedIdentity;
    private readonly Func<CancellationToken, Task<bool>>? _applySeedAsync;
    private readonly string? _backendSeedIdentity;
    private readonly Func<CancellationToken, Task<bool>>? _applyBackendSeedAsync;
    private readonly string _collectionItemKey;

    /// <param name="appBuildIdentity">
    /// A digest of the exact artifact the owner installed. The owner computed it from the file it
    /// deployed, so it identifies the build without asking the app for its version.
    /// </param>
    /// <param name="seedIdentity">
    /// Identity of the app-state seed this owner applies after wiping, or null when it applies none
    /// and the app's own post-wipe state is the seeded state.
    /// </param>
    internal AndroidFlowLifecycleResetOwner(
        IAndroidAppDeployment deployment,
        AndroidAppDeploymentSession session,
        string appBuildIdentity,
        string? seedIdentity = null,
        Func<CancellationToken, Task<bool>>? applySeedAsync = null,
        string? backendSeedIdentity = null,
        Func<CancellationToken, Task<bool>>? applyBackendSeedAsync = null,
        string? collectionItemKey = null)
    {
        _deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentException.ThrowIfNullOrWhiteSpace(appBuildIdentity);
        _appBuildIdentity = appBuildIdentity.Trim();
        _seedIdentity = string.IsNullOrWhiteSpace(seedIdentity) ? null : seedIdentity.Trim();
        _applySeedAsync = applySeedAsync;
        _backendSeedIdentity = string.IsNullOrWhiteSpace(backendSeedIdentity) ? null : backendSeedIdentity.Trim();
        _applyBackendSeedAsync = applyBackendSeedAsync;
        _collectionItemKey = string.IsNullOrWhiteSpace(collectionItemKey)
            ? FlowLifecycleResetFingerprints.NoCollectionItem
            : collectionItemKey.Trim();
        if (_seedIdentity is not null && _applySeedAsync is null)
            throw new ArgumentNullException(nameof(applySeedAsync), "A declared app-state seed must be appliable.");
        if (_backendSeedIdentity is not null && _applyBackendSeedAsync is null)
            throw new ArgumentNullException(nameof(applyBackendSeedAsync), "A declared backend seed must be appliable.");
    }

    public string OwnerId => "android-flow-execution-host";

    /// <summary>
    /// The state the owner already applied. The deployment refuses a pre-existing package, so the
    /// app it launched started from wiped storage — the same state <see cref="ResetAsync"/> restores.
    /// </summary>
    public Task<FlowLifecycleAppliedState?> GetAppliedStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.InstalledByInvocation || !_session.LaunchedByInvocation)
            return Task.FromResult<FlowLifecycleAppliedState?>(null);
        return Task.FromResult<FlowLifecycleAppliedState?>(BuildState(
            appStateSucceeded: true,
            backendTestDataSucceeded: _backendSeedIdentity is not null));
    }

    public async Task<FlowLifecycleResetOutcome> ResetAsync(
        FlowLifecycleResetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_session.InstalledByInvocation || !_session.LaunchedByInvocation)
            return Failed("android-reset-not-owned");
        // The caller may pin the seed it believes is applied, but it can never introduce one: a
        // mismatch is refused rather than adopted.
        if (!string.IsNullOrWhiteSpace(request.ExpectedSeedIdentity) &&
            !string.Equals(request.ExpectedSeedIdentity.Trim(), _seedIdentity, StringComparison.Ordinal))
        {
            return Failed("android-reset-seed-identity-mismatch");
        }
        if (request.RequiresBackendSeed && _backendSeedIdentity is null)
            return Failed("android-reset-backend-seed-unavailable");
        if (request.RequiresCollectionItemKey &&
            string.Equals(_collectionItemKey, FlowLifecycleResetFingerprints.NoCollectionItem, StringComparison.Ordinal))
        {
            return Failed("android-reset-collection-item-unavailable");
        }

        AndroidAppDataResetResult reset;
        try
        {
            reset = await _deployment
                .ResetAppDataAndRelaunchAsync(_session, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Failed("android-reset-failed");
        }

        if (!reset.Succeeded)
            return Failed(reset.DetailCode ?? "android-reset-failed");

        if (_applySeedAsync is not null && !await _applySeedAsync(cancellationToken).ConfigureAwait(false))
            return Failed("android-reset-seed-apply-failed");
        var backendApplied = _applyBackendSeedAsync is null ||
            await _applyBackendSeedAsync(cancellationToken).ConfigureAwait(false);
        if (!backendApplied)
            return Failed("android-reset-backend-seed-apply-failed");

        return new FlowLifecycleResetOutcome
        {
            Succeeded = true,
            Applied = BuildState(
                appStateSucceeded: true,
                backendTestDataSucceeded: _backendSeedIdentity is not null),
            EvidenceIds = string.IsNullOrWhiteSpace(reset.DetailCode)
                ? []
                : [$"android-reset:{reset.DetailCode}:{_session.PackageId}"],
        };
    }

    private FlowLifecycleAppliedState BuildState(bool appStateSucceeded, bool backendTestDataSucceeded)
    {
        var resetIdentity = FlowLifecycleResetFingerprints.ResetIdentity(
            OwnerId,
            ResetStrategy,
            _session.PackageId,
            _session.DeviceSerial);
        return new FlowLifecycleAppliedState
        {
            Strategy = ResetStrategy,
            ResetIdentity = resetIdentity,
            SeedIdentity = _seedIdentity,
            SeedFingerprint = FlowLifecycleResetFingerprints.SeedFingerprint(
                resetIdentity,
                _appBuildIdentity,
                _seedIdentity),
            BackendStateFingerprint = _backendSeedIdentity is null
                ? FlowLifecycleResetFingerprints.NoBackendApplied
                : FlowLifecycleResetFingerprints.BackendFingerprint(resetIdentity, _backendSeedIdentity),
            CollectionItemKey = _collectionItemKey,
            AppStateSucceeded = appStateSucceeded,
            BackendTestDataSucceeded = backendTestDataSucceeded,
        };
    }

    private static FlowLifecycleResetOutcome Failed(string failureCode)
        => new() { Succeeded = false, FailureCode = failureCode };
}
