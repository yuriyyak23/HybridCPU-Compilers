param(
    [string]$ProducerRoot = 'C:\Users\Yuriy\Desktop\CircuitWeave-main',
    [string]$OutputDirectory = 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\processor-20261114'
)
$ErrorActionPreference = 'Stop'
$inventoryRoot = Join-Path $OutputDirectory 'inventory-refresh-v1'
$inventoryIndexPath = Join-Path $inventoryRoot 'coverage-index-v41-fresh.json'
$semanticReportPath = Join-Path $inventoryRoot 'semantic-inventory-v2-fresh.json'
$sourceCaptureIndexPath = Join-Path $inventoryRoot 'source-bytes.json'
$projectGraphPath = Join-Path $inventoryRoot 'project-graph.json'
$inventoryPath = Join-Path $OutputDirectory 'trap-carrier-projection-v8-source-inventory.json'
$capturedSourcesPath = Join-Path $OutputDirectory 'trap-carrier-projection-v8-captured-sources.json'
function Sha([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }
$indexBytes = [IO.File]::ReadAllBytes($inventoryIndexPath)
$reportBytes = [IO.File]::ReadAllBytes($semanticReportPath)
$captureIndexBytes = [IO.File]::ReadAllBytes($sourceCaptureIndexPath)
$graphBytes = [IO.File]::ReadAllBytes($projectGraphPath)
$index = [Text.Encoding]::UTF8.GetString($indexBytes) | ConvertFrom-Json
$report = [Text.Encoding]::UTF8.GetString($reportBytes) | ConvertFrom-Json
$captureIndex = [Text.Encoding]::UTF8.GetString($captureIndexBytes) | ConvertFrom-Json
$graph = [Text.Encoding]::UTF8.GetString($graphBytes) | ConvertFrom-Json
if ($index.schema -ne 'circuitweave.source-coverage-index/v1' -or $index.disposition -ne 'incomplete-unqualified' -or
    $report.schema -ne 'hardwarebridge.semantic-inventory/v1' -or $report.disposition -ne 'incomplete-unqualified' -or
    $captureIndex.schema -ne 'circuitweave.msbuild-source-byte-inventory.v1' -or
    $graph.schema -ne 'hardwarebridge.msbuild-project-graph/v1' -or
    $index.semanticReportSha256 -ne (Sha $reportBytes) -or
    $report.sourceInventorySha256 -ne (Sha $captureIndexBytes) -or
    $report.projectGraphSha256 -ne (Sha $graphBytes) -or
    $captureIndex.projectSha256 -ne (Sha ([IO.File]::ReadAllBytes($captureIndex.project))) -or
    $graph.rootInventorySha256 -ne (Sha $captureIndexBytes) -or
    $index.project -ne $captureIndex.project -or $index.configuration -ne $captureIndex.properties.Configuration) {
    throw 'Coverage index/report/source capture/project graph identities do not match.'
}
if ($index.symbols.Count -ne $report.types.Count -or $captureIndex.files.Count -ne $report.files.Count) {
    throw 'Coverage index does not contain the complete semantic report projection or source file set.'
}
$captured = [Collections.Generic.List[object]]::new()
foreach ($file in $captureIndex.files) {
    $bytes = [IO.File]::ReadAllBytes($file.path)
    if ($bytes.LongLength -ne $file.length -or (Sha $bytes) -ne $file.sha256 -or $file.status -ne 'captured') {
        throw "Active source changed from the MSBuild capture: $($file.path)"
    }
    $captured.Add([ordered]@{ path = $file.path; bytes = [Convert]::ToBase64String($bytes) })
}
if ($captured.Count -ne 1135) { throw "Unexpected active root source count: $($captured.Count)" }
$projectDirectory = Split-Path $captureIndex.project -Parent
$iseRoot = Split-Path $projectDirectory -Parent
$globalJsonPath = Join-Path $iseRoot 'global.json'
$globalJsonBytes = [IO.File]::ReadAllBytes($globalJsonPath)
$globalJson = [Text.Encoding]::UTF8.GetString($globalJsonBytes) | ConvertFrom-Json
Push-Location $iseRoot
try { $sdkResolved = (& dotnet --version).Trim() }
finally { Pop-Location }
$projectGraph = [Text.Encoding]::UTF8.GetString($graphBytes) | ConvertFrom-Json
$projectEvaluation = [ordered]@{
    project = $captureIndex.project
    projectSha256 = Sha ([IO.File]::ReadAllBytes($captureIndex.project))
    configuration = $captureIndex.properties.Configuration
    targetFramework = $captureIndex.properties.TargetFramework
    defineConstants = $captureIndex.properties.DefineConstants
    defines = @($captureIndex.properties.DefineConstants -split ';' | Where-Object { $_ })
    langVersion = $captureIndex.properties.LangVersion
    nullable = $captureIndex.properties.Nullable
    implicitUsings = $captureIndex.properties.ImplicitUsings
    enableInternalTestHooks = $captureIndex.properties.EnableInternalTestHooks
    assemblyName = $captureIndex.properties.AssemblyName
    requestedSdkVersion = $globalJson.sdk.version
    resolvedSdkVersion = $sdkResolved
    globalJsonSha256 = Sha $globalJsonBytes
    directProjectReferences = @($captureIndex.projectReferences)
    evaluatedProjectGraph = @($projectGraph.projects | ForEach-Object {
        [ordered]@{ project = $_.project; projectSha256 = $_.projectSha256; targetFramework = $_.properties.TargetFramework;
            defineConstants = $_.properties.DefineConstants; projectReferences = @($_.projectReferences) }
    })
    generatorOutputs = 'not-enumerated-by-this-source-coverage-index'
}
$index | Add-Member -NotePropertyName targetFramework -NotePropertyValue $captureIndex.properties.TargetFramework -Force
$index | Add-Member -NotePropertyName defines -NotePropertyValue @($captureIndex.properties.DefineConstants -split ';' | Where-Object { $_ }) -Force
$index | Add-Member -NotePropertyName sdk -NotePropertyValue ([ordered]@{ requested = $globalJson.sdk.version; resolved = $sdkResolved; globalJsonSha256 = Sha $globalJsonBytes }) -Force
$index | Add-Member -NotePropertyName projectEvaluation -NotePropertyValue $projectEvaluation -Force
$index | Add-Member -NotePropertyName capturedSourceFiles -NotePropertyValue @($captureIndex.files | ForEach-Object {
    [ordered]@{ path = $_.path; sha256 = $_.sha256; length = [long]$_.length }
}) -Force
$index | Add-Member -NotePropertyName captureVerification -NotePropertyValue ([ordered]@{
    schema = 'circuitweave.source-capture-check/v1'
    status = 'producer-reported-listed-inputs-match'
    semanticReportSha256 = $index.semanticReportSha256
    sourceCaptureSha256 = Sha $captureIndexBytes
    projectGraphSha256 = Sha $graphBytes
    scope = 'All 1,135 root project source paths in the MSBuild source-byte inventory were re-read and SHA-256 checked for this publication.'
    rootProject = 'matched'
    checkedFiles = $captured.Count
}) -Force
$inventoryJson = ConvertTo-Json -InputObject $index -Depth 100 -Compress
[IO.File]::WriteAllText($inventoryPath, $inventoryJson, [Text.UTF8Encoding]::new($false))
$inventorySha = Sha ([IO.File]::ReadAllBytes($inventoryPath))
$capturedDocument = [ordered]@{ schema = 'circuitweave.captured-sources/v1'; inventorySha256 = $inventorySha; files = $captured }
$captureJson = ConvertTo-Json -InputObject $capturedDocument -Depth 6 -Compress
[IO.File]::WriteAllText($capturedSourcesPath, $captureJson, [Text.UTF8Encoding]::new($false))
[pscustomobject]@{
    status = 'PASS'
    inventory = $inventoryPath
    inventorySha256 = $inventorySha
    capturedSources = $capturedSourcesPath
    capturedFiles = $captured.Count
    activeSourceBytes = ($captureIndex.files | Measure-Object -Property length -Sum).Sum
    disposition = 'incomplete-unqualified'
}
