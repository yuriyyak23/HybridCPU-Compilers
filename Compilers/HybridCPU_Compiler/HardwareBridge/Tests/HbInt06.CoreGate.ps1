#requires -Version 7.3
# HB-INT-06: no-secret Core read-access preflight and immutable checkout guard.
# Does not parse Core sources or grant qualification; all failed gates exit 2 (Blocked).
[CmdletBinding()]
param(
    [ValidateSet('Preflight','Checkout','SelfTest')][string]$Mode = 'Preflight',
    [string]$ExpectedBridgeSha = '',
    [string]$ExpectedCoreSha = '',
    [string]$CoreRoot = '',
    [string]$CheckoutOutcome = 'success',
    [string]$CoreImporterProject = 'tests/CircuitWeave.CWINT03.BridgeImport/CircuitWeave.CWINT03.BridgeImport.csproj'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-HbCorePin([string]$Pin) {
    if ($Pin -cnotmatch '^[0-9a-f]{40}$') { throw 'An immutable lowercase 40-hex commit SHA is required' }
}
function Assert-HbCoreCredential([string]$Credential) {
    if ([string]::IsNullOrWhiteSpace($Credential)) {
        throw 'HB_INT_06_CORE_READ_TOKEN is unavailable (no token or untrusted event); Core importer NotRun'
    }
}
function Assert-HbCoreCheckoutOutcome([string]$Outcome) {
    if ($Outcome -cne 'success') {
        throw 'Private Core checkout failed (expired, unauthorized or wrong repo/SHA); Core importer NotRun'
    }
}
function Invoke-HbCoreGit([string]$Root, [string[]]$Args) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'git'
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.UseShellExecute = $false
    $start.ArgumentList.Add('-C')
    $start.ArgumentList.Add($Root)
    foreach ($arg in $Args) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Cannot inspect Git checkout' }
    try {
        $stdout = $process.StandardOutput.ReadToEnd()
        $null = $process.StandardError.ReadToEnd() # no auth/remote details in diagnostics
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw 'Git inspection timed out'
        }
        if ($process.ExitCode -ne 0) { throw "Git inspection failed (exit $($process.ExitCode))" }
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
        $null = Invoke-HbCoreGit $fixture @('add','.')
        $null = Invoke-HbCoreGit $fixture @('-c','user.name=QA','-c','user.email=qa@example.invalid',
            'commit','-q','-m','synthetic-only')
        $sha = Invoke-HbCoreGit $fixture @('rev-parse','HEAD')
        Assert-HbCoreCheckout $fixture $sha $project
        Must-Refuse { Assert-HbCoreCheckout $fixture ('0' * 40) $project } 'wrong Core SHA'
        Must-Refuse { Assert-HbCoreCheckout $fixture $sha '../outside.csproj' } 'unauthorized project traversal'
        Must-Refuse { Assert-HbCoreCheckout $fixture $sha 'tests/Missing/Missing.csproj' } 'missing importer artifact/project'
        [IO.File]::AppendAllText($projectFile, ' ')
        Must-Refuse { Assert-HbCoreCheckout $fixture $sha $project } 'tampered source bytes/dirty worktree'
        $null = Invoke-HbCoreGit $fixture @('checkout','--',$project)
        $shallowFile = Join-Path $fixture '.git/shallow'
        [IO.File]::WriteAllText($shallowFile, $sha + [char]10)
        Must-Refuse { Assert-HbCoreCheckout $fixture $sha $project } 'shallow Core checkout'
        Remove-Item -LiteralPath $shallowFile -Force
        Assert-HbCoreCheckout $fixture $sha $project
        Write-Host 'PASS: HB-INT-06 no-secret checkout gate fixture (NO private Core/Golden used).'
    }
    finally {
        if (Test-Path -LiteralPath $parent) { Remove-Item -LiteralPath $parent -Recurse -Force }
    }
}
try {
    switch ($Mode) {
        'SelfTest' { Assert-HbCoreSelfTests; exit 0 }
        'Preflight' {
            Assert-HbCorePin $ExpectedBridgeSha
            Assert-HbCorePin $ExpectedCoreSha
            if ($env:GITHUB_EVENT_NAME -notin @('push','workflow_dispatch') -or
                $env:GITHUB_REF -cne 'refs/heads/master') {
                throw 'Private Core credential is reserved for trusted master push/dispatch; PR gate Blocked'
            }
            Assert-HbCoreCredential $env:HB_CORE_READ_TOKEN
            Write-Host "Configured credential present (authorization NOT inferred); Bridge SHA=$ExpectedBridgeSha Core SHA=$ExpectedCoreSha"
        }
        'Checkout' {
            Assert-HbCoreCheckoutOutcome $CheckoutOutcome
            Assert-HbCoreCheckout $CoreRoot $ExpectedCoreSha $CoreImporterProject
            Write-Host "PASS read-only Git checkout identity: Core SHA=$ExpectedCoreSha full-depth clean checkout; test project present"
        }
    }
}
catch {
    Write-Host "::error title=HB-INT-06 Blocked::$($_.Exception.Message)"
    exit 2
}
