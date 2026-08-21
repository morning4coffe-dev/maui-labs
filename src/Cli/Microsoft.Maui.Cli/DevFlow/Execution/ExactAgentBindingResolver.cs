using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.DevFlow.Devices;
using Microsoft.Maui.DevFlow.Driver;

namespace Microsoft.Maui.Cli.DevFlow.Execution;

internal enum ExactAgentBindingSelectionKind
{
    Pending,
    Matched,
    Ambiguous,
}

internal sealed record ExactAgentBindingSelection(
    ExactAgentBindingSelectionKind Kind,
    AgentRegistration? Agent,
    bool MatchingStaleAgentObserved);

internal sealed class ExactAgentBindingResolver
{
    private readonly Func<int, Task<AgentRegistration[]?>> _listAgentsAsync;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _pollInterval;

    public ExactAgentBindingResolver(
        Func<int, Task<AgentRegistration[]?>> listAgentsAsync,
        TimeProvider? clock = null,
        TimeSpan? pollInterval = null)
    {
        _listAgentsAsync = listAgentsAsync ?? throw new ArgumentNullException(nameof(listAgentsAsync));
        _clock = clock ?? TimeProvider.System;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
    }

    public async Task<AgentRegistration[]> SnapshotAsync(
        int brokerPort,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _listAgentsAsync(brokerPort).ConfigureAwait(false)
            ?? throw FlowExecutionException.Infrastructure(
                "broker-agent-snapshot-failed",
                "The DevFlow broker could not provide the pre-launch agent snapshot.");
    }

