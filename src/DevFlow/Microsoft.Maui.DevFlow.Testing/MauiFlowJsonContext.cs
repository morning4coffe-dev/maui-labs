using System.Text.Json.Serialization;

namespace Microsoft.Maui.DevFlow.Testing;

/// <summary>Source-generated JSON metadata for executable DevFlow flow contracts.</summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(MauiFlow))]
[JsonSerializable(typeof(List<FlowAssert>))]
public sealed partial class MauiFlowJsonContext : JsonSerializerContext;
