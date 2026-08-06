using System.Text.Json;
using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed class WorkflowPlanStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "workflow-plan-store-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveLoad_RoundTripsPlanSidecarAndUnknownFields()
    {
        var store = CreateStore(out var flowName, out var flow);
        var request = new WorkflowPlanSaveRequest
        {
            FlowName = flowName,
            PlanJson = PlanJson(flowName, MauiFlowRunReportSerializer.ComputeFlowDigest(flow), extension: true),
            ExpectedFlowDigest = MauiFlowRunReportSerializer.ComputeFlowDigest(flow),
        };

        var saved = store.Save(request);
        var loaded = store.Load(flowName);

        Assert.True(saved.Ok, saved.Error);
        Assert.NotNull(saved.Snapshot?.Plan);
        Assert.Equal(1, saved.Snapshot!.Plan!.Revision);
        Assert.True(loaded.Ok, loaded.Error);
        Assert.Contains("\"x-plan-extension\"", loaded.Snapshot!.PlanJson);
        Assert.True(loaded.Snapshot.Plan!.ExtensionData!.ContainsKey("x-plan-extension"));
        Assert.True(File.Exists(Path.Combine(_root, "maui-tests", "login.maui-plan.json")));
    }

    [Fact]
    public void Load_ChangedFlow_PreservesStalePlanDigestWarning()
    {
        var store = CreateStore(out var flowName, out var flow);
        var originalDigest = MauiFlowRunReportSerializer.ComputeFlowDigest(flow);
        var saved = store.Save(new WorkflowPlanSaveRequest
        {
            FlowName = flowName,
            PlanJson = PlanJson(flowName, originalDigest),
            ExpectedFlowDigest = originalDigest,
        });
        Assert.True(saved.Ok, saved.Error);

        flow.Steps.Add(new FlowStep
        {
            Seq = 2,
            Action = FlowActions.Tap,
            Target = new FlowSelector { AutomationId = "another-button" },
        });
        File.WriteAllText(Path.Combine(_root, "maui-tests", flowName), FlowMarkdown.Serialize(flow));

        var loaded = store.Load(flowName);

        Assert.True(loaded.Ok, loaded.Error);
        Assert.Contains(loaded.Warnings, warning => warning.Contains("older flow digest", StringComparison.Ordinal));
    }

    [Fact]
    public void Save_ChangedRevision_ReturnsExplicitStaleConflict()
    {
        var store = CreateStore(out var flowName, out var flow);
        var first = store.Save(new WorkflowPlanSaveRequest
        {
            FlowName = flowName,
            PlanJson = PlanJson(flowName, MauiFlowRunReportSerializer.ComputeFlowDigest(flow)),
            ExpectedFlowDigest = MauiFlowRunReportSerializer.ComputeFlowDigest(flow),
        });
        Assert.True(first.Ok, first.Error);

        var stale = store.Save(new WorkflowPlanSaveRequest
        {
            FlowName = flowName,
            PlanJson = PlanJson(flowName, MauiFlowRunReportSerializer.ComputeFlowDigest(flow)),
            ExpectedPlanRevision = 0,
            ExpectedPlanDigest = "old",
            ExpectedFlowDigest = first.Snapshot!.FlowDigest,
        });

        Assert.False(stale.Ok);
        Assert.True(stale.Stale);
        Assert.True(stale.RequiresOverwriteConfirmation);
        Assert.Equal("stale", stale.Code);
        Assert.Equal(1, store.Load(flowName).Snapshot!.Plan!.Revision);
    }

    [Fact]
    public void Load_TraversalName_IsRejectedBeforeFilesystemAccess()
    {
        var store = CreateStore(out _, out _);

        var result = store.Load("../outside.md");

        Assert.False(result.Ok);
        Assert.Equal("flow-name-invalid", result.Code);
    }

    [Fact]
    public void Commit_InvalidPlan_DoesNotPartiallyReplaceFlow()
    {
        var store = CreateStore(out var flowName, out var original);
        var before = File.ReadAllText(Path.Combine(_root, "maui-tests", flowName));
        var changed = new MauiFlow
        {
            Name = original.Name,
            Steps =
            [
                new FlowStep
                {
                    Seq = 1,
                    Action = FlowActions.Tap,
                    Target = new FlowSelector { AutomationId = "new-save" },
                },
            ],
        };

        var result = store.Commit(new WorkflowBundleCommitRequest
        {
            FlowName = flowName,
            Markdown = FlowMarkdown.Serialize(changed),
            PlanJson = "{}",
            ExpectedFlowDigest = MauiFlowRunReportSerializer.ComputeFlowDigest(original),
        });

        Assert.False(result.Ok);
        Assert.Equal("bundle-invalid", result.Code);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "maui-tests", flowName)));
        Assert.False(File.Exists(Path.Combine(_root, "maui-tests", "login.maui-plan.json")));
    }

    [Fact]
    public void Commit_WriteFailure_RestoresOriginalFlow()
    {
        var store = CreateStore(out var flowName, out var original);
        var flowPath = Path.Combine(_root, "maui-tests", flowName);
        var before = File.ReadAllText(flowPath);
        Directory.CreateDirectory(Path.Combine(_root, "maui-tests", "login.maui-plan.json"));
        var changed = new MauiFlow
        {
            Name = original.Name,
            Steps =
            [
                new FlowStep
                {
                    Seq = 1,
                    Action = FlowActions.Tap,
                    Target = new FlowSelector { AutomationId = "changed" },
                },
            ],
        };

        var result = store.Commit(new WorkflowBundleCommitRequest
        {
            FlowName = flowName,
            Markdown = FlowMarkdown.Serialize(changed),
            PlanJson = PlanJson(flowName, MauiFlowRunReportSerializer.ComputeFlowDigest(changed)),
            ExpectedFlowDigest = MauiFlowRunReportSerializer.ComputeFlowDigest(original),
        });

        Assert.False(result.Ok);
        Assert.Equal("write-failed", result.Code);
        Assert.Equal(before, File.ReadAllText(flowPath));
    }

    [Fact]
    public void Commit_NewFlow_WritesCanonicalFlowAndBoundPlan()
    {
        Directory.CreateDirectory(_root);
        var store = new WorkflowPlanStore(_root);
        var flow = ValidFlow();
        var flowName = "new-login.md";

        var result = store.Commit(new WorkflowBundleCommitRequest
        {
            FlowName = flowName,
            Markdown = FlowMarkdown.Serialize(flow),
            PlanJson = PlanJson(flowName, MauiFlowRunReportSerializer.ComputeFlowDigest(flow)),
        });

        Assert.True(result.Ok, result.Error);
        Assert.True(File.Exists(Path.Combine(_root, "maui-tests", flowName)));
        Assert.True(File.Exists(Path.Combine(_root, "maui-tests", "new-login.maui-plan.json")));
        Assert.Equal(1, result.Snapshot!.Plan!.Revision);
        Assert.Equal(result.Snapshot.FlowDigest, result.Snapshot.Plan.Flow!.Digest);
    }

    [Fact]
    public void Diff_SameDraft_ProducesStableOutput()
    {
        var store = CreateStore(out var flowName, out var original);
        var changed = new MauiFlow
        {
            Name = original.Name,
            Steps =
            [
                new FlowStep
                {
                    Seq = 1,
                    Action = FlowActions.Tap,
                    Target = new FlowSelector { AutomationId = "changed" },
                },
            ],
        };
        var plan = PlanJson(flowName, MauiFlowRunReportSerializer.ComputeFlowDigest(changed));

        var first = store.Diff(flowName, FlowMarkdown.Serialize(changed), plan);
        var second = store.Diff(flowName, FlowMarkdown.Serialize(changed), plan);

        Assert.True(first.Ok, first.Error);
        Assert.Equal(first.Diff, second.Diff);
        Assert.Contains("changed", first.Diff);
    }

    [Fact]
    public void Load_ReparsePoint_IsRejected()
    {
        var store = CreateStore(out _, out _);
        var target = Path.Combine(_root, "target.md");
        File.WriteAllText(target, FlowMarkdown.Serialize(ValidFlow()));
        var link = Path.Combine(_root, "maui-tests", "linked.md");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var result = store.Load("linked.md");

        Assert.False(result.Ok);
        Assert.Contains(result.Code, new[] { "flow-not-found", "flow-invalid", "workspace-unsafe" });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private WorkflowPlanStore CreateStore(out string flowName, out MauiFlow flow)
    {
        Directory.CreateDirectory(Path.Combine(_root, "maui-tests"));
        flowName = "login.md";
        flow = ValidFlow();
        File.WriteAllText(Path.Combine(_root, "maui-tests", flowName), FlowMarkdown.Serialize(flow));
        return new WorkflowPlanStore(_root);
    }

    private static MauiFlow ValidFlow() => new()
    {
        Name = "login",
        Steps =
        [
            new FlowStep
            {
                Seq = 1,
                Action = FlowActions.Tap,
                Target = new FlowSelector { AutomationId = "sign-in" },
            },
        ],
    };

    private static string PlanJson(string flowName, string digest, bool extension = false)
    {
        var plan = new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["planId"] = "plan-login",
            ["revision"] = 1,
            ["flow"] = new { path = flowName, digest },
            ["goal"] = "Sign in",
            ["scenarios"] = new[] { new { scenarioId = "login", description = "Sign in", acceptanceCriterionIds = new[] { "session" } } },
            ["assumptions"] = Array.Empty<string>(),
            ["preconditions"] = Array.Empty<object>(),
            ["reset"] = new { required = true, strategy = "fixture" },
            ["acceptanceCriteria"] = new[] { new { criterionId = "session", description = "Session exists", required = true } },
            ["requirements"] = new { requiredCapabilities = Array.Empty<object>(), requiredSemantics = Array.Empty<object>() },
            ["sideEffectPolicy"] = "none",
            ["provenance"] = new { actorKind = "human", channel = "inspector" },
        };
        if (extension)
            plan["x-plan-extension"] = new { retained = true };
        return JsonSerializer.Serialize(plan);
    }
}
