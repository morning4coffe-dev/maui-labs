using System.Security.Cryptography;
using System.Text;

namespace Microsoft.Maui.DevFlow.TestAgent.Protocol;

/// <summary>Completed artifact retained in memory only until the host writes its redacted manifest.</summary>
public sealed class AppleTestAgentArtifact
{
    public AppleTestAgentArtifactReference Reference { get; init; } = new();
    public byte[] Content { get; init; } = [];
}

/// <summary>Validates and reassembles bounded artifact chunks received over the authenticated channel.</summary>
public sealed class AppleTestAgentArtifactChunkAssembler
{
    private readonly object _gate = new();
    private readonly int _maximumChunkBytes;
    private readonly int _maximumArtifactBytes;
    private readonly Dictionary<string, PendingArtifact> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AppleTestAgentArtifact> _completed = new(StringComparer.Ordinal);

    public AppleTestAgentArtifactChunkAssembler(
        int maximumChunkBytes = AppleTestAgentProtocolVersions.MaximumArtifactChunkBytes,
        int maximumArtifactBytes = AppleTestAgentProtocolVersions.MaximumArtifactBytes)
    {
        if (maximumChunkBytes is < 1 or > AppleTestAgentProtocolVersions.MaximumArtifactChunkBytes ||
            maximumArtifactBytes is < 1 or > AppleTestAgentProtocolVersions.MaximumArtifactBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumChunkBytes));
        }

        _maximumChunkBytes = maximumChunkBytes;
        _maximumArtifactBytes = maximumArtifactBytes;
    }

    public AppleTestAgentError? Add(AppleTestAgentArtifactChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (string.IsNullOrWhiteSpace(chunk.ArtifactId) ||
            string.IsNullOrWhiteSpace(chunk.Kind) ||
            chunk.ChunkIndex < 0 ||
            chunk.TotalChunks is < 1 or > AppleTestAgentProtocolVersions.MaximumArtifactChunks ||
            chunk.ChunkIndex >= chunk.TotalChunks)
        {
            return Reject("The artifact chunk metadata is invalid.");
        }

        byte[] content;
        try
        {
            content = Convert.FromBase64String(chunk.ContentBase64);
        }
        catch (FormatException)
        {
            return Reject("The artifact chunk is not valid base64.");
        }

        if (content.Length > _maximumChunkBytes ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(AppleTestAgentAuthenticator.ComputeDigest(content)),
                Encoding.ASCII.GetBytes(chunk.ContentDigest)))
        {
            return Reject("The artifact chunk exceeds its limit or digest check failed.");
        }

        lock (_gate)
        {
            if (_completed.ContainsKey(chunk.ArtifactId))
                return Reject("The artifact is already complete.");

            if (!_pending.TryGetValue(chunk.ArtifactId, out var pending))
            {
                pending = new PendingArtifact(chunk.Kind, chunk.TotalChunks);
                _pending.Add(chunk.ArtifactId, pending);
            }
            else if (!string.Equals(pending.Kind, chunk.Kind, StringComparison.Ordinal) ||
                pending.TotalChunks != chunk.TotalChunks)
            {
                return Reject("The artifact chunk does not match the existing artifact.");
            }

            if (pending.Chunks.ContainsKey(chunk.ChunkIndex) ||
                pending.TotalBytes + content.Length > _maximumArtifactBytes)
            {
                return Reject("The artifact chunk is duplicated or exceeds the artifact limit.");
            }

            pending.Chunks.Add(chunk.ChunkIndex, content);
            pending.TotalBytes += content.Length;
            if (!chunk.IsFinal)
                return null;

            if (chunk.ChunkIndex != chunk.TotalChunks - 1 || pending.Chunks.Count != chunk.TotalChunks)
                return Reject("The final artifact chunk is incomplete.");

            using var output = new MemoryStream(pending.TotalBytes);
            for (var index = 0; index < pending.TotalChunks; index++)
            {
                if (!pending.Chunks.TryGetValue(index, out var item))
                    return Reject("The artifact chunks are not contiguous.");
                output.Write(item);
            }

            var bytes = output.ToArray();
            _completed.Add(chunk.ArtifactId, new AppleTestAgentArtifact
            {
                Reference = new AppleTestAgentArtifactReference
                {
                    ArtifactId = chunk.ArtifactId,
                    Kind = pending.Kind,
                    SizeBytes = bytes.Length,
                    Sha256 = AppleTestAgentAuthenticator.ComputeDigest(bytes),
                },
                Content = bytes,
            });
            _pending.Remove(chunk.ArtifactId);
            return null;
        }
    }

    public bool TryGetCompleted(string artifactId, out AppleTestAgentArtifact? artifact)
    {
        lock (_gate)
            return _completed.TryGetValue(artifactId, out artifact);
    }

    public IReadOnlyList<AppleTestAgentArtifactReference> CompletedReferences
    {
        get
        {
            lock (_gate)
                return _completed.Values.Select(static artifact => artifact.Reference).ToArray();
        }
    }

    private static AppleTestAgentError Reject(string message)
        => new()
        {
            Code = AppleTestAgentErrorCodes.ArtifactRejected,
            Category = "artifact",
            Message = message,
            Retryable = false,
        };

    private sealed class PendingArtifact(string kind, int totalChunks)
    {
        public string Kind { get; } = kind;
        public int TotalChunks { get; } = totalChunks;
        public Dictionary<int, byte[]> Chunks { get; } = [];
        public int TotalBytes { get; set; }
    }
}
