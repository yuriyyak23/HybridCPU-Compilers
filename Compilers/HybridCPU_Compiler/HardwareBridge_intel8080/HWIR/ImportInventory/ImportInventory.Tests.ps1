param([Parameter(Mandatory=$true)][string]$SdkDirectory)
$ErrorActionPreference = 'Stop'
$tool = Join-Path $PSScriptRoot 'ImportInventory.csproj'
$id = [Guid]::NewGuid().ToString('N')
$case = Join-Path $env:TEMP ('cw-import-fixture-' + $id)
$projectDir = Join-Path $case 'model'
$output = Join-Path $env:TEMP ('cw-import-result-' + $id + '.json')
New-Item -ItemType Directory -Path $projectDir | Out-Null
$project = Join-Path $projectDir 'Model.csproj'
$props = Join-Path $projectDir 'model.props'
[IO.File]::WriteAllText($project, '<Project Sdk="Microsoft.NET.Sdk"><Import Project="model.props" /><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>')
[IO.File]::WriteAllText($props, '<Project><PropertyGroup><DefineConstants>$(DefineConstants);MODEL_IMPORT_TEST</DefineConstants></PropertyGroup></Project>')
[IO.File]::WriteAllText((Join-Path $projectDir 'Model.cs'), 'public class Model {}')
$before = (Get-FileHash -LiteralPath $props -Algorithm SHA256).Hash
& dotnet run --project $tool -- $project $SdkDirectory $output
if ($LASTEXITCODE -ne 0) { throw 'Import inventory failed.' }
$result = Get-Content -Raw -LiteralPath $output | ConvertFrom-Json
if ($result.schema -ne 'hardwarebridge.msbuild-import-inventory/v1' -or
    @($result.files | Where-Object path -eq $props).Count -ne 1 -or
    @($result.compileFiles | Where-Object { $_ -eq (Join-Path $projectDir 'Model.cs') }).Count -ne 1 -or
    $result.defineConstants -notmatch 'MODEL_IMPORT_TEST' -or
    (Get-FileHash -LiteralPath $props -Algorithm SHA256).Hash -ne $before) { throw 'Import, property, Compile or read-only check failed.' }
Write-Output 'MSBuild import evaluation fixture PASS; no model build or target execution.'
