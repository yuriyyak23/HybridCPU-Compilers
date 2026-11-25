param(
    [Parameter(Mandatory = $true)][string]$ToolDll,
    [Parameter(Mandatory = $true)][string]$DotnetExe,
    [Parameter(Mandatory = $true)][string]$SdkDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'

foreach ($path in @($ToolDll, $DotnetExe, $SdkDirectory)) {
    if (-not [IO.Path]::IsPathRooted($path) -or -not (Test-Path -LiteralPath $path)) {
        throw "Required absolute input is missing: $path"
    }
}
if (-not [IO.Path]::IsPathRooted($OutputDirectory) -or (Test-Path -LiteralPath $OutputDirectory)) {
    throw 'OutputDirectory must be a new absolute directory.'
}
$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
if (-not $OutputDirectory.StartsWith($temp + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Fixture outputs must be below TEMP.'
}
$null = New-Item -ItemType Directory -Path $OutputDirectory
$projectDir = Join-Path $OutputDirectory 'model\fixture'
$null = New-Item -ItemType Directory -Path $projectDir
$project = Join-Path $projectDir 'GlobalsFixture.csproj'
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <EnableInternalTestHooks Condition="'$(EnableInternalTestHooks)' == ''">false</EnableInternalTestHooks>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="DebugOnly.cs" Condition="'$(Configuration)' == 'Debug'" />
    <Compile Include="ReleaseOnly.cs" Condition="'$(Configuration)' == 'Release'" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath $project -Encoding utf8
'internal sealed class DebugOnly { }' | Set-Content -LiteralPath (Join-Path $projectDir 'DebugOnly.cs') -Encoding utf8
'internal sealed class ReleaseOnly { }' | Set-Content -LiteralPath (Join-Path $projectDir 'ReleaseOnly.cs') -Encoding utf8

$debugGraph = Join-Path $OutputDirectory 'graph-debug.json'
$releaseGraph = Join-Path $OutputDirectory 'graph-release.json'
& $DotnetExe $ToolDll $project $SdkDirectory $debugGraph
if ($LASTEXITCODE -ne 0) { throw "Default graph evaluation failed: $LASTEXITCODE" }
& $DotnetExe $ToolDll $project $SdkDirectory $releaseGraph '--global-property=Configuration=Release'
if ($LASTEXITCODE -ne 0) { throw "Release graph evaluation failed: $LASTEXITCODE" }

function Read-Graph([string]$Path) {
    $doc = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($doc.schema -ne 'hardwarebridge.evaluated-project-graph/v2' -or $doc.disposition -ne 'evaluation-only-unqualified') {
        throw "Unexpected graph contract: $Path"
    }
    if ($doc.nodes.Count -ne 1) { throw "Fixture graph must contain one node: $Path" }
    return $doc
}
$debug = Read-Graph $debugGraph
$release = Read-Graph $releaseGraph
$d = $debug.nodes[0]
$r = $release.nodes[0]
if ($debug.globalProperties.Configuration -ne 'Debug' -or $d.properties.configuration -ne 'Debug' -or
    $d.globalProperties.Configuration -ne 'Debug' -or $d.globalProperties.EnableInternalTestHooks -ne 'true') {
    throw 'Legacy Debug defaults changed.'
}
if ($release.globalProperties.Configuration -ne 'Release' -or $r.properties.configuration -ne 'Release' -or
    $r.globalProperties.Configuration -ne 'Release' -or $r.globalProperties.EnableInternalTestHooks -ne 'true') {
    throw 'Explicit Release override or preserved test-hook default is wrong.'
}
if (-not ($d.compileFiles -contains (Join-Path $projectDir 'DebugOnly.cs')) -or
    ($d.compileFiles -contains (Join-Path $projectDir 'ReleaseOnly.cs')) -or
    -not ($r.compileFiles -contains (Join-Path $projectDir 'ReleaseOnly.cs')) -or
    ($r.compileFiles -contains (Join-Path $projectDir 'DebugOnly.cs'))) {
    throw 'Configuration-specific evaluated Compile items do not follow the requested global.'
}
if ($d.id -eq $r.id) { throw 'Node identity failed to include different global properties.' }
Write-Output "PASS evaluator defaults/override; Debug node=$($d.id); Release node=$($r.id)"
