param(
  [string]$OracleJsonl = (Join-Path $env:TEMP 'issue-packet-nonscalar-oracle-v2.jsonl'),
  [string]$CreateOracleJsonl = '',
  [string]$ModelRoot = 'C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE',
  [string]$CirctOpt = 'C:\Users\Yuriy\Desktop\CircuitWeave ExTool\final\packages\circt-firtool-1.163.0\firtool-1.163.0\bin\circt-opt.exe',
  [string]$OssBin = 'C:\Users\Yuriy\Desktop\CircuitWeave ExTool\final\packages\oss-cad-suite-2026-11-10\oss-cad-suite\bin',
  [string]$ArtifactDirectory = ''
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $OracleJsonl -PathType Leaf)) { throw "Oracle not found: $OracleJsonl" }
if (-not (Test-Path -LiteralPath $CirctOpt -PathType Leaf)) { throw "circt-opt not found: $CirctOpt" }
$iverilog = Join-Path $OssBin 'iverilog.exe'; $vvp = Join-Path $OssBin 'vvp.exe'
if (-not (Test-Path -LiteralPath $iverilog) -or -not (Test-Path -LiteralPath $vvp)) { throw 'Icarus tools missing.' }
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
  $ArtifactDirectory = Join-Path $env:TEMP ('cw-nonscalar-resolver-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [guid]::NewGuid().ToString('N'))
}
$dest = [IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $dest) { throw "Immutable artifact directory already exists: $dest" }
New-Item -ItemType Directory -Path $dest | Out-Null
$core = Join-Path $ModelRoot 'CloseToHSL\Core'
$stage = Join-Path $core 'Pipeline\ExecutionFlow\StageFlow\CPU_Core.PipelineExecution.StageFlow.cs'
$dep = Join-Path $core 'Decoder\DecodedBundleDescriptor.cs'
$lane = Join-Path $core 'Pipeline\Core\RuntimeClusterAdmissionPreparation.IssuePacketLane.cs'
$packet = Join-Path $core 'Pipeline\Core\RuntimeClusterAdmissionPreparation.BundleIssuePacket.cs'
$bridgeRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$project = Join-Path $PSScriptRoot 'IssuePacketNonScalarResolver.csproj'
$bridgeArgs = @($stage, $dep, $lane, $packet, $OracleJsonl, $dest, $ModelRoot)
if (-not [string]::IsNullOrWhiteSpace($CreateOracleJsonl)) {
  if (-not (Test-Path -LiteralPath $CreateOracleJsonl -PathType Leaf)) { throw "Create oracle not found: $CreateOracleJsonl" }
  $bridgeArgs += [IO.Path]::GetFullPath($CreateOracleJsonl)
}
dotnet run --project $project --configuration Release -- @bridgeArgs
if ($LASTEXITCODE -ne 0) { throw 'Source pinning, HWIR validation, or MLIR lowering failed.' }
$mlir = Join-Path $dest 'resolver.mlir'; $rtl = Join-Path $dest 'resolver.sv'
$sv = & $CirctOpt $mlir --lower-comb --export-verilog -o NUL
if ($LASTEXITCODE -ne 0) { throw 'CIRCT RTL export failed.' }
[IO.File]::WriteAllText($rtl, (($sv -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
$compositionMlir = Join-Path $dest 'composition.mlir'
$compositionSv = & $CirctOpt $compositionMlir --lower-comb --export-verilog -o NUL
if ($LASTEXITCODE -ne 0) { throw 'CIRCT integrated composition RTL export failed.' }
[IO.File]::WriteAllText((Join-Path $dest 'composition.sv'), (($compositionSv -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
$env:PATH = $OssBin + ';' + (Join-Path (Split-Path $OssBin -Parent) 'lib') + ';' + $env:PATH
Push-Location $dest
try {
  & $iverilog -g2012 -s tb -o resolver.vvp resolver.sv resolver_tb.sv
  if ($LASTEXITCODE -ne 0) { throw 'Icarus compile of CIRCT RTL failed.' }
  & $vvp resolver.vvp *> simulation.log
  if ($LASTEXITCODE -ne 0 -or -not (Select-String -LiteralPath simulation.log -SimpleMatch 'PASS exact-source non-scalar resolver vectors=5290' -Quiet)) { throw 'Oracle simulation failed.' }
  & $iverilog -g2012 -s composition_tb -o composition.vvp composition.sv composition_tb.sv
  if ($LASTEXITCODE -ne 0) { throw 'Icarus compile of integrated composition RTL failed.' }
  & $vvp composition.vvp *> composition-simulation.log
  if ($LASTEXITCODE -ne 0 -or -not (Select-String -LiteralPath composition-simulation.log -SimpleMatch 'PASS integrated nonzero Create-resolver-materialization hierarchy vectors=' -Quiet)) { throw 'Integrated nonzero Create hierarchy oracle simulation failed.' }
  & $iverilog -g2012 -s tb -o resolver-zero.vvp resolver_zero_output.sv resolver_tb.sv
  if ($LASTEXITCODE -ne 0) { throw 'Icarus compile of zero-output sensitivity mutant failed.' }
  & $vvp resolver-zero.vvp *> mutation.log
  $mutationExit = $LASTEXITCODE
  if ($mutationExit -eq 0 -or -not (Select-String -LiteralPath mutation.log -SimpleMatch 'MISMATCH row=' -Quiet)) { throw 'Fault sensitivity check did not detect the zero-output mutant.' }
} finally { Pop-Location }
$sourceHashes = Get-Content -LiteralPath (Join-Path $dest 'source-hashes.txt')
$files = @('resolver.hwir.json','resolver.mlir','resolver.sv','resolver_tb.sv','oracle-vectors.mem','oracle.jsonl','simulation.log','mutation.log','lowering-receipt.json','source-hashes.txt','composition.hwir.json','composition.mlir','composition.sv','composition_tb.sv','composition-oracle-vectors.mem','composition-simulation.log')
if (Test-Path -LiteralPath (Join-Path $dest 'create-oracle.jsonl')) { $files += 'create-oracle.jsonl' }
$compositionLog = Get-Content -LiteralPath (Join-Path $dest 'composition-simulation.log') -Raw
$compositionMatch = [regex]::Match($compositionLog, 'PASS integrated nonzero Create-resolver-materialization hierarchy vectors=(\d+) differing_slot_lane_masks=(\d+)')
if (-not $compositionMatch.Success) { throw 'Integrated composition receipt lacks the shared-slot-mask mismatch coverage marker.' }
$compositionVectorCount = [int]$compositionMatch.Groups[1].Value
$compositionDifferentMaskCount = [int]$compositionMatch.Groups[2].Value
if ($compositionVectorCount -ne 4008 -or $compositionDifferentMaskCount -ne 3746) { throw "Unexpected integrated coverage counts: vectors=$compositionVectorCount differing_slot_lane_masks=$compositionDifferentMaskCount" }
$fileHashes = [ordered]@{}
foreach ($name in $files) { $fileHashes[$name] = (Get-FileHash -LiteralPath (Join-Path $dest $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
$receipt = [ordered]@{
  schema = 'circuitweave.issue-packet-nonscalar-resolver-simulation/v1'
  modelCommit = '1bfe41a49cf11c80fa6c6aee87accb54b78b331f'
  oracleCases = 5290
  simulation = 'PASS'
  compositionSimulation = 'PASS: Create and resolver share the slot-domain scalar mask; separate assigned physical-lane occupancy gates all eight eligibility instances'
  compositionVectors = $compositionVectorCount
  compositionDifferentSlotAndLaneMaskCases = $compositionDifferentMaskCount
  zeroOutputMutation = 'DETECTED'
  sourcePins = $sourceHashes
  artifacts = $fileHashes
}
$receiptPath = Join-Path $dest 'simulation-receipt.json'
[IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
$receiptHash = (Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "PASS: exact-source resolver RTL, 5290 oracle vectors; zero-output mutant detected."
Write-Host "Artifacts: $dest"
Write-Host "Receipt SHA-256: $receiptHash"
$global:LASTEXITCODE = 0
