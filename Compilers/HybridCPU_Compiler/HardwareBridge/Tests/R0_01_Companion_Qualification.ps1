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
# A reused cache can carry counterfeit/stale outputs into an otherwise clean run.
if (@(Get-ChildItem -LiteralPath $work -Force).Count -ne 0) {
    throw 'R0-01 output root must be new and empty; shared/stale caches are forbidden.'
}
$receiptPath = Join-Path $work 'r0-01-qualification.json'
if ([IO.File]::Exists($receiptPath)) { throw 'Refuse to overwrite existing immutable receipt.' }
# A temporary global.json is the actual SDK resolver pin; a repo-root global.json is absent.
# It deliberately lives outside the checkout and is never uploaded.
$localSdkSelection = [ordered]@{
    sdk = [ordered]@{ version = $ExpectedSdk; rollForward = 'disable'; allowPrerelease = $true }
}
[IO.File]::WriteAllText((Join-Path $work 'global.json'),
    ($localSdkSelection | ConvertTo-Json -Depth 4 -Compress), [Text.Encoding]::UTF8)
$stages = [Collections.Generic.List[object]]::new()
$builds = [Collections.Generic.List[object]]::new()
$negativeTests = [Collections.Generic.List[object]]::new()
$report = [ordered]@{
    Schema = 'hybridcpu.hardwarebridge.r0-01-companion-build-receipt/v2'
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
    FailureKind = 'NotRun'
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
    # Lexical containment alone accepts a junction/reparse ancestor that escapes TEMP.
    $cursor = $full
    while ($cursor -and $cursor -ne [IO.Path]::GetPathRoot($cursor)) {
        if ([IO.File]::Exists($cursor) -or [IO.Directory]::Exists($cursor)) {
            if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Reparse/symlink path component refused.'
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if (-not $parent -or $parent -eq $cursor) { break }
        $cursor = $parent
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
    $psi.WorkingDirectory = $work
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
    # The SDK's evaluated intermediate and binary paths must be outside the source checkout.
    # Project references can have their own Directory.Build.props; inspect each independently.
    $evaluatedPaths = @{}
    foreach ($propertyName in @('BaseIntermediateOutputPath', 'OutputPath')) {
        $evaluatedPath = [string]$graph.Properties.$propertyName
        if ([string]::IsNullOrWhiteSpace($evaluatedPath)) {
            throw 'Required MSBuild output property is empty.'
        }
        if (-not [IO.Path]::IsPathFullyQualified($evaluatedPath)) {
            $evaluatedPath = Join-Path (Split-Path $project -Parent) $evaluatedPath
        }
        $evaluatedPaths[$propertyName] = RequireWithin $evaluatedPath $artifact
    }
    $items = @($graph.Items.Compile)
    if ($items.Count -lt 1 -or $items.Count -gt 20000) { throw 'Unbounded or empty evaluated Compile graph.' }
    foreach ($item in $items) {
        $identity = [string]$item.Identity
        if ($identity -match '(^|[/\\])(obj|bin|\.artifacts)([/\\]|$)') {
            throw 'Stale/generated Compile item admitted into production graph.'
        }
    }
    $refs = @($graph.Items.ProjectReference | ForEach-Object { ([string]$_.Identity).Replace('\', '/') })
    return [pscustomobject]@{
        Count = $items.Count
        OutputDirectory = $evaluatedPaths['OutputPath']
        References = $refs
        ItemSha256 = HashText $response.Stdout
        # Configuration-specific paths/defines are excluded from the source-closure identity.
        SourceSha256 = HashText ((@($items | ForEach-Object { ([string]$_.Identity).Replace('\\','/') } |
            Sort-Object -CaseSensitive) -join "`n") + "`n")
        DefineConstantsSha256 = HashText ([string]$graph.Properties.DefineConstants)
    }
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
    Reject 'debug-release-source-drift' { RequireCleanInventory ('a' * 64) ('b' * 64) }
    Reject 'short-lived-cache-drift' { RequireCleanInventory ('1' * 64) ('2' * 64) }
    Reject 'stale-generated-compile-item' {
        $injected = 'obj/Stale.Generator.g.cs'
        if ($injected -match '(^|[/\\])(obj|bin|\.artifacts)([/\\]|$)') { throw 'Generated item is not an owned source.' }
    }

    $core = Join-Path $repo 'Compilers/HybridCPU_Compiler/Core/HybridCPU.Compiler.Core.csproj'
    $cil = Join-Path $repo 'Compilers/HybridCPU_Compiler/Cil/HybridCPU.Compiler.Cil.csproj'
    $roslyn = Join-Path $repo 'Compilers/HybridCPU_Compiler/Roslyn/HybridCPU.Compiler.Roslyn.csproj'
    $companion = Join-Path $repo 'Compilers/HybridCPU_Compiler/HardwareBridge/HardwareBridge.Companion.csproj'
    $selfTests = Join-Path $repo 'Compilers/HybridCPU_Compiler/HardwareBridge/Tests/HardwareBridge.SelfTests.csproj'
    $qualifiedSources = @{}
    foreach ($config in @('Debug','Release')) {
        $artifacts = Join-Path $work ('artifacts-' + $config)
        [IO.Directory]::CreateDirectory($artifacts) | Out-Null
        $properties = @('-p:UseArtifactsOutput=true', ('-p:ArtifactsPath=' + $artifacts),
            ('-p:Configuration=' + $config),
            ('-p:RestorePackagesPath=' + (Join-Path $work 'packages')))
        $mode = [ordered]@{
            Configuration = $config; Restore = 'NotRun'; Build = 'NotRun'
            PeCheck = 'NotRun'; SelfTests = 'NotRun'; DllSha256 = $null; PdbSha256 = $null
            FailureKind = 'NotRun'; FirstFailedProject = $null
            ProjectBuilds = @(); AssemblySha256 = @()
        }
        $builds.Add($mode)
        $coreGraph = CheckGraph $core $config $artifacts
        $cilGraph = CheckGraph $cil $config $artifacts
        $roslynGraph = CheckGraph $roslyn $config $artifacts
        $companionGraph = CheckGraph $companion $config $artifacts
        RequireReference $cilGraph.References '../Core/HybridCPU.Compiler.Core.csproj'
        RequireReference $roslynGraph.References '../Cil/HybridCPU.Compiler.Cil.csproj'
        RequireReference $companionGraph.References '../Roslyn/HybridCPU.Compiler.Roslyn.csproj'
        $sourceClosure = @($coreGraph.SourceSha256, $cilGraph.SourceSha256,
            $roslynGraph.SourceSha256, $companionGraph.SourceSha256) -join ':'
        if ($qualifiedSources.ContainsKey('Debug') -and $config -eq 'Release') {
            RequireCleanInventory $qualifiedSources['Debug'] $sourceClosure
        }
        $qualifiedSources[$config] = $sourceClosure
        $report.Graph += [ordered]@{ Configuration = $config; CoreItems = $coreGraph.Count; CilItems = $cilGraph.Count; RoslynItems = $roslynGraph.Count; CompanionItems = $companionGraph.Count; CoreItemSha256 = $coreGraph.ItemSha256; CompanionItemSha256 = $companionGraph.ItemSha256; CoreDefinesSha256 = $coreGraph.DefineConstantsSha256; CompanionDefinesSha256 = $companionGraph.DefineConstantsSha256; GraphValid = $true }
        $restore = InvokeCaptured ('restore-' + $config) $dotnet (@('restore', $companion, '-v:q') + $properties) 600
        $mode.Restore = if ($restore.Exit -eq 0) { 'Passed' } else { 'Failed' }
        if ($restore.Exit -ne 0) { continue }
        # An actual Core->Cil->Roslyn->Companion progression, not a restore-only PASS.
        # Each build runs the *unchanged* production project, never a trimmed copy.
        $projects = @(
            @{ Name = 'Core'; Project = $core; Assembly = 'HybridCPU.Compiler.Core'; Graph = $coreGraph },
            @{ Name = 'Cil'; Project = $cil; Assembly = 'HybridCPU.Compiler.Cil'; Graph = $cilGraph },
            @{ Name = 'Roslyn'; Project = $roslyn; Assembly = 'HybridCPU.Compiler.Roslyn'; Graph = $roslynGraph },
            @{ Name = 'Companion'; Project = $companion; Assembly = 'HardwareBridge.Companion'; Graph = $companionGraph }
        )
        $builtAll = $true
        foreach ($entry in $projects) {
            $build = InvokeCaptured ('build-' + $config + '-' + $entry.Name) $dotnet (
                @('build', $entry.Project, '--no-restore', '-c', $config, '-v:q') + $properties) 600
            $mode.ProjectBuilds += [ordered]@{ Name = $entry.Name; Exit = $build.Exit }
            if ($build.Exit -ne 0) {
                $mode.Build = 'Failed'
                $mode.FirstFailedProject = $entry.Name
                $unresolvedArtifacts = $report.ProductionArtifactTrackedFiles -eq 0 -and
                    ($build.Stdout -match 'CS0234|CS0246') -and
                    ($build.Stdout -match 'Core[.]IR[.]Artifacts|CompilerEmissionPackage|CompilerSidebandEnvelope|DescriptorEnvelope|TypedSlotFactsEnvelope|CompilerEvidenceEnvelope')
                $mode.FailureKind = if ($entry.Name -eq 'Core' -and $unresolvedArtifacts) {
                    'SourceDependencyMissing'
                } else { 'BuildFailed' }
                $builtAll = $false
                break
            }
        }
        if (-not $builtAll) { continue }
        $mode.Build = 'Passed'
        $peFiles = @{}
        foreach ($entry in $projects) {
            $projectOutput = $entry.Graph.OutputDirectory
            $candidates = @(
                if ([IO.Directory]::Exists($projectOutput)) {
                    Get-ChildItem -LiteralPath $projectOutput -Recurse -File -Filter ($entry.Assembly + '.dll') |
                        Where-Object { $_.FullName -notmatch '[/\\]ref(int)?[/\\]' }
                }
            )
            if ($candidates.Count -ne 1) {
                $mode.FailureKind = 'RealPEMissing'
                break
            }
            $dll = RequireWithin $candidates[0].FullName $artifacts
            $peFiles[$entry.Name] = [ordered]@{ Path = $dll; Sha256 = (FileSha $dll) }
            $assembledPdb = [IO.Path]::ChangeExtension($dll, '.pdb')
            $mode.AssemblySha256 += [ordered]@{
                Name = $entry.Name; Sha256 = $peFiles[$entry.Name].Sha256
                PdbSha256 = if ([IO.File]::Exists($assembledPdb)) { FileSha $assembledPdb } else { $null }
            }
        }
        if ($mode.FailureKind -eq 'RealPEMissing') { continue }
        $mode.DllSha256 = $peFiles['Companion'].Sha256
        $pdb = [IO.Path]::ChangeExtension($peFiles['Companion'].Path, '.pdb')
        if ([IO.File]::Exists($pdb)) { $mode.PdbSha256 = FileSha $pdb }
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
        if ($mode.SelfTests -ne 'Passed') { $mode.FailureKind = 'BuildFailed'; continue }
        foreach ($entry in $projects) {
            if ((FileSha $peFiles[$entry.Name].Path) -cne $peFiles[$entry.Name].Sha256) {
                $mode.FailureKind = 'RealPEMissing'
                break
            }
        }
        if ($mode.FailureKind -ne 'RealPEMissing') {
            $mode.PeCheck = 'Passed'
            $mode.FailureKind = 'Passed'
        }
    }
    if (@($builds | Where-Object { $_.Build -ne 'Passed' -or $_.PeCheck -ne 'Passed' -or $_.SelfTests -ne 'Passed' }).Count -eq 0) {
        $report.Qualification = 'Passed'
        $report.FailureKind = 'Passed'
    } else {
        $report.Qualification = 'Blocked'
        $failures = @($builds | Where-Object { $_.FailureKind -ne 'Passed' })
        $report.FailureKind = if (@($failures | Where-Object { $_.FailureKind -eq 'SourceDependencyMissing' }).Count -gt 0) {
            'SourceDependencyMissing'
        } elseif (@($failures | Where-Object { $_.FailureKind -eq 'RealPEMissing' }).Count -gt 0) {
            'RealPEMissing'
        } else { 'BuildFailed' }
        $report.Blocker = if ($report.FailureKind -eq 'SourceDependencyMissing') {
            'MissingTrackedCompilerCoreArtifactsProductionContracts'
        } else { 'RealCompanionBuildOrRuntimeTestFailed' }
    }
}
catch {
    $report.Qualification = 'Blocked'
    $report.Blocker = 'PreflightGraphSourceOrQualificationGuardFailed'
    $report.FailureKind = 'BuildFailed'
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
        $report.FailureKind = 'BuildFailed'
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
