param(
    [Parameter(Mandatory=$true)][string]$RootInventory,
    [Parameter(Mandatory=$true)][string]$Output
)
$ErrorActionPreference = 'Stop'
$inputPath = [IO.Path]::GetFullPath($RootInventory)
$outputPath = [IO.Path]::GetFullPath($Output)
$rootData = Get-Content -Raw -LiteralPath $inputPath | ConvertFrom-Json
if ($rootData.schema -ne 'circuitweave.msbuild-source-byte-inventory.v1') { throw 'Invalid root inventory' }
$modelRoot = [IO.Directory]::GetParent([IO.Path]::GetDirectoryName($rootData.project)).FullName
if ($outputPath.StartsWith($modelRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Output inside model repository forbidden' }
if (Test-Path -LiteralPath $outputPath) { throw 'Output already exists' }
$queue = [Collections.Generic.Queue[string]]::new()
foreach ($p in $rootData.projectReferences) { $queue.Enqueue([IO.Path]::GetFullPath($p)) }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$projects = [Collections.Generic.List[object]]::new()
while ($queue.Count -gt 0) {
    $project = $queue.Dequeue()
    if (-not $seen.Add($project)) { continue }
    $raw = & dotnet msbuild $project '-getProperty:Configuration,TargetFramework,DefineConstants,LangVersion,Nullable,AssemblyName' '-getItem:Compile,ProjectReference,Using,Analyzer' '-p:Configuration=Debug'
    if ($LASTEXITCODE -ne 0) { throw "MSBuild evaluation failed: $project" }
    $data = ($raw | Out-String | ConvertFrom-Json)
    $files = @($data.Items.Compile | ForEach-Object {
        $path = $_.FullPath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing Compile source $path" }
        [ordered]@{ path=$path; length=(Get-Item -LiteralPath $path).Length; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $references = @($data.Items.ProjectReference | ForEach-Object { $_.FullPath })
    foreach ($p in $references) { $queue.Enqueue($p) }
    $projects.Add([ordered]@{
        project=$project; projectSha256=(Get-FileHash -LiteralPath $project -Algorithm SHA256).Hash.ToLowerInvariant()
        properties=$data.Properties; files=$files; projectReferences=$references
        globalUsings=@($data.Items.Using | ForEach-Object { [ordered]@{ identity=$_.Identity; alias=$_.Alias; static=$_.Static } })
        analyzers=@($data.Items.Analyzer | ForEach-Object { $_.FullPath })
    })
}
$result = [ordered]@{ schema='hardwarebridge.msbuild-project-graph/v1'; disposition='independent-evaluations-unqualified'; rootInventorySha256=(Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash.ToLowerInvariant(); projects=@($projects) }
[IO.File]::WriteAllText($outputPath, ($result | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
Write-Output "Captured $($projects.Count) evaluated referenced projects. ProjectReference global properties and generators still require qualification."
