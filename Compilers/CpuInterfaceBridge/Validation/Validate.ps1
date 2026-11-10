param([switch]$IncludeIntegration, [switch]$IncludeRunner, [switch]$IncludeHostAdapterSmoke,
    [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$ArtifactName)
$ErrorActionPreference = 'Stop'
$bridge = Split-Path $PSScriptRoot -Parent
$validationRoot = Join-Path $bridge '.artifacts/diagnostics-20260905'
if ($IncludeRunner) { $validationRoot = Join-Path $bridge '.artifacts/tool-integration-20260905' }
if ($IncludeHostAdapterSmoke) { $validationRoot = Join-Path $bridge '.artifacts/host-debug-20260905' }
if ($ArtifactName) { $validationRoot = Join-Path (Join-Path $bridge '.artifacts') $ArtifactName }
New-Item -ItemType Directory -Force -Path $validationRoot | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $validationRoot 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $validationRoot 'packages'
$env:TEMP = Join-Path $validationRoot 'tmp'
$env:TMP = $env:TEMP
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
$props = Join-Path $PSScriptRoot 'Isolation.props'
$integrationExit = 0
if ($IncludeIntegration -or $IncludeRunner) {
    $project = Join-Path $bridge 'HybridCpu_InterfaceBridge.csproj'
    if ($IncludeRunner) { $project = Join-Path $bridge '../../Tools/HybridCpuIseImageRunner/HybridCpuIseImageRunner.csproj' }
    dotnet build $project --disable-build-servers -m:1 -p:DefineTestSupport=false "-p:DirectoryBuildPropsPath=$props" "-p:BridgeValidationRoot=$validationRoot" *> "$validationRoot/integration-build.log"
    $integrationExit = $LASTEXITCODE
    Write-Output "Integration build exit=$integrationExit (see integration-build.log)"
}
dotnet run --project (Join-Path $PSScriptRoot 'Bridge.Diagnostics.Validation.csproj') --disable-build-servers "-p:DirectoryBuildPropsPath=$props" "-p:BridgeValidationRoot=$validationRoot" *> "$validationRoot/harness.log"
$harnessExit = $LASTEXITCODE
Get-Content "$validationRoot/harness.log"
Write-Output "Harness exit=$harnessExit"
if ($harnessExit -ne 0) { exit $harnessExit }
if ($IncludeHostAdapterSmoke -and $integrationExit -eq 0) {
    dotnet run --project (Join-Path $PSScriptRoot 'RealHostAdapter/RealHostAdapter.csproj') --disable-build-servers "-p:DirectoryBuildPropsPath=$props" "-p:BridgeValidationRoot=$validationRoot" *> "$validationRoot/real-host-adapter.log"
    $adapterExit = $LASTEXITCODE
    Get-Content "$validationRoot/real-host-adapter.log"
    Write-Output "Real host adapter exit=$adapterExit"
    if ($adapterExit -ne 0) { exit $adapterExit }
}
exit $integrationExit
