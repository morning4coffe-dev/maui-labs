using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Maui.DevFlow.Devices;

/// <summary>
/// State written by an external Mobile Canvas host to <c>~/.mobile-canvas/host.json</c>.
/// <para>
/// Only the fields DevFlow actually needs are modelled. The file is owned by another product, so
/// unknown fields must be tolerated and a shape change must degrade to "no host" rather than
/// throwing into a caller that was only asking whether a device was available.
/// </para>
/// </summary>
public sealed record MobileCanvasHostState
{
    [JsonPropertyName("port")] public int Port { get; init; }
    [JsonPropertyName("pid")] public int Pid { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }

    /// <summary>Single-use secret exchanged for a session cookie by a local client.</summary>
    [JsonPropertyName("bootstrapSecret")] public string? BootstrapSecret { get; init; }

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
