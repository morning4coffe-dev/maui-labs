using System.Collections.Concurrent;

namespace Microsoft.Maui.Cli.DevFlow.Broker;

internal sealed class MutationLeaseRegistry
{
    internal const int DefaultLeaseDurationMs = 10_000;

    private readonly ConcurrentDictionary<string, LeaseState> _leases = new(StringComparer.Ordinal);
    private readonly long _leaseDurationMs;

    public MutationLeaseRegistry(int leaseDurationMs = DefaultLeaseDurationMs)
    {
        _leaseDurationMs = Math.Max(1_000, leaseDurationMs);
    }

    public MutationLeaseSnapshot Control(
        string agentId,
        string action,
        string? leaseId,
        string? holderKind,
        string? label,
        bool force)
    {
        var state = _leases.GetOrAdd(agentId, static _ => new LeaseState());
        MutationLeaseSnapshot snapshot;
        lock (state.Gate)
        {
            ExpireIfNeeded(state);
            switch (action)
            {
                case "claim":
                    if (!string.IsNullOrWhiteSpace(leaseId) &&
                        (force || state.LeaseId is null || state.LeaseId == leaseId))
                    {
                        SetHolder(state, leaseId, holderKind, label);
                    }
                    break;
                case "heartbeat":
                case "validate":
                    if (!string.IsNullOrWhiteSpace(leaseId) && state.LeaseId == leaseId)
                        state.LastSeenTicks = Environment.TickCount64;
                    break;
                case "release":
                    if (!string.IsNullOrWhiteSpace(leaseId) && state.LeaseId == leaseId)
                    {
                        Clear(state);
                    }
                    break;
                case "status":
                    break;
                default:
                    throw new ArgumentException($"Unknown lease action '{action}'.", nameof(action));
            }

            snapshot = Snapshot(state, leaseId);
        }
        return snapshot;
    }

    public void Remove(string agentId) => _leases.TryRemove(agentId, out _);

    public void Clear() => _leases.Clear();

    private void ExpireIfNeeded(LeaseState state)
    {
        if (state.LeaseId is not null &&
            Environment.TickCount64 - state.LastSeenTicks > _leaseDurationMs)
        {
            Clear(state);
        }
    }

    private static void SetHolder(LeaseState state, string leaseId, string? holderKind, string? label)
    {
        state.LeaseId = leaseId;
        state.HolderKind = Clean(holderKind) ?? "unknown";
        state.Label = Clean(label);
        state.LastSeenTicks = Environment.TickCount64;
    }

    private MutationLeaseSnapshot Snapshot(LeaseState state, string? callerLeaseId)
    {
        var youHold = state.LeaseId is not null &&
            !string.IsNullOrWhiteSpace(callerLeaseId) &&
            string.Equals(state.LeaseId, callerLeaseId, StringComparison.Ordinal);
        return new MutationLeaseSnapshot(
            Allowed: youHold,
            YouHold: youHold,
            HeldByOther: state.LeaseId is not null && !youHold,
            LeaseId: youHold ? state.LeaseId : null,
            HolderKind: state.HolderKind,
            Label: state.Label,
            ExpiresInMs: state.LeaseId is null
                ? 0
                : Math.Max(0, _leaseDurationMs - (Environment.TickCount64 - state.LastSeenTicks)));
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Clear(LeaseState state)
    {
        state.LeaseId = null;
        state.HolderKind = null;
        state.Label = null;
        state.LastSeenTicks = 0;
    }

    private sealed class LeaseState
    {
        public object Gate { get; } = new();
        public string? LeaseId { get; set; }
        public string? HolderKind { get; set; }
        public string? Label { get; set; }
        public long LastSeenTicks { get; set; }
    }
}

internal sealed record MutationLeaseSnapshot(
    bool Allowed,
    bool YouHold,
    bool HeldByOther,
    string? LeaseId,
    string? HolderKind,
    string? Label,
    long ExpiresInMs);
