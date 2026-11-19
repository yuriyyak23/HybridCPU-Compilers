param(
  [Parameter(Mandatory=$true)][string]$ProjectionPath,
  [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$allowedRoot = [IO.Path]::GetFullPath('C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080').TrimEnd('\') + '\'
$projection = [IO.Path]::GetFullPath($ProjectionPath)
$output = [IO.Path]::GetFullPath($OutputDirectory)
foreach($path in @($projection,$output)) {
  if(-not $path.StartsWith($allowedRoot,[StringComparison]::OrdinalIgnoreCase)) { throw "Path must remain under artifacts/8080: $path" }
  for($d=[IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($path)); $null -ne $d; $d=$d.Parent) {
    if((Test-Path -LiteralPath $d.FullName) -and (($d.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw "Linked path refused: $($d.FullName)" }
  }
}
if(-not (Test-Path -LiteralPath $projection -PathType Leaf)) { throw "ADD projection not found: $projection" }
if(Test-Path -LiteralPath $output) { throw "Existing output refused: $output" }
$project = Join-Path $PSScriptRoot 'HardwareBridge.Intel8080.AddMlir.csproj'
& dotnet run --project $project -- $projection $output
if($LASTEXITCODE -ne 0) { throw "CircuitWeave scalar ADD export failed with exit code $LASTEXITCODE" }
