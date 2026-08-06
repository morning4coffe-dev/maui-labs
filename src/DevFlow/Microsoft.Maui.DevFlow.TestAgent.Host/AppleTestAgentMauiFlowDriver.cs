using System.Text;
using System.Text.Json;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.TestAgent.Protocol;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.TestAgent.Host;

/// <summary>
/// Maps operation receipts from the separate XCTest agent to <see cref="IMauiFlowDriver"/>.
/// It contains no selector, actionability, assertion, retry, or replay policy.
/// </summary>
public sealed class AppleTestAgentMauiFlowDriver : IMauiFlowDriver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IAppleTestAgentTransport _transport;

    public AppleTestAgentMauiFlowDriver(IAppleTestAgentTransport transport)
        => _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public WorkflowCommandReceipt? LastWorkflowCommandReceipt { get; private set; }
    /// <summary>
    /// Latest value-free lifecycle facts returned with the status operation. These facts are
    /// host-owned checkpoint evidence, not runner policy or a device-side execution engine.
    /// </summary>
    public AppleTestAgentCheckpointFacts? LastCheckpointFacts { get; private set; }

    public async Task<List<ElementInfo>> QueryAsync(string? type = null, string? automationId = null, string? text = null)
    {
        var completion = await SendAsync(
            AppleTestAgentOperations.Query,
            Arguments(("type", type), ("automationId", automationId), ("text", text))).ConfigureAwait(false);
        return Deserialize<List<ElementInfo>>(completion) ?? [];
    }

    public async Task<ElementInfo?> GetElementAsync(string id)
    {
        var completion = await SendAsync(
            AppleTestAgentOperations.Element,
            Arguments(("elementId", id))).ConfigureAwait(false);
        return Deserialize<ElementInfo>(completion);
    }

    public async Task<bool> TapAsync(string elementId)
        => (await SendAsync(AppleTestAgentOperations.Tap, Arguments(("elementId", elementId))).ConfigureAwait(false)).Ok;

    public async Task<bool> FillAsync(string elementId, string text)
        => (await SendAsync(AppleTestAgentOperations.Fill, Arguments(("elementId", elementId), ("text", text))).ConfigureAwait(false)).Ok;

    public async Task<bool> SetPropertyAsync(string elementId, string propertyName, string value)
        => (await SendAsync(
            AppleTestAgentOperations.SetProperty,
            Arguments(("elementId", elementId), ("propertyName", propertyName), ("value", value))).ConfigureAwait(false)).Ok;

    public async Task<bool> ScrollAsync(
        string? elementId = null,
        double deltaX = 0,
        double deltaY = 0,
        bool animated = true,
        int? itemIndex = null,
        string? scrollToPosition = null)
        => (await SendAsync(
            AppleTestAgentOperations.Scroll,
            Arguments(
                ("elementId", elementId),
                ("deltaX", deltaX.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("deltaY", deltaY.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("animated", animated ? "true" : "false"),
                ("itemIndex", itemIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("scrollToPosition", scrollToPosition))).ConfigureAwait(false)).Ok;

    public async Task<bool> NavigateAsync(string route)
        => (await SendAsync(AppleTestAgentOperations.Navigate, Arguments(("route", route))).ConfigureAwait(false)).Ok;

    public async Task<bool> BackAsync()
        => (await SendAsync(AppleTestAgentOperations.Back).ConfigureAwait(false)).Ok;

    public async Task<ThemeResult> SetThemeAsync(DevFlowTheme theme)
    {
        var completion = await SendAsync(
            AppleTestAgentOperations.SetTheme,
            Arguments(("theme", theme.ToProtocolString()))).ConfigureAwait(false);
        return Deserialize<ThemeResult>(completion) ??
            new ThemeResult
            {
                Theme = theme,
                RequestedTheme = theme,
                Success = completion.Ok,
                Source = "apple-test-agent",
            };
    }

    public async Task<string?> GetPropertyAsync(string elementId, string propertyName)
    {
        var completion = await SendAsync(
            AppleTestAgentOperations.Property,
            Arguments(("elementId", elementId), ("propertyName", propertyName))).ConfigureAwait(false);
        var bytes = ResultBytes(completion);
        if (bytes is null)
            return null;

        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.TryGetProperty("value", out var value)
            ? value.GetString()
            : null;
    }

    public async Task<AgentStatus?> GetStatusAsync()
    {
        var completion = await SendAsync(AppleTestAgentOperations.Status).ConfigureAwait(false);
        var bytes = ResultBytes(completion);
        LastCheckpointFacts = AppleTestAgentCheckpointFacts.TryParse(bytes);
        return bytes is null ? null : JsonSerializer.Deserialize<AgentStatus>(bytes, JsonOptions);
    }

    public async Task<List<ElementInfo>> GetTreeAsync(int maxDepth = 0)
    {
        var completion = await SendAsync(
            AppleTestAgentOperations.Tree,
            Arguments(("maxDepth", maxDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ConfigureAwait(false);
        return Deserialize<List<ElementInfo>>(completion) ?? [];
    }

    private async Task<AppleTestAgentOperationCompletion> SendAsync(
        string operation,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        AppleTestAgentOperationCompletion completion;
        try
        {
            completion = await _transport.SendAsync(operation, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var commandId = _transport.LastReceipt?.CommandId;
            if (!string.IsNullOrWhiteSpace(commandId))
                await _transport.CancelAsync(commandId, "host-cancellation", CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        LastWorkflowCommandReceipt = ToWorkflowReceipt(completion.Receipt);
        if (completion.Ok)
            return completion;

        var reason = completion.CompletionCertainty == "unknown"
            ? "workflow-unknown-completion"
            : completion.Error?.Code ?? "apple-agent-operation";
        throw new WorkflowCommandException(reason, completion.Error?.Message, LastWorkflowCommandReceipt);
    }

    private static Dictionary<string, string>? Arguments(params (string Key, string? Value)[] values)
    {
        var arguments = values
            .Where(static value => !string.IsNullOrWhiteSpace(value.Value))
            .ToDictionary(static value => value.Key, static value => value.Value!, StringComparer.Ordinal);
        return arguments.Count == 0 ? null : arguments;
    }

    private static T? Deserialize<T>(AppleTestAgentOperationCompletion completion)
    {
        var bytes = ResultBytes(completion);
        return bytes is null
            ? default
            : JsonSerializer.Deserialize<T>(bytes, JsonOptions);
    }

    private static byte[]? ResultBytes(AppleTestAgentOperationCompletion completion)
    {
        if (string.IsNullOrWhiteSpace(completion.ResultBase64))
            return null;

        try
        {
            return Convert.FromBase64String(completion.ResultBase64);
        }
        catch (FormatException ex)
        {
            throw new WorkflowCommandException(
                "apple-agent-invalid-result",
                "The Apple test agent returned an invalid bounded result.",
                ToWorkflowReceipt(completion.Receipt),
                ex);
        }
    }

    private static WorkflowCommandReceipt ToWorkflowReceipt(AppleTestAgentCommandReceipt receipt)
        => new()
        {
            RunId = receipt.SessionId,
            Sequence = receipt.Sequence,
            CommandId = receipt.CommandId,
            ActionDigest = receipt.ActionDigest,
            AuthorityEpoch = receipt.AuthorityEpoch,
        };
}

/// <summary>Value-free state identities projected by the XCTest status operation for host preflight.</summary>
public sealed class AppleTestAgentCheckpointFacts
{
    public string? Route { get; init; }
    public string? SeedFingerprint { get; init; }
    public string? BackendStateFingerprint { get; init; }
    public string? StateFingerprint { get; init; }
    public string? ProcessInstanceId { get; init; }

    internal static AppleTestAgentCheckpointFacts? TryParse(byte[]? bytes)
    {
        if (bytes is null)
            return null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var state = root.TryGetProperty("testState", out var stateProperty) &&
                stateProperty.ValueKind == JsonValueKind.Object
                ? stateProperty
                : default;
            return new AppleTestAgentCheckpointFacts
            {
                Route = String(root, "route"),
                SeedFingerprint = String(state, "seedFingerprint"),
                BackendStateFingerprint = String(state, "backendStateFingerprint"),
                StateFingerprint = String(state, "stateFingerprint"),
                ProcessInstanceId = String(state, "processInstanceId"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
