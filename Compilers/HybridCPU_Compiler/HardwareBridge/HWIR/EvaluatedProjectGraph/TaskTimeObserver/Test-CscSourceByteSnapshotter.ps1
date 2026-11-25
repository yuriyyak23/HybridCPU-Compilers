param(
    [Parameter(Mandatory = $true)][string]$ToolDll,
    [Parameter(Mandatory = $true)][string]$DotnetExe,
    [Parameter(Mandatory = $true)][string]$FixtureRoot
)
$ErrorActionPreference = 'Stop'
foreach ($path in @($ToolDll, $DotnetExe)) {
    if (-not [IO.Path]::IsPathRooted($path) -or -not (Test-Path -LiteralPath $path)) { throw "Required absolute input missing: $path" }
}
if (-not [IO.Path]::IsPathRooted($FixtureRoot) -or (Test-Path -LiteralPath $FixtureRoot)) { throw 'FixtureRoot must be new and absolute.' }
$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
if (-not $FixtureRoot.StartsWith($temp + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'FixtureRoot must be below TEMP.' }
$null = New-Item -ItemType Directory -Path $FixtureRoot
$inputRoot = Join-Path $FixtureRoot 'inputs'
$sourceRoot = Join-Path $inputRoot 'Golden'
$projectDir = Join-Path $sourceRoot 'Project'
$generatedRoot = Join-Path $inputRoot 'Build'
New-Item -ItemType Directory -Path $projectDir,$generatedRoot -Force | Out-Null
$alpha = Join-Path $projectDir 'Alpha.cs'
$beta = Join-Path $projectDir 'Beta.cs'
$project = Join-Path $projectDir 'Fixture.csproj'
$generated = Join-Path $generatedRoot 'Generated.g.cs'
[IO.File]::WriteAllText($project, '<Project />', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($alpha, 'internal class Alpha {}', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($beta, 'internal class Beta {}', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($generated, 'internal class Generated {}', [Text.UTF8Encoding]::new($false))
$nodeId = "$project|Configuration=Release;EnableInternalTestHooks=true;IsGraphBuild=true"
$graphSha = 'a' * 64
function Hash-File([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }
function Manifest-SourceRow([string]$FullPath, [string]$RelativePath) {
    [ordered]@{ path=$RelativePath; bytes=(Get-Item -LiteralPath $FullPath).Length; sha256=(Hash-File $FullPath) }
}
$pre = Join-Path $inputRoot 'source-manifest-pre.json'
$post = Join-Path $inputRoot 'source-manifest-post.json'
$sourceRows = @((Manifest-SourceRow $alpha 'Project/Alpha.cs'),(Manifest-SourceRow $beta 'Project/Beta.cs'))
$sourceJson = ConvertTo-Json -InputObject $sourceRows -Depth 5 -Compress
[IO.File]::WriteAllText($pre, $sourceJson, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($post, $sourceJson, [Text.UTF8Encoding]::new($false))
$preSha = Hash-File $pre
$postSha = Hash-File $post

$mapRoot = Join-Path $inputRoot 'maps'
New-Item -ItemType Directory -Path $mapRoot | Out-Null
$mapFile = Join-Path $mapRoot 'Fixture.csproj.map.json'
$generatedFile = Join-Path $mapRoot 'generated-source-inventory.v1.json'
$inventory = [ordered]@{
    schema='hardwarebridge.csc-generated-source-inventory/v1'; disposition='snapshot-unqualified'; graphSha256=$graphSha
    files=@([ordered]@{ nodeId=$nodeId; project=$project; role='msbuild-generated'; path=$generated;
        bytes=(Get-Item -LiteralPath $generated).Length; sha256=(Hash-File $generated) })
}
[IO.File]::WriteAllText($generatedFile, (ConvertTo-Json $inventory -Depth 6 -Compress), [Text.UTF8Encoding]::new($false))
function Make-Map([string]$AlphaPath, [string]$BetaPath) {
    [ordered]@{
        schema='hardwarebridge.csc-source-argument-map/v1'; disposition='source-argument-map-unqualified'
        graphSha256=$graphSha; rspSha256=('b' * 64); generatedInventorySha256=(Hash-File $generatedFile)
        nodeId=$nodeId; project=$project; workingDirectory=$projectDir; sourceCount=3; generatedSourceCount=1
        claims=[ordered]@{ evaluatedCompileAndPinnedGeneratedSetMatched=$true; ordinarySourceBytesHashed=$false;
            cscReadOrConsumedResponse='NotEstablished'; completeCompilerInputClosure='NotEstablished'; admission='NotAdmitted' }
        sources=@(
            [ordered]@{ argumentIndex=1; token='Alpha.cs'; resolvedPath=$AlphaPath; evaluatedCompilePath=$AlphaPath; role='evaluated-compile'; generatedBytes=$null; generatedSha256=$null },
            [ordered]@{ argumentIndex=2; token='Beta.cs'; resolvedPath=$BetaPath; evaluatedCompilePath=$BetaPath; role='evaluated-compile'; generatedBytes=$null; generatedSha256=$null },
            [ordered]@{ argumentIndex=3; token=$generated; resolvedPath=$generated; evaluatedCompilePath=$null; role='msbuild-generated'; generatedBytes=(Get-Item -LiteralPath $generated).Length; generatedSha256=(Hash-File $generated) }
        )
    }
}
function Write-MapAndSet([string]$MappedAlpha, [string]$MappedBeta) {
    $mapObject = Make-Map $MappedAlpha $MappedBeta
    [IO.File]::WriteAllText($mapFile, (ConvertTo-Json $mapObject -Depth 8 -Compress), [Text.UTF8Encoding]::new($false))
    $mapBytes=(Get-Item -LiteralPath $mapFile).Length
    $generatedBytes=(Get-Item -LiteralPath $generatedFile).Length
    $set=[ordered]@{
        schema='circuitweave.csc-source-argument-map-snapshot/v1';
        files=@(
            [ordered]@{path='Fixture.csproj.map.json';bytes=$mapBytes;sha256=(Hash-File $mapFile)},
            [ordered]@{path='generated-source-inventory.v1.json';bytes=$generatedBytes;sha256=(Hash-File $generatedFile)}
        ); fileCount=2; totalBytes=($mapBytes+$generatedBytes)
    }
    $setPath=Join-Path $mapRoot 'manifest.json'
    [IO.File]::WriteAllText($setPath, (ConvertTo-Json $set -Depth 6 -Compress), [Text.UTF8Encoding]::new($false))
    return $setPath
}
$mapSet=Write-MapAndSet $alpha $beta
$generatedSha=Hash-File $generatedFile
$runId=[guid]::NewGuid().ToString('N')

function Invoke-Snapshot([string]$OutputName, [string]$SetPath, [string]$PrePath, [string]$PostPath, [int]$Rows=2, [string]$PrePin=$preSha) {
    $output=Join-Path $temp ($OutputName + '-' + $runId)
    & $DotnetExe $ToolDll snapshot $SetPath (Hash-File $SetPath) $sourceRoot $PrePath $PrePin $PostPath (Hash-File $PostPath) $generatedSha $Rows $output
    return @{ Exit=$LASTEXITCODE; Output=$output }
}
$positive=Invoke-Snapshot 'cw-source-byte-snapshot-positive-fixture' $mapSet $pre $post
if ($positive.Exit -ne 0) { throw "Positive snapshot failed: $($positive.Exit)" }
$snapshotManifest=Get-Content -LiteralPath (Join-Path $positive.Output 'snapshot-manifest.json') -Raw | ConvertFrom-Json
if ($snapshotManifest.schema -ne 'hardwarebridge.csc-source-byte-snapshot/v1' -or $snapshotManifest.files.Count -ne 3 -or
    $snapshotManifest.evidence.Count -ne 5 -or $snapshotManifest.claims.cscReadOrConsumedResponse -ne 'NotEstablished' -or
    $snapshotManifest.claims.mappedOrdinarySourceBytesMatchPinnedManifest -ne $true) {
    throw 'Positive snapshot manifest is incomplete or overclaims evidence.'
}

function Expect-NoPackage($Result, [string]$Label) {
    if ($Result.Exit -ne 2) { throw "$Label did not refuse with exit 2." }
    if (Test-Path -LiteralPath $Result.Output) { throw "$Label created an output package before refusing." }
}
$stale=Invoke-Snapshot 'cw-source-byte-snapshot-stale-pin' $mapSet $pre $post 2 ('0' * 64)
Expect-NoPackage $stale 'Stale source-manifest pin'

$alphaOriginal=[IO.File]::ReadAllBytes($alpha)
try { [IO.File]::WriteAllText($alpha,'internal class Alpha { int Drift; }',[Text.UTF8Encoding]::new($false)); $changedSource=Invoke-Snapshot 'cw-source-byte-snapshot-altered-source' $mapSet $pre $post; Expect-NoPackage $changedSource 'Altered ordinary source bytes' }
finally { [IO.File]::WriteAllBytes($alpha,$alphaOriginal) }

$generatedOriginal=[IO.File]::ReadAllBytes($generated)
try { [IO.File]::WriteAllText($generated,'internal class Generated { int Drift; }',[Text.UTF8Encoding]::new($false)); $changedGenerated=Invoke-Snapshot 'cw-source-byte-snapshot-altered-generated' $mapSet $pre $post; Expect-NoPackage $changedGenerated 'Altered generated source bytes' }
finally { [IO.File]::WriteAllBytes($generated,$generatedOriginal) }

$changedPath=Write-MapAndSet (Join-Path $projectDir 'Unlisted.cs') $beta
$alteredPath=Invoke-Snapshot 'cw-source-byte-snapshot-altered-path' $changedPath $pre $post
Expect-NoPackage $alteredPath 'Altered source path'
$mapSet=Write-MapAndSet $alpha $beta

$missingPre=Join-Path $inputRoot 'source-manifest-missing-pre.json'
$missingPost=Join-Path $inputRoot 'source-manifest-missing-post.json'
$missingJson=ConvertTo-Json -InputObject @((Manifest-SourceRow $alpha 'Project/Alpha.cs')) -Depth 5 -Compress
[IO.File]::WriteAllText($missingPre,$missingJson,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($missingPost,$missingJson,[Text.UTF8Encoding]::new($false))
$missing=Invoke-Snapshot 'cw-source-byte-snapshot-missing-row' $mapSet $missingPre $missingPost 1 (Hash-File $missingPre)
Expect-NoPackage $missing 'Missing source-manifest row'

Write-Output 'PASS snapshot package, stale pin, altered source/generated bytes/path, and missing row all fail closed before output.'
