param(
    [string]$GoldenGitDir = "$env:TEMP\cw-golden-6946ccab-20261114",
    [string]$ArtifactRoot = "C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\processor-20261114\memory-controller-active-golden-v2"
)

$ErrorActionPreference = 'Stop'
$goldenCommit = '6946ccab9d888f7dae0a4d8e18480f436138acd9'
$activeCommit = '1bfe41a49cf11c80fa6c6aee87accb54b78b331f'
$iseRoot = 'C:\Users\Yuriy\Desktop\HybridCPU ISE'
$compilerRoot = 'C:\Users\Yuriy\Desktop\HybridCPU Compilers'
$tempRoot = Join-Path $env:TEMP ('memory-cycle-active-golden-' + [guid]::NewGuid().ToString('N'))
$activeTree = Join-Path $tempRoot 'active'
$goldenTree = Join-Path $tempRoot 'golden'
$probePath = Join-Path $PSScriptRoot 'MemoryCycleControllerTraceTests.cs'
if (Test-Path -LiteralPath $ArtifactRoot) { throw "ArtifactRoot must be new: $ArtifactRoot" }
if ((git -C $iseRoot rev-parse HEAD).Trim() -cne $activeCommit) { throw 'Active ISE HEAD differs from the pinned active revision.' }
New-Item -ItemType Directory -Force -Path $activeTree, $ArtifactRoot | Out-Null

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

function Configure-ExternalBuildPaths([string]$root, [string]$iseRootPath) {
    $addition = @'
<PropertyGroup>
  <BaseIntermediateOutputPath>$(MSBuildThisFileDirectory).memory-cycle-build/obj/$(MSBuildProjectName)/</BaseIntermediateOutputPath>
  <MSBuildProjectExtensionsPath>$(BaseIntermediateOutputPath)</MSBuildProjectExtensionsPath>
  <BaseOutputPath>$(MSBuildThisFileDirectory).memory-cycle-build/bin/$(MSBuildProjectName)/</BaseOutputPath>
</PropertyGroup>
<PropertyGroup Condition="'$(HybridCpuTestArtifactsRoot)' != ''">
  <HybridCpuIseProjectReferenceProperties>DefineTestSupport=$(DefineTestSupport);EnableInternalTestHooks=true;HybridCpuTestArtifactsRoot=$(HybridCpuTestArtifactsRoot);BaseIntermediateOutputPath=$(MSBuildThisFileDirectory).memory-cycle-build/obj/HybridCPU_ISE/;MSBuildProjectExtensionsPath=$(MSBuildThisFileDirectory).memory-cycle-build/obj/HybridCPU_ISE/</HybridCpuIseProjectReferenceProperties>
</PropertyGroup>
'@
    $propsPath = Join-Path $root 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $propsPath)) { [IO.File]::WriteAllText($propsPath, '<Project></Project>', [Text.UTF8Encoding]::new($false)) }
    $props = [IO.File]::ReadAllText($propsPath); $close = $props.LastIndexOf('</Project>', [StringComparison]::OrdinalIgnoreCase)
    if ($close -lt 0) { throw "Invalid Directory.Build.props: $propsPath" }
    [IO.File]::WriteAllText($propsPath, $props.Insert($close, $addition), [Text.UTF8Encoding]::new($false))
    $workspacePropsPath = Join-Path (Split-Path $iseRootPath -Parent) 'Directory.Build.props'
    $workspaceProps = [IO.File]::ReadAllText($workspacePropsPath); $workspaceClose = $workspaceProps.LastIndexOf('</Project>', [StringComparison]::OrdinalIgnoreCase)
    if ($workspaceClose -lt 0) { throw "Invalid ISE workspace Directory.Build.props: $workspacePropsPath" }
    [IO.File]::WriteAllText($workspacePropsPath, $workspaceProps.Insert($workspaceClose, $addition), [Text.UTF8Encoding]::new($false))
    $localPropsPath = Join-Path $iseRootPath 'Directory.Build.props'
    $localProps = @'
<Project><PropertyGroup>
  <BaseIntermediateOutputPath>$(MSBuildThisFileDirectory)..\.memory-cycle-build/obj/HybridCPU_ISE/</BaseIntermediateOutputPath>
  <MSBuildProjectExtensionsPath>$(BaseIntermediateOutputPath)</MSBuildProjectExtensionsPath>
  <HybridCpuIseProjectReferenceProperties>DefineTestSupport=$(DefineTestSupport);HybridCpuTestArtifactsRoot=$(HybridCpuTestArtifactsRoot);BaseIntermediateOutputPath=$(MSBuildThisFileDirectory)..\.memory-cycle-build/obj/HybridCPU_ISE/;MSBuildProjectExtensionsPath=$(MSBuildThisFileDirectory)..\.memory-cycle-build/obj/HybridCPU_ISE/;OutputPath=$(HybridCpuTestArtifactsRoot)\ise-bin\</HybridCpuIseProjectReferenceProperties>
