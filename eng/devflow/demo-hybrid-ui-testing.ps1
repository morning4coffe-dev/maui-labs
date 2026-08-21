# MAUI DevFlow - hybrid, agent-led UI testing: end-to-end demo
# Run:  pwsh -NoExit -File .\eng\devflow\demo-hybrid-ui-testing.ps1
[CmdletBinding()]
param(
    [switch] $SkipDevice,
    [string] $Avd = 'devflow-tests-api35',
    [string] $Device = 'emulator-5554'
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $repo

$maui = Join-Path $repo 'artifacts\bin\Microsoft.Maui.Cli\Debug\net10.0\maui.exe'
$sdk = Join-Path $env:USERPROFILE '.maui\android-sdk'
$adb = Join-Path $sdk 'platform-tools\adb.exe'
$tests = 'samples\DevFlow.Sample\maui-tests'
$app = 'samples\DevFlow.Sample\DevFlow.Sample.csproj'
$pkg = 'com.companyname.mauitodo'

function Step {
    param([string] $Number, [string] $Title, [string] $Why)
    Write-Host ''
    Write-Host ('=' * 100) -ForegroundColor DarkCyan
    Write-Host "  SCENARIO $Number  $Title" -ForegroundColor Cyan
    if ($Why) { Write-Host "  $Why" -ForegroundColor DarkGray }
    Write-Host ('=' * 100) -ForegroundColor DarkCyan
}

function Run {
    param([string] $Display, [scriptblock] $Action)
    Write-Host ''
    Write-Host "PS> $Display" -ForegroundColor Yellow
    & $Action
}

Clear-Host
Write-Host ''
Write-Host '  MAUI DevFlow - hybrid, agent-led, self-repairing UI testing' -ForegroundColor White
Write-Host '  ---------------------------------------------------------' -ForegroundColor DarkGray
Write-Host "  repo   : $repo" -ForegroundColor DarkGray
Write-Host "  branch : $(git rev-parse --abbrev-ref HEAD)" -ForegroundColor DarkGray
Write-Host "  cli    : $maui" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
Step '1' 'A test is a committed artifact' 'Plain markdown + a fenced json block. Human-readable, agent-writable, git-reviewable.'
Run "Get-Content $tests\verified-add-todo.md" { Get-Content "$tests\verified-add-todo.md" }

# ---------------------------------------------------------------------------
Step '2' 'The skills and agents that drive the loop' 'Only .github/skills and .github/agents are visible to a GitHub-hosted agent.'
Run 'Get-ChildItem .github\skills -Directory' { Get-ChildItem '.github\skills' -Directory | Select-Object -ExpandProperty Name }
Run 'Get-ChildItem .github\agents -File' { Get-ChildItem '.github\agents' -File | Select-Object -ExpandProperty Name }
Write-Host ''
Write-Host '  maui-devflow-ci-triage  -> analyses a failed UI test from CI evidence' -ForegroundColor DarkGray
Write-Host '  devflow-ci-repair       -> proposes a reviewable repair PR from a CI issue' -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
Step '3' 'Compute the CI test identity' 'A CI issue carries no test name - only this digest. This is how it is produced.'
Run "& maui devflow flow identity $tests\verified-add-todo.md --platform android --json" {
    & $maui devflow flow identity "$tests\verified-add-todo.md" --platform android --json
}

$identity = $null
try {
    $j = & $maui devflow flow identity "$tests\verified-add-todo.md" --platform android --json | ConvertFrom-Json
    $identity = $j.flows[0].identities[0].testIdentitySha256
} catch { }

# ---------------------------------------------------------------------------
Step '4' 'Resolve the identity back to the failing test' 'The step that makes a scrubbed CI issue actionable.'
if ($identity) {
    Run "& maui devflow flow identity --resolve $identity --platform android --search $tests --json" {
        & $maui devflow flow identity --resolve $identity --platform android --search $tests --json
    }
} else {
    Write-Host '  (skipped - identity not computed)' -ForegroundColor Red
}

# ---------------------------------------------------------------------------
Step '5' 'Detect that the test drifted since the run' 'Edit the flow, then resolve the ORIGINAL identity again.'
$flowFile = "$tests\verified-add-todo.md"
$backup = Get-Content $flowFile -Raw
try {
    (Get-Content $flowFile -Raw).Replace('"automationId": "AddButton"', '"automationId": "AddTodoButton"') |
        Set-Content $flowFile -NoNewline
    Write-Host ''
    Write-Host '  (renamed AddButton -> AddTodoButton in the committed flow)' -ForegroundColor DarkGray
    if ($identity) {
        Run "& maui devflow flow identity --resolve $identity --platform android --search $tests --json" {
            & $maui devflow flow identity --resolve $identity --platform android --search $tests --json
        }
    }
} finally {
    Set-Content $flowFile -Value $backup -NoNewline
    Write-Host ''
    Write-Host '  (flow restored)' -ForegroundColor DarkGray
    Run 'git status --porcelain' { $s = git status --porcelain; if ($s) { $s } else { 'clean' } }
}

if ($SkipDevice) {
    Write-Host ''
    Write-Host '  -SkipDevice set: stopping before the device scenarios.' -ForegroundColor Yellow
    return
}

# ---------------------------------------------------------------------------
Step '6' 'Run the verified test on a device' 'Expect ok:true - it carries an INDEPENDENT oracle, not just a UI assertion.'
Run "& adb wait-for-device; uninstall $pkg" {
    & $adb wait-for-device
    while ((& $adb shell getprop sys.boot_completed 2>$null).Trim() -ne '1') { Start-Sleep 3 }
    & $adb uninstall $pkg 2>&1 | Out-Host
}
$out1 = "artifacts\devflow\demo-verified-$(Get-Date -Format 'HHmmss')"
Run "& maui devflow flow run $tests\verified-add-todo.md --platform android --device $Device --output $out1 --json" {
    & $maui devflow flow run "$tests\verified-add-todo.md" --project $app --platform android --device $Device --output $out1 --json
}

# ---------------------------------------------------------------------------
Step '7' 'Run a drifted test and see it classified' 'A renamed selector must be called test drift, NOT an app regression.'
Run "& adb uninstall $pkg" { & $adb uninstall $pkg 2>&1 | Out-Host }
$out2 = "artifacts\devflow\demo-drift-$(Get-Date -Format 'HHmmss')"
Run "& maui devflow flow run $tests\drifted-add-todo.md --platform android --device $Device --output $out2 --json" {
    & $maui devflow flow run "$tests\drifted-add-todo.md" --project $app --platform android --device $Device --output $out2 --json
}
$report = Join-Path $out2 'flow-run.json'
if (Test-Path $report) {
    Run "disposition from $report" {
        $r = Get-Content $report -Raw | ConvertFrom-Json
        [pscustomobject]@{
            outcome     = $r.outcome
            failureClass= $r.failure.class
            category    = $r.failure.category
            disposition = $r.triage.disposition
            evidence    = $r.triage.evidence
        } | Format-List
    }
}

# ---------------------------------------------------------------------------
Step '8' 'Try to reproduce the failure locally' 'Expect matched:false. That is CORRECT and is the open design question.'
$out3 = "artifacts\devflow\demo-repro-$(Get-Date -Format 'HHmmss')"
if (Test-Path $report) {
    Run "& maui devflow flow reproduce $tests\drifted-add-todo.md --import $report --output $out3 --json" {
        & $maui devflow flow reproduce "$tests\drifted-add-todo.md" `
            --project $app --platform android --device $Device `
            --import $report --output $out3 --json
    }
}
Write-Host ''
Write-Host '  Behaviour DOES reproduce: step + runtime fingerprints match exactly.' -ForegroundColor DarkGray
Write-Host '  Android cannot emit a byte-identical APK across builds (ZIP timestamps,' -ForegroundColor DarkGray
Write-Host '  Kotlin metadata), so package identity blocks the match. Open question:' -ForegroundColor DarkGray
Write-Host '  should matched require package bytes, or source + behavioural identity?' -ForegroundColor DarkGray

Write-Host ''
Write-Host ('=' * 100) -ForegroundColor DarkCyan
Write-Host '  demo complete' -ForegroundColor Green
Write-Host ('=' * 100) -ForegroundColor DarkCyan
