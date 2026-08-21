[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ManifestPath,

    [Parameter(Mandatory)]
    [string] $RepositoryRoot,

    [Parameter(Mandatory)]
    [string[]] $ArtifactRoots,

    [string] $Platform = 'android',
    [string] $RepositoryCommit = $env:DEVFLOW_FLOW_PILOT_REPOSITORY_COMMIT,
    [string] $WorkflowRunId = $env:DEVFLOW_FLOW_PILOT_WORKFLOW_RUN_ID,
    [string] $TestingPackageVersion = 'unknown',
    [string] $PackageId = 'com.companyname.mauitodo',
    [string] $AndroidApiLevel = $env:DEVFLOW_TEST_ANDROID_API,
    [string] $AndroidAvdName = $env:DEVFLOW_TEST_ANDROID_AVD,
    [ValidateSet('physical-device', 'real-device', 'emulator', 'unknown')]
    [string] $DeviceEvidenceKind = 'emulator',
    [switch] $RealDevice
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$maxArtifacts = 256

function New-FallbackManifest {
    param([string] $Reason)

    return [ordered]@{
        schema = 1
        kind = 'devflow-flow-pilot'
        generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
        repository = [ordered]@{ commit = $(if ($RepositoryCommit) { $RepositoryCommit } else { 'unknown' }) }
        workflow = [ordered]@{ runId = $(if ($WorkflowRunId) { $WorkflowRunId } else { 'unknown' }) }
        testing = [ordered]@{ packageVersion = $TestingPackageVersion }
        platform = [ordered]@{
            name = $Platform
            deviceEvidence = [ordered]@{
                kind = $DeviceEvidenceKind
                realDevice = ($RealDevice -and $DeviceEvidenceKind -in @('physical-device', 'real-device'))
            }
            androidSdk = [ordered]@{
                apiLevel = $(if ($AndroidApiLevel) { $AndroidApiLevel } else { 'unknown' })
                avdName = $(if ($AndroidAvdName) { $AndroidAvdName } else { 'unknown' })
            }
        }
        app = [ordered]@{ packageId = $PackageId }
        flows = @()
        artifacts = @()
        privacy = [ordered]@{
            excludedByDefault = @('screenshots', 'source', 'raw-model-context')
        }
        truncated = $false
        truncation = [ordered]@{ maxArtifacts = 256; omittedArtifacts = 0 }
        omissions = @(
            [ordered]@{
                kind = 'pilot-manifest'
                reason = $Reason
            }
        )
        validationErrors = @($Reason)
    }
}

function Get-RepositoryRelativePath {
    param([string] $Path)

    return [System.IO.Path]::GetRelativePath($RepositoryRoot, $Path) -replace '\\', '/'
}

function Get-ArtifactKind {
    param([System.IO.FileInfo] $File)

    switch ($File.Extension.ToLowerInvariant()) {
        '.trx' { return 'test-results' }
        '.mauitrace' { return 'mauitrace' }
        '.json' { return 'json' }
        default { return 'host-diagnostic' }
    }
}

$manifestFull = [System.IO.Path]::GetFullPath($ManifestPath)
$manifestDirectory = Split-Path -Parent $manifestFull
New-Item -ItemType Directory -Force -Path $manifestDirectory | Out-Null

$manifest = $null
if (Test-Path -LiteralPath $manifestFull -PathType Leaf) {
    try {
        $manifest = Get-Content -LiteralPath $manifestFull -Raw | ConvertFrom-Json -AsHashtable
    }
    catch {
        $manifest = New-FallbackManifest 'The test process wrote an unreadable flow-pilot manifest.'
    }
}
else {
    $manifest = New-FallbackManifest 'The test process did not produce a flow-pilot manifest.'
}

if ($null -eq $manifest['artifacts']) {
    $manifest['artifacts'] = @()
}
if ($null -eq $manifest['omissions']) {
    $manifest['omissions'] = @()
}
if ($null -eq $manifest['truncation']) {
    $manifest['truncation'] = [ordered]@{ maxArtifacts = $maxArtifacts; omittedArtifacts = 0 }
}
if ($null -eq $manifest['platform']) {
    $manifest['platform'] = [ordered]@{ name = $Platform }
}
if ($null -eq $manifest['platform']['deviceEvidence']) {
    $manifest['platform']['deviceEvidence'] = [ordered]@{
        kind = $DeviceEvidenceKind
        realDevice = ($RealDevice -and $DeviceEvidenceKind -in @('physical-device', 'real-device'))
    }
}

$existingPaths = @{}
foreach ($artifact in @($manifest['artifacts'])) {
    if ($null -ne $artifact -and $artifact['path']) {
        $existingPaths[[string] $artifact['path']] = $true
    }
}

$artifacts = [System.Collections.Generic.List[object]]::new()
foreach ($artifact in @($manifest['artifacts'])) {
    $artifacts.Add($artifact)
}
$artifactLimitReached = $false

foreach ($artifactRoot in $ArtifactRoots) {
    if ([string]::IsNullOrWhiteSpace($artifactRoot) -or -not (Test-Path -LiteralPath $artifactRoot -PathType Container)) {
        continue
    }

    Get-ChildItem -LiteralPath $artifactRoot -Recurse -File | ForEach-Object {
        if ($_.FullName -eq $manifestFull -or $_.Name.EndsWith('.tmp', [System.StringComparison]::OrdinalIgnoreCase)) {
            return
        }

        $relativePath = Get-RepositoryRelativePath $_.FullName
        if ($relativePath.StartsWith('../', [System.StringComparison]::Ordinal) -or $existingPaths.ContainsKey($relativePath)) {
            return
        }
        if ($artifacts.Count -ge $maxArtifacts) {
            if (-not $artifactLimitReached) {
                $artifactLimitReached = $true
                $manifest['truncated'] = $true
                $manifest['truncation']['maxArtifacts'] = $maxArtifacts
                $manifest['truncation']['omittedArtifacts'] = [int] $manifest['truncation']['omittedArtifacts'] + 1
                $manifest['omissions'] += [ordered]@{
                    kind = 'artifact-limit'
                    reason = "Only the first $maxArtifacts artifact references were hashed."
                }
            }
            return
        }

        $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
        $artifacts.Add([ordered]@{
                kind = Get-ArtifactKind $_
                path = $relativePath
                sha256 = "sha256:$($hash.Hash.ToLowerInvariant())"
                sizeBytes = [Int64] $_.Length
                redacted = $true
            })
        $existingPaths[$relativePath] = $true
    }
}

$manifest['artifacts'] = @($artifacts)

# The test process validates the manifest before this script exists, so it records "at least one
# artifact is required" against a manifest whose artifact references are, by design, gathered here
# afterwards. Left alone that error is permanent and false: the finalized manifest carries the
# artifacts, but the downstream failure handoff refuses any manifest with a non-empty
# validationErrors array, so the CI failure-to-issue path could never start. Only the invariant this
# script has actually satisfied is cleared; every other recorded error is preserved untouched, and
# nothing is cleared when no artifact was found.
if ($artifacts.Count -gt 0 -and $manifest['validationErrors'] -is [System.Collections.IEnumerable]) {
    $manifest['validationErrors'] = @(
        @($manifest['validationErrors']) |
            Where-Object { [string] $_ -ne 'At least one artifact is required.' })
}

$manifest['finalizedAt'] = [DateTimeOffset]::UtcNow.ToString('O')

$temporary = "$manifestFull.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $json = $manifest | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($temporary, $json, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::Move($temporary, $manifestFull, $true)
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Force
    }
}
