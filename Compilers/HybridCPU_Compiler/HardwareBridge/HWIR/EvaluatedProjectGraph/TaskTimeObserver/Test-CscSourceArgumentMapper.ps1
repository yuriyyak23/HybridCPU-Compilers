param(
    [Parameter(Mandatory = $true)][string]$ToolDll,
    [Parameter(Mandatory = $true)][string]$DotnetExe,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
foreach ($path in @($ToolDll, $DotnetExe)) {
    if (-not [IO.Path]::IsPathRooted($path) -or -not (Test-Path -LiteralPath $path)) { throw "Required absolute input missing: $path" }
}
if (-not [IO.Path]::IsPathRooted($OutputDirectory) -or (Test-Path -LiteralPath $OutputDirectory)) { throw 'OutputDirectory must be new and absolute.' }
$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
if (-not $OutputDirectory.StartsWith($temp + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture must write under TEMP.' }
$null = New-Item -ItemType Directory -Path $OutputDirectory
$projectDir = Join-Path $OutputDirectory 'model with spaces'
$null = New-Item -ItemType Directory -Path $projectDir
$sourceA = Join-Path $projectDir 'A.cs'
$sourceB = Join-Path $projectDir 'B file.cs'
$generated = Join-Path $projectDir 'Generated.g.cs'
$project = Join-Path $projectDir 'Fixture.csproj'
'<Project />' | Set-Content -LiteralPath $project -Encoding utf8
'internal class A {}' | Set-Content -LiteralPath $sourceA -Encoding utf8
'internal class B {}' | Set-Content -LiteralPath $sourceB -Encoding utf8
'internal class Generated {}' | Set-Content -LiteralPath $generated -Encoding utf8
$nodeId = "$project|Configuration=Release;EnableInternalTestHooks=true;IsGraphBuild=true"
$graphPath = Join-Path $OutputDirectory 'graph.json'
$rspPath = Join-Path $OutputDirectory 'capture.rsp'
$graph = [ordered]@{
    schema = 'hardwarebridge.evaluated-project-graph/v2'
    disposition = 'evaluation-only-unqualified'
    nodes = @([ordered]@{ id=$nodeId; project=$project; compileFiles=@($sourceA,$sourceB) })
}
[IO.File]::WriteAllText($graphPath, ($graph | ConvertTo-Json -Depth 5 -Compress), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($rspPath, "/warn:4 `"$sourceA`" `"$sourceB`"", [Text.UTF8Encoding]::new($false))
$graphSha = (Get-FileHash -Algorithm SHA256 $graphPath).Hash.ToLowerInvariant()
$generatedRow = [ordered]@{ nodeId=$nodeId; project=$project; role='msbuild-generated'; path=$generated;
    bytes=(Get-Item -LiteralPath $generated).Length; sha256=(Get-FileHash -Algorithm SHA256 $generated).Hash.ToLowerInvariant() }
$inventoryPath = Join-Path $OutputDirectory 'generated-inventory.json'
$inventory = [ordered]@{ schema='hardwarebridge.csc-generated-source-inventory/v1'; disposition='snapshot-unqualified'; graphSha256=$graphSha; files=@($generatedRow) }
[IO.File]::WriteAllText($inventoryPath, ($inventory | ConvertTo-Json -Depth 5 -Compress), [Text.UTF8Encoding]::new($false))
$inventorySha = (Get-FileHash -Algorithm SHA256 $inventoryPath).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($rspPath, "/warn:4 `"$sourceA`" `"$sourceB`" `"$generated`"", [Text.UTF8Encoding]::new($false))
$rspSha = (Get-FileHash -Algorithm SHA256 $rspPath).Hash.ToLowerInvariant()
$positive = Join-Path $OutputDirectory 'mapped.json'
& $DotnetExe $ToolDll map $graphPath $graphSha $nodeId $rspPath $rspSha $inventoryPath $inventorySha $projectDir $positive
if ($LASTEXITCODE -ne 0) { throw "Positive source mapping failed: $LASTEXITCODE" }
$mapping = Get-Content -LiteralPath $positive -Raw | ConvertFrom-Json
if ($mapping.schema -ne 'hardwarebridge.csc-source-argument-map/v1' -or $mapping.sources.Count -ne 3 -or
    $mapping.generatedSourceCount -ne 1 -or $mapping.claims.ordinarySourceBytesHashed -ne $false -or
    $mapping.claims.cscReadOrConsumedResponse -ne 'NotEstablished') {
    throw 'Positive mapping output is incomplete or overclaims evidence.'
}

function Expect-Refusal([string[]]$Arguments, [string]$Label) {
    & $DotnetExe $ToolDll @Arguments
    if ($LASTEXITCODE -ne 2) { throw "$Label did not refuse with exit 2 (actual $LASTEXITCODE)." }
}
Expect-Refusal @('map',$graphPath,('0' * 64),$nodeId,$rspPath,$rspSha,$inventoryPath,$inventorySha,$projectDir,(Join-Path $OutputDirectory 'wrong-graph-pin.json')) 'Wrong graph pin'
Expect-Refusal @('map',$graphPath,$graphSha,$nodeId,$rspPath,('0' * 64),$inventoryPath,$inventorySha,$projectDir,(Join-Path $OutputDirectory 'wrong-rsp-pin.json')) 'Wrong RSP pin'
Expect-Refusal @('map',$graphPath,$graphSha,$nodeId,$rspPath,$rspSha,$inventoryPath,('0' * 64),$projectDir,(Join-Path $OutputDirectory 'stale-inventory-pin.json')) 'Stale generated-inventory pin'

$missingInventoryPath = Join-Path $OutputDirectory 'generated-inventory-missing.json'
$inventoryMissing = [ordered]@{ schema='hardwarebridge.csc-generated-source-inventory/v1'; disposition='snapshot-unqualified'; graphSha256=$graphSha; files=@() }
[IO.File]::WriteAllText($missingInventoryPath, ($inventoryMissing | ConvertTo-Json -Depth 5 -Compress), [Text.UTF8Encoding]::new($false))
$inventoryMissingSha = (Get-FileHash -Algorithm SHA256 $missingInventoryPath).Hash.ToLowerInvariant()
Expect-Refusal @('map',$graphPath,$graphSha,$nodeId,$rspPath,$rspSha,$missingInventoryPath,$inventoryMissingSha,$projectDir,(Join-Path $OutputDirectory 'missing-inventory-row.json')) 'Missing generated-inventory row'

$alteredInventoryPath = Join-Path $OutputDirectory 'generated-inventory-altered.json'
$alteredRow = [ordered]@{ nodeId=$nodeId; project=$project; role='msbuild-generated'; path=$generated; bytes=$generatedRow.bytes; sha256=('0' * 64) }
$alteredInventory = [ordered]@{ schema='hardwarebridge.csc-generated-source-inventory/v1'; disposition='snapshot-unqualified'; graphSha256=$graphSha; files=@($alteredRow) }
[IO.File]::WriteAllText($alteredInventoryPath, ($alteredInventory | ConvertTo-Json -Depth 5 -Compress), [Text.UTF8Encoding]::new($false))
$alteredInventorySha = (Get-FileHash -Algorithm SHA256 $alteredInventoryPath).Hash.ToLowerInvariant()
Expect-Refusal @('map',$graphPath,$graphSha,$nodeId,$rspPath,$rspSha,$alteredInventoryPath,$alteredInventorySha,$projectDir,(Join-Path $OutputDirectory 'altered-inventory.json')) 'Altered generated-inventory row'
$alteredGraph = [ordered]@{ schema='hardwarebridge.evaluated-project-graph/v2'; disposition='evaluation-only-unqualified'; nodes=@([ordered]@{id=$nodeId;project=$project;compileFiles=@($sourceA)}) }
[IO.File]::WriteAllText($graphPath, ($alteredGraph | ConvertTo-Json -Depth 5 -Compress), [Text.UTF8Encoding]::new($false))
$alteredGraphSha = (Get-FileHash -Algorithm SHA256 $graphPath).Hash.ToLowerInvariant()
Expect-Refusal @('map',$graphPath,$alteredGraphSha,$nodeId,$rspPath,$rspSha,$inventoryPath,$inventorySha,$projectDir,(Join-Path $OutputDirectory 'missing-compile.json')) 'Compile-set omission'
foreach ($name in @('wrong-graph-pin.json','wrong-rsp-pin.json','stale-inventory-pin.json','missing-inventory-row.json','altered-inventory.json','missing-compile.json')) {
    if (Test-Path -LiteralPath (Join-Path $OutputDirectory $name)) { throw "Refusal path created output: $name" }
}
Write-Output 'PASS exact Compile + generated-source mapping and 6 fail-closed negatives; no ordinary-source byte or Csc-consumption claim.'
