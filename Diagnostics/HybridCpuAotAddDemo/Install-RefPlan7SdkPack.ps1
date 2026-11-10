[CmdletBinding()]
param(
    [string] $Destination = (Join-Path $PSScriptRoot '.hybridcpu-pack-refplan7'),
    [Parameter(Mandatory = $true)]
    [string] $IlCompilerDirectory,
    [Parameter(Mandatory = $true)]
    [string] $RuntimeReferenceDirectory
)

$ErrorActionPreference = 'Stop'
$demoRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $demoRoot '..\..'))
$compilerRoot = Join-Path $repositoryRoot 'Compilers'
$installer = Join-Path $compilerRoot 'HybridCPU_Compiler\NativeAot\Packaging\Install-HybridCpuSdkPack.ps1'
$platformProject = Join-Path $compilerRoot 'HybridCPU_Platform.Contracts\HybridCPU.Platform.Contracts.csproj'
$platformBinary = Join-Path $compilerRoot 'HybridCPU_Platform.Contracts\bin\Release\net10.0\HybridCPU.Platform.Contracts.dll'

& $installer -Destination $Destination -IlCompilerDirectory $IlCompilerDirectory `
    -RuntimeReferenceDirectory $RuntimeReferenceDirectory -Configuration Release
if ($LASTEXITCODE -ne 0) { throw 'The base HybridCPU SDK-pack installer failed.' }

# Older base installers omitted the separate RefPlan7 target/ABI assembly.
# Complete such packs without duplicating the entry now emitted by the current installer.
& dotnet build $platformProject -c Release --no-restore /p:UseSharedCompilation=false /m:1
if ($LASTEXITCODE -ne 0) { throw 'HybridCPU.Platform.Contracts build failed.' }

$destinationRoot = [IO.Path]::GetFullPath($Destination)
$installedPlatformBinary = Join-Path $destinationRoot 'HybridCPU.Platform.Contracts.dll'
Copy-Item -LiteralPath $platformBinary -Destination $installedPlatformBinary

$runtimeManifestPath = Join-Path $destinationRoot 'hybridcpu.runtime-pack.json'
$filesManifestPath = Join-Path $destinationRoot 'hybridcpu.pack-files.json'
$runtimeManifest = Get-Content -Raw -LiteralPath $runtimeManifestPath | ConvertFrom-Json
$filesManifest = Get-Content -Raw -LiteralPath $filesManifestPath | ConvertFrom-Json
$filesManifest.packVersion = $runtimeManifest.PackVersion
if (!(@($filesManifest.owners.compiler) -contains 'HybridCPU.Platform.Contracts.dll')) {
    $filesManifest.owners.compiler = @($filesManifest.owners.compiler) + 'HybridCPU.Platform.Contracts.dll'
}
if (!(@($filesManifest.files.name) -contains 'HybridCPU.Platform.Contracts.dll')) {
    $filesManifest.files = @($filesManifest.files) + [pscustomobject]@{
        name = 'HybridCPU.Platform.Contracts.dll'
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $installedPlatformBinary).Hash.ToLowerInvariant()
        bytes = (Get-Item -LiteralPath $installedPlatformBinary).Length
    }
}
$filesManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $filesManifestPath -Encoding utf8NoBOM

Write-Output $destinationRoot
