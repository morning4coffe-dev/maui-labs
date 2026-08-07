using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Maui.DevFlow.Devices;

/// <summary>
/// Versioning of the external device host's wire protocol.
/// </summary>
public static class MobileCanvasProtocol
{
    /// <summary>
    /// The major version this build was written against. A host reporting a different major is
    /// treated as incompatible rather than optimistically driven, because the failure mode of
    /// guessing is silently wrong device control rather than a clean error.
    /// </summary>
    public const int SupportedMajorVersion = 1;

    /// <summary>Whether a reported protocol version is one this build can talk to.</summary>
    public static bool IsSupported(string? reported)
    {
        // An older host predates schema stamping; assume compatibility rather than refusing to
        // work with it, since the routes we use are the long-standing ones.
        if (string.IsNullOrWhiteSpace(reported))
            return true;

        var major = reported.Split('.', 2)[0];
        return int.TryParse(major, out var value) && value == SupportedMajorVersion;
    }
}

/// <summary>
/// State written by an external Mobile Canvas host to <c>~/.mobile-canvas/host.json</c>.
/// <para>
/// Field names mirror that product's own contract exactly. Only what DevFlow needs is modelled,
/// and unknown fields are tolerated: the file is owned by another product, so a shape change must
/// degrade to "no usable host" rather than throwing into a caller that was only asking whether a
/// device was available.
/// </para>
/// <para>
/// Note that this file exists only while the host is <em>running</em> — it is removed on shutdown.
/// Its absence therefore means "no host running", which is not the same as "not installed".
/// </para>
/// </summary>
public sealed record MobileCanvasHostState
{
    [JsonPropertyName("schemaVersion")] public string? SchemaVersion { get; init; }
    [JsonPropertyName("port")] public int Port { get; init; }
    [JsonPropertyName("processId")] public int ProcessId { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }

    /// <summary>
    /// Bearer token a trusted local control client presents on every request. Distinct from the
    /// single-use bootstrap secret the host issues to canvas panels, which is a different flow.
    /// </summary>
    [JsonPropertyName("controlToken")] public string? ControlToken { get; init; }

    [JsonIgnore]
    public bool IsUsable => Port is > 0 and <= 65535;

    [JsonIgnore]
    public string BaseUrl => $"http://127.0.0.1:{Port}";
}

/// <summary>
/// Discovers a locally installed Mobile Canvas host.
/// <para>
/// Discovery is deliberately file-based and never launches anything as a side effect of merely
/// looking. A DevFlow session that only wants to know whether device control is possible must not
/// start a background daemon to find out.
/// </para>
/// </summary>
public static class MobileCanvasHost
{
    /// <summary>Overrides the home directory. For tests only.</summary>
    public static string? HomeOverride { get; set; }

    /// <summary>The per-user directory the external host owns.</summary>
    public static string HomeDirectory =>
        HomeOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mobile-canvas");

    /// <summary>The state file the external host writes when it is running.</summary>
    public static string StateFilePath => Path.Combine(HomeDirectory, "host.json");

    /// <summary>
    /// Reads the host state, or <c>null</c> when no usable host is present.
    /// <para>
    /// Every failure mode — missing file, partial write, foreign schema — collapses to
    /// <c>null</c>, because "there is no device layer here" is an ordinary answer that the whole
    /// design is built to handle gracefully.
    /// </para>
    /// </summary>
    public static MobileCanvasHostState? TryRead()
    {
        try
        {
            var path = StateFilePath;
            if (!File.Exists(path))
                return null;

            using var stream = File.OpenRead(path);
            var state = JsonSerializer.Deserialize(stream, MobileCanvasJsonContext.Default.MobileCanvasHostState);
            return state?.IsUsable == true ? state : null;
        }
        catch (IOException)
        {
            // The host rewrites this file; a torn read is transient and not an error worth raising.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            // A schema we do not recognise means we cannot safely talk to it.
            return null;
        }
    }

    /// <summary>Whether a Mobile Canvas host appears to be installed and running.</summary>
    public static bool IsPresent() => TryRead() is not null;
}

/// <summary>A point payload sent to the device host's input endpoints.</summary>
internal sealed record DevicePointPayload(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y);

[JsonSerializable(typeof(MobileCanvasHostState))]
[JsonSerializable(typeof(DeviceTarget))]
[JsonSerializable(typeof(DeviceTarget[]))]
[JsonSerializable(typeof(DisplayGeometry))]
[JsonSerializable(typeof(DeviceCapabilities))]
[JsonSerializable(typeof(DevicePointPayload))]
internal sealed partial class MobileCanvasJsonContext : JsonSerializerContext;
