using System.Text.Json;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Tests;

public class TestingSchemaContractTests
{
    [Fact]
    public void TestingContracts_RoundTripExtensionFieldsWithSourceGeneratedContext()
    {
        const string planJson = """
            {
              "schema": 1,
              "planId": "plan-login",
              "goal": "Sign in safely",
              "sideEffectPolicy": "none",
              "x-plan-extension": { "retained": true }
            }
            """;
        const string reportJson = """
            {
              "schema": 1,
              "runId": "run-1",
              "x-report-extension": [ "retained" ]
            }
            """;
        const string proposalJson = """
            {
              "schema": 1,
              "proposalId": "repair-1",
              "baseFlow": { "digest": "abc" },
              "oldSelector": { "automationId": "old" },
              "proposedSelector": { "automationId": "new" },
              "x-proposal-extension": { "retained": true }
            }
            """;
        const string outcomeJson = """
            {
              "schema": 1,
              "proposalId": "repair-1",
              "state": "verified",
              "x-outcome-extension": "retained"
            }
            """;

        var plan = JsonSerializer.Deserialize(planJson, MauiTestingJsonContext.Default.MauiTestPlan);
        var report = JsonSerializer.Deserialize(reportJson, MauiTestingJsonContext.Default.MauiFlowRunReport);
        var proposal = JsonSerializer.Deserialize(proposalJson, MauiTestingJsonContext.Default.MauiFlowRepairProposal);
        var outcome = JsonSerializer.Deserialize(outcomeJson, MauiTestingJsonContext.Default.MauiFlowRepairOutcome);

        Assert.NotNull(plan);
        Assert.NotNull(report);
        Assert.NotNull(proposal);
        Assert.NotNull(outcome);
        Assert.True(plan.ExtensionData!.ContainsKey("x-plan-extension"));
        Assert.True(report.ExtensionData!.ContainsKey("x-report-extension"));
        Assert.True(proposal.ExtensionData!.ContainsKey("x-proposal-extension"));
        Assert.True(outcome.ExtensionData!.ContainsKey("x-outcome-extension"));

        AssertExtensionRoundTrips(
            JsonSerializer.Serialize(plan, MauiTestingJsonContext.Default.MauiTestPlan),
            "x-plan-extension");
        AssertExtensionRoundTrips(
            JsonSerializer.Serialize(report, MauiTestingJsonContext.Default.MauiFlowRunReport),
            "x-report-extension");
        AssertExtensionRoundTrips(
            JsonSerializer.Serialize(proposal, MauiTestingJsonContext.Default.MauiFlowRepairProposal),
            "x-proposal-extension");
        AssertExtensionRoundTrips(
            JsonSerializer.Serialize(outcome, MauiTestingJsonContext.Default.MauiFlowRepairOutcome),
            "x-outcome-extension");
    }

