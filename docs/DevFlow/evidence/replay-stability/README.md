# Replay stability

The goal asks for replay stability as one of five metrics. This is the first
measured value, produced by `eng/devflow/Measure-ReplayStability.ps1`.

## Result

| | |
|---|---:|
| Flow | `samples/DevFlow.Sample/maui-tests/verified-add-todo.md` |
| Platform / device | android / `emulator-5554` (`devflow-tests-api35`) |
| Runs | 5 |
| First-attempt pass | **5/5** |
| Verified (independent oracle satisfied) | **5/5** |
| Value | 1.0 |
| Wilson 95% | [0.5655, 1.0] |

Reproduce:

```powershell
.\eng\devflow\Measure-ReplayStability.ps1 -Runs 5
```

Each run does a full build, uninstall, deploy, launch, replay, and evidence
capture into a fresh `--output`, so this measures the whole pipeline, not a
warm in-process replay.

## How to read it honestly

**n = 5 is small.** The Wilson lower bound is 0.57, so this supports "the loop
is not obviously flaky" and does **not** support a claim like "99% stable".
Raise `-Runs` before quoting a number anywhere that matters.

It is also **one flow, one platform, one device**. It says nothing about iOS,
Mac Catalyst, Windows, physical devices, or flows with different timing.

## What it does cover

Every run is `verified: true` — the independent business oracle
(`todo-ledger-record`, which reads the app's private storage over adb, outside
the DevFlow agent channel that drove the flow) agreed each time. A run that
merely passed its UI assertion would report `pass` but `verified: false`.

## Known interference

A concurrent run of the same app on another target can make these runs fail as
`infrastructure-failure` rather than `pass`. During the first attempt at this
measurement a Windows instance of the same sample held the agent port, and
Android was pushed onto a Windows-reserved port; all runs failed. Close other
instances of the app under test before measuring. See
`AndroidFlowExecutionAdapter.DescribeAgentForwardFailure` for the error text
that now explains this case.
