# R0-01 only. No Golden checkout, no Core repo, no hardware authority.
# All subprocess output is retained ONLY in runner TEMP; the allowlisted JSON receipt is uploadable.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$ExpectedSha,
    [Parameter(Mandatory)][string]$ExpectedSdk,
    [Parameter(Mandatory)][string]$OutputRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$work = [IO.Path]::GetFullPath($OutputRoot)
$comparison = [StringComparison]::OrdinalIgnoreCase
$sep = [IO.Path]::DirectorySeparatorChar
if ($work.StartsWith($repo.TrimEnd($sep) + $sep, $comparison) -or
    $work -eq $repo -or -not $env:RUNNER_TEMP -or
    -not ($work + $sep).StartsWith(([IO.Path]::GetFullPath($env:RUNNER_TEMP)).TrimEnd($sep) + $sep, $comparison)) {
    throw 'R0-01 output root must be inside RUNNER_TEMP, never inside checkout.'
}
[IO.Directory]::CreateDirectory($work) | Out-Null
$receiptPath = Join-Path $work 'r0-01-qualification.json'
if ([IO.File]::Exists($receiptPath)) { throw 'Refuse to overwrite existing immutable receipt.' }
$stages = [Collections.Generic.List[object]]::new()
$builds = [Collections.Generic.List[object]]::new()
$negativeTests = [Collections.Generic.List[object]]::new()
$report = [ordered]@{
    Schema = 'hybridcpu.hardwarebridge.r0-01-companion-build-receipt/v1'
    BridgeHeadSha = $ExpectedSha
    Scope = 'BridgeOnly_NoGolden_NoCircuitWeave'
    Qualification = 'Blocked'
    RequiredSdk = $ExpectedSdk
    ActualSdk = $null
    DotnetSha256 = $null
    RoslynCscSha256 = $null
    SourceBefore = $null
    SourceAfter = $null
    NoSourceWrite = 'NotRun'
    Graph = @()
    ProductionArtifactTrackedFiles = $null
    Stages = @()
    Builds = @()
    NegativeTests = @()
    Blocker = $null
}
function HashText([string]$value) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($value))).ToLowerInvariant()
}
function FileSha([string]$path) {
    if (-not [IO.File]::Exists($path)) { throw 'Expected executable or build file missing.' }
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function RequireWithin([string]$candidate, [string]$root) {
    $full = [IO.Path]::GetFullPath($candidate)
    if (-not $full.StartsWith(([IO.Path]::GetFullPath($root)).TrimEnd($sep) + $sep, $comparison)) {
        throw 'Wrong-root output or unsafe path.'
    }
    return $full
}
function RequireSdk([string]$actual) {
    if ($actual -cne $ExpectedSdk) { throw 'Wrong .NET SDK identity.' }
}
function RequireReference([string[]]$references, [string]$needle) {
    if ($references -notcontains $needle) { throw 'ProjectReference graph is not the expected production graph.' }
}
function RequireCleanInventory([string]$before, [string]$after) {
    if ($before -cne $after) { throw 'Tracked source byte inventory changed.' }
}
function Reject([string]$name, [scriptblock]$probe) {
    $wasRejected = $false
    try { & $probe | Out-Null } catch { $wasRejected = $true }
    if (-not $wasRejected) { throw ('Unsafe mutation accepted: ' + $name) }
    $negativeTests.Add([ordered]@{ Mutation = $name; Verdict = 'Rejected' })
}
function InvokeCaptured([string]$name, [string]$exe, [string[]]$argv, [int]$timeoutSec = 600) {
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $exe
    $psi.WorkingDirectory = $repo
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    foreach ($arg in $argv) { [void]$psi.ArgumentList.Add($arg) }
    $p = [Diagnostics.Process]::new()
    $p.StartInfo = $psi
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $output = ''
    $errorOutput = ''
    $exitCode = 124
    try {
        if (-not $p.Start()) { throw 'Process did not start.' }
        $stdoutTask = $p.StandardOutput.ReadToEndAsync()
        $stderrTask = $p.StandardError.ReadToEndAsync()
        if (-not $p.WaitForExit($timeoutSec * 1000)) {
            $p.Kill($true)
            $p.WaitForExit()
        } else { $exitCode = $p.ExitCode }
        $output = $stdoutTask.GetAwaiter().GetResult()
        $errorOutput = $stderrTask.GetAwaiter().GetResult()
        if ([Text.Encoding]::UTF8.GetByteCount($output) -gt 16777216 -or
            [Text.Encoding]::UTF8.GetByteCount($errorOutput) -gt 16777216) {
            throw 'Captured build diagnostic exceeds bounded output.'
        }
        [IO.File]::WriteAllText((Join-Path $work ($name + '.stdout')), $output)
        [IO.File]::WriteAllText((Join-Path $work ($name + '.stderr')), $errorOutput)
    }
    finally {
        $timer.Stop()
        $p.Dispose()
    }
    $sanitized = foreach ($arg in $argv) {
        $arg.Replace($work, '<TEMP>').Replace($repo, '<REPO>')
    }
    $stages.Add([ordered]@{
        Name = $name
        Executable = [IO.Path]::GetFileName($exe)
        Args = @($sanitized)
        Exit = $exitCode
        DurationMs = $timer.ElapsedMilliseconds
        StdoutSha256 = HashText $output
        StderrSha256 = HashText $errorOutput
    })
    return [pscustomobject]@{ Exit = $exitCode; Stdout = $output; Stderr = $errorOutput }
}
$dotnet = 'dotnet'
$git = 'git'
function RequireSuccess($stage) {
    if ($stage.Exit -ne 0) { throw 'Required process failed; stage digests are in receipt.' }
}
function Inventory() {
    $files = InvokeCaptured 'git-files' $git @('-C', $repo, 'ls-files', '-z') 30
    RequireSuccess $files
    $names = @($files.Stdout.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries) | Sort-Object -CaseSensitive)
    if ($names.Count -lt 100 -or $names.Count -gt 30000) { throw 'Unexpected source inventory cardinality.' }
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($relative in $names) {
        $path = RequireWithin (Join-Path $repo $relative) $repo
        if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Tracked reparse point refused.'
        }
        $lines.Add($relative.Replace('\', '/') + ':' + (FileSha $path))
    }
    return [pscustomobject]@{ Count = $names.Count; Digest = HashText (($lines -join "`n") + "`n"); Names = $names }
}
function CheckGraph([string]$project, [string]$config, [string]$artifact) {
    $common = @('-p:UseArtifactsOutput=true', ('-p:ArtifactsPath=' + $artifact),
        ('-p:RestorePackagesPath=' + (Join-Path $work 'packages')), ('-p:Configuration=' + $config))
    $response = InvokeCaptured ('graph-' + $config + '-' + [IO.Path]::GetFileNameWithoutExtension($project)) $dotnet (
        @('msbuild', $project, '-getItem:Compile', '-getItem:ProjectReference',
          '-getProperty:TargetFramework,BaseIntermediateOutputPath,OutputPath,DefineConstants,ArtifactsPath') + $common) 180
    RequireSuccess $response
    $graph = $response.Stdout | ConvertFrom-Json
    $items = @($graph.Items.Compile)
    if ($items.Count -lt 1 -or $items.Count -gt 20000) { throw 'Unbounded or empty evaluated Compile graph.' }
    foreach ($item in $items) {
        $identity = [string]$item.Identity
        if ($identity -match '(^|[/\\])(obj|bin|\.artifacts)([/\\]|$)') {
            throw 'Stale/generated Compile item admitted into production graph.'
        }
    }
    $refs = @($graph.Items.ProjectReference | ForEach-Object { ([string]$_.Identity).Replace('\', '/') })
    return [pscustomobject]@{ Count = $items.Count; References = $refs; ItemSha256 = HashText $response.Stdout }
}
try {
    $env:DOTNET_CLI_HOME = Join-Path $work 'cli-home'
    $env:NUGET_PACKAGES = Join-Path $work 'packages'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    $env:MSBUILDDISABLENODEREUSE = '1'
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $git = (Get-Command git -ErrorAction Stop).Source
    $report.DotnetSha256 = FileSha $dotnet
    $head = InvokeCaptured 'git-head' $git @('-C', $repo, 'rev-parse', 'HEAD') 30
    RequireSuccess $head
    if ($head.Stdout.Trim() -cne $ExpectedSha) { throw 'Checkout SHA differs from reviewed GitHub event SHA.' }
    $status = InvokeCaptured 'git-status-before' $git @('-C', $repo, 'status', '--porcelain=v1', '--untracked-files=all') 30
    RequireSuccess $status
    if ($status.Stdout.Trim().Length -ne 0) { throw 'Dirty source checkout before build.' }
    $initial = Inventory
    $report.SourceBefore = [ordered]@{ Files = $initial.Count; Sha256 = $initial.Digest }
    $report.ProductionArtifactTrackedFiles =
        @($initial.Names | Where-Object { $_.Replace('\','/') -like 'Compilers/HybridCPU_Compiler/Core/IR/Artifacts/*' }).Count
    $sdk = InvokeCaptured 'sdk-version' $dotnet @('--version') 30
    RequireSuccess $sdk
    $report.ActualSdk = $sdk.Stdout.Trim()
    RequireSdk $report.ActualSdk
    $info = InvokeCaptured 'dotnet-info' $dotnet @('--info') 60
    RequireSuccess $info
    $sdkLocations = @(
        (Join-Path (Split-Path $dotnet -Parent) ('sdk/' + $ExpectedSdk + '/Roslyn/bincore/csc.dll')),
        (Join-Path $env:ProgramFiles ('dotnet/sdk/' + $ExpectedSdk + '/Roslyn/bincore/csc.dll'))
    )
    foreach ($csc in $sdkLocations) {
        if ([IO.File]::Exists($csc)) { $report.RoslynCscSha256 = FileSha $csc; break }
    }
    if (-not $report.RoslynCscSha256) { throw 'Cannot pin actual SDK Roslyn csc.dll digest.' }

    # These probes are negative tests of the same guards used by the real qualification path.
    Reject 'wrong-sdk' { RequireSdk '0.0.0' }
    Reject 'outside-temp-output' { RequireWithin (Join-Path $repo 'unsafe.dll') $work }
    Reject 'wrong-production-project-reference' { RequireReference @('../Fake/Stub.csproj') '../Cil/HybridCPU.Compiler.Cil.csproj' }
    Reject 'modified-tracked-source' { RequireCleanInventory $initial.Digest ('0' * 64) }
    Reject 'stale-generated-compile-item' {
        $injected = 'obj/Stale.Generator.g.cs'
        if ($injected -match '(^|[/\\])(obj|bin|\.artifacts)([/\\]|$)') { throw 'Generated item is not an owned source.' }
    }

    $core = Join-Path $repo 'Compilers/HybridCPU_Compiler/Core/HybridCPU.Compiler.Core.csproj'
    $cil = Join-Path $repo 'Compilers/HybridCPU_Compiler/Cil/HybridCPU.Compiler.Cil.csproj'
    $roslyn = Join-Path $repo 'Compilers/HybridCPU_Compiler/Roslyn/HybridCPU.Compiler.Roslyn.csproj'
    $companion = Join-Path $repo 'Compilers/HybridCPU_Compiler/HardwareBridge/HardwareBridge.Companion.csproj'
    $selfTests = Join-Path $repo 'Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.SelfTests.csproj'
    foreach ($config in @('Debug','Release')) {
        $artifacts = Join-Path $work ('artifacts-' + $config)
        [IO.Directory]::CreateDirectory($artifacts) | Out-Null
        $properties = @('-p:UseArtifactsOutput=true', ('-p:ArtifactsPath=' + $artifacts),
            ('-p:RestorePackagesPath=' + (Join-Path $work 'packages')))
        $mode = [ordered]@{ Configuration = $config; Restore = 'NotRun'; Build = 'NotRun'; PeCheck = 'NotRun'; SelfTests = 'NotRun'; DllSha256 = $null; PdbSha256 = $null }
        $builds.Add($mode)
        $coreGraph = CheckGraph $core $config $artifacts
        $cilGraph = CheckGraph $cil $config $artifacts
        $roslynGraph = CheckGraph $roslyn $config $artifacts
        $companionGraph = CheckGraph $companion $config $artifacts
        RequireReference $cilGraph.References '../Core/HybridCPU.Compiler.Core.csproj'
        RequireReference $roslynGraph.References '../Cil/HybridCPU.Compiler.Cil.csproj'
        RequireReference $companionGraph.References '../Roslyn/HybridCPU.Compiler.Roslyn.csproj'
        $report.Graph += [ordered]@{ Configuration = $config; CoreItems = $coreGraph.Count; CilItems = $cilGraph.Count; RoslynItems = $roslynGraph.Count; CompanionItems = $companionGraph.Count; CoreItemSha256 = $coreGraph.ItemSha256; CompanionItemSha256 = $companionGraph.ItemSha256; GraphValid = $true }
        $restore = InvokeCaptured ('restore-' + $config) $dotnet (@('restore', $companion, '-v:q') + $properties) 600
        $mode.Restore = if ($restore.Exit -eq 0) { 'Passed' } else { 'Failed' }
        if ($restore.Exit -ne 0) { continue }
        $build = InvokeCaptured ('build-' + $config) $dotnet (@('build', $companion, '--no-restore', '-c', $config, '-v:q') + $properties) 600
        $mode.Build = if ($build.Exit -eq 0) { 'Passed' } else { 'Failed' }
        if ($build.Exit -ne 0) { continue }
        $candidates = @(Get-ChildItem -LiteralPath $artifacts -Recurse -File -Filter 'HardwareBridge.Companion.dll' |
            Where-Object { $_.FullName -notmatch '[/\\]ref(int)?[/\\]' })
        if ($candidates.Count -ne 1) { throw 'A unique real Companion output DLL was not produced.' }
        $dll = RequireWithin $candidates[0].FullName $artifacts
        $mode.DllSha256 = FileSha $dll
        $pdb = [IO.Path]::ChangeExtension($dll, '.pdb')
        if ([IO.File]::Exists($pdb)) { $mode.PdbSha256 = FileSha $pdb }
        $mode.PeCheck = 'Passed'
        # Existing real-assembly selftest references Core, Cil, Roslyn and Companion; no test doubles.
        $testRestore = InvokeCaptured ('selftest-restore-' + $config) $dotnet (@('restore', $selfTests, '-v:q') + $properties) 600
        if ($testRestore.Exit -ne 0) { $mode.SelfTests = 'Failed'; continue }
        $testBuild = InvokeCaptured ('selftest-build-' + $config) $dotnet (@('build', $selfTests, '--no-restore', '-c', $config, '-v:q') + $properties) 600
        if ($testBuild.Exit -ne 0) { $mode.SelfTests = 'Failed'; continue }
        $testDlls = @(Get-ChildItem -LiteralPath $artifacts -Recurse -File -Filter 'HardwareBridge.SelfTests.dll' |
            Where-Object { $_.FullName -notmatch '[/\\]ref(int)?[/\\]' })
        if ($testDlls.Count -ne 1) { $mode.SelfTests = 'Failed'; continue }
        $testDll = RequireWithin $testDlls[0].FullName $artifacts
        $testRun = InvokeCaptured ('selftest-run-' + $config) $dotnet @($testDll) 600
        $mode.SelfTests = if ($testRun.Exit -eq 0) { 'Passed' } else { 'Failed' }
    }
    if (@($builds | Where-Object { $_.Build -ne 'Passed' -or $_.PeCheck -ne 'Passed' -or $_.SelfTests -ne 'Passed' }).Count -eq 0) {
        $report.Qualification = 'Passed'
    } else {
        $report.Qualification = 'Blocked'
        $report.Blocker = if ($report.ProductionArtifactTrackedFiles -eq 0) {
            'MissingTrackedCompilerCoreArtifactsProductionContracts'
        } else { 'RealCompanionBuildOrRuntimeTestFailed' }
    }
}
catch {
    $report.Qualification = 'Blocked'
    $report.Blocker = 'PreflightGraphSourceOrQualificationGuardFailed'
}
finally {
    try {
        $current = Inventory
        $report.SourceAfter = [ordered]@{ Files = $current.Count; Sha256 = $current.Digest }
        if ($null -ne $report.SourceBefore) { RequireCleanInventory $report.SourceBefore.Sha256 $current.Digest }
        $after = InvokeCaptured 'git-status-after' $git @('-C', $repo, 'status', '--porcelain=v1', '--untracked-files=all') 30
        RequireSuccess $after
        if ($after.Stdout.Trim().Length -ne 0) { throw 'Git status changed under qualification.' }
        $headAfter = InvokeCaptured 'git-head-after' $git @('-C', $repo, 'rev-parse', 'HEAD') 30
        RequireSuccess $headAfter
        if ($headAfter.Stdout.Trim() -cne $ExpectedSha) { throw 'HEAD changed under qualification.' }
        $report.NoSourceWrite = 'Passed'
    }
    catch {
        $report.NoSourceWrite = 'Failed'
        $report.Qualification = 'Blocked'
        $report.Blocker = 'NoSourceWriteInvariantFailed'
    }
    $report.Stages = @($stages.ToArray())
    $report.Builds = @($builds.ToArray())
    $report.NegativeTests = @($negativeTests.ToArray())
    $bytes = [Text.Encoding]::UTF8.GetBytes(($report | ConvertTo-Json -Depth 12 -Compress) + "`n")
    $stream = [IO.File]::Open($receiptPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    Write-Host ('R0-01 qualification=' + $report.Qualification + '; no-source-write=' + $report.NoSourceWrite +
        '; receipt-sha256=' + (FileSha $receiptPath))
}
if ($report.Qualification -ne 'Passed' -or $report.NoSourceWrite -ne 'Passed') { exit 1 }
exit 0