    public async Task<AgentRegistration> WaitForNewAsync(
        int brokerPort,
        IReadOnlyCollection<AgentRegistration> snapshot,
        ExactAgentBindingExpectation expectation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = _clock.GetUtcNow() + timeout;
        var staleObserved = false;
        while (_clock.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await _listAgentsAsync(brokerPort).ConfigureAwait(false);
            if (current is null)
            {
                throw FlowExecutionException.Infrastructure(
                    "broker-agent-list-failed",
                    "The DevFlow broker became unavailable while waiting for the launched app.");
            }

            var selection = SelectNewMatch(snapshot, current, expectation);
            staleObserved |= selection.MatchingStaleAgentObserved;
            if (selection.Kind == ExactAgentBindingSelectionKind.Ambiguous)
            {
                throw FlowExecutionException.Invalid(
                    "agent-binding-ambiguous",
                    "Multiple new DevFlow agents matched the launched app and device.");
            }
            if (selection.Agent is not null)
                return selection.Agent;

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw FlowExecutionException.Infrastructure(
            staleObserved ? "stale-agent-rejected" : "new-agent-timeout",
            staleObserved
                ? "Only a stale pre-launch DevFlow agent matched; a new launched instance never registered."
                : "The launched app did not register a matching new DevFlow agent before the timeout.");
    }

    internal static ExactAgentBindingSelection SelectNewMatch(
        IReadOnlyCollection<AgentRegistration> snapshot,
        IReadOnlyCollection<AgentRegistration> current,
        ExactAgentBindingExpectation expectation)
    {
        var previous = snapshot.Select(InstanceKey).ToHashSet(StringComparer.Ordinal);
        var matching = current.Where(agent => Matches(agent, expectation)).ToArray();
        var newMatches = matching.Where(agent => !previous.Contains(InstanceKey(agent))).ToArray();
        if (newMatches.Length > 1)
            return new(ExactAgentBindingSelectionKind.Ambiguous, null, matching.Length > newMatches.Length);
        if (newMatches.Length == 1)
            return new(ExactAgentBindingSelectionKind.Matched, newMatches[0], matching.Length > 1);
        return new(ExactAgentBindingSelectionKind.Pending, null, matching.Length > 0);
    }

    /// <summary>
    /// Polls the newly bound agent until it reports itself running, or the budget runs out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registration and readiness are not the same event. The agent registers with the broker as
    /// soon as it starts, but its own HTTP surface answers a moment later, and a freshly installed
    /// Debug build cold-starting on an emulator can take longer still. A single probe taken right
    /// after port forwarding therefore fails on a healthy app that is merely slow, which reads as
    /// a broken product rather than a race.
    /// </para>
    /// <para>
    /// The wait shares the caller's agent budget rather than adding one of its own, so the total
    /// time spent getting an agent ready stays bounded by what the operator asked for. A read that
    /// throws is treated as not-yet-ready and retried; only the deadline ends the wait.
    /// </para>
    /// </remarks>
    public async Task<AgentStatus> WaitForLiveStatusAsync(
        Func<Task<AgentStatus?>> readStatusAsync,
        ExactAgentBindingExpectation expectation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readStatusAsync);

        var deadline = _clock.GetUtcNow() + timeout;
        AgentStatus? status = null;
        var everReachable = false;
        var attempts = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            try
            {
                status = await readStatusAsync().ConfigureAwait(false);
                if (status is not null)
                    everReachable = true;
            }
            catch (Exception) when (_clock.GetUtcNow() < deadline)
            {
                status = null;
            }

            if (status?.Running == true)
                break;

            if (_clock.GetUtcNow() >= deadline)
            {
                // Name which of the two faults happened. "Not running" sends someone looking at the
                // app; "never answered" sends them to forwarding and ports, and telling them apart
                // afterwards costs a reproduction.
                throw everReachable
                    ? FlowExecutionException.Infrastructure(
                        "agent-not-running",
                        $"The newly bound DevFlow agent answered but never reported itself running " +
                        $"within {timeout.TotalSeconds:0} seconds ({attempts} probes).")
                    : FlowExecutionException.Infrastructure(
                        "agent-not-reachable",
                        $"The newly bound DevFlow agent never answered its status endpoint within " +
                        $"{timeout.TotalSeconds:0} seconds ({attempts} probes). Check agent port forwarding.");
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        ValidateLiveStatus(status, expectation);
        return status!;
    }

    internal static void ValidateLiveStatus(
        AgentStatus? status,
        ExactAgentBindingExpectation expectation)
    {
        if (status?.Running != true)
            throw FlowExecutionException.Infrastructure("agent-not-running", "The newly bound DevFlow agent is not running.");
        if (!string.Equals(status.App?.PackageId, expectation.PackageId, StringComparison.Ordinal))
        {
            throw FlowExecutionException.Invalid(
                "agent-package-mismatch",
                "The newly bound DevFlow agent reported a different package identity.");
        }
        if (expectation.ProcessId is not null &&
            status.App?.ProcessId != expectation.ProcessId)
        {
            throw FlowExecutionException.Invalid(
                "agent-process-mismatch",
                "The newly bound DevFlow agent reported a different process identity.");
        }
        if (!string.IsNullOrWhiteSpace(status.Device?.Platform) &&
            !MatchesPlatform(status.Device.Platform, expectation.PlatformAliases))
        {
            throw FlowExecutionException.Invalid(
                "agent-platform-mismatch",
                "The newly bound DevFlow agent reported a different platform.");
        }
        if (!string.IsNullOrWhiteSpace(expectation.AppBuild) &&
            !string.IsNullOrWhiteSpace(status.App?.Build) &&
            !string.Equals(status.App.Build, expectation.AppBuild, StringComparison.Ordinal))
        {
            throw FlowExecutionException.Invalid(
                "agent-build-mismatch",
                "The newly bound DevFlow agent reported a different app build.");
        }
    }

    private static bool Matches(AgentRegistration agent, ExactAgentBindingExpectation expectation)
    {
        if (!string.Equals(agent.SessionId, expectation.SessionId, StringComparison.Ordinal) ||
            !string.Equals(agent.Tfm, expectation.TargetFramework, StringComparison.OrdinalIgnoreCase) ||
            !MatchesPlatform(agent.Platform, expectation.PlatformAliases) ||
            !string.Equals(agent.PackageId, expectation.PackageId, StringComparison.Ordinal))
        {
            return false;
        }

        if (expectation.ProcessId is not null &&
            agent.ProcessId != expectation.ProcessId)
        {
            return false;
        }

        if (!expectation.RequireDeviceIdentityMatch)
            return true;
        if (string.IsNullOrWhiteSpace(agent.DeviceId))
            return false;

        var identity = DeviceIdentity.Parse(agent.DeviceId);
        if (!string.IsNullOrWhiteSpace(identity.Platform) &&
            !MatchesPlatform(identity.Platform, expectation.PlatformAliases))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectation.DeviceEmulatorId))
        {
            return string.Equals(
                identity.AvdName?.Trim(),
                expectation.DeviceEmulatorId.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(expectation.DeviceSerial))
        {
            return string.Equals(identity.Serial, expectation.DeviceSerial, StringComparison.Ordinal) ||
                string.Equals(identity.Udid, expectation.DeviceSerial, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool MatchesPlatform(string value, IReadOnlyCollection<string> aliases)
        => aliases.Any(alias => string.Equals(alias, value, StringComparison.OrdinalIgnoreCase));

    private static string InstanceKey(AgentRegistration agent)
        => !string.IsNullOrWhiteSpace(agent.InstanceId)
            ? agent.InstanceId
            : string.Join(
                "\u001f",
                agent.Id,
                agent.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                agent.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                agent.ConnectedAt.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));

}