</PropertyGroup></Project>
'@
    [IO.File]::WriteAllText($localPropsPath, $localProps, [Text.UTF8Encoding]::new($false))
}

function Prepare-Test([string]$tree, [string]$name) {
    $ise = if (Test-Path -LiteralPath (Join-Path $tree 'HybridCPU_ISE')) { $tree } else { Join-Path $tree 'HybridCPU ISE' }
    $testRoot = Join-Path $ise 'HybridCPU_ISE.Tests'
    $testFile = Join-Path $testRoot 'tests\MemoryCycleControllerTraceTests.cs'
    Copy-Item -LiteralPath $probePath -Destination $testFile
    $globalUsings = Join-Path $testRoot 'GlobalUsings.cs'
    $iseProject = Join-Path $ise 'HybridCPU_ISE\HybridCPU_ISE.csproj'
    $project = Join-Path $ise 'MemoryCycleTraceRuntime.csproj'
    $compilerCandidates = @((Join-Path $tree 'HybridCPU Compilers\Compilers'),(Join-Path $tree 'Compilers'))
    $compilerProjectRoot = $compilerCandidates | Where-Object { Test-Path (Join-Path $_ 'HybridCPU_Platform.Contracts\HybridCPU.Platform.Contracts.csproj') } | Select-Object -First 1
    if (-not $compilerProjectRoot) { throw "Copied Platform/RuntimeKernel projects not found below $tree" }
    $iseProjectDirectory = Split-Path $iseProject -Parent
    $platformProject = [IO.Path]::GetRelativePath($iseProjectDirectory,(Join-Path $compilerProjectRoot 'HybridCPU_Platform.Contracts\HybridCPU.Platform.Contracts.csproj'))
    $runtimeKernelProject = [IO.Path]::GetRelativePath($iseProjectDirectory,(Join-Path $compilerProjectRoot 'HybridCPU_RuntimeKernel\HybridCPU.RuntimeKernel.csproj'))
    [IO.File]::WriteAllBytes($iseProject + '.source-before-harness', [IO.File]::ReadAllBytes($iseProject))
    $iseText = [IO.File]::ReadAllText($iseProject); $itemClose = $iseText.IndexOf('</ItemGroup>',[StringComparison]::Ordinal)
    if ($itemClose -lt 0) { throw "Could not locate ISE project-reference group: $iseProject" }
    $references = "`r`n        <ProjectReference Include=`"$platformProject`" GlobalPropertiesToRemove=`"DefineTestSupport;BuildRuntimeDiagnosticConsole;HybridCpuIseProjectReferenceProperties`" />`r`n        <ProjectReference Include=`"$runtimeKernelProject`" GlobalPropertiesToRemove=`"DefineTestSupport;BuildRuntimeDiagnosticConsole;HybridCpuIseProjectReferenceProperties`" />`r`n    "
    [IO.File]::WriteAllText($iseProject,$iseText.Insert($itemClose,$references),[Text.UTF8Encoding]::new($false))
    $runRoot = Join-Path $ArtifactRoot ($name + '-test-artifacts'); New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
    $objPath = Join-Path $ise '.memory-cycle-build\obj\HybridCPU_ISE'
    $escapedTest = [Security.SecurityElement]::Escape($testFile)
    $escapedGlobal = [Security.SecurityElement]::Escape($globalUsings)
    $escapedIse = [Security.SecurityElement]::Escape($iseProject)
    $escapedRunRoot = [Security.SecurityElement]::Escape($runRoot)
    $escapedObj = [Security.SecurityElement]::Escape($objPath)
    $projectText = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net11.0</TargetFramework><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject><EnableDefaultCompileItems>false</EnableDefaultCompileItems><AssemblyName>HybridCPU_ISE.Tests</AssemblyName><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.12.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="4.0.1" PrivateAssets="all" />
    <Using Include="Xunit" />
    <Compile Include="$escapedTest" /><Compile Include="$escapedGlobal" />
    <ProjectReference Include="$escapedIse"><AdditionalProperties>DefineTestSupport=true;EnableInternalTestHooks=true;HybridCpuTestArtifactsRoot=$escapedRunRoot;BaseIntermediateOutputPath=$escapedObj\;MSBuildProjectExtensionsPath=$escapedObj\;OutputPath=$escapedRunRoot\ise-bin\</AdditionalProperties></ProjectReference>
  </ItemGroup>
</Project>
"@
    [IO.File]::WriteAllText($project,$projectText,[Text.UTF8Encoding]::new($false))
    return @{Project=$project;TestFile=$testFile;RunRoot=$runRoot;TestSourceSha256=(Get-FileHash $probePath -Algorithm SHA256).Hash.ToLowerInvariant()}
}

try {
    Copy-ActiveInputs $activeTree
    $paths = @('HybridCPU_ISE','HybridCPU_ISE.Tests','HybridCPU_ExternalRuntime.Contracts','Compilers','corpus','Directory.Build.props','global.json')
    $materializationManifest = Join-Path $ArtifactRoot 'golden-raw-blob-materialization.json'
    & python (Join-Path $PSScriptRoot 'MaterializePinnedGoldenBlobs.py') --git-dir $GoldenGitDir --commit $goldenCommit --output-root $goldenTree --manifest $materializationManifest --paths $paths
    if ($LASTEXITCODE -ne 0) { throw 'Pinned Golden raw-blob materialization failed.' }
    $rawManifest = Get-Content -LiteralPath $materializationManifest -Raw | ConvertFrom-Json
    if ($rawManifest.schema -ne 'circuitweave.pinned-git-raw-blob-materialization/v1' -or $rawManifest.commit -cne $goldenCommit -or $rawManifest.fileCount -ne 3091) { throw 'Pinned Golden raw materialization receipt mismatch.' }
    foreach ($relative in @('Compilers','corpus')) { Copy-SourceTree (Join-Path $compilerRoot $relative) (Join-Path (Join-Path $goldenTree 'HybridCPU Compilers') $relative) }
    $activeTest = Prepare-Test $activeTree 'active'
    $goldenTest = Prepare-Test $goldenTree 'golden'
    Configure-ExternalBuildPaths $activeTree (Join-Path $activeTree 'HybridCPU ISE\HybridCPU_ISE')
    Configure-ExternalBuildPaths $goldenTree (Join-Path $goldenTree 'HybridCPU_ISE')
    $rows = @{}
    foreach ($run in @(@{Name='active';Test=$activeTest},@{Name='golden';Test=$goldenTest})) {
        $trace = Join-Path $ArtifactRoot ($run.Name + '-trace.jsonl')
        [IO.File]::WriteAllText($trace,'',[Text.UTF8Encoding]::new($false)); $env:CW_MEMORY_CYCLE_TRACE = $trace
        $storeAdmissionTrace = Join-Path $ArtifactRoot ($run.Name + '-store-admission-trace.jsonl')
        [IO.File]::WriteAllText($storeAdmissionTrace,'',[Text.UTF8Encoding]::new($false)); $env:CW_MEMORY_STORE_ADMISSION_TRACE = $storeAdmissionTrace
        $log = Join-Path $ArtifactRoot ($run.Name + '-test.log'); $trx = $run.Name + '.trx'
        & dotnet test $run.Test.Project --configuration Debug --filter 'FullyQualifiedName~MemoryCycleControllerTraceTests' -m:1 -p:UseSharedCompilation=false --logger 'console;verbosity=minimal' --logger ("trx;LogFileName=" + $trx) --results-directory $ArtifactRoot *> $log
        if ($LASTEXITCODE -ne 0) { Get-Content $log; throw "$($run.Name) memory controller test failed." }
        [xml]$trxXml = Get-Content (Join-Path $ArtifactRoot $trx) -Raw
        $count = $trxXml.TestRun.ResultSummary.Counters
        if ([int]$count.passed -ne 2 -or [int]$count.failed -ne 0 -or [int]$count.total -ne 2) { throw "$($run.Name) expected exactly two passing trace tests." }
        $observed = @(Get-Content $trace | ForEach-Object { $_ | ConvertFrom-Json })
        if ($observed.Count -ne 6) { throw "$($run.Name) expected six memory-controller observations." }
        $storeObserved = @(Get-Content $storeAdmissionTrace | ForEach-Object { $_ | ConvertFrom-Json })
        if ($storeObserved.Count -ne 5) { throw "$($run.Name) expected five store-admission observations." }
        $rows[$run.Name] = @{Trace=$trace;Rows=$observed;StoreTrace=$storeAdmissionTrace;StoreRows=$storeObserved;Log=$log;Trx=(Join-Path $ArtifactRoot $trx);Test=$run.Test}
    }
    $activeBytes = [IO.File]::ReadAllBytes($rows.active.Trace); $goldenBytes = [IO.File]::ReadAllBytes($rows.golden.Trace)
    $equal = [Convert]::ToBase64String($activeBytes) -ceq [Convert]::ToBase64String($goldenBytes)
    if (-not $equal) { throw 'Active and pinned Golden memory-controller traces differ.' }
    $activeStoreBytes = [IO.File]::ReadAllBytes($rows.active.StoreTrace); $goldenStoreBytes = [IO.File]::ReadAllBytes($rows.golden.StoreTrace)
    $storeEqual = [Convert]::ToBase64String($activeStoreBytes) -ceq [Convert]::ToBase64String($goldenStoreBytes)
    if (-not $storeEqual) { throw 'Active and pinned Golden scalar-store admission traces differ.' }
    $sourcePins = @()
    foreach ($relative in @('HybridCPU_ISE/CloseToHSL/Memory/Timing/MemoryCycleController.cs','HybridCPU_ISE/CloseToHSL/Memory/Subsystem/PhysicalMemoryBankBinding.cs','HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.PipelineExecution.StageFlow.cs','HybridCPU_ISE/HybridCPU_ISE.csproj')) {
        $file = @($rawManifest.files | Where-Object { $_.path -ceq $relative })
        if ($file.Count -ne 1) { throw "Pinned Golden source pin absent/duplicate: $relative" }
        $activePath = Join-Path $activeTree (Join-Path 'HybridCPU ISE' $relative.Replace('/','\'))
        $goldenPath = Join-Path $goldenTree $relative.Replace('/','\')
        if ($relative -eq 'HybridCPU_ISE/HybridCPU_ISE.csproj') { $activePath += '.source-before-harness'; $goldenPath += '.source-before-harness' }
        $activeHash = (Get-FileHash $activePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $goldenHash = (Get-FileHash $goldenPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($file[0].sha256 -cne $goldenHash) { throw "Golden source differs from raw blob manifest: $relative" }
        $sourcePins += [ordered]@{path=$relative;activeSha256=$activeHash;goldenGitBlob=$file[0].blob;goldenRawBlobSha256=$file[0].sha256}
    }
    $receipt = [ordered]@{
        schema='circuitweave.memory-cycle-controller-active-golden-runtime/v1';status='ACTIVE_GOLDEN_MEMORY_CONTROLLER_TRACE_MATCHED';
        activeModelHead=(git -C $iseRoot rev-parse HEAD).Trim();goldenCommit=$goldenCommit;rawBlobCount=$rawManifest.fileCount;
        rawBlobManifestSha256=(Get-FileHash $materializationManifest -Algorithm SHA256).Hash.ToLowerInvariant();
        activeTraceSha256=(Get-FileHash $rows.active.Trace -Algorithm SHA256).Hash.ToLowerInvariant();goldenTraceSha256=(Get-FileHash $rows.golden.Trace -Algorithm SHA256).Hash.ToLowerInvariant();tracesEqual=$equal;
        activeTests=@{passed=2;failed=0;total=2;logSha256=(Get-FileHash $rows.active.Log -Algorithm SHA256).Hash.ToLowerInvariant();trxSha256=(Get-FileHash $rows.active.Trx -Algorithm SHA256).Hash.ToLowerInvariant()};
        goldenTests=@{passed=2;failed=0;total=2;logSha256=(Get-FileHash $rows.golden.Log -Algorithm SHA256).Hash.ToLowerInvariant();trxSha256=(Get-FileHash $rows.golden.Trx -Algorithm SHA256).Hash.ToLowerInvariant()};
        observations=$rows.active.Rows;storeAdmission=@{tracesEqual=$storeEqual;activeTraceSha256=(Get-FileHash $rows.active.StoreTrace -Algorithm SHA256).Hash.ToLowerInvariant();goldenTraceSha256=(Get-FileHash $rows.golden.StoreTrace -Algorithm SHA256).Hash.ToLowerInvariant();observations=$rows.active.StoreRows;scope='single-lane scalar-store admission capacity/backpressure/request-id omission/cancellation release only; no store effect or RAM timing'};sourcePins=$sourcePins;probeTestSha256=(Get-FileHash $probePath -Algorithm SHA256).Hash.ToLowerInvariant();
        scope='Actual software MemoryCycleController load arbitration/publication/take/cancellation and scalar-store admission/capacity/backpressure/cancellation, compared on current dirty model bytes and exact pinned Golden raw blobs.';
        limitations=@('controller software cycle API, not physical clock/reset','no RAM port/timing/collision model','no pipeline stall/replay coupling','no HardwareBridge RTL simulation or HWIR state equivalence')
    }
    $receipt | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $ArtifactRoot 'comparison.json') -Encoding utf8
    Get-Content (Join-Path $ArtifactRoot 'comparison.json') -Raw
}
finally { Remove-Item Env:CW_MEMORY_CYCLE_TRACE -ErrorAction SilentlyContinue; Remove-Item Env:CW_MEMORY_STORE_ADMISSION_TRACE -ErrorAction SilentlyContinue }
