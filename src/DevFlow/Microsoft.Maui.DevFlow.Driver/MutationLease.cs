using System.Text.Json.Serialization;

namespace Microsoft.Maui.DevFlow.Driver;

public sealed class MutationLeaseStatus
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
    public string? Authority { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class MutationLeaseException : InvalidOperationException
{
    public MutationLeaseException(MutationLeaseStatus status)
        : base(status.HeldByOther
            ? $"Another DevFlow session is driving this app ({status.Label ?? status.HolderKind ?? "unknown holder"})."
            : status.Error ?? "Could not acquire the DevFlow mutation lease.")
    {
        Status = status;
    }

    public MutationLeaseStatus Status { get; }
}

public sealed class MutationRecordingStatus
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }
    [JsonPropertyName("recording")]
    public bool Recording { get; set; }
    [JsonPropertyName("recordingId")]
    public string? RecordingId { get; set; }
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("steps")]
    public int Steps { get; set; }
    [JsonPropertyName("seq")]
    public int? Seq { get; set; }
    [JsonPropertyName("fragile")]
    public bool Fragile { get; set; }
    [JsonPropertyName("markdown")]
    public string? Markdown { get; set; }
    [JsonPropertyName("warnings")]
    public string[]? Warnings { get; set; }
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
