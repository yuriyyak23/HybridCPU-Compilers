param(
    [string] $GoldenGitDir = "$env:TEMP\cw-golden-6946ccab-20261114",
    [string] $ArtifactRoot = "C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\processor-20261114\trap-carrier-runtime-v1",
    [ValidateSet('PinnedGitTree','WorkingTree')][string] $ActiveSourceMode = 'PinnedGitTree'
)

$ErrorActionPreference = 'Stop'
$goldenCommit = '6946ccab9d888f7dae0a4d8e18480f436138acd9'
$activeCommit = '1bfe41a49cf11c80fa6c6aee87accb54b78b331f'
$iseRoot = 'C:\Users\Yuriy\Desktop\HybridCPU ISE'
$compilerRoot = 'C:\Users\Yuriy\Desktop\HybridCPU Compilers'
$tempRoot = Join-Path $env:TEMP ('trap-carrier-active-golden-' + [guid]::NewGuid().ToString('N'))
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

function Copy-ActiveBuildInputs([string] $tree, [string] $iseSource, [string] $compilerSource, [bool] $includeCompanionCompiler) {
    $ise = Join-Path $tree 'HybridCPU ISE'
    $compiler = Join-Path $tree 'HybridCPU Compilers'
    foreach ($relative in @('HybridCPU_ISE', 'HybridCPU_ISE.Tests', 'HybridCPU_ExternalRuntime.Contracts')) {
        Copy-SourceTree (Join-Path $iseSource $relative) (Join-Path $ise $relative)
    }
    $shadowWorkload = Join-Path $iseSource 'Diagnostics\ShadowFrontendCpuWorkload'
    if (Test-Path -LiteralPath $shadowWorkload -PathType Container) {
        Copy-SourceTree $shadowWorkload (Join-Path $ise 'Diagnostics\ShadowFrontendCpuWorkload')
    }
    foreach ($file in @('Directory.Build.props', 'global.json', '.editorconfig')) {
        $inputFile = Join-Path $iseSource $file
        if (Test-Path -LiteralPath $inputFile -PathType Leaf) {
            Copy-Item -LiteralPath $inputFile -Destination (Join-Path $ise $file)
        }
    }
    foreach ($relative in @('Compilers', 'corpus')) {
        $modelRelative = Join-Path $iseSource $relative
        if (Test-Path -LiteralPath $modelRelative -PathType Container) {
            Copy-SourceTree $modelRelative (Join-Path $ise $relative)
        }
        if ($includeCompanionCompiler) {
            Copy-SourceTree (Join-Path $compilerSource $relative) (Join-Path $compiler $relative)
        }
    }
}

