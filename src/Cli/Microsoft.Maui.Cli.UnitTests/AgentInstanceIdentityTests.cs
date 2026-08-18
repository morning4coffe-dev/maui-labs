using Microsoft.Maui.Cli.DevFlow.Broker;
using Xunit;

namespace Microsoft.Maui.Cli.UnitTests;

/// <summary>
/// `agentInstanceId` is what authoring sessions, approvals, grants, run bindings, and checkpoints
/// bind to when they mean "this exact app process". The broker used to mint a fresh random value on
/// every WebSocket registration, so restarting the broker — or any transient reconnect — made a
/// still-running app look like a different process and invalidated everything bound to it. Observed
/// directly: the same app process (pid 13173, unchanged app session id) was assigned
/// 1d063cf5805c248f590e71884164ca3f and then ad5f729f347d6078e75976546d29eb5d across one broker
/// restart.
/// </summary>
public class AgentInstanceIdentityTests
{
    private const string Package = "com.companyname.mauitodo";
    private const string Tfm = "net10.0-android";
    private const string AppSession = "flow26727fd0de7343635b1a15d3ee97bf0d";

    [Fact]
    public void InstanceId_IsStableAcrossReconnectsOfTheSameProcess()
    {
        var first = AgentRegistration.ComputeInstanceId(Package, Tfm, AppSession, 13173);
        var second = AgentRegistration.ComputeInstanceId(Package, Tfm, AppSession, 13173);

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void InstanceId_DiffersForANewProcessOfTheSameApp()
    {
        // A relaunched app reports a new process id, and usually a new session id too. Either alone
        // must be enough to produce a new identity, or a stale approval could be spent on a process
        // that never received the human's review.
        var original = AgentRegistration.ComputeInstanceId(Package, Tfm, AppSession, 13173);

        Assert.NotEqual(original, AgentRegistration.ComputeInstanceId(Package, Tfm, AppSession, 13174));
        Assert.NotEqual(original, AgentRegistration.ComputeInstanceId(Package, Tfm, "flow-other", 13173));
    }

    [Fact]
    public void InstanceId_DiffersAcrossAppsAndTargetFrameworks()
    {
        var original = AgentRegistration.ComputeInstanceId(Package, Tfm, AppSession, 13173);

        Assert.NotEqual(original, AgentRegistration.ComputeInstanceId("com.other.app", Tfm, AppSession, 13173));
        Assert.NotEqual(original, AgentRegistration.ComputeInstanceId(Package, "net10.0-windows", AppSession, 13173));
    }

    [Fact]
    public void InstanceId_IsAbsentWhenTheRegistrationCannotProveWhichProcessItIs()
    {
        // Without a process id there is no evidence of continuity, so the broker must keep minting
        // an unguessable per-connection value instead of inventing a stable one.
        Assert.Null(AgentRegistration.ComputeInstanceId(Package, Tfm, AppSession, null));
        Assert.Null(AgentRegistration.ComputeInstanceId(Package, Tfm, AppSession, 0));
    }

    [Fact]
    public void InstanceId_IsNeverEqualToTheAgentIdComputedFromTheSameFacts()
    {
        // The two identities travel together in every envelope. If one could equal the other, a
        // caller could present the wrong one and still satisfy an exact-match check.
        var agentId = AgentRegistration.ComputeId(Package, Tfm, AppSession, 13173);
        var instanceId = AgentRegistration.ComputeInstanceId(Package, Tfm, AppSession, 13173);

        Assert.NotEqual(agentId, instanceId);
        Assert.Equal(32, instanceId!.Length);
        Assert.True(instanceId.All(Uri.IsHexDigit));
    }
}
