using System.Text.Json.Serialization;

namespace Microsoft.Maui.DevFlow.Agent.Core;

internal sealed class MutationLeaseCoordinator
{
    private readonly Func<BrokerRegistration?> _brokerRegistration;
    private readonly LocalLeaseState _local = new();
    private readonly int _leaseDurationMs;

    public MutationLeaseCoordinator(Func<BrokerRegistration?> brokerRegistration, int leaseDurationMs)
    {
        _brokerRegistration = brokerRegistration;
        _leaseDurationMs = Math.Max(1_000, leaseDurationMs);
    }

    public async Task<MutationLeaseStatus> ControlAsync(MutationLeaseRequest request)
    {
        var broker = _brokerRegistration();
        if (broker?.IsConnected == true)
        {
            var brokerStatus = await broker.ControlMutationLeaseAsync(request).ConfigureAwait(false);
            if (brokerStatus is not null)
                return brokerStatus;
        }

        return ControlLocal(request);
    }

    public Task<MutationLeaseStatus> ValidateAsync(string? leaseId)
        => ControlAsync(new MutationLeaseRequest
        {
            Action = "validate",
            LeaseId = leaseId
        });

    private MutationLeaseStatus ControlLocal(MutationLeaseRequest request)
    {
        lock (_local.Gate)
        {
            ExpireLocal();
            switch (request.Action)
            {
                case "claim":
                    if (!string.IsNullOrWhiteSpace(request.LeaseId) &&
                        (request.Force || _local.LeaseId is null || _local.LeaseId == request.LeaseId))
                    {
                        _local.LeaseId = request.LeaseId;
                        _local.HolderKind = Clean(request.HolderKind) ?? "unknown";
                        _local.Label = Clean(request.Label);
                        _local.LastSeenTicks = Environment.TickCount64;
                    }
                    break;
                case "heartbeat":
                case "validate":
                    if (!string.IsNullOrWhiteSpace(request.LeaseId) && _local.LeaseId == request.LeaseId)
                        _local.LastSeenTicks = Environment.TickCount64;
                    break;
                case "release":
                    if (!string.IsNullOrWhiteSpace(request.LeaseId) && _local.LeaseId == request.LeaseId)
                        ClearLocal();
                    break;
                case "status":
                    break;
                default:
                    return MutationLeaseStatus.Failure($"Unknown lease action '{request.Action}'.");
            }

            var youHold = _local.LeaseId is not null &&
                !string.IsNullOrWhiteSpace(request.LeaseId) &&
                string.Equals(_local.LeaseId, request.LeaseId, StringComparison.Ordinal);
            return new MutationLeaseStatus
            {
                Ok = true,
                Allowed = youHold,
                YouHold = youHold,
                HeldByOther = _local.LeaseId is not null && !youHold,
                LeaseId = youHold ? _local.LeaseId : null,
                HolderKind = _local.HolderKind,
                Label = _local.Label,
                ExpiresInMs = _local.LeaseId is null
                    ? 0
                    : Math.Max(0, _leaseDurationMs - (Environment.TickCount64 - _local.LastSeenTicks)),
                Authority = "agent"
            };
        }
    }

    private void ExpireLocal()
    {
        if (_local.LeaseId is not null &&
            Environment.TickCount64 - _local.LastSeenTicks > _leaseDurationMs)
        {
            ClearLocal();
        }
    }

    private void ClearLocal()
    {
        _local.LeaseId = null;
        _local.HolderKind = null;
        _local.Label = null;
        _local.LastSeenTicks = 0;
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class LocalLeaseState
    {
        public object Gate { get; } = new();
        public string? LeaseId { get; set; }
        public string? HolderKind { get; set; }
        public string? Label { get; set; }
        public long LastSeenTicks { get; set; }
    }
}

internal sealed class MutationLeaseRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "status";
    [JsonPropertyName("leaseId")]
    public string? LeaseId { get; set; }
    [JsonPropertyName("holderKind")]
    public string? HolderKind { get; set; }
    [JsonPropertyName("label")]
    public string? Label { get; set; }
    [JsonPropertyName("force")]
    public bool Force { get; set; }
}

internal sealed class MutationLeaseStatus
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }
    [JsonPropertyName("allowed")]
    public bool Allowed { get; set; }
    [JsonPropertyName("youHold")]
    public bool YouHold { get; set; }
    [JsonPropertyName("heldByOther")]
    public bool HeldByOther { get; set; }
    [JsonPropertyName("leaseId")]
    public string? LeaseId { get; set; }
    [JsonPropertyName("holderKind")]
    public string? HolderKind { get; set; }
    [JsonPropertyName("label")]
    public string? Label { get; set; }
    [JsonPropertyName("expiresInMs")]
    public long ExpiresInMs { get; set; }
    [JsonPropertyName("authority")]
    public string Authority { get; set; } = "agent";
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    public static MutationLeaseStatus Failure(string error) => new()
    {
        Ok = false,
        Error = error
    };

    public static MutationLeaseStatus Unrestricted() => new()
    {
        Ok = true,
        Allowed = true,
        YouHold = true,
        Authority = "disabled"
    };
}
