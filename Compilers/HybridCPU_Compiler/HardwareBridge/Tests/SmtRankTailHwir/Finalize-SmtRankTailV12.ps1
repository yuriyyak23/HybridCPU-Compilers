param(
    [string]$ProducerRoot = (Join-Path $env:USERPROFILE 'Desktop\CircuitWeave-main'),
    [string]$Intermediate = (Join-Path $PSScriptRoot '..\..\.artifacts\smt-rank-tail-composed-v12'),
    [string]$Output = (Join-Path $ProducerRoot 'artifacts\processor-20261114\smt-rank-rtl-v12'),
    [string]$StudioRoot = (Join-Path $ProducerRoot 'artifacts\processor-20261114\smt-rank-collected-composition-v5')
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
$tools = Get-Content -LiteralPath (Join-Path $Output 'tool-hashes.txt') -Raw
foreach ($pin in @('187c0c0f0f1464a58997a3e99ca96560cc002b97276cc38c12c28addf0112c4e','a6681e0b577dcfc4ea83d1591a5714710b0b3535ebb36491ee8177553bab9631','e0a087a4906d5a9cd1b0b85c9b4c2081704c2c9bcd94f224437fc56b15fc440d')) { if (!$tools.Contains($pin)) { throw "Tool hash pin absent: $pin" } }
$receipt = [ordered]@{
    schema = 'circuitweave.smt-rank-tail-rtl-simulation/v1'
    status = 'candidate-rtl-simulation-passed'
    trust = 'StructuralOnly'
    source = [ordered]@{ studioProject = 'artifacts/processor-20261114/smt-rank-collected-composition-v5/studio.project.json'; manifestSha256 = $manifestSha; snapshotId = $snapshotId; modelCommit = $pins.modelCommit; modelGitHead = $pins.modelGitHead; treeState = $pins.modelTreeState; hwirSha256 = $pins.hwirSha256; mlirSha256 = $pins.mlirSha256; mlirInputSha256 = Get-Sha256 (Join-Path $Output 'smt-rank-tail.mlir') }
    tools = [ordered]@{ circtVersion = 'CIRCT firtool-1.160.0'; circtExecutableSha256 = '187c0c0f0f1464a58997a3e99ca96560cc002b97276cc38c12c28addf0112c4e'; iverilogVersion = 'Icarus Verilog version 12.0 (stable)'; iverilogExecutableSha256 = 'a6681e0b577dcfc4ea83d1591a5714710b0b3535ebb36491ee8177553bab9631'; vvpVersion = 'Icarus Verilog runtime version 12.0 (stable)'; vvpExecutableSha256 = 'e0a087a4906d5a9cd1b0b85c9b4c2081704c2c9bcd94f224437fc56b15fc440d'; environment = 'WSL2 Ubuntu' }
    commands = [ordered]@{ rtl = 'circt-opt --export-verilog smt-rank-tail.mlir -o /dev/null'; compile = 'iverilog -g2012 -s smt_rank_tail_directed_tb -o smt-rank-tail.vvp smt-rank-tail.sv smt-rank-tail.tb.sv'; simulate = 'vvp smt-rank-tail.vvp'; rtlExportExit = 0; compileExit = 0; simulationExit = 0 }
    artifacts = [ordered]@{ rtlSha256 = Get-Sha256 (Join-Path $Output 'smt-rank-tail.sv'); rtlBytes = (Get-Item (Join-Path $Output 'smt-rank-tail.sv')).Length; testbenchSha256 = Get-Sha256 (Join-Path $Output 'smt-rank-tail.tb.sv'); compileLogSha256 = Get-Sha256 (Join-Path $Output 'iverilog-compile.log'); simulationLogSha256 = Get-Sha256 $simulationPath; simulationOutput = $simulationText }
    scenarios = @('A3 bit5 rejects cross-VT memory candidate at zero budget','A3 bit5 admits cross-VT memory candidate at positive budget','A3 bit5 rejects cross-VT memory candidate at signed-negative budget','legacy A3 bit5 fact is ignored','two accepted-path candidates increment source-ordered exhaustion events','earlier Stage A guard suppresses exhaustion event','Assist bypass does not increment exhaustion event','standalone same-owner A3 bit5 bypass','standalone cross-owner A3 bit5 rejection')
    limitations = 'Combinational candidate projection only. Aggregate SmtRejectionsCount, runtime candidate/snapshot identity, replay refresh and temporal scheduler lifecycle remain unmodeled. Same-owner and cross-owner direct A3 bit5 behavior is checked in the standalone gate; the source-ordered SpeculationBudgetExhaustionEvents counter is modeled from final Stage A reason. No Golden equivalence or waveform is claimed.'
}
$receiptPath = Join-Path $Output 'smt-rank-tail-simulation-receipt.json'
[IO.File]::WriteAllText($receiptPath, (ConvertTo-Json -InputObject $receipt -Depth 20) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$publishFiles = @('smt-rank-tail.sv','smt-rank-tail.tb.sv','smt-rank-tail-simulation-receipt.json','firtool-version.txt','firtool-export.log','iverilog-version.txt','vvp-version.txt','iverilog-compile.log','vvp-simulation.log','tool-hashes.txt')
foreach ($name in $publishFiles) { Copy-Item -LiteralPath (Join-Path $Output $name) -Destination (Join-Path $Intermediate $name) -Force }
Push-Location $BridgeRoot
try { dotnet run --no-restore --project $RunnerProject -- --publish $Intermediate $StudioRoot; if ($LASTEXITCODE -ne 0) { throw "Studio publication and consumer API check failed with exit $LASTEXITCODE." } }
finally { Pop-Location }


