param(
    [Parameter(Mandatory=$true)][string]$RootInventory,
    [Parameter(Mandatory=$true)][string]$ProjectGraph,
    [Parameter(Mandatory=$true)][string]$Output
)
$ErrorActionPreference = 'Stop'
$capturePath = [IO.Path]::GetFullPath($RootInventory)
$graphPath = [IO.Path]::GetFullPath($ProjectGraph)
$outPath = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $outPath) { throw 'Output already exists.' }
$capture = Get-Content -Raw -LiteralPath $capturePath | ConvertFrom-Json
$graph = Get-Content -Raw -LiteralPath $graphPath | ConvertFrom-Json
if ($capture.schema -ne 'circuitweave.msbuild-source-byte-inventory.v1' -or
    $graph.schema -ne 'hardwarebridge.msbuild-project-graph/v1' -or
    $graph.rootInventorySha256 -ne (Get-FileHash -LiteralPath $capturePath -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Invalid or mismatched capture and graph.' }
$modelRoot = [IO.Directory]::GetParent([IO.Path]::GetDirectoryName($capture.project)).FullName
$compilerRoot = [IO.Path]::GetFullPath('C:\Users\Yuriy\Desktop\HybridCPU Compilers')
foreach ($forbidden in @($modelRoot, $compilerRoot)) {
    if ($outPath.StartsWith($forbidden.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Output inside a protected source tree is forbidden.' }
}
$initial = [ordered]@{ Configuration = $capture.properties.Configuration; EnableInternalTestHooks = $capture.properties.EnableInternalTestHooks }
if ($initial.Configuration -ne 'Debug' -or $initial.EnableInternalTestHooks -ne 'true') { throw 'Expected Debug/EnableInternalTestHooks capture.' }
$queue = [Collections.Generic.Queue[object]]::new()
$queue.Enqueue([pscustomobject]@{ project=$capture.project; properties=$initial; via='<root>' })
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$rows = [Collections.Generic.List[object]]::new()
while ($queue.Count -gt 0) {
    $entry = $queue.Dequeue()
    $key = $entry.project + '|' + (($entry.properties.GetEnumerator() | Sort-Object Name | ForEach-Object { $_.Key + '=' + $_.Value }) -join ';')
    if (-not $seen.Add($key)) { continue }
    if ($seen.Count -gt 100) { throw 'Evaluation graph exceeds limit.' }
    $args = @('msbuild', $entry.project, '-getProperty:Configuration,TargetFramework,DefineConstants,LangVersion,Nullable,AssemblyName,EnableInternalTestHooks', '-getItem:Compile,ProjectReference,Using,Analyzer')
    foreach ($p in $entry.properties.GetEnumerator()) { $args += '-p:' + $p.Key + '=' + $p.Value }
    $raw = & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "Evaluation failed: $($entry.project)" }
    $evaluated = ($raw | Out-String | ConvertFrom-Json)
    $baseline = @($graph.projects | Where-Object { $_.project -eq $entry.project })
    if ($entry.via -ne '<root>' -and $baseline.Count -ne 1) { throw "Graph does not have exactly one baseline: $($entry.project)" }
    if ($baseline.Count -eq 1) {
        if ((Get-FileHash -LiteralPath $entry.project -Algorithm SHA256).Hash.ToLowerInvariant() -ne $baseline[0].projectSha256) { throw "Stale project bytes: $($entry.project)" }
        foreach ($file in $baseline[0].files) {
            $item = Get-Item -LiteralPath $file.path
            if ($item.Length -ne $file.length -or (Get-FileHash -LiteralPath $file.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Stale source bytes: $($file.path)" }
        }
        $old = @($baseline[0].files | ForEach-Object { $_.path } | Sort-Object)
        $new = @($evaluated.Items.Compile | ForEach-Object { $_.FullPath } | Sort-Object)
        $changes = @(Compare-Object $old $new | ForEach-Object { [ordered]@{ path=$_.InputObject; side=$_.SideIndicator } })
        $propertyChanges = @($baseline[0].properties.PSObject.Properties | Where-Object { $_.Value -ne $evaluated.Properties.($_.Name) } | ForEach-Object { [ordered]@{ name=$_.Name; baseline=$_.Value; evaluated=$evaluated.Properties.($_.Name) } })
        $oldUsings = @($baseline[0].globalUsings | ForEach-Object { $_.identity + '|' + $_.alias + '|' + $_.static } | Sort-Object)
        $newUsings = @($evaluated.Items.Using | ForEach-Object { $_.Identity + '|' + $_.Alias + '|' + $_.Static } | Sort-Object)
        $usingChanges = @(Compare-Object $oldUsings $newUsings | ForEach-Object { [ordered]@{ value=$_.InputObject; side=$_.SideIndicator } })
        $rows.Add([ordered]@{ project=$entry.project; via=$entry.via; propagatedGlobalProperties=$entry.properties; baselineProperties=$baseline[0].properties; evaluatedProperties=$evaluated.Properties; baselineCompileCount=$old.Count; evaluatedCompileCount=$new.Count; compileDifferences=$changes; propertyDifferences=$propertyChanges; usingDifferences=$usingChanges; baselineProjectAndSourceBytes='matched' })
    }
    foreach ($reference in $evaluated.Items.ProjectReference) {
        $child = [ordered]@{}
        foreach ($p in $entry.properties.GetEnumerator()) { $child[$p.Key]=$p.Value }
        foreach ($remove in @($reference.GlobalPropertiesToRemove -split ';' | Where-Object { $_ })) { $child.Remove($remove) }
        foreach ($metadata in @('SetConfiguration','SetPlatform','SetTargetFramework','AdditionalProperties')) {
            if ($reference.$metadata) { throw "Unsupported reference metadata $metadata on $($reference.FullPath); no exact graph claim." }
        }
        $queue.Enqueue([pscustomobject]@{ project=$reference.FullPath; properties=$child; via=$entry.project })
    }
}
$result = [ordered]@{
    schema='hardwarebridge.project-reference-property-comparison/v1'; disposition='partial-unqualified'
    rootInventorySha256=(Get-FileHash -LiteralPath $capturePath -Algorithm SHA256).Hash.ToLowerInvariant()
    projectGraphSha256=(Get-FileHash -LiteralPath $graphPath -Algorithm SHA256).Hash.ToLowerInvariant()
    scope='Independent MSBuild evaluations with two explicit global properties and evaluated GlobalPropertiesToRemove. Baseline project and source bytes are rechecked. Does not execute targets, prove target-framework negotiation, imported file freshness, generator or resolved-reference equivalence.'
    projects=@($rows)
}
[IO.File]::WriteAllText($outPath, ($result | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
Write-Output "Compared $($rows.Count) reference evaluations."
