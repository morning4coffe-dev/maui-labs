[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $CliArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ExitSuccess = 0
$ExitUsage = 2
$ExitPrerequisite = 3
$ExitFlowFailure = 4
$ExitPending = 5
$MaxArtifactRecords = 256
$MaxDiagnosticCharacters = 65536

function Write-Usage {
    @"
Usage:
  Run-DevFlowFlowQa.ps1 --platform android|windows|ios|maccatalyst|macos `
    --results-root <repo>/artifacts/TestResults/devflow-flow/<platform> [options]

Required:
  --platform <name>       android, windows, ios, maccatalyst, or macos
  --results-root <path>   Exact repository-local results directory for the selected platform

Options:
  --repeat <N>            Clean repetitions (default: 3; maximum: 20)
  --configuration <name>  Test configuration (default: Debug)
  --flow-filter <filter>  Additional VSTest filter appended to the platform filter
  --no-build              Pass --no-build to dotnet test
  --qualification         Run the read-only qualification evaluator after the flow host
  --experimental          Required for the experimental AppKit/macOS lane
  --physical-device       Run the separately identified physical-iOS lane
  --device-id <id>        Android serial or required physical-iOS device identifier
  --ios-runtime <version> iOS Simulator runtime selector (for ios simulator only)
  --signing-identity <id> Physical-iOS signing identity; never written to artifacts
  --provisioning-profile <id>
                           Physical-iOS provisioning profile; never written to artifacts
  --keychain <path>       Physical-iOS keychain reference; never written to artifacts
  --verbosity <level>     quiet, minimal, normal, detailed, or diagnostic
  --verbose               Alias for --verbosity detailed
  --dry-run               Validate arguments and emit the planned, non-executing command as JSON
  --help                  Show this help text

Exit codes: 0 succeeded, 2 invalid invocation, 3 prerequisite/infrastructure failure,
4 flow failure, 5 pending capability or not-qualified result.
"@
}

function Exit-Usage {
    param([Parameter(Mandatory)][string] $Message)

    [Console]::Error.WriteLine("flow-qa: $Message")
    [Console]::Error.WriteLine((Write-Usage))
    exit $ExitUsage
}

function Get-RequiredValue {
    param(
        [Parameter(Mandatory)][string] $Option,
        [Parameter(Mandatory)][int] $Index,
        [Parameter(Mandatory)][string[]] $Tokens
    )

    if ($Index + 1 -ge $Tokens.Count -or
        [string]::IsNullOrWhiteSpace($Tokens[$Index + 1]) -or
        $Tokens[$Index + 1].StartsWith('-')) {
        Exit-Usage "$Option requires a value."
    }

    $Tokens[$Index + 1]
}

function Test-OptionValue {
    param(
        [string] $Value,
        [string] $Name
    )

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -match "[`r`n]") {
        Exit-Usage "$Name must be a single non-empty line."
    }
}

function Test-UnsafePath {
    param([Parameter(Mandatory)][string] $Path)

    if ($Path.IndexOfAny([char[]] '*?[]') -ge 0 -or
        $Path -match '(^|[\\/])\.\.($|[\\/])') {
        Exit-Usage '--results-root must not contain wildcards or parent-directory segments.'
    }
}

function Get-PathComparison {
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)) {
        return [System.StringComparison]::OrdinalIgnoreCase
    }

    return [System.StringComparison]::Ordinal
}

function Get-CanonicalPath {
    param([Parameter(Mandatory)][string] $Path)

    [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]] @('\', '/'))
}

function Assert-ExistingPathIsNotReparsePoint {
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string] $Path
    )

    $relative = [System.IO.Path]::GetRelativePath($RepositoryRoot, $Path)
    if ($relative.StartsWith('..', [System.StringComparison]::Ordinal) -or
        [System.IO.Path]::IsPathRooted($relative)) {
        Exit-Usage 'The output path must remain inside the repository.'
    }

    $current = $RepositoryRoot
    foreach ($segment in $relative -split '[\\/]') {
        if ([string]::IsNullOrWhiteSpace($segment)) {
            continue
        }

        $current = Join-Path $current $segment
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                Exit-Usage 'The output path must not traverse a symbolic link or reparse point.'
            }
        }
    }
}

function Resolve-ResultsRoot {
    param(
        [Parameter(Mandatory)][string] $InputPath,
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string] $Platform
    )

    Test-UnsafePath $InputPath
    $expected = Get-CanonicalPath (Join-Path $RepositoryRoot "artifacts/TestResults/devflow-flow/$Platform")
    $candidate = if ([System.IO.Path]::IsPathRooted($InputPath)) {
        Get-CanonicalPath $InputPath
    }
    else {
        Get-CanonicalPath (Join-Path $RepositoryRoot $InputPath)
    }

    if (-not [string]::Equals($candidate, $expected, (Get-PathComparison))) {
        Exit-Usage "--results-root must resolve exactly to '$expected' for platform '$Platform'."
    }

    Assert-ExistingPathIsNotReparsePoint -RepositoryRoot $RepositoryRoot -Path $candidate
    $candidate
}

function Resolve-ArtifactRoot {
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string] $Platform,
        [Parameter(Mandatory)][string] $RunId
    )

    $expected = Get-CanonicalPath (Join-Path $RepositoryRoot "artifacts/devflow/$RunId/$Platform")
    $configured = if ($Platform -eq 'android') {
        $env:DEVFLOW_FLOW_PILOT_ARTIFACT_ROOT
    }
    else {
        $env:DEVFLOW_FLOW_QA_ARTIFACT_ROOT
    }

    if (-not [string]::IsNullOrWhiteSpace($configured)) {
        Test-UnsafePath $configured
        $candidate = if ([System.IO.Path]::IsPathRooted($configured)) {
            Get-CanonicalPath $configured
        }
        else {
            Get-CanonicalPath (Join-Path $RepositoryRoot $configured)
        }

        if (-not [string]::Equals($candidate, $expected, (Get-PathComparison))) {
            Exit-Usage "The configured artifact root must resolve exactly to '$expected'."
        }
    }

    Assert-ExistingPathIsNotReparsePoint -RepositoryRoot $RepositoryRoot -Path $expected
    $expected
}

function Get-RepositoryRelativePath {
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string] $Path
    )

    ([System.IO.Path]::GetRelativePath($RepositoryRoot, $Path) -replace '\\', '/')
}

function Get-RunId {
    $configured = $env:DEVFLOW_FLOW_QA_RUN_ID
    if (-not [string]::IsNullOrWhiteSpace($configured)) {
        if ($configured -notmatch '^[A-Za-z0-9._-]+$') {
            Exit-Usage 'DEVFLOW_FLOW_QA_RUN_ID may contain only letters, digits, dot, underscore, and hyphen.'
        }

        return $configured
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_RUN_ID)) {
        if ([string]::IsNullOrWhiteSpace($env:GITHUB_RUN_ATTEMPT)) {
            return $env:GITHUB_RUN_ID
        }

        return "$($env:GITHUB_RUN_ID)-$($env:GITHUB_RUN_ATTEMPT)"
    }

    "local-$([DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmss'))-$PID"
}

function Get-RepositoryCommit {
    param([Parameter(Mandatory)][string] $RepositoryRoot)

    try {
        $commit = (& git -C $RepositoryRoot rev-parse HEAD 2>$null | Select-Object -First 1).Trim()
        if (-not [string]::IsNullOrWhiteSpace($commit)) {
            return $commit
        }
    }
    catch {
    }

    'unknown'
}

function Get-FileDigest {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    try {
        "sha256:$((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant())"
    }
    catch {
        $null
    }
}

function Get-FlowDigests {
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string] $Platform
    )

    $flowDirectory = if ($Platform -eq 'macos') {
        Join-Path $RepositoryRoot 'samples/DevFlow.Sample.MacOS/maui-tests'
    }
    else {
        Join-Path $RepositoryRoot 'samples/DevFlow.Sample/maui-tests'
    }
    if (-not (Test-Path -LiteralPath $flowDirectory -PathType Container)) {
        return @()
    }

    @(
        Get-ChildItem -LiteralPath $flowDirectory -Filter '*.md' -File |
            Where-Object { $_.Name -ne 'README.md' } |
            Sort-Object FullName |
            ForEach-Object {
                [ordered]@{
                    path = Get-RepositoryRelativePath -RepositoryRoot $RepositoryRoot -Path $_.FullName
                    sha256 = Get-FileDigest $_.FullName
                }
            })
}

function Get-ArtifactKind {
    param([Parameter(Mandatory)][System.IO.FileInfo] $File)

    switch ($File.Extension.ToLowerInvariant()) {
        '.trx' { 'test-results'; break }
        '.mauitrace' { 'mauitrace'; break }
        '.json' { 'json'; break }
        default { 'host-diagnostic'; break }
    }
}

function Get-ArtifactRecords {
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string[]] $Roots,
        [Parameter(Mandatory)][string] $ResultsRoot,
        [Parameter(Mandatory)][string] $RunId
    )

    $records = [System.Collections.Generic.List[object]]::new()
    $omissions = [System.Collections.Generic.List[object]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($root in $Roots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            continue
        }

        foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue) {
            if ($file.Name.EndsWith('.tmp', [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            if ([string]::Equals($root, $ResultsRoot, (Get-PathComparison)) -and
                $file.Name -notlike "*$RunId*") {
                continue
            }

            $relative = Get-RepositoryRelativePath -RepositoryRoot $RepositoryRoot -Path $file.FullName
            if ($relative.StartsWith('../', [System.StringComparison]::Ordinal) -or -not $seen.Add($relative)) {
                continue
            }

            if ($records.Count -ge $MaxArtifactRecords) {
                $omissions.Add([ordered]@{
                    kind = 'artifact-limit'
                    reason = "Only the first $MaxArtifactRecords artifact references were hashed."
                })
                break
            }

            $digest = Get-FileDigest $file.FullName
            if ($null -eq $digest) {
                $omissions.Add([ordered]@{
                    kind = 'artifact-hash'
                    reason = 'An artifact could not be hashed.'
                    path = $relative
                })
                continue
            }

            $records.Add([ordered]@{
                kind = Get-ArtifactKind $file
                path = $relative
                sha256 = $digest
                sizeBytes = [Int64] $file.Length
                redacted = $true
            })
        }
    }

    [ordered]@{
        records = @($records)
        omissions = @($omissions)
    }
}

function Protect-DiagnosticText {
    param([AllowNull()][string] $Text)

    if ($null -eq $Text) {
        return ''
    }

    $redacted = $Text -replace '(?i)\b(token|password|secret|authorization|api[_-]?key)\s*([:=])\s*\S+', '$1$2[REDACTED]'
    $redacted -replace '(?i)(DEVFLOW_IOS_(?:SIGNING_IDENTITY|PROVISIONING_PROFILE|KEYCHAIN))\s*([:=])\s*\S+', '$1$2[REDACTED]'
}

function Write-AtomicJson {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)] $Value
    )

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $json = $Value | ConvertTo-Json -Depth 32
        [System.IO.File]::WriteAllText($temporary, $json, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::Move($temporary, $Path, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Invoke-RecordedCommand {
    param(
        [Parameter(Mandatory)][string] $FileName,
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][string] $DiagnosticPath
    )

    $output = [System.Collections.Generic.List[string]]::new()
    $characterCount = 0
    $exitCode = 1
    try {
        & $FileName @Arguments 2>&1 | ForEach-Object {
            $line = Protect-DiagnosticText ([string] $_)
            if ($output.Count -lt 1000 -and $characterCount -lt $MaxDiagnosticCharacters) {
                $output.Add($line)
                $characterCount += $line.Length + [Environment]::NewLine.Length
            }
        }
        $exitCode = $LASTEXITCODE
    }
    catch {
        $output.Add((Protect-DiagnosticText $_.Exception.Message))
        $exitCode = 1
    }

    $text = ($output -join [Environment]::NewLine)
    if ($text.Length -gt $MaxDiagnosticCharacters) {
        $text = $text.Substring(0, $MaxDiagnosticCharacters) + [Environment]::NewLine + '[truncated]'
    }

    $directory = Split-Path -Parent $DiagnosticPath
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    [System.IO.File]::WriteAllText($DiagnosticPath, $text, [System.Text.UTF8Encoding]::new($false))
    [ordered]@{
        exitCode = $exitCode
        output = $text
        diagnosticPath = $DiagnosticPath
    }
}

function Get-ExecutionClassification {
    param(
        [int] $ExitCode,
        [string] $Output
    )

    if ($ExitCode -eq 0) {
        return 'passed'
    }

    if ($Output -match '(?i)\bcapability-missing\b') {
        return 'capability-missing'
    }

    if ($Output -match '(?i)\b(workload|sdk .*not found|adb .*not found|xcrun .*not found|simctl|emulator|agent readiness|fixture.*initializ|infrastructure|device.*not found|timed out|timeout)\b') {
        return 'infrastructure-failure'
    }

    'flow-failure'
}

function Get-HostMetadata {
    param(
        [Parameter(Mandatory)][string] $Platform,
        [string] $IosRuntime,
        [bool] $PhysicalDevice,
        [string] $DeviceId
    )

    $hostOs = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    $dotnetSdk = 'unknown'
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        try {
            $dotnetSdk = (& dotnet --version 2>$null | Select-Object -First 1).Trim()
        }
        catch {
        }
    }

    $xcode = 'not-applicable'
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::OSX)) {
        $xcode = 'unavailable'
        if (Get-Command xcodebuild -ErrorAction SilentlyContinue) {
            try {
                $xcode = ((& xcodebuild -version 2>$null) -join '; ').Trim()
            }
            catch {
            }
        }
    }

    [ordered]@{
        hostOs = $hostOs
        dotnetSdk = $dotnetSdk
        workloadVersion = if ($env:DOTNET_WORKLOAD_VERSION) { $env:DOTNET_WORKLOAD_VERSION } else { 'unknown' }
        xcode = $xcode
        runtime = if ($IosRuntime) { $IosRuntime } else { 'default' }
        deviceEvidence = [ordered]@{
            kind = if ($PhysicalDevice) { 'physical-device' } elseif ($Platform -eq 'android') { 'emulator' } elseif ($Platform -eq 'ios') { 'simulator' } else { 'desktop-host' }
            realDevice = $PhysicalDevice
            deviceIdFingerprint = if ($DeviceId) { Get-StringDigest $DeviceId } else { $null }
            profile = 'not-observed'
        }
    }
}

function Get-WindowsDesktopSessionAdmission {
    $timestamp = [DateTimeOffset]::UtcNow.ToString('O')
    $result = [ordered]@{
        schema = 1
        kind = 'devflow-windows-desktop-session'
        sessionId = $null
        wtsConnectionState = 'unavailable'
        desktopLockState = 'unavailable'
        admissionResult = 'unavailable'
        admissionTimestampUtc = $timestamp
        reason = 'windows-host-required'
    }

    # This fail-only hook makes the preflight testable without ever allowing an environment
    # variable to bypass the native WTS check.
    $forcedFailure = $env:DEVFLOW_WINDOWS_SESSION_PREFLIGHT_TEST_STATE
    if ($forcedFailure -eq 'disconnected') {
        try {
            $result['sessionId'] = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
        }
        catch {
        }
        $result['wtsConnectionState'] = 'disconnected'
        $result['admissionResult'] = 'rejected'
        $result['reason'] = 'wts-connection-state-disconnected'
        return [pscustomobject] $result
    }
    if (-not [string]::IsNullOrWhiteSpace($forcedFailure)) {
        $result['reason'] = 'wts-connection-state-unavailable'
        return [pscustomobject] $result
    }

    if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)) {
        return [pscustomobject] $result
    }

    try {
        $sessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
        $result['sessionId'] = $sessionId
    }
    catch {
        $result['reason'] = 'current-process-session-unavailable'
        return [pscustomobject] $result
    }

    if ($sessionId -lt 0) {
        $result['sessionId'] = $null
        $result['reason'] = 'current-process-session-unavailable'
        return [pscustomobject] $result
    }
    if ($sessionId -eq 0) {
        $result['admissionResult'] = 'rejected'
        $result['reason'] = 'session-zero-not-desktop'
        return [pscustomobject] $result
    }

    try {
        if ($null -eq ('DevFlow.FlowQa.WindowsSessionNative' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace DevFlow.FlowQa
{
    public static class WindowsSessionNative
    {
        const int WTSConnectState = 8;
        const int WTSSessionInfoEx = 25;

        public static int QueryConnectionState(int sessionId)
        {
            return QueryInt32(sessionId, WTSConnectState, 0);
        }

        public static int QuerySessionFlags(int sessionId)
        {
            return QueryInt32(sessionId, WTSSessionInfoEx, 12);
        }

        static int QueryInt32(int sessionId, int informationClass, int offset)
        {
            IntPtr buffer = IntPtr.Zero;
            int bytesReturned = 0;
            try
            {
                if (!WTSQuerySessionInformation(
                        IntPtr.Zero,
                        sessionId,
                        informationClass,
                        out buffer,
                        out bytesReturned) ||
                    buffer == IntPtr.Zero ||
                    bytesReturned < offset + sizeof(int))
                {
                    return -1;
                }

                if (informationClass == WTSSessionInfoEx &&
                    (Marshal.ReadInt32(buffer, 0) != 1 ||
                     Marshal.ReadInt32(buffer, sizeof(int)) != sessionId))
                {
                    return -1;
                }

                return Marshal.ReadInt32(buffer, offset);
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    WTSFreeMemory(buffer);
                }
            }
        }

        [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool WTSQuerySessionInformation(
            IntPtr hServer,
            int sessionId,
            int wtsInfoClass,
            out IntPtr ppBuffer,
            out int pBytesReturned);

        [DllImport("Wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr memory);
    }
}
'@
        }

        $connectionState = [DevFlow.FlowQa.WindowsSessionNative]::QueryConnectionState($sessionId)
    }
    catch {
        $result['reason'] = 'wts-connection-state-unavailable'
        return [pscustomobject] $result
    }

    $connectionStateName = switch ($connectionState) {
        0 { 'active'; break }
        1 { 'connected'; break }
        2 { 'connect-query'; break }
        3 { 'shadow'; break }
        4 { 'disconnected'; break }
        5 { 'idle'; break }
        6 { 'listen'; break }
        7 { 'reset'; break }
        8 { 'down'; break }
        9 { 'init'; break }
        default { $null }
    }
    if ($null -eq $connectionStateName) {
        $result['reason'] = 'wts-connection-state-unavailable'
        return [pscustomobject] $result
    }

    $result['wtsConnectionState'] = $connectionStateName
    if ($connectionStateName -ne 'active') {
        $result['admissionResult'] = 'rejected'
        $result['reason'] = "wts-connection-state-$connectionStateName"
        return [pscustomobject] $result
    }

    try {
        $sessionFlags = [DevFlow.FlowQa.WindowsSessionNative]::QuerySessionFlags($sessionId)
    }
    catch {
        $result['reason'] = 'desktop-lock-state-unavailable'
        return [pscustomobject] $result
    }

    switch ($sessionFlags) {
        1 {
            $result['desktopLockState'] = 'unlocked'
            $result['admissionResult'] = 'allowed'
            $result['reason'] = 'active-unlocked-desktop'
            break
        }
        0 {
            $result['desktopLockState'] = 'locked'
            $result['admissionResult'] = 'rejected'
            $result['reason'] = 'desktop-locked'
            break
        }
        default {
            $result['reason'] = 'desktop-lock-state-unavailable'
            break
        }
    }

    [pscustomobject] $result
}

function Get-StringDigest {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    "sha256:$([System.Convert]::ToHexString($hash).ToLowerInvariant())"
}

function Write-HostDiagnostics {
    param(
        [Parameter(Mandatory)][string] $Directory,
        [Parameter(Mandatory)] $Metadata,
        [Parameter(Mandatory)][string] $Status,
        [Parameter(Mandatory)][string] $Classification
    )

    Write-AtomicJson -Path (Join-Path $Directory 'summary.json') -Value ([ordered]@{
            schema = 1
            kind = 'devflow-flow-qa-host-diagnostics'
            generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
            status = $Status
            classification = $Classification
            host = $Metadata
        })
}

function Write-WindowsSessionDiagnostic {
    param(
        [Parameter(Mandatory)][string] $Directory,
        [Parameter(Mandatory)] $Admission
    )

    Write-AtomicJson -Path (Join-Path $Directory 'windows-session.json') -Value ([ordered]@{
            schema = 1
            kind = 'devflow-windows-desktop-session'
            sessionId = $Admission.sessionId
            wtsConnectionState = $Admission.wtsConnectionState
            desktopLockState = $Admission.desktopLockState
            admissionResult = $Admission.admissionResult
            admissionTimestampUtc = $Admission.admissionTimestampUtc
            reason = $Admission.reason
        })
}

function Write-GenericManifest {
    param(
        [Parameter(Mandatory)][string] $ManifestPath,
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string] $Commit,
        [Parameter(Mandatory)][string] $RunId,
        [Parameter(Mandatory)][string] $Platform,
        [Parameter(Mandatory)][string] $AppProject,
        [Parameter(Mandatory)] $FlowDigests,
        [Parameter(Mandatory)] $HostQa,
        [Parameter(Mandatory)] $Artifacts,
        [Parameter(Mandatory)] $Omissions,
        [string] $AppBuildFingerprint,
        [string] $PackageDigest
    )

    $appDigest = Get-FileDigest $AppProject
    $allOmissions = [System.Collections.Generic.List[object]]::new()
    foreach ($omission in @($Omissions)) {
        $allOmissions.Add($omission)
    }
    if ($null -eq $appDigest) {
        $allOmissions.Add([ordered]@{
                kind = 'app-digest'
                reason = 'The selected app project was unavailable or could not be hashed.'
            })
    }
    if ([string]::IsNullOrWhiteSpace($PackageDigest) -and
        @($allOmissions | Where-Object { $_.kind -eq 'package-digest' }).Count -eq 0) {
        $allOmissions.Add([ordered]@{
                kind = 'package-digest'
                reason = 'The platform host did not emit a packaged-app digest for this run.'
            })
    }

    Write-AtomicJson -Path $ManifestPath -Value ([ordered]@{
            schema = 1
            kind = 'devflow-flow-qa'
            generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
            repository = [ordered]@{ commit = $Commit }
            workflow = [ordered]@{ runId = $RunId; attempt = $env:GITHUB_RUN_ATTEMPT }
            experimental = if ($Platform -eq 'macos') { $true } else { $false }
            backend = if ($Platform -eq 'macos') { 'appkit' } else { $null }
            officialCoverage = if ($Platform -eq 'macos') { $false } else { $true }
            macCatalystEquivalent = if ($Platform -eq 'macos') { $false } else { $null }
            testing = [ordered]@{
                project = Get-RepositoryRelativePath -RepositoryRoot $RepositoryRoot -Path (Join-Path $RepositoryRoot 'src/DevFlow/Microsoft.Maui.DevFlow.Agent.IntegrationTests/Microsoft.Maui.DevFlow.Agent.IntegrationTests.csproj')
                packageVersion = 'unknown'
            }
            platform = [ordered]@{
                name = $Platform
                experimental = if ($Platform -eq 'macos') { $true } else { $false }
                backend = if ($Platform -eq 'macos') { 'appkit' } else { $null }
                officialCoverage = if ($Platform -eq 'macos') { $false } else { $true }
                macCatalystEquivalent = if ($Platform -eq 'macos') { $false } else { $null }
                host = $HostQa.host
            }
            app = [ordered]@{
                project = Get-RepositoryRelativePath -RepositoryRoot $RepositoryRoot -Path $AppProject
                sourceDigest = $appDigest
                buildFingerprint = $AppBuildFingerprint
                packageDigest = $PackageDigest
            }

            flows = @($FlowDigests)
            hostQa = $HostQa
            artifacts = @($Artifacts)
            omissions = @($allOmissions)
            privacy = [ordered]@{
                excludedByDefault = @('screenshots', 'source', 'raw-model-context', 'environment', 'signing-inputs')
            }
        })
}

function Get-WindowsTierOneFacts {
    param([Parameter(Mandatory)][string] $ArtifactRoot)

    $path = Join-Path $ArtifactRoot 'windows-tier1-manifest.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return $null
    }

    try {
        $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
        $firstAttempt = @($manifest['flows'] |
            ForEach-Object { $_['firstAttempt'] } |
            Where-Object { $null -ne $_ }) |
            Select-Object -First 1
        return [ordered]@{
            manifestPath = $path
            appBuildFingerprint = $manifest['app']['buildFingerprint']
            packageDigest = $manifest['app']['packageDigest']
            resetFingerprint = $firstAttempt['resetFingerprint']
            seedFingerprint = $firstAttempt['seedFingerprint']
            backendStateFingerprint = $firstAttempt['backendStateFingerprint']
        }
    }
    catch {
        return $null
    }
}

function Finalize-AndroidManifest {
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string] $ManifestPath,
        [Parameter(Mandatory)][string] $ArtifactRoot,
        [Parameter(Mandatory)][string] $ResultsRoot,
        [Parameter(Mandatory)][string] $Commit,
        [Parameter(Mandatory)][string] $RunId,
        [Parameter(Mandatory)] $HostQa,
        [Parameter(Mandatory)] $FallbackArguments
    )

    $finalizer = Join-Path $RepositoryRoot 'eng/devflow/Finalize-DevFlowFlowPilotManifest.ps1'
    try {
        & $finalizer `
            -ManifestPath $ManifestPath `
            -RepositoryRoot $RepositoryRoot `
            -ArtifactRoots @($ArtifactRoot, $ResultsRoot) `
            -Platform android `
            -RepositoryCommit $Commit `
            -WorkflowRunId $RunId `
            -AndroidApiLevel $env:DEVFLOW_TEST_ANDROID_API `
            -AndroidAvdName $env:DEVFLOW_TEST_ANDROID_AVD `
            -DeviceEvidenceKind emulator
        if ($LASTEXITCODE -ne 0) {
            throw "The shared manifest finalizer returned exit code $LASTEXITCODE."
        }

        $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json -AsHashtable
        $manifest['hostQa'] = $HostQa
        Write-AtomicJson -Path $ManifestPath -Value $manifest
    }
    catch {
        $FallbackArguments.omissions += [ordered]@{
            kind = 'shared-manifest'
            reason = 'The shared Android flow-pilot manifest could not be finalized.'
        }
        Write-GenericManifest @FallbackArguments
    }
}

