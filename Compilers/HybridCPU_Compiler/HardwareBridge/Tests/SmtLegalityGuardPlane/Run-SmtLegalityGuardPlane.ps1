param(
    [string]$ProducerRoot = (Join-Path $env:USERPROFILE 'Desktop\CircuitWeave-main'),
    [string]$Distro = 'Ubuntu'
)
$ErrorActionPreference = 'Stop'
$BridgeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$RunnerProject = Join-Path $PSScriptRoot 'SmtLegalityGuardPlane.csproj'
$Intermediate = Join-Path $BridgeRoot '.artifacts\smt-legality-guard-plane'
$ArtifactRoot = Join-Path $ProducerRoot 'artifacts\processor-20261114\smt-legality-guard-plane-v1'
$FirtoolWindows = Join-Path $ProducerRoot 'artifacts\processor-20261114\power-control-candidate\tool\firtool-1.160.0\bin\circt-opt'
$FirtoolSha = '187c0c0f0f1464a58997a3e99ca96560cc002b97276cc38c12c28addf0112c4e'
$IverilogSha = 'a6681e0b577dcfc4ea83d1591a5714710b0b3535ebb36491ee8177553bab9631'
$VvpSha = 'e0a087a4906d5a9cd1b0b85c9b4c2081704c2c9bcd94f224437fc56b15fc440d'

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function To-WslPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).Replace('\', '/')
    if ($full -notmatch '^([A-Za-z]):/(.*)$') { throw "Expected drive-qualified path: $Path" }
    "/mnt/$($Matches[1].ToLowerInvariant())/$($Matches[2])"
}
function Invoke-Wsl([string]$Command) {
    & wsl.exe -d $Distro --exec /bin/bash -c $Command
    if ($LASTEXITCODE -ne 0) { throw "WSL command failed ($LASTEXITCODE): $Command" }
}
function Write-Wsl([string]$Command, [string]$Stdout, [string]$Stderr) {
    Invoke-Wsl "$Command > $(To-WslPath $Stdout) 2> $(To-WslPath $Stderr)"
}

if (!(Test-Path -LiteralPath $ProducerRoot -PathType Container)) { throw "Producer root missing: $ProducerRoot" }
if ((Get-Sha256 $FirtoolWindows) -ne $FirtoolSha) { throw 'Pinned firtool executable hash mismatch.' }
New-Item -ItemType Directory -Force -Path $Intermediate,$ArtifactRoot | Out-Null
Push-Location $BridgeRoot
try {
    dotnet run --project $RunnerProject -- $Intermediate
    if ($LASTEXITCODE -ne 0) { throw "HWIR/oracle runner failed: $LASTEXITCODE" }
} finally { Pop-Location }
$pins = Get-Content -LiteralPath (Join-Path $Intermediate 'smt-legality-guard-plane-source-pins.json') -Raw | ConvertFrom-Json
if ($pins.baseCommit -ne '1bfe41a49cf11c80fa6c6aee87accb54b78b331f' -or $pins.vectors -ne 12288 -or
    $pins.sources[0].sha256 -ne '89cb7e9f737815d9526bed00186c62f324ebaf805c1520a2ed5ebd1349b529a4' -or
    $pins.sources[1].sha256 -ne 'f87ba64048e2d0a22642b254fe3eebd696553d3b8b4355b831b32ca77acbbf16') { throw 'Source/model pin validation failed.' }