function Prepare-TraceTest([string] $isePath) {
    $testPath = Join-Path $isePath 'HybridCPU_ISE.Tests\tests\Phase09TrapCarrierFollowThroughTests.cs'
    Copy-Item -LiteralPath $testPath -Destination ($testPath + '.source-before-trace')
    $source = [IO.File]::ReadAllText($testPath)
    $source = $source.Replace('using HybridCPU_ISE.Arch;', "using System.Collections.Generic;`nusing System.IO;`nusing System.Text.Json;`nusing HybridCPU_ISE.Arch;")
    $anchors = @(
        '        const int vtId = 2;',
        '        Assert.Equal(SlotClass.LsuClass, publishedSlot.Placement.RequiredSlotClass);',
        '        Assert.Equal((byte)0, decisionDraft.ScalarIssueMask);',
        '        Assert.Equal(0UL, generatedTrapEvent.FaultAddress);',
        '        Assert.Equal(1UL, control.NonScalarLanesRetired);')
    foreach ($anchor in $anchors) { if (-not $source.Contains($anchor)) { throw "Trap trace injection anchor missing: $anchor" } }

    $source = $source.Replace('        const int vtId = 2;', @'
        var runtimeEvents = new List<object>();
        const int vtId = 2;
'@
    )
    $source = $source.Replace('        Assert.Equal(SlotClass.LsuClass, publishedSlot.Placement.RequiredSlotClass);', @'
        Assert.Equal(SlotClass.LsuClass, publishedSlot.Placement.RequiredSlotClass);
        runtimeEvents.Add(new { stage = "canonical-publish", opcode = (uint)opcode,
            decodeFault = publishedCanonicalBundle.HasDecodeFault, empty = publishedCanonicalBundle.IsEmpty,
            validSlots = publishedTransportFacts.ValidNonEmptyMask,
            scalarCandidates = publishedTransportFacts.AdmissionPrep.ScalarCandidateMask,
            auxiliaryCandidates = publishedTransportFacts.AdmissionPrep.AuxiliaryOpMask,
            slotType = publishedSlot.MicroOp.GetType().Name, isMemory = publishedSlot.IsMemoryOp,
            isControlFlow = publishedSlot.IsControlFlow, requiredSlotClass = publishedSlot.Placement.RequiredSlotClass.ToString() });
'@
    )
    $source = $source.Replace('        Assert.Equal((byte)0, decisionDraft.ScalarIssueMask);', @'
        Assert.Equal((byte)0, decisionDraft.ScalarIssueMask);
        runtimeEvents.Add(new { stage = "decode-admission", opcode = (uint)opcode,
            executionMode = decisionDraft.ExecutionMode.ToString(), usesIssuePacket = decisionDraft.UsesIssuePacketAsExecutionSource,
            auxiliaryReservationMask = decisionDraft.AuxiliaryReservationMask, scalarIssueMask = decisionDraft.ScalarIssueMask,
            lane4Occupied = issuePacket.Lane4.IsOccupied, lane4Type = issuePacket.Lane4.MicroOp?.GetType().Name,
            lane7Occupied = issuePacket.Lane7.IsOccupied });
'@
    )
    $source = $source.Replace('        Assert.Equal(0UL, generatedTrapEvent.FaultAddress);', @'
        Assert.Equal(0UL, generatedTrapEvent.FaultAddress);
        runtimeEvents.Add(new { stage = "execute-trap-event", opcode = (uint)opcode,
            executeValid = executeStage.Valid, lane4Occupied = lane4.IsOccupied,
            microOpType = lane4.MicroOp?.GetType().Name, resultReady = lane4.ResultReady,
            vectorComplete = lane4.VectorComplete, lane7Occupied = executeStage.Lane7.IsOccupied,
            eventType = generatedTrapEvent.GetType().Name, causeCode = generatedTrapEvent.CauseCode,
            faultAddress = generatedTrapEvent.FaultAddress, hasFaultAddress = generatedTrapEvent.HasFaultAddress });
'@
    )
    $source = $source.Replace('        Assert.Equal(1UL, control.NonScalarLanesRetired);', @'
        Assert.Equal(1UL, control.NonScalarLanesRetired);
        ReplayPhaseContext finalReplay = core.GetReplayPhaseContext();
        ReplayPhaseContext finalSchedulerReplay = scheduler.TestGetReplayPhaseContext();
        runtimeEvents.Add(new { stage = "trap-retire-boundary", opcode = (uint)opcode,
            serializingEpochDelta = scheduler.SerializingEpochCount - serializingEpochCountBefore,
            certificateInvalidationReason = scheduler.LastPhaseCertificateInvalidationReason.ToString(),
            assistInvalidationReason = scheduler.LastAssistInvalidationReason.ToString(),
            coreReplayActive = finalReplay.IsActive, coreReplayReason = finalReplay.LastInvalidationReason.ToString(),
            schedulerReplayActive = finalSchedulerReplay.IsActive, schedulerReplayReason = finalSchedulerReplay.LastInvalidationReason.ToString(),
            mepc = core.Csr.DirectRead(CsrAddresses.Mepc), mcause = core.Csr.DirectRead(CsrAddresses.Mcause),
            mtval = core.Csr.DirectRead(CsrAddresses.Mtval), committedPc = core.ReadCommittedPc(vtId),
            activeLivePc = core.ReadActiveLivePc(), pipelineState = core.ReadVirtualThreadPipelineState(vtId).ToString(),
            instructionsRetired = control.InstructionsRetired, scalarLanesRetired = control.ScalarLanesRetired,
            nonScalarLanesRetired = control.NonScalarLanesRetired });
        string tracePath = Environment.GetEnvironmentVariable("CW_TRAP_CARRIER_TRACE_OUTPUT")
            ?? throw new InvalidOperationException("CW_TRAP_CARRIER_TRACE_OUTPUT is required.");
        string perOpcodePath = Path.Combine(Path.GetDirectoryName(tracePath)!,
            Path.GetFileNameWithoutExtension(tracePath) + "-" + opcode + ".json");
        File.WriteAllText(perOpcodePath, JsonSerializer.Serialize(runtimeEvents,
            new JsonSerializerOptions { WriteIndented = true }));
'@
    )
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
    $value = 'DefineTestSupport=$(DefineTestSupport);HybridCpuTestArtifactsRoot=$(HybridCpuTestArtifactsRoot);BaseIntermediateOutputPath=$(MSBuildThisFileDirectory)..\.trapcarrier-build/obj/HybridCPU_ISE/;MSBuildProjectExtensionsPath=$(MSBuildThisFileDirectory)..\.trapcarrier-build/obj/HybridCPU_ISE/'
    $source = $source.Substring(0, $start) + $startTag + $value + $endTag + $source.Substring($end + $endTag.Length)
    [IO.File]::WriteAllText($project, $source, [Text.UTF8Encoding]::new($false))
    $iseProjectDirectory = Join-Path $isePath 'HybridCPU_ISE'
    $localProps = @'
<Project>
  <PropertyGroup>
    <BaseIntermediateOutputPath>$(MSBuildThisFileDirectory)..\.trapcarrier-build/obj/HybridCPU_ISE/</BaseIntermediateOutputPath>
    <MSBuildProjectExtensionsPath>$(BaseIntermediateOutputPath)</MSBuildProjectExtensionsPath>
  </PropertyGroup>
</Project>
'@
    [IO.File]::WriteAllText((Join-Path $iseProjectDirectory 'Directory.Build.props'), $localProps, [Text.UTF8Encoding]::new($false))
}