function Invoke-Qualification {
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string] $Configuration,
        [Parameter(Mandatory)][bool] $NoBuild,
        [Parameter(Mandatory)][string] $Platform,
        [Parameter(Mandatory)][string] $ManifestPath,
        [Parameter(Mandatory)][string] $ArtifactRoot,
        [Parameter(Mandatory)][string] $DiagnosticDirectory
    )

    $cliProject = Join-Path $RepositoryRoot 'src/Cli/Microsoft.Maui.Cli/Microsoft.Maui.Cli.csproj'
    $outputPath = Join-Path $ArtifactRoot 'qualification.json'
    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.AddRange([string[]] @('run', '--project', $cliProject, '-f', 'net10.0', '--configuration', $Configuration))
    if ($NoBuild) {
        $arguments.Add('--no-build')
    }
    $arguments.AddRange([string[]] @(
            '--',
            'devflow', 'flow', 'qualify',
            '--platform', $Platform,
            '--corpus', (Join-Path $RepositoryRoot 'tests/DevFlow/InspectorCorpus'),
            '--artifact-manifest', $ManifestPath,
            '--output', $outputPath,
            '--json',
            '--fail-on-non-pass'))

    $result = Invoke-RecordedCommand -FileName 'dotnet' -Arguments $arguments.ToArray() -DiagnosticPath (Join-Path $DiagnosticDirectory 'qualification-output.txt')
    $status = if ($result.exitCode -eq 0) {
        'qualified'
    }
    elseif (Test-Path -LiteralPath $outputPath -PathType Leaf) {
        try {
            $report = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
            if ($report.status -eq 'not-qualified') { 'not-qualified' } else { 'qualification-failed' }
        }
        catch {
            'qualification-failed'
        }
    }
    else {
        'qualification-failed'
    }

    [ordered]@{
        status = $status
        path = $outputPath
        exitCode = $result.exitCode
    }
}

