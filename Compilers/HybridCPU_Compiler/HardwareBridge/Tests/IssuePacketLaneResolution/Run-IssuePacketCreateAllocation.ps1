param(
  [string]$ModelRoot = 'C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE',
  [string]$ProducerRoot = 'C:\Users\Yuriy\Desktop\CircuitWeave-main',
  [string]$CirctOpt = 'C:\Users\Yuriy\Desktop\CircuitWeave ExTool\final\packages\circt-firtool-1.163.0\firtool-1.163.0\bin\circt-opt.exe',
  [string]$OssBin = 'C:\Users\Yuriy\Desktop\CircuitWeave ExTool\final\packages\oss-cad-suite-2026-11-10\oss-cad-suite\bin',
  [string]$ArtifactDirectory = (Join-Path $PSScriptRoot 'artifacts\issue-packet-create-allocation-v1')
)
$ErrorActionPreference = 'Stop'
$modelFiles = @(
  (Join-Path $ModelRoot 'CloseToHSL\Core\Pipeline\Core\RuntimeClusterAdmissionPreparation.BundleIssuePacket.cs'),
  (Join-Path $ModelRoot 'CloseToHSL\Core\Legality\ClusterIssueIntent.cs'),
  (Join-Path $ModelRoot 'CloseToHSL\Core\Decoder\ClusterIssuePreparation.cs'),
  (Join-Path $ModelRoot 'CloseToHSL\Core\Pipeline\Scheduling\SlotLegality\SlotClassDefinitions.cs'),
  (Join-Path $ModelRoot 'CloseToHSL\Core\Pipeline\Scheduling\SlotPlacementMetadata.cs'),
  (Join-Path $ModelRoot 'CloseToHSL\Core\Pipeline\Metadata\BundleMetadata.cs'),
  (Join-Path $ModelRoot 'CloseToHSL\Core\Decoder\DecodedBundleDescriptor.cs')
)
$oracleFiles = @($modelFiles[0], $modelFiles[3], $modelFiles[4], $modelFiles[2], $modelFiles[1])
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('cw-issue-packet-create-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
$hwir = Join-Path ([IO.Path]::GetTempPath()) 'cw-issue-packet-create-allocation.hwir.json'
$mlir = Join-Path ([IO.Path]::GetTempPath()) 'cw-issue-packet-create-allocation.mlir'
$rtl = Join-Path $tempRoot 'issue-packet-create.sv'
$vectors = Join-Path $tempRoot 'oracle.jsonl'
$tb = Join-Path $tempRoot 'tb.sv'
$sim = Join-Path $tempRoot 'sim.vvp'
$bridgeRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$bridgeProject = Join-Path $PSScriptRoot 'IssuePacketLaneResolution.csproj'
$oracleProject = Join-Path $ProducerRoot 'tests\CircuitWeave.IssuePacketLaneOracle\CircuitWeave.IssuePacketLaneOracle.csproj'

dotnet run --project $bridgeProject --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Bridge source guard, validation, or MLIR lowering failed.' }
$oracleArgs = @('--configuration','Release','--','--create') + $oracleFiles + @($vectors)
dotnet run --project $oracleProject @oracleArgs
if ($LASTEXITCODE -ne 0) { throw 'Exact-source BundleIssuePacket.Create Roslyn oracle failed.' }
& $CirctOpt $mlir --lower-comb --export-verilog -o NUL | Set-Content -Encoding ascii $rtl
if ($LASTEXITCODE -ne 0) { throw 'CIRCT SystemVerilog export failed.' }
python (Join-Path $PSScriptRoot 'IssuePacketCreateRtlTest.py') $rtl $vectors $tb
if ($LASTEXITCODE -ne 0) { throw 'RTL testbench generation failed.' }
$env:PATH = $OssBin + ';' + (Join-Path (Split-Path $OssBin -Parent) 'lib') + ';' + $env:PATH
& (Join-Path $OssBin 'iverilog.exe') -g2012 -s tb -o $sim $rtl $tb
if ($LASTEXITCODE -ne 0) { throw 'Icarus RTL compilation failed.' }
& (Join-Path $OssBin 'vvp.exe') $sim
if ($LASTEXITCODE -ne 0) { throw 'Icarus exact-source comparison failed.' }

$dest = [IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $dest) { throw "Immutable artifact directory already exists: $dest" }
New-Item -ItemType Directory -Path $dest | Out-Null
Copy-Item $mlir (Join-Path $dest 'issue-packet-create.mlir')
Copy-Item $rtl (Join-Path $dest 'issue-packet-create.sv')
Copy-Item $tb (Join-Path $dest 'tb.sv')
Copy-Item $vectors (Join-Path $dest 'oracle.jsonl')
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
$sourceRecords = $modelFiles | ForEach-Object { [ordered]@{ path = [IO.Path]::GetFullPath($_); sha256 = Hash $_ } }
$receipt = [ordered]@{
  schema = 'circuitweave.issue-packet-create-allocation-run/v1'
  status = 'candidate-combinational-independent-function-oracle-pass'
  modelCommit = '1bfe41a49cf11c80fa6c6aee87accb54b78b331f'
  goldenCommit = '6946ccab9d888f7dae0a4d8e18480f436138acd9'
  modelSources = $sourceRecords
  bridgeSource = [IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) '..\HWIR\BridgeHwirIssuePacketCreateAllocation.cs'))
  bridgeSourceSha256 = Hash (Join-Path (Split-Path $PSScriptRoot -Parent) '..\HWIR\BridgeHwirIssuePacketCreateAllocation.cs')
  vectorCases = 1028
  directedCases = 4
  randomizedCases = 1024
  randomSeed = '0x6946CCAB'
  rtlMismatches = 0
  simulator = 'Icarus Verilog; PASS'
  classDomain = 'Every adapter slot_class input must equal an explicit SlotClass enum member; class_inputs_defined is checked high.'
  scalarMicroOp = 'Every selected scalar entry must have a live MicroOp; source_adapter_valid is checked high. Active decoder candidate builder rejects null as empty/Nop and filters to ALU class-flexible placements.'
  scalarEntries = '0..4 active entries, contiguous indices, unique SlotIndex in [0,7]; enforced by active ClusterIssueIntentBuilder source guard and source_adapter_valid.'
  slots = 'Bundle width 8; auxiliary_slot_count supports 0..8 and invalid/missing selected descriptors map to unmapped.'
  scope = 'Assigned lane mask, assigned slot mask, scalar lane mask, lane-to-slot map and unmapped selected mask only. Does not model IssuePacketLane payload, execution mode metadata, active handoff, state/time, Golden equivalence or whole Processor.'
  artifacts = [ordered]@{ mlirSha256 = Hash (Join-Path $dest 'issue-packet-create.mlir'); rtlSha256 = Hash (Join-Path $dest 'issue-packet-create.sv'); oracleSha256 = Hash (Join-Path $dest 'oracle.jsonl'); testbenchSha256 = Hash (Join-Path $dest 'tb.sv') }
}
$receiptPath = Join-Path $dest 'run-receipt.json'
[IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
Write-Host "Artifacts: $dest"
Write-Host "Receipt SHA-256: $(Hash $receiptPath)"
