$ErrorActionPreference = 'Stop'
$bridge = Split-Path -Parent $PSScriptRoot
$project = Join-Path $bridge 'SemanticInventory\HardwareBridge.SemanticInventory.csproj'
dotnet build $project -v:q
if ($LASTEXITCODE -ne 0) { throw 'Inventory build failed' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('cw-inventory-test-' + [guid]::NewGuid().ToString('N'))
$model = Join-Path $root 'model\project'
New-Item -ItemType Directory -Path $model -Force | Out-Null
$a = Join-Path $model 'A.cs'; $b = Join-Path $model 'B.cs'
[IO.File]::WriteAllText($a, 'namespace Demo; [System.Obsolete] public partial struct Processor { public int A; public void First() { Second(); } public void Overload(int x) { } public partial class Nested { public int Isolated; } }')
[IO.File]::WriteAllText($b, "namespace Demo; public partial struct Processor { public long B; public void Second() { B = 1; } public void Overload(long x) { }`n#if FEATURE`npublic class Enabled { }`n#endif`n}")
$input = Join-Path $root 'input.json'; $output = Join-Path $root 'output.json'
$entries = @($a,$b | ForEach-Object { @{ path = $_; length = (Get-Item -LiteralPath $_).Length; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(); status = 'captured' } })
@{ schema='circuitweave.msbuild-source-byte-inventory.v1'; project=(Join-Path $model 'model.csproj'); properties=@{Configuration='Debug'; TargetFramework='net11.0'; DefineConstants='FEATURE'; LangVersion='preview'; Nullable='enable'; AssemblyName='DemoAssembly'}; projectReferences=@(); globalUsings=@(); files=$entries } | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 $input
$exe = Join-Path $bridge '..\..\..\..\CircuitWeave-Studio-main\eng\HardwareBridgeBuild\bin\HardwareBridge.SemanticInventory\net11.0\HardwareBridge.SemanticInventory.dll'
dotnet $exe $input $output
if ($LASTEXITCODE -ne 0) { throw 'Fixture inventory failed' }
$result = Get-Content -Raw $output | ConvertFrom-Json
$proc = @($result.types | Where-Object symbol -eq 'global::Demo.Processor')
if ($proc.Count -ne 1 -or $proc[0].assembly -ne 'DemoAssembly' -or $proc[0].declarations.Count -ne 2 -or $proc[0].members.Count -ne 8 -or $proc[0].attributes.Count -ne 1) { throw 'Partial, member or attribute coverage failed' }
if (@($result.types | Where-Object symbol -eq 'global::Demo.Processor.Nested').Count -ne 1 -or @($result.types | Where-Object symbol -eq 'global::Demo.Processor.Enabled').Count -ne 1) { throw 'Nested or conditional symbol missing' }
if ($proc[0].declarations[0].location.startLine -ne 1 -or $result.files.Count -ne 2) { throw 'Source provenance missing' }
$first = @($proc[0].members | Where-Object id -eq 'M:Demo.Processor.First')[0]
$second = @($proc[0].members | Where-Object id -eq 'M:Demo.Processor.Second')[0]
$partA = @($proc[0].declarations | Where-Object { $_.location.file -eq $a })[0]
$partB = @($proc[0].declarations | Where-Object { $_.location.file -eq $b })[0]
if ($partA.memberIds -notcontains 'M:Demo.Processor.First' -or $partA.memberIds -contains 'M:Demo.Processor.Second' -or $partB.memberIds -notcontains 'M:Demo.Processor.Second' -or $partB.memberIds -contains 'M:Demo.Processor.First') { throw 'Partial member ownership incorrect' }
if (@($first.dependencies | Where-Object { $_.kind -eq 'invocation' -and $_.target -eq 'M:Demo.Processor.Second' }).Count -ne 1 -or @($second.dependencies | Where-Object { $_.kind -eq 'member-reference' -and $_.target -eq 'F:Demo.Processor.B' }).Count -ne 1) { throw 'Semantic call or field reference missing' }
if (@($proc[0].members | Where-Object { $_.id -like 'M:Demo.Processor.Overload*' }).Count -ne 2 -or @($proc[0].members | Where-Object { $_.id -eq 'F:Demo.Processor.Nested.Isolated' }).Count -ne 0) { throw 'Overloads or nested ownership conflated' }

# Source ProjectReference compilation: dependency source is resolved without a DLL.
$depDir = Join-Path $root 'dependency'; New-Item -ItemType Directory -Path $depDir | Out-Null
$depProject = Join-Path $depDir 'Dependency.csproj'; [IO.File]::WriteAllText($depProject, '<Project/>')
$depSource = Join-Path $depDir 'Dependency.cs'; [IO.File]::WriteAllText($depSource, 'namespace Linked; public class Value : LinkedBase.Base { }')
$dep2Project = Join-Path $depDir 'Dependency2.csproj'; [IO.File]::WriteAllText($dep2Project, '<Project/>')
$dep2Source = Join-Path $depDir 'Dependency2.cs'; [IO.File]::WriteAllText($dep2Source, 'namespace LinkedBase; public class Base { }')
$c = Join-Path $model 'UsesDependency.cs'; [IO.File]::WriteAllText($c, 'using System.Text.Json.Serialization; namespace Demo; public class UsesDependency { public Linked.Value Field; public LinkedBase.Base BaseField; } [JsonSerializable(typeof(int))] public partial class ExampleContext : JsonSerializerContext { }')
$graphInput = Join-Path $root 'graph-input.json'
$graphFiles = @($a,$b,$c | ForEach-Object { @{ path = $_; length=(Get-Item -LiteralPath $_).Length; sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(); status='captured' } })
@{ schema='circuitweave.msbuild-source-byte-inventory.v1'; project=(Join-Path $model 'model.csproj'); properties=@{Configuration='Debug'; TargetFramework='net11.0'; DefineConstants='FEATURE'; LangVersion='preview'; Nullable='enable'; AssemblyName='DemoAssembly'}; projectReferences=@($depProject); globalUsings=@(); files=$graphFiles } | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 $graphInput
$graph = Join-Path $root 'graph.json'
@{ schema='hardwarebridge.msbuild-project-graph/v1'; rootInventorySha256=(Get-FileHash -LiteralPath $graphInput -Algorithm SHA256).Hash.ToLowerInvariant(); projects=@(@{ project=$depProject; projectSha256=(Get-FileHash -LiteralPath $depProject -Algorithm SHA256).Hash.ToLowerInvariant(); properties=@{LangVersion='preview'; DefineConstants=''; Nullable='enable'; AssemblyName='Dependency'}; files=@(@{path=$depSource; length=(Get-Item -LiteralPath $depSource).Length; sha256=(Get-FileHash -LiteralPath $depSource -Algorithm SHA256).Hash.ToLowerInvariant()}); projectReferences=@($dep2Project); globalUsings=@() }, @{ project=$dep2Project; projectSha256=(Get-FileHash -LiteralPath $dep2Project -Algorithm SHA256).Hash.ToLowerInvariant(); properties=@{LangVersion='preview'; DefineConstants=''; Nullable='enable'; AssemblyName='Dependency2'}; files=@(@{path=$dep2Source; length=(Get-Item -LiteralPath $dep2Source).Length; sha256=(Get-FileHash -LiteralPath $dep2Source -Algorithm SHA256).Hash.ToLowerInvariant()}); projectReferences=@(); globalUsings=@() }) } | ConvertTo-Json -Depth 9 | Set-Content -Encoding utf8 $graph
# ConvertFrom PowerShell 5 UTF-8 output includes BOM; graph parser expects raw UTF-8.
[IO.File]::WriteAllText($graph, (Get-Content -Raw -LiteralPath $graph), [Text.UTF8Encoding]::new($false))
$graphOutput = Join-Path $root 'graph-output.json'
dotnet $exe $graphInput $graphOutput --project-graph $graph
if ($LASTEXITCODE -ne 0) { throw 'Source project graph inventory failed' }
$linked = Get-Content -Raw $graphOutput | ConvertFrom-Json
$consumer = @($linked.types | Where-Object symbol -eq 'global::Demo.UsesDependency')
if ($consumer.Count -ne 1 -or ($consumer[0].dependencies -notcontains 'global::Linked.Value' -or $consumer[0].dependencies -notcontains 'global::LinkedBase.Base') -or @($linked.types | Where-Object symbol -eq 'global::Linked.Value').Count -ne 1) { throw 'Source ProjectReference binding failed' }
if (@($linked.diagnostics | Where-Object id -eq 'CS0246').Count -ne 0) { throw 'Reference unresolved' }
$sdkGenerator = Join-Path $env:ProgramFiles 'dotnet\packs\Microsoft.NETCore.App.Ref\11.0.0\analyzers\dotnet\cs\System.Text.Json.SourceGeneration.dll'
$genManifest = Join-Path $root 'generator.json'
@{ schema='hardwarebridge.sdk-json-generator/v1'; path=$sdkGenerator; sha256=(Get-FileHash -LiteralPath $sdkGenerator -Algorithm SHA256).Hash.ToLowerInvariant() } | ConvertTo-Json | Set-Content -Encoding utf8 $genManifest
[IO.File]::WriteAllText($genManifest, (Get-Content -Raw -LiteralPath $genManifest), [Text.UTF8Encoding]::new($false))
$generatedOutput = Join-Path $root 'generated.json'
dotnet $exe $graphInput $generatedOutput --project-graph $graph --sdk-json-generator $genManifest
if ($LASTEXITCODE -ne 0) { throw 'SDK JSON generator run failed' }
$generated = Get-Content -Raw $generatedOutput | ConvertFrom-Json
if ($generated.generatorProvenance.generated.Count -lt 1 -or $generated.diagnostics.Count -ne 0 -or @($generated.types | Where-Object symbol -eq 'global::Demo.ExampleContext')[0].declarations.Count -lt 2) { throw 'Generated partial declarations or diagnostics incorrect' }
$generatedOutput2 = Join-Path $root 'generated-replay.json'
dotnet $exe $graphInput $generatedOutput2 --project-graph $graph --sdk-json-generator $genManifest
if ($LASTEXITCODE -ne 0) { throw 'Generator replay failed' }
$replayed = Get-Content -Raw $generatedOutput2 | ConvertFrom-Json
$firstHashes = @($generated.generatorProvenance.generated | ForEach-Object { $_.hintName + ':' + $_.sha256 }) -join '|'
$replayHashes = @($replayed.generatorProvenance.generated | ForEach-Object { $_.hintName + ':' + $_.sha256 }) -join '|'
if ($firstHashes -ne $replayHashes) { throw 'Generator output is not deterministic' }
$badManifest = Join-Path $root 'bad-generator.json'
@{ schema='hardwarebridge.sdk-json-generator/v1'; path=$sdkGenerator; sha256=('0' * 64) } | ConvertTo-Json | Set-Content -Encoding utf8 $badManifest
[IO.File]::WriteAllText($badManifest, (Get-Content -Raw -LiteralPath $badManifest), [Text.UTF8Encoding]::new($false))
$badGenerated = Join-Path $root 'bad-generated.json'
dotnet $exe $graphInput $badGenerated --project-graph $graph --sdk-json-generator $badManifest
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $badGenerated)) { throw 'Stale generator accepted' }
[IO.File]::AppendAllText($depSource, ' ')
$staleGraphOutput = Join-Path $root 'stale-graph.json'
dotnet $exe $graphInput $staleGraphOutput --project-graph $graph
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $staleGraphOutput)) { throw 'Stale referenced source accepted' }
[IO.File]::AppendAllText($a, ' ')
$bad = Join-Path $root 'stale.json'
dotnet $exe $input $bad
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $bad)) { throw 'Stale input accepted' }
$forbidden = Join-Path $model 'forbidden.json'
dotnet $exe $input $forbidden
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $forbidden)) { throw 'Model output accepted' }
Write-Output 'Semantic inventory partial/project references/generator/provenance/stale/output boundary PASS'
