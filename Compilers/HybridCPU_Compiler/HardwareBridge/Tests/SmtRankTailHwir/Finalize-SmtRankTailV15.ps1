param(
    [string]$ProducerRoot = (Join-Path $env:USERPROFILE 'Desktop\CircuitWeave-main'),
    [string]$Intermediate = (Join-Path $PSScriptRoot '..\..\.artifacts\smt-rank-tail-composed-v15'),
    [string]$Output = (Join-Path $ProducerRoot 'artifacts\processor-20261114\smt-rank-rtl-v15'),
    [string]$StudioRoot = (Join-Path $ProducerRoot 'artifacts\processor-20261114\smt-rank-collected-composition-v8')
)
$ErrorActionPreference = 'Stop'
$BridgeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$RunnerProject = Join-Path $PSScriptRoot 'SmtRankTailHwir.csproj'
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

$manifestPath = Join-Path $StudioRoot 'studio.project.json'
$manifestSha = Get-Sha256 $manifestPath
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$mlirArtifact = $manifest.artifacts | Where-Object { $_.id -eq 'smt-rank-tail-mlir' } | Select-Object -First 1
if ($null -eq $mlirArtifact) { throw 'Stable manifest has no MLIR artifact.' }
$snapshotId = ($mlirArtifact.relativePath -split '[\/]')[1]
$pins = Get-Content -LiteralPath (Join-Path $Intermediate 'smt-rank-tail-source-pins.json') -Raw | ConvertFrom-Json
$simulationPath = Join-Path $Output 'vvp-simulation.log'
$simulationText = Get-Content -LiteralPath $simulationPath -Raw
if (!$simulationText.Contains('PASS all directed RTL scenarios')) { throw 'RTL simulation log has no passing terminator.' }
if (!$simulationText.Contains('PASS null, ordinary and stealable NOP opportunities map to source masks')) { throw 'BundleOpportunityState RTL assertion missing.' }
if (!$simulationText.Contains('PASS non-stealable NOP remains occupied without reference opportunity')) { throw 'Non-stealable NOP RTL assertion missing.' }
if (!$simulationText.Contains('PASS eight-slot typed class occupancy derives from sampled slot tags')) { throw 'SlotClassCapacity RTL assertion missing.' }
$tools = Get-Content -LiteralPath (Join-Path $Output 'tool-hashes.txt') -Raw
foreach ($pin in @('187c0c0f0f1464a58997a3e99ca96560cc002b97276cc38c12c28addf0112c4e','a6681e0b577dcfc4ea83d1591a5714710b0b3535ebb36491ee8177553bab9631','e0a087a4906d5a9cd1b0b85c9b4c2081704c2c9bcd94f224437fc56b15fc440d')) { if (!$tools.Contains($pin)) { throw "Tool hash pin absent: $pin" } }
$receipt = [ordered]@{
    schema = 'circuitweave.smt-rank-tail-rtl-simulation/v3'
    status = 'candidate-rtl-simulation-passed'
    trust = 'StructuralOnly'
    source = [ordered]@{ studioProject = 'artifacts/processor-20261114/smt-rank-collected-composition-v8/studio.project.json'; manifestSha256 = $manifestSha; snapshotId = $snapshotId; modelCommit = $pins.modelCommit; modelGitHead = $pins.modelGitHead; treeState = $pins.modelTreeState; hwirSha256 = $pins.hwirSha256; mlirSha256 = $pins.mlirSha256; mlirInputSha256 = Get-Sha256 (Join-Path $Output 'smt-rank-tail.mlir') }
    tools = [ordered]@{ circtVersion = 'CIRCT firtool-1.160.0'; circtExecutableSha256 = '187c0c0f0f1464a58997a3e99ca96560cc002b97276cc38c12c28addf0112c4e'; iverilogVersion = 'Icarus Verilog version 12.0 (stable)'; iverilogExecutableSha256 = 'a6681e0b577dcfc4ea83d1591a5714710b0b3535ebb36491ee8177553bab9631'; vvpVersion = 'Icarus Verilog runtime version 12.0 (stable)'; vvpExecutableSha256 = 'e0a087a4906d5a9cd1b0b85c9b4c2081704c2c9bcd94f224437fc56b15fc440d'; environment = 'WSL2 Ubuntu' }
    commands = [ordered]@{ rtl = 'circt-opt --export-verilog smt-rank-tail.mlir -o /dev/null'; compile = 'iverilog -g2012 -s smt_rank_tail_directed_tb -o smt-rank-tail.vvp smt-rank-tail.sv smt-rank-tail.tb.sv'; simulate = 'vvp smt-rank-tail.vvp'; rtlExportExit = 0; compileExit = 0; simulationExit = 0 }
    semanticCrosscheck = [ordered]@{ status = $pins.semanticCrosscheck.status; vectors = $pins.semanticCrosscheck.vectors; scope = $pins.semanticCrosscheck.scope }
    artifacts = [ordered]@{ rtlSha256 = Get-Sha256 (Join-Path $Output 'smt-rank-tail.sv'); rtlBytes = (Get-Item (Join-Path $Output 'smt-rank-tail.sv')).Length; testbenchSha256 = Get-Sha256 (Join-Path $Output 'smt-rank-tail.tb.sv'); compileLogSha256 = Get-Sha256 (Join-Path $Output 'iverilog-compile.log'); simulationLogSha256 = Get-Sha256 $simulationPath; vcdSha256 = Get-Sha256 (Join-Path $Output 'smt-rank-tail.vcd'); simulationOutput = $simulationText }
    rtlAssertions = 57
    scenarioGroups = 14
    scenarioGroupDescriptions = @('A3 bit5 direct-gate and source-ordered rejection behavior','fair rank, collection, fourth-rank and duplicate retry','shared/register certificate and legality guard','bank-pending and bundle-local Store checks','projected-memory and A3 budget state','null/ordinary/stealable-NOP original slot masks','non-stealable NOP reference-opportunity behavior','accepted HardPinned lane6 commit mask update','Stage B lane6 rejection holds state','outer gate source-order priority','Assist bypass behavior','aggregate SmtRejectionsCount attribution','typed class occupancy derivation from eight source slot tags','other sampled admission and replay contracts')
    limitations = 'Combinational candidate projection only. Initial occupancy, empty-slot and reference-opportunity masks are derived from sampled eight-slot presence plus sampled NOP/stealable masks; these facts are not bound to the same live result array/runtime identity. Replay refresh and temporal scheduler lifecycle remain unmodeled. Six class occupancy counters and six lane-map totals are derived from the same eight sampled slot presence/class tuples; tuple identity is still unbound to live MicroOp references. Template state remains sampled. SmtRejectionsCount is modeled only for the typed non-Assist ordered four-candidate projection; Assist quota/backpressure, legacy untyped and FSP paths remain excluded. Same-owner and cross-owner direct A3 bit5 behavior is checked in the standalone gate; the source-ordered SpeculationBudgetExhaustionEvents counter is modeled from final Stage A reason. No Golden equivalence or cycle-level behavior is claimed.'
}
$receiptPath = Join-Path $Output 'smt-rank-tail-simulation-receipt.json'
[IO.File]::WriteAllText($receiptPath, (ConvertTo-Json -InputObject $receipt -Depth 20) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$publishFiles = @('smt-rank-tail.sv','smt-rank-tail.tb.sv','smt-rank-tail.vcd','smt-rank-tail-simulation-receipt.json','firtool-version.txt','firtool-export.log','iverilog-version.txt','vvp-version.txt','iverilog-compile.log','vvp-simulation.log','tool-hashes.txt')
foreach ($name in $publishFiles) { Copy-Item -LiteralPath (Join-Path $Output $name) -Destination (Join-Path $Intermediate $name) -Force }
Push-Location $BridgeRoot
try { dotnet run --no-restore --project $RunnerProject -- --publish $Intermediate $StudioRoot; if ($LASTEXITCODE -ne 0) { throw "Studio publication and consumer API check failed with exit $LASTEXITCODE." } }
finally { Pop-Location }