    [Fact]
    public void MigrationPreview_Schema1ToSchema2PreservesUnknownFieldsWithoutInventingFacts()
    {
        const string markdown = """
            # Scenario: legacy

            ```json maui-test
            {
              "schema": 1,
              "name": "legacy",
              "x-root-extension": { "keep": true },
              "steps": [
                {
                  "seq": 1,
                  "action": "tap",
                  "args": {
                    "selector": { "automationId": "save" },
                    "x-args-extension": "keep"
                  },
                  "x-step-extension": [ 1, 2, 3 ]
                }
              ]
            }
            ```
            """;

        var parsed = FlowMarkdown.Parse(markdown);

        Assert.True(parsed.Ok, parsed.Error);
        var preview = MauiFlowMigration.Preview(parsed.Flow!);

        Assert.Equal(1, preview.SourceSchema);
        Assert.Equal(2, preview.TargetSchema);
        Assert.True(preview.CanWrite);
        Assert.True(preview.WriteRequired);
        Assert.Single(preview.Changes);
        Assert.Equal(2, preview.NormalizedFlow!.Schema);
        Assert.DoesNotContain("flowId", preview.NormalizedFlow.ExtensionData!.Keys);
        Assert.True(preview.NormalizedFlow.ExtensionData.ContainsKey("x-root-extension"));
        Assert.True(preview.NormalizedFlow.Steps[0].ExtensionData!.ContainsKey("x-step-extension"));
        Assert.True(preview.NormalizedFlow.Steps[0].Args!.ExtensionData!.ContainsKey("x-args-extension"));

        var serialized = FlowMarkdown.Serialize(preview.NormalizedFlow);
        Assert.Contains("\"x-root-extension\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"x-step-extension\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"x-args-extension\"", serialized, StringComparison.Ordinal);

        var serializedPreview = JsonSerializer.Serialize(
            preview,
            MauiTestingJsonContext.Default.MauiFlowMigrationResult);
        Assert.Contains("\"sourceSchema\": 1", serializedPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationPreview_Schema2DoesNotRequireWrite()
    {
        var preview = MauiFlowMigration.Preview(new MauiFlow
        {
            Schema = 2,
            Name = "current",
            Steps =
            {
                new FlowStep
                {
                    Seq = 1,
                    Action = FlowActions.Assert,
                },
            },
        });

        Assert.Equal(2, preview.SourceSchema);
        Assert.Equal(2, preview.TargetSchema);
        Assert.True(preview.CanWrite);
        Assert.False(preview.WriteRequired);
        Assert.Empty(preview.Changes);
    }

    [Fact]
    public void MigrationPreview_RejectsSchema3WithoutGeneratingAFlow()
    {
        var preview = MauiFlowMigration.Preview(new MauiFlow { Schema = 2 }, targetSchema: 3);

        Assert.Equal(2, preview.SourceSchema);
        Assert.Equal(3, preview.TargetSchema);
        Assert.False(preview.CanWrite);
        Assert.False(preview.WriteRequired);
        Assert.Null(preview.NormalizedFlow);
        Assert.Contains(preview.Warnings, warning => warning.Contains("not a supported migration target"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FlowMarkdown_ParsesAndValidatesCurrentAndLegacySchemas(int schema)
    {
        var markdown = $$"""
            ```json maui-test
            {
              "schema": {{schema}},
              "name": "compatibility",
              "steps": [
                {
                  "seq": 1,
                  "action": "tap",
                  "args": { "selector": { "automationId": "save" } }
                }
              ]
            }
            ```
            """;

        var parsed = FlowMarkdown.Parse(markdown);

        Assert.True(parsed.Ok, parsed.Error);
        Assert.Equal(schema, parsed.Flow!.Schema);
        Assert.True(FlowValidator.Validate(parsed.Flow).Ok);
    }

    [Fact]
    public void RequirementValidation_UnsupportedRequiredSemanticFailsClosed()
    {
        var requirements = new MauiFlowRequirements
        {
            RequiredSemantics =
            [
                new MauiRequiredSemantic
                {
                    Name = "future.checkpoint.v9",
                    Required = true,
                },
            ],
        };

        var validation = MauiFlowRequirementValidator.Validate(requirements, new MauiFlowCapabilitySet());

        Assert.False(validation.IsValid);
        var error = Assert.Single(validation.Errors);
        Assert.Equal("required-semantics-unsupported", error.Code);
        Assert.Contains("future.checkpoint.v9", error.Message, StringComparison.Ordinal);

        var serialized = JsonSerializer.Serialize(
            validation,
            MauiTestingJsonContext.Default.MauiFlowRequirementValidation);
        Assert.Contains("\"required-semantics-unsupported\"", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void SafetyContracts_RoundTripWithSourceGeneratedContext()
    {
        const string requestJson = """
            {
              "plan": {
                "schema": 1,
                "planId": "plan-safety",
                "sideEffectPolicy": "test-tenant-resettable",
                "checkpoint": {
                  "appBuildFingerprint": "build-1",
                  "appStateSeed": { "fingerprint": "app-seed" },
                  "backendTestDataSeed": { "fingerprint": "backend-seed" },
                  "route": "/home",
                  "window": "main",
                  "modal": "none",
                  "locale": "en-US",
                  "theme": "light",
                  "orientation": "portrait",
                  "displayProfile": "phone",
                  "collectionItemKey": "item-1"
                },
                "independentBusinessOracles": [
                  { "oracleId": "order-recorded", "required": true, "independent": true }
                ]
              },
              "context": {
                "intent": "ordinary-replay",
                "preconditions": {
                  "expected": { "appBuildFingerprint": "build-1", "route": "/home" },
                  "observed": { "appBuildFingerprint": "build-1", "route": "/home" }
                },
                "reset": {
                  "succeeded": true,
                  "appStateSucceeded": true,
                  "backendTestDataSucceeded": true,
                  "appStateSeed": { "fingerprint": "app-seed" },
                  "backendTestDataSeed": { "fingerprint": "backend-seed" }
                },
                "businessOracles": [
                  { "oracleId": "order-recorded", "succeeded": true, "independent": true }
                ]
              }
            }
            """;

        var request = JsonSerializer.Deserialize(requestJson, MauiTestingJsonContext.Default.MauiFlowRunRequest);

        Assert.NotNull(request);
        Assert.Equal(MauiFlowSideEffectPolicy.TestTenantResettable, request!.Plan!.ParsedSideEffectPolicy);
        Assert.Equal("app-seed", request.Context!.Reset!.AppStateSeed!.Fingerprint);
        Assert.Single(request.Context.BusinessOracles);
        var json = JsonSerializer.Serialize(request, MauiTestingJsonContext.Default.MauiFlowRunRequest);
        Assert.Contains("\"sideEffectPolicy\": \"test-tenant-resettable\"", json, StringComparison.Ordinal);
        Assert.Contains("\"independentBusinessOracles\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SideEffectPolicy_UsesStableLowerCaseWireValues()
    {
        var json = JsonSerializer.Serialize(
            MauiFlowSideEffectPolicy.TestTenantResettable,
            MauiTestingJsonContext.Default.MauiFlowSideEffectPolicy);
        var parsed = JsonSerializer.Deserialize(
            "\"non-replayable\"",
            MauiTestingJsonContext.Default.MauiFlowSideEffectPolicy);

        Assert.Equal("\"test-tenant-resettable\"", json);
        Assert.Equal(MauiFlowSideEffectPolicy.NonReplayable, parsed);
    }

    private static void AssertExtensionRoundTrips(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty(name, out _), $"Missing extension field '{name}'.");
    }
}
