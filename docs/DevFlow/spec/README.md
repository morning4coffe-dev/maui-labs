# DevFlow protocol spec

This directory contains the canonical DevFlow protocol contract used by the MAUI implementation in this repository.

- `openapi.yaml` defines the versioned HTTP surface under `/api/v1/*` and is the canonical OpenAPI document, including logical storage root discovery and sandboxed file management. The current shared implementation advertises only the `appData` root.
- `broker-workflow-runs-v1.yaml` defines the separate local broker workflow-run surface under
  `/api/workflow-runs/*`. It is intentionally not part of the in-app agent OpenAPI document.
- `asyncapi.yaml` defines the streaming channels under `/ws/v1/*`
- `schemas/` contains the shared payload models
- `examples/` contains representative request and response payloads, including platform job listing and run requests

These spec files are intended to stay framework-agnostic so the same DevFlow contract can be implemented across MAUI and other UI stacks.

Do not commit a generated JSON copy of the OpenAPI document. If a consumer needs JSON, generate it from `openapi.yaml` as part of that workflow so there is only one source of truth.

The DevFlow unit tests parse `openapi.yaml` with OpenAPI tooling and validate YAML/JSON syntax plus `$ref` targets across this directory.

## Testing contracts

The public `Microsoft.Maui.DevFlow.Testing` package uses these provider-neutral contracts:

- [Executable MAUI flow v2](schemas/maui-flow-v2.json) — the authoritative payload in a
  Markdown `json maui-test` block.
- [Test plan v1](schemas/maui-test-plan-v1.json) — non-executable goals, safety constraints,
  reset requirements, side-effect policy, expected clean-state preconditions, compensator
  declarations, independent business-oracle declarations, acceptance criteria, and approval
  metadata.
- [Flow run report v1](schemas/maui-flow-run-report-v1.json) — bounded run, target, reset,
  expected/observed preconditions, side-effect admission decision, compensation/oracle evidence,
  outcome, ordered actionability/command/assertion step evidence, typed failure, and artifact
  facts. Failure evidence links back through additive `manifest.flowRun` metadata in `.mauitrace`
  v1; it never adds a ZIP entry or changes the evidence allow-list.
- [Broker workflow run v1](schemas/broker-workflow-run-v1.json) — broker lifecycle, explicit
  agent-instance targeting, idempotency, and per-run capability-token contracts.
- [Workflow command ledger](schemas/workflow-command-ledger.json) — agent-instance-bound begin/end
  control, contiguous command envelopes, bounded duplicate receipts, and unknown-completion fencing.
- [Repair proposal v1](schemas/maui-flow-repair-proposal-v1.json) and
  [repair outcome v1](schemas/maui-flow-repair-outcome-v1.json) — reviewable selector-repair
  evidence and immutable outcomes.

Schema 2 is the current executable flow contract. Schema 1 remains readable by the package and
can be preview-normalized to schema 2 without writing a file or inventing fingerprints, source
anchors, IDs, revisions, or live validation facts. Schema 3 is intentionally not defined yet.
Compatible readers and writers preserve unknown extension fields. Plans, reports, and repair
documents are data contracts only: a plan never authorizes reset or device lifecycle execution.

Required capabilities and semantics are declared by a plan rather than changing flow schema 2.
Hosts must reject an unsupported required semantic before any mutation is attempted.

### Side-effect admission

`sideEffectPolicy` is one of `none`, `test-tenant-resettable`, `compensated`, or
`non-replayable`. The broker and Testing runtime use the same pure admission decision before a
mutation lease or device mutation:

- `none` requires matching declared/observed preconditions.
- `test-tenant-resettable` additionally requires successful app-state and backend/test-data reset
  evidence and matching fingerprints.
- `compensated` additionally requires either that reset evidence or a successful result for the
  plan's declared compensator.
- `non-replayable` rejects automatic replay and repair validation. A distinct
  `manualOneShotAuthorization` context flag can admit one human run, never a repair-eligible run.

The context/checkpoint contract covers app build, app-state seed, backend/test-data seed, route,
window, modal, locale, theme, orientation, display profile, and applicable collection-item key.
Missing or mismatched declared evidence fails admission. A required independent business oracle
must succeed before a run or repair can be represented as verified. The broker workflow-run start
and snapshot contracts carry the additive `plan`, `context`, and `admission` fields; schema-2
manual flow requests remain supported and report `sideEffectPolicy: "unspecified"` with
`repairEligibility: false`.

## Extension discovery

Agents can expose app-specific diagnostics or automation under `/api/v1/ext/{namespace}/...`. Extension namespaces use reverse-domain notation such as `com.example.diagnostics`.

Extensions are discovered through `GET /api/v1/agent/capabilities`. The response includes an `extensions` object keyed by namespace. Each extension descriptor includes:

- `version`: semantic version for the extension descriptor contract
- `description`: human-readable summary
- `tools[]`: self-describing tool descriptors with `name`, `description`, `method`, `path`, optional JSON Schema `parameters`, optional JSON Schema `returns`, and optional behavior `annotations`

`GET /api/v1/agent/status` includes an `extensions` marker with `count` and `hash`. Clients can cache extension descriptors by hash and avoid fetching full capabilities when the marker has not changed.
