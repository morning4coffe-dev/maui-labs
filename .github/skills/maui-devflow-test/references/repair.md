# Selector Repair Boundary

Repair is a human Workbench lifecycle, not an agent action. The restricted
`maui_test_patch` tool can retain an inert selector proposal, preview it, or
reject it. `approve`, `apply`, and rollback requests fail closed.

## Eligibility Gate

Discuss a proposal only after all of these are true:

- The failure is a primary, pre-dispatch `locator-not-found`.
- It came from a current local run, or an imported failure has a fresh matching
  local reproduction.
- The recorded checkpoint matches current build, instance, reset/seed, route,
  window, modal, locale, theme, orientation, display profile, and collection
  key.
- The durable selector-health shortlist yields exactly one current candidate
  with a matching value-free semantic fingerprint.
- Hard assertions remain unchanged and a required independent business oracle
  is available for validation.

Reject repair for ambiguity, wrong route, assertion failure, unknown completion,
capability/infrastructure failure, stale source, platform divergence, data or
secret errors, coordinates, runtime IDs, type/index selectors, visible-text
fallbacks, duplicate IDs, and unscoped virtualized rows.

## Human Ceremony

1. Store or preview one inert selector proposal with `maui_test_patch`.
2. The human Workbench reviews the rationale, score, risks, and evidence.
3. The human issues a bounded validation grant. The platform lifecycle performs
   a hard reset and in-memory override replay; it does not commit a flow.
4. The human separately approves a selector-only compare-and-swap apply bound
   to the proposal, flow digest/revision, target, and expiry.
5. The host performs the clean reset/oracle verification replays or enters
   rollback-required.

The agent does not issue grants, apply the patch, alter actions/order/assertions,
or change application source. When a durable selector requires a new
`AutomationId` or stable item key, make a separate testability recommendation.
