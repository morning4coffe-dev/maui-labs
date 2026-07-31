# Microsoft.Maui.DevFlow.Testing

> ⚠️ **Experimental preview** — APIs and flow contracts may change before a stable release.

`Microsoft.Maui.DevFlow.Testing` provides the framework-neutral implementation of DevFlow
Markdown workflow tests. It owns flow contracts, Markdown parsing and serialization, validation,
recording, selector actionability, and replay reports. It does not depend on the CLI, broker,
Inspector, MCP, a test framework, or app/device lifecycle orchestration.

## Install

```xml
<PackageReference Include="Microsoft.Maui.DevFlow.Testing" Version="0.1.0-preview.*" />
```

The package targets `net9.0` and references `Microsoft.Maui.DevFlow.Driver`. It can be consumed by
`net9.0`, `net10.0`, and compatible MAUI test hosts.

## Use a flow contract

```csharp
using Microsoft.Maui.DevFlow.Testing;

var parsed = FlowMarkdown.Parse(File.ReadAllText("maui-tests/login.md"));
if (!parsed.Ok)
    throw new InvalidOperationException(parsed.Error);

var validation = FlowValidator.Validate(parsed.Flow!);
if (!validation.Ok)
    throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));
```

Use `MauiFlowRunner` with a DevFlow `AgentClient` (or an `IMauiFlowDriver`) when a host has already
arranged app lifecycle, connection, reset, and test data. It is the canonical execution engine and
returns a bounded `MauiFlowRunReport`:

```csharp
var runner = new MauiFlowRunner(agent, new MauiFlowRunnerOptions
{
    RunId = "run_example",
    ArtifactRoot = "artifacts/devflow",
});

MauiFlowRunReport report = await runner.RunAsync(parsed.Flow!);
if (report.Outcome?.Status != MauiFlowRunOutcomes.Passed)
    throw new InvalidOperationException(report.Failure?.Code);
```

The report contains ordered lifecycle events, step timing, selector/candidate and actionability
facts, fenced mutation receipts, assertion disclosure state, typed failure classification, and
artifact references. Lists and JSON output are bounded (1 MB by default); every omission is
explicit. String values are disclosed only for safe scalars. Typed text and sensitive values are
represented by a redacted descriptor with type, length, and SHA-256 digest.

`FlowReplayer` remains a compatibility facade. It delegates to `MauiFlowRunner` and returns the
legacy `FlowReplayReport` shape with additive `report`, `reportPath`, and `reportDigest` fields.
This keeps existing CLI, MCP, and Inspector consumers compatible while allowing new hosts to use
the structured result directly.

## Replay safety, reset evidence, and business oracles

`MauiTestPlan` can declare a side-effect policy. The package defines data contracts and deterministic
admission only; it does **not** publish reset, device, backend, compensator, or oracle execution
interfaces.

| Policy | Admission rule |
|---|---|
| `none` | Expected and observed clean-state checkpoints must match. |
| `test-tenant-resettable` | Matching checkpoints plus successful app-state and backend/test-data reset evidence with matching seed fingerprints. |
| `compensated` | Matching checkpoints plus either matching successful reset evidence or a successful outcome for the plan's declared compensator. |
| `non-replayable` | No automatic replay, continuation, or repair validation. A caller can admit one human run only with `manualOneShotAuthorization: true`; it is never repair-eligible. |

Plans declare expected build, app-state seed, backend/test-data seed, route, window, modal, locale,
theme, orientation, display profile, and (when applicable) collection-item key. Hosts provide the
observed checkpoint in `MauiFlowRunContext`. A missing declared observation or a mismatch denies
admission before the runner sends a mutating command.