try {
    # Default to the exact pinned active ISE Git tree, including its in-repository Compilers subtree.
    # Working-tree mode remains explicit for exploratory overlays and is not commit-equivalent evidence.
    $activeIseSourceRoot = $iseRoot
    $activeIseGitHead = (git -C $iseRoot rev-parse HEAD).Trim()
    $activeCompilerGitHead = (git -C $compilerRoot rev-parse HEAD).Trim()
    $activeIseArchiveSha256 = $null
    $includeCompanionCompiler = ($ActiveSourceMode -eq 'WorkingTree')
    if ($ActiveSourceMode -eq 'PinnedGitTree') {
        if ($activeIseGitHead -cne $activeCommit) { throw "ISE HEAD is not the pinned active commit: $activeIseGitHead" }
        $activeIseArchive = Join-Path $tempRoot 'active-ise.tar'
        $activeIsePaths = @('HybridCPU_ISE','HybridCPU_ISE.Tests','HybridCPU_ExternalRuntime.Contracts','Compilers','corpus','Directory.Build.props','global.json')
        & git -C $iseRoot archive --format=tar --output=$activeIseArchive $activeCommit -- @activeIsePaths
        if ($LASTEXITCODE -ne 0) { throw "git archive of pinned active ISE failed: $LASTEXITCODE" }
        $activeIseSourceRoot = Join-Path $tempRoot 'active-ise-source'
        New-Item -ItemType Directory -Force -Path $activeIseSourceRoot | Out-Null
        & tar -xf $activeIseArchive -C $activeIseSourceRoot
        if ($LASTEXITCODE -ne 0) { throw "active ISE archive extraction failed: $LASTEXITCODE" }
        $activeIseArchiveSha256 = (Get-FileHash $activeIseArchive -Algorithm SHA256).Hash.ToLowerInvariant()
        $activeCompilerSourceRoot = $compilerRoot
    } else {
        $activeIseSourceRoot = $iseRoot
        $activeCompilerSourceRoot = $compilerRoot
    }

    # Active snapshot is an isolated copy; all build products are in temp.
    $activeIse = Join-Path $activeTree 'HybridCPU ISE'
    $activeCompiler = if ($includeCompanionCompiler) { Join-Path $activeTree 'HybridCPU Compilers' } else { $null }
    Copy-ActiveBuildInputs $activeTree $activeIseSourceRoot $activeCompilerSourceRoot $includeCompanionCompiler
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
  <BaseIntermediateOutputPath>$(MSBuildThisFileDirectory).trapcarrier-build/obj/$(MSBuildProjectName)/</BaseIntermediateOutputPath>
  <MSBuildProjectExtensionsPath>$(BaseIntermediateOutputPath)</MSBuildProjectExtensionsPath>
  <BaseOutputPath>$(MSBuildThisFileDirectory).trapcarrier-build/bin/$(MSBuildProjectName)/</BaseOutputPath>
</PropertyGroup>
<PropertyGroup Condition="'$(HybridCpuTestArtifactsRoot)' != ''">
  <HybridCpuIseProjectReferenceProperties>DefineTestSupport=$(DefineTestSupport);HybridCpuTestArtifactsRoot=$(HybridCpuTestArtifactsRoot);BaseIntermediateOutputPath=$(MSBuildThisFileDirectory).trapcarrier-build/obj/HybridCPU_ISE/;MSBuildProjectExtensionsPath=$(MSBuildThisFileDirectory).trapcarrier-build/obj/HybridCPU_ISE/</HybridCpuIseProjectReferenceProperties>
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
  <HybridCpuIseProjectReferenceProperties>DefineTestSupport=$(DefineTestSupport);HybridCpuTestArtifactsRoot=$(HybridCpuTestArtifactsRoot);BaseIntermediateOutputPath=$(MSBuildThisFileDirectory).trapcarrier-build/obj/HybridCPU_ISE/;MSBuildProjectExtensionsPath=$(MSBuildThisFileDirectory).trapcarrier-build/obj/HybridCPU_ISE/</HybridCpuIseProjectReferenceProperties>
</PropertyGroup>
'@
    $activeIseText = $activeIseText.Replace('</Project>', $activeExtraProps + [Environment]::NewLine + '</Project>')
    [IO.File]::WriteAllText($activeIseProps, $activeIseText, [Text.UTF8Encoding]::new($false))

    $runs = @(
        @{ Name = 'active'; Ise = $activeIse; Compiler = $activeCompiler; Expected = $activeCommit },
        @{ Name = 'golden'; Ise = $goldenIse; Compiler = $goldenCompiler; Expected = $goldenCommit }
    )
    foreach ($run in $runs) {
        $testSource = Join-Path $run.Ise 'HybridCPU_ISE.Tests\tests\Phase09TrapCarrierFollowThroughTests.cs'
        $helperSource = Join-Path $run.Ise 'HybridCPU_ISE.Tests\TestHelpers\MicroOpTestHelper.cs'
        $globalUsingsSource = Join-Path $run.Ise 'HybridCPU_ISE.Tests\GlobalUsings.cs'
        $project = Join-Path $run.Ise 'TrapCarrierRuntimeTrace.csproj'
        $trace = Join-Path $ArtifactRoot ($run.Name + '-trace.json')
        $runRoot = Join-Path $tempRoot ($run.Name + '-test-artifacts')
        New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
        $iseProject = Join-Path $run.Ise 'HybridCPU_ISE\HybridCPU_ISE.csproj'
        $externalIntermediate = Join-Path $run.Ise '.trapcarrier-build\obj\HybridCPU_ISE'
        $escapedIseProject = [Security.SecurityElement]::Escape($iseProject)
        $escapedTraceSource = [Security.SecurityElement]::Escape($testSource)
        $escapedHelperSource = [Security.SecurityElement]::Escape($helperSource)
        $escapedGlobalUsingsSource = [Security.SecurityElement]::Escape($globalUsingsSource)
        $probeSource = Join-Path $PSScriptRoot 'TranslationFaultRuntimeProbeTests.cs'
        $escapedProbeSource = [Security.SecurityElement]::Escape($probeSource)
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
    <Compile Include="$escapedHelperSource" />
    <Compile Include="$escapedGlobalUsingsSource" />
    <Compile Include="$escapedProbeSource" />
    <ProjectReference Include="$escapedIseProject">
      <AdditionalProperties>DefineTestSupport=true;EnableInternalTestHooks=true;HybridCpuTestArtifactsRoot=$escapedRunRoot;BaseIntermediateOutputPath=$escapedIntermediate\;MSBuildProjectExtensionsPath=$escapedIntermediate\</AdditionalProperties>
    </ProjectReference>
  </ItemGroup>
</Project>
"@
        [IO.File]::WriteAllText($project, $minimalProject, [Text.UTF8Encoding]::new($false))
        $env:CW_TRAP_CARRIER_TRACE_OUTPUT = $trace
        $faultTrace = Join-Path $ArtifactRoot ($run.Name + '-translation-fault.json')
        $env:CW_TRANSLATION_FAULT_RUNTIME_TRACE_OUTPUT = $faultTrace
        $testFilter = 'FullyQualifiedName~TrapCarrierFollowThroughTests|FullyQualifiedName~TranslationFaultRuntimeProbeTests'
        $testLog = Join-Path $ArtifactRoot ($run.Name + '-test.log')
        & dotnet test $project --configuration Debug --filter $testFilter "-p:HybridCpuTestArtifactsRoot=$runRoot" -m:1 -p:UseSharedCompilation=false --logger 'console;verbosity=minimal' *> $testLog
        $testExitCode = $LASTEXITCODE
        Get-Content -LiteralPath $testLog
        if ($testExitCode -ne 0) { throw "$($run.Name) trap-carrier runtime test failed: $testExitCode" }
        if (-not (Test-Path -LiteralPath $faultTrace)) { throw "$($run.Name) production translation-fault probe passed without emitting its observation." }
        if (-not (Test-Path -LiteralPath ($faultTrace + '.scalar-memory.json'))) { throw "$($run.Name) scalar load/store translation-fault probe passed without emitting observations." }
        if (-not (Test-Path -LiteralPath ($faultTrace + '.second-stage.json'))) { throw "$($run.Name) second-stage production fetch probe passed without emitting its observation." }
        if (-not (Test-Path -LiteralPath ($faultTrace + '.second-stage-scalar-memory.json'))) { throw "$($run.Name) second-stage scalar-memory probe passed without emitting observations." }
        if (-not (Test-Path -LiteralPath ($faultTrace + '.second-stage-rebind-race.json'))) { throw "$($run.Name) second-stage rebind/retry probe passed without emitting its observation." }
        foreach ($opcodeName in @('VGATHER','VSCATTER')) {
            $opcodeTrace = Join-Path $ArtifactRoot ($run.Name + '-trace-' + $opcodeName + '.json')
            if (-not (Test-Path $opcodeTrace)) { throw "$($run.Name) $opcodeName test passed without emitting the expected runtime trace." }
        }
        $combined = @()
        foreach ($opcodeName in @('VGATHER','VSCATTER')) {
            $combined += @(Get-Content (Join-Path $ArtifactRoot ($run.Name + '-trace-' + $opcodeName + '.json')) -Raw | ConvertFrom-Json)
        }
        $combined | ConvertTo-Json -Depth 20 | Set-Content $trace -Encoding utf8
    }
    $activeTrace = Get-Content (Join-Path $ArtifactRoot 'active-trace.json') -Raw | ConvertFrom-Json
    $goldenTrace = Get-Content (Join-Path $ArtifactRoot 'golden-trace.json') -Raw | ConvertFrom-Json
    $activeJson = ConvertTo-Json -InputObject $activeTrace -Depth 50 -Compress
    $goldenJson = ConvertTo-Json -InputObject $goldenTrace -Depth 50 -Compress
    $same = $activeJson -ceq $goldenJson
    $activeFault = Get-Content (Join-Path $ArtifactRoot 'active-translation-fault.json') -Raw | ConvertFrom-Json
    $goldenFault = Get-Content (Join-Path $ArtifactRoot 'golden-translation-fault.json') -Raw | ConvertFrom-Json
    $normalizeFault = {
        param($o)
        [ordered]@{reason=$o.fault.reason;scope=$o.fault.request.scope;accessKind=$o.fault.request.accessKind;
            virtualAddress=$o.fault.request.virtualAddress;accessSize=$o.fault.request.accessSize;
            operationIdentity=$o.fault.request.operationIdentity;ownerEpoch=$o.fault.ownerEpoch;
            qualification=$o.fault.qualification;auxiliary=$o.fault.auxiliary;faultStage=$o.fault.faultStage;
            hasGuestPhysicalAddress=$o.fault.hasGuestPhysicalAddress;guestPhysicalAddress=$o.fault.guestPhysicalAddress;
            memoryDomainOwnerEpoch=$o.fault.memoryDomainOwnerEpoch;addressSpaceGeneration=$o.fault.addressSpaceGeneration;
            addressSpaceIdentity=$o.fault.addressSpaceIdentity;isValid=$o.isValid}
    }
    $activeFaultCommon = & $normalizeFault $activeFault
    $goldenFaultCommon = & $normalizeFault $goldenFault
    $faultCommonDifferences = @($activeFaultCommon.Keys | Where-Object {
        (ConvertTo-Json -InputObject $activeFaultCommon[$_] -Depth 20 -Compress) -cne
        (ConvertTo-Json -InputObject $goldenFaultCommon[$_] -Depth 20 -Compress)
    })
    $faultCommonSame = $faultCommonDifferences.Count -eq 0
    if ($activeFault.fault.reason -ne 1 -or $activeFault.fault.request.accessKind -ne 1 -or
        $activeFault.fault.ownerEpoch -eq 0 -or $activeFault.isValid -ne $true) {
        throw 'Active production translation-fault observation did not satisfy its expected source contract.'
    }
    $faultComparison = [ordered]@{schema='circuitweave.processor-translation-fault-runtime-differential/v1';scenario='production-fetch-unmapped-address';active=$activeFault;golden=$goldenFault;activeGoldenCommonFieldsEqual=$faultCommonSame;differingCommonFields=$faultCommonDifferences;derivedSurfaceDifference=@{activeFailingVirtualAddress=$activeFault.failingVirtualAddress;goldenFailingVirtualAddressProperty=$goldenFault.failingVirtualAddressException};activeOnlyFields=$activeFault.fault.optionalActiveFields;goldenOnlyFields=$goldenFault.fault.optionalActiveFields;runtimeFrom= 'real CPU_Core.ExecutePipelineCycle and CpuInstructionTranslationFaultException';trust='software runtime observation; not RTL equivalence'}
    $faultComparison | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $ArtifactRoot 'translation-fault-runtime-comparison.json') -Encoding utf8
    $activeScalarFaults = @(Get-Content (Join-Path $ArtifactRoot 'active-translation-fault.json.scalar-memory.json') -Raw | ConvertFrom-Json)
    $goldenScalarFaults = @(Get-Content (Join-Path $ArtifactRoot 'golden-translation-fault.json.scalar-memory.json') -Raw | ConvertFrom-Json)
    if ($activeScalarFaults.Count -ne 2 -or $goldenScalarFaults.Count -ne 2) { throw 'Expected two production scalar-memory translation-fault records per revision.' }
    $scalarFaultRows = [Collections.Generic.List[object]]::new()
    for ($i=0; $i -lt 2; $i++) {
        $activeRecord=$activeScalarFaults[$i];$goldenRecord=$goldenScalarFaults[$i]
        if ($activeRecord.scenario -cne $goldenRecord.scenario) { throw "Active/Golden scalar translation fault scenario differs at index $i." }
        $activeCommon=& $normalizeFault $activeRecord;$goldenCommon=& $normalizeFault $goldenRecord
        $differences=@($activeCommon.Keys|Where-Object {(ConvertTo-Json -InputObject $activeCommon[$_] -Depth 20 -Compress) -cne (ConvertTo-Json -InputObject $goldenCommon[$_] -Depth 20 -Compress)})
        if ($differences.Count -ne 0) { throw "Active/Golden common scalar-memory fault fields differ for $($activeRecord.scenario): $($differences -join ', ')" }
        $scalarFaultRows.Add([ordered]@{scenario=$activeRecord.scenario;activeGoldenCommonFieldsEqual=$true;differingCommonFields=@();active=$activeRecord;golden=$goldenRecord;activeOnlyFields=$activeRecord.fault.optionalActiveFields;goldenOnlyFields=$goldenRecord.fault.optionalActiveFields;runtimeFrom='production LoadMicroOp.Execute/StoreMicroOp.Execute';trust='software production caller observation; not pipeline-cycle timing'})
    }
    $scalarFaultComparison=[ordered]@{schema='circuitweave.processor-scalar-memory-translation-fault-runtime-differential/v1';scenarioCount=$scalarFaultRows.Count;allCommonFieldsEqual=$true;activeTraceSha256=(Get-FileHash (Join-Path $ArtifactRoot 'active-translation-fault.json.scalar-memory.json') -Algorithm SHA256).Hash.ToLowerInvariant();goldenTraceSha256=(Get-FileHash (Join-Path $ArtifactRoot 'golden-translation-fault.json.scalar-memory.json') -Algorithm SHA256).Hash.ToLowerInvariant();records=@($scalarFaultRows)}
    $scalarFaultComparison|ConvertTo-Json -Depth 30|Set-Content (Join-Path $ArtifactRoot 'scalar-memory-translation-fault-comparison.json') -Encoding utf8
    $activeSecondStage=Get-Content (Join-Path $ArtifactRoot 'active-translation-fault.json.second-stage.json') -Raw|ConvertFrom-Json
    $goldenSecondStage=Get-Content (Join-Path $ArtifactRoot 'golden-translation-fault.json.second-stage.json') -Raw|ConvertFrom-Json
    $activeSecondCommon=& $normalizeFault $activeSecondStage;$goldenSecondCommon=& $normalizeFault $goldenSecondStage
    $secondStageDifferences=@($activeSecondCommon.Keys|Where-Object {(ConvertTo-Json -InputObject $activeSecondCommon[$_] -Depth 20 -Compress) -cne (ConvertTo-Json -InputObject $goldenSecondCommon[$_] -Depth 20 -Compress)})
    if($secondStageDifferences.Count -ne 0){throw "Active/Golden second-stage fault fields differ: $($secondStageDifferences -join ', ')"}
    if($activeSecondStage.scenario -cne 'production-fetch-second-stage-violation' -or $activeSecondStage.fault.reason -ne 5 -or $activeSecondStage.fault.faultStage -ne 2 -or -not $activeSecondStage.fault.hasGuestPhysicalAddress -or $activeSecondStage.fault.guestPhysicalAddress -ne 0x3000 -or -not $activeSecondStage.isValid){throw 'Second-stage runtime fault did not satisfy pinned source contract (reason/stage/GPA/validity).'}
    $secondStageComparison=[ordered]@{schema='circuitweave.processor-second-stage-translation-fault-runtime-differential/v1';scenario=$activeSecondStage.scenario;activeGoldenCommonFieldsEqual=$true;differingCommonFields=@();active=$activeSecondStage;golden=$goldenSecondStage;activeOnlyFields=$activeSecondStage.fault.optionalActiveFields;goldenOnlyFields=$goldenSecondStage.fault.optionalActiveFields;activeTraceSha256=(Get-FileHash (Join-Path $ArtifactRoot 'active-translation-fault.json.second-stage.json') -Algorithm SHA256).Hash.ToLowerInvariant();goldenTraceSha256=(Get-FileHash (Join-Path $ArtifactRoot 'golden-translation-fault.json.second-stage.json') -Algorithm SHA256).Hash.ToLowerInvariant();runtimeFrom='real CPU_Core.ExecutePipelineCycle with two-stage bounded policy and memory-domain page walk';trust='software runtime observation; not RTL equivalence'}
    $secondStageComparison|ConvertTo-Json -Depth 30|Set-Content (Join-Path $ArtifactRoot 'second-stage-translation-fault-comparison.json') -Encoding utf8
    $activeSecondStageScalar=@(Get-Content (Join-Path $ArtifactRoot 'active-translation-fault.json.second-stage-scalar-memory.json') -Raw|ConvertFrom-Json)
    $goldenSecondStageScalar=@(Get-Content (Join-Path $ArtifactRoot 'golden-translation-fault.json.second-stage-scalar-memory.json') -Raw|ConvertFrom-Json)
    if($activeSecondStageScalar.Count -ne 2 -or $goldenSecondStageScalar.Count -ne 2){throw 'Expected two second-stage scalar memory records per revision.'}
    $secondStageScalarRows=[Collections.Generic.List[object]]::new()
    for($i=0;$i -lt 2;$i++){
        $a=$activeSecondStageScalar[$i];$g=$goldenSecondStageScalar[$i]
        if($a.scenario -cne $g.scenario){throw "Active/Golden second-stage scalar scenario differs at index $i."}
        $ac=& $normalizeFault $a;$gc=& $normalizeFault $g
        $diff=@($ac.Keys|Where-Object {(ConvertTo-Json -InputObject $ac[$_] -Depth 20 -Compress) -cne (ConvertTo-Json -InputObject $gc[$_] -Depth 20 -Compress)})
        if($diff.Count){throw "Active/Golden second-stage scalar fault differs: $($diff -join ', ')"}
        $expectedKind=if($i -eq 0){2}else{3}
        if($a.fault.reason -ne 5 -or $a.fault.faultStage -ne 2 -or $a.fault.request.accessKind -ne $expectedKind -or -not $a.fault.hasGuestPhysicalAddress -or $a.fault.guestPhysicalAddress -ne 0x3000 -or -not $a.isValid){throw "Unexpected second-stage scalar fault facts: $($a.scenario)"}
        $secondStageScalarRows.Add([ordered]@{scenario=$a.scenario;activeGoldenCommonFieldsEqual=$true;differingCommonFields=@();active=$a;golden=$g;activeOnlyFields=$a.fault.optionalActiveFields;goldenOnlyFields=$g.fault.optionalActiveFields;runtimeFrom='production LoadMicroOp.Execute/StoreMicroOp.Execute with two-stage policy';trust='software production caller observation; not pipeline-cycle timing'})
    }
    $secondStageScalarComparison=[ordered]@{schema='circuitweave.processor-second-stage-scalar-memory-translation-fault-differential/v1';scenarioCount=$secondStageScalarRows.Count;allCommonFieldsEqual=$true;activeTraceSha256=(Get-FileHash (Join-Path $ArtifactRoot 'active-translation-fault.json.second-stage-scalar-memory.json') -Algorithm SHA256).Hash.ToLowerInvariant();goldenTraceSha256=(Get-FileHash (Join-Path $ArtifactRoot 'golden-translation-fault.json.second-stage-scalar-memory.json') -Algorithm SHA256).Hash.ToLowerInvariant();records=@($secondStageScalarRows)}
    $secondStageScalarComparison|ConvertTo-Json -Depth 30|Set-Content (Join-Path $ArtifactRoot 'second-stage-scalar-memory-translation-fault-comparison.json') -Encoding utf8
    $activeRace=Get-Content (Join-Path $ArtifactRoot 'active-translation-fault.json.second-stage-rebind-race.json') -Raw|ConvertFrom-Json
    $goldenRace=Get-Content (Join-Path $ArtifactRoot 'golden-translation-fault.json.second-stage-rebind-race.json') -Raw|ConvertFrom-Json
    $raceFields=@('scenario','inflightRebindSucceeded','inflightRebindExceptionType','generationUnchangedDuringWalk','firstCycleExceptionType','postCycleRebindAttempted','postCycleRebindSucceeded','postCycleRebindExceptionType','retryExceptionType')
    $raceDifferences=[Collections.Generic.List[string]]::new()
    foreach($field in $raceFields){if((ConvertTo-Json $activeRace.$field -Compress) -cne (ConvertTo-Json $goldenRace.$field -Compress)){$raceDifferences.Add($field)}}
    $raceFaultDifferences=@()
    if($null -ne $activeRace.retryFault -and $null -ne $goldenRace.retryFault){
        $activeRaceFault=& $normalizeFault ([pscustomobject]@{fault=$activeRace.retryFault.fault;isValid=$activeRace.retryFault.isValid})
        $goldenRaceFault=& $normalizeFault ([pscustomobject]@{fault=$goldenRace.retryFault.fault;isValid=$goldenRace.retryFault.isValid})
        $raceFaultDifferences=@($activeRaceFault.Keys|Where-Object {(ConvertTo-Json -InputObject $activeRaceFault[$_] -Depth 20 -Compress) -cne (ConvertTo-Json -InputObject $goldenRaceFault[$_] -Depth 20 -Compress)})
        if($raceFaultDifferences.Count){$raceDifferences.Add('retryFault.'+($raceFaultDifferences -join ','))}
    } elseif(($null -eq $activeRace.retryFault) -ne ($null -eq $goldenRace.retryFault)){$raceDifferences.Add('retryFault.presence')}
    $raceComparison=[ordered]@{schema='circuitweave.processor-second-stage-rebind-race-runtime-differential/v1';scenario=$activeRace.scenario;activeGoldenCommonFieldsEqual=($raceDifferences.Count -eq 0);differingFields=@($raceDifferences);differingRetryFaultFields=$raceFaultDifferences;active=$activeRace;golden=$goldenRace;activeTraceSha256=(Get-FileHash (Join-Path $ArtifactRoot 'active-translation-fault.json.second-stage-rebind-race.json') -Algorithm SHA256).Hash.ToLowerInvariant();goldenTraceSha256=(Get-FileHash (Join-Path $ArtifactRoot 'golden-translation-fault.json.second-stage-rebind-race.json') -Algorithm SHA256).Hash.ToLowerInvariant();runtimeFrom='ExecutePipelineCycle blocked page walk, in-flight rebind attempt, completion, and stale retry';trust='runtime transition evidence; observed model divergence remains an open contract'}
    $raceComparison|ConvertTo-Json -Depth 30|Set-Content (Join-Path $ArtifactRoot 'second-stage-rebind-race-runtime-comparison.json') -Encoding utf8
    if ($activeTrace.Count -ne 8) { throw "Unexpected trap-carrier runtime observation count: $($activeTrace.Count)" }
    foreach ($opcodeName in @('VGATHER','VSCATTER')) {
        $opcode = @($activeTrace | Where-Object opcode -eq $(if($opcodeName -eq 'VGATHER'){213}else{214}))
        if ($opcode.Count -ne 4 -or
            (@($opcode | ForEach-Object stage) -join ',') -ne 'canonical-publish,decode-admission,execute-trap-event,trap-retire-boundary') {
            throw "Trap-carrier event order/count mismatch for $opcodeName."
        }
        $publish=$opcode[0]; $admission=$opcode[1]; $execute=$opcode[2]; $retire=$opcode[3]
        if ($publish.decodeFault -or $publish.empty -or $publish.validSlots -ne 1 -or $publish.auxiliaryCandidates -ne 1 -or
            $publish.scalarCandidates -ne 0 -or $publish.slotType -ne 'TrapMicroOp' -or $publish.requiredSlotClass -ne 'LsuClass' -or
            $admission.executionMode -ne 'AuxiliaryOnlyReference' -or -not $admission.usesIssuePacket -or
            $admission.auxiliaryReservationMask -ne 1 -or $admission.scalarIssueMask -ne 0 -or
            -not $admission.lane4Occupied -or $admission.lane4Type -ne 'TrapMicroOp' -or $admission.lane7Occupied -or
            $execute.causeCode -ne 2 -or $execute.faultAddress -ne 0 -or $execute.eventType -ne 'TrapEntryEvent' -or
            -not $execute.resultReady -or -not $execute.vectorComplete -or $execute.lane7Occupied -or
            $retire.serializingEpochDelta -ne 1 -or $retire.assistInvalidationReason -ne 'Trap' -or
            $retire.coreReplayActive -or $retire.schedulerReplayActive -or
            $retire.coreReplayReason -ne 'SerializingEvent' -or $retire.schedulerReplayReason -ne 'SerializingEvent' -or
            $retire.mepc -ne 0x7C00 -or $retire.mcause -ne 2 -or $retire.mtval -ne 0 -or
            $retire.committedPc -ne 0x1800 -or $retire.activeLivePc -ne 0x1800 -or
            $retire.pipelineState -ne 'Task' -or $retire.instructionsRetired -ne 1 -or
            $retire.scalarLanesRetired -ne 0 -or $retire.nonScalarLanesRetired -ne 1) {
            throw "Independent trap-carrier oracle rejected $opcodeName runtime observations."
        }
    }
    $sourcePaths = @(
        'HybridCPU_ISE.Tests/tests/Phase09TrapCarrierFollowThroughTests.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Runtime/Memory/Translation/CpuInstructionTranslationContour.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Architecture/State/Architectural/CPU_Core.StateData.cs',
        'HybridCPU_ISE/CloseToHSL/Core/State/CpuCorePlatformContext.cs',
        'HybridCPU_ISE/NonRTL/Arch/Generated/GeneratedIsaOpcodeValues.g.cs',
        'HybridCPU_ISE/Legacy/CloseToHSL/Core/Decoder/DecodedBundleTransportProjector.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/MicroOps/Types/MicroOp.IO.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.PipelineExecution.Types.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.PipelineExecution.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.PipelineExecution.StageFlow.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/Core/CPU_Core.TestSupport.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.PipelineExecution.Retire.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/PipelineEvent.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/PipelineFsmEventHandler.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Architecture/State/Pipeline/PipelineFsmGuard.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Architecture/State/Pipeline/PipelineState.cs')
    $sourcePins = foreach ($relative in $sourcePaths) {
        $activePath = Join-Path $activeTree (Join-Path 'HybridCPU ISE' $relative.Replace('/', '\'))
        $goldenPath = Join-Path $extract ($relative.Replace('/', '\'))
        if ($relative -eq 'HybridCPU_ISE.Tests/tests/Phase09TrapCarrierFollowThroughTests.cs') {
            $activePath += '.source-before-trace'
            $goldenPath += '.source-before-trace'
        }
        $blob = (git --git-dir=$GoldenGitDir rev-parse ($goldenCommit + ':' + $relative)).Trim()
        $materializedBlob = (git hash-object $goldenPath).Trim()
        if ($materializedBlob -cne $blob) { throw "Materialized Golden source is not the pinned Git blob: $relative" }
        $activePin = if ($ActiveSourceMode -eq 'PinnedGitTree') { (git -C $iseRoot rev-parse ($activeCommit + ':' + $relative)).Trim() } else { $null }
        if ($ActiveSourceMode -eq 'PinnedGitTree' -and (git hash-object $activePath).Trim() -cne $activePin) { throw "Materialized active source is not the pinned Git blob: $relative" }
        [ordered]@{ path=$relative; activeSha256=(Get-FileHash $activePath -Algorithm SHA256).Hash.ToLowerInvariant(); activeGitBlob=$activePin; goldenMaterializedSha256=(Get-FileHash $goldenPath -Algorithm SHA256).Hash.ToLowerInvariant(); goldenGitBlob=$blob }
    }
    $activeTestAssembly = Join-Path $activeIse '.trapcarrier-build\bin\TrapCarrierRuntimeTrace\Debug\net11.0\HybridCPU_ISE.Tests.dll'
    $goldenTestAssembly = Join-Path $goldenIse '.trapcarrier-build\bin\TrapCarrierRuntimeTrace\Debug\net11.0\HybridCPU_ISE.Tests.dll'
    $activeHarnessProject = Join-Path $activeIse 'TrapCarrierRuntimeTrace.csproj'
    $goldenHarnessProject = Join-Path $goldenIse 'TrapCarrierRuntimeTrace.csproj'
    $activeTraceTestSource = Join-Path $activeIse 'HybridCPU_ISE.Tests\tests\Phase09TrapCarrierFollowThroughTests.cs'
    $goldenTraceTestSource = Join-Path $goldenIse 'HybridCPU_ISE.Tests\tests\Phase09TrapCarrierFollowThroughTests.cs'
    $activeWorkingTreeDescription = if ($ActiveSourceMode -eq 'PinnedGitTree') {
        'git archive of exact active model commit; source pins require blob equality'
    } else {
        'copied from current checkout; uncommitted overlays may be present'
    }
    $activeCompilersTreeDescription = if ($ActiveSourceMode -eq 'PinnedGitTree') {
        'Compilers subtree included in active model commit archive'
    } else {
        'companion compiler worktree copied; its tracked/untracked inputs may differ from active model commit'
    }
    $comparison = [ordered]@{
        schema = 'circuitweave.trap-carrier-runtime-differential/v1'
        scenario = 'VGATHER/VSCATTER canonical unsupported auxiliary decode, issue, trap event, retire and replay-boundary publication'
        activeModelCommit = $activeCommit
        activeSourceMode = $ActiveSourceMode
        activeModelCompilersTree = $activeCompilersTreeDescription
        activeModelArchiveSha256 = $activeIseArchiveSha256
        activeWorkingTreeOverlayUsed = ($ActiveSourceMode -eq 'WorkingTree')
        activeWorkingTreeSnapshot = $activeWorkingTreeDescription
        goldenCommit = $goldenCommit
        goldenTreeSource = 'git archive from pinned commit object'
        activeModelGitHead = $activeIseGitHead
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
        translationFaultProbeSourceSha256 = (Get-FileHash (Join-Path $PSScriptRoot 'TranslationFaultRuntimeProbeTests.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
        activeTranslationFaultFixtureTestSha256 = (Get-FileHash (Join-Path $activeIse 'HybridCPU_ISE.Tests\VmxRefactoring\VmxPhase54CanonicalCpuInstructionTranslationContourTests.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
        activeTranslationFaultTraceSha256 = (Get-FileHash (Join-Path $ArtifactRoot 'active-translation-fault.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        goldenTranslationFaultTraceSha256 = (Get-FileHash (Join-Path $ArtifactRoot 'golden-translation-fault.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        translationFaultCommonFieldsEqual = $faultCommonSame
        translationFaultRuntimeComparisonSha256 = (Get-FileHash (Join-Path $ArtifactRoot 'translation-fault-runtime-comparison.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        scalarMemoryTranslationFaultCommonFieldsEqual = $true
        activeScalarMemoryTranslationFaultTraceSha256 = (Get-FileHash (Join-Path $ArtifactRoot 'active-translation-fault.json.scalar-memory.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        goldenScalarMemoryTranslationFaultTraceSha256 = (Get-FileHash (Join-Path $ArtifactRoot 'golden-translation-fault.json.scalar-memory.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        scalarMemoryTranslationFaultRuntimeComparisonSha256 = (Get-FileHash (Join-Path $ArtifactRoot 'scalar-memory-translation-fault-comparison.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        scalarMemoryTranslationFaultScenarios = @('production-scalar-load-unmapped','production-scalar-store-access-denied')
        secondStageTranslationFaultCommonFieldsEqual = $true
        activeSecondStageTranslationFaultTraceSha256 = $secondStageComparison.activeTraceSha256
        goldenSecondStageTranslationFaultTraceSha256 = $secondStageComparison.goldenTraceSha256
        secondStageTranslationFaultRuntimeComparisonSha256 = (Get-FileHash (Join-Path $ArtifactRoot 'second-stage-translation-fault-comparison.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        secondStageScalarMemoryCommonFieldsEqual = $true
        activeSecondStageScalarTraceSha256 = $secondStageScalarComparison.activeTraceSha256
        goldenSecondStageScalarTraceSha256 = $secondStageScalarComparison.goldenTraceSha256
        secondStageScalarMemoryComparisonSha256 = (Get-FileHash (Join-Path $ArtifactRoot 'second-stage-scalar-memory-translation-fault-comparison.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        secondStageRebindRaceCommonFieldsEqual = $raceEqual
        activeSecondStageRebindRaceTraceSha256 = $raceComparison.activeTraceSha256
        goldenSecondStageRebindRaceTraceSha256 = $raceComparison.goldenTraceSha256
        secondStageRebindRaceComparisonSha256 = (Get-FileHash (Join-Path $ArtifactRoot 'second-stage-rebind-race-runtime-comparison.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        eventCount = $activeTrace.Count
        opcodeCases = @('VGATHER','VSCATTER')
        stageEventsPerCase = @('canonical-publish','decode-admission','execute-trap-event','trap-retire-boundary')
        tracesEqual = $same
        interpretation = 'Observations from the existing Phase09TrapCarrierFollowThroughTests in active and exact pinned-Golden builds. The original test uses internal TestDecode/TestRun stage support; these are bounded software pipeline-stage operations and not an ExecutePipelineCycle timing trace or physical trap RTL equivalence.'
        isolatedBuildRoot = $tempRoot
    }
    $comparison | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $ArtifactRoot 'comparison.json') -Encoding utf8
    if (-not $same) { throw 'Active and pinned-Golden normalized runtime event traces differ; inspect comparison artifacts.' }
    Write-Output (Get-Content (Join-Path $ArtifactRoot 'comparison.json') -Raw)
}
finally {
    Remove-Item Env:CW_TRAP_CARRIER_TRACE_OUTPUT -ErrorAction SilentlyContinue
    Remove-Item Env:CW_TRANSLATION_FAULT_RUNTIME_TRACE_OUTPUT -ErrorAction SilentlyContinue
}
