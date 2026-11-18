param(
    [string]$GoldenGitDir = "$env:TEMP\cw-golden-6946ccab-20261114",
    [string]$ArtifactRoot = "C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\processor-20261114\retire-cycle-active-golden-v1",
    [switch]$IncludeScalarStorePublicationProbe,
    [switch]$IncludeSelectedStoreRevalidationProbe,
    [switch]$IncludeSelectedStoreCurrentPteProbe,
    [switch]$IncludeSelectedStoreBackingOwnerProbe,
    [switch]$SelectedStoreBackingOwnerOnly
)

$ErrorActionPreference = 'Stop'
$goldenCommit = '6946ccab9d888f7dae0a4d8e18480f436138acd9'
$activeCommit = '1bfe41a49cf11c80fa6c6aee87accb54b78b331f'
$iseRoot = 'C:\Users\Yuriy\Desktop\HybridCPU ISE'
$compilerRoot = 'C:\Users\Yuriy\Desktop\HybridCPU Compilers'
$tempRoot = Join-Path $env:TEMP ('writeback-fault-active-golden-' + [guid]::NewGuid().ToString('N'))
$activeTree = Join-Path $tempRoot 'active'
$goldenTree = Join-Path $tempRoot 'golden'
if (Test-Path -LiteralPath $ArtifactRoot) { throw "ArtifactRoot must be new: $ArtifactRoot" }
if ((git -C $iseRoot rev-parse HEAD).Trim() -cne $activeCommit) { throw 'Active ISE HEAD differs from the pinned revision.' }
New-Item -ItemType Directory -Force -Path $activeTree, $goldenTree, $ArtifactRoot | Out-Null

function Copy-SourceTree([string]$source, [string]$destination) {
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Missing source directory: $source" }
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    $excluded = @('.git','bin','obj','TestResults','packages','node_modules','.vs','Documentation','docs','TempEnv',(Join-Path $source 'artifacts'),(Join-Path $source '.artifacts'))
    & robocopy $source $destination /E /NFL /NDL /NJH /NJS /NP /XD $excluded /XF *.user *.suo
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE): $source" }
}

function Copy-ActiveInputs([string]$tree) {
    $ise = Join-Path $tree 'HybridCPU ISE'; $compiler = Join-Path $tree 'HybridCPU Compilers'
    foreach ($relative in @('HybridCPU_ISE','HybridCPU_ISE.Tests','HybridCPU_ExternalRuntime.Contracts')) { Copy-SourceTree (Join-Path $iseRoot $relative) (Join-Path $ise $relative) }
    Copy-SourceTree (Join-Path $iseRoot 'Diagnostics\ShadowFrontendCpuWorkload') (Join-Path $ise 'Diagnostics\ShadowFrontendCpuWorkload')
    foreach ($file in @('Directory.Build.props','global.json','.editorconfig')) { Copy-Item -LiteralPath (Join-Path $iseRoot $file) -Destination (Join-Path $ise $file) }
    foreach ($relative in @('Compilers','corpus')) { Copy-SourceTree (Join-Path $compilerRoot $relative) (Join-Path $compiler $relative) }
}

