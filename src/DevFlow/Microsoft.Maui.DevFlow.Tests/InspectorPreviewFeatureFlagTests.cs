using Microsoft.Maui.Cli.DevFlow;
using Microsoft.Maui.Cli.DevFlow.Inspector;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed class InspectorPreviewFeatureFlagTests
{
    [Fact]
    public void DisabledAgentAuthoring_AllowsReviewAndRejectButNotApproval()
    {
        var flags = MauiPreviewFeatureFlags.CreateDefault();

        Assert.True(InspectorServer.IsPreviewRouteEnabled(flags, "/api/workbench/agent-requests"));
        Assert.False(InspectorServer.IsPreviewRouteEnabled(
            flags,
            "/api/workbench/agent-requests/request-1/approve"));
        Assert.True(InspectorServer.IsPreviewRouteEnabled(
            flags,
            "/api/workbench/agent-requests/request-1/reject"));
        Assert.True(InspectorServer.IsPreviewRouteEnabled(
            flags,
            "/api/workbench/approval-confirmations/issue"));
        Assert.False(InspectorServer.IsPreviewRouteEnabled(flags, "/api/workbench/run/start"));
        Assert.False(flags.WorkbenchEnabled);
        Assert.False(flags.RepairProposalsEnabled);
        Assert.False(flags.SourceProposalsEnabled);
        Assert.False(flags.TraceImportExportEnabled);
    }

    [Fact]
    public void EnabledAgentAuthoring_AllowsApprovalRoute()
    {
        var flags = new MauiPreviewFeatureFlags { AgentAuthoringEnabled = true };

        Assert.True(InspectorServer.IsPreviewRouteEnabled(
            flags,
            "/api/workbench/agent-requests/request-1/approve"));
    }

    [Fact]
    public void BrokerTestAgentPolicy_Disabled_AllowsOnlySafeCleanupAndReviewReads()
    {
        var flags = MauiPreviewFeatureFlags.CreateDefault();

        Assert.True(DevFlowPreviewPolicy.IsBrokerTestAgentRouteEnabled(
            flags,
            "/api/test-agent/sessions/status"));
        Assert.True(DevFlowPreviewPolicy.IsBrokerTestAgentRouteEnabled(
            flags,
            "/api/test-agent/sessions/abandon"));
        Assert.True(DevFlowPreviewPolicy.IsBrokerTestAgentRouteEnabled(
            flags,
            "/api/test-agent/mutations/complete"));
        Assert.False(DevFlowPreviewPolicy.IsBrokerTestAgentRouteEnabled(
            flags,
            "/api/test-agent/sessions/begin"));
        Assert.False(DevFlowPreviewPolicy.IsBrokerTestAgentRouteEnabled(
            flags,
            "/api/test-agent/approvals/request"));
        Assert.False(DevFlowPreviewPolicy.IsBrokerTestAgentRouteEnabled(
            flags,
            "/api/test-agent/grants/issue"));
        Assert.False(DevFlowPreviewPolicy.IsBrokerTestAgentRouteEnabled(
            flags,
            "/api/test-agent/draft/commit"));
    }

    [Fact]
    public void InspectorDefaultPreviewFlags_UseEnvironmentAndRemainOffWhenUnset()
    {
        var defaults = InspectorServer.ResolvePreviewFlags(
            supplied: null,
            readEnvironment: _ => null);

        Assert.False(defaults.WorkbenchEnabled);
        Assert.False(defaults.AgentAuthoringEnabled);
        Assert.False(defaults.RepairProposalsEnabled);
        Assert.False(defaults.SourceProposalsEnabled);
        Assert.False(defaults.TraceImportExportEnabled);

        var supplied = PreviewTestFeatures.AllEnabled();
        Assert.Same(supplied, InspectorServer.ResolvePreviewFlags(supplied));
    }

    [Fact]
    public void AdvancedRoutes_RequireTheirSpecificCapability()
    {
        var flags = new MauiPreviewFeatureFlags
        {
            WorkbenchEnabled = true,
            RepairProposalsEnabled = false,
            SourceProposalsEnabled = false,
            TraceImportExportEnabled = false,
        };

        Assert.True(InspectorServer.IsPreviewRouteEnabled(flags, "/api/workbench/run/preflight"));
        Assert.False(InspectorServer.IsPreviewRouteEnabled(flags, "/api/workbench/repair/propose"));
        Assert.False(InspectorServer.IsPreviewRouteEnabled(flags, "/api/workbench/source/propose"));
        Assert.False(InspectorServer.IsPreviewRouteEnabled(flags, "/api/workbench/artifacts/import"));
    }

    [Fact]
    public void KillSwitch_OverridesEnabledCapability()
    {
        var flags = new MauiPreviewFeatureFlags
        {
            WorkbenchEnabled = true,
            RepairProposalsEnabled = true,
            KillSwitches = ["repair-proposals"],
        };

        Assert.False(InspectorServer.IsPreviewRouteEnabled(flags, "/api/workbench/repair/classify"));
    }
}
