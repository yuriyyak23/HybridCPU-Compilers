[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $IlCompilerDirectory,
    [Parameter(Mandatory = $true)]
    [string] $RuntimeReferenceDirectory,
    [string] $DotNetHostPath
)

$ErrorActionPreference = 'Stop'
$ilCompiler = [IO.Path]::GetFullPath($IlCompilerDirectory)
$references = [IO.Path]::GetFullPath($RuntimeReferenceDirectory)
$adapterRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sample = Join-Path $adapterRoot 'Samples\Phase05ScalarControlFlowV2Publish\Phase05ScalarControlFlowV2Publish.csproj'
if ([string]::IsNullOrWhiteSpace($DotNetHostPath)) { $DotNetHostPath = (Get-Command dotnet).Source }
$dotnetHost = [IO.Path]::GetFullPath($DotNetHostPath)
if (!(Test-Path -LiteralPath (Join-Path $ilCompiler 'ilc.dll')) -or
    !(Test-Path -LiteralPath $references) -or !(Test-Path -LiteralPath $dotnetHost)) {
    throw 'HCPUB1401: pinned ILCompiler, runtime references, or dotnet host are missing.'
}

$verificationRoot = Join-Path ([IO.Path]::GetTempPath()) "hybridcpu-r6-phase05-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $verificationRoot | Out-Null
try {
    & dotnet clean (Join-Path $adapterRoot 'HybridCPU.Compiler.NativeAot.Adapter.csproj') -c Release /p:UseSharedCompilation=false /m:1
    if ($LASTEXITCODE -ne 0) { throw 'HCPUB1402: clean adapter baseline failed.' }
    & dotnet restore (Join-Path $adapterRoot 'HybridCPU.Compiler.NativeAot.Adapter.csproj') --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw 'HCPUB1403: clean adapter restore failed.' }

    $packs = @((Join-Path $verificationRoot 'pack-1'), (Join-Path $verificationRoot 'pack-2'))
    foreach ($pack in $packs) {
        & (Join-Path $PSScriptRoot 'Install-HybridCpuSdkPack.ps1') -Destination $pack `
            -IlCompilerDirectory $ilCompiler -RuntimeReferenceDirectory $references | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'HCPUB1404: deterministic SDK pack installation failed.' }
    }
    $packManifest1 = Get-Content -Raw -LiteralPath (Join-Path $packs[0] 'hybridcpu.pack-files.json')
    $packManifest2 = Get-Content -Raw -LiteralPath (Join-Path $packs[1] 'hybridcpu.pack-files.json')
    if ($packManifest1 -ne $packManifest2) { throw 'HCPUB1405: independently installed pack manifests differ.' }
    if ($packManifest1 -match '(?i)llvm') { throw 'HCPUB1406: ScalarControlFlowV2 pack contains LLVM contamination.' }

    & dotnet clean $sample -c Release -r hybridcpu /p:UseSharedCompilation=false /m:1
    if ($LASTEXITCODE -ne 0) { throw 'HCPUB1407: sample clean failed.' }
    & dotnet restore $sample -r hybridcpu --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw 'HCPUB1408: clean sample restore failed.' }

    $results = @()
    for ($index = 0; $index -lt $packs.Count; $index++) {
        $pack = $packs[$index]
        $publish = Join-Path $verificationRoot "publish-$($index + 1)"
        & dotnet publish $sample -c Release -r hybridcpu -p:PublishAot=true --no-restore `
            "/p:HybridCpuSdkPackRoot=$pack\" `
            "/p:HybridCpuCompilerPath=$(Join-Path $pack 'HybridCPU.Compiler.NativeAot.Adapter.dll')" `
            "/p:HybridCpuDotNetHostPath=$dotnetHost" "/p:PublishDir=$publish\" `
            /p:UseSharedCompilation=false /m:1
        if ($LASTEXITCODE -ne 0) { throw "HCPUB1409: publish run $($index + 1) failed." }
        $image = Join-Path $publish 'Phase05ScalarControlFlowV2Publish.hcexe'
        $sidecar = Get-Content -Raw -LiteralPath ($image + '.json') | ConvertFrom-Json
        $provenance = Get-Content -Raw -LiteralPath ($image + '.provenance.json') | ConvertFrom-Json
        if (!$provenance.TraversedIlCompilerGraph -or
            $provenance.ProfileId -ne 'HybridCPU.DotNetAot.ScalarControlFlowV2' -or
            $provenance.CompiledMethodCount -ne 5 -or
            $sidecar.CompiledMethodCount -ne 5 -or
            $sidecar.ProfileId -ne $provenance.ProfileId -or
            $sidecar.ManagedGraphDigest -ne $provenance.ManagedGraphDigest -or
            $sidecar.BodyPresentationDigest -ne $provenance.BodyPresentationDigest) {
            throw 'HCPUB1410: publish provenance does not prove the exact adapter-presented compiler graph.'
        }
        $results += [ordered]@{
            imageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $image).Hash.ToLowerInvariant()
            imageManifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath ($image + '.json')).Hash.ToLowerInvariant()
            provenanceSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath ($image + '.provenance.json')).Hash.ToLowerInvariant()
            inputAssemblySha256 = $provenance.InputAssemblySha256
            imageBytes = (Get-Item -LiteralPath $image).Length
            graphDigest = $provenance.ManagedGraphDigest
            bodyPresentationDigest = $provenance.BodyPresentationDigest
            orderedObjectDigest = $provenance.OrderedObjectDigest
            backendProvenanceDigest = $provenance.BackendProvenanceDigest
        }
    }
    if ($results[0].imageSha256 -ne $results[1].imageSha256 -or
        $results[0].imageManifestSha256 -ne $results[1].imageManifestSha256 -or
        $results[0].provenanceSha256 -ne $results[1].provenanceSha256 -or
        $results[0].inputAssemblySha256 -ne $results[1].inputAssemblySha256) {
        throw 'HCPUB1411: clean installed-pack publishes are not deterministic.'
    }

    [ordered]@{
        schema = 'hybridcpu.refplan6-phase05-publish-verification/v1'
        profileId = 'HybridCPU.DotNetAot.ScalarControlFlowV2'
        sourceCommit = '94ea82652cdd4e0f8046b5bd5becbd11461482ca'
        targetRid = 'hybridcpu'
        hostRid = 'win-x64'
        publishSdk = (& dotnet --version)
        installedPackFiles = (ConvertFrom-Json $packManifest1).files.Count
        deterministicRuns = 2
        compiledMethods = 5
        imageSha256 = $results[0].imageSha256
        imageManifestSha256 = $results[0].imageManifestSha256
        provenanceSha256 = $results[0].provenanceSha256
        inputAssemblySha256 = $results[0].inputAssemblySha256
        imageBytes = $results[0].imageBytes
        managedGraphDigest = $results[0].graphDigest
        bodyPresentationDigest = $results[0].bodyPresentationDigest
        orderedObjectDigest = $results[0].orderedObjectDigest
        backendProvenanceDigest = $results[0].backendProvenanceDigest
        traversedIlCompilerGraph = $true
        llvmRequired = $false
        hostJitFallback = $false
        hostNativeFallback = $false
    } | ConvertTo-Json
}
finally {
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedVerification = [IO.Path]::GetFullPath($verificationRoot)
    if (!$resolvedVerification.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedVerification) -notlike 'hybridcpu-r6-phase05-*') {
        throw 'HCPUB1412: refusing to remove a non-verification directory.'
    }
    if (Test-Path -LiteralPath $resolvedVerification) {
        Remove-Item -LiteralPath $resolvedVerification -Recurse -Force
    }
}