function Add-TraceToMethod([string]$source, [string]$methodName, [string]$traceStatement) {
    $marker = "public void $methodName()"
    $start = $source.IndexOf($marker, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Test method changed or missing: $methodName" }
    $open = $source.IndexOf('{', $start)
    if ($open -lt 0) { throw "Test method has no body: $methodName" }
    $depth = 0
    for ($i = $open; $i -lt $source.Length; $i++) {
        if ($source[$i] -eq '{') { $depth++ }
        elseif ($source[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { return $source.Insert($i, "`r`n$traceStatement`r`n        ") }
        }
    }
    throw "Could not find end of method: $methodName"
}

function Prepare-Test([string]$tree, [string]$name, [bool]$includeScalarStorePublicationProbe, [bool]$includeSelectedStoreRevalidationProbe, [bool]$includeSelectedStoreCurrentPteProbe, [bool]$includeSelectedStoreBackingOwnerProbe) {
    $ise = if (Test-Path -LiteralPath (Join-Path $tree 'HybridCPU_ISE')) { $tree } else { Join-Path $tree 'HybridCPU ISE' }
    $testFiles = @(
        'HybridCPU_ISE.Tests\tests\WriteBackFaultOrderingTests.cs',
        'HybridCPU_ISE.Tests\tests\Phase09SingleLaneExecuteFaultTailTests.cs',
        'HybridCPU_ISE.Tests\tests\Phase09ExplicitPacketExecuteFaultTailTests.cs'
    ) | ForEach-Object { Join-Path $ise $_ }
    $retireCycleTest = Join-Path $ise 'HybridCPU_ISE.Tests\tests\RetireCycleTraceTests.cs'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RetireCycleTraceTests.cs') -Destination $retireCycleTest
    $testFiles += $retireCycleTest
    if ($includeScalarStorePublicationProbe) {
        $storeProbeTest = Join-Path $ise 'HybridCPU_ISE.Tests\tests\RetireStorePublicationTraceTests.cs'
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RetireStorePublicationTraceTests.cs') -Destination $storeProbeTest
        $testFiles += $storeProbeTest
    }
    if ($includeSelectedStoreBackingOwnerProbe -and $name -eq 'active') {
        $ownerProbe = Join-Path $ise 'HybridCPU_ISE.Tests\Architecture\SelectedStoreBackingOwnerAtomicTests.cs'
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SelectedStoreBackingOwnerAtomicTests.cs') -Destination $ownerProbe
        $testFiles += $ownerProbe
    }
    $probeTest = Join-Path $ise 'HybridCPU_ISE.Tests\tests\PageFaultDeliveryRuntimeProbeTests.cs'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PageFaultDeliveryRuntimeProbeTests.cs') -Destination $probeTest
    $testFiles += $probeTest
    if (($includeSelectedStoreRevalidationProbe -or $includeSelectedStoreCurrentPteProbe) -and $name -eq 'active') {
        if ($includeSelectedStoreRevalidationProbe) {
            $selectedProbe = Join-Path $ise 'HybridCPU_ISE.Tests\tests\SelectedStoreRetireRevalidationProbeTests.cs'
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SelectedStoreRetireRevalidationProbeTests.cs') -Destination $selectedProbe
            $testFiles += $selectedProbe
        }
        if ($includeSelectedStoreCurrentPteProbe) {
            $pteProbe = Join-Path $ise 'HybridCPU_ISE.Tests\tests\SelectedStoreCurrentPteRetireRevalidationProbeTests.cs'
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SelectedStoreCurrentPteRetireRevalidationProbeTests.cs') -Destination $pteProbe
            $testFiles += $pteProbe
        }
        $supportPath = Join-Path $ise 'HybridCPU_ISE\CloseToHSL\Core\Pipeline\Core\CPU_Core.TestSupport.cs'
        $supportText = [IO.File]::ReadAllText($supportPath)
        $signaturePattern = 'int vtId = 0,\s+InstructionsEnum\? opcode = null\)'
        $helperStart = $supportText.IndexOf('internal void TestPrepareExplicitPacketStoreForWriteBack', [StringComparison]::Ordinal)
        $helperOpen = $supportText.IndexOf('{', $helperStart)
        if ($helperStart -lt 0 -or $helperOpen -lt 0) { throw 'Selected-store test helper declaration changed.' }
        $helperHeader = $supportText.Substring($helperStart, $helperOpen - $helperStart)
        if ([regex]::Matches($helperHeader, $signaturePattern).Count -ne 1) { throw 'Selected-store test helper signature changed or is ambiguous.' }
        $signatureReplacement = "int vtId = 0,`r`n                InstructionsEnum? opcode = null,`r`n                ulong domainTag = 0,`r`n                int contextId = 0)"
        $helperHeader = [regex]::Replace($helperHeader, $signaturePattern, $signatureReplacement, 1)
        $supportText = $supportText.Substring(0, $helperStart) + $helperHeader + $supportText.Substring($helperOpen)
        $methodEnd = $supportText.IndexOf('/// <summary>', $helperOpen, [StringComparison]::Ordinal)
        if ($methodEnd -lt 0) { throw 'Selected-store test helper end marker is missing.' }
        $helperBody = $supportText.Substring($helperStart, $methodEnd - $helperStart)
        $helperBody = $helperBody.Replace('OwnerContextId = vtId;', 'OwnerContextId = (contextId == 0 ? vtId : contextId);')
        $helperBody = $helperBody.Replace('OwnerContextId = vtId,', 'OwnerContextId = (contextId == 0 ? vtId : contextId),')
        if (-not $helperBody.Contains('OwnerContextId = (contextId == 0 ? vtId : contextId);')) { throw 'Could not add selected-store scope context to helper copy.' }
        $supportText = $supportText.Substring(0, $helperStart) + $helperBody + $supportText.Substring($methodEnd)
        $metadataMarker = 'storeOp.InitializeMetadata();'
        $metadataIndex = $supportText.IndexOf($metadataMarker, $helperStart, [StringComparison]::Ordinal)
        if ($helperStart -lt 0 -or $metadataIndex -lt 0) { throw 'Selected-store helper metadata marker is missing.' }
        $supportText = $supportText.Insert($metadataIndex + $metadataMarker.Length, "`r`n                storeOp.Placement = storeOp.Placement with { DomainTag = domainTag };")
        [IO.File]::WriteAllText($supportPath, $supportText, [Text.UTF8Encoding]::new($false))
    }    $originalHashes = [ordered]@{}
    foreach ($testFile in $testFiles) {
        $bytes = [IO.File]::ReadAllBytes($testFile)
        $originalHashes[[IO.Path]::GetFileName($testFile)] = (Get-FileHash $testFile -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllBytes($testFile + '.source-before-trace', $bytes)
    }
    $tracePrefix = 'File.AppendAllText(Environment.GetEnvironmentVariable("CW_WRITEBACK_FAULT_TRACE")!, JsonSerializer.Serialize('
    $traceSuffix = ') + Environment.NewLine);'
    $snippets = @{
        'WriteBackFaultWinner_WhenLane7FaultIsOlderBySlotOrder_ThenYoungerLane4StoreDoesNotCommit' = $tracePrefix + 'new { scenario = "lane7-fault-before-lane4-store", address = ex.FaultAddress, isWrite = ex.IsWrite, storeValue = BitConverter.ToUInt64(Processor.MainMemory.ReadFromPosition(new byte[8], storeAddress, 8), 0), retired = control.InstructionsRetired, nonScalarRetired = control.NonScalarLanesRetired }' + $traceSuffix
        'WriteBackFaultWinner_WhenLane4FaultIsYoungerBySlotOrder_ThenOlderLane7CsrEffectRetiresBeforeTrap' = $tracePrefix + 'new { scenario = "lane7-csr-before-lane4-fault", address = ex.FaultAddress, isWrite = ex.IsWrite, fspEnabled = core.VectorConfig.FSP_Enabled, csrReadback = core.ReadArch(0, 9), retired = control.InstructionsRetired, nonScalarRetired = control.NonScalarLanesRetired }' + $traceSuffix
        'WriteBackFaultWinner_WhenOlderLane7ConditionalBranchRetiresRedirect_ThenYoungerLane4FaultIsSuppressed' = $tracePrefix + 'new { scenario = "lane7-branch-before-lane4-fault", committedPc = core.ReadCommittedPc(0), livePc = core.ReadActiveLivePc(), retired = control.InstructionsRetired, scalarRetired = control.ScalarLanesRetired, nonScalarRetired = control.NonScalarLanesRetired, youngerSuppressed = control.ExceptionYoungerSuppressCount }' + $traceSuffix
        'WriteBackFaultWinner_WhenOlderLane4FaultPrecedesYoungerLane7ConditionalBranch_ThenBranchDoesNotRedirect' = $tracePrefix + 'new { scenario = "lane4-fault-before-lane7-branch", address = ex.FaultAddress, isWrite = ex.IsWrite, committedPc = core.ReadCommittedPc(0), livePc = core.ReadActiveLivePc(), retired = control.InstructionsRetired, nonScalarRetired = control.NonScalarLanesRetired }' + $traceSuffix
        'SingleLaneMicroOp_WhenExecuteThrowsPageFault_ThenPropagatesStageAwareFault' = $tracePrefix + 'new { scenario = "single-lane-read-page-fault", address = ex.FaultAddress, isWrite = ex.IsWrite, executeValid = core.GetExecuteStage().Valid, forwardingValid = core.TestGetExecuteForwardingPath().Valid, retired = core.GetPipelineControl().InstructionsRetired, rawFallbacks = core.TestGetReferenceRawFallbackCount() }' + $traceSuffix
        'SingleLaneMicroOp_WhenExecuteThrowsAlignmentFault_ThenRethrowsTranslatedPageFault' = $tracePrefix + 'new { scenario = "single-lane-write-alignment-to-page-fault", address = ex.FaultAddress, isWrite = ex.IsWrite, alignmentCause = ex.InnerException is MemoryAlignmentException, executeValid = core.GetExecuteStage().Valid, forwardingValid = core.TestGetExecuteForwardingPath().Valid, retired = core.GetPipelineControl().InstructionsRetired, rawFallbacks = core.TestGetReferenceRawFallbackCount() }' + $traceSuffix
        'ExplicitPacketGenericMicroOp_WhenExecuteThrowsPageFault_ThenPropagatesStageAwareFault' = $tracePrefix + 'new { scenario = "explicit-packet-read-page-fault", address = ex.FaultAddress, isWrite = ex.IsWrite, executeValid = core.GetExecuteStage().Valid, forwardingValid = core.TestGetExecuteForwardingPath().Valid, retired = core.GetPipelineControl().InstructionsRetired, rawFallbacks = core.TestGetReferenceRawFallbackCount() }' + $traceSuffix
        'ExplicitPacketGenericMicroOp_WhenExecuteThrowsAlignmentFault_ThenRethrowsTranslatedPageFault' = $tracePrefix + 'new { scenario = "explicit-packet-write-alignment-to-page-fault", address = ex.FaultAddress, isWrite = ex.IsWrite, alignmentCause = ex.InnerException is MemoryAlignmentException, executeValid = core.GetExecuteStage().Valid, forwardingValid = core.TestGetExecuteForwardingPath().Valid, retired = core.GetPipelineControl().InstructionsRetired, rawFallbacks = core.TestGetReferenceRawFallbackCount() }' + $traceSuffix
    }
    foreach ($testFile in $testFiles) {
        $source = [IO.File]::ReadAllText($testFile)
        if ($source -match 'using System;') { $source = $source.Replace('using System;', "using System;`r`nusing System.IO;`r`nusing System.Text.Json;") }
        elseif ($source -match 'using System.IO;') { }
        else { throw "Cannot inject trace imports into $testFile" }
        foreach ($methodName in $snippets.Keys) {
            if ($source.Contains("public void $methodName()")) { $source = Add-TraceToMethod $source $methodName $snippets[$methodName] }
        }
        [IO.File]::WriteAllText($testFile, $source, [Text.UTF8Encoding]::new($false))
    }

    $globalUsings = Join-Path $ise 'HybridCPU_ISE.Tests\GlobalUsings.cs'
    $project = Join-Path $ise 'WriteBackFaultRuntime.csproj'
    $iseProject = Join-Path $ise 'HybridCPU_ISE\HybridCPU_ISE.csproj'
    $iseProjectOriginalSha256 = (Get-FileHash $iseProject -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllBytes($iseProject + '.source-before-harness', [IO.File]::ReadAllBytes($iseProject))
    $compilerCandidates = @(
        (Join-Path $tree 'HybridCPU Compilers\Compilers'),
        (Join-Path $tree 'Compilers')
    )
    $compilerProjectRoot = $compilerCandidates | Where-Object { Test-Path (Join-Path $_ 'HybridCPU_Platform.Contracts\HybridCPU.Platform.Contracts.csproj') } | Select-Object -First 1
    if (-not $compilerProjectRoot) { throw "Copied Platform/RuntimeKernel projects not found below $tree" }
    $iseProjectDirectory = Split-Path $iseProject -Parent
    $platformProject = [IO.Path]::GetRelativePath($iseProjectDirectory, (Join-Path $compilerProjectRoot 'HybridCPU_Platform.Contracts\HybridCPU.Platform.Contracts.csproj'))
    $runtimeKernelProject = [IO.Path]::GetRelativePath($iseProjectDirectory, (Join-Path $compilerProjectRoot 'HybridCPU_RuntimeKernel\HybridCPU.RuntimeKernel.csproj'))
    $iseProjectText = [IO.File]::ReadAllText($iseProject)
    $itemClose = $iseProjectText.IndexOf('</ItemGroup>', [StringComparison]::Ordinal)
    if ($itemClose -lt 0) { throw "Could not find project reference ItemGroup in copied ISE project: $iseProject" }
    $references = "`r`n        <ProjectReference Include=`"$platformProject`" GlobalPropertiesToRemove=`"DefineTestSupport;BuildRuntimeDiagnosticConsole;HybridCpuIseProjectReferenceProperties`" />`r`n        <ProjectReference Include=`"$runtimeKernelProject`" GlobalPropertiesToRemove=`"DefineTestSupport;BuildRuntimeDiagnosticConsole;HybridCpuIseProjectReferenceProperties`" />`r`n    "
    [IO.File]::WriteAllText($iseProject, $iseProjectText.Insert($itemClose, $references), [Text.UTF8Encoding]::new($false))
    $runRoot = Join-Path $ArtifactRoot ($name + '-test-artifacts')
    New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
    $objPath = Join-Path $ise '.writeback-fault-build\obj\HybridCPU_ISE'
    $escapedTests = $testFiles | ForEach-Object { '<Compile Include="' + [Security.SecurityElement]::Escape($_) + '" />' }
    $escapedGlobal = [Security.SecurityElement]::Escape($globalUsings)
    $escapedIse = [Security.SecurityElement]::Escape($iseProject)
    $escapedObj = [Security.SecurityElement]::Escape($objPath)
    $escapedRun = [Security.SecurityElement]::Escape($runRoot)
    $projectText = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net11.0</TargetFramework><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject><EnableDefaultCompileItems>false</EnableDefaultCompileItems><AssemblyName>HybridCPU_ISE.Tests</AssemblyName><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.12.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="4.0.1" PrivateAssets="all" />
    <Using Include="Xunit" />
    $($escapedTests -join "`r`n    ")<Compile Include="$escapedGlobal" />
    <ProjectReference Include="$escapedIse"><AdditionalProperties>DefineTestSupport=true;EnableInternalTestHooks=true;HybridCpuTestArtifactsRoot=$escapedRun;BaseIntermediateOutputPath=$escapedObj\;MSBuildProjectExtensionsPath=$escapedObj\;OutputPath=$escapedRun\ise-bin\</AdditionalProperties></ProjectReference>
  </ItemGroup>
</Project>
"@
    [IO.File]::WriteAllText($project, $projectText, [Text.UTF8Encoding]::new($false))
    return @{ Project=$project; TestFiles=$testFiles; OriginalHashes=$originalHashes; RunRoot=$runRoot; IseProjectOriginalSha256=$iseProjectOriginalSha256; IseProjectWithHarnessReferencesSha256=(Get-FileHash $iseProject -Algorithm SHA256).Hash.ToLowerInvariant(); HarnessCompilerReferences=@($platformProject,$runtimeKernelProject) }
}

function Configure-ExternalBuildPaths([string]$root, [string]$iseRootPath) {
    $propsPath = Join-Path $root 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $propsPath)) { [IO.File]::WriteAllText($propsPath, '<Project></Project>', [Text.UTF8Encoding]::new($false)) }
    $props = [IO.File]::ReadAllText($propsPath)
    $close = $props.LastIndexOf('</Project>', [StringComparison]::OrdinalIgnoreCase)
    if ($close -lt 0) { throw "Invalid Directory.Build.props: $propsPath" }
    $addition = @'
<PropertyGroup>
  <BaseIntermediateOutputPath>$(MSBuildThisFileDirectory).writeback-fault-build/obj/$(MSBuildProjectName)/</BaseIntermediateOutputPath>
  <MSBuildProjectExtensionsPath>$(BaseIntermediateOutputPath)</MSBuildProjectExtensionsPath>
  <BaseOutputPath>$(MSBuildThisFileDirectory).writeback-fault-build/bin/$(MSBuildProjectName)/</BaseOutputPath>
</PropertyGroup>
<PropertyGroup Condition="'$(HybridCpuTestArtifactsRoot)' != ''">
  <HybridCpuIseProjectReferenceProperties>DefineTestSupport=$(DefineTestSupport);EnableInternalTestHooks=true;HybridCpuTestArtifactsRoot=$(HybridCpuTestArtifactsRoot);BaseIntermediateOutputPath=$(MSBuildThisFileDirectory).writeback-fault-build/obj/HybridCPU_ISE/;MSBuildProjectExtensionsPath=$(MSBuildThisFileDirectory).writeback-fault-build/obj/HybridCPU_ISE/</HybridCpuIseProjectReferenceProperties>
</PropertyGroup>
'@
    [IO.File]::WriteAllText($propsPath, $props.Insert($close, $addition), [Text.UTF8Encoding]::new($false))
    $workspacePropsPath = Join-Path (Split-Path $iseRootPath -Parent) 'Directory.Build.props'
    $workspaceProps = [IO.File]::ReadAllText($workspacePropsPath)
    $workspaceClose = $workspaceProps.LastIndexOf('</Project>', [StringComparison]::OrdinalIgnoreCase)
    if ($workspaceClose -lt 0) { throw "Invalid ISE workspace Directory.Build.props: $workspacePropsPath" }
    [IO.File]::WriteAllText($workspacePropsPath, $workspaceProps.Insert($workspaceClose, $addition), [Text.UTF8Encoding]::new($false))
    $localPropsPath = Join-Path $iseRootPath 'Directory.Build.props'
    $localProps = @'
<Project><PropertyGroup>
  <BaseIntermediateOutputPath>$(MSBuildThisFileDirectory)..\.writeback-fault-build/obj/HybridCPU_ISE/</BaseIntermediateOutputPath>
  <MSBuildProjectExtensionsPath>$(BaseIntermediateOutputPath)</MSBuildProjectExtensionsPath>
  <HybridCpuIseProjectReferenceProperties>DefineTestSupport=$(DefineTestSupport);HybridCpuTestArtifactsRoot=$(HybridCpuTestArtifactsRoot);BaseIntermediateOutputPath=$(MSBuildThisFileDirectory)..\.writeback-fault-build/obj/HybridCPU_ISE/;MSBuildProjectExtensionsPath=$(MSBuildThisFileDirectory)..\.writeback-fault-build/obj/HybridCPU_ISE/;OutputPath=$(HybridCpuTestArtifactsRoot)\ise-bin\</HybridCpuIseProjectReferenceProperties>
</PropertyGroup></Project>
'@
    [IO.File]::WriteAllText($localPropsPath, $localProps, [Text.UTF8Encoding]::new($false))
}

try {
    Copy-ActiveInputs $activeTree
    $paths = @('HybridCPU_ISE','HybridCPU_ISE.Tests','HybridCPU_ExternalRuntime.Contracts','Compilers','corpus','Directory.Build.props','global.json')
    $goldenRoot = Join-Path $tempRoot 'golden\source'
    $goldenMaterializationManifest = Join-Path $ArtifactRoot 'golden-raw-blob-materialization.json'
    if (-not $SelectedStoreBackingOwnerOnly) {
        & python (Join-Path $PSScriptRoot 'MaterializePinnedGoldenBlobs.py') --git-dir $GoldenGitDir --commit $goldenCommit --output-root $goldenRoot --manifest $goldenMaterializationManifest --paths $paths
        if ($LASTEXITCODE -ne 0) { throw 'Pinned Golden raw-blob materialization failed.' }
        $goldenMaterialization = Get-Content -LiteralPath $goldenMaterializationManifest -Raw | ConvertFrom-Json -AsHashtable
        if ($goldenMaterialization.schema -ne 'circuitweave.pinned-git-raw-blob-materialization/v1' -or $goldenMaterialization.commit -cne $goldenCommit) { throw 'Pinned Golden materialization receipt mismatch.' }
    }
    $activeTest = Prepare-Test $activeTree 'active' $IncludeScalarStorePublicationProbe.IsPresent $IncludeSelectedStoreRevalidationProbe.IsPresent $IncludeSelectedStoreCurrentPteProbe.IsPresent $IncludeSelectedStoreBackingOwnerProbe.IsPresent
    if (-not $SelectedStoreBackingOwnerOnly) { $goldenTest = Prepare-Test $goldenRoot 'golden' $IncludeScalarStorePublicationProbe.IsPresent $IncludeSelectedStoreRevalidationProbe.IsPresent $IncludeSelectedStoreCurrentPteProbe.IsPresent $false }
    Configure-ExternalBuildPaths $activeTree (Join-Path $activeTree 'HybridCPU ISE\HybridCPU_ISE')
    if (-not $SelectedStoreBackingOwnerOnly) { Configure-ExternalBuildPaths $goldenRoot (Join-Path $goldenRoot 'HybridCPU_ISE') }
    $runs = if ($SelectedStoreBackingOwnerOnly) { @(@{Name='active';Test=$activeTest}) } else { @(@{Name='active';Test=$activeTest},@{Name='golden';Test=$goldenTest}) }
    $traces = @{}
    $suiteDefinitions = @(
        @{Name='WriteBackFaultOrdering';Filter='WriteBackFaultOrderingTests';Expected=4},
        @{Name='SingleLaneExecuteFaultTail';Filter='SingleLaneExecuteFaultTailTests';Expected=5},
        @{Name='ExplicitPacketExecuteFaultTail';Filter='ExplicitPacketExecuteFaultTailTests';Expected=3},
        @{Name='PageFaultDeliveryRuntimeProbe';Filter='PageFaultDeliveryRuntimeProbeTests&FullyQualifiedName!~ManagedGuestRunner_MemoryReadFault_EscapesPublicApi';Expected=4},
        @{Name='RetireCycleTrace';Filter='RetireCycleTraceTests';Expected=1}
    )
    if ($IncludeScalarStorePublicationProbe) {
        $suiteDefinitions += @{Name='RetireStorePublicationTrace';Filter='RetireStorePublicationTraceTests';Expected=1}
    }
    $selectedSuite = @{Name='SelectedStoreRetireRevalidation';Filter='SelectedStoreRetireRevalidationProbeTests';Expected=1}
    $selectedPteSuite = @{Name='SelectedStoreCurrentPteRetireRevalidation';Filter='SelectedStoreCurrentPteRetireRevalidationProbeTests';Expected=3}
    $selectedBackingOwnerSuite = @{Name='SelectedStoreBackingOwnerAtomic';Filter='SelectedStoreBackingOwnerAtomicTests';Expected=14}
    $suiteCounts = [ordered]@{}
    foreach ($run in $runs) {
        $tracePath = Join-Path $ArtifactRoot ($run.Name + '-trace.jsonl')
        [IO.File]::WriteAllText($tracePath, '', [Text.UTF8Encoding]::new($false))
        $env:CW_WRITEBACK_FAULT_TRACE = $tracePath
        $env:CW_PAGEFAULT_DELIVERY_TRACE = $tracePath
        if ($IncludeSelectedStoreRevalidationProbe -and $run.Name -eq 'active') {
            $selectedTracePath = Join-Path $ArtifactRoot 'selected-store-active-only.jsonl'
            [IO.File]::WriteAllText($selectedTracePath, '', [Text.UTF8Encoding]::new($false))
            $env:CW_SELECTED_STORE_TRACE = $selectedTracePath
        }
        if ($IncludeSelectedStoreCurrentPteProbe -and $run.Name -eq 'active') {
            $pteTracePath = Join-Path $ArtifactRoot 'selected-store-current-pte-active-only.jsonl'
            [IO.File]::WriteAllText($pteTracePath, '', [Text.UTF8Encoding]::new($false))
            $env:CW_SELECTED_STORE_PTE_TRACE = $pteTracePath
        }
        $suiteCounts[$run.Name] = [ordered]@{}
        $firstSuite = $true
        $runSuites = if ($SelectedStoreBackingOwnerOnly) { @($selectedBackingOwnerSuite) } else { @($suiteDefinitions) }
        if ($IncludeSelectedStoreRevalidationProbe -and $run.Name -eq 'active') { $runSuites += $selectedSuite }
        if ($IncludeSelectedStoreCurrentPteProbe -and $run.Name -eq 'active') { $runSuites += $selectedPteSuite }
        if (-not $SelectedStoreBackingOwnerOnly -and $IncludeSelectedStoreBackingOwnerProbe -and $run.Name -eq 'active') { $runSuites += $selectedBackingOwnerSuite }
        foreach ($suite in $runSuites) {
            $logPath = Join-Path $ArtifactRoot ($run.Name + '-' + $suite.Name + '.log')
            $trxName = $run.Name + '-' + $suite.Name + '.trx'
            $dotnetArgs = @('test', $run.Test.Project, '--configuration', 'Debug', '--filter', ("FullyQualifiedName~" + $suite.Filter), '-m:1', '-p:UseSharedCompilation=false')
            if (-not $firstSuite) { $dotnetArgs += @('--no-build', '--no-restore') }
            $dotnetArgs += @('--logger', 'console;verbosity=minimal', '--logger', ("trx;LogFileName=" + $trxName), '--results-directory', $ArtifactRoot)
            & dotnet @dotnetArgs *> $logPath
            $testExit = $LASTEXITCODE; Get-Content $logPath
            if ($testExit -ne 0) { throw "$($run.Name) $($suite.Name) suite failed: $testExit" }
            $trxPath = Join-Path $ArtifactRoot $trxName
            if (-not (Test-Path -LiteralPath $trxPath)) { throw "Missing TRX test count receipt: $trxPath" }
            [xml]$trx = Get-Content $trxPath -Raw
            $counters = $trx.TestRun.ResultSummary.Counters
            $passed = [int]$counters.passed; $failed = [int]$counters.failed; $total = [int]$counters.total
            if ($passed -ne $suite.Expected -or $failed -ne 0 -or $total -ne $suite.Expected) {
                throw "$($run.Name) $($suite.Name) count mismatch: passed=$passed failed=$failed total=$total expected=$($suite.Expected)"
            }
            $suiteCounts[$run.Name][$suite.Name] = [ordered]@{passed=$passed;failed=$failed;total=$total;logSha256=(Get-FileHash $logPath -Algorithm SHA256).Hash.ToLowerInvariant();trxSha256=(Get-FileHash $trxPath -Algorithm SHA256).Hash.ToLowerInvariant()}
            $firstSuite = $false
        }
        if (-not $SelectedStoreBackingOwnerOnly) {
            $rows = @(Get-Content $tracePath | ForEach-Object { $_ | ConvertFrom-Json } | Sort-Object scenario)
            $expectedRowCount = if ($IncludeScalarStorePublicationProbe) { 15 } else { 13 }
            if ($rows.Count -ne $expectedRowCount) { throw "$($run.Name) run emitted $($rows.Count) observations; expected $expectedRowCount for the selected probes." }
            $traces[$run.Name] = $rows
        }
    }
    if ($SelectedStoreBackingOwnerOnly) {
        $ownerSources = @(
            'HybridCPU_ISE/NonRTL/Processor/Memory/Processor.SelectedStoreBacking.cs',
            'HybridCPU_ISE/NonRTL/Processor/Memory/Processor.PagedSelectedStoreBacking.cs',
            'HybridCPU_ISE/NonRTL/Processor/Memory/Processor.Memory.cs',
            'HybridCPU_ISE/CloseToHSL/Core/Runtime/Memory/Translation/CpuPhysicalRangeLayout.cs',
            'HybridCPU_ISE/Properties/AssemblyInfo.cs',
            'HybridCPU_ISE.Tests/HybridCPU_ISE.Tests.csproj')
        $inputHashes = foreach ($relative in $ownerSources) {
            $source = Join-Path $activeTree ('HybridCPU ISE\' + $relative.Replace('/','\'))
            [ordered]@{path=$relative;sha256=(Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant()}
        }
        $ownerReceipt = [ordered]@{
            schema='circuitweave.selected-store-backing-owner-atomic-probe/v1'
            status='ACTIVE_SELECTED_STORE_OWNER_ATOMIC_ASSERTIONS_PASSED'
            modelHead=(git -C $iseRoot rev-parse HEAD).Trim()
            modelDirtyStatus=(git -C $iseRoot status --porcelain=v1 --untracked-files=all)
            exactCopiedActiveInputSha256=$inputHashes
            probeSourceSha256=(Get-FileHash (Join-Path $PSScriptRoot 'SelectedStoreBackingOwnerAtomicTests.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
            runnerSha256=(Get-FileHash $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
            suite=$suiteCounts.active.SelectedStoreBackingOwnerAtomic
            testLogSha256=(Get-FileHash (Join-Path $ArtifactRoot 'active-SelectedStoreBackingOwnerAtomic.log') -Algorithm SHA256).Hash.ToLowerInvariant()
            trxSha256=(Get-FileHash (Join-Path $ArtifactRoot 'active-SelectedStoreBackingOwnerAtomic.trx') -Algorithm SHA256).Hash.ToLowerInvariant()
            isolatedBuildRoot=$tempRoot
            publication='direct IPreparedSelectedStore owner commit seam only; excludes CPU tickets, retirement, physical RAM timing and RTL'
        }
        $ownerReceipt | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $ArtifactRoot 'selected-store-backing-owner-receipt.json') -Encoding utf8
        Write-Output (Get-Content (Join-Path $ArtifactRoot 'selected-store-backing-owner-receipt.json') -Raw)
        return
    }
    if ($IncludeSelectedStoreRevalidationProbe) {
        $selectedRows = @(Get-Content (Join-Path $ArtifactRoot 'selected-store-active-only.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
        if ($selectedRows.Count -ne 1 -or $selectedRows[0].scenario -cne 'selected-store-domain-revoked-before-writeback' -or $selectedRows[0].before -cne $selectedRows[0].after -or $selectedRows[0].retired -ne 0) { throw 'Selected-store active-only observation failed its independent assertions.' }
    }
    if ($IncludeSelectedStoreCurrentPteProbe) {
        $pteRows = @(Get-Content (Join-Path $ArtifactRoot 'selected-store-current-pte-active-only.jsonl') | ForEach-Object { $_ | ConvertFrom-Json } | Sort-Object scenario)
        if ($pteRows.Count -ne 3 -or $pteRows[0].scenario -cne 'selected-store-current-leaf-pte-missing-before-writeback' -or $pteRows[0].exception -cne 'CpuInstructionTranslationFaultException' -or $pteRows[0].faultReason -cne 'SecondStageViolation' -or $pteRows[1].scenario -cne 'selected-store-current-leaf-pte-remap-before-writeback' -or $pteRows[1].exception -cne 'CpuStoreTranslationRemappedException' -or $pteRows[2].scenario -cne 'selected-store-current-leaf-pte-unchanged-success' -or $pteRows[2].exception -ne $null) { throw 'Selected-store current-PTE active-only observations failed their independent assertions.' }
        foreach ($row in $pteRows[0..1]) { if ($row.retired -ne 0) { throw 'Rejected selected-store current-PTE probe retired.' }; if ($row.scenario -ceq 'selected-store-current-leaf-pte-missing-before-writeback' -and $row.before -cne $row.after) { throw 'Missing-PTE probe changed the original physical bytes.' }; if ($row.scenario -ceq 'selected-store-current-leaf-pte-remap-before-writeback' -and ($row.before -cne $row.originalAfter -or $row.before -cne $row.remappedAfter)) { throw 'Remapped-PTE probe changed original or replacement physical bytes.' } }
        $success = $pteRows[2]
        if ($success.before -cne '5A5A5A5A5A5A5A5A' -or $success.after -cne '8877665544332211' -or $success.retired -ne 1 -or $success.scalarRetired -ne 0 -or $success.nonScalarRetired -ne 1 -or $success.typedAttemptIdentity -ne $false -or $success.preparedAttemptTicket -ne $false) { throw 'Unchanged-PTE positive control failed byte-order or retire-count assertions.' }
    }
    $same = (ConvertTo-Json -InputObject $traces.active -Depth 10 -Compress) -ceq (ConvertTo-Json -InputObject $traces.golden -Depth 10 -Compress)
    if (-not $same) { throw 'Active and Golden writeback fault observations differ.' }
    $relativePaths = @(
        'HybridCPU_ISE.Tests/tests/WriteBackFaultOrderingTests.cs',
        'HybridCPU_ISE.Tests/tests/Phase09SingleLaneExecuteFaultTailTests.cs',
        'HybridCPU_ISE.Tests/tests/Phase09ExplicitPacketExecuteFaultTailTests.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.PipelineExecution.StageFlow.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/Core/CPU_Core.TestSupport.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/Stages/Issue/CPU_Core.Pipeline.cs',
        'HybridCPU_ISE/HybridCPU_ISE.csproj',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.PipelineExecution.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.Pipeline.Helpers.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/Faults/CPU_Core.PipelineExecution.Exceptions.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/Stages/Memory/CPU_Core.Pipeline.Stages.MemoryStage.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/Stages/Memory/CPU_Core.Pipeline.Stages.ScalarMemoryLaneState.cs',
        'HybridCPU_ISE/CloseToHSL/Core/State/CPU_Core.RuntimeState.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/Stages/WriteBack/CPU_Core.Pipeline.Stages.ScalarWriteBackLaneState.cs',
        'HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/WriteBackCleanup/CPU_Core.PipelineExecution.WriteBackCleanup.cs')
    $sourcePins = foreach ($relative in $relativePaths) {
        $activePath = Join-Path $activeTree (Join-Path 'HybridCPU ISE' $relative.Replace('/','\'))
        $goldenPath = Join-Path $goldenRoot ($relative.Replace('/','\'))
        if ($relative.StartsWith('HybridCPU_ISE.Tests/tests/', [StringComparison]::Ordinal)) {
            $activePath += '.source-before-trace'
            $goldenPath += '.source-before-trace'
        }
        elseif ($relative -eq 'HybridCPU_ISE/HybridCPU_ISE.csproj') {
            $activePath += '.source-before-harness'
            $goldenPath += '.source-before-harness'
        }
        $pinnedFile = @($goldenMaterialization.files | Where-Object { $_.path -ceq $relative })
        if ($pinnedFile.Count -ne 1) { throw "Pinned raw Golden blob missing/duplicated: $relative" }
        $blob = (git --git-dir=$GoldenGitDir rev-parse ($goldenCommit + ':' + $relative)).Trim()
        $materializedHash = (Get-FileHash $goldenPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($pinnedFile[0].blob -cne $blob -or $pinnedFile[0].sha256 -cne $materializedHash) { throw "Golden raw blob byte mismatch: $relative" }
        [ordered]@{path=$relative;activeSha256=(Get-FileHash $activePath -Algorithm SHA256).Hash.ToLowerInvariant();goldenGitBlob=$blob;goldenMaterializedSha256=$materializedHash;goldenRawBlobSha256=$pinnedFile[0].sha256}
    }
    $receipt = [ordered]@{
        schema='circuitweave.writeback-and-execute-fault-active-golden-runtime/v2';status='ACTIVE_GOLDEN_SELECTED_TESTS_AND_OBSERVATIONS_MATCH'
        activeModelCommit=$activeCommit;activeModelGitHead=(git -C $iseRoot rev-parse HEAD).Trim();goldenCommit=$goldenCommit
        isolatedBuildRoot=$tempRoot;scenarioCount=$traces.active.Count;tracesEqual=$same;observations=@($traces.active);suiteCounts=$suiteCounts
        activeTraceSha256=(Get-FileHash (Join-Path $ArtifactRoot 'active-trace.jsonl') -Algorithm SHA256).Hash.ToLowerInvariant()
        goldenTraceSha256=(Get-FileHash (Join-Path $ArtifactRoot 'golden-trace.jsonl') -Algorithm SHA256).Hash.ToLowerInvariant()
        originalActiveTestSourceSha256=$activeTest.OriginalHashes;originalGoldenTestSourceSha256=$goldenTest.OriginalHashes
        harnessBuildGraph=@{activeIseProjectOriginalSha256=$activeTest.IseProjectOriginalSha256;activeIseProjectWithHarnessReferencesSha256=$activeTest.IseProjectWithHarnessReferencesSha256;goldenIseProjectOriginalSha256=$goldenTest.IseProjectOriginalSha256;goldenIseProjectWithHarnessReferencesSha256=$goldenTest.IseProjectWithHarnessReferencesSha256;addedProjectReferences=$activeTest.HarnessCompilerReferences}
        instrumentedCopiedTestSourcesSha256=@($activeTest.TestFiles | ForEach-Object { [ordered]@{path=[IO.Path]::GetFileName($_);sha256=(Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant()} })
        sourcePins=@($sourcePins)
        pinnedGoldenMaterialization=@{method=$goldenMaterialization.materialization;fileCount=$goldenMaterialization.fileCount;manifestSha256=(Get-FileHash $goldenMaterializationManifest -Algorithm SHA256).Hash.ToLowerInvariant()}
        pageFaultDeliveryProbeSourceSha256=(Get-FileHash (Join-Path $PSScriptRoot 'PageFaultDeliveryRuntimeProbeTests.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
        retireCycleTraceProbeSourceSha256=(Get-FileHash (Join-Path $PSScriptRoot 'RetireCycleTraceTests.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
        scalarStorePublicationProbeIncluded=$IncludeScalarStorePublicationProbe.IsPresent
        scalarStorePublicationProbeSourceSha256=if ($IncludeScalarStorePublicationProbe) { (Get-FileHash (Join-Path $PSScriptRoot 'RetireStorePublicationTraceTests.cs') -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        selectedStoreRevalidationProbeIncluded=$IncludeSelectedStoreRevalidationProbe.IsPresent
        selectedStoreRevalidationProbeTraceSha256=if ($IncludeSelectedStoreRevalidationProbe) { (Get-FileHash (Join-Path $ArtifactRoot 'selected-store-active-only.jsonl') -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        selectedStoreCurrentPteProbeIncluded=$IncludeSelectedStoreCurrentPteProbe.IsPresent
        selectedStoreCurrentPteProbeTraceSha256=if ($IncludeSelectedStoreCurrentPteProbe) { (Get-FileHash (Join-Path $ArtifactRoot 'selected-store-current-pte-active-only.jsonl') -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        selectedStoreBackingOwnerProbeIncluded=$IncludeSelectedStoreBackingOwnerProbe.IsPresent
        selectedStoreBackingOwnerProbeSourceSha256=if ($IncludeSelectedStoreBackingOwnerProbe) { (Get-FileHash (Join-Path $PSScriptRoot 'SelectedStoreBackingOwnerAtomicTests.cs') -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        selectedStoreBackingOwnerProbeTrxSha256=if ($IncludeSelectedStoreBackingOwnerProbe) { (Get-FileHash (Join-Path $ArtifactRoot 'active-SelectedStoreBackingOwnerAtomic.trx') -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        scope=if ($IncludeScalarStorePublicationProbe) { 'Selected writeback-fault ordering plus legacy no-selected-layout deferred-Store success and invalid-range prevalidation rejection on active and exact pinned-Golden sources; also existing execute-fault, page-fault delivery and logical cycle/stall/replay/reset observations. The store probe uses TESTING helper TestRetireLegacyScalarStoreThroughWriteBack; it does not exercise canonical selected-store translation/layout revalidation, typed attempt identity, prepared backing, physical RAM timing, or RTL equivalence.' } else { 'Selected software writeback-fault ordering, execute-stage carriers, direct MEM fault delivery, one actual ExecutePipelineCycle memory-fault scenario, plus active-vs-pinned-Golden software-cycle state trace through mainline WriteBack with a test-staged ready lane, explicit stall, replay flush, and execution-start reset. Test-staged lane is not fetch/decode-created architectural work; no physical clock/reset or RTL equivalence.' }
        excludedExploratoryFixture='ManagedGuestRunner_MemoryReadFault_EscapesPublicApi was excluded because the handcrafted image returns ExecutionFault at the executable mapping boundary with retire sequence zero; it is not a fault-delivery observation.'
    }
    $receipt | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $ArtifactRoot 'comparison.json') -Encoding utf8
    Write-Output (Get-Content (Join-Path $ArtifactRoot 'comparison.json') -Raw)
}
finally { Remove-Item Env:CW_WRITEBACK_FAULT_TRACE -ErrorAction SilentlyContinue; Remove-Item Env:CW_PAGEFAULT_DELIVERY_TRACE -ErrorAction SilentlyContinue; Remove-Item Env:CW_SELECTED_STORE_TRACE -ErrorAction SilentlyContinue; Remove-Item Env:CW_SELECTED_STORE_PTE_TRACE -ErrorAction SilentlyContinue }

