param(
  [string]$ProjectPath = 'C:\Users\Yuriy\Desktop\8080-emulator-net-master\emu8080.Core\emu8080.Core.csproj',
  [string]$SdkDirectory,
  [string]$OutputFile = 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\project-inventory.json'
)
$ErrorActionPreference = 'Stop'
$allowedRoot = [IO.Path]::GetFullPath('C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080').TrimEnd('\') + '\'
$output = [IO.Path]::GetFullPath($OutputFile)
if (-not $output.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "8080 project inventory must be written inside: $allowedRoot" }
if (Test-Path -LiteralPath $output) { throw "Existing output refused: $output" }
if ([string]::IsNullOrWhiteSpace($SdkDirectory)) {
  $sdkRoot = (& dotnet --info | Select-String '^ Base Path:' | Select-Object -First 1).ToString().Split(':',2)[1].Trim()
  if (-not $sdkRoot) { throw 'Could not determine installed .NET SDK Base Path.' }
  $SdkDirectory = $sdkRoot
}
$project = Join-Path $PSScriptRoot '..\ProjectInventory\HardwareBridge.Intel8080.ProjectInventory.csproj'
& dotnet run --project $project -- $ProjectPath $SdkDirectory $output
if ($LASTEXITCODE -ne 0) { throw "Project inventory failed with exit code $LASTEXITCODE" }
Write-Output "Created: $output"