$platform = $null
$repeat = 3
$resultsRootInput = $null
$configuration = 'Debug'
$flowFilter = $null
$noBuild = $false
$qualification = $false
$experimental = $false
$physicalDevice = $false
$deviceId = $null
$iosRuntime = $null
$signingIdentity = $null
$provisioningProfile = $null
$keychain = $null
$verbosity = 'normal'
$dryRun = $false

for ($index = 0; $index -lt $CliArgs.Count; $index++) {
    $token = $CliArgs[$index].ToLowerInvariant()
    switch ($token) {
        '--help' {
            Write-Output (Write-Usage)
            exit $ExitSuccess
        }
        '-help' {
            Write-Output (Write-Usage)
            exit $ExitSuccess
        }
        '--platform' { $platform = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '-platform' { $platform = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '--repeat' { $repeat = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '-repeat' { $repeat = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '--results-root' { $resultsRootInput = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '-results-root' { $resultsRootInput = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '--configuration' { $configuration = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '-configuration' { $configuration = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '--flow-filter' { $flowFilter = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '-flow-filter' { $flowFilter = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '--no-build' { $noBuild = $true; break }
        '-no-build' { $noBuild = $true; break }
        '--qualification' { $qualification = $true; break }
        '-qualification' { $qualification = $true; break }
        '--experimental' { $experimental = $true; break }
        '-experimental' { $experimental = $true; break }
        '--physical-device' { $physicalDevice = $true; break }
        '-physical-device' { $physicalDevice = $true; break }
        '--device-id' { $deviceId = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '-device-id' { $deviceId = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '--ios-runtime' { $iosRuntime = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '-ios-runtime' { $iosRuntime = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '--signing-identity' { $signingIdentity = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '--ios-signing-identity' { $signingIdentity = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '--provisioning-profile' { $provisioningProfile = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '--ios-provisioning-profile' { $provisioningProfile = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '--keychain' { $keychain = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '--ios-keychain' { $keychain = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '--verbosity' { $verbosity = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '-verbosity' { $verbosity = Get-RequiredValue $token $index $CliArgs; $index++; break }
        '--verbose' { $verbosity = 'detailed'; break }
        '--dry-run' { $dryRun = $true; break }
        '-dry-run' { $dryRun = $true; break }
        default { Exit-Usage "Unknown option '$($CliArgs[$index])'." }
    }
}

if ([string]::IsNullOrWhiteSpace($platform)) {
    Exit-Usage '--platform is required.'
}
$platform = $platform.ToLowerInvariant()
if ($platform -notin @('android', 'windows', 'ios', 'maccatalyst', 'macos')) {
    Exit-Usage "--platform '$platform' is not supported."
}
if ([string]::IsNullOrWhiteSpace($resultsRootInput)) {
    Exit-Usage '--results-root is required.'
}
$parsedRepeat = 0
if (-not [int]::TryParse([string] $repeat, [ref] $parsedRepeat) -or $parsedRepeat -lt 1 -or $parsedRepeat -gt 20) {
    Exit-Usage '--repeat must be an integer from 1 through 20.'
}
$repeat = $parsedRepeat
Test-OptionValue $configuration '--configuration'
if ($configuration -notmatch '^[A-Za-z0-9._-]+$') {
    Exit-Usage '--configuration may contain only letters, digits, dot, underscore, and hyphen.'
}
if ($verbosity -notin @('quiet', 'minimal', 'normal', 'detailed', 'diagnostic')) {
    Exit-Usage '--verbosity must be quiet, minimal, normal, detailed, or diagnostic.'
}
if ($flowFilter) { Test-OptionValue $flowFilter '--flow-filter' }
if ($deviceId) { Test-OptionValue $deviceId '--device-id' }
if ($iosRuntime) { Test-OptionValue $iosRuntime '--ios-runtime' }
if ($signingIdentity) { Test-OptionValue $signingIdentity '--signing-identity' }
if ($provisioningProfile) { Test-OptionValue $provisioningProfile '--provisioning-profile' }
if ($keychain) { Test-OptionValue $keychain '--keychain' }

if ($platform -eq 'macos' -and -not $experimental) {
    Exit-Usage '--platform macos is experimental and requires --experimental.'
}
if ($experimental -and $platform -ne 'macos') {
    Exit-Usage '--experimental applies only to the separately labeled macos/AppKit lane.'
}
if ($platform -eq 'macos' -and $qualification) {
    Exit-Usage '--qualification cannot be used for experimental AppKit; it never qualifies an official MAUI or Mac Catalyst gate.'
}
if ($physicalDevice -and $platform -ne 'ios') {
    Exit-Usage '--physical-device applies only to --platform ios.'
}
if ($physicalDevice -and $iosRuntime) {
    Exit-Usage '--ios-runtime is a simulator selector and cannot be combined with --physical-device.'
}
if ($iosRuntime -and $platform -ne 'ios') {
    Exit-Usage '--ios-runtime applies only to the iOS Simulator lane.'
}
if ($deviceId -and -not ($platform -eq 'android' -or ($platform -eq 'ios' -and $physicalDevice))) {
    Exit-Usage '--device-id applies to Android or to the physical-iOS lane.'
}
if ($platform -eq 'ios' -and $physicalDevice) {
    foreach ($required in @(
            @{ name = '--device-id'; value = $deviceId },
            @{ name = '--signing-identity'; value = $signingIdentity },
            @{ name = '--provisioning-profile'; value = $provisioningProfile },
            @{ name = '--keychain'; value = $keychain })) {
        if ([string]::IsNullOrWhiteSpace($required.value)) {
            Exit-Usage "Physical iOS requires $($required.name)."
        }
    }
}
elseif ($signingIdentity -or $provisioningProfile -or $keychain) {
    Exit-Usage 'Signing, provisioning, and keychain options apply only to --platform ios --physical-device.'
}

$repositoryRoot = Get-CanonicalPath (Join-Path $PSScriptRoot '../..')
if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'global.json') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'MauiLabs.slnx') -PathType Leaf)) {
    [Console]::Error.WriteLine('flow-qa: The script must be run from a maui-labs checkout with global.json.')
    exit $ExitPrerequisite
}

$resultsRoot = Resolve-ResultsRoot -InputPath $resultsRootInput -RepositoryRoot $repositoryRoot -Platform $platform
$runId = Get-RunId
$artifactRoot = Resolve-ArtifactRoot -RepositoryRoot $repositoryRoot -Platform $platform -RunId $runId
$diagnosticDirectory = Join-Path $artifactRoot 'host-diagnostics'
$manifestPath = Join-Path $artifactRoot 'manifest.json'
$flowRunPath = Join-Path $artifactRoot 'flow-run.json'
$testProject = Join-Path $repositoryRoot 'src/DevFlow/Microsoft.Maui.DevFlow.Agent.IntegrationTests/Microsoft.Maui.DevFlow.Agent.IntegrationTests.csproj'
$appProject = if ($platform -eq 'macos') {
    Join-Path $repositoryRoot 'samples/DevFlow.Sample.MacOS/DevFlow.Sample.MacOS.csproj'
}
else {
    Join-Path $repositoryRoot 'samples/DevFlow.Sample/DevFlow.Sample.csproj'
}
$appleTestAgentRoot = Join-Path $repositoryRoot 'src/DevFlow/Microsoft.Maui.DevFlow.TestAgent'
$appleTestAgentHostProject = Join-Path $repositoryRoot 'src/DevFlow/Microsoft.Maui.DevFlow.TestAgent.Host/Microsoft.Maui.DevFlow.TestAgent.Host.csproj'
$appleTestAgentNativeProject = Join-Path $appleTestAgentRoot 'AppleXCTestAgent/DevFlowAppleTestAgent.xcodeproj/project.pbxproj'
$appleTestAgentSourceAvailable =
    (Test-Path -LiteralPath $appleTestAgentHostProject -PathType Leaf) -and
    (Test-Path -LiteralPath $appleTestAgentNativeProject -PathType Leaf)
# The PowerShell entry point intentionally remains a non-executing Apple lane. Only the guarded
# macOS shell --apple-spike command can establish the runtime proof and emit its machine-readable
# report, so source presence must never convert this lane into a passing test invocation.
$appleTestAgentAvailable = $false
$baseFilter = switch ($platform) {
    'android' { 'Category=FlowPilot' }
    'windows' { 'Category=WindowsFlowQa' }
    'macos' { 'Category=AppKitFlowQa' }
    default { 'Category=AppleTestAgent' }
}
$testFilter = if ($flowFilter) { "$baseFilter&($flowFilter)" } else { $baseFilter }
$trxFileName = if ($platform -in @('android', 'windows')) {
    "devflow-flow-$platform-$runId.trx"
}
else {
    "devflow-flow-$platform-$runId-attempt-{attempt}.trx"
}
$testArguments = [System.Collections.Generic.List[string]]::new()
$testArguments.AddRange([string[]] @(
        'test',
        $testProject,
        '--configuration',
        $configuration,
        '--filter',
        $testFilter,
        '--logger',
        "trx;LogFileName=$trxFileName",
        '--logger',
        "console;verbosity=$verbosity",
        '--results-directory',
        $resultsRoot))
if ($noBuild) {
    $testArguments.Add('--no-build')
}

function Get-TestArgumentsForAttempt {
    param([Parameter(Mandatory)][int] $Attempt)

    $arguments = [string[]] @($testArguments)
    for ($index = 0; $index -lt $arguments.Length; $index++) {
        $arguments[$index] = $arguments[$index].Replace('{attempt}', $Attempt.ToString())
    }

    $arguments
}

$dryRunObject = [ordered]@{
    schema = 1
    kind = 'devflow-flow-qa-dry-run'
    status = 'dry-run'
    platform = $platform
    repeat = $repeat
    configuration = $configuration
    testFilter = $testFilter
    noBuild = $noBuild
    qualificationRequested = $qualification
    experimental = $experimental
    backend = if ($platform -eq 'macos') { 'appkit' } else { $null }
    officialCoverage = if ($platform -eq 'macos') { $false } else { $true }
    macCatalystEquivalent = if ($platform -eq 'macos') { $false } else { $null }
    physicalDevice = $physicalDevice
    signingInputsConfigured = [bool] ($signingIdentity -and $provisioningProfile -and $keychain)
    appProject = Get-RepositoryRelativePath -RepositoryRoot $repositoryRoot -Path $appProject
    command = [ordered]@{
        tool = 'dotnet'
        arguments = @($testArguments)
    }
    artifactPaths = [ordered]@{
        testResults = Get-RepositoryRelativePath -RepositoryRoot $repositoryRoot -Path $resultsRoot
        artifactRoot = Get-RepositoryRelativePath -RepositoryRoot $repositoryRoot -Path $artifactRoot
        manifest = Get-RepositoryRelativePath -RepositoryRoot $repositoryRoot -Path $manifestPath
        flowRun = Get-RepositoryRelativePath -RepositoryRoot $repositoryRoot -Path $flowRunPath
    }
    capability = [ordered]@{
        required = if ($platform -in @('ios', 'maccatalyst', 'macos')) { 'apple-test-agent' } else { 'platform-fixture' }
        available = if ($platform -in @('ios', 'maccatalyst', 'macos')) { $appleTestAgentAvailable } else { $true }
        state = if ($platform -in @('ios', 'maccatalyst', 'macos') -and -not $appleTestAgentAvailable) { 'pending-spike' } else { 'planned' }
        sourceAvailable = if ($platform -in @('ios', 'maccatalyst', 'macos')) { $appleTestAgentSourceAvailable } else { $true }
    }
}

if ($dryRun) {
    $dryRunObject | ConvertTo-Json -Depth 16 -Compress
    exit $ExitSuccess
}

Push-Location $repositoryRoot
try {
    New-Item -ItemType Directory -Force -Path $resultsRoot, $artifactRoot, $diagnosticDirectory | Out-Null
    Assert-ExistingPathIsNotReparsePoint -RepositoryRoot $repositoryRoot -Path $resultsRoot
    Assert-ExistingPathIsNotReparsePoint -RepositoryRoot $repositoryRoot -Path $artifactRoot
    if ((Test-Path -LiteralPath $manifestPath) -or (Test-Path -LiteralPath $flowRunPath)) {
        [Console]::Error.WriteLine("flow-qa: Refusing to overwrite existing run artifacts for '$runId'.")
        exit $ExitUsage
    }

    $commit = Get-RepositoryCommit $repositoryRoot
    $hostMetadata = Get-HostMetadata -Platform $platform -IosRuntime $iosRuntime -PhysicalDevice $physicalDevice -DeviceId $deviceId
    $flowDigests = Get-FlowDigests -RepositoryRoot $repositoryRoot -Platform $platform
    $attempts = [System.Collections.Generic.List[object]]::new()
    $omissions = [System.Collections.Generic.List[object]]::new()
    $omissions.Add([ordered]@{
            kind = 'diagnostic-rerun'
            reason = 'No automatic diagnostic rerun was performed because replay may mutate state.'
        })
    $status = 'pending'
    $classification = 'pending'
    $qualificationResult = $null

    $hostQa = [ordered]@{
        runId = $runId
        configuration = $configuration
        repeat = $repeat
        platformFilter = $baseFilter
        testFilterDigest = Get-StringDigest $testFilter
        noBuild = $noBuild
        host = $hostMetadata
        resetSeed = [ordered]@{
            resetFingerprint = 'not-observed'
            seedFingerprint = 'not-observed'
            backendStateFingerprint = 'not-observed'
        }
        windowsSession = $null
        firstAttempt = $null
        cleanAttempts = @()
        diagnosticReruns = @()
        diagnosticRerunPolicy = 'No automatic diagnostic rerun is performed because replay may mutate state.'
        repeatOwner = if ($platform -eq 'windows') {
            'windows-fixture-per-flow-clean-attempts'
        }
        elseif ($platform -eq 'android') {
            'android-fixture-per-flow-clean-attempts'
        }
        else {
            'script-per-attempt'
        }
    }

    $writeArtifacts = {
        param([string] $CurrentStatus, [string] $CurrentClassification)

        $windowsTierOneFacts = if ($platform -eq 'windows') {
            Get-WindowsTierOneFacts -ArtifactRoot $artifactRoot
        }
        else {
            $null
        }
        $windowsAppBuildFingerprint = $null
        $windowsPackageDigest = $null
        if ($null -ne $windowsTierOneFacts) {
            $windowsAppBuildFingerprint = $windowsTierOneFacts.appBuildFingerprint
            $windowsPackageDigest = $windowsTierOneFacts.packageDigest
            if ([string]::IsNullOrWhiteSpace($windowsAppBuildFingerprint)) {
                $windowsAppBuildFingerprint = $null
            }
            if ([string]::IsNullOrWhiteSpace($windowsPackageDigest)) {
                $windowsPackageDigest = $null
            }
            $hostQa.resetSeed.resetFingerprint = $windowsTierOneFacts.resetFingerprint ?? 'not-observed'
            $hostQa.resetSeed.seedFingerprint = $windowsTierOneFacts.seedFingerprint ?? 'not-observed'
            $hostQa.resetSeed.backendStateFingerprint = $windowsTierOneFacts.backendStateFingerprint ?? 'not-observed'
            $hostQa.tierOneManifest = Get-RepositoryRelativePath -RepositoryRoot $repositoryRoot -Path $windowsTierOneFacts.manifestPath
        }

        if ($attempts.Count -gt 0) {
            $hostQa.firstAttempt = $attempts[0]
        }
        $hostQa.cleanAttempts = @($attempts)
        $hostQa.status = $CurrentStatus
        $hostQa.classification = $CurrentClassification
        $hostQa.qualification = $qualificationResult

        if ($CurrentClassification -in @('flow-failure', 'infrastructure-failure') -and
            @($omissions | Where-Object { $_.kind -eq 'failure-evidence' }).Count -eq 0) {
            $failureTrace = Get-ChildItem -LiteralPath $artifactRoot -Recurse -Filter '*.mauitrace' -File -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if ($null -eq $failureTrace) {
                $omissions.Add([ordered]@{
                        kind = 'failure-evidence'
                        reason = 'No failure .mauitrace was available for this terminal outcome.'
                    })
            }
        }
        if ($platform -ne 'android' -and
            [string]::IsNullOrWhiteSpace($windowsPackageDigest) -and
            @($omissions | Where-Object { $_.kind -eq 'package-digest' }).Count -eq 0) {
            $omissions.Add([ordered]@{
                    kind = 'package-digest'
                    reason = 'The platform host did not emit a packaged-app digest for this run.'
                })
        }

        Write-HostDiagnostics -Directory $diagnosticDirectory -Metadata $hostMetadata -Status $CurrentStatus -Classification $CurrentClassification
        $flowRun = [ordered]@{
            schema = 1
            kind = 'devflow-flow-qa-run'
            generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
            repository = [ordered]@{ commit = $commit }
            platform = $platform
            experimental = if ($platform -eq 'macos') { $true } else { $false }
            backend = if ($platform -eq 'macos') { 'appkit' } else { $null }
            officialCoverage = if ($platform -eq 'macos') { $false } else { $true }
            macCatalystEquivalent = if ($platform -eq 'macos') { $false } else { $null }
            hostQa = $hostQa
            flows = @($flowDigests)
            app = [ordered]@{
                project = Get-RepositoryRelativePath -RepositoryRoot $repositoryRoot -Path $appProject
                sourceDigest = Get-FileDigest $appProject
                buildFingerprint = $windowsAppBuildFingerprint
                packageDigest = $windowsPackageDigest
            }
            firstAttempt = $hostQa.firstAttempt
            diagnosticReruns = @()
            omissions = @($omissions)
            privacy = [ordered]@{
                excludedByDefault = @('screenshots', 'source', 'raw-model-context', 'environment', 'signing-inputs')
            }
        }
        Write-AtomicJson -Path $flowRunPath -Value $flowRun

        $artifactData = Get-ArtifactRecords -RepositoryRoot $repositoryRoot -Roots @($artifactRoot, $resultsRoot) -ResultsRoot $resultsRoot -RunId $runId
        foreach ($omission in @($artifactData.omissions)) {
            $omissions.Add($omission)
        }

        $fallbackArguments = @{
            ManifestPath = $manifestPath
            RepositoryRoot = $repositoryRoot
            Commit = $commit
            RunId = $runId
            Platform = $platform
            AppProject = $appProject
            FlowDigests = $flowDigests
            HostQa = $hostQa
            Artifacts = $artifactData.records
            Omissions = @($omissions)
            AppBuildFingerprint = $windowsAppBuildFingerprint
            PackageDigest = $windowsPackageDigest
        }
        if ($platform -eq 'android') {
            Finalize-AndroidManifest -RepositoryRoot $repositoryRoot -ManifestPath $manifestPath -ArtifactRoot $artifactRoot -ResultsRoot $resultsRoot -Commit $commit -RunId $runId -HostQa $hostQa -FallbackArguments $fallbackArguments
        }
        else {
            Write-GenericManifest @fallbackArguments
        }
    }
    $writeStatus = {
        param([string] $CurrentStatus, [string] $CurrentClassification)
        [Console]::Error.WriteLine(
            "flow-qa: platform=$platform status=$CurrentStatus classification=$CurrentClassification artifacts=$(Get-RepositoryRelativePath -RepositoryRoot $repositoryRoot -Path $artifactRoot)")
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        $status = 'failed'
        $classification = 'prerequisite-missing'
        $omissions.Add([ordered]@{ kind = 'prerequisite'; reason = 'dotnet was not found. The script does not install SDKs or workloads.' })
        & $writeArtifacts $status $classification
        & $writeStatus $status $classification
        exit $ExitPrerequisite
    }
    if (-not (Test-Path -LiteralPath $testProject -PathType Leaf)) {
        $status = 'failed'
        $classification = 'prerequisite-missing'
        $omissions.Add([ordered]@{ kind = 'test-project'; reason = 'The integration test project was unavailable.' })
        & $writeArtifacts $status $classification
        & $writeStatus $status $classification
        exit $ExitPrerequisite
    }

    $runningOnWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
    $runningOnMacOS = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)
    if (($platform -eq 'windows' -and -not $runningOnWindows) -or
        ($platform -in @('ios', 'maccatalyst', 'macos') -and -not $runningOnMacOS)) {
        $status = 'failed'
        $classification = 'prerequisite-missing'
        $omissions.Add([ordered]@{ kind = 'host-platform'; reason = "The '$platform' lane requires its matching host OS." })
        & $writeArtifacts $status $classification
        & $writeStatus $status $classification
        exit $ExitPrerequisite
    }

    if ($platform -eq 'windows') {
        $windowsSessionAdmission = Get-WindowsDesktopSessionAdmission
        $hostQa.windowsSession = $windowsSessionAdmission
        Write-WindowsSessionDiagnostic -Directory $diagnosticDirectory -Admission $windowsSessionAdmission
        if ($windowsSessionAdmission.admissionResult -ne 'allowed') {
            $status = 'failed'
            $classification = 'infrastructure-failure'
            $hostQa.firstAttempt = [ordered]@{
                kind = 'preflight'
                repetition = 1
                outcome = 'infrastructure-error'
                failureClass = 'infrastructure'
                failurePhase = 'windows-desktop-session-admission'
                mutationDispatched = $false
            }
            $omissions.Add([ordered]@{
                    kind = 'windows-desktop-session'
                    reason = 'Windows desktop session admission failed before the test process launched or any flow replay was dispatched.'
                })
            & $writeArtifacts $status $classification
            & $writeStatus $status $classification
            [Console]::Error.WriteLine(
                "flow-qa: Windows requires an active, unlocked desktop session; admission=$($windowsSessionAdmission.admissionResult) reason=$($windowsSessionAdmission.reason).")
            exit $ExitPrerequisite
        }
    }

    if ($platform -eq 'macos' -and -not (Test-Path -LiteralPath $appProject -PathType Leaf)) {
        $status = 'unsupported'
        $classification = 'unsupported-platform'
        $omissions.Add([ordered]@{ kind = 'appkit-sample'; reason = 'No experimental AppKit sample or fixture project is available.' })
        & $writeArtifacts $status $classification
        & $writeStatus $status $classification
        exit $ExitPending
    }

    if ($platform -in @('ios', 'maccatalyst', 'macos') -and -not $appleTestAgentAvailable) {
        $status = 'pending-spike'
        $classification = 'capability-missing'
        $omissions.Add([ordered]@{
                kind = 'apple-test-agent'
                reason = 'Apple XCTest agent source alone is not runtime proof. Run the guarded macOS shell --apple-spike command to prove target foreground, authenticated transport, cancellation, parity, and artifacts.'
            })
        & $writeArtifacts $status $classification
        & $writeStatus $status $classification
        [Console]::Error.WriteLine("flow-qa: $platform is pending the Apple Test Agent spike (capability-missing).")
        exit $ExitPending
    }

    if ($platform -eq 'android') {
        $env:DEVFLOW_TEST_PLATFORM = 'android'
        $env:DEVFLOW_RUN_ANDROID_FLOW_PILOT = '1'
        $env:DEVFLOW_FLOW_PILOT_REPEAT = $repeat.ToString()
        $env:DEVFLOW_FLOW_PILOT_ARTIFACT_ROOT = $artifactRoot
        $env:DEVFLOW_FLOW_PILOT_RESULTS_ROOT = $resultsRoot
        $env:DEVFLOW_FLOW_PILOT_WORKFLOW_RUN_ID = $runId
        $env:DEVFLOW_FLOW_PILOT_REPOSITORY_COMMIT = $commit
        $env:DEVFLOW_FLOW_PILOT_DEVICE_EVIDENCE_KIND = 'emulator'
        if ($deviceId) {
            $env:DEVFLOW_TEST_ANDROID_SERIAL = $deviceId
        }

        $result = Invoke-RecordedCommand -FileName 'dotnet' -Arguments (Get-TestArgumentsForAttempt -Attempt 1) -DiagnosticPath (Join-Path $diagnosticDirectory 'test-output-attempt-1.txt')
        $attempts.Add([ordered]@{
                kind = 'clean'
                repetition = 1
                exitCode = $result.exitCode
                classification = Get-ExecutionClassification -ExitCode $result.exitCode -Output $result.output
                diagnostic = Get-RepositoryRelativePath -RepositoryRoot $repositoryRoot -Path $result.diagnosticPath
            })
    }
    elseif ($platform -eq 'windows') {
        $env:DEVFLOW_TEST_PLATFORM = 'windows'
        $env:DEVFLOW_RUN_WINDOWS_FLOW_QA = '1'
        $env:DEVFLOW_FLOW_QA_RUN_ID = $runId
        $env:DEVFLOW_FLOW_QA_REPEAT = $repeat.ToString()
        $env:DEVFLOW_FLOW_QA_ARTIFACT_ROOT = $artifactRoot
        $env:DEVFLOW_FLOW_QA_APP_PROJECT = $appProject

        # WindowsFixture owns each Tier-1 clean reset/relaunch/seed attempt. Invoking the test
        # host once avoids multiplying a requested three clean attempts into nine replays.
        $result = Invoke-RecordedCommand -FileName 'dotnet' -Arguments (Get-TestArgumentsForAttempt -Attempt 1) -DiagnosticPath (Join-Path $diagnosticDirectory 'test-output-attempt-1.txt')
        $attempts.Add([ordered]@{
                kind = 'tier-1-corpus'
                repetition = 1
                cleanRepetitionsPerFlow = $repeat
                exitCode = $result.exitCode
                classification = Get-ExecutionClassification -ExitCode $result.exitCode -Output $result.output
                diagnostic = Get-RepositoryRelativePath -RepositoryRoot $repositoryRoot -Path $result.diagnosticPath
            })
    }
    else {
        $env:DEVFLOW_TEST_PLATFORM = $platform
        $env:DEVFLOW_FLOW_QA_RUN_ID = $runId
        $env:DEVFLOW_FLOW_QA_REPEAT = $repeat.ToString()
        $env:DEVFLOW_FLOW_QA_APP_PROJECT = $appProject
        if ($physicalDevice) {
            $env:DEVFLOW_FLOW_QA_PHYSICAL_DEVICE = '1'
            $env:DEVFLOW_FLOW_QA_DEVICE_ID = $deviceId
            $env:DEVFLOW_IOS_SIGNING_IDENTITY = $signingIdentity
            $env:DEVFLOW_IOS_PROVISIONING_PROFILE = $provisioningProfile
            $env:DEVFLOW_IOS_KEYCHAIN = $keychain
        }
        if ($iosRuntime) {
            $env:DEVFLOW_TEST_IOS_VERSION = $iosRuntime
        }

        for ($attempt = 1; $attempt -le $repeat; $attempt++) {
            $result = Invoke-RecordedCommand -FileName 'dotnet' -Arguments (Get-TestArgumentsForAttempt -Attempt $attempt) -DiagnosticPath (Join-Path $diagnosticDirectory "test-output-attempt-$attempt.txt")
            $attempts.Add([ordered]@{
                    kind = 'clean'
                    repetition = $attempt
                    exitCode = $result.exitCode
                    classification = Get-ExecutionClassification -ExitCode $result.exitCode -Output $result.output
                    diagnostic = Get-RepositoryRelativePath -RepositoryRoot $repositoryRoot -Path $result.diagnosticPath
                })
        }
    }

    $attemptClassifications = @($attempts | ForEach-Object { $_.classification })
    if ($attemptClassifications -contains 'capability-missing') {
        $status = 'capability-missing'
        $classification = 'capability-missing'
    }
    elseif ($attemptClassifications -contains 'infrastructure-failure') {
        $status = 'failed'
        $classification = 'infrastructure-failure'
    }
    elseif ($attemptClassifications -contains 'flow-failure') {
        $status = 'failed'
        $classification = 'flow-failure'
    }
    else {
        $status = 'passed'
        $classification = 'passed'
    }

    & $writeArtifacts $status $classification

    if ($qualification -and $classification -eq 'passed') {
        $qualificationResult = Invoke-Qualification -RepositoryRoot $repositoryRoot -Configuration $configuration -NoBuild $noBuild -Platform $platform -ManifestPath $manifestPath -ArtifactRoot $artifactRoot -DiagnosticDirectory $diagnosticDirectory
        $hostQa.qualification = $qualificationResult
        if ($qualificationResult.status -eq 'not-qualified') {
            $status = 'not-qualified'
            $classification = 'not-qualified'
        }
        elseif ($qualificationResult.status -ne 'qualified') {
            $status = 'failed'
            $classification = 'infrastructure-failure'
        }
        & $writeArtifacts $status $classification
    }
    elseif ($qualification) {
        $omissions.Add([ordered]@{ kind = 'qualification'; reason = 'Qualification was not run because platform execution did not pass.' })
        & $writeArtifacts $status $classification
    }

    [Console]::Error.WriteLine("flow-qa: platform=$platform status=$status classification=$classification artifacts=$(Get-RepositoryRelativePath -RepositoryRoot $repositoryRoot -Path $artifactRoot)")
    switch ($classification) {
        'passed' { exit $ExitSuccess }
        'flow-failure' { exit $ExitFlowFailure }
        'capability-missing' { exit $ExitPending }
        'not-qualified' { exit $ExitPending }
        default { exit $ExitPrerequisite }
    }
}
finally {
    Pop-Location
}
