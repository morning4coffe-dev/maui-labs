using System.ComponentModel;
using System.Reflection;
using Microsoft.Maui.Cli.DevFlow;
using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.Cli.DevFlow.Mcp;
using Microsoft.Maui.Cli.DevFlow.Mcp.Tools;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;
using ModelContextProtocol.Server;
using Xunit;

namespace Microsoft.Maui.Cli.UnitTests;

public class TestAgentSessionServiceTests
{
    [Fact]
    public void ToolInventory_TestAgentProfileHasOnlyRestrictedTools()
    {
        var full = McpServerHost.GetToolInventory(McpServerProfile.Full);
        var restricted = McpServerHost.GetToolInventory(McpServerProfile.TestAgent);

        Assert.Contains("maui_secure_storage_set", full);
        Assert.Contains("maui_cdp_evaluate", full);
        Assert.Contains("maui_test_action", restricted);
        Assert.Contains("maui_test_agents", restricted);
        Assert.Contains("maui_test_status", restricted);
        Assert.DoesNotContain("maui_secure_storage_set", restricted);
        Assert.DoesNotContain("maui_preferences_set", restricted);
        Assert.DoesNotContain("maui_files_download", restricted);
        Assert.DoesNotContain("maui_network_detail", restricted);
        Assert.DoesNotContain("maui_cdp_evaluate", restricted);
        Assert.DoesNotContain("maui_extension_call", restricted);
        Assert.DoesNotContain("maui_invoke_action", restricted);
        Assert.DoesNotContain("maui_set_property", restricted);
        Assert.DoesNotContain("maui_evidence_capture", restricted);
    }

