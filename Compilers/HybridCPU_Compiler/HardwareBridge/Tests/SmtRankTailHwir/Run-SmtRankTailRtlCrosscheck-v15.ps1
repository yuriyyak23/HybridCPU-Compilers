param(
    [string]$ProducerRoot = (Join-Path $env:USERPROFILE 'Desktop\CircuitWeave-main'),
    [string]$Distro = 'Ubuntu'
)
$ErrorActionPreference = 'Stop'
$BridgeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$RunnerProject = Join-Path $PSScriptRoot 'SmtRankTailHwir.csproj'
$Intermediate = Join-Path $BridgeRoot '.artifacts\smt-rank-tail-composed-v15'
$ArtifactRoot = Join-Path $ProducerRoot 'artifacts\processor-20261114'
$StudioRoot = Join-Path $ArtifactRoot 'smt-rank-collected-composition-v8'
$Output = Join-Path $ArtifactRoot 'smt-rank-rtl-v15'
$FirtoolWindows = Join-Path $ArtifactRoot 'power-control-candidate\tool\firtool-1.160.0\bin\circt-opt'
$FirtoolSha = '187c0c0f0f1464a58997a3e99ca96560cc002b97276cc38c12c28addf0112c4e'
$IverilogSha = 'a6681e0b577dcfc4ea83d1591a5714710b0b3535ebb36491ee8177553bab9631'
$VvpSha = 'e0a087a4906d5a9cd1b0b85c9b4c2081704c2c9bcd94f224437fc56b15fc440d'

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function To-WslPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).Replace('\', '/')
    if ($full -notmatch '^([A-Za-z]):/(.*)$') { throw "Expected drive-qualified Windows path: $Path" }
    return "/mnt/$($Matches[1].ToLowerInvariant())/$($Matches[2])"
}
function Invoke-Wsl([string]$Command) {
    & wsl.exe -d $Distro --exec /bin/bash -c $Command
    if ($LASTEXITCODE -ne 0) { throw "WSL command failed with exit $LASTEXITCODE`: $Command" }
}
function Write-Wsl([string]$Command, [string]$Stdout, [string]$Stderr) {
    $cmd = "$Command > $(To-WslPath $Stdout) 2> $(To-WslPath $Stderr)"
    Invoke-Wsl $cmd
}

if (!(Test-Path -LiteralPath $ProducerRoot -PathType Container)) { throw "Producer root missing: $ProducerRoot" }
New-Item -ItemType Directory -Force -Path $StudioRoot | Out-Null
if ((Get-Sha256 $FirtoolWindows) -ne $FirtoolSha) { throw 'Pinned firtool executable hash mismatch.' }
New-Item -ItemType Directory -Force -Path $Intermediate | Out-Null
foreach ($staleName in @('smt-rank-tail.sv','smt-rank-tail.vcd','smt-rank-tail-simulation-receipt.json','firtool-version.txt','firtool-version.stderr.txt','firtool-export.log','iverilog-version.txt','iverilog-version.stderr.txt','vvp-version.txt','vvp-version.stderr.txt','iverilog-compile.log','vvp-simulation.log','tool-hashes.txt','smt-rank-tail.vvp')) {
    $stalePath = Join-Path $Intermediate $staleName
    if (Test-Path -LiteralPath $stalePath -PathType Leaf) { Remove-Item -LiteralPath $stalePath -Force }
}

Push-Location $BridgeRoot
try {
    dotnet run --project $RunnerProject -- $Intermediate
    if ($LASTEXITCODE -ne 0) { throw "HWIR build/oracle runner failed with exit $LASTEXITCODE." }
} finally { Pop-Location }
$pins = Get-Content -LiteralPath (Join-Path $Intermediate 'smt-rank-tail-source-pins.json') -Raw | ConvertFrom-Json
if ($pins.mlirSha256 -ne (Get-Sha256 (Join-Path $Intermediate 'smt-rank-tail.mlir')) -or $pins.modelGitHead -ne '1bfe41a49cf11c80fa6c6aee87accb54b78b331f') { throw 'Generated design or model HEAD differs from captured source pins.' }
Push-Location $BridgeRoot
try {
    dotnet run --project $RunnerProject -- --publish $Intermediate $StudioRoot
    if ($LASTEXITCODE -ne 0) { throw "Initial immutable source snapshot publication and Studio API check failed with exit $LASTEXITCODE." }
} finally { Pop-Location }
$manifestPath = Join-Path $StudioRoot 'studio.project.json'
$sourceManifestSha = Get-Sha256 $manifestPath
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$mlirRecord = $manifest.artifacts | Where-Object { $_.id -eq 'smt-rank-tail-mlir' } | Select-Object -First 1
if ($null -eq $mlirRecord) { throw 'Published Studio snapshot has no generated SMT MLIR artifact.' }
$SnapshotId = ($mlirRecord.relativePath -split '[\\/]')[1]
$InputSnapshot = Join-Path (Join-Path $StudioRoot 'snapshots') $SnapshotId
$mlirArtifact = Join-Path $InputSnapshot 'smt-rank-tail.mlir'
if ((Get-Sha256 $mlirArtifact) -ne $pins.mlirSha256) { throw 'Published immutable snapshot MLIR differs from generated candidate.' }
New-Item -ItemType Directory -Force -Path $Output | Out-Null
Copy-Item -LiteralPath $mlirArtifact -Destination (Join-Path $Output 'smt-rank-tail.mlir') -Force
Copy-Item -LiteralPath (Join-Path $Intermediate 'smt-rank-tail.tb.sv') -Destination (Join-Path $Output 'smt-rank-tail.tb.sv') -Force

