param(
    [string] $GoldenGitDir = "$env:TEMP\cw-golden-6946ccab-20261114",
    [string] $ArtifactRoot = "C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\processor-20261114\vector-segment-runtime-rf106-v2"
)

$ErrorActionPreference = 'Stop'
$goldenCommit = '6946ccab9d888f7dae0a4d8e18480f436138acd9'
$activeCommit = '1bfe41a49cf11c80fa6c6aee87accb54b78b331f'
$iseRoot = 'C:\Users\Yuriy\Desktop\HybridCPU ISE'
$compilerRoot = 'C:\Users\Yuriy\Desktop\HybridCPU Compilers'
$tempRoot = Join-Path $env:TEMP ('vector-segment-rf106-' + [guid]::NewGuid().ToString('N'))
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
    $testPath = Join-Path $isePath 'HybridCPU_ISE.Tests\Architecture\Rf106VectorSegmentLoadMemoryCycleTests.cs'
    $source = [IO.File]::ReadAllText($testPath)
    if ($source.Contains('CW-RF106-TRACE-INJECTED')) { return }
    $anchor = '    private static Processor.CPU_Core CreateBoundCore()'
    if (-not $source.Contains($anchor)) { throw "Rf106 insertion anchor not found: $testPath" }
    $injected = @'
    // CW-RF106-TRACE-INJECTED: identical event trace in active and pinned-Golden snapshots.
    [Fact]
    public void EmitsNormalizedRuntimeTrace()
    {
        var events = new System.Collections.Generic.List<object>();
        WithMappedMemory((_, memory) =>
        {
            var explicitScalar = memory.CycleController.TryAcceptExplicitPacketScalarLoad(0, 0x100, 4);
            var vector = memory.CycleController.TryAcceptVectorSegmentLoad(0, 0x200, 16);
            var singleScalar = memory.CycleController.TryAcceptSingleLaneScalarLoad(0, 0x300, 8);
            void Admission(string name, MemoryAdmissionResult result) => events.Add(new
            {
                eventKind = "accept", requestClass = name, status = result.Status.ToString(),
                requestId = result.RequestId.Value, reason = result.Reason
            });
            Admission("explicit-packet-scalar", explicitScalar);
            Admission("vector-segment", vector);
            Admission("single-lane-scalar", singleScalar);
            void Edge(int number)
            {
                memory.AdvanceCycles(1);
                events.Add(new { eventKind = "edge", cycle = number });
            }
            void Take(string name, MemoryRequestId id)
            {
                bool ready = memory.CycleController.TryTakeCompletion(id, out MemoryCompletion? completion);
                byte[] payload = completion?.Data.ToArray() ?? Array.Empty<byte>();
                events.Add(new
                {
                    eventKind = "take", requestClass = name, ready,
                    requestId = id.Value, succeeded = completion?.Succeeded,
                    publishedCycle = completion?.PublishedCycle,
                    payloadLength = completion?.Data.Length,
                    payloadSha256 = payload.Length == 0 ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant()
                });
            }
            Edge(1); Take("explicit-packet-scalar", explicitScalar.RequestId);
            Edge(2); Take("explicit-packet-scalar", explicitScalar.RequestId); Take("vector-segment", vector.RequestId);
            Edge(3); Take("vector-segment", vector.RequestId); Take("single-lane-scalar", singleScalar.RequestId);
            Edge(4); Take("single-lane-scalar", singleScalar.RequestId);
        });
        WithMappedMemory((_, memory) =>
        {
            const string scenario = "capacity-cancel";
            var accepted = new System.Collections.Generic.List<MemoryRequestId>();
            for (int i = 0; i < MemoryCycleController.VectorSegmentLoadCapacity; i++)
            {
                MemoryAdmissionResult result = memory.CycleController.TryAcceptVectorSegmentLoad(0, 0x400UL + (ulong)(i * 16), 16);
                if (result.Status != MemoryAdmissionStatus.Accepted) throw new InvalidOperationException("Expected vector admission before capacity.");
                accepted.Add(result.RequestId);
                events.Add(new { scenario, eventKind = "accept", index = i, status = result.Status.ToString(), requestId = result.RequestId.Value });
            }
            MemoryAdmissionResult full = memory.CycleController.TryAcceptVectorSegmentLoad(0, 0x500, 16);
            events.Add(new { scenario, eventKind = "backpressure", status = full.Status.ToString(), requestId = full.RequestId.Value, outstanding = memory.CycleController.OutstandingVectorSegmentLoads });
            MemoryRequestId canceledId = accepted[0];
            bool canceled = memory.CycleController.TryCancel(canceledId);
            events.Add(new { scenario, eventKind = "cancel", requestId = canceledId.Value, canceled, outstanding = memory.CycleController.OutstandingVectorSegmentLoads });
            bool canceledAgain = memory.CycleController.TryCancel(canceledId);
            events.Add(new { scenario, eventKind = "cancel-repeat", requestId = canceledId.Value, canceled = canceledAgain, outstanding = memory.CycleController.OutstandingVectorSegmentLoads });
            MemoryAdmissionResult retry = memory.CycleController.TryAcceptVectorSegmentLoad(0, 0x600, 16);
            if (retry.Status != MemoryAdmissionStatus.Accepted) throw new InvalidOperationException("Canceled request did not release vector capacity.");
            accepted.Add(retry.RequestId);
            events.Add(new { scenario, eventKind = "retry", status = retry.Status.ToString(), requestId = retry.RequestId.Value, outstanding = memory.CycleController.OutstandingVectorSegmentLoads });
            for (int edge = 1; edge <= 9; edge++)
            {
                memory.AdvanceCycles(1);
                events.Add(new { scenario, eventKind = "edge", cycle = edge, outstanding = memory.CycleController.OutstandingVectorSegmentLoads });
            }
            foreach (MemoryRequestId id in new[] { canceledId }.Concat(accepted.Skip(1)))
            {
                bool ready = memory.CycleController.TryTakeCompletion(id, out MemoryCompletion? completion);
                events.Add(new { scenario, eventKind = "take", requestId = id.Value, ready, succeeded = completion?.Succeeded, publishedCycle = completion?.PublishedCycle, payloadLength = completion?.Data.Length });
            }
        });
        WithMappedMemory((_, memory) =>
        {
            const string scenario = "pipeline-flush";
            Processor.CPU_Core core = CreateBoundCore();
            VLIW_Instruction instruction = CreateInstruction(0xA600);
            LoadSegmentMicroOp load = new LoadSegmentMicroOp { Instruction = instruction };
            load.InitializeMetadata();
            core.TestRunExecuteStageWithDecodedInstruction(instruction, load, isVectorOp: true, isMemoryOp: true, pc: 0xA600);
            events.Add(new { scenario, eventKind = "admitted", ownsCompletion = load.OwnsPendingMemoryCompletion, outstanding = memory.CycleController.OutstandingVectorSegmentLoads });
            core.FlushPipeline();
            events.Add(new { scenario, eventKind = "flush", ownsCompletion = load.OwnsPendingMemoryCompletion, outstanding = memory.CycleController.OutstandingVectorSegmentLoads });
            memory.AdvanceCycles(2);
            events.Add(new { scenario, eventKind = "after-edges", ownsCompletion = load.OwnsPendingMemoryCompletion, outstanding = memory.CycleController.OutstandingVectorSegmentLoads });
        });
        string output = Environment.GetEnvironmentVariable("CW_RF106_TRACE_OUTPUT")
            ?? throw new InvalidOperationException("CW_RF106_TRACE_OUTPUT is required.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(events, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

'@
    [IO.File]::WriteAllText($testPath, $source.Replace($anchor, $injected + $anchor), [Text.UTF8Encoding]::new($false))
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
        $testSource = Join-Path $run.Ise 'HybridCPU_ISE.Tests\Architecture\Rf106VectorSegmentLoadMemoryCycleTests.cs'
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
        $env:CW_RF106_TRACE_OUTPUT = $trace
        $testFilter = 'FullyQualifiedName~EmitsNormalizedRuntimeTrace'
        $testLog = Join-Path $ArtifactRoot ($run.Name + '-test.log')
        & dotnet test $project --configuration Debug --filter $testFilter "-p:HybridCpuTestArtifactsRoot=$runRoot" --logger 'console;verbosity=minimal' *> $testLog
        $testExitCode = $LASTEXITCODE
        Get-Content -LiteralPath $testLog
        if ($testExitCode -ne 0) { throw "$($run.Name) Rf106 tests failed: $testExitCode" }
        if (-not (Test-Path $trace)) { throw "$($run.Name) tests passed without emitting the expected runtime trace." }
    }
    $activeTrace = Get-Content (Join-Path $ArtifactRoot 'active-trace.json') -Raw | ConvertFrom-Json
    $goldenTrace = Get-Content (Join-Path $ArtifactRoot 'golden-trace.json') -Raw | ConvertFrom-Json
    $activeJson = ConvertTo-Json -InputObject $activeTrace -Depth 50 -Compress
    $goldenJson = ConvertTo-Json -InputObject $goldenTrace -Depth 50 -Compress
    $same = $activeJson -ceq $goldenJson
    if ($activeTrace.Count -ne 46) { throw "Unexpected normalized Rf106 trace length: $($activeTrace.Count)" }
    $capacityEvents = @($activeTrace | Where-Object scenario -eq 'capacity-cancel')
    $flushEvents = @($activeTrace | Where-Object scenario -eq 'pipeline-flush')
    $canceledTake = @($capacityEvents | Where-Object { $_.eventKind -eq 'take' -and $_.requestId -eq 1 })
    $liveTakes = @($capacityEvents | Where-Object { $_.eventKind -eq 'take' -and $_.requestId -ne 1 })
    $flushAdmit = @($flushEvents | Where-Object eventKind -eq 'admitted')
    $flushTerminal = @($flushEvents | Where-Object eventKind -eq 'flush')
    $flushEdges = @($flushEvents | Where-Object eventKind -eq 'after-edges')
    if ($capacityEvents.Count -ne 30 -or $flushEvents.Count -ne 3 -or
        (@($capacityEvents | Where-Object eventKind -eq 'backpressure')[0]).status -ne 'Backpressured' -or
        (@($capacityEvents | Where-Object eventKind -eq 'backpressure')[0]).requestId -ne 0 -or
        (@($capacityEvents | Where-Object eventKind -eq 'cancel')[0]).canceled -ne $true -or
        (@($capacityEvents | Where-Object eventKind -eq 'cancel-repeat')[0]).canceled -ne $false -or
        (@($capacityEvents | Where-Object eventKind -eq 'retry')[0]).status -ne 'Accepted' -or
        (@($capacityEvents | Where-Object eventKind -eq 'retry')[0]).requestId -ne 9 -or
        $canceledTake.Count -ne 1 -or $canceledTake[0].ready -ne $false -or
        $liveTakes.Count -ne 8 -or @($liveTakes | Where-Object ready -ne $true).Count -ne 0 -or
        $flushAdmit.Count -ne 1 -or $flushAdmit[0].ownsCompletion -ne $true -or $flushAdmit[0].outstanding -ne 1 -or
        $flushTerminal.Count -ne 1 -or $flushTerminal[0].ownsCompletion -ne $false -or $flushTerminal[0].outstanding -ne 0 -or
        $flushEdges.Count -ne 1 -or $flushEdges[0].ownsCompletion -ne $false -or $flushEdges[0].outstanding -ne 0) {
        throw 'Independent Rf106 runtime trace oracle rejected capacity/cancel/flush observations.'
    }
    $controllerRelative = 'HybridCPU_ISE\CloseToHSL\Memory\Timing\MemoryCycleController.cs'
    $activeController = Join-Path $activeIse $controllerRelative
    $goldenController = Join-Path $goldenIse $controllerRelative
    $activeSourceSha = (Get-FileHash -LiteralPath $activeController -Algorithm SHA256).Hash.ToLowerInvariant()
    $goldenSourceSha = (Get-FileHash -LiteralPath $goldenController -Algorithm SHA256).Hash.ToLowerInvariant()
    $goldenControllerGitPath = 'HybridCPU_ISE/CloseToHSL/Memory/Timing/MemoryCycleController.cs'
    $goldenControllerBlob = (git --git-dir=$GoldenGitDir rev-parse ($goldenCommit + ':' + $goldenControllerGitPath)).Trim()
    $materializedGoldenBlob = (git hash-object $goldenController).Trim()
    if ($materializedGoldenBlob -cne $goldenControllerBlob) { throw 'Materialized Golden controller bytes are not the pinned Git blob.' }
    $activeTestAssembly = Join-Path $activeIse '.rf106-build\bin\Rf106RuntimeTrace\Debug\net11.0\HybridCPU_ISE.Tests.dll'
    $goldenTestAssembly = Join-Path $goldenIse '.rf106-build\bin\Rf106RuntimeTrace\Debug\net11.0\HybridCPU_ISE.Tests.dll'
    $activeHarnessProject = Join-Path $activeIse 'Rf106RuntimeTrace.csproj'
    $goldenHarnessProject = Join-Path $goldenIse 'Rf106RuntimeTrace.csproj'
    $activeTraceTestSource = Join-Path $activeIse 'HybridCPU_ISE.Tests\Architecture\Rf106VectorSegmentLoadMemoryCycleTests.cs'
    $goldenTraceTestSource = Join-Path $goldenIse 'HybridCPU_ISE.Tests\Architecture\Rf106VectorSegmentLoadMemoryCycleTests.cs'
    $comparison = [ordered]@{
        schema = 'circuitweave.vector-segment-runtime-differential/v1'
        activeModelCommit = $activeCommit
        activeWorkingTreeSnapshot = 'copied from current checkout; current source hashes recorded by adjacent harness receipt'
        goldenCommit = $goldenCommit
        goldenTreeSource = 'git archive from pinned commit object'
        activeModelGitHead = (git -C $iseRoot rev-parse HEAD).Trim()
        activeControllerSourceSha256 = $activeSourceSha
        goldenControllerSourceSha256 = $goldenSourceSha
        goldenControllerGitBlob = $goldenControllerBlob
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
        scenarioCounts = [ordered]@{ fifo = 13; capacityCancel = $capacityEvents.Count; pipelineFlush = $flushEvents.Count }
        tracesEqual = $same
        interpretation = 'Real Rf106 test setup, event sequence and completion observations; not RTL equivalence.'
        isolatedBuildRoot = $tempRoot
    }
    $comparison | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $ArtifactRoot 'comparison.json') -Encoding utf8
    if (-not $same) { throw 'Active and pinned-Golden normalized runtime event traces differ; inspect comparison artifacts.' }
    Write-Output (Get-Content (Join-Path $ArtifactRoot 'comparison.json') -Raw)
}
finally {
    Remove-Item Env:CW_RF106_TRACE_OUTPUT -ErrorAction SilentlyContinue
}
