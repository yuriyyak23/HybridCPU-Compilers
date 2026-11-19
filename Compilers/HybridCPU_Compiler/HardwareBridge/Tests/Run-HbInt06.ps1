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
    [switch]$RequirePaired,
    [switch]$CoreOnly
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
$coreBefore = $null
if ($core) {
    $actualCore = Invoke-HbGit $core @('rev-parse', 'HEAD')
    Assert-HbExpectedSha $actualCore $ExpectedCoreSha
    $coreBefore = Get-HbSourceState $core
}
elseif ($CoreImporterProject -or $ExpectedCoreSha -or $RequirePaired -or $CoreOnly) {
    throw 'Paired Core requires explicit CoreRoot, Core SHA and test project'
}

New-Item -ItemType Directory -Path $OutputRoot -ErrorAction Stop | Out-Null
$artifacts = Join-Path $OutputRoot 'build'
# Pin CLI selection *from a fresh external work directory*: hosted runners may
# already have a newer GA SDK than the requested prerelease.
[IO.File]::WriteAllText((Join-Path $OutputRoot 'global.json'),
    (([ordered]@{ sdk = [ordered]@{
        version = $ExpectedSdkVersion; rollForward = 'disable'; allowPrerelease = $true
    } } | ConvertTo-Json -Depth 4)), [Text.UTF8Encoding]::new($false))
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
    CoreBefore = $coreBefore
    CoreAfter = $null
    CoreSourceUnchanged = $false
    ExpectedSdkVersion = $ExpectedSdkVersion
    Commands = @()
    Matrix = [ordered]@{
        StandaloneCompanion = 'NotRun'
        SidecarV1 = 'NotRun'
        CscPePdb = 'NotRun'
        SegmentedV2CodecContract = 'NotRun'
        SegmentedCaptureV2 = 'Blocked'
        StrictCoreImporter = 'Blocked'
        PairedProducerToCore = 'Blocked'
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
    $sdk = Invoke-HbRecordedCommand 'dotnet-version' 'dotnet' @('--version') $OutputRoot $OutputRoot $TimeoutSeconds
    $report.Commands += $sdk
    if ($sdk.Status -ne 'Passed') {
        $notRun = $true
        throw 'NET 11 SDK probe was unavailable/failed; no test can be counted Passed'
    }
    $versionText = ([IO.File]::ReadAllText((Join-Path $OutputRoot 'dotnet-version.stdout.txt'))).Trim()
    if ($versionText -cne $ExpectedSdkVersion) {
        throw "Wrong .NET SDK: pinned $ExpectedSdkVersion, executed $versionText"
    }
    if (-not $CoreOnly) {
    $protocolProject = Join-Path $PSScriptRoot 'HardwareBridge.ProtocolTests.csproj'
    $protocolArgs = @('run','--project',$protocolProject,'--configuration','Release',
        "-p:ArtifactsPath=$artifacts",'-p:UseArtifactsOutput=true')
    $protocol = Invoke-HbRecordedCommand 'v1-protocol-tests' 'dotnet' $protocolArgs $OutputRoot $OutputRoot $TimeoutSeconds
    $report.Commands += $protocol
    $report.Matrix.SidecarV1 = $protocol.Status
    if ($protocol.Status -ne 'Passed') {
        $failed = $true
        $report.Blockers += 'Isolated v1 protocol tests failed: inspect v1-protocol-tests.stdout/stderr.txt'
        $report.Error = 'Isolated v1 strict protocol tests failed or blocked'
    }
    $v2Project = Join-Path $PSScriptRoot 'HardwareBridge.V2.ContractTests.csproj'
    $v2Args = @('run','--project',$v2Project,'--configuration','Release',
        "-p:ArtifactsPath=$artifacts",'-p:UseArtifactsOutput=true')
    $v2 = Invoke-HbRecordedCommand 'v2-codec-contract' 'dotnet' $v2Args $OutputRoot $OutputRoot $TimeoutSeconds
    $report.Commands += $v2
    $report.Matrix.SegmentedV2CodecContract = $v2.Status
    if ($v2.Status -ne 'Passed') {
        $failed = $true
        $report.Blockers += 'Opt-in v2 byte codec tests failed; these are not real Golden Csc capture receipts'
        $report.Error = 'Isolated v2 codec contract tests failed or blocked'
    }
    $project = Join-Path $PSScriptRoot 'HardwareBridge.SelfTests.csproj'
    $args = @('run','--project',$project,'--configuration','Release',
        "-p:ArtifactsPath=$artifacts",'-p:UseArtifactsOutput=true')
    $standalone = Invoke-HbRecordedCommand 'companion-selftests' 'dotnet' $args $OutputRoot $OutputRoot $TimeoutSeconds
    $report.Commands += $standalone
    $report.Matrix.StandaloneCompanion = $standalone.Status
    $report.Matrix.CscPePdb = $standalone.Status
    if ($standalone.Status -ne 'Passed') {
        $failed = $true
        $report.Blockers += 'Actual Companion Csc PE/PDB test could not complete: inspect companion-selftests.stdout.txt; no fixture substitution'
        $report.Error = 'Standalone Companion / Csc PE-PDB tests failed or blocked'
    }
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
    } # !CoreOnly

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
        $paired = Invoke-HbRecordedCommand 'paired-core-importer' 'dotnet' $pairedArgs $OutputRoot $OutputRoot $TimeoutSeconds
        $report.Commands += $paired
        $report.Matrix.StrictCoreImporter = $paired.Status
        if ($paired.Status -ne 'Passed') { throw 'Core importer integration test failed/blocked' }
    }
    elseif ($RequirePaired -or $CoreOnly) {
        throw 'Paired Core importer not configured; refusing false integration PASS'
    }
}
catch {
    $failed = $true
    $report.Error = $_.Exception.Message
}
finally {
    if ($core) {
        try {
            $report.CoreAfter = Get-HbSourceState $core
            Assert-HbSourceUnchanged $coreBefore $report.CoreAfter
            $report.CoreSourceUnchanged = $true
        }
        catch {
            $failed = $true
            $report.Error = "Core source immutability failure: $($_.Exception.Message)"
        }
    }
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
    if ($core) {
        $coreAfterDigest = if ($null -eq $report.CoreAfter) { "NOTRUN" } else { $report.CoreAfter.TreeSha256 }
        Write-Host "Core pre=$($coreBefore.TreeSha256) post=$coreAfterDigest; unchanged=$($report.CoreSourceUnchanged)"
    }
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
if ($CoreOnly) {
    Write-Host 'HB-INT-06 Core importer contract host Passed (producer-to-Core and full release gates remain Blocked).'
} else {
    Write-Host 'HB-INT-06 standalone software QA Passed (full release gates remain Blocked).'
}
exit 0
