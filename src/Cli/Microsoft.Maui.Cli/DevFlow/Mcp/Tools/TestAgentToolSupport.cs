using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;
using ModelContextProtocol;

namespace Microsoft.Maui.Cli.DevFlow.Mcp.Tools;

internal static class TestAgentToolSupport
{
    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string Success(string? requestId, object? data = null, IEnumerable<MauiTestAgentUntrustedInput>? untrusted = null)
    {
        var response = new JsonObject
        {
            ["ok"] = true,
            ["requestId"] = requestId,
        };
        if (data is not null)
            response["data"] = JsonSerializer.SerializeToNode(data, OutputJsonOptions);
        var labels = untrusted?.ToArray() ?? [];
        if (labels.Length > 0)
            response["untrustedInputs"] = JsonSerializer.SerializeToNode(labels, OutputJsonOptions);
        return CliJson.SerializeUntyped(response, indented: false);
    }

    internal static string Failure(string? requestId, MauiTestAgentError error)
    {
        var response = new JsonObject
        {
            ["ok"] = false,
            ["requestId"] = requestId,
            ["error"] = JsonSerializer.SerializeToNode(error, OutputJsonOptions),
        };
        return CliJson.SerializeUntyped(response, indented: false);
    }

    internal static string BrokerFailure<T>(string? requestId, TestAgentBrokerResponse<T> response)
        where T : class
    {
        var error = response.Value switch
        {
            MauiTestAgentSessionResult value => value.Error,
            MauiTestAgentGrantIssueResult value => value.Error,
            MauiTestAgentApprovalResult value => value.Error,
            MauiTestAgentMutationAuthorizationResult value => value.Error,
            MauiTestAgentToolResult value => value.Error,
            MauiTestAgentPatchResult value => value.Error,
            MauiTestAgentAuditResult value => value.Error,
            MauiTestAgentRunBindingResult value => value.Error,
            _ => null,
        };
        return Failure(
            requestId,
            error ?? Error(
                MauiTestAgentErrorCodes.TargetUnavailable,
                response.TransportError is null
                    ? MauiTestAgentErrorCategories.Transport
                    : MauiTestAgentErrorCategories.Target,
                response.TransportError ?? "The broker rejected the restricted test-agent request.",
                retryable: response.StatusCode == 0));
    }

    internal static MauiTestAgentError Error(string code, string category, string message, bool retryable)
        => new()
        {
            Code = code,
            Category = category,
            Message = message,
            Retryable = retryable,
        };

    internal static async Task<TestAgentTargetResolution> ResolveTargetAsync(
        McpAgentSession session,
        MauiTestAgentTarget? target)
    {
        try
        {
            var registration = await session.ResolveTestAgentAsync(target).ConfigureAwait(false);
            using var agent = await session.GetTestAgentClientAsync(target).ConfigureAwait(false);
            var status = await agent.GetStatusAsync().ConfigureAwait(false);
            if (status is null)
            {
                return TestAgentTargetResolution.Failure(Error(
                    MauiTestAgentErrorCodes.TargetUnavailable,
                    MauiTestAgentErrorCategories.Target,
                    "The explicitly selected target did not return status.",
                    retryable: true));
            }

            return TestAgentTargetResolution.Success(
                registration,
                new MauiTestAgentTargetState
                {
                    AgentId = registration.Id,
                    AgentInstanceId = registration.InstanceId,
                    AppBuildFingerprint = BuildAppFingerprint(status),
                    // Seed/backend fingerprints require a trusted live attestation. The MCP caller
                    // cannot promote echoed target fields into broker-observed state.
                    SeedFingerprint = null,
                    BackendStateFingerprint = null,
                    Route = status.Route,
                    Window = status.Window,
                    ObservedAt = DateTimeOffset.UtcNow,
                });
        }
        catch (McpException)
        {
            return TestAgentTargetResolution.Failure(Error(
                MauiTestAgentErrorCodes.ExplicitTargetRequired,
                MauiTestAgentErrorCategories.Target,
                "The restricted test-agent profile requires a currently connected explicit agentId and agentInstanceId.",
                retryable: false));
        }
        catch (HttpRequestException)
        {
            return TestAgentTargetResolution.Failure(Error(
                MauiTestAgentErrorCodes.TargetUnavailable,
                MauiTestAgentErrorCategories.Target,
                "The explicitly selected target is unavailable.",
                retryable: true));
        }
    }