foreach ($name in @('smt-legality-guard-plane.mlir','smt-legality-guard-plane.tb.sv','smt-legality-guard-plane.hwir.json','smt-legality-guard-plane-source-pins.json','smt-legality-guard-plane-source-inventory.json','smt-legality-guard-plane-captured-sources.json','smt-legality-guard-plane-source-capture-index.json','smt-legality-guard-plane-semantic-crosscheck.json')) {
    Copy-Item -LiteralPath (Join-Path $Intermediate $name) -Destination (Join-Path $ArtifactRoot $name) -Force
}
$wslOut = To-WslPath $ArtifactRoot
$wslFirtool = To-WslPath $FirtoolWindows
Write-Wsl "$wslFirtool --version" (Join-Path $ArtifactRoot 'firtool-version.txt') (Join-Path $ArtifactRoot 'firtool-version.stderr.txt')
Write-Wsl "$wslFirtool --export-verilog $wslOut/smt-legality-guard-plane.mlir -o /dev/null" (Join-Path $ArtifactRoot 'smt-legality-guard-plane.sv') (Join-Path $ArtifactRoot 'firtool-export.log')
Write-Wsl '/usr/bin/iverilog -V' (Join-Path $ArtifactRoot 'iverilog-version.txt') (Join-Path $ArtifactRoot 'iverilog-version.stderr.txt')
Write-Wsl '/usr/bin/vvp -V' (Join-Path $ArtifactRoot 'vvp-version.txt') (Join-Path $ArtifactRoot 'vvp-version.stderr.txt')
Invoke-Wsl "/usr/bin/iverilog -g2012 -s smt_legality_guard_plane_tb -o $wslOut/smt-legality-guard-plane.vvp $wslOut/smt-legality-guard-plane.sv $wslOut/smt-legality-guard-plane.tb.sv > $wslOut/iverilog-compile.log 2>&1"
Invoke-Wsl "/usr/bin/vvp $wslOut/smt-legality-guard-plane.vvp > $wslOut/vvp-simulation.log 2>&1"
Invoke-Wsl "sha256sum $wslFirtool /usr/bin/iverilog /usr/bin/vvp > $wslOut/tool-hashes.txt"
$firtoolText = (Get-Content (Join-Path $ArtifactRoot 'firtool-version.txt') -Raw) + (Get-Content (Join-Path $ArtifactRoot 'firtool-version.stderr.txt') -Raw)
$iverilogText = (Get-Content (Join-Path $ArtifactRoot 'iverilog-version.txt') -Raw) + (Get-Content (Join-Path $ArtifactRoot 'iverilog-version.stderr.txt') -Raw)
$vvpText = (Get-Content (Join-Path $ArtifactRoot 'vvp-version.txt') -Raw) + (Get-Content (Join-Path $ArtifactRoot 'vvp-version.stderr.txt') -Raw)
$simulation = Get-Content (Join-Path $ArtifactRoot 'vvp-simulation.log') -Raw
if (!$firtoolText.Contains('CIRCT firtool-1.160.0') -or !$iverilogText.Contains('Icarus Verilog version 12.0 (stable)') -or
    !$vvpText.Contains('Icarus Verilog runtime version 12.0 (stable)') -or !$simulation.Contains('PASS SMT legality guard-plane vectors: 12288')) {
    throw 'Pinned tool version or RTL simulation assertion failed.'
}
$toolHashes = Get-Content (Join-Path $ArtifactRoot 'tool-hashes.txt') -Raw
foreach ($pin in @($FirtoolSha,$IverilogSha,$VvpSha)) { if (!$toolHashes.Contains($pin)) { throw "Tool hash pin absent: $pin" } }
$receipt = [ordered]@{
    schema='circuitweave.smt-legality-guard-plane-rtl-simulation/v1'; status='candidate-rtl-simulation-passed'; trust='StructuralOnly'
    model=[ordered]@{commit=$pins.baseCommit;treeState=$pins.treeState;sourcePinsSha256=(Get-Sha256 (Join-Path $ArtifactRoot 'smt-legality-guard-plane-source-pins.json'));hwirSha256=$pins.hwirSha256;hwirArtifactSha256=$pins.hwirArtifactSha256;mlirSha256=$pins.mlirSha256;vectors=$pins.vectors}
    tools=[ordered]@{circtVersion='CIRCT firtool-1.160.0';circtExecutableSha256=$FirtoolSha;iverilogVersion='Icarus Verilog version 12.0 (stable)';iverilogExecutableSha256=$IverilogSha;vvpVersion='Icarus Verilog runtime version 12.0 (stable)';vvpExecutableSha256=$VvpSha;environment="WSL2 $Distro"}
    commands=[ordered]@{rtl='circt-opt --export-verilog smt-legality-guard-plane.mlir -o /dev/null';compile='iverilog -g2012 -s smt_legality_guard_plane_tb -o smt-legality-guard-plane.vvp smt-legality-guard-plane.sv smt-legality-guard-plane.tb.sv';simulate='vvp smt-legality-guard-plane.vvp';rtlExportExit=0;compileExit=0;simulationExit=0}
    artifacts=[ordered]@{rtlSha256=(Get-Sha256 (Join-Path $ArtifactRoot 'smt-legality-guard-plane.sv'));rtlBytes=(Get-Item (Join-Path $ArtifactRoot 'smt-legality-guard-plane.sv')).Length;testbenchSha256=(Get-Sha256 (Join-Path $ArtifactRoot 'smt-legality-guard-plane.tb.sv'));simulationLogSha256=(Get-Sha256 (Join-Path $ArtifactRoot 'vvp-simulation.log'));simulationOutput=$simulation}
    scope='Boundary, owner-context and domain guard priority plus replay-vs-structural selection gating. Template equality and selected certificate result are explicit inputs; no phase-cache refresh, physical timing or Golden equivalence.'
}
[IO.File]::WriteAllText((Join-Path $ArtifactRoot 'smt-legality-guard-plane-simulation-receipt.json'),(ConvertTo-Json -InputObject $receipt -Depth 16)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
foreach ($name in @('smt-legality-guard-plane.sv','smt-legality-guard-plane.tb.sv','smt-legality-guard-plane-simulation-receipt.json','firtool-version.txt','firtool-export.log','iverilog-version.txt','vvp-version.txt','iverilog-compile.log','vvp-simulation.log','tool-hashes.txt')) {
    Copy-Item -LiteralPath (Join-Path $ArtifactRoot $name) -Destination (Join-Path $Intermediate $name) -Force
}
Push-Location $BridgeRoot
try {
    dotnet run --project $RunnerProject -- --publish $Intermediate $ArtifactRoot
    if ($LASTEXITCODE -ne 0) { throw "Studio publication/API validation failed: $LASTEXITCODE" }
} finally { Pop-Location }
Write-Output "Candidate legality guard-plane RTL passed 12,288 Icarus vectors and published to $ArtifactRoot"
