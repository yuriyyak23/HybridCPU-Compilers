param([Parameter(Mandatory=$true)][string]$SdkDirectory)
$ErrorActionPreference = 'Stop'
$case = Join-Path $env:TEMP ('cw-graph-fixture-' + [Guid]::NewGuid().ToString('N'))
$model = Join-Path $case 'model'
$root = Join-Path $model 'root'
$child = Join-Path $model 'child'
New-Item -ItemType Directory -Path $root,$child | Out-Null
$rootProject = Join-Path $root 'Root.csproj'
$childProject = Join-Path $child 'Child.csproj'
$out = Join-Path $case 'graph.json'
[IO.File]::WriteAllText($rootProject, '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="..\child\Child.csproj" GlobalPropertiesToRemove="EnableInternalTestHooks" /></ItemGroup></Project>')
[IO.File]::WriteAllText($childProject, '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup Condition="''$(EnableInternalTestHooks)'' == ''true''"><Compile Include="Hook.cs" /></ItemGroup></Project>')
[IO.File]::WriteAllText((Join-Path $child 'Hook.cs'), 'public class Hook {}')
$before = (Get-FileHash -LiteralPath $rootProject,$childProject -Algorithm SHA256).Hash -join ','
& dotnet run --project $PSScriptRoot -- $rootProject $SdkDirectory $out
if ($LASTEXITCODE -ne 0) { throw 'Graph fixture failed.' }
$graph = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
$node = @($graph.nodes | Where-Object project -eq $childProject)
if ($graph.schema -ne 'hardwarebridge.evaluated-project-graph/v2' -or $node.Count -ne 1 -or
    $node[0].evaluatedItems.PSObject.Properties.Name -notcontains 'Analyzer' -or
    $node[0].globalProperties.PSObject.Properties.Name -contains 'EnableInternalTestHooks' -or
    $node[0].compileFiles -contains (Join-Path $child 'Hook.cs') -or
    ((Get-FileHash -LiteralPath $rootProject,$childProject -Algorithm SHA256).Hash -join ',') -ne $before -or
    (Test-Path -LiteralPath (Join-Path $root 'obj')) -or (Test-Path -LiteralPath (Join-Path $child 'obj'))) { throw 'Evaluation, propagation or read-only check failed.' }
Write-Output 'ProjectGraph propagation and read-only fixture PASS.'
