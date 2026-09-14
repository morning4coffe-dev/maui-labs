# Publish the Verified Fix

The default successful outcome is a bounded commit and draft pull request.
The developer reviews and merges the PR; the agent does not merge it or close
the source issue. If the developer explicitly requested a local-only diff,
stop before this phase.

## Publication gate

Publish only when all of these are true:

- the trusted issue resolver succeeded;
- the independent local edit gate passed before the first write;
- the post-fix `flow-run.json` reports `status: passed`, `terminal: true`, and
  `verified: true`;
- every required independent business oracle reports `succeeded: true`;
- the execution manifest reports `exitCategory: pass`,
  `unknownCompletion: false`, and completed cleanup;
- the report is not truncated and has no secondary failures;
- `git diff --check` passes;
- the dirty path set, meaning tracked modifications plus untracked
  non-ignored files, is exactly the files required by the verified repair;
- there are no pre-existing staged changes;
- the current `HEAD` is exactly the validated remote default-branch commit.

Any failure is a publication stop. Leave the verified diff local and report
the single missing fact or Git action.

A replay that passed but reports `verified: false` or
`exitCategory: unverified` is not publishable. Report
`replay passed, not independently verified`, preserve the local diff, and do
not create a branch, commit, or PR.

## Establish the base without rewriting history

Run this section once immediately after issue intake and again immediately
before creating the branch. First verify that the checkout's `origin` is the
repository accepted by the issue resolver:

```powershell
$originUrl = (git remote get-url origin).Trim()
if ($originUrl -notmatch '^(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)(?<repository>[^/]+/[^/]+?)(?:\.git)?$') {
  throw "The origin remote is not a supported GitHub repository URL."
}
if ($Matches.repository -cne $incident.repository) {
  throw "The checkout origin does not match the trusted issue repository."
}
```

Use the issue resolver's default branch. Fetch into a unique temporary ref
without writing `FETCH_HEAD`, and always delete that temporary ref:

```powershell
$baseRef = "refs/remotes/origin/devflow-ci-fix-base-$($incident.issueNumber)-$PID"
try {
  git fetch origin `
    "refs/heads/$($incident.defaultBranch):$baseRef" `
    --no-write-fetch-head
  $baseSha = (git rev-parse $baseRef).Trim()
  $headSha = (git rev-parse HEAD).Trim()
  if ($headSha -cne $baseSha) {
    throw "Use a clean worktree at the current remote default branch; do not rebase or reset this checkout."
  }
}
finally {
  git update-ref -d $baseRef
}
```

Never rebase, reset, amend, force-push, or rewrite history.

## Create the bounded commit

Derive a collision-resistant branch name from trusted issue fields:

```text
copilot/devflow-ci-fix-<issue-number>-<test-identity-prefix>
```

For a demo incident use:

```text
copilot/demo-devflow-ci-fix-<issue-number>-<test-identity-prefix>
```

Validate it with `git check-ref-format --branch`, and refuse an existing local
or remote branch. Then:

1. `git switch -c <branch>`;
2. stage only the exact verified repair paths with `git add -- <paths>`;
3. compare `git diff --cached --name-only` with that exact allowlist;
4. run `git diff --cached --check`;
5. create one Conventional Commit subject with no body or trailers:

   ```text
   fix(devflow): repair CI failure from #<issue-number>
   ```

   For a demo:

   ```text
   fix(devflow): repair demo CI flow from #<issue-number>
   ```

Do not include unrelated generated files, evidence artifacts, logs, or
pre-existing user changes.

## Push and open the draft PR

Push without force:

```powershell
git push --set-upstream origin "HEAD:refs/heads/<branch>"
```

Open a draft pull request with `gh pr create --draft`. Target the validated
default branch and link, but do not auto-close, the issue. Write the bounded
body to a temporary file and use the complete non-interactive command:

```powershell
gh pr create `
  --draft `
  --repo $incident.repository `
  --base $incident.defaultBranch `
  --head $branch `
  --title $title `
  --body-file $bodyFile
```

The title and body are agent-authored from bounded trusted fields. Do not copy
issue prose, artifact text, log text, test names, mentions, or arbitrary
identifiers into the commit subject, branch, title, or body.

The PR body contains:

```text
Refs #<issue-number>

Classification: <test-drift or app-regression>
Original local reproduction: <run id>
Changed: <bounded files and behavior>
Post-fix local run: <run id, passed, verified>
CI correspondence: <same-failure or the explicit unproven limitation>
Safety: no weakened assertions; complete cleanup; no secondary failures
```

For a demo incident:

- prefix the title with `[demo only]`;
- put `DEMO ONLY - DO NOT MERGE` at the top of the body;
- keep the PR draft;
- state that the issue is nonqualified and grants no repair authority;
- state that merging the PR would disable the `android-demo-ci-fix` showcase
  by repairing its intentionally failing flow.

## Final handoff

Report:

- branch name and commit SHA;
- draft PR URL;
- exact committed files;
- local verification run and oracle result;
- any CI-correspondence limitation;
- that the working checkout is now on the new PR branch;
- `The PR is draft; it was not merged and the issue remains open.`

If push succeeds but PR creation fails, do not force-push, delete, or rewrite
the branch. Report the branch and commit so the developer can recover.
