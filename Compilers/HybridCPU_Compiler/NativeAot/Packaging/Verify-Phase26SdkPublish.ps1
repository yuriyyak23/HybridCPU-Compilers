[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SourceRoot,
    [Parameter(Mandatory = $true)]
    [string] $RuntimeReferenceDirectory,
    [string] $DotNetHostPath,
    [switch] $ApplyPublishPatch
)

$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourceRoot)
$references = [IO.Path]::GetFullPath($RuntimeReferenceDirectory)
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$adapterRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$patch1 = Join-Path $adapterRoot 'Patches\0001-hybridcpu-phase23-single-method-seam.patch'
$patch2 = Join-Path $adapterRoot 'Patches\0002-hybridcpu-phase26-publish-image.patch'
$expectedPatch1 = 'e8c8169badfd8b70541c7ee733b384fdb41285b0652d7a6c94d427647d93e1b9'
$expectedPatch2 = '1e0a458fef0d35b58df9c6eb851d26ddf6c7cad577c6d9510c14376c90c26644'
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $patch1).Hash.ToLowerInvariant() -ne $expectedPatch1) { throw 'HCPUB1301: Phase 23 patch drift.' }
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $patch2).Hash.ToLowerInvariant() -ne $expectedPatch2) { throw 'HCPUB1302: Phase 26 patch drift.' }

& git -C $source apply --check --reverse $patch2
if ($LASTEXITCODE -ne 0) {
    if (!$ApplyPublishPatch) { throw 'HCPUB1303: exact Phase 26 patch is not applied.' }
    & git -C $source apply --check $patch2
    if ($LASTEXITCODE -ne 0) { throw 'HCPUB1303: Phase 26 patch neither applies nor reverses exactly.' }
    & git -C $source apply $patch2
    if ($LASTEXITCODE -ne 0) { throw 'HCPUB1303: Phase 26 patch application failed.' }
}

$verificationRoot = Join-Path ([IO.Path]::GetTempPath()) "hybridcpu-phase26-$([Guid]::NewGuid().ToString('N'))"
$sourceCheck = Join-Path $verificationRoot 'source-check'
$ryuRelative = 'src\coreclr\tools\aot\ILCompiler.RyuJit\Compiler\RyuJitCompilation.cs'
$builderRelative = 'src\coreclr\tools\aot\ILCompiler.RyuJit\Compiler\RyuJitCompilationBuilder.cs'
New-Item -ItemType Directory -Path (Split-Path (Join-Path $sourceCheck $ryuRelative)), (Split-Path (Join-Path $sourceCheck $builderRelative)) -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $source $ryuRelative) -Destination (Join-Path $sourceCheck $ryuRelative)
Copy-Item -LiteralPath (Join-Path $source $builderRelative) -Destination (Join-Path $sourceCheck $builderRelative)
& git -C $sourceCheck apply --reverse $patch2
if ($LASTEXITCODE -ne 0) { throw 'HCPUB1304: Phase 26 patched source state is not exact.' }
& git -C $sourceCheck apply --reverse --unidiff-zero --ignore-space-change --ignore-whitespace $patch1
if ($LASTEXITCODE -ne 0) { throw 'HCPUB1305: Phase 23 base patch state is not exact.' }
$cleanRyuText = [IO.File]::ReadAllText((Join-Path $sourceCheck $ryuRelative)).Replace("`r`n", "`n")
$cleanBuilderText = [IO.File]::ReadAllText((Join-Path $sourceCheck $builderRelative)).Replace("`r`n", "`n")
$cleanRyu = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($cleanRyuText))).ToLowerInvariant()
$cleanBuilder = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($cleanBuilderText))).ToLowerInvariant()
if ($cleanRyu -ne '65b6524eb722020dbd155342adab989133db1b63d6eb44cb720672d182beebb7' -or
    $cleanBuilder -ne '47b031948f7c8280ad91284fa539cb90e2a02264c9431408eb29157650493c0d') {
    throw 'HCPUB1306: reconstructed clean source hashes do not match the pinned baseline.'
}

$patchedRyuText = Get-Content -Raw -LiteralPath (Join-Path $source $ryuRelative)
if ($patchedRyuText -notmatch 'StandardOutput\.ReadToEndAsync\(\)' -or
    $patchedRyuText -notmatch 'StandardError\.ReadToEndAsync\(\)' -or
    $patchedRyuText -notmatch '_hybridCpuSeam\.OutputKind != "image" && code\.Length % 32 != 0' -or
    $patchedRyuText -match 'code\.Length == 0 \|\| code\.Length % 32 != 0') {
    throw 'HCPUB1306: patched publish seam does not preserve concurrent pipe draining and image-specific validation.'
}

if ([string]::IsNullOrWhiteSpace($DotNetHostPath)) { $DotNetHostPath = Join-Path $source '.dotnet\dotnet.exe' }
$dotnetHost = [IO.Path]::GetFullPath($DotNetHostPath)
$ilCompilerDirectory = Join-Path $source 'artifacts\bin\coreclr\windows.x64.Release\ilc'
$ilCompiler = Join-Path $ilCompilerDirectory 'ilc.dll'
if (!(Test-Path -LiteralPath $dotnetHost) -or !(Test-Path -LiteralPath $references)) { throw 'HCPUB1307: pinned host or runtime references are missing.' }
Push-Location $source
try {
    & $dotnetHost build (Join-Path $source 'src\coreclr\tools\aot\ILCompiler\ILCompiler.csproj') -c Release --no-restore /p:UseSharedCompilation=false /m:1
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $ilCompiler)) { throw 'HCPUB1308: patched ILCompiler build failed.' }
} finally {
    Pop-Location
}

