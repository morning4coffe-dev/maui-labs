using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Maui.DevFlow.Driver;

namespace Microsoft.Maui.Cli.DevFlow.Execution;

/// <summary>
/// A lifecycle reset owner that re-establishes app state by invoking a DevFlow Action the app
/// itself registers, rather than by reinstalling or clearing the package from outside.
/// </summary>
/// <remarks>
/// <para>
/// This is the in-app reset surface <see cref="IFlowLifecycleResetOwner"/> describes. A repair
/// validation is fenced to one agent instance, so an owner that force-stops and relaunches the app
/// invalidates the validation it was asked to enable, after having destroyed state. Invoking an
/// in-process action keeps the same process, the same registration, and the same instance id, which
/// is what makes this owner conformant where an <c>adb pm clear</c> owner cannot be.
/// </para>
/// <para>
/// Ownership is preserved because the owner decides which seed to apply and names it: the app is
/// told what to establish and only reports success. Every fingerprint below is derived from facts
/// the owner supplied, never from a value the app echoed back, so an app cannot talk the owner into
/// attesting state it did not ask for.
/// </para>
/// </remarks>
internal sealed class AppActionFlowLifecycleResetOwner : IFlowLifecycleResetOwner
{
    /// <summary>The action an app registers to opt into agent-led repair validation.</summary>
    internal const string ResetActionName = "devflow-reset";

    /// <summary>
    /// The only strategy this owner can perform. It is deliberately not <c>uninstall-reinstall</c>
    /// or <c>pm-clear</c>: naming one of those would claim a package-level guarantee that an
    /// in-process action does not provide.
    /// </summary>
    internal const string ResetStrategy = "app-action-reset";

    private readonly AgentClient _client;
    private readonly string _appIdentity;
    private readonly string _deviceIdentity;
    private readonly string _appBuildIdentity;
    private FlowLifecycleAppliedState? _applied;

    internal AppActionFlowLifecycleResetOwner(
        AgentClient client,
        string appIdentity,
        string deviceIdentity,
        string appBuildIdentity)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _appIdentity = appIdentity;
        _deviceIdentity = deviceIdentity;
        _appBuildIdentity = appBuildIdentity;
    }

    public string OwnerId => "devflow-app-action-reset";

    /// <summary>
    /// What this owner has applied so far. It stays null until the owner has actually performed a
    /// reset: before that it has established nothing, and reporting the app's current state would
    /// be echoing the app rather than attesting the owner's own action.
    /// </summary>
    public Task<FlowLifecycleAppliedState?> GetAppliedStateAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_applied);

    public async Task<FlowLifecycleResetOutcome> ResetAsync(
        FlowLifecycleResetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Refuse everything this owner cannot itself establish, before touching the app. A reset
        // that runs and then cannot be attested has already destroyed the state it was meant to
        // restore, so each check below fails closed rather than proceeding hopefully.
        if (!string.IsNullOrWhiteSpace(request.RequiredStrategy) &&
            !string.Equals(request.RequiredStrategy.Trim(), ResetStrategy, StringComparison.Ordinal))
        {
            return Failed("repair-reset-strategy-unsupported");
        }

        if (request.RequiresBackendSeed)
            return Failed("repair-backend-seed-unsupported");

        if (request.RequiresCollectionItemKey)
            return Failed("repair-collection-item-unsupported");

        if (!await SupportsResetActionAsync(cancellationToken).ConfigureAwait(false))
            return Failed("repair-reset-action-unavailable");

        // The owner chooses the seed. The app is told which one to establish and never reports one
        // back, so a compromised or buggy app cannot relabel the state the owner attests.
        var seedIdentity = string.IsNullOrWhiteSpace(request.ExpectedSeedIdentity)
            ? null
            : request.ExpectedSeedIdentity.Trim();

        InvokeResult? result;
        try
        {
            var args = new JsonArray { seedIdentity };
            result = await _client.InvokeActionAsync(ResetActionName, args).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Any failure to invoke is reported as a refusal rather than rethrown. The attester
            // reads failure codes, and an exception escaping into the broker would abandon a
            // validation midway instead of failing it cleanly.
            return Failed("repair-app-state-reset-failed");
        }

        if (result is null || !result.Success)
            return Failed("repair-app-state-reset-failed");

        var resetIdentity = FlowLifecycleResetFingerprints.ResetIdentity(
            OwnerId,
            ResetStrategy,
            _appIdentity,
            _deviceIdentity);

        var applied = new FlowLifecycleAppliedState
        {
            Strategy = ResetStrategy,
            ResetIdentity = resetIdentity,
            SeedIdentity = seedIdentity,
            SeedFingerprint = FlowLifecycleResetFingerprints.SeedFingerprint(
                resetIdentity,
                _appBuildIdentity,
                seedIdentity),
            // This owner seeds no backend, and says so explicitly rather than leaving the field to
            // be read as an unmeasured backend state.
            BackendStateFingerprint = FlowLifecycleResetFingerprints.NoBackendApplied,
            CollectionItemKey = FlowLifecycleResetFingerprints.NoCollectionItem,
            AppStateSucceeded = true,
            BackendTestDataSucceeded = false,
        };

        _applied = applied;
        return new FlowLifecycleResetOutcome { Succeeded = true, Applied = applied };
    }

    /// <summary>
    /// True when the connected app registers the well-known reset action. Probed per reset rather
    /// than cached, because an app can be rebuilt with the action added or removed while the broker
    /// keeps running.
    /// </summary>
    private async Task<bool> SupportsResetActionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var actions = await _client.ListActionsAsync().ConfigureAwait(false);
            if (!actions.TryGetProperty("actions", out var list) || list.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var action in list.EnumerateArray())
            {
                if (action.TryGetProperty("name", out var name) &&
                    string.Equals(name.GetString(), ResetActionName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // The probe asks one question: can this app be asked what it supports? Any failure —
            // unreachable agent, malformed payload, a client that rejects the call outright — is
            // answered "no", so an app is never assumed to support a reset it may not have.
        }

        return false;
    }

    private static FlowLifecycleResetOutcome Failed(string failureCode)
        => new() { Succeeded = false, FailureCode = failureCode };
}