    [Fact]
    public void RestrictedTools_EveryParameterHasDescription()
    {
        var types = new[]
        {
            typeof(TestAgentDiscoveryTools),
            typeof(TestAgentCapabilitiesTool),
            typeof(TestAgentAuthoringTool),
            typeof(TestAgentActionTool),
            typeof(TestAgentAssertionTool),
            typeof(TestAgentValidationTool),
            typeof(TestAgentRunTool),
            typeof(TestAgentTraceTool),
            typeof(TestAgentFailureTool),
            typeof(TestAgentPatchTool),
            typeof(TestAgentImprovementsTool),
        };

        foreach (var method in types.SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                     .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null))
        {
            foreach (var parameter in method.GetParameters())
            {
                Assert.NotNull(
                    parameter.GetCustomAttribute<DescriptionAttribute>());
            }
        }
    }

    [Fact]
    public void ToolSupport_SerializesProtocolResultsWithCamelCaseNames()
    {
        var json = TestAgentToolSupport.Success("req-status", new MauiTestAgentSessionResult
        {
            Ok = true,
            Snapshot = new MauiTestAgentAuthoringSnapshot
            {
                SessionId = "author-1",
                ApprovalRequests =
                [
                    new MauiTestAgentApprovalRecord
                    {
                        ApprovalRequestId = "approval-1",
                        State = MauiTestAgentApprovalStates.Approved,
                        GrantId = "grant-opaque",
                    },
                ],
            },
        });

        Assert.Contains("\"snapshot\"", json, StringComparison.Ordinal);
        Assert.Contains("\"approvalRequests\"", json, StringComparison.Ordinal);
        Assert.Contains("\"grantId\":\"grant-opaque\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Snapshot\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ApprovalRequests\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolSupport_BuildFingerprintMatchesRunnerCheckpointFormat()
    {
        Assert.Equal("1.2.3:42", TestAgentToolSupport.BuildAppFingerprint(new AgentStatus
        {
            App = new AppDescriptor { Version = "1.2.3", Build = "42" },
        }));
        Assert.Equal("unknown:42", TestAgentToolSupport.BuildAppFingerprint(new AgentStatus
        {
            App = new AppDescriptor { Build = "42" },
        }));
        Assert.Null(TestAgentToolSupport.BuildAppFingerprint(new AgentStatus
        {
            App = new AppDescriptor { Version = "unknown", Build = "unknown" },
        }));
    }

    [Fact]
    public void WorkflowRunRequests_AreCoveredByAotJsonMetadata()
    {
        var startJson = TestAgentBrokerClient.SerializeWorkflowRunRequest(
            new WorkflowRunStartRequest
            {
                AgentId = "agent-a",
                AgentInstanceId = "instance-a",
                IdempotencyKey = "run-a",
                Flow = new MauiFlow { Name = "Run A" },
                Plan = new MauiTestPlan { PlanId = "plan-a" },
            },
            DevFlowCliJsonContext.Default.WorkflowRunStartRequest);
        var accessJson = TestAgentBrokerClient.SerializeWorkflowRunRequest(
            new WorkflowRunAccessRequest { CapabilityToken = "run-token" },
            DevFlowCliJsonContext.Default.WorkflowRunAccessRequest);

        Assert.Contains("\"agentId\":\"agent-a\"", startJson, StringComparison.Ordinal);
        Assert.Contains("\"flow\":{\"schema\":2,\"name\":\"Run A\"", startJson, StringComparison.Ordinal);
        Assert.Contains("\"capabilityToken\":\"run-token\"", accessJson, StringComparison.Ordinal);
    }

    [Fact]
    public void RunContext_NonReplayablePlanUsesApprovedOneShotAuthorization()
    {
        var oneShot = TestAgentRunTool.CreateRunContext(new MauiTestPlan
        {
            SideEffectPolicy = MauiFlowSideEffectPolicies.NonReplayable,
        },
        new MauiTestAgentTargetState
        {
            AgentInstanceId = "instance-a",
            AppBuildFingerprint = "build-a",
            Route = "//native",
            Window = "window-0",
        },
        new MauiTestAgentTargetState
        {
            AgentInstanceId = "instance-a",
            AppBuildFingerprint = "build-a",
            Route = "//native",
            Window = "window-0",
        });
        var resettable = TestAgentRunTool.CreateRunContext(new MauiTestPlan
        {
            SideEffectPolicy = MauiFlowSideEffectPolicies.TestTenantResettable,
        });

        Assert.Equal(MauiFlowReplayIntents.OrdinaryReplay, oneShot.Intent);
        Assert.True(oneShot.ManualOneShotAuthorization);
        Assert.Equal("build-a", oneShot.Preconditions?.Expected?.AppBuildFingerprint);
        Assert.Equal("build-a", oneShot.Preconditions?.Observed?.AppBuildFingerprint);
        Assert.True(MauiFlowReplaySafetyEvaluator.Evaluate(new MauiFlowRunRequest
        {
            Plan = new MauiTestPlan
            {
                SideEffectPolicy = MauiFlowSideEffectPolicies.NonReplayable,
            },
            Context = oneShot,
        }).OrdinaryReplayAllowed);
        Assert.Null(resettable.ManualOneShotAuthorization);
    }

    [Fact]
    public void AuthorRequest_AcceptsSelectorObjectsInApprovalScope()
    {
        const string json =
            """
            {
              "approvalScope": {
                "allowedActions": ["fill", "assert"],
                "allowedSelectors": [
                  { "automationId": "NewTodoEntry" },
                  { "typeIndex": { "type": "Label", "index": 12 } }
                ],
                "allowedSideEffectClasses": ["authoring"],
                "maxActionCount": 2
              }
            }
            """;

        var request = System.Text.Json.JsonSerializer.Deserialize(
            json,
            MauiTestingJsonContext.Default.MauiTestAgentAuthorRequest);

        Assert.Equal(
            ["automationId:NewTodoEntry", "typeIndex:Label:12"],
            request!.ApprovalScope!.AllowedSelectors);
    }

    [Fact]
    public void RunStartRejection_PreservesBoundedBrokerAdmissionReasons()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """
            {
              "ok": false,
              "error": "Flow replay admission was denied before mutation.",
              "errors": [
                "A non-replayable flow may run only with explicit one-shot human authorization.",
                "A second deterministic reason."
              ]
            }
            """);

        var error = TestAgentRunTool.CreateRunStartRejection(document.RootElement);

        Assert.Equal(MauiTestAgentErrorCodes.InvalidRequest, error.Code);
        Assert.Equal(MauiTestAgentErrorCategories.State, error.Category);
        Assert.Contains("Flow replay admission was denied", error.Message, StringComparison.Ordinal);
        Assert.Contains("explicit one-shot human authorization", error.Message, StringComparison.Ordinal);
        Assert.False(error.Retryable);
    }

    [Fact]
    public void Begin_MissingExplicitInstance_IsRejected()
    {
        var service = new TestAgentSessionService();
        var result = service.Begin(new MauiTestAgentSessionBeginRequest
        {
            Envelope = Envelope(new MauiTestAgentTarget { AgentId = "agent-a" }, "begin-no-instance"),
            TargetState = State(),
        });

        Assert.False(result.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.InvalidRequest, result.Error?.Code);
    }

    [Fact]
    public void Grant_RequiresHumanApprovalAndBindsScopeAndSingleUse()
    {
        var fixture = BeginFixture();
        var denied = fixture.Service.IssueGrant(new MauiTestAgentGrantIssueRequest
        {
            SessionId = fixture.SessionId,
            ReadCapabilityId = fixture.ReadCapability,
            TargetState = fixture.State,
            Correlation = fixture.Correlation(),
            Scope = Scope(MauiTestAgentActions.Tap),
            Approval = new MauiTestAgentHumanApproval
            {
                Approved = true,
                ApprovalChannel = "workbench",
                Actor = new MauiActorProvenance
                {
                    ActorKind = "agent",
                    ActorId = "agent-author",
                    Channel = "mcp",
                    Provider = "host-owned",
                },
            },
        });
        Assert.False(denied.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.HumanApprovalRequired, denied.Error?.Code);

        var grant = fixture.IssueGrant(Scope(MauiTestAgentActions.Tap));
        Assert.True(grant.Ok);
        Assert.NotNull(grant.GrantId);
        Assert.NotEqual(grant.GrantId, grant.GrantDigest);

        var wrongSelector = fixture.Authorize(
            "tap-wrong-selector",
            grant.GrantId!,
            MauiTestAgentActions.Tap,
            selector: new FlowSelector { AutomationId = "other" });
        Assert.False(wrongSelector.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.MutationGrantScopeDenied, wrongSelector.Error?.Code);

        var allowed = fixture.Authorize(
            "tap-allowed",
            grant.GrantId!,
            MauiTestAgentActions.Tap,
            selector: new FlowSelector { AutomationId = "save" });
        Assert.True(allowed.Ok);
        Assert.True(allowed.DispatchAllowed);
        Assert.Equal(0, allowed.RemainingActions);

        var reused = fixture.Authorize(
            "tap-reused",
            grant.GrantId!,
            MauiTestAgentActions.Tap,
            selector: new FlowSelector { AutomationId = "save" });
        Assert.False(reused.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.MutationGrantReused, reused.Error?.Code);
    }

    [Fact]
    public void Grant_RejectsStaleTargetValueLimitAndIdempotencyReuseBeforeDispatch()
    {
        var fixture = BeginFixture();
        var grant = fixture.IssueGrant(new MauiTestAgentMutationScope
        {
            AllowedActions = [MauiTestAgentActions.Fill],
            AllowedSelectors = ["automationId:query"],
            AllowedSideEffectClasses = ["ui"],
            MaxActionCount = 2,
            MaxValueBytes = 2,
        });

        var staleState = new MauiTestAgentTargetState
        {
            AgentId = fixture.State.AgentId,
            AgentInstanceId = fixture.State.AgentInstanceId,
            AppBuildFingerprint = "build-b",
            SeedFingerprint = fixture.State.SeedFingerprint,
        };
        var stale = fixture.Authorize(
            "fill-stale",
            grant.GrantId!,
            MauiTestAgentActions.Fill,
            selector: new FlowSelector { AutomationId = "query" },
            value: "a",
            state: staleState);
        Assert.False(stale.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.TargetStale, stale.Error?.Code);

        var oversized = fixture.Authorize(
            "fill-oversized",
            grant.GrantId!,
            MauiTestAgentActions.Fill,
            selector: new FlowSelector { AutomationId = "query" },
            value: "abc");
        Assert.False(oversized.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.ValueLimitExceeded, oversized.Error?.Code);

        var allowed = fixture.Authorize(
            "fill-once",
            grant.GrantId!,
            MauiTestAgentActions.Fill,
            selector: new FlowSelector { AutomationId = "query" },
            value: "ok");
        Assert.True(allowed.Ok);

        var replay = fixture.Authorize(
            "fill-once",
            grant.GrantId!,
            MauiTestAgentActions.Fill,
            selector: new FlowSelector { AutomationId = "query" },
            value: "ok");
        Assert.False(replay.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.IdempotencyReused, replay.Error?.Code);
        Assert.False(replay.DispatchAllowed);
    }

    [Fact]
    public void Grant_RejectsMultiUseCommitScope()
    {
        var fixture = BeginFixture();
        var grant = fixture.IssueGrant(new MauiTestAgentMutationScope
        {
            AllowedActions = [MauiTestAgentActions.AuthorCommit],
            AllowedSideEffectClasses = ["authoring"],
            MaxActionCount = 2,
            MaxValueBytes = 0,
        });

        Assert.False(grant.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.InvalidRequest, grant.Error?.Code);
    }

    [Theory]
    [InlineData("authoring-commit", "commit", MauiTestAgentApprovalKinds.Commit, MauiTestAgentActions.AuthorCommit)]
    [InlineData("test-run", "start-run", MauiTestAgentApprovalKinds.Run, MauiTestAgentActions.Run)]
    [InlineData("assertions", "assert", MauiTestAgentApprovalKinds.Assertion, MauiTestAgentActions.Assert)]
    public void ApprovalRequest_NormalizesCommonAgentAliases(
        string suppliedKind,
        string suppliedAction,
        string expectedKind,
        string expectedAction)
    {
        var fixture = BeginFixture();
        var submitted = fixture.Service.SubmitApprovalRequest(new MauiTestAgentApprovalSubmitRequest
        {
            Envelope = fixture.Envelope($"alias-{suppliedKind}", grantId: null),
            Kind = suppliedKind,
            Scope = new MauiTestAgentMutationScope
            {
                AllowedActions = [suppliedAction],
                AllowedSideEffectClasses = ["authoring"],
                MaxActionCount = 1,
                MaxValueBytes = 0,
            },
        });

        Assert.True(submitted.Ok, submitted.Error?.Message);
        Assert.Equal(expectedKind, submitted.Request!.Kind);
        Assert.Equal([expectedAction], submitted.Request.RequestedScope!.AllowedActions);
        if (expectedKind == MauiTestAgentApprovalKinds.Run)
            Assert.Equal(["run"], submitted.Request.RequestedScope.AllowedSideEffectClasses);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("execution")]
    [InlineData("app-mutation")]
    [InlineData("workflow-run")]
    public void ApprovalRequest_NormalizesRunSideEffectClass(string? suppliedClass)
    {
        var fixture = BeginFixture();
        var submitted = fixture.Service.SubmitApprovalRequest(new MauiTestAgentApprovalSubmitRequest
        {
            Envelope = fixture.Envelope($"run-class-{suppliedClass ?? "empty"}", grantId: null),
            Kind = MauiTestAgentApprovalKinds.Run,
            Scope = new MauiTestAgentMutationScope
            {
                AllowedActions = [MauiTestAgentActions.Run],
                AllowedSideEffectClasses = suppliedClass is null ? [] : [suppliedClass],
                MaxActionCount = 1,
                MaxValueBytes = 0,
            },
        });

        Assert.True(submitted.Ok, submitted.Error?.Message);
        Assert.Equal(["run"], submitted.Request!.RequestedScope!.AllowedSideEffectClasses);
    }

    [Theory]
    [InlineData(MauiTestAgentApprovalKinds.DraftChange)]
    [InlineData(MauiTestAgentApprovalKinds.Assertion)]
    [InlineData(MauiTestAgentApprovalKinds.Commit)]
    public void ApprovalRequest_NormalizesAuthoringSideEffectClass(string kind)
    {
        var fixture = BeginFixture();
        var action = kind switch
        {
            MauiTestAgentApprovalKinds.Assertion => MauiTestAgentActions.Assert,
            MauiTestAgentApprovalKinds.Commit => MauiTestAgentActions.AuthorCommit,
            _ => MauiTestAgentActions.Tap,
        };
        var submitted = fixture.Service.SubmitApprovalRequest(new MauiTestAgentApprovalSubmitRequest
        {
            Envelope = fixture.Envelope($"normalize-authoring-{kind}", grantId: null),
            Kind = kind,
            Scope = new MauiTestAgentMutationScope
            {
                AllowedActions = [action],
                AllowedSelectors = kind == MauiTestAgentApprovalKinds.Commit ? [] : ["automationId:save"],
                AllowedSideEffectClasses = ["non-replayable"],
                MaxActionCount = 1,
                MaxValueBytes = 0,
            },
        });

        Assert.True(submitted.Ok, submitted.Error?.Message);
        Assert.Equal(["authoring"], submitted.Request!.RequestedScope!.AllowedSideEffectClasses);
    }

    [Fact]
    public void DraftOnlyAction_UsesAuthoringScopeRegardlessOfPlanPolicyVocabulary()
    {
        Assert.Equal("authoring", TestAgentActionTool.ResolveSideEffectClass(new MauiTestAgentActionRequest
        {
            Execute = false,
            AppendDraft = true,
            SideEffectClass = "non-replayable",
        }));
        Assert.Equal("ui", TestAgentActionTool.ResolveSideEffectClass(new MauiTestAgentActionRequest
        {
            Execute = true,
            AppendDraft = false,
        }));
    }

    [Fact]
    public void ReadCapability_AllowsStatusButCannotAuthorizeMutation()
    {
        var fixture = BeginFixture();
        var status = fixture.Service.Status(new MauiTestAgentSessionAccessRequest
        {
            SessionId = fixture.SessionId,
            ReadCapabilityId = fixture.ReadCapability,
        });
        Assert.True(status.Ok);

        var noGrant = fixture.Authorize(
            "tap-without-grant",
            grantId: null,
            MauiTestAgentActions.Tap,
            selector: new FlowSelector { AutomationId = "save" });
        Assert.False(noGrant.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.MutationGrantRequired, noGrant.Error?.Code);
    }

    [Fact]
    public void DraftActionsAssertionsAndCommit_RequireSeparateHumanGrants()
    {
        var fixture = BeginFixture();
        var actionGrant = fixture.IssueGrant(Scope(MauiTestAgentActions.Tap));
        var actionAuthorization = fixture.Authorize(
            "draft-tap",
            actionGrant.GrantId!,
            MauiTestAgentActions.Tap,
            selector: new FlowSelector { AutomationId = "save" });
        Assert.True(actionAuthorization.Ok);

        var appended = fixture.Service.AppendAction(
            fixture.SessionId,
            actionAuthorization.AuthorizationId,
            new MauiTestAgentActionRequest
            {
                Envelope = fixture.Envelope("draft-tap", actionGrant.GrantId!),
                AuthorizationId = actionAuthorization.AuthorizationId,
                Action = MauiTestAgentActions.Tap,
                Selector = new FlowSelector { AutomationId = "save" },
                AppendDraft = true,
                Execute = false,
            });
        Assert.True(appended.Ok);
        Assert.Single(appended.Snapshot!.Flow!.Steps);

        var assertionGrant = fixture.IssueGrant(new MauiTestAgentMutationScope
        {
            AllowedActions = [MauiTestAgentActions.Assert],
            AllowedSelectors = ["automationId:save"],
            AllowedSideEffectClasses = ["authoring"],
            MaxActionCount = 1,
            MaxValueBytes = 32,
        });
        var assertionAuthorization = fixture.Authorize(
            "draft-assert",
            assertionGrant.GrantId!,
            MauiTestAgentActions.Assert,
            selector: new FlowSelector { AutomationId = "save" },
            sideEffectClass: "authoring");
        var asserted = fixture.Service.AddAssertion(
            fixture.SessionId,
            assertionAuthorization.AuthorizationId,
            new MauiTestAgentAssertionRequest
            {
                Envelope = fixture.Envelope("draft-assert", assertionGrant.GrantId!),
                AuthorizationId = assertionAuthorization.AuthorizationId,
                Assertion = new FlowAssert
                {
                    Kind = "exists",
                    Selector = new FlowSelector { AutomationId = "save" },
                    Verify = true,
                },
            });
        Assert.True(asserted.Ok);

        var commitGrant = fixture.IssueGrant(new MauiTestAgentMutationScope
        {
            AllowedActions = [MauiTestAgentActions.AuthorCommit],
            AllowedSideEffectClasses = ["authoring"],
            MaxActionCount = 1,
            MaxValueBytes = 0,
        });
        var commitAuthorization = fixture.Authorize(
            "draft-commit",
            commitGrant.GrantId!,
            MauiTestAgentActions.AuthorCommit,
            sideEffectClass: "authoring");
        var committed = fixture.Service.Commit(
            new MauiTestAgentSessionAccessRequest
            {
                SessionId = fixture.SessionId,
                AuthorizationId = commitAuthorization.AuthorizationId,
                Envelope = fixture.Envelope("draft-commit", commitGrant.GrantId!),
            },
            commitAuthorization.AuthorizationId);

        Assert.True(committed.Ok, committed.Error?.Message);
        Assert.Equal(2, committed.Snapshot!.FlowRevision);
        Assert.Equal(2, committed.Snapshot.Plan!.Revision);
    }

    [Fact]
    public void AuthorizedDraftAppend_CannotSubstituteActionSelectorOrValue()
    {
        var fixture = BeginFixture();
        var grant = fixture.IssueGrant(Scope(MauiTestAgentActions.Tap));
        var authorization = fixture.Authorize(
            "approved-tap",
            grant.GrantId!,
            MauiTestAgentActions.Tap,
            selector: new FlowSelector { AutomationId = "save" });

        var substituted = fixture.Service.AppendAction(
            fixture.SessionId,
            authorization.AuthorizationId,
            new MauiTestAgentActionRequest
            {
                Envelope = fixture.Envelope("approved-tap", grant.GrantId!),
                AuthorizationId = authorization.AuthorizationId,
                Action = MauiTestAgentActions.Fill,
                Selector = new FlowSelector { AutomationId = "other" },
                Value = "unapproved",
                AppendDraft = true,
                Execute = false,
            });

        Assert.False(substituted.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.MutationGrantScopeDenied, substituted.Error?.Code);
    }

    [Fact]
    public void AuthorizedDraftAppend_SameAuthorizationCannotMutateTwiceBeforeCompletion()
    {
        var fixture = BeginFixture();
        var grant = fixture.IssueGrant(Scope(MauiTestAgentActions.Tap));
        var authorization = fixture.Authorize(
            "single-authorization",
            grant.GrantId!,
            MauiTestAgentActions.Tap,
            selector: new FlowSelector { AutomationId = "save" });
        var request = new MauiTestAgentActionRequest
        {
            Envelope = fixture.Envelope("single-authorization", grant.GrantId!),
            AuthorizationId = authorization.AuthorizationId,
            Action = MauiTestAgentActions.Tap,
            Selector = new FlowSelector { AutomationId = "save" },
            AppendDraft = true,
            Execute = false,
        };

        var first = fixture.Service.AppendAction(
            fixture.SessionId,
            authorization.AuthorizationId,
            request);
        var replay = fixture.Service.AppendAction(
            fixture.SessionId,
            authorization.AuthorizationId,
            request);

        Assert.True(first.Ok, first.Error?.Message);
        Assert.False(replay.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.MutationGrantRequired, replay.Error?.Code);
        Assert.Single(fixture.Service.Status(new MauiTestAgentSessionAccessRequest
        {
            SessionId = fixture.SessionId,
            ReadCapabilityId = fixture.ReadCapability,
        }).Snapshot!.Flow!.Steps);
        Assert.True(fixture.Service.CompleteMutation(new MauiTestAgentMutationCompletion
        {
            AuthorizationId = authorization.AuthorizationId,
            Outcome = "completed",
        }).Ok);
    }

    [Fact]
    public void Patch_ApplyApprovalAndRollbackAreForbiddenAndAuditIsBoundedAndRedacted()
    {
        var fixture = BeginFixture(
            options: new TestAgentSessionServiceOptions
            {
                MaxAuditEntries = 3,
                SessionLifetime = TimeSpan.FromHours(1),
                DefaultGrantLifetime = TimeSpan.FromMinutes(5),
                MaximumGrantLifetime = TimeSpan.FromMinutes(5),
                AuditRetention = TimeSpan.FromHours(1),
            });
        var rejected = fixture.Service.Patch(new MauiTestAgentPatchRequest
        {
            Envelope = fixture.Envelope("patch-apply", null),
            Operation = "apply",
        });

        Assert.False(rejected.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.PatchApplyForbidden, rejected.Error?.Code);
        var rollback = fixture.Service.Patch(new MauiTestAgentPatchRequest
        {
            Envelope = fixture.Envelope("patch-rollback", null),
            Operation = "rollback",
        });
        Assert.False(rollback.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.PatchApplyForbidden, rollback.Error?.Code);

        var audit = fixture.Service.Audit(new MauiTestAgentSessionAccessRequest
        {
            SessionId = fixture.SessionId,
            ReadCapabilityId = fixture.ReadCapability,
        });
        Assert.True(audit.Ok);
        Assert.True(audit.Entries.Count <= 3);
        var serialized = System.Text.Json.JsonSerializer.Serialize(audit, MauiTestingJsonContext.Default.MauiTestAgentAuditResult);
        Assert.DoesNotContain("do not expose this prompt", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_ExpiresAndAuditRetentionPurgesOldEntries()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var fixture = BeginFixture(
            clock,
            new TestAgentSessionServiceOptions
            {
                SessionLifetime = TimeSpan.FromSeconds(1),
                DefaultGrantLifetime = TimeSpan.FromSeconds(1),
                MaximumGrantLifetime = TimeSpan.FromSeconds(1),
                AuditRetention = TimeSpan.FromSeconds(1),
            });
        clock.Advance(TimeSpan.FromSeconds(2));

        var status = fixture.Service.Status(new MauiTestAgentSessionAccessRequest
        {
            SessionId = fixture.SessionId,
            ReadCapabilityId = fixture.ReadCapability,
        });
        Assert.False(status.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.SessionExpired, status.Error?.Code);
    }

    [Fact]
    public void Grant_ExpiryIsReportedBeforeDispatch()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var fixture = BeginFixture(
            clock,
            new TestAgentSessionServiceOptions
            {
                SessionLifetime = TimeSpan.FromMinutes(10),
                DefaultGrantLifetime = TimeSpan.FromSeconds(1),
                MaximumGrantLifetime = TimeSpan.FromSeconds(1),
                AuditRetention = TimeSpan.FromMinutes(10),
            });
        var grant = fixture.IssueGrant(Scope(MauiTestAgentActions.Tap));

        clock.Advance(TimeSpan.FromSeconds(2));
        var expired = fixture.Authorize(
            "expired-tap",
            grant.GrantId!,
            MauiTestAgentActions.Tap,
            selector: new FlowSelector { AutomationId = "save" });

        Assert.False(expired.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.MutationGrantExpired, expired.Error?.Code);
        Assert.False(expired.DispatchAllowed);
    }

    [Fact]
    public void ExplorationGrant_DecrementsAtomicallyAndAuditRetentionIsBounded()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var fixture = BeginFixture(
            clock,
            new TestAgentSessionServiceOptions
            {
                SessionLifetime = TimeSpan.FromHours(1),
                DefaultGrantLifetime = TimeSpan.FromMinutes(5),
                MaximumGrantLifetime = TimeSpan.FromMinutes(5),
                AuditRetention = TimeSpan.FromSeconds(1),
                MaxAuditEntries = 32,
            });
        var grant = fixture.IssueGrant(new MauiTestAgentMutationScope
        {
            AllowedActions = [MauiTestAgentActions.Tap],
            AllowedSelectors = ["automationId:save"],
            AllowedSideEffectClasses = ["ui"],
            MaxActionCount = 2,
            MaxValueBytes = 0,
        });

        var first = fixture.Authorize(
            "explore-1",
            grant.GrantId!,
            MauiTestAgentActions.Tap,
            selector: new FlowSelector { AutomationId = "save" });
        var second = fixture.Authorize(
            "explore-2",
            grant.GrantId!,
            MauiTestAgentActions.Tap,
            selector: new FlowSelector { AutomationId = "save" });
        var third = fixture.Authorize(
            "explore-3",
            grant.GrantId!,
            MauiTestAgentActions.Tap,
            selector: new FlowSelector { AutomationId = "save" });

        Assert.True(first.Ok);
        Assert.Equal(1, first.RemainingActions);
        Assert.True(second.Ok);
        Assert.Equal(0, second.RemainingActions);
        Assert.False(third.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.MutationGrantReused, third.Error?.Code);

        clock.Advance(TimeSpan.FromSeconds(2));
        var audit = fixture.Service.Audit(new MauiTestAgentSessionAccessRequest
        {
            SessionId = fixture.SessionId,
            ReadCapabilityId = fixture.ReadCapability,
        });
        Assert.True(audit.Ok);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public void ApprovalRequest_WorkbenchApprovalDeliversGrantOnlyThroughSessionStatus()
    {
        var fixture = BeginFixture();
        var submitted = fixture.Service.SubmitApprovalRequest(new MauiTestAgentApprovalSubmitRequest
        {
            Envelope = fixture.Envelope("request-draft-change", grantId: null),
            Kind = MauiTestAgentApprovalKinds.DraftChange,
            Scope = Scope(MauiTestAgentActions.Tap),
        });

        Assert.True(submitted.Ok, submitted.Error?.Message);
        Assert.Equal(MauiTestAgentApprovalStates.Pending, submitted.Request!.State);
        Assert.Null(submitted.Request.GrantId);

        var listed = fixture.Service.ListApprovalRequests(
            fixture.State.AgentId,
            fixture.State.AgentInstanceId,
            includeGrant: false);
        var pending = Assert.Single(listed);
        Assert.Null(pending.GrantId);

        var approved = fixture.Service.ApproveApprovalRequest(
            pending.ApprovalRequestId,
            approvedScope: null,
            fixture.State,
            fixture.HumanDecision(approved: true),
            grantExpiresAt: null);
        Assert.True(approved.Ok, approved.Error?.Message);
        Assert.Equal(MauiTestAgentApprovalStates.Approved, approved.Request!.State);
        Assert.Null(approved.Request.GrantId);

        var status = fixture.Service.Status(new MauiTestAgentSessionAccessRequest
        {
            SessionId = fixture.SessionId,
            ReadCapabilityId = fixture.ReadCapability,
        });
        var delivered = Assert.Single(status.Snapshot!.ApprovalRequests);
        Assert.Equal(MauiTestAgentApprovalStates.Approved, delivered.State);
        Assert.False(string.IsNullOrWhiteSpace(delivered.GrantId));

        var authorization = fixture.Authorize(
            "approved-request-tap",
            delivered.GrantId,
            MauiTestAgentActions.Tap,
            selector: new FlowSelector { AutomationId = "save" },
            sideEffectClass: "authoring");
        Assert.True(authorization.Ok, authorization.Error?.Message);

        var consumedStatus = fixture.Service.Status(new MauiTestAgentSessionAccessRequest
        {
            SessionId = fixture.SessionId,
            ReadCapabilityId = fixture.ReadCapability,
        });
        var consumed = Assert.Single(consumedStatus.Snapshot!.ApprovalRequests);
        Assert.Equal(MauiTestAgentApprovalStates.Consumed, consumed.State);
        Assert.Null(consumed.GrantId);
    }

    [Fact]
    public void ApprovalRequest_DefaultDecisionWindowAllowsTenMinutesForHumanReview()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var fixture = BeginFixture(clock);
        var submitted = fixture.Service.SubmitApprovalRequest(new MauiTestAgentApprovalSubmitRequest
        {
            Envelope = fixture.Envelope("request-default-window", grantId: null),
            Kind = MauiTestAgentApprovalKinds.DraftChange,
            Scope = Scope(MauiTestAgentActions.Tap),
        });

        Assert.True(submitted.Ok, submitted.Error?.Message);
        Assert.Equal(TimeSpan.FromMinutes(10), submitted.Request!.ExpiresAt - submitted.Request.CreatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void ApprovalRequest_NullValueLimitCanRemainNullOrNarrowToZero(int? approvedMaxValueBytes)
    {
        var fixture = BeginFixture();
        var requestedScope = Scope(MauiTestAgentActions.Tap);
        requestedScope.MaxValueBytes = null;
        var submitted = fixture.Service.SubmitApprovalRequest(new MauiTestAgentApprovalSubmitRequest
        {
            Envelope = fixture.Envelope($"request-null-value-limit-{approvedMaxValueBytes}", grantId: null),
            Kind = MauiTestAgentApprovalKinds.DraftChange,
            Scope = requestedScope,
        });

        var approvedScope = Scope(MauiTestAgentActions.Tap);
        approvedScope.MaxValueBytes = approvedMaxValueBytes;
        var approved = fixture.Service.ApproveApprovalRequest(
            submitted.Request!.ApprovalRequestId,
            approvedScope,
            fixture.State,
            fixture.HumanDecision(approved: true),
            grantExpiresAt: null);

        Assert.True(approved.Ok, approved.Error?.Message);
        Assert.Equal(MauiTestAgentApprovalStates.Approved, approved.Request!.State);
    }

    [Fact]
    public void ApprovalGrant_RemainsBoundedAcrossItsOwnDraftDigestChanges()
    {
        var fixture = BeginFixture();
        var submitted = fixture.Service.SubmitApprovalRequest(new MauiTestAgentApprovalSubmitRequest
        {
            Envelope = fixture.Envelope("request-two-draft-actions", grantId: null),
            Kind = MauiTestAgentApprovalKinds.DraftChange,
            Scope = new MauiTestAgentMutationScope
            {
                AllowedActions = [MauiTestAgentActions.Tap],
                AllowedSelectors = ["automationId:save"],
                AllowedSideEffectClasses = ["ui"],
                MaxActionCount = 2,
                MaxValueBytes = 0,
            },
        });
        var approved = fixture.Service.ApproveApprovalRequest(
            submitted.Request!.ApprovalRequestId,
            approvedScope: null,
            fixture.State,
            fixture.HumanDecision(approved: true),
            grantExpiresAt: null);
        Assert.True(approved.Ok, approved.Error?.Message);
        var grantId = Assert.Single(fixture.Service.Status(new MauiTestAgentSessionAccessRequest
        {
            SessionId = fixture.SessionId,
            ReadCapabilityId = fixture.ReadCapability,
        }).Snapshot!.ApprovalRequests).GrantId!;
        var originalCorrelation = fixture.Correlation();

        MauiTestAgentSessionResult Append(string key)
        {
            var envelope = fixture.Envelope(key, grantId);
            envelope.Correlation = new MauiTestAgentCorrelation
            {
                AuthoringSessionId = originalCorrelation.AuthoringSessionId,
                PlanId = originalCorrelation.PlanId,
                PlanRevision = originalCorrelation.PlanRevision,
                PlanDigest = originalCorrelation.PlanDigest,
                FlowId = originalCorrelation.FlowId,
                FlowRevision = originalCorrelation.FlowRevision,
                FlowDigest = originalCorrelation.FlowDigest,
            };
            var authorization = fixture.Service.AuthorizeMutation(new MauiTestAgentMutationAuthorizationRequest
            {
                Envelope = envelope,
                Action = MauiTestAgentActions.Tap,
                Selector = new FlowSelector { AutomationId = "save" },
                SideEffectClass = "authoring",
                CurrentTargetState = fixture.State,
            });
            Assert.True(authorization.Ok, authorization.Error?.Message);
            return fixture.Service.AppendAction(
                fixture.SessionId,
                authorization.AuthorizationId,
                new MauiTestAgentActionRequest
                {
                    Envelope = envelope,
                    AuthorizationId = authorization.AuthorizationId,
                    Action = MauiTestAgentActions.Tap,
                    Selector = new FlowSelector { AutomationId = "save" },
                    AppendDraft = true,
                    Execute = false,
                });
        }

        Assert.True(Append("approved-step-1").Ok);
        var second = Append("approved-step-2");
        Assert.True(second.Ok, second.Error?.Message);
        Assert.Equal(2, second.Snapshot!.Flow!.Steps.Count);
        var consumed = Assert.Single(second.Snapshot.ApprovalRequests);
        Assert.Equal(MauiTestAgentApprovalStates.Consumed, consumed.State);
        Assert.Null(consumed.GrantId);
    }

    [Fact]
    public void ApprovalRequest_CannotBeBroadenedAndStaleTargetFailsClosed()
    {
        var fixture = BeginFixture();
        var submitted = fixture.Service.SubmitApprovalRequest(new MauiTestAgentApprovalSubmitRequest
        {
            Envelope = fixture.Envelope("request-bounded", grantId: null),
            Kind = MauiTestAgentApprovalKinds.DraftChange,
            Scope = Scope(MauiTestAgentActions.Tap),
        });
        Assert.True(submitted.Ok, submitted.Error?.Message);

        var broadened = fixture.Service.ApproveApprovalRequest(
            submitted.Request!.ApprovalRequestId,
            new MauiTestAgentMutationScope
            {
                AllowedActions = [MauiTestAgentActions.Tap, MauiTestAgentActions.Fill],
                AllowedSelectors = ["automationId:save"],
                AllowedSideEffectClasses = ["ui"],
                MaxActionCount = 2,
                MaxValueBytes = 64,
            },
            fixture.State,
            fixture.HumanDecision(approved: true),
            grantExpiresAt: null);
        Assert.False(broadened.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.ApprovalRequestScopeDenied, broadened.Error?.Code);

        var stale = fixture.Service.ApproveApprovalRequest(
            submitted.Request.ApprovalRequestId,
            approvedScope: null,
            new MauiTestAgentTargetState
            {
                AgentId = fixture.State.AgentId,
                AgentInstanceId = fixture.State.AgentInstanceId,
                AppBuildFingerprint = "different-build",
                SeedFingerprint = fixture.State.SeedFingerprint,
            },
            fixture.HumanDecision(approved: true),
            grantExpiresAt: null);
        Assert.False(stale.Ok);
        Assert.Equal(MauiTestAgentErrorCodes.MutationGrantStale, stale.Error?.Code);
        Assert.Equal(
            MauiTestAgentApprovalStates.Stale,
            fixture.Service.GetApprovalRequest(submitted.Request.ApprovalRequestId, includeGrant: true).Request!.State);
    }

    [Fact]
    public void ApprovalRequest_RejectAndExpiryNeverIssueGrant()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var fixture = BeginFixture(
            clock,
            new TestAgentSessionServiceOptions
            {
                SessionLifetime = TimeSpan.FromMinutes(10),
                ApprovalRequestLifetime = TimeSpan.FromSeconds(1),
                ApprovalRetention = TimeSpan.FromMinutes(1),
            });
        var rejected = fixture.Service.SubmitApprovalRequest(new MauiTestAgentApprovalSubmitRequest
        {
            Envelope = fixture.Envelope("request-reject", grantId: null),
            Kind = MauiTestAgentApprovalKinds.Assertion,
            Scope = new MauiTestAgentMutationScope
            {
                AllowedActions = [MauiTestAgentActions.Assert],
                AllowedSelectors = ["automationId:save"],
                AllowedSideEffectClasses = ["authoring"],
                MaxActionCount = 1,
                MaxValueBytes = 0,
            },
        });
        var rejection = fixture.Service.RejectApprovalRequest(
            rejected.Request!.ApprovalRequestId,
            fixture.HumanDecision(approved: false),
            "not-this-test");
        Assert.True(rejection.Ok);
        Assert.Equal(MauiTestAgentApprovalStates.Rejected, rejection.Request!.State);
        Assert.Null(rejection.Request.GrantId);

        var expiring = fixture.Service.SubmitApprovalRequest(new MauiTestAgentApprovalSubmitRequest
        {
            Envelope = fixture.Envelope("request-expire", grantId: null),
            Kind = MauiTestAgentApprovalKinds.DraftChange,
            Scope = Scope(MauiTestAgentActions.Tap),
        });
        clock.Advance(TimeSpan.FromSeconds(2));
        var expired = fixture.Service.GetApprovalRequest(expiring.Request!.ApprovalRequestId, includeGrant: true);
        Assert.Equal(MauiTestAgentApprovalStates.Expired, expired.Request!.State);
        Assert.Null(expired.Request.GrantId);
    }

    [Fact]
    public void SelectorProjection_ReturnsValueFreeDurableAndAmbiguousIdentities()
    {
        var projection = TestAgentImprovementsTool.BuildSelectorProjection(
        [
            new MauiSelectorObservationElement
            {
                Id = "entry",
                Type = "Entry",
                AutomationId = "NewTodoEntry",
                IsVisible = true,
                IsEnabled = true,
            },
            new MauiSelectorObservationElement
            {
                Id = "add",
                Type = "Button",
                AutomationId = "AddButton",
                IsVisible = true,
                IsEnabled = true,
            },
            new MauiSelectorObservationElement
            {
                Id = "check-1",
                Type = "CheckBox",
                AutomationId = "TodoCheckBox",
                IsVisible = true,
                IsEnabled = true,
            },
            new MauiSelectorObservationElement
            {
                Id = "check-2",
                Type = "CheckBox",
                AutomationId = "TodoCheckBox",
                IsVisible = true,
                IsEnabled = true,
            },
        ]);

        Assert.Equal(4, projection.Count);
        Assert.Contains(projection, item =>
            item.Selector == "automationId:NewTodoEntry" &&
            item.Quality == "durable" &&
            item.MatchCount == 1);
        Assert.Equal(
            2,
            projection.Count(item =>
                item.Selector == "automationId:TodoCheckBox" &&
                item.Quality == "ambiguous" &&
                item.MatchCount == 2));
    }

    private static Fixture BeginFixture(
        MutableTimeProvider? clock = null,
        TestAgentSessionServiceOptions? options = null)
    {
        var service = new TestAgentSessionService(options, clock);
        var state = State();
        var begin = service.Begin(new MauiTestAgentSessionBeginRequest
        {
            Envelope = Envelope(Target(), "begin"),
            TargetState = state,
        });
        Assert.True(begin.Ok, begin.Error?.Message);
        return new Fixture(service, state, begin.Snapshot!);
    }

    private static MauiTestAgentTarget Target() => new()
    {
        AgentId = "agent-a",
        AgentInstanceId = "instance-a",
        AppBuildFingerprint = "build-a",
        SeedFingerprint = "seed-a",
    };

    private static MauiTestAgentTargetState State() => new()
    {
        AgentId = "agent-a",
        AgentInstanceId = "instance-a",
        AppBuildFingerprint = "build-a",
        SeedFingerprint = "seed-a",
    };

    private static MauiTestAgentRequestEnvelope Envelope(MauiTestAgentTarget target, string key) => new()
    {
        RequestId = "req-" + key,
        IdempotencyKey = "idem-" + key,
        Target = target,
        Correlation = new MauiTestAgentCorrelation(),
        Provenance = new MauiActorProvenance
        {
            ActorKind = "agent",
            ActorId = "agent-author",
            Channel = "mcp",
            Provider = "host-owned",
        },
        Intent = "Test authoring intent; do not expose this prompt.",
        DeadlineMs = 30_000,
        PolicyVersion = MauiTestAgentProtocolVersions.PolicyVersion,
    };

    private static MauiTestAgentMutationScope Scope(string action) => new()
    {
        AllowedActions = [action],
        AllowedSelectors = ["automationId:save"],
        AllowedSideEffectClasses = ["ui"],
        MaxActionCount = 1,
        MaxValueBytes = 64,
    };

    private sealed class Fixture
    {
        public Fixture(TestAgentSessionService service, MauiTestAgentTargetState state, MauiTestAgentAuthoringSnapshot snapshot)
        {
            Service = service;
            State = state;
            Snapshot = snapshot;
        }

        public TestAgentSessionService Service { get; }
        public MauiTestAgentTargetState State { get; }
        public MauiTestAgentAuthoringSnapshot Snapshot { get; }
        public string SessionId => Snapshot.SessionId!;
        public string ReadCapability => Snapshot.ReadCapabilityId!;

        private MauiTestAgentAuthoringSnapshot CurrentSnapshot()
            => Service.Status(new MauiTestAgentSessionAccessRequest
            {
                SessionId = SessionId,
                ReadCapabilityId = ReadCapability,
            }).Snapshot!;

        public MauiTestAgentCorrelation Correlation()
        {
            var current = CurrentSnapshot();
            return new MauiTestAgentCorrelation
            {
                AuthoringSessionId = SessionId,
                PlanId = current.Plan!.PlanId,
                PlanRevision = current.Plan.Revision,
                PlanDigest = current.PlanDigest,
                FlowId = current.Plan.Flow!.FlowId,
                FlowRevision = current.FlowRevision,
                FlowDigest = current.FlowDigest,
            };
        }

        public MauiTestAgentRequestEnvelope Envelope(string key, string? grantId) => new()
        {
            RequestId = "req-" + key,
            IdempotencyKey = "idem-" + key,
            Target = Target(),
            Correlation = Correlation(),
            Provenance = new MauiActorProvenance
            {
                ActorKind = "agent",
                ActorId = "agent-author",
                Channel = "mcp",
                Provider = "host-owned",
            },
            Intent = "Test authoring intent; do not expose this prompt.",
            ApprovalGrantId = grantId,
            ReadCapabilityId = ReadCapability,
            DeadlineMs = 30_000,
            PolicyVersion = MauiTestAgentProtocolVersions.PolicyVersion,
        };

        public MauiTestAgentGrantIssueResult IssueGrant(MauiTestAgentMutationScope scope) => Service.IssueGrant(new MauiTestAgentGrantIssueRequest
        {
            SessionId = SessionId,
            ReadCapabilityId = ReadCapability,
            TargetState = State,
            Correlation = Correlation(),
            Scope = scope,
            Approval = new MauiTestAgentHumanApproval
            {
                Approved = true,
                ApprovalChannel = "workbench",
                Actor = new MauiActorProvenance
                {
                    ActorKind = "human",
                    ActorId = "reviewer",
                    Channel = "workbench",
                    Provider = "host-owned",
                },
            },
        });

        public MauiTestAgentHumanApproval HumanDecision(bool approved) => new()
        {
            Approved = approved,
            ApprovalChannel = "workbench",
            Actor = new MauiActorProvenance
            {
                ActorKind = "human",
                ActorId = "reviewer",
                Channel = "workbench",
                Provider = "host-owned",
            },
        };

        public MauiTestAgentMutationAuthorizationResult Authorize(
            string key,
            string? grantId,
            string action,
            FlowSelector? selector = null,
            string? value = null,
            string? sideEffectClass = "ui",
            MauiTestAgentTargetState? state = null)
            => Service.AuthorizeMutation(new MauiTestAgentMutationAuthorizationRequest
            {
                Envelope = Envelope(key, grantId),
                Action = action,
                Selector = selector,
                SideEffectClass = sideEffectClass,
                ValueLength = value is null ? null : System.Text.Encoding.UTF8.GetByteCount(value),
                ValueDigest = value is null ? null : "super-secret",
                CurrentTargetState = state ?? State,
            });
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