$pack1 = Join-Path $verificationRoot 'pack-1'
$pack2Root = Join-Path $verificationRoot 'pack-2'
& (Join-Path $PSScriptRoot 'Install-HybridCpuSdkPack.ps1') -Destination $pack1 -IlCompilerDirectory $ilCompilerDirectory -RuntimeReferenceDirectory $references | Out-Null
& (Join-Path $PSScriptRoot 'Install-HybridCpuSdkPack.ps1') -Destination $pack2Root -IlCompilerDirectory $ilCompilerDirectory -RuntimeReferenceDirectory $references | Out-Null
$packManifest1 = Get-Content -Raw -LiteralPath (Join-Path $pack1 'hybridcpu.pack-files.json')
$packManifest2 = Get-Content -Raw -LiteralPath (Join-Path $pack2Root 'hybridcpu.pack-files.json')
if ($packManifest1 -ne $packManifest2) { throw 'HCPUB1309: independently installed pack manifests differ.' }
if ($packManifest1 -match '(?i)llvm') { throw 'HCPUB1310: installed native publish pack contains LLVM contamination.' }

$sample = Join-Path $adapterRoot 'Samples\Phase26RestrictedPublish\Phase26RestrictedPublish.csproj'
& dotnet restore $sample -r hybridcpu --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'HCPUB1311: sample restore failed.' }
$results = @()
foreach ($run in @(@{ Pack = $pack1; Name = 'run-1' }, @{ Pack = $pack2Root; Name = 'run-2' })) {
    $publishDirectory = Join-Path $verificationRoot $run.Name
    $packRoot = $run.Pack + [IO.Path]::DirectorySeparatorChar
    & dotnet publish $sample -c Release -r hybridcpu -p:PublishAot=true --no-restore "/p:HybridCpuSdkPackRoot=$packRoot" "/p:HybridCpuCompilerPath=$(Join-Path $run.Pack 'HybridCPU.Compiler.NativeAot.Adapter.dll')" "/p:HybridCpuDotNetHostPath=$dotnetHost" "/p:PublishDir=$publishDirectory\" /p:UseSharedCompilation=false /m:1
    if ($LASTEXITCODE -ne 0) { throw "HCPUB1312: $($run.Name) dotnet publish failed." }
    $image = Join-Path $publishDirectory 'Phase26RestrictedPublish.hcexe'
    $provenance = Get-Content -Raw -LiteralPath ($image + '.provenance.json') | ConvertFrom-Json
    if (!$provenance.TraversedIlCompilerGraph) { throw 'HCPUB1313: provenance does not prove ILCompiler graph traversal.' }
    $results += [ordered]@{
        imageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $image).Hash.ToLowerInvariant()
        imageManifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath ($image + '.json')).Hash.ToLowerInvariant()
        provenanceSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath ($image + '.provenance.json')).Hash.ToLowerInvariant()
        imageBytes = (Get-Item -LiteralPath $image).Length
    }
}
if ($results[0].imageSha256 -ne $results[1].imageSha256 -or
    $results[0].imageManifestSha256 -ne $results[1].imageManifestSha256 -or
    $results[0].provenanceSha256 -ne $results[1].provenanceSha256) {
    throw 'HCPUB1314: two installed-pack publish outputs are not deterministic.'
}
$publishSdk = (& dotnet --version)
if ($LASTEXITCODE -ne 0) { throw 'HCPUB1315: publish SDK identity is unavailable.' }
Push-Location $source
try {
    $ilCompilerHostSdk = (& $dotnetHost --version)
    if ($LASTEXITCODE -ne 0) { throw 'HCPUB1316: ILCompiler host SDK identity is unavailable.' }
} finally {
    Pop-Location
}

[ordered]@{
    schema = 'hybridcpu.phase26-sdk-publish-verification/v1'
    sourceCommit = '94ea82652cdd4e0f8046b5bd5becbd11461482ca'
    phase23PatchSha256 = $expectedPatch1
    phase26PatchSha256 = $expectedPatch2
    patchSetSha256 = '08a6ab21735aeb76b23e5e4f14be87007320475d3aaa14a513a8a713f3dbe7de'
    hostRid = 'win-x64'
    targetRid = 'hybridcpu'
    publishSdk = $publishSdk
    ilCompilerHostSdk = $ilCompilerHostSdk
    installedPackFiles = (ConvertFrom-Json $packManifest1).files.Count
    deterministicRuns = 2
    imageSha256 = $results[0].imageSha256
    imageManifestSha256 = $results[0].imageManifestSha256
    provenanceSha256 = $results[0].provenanceSha256
    imageBytes = $results[0].imageBytes
    llvmRequired = $false
    hostObjectWriterInvoked = $false
} | ConvertTo-Json

Remove-Item -LiteralPath $verificationRoot -Recurse -Force