$wslOut = To-WslPath $Output
$wslFirtool = To-WslPath $FirtoolWindows
Write-Wsl "$wslFirtool --version" (Join-Path $Output 'firtool-version.txt') (Join-Path $Output 'firtool-version.stderr.txt')
Write-Wsl "$wslFirtool --export-verilog $wslOut/smt-rank-tail.mlir -o /dev/null" (Join-Path $Output 'smt-rank-tail.sv') (Join-Path $Output 'firtool-export.log')
Write-Wsl '/usr/bin/iverilog -V' (Join-Path $Output 'iverilog-version.txt') (Join-Path $Output 'iverilog-version.stderr.txt')
Write-Wsl '/usr/bin/vvp -V' (Join-Path $Output 'vvp-version.txt') (Join-Path $Output 'vvp-version.stderr.txt')
Invoke-Wsl "/usr/bin/iverilog -g2012 -s smt_rank_tail_directed_tb -o $wslOut/smt-rank-tail.vvp $wslOut/smt-rank-tail.sv $wslOut/smt-rank-tail.tb.sv > $wslOut/iverilog-compile.log 2>&1"
Invoke-Wsl "cd $wslOut && /usr/bin/vvp ./smt-rank-tail.vvp > ./vvp-simulation.log 2>&1"
Invoke-Wsl "sha256sum $wslFirtool /usr/bin/iverilog /usr/bin/vvp > $wslOut/tool-hashes.txt"
$firtoolText = (Get-Content -LiteralPath (Join-Path $Output 'firtool-version.txt') -Raw) + (Get-Content -LiteralPath (Join-Path $Output 'firtool-version.stderr.txt') -Raw)
Write-Output 'v15 receipt step: read firtool version'
$iverilogText = (Get-Content -LiteralPath (Join-Path $Output 'iverilog-version.txt') -Raw) + (Get-Content -LiteralPath (Join-Path $Output 'iverilog-version.stderr.txt') -Raw)
Write-Output 'v15 receipt step: read iverilog version'
$vvpText = (Get-Content -LiteralPath (Join-Path $Output 'vvp-version.txt') -Raw) + (Get-Content -LiteralPath (Join-Path $Output 'vvp-version.stderr.txt') -Raw)
Write-Output 'v15 receipt step: read vvp version'
$simulationText = Get-Content -LiteralPath (Join-Path $Output 'vvp-simulation.log') -Raw
if (!(Test-Path -LiteralPath (Join-Path $Output 'smt-rank-tail.vcd')) -or (Get-Item -LiteralPath (Join-Path $Output 'smt-rank-tail.vcd')).Length -eq 0) { throw 'Directed simulation waveform missing.' }
Write-Output 'v15 receipt step: read simulation log'
if (!$firtoolText.Contains('CIRCT firtool-1.160.0') -or !$iverilogText.Contains('Icarus Verilog version 12.0 (stable)') -or
    !$vvpText.Contains('Icarus Verilog runtime version 12.0 (stable)') -or !$simulationText.Contains('PASS all directed RTL scenarios')) {
    throw 'RTL tool version, simulation output, or one or more directed assertions failed.'
}
$toolHashes = Get-Content -LiteralPath (Join-Path $Output 'tool-hashes.txt') -Raw
Write-Output 'v15 receipt step: read tool hashes'
foreach ($pin in @($FirtoolSha, $IverilogSha, $VvpSha)) { if (!$toolHashes.Contains($pin)) { throw "Tool hash pin absent: $pin" } }
Write-Output 'v15 receipt step: tool hashes passed'
& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Finalize-SmtRankTailV15.ps1') -ProducerRoot $ProducerRoot -Intermediate $Intermediate -Output $Output -StudioRoot $StudioRoot
if ($LASTEXITCODE -ne 0) { throw "Final receipt creation and Studio publication failed with exit $LASTEXITCODE." }
Write-Output "Candidate RTL and directed Icarus scenarios passed; published to $StudioRoot"



