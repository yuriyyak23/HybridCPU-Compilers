#requires -Version 7.3
# HB-INT-06: standalone software-facts QA, with explicit paired Core opt-in.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GoldenRoot,
    [Parameter(Mandatory)][string]$ExpectedGoldenSha,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$BridgeRoot = (Join-Path $PSScriptRoot '../../../..'),
    [string]$ExpectedBridgeSha = '',
    [string]$CoreRoot = '',
    [string]$ExpectedCoreSha = '',
    [string]$CoreImporterProject = '',
    [string[]]$CoreImporterArguments = @(),
    [string]$ExpectedSdkVersion = '11.0.100-rc.2.26504.105',
    [int]$TimeoutSeconds = 900,
    [switch]$RequirePaired
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HbInt06.SourceAudit.ps1')

$bridge = (Resolve-Path -LiteralPath $BridgeRoot).ProviderPath
$golden = (Resolve-Path -LiteralPath $GoldenRoot).ProviderPath
$core = if ($CoreRoot) { (Resolve-Path -LiteralPath $CoreRoot).ProviderPath } else { '' }
Assert-HbOutputOutside $OutputRoot @($bridge, $golden, $core)
if ($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 3600) { throw 'Unsupported test timeout budget' }
$actualBridge = Invoke-HbGit $bridge @('rev-parse', 'HEAD')
if ($ExpectedBridgeSha) { Assert-HbExpectedSha $actualBridge $ExpectedBridgeSha }
$before = Get-HbSourceState $golden
Assert-HbExpectedSha $before.Head $ExpectedGoldenSha
$actualCore = $null
if ($core) {
    $actualCore = Invoke-HbGit $core @('rev-parse', 'HEAD')
    Assert-HbExpectedSha $actualCore $ExpectedCoreSha
}
elseif ($CoreImporterProject -or $ExpectedCoreSha -or $RequirePaired) {
    throw 'Paired Core requires explicit CoreRoot, Core SHA and test project'
}

