[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SourceRoot,
    [string] $RuntimePackDirectory = "$env:USERPROFILE\.nuget\packages\microsoft.netcore.app.runtime.win-x64\10.0.8\runtimes\win-x64\lib\net10.0",
    [string] $TestAssembly,
    [switch] $ApplyPatch
)

$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourceRoot)
$nativeAotDirectory = $PSScriptRoot
$repository = [IO.Path]::GetFullPath((Join-Path $nativeAotDirectory '..\..'))
$patch = Join-Path $nativeAotDirectory 'Patches\0001-hybridcpu-phase23-single-method-seam.patch'
$expectedPatch = 'e8c8169badfd8b70541c7ee733b384fdb41285b0652d7a6c94d427647d93e1b9'
$actualPatch = (Get-FileHash -Algorithm SHA256 -LiteralPath $patch).Hash.ToLowerInvariant()
if ($actualPatch -ne $expectedPatch) { throw "HCNAOT1002: patch drift: expected $expectedPatch, got $actualPatch" }

$cleanHashes = [ordered]@{
    'src/coreclr/tools/Common/TypeSystem/Common/TargetArchitecture.cs' = '6787e1f59f5e27d773f5f1564e00deded72ead529a80be3a05bbb22334474904'
    'src/coreclr/tools/Common/TypeSystem/Common/TargetDetails.cs' = 'aa81c37ea6e0d288ec58a3582a214bb51d3717183d94abcaa3b1811892ce2f66'
    'src/coreclr/tools/Common/CommandLineHelpers.cs' = '1e76a64cf06b20ed12917d4e63009860bf4948f26a176b8a8449a7065683d7a4'
    'src/coreclr/tools/aot/ILCompiler/ILCompilerRootCommand.cs' = '86cd3e57e9ddb12c6db11efd3c61c77c6b28f2fe2be01040d5db4a8ad1f08d1c'
    'src/coreclr/tools/aot/ILCompiler/Program.cs' = 'fdd1e2c6cb47d67b7f920a449d155434f8ad07a5779a574e9fcb1f52a5db37b6'
    'src/coreclr/tools/aot/ILCompiler.RyuJit/Compiler/DependencyAnalysis/MethodCodeNode.cs' = '60ff0edfb63bf5c350c78aeda7df1264938c4df0fd0b6f8d6622dbf1b6b5febe'
    'src/coreclr/tools/aot/ILCompiler.Compiler/Compiler/Compilation.cs' = '202c109e628ce502a2f3212af8fa4c1e379bc9e42ce8f5947365a477a937131a'
    'src/coreclr/tools/aot/ILCompiler.Compiler/Compiler/DependencyAnalysis/NodeFactory.cs' = '264d411fffed2c5aa9742f3fea7dd6b905c225baa2781df20f584b28f8e2b0c7'
    'src/coreclr/tools/Common/Compiler/DependencyAnalysis/ObjectDataBuilder.cs' = 'fd8c0f1ab1e6f2166253143e8f9e96481cc52a8cf929fab1b3e651d3fc771a25'
    'src/coreclr/tools/Common/Compiler/DependencyAnalysis/Relocation.cs' = 'a0758d2aa028835da4017a46320b9f3d269ba7b9e6b14bb1fc8b838d0055bdbf'
    'src/coreclr/tools/Common/Compiler/DependencyAnalysis/ObjectNodeSection.cs' = 'd4d71b4676b5d632ea7c7a31433e654a1e998e14a073f4ce9bac444bec1a4cd2'
    'src/coreclr/tools/aot/ILCompiler.Compiler/Compiler/ObjectWriter/ObjectWriter.cs' = 'fabeb9083c4c20e254d29eb833a5a751ba55b2d1bef97bd05f7f55c4ee8de18f'
}
foreach ($entry in $cleanHashes.GetEnumerator()) {
    $path = Join-Path $source $entry.Key
    if (!(Test-Path -LiteralPath $path)) { throw "HCNAOT1001: missing pinned source $($entry.Key)" }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
    if ($hash -ne $entry.Value) { throw "HCNAOT1001: source drift in $($entry.Key)" }
}

$patchTargets = @(
    'src/coreclr/tools/aot/ILCompiler.RyuJit/Compiler/RyuJitCompilationBuilder.cs',
    'src/coreclr/tools/aot/ILCompiler.RyuJit/Compiler/RyuJitCompilation.cs'
)
$cleanPatchTargets = @{
    $patchTargets[0] = '47b031948f7c8280ad91284fa539cb90e2a02264c9431408eb29157650493c0d'
    $patchTargets[1] = '65b6524eb722020dbd155342adab989133db1b63d6eb44cb720672d182beebb7'
}
$isClean = $true
foreach ($relative in $patchTargets) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $source $relative)).Hash.ToLowerInvariant()
    if ($hash -ne $cleanPatchTargets[$relative]) { $isClean = $false }
}
if ($isClean) {
    if (!$ApplyPatch) { throw 'HCNAOT1003: clean pinned source requires -ApplyPatch' }
    & git -C $source apply --check --unidiff-zero --ignore-space-change --ignore-whitespace $patch
    if ($LASTEXITCODE -ne 0) { throw 'HCNAOT1003: maintained patch does not apply to pinned source' }
    & git -C $source apply --unidiff-zero --ignore-space-change --ignore-whitespace $patch
    if ($LASTEXITCODE -ne 0) { throw 'HCNAOT1003: maintained patch application failed' }
} else {
    & git -C $source apply --check --reverse --unidiff-zero --ignore-space-change --ignore-whitespace $patch
    if ($LASTEXITCODE -ne 0) { throw 'HCNAOT1003: source is neither the clean pin nor the exact patched state' }
}

