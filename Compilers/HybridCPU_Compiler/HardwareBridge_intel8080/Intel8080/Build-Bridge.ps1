param(
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$bridgeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $bridgeRoot '.artifacts'))
if (-not $artifactRoot.StartsWith($bridgeRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Build artifact path escaped the target bridge directory.'
}

$projects = @(
    'Intel8080\HardwareBridge.Intel8080.csproj',
    'SemanticInventory\HardwareBridge.SemanticInventory.csproj',
    'HWIR\CscSemanticInventory\CscSemanticInventory.csproj',
    'HWIR\EvaluatedProjectGraph\EvaluatedProjectGraph.csproj',
    'HWIR\ImportInventory\ImportInventory.csproj',
    'HWIR\SemanticClosureAudit\SemanticClosureAudit.csproj',
    'ProjectInventory\HardwareBridge.Intel8080.ProjectInventory.csproj'
)

$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$logDirectory = Join-Path $artifactRoot "build-logs\bridge-$runId"
if (Test-Path -LiteralPath $logDirectory) {
    throw "Refusing to overwrite existing build log directory: $logDirectory"
}
New-Item -ItemType Directory -Path $logDirectory | Out-Null
$results = [Collections.Generic.List[object]]::new()

foreach ($relativeProject in $projects) {
    $projectPath = [IO.Path]::GetFullPath((Join-Path $bridgeRoot $relativeProject))
    if (-not $projectPath.StartsWith($bridgeRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Missing or out-of-scope target project: $relativeProject"
    }

    $name = [IO.Path]::GetFileNameWithoutExtension($projectPath)
    $logPath = Join-Path $logDirectory "$name.log"
    & dotnet build $projectPath --configuration $Configuration --nologo --verbosity:minimal *> $logPath
    $exitCode = $LASTEXITCODE
    $results.Add([pscustomobject]@{
        Project = $relativeProject
        ExitCode = $exitCode
        Log = $logPath
    })
}

$results | Format-Table -AutoSize
if (@($results | Where-Object ExitCode -ne 0).Count -gt 0) {
    throw "One or more target bridge builds failed. Logs: $logDirectory"
}
Write-Output "All $($results.Count) target projects built successfully. Logs: $logDirectory"