New-Item -ItemType Directory -Path $OutputRoot -ErrorAction Stop | Out-Null
$artifacts = Join-Path $OutputRoot 'build'
$env:DOTNET_CLI_HOME = Join-Path $OutputRoot 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $OutputRoot 'nuget-packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:DOTNET_NOLOGO = '1'
$report = [ordered]@{
    Schema = 'hybridcpu.hardwarebridge.qa-receipt/v1'
    Scope = 'unqualified-software-facts-only'
    BridgeHead = $actualBridge
    GoldenHead = $before.Head
    GoldenBefore = $before
    CoreHead = $actualCore
    ExpectedSdkVersion = $ExpectedSdkVersion
    Commands = @()
    Matrix = [ordered]@{
        StandaloneCompanion = 'NotRun'
        SidecarV1 = 'NotRun'
        CscPePdb = 'NotRun'
        SegmentedCaptureV2 = 'Blocked'
        StrictCoreImporter = 'Blocked'
        IndependentSourceVerifier = 'Blocked'
        NativeApprovalAndToolPins = 'NotRun'
        RtlAcceptance = 'Blocked'
        PhysicalSignoff = 'Blocked'
    }
    BuildArtifacts = @()
    Blockers = @(
        'v2 segmented capture must be supplied by HB-INT-02/03 and a compatible Core reader',
        'strict Core importer/independent source verifier require CW-INT-02/03 (and CW-INT-10 for Golden)',
        'native EDA approval and independent RTL/target receipts are outside HardwareBridge'
    )
    GoldenAfter = $null
    SourceUnchanged = $false
    Error = $null
}
$failed = $false
$notRun = $false
try {
    $sdk = Invoke-HbRecordedCommand 'dotnet-version' 'dotnet' @('--version') $bridge $OutputRoot $TimeoutSeconds
    $report.Commands += $sdk
    if ($sdk.Status -ne 'Passed') {
        $notRun = $true
        throw 'NET 11 SDK probe was unavailable/failed; no test can be counted Passed'
    }
    $versionText = ([IO.File]::ReadAllText((Join-Path $OutputRoot 'dotnet-version.stdout.txt'))).Trim()
    if ($versionText -cne $ExpectedSdkVersion) {
        throw "Wrong .NET SDK: pinned $ExpectedSdkVersion, executed $versionText"
    }
    $project = Join-Path $PSScriptRoot 'HardwareBridge.SelfTests.csproj'
    $args = @('run','--project',$project,'--configuration','Release',
        "-p:ArtifactsPath=$artifacts",'-p:UseArtifactsOutput=true')
    $standalone = Invoke-HbRecordedCommand 'companion-selftests' 'dotnet' $args $bridge $OutputRoot $TimeoutSeconds
    $report.Commands += $standalone
    $report.Matrix.StandaloneCompanion = $standalone.Status
    $report.Matrix.SidecarV1 = $standalone.Status
    $report.Matrix.CscPePdb = $standalone.Status
    if ($standalone.Status -ne 'Passed') { throw 'Standalone Companion / sidecar / Csc PE-PDB tests failed or blocked' }
    if (-not (Test-Path -LiteralPath $artifacts)) {
        throw 'No external build artifact directory: compiled test may have written inside sources'
    }
    $buildFiles = Get-ChildItem -LiteralPath $artifacts -Recurse -File -ErrorAction Stop |
        Where-Object { $_.Extension -in @('.dll','.pdb') }
    $report.BuildArtifacts = @($buildFiles | ForEach-Object {
        [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($artifacts, $_.FullName).Replace('\','/');
            Bytes = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    if ($report.BuildArtifacts.Count -lt 2) { throw 'Compiled DLL/PDB artifact hashes were not captured' }

    $mutationArgs = @('-NoProfile','-NonInteractive','-File',
        (Join-Path $PSScriptRoot 'HbInt06.MutationTests.ps1'),
        '-ScratchRoot',(Join-Path $OutputRoot 'mutation-fixtures'))
    $mutations = Invoke-HbRecordedCommand 'no-write-mutations' 'pwsh' $mutationArgs $bridge $OutputRoot $TimeoutSeconds
    $report.Commands += $mutations
    if ($mutations.Status -ne 'Passed') { throw 'Mutation tests failed or were not executed' }

    if ($core -and $CoreImporterProject) {
        if ($CoreImporterProject -match '(^|[\\/])\.\.([\\/]|$)' -or
            [IO.Path]::IsPathRooted($CoreImporterProject)) {
            throw 'CoreImporterProject must be a safe repo-relative path'
        }
        $coreProject = [IO.Path]::GetFullPath((Join-Path $core $CoreImporterProject))
        if (-not (Test-Path -LiteralPath $coreProject -PathType Leaf)) {
            throw "Pinned Core importer project absent: $CoreImporterProject"
        }
        $pairedArgs = @('run','--project',$coreProject,'--configuration','Release',
            "-p:ArtifactsPath=$artifacts",'-p:UseArtifactsOutput=true')
        if ($CoreImporterArguments.Count -gt 0) { $pairedArgs += '--'; $pairedArgs += $CoreImporterArguments }
        $paired = Invoke-HbRecordedCommand 'paired-core-importer' 'dotnet' $pairedArgs $core $OutputRoot $TimeoutSeconds
        $report.Commands += $paired
        $report.Matrix.StrictCoreImporter = $paired.Status
        if ($paired.Status -ne 'Passed') { throw 'Core importer integration test failed/blocked' }
    }
    elseif ($RequirePaired) {
        throw 'Paired Core importer not configured; refusing false integration PASS'
    }
}
catch {
    $failed = $true
    $report.Error = $_.Exception.Message
}
finally {
    try {
        $after = Get-HbSourceState $golden
        $report.GoldenAfter = $after
        Assert-HbSourceUnchanged $before $after
        $report.SourceUnchanged = $true
    }
    catch {
        $failed = $true
        $report.Error = "Golden source immutability failure: $($_.Exception.Message)"
    }
    foreach ($receipt in $report.Commands) {
        try { Assert-HbCommandReceipt $receipt }
        catch {
            $failed = $true
            $report.Error = "Invalid command receipt: $($_.Exception.Message)"
        }
    }
    $file = Join-Path $OutputRoot 'hb-int-06-qa-receipt.json'
    $report | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $file -Encoding utf8NoBOM
    $sha = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    Write-Host "HB-INT-06 report: $file SHA256=$sha"
    $afterDigest = if ($null -eq $report.GoldenAfter) { "NOTRUN" } else { $report.GoldenAfter.TreeSha256 }
    Write-Host "Golden pre=$($before.TreeSha256) post=$afterDigest; unchanged=$($report.SourceUnchanged)"
    foreach ($entry in $report.Commands) {
        Write-Host "EXEC $($entry.Name): status=$($entry.Status) exit=$($entry.ExitCode) stdoutSHA=$($entry.StdoutSha256) stderrSHA=$($entry.StderrSha256)"
    }
    Write-Host "Downstream v2/Core/native/RTL/physical: Blocked or NotRun (never promoted)"
}
if ($failed) {
    Write-Error "HB-INT-06 FAILED/BLOCKED: $($report.Error)" -ErrorAction Continue
    if ($notRun) { exit 2 }
    exit 1
}
Write-Host 'HB-INT-06 standalone QA Passed (cross-repo/full release gates remain Blocked).'
exit 0
