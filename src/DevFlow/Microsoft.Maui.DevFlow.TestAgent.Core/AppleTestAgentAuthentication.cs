using System.Security.Cryptography;
using System.Text;

namespace Microsoft.Maui.DevFlow.TestAgent.Protocol;

/// <summary>Canonical HMAC authentication for local host/device agent messages.</summary>
public static class AppleTestAgentAuthenticator
{
    public static string CreateSignature(
        ReadOnlySpan<byte> secret,
        string method,
        string path,
        string sessionId,
        string? commandId,
        long sequence,
        long timestampUnixSeconds,
        string nonce,
        string bodyDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyDigest);

        var material = string.Join(
            "\n",
            method.Trim().ToUpperInvariant(),
            path,
            sessionId,
            commandId ?? string.Empty,
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            timestampUnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            nonce,
            bodyDigest);
        return Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(material)));
    }

    public static bool Verify(
        ReadOnlySpan<byte> secret,
        AppleTestAgentAuthentication authentication,
        string method,
        string path,
        string? commandId,
        long sequence,
        string bodyDigest,
        DateTimeOffset now,
        TimeSpan maximumClockSkew,
        AppleTestAgentReplayProtector replayProtector)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(replayProtector);
        if (string.IsNullOrWhiteSpace(authentication.SessionId) ||
            string.IsNullOrWhiteSpace(authentication.Nonce) ||
            string.IsNullOrWhiteSpace(authentication.Signature) ||
            Math.Abs(now.ToUnixTimeSeconds() - authentication.TimestampUnixSeconds) > maximumClockSkew.TotalSeconds)
        {
            return false;
        }

        var expected = CreateSignature(
            secret,
            method,
            path,
            authentication.SessionId,
            commandId,
            sequence,
            authentication.TimestampUnixSeconds,
            authentication.Nonce,
            bodyDigest);
        var valid = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(authentication.Signature));
        return valid && replayProtector.TryConsume(
            authentication.SessionId,
            authentication.Nonce,
            now,
            maximumClockSkew);
    }

    public static string ComputeDigest(ReadOnlySpan<byte> content)
        => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(content))}";
}

/// <summary>Bounded nonce cache used to reject authenticated request replay within the clock-skew window.</summary>
public sealed class AppleTestAgentReplayProtector
{
    private readonly object _gate = new();
    private readonly int _maximumEntries;
    private readonly Dictionary<string, DateTimeOffset> _entries = new(StringComparer.Ordinal);

    public AppleTestAgentReplayProtector(int maximumEntries = 2_048)
    {
        if (maximumEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));

        _maximumEntries = maximumEntries;
    }

    public bool TryConsume(string sessionId, string nonce, DateTimeOffset now, TimeSpan retention)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(nonce))
            return false;

        var key = $"{sessionId}\n{nonce}";
        lock (_gate)
        {
            foreach (var expired in _entries
                .Where(pair => now - pair.Value > retention)
                .Select(static pair => pair.Key)
                .ToArray())
            {
                _entries.Remove(expired);
            }

            if (_entries.ContainsKey(key) || _entries.Count >= _maximumEntries)
                return false;

            _entries.Add(key, now);
            return true;
        }
    }
}
