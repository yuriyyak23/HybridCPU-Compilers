param(
    [string] $GoldenGitDir = "$env:TEMP\cw-golden-6946ccab-20261114",
    [string] $ArtifactRoot = "C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\processor-20261114\csr-clear-runtime-v1"
)

$ErrorActionPreference = 'Stop'
$goldenCommit = '6946ccab9d888f7dae0a4d8e18480f436138acd9'
$activeCommit = '1bfe41a49cf11c80fa6c6aee87accb54b78b331f'
$iseRoot = 'C:\Users\Yuriy\Desktop\HybridCPU ISE'
$compilerRoot = 'C:\Users\Yuriy\Desktop\HybridCPU Compilers'
$tempRoot = Join-Path $env:TEMP ('csr-clear-active-golden-' + [guid]::NewGuid().ToString('N'))
$activeTree = Join-Path $tempRoot 'active'
$goldenTree = Join-Path $tempRoot 'golden'
if (Test-Path -LiteralPath $ArtifactRoot) { throw "ArtifactRoot must be new to preserve immutable runtime evidence: $ArtifactRoot" }
New-Item -ItemType Directory -Force -Path $activeTree, $goldenTree, $ArtifactRoot | Out-Null

function Copy-SourceTree([string] $source, [string] $destination) {
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Required source directory is missing: $source" }
    $sourceRoot = (Resolve-Path -LiteralPath $source).Path.TrimEnd('\')
    $sourceBytes = (Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | Where-Object {
        $relative = $_.FullName.Substring($sourceRoot.Length).TrimStart('\')
        $relative -notmatch '(^|\\)(\.git|bin|obj|TestResults|packages|node_modules|\.vs|Documentation|docs|TempEnv)(\\|$)' -and
        $relative -notmatch '^(artifacts|\.artifacts)(\\|$)'
    } | Measure-Object -Property Length -Sum).Sum
    if ($sourceBytes -gt 900MB) { throw "Refusing oversized source tree copy: $source ($sourceBytes bytes); expected project-only source trees." }
    Write-Host ("Bounded input {0}: {1:N1} MiB" -f $source, ($sourceBytes / 1MB))
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    $excluded = @('.git', 'bin', 'obj', 'TestResults', 'packages', 'node_modules', '.vs', 'Documentation', 'docs', 'TempEnv', (Join-Path $source 'artifacts'), (Join-Path $source '.artifacts'))
    & robocopy $source $destination /E /NFL /NDL /NJH /NJS /NP /XD $excluded /XF *.user *.suo
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE): $source" }
}

function Copy-ActiveBuildInputs([string] $tree, [string] $iseSource, [string] $compilerSource) {
    $ise = Join-Path $tree 'HybridCPU ISE'
    $compiler = Join-Path $tree 'HybridCPU Compilers'
    foreach ($relative in @('HybridCPU_ISE', 'HybridCPU_ISE.Tests', 'HybridCPU_ExternalRuntime.Contracts')) {
        Copy-SourceTree (Join-Path $iseSource $relative) (Join-Path $ise $relative)
    }
    Copy-SourceTree (Join-Path $iseSource 'Diagnostics\ShadowFrontendCpuWorkload') (Join-Path $ise 'Diagnostics\ShadowFrontendCpuWorkload')
    foreach ($file in @('Directory.Build.props', 'global.json', '.editorconfig')) {
        Copy-Item -LiteralPath (Join-Path $iseSource $file) -Destination (Join-Path $ise $file)
    }
    foreach ($relative in @('Compilers', 'corpus')) {
        Copy-SourceTree (Join-Path $compilerSource $relative) (Join-Path $compiler $relative)
    }
}

function Prepare-TraceTest([string] $isePath) {
    $testPath = Join-Path $isePath 'HybridCPU_ISE.Tests\Architecture\CsrClearRuntimeTraceTests.cs'
    $source = @"
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Arch;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Pipeline;
using YAKSys_Hybrid_CPU.Core.Pipeline.MicroOps;
using YAKSys_Hybrid_CPU.Core.Registers.Retire;

namespace HybridCPU_ISE.Tests.Architecture;

public sealed class CsrClearRuntimeTraceTests
{
    private static readonly uint[] Seed = [11, 13, 17, 19, 23];

    private static Processor.CPU_Core NewCore()
    {
        Processor.CPU_Core core = new(0);
        core.ExceptionStatus.OverflowCount = Seed[0];
        core.ExceptionStatus.UnderflowCount = Seed[1];
        core.ExceptionStatus.DivByZeroCount = Seed[2];
        core.ExceptionStatus.InvalidOpCount = Seed[3];
        core.ExceptionStatus.InexactCount = Seed[4];
        core.ExceptionStatus.SetExceptionMode(2);
        core.ExceptionStatus.SetExceptionMask(Processor.CPU_Core.VectorException.Overflow, true);
        core.ExceptionStatus.SetExceptionPriority(Processor.CPU_Core.VectorException.Overflow, 5);
        return core;
    }

    private static uint[] Counters(Processor.CPU_Core core) =>
        [core.ExceptionStatus.OverflowCount, core.ExceptionStatus.UnderflowCount,
         core.ExceptionStatus.DivByZeroCount, core.ExceptionStatus.InvalidOpCount,
         core.ExceptionStatus.InexactCount];

    private static object Observe(string scenario, int applyEvent, bool decoded,
        bool effectCreated, bool batchAccepted, Processor.CPU_Core core, uint[] before) => new
    {
        scenario, opcode = (ushort)Processor.CPU_Core.InstructionsEnum.CSR_CLEAR,
        instructionValid = true, retireBatchApplyEvent = applyEvent,
        decoded, effectCreated, batchAccepted, before, after = Counters(core),
        exceptionMode = core.ExceptionStatus.ExceptionMode,
        overflowMasked = core.ExceptionStatus.GetExceptionMask(Processor.CPU_Core.VectorException.Overflow),
        overflowPriority = core.ExceptionStatus.GetExceptionPriority(Processor.CPU_Core.VectorException.Overflow)
    };

    private static (CSRMicroOp Op, Processor.CPU_Core InstructionCore) Decode()
    {
        Processor.CPU_Core.InstructionsEnum opcode = Processor.CPU_Core.InstructionsEnum.CSR_CLEAR;
        InstructionIR instruction = new()
        {
            CanonicalOpcode = opcode,
            Class = InstructionClassifier.GetClass(opcode),
            SerializationClass = InstructionClassifier.GetSerializationClass(opcode),
            Rd = 0, Rs1 = 0, Rs2 = 0, Imm = 0
        };
        if (!InstructionRegistry.TryCreatePublishedCsrMicroOp(in instruction, out CSRMicroOp? op) || op is null)
            throw new InvalidOperationException("Published CSR_CLEAR decode was rejected.");
        Processor.CPU_Core core = new(0);
        op.InitializeMetadata();
        if (!op.Execute(ref core)) throw new InvalidOperationException("CSR_CLEAR micro-op execution failed.");
        return (op, core);
    }

    [Fact]
    public void EmitsCsrClearRuntimeTrace()
    {
        var events = new List<object>();

        Processor.CPU_Core omitted = NewCore();
        uint[] omittedBefore = Counters(omitted);
        var (omittedOp, omittedDecodeCore) = Decode();
        CsrRetireEffect omittedEffect = omittedOp.CreateRetireEffect(ref omittedDecodeCore);
        if (!omittedEffect.ClearsArchitecturalExceptionState) throw new InvalidOperationException("CSR_CLEAR effect flag missing.");
        Span<RetireRecord> omittedRecords = stackalloc RetireRecord[2];
        Span<Processor.CPU_Core.RetireWindowEffect> omittedEffects = stackalloc Processor.CPU_Core.RetireWindowEffect[2];
        var omittedBatch = new Processor.CPU_Core.RetireWindowBatch(omittedRecords, omittedEffects, new PipelineEvent?[1]);
        omitted.ApplyCapturedRetireWindowBatch(ref omittedBatch, countRetireCycle: false);
        Xunit.Assert.Equal(omittedBefore, Counters(omitted));
        events.Add(Observe("effect-excluded-from-selected-prefix", 0, true, true, true, omitted, omittedBefore));

        Processor.CPU_Core rejected = NewCore();
        uint[] rejectedBefore = Counters(rejected);
        var (rejectedOp, rejectedDecodeCore) = Decode();
        CsrRetireEffect rejectedEffect = rejectedOp.CreateRetireEffect(ref rejectedDecodeCore);
        Span<RetireRecord> rejectedRecords = stackalloc RetireRecord[2];
        Span<Processor.CPU_Core.RetireWindowEffect> rejectedEffects = stackalloc Processor.CPU_Core.RetireWindowEffect[2];
        var rejectedBatch = new Processor.CPU_Core.RetireWindowBatch(rejectedRecords, rejectedEffects, new PipelineEvent?[1]);
        rejectedBatch.CaptureRetireWindowCsrEffect(rejectedEffect);
        rejectedBatch.CaptureRetireWindowAtomicEffect(default);
        bool batchAccepted = true;
        try { rejected.ApplyCapturedRetireWindowBatch(ref rejectedBatch, countRetireCycle: false); }
        catch (InvalidOperationException) { batchAccepted = false; }
        Xunit.Assert.False(batchAccepted);
        Xunit.Assert.Equal(rejectedBefore, Counters(rejected));
        events.Add(Observe("prevalidation-rejected-batch", 0, true, true, batchAccepted, rejected, rejectedBefore));

        Processor.CPU_Core selected = NewCore();
        uint[] selectedBefore = Counters(selected);
        var (selectedOp, selectedDecodeCore) = Decode();
        CsrRetireEffect selectedEffect = selectedOp.CreateRetireEffect(ref selectedDecodeCore);
        Span<RetireRecord> selectedRecords = stackalloc RetireRecord[2];
        Span<Processor.CPU_Core.RetireWindowEffect> selectedEffects = stackalloc Processor.CPU_Core.RetireWindowEffect[2];
        var selectedBatch = new Processor.CPU_Core.RetireWindowBatch(selectedRecords, selectedEffects, new PipelineEvent?[1]);
        selectedBatch.CaptureRetireWindowCsrEffect(selectedEffect);
        selected.ApplyCapturedRetireWindowBatch(ref selectedBatch, countRetireCycle: false);
        Xunit.Assert.Equal(new uint[5], Counters(selected));
        Xunit.Assert.Equal(2, selected.ExceptionStatus.ExceptionMode);
        Xunit.Assert.True(selected.ExceptionStatus.GetExceptionMask(Processor.CPU_Core.VectorException.Overflow));
        Xunit.Assert.Equal((byte)5, selected.ExceptionStatus.GetExceptionPriority(Processor.CPU_Core.VectorException.Overflow));
        events.Add(Observe("selected-batch-applied", 1, true, true, true, selected, selectedBefore));

        string tracePath = Environment.GetEnvironmentVariable("CW_CSR_CLEAR_TRACE_OUTPUT")
            ?? throw new InvalidOperationException("CW_CSR_CLEAR_TRACE_OUTPUT is required.");
        Directory.CreateDirectory(Path.GetDirectoryName(tracePath)!);
        File.WriteAllText(tracePath, JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
    }
}
"@
    [IO.File]::WriteAllText($testPath, $source, [Text.UTF8Encoding]::new($false))
}
function Redirect-IseProjectIntermediatePaths([string] $isePath) {
    $project = Join-Path $isePath 'HybridCPU_ISE.Tests\HybridCPU_ISE.Tests.csproj'
    $source = [IO.File]::ReadAllText($project)
    $propertyPrefix = '<HybridCpuIseProjectReferenceProperties Condition='
    $endTag = '</HybridCpuIseProjectReferenceProperties>'
    $first = $source.IndexOf($propertyPrefix, [StringComparison]::Ordinal)
    $start = $source.IndexOf($propertyPrefix, $first + $propertyPrefix.Length, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Could not find external ISE reference property in $project" }
    $tagEnd = $source.IndexOf('>', $start, [StringComparison]::Ordinal) + 1
    $startTag = $source.Substring($start, $tagEnd - $start)
    $end = $source.IndexOf($endTag, $tagEnd, [StringComparison]::Ordinal)
    if ($end -lt 0) { throw "Could not find end of external ISE reference property in $project" }
    $value = 'DefineTestSupport=$(DefineTestSupport);HybridCpuTestArtifactsRoot=$(HybridCpuTestArtifactsRoot);BaseIntermediateOutputPath=$(MSBuildThisFileDirectory)..\.rf106-build/obj/HybridCPU_ISE/;MSBuildProjectExtensionsPath=$(MSBuildThisFileDirectory)..\.rf106-build/obj/HybridCPU_ISE/'
    $source = $source.Substring(0, $start) + $startTag + $value + $endTag + $source.Substring($end + $endTag.Length)
    [IO.File]::WriteAllText($project, $source, [Text.UTF8Encoding]::new($false))
    $iseProjectDirectory = Join-Path $isePath 'HybridCPU_ISE'
    $localProps = @'
<Project>
  <PropertyGroup>
    <BaseIntermediateOutputPath>$(MSBuildThisFileDirectory)..\.rf106-build/obj/HybridCPU_ISE/</BaseIntermediateOutputPath>
    <MSBuildProjectExtensionsPath>$(BaseIntermediateOutputPath)</MSBuildProjectExtensionsPath>
  </PropertyGroup>
</Project>
'@
    [IO.File]::WriteAllText((Join-Path $iseProjectDirectory 'Directory.Build.props'), $localProps, [Text.UTF8Encoding]::new($false))
}

try {
    # Active snapshot is a byte copy of the current read-only checkout; all build products are in temp.
    $activeIse = Join-Path $activeTree 'HybridCPU ISE'
    $activeCompiler = Join-Path $activeTree 'HybridCPU Compilers'
    Copy-ActiveBuildInputs $activeTree $iseRoot $compilerRoot
    Prepare-TraceTest $activeIse

    # Materialize the exact pinned Git tree, then place its Compilers subtree beside ISE as project references expect.
    $archive = Join-Path $tempRoot 'golden.tar'
    $archivePaths = @('HybridCPU_ISE', 'HybridCPU_ISE.Tests', 'HybridCPU_ExternalRuntime.Contracts', 'Compilers', 'corpus', 'Directory.Build.props', 'global.json')
    $git = Start-Process git -ArgumentList (@("--git-dir=$GoldenGitDir", 'archive', '--format=tar', "--output=$archive", $goldenCommit) + $archivePaths) -NoNewWindow -Wait -PassThru
    if ($git.ExitCode -ne 0) { throw "git archive of pinned Golden failed: $($git.ExitCode)" }
    $extract = Join-Path $goldenTree 'source'
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    & tar -xf $archive -C $extract
    if ($LASTEXITCODE -ne 0) { throw "tar extraction failed: $LASTEXITCODE" }
    $goldenIse = $extract
    $goldenCompiler = Join-Path $extract 'Compilers'
    Prepare-TraceTest $goldenIse

    # Force every project (including referenced compiler projects) to unique external obj/bin folders.
    $externalBuildProps = @'
<PropertyGroup>
  <BaseIntermediateOutputPath>$(MSBuildThisFileDirectory).rf106-build/obj/$(MSBuildProjectName)/</BaseIntermediateOutputPath>
  <MSBuildProjectExtensionsPath>$(BaseIntermediateOutputPath)</MSBuildProjectExtensionsPath>
  <BaseOutputPath>$(MSBuildThisFileDirectory).rf106-build/bin/$(MSBuildProjectName)/</BaseOutputPath>
</PropertyGroup>
<PropertyGroup Condition="'$(HybridCpuTestArtifactsRoot)' != ''">
  <HybridCpuIseProjectReferenceProperties>DefineTestSupport=$(DefineTestSupport);HybridCpuTestArtifactsRoot=$(HybridCpuTestArtifactsRoot);BaseIntermediateOutputPath=$(MSBuildThisFileDirectory).rf106-build/obj/HybridCPU_ISE/;MSBuildProjectExtensionsPath=$(MSBuildThisFileDirectory).rf106-build/obj/HybridCPU_ISE/</HybridCpuIseProjectReferenceProperties>
</PropertyGroup>
'@
    foreach ($workspace in @($activeTree, $extract)) {
        $propsPath = Join-Path $workspace 'Directory.Build.props'
        if (-not (Test-Path -LiteralPath $propsPath)) { [IO.File]::WriteAllText($propsPath, '<Project>' + [Environment]::NewLine + '</Project>', [Text.UTF8Encoding]::new($false)) }
        $existingProps = [IO.File]::ReadAllText($propsPath)
        $close = $existingProps.LastIndexOf('</Project>', [StringComparison]::OrdinalIgnoreCase)
        if ($close -lt 0) { throw "Could not inject external output paths in $propsPath" }
        $existingProps = $existingProps.Insert($close, $externalBuildProps + [Environment]::NewLine)
        [IO.File]::WriteAllText($propsPath, $existingProps, [Text.UTF8Encoding]::new($false))
    }
    $activeIseProps = Join-Path $activeIse 'Directory.Build.props'
    $activeIseText = [IO.File]::ReadAllText($activeIseProps)
    $activeExtraProps = $externalBuildProps + @'
<PropertyGroup Condition="'$(HybridCpuTestArtifactsRoot)' != ''">
  <HybridCpuIseProjectReferenceProperties>DefineTestSupport=$(DefineTestSupport);HybridCpuTestArtifactsRoot=$(HybridCpuTestArtifactsRoot);BaseIntermediateOutputPath=$(MSBuildThisFileDirectory).rf106-build/obj/HybridCPU_ISE/;MSBuildProjectExtensionsPath=$(MSBuildThisFileDirectory).rf106-build/obj/HybridCPU_ISE/</HybridCpuIseProjectReferenceProperties>
</PropertyGroup>
'@
    $activeIseText = $activeIseText.Replace('</Project>', $activeExtraProps + [Environment]::NewLine + '</Project>')
    [IO.File]::WriteAllText($activeIseProps, $activeIseText, [Text.UTF8Encoding]::new($false))

    $runs = @(
        @{ Name = 'active'; Ise = $activeIse; Compiler = $activeCompiler; Expected = $activeCommit },
        @{ Name = 'golden'; Ise = $goldenIse; Compiler = $goldenCompiler; Expected = $goldenCommit }
    )
    foreach ($run in $runs) {
        $testSource = Join-Path $run.Ise 'HybridCPU_ISE.Tests\Architecture\CsrClearRuntimeTraceTests.cs'
        $project = Join-Path $run.Ise 'Rf106RuntimeTrace.csproj'
        $trace = Join-Path $ArtifactRoot ($run.Name + '-trace.json')
        $runRoot = Join-Path $tempRoot ($run.Name + '-test-artifacts')
        New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
        $iseProject = Join-Path $run.Ise 'HybridCPU_ISE\HybridCPU_ISE.csproj'
        $externalIntermediate = Join-Path $run.Ise '.rf106-build\obj\HybridCPU_ISE'
        $escapedIseProject = [Security.SecurityElement]::Escape($iseProject)
        $escapedTraceSource = [Security.SecurityElement]::Escape($testSource)
        $escapedIntermediate = [Security.SecurityElement]::Escape($externalIntermediate)
        $escapedRunRoot = [Security.SecurityElement]::Escape($runRoot)
        $minimalProject = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <AssemblyName>HybridCPU_ISE.Tests</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.12.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="4.0.1" PrivateAssets="all" />
    <Using Include="Xunit" />
    <Compile Include="$escapedTraceSource" />
    <ProjectReference Include="$escapedIseProject">
      <AdditionalProperties>DefineTestSupport=true;EnableInternalTestHooks=true;HybridCpuTestArtifactsRoot=$escapedRunRoot;BaseIntermediateOutputPath=$escapedIntermediate\;MSBuildProjectExtensionsPath=$escapedIntermediate\</AdditionalProperties>
    </ProjectReference>
  </ItemGroup>
</Project>
"@
        [IO.File]::WriteAllText($project, $minimalProject, [Text.UTF8Encoding]::new($false))
        $env:CW_CSR_CLEAR_TRACE_OUTPUT = $trace
        $testFilter = 'FullyQualifiedName~EmitsCsrClearRuntimeTrace'
        $testLog = Join-Path $ArtifactRoot ($run.Name + '-test.log')
        & dotnet test $project --configuration Debug --filter $testFilter "-p:HybridCpuTestArtifactsRoot=$runRoot" -m:1 -p:UseSharedCompilation=false --logger 'console;verbosity=minimal' *> $testLog
        $testExitCode = $LASTEXITCODE
        Get-Content -LiteralPath $testLog
        if ($testExitCode -ne 0) { throw "$($run.Name) CSR_CLEAR runtime test failed: $testExitCode" }
        if (-not (Test-Path $trace)) { throw "$($run.Name) tests passed without emitting the expected runtime trace." }
    }
    $activeTrace = Get-Content (Join-Path $ArtifactRoot 'active-trace.json') -Raw | ConvertFrom-Json
    $goldenTrace = Get-Content (Join-Path $ArtifactRoot 'golden-trace.json') -Raw | ConvertFrom-Json
    $activeJson = ConvertTo-Json -InputObject $activeTrace -Depth 50 -Compress
    $goldenJson = ConvertTo-Json -InputObject $goldenTrace -Depth 50 -Compress
    $same = $activeJson -ceq $goldenJson
    if ($activeTrace.Count -ne 3) { throw "Unexpected CSR_CLEAR runtime trace length: $($activeTrace.Count)" }
    $omitted = @($activeTrace | Where-Object scenario -eq 'effect-excluded-from-selected-prefix')[0]
    $prevalidation = @($activeTrace | Where-Object scenario -eq 'prevalidation-rejected-batch')[0]
    $selected = @($activeTrace | Where-Object scenario -eq 'selected-batch-applied')[0]
    $seed = @(11,13,17,19,23)
    $zeroes = @(0,0,0,0,0)
    if ($null -eq $omitted -or $null -eq $prevalidation -or $null -eq $selected -or
        -not $omitted.decoded -or -not $omitted.effectCreated -or $omitted.retireBatchApplyEvent -ne 0 -or
        (@($omitted.before) -join ',') -ne ($seed -join ',') -or (@($omitted.after) -join ',') -ne ($seed -join ',') -or
        $prevalidation.batchAccepted -ne $false -or $prevalidation.retireBatchApplyEvent -ne 0 -or
        (@($prevalidation.before) -join ',') -ne ($seed -join ',') -or (@($prevalidation.after) -join ',') -ne ($seed -join ',') -or
        $selected.batchAccepted -ne $true -or $selected.retireBatchApplyEvent -ne 1 -or
        (@($selected.before) -join ',') -ne ($seed -join ',') -or (@($selected.after) -join ',') -ne ($zeroes -join ',') -or
        @($activeTrace | Where-Object { $_.exceptionMode -ne 2 -or -not $_.overflowMasked -or $_.overflowPriority -ne 5 }).Count -ne 0) {
        throw 'Independent CSR_CLEAR runtime oracle rejected decode/effect/selected-batch/counter observations.'
    }
    $sourcePaths = @(
        'HybridCPU_ISE/CloseToHSL/Core/Diagnostics/InstructionRegistry.Helpers.Csr.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/MicroOps/Control/MicroOp.Control.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.PipelineExecution.Types.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.PipelineExecution.Retire.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Architecture/State/Architectural/CPU_Core.State.cs')
    $sourcePins = foreach ($relative in $sourcePaths) {
        $activePath = Join-Path $activeTree (Join-Path 'HybridCPU ISE' $relative.Replace('/', '\'))
        $goldenPath = Join-Path $extract ($relative.Replace('/', '\'))
        $blob = (git --git-dir=$GoldenGitDir rev-parse ($goldenCommit + ':' + $relative)).Trim()
        $materializedBlob = (git hash-object $goldenPath).Trim()
        if ($materializedBlob -cne $blob) { throw "Materialized Golden source is not the pinned Git blob: $relative" }
        [ordered]@{ path=$relative; activeSha256=(Get-FileHash $activePath -Algorithm SHA256).Hash.ToLowerInvariant(); goldenMaterializedSha256=(Get-FileHash $goldenPath -Algorithm SHA256).Hash.ToLowerInvariant(); goldenGitBlob=$blob }
    }
    $activeTestAssembly = Join-Path $activeIse '.rf106-build\bin\Rf106RuntimeTrace\Debug\net11.0\HybridCPU_ISE.Tests.dll'
    $goldenTestAssembly = Join-Path $goldenIse '.rf106-build\bin\Rf106RuntimeTrace\Debug\net11.0\HybridCPU_ISE.Tests.dll'
    $activeHarnessProject = Join-Path $activeIse 'Rf106RuntimeTrace.csproj'
    $goldenHarnessProject = Join-Path $goldenIse 'Rf106RuntimeTrace.csproj'
    $activeTraceTestSource = Join-Path $activeIse 'HybridCPU_ISE.Tests\Architecture\CsrClearRuntimeTraceTests.cs'
    $goldenTraceTestSource = Join-Path $goldenIse 'HybridCPU_ISE.Tests\Architecture\CsrClearRuntimeTraceTests.cs'
    $comparison = [ordered]@{
        schema = 'circuitweave.csr-clear-runtime-differential/v1'
        scenario = 'CSR_CLEAR decode/effect/captured-batch/counter state'
        activeModelCommit = $activeCommit
        activeWorkingTreeSnapshot = 'copied from current checkout; current source hashes recorded by adjacent harness receipt'
        goldenCommit = $goldenCommit
        goldenTreeSource = 'git archive from pinned commit object'
        activeModelGitHead = (git -C $iseRoot rev-parse HEAD).Trim()
        sourcePins = @($sourcePins)
        activeTraceTestSourceSha256 = (Get-FileHash $activeTraceTestSource -Algorithm SHA256).Hash.ToLowerInvariant()
        goldenTraceTestSourceSha256 = (Get-FileHash $goldenTraceTestSource -Algorithm SHA256).Hash.ToLowerInvariant()
        activeHarnessProjectSha256 = (Get-FileHash $activeHarnessProject -Algorithm SHA256).Hash.ToLowerInvariant()
        goldenHarnessProjectSha256 = (Get-FileHash $goldenHarnessProject -Algorithm SHA256).Hash.ToLowerInvariant()
        activeTraceTestLogSha256 = (Get-FileHash (Join-Path $ArtifactRoot 'active-test.log') -Algorithm SHA256).Hash.ToLowerInvariant()
        goldenTraceTestLogSha256 = (Get-FileHash (Join-Path $ArtifactRoot 'golden-test.log') -Algorithm SHA256).Hash.ToLowerInvariant()
        activeTestAssemblySha256 = (Get-FileHash $activeTestAssembly -Algorithm SHA256).Hash.ToLowerInvariant()
        goldenTestAssemblySha256 = (Get-FileHash $goldenTestAssembly -Algorithm SHA256).Hash.ToLowerInvariant()
        activeTraceSha256 = (Get-FileHash (Join-Path $ArtifactRoot 'active-trace.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        goldenTraceSha256 = (Get-FileHash (Join-Path $ArtifactRoot 'golden-trace.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        eventCount = $activeTrace.Count
        scenarios = @('effect-excluded-from-selected-prefix','prevalidation-rejected-batch','selected-batch-applied')
        tracesEqual = $same
        interpretation = 'Real CSR_CLEAR decode/effect/captured-batch/counter observations from active and pinned-Golden test builds; bounded software retire call-event trace, not a physical cycle or general sequential RTL equivalence.'
        isolatedBuildRoot = $tempRoot
    }
    $comparison | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $ArtifactRoot 'comparison.json') -Encoding utf8
    if (-not $same) { throw 'Active and pinned-Golden normalized runtime event traces differ; inspect comparison artifacts.' }
    Write-Output (Get-Content (Join-Path $ArtifactRoot 'comparison.json') -Raw)
}
finally {
    Remove-Item Env:CW_CSR_CLEAR_TRACE_OUTPUT -ErrorAction SilentlyContinue
}
