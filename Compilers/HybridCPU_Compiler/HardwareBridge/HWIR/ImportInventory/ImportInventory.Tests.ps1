param([Parameter(Mandatory=$true)][string]$SdkDirectory)
$ErrorActionPreference = 'Stop'
$tool = Join-Path $PSScriptRoot 'ImportInventory.csproj'
$id = [Guid]::NewGuid().ToString('N')
$case = Join-Path $env:TEMP ('cw-import-fixture-' + $id)
$projectDir = Join-Path $case 'model'
$output = Join-Path $env:TEMP ('cw-import-result-' + $id + '.json')
$outputRelease = Join-Path $env:TEMP ('cw-import-result-release-' + $id + '.json')
$outputFramework = Join-Path $env:TEMP ('cw-import-result-framework-' + $id + '.json')
$capture = Join-Path $env:TEMP ('cw-import-capture-' + $id + '.json')
$graph = Join-Path $env:TEMP ('cw-import-graph-' + $id + '.json')
New-Item -ItemType Directory -Path $projectDir | Out-Null
$project = Join-Path $projectDir 'Model.csproj'
$props = Join-Path $projectDir 'model.props'
[IO.File]::WriteAllText($project, '<Project Sdk="Microsoft.NET.Sdk"><Import Project="model.props" /><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>')
[IO.File]::WriteAllText($props, '<Project><PropertyGroup><DefineConstants>$(DefineConstants);MODEL_IMPORT_TEST</DefineConstants></PropertyGroup></Project>')
[IO.File]::WriteAllText((Join-Path $projectDir 'Model.cs'), 'public class Model {}')
[IO.File]::WriteAllText($graph, '{"schema":"hardwarebridge.msbuild-project-graph/v1","projects":[]}')
$before = (Get-FileHash -LiteralPath $props -Algorithm SHA256).Hash
& dotnet run --project $tool -- $project $SdkDirectory $output
if ($LASTEXITCODE -ne 0) { throw 'Import inventory failed.' }
$result = Get-Content -Raw -LiteralPath $output | ConvertFrom-Json
if ($result.schema -ne 'hardwarebridge.msbuild-import-inventory/v1' -or
    @($result.files | Where-Object path -eq $props).Count -ne 1 -or
    @($result.compileFiles | Where-Object { $_ -eq (Join-Path $projectDir 'Model.cs') }).Count -ne 1 -or
    $result.defineConstants -notmatch 'MODEL_IMPORT_TEST' -or
    (Get-FileHash -LiteralPath $props -Algorithm SHA256).Hash -ne $before) { throw 'Import, property, Compile or read-only check failed.' }
& dotnet run --project $tool -- $project $SdkDirectory $outputRelease $graph 'Release'
if ($LASTEXITCODE -ne 0) { throw 'Release import graph evaluation failed.' }
$release = Get-Content -Raw -LiteralPath $outputRelease | ConvertFrom-Json
if ($release.schema -ne 'hardwarebridge.msbuild-import-graph/v1' -or
    $release.projectGraphSha256 -ne (Get-FileHash -LiteralPath $graph -Algorithm SHA256).Hash.ToLowerInvariant() -or
    $release.root.configuration -ne 'Release' -or
    @($release.root.files | Where-Object path -eq $props).Count -ne 1 -or
    (Get-FileHash -LiteralPath $props -Algorithm SHA256).Hash -ne $before) { throw 'Release configuration, graph identity or read-only check failed.' }
$captureObject = @{
    schema = 'circuitweave.msbuild-source-byte-inventory.v1'
    project = $project
    files = @(@{
        path = Join-Path $projectDir 'Model.cs'
        length = (Get-Item -LiteralPath (Join-Path $projectDir 'Model.cs')).Length
        sha256 = (Get-FileHash -LiteralPath (Join-Path $projectDir 'Model.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
        status = 'captured'
    })
    properties = @{
        Configuration = 'Release'
        TargetFramework = 'net11.0'
        DefineConstants = 'TRACE;RELEASE;NET11_0;NETCOREAPP;MODEL_IMPORT_TEST'
        LangVersion = '15.0'
        Nullable = 'enable'
    }
}
[IO.File]::WriteAllText($capture, ($captureObject | ConvertTo-Json -Depth 5 -Compress))
& dotnet run --project $tool -- $project $SdkDirectory $outputFramework $graph 'Release' 'net11.0' $capture
if ($LASTEXITCODE -ne 0) { throw 'TargetFramework-scoped import graph evaluation failed.' }
$framework = Get-Content -Raw -LiteralPath $outputFramework | ConvertFrom-Json
if ($framework.schema -ne 'hardwarebridge.msbuild-import-graph/v2' -or
    $framework.disposition -ne 'evaluation-with-captured-csc-properties-unqualified' -or
    $framework.sourceCaptureSha256 -ne (Get-FileHash -LiteralPath $capture -Algorithm SHA256).Hash.ToLowerInvariant() -or
    $framework.root.targetFramework -ne 'net11.0' -or
    $framework.root.defineConstants -notmatch 'NET11_0' -or
    $framework.root.defineConstants -notmatch 'NETCOREAPP' -or
    $framework.root.defineConstants -notmatch 'MODEL_IMPORT_TEST' -or
    (Get-FileHash -LiteralPath $props -Algorithm SHA256).Hash -ne $before) { throw 'TargetFramework defines or read-only check failed.' }
Write-Output 'MSBuild import evaluation Debug + Release + capture-bound TargetFramework fixture PASS; no model build or target execution.'