$patchedRyuJit = Get-Content -Raw -LiteralPath (Join-Path $source $patchTargets[1])
if ($patchedRyuJit -notmatch 'StandardOutput\.ReadToEndAsync\(\)' -or
    $patchedRyuJit -notmatch 'StandardError\.ReadToEndAsync\(\)' -or
    $patchedRyuJit -match 'StandardOutput\.ReadToEnd\(\)' -or
    $patchedRyuJit -match 'StandardError\.ReadToEnd\(\)') {
    throw 'HCNAOT1004: patched seam must drain stdout and stderr concurrently.'
}

$dotnet = Join-Path $source '.dotnet\dotnet.exe'
$ilc = Join-Path $source 'artifacts\bin\coreclr\windows.x64.Release\ilc\ilc.dll'
$adapterProject = Join-Path $nativeAotDirectory 'HybridCPU.Compiler.NativeAot.Adapter.csproj'
& dotnet build $adapterProject -c Release --no-restore /p:UseSharedCompilation=false /m:1
if ($LASTEXITCODE -ne 0) { throw 'HCNAOT2002: adapter build failed' }
$adapter = Join-Path $nativeAotDirectory 'bin\Release\net10.0\HybridCPU.Compiler.NativeAot.Adapter.dll'
Push-Location $source
try {
    & $dotnet build (Join-Path $source 'src\coreclr\tools\aot\ILCompiler\ILCompiler.csproj') -c Release --no-restore /p:UseSharedCompilation=false /m:1
    if ($LASTEXITCODE -ne 0) { throw 'HCNAOT2003: patched ILCompiler build failed' }
} finally {
    Pop-Location
}

if ([string]::IsNullOrWhiteSpace($TestAssembly)) {
    $testProject = Join-Path $repository 'HybridCPU_ISE.Tests\HybridCPU_ISE.Tests.csproj'
    & dotnet build $testProject -c Debug --no-restore /p:UseSharedCompilation=false /m:1
    if ($LASTEXITCODE -ne 0) { throw 'HCNAOT2004: fixture build failed' }
    $TestAssembly = Join-Path $repository 'HybridCPU_ISE.Tests\bin\Debug\net10.0\HybridCPU_ISE.Tests.dll'
}
$TestAssembly = [IO.Path]::GetFullPath($TestAssembly)
if (!(Test-Path -LiteralPath $RuntimePackDirectory)) { throw "HCNAOT0004: runtime reference directory missing: $RuntimePackDirectory" }

$outputs = @(
    (Join-Path ([IO.Path]::GetTempPath()) "hybridcpu-phase23-$([Guid]::NewGuid().ToString('N')).bin"),
    (Join-Path ([IO.Path]::GetTempPath()) "hybridcpu-phase23-$([Guid]::NewGuid().ToString('N')).bin")
)
try {
    foreach ($output in $outputs) {
        $arguments = [Collections.Generic.List[string]]::new()
        $arguments.Add($ilc)
        foreach ($reference in Get-ChildItem -LiteralPath $RuntimePackDirectory -Filter '*.dll') {
            $arguments.Add('--reference'); $arguments.Add($reference.FullName)
        }
        foreach ($reference in Get-ChildItem -LiteralPath (Split-Path $TestAssembly) -Filter '*.dll') {
            $arguments.Add('--reference'); $arguments.Add($reference.FullName)
        }
        foreach ($argument in @(
            '--out', $output, '--noscan',
            '--singlemethodtypename', 'HybridCPU_ISE.Tests.CompilerTests.RestrictedCilCSharpFixtures, HybridCPU_ISE.Tests',
            '--singlemethodname', 'Add',
            '--codegenopt', "HybridCpuDotNetHost=$dotnet",
            '--codegenopt', "HybridCpuAdapter=$adapter",
            '--codegenopt', "HybridCpuAssembly=$TestAssembly",
            '--codegenopt', 'HybridCpuType=HybridCPU_ISE.Tests.CompilerTests.RestrictedCilCSharpFixtures',
            '--codegenopt', 'HybridCpuMethod=Add',
            '--codegenopt', 'HybridCpuSourceCommit=94ea82652cdd4e0f8046b5bd5becbd11461482ca',
            '--codegenopt', "HybridCpuPatchDigest=$expectedPatch",
            $TestAssembly)) { $arguments.Add($argument) }
        & $dotnet $arguments
        if ($LASTEXITCODE -ne 0) { throw "HCNAOT2005: ILCompiler graph seam failed with exit $LASTEXITCODE" }
        if ((Get-Item -LiteralPath $output).Length -eq 0 -or (Get-Item -LiteralPath $output).Length % 32 -ne 0) {
            throw 'HCNAOT2006: output is empty or not bundle aligned'
        }
    }
    $first = (Get-FileHash -Algorithm SHA256 -LiteralPath $outputs[0]).Hash.ToLowerInvariant()
    $second = (Get-FileHash -Algorithm SHA256 -LiteralPath $outputs[1]).Hash.ToLowerInvariant()
    if ($first -ne $second) { throw "HCNAOT2007: nondeterministic output: $first versus $second" }
    [pscustomobject]@{
        schema = 'hybridcpu.nativeaot-phase23-verification/v1'
        sourceCommit = '94ea82652cdd4e0f8046b5bd5becbd11461482ca'
        patchSha256 = $expectedPatch
        outputSha256 = $first
        outputBytes = (Get-Item -LiteralPath $outputs[0]).Length
        dependencyGraph = 'SingleMethodRootProvider -> ComputeMarkedNodes -> MethodCodeNode.SetCode'
        objectWriterInvoked = $false
        deterministicRuns = 2
    } | ConvertTo-Json
} finally {
    foreach ($output in $outputs) {
        Remove-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath ($output + '.json') -Force -ErrorAction SilentlyContinue
    }
}
