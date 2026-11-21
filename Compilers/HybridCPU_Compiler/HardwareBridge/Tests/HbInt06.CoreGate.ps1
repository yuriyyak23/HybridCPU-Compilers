#requires -Version 7.3
# HB-INT-06: no-secret Core read-access preflight and immutable checkout guard.
# Does not grant qualification. Preflight/checkout Blocked=2, downstream Failed=1.
[CmdletBinding()]
param(
    [ValidateSet('Preflight','Checkout','Downstream','SelfTest')][string]$Mode = 'Preflight',
    [string]$ExpectedBridgeSha = '',
    [string]$BridgeRoot = '',
    [string]$ExpectedCoreSha = '',
    [string]$CoreRoot = '',
    [string]$CheckoutOutcome = 'failure',
    [int]$DownstreamExitCode = -1,
    [string]$PairedReceiptPath = '',
    [string]$CoreImporterProject = 'tests/CircuitWeave.CWINT03.BridgeImport/CircuitWeave.CWINT03.BridgeImport.csproj'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Reviewed exact pin from R0-02 request, NOT a moving master reference.
$script:R002ReviewedCoreSha = 'b5c44685197a40df99a4d48891120b12475c6ca5'
$script:R002GitCommands = [Collections.Generic.List[object]]::new()
$script:R002Clock = [Diagnostics.Stopwatch]::StartNew()

function Assert-HbCorePin([string]$Pin) {
    if ($Pin -cnotmatch '^[0-9a-f]{40}$') { throw 'An immutable lowercase 40-hex commit SHA is required' }
}

function Assert-R002ReviewedCoreSha([string]$Pin) {
    Assert-HbCorePin $Pin
    if ($Pin -cne $script:R002ReviewedCoreSha) { throw 'UnreviewedCorePin' }
}
function Assert-R002Event([string]$Event, [string]$Ref, [string]$Repository,
                           [string]$EventSha, [string]$BridgeSha, [string]$Protected) {
    Assert-HbCorePin $BridgeSha
    if ($Event -cnotin @('push','workflow_dispatch')) { throw 'UntrustedEvent' }
    if ($Ref -cne 'refs/heads/master') { throw 'UntrustedRef' }
    if ($Repository -cne 'yuriyyak23/HybridCPU-Compilers') { throw 'WrongEventRepository' }
    if ($Protected -cne 'true') { throw 'UnprotectedMasterRef' }
    if ($EventSha -cne $BridgeSha) { throw 'WrongBridgeEventSha' }
}
function Get-R002Sha256([string]$Value) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($Value))).ToLowerInvariant()
}
function Get-R002ReceiptDirectory {
    if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP) -or
        -not (Test-Path -LiteralPath $env:RUNNER_TEMP -PathType Container)) {
        throw 'RunnerTempUnavailable'
    }
    $dir = Join-Path (Resolve-Path -LiteralPath $env:RUNNER_TEMP).ProviderPath 'hb-r0-02-core-gate'
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -Path $dir -ItemType Directory | Out-Null
    }
    $item = Get-Item -LiteralPath $dir -Force
    if (-not $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'UnsafeReceiptDirectory'
    }
    return $dir
}
function Write-R002Receipt([string]$Stage, [string]$State, [string]$Reason,
                           [string]$RequestedSha, [int]$ExitCode) {
    $dir = Get-R002ReceiptDirectory
    $receipt = [ordered]@{
        Schema = 'hybridcpu.hardwarebridge.r0-02-core-gate/v1'
        Stage = $Stage; State = $State; Reason = $Reason
        ReviewedCoreSha = $script:R002ReviewedCoreSha
        BridgeHead = $(if ($env:GITHUB_SHA -cmatch '^[0-9a-f]{40}$') { $env:GITHUB_SHA } else { 'Invalid' })
        RequestedCoreSha = $(if ($RequestedSha -cmatch '^[0-9a-f]{40}$') { $RequestedSha } else { 'Invalid' })
        EventClass = $(if ($env:GITHUB_EVENT_NAME -cin @('push','workflow_dispatch','pull_request')) { $env:GITHUB_EVENT_NAME } else { 'Unknown' })
        CoreImporter = $(if ($Stage -eq 'Downstream' -and $State -eq 'Passed') { 'PassedUnqualifiedContractOnly' } elseif ($Stage -eq 'Downstream') { 'Failed' } else { 'NotRun' })
        HardwareQualification = 'NotRun'
        DurationMs = $script:R002Clock.ElapsedMilliseconds
        CommandChain = @(
            [ordered]@{ Name='Preflight'; State=$(if ($Stage -eq 'Preflight') { $State } else { 'NotRun' }) },
            [ordered]@{ Name='Checkout'; State=$(if ($Stage -eq 'Checkout') { $State } else { 'NotRun' }) },
            [ordered]@{ Name='CoreReader'; State=$(if ($Stage -eq 'Downstream') { $State } else { 'NotRun' }); ExitCode=$(if ($Stage -eq 'Downstream') { $ExitCode } else { $null }) }
        )
        GitInspections = @($script:R002GitCommands.ToArray())
    }
    $json = $receipt | ConvertTo-Json -Depth 10
    $path = Join-Path $dir ($Stage.ToLowerInvariant() + '.json')
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) }
    finally { $stream.Dispose() }
    Write-Host "R0-02 $Stage=$State reason=$Reason receiptSHA256=$(Get-R002Sha256 $json)"
}
function Assert-R002PriorReceipt([string]$Stage) {
    $path = Join-Path (Get-R002ReceiptDirectory) ($Stage.ToLowerInvariant() + '.json')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'PriorGateNotRun' }
    $r = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($r.Schema -cne 'hybridcpu.hardwarebridge.r0-02-core-gate/v1' -or
        $r.Stage -cne $Stage -or $r.State -cne 'Passed' -or
        $r.ReviewedCoreSha -cne $script:R002ReviewedCoreSha) {
        throw 'PriorGateNotPassed'
    }
}
function Assert-R002UniqueJson([System.Text.Json.JsonElement]$Element) {
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $seen.Add($property.Name)) { throw 'DuplicatePairedReceiptField' }
            Assert-R002UniqueJson $property.Value
        }
    } elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        foreach ($value in $Element.EnumerateArray()) { Assert-R002UniqueJson $value }
    }
}
function Assert-R002PairedReceipt([string]$Path, [string]$CoreSha) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'PairedReceiptMissing' }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -gt 4194304) {
        throw 'UnsafePairedReceipt'
    }
    $json = [IO.File]::ReadAllText($item.FullName)
    $doc = [System.Text.Json.JsonDocument]::Parse($json)
    try {
        Assert-R002UniqueJson $doc.RootElement
        $r = $json | ConvertFrom-Json
        if ($r.Schema -cne 'hybridcpu.hardwarebridge.qa-receipt/v1' -or
            $r.CoreHead -cne $CoreSha -or
            $r.Matrix.StrictCoreImporter -cne 'Passed' -or
            $r.Matrix.CompanionBuild -cne 'Passed' -or
            $r.CoreSourceUnchanged -ne $true -or $r.SourceUnchanged -ne $true) {
            throw 'PairedReceiptNotPassed'
        }
    } finally { $doc.Dispose() }
}
function Assert-HbCoreCredential([string]$Credential) {
    if ([string]::IsNullOrWhiteSpace($Credential)) { throw 'MissingCoreReadCredential' }
    if ($Credential.Length -lt 12 -or $Credential.Length -gt 4096) { throw 'MalformedCoreReadCredential' }
    foreach ($character in $Credential.ToCharArray()) {
        if ([int]$character -lt 33 -or [int]$character -gt 126) { throw 'MalformedCoreReadCredential' }
    }
}
function Assert-HbCoreCheckoutOutcome([string]$Outcome) {
    if ($Outcome -cne 'success') {
        throw 'Private Core checkout failed (expired, unauthorized or wrong repo/SHA); Core importer NotRun'
    }
}
function Invoke-HbCoreGit([string]$Root, [string[]]$GitArguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'git'
    $commandClock = [Diagnostics.Stopwatch]::StartNew()
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.UseShellExecute = $false
    $start.ArgumentList.Add('-C')
    $start.ArgumentList.Add($Root)
    foreach ($gitArgument in $GitArguments) { $start.ArgumentList.Add($gitArgument) }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Cannot inspect Git checkout' }
    try {
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd() # hashed only; never logged
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw 'Git inspection timed out'
        }
        $script:R002GitCommands.Add([ordered]@{
            Name = 'git-' + $GitArguments[0]
            ExitCode = $process.ExitCode
            DurationMs = $commandClock.ElapsedMilliseconds
            ExecutableSha256 = (Get-FileHash -LiteralPath (Get-Command git -CommandType Application).Source -Algorithm SHA256).Hash.ToLowerInvariant()
            StdoutSha256 = Get-R002Sha256 $stdout
            StderrSha256 = Get-R002Sha256 $stderr
        })
        if ($process.ExitCode -ne 0) { throw 'GitInspectionFailed' }
        return $stdout.TrimEnd([char]13,[char]10)
    }
    finally { $process.Dispose() }
}
function Assert-HbCoreCheckout([string]$Root, [string]$Sha, [string]$Project) {
    Assert-HbCorePin $Sha
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw 'Core checkout missing' }
    $rootPath = (Resolve-Path -LiteralPath $Root).ProviderPath
    $gitTop = Invoke-HbCoreGit $rootPath @('rev-parse','--show-toplevel')
    if ([IO.Path]::GetFullPath($rootPath).TrimEnd([char[]]@([char]'/',[char]'\')) -ine
        [IO.Path]::GetFullPath($gitTop).TrimEnd([char[]]@([char]'/',[char]'\'))) {
        throw 'Core checkout must be an independent Git root'
    }
    $origin = Invoke-HbCoreGit $rootPath @('remote','get-url','origin')
    if ($origin -cnotin @('https://github.com/yuriyyak23/CircuitWeave.git','https://github.com/yuriyyak23/CircuitWeave')) {
        throw 'WrongCoreRepositoryOrigin'
    }
    $actualSha = Invoke-HbCoreGit $rootPath @('rev-parse','HEAD')
    if ($actualSha -cne $Sha) { throw "Wrong pinned Core SHA: expected $Sha; actual $actualSha" }
    $shallow = Invoke-HbCoreGit $rootPath @('rev-parse','--is-shallow-repository')
    if ($shallow -cne 'false') { throw 'Shallow/incompatible Core checkout refused' }
    if ((Invoke-HbCoreGit $rootPath @('status','--porcelain=v1','--untracked-files=all'))) {
        throw 'Core checkout must be source-clean before test'
    }
    if ([IO.Path]::IsPathRooted($Project) -or
        $Project -match '(^|[\\/])\.\.([\\/]|$)' -or
        $Project -notmatch '^tests[/\\]CircuitWeave\.CWINT03\.BridgeImport[/\\][^/\\]+\.csproj$') {
        throw 'Unauthorized Core importer test project path'
    }
    $parts = $Project -split '[\\/]'
    $cursor = $rootPath
    foreach ($part in $parts) {
        $cursor = Join-Path $cursor $part
        if (-not (Test-Path -LiteralPath $cursor)) { throw 'Pinned Core importer test project absent' }
        $item = Get-Item -LiteralPath $cursor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Linked Core project path is forbidden'
        }
    }
    if (-not (Test-Path -LiteralPath $cursor -PathType Leaf)) {
        throw 'Pinned Core importer project is not a regular file'
    }
}

function Assert-HbCoreSelfTests {
    function Must-Refuse([scriptblock]$Action, [string]$Label) {
        $refused = $false
        try { & $Action } catch { $refused = $true }
        if (-not $refused) { throw "Negative fixture admitted: $Label" }
        Write-Host "PASS synthetic negative: $Label"
    }
    Must-Refuse { Assert-HbCoreCredential '' } 'missing token'
    Must-Refuse { Assert-HbCoreCredential '   ' } 'empty credential'
    Must-Refuse { Assert-HbCoreCredential ('synthetic-value' + [char]10) } 'malformed secret-env'
    Must-Refuse { Assert-R002ReviewedCoreSha ('0' * 40) } 'unreviewed Core pin'
    Must-Refuse { Assert-R002ReviewedCoreSha $script:R002ReviewedCoreSha.ToUpperInvariant() } 'uppercase SHA'
    $fakeSha = 'a' * 40
    Assert-R002Event 'push' 'refs/heads/master' 'yuriyyak23/HybridCPU-Compilers' $fakeSha $fakeSha 'true'
    Assert-R002Event 'workflow_dispatch' 'refs/heads/master' 'yuriyyak23/HybridCPU-Compilers' $fakeSha $fakeSha 'true'
    Must-Refuse { Assert-R002Event 'pull_request' 'refs/heads/master' 'yuriyyak23/HybridCPU-Compilers' $fakeSha $fakeSha 'true' } 'untrusted PR'
    Must-Refuse { Assert-R002Event 'fake_event' 'refs/heads/master' 'yuriyyak23/HybridCPU-Compilers' $fakeSha $fakeSha 'true' } 'fake event'
    Must-Refuse { Assert-R002Event 'push' 'refs/heads/feature' 'yuriyyak23/HybridCPU-Compilers' $fakeSha $fakeSha 'true' } 'non-master ref'
    Must-Refuse { Assert-R002Event 'push' 'refs/heads/master' 'other/repo' $fakeSha $fakeSha 'true' } 'wrong repository'
    Must-Refuse { Assert-R002Event 'push' 'refs/heads/master' 'yuriyyak23/HybridCPU-Compilers' $fakeSha $fakeSha 'false' } 'master missing protection'
    Must-Refuse { Assert-R002Event 'push' 'refs/heads/master' 'yuriyyak23/HybridCPU-Compilers' ('b' * 40) $fakeSha 'true' } 'event SHA mismatch'
    $marker = 'SYNTHETIC_SECRET_NEVER_ECHO_R002'
    try { Assert-HbCoreCredential ($marker + [char]10) }
    catch {
        if ($_.Exception.Message.Contains($marker)) { throw 'SecretLeakInDiagnostics' }
    }
    Must-Refuse { Assert-HbCoreCheckoutOutcome 'failure' } 'expired/unauthorized token checkout outcome'
    Must-Refuse { Assert-HbCorePin ('0' * 39) } 'truncated SHA'
    Must-Refuse { Assert-HbCorePin ('Z' * 40) } 'invalid SHA characters'
    $parent = Join-Path ([IO.Path]::GetTempPath()) ("hb-int-06-core-gate-" + [Guid]::NewGuid().ToString('N'))
    $fixture = Join-Path $parent 'synthetic-core'
    try {
        New-Item -Path $fixture -ItemType Directory -Force | Out-Null
        $project = 'tests/CircuitWeave.CWINT03.BridgeImport/CircuitWeave.CWINT03.BridgeImport.csproj'
        $projectFile = Join-Path $fixture $project
        New-Item -Path (Split-Path -Parent $projectFile) -ItemType Directory -Force | Out-Null
        [IO.File]::WriteAllText($projectFile, '<Project Sdk="Microsoft.NET.Sdk" />')
        $null = Invoke-HbCoreGit $fixture @('init','-q')
        $null = Invoke-HbCoreGit $fixture @('remote','add','origin','https://github.com/yuriyyak23/CircuitWeave.git')
        $null = Invoke-HbCoreGit $fixture @('add','.')
        $null = Invoke-HbCoreGit $fixture @('-c','user.name=QA','-c','user.email=qa@example.invalid',
            'commit','-q','-m','synthetic-only')
        $sha = Invoke-HbCoreGit $fixture @('rev-parse','HEAD')
        Assert-HbCoreCheckout $fixture $sha $project
        Must-Refuse { Assert-HbCoreCheckout $fixture ('0' * 40) $project } 'wrong Core SHA'
        Must-Refuse { Assert-HbCoreCheckout $fixture $sha '../outside.csproj' } 'unauthorized project traversal'
        $null = Invoke-HbCoreGit $fixture @('remote','set-url','origin','https://github.com/other/private.git')
        Must-Refuse { Assert-HbCoreCheckout $fixture $sha $project } 'wrong Core repo origin'
        $null = Invoke-HbCoreGit $fixture @('remote','set-url','origin','https://github.com/yuriyyak23/CircuitWeave.git')
        Must-Refuse { Assert-HbCoreCheckout $fixture $sha 'tests/Missing/Missing.csproj' } 'missing importer artifact/project'
        [IO.File]::AppendAllText($projectFile, ' ')
        Must-Refuse { Assert-HbCoreCheckout $fixture $sha $project } 'tampered source bytes/dirty worktree'
        $null = Invoke-HbCoreGit $fixture @('checkout','--',$project)
        $shallowFile = Join-Path $fixture '.git/shallow'
        [IO.File]::WriteAllText($shallowFile, $sha + [char]10)
        Must-Refuse { Assert-HbCoreCheckout $fixture $sha $project } 'shallow Core checkout'
        Remove-Item -LiteralPath $shallowFile -Force
        Assert-HbCoreCheckout $fixture $sha $project
        Must-Refuse { Assert-R002PairedReceipt (Join-Path $parent 'missing.json') $sha } 'missing downstream receipt'
        $duplicate = Join-Path $parent 'duplicate.json'
        [IO.File]::WriteAllText($duplicate, '{"Schema":"x","Schema":"y"}')
        Must-Refuse { Assert-R002PairedReceipt $duplicate $sha } 'duplicate JSON property'
        Write-Host 'PASS: R0-02 trust/secret/checkout/downstream synthetic negatives (NO private Core/Golden used).'
    }
    finally {
        if (Test-Path -LiteralPath $parent) { Remove-Item -LiteralPath $parent -Recurse -Force }
    }
}
. (Join-Path $PSScriptRoot 'R0_02_TrustExtension.ps1')
if ($Mode -eq 'SelfTest') {
    try { Assert-HbCoreSelfTests; Invoke-R0_02SyntheticNegatives; exit 0 }
    catch { Write-Host '::error title=R0-02 synthetic tests::SyntheticTestsFailed'; exit 1 }
}
$state = 'Blocked'
$reason = 'GateRefused'
try {
    switch ($Mode) {
        'Preflight' {
            Assert-R002ReviewedCoreSha $ExpectedCoreSha
            Assert-R002Event $env:GITHUB_EVENT_NAME $env:GITHUB_REF $env:GITHUB_REPOSITORY $env:GITHUB_SHA $ExpectedBridgeSha $env:GITHUB_REF_PROTECTED
            Assert-R0_02DispatchTrust
            Assert-R0_02BridgeCheckout $BridgeRoot $ExpectedBridgeSha
            Assert-HbCoreCredential $env:HB_CORE_READ_TOKEN
            Assert-R0_02FineGrainedToken $env:HB_CORE_READ_TOKEN
            $state = 'Passed'
            $reason = 'TrustedAndCredentialPresent'
        }
        'Checkout' {
            Assert-R002ReviewedCoreSha $ExpectedCoreSha
            Assert-R002PriorReceipt 'Preflight'
            Assert-R0_02PriorReceipt 'Preflight' $env:GITHUB_SHA
            Assert-R0_02DispatchTrust
            Assert-R0_02BridgeCheckout $BridgeRoot $env:GITHUB_SHA
            Assert-HbCoreCheckoutOutcome $CheckoutOutcome
            Assert-R0_02CoreCheckout $CoreRoot $ExpectedCoreSha $CoreImporterProject
            $state = 'Passed'
            $reason = 'ExactCleanCoreCheckout'
        }
        'Downstream' {
            Assert-R002ReviewedCoreSha $ExpectedCoreSha
            Assert-R002PriorReceipt 'Preflight'
            Assert-R002PriorReceipt 'Checkout'
            Assert-R0_02PriorReceipt 'Preflight' $env:GITHUB_SHA
            Assert-R0_02PriorReceipt 'Checkout' $env:GITHUB_SHA
            Assert-R0_02DispatchTrust
            Assert-R0_02BridgeCheckout $BridgeRoot $env:GITHUB_SHA
            Assert-R0_02CoreCheckout $CoreRoot $ExpectedCoreSha $CoreImporterProject
            if ($DownstreamExitCode -lt 0) { throw 'DownstreamNotRun' }
            if ($DownstreamExitCode -ne 0) { throw 'DownstreamValidatorFailed' }
            Assert-R002PairedReceipt $PairedReceiptPath $ExpectedCoreSha
            Assert-R0_02PairedReceipt $PairedReceiptPath $ExpectedCoreSha $env:GITHUB_SHA $CoreRoot
            $state = 'Passed'
            $reason = 'PairedCoreReaderPassedUnqualified'
        }
    }
}
catch {
    $candidate = [string]$_.Exception.Message
    $reason = $(if ($candidate -cmatch '^[A-Za-z][A-Za-z0-9]{2,63}$') { $candidate } else { 'GateRefused' })
    if ($Mode -eq 'Downstream' -and $reason -cnotin @('DownstreamNotRun','PriorGateNotRun')) { $state = 'Failed' }
    Write-Host "::error title=R0-02 Core gate::$Mode $state ($reason); Core importer not qualified"
}
try { Write-R002Receipt $Mode $state $reason $ExpectedCoreSha $DownstreamExitCode }
catch { Write-Host '::error title=R0-02 Core gate::ReceiptWriteFailed'; exit 1 }
if ($state -eq 'Passed') { exit 0 }
if ($state -eq 'Blocked') { exit 2 }
exit 1
