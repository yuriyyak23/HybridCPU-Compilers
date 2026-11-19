#requires -Version 7.3
# Negative/mutation probes use ONLY a fresh synthetic Git fixture under ScratchRoot.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ScratchRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HbInt06.SourceAudit.ps1')
if (Test-Path -LiteralPath $ScratchRoot) { throw 'Mutation fixture root must not pre-exist' }
New-Item -Path $ScratchRoot -ItemType Directory | Out-Null
$fixture = Join-Path $ScratchRoot 'fixture-golden'
New-Item -Path $fixture -ItemType Directory | Out-Null
[IO.File]::WriteAllText((Join-Path $fixture 'Model.cs'), 'class TestOnlyGolden { }', [Text.UTF8Encoding]::new($false))
function Run-Git([string[]]$GitArgs) {
    & git -C $fixture @GitArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Synthetic fixture git command failed with $LASTEXITCODE" }
}
Run-Git @('init','-q')
Run-Git @('add','Model.cs')
Run-Git @('-c','user.name=HB-QA','-c','user.email=qa@example.invalid',
    'commit','-q','-m','synthetic QA fixture only')
function Must-Refuse([scriptblock]$Action, [string]$Label) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw "FAIL: mutation admitted: $Label" }
    Write-Host "PASS negative: $Label"
}
$before = Get-HbSourceState $fixture
Assert-HbExpectedSha $before.Head $before.Head
Must-Refuse { Assert-HbExpectedSha $before.Head ('0' * 40) } 'wrong pinned Git SHA'
Must-Refuse { Assert-HbOutputOutside (Join-Path $fixture 'obj') @($fixture) } 'Golden output write location'
Must-Refuse { Assert-HbOutputOutside (Join-Path $fixture '../fixture-golden/../fixture-golden') @($fixture) } 'canonicalized traversal'
$fake = [pscustomobject]@{
    Name = 'fake-tool'; Status = 'Passed'; Started = $false; ExitCode = 0
    Arguments = @(); ExecutableSha256 = $null; StdoutSha256 = $null; StderrSha256 = $null
}
Must-Refuse { Assert-HbCommandReceipt $fake } 'fake command and zero-step PASS'
$fake.Started = $true
$fake.Arguments = @('run')
Must-Refuse { Assert-HbCommandReceipt $fake } 'missing executable and log hashes'
$fake.Name = 'native-not-approved'
$fake.ExecutableSha256 = 'A' * 64
$fake.StdoutSha256 = 'B' * 64
$fake.StderrSha256 = 'C' * 64
Must-Refuse { Assert-HbCommandReceipt $fake } 'Bridge native PASS without approval'
[IO.File]::AppendAllText((Join-Path $fixture 'Model.cs'), '// mutation')
$changed = Get-HbSourceState $fixture
Must-Refuse { Assert-HbSourceUnchanged $before $changed } 'source changed during capture-validation'
Run-Git @('checkout','--','Model.cs')
$clean = Get-HbSourceState $fixture
Assert-HbSourceUnchanged $before $clean
[IO.File]::WriteAllText((Join-Path $fixture 'accidental.g.cs'), '// generated in Golden')
$written = Get-HbSourceState $fixture
Must-Refuse { Assert-HbSourceUnchanged $before $written } 'accidental generated Golden file'
Remove-Item -LiteralPath (Join-Path $fixture 'accidental.g.cs')
Assert-HbSourceUnchanged $before (Get-HbSourceState $fixture)
$linked = Join-Path $fixture 'linked-source.cs'
try {
    New-Item -Path $linked -ItemType SymbolicLink -Target (Join-Path $fixture 'Model.cs') -ErrorAction Stop | Out-Null
    Must-Refuse { Get-HbSourceState $fixture | Out-Null } 'linked Golden source entry'
    Remove-Item -LiteralPath $linked -Force
    Assert-HbSourceUnchanged $before (Get-HbSourceState $fixture)
}
catch [System.UnauthorizedAccessException] {
    Write-Host 'NOTRUN: symlink creation refused by OS privileges (not PASS)'
}
catch [System.Management.Automation.ItemNotFoundException] {
    Write-Host 'NOTRUN: symlink creation not available (not PASS)'
}
$timeout = Invoke-HbRecordedCommand 'timeout-probe' 'pwsh' @(
    '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 4'
) $ScratchRoot $ScratchRoot 1
if ($timeout.Status -ne 'Blocked' -or -not $timeout.Started -or $null -ne $timeout.ExitCode) {
    throw 'FAIL: timeout was not recorded as Blocked with killed child'
}
Write-Host 'PASS negative: real timeout and child kill recorded Blocked'
Write-Host 'PASS: HB-INT-06 synthetic mutations; no Golden repository touched.'
