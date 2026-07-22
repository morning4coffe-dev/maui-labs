# DevFlow Public Preview Release

DevFlow ships one coordinated preview across four host surfaces:

| Surface | Preview artifact |
|---|---|
| Agent, driver, and supporting libraries | Signed NuGet packages from the official pipeline |
| CLI and MCP | `Microsoft.Maui.Cli` global tool from the official pipeline |
| MAUI DevFlow Inspector for VS Code | Self-contained pre-release VSIX |
| MAUI DevFlow Inspector for GitHub Copilot Canvas | Repo-scoped extension bundle |

The release train is `0.1.0-preview.12`. VS Code requires a numeric manifest version, so its
manifest is `0.1.0` and the VSIX is packaged with VS Code's pre-release flag; the artifact filename
retains the full release-train version.

## Compatibility

Mutation coordination is enforced by current agents:

- A current client can use an older agent; a missing lease endpoint is treated as unsupported.
- An older client cannot mutate a current agent because it does not send a mutation lease.

Upgrade the agent package, CLI, VS Code extension, and Canvas extension together. Read-only calls
remain available when another host owns the lease. If a host is abandoned, its lease expires; an
active app-scoped recording can then be resumed and stopped by the next valid lease holder.

## CI gates

`.github/workflows/ci-devflow.yml` produces:

- Windows DevFlow builds, tests, and NuGet packages;
- TypeScript builds and Node tests;
- Canvas adapter and recorder/replay tests;
- a bundled VSIX with no runtime `node_modules` dependency;
- a Canvas source bundle;
- SHA-256 checksums for host artifacts.

The real-device workflow in `.github/workflows/devflow-integration.yml` remains the release E2E
lane for Android, iOS, Mac Catalyst, and Windows. Before promotion, run the Windows inspector
Playwright suite against the launched sample and exercise VS Code and Canvas manually with the same
agent to verify lease handoff and shared recording.

## Promotion

1. Run the signed Azure DevOps official pipeline and enable the DevFlow/CLI NuGet publish gates.
2. Run `CI - DevFlow` from the exact release commit.
3. Run `DevFlow Integration Tests` via `workflow_dispatch` for all platforms from that same commit.
4. Download `devflow-host-artifacts` from the CI run and complete the packaged VSIX and Canvas
   manual-host checklist below.
5. Invoke `Promote DevFlow Preview Artifacts` with both successful GitHub Actions run IDs, the exact
   commit SHA, the tag `devflow-v0.1.0-preview.12`, and manual-host validation confirmed.
6. The workflow verifies both runs, `SHA256SUMS`, and creates a draft pre-release from the immutable CI
   artifacts. Review and publish the draft after the signed package promotion succeeds.

Do not rebuild host artifacts during promotion.

## Release checklist

- All package and host versions match the release train.
- Source-map and project-path privacy defaults are unchanged.
- Browser, VS Code, Canvas, MCP, and CLI can hand off the global lease.
- Mutations from multiple hosts appear in one broker-owned workflow recording.
- The VSIX installs without the repository or workspace `node_modules`.
- The old user-scoped `maui-live-canvas` extension is removed before testing the repo-scoped Canvas.
- Preview limitations and privacy guidance are linked from product documentation.
- The workflow-dispatched integration run passes Android, iOS, Mac Catalyst, and Windows from the
   exact release commit, including the Windows live Inspector Playwright suite.
- The packaged VSIX and Canvas bundle are exercised manually against the same app for panel startup,
   lease handoff, shared recording, source navigation, event-driven refresh, and reconnect behavior.