    internal static string? BuildAppFingerprint(AgentStatus status)
    {
        var version = NormalizeFingerprintPart(status.App?.Version);
        var build = NormalizeFingerprintPart(status.App?.Build);
        return version is null && build is null
            ? null
            : $"{version ?? "unknown"}:{build ?? "unknown"}";
    }

    private static string? NormalizeFingerprintPart(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ||
               string.Equals(normalized, "unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    internal static async Task<TestAgentBrokerResponse<MauiTestAgentMutationAuthorizationResult>> AuthorizeAsync(
        McpAgentSession session,
        MauiTestAgentRequestEnvelope? envelope,
        string action,
        FlowSelector? selector = null,
        string? route = null,
        string? sideEffectClass = null,
        string? value = null,
        CancellationToken cancellationToken = default)
    {
        if (envelope is null)
        {
            return new TestAgentBrokerResponse<MauiTestAgentMutationAuthorizationResult>(
                400,
                new MauiTestAgentMutationAuthorizationResult
                {
                    Error = Error(
                        MauiTestAgentErrorCodes.InvalidRequest,
                        MauiTestAgentErrorCategories.Validation,
                        "A protocol envelope is required.",
                        retryable: false),
                },
                null);
        }

        var target = await ResolveTargetAsync(session, envelope.Target).ConfigureAwait(false);
        if (target.Error is not null)
        {
            return new TestAgentBrokerResponse<MauiTestAgentMutationAuthorizationResult>(
                409,
                new MauiTestAgentMutationAuthorizationResult { Error = target.Error },
                null);
        }

        var brokerPort = await session.GetBrokerPortAsync().ConfigureAwait(false);
        return await TestAgentBrokerClient.AuthorizeAsync(
            brokerPort,
            new MauiTestAgentMutationAuthorizationRequest
            {
                Envelope = envelope,
                Action = action,
                Selector = selector,
                Route = route,
                SideEffectClass = sideEffectClass,
                ValueLength = value is null ? null : System.Text.Encoding.UTF8.GetByteCount(value),
                ValueDigest = value is null ? null : Digest(value),
                CurrentTargetState = target.State,
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<TestAgentBrokerResponse<MauiTestAgentSessionResult>> SessionAsync(
        McpAgentSession session,
        MauiTestAgentRequestEnvelope? envelope,
        CancellationToken cancellationToken = default)
    {
        if (envelope?.Correlation?.AuthoringSessionId is not { Length: > 0 })
        {
            return new TestAgentBrokerResponse<MauiTestAgentSessionResult>(
                400,
                new MauiTestAgentSessionResult
                {
                    Error = Error(
                        MauiTestAgentErrorCodes.SessionNotFound,
                        MauiTestAgentErrorCategories.Validation,
                        "The envelope must name an authoringSessionId.",
                        retryable: false),
                },
                null);
        }

        var brokerPort = await session.GetBrokerPortAsync().ConfigureAwait(false);
        return await TestAgentBrokerClient.StatusAsync(
            brokerPort,
            new MauiTestAgentSessionAccessRequest
            {
                SessionId = envelope.Correlation.AuthoringSessionId,
                ReadCapabilityId = envelope.ReadCapabilityId,
                Envelope = envelope,
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal static MauiTestAgentUntrustedInput Untrusted(string kind)
        => new()
        {
            Kind = kind,
            PolicyInfluencing = false,
        };

    internal static string Digest(string value)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}

internal sealed class TestAgentTargetResolution
{
    public AgentRegistration? Registration { get; private init; }
    public MauiTestAgentTargetState? State { get; private init; }
    public MauiTestAgentError? Error { get; private init; }

    public static TestAgentTargetResolution Success(AgentRegistration registration, MauiTestAgentTargetState state)
        => new() { Registration = registration, State = state };

    public static TestAgentTargetResolution Failure(MauiTestAgentError error)
        => new() { Error = error };
}
