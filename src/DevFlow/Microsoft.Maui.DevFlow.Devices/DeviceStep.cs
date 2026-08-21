using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Maui.DevFlow.Devices;

/// <summary>
/// A recorded interaction with the device rather than the app.
/// <para>
/// Flows record semantic steps against MAUI elements, which is what makes them durable. But a
/// first-run permission prompt, a share sheet, or the soft keyboard is not in the visual tree at
/// all, so a flow that touches one simply could not be authored — the recording dead-ended.
/// </para>
/// <para>
/// A device step closes that gap without weakening the rest. It carries both a coordinate and a
/// description of the native view that was under it, so replay can match the view by text or id
/// and fall back to coordinates only when it must.
/// </para>
/// </summary>
public sealed record DeviceStep
{
    /// <summary>The extension field name flows carry these under.</summary>
    public const string ExtensionKey = "deviceSteps";

    /// <summary>Index of the flow step this device interaction follows.</summary>
    [JsonPropertyName("afterStep")]
    public int AfterStep { get; init; }

    /// <summary>What was done. Currently only <c>tap</c>.</summary>
    [JsonPropertyName("action")]
    public string Action { get; init; } = "tap";

    /// <summary>X in device-independent points from the left of the display.</summary>
    [JsonPropertyName("x")]
    public double X { get; init; }

    /// <summary>Y in device-independent points from the top of the display.</summary>
    [JsonPropertyName("y")]
    public double Y { get; init; }

    /// <summary>
    /// Text of the native view that was under the point, when the device could report one.
    /// Preferred over the coordinate on replay because it survives a layout change.
    /// </summary>
    [JsonPropertyName("nativeText")]
    public string? NativeText { get; init; }

    /// <summary>Resource or accessibility id of that native view, when reported.</summary>
    [JsonPropertyName("nativeId")]
    public string? NativeId { get; init; }

    /// <summary>
    /// True when this step can only be replayed by coordinate.
    /// <para>
    /// Mirrors the vocabulary the flow recorder already uses for a selector without an
    /// AutomationId: it does not block anything, it tells a reviewer which steps will break first
    /// when the UI moves.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public bool IsFragile =>
        string.IsNullOrWhiteSpace(NativeText) && string.IsNullOrWhiteSpace(NativeId);

    /// <summary>
    /// Reads device steps from a flow's extension data. Returns an empty list when the flow
    /// declares none, which is the overwhelmingly common case.
    /// </summary>
    public static IReadOnlyList<DeviceStep> FromExtensionData(
        IReadOnlyDictionary<string, JsonElement>? extensionData)
    {
        if (extensionData is null || !extensionData.TryGetValue(ExtensionKey, out var element))
            return [];

        if (element.ValueKind != JsonValueKind.Array)
            return [];

        return element.Deserialize(DeviceStepJsonContext.Default.DeviceStepArray) ?? [];
    }

    /// <summary>
    /// A one-line description for a review surface. Names the native view when known, because
    /// "tap Allow" is reviewable and "tap (540, 1620)" is not.
    /// </summary>
    public string Describe() =>
        NativeText is { Length: > 0 } text ? $"{Action} \"{text}\" on the device"
        : NativeId is { Length: > 0 } id ? $"{Action} {id} on the device"
        : $"{Action} the device at ({X:0.#}, {Y:0.#})";
}

[JsonSerializable(typeof(DeviceStep))]
[JsonSerializable(typeof(DeviceStep[]))]
internal sealed partial class DeviceStepJsonContext : JsonSerializerContext;
