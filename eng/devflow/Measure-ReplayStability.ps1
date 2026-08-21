# Measures replay stability: N consecutive runs of a known-green flow.
# Reports first-attempt pass rate with a Wilson 95% interval.
param(
    [int] $Runs = 5,
    [string] $Flow = 'samples\DevFlow.Sample\maui-tests\verified-add-todo.md',
    [string] $Device = 'emulator-5554'
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $repo

$maui = Join-Path $repo 'artifacts\bin\Microsoft.Maui.Cli\Debug\net10.0\maui.exe'
$adb = Join-Path $env:USERPROFILE '.maui\android-sdk\platform-tools\adb.exe'
$app = 'samples\DevFlow.Sample\DevFlow.Sample.csproj'
$pkg = 'com.companyname.mauitodo'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$root = "artifacts\devflow\stability-$stamp"

$results = @()
for ($i = 1; $i -le $Runs; $i++) {
    Write-Host ''
    Write-Host "--- run $i of $Runs ---" -ForegroundColor Cyan
    & $adb uninstall $pkg 2>&1 | Out-Null
    $out = Join-Path $root "run$i"
    $raw = & $maui devflow flow run $Flow --project $app --platform android `
        --device $Device --output $out --json 2>&1 | Out-String
    $ok = $null; $cat = $null; $verified = $null
    try {
        $j = $raw | ConvertFrom-Json
        $ok = $j.ok; $cat = $j.exitCategory
    } catch { }
    $report = Join-Path $out 'flow-run.json'
    if (Test-Path $report) {
        try { $verified = (Get-Content $report -Raw | ConvertFrom-Json).outcome.verified } catch { }
    }
    Write-Host ("  ok={0} exitCategory={1} verified={2}" -f $ok, $cat, $verified)
    $results += [pscustomobject]@{ Run = $i; Ok = $ok; ExitCategory = $cat; Verified = $verified }
}

$n = $results.Count
$passes = ($results | Where-Object { $_.ExitCategory -eq 'pass' }).Count
$verifiedCount = ($results | Where-Object { $_.Verified -eq $true }).Count

function WilsonInterval {
    param([int] $Successes, [int] $Total)
    if ($Total -eq 0) { return @(0, 0) }
    $z = 1.959963985
    $p = $Successes / $Total
    $d = 1 + ($z * $z / $Total)
    $c = $p + ($z * $z / (2 * $Total))
    $m = $z * [Math]::Sqrt(($p * (1 - $p) / $Total) + ($z * $z / (4 * $Total * $Total)))
    return @([Math]::Round(($c - $m) / $d, 4), [Math]::Round(($c + $m) / $d, 4))
}

$ci = WilsonInterval -Successes $passes -Total $n
Write-Host ''
Write-Host '=========================================================' -ForegroundColor DarkCyan
Write-Host '  REPLAY STABILITY' -ForegroundColor Cyan
Write-Host '=========================================================' -ForegroundColor DarkCyan
$results | Format-Table -AutoSize | Out-String -Width 120 | Write-Host
Write-Host ("  first-attempt pass : {0}/{1}" -f $passes, $n)
Write-Host ("  verified           : {0}/{1}" -f $verifiedCount, $n)
Write-Host ("  wilson 95%         : [{0}, {1}]" -f $ci[0], $ci[1])
Write-Host ("  artifacts          : {0}" -f $root)

$summary = [pscustomobject]@{
    metric              = 'replay-stability'
    flow                = $Flow
    platform            = 'android'
    device              = $Device
    runs                = $n
    firstAttemptPasses  = $passes
    verifiedRuns        = $verifiedCount
    value               = if ($n) { [Math]::Round($passes / $n, 4) } else { $null }
    confidenceInterval  = @{ method = 'wilson-95'; lower = $ci[0]; upper = $ci[1] }
    measuredAtUtc       = (Get-Date).ToUniversalTime().ToString('o')
    runs_detail         = $results
}
$summaryPath = Join-Path $root 'replay-stability.json'
$summary | ConvertTo-Json -Depth 6 | Set-Content $summaryPath
Write-Host ("  summary            : {0}" -f $summaryPath)