```csharp
var decision = MauiFlowReplaySafetyEvaluator.Evaluate(new MauiFlowRunRequest
{
    Plan = plan,
    Context = new MauiFlowRunContext
    {
        Intent = MauiFlowReplayIntents.OrdinaryReplay,
        Preconditions = new MauiFlowReplayPreconditions
        {
            Expected = expectedCheckpoint,
            Observed = observedCheckpoint,
        },
        Reset = hostReportedReset,
        BusinessOracles =
        [
            new MauiIndependentBusinessOracleResult
            {
                OracleId = "order-recorded",
                Succeeded = true,
                Independent = true,
            },
        ],
    },
});

if (!decision.OrdinaryReplayAllowed)
    throw new InvalidOperationException(string.Join("; ", decision.Reasons.Select(x => x.Code)));
```

An independent business oracle must be declared and succeed before a passing run or repair can be
marked verified. `MauiFlowRunReport` retains the reset result, expected/observed preconditions,
compensator outcome, oracle results, and `replayEligibility` reasons. These values are redacted and
bounded with the rest of the report.

Legacy schema-1/schema-2 manual flows with no plan remain ordinary-replay compatible. Their report
explicitly records `sideEffectPolicy: "unspecified"` and `repairEligibility: false`; hosts should
surface that warning rather than treating the run as repair-verified.

## Schemas and compatibility

The formal contracts are maintained with the DevFlow protocol specification:

- [Executable flow v2](../../../docs/DevFlow/spec/schemas/maui-flow-v2.json)
- [Test plan v1](../../../docs/DevFlow/spec/schemas/maui-test-plan-v1.json)
- [Flow run report v1](../../../docs/DevFlow/spec/schemas/maui-flow-run-report-v1.json)
- [Repair proposal v1](../../../docs/DevFlow/spec/schemas/maui-flow-repair-proposal-v1.json)
  and [repair outcome v1](../../../docs/DevFlow/spec/schemas/maui-flow-repair-outcome-v1.json)

Schema 1 and schema 2 executable flows remain readable. Schema 2 is current; use
`MauiFlowMigration.Preview` to inspect a schema-1-to-schema-2 normalization without writing a
file. The preview preserves unknown fields and never creates fingerprints, source anchors, IDs,
revisions, or live validation facts. Schema 3 is not implemented.

```csharp
var preview = MauiFlowMigration.Preview(parsed.Flow!);
if (preview.WriteRequired)
{
    // Show preview.Changes and preview.Warnings before any host writes a file.
    var normalized = preview.NormalizedFlow!;
}
```

`MauiTestPlan`, `MauiFlowRunReport`, `MauiFlowRepairProposal`, and
`MauiFlowRepairOutcome` are provider-neutral data contracts. Plans declare side-effect policy,
reset expectations, capability requirements, and approval metadata, but they do not expose a
public reset, launch, install, broker, or device lifecycle interface. Validate future required
semantics explicitly before a run:

```csharp
var requirements = MauiFlowRequirementValidator.Validate(plan.Requirements, availableCapabilities);
if (!requirements.IsValid)
    throw new InvalidOperationException(requirements.Errors[0].Message);
```

Executable flows use `MauiFlowJsonContext`; plans, reports, repair contracts, and migration
previews use `MauiTestingJsonContext`. Both are source-generated and compatible readers and
writers retain unknown extension fields to support future additive versions.

`MauiFlowFailureClassifier` is pure and deterministic: it maps observed terminal state,
precondition facts, and legacy `FailureKind` strings to one typed code/category/phase. It does not
retry mutations or generate selector repairs. Only a pre-dispatch `locator-not-found` with a
verified matching checkpoint is marked repair-eligible; the package does not generate proposals.

The CLI's `maui devflow flow validate <flow.md>` command uses this same parser and validator
without connecting to or driving an app.

## Platform support

| Host | Status |
|---|---|
| .NET 9 / .NET 10 test hosts | Preview |
| Android, iOS, Mac Catalyst, Windows MAUI hosts | Preview through the DevFlow Driver |
| CLI, broker, Inspector, MCP | Adapters; not package dependencies |

The package deliberately does not publish reset, install, launch, broker, or device orchestration
interfaces. Hosts own those operations and invoke the same flow runtime after the app is ready.
