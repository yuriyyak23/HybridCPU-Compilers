param(
  [string]$InventoryPath = 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\bridge-semantic-deps-3\source-inventory.json',
  [string]$ProjectInventoryPath = 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\project-inventory.json',
  [string]$OutputFile = 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\hardware-model-8080-bridge-derived-spec-ana-hlt-v19-20261119.cwtemplate.json',
  [string]$RtlValidationLogPath = 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\tests\rtl-validation-daa-v4\rtl-test.log',
  [string]$SemanticProjectionDirectory = 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\bridge-dad-projections-20261119',
  [string[]]$CandidateEvidencePaths = @(
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\golden-runs\20261119-170638-163961\summary.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\logical-projection-xri-bridge-test-20261119.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\logical-sv-exhaustive-xri-20261119.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\sub-sv-exhaustive-validation-20261118.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\sbb-sv-exhaustive-validation-20261118.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\golden-runs\20261119-002013-025782\summary.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\golden-runs\20261119-004537-891806\summary.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\golden-runs\20261119-005051-841369\summary.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\golden-runs\20261119-010031-541835\summary.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\golden-runs\20261119-143523-873163\summary.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\golden-runs\20261119-144129-023534\summary.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\golden-runs\20261119-144658-350519\summary.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\golden-runs\20261119-150500-119723\summary.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\golden-runs\20261119-150812-735796\summary.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\daa-exhaustive-run-20261119-v2\simulation.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\daa-exhaustive-bridge-run-20261119\simulation.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\daa-exhaustive-bridge-run-20261119\rtl-hash-check.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\runs\8080-run-20261119-003805-175702\simulation.log',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\runs\8080-run-20261119-003805-175702\image-byte-check.log'
  ),
  [string[]]$MlirArtifactPaths = @(
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\bridge-add-projection-20261118\circuitweave-sum9\add-sum9.mlir',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\bridge-add-adc-projection-20261118\circuitweave-adc-sum9\adc-sum9.mlir',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\bridge-arithmetic-projections-sbb-20261118\circuitweave-sbb-result16\sbb-result16.mlir',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\sub-result16-circuitweave-20261118\sub-result16.mlir',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\dad-circuitweave-sum17-20261119\dad-sum17.mlir',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\logical-circuitweave-xri-20261119\logical-and.mlir',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\logical-circuitweave-xri-20261119\logical-or.mlir',
    'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\logical-circuitweave-xri-20261119\logical-xor.mlir'
  )
)
$ErrorActionPreference = 'Stop'
$allowedRoot = [IO.Path]::GetFullPath('C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080').TrimEnd('\') + '\'
function Assert-UnderArtifactRoot([string]$Path, [string]$Role) {
  $full = [IO.Path]::GetFullPath($Path)
  if (-not $full.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "$Role must be inside $allowedRoot : $full" }
  for ($d = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($full)); $null -ne $d; $d = $d.Parent) {
    if (($d.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked path refused for ${Role}: $($d.FullName)" }
  }
  return $full
}
$inventory = Assert-UnderArtifactRoot $InventoryPath 'Source inventory'
$output = Assert-UnderArtifactRoot $OutputFile 'Template output'
if (-not (Test-Path -LiteralPath $inventory -PathType Leaf)) { throw "Source inventory not found: $inventory" }
if (Test-Path -LiteralPath $output) { throw "Existing output refused: $output" }
$projectInventory = $null
if (-not [string]::IsNullOrWhiteSpace($ProjectInventoryPath)) {
  $projectInventory = Assert-UnderArtifactRoot $ProjectInventoryPath 'Project inventory'
  if (-not (Test-Path -LiteralPath $projectInventory -PathType Leaf)) { throw "Project inventory not found: $projectInventory" }
}
$model = Get-Content -LiteralPath $inventory -Raw | ConvertFrom-Json
if ($model.schema -ne 'hardwarebridge.intel8080.import/v2') { throw "Unsupported source inventory schema: $($model.schema)" }
if (@($model.files).Count -ne 7 -or @($model.types).Count -lt 7) { throw 'Expected the selected seven-file 8080 source inventory.' }
$projectionRoot = Assert-UnderArtifactRoot $SemanticProjectionDirectory 'Semantic projection directory'
if (-not (Test-Path -LiteralPath $projectionRoot -PathType Container)) { throw "Semantic projection directory missing: $projectionRoot" }
$projectionSpecs = @(
  @{ file='add-behavior-projection.json'; schema='hardwarebridge.intel8080.add-hwir/v1' },
  @{ file='adc-behavior-projection.json'; schema='hardwarebridge.intel8080.adc-hwir/v1' },
  @{ file='sbb-behavior-projection.json'; schema='hardwarebridge.intel8080.sbb-hwir/v1' },
  @{ file='sub-behavior-projection.json'; schema='hardwarebridge.intel8080.sub-hwir/v1' },
  @{ file='dad-behavior-projection.json'; schema='hardwarebridge.intel8080.dad-hwir/v1' },
  @{ file='logical-behavior-projection.json'; schema='hardwarebridge.intel8080.logical-hwir/v1' }
)
$semanticProjections = foreach ($spec in $projectionSpecs) {
  $path = Assert-UnderArtifactRoot (Join-Path $projectionRoot $spec.file) 'Semantic projection'
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Semantic projection missing: $path" }
  $document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
  if ($document.schema -ne $spec.schema) { throw "Unexpected projection schema in $path : $($document.schema)" }
  @{ path=([IO.Path]::GetRelativePath($allowedRoot,$path).Replace('\','/')); schema=$document.schema;
     sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant();
     dispatchCount=@($document.dispatch).Count; equationCount=@($document.equations).Count;
     sourceTraceCount=@($document.sourceTrace).Count; sourceHashes=@($document.sourceHashes) }
}
$candidateEvidence = foreach ($candidatePath in $CandidateEvidencePaths) {
  $path = Assert-UnderArtifactRoot $candidatePath 'Candidate evidence'
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Candidate evidence missing: $path" }
  $text = Get-Content -LiteralPath $path -Raw
  $status = if ($text -match '(?m)^PASS\b|All cases passed') { 'PASS' } else { 'INCOMPLETE OR NOT PASS' }
  @{ path=([IO.Path]::GetRelativePath($allowedRoot,$path).Replace('\','/')); sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); status=$status }
}
$mlirEvidence = foreach ($mlirPath in $MlirArtifactPaths) {
  $path = Assert-UnderArtifactRoot $mlirPath 'MLIR artifact'
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "MLIR artifact missing: $path" }
  @{ path=([IO.Path]::GetRelativePath($allowedRoot,$path).Replace('\','/')); sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); scope='candidate scalar result slice; no whole CPU or external CIRCT verification implied' }
}
$project = if ($projectInventory) { Get-Content -LiteralPath $projectInventory -Raw | ConvertFrom-Json } else { $null }
$rtlLog = Assert-UnderArtifactRoot $RtlValidationLogPath 'RTL validation log'
if (-not (Test-Path -LiteralPath $rtlLog -PathType Leaf)) { throw "RTL validation log not found: $rtlLog" }
$rtlLogText = Get-Content -LiteralPath $rtlLog -Raw
$rtlStatus = if ($rtlLogText -match 'STATUS PASS') { 'PASS' } elseif ($rtlLogText -match 'STATUS NOT RUN') { 'NOT RUN' } else { 'FAIL OR INCOMPLETE' }
if ($project -and $project.schema -ne 'hardwarebridge.intel8080.msbuild-import-inventory/v1') { throw 'Unsupported project inventory schema.' }

$root = 'C:\Users\Yuriy\Desktop\HybridCPU Compilers\Compilers\HybridCPU_Compiler\HardwareBridge_intel8080\Intel8080\Rtl'
$rtlSpecs = @(
  @{ path = 'cw8080_subset_cpu.sv'; artifactPath = 'cw8080_subset_cpu_mov_alu_mvi_stack_pushpop_io_memops_daa_specana_hlt_v19_20261119.sv'; status = 'manual partial implementation; 11 Icarus 12.0 benches pass via WSL including 262144 ALU vectors, plus 46 x86 golden programs pass on this exact RTL snapshot; not exhaustive and not a full CPU conformance proof' },
  @{ path = 'cw8080_add.sv'; artifactPath = 'cw8080_add_specana_hlt_v19_20261119.sv'; status = 'manual arithmetic datapath variants; not integrated as a verified CPU' }
)
$rtlFiles = foreach ($spec in $rtlSpecs) {
  $path = Join-Path $root $spec.path
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Expected adapter RTL missing: $path" }
  $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
  $artifactRtl = Join-Path $allowedRoot (Join-Path 'rtl' $spec.artifactPath)
  if (-not (Test-Path -LiteralPath $artifactRtl -PathType Leaf)) { throw "Template RTL link target missing: $artifactRtl" }
  $artifactHash = (Get-FileHash -LiteralPath $artifactRtl -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($hash -ne $artifactHash) { throw "Bridge RTL copy differs from its artifact target; refusing a misleading source map: $path" }
  @{ path = "rtl/$($spec.artifactPath)"; sha256 = $hash; status = $spec.status }
}
$tbPath = Join-Path $root 'tb_cw8080_subset_cpu.sv'
$tbArtifact = Join-Path $allowedRoot 'tests\tb_cw8080_subset_cpu_specana_hlt_v19_20261119.sv'
if (-not (Test-Path -LiteralPath $tbPath -PathType Leaf) -or -not (Test-Path -LiteralPath $tbArtifact -PathType Leaf)) { throw 'Updated integrated CPU testbench or its artifact copy is missing.' }
$tbHash = (Get-FileHash -LiteralPath $tbPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($tbHash -ne (Get-FileHash -LiteralPath $tbArtifact -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Bridge CPU testbench differs from its artifact copy.' }
$aluTbPath = Join-Path $root 'tb_cw8080_add.sv'
$aluTbArtifact = Join-Path $allowedRoot 'tests\tb_cw8080_add_specana_hlt_v19_20261119.sv'
if (-not (Test-Path -LiteralPath $aluTbPath -PathType Leaf) -or -not (Test-Path -LiteralPath $aluTbArtifact -PathType Leaf)) { throw 'ALU testbench or its artifact copy is missing.' }
$aluTbHash = (Get-FileHash -LiteralPath $aluTbPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($aluTbHash -ne (Get-FileHash -LiteralPath $aluTbArtifact -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Bridge ALU testbench differs from its artifact copy.' }
$pairTbPath = Join-Path $root 'tb_cw8080_pair_cpu.sv'
$pairTbArtifact = Join-Path $allowedRoot 'tests\tb_cw8080_pair_cpu_specana_hlt_v19_20261119.sv'
if (-not (Test-Path -LiteralPath $pairTbPath -PathType Leaf) -or -not (Test-Path -LiteralPath $pairTbArtifact -PathType Leaf)) { throw 'Pair CPU testbench or artifact copy missing.' }
$pairTbHash = (Get-FileHash -LiteralPath $pairTbPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($pairTbHash -ne (Get-FileHash -LiteralPath $pairTbArtifact -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Pair CPU testbench differs from its artifact copy.' }
$branchTbPath = Join-Path $root 'tb_cw8080_branch_cpu.sv'
$branchTbArtifact = Join-Path $allowedRoot 'tests\tb_cw8080_branch_cpu_specana_hlt_v19_20261119.sv'
if (-not (Test-Path -LiteralPath $branchTbPath -PathType Leaf) -or -not (Test-Path -LiteralPath $branchTbArtifact -PathType Leaf)) { throw 'Branch CPU testbench or artifact copy missing.' }
$branchTbHash = (Get-FileHash -LiteralPath $branchTbPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($branchTbHash -ne (Get-FileHash -LiteralPath $branchTbArtifact -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Branch CPU testbench differs from artifact copy.' }
$stackTbPath = Join-Path $root 'tb_cw8080_stack_cpu.sv'
$stackTbArtifact = Join-Path $allowedRoot 'tests\tb_cw8080_stack_cpu_specana_hlt_v19_20261119.sv'
if (-not (Test-Path -LiteralPath $stackTbPath -PathType Leaf) -or -not (Test-Path -LiteralPath $stackTbArtifact -PathType Leaf)) { throw 'Stack CPU testbench or artifact copy missing.' }
$stackTbHash = (Get-FileHash -LiteralPath $stackTbPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($stackTbHash -ne (Get-FileHash -LiteralPath $stackTbArtifact -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Stack CPU testbench differs from artifact copy.' }
$pushPopTbPath = Join-Path $root 'tb_cw8080_push_pop_cpu.sv'
$pushPopTbArtifact = Join-Path $allowedRoot 'tests\tb_cw8080_push_pop_cpu_specana_hlt_v19_20261119.sv'
if (-not (Test-Path -LiteralPath $pushPopTbPath -PathType Leaf) -or -not (Test-Path -LiteralPath $pushPopTbArtifact -PathType Leaf)) { throw 'PUSH/POP CPU testbench or artifact copy missing.' }
$pushPopTbHash = (Get-FileHash -LiteralPath $pushPopTbPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($pushPopTbHash -ne (Get-FileHash -LiteralPath $pushPopTbArtifact -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'PUSH/POP CPU testbench differs from artifact copy.' }
$ioTbPath = Join-Path $root 'tb_cw8080_io_cpu.sv'
$ioTbArtifact = Join-Path $allowedRoot 'tests\tb_cw8080_io_cpu_specana_hlt_v19_20261119.sv'
if (-not (Test-Path -LiteralPath $ioTbPath -PathType Leaf) -or -not (Test-Path -LiteralPath $ioTbArtifact -PathType Leaf)) { throw 'I/O CPU testbench or artifact copy missing.' }
$ioTbHash = (Get-FileHash -LiteralPath $ioTbPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ioTbHash -ne (Get-FileHash -LiteralPath $ioTbArtifact -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'I/O CPU testbench differs from artifact copy.' }
$rotateTbPath = Join-Path $root 'tb_cw8080_rotate_cpu.sv'
$rotateTbArtifact = Join-Path $allowedRoot 'tests\tb_cw8080_rotate_cpu_specana_hlt_v19_20261119.sv'
if (-not (Test-Path -LiteralPath $rotateTbPath -PathType Leaf) -or -not (Test-Path -LiteralPath $rotateTbArtifact -PathType Leaf)) { throw 'Rotate CPU testbench or artifact copy missing.' }
$rotateTbHash = (Get-FileHash -LiteralPath $rotateTbPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($rotateTbHash -ne (Get-FileHash -LiteralPath $rotateTbArtifact -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Rotate CPU testbench differs from artifact copy.' }
$immAluTbPath = Join-Path $root 'tb_cw8080_immediate_alu_cpu.sv'
$immAluTbArtifact = Join-Path $allowedRoot 'tests\tb_cw8080_immediate_alu_cpu_specana_hlt_v19_20261119.sv'
if (-not (Test-Path -LiteralPath $immAluTbPath -PathType Leaf) -or -not (Test-Path -LiteralPath $immAluTbArtifact -PathType Leaf)) { throw 'Immediate ALU testbench or artifact copy missing.' }
$immAluTbHash = (Get-FileHash -LiteralPath $immAluTbPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($immAluTbHash -ne (Get-FileHash -LiteralPath $immAluTbArtifact -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Immediate ALU testbench differs from artifact copy.' }
$memoryAddressTbPath = Join-Path $root 'tb_cw8080_memory_address_cpu.sv'
$memoryAddressTbArtifact = Join-Path $allowedRoot 'tests\tb_cw8080_memory_address_cpu_specana_hlt_v19_20261119.sv'
if (-not (Test-Path -LiteralPath $memoryAddressTbPath -PathType Leaf) -or -not (Test-Path -LiteralPath $memoryAddressTbArtifact -PathType Leaf)) { throw 'Addressed memory testbench or artifact copy missing.' }
$memoryAddressTbHash = (Get-FileHash -LiteralPath $memoryAddressTbPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($memoryAddressTbHash -ne (Get-FileHash -LiteralPath $memoryAddressTbArtifact -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Addressed memory testbench differs from artifact copy.' }
$daaTbPath = Join-Path $root 'tb_cw8080_daa_cpu.sv'
$daaTbArtifact = Join-Path $allowedRoot 'tests\tb_cw8080_daa_cpu_specana_hlt_v19_20261119.sv'
if (-not (Test-Path -LiteralPath $daaTbPath -PathType Leaf) -or -not (Test-Path -LiteralPath $daaTbArtifact -PathType Leaf)) { throw 'DAA testbench or artifact copy missing.' }
$daaTbHash = (Get-FileHash -LiteralPath $daaTbPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($daaTbHash -ne (Get-FileHash -LiteralPath $daaTbArtifact -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'DAA testbench differs from artifact copy.' }
$testbenches = @(
  @{ path = 'tests/tb_cw8080_add_specana_hlt_v19_20261119.sv'; sha256 = $aluTbHash; status = 'ADD/ADC datapath; compiled and executed by Icarus 12.0 via WSL' },
  @{ path = 'tests/tb_cw8080_subset_cpu_specana_hlt_v19_20261119.sv'; sha256 = $tbHash; status = 'checks MOV, INR/DCR, MVI D/M, ALU flags including SUB/SBB/CMP auxiliary carry, handshakes, HALT and IRQ; compiled and executed by Icarus 12.0 via WSL' },
  @{ path = 'tests/tb_cw8080_pair_cpu_specana_hlt_v19_20261119.sv'; sha256 = $pairTbHash; status = 'checks LXI/INX/DCX/DAD across BC/DE/HL/SP with memory stalls; compiled and executed by Icarus 12.0 via WSL' },
  @{ path = 'tests/tb_cw8080_branch_cpu_specana_hlt_v19_20261119.sv'; sha256 = $branchTbHash; status = 'checks taken JZ, not-taken JNZ, JMP, condition-flag preservation and stalled address reads; compiled and executed by Icarus 12.0 via WSL' },
  @{ path = 'tests/tb_cw8080_stack_cpu_specana_hlt_v19_20261119.sv'; sha256 = $stackTbHash; status = 'checks nested CALL/RET, conditional call/return, RST 5, stack byte ordering and balanced SP; compiled and executed by Icarus 12.0 via WSL' },
  @{ path = 'tests/tb_cw8080_push_pop_cpu_specana_hlt_v19_20261119.sv'; sha256 = $pushPopTbHash; status = 'checks PUSH/POP BC and PSW, flag packing, byte order, SP and acknowledged stack transfers; compiled and executed by Icarus 12.0 via WSL' },
  @{ path = 'tests/tb_cw8080_io_cpu_specana_hlt_v19_20261119.sv'; sha256 = $ioTbHash; status = 'checks IN/OUT port immediate, data, request/ready stalls and PC; chosen hardware semantics; compiled and executed by Icarus 12.0 via WSL' },
  @{ path = 'tests/tb_cw8080_rotate_cpu_specana_hlt_v19_20261119.sv'; sha256 = $rotateTbHash; status = 'checks four accumulator rotates, CMA, STC/CMC and flag preservation; compiled and executed by Icarus 12.0 via WSL' },
  @{ path = 'tests/tb_cw8080_immediate_alu_cpu_specana_hlt_v19_20261119.sv'; sha256 = $immAluTbHash; status = 'checks all immediate arithmetic/logical ALU instructions, CPI non-writeback and auxiliary carry behavior; compiled and executed by Icarus 12.0 via WSL' },
  @{ path = 'tests/tb_cw8080_memory_address_cpu_specana_hlt_v19_20261119.sv'; sha256 = $memoryAddressTbHash; status = 'checks BC/DE indirect transfers, direct A and paired HL loads/stores, SPHL/XTHL/XCHG/PCHL, PC advancement, memory contents and ready stalls; compiled and executed by Icarus 12.0 via WSL' },
  @{ path = 'tests/tb_cw8080_daa_cpu_specana_hlt_v19_20261119.sv'; sha256 = $daaTbHash; status = 'checks low/high decimal corrections and CY/AC/SZP outcomes against Intel manual examples; compiled and executed by Icarus 12.0 via WSL' }
)
$blockForType = @{ Cpu = 'instruction_control'; Ops = 'execution'; Registers = 'registers_flags'; Flags = 'registers_flags'; Memory = 'memory'; Bus = 'io_interrupt'; Utils = 'execution' }
$selectedTypes = foreach ($type in $model.types | Sort-Object Name) {
  $deps = @($model.memberDependencies | Where-Object { $_.sourceMember.StartsWith($type.Name + '.', [StringComparison]::Ordinal) } | Sort-Object File,Line,Kind)
  @{
    Name = $type.name; Namespace = $type.namespace; File = $type.file; Line = $type.line
    HardwareBlock = $blockForType[$type.name]
    Members = @($type.members | ForEach-Object { "$($_.kind) $($_.signature) [$($_.line):$($_.column)]" })
    Dependencies = @($deps | Where-Object targetSymbol | ForEach-Object { $_.targetSymbol } | Sort-Object -Unique)
    Warnings = @($type.warnings)
  }
}
$sourceDispatch = foreach ($op in $model.opcodes | Sort-Object code) {
  '0x{0:X2} {1} — {2}:{3}' -f $op.code,$op.method,[IO.Path]::GetFileName($op.file),$op.line
}
$sourceDispatchOpcodes = @($model.opcodes | ForEach-Object { '0x{0:X2}' -f $_.code } | Sort-Object -Unique)
$manualAddAdc = @('0x83 ADD_E','0x84 ADD_H','0x85 ADD_L','0x86 ADD_M','0x8A ADC_D','0x8F ADC_A')
$movSourceOpcodes = @($model.opcodes | Where-Object { $_.code -ge 0x40 -and $_.code -le 0x7f -and $_.code -ne 0x76 } | Sort-Object code | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method })
$movSourceCodes = @($model.opcodes | Where-Object { $_.code -ge 0x40 -and $_.code -le 0x7f -and $_.code -ne 0x76 } | ForEach-Object { [int]$_.code })
$movManualOpcodes = @(0x40..0x7f | Where-Object { $_ -ne 0x76 -and $_ -notin $movSourceCodes } | ForEach-Object { '0x{0:X2}' -f $_ })
$aluNames = @('ADD','ADC','SUB','SBB','ANA','XRA','ORA','CMP')
$aluInstructions = foreach ($group in 0..7) {
  $base = 0x80 + ($group * 8)
  $codes = @($base..($base + 7))
  $mapped = @($model.opcodes | Where-Object { $_.code -in $codes } | Sort-Object code | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method })
  $mappedCodes = @($model.opcodes | Where-Object { $_.code -in $codes } | ForEach-Object { [int]$_.code })
  $manual = @($codes | Where-Object { $_ -notin $mappedCodes } | ForEach-Object { '0x{0:X2}' -f $_ })
  @{ opcodePattern = ('0x{0:X2}-0x{1:X2}' -f $base,($base+7)); name = ($aluNames[$group] + ' r/(HL)'); sourceMapped = $mapped; manualRtlOnly = $manual }
}
$pairSpecs = @(
  @{ name='LXI rp,d16'; pattern='01/11/21/31'; codes=@(0x01,0x11,0x21,0x31) },
  @{ name='INX rp'; pattern='03/13/23/33'; codes=@(0x03,0x13,0x23,0x33) },
  @{ name='DCX rp'; pattern='0B/1B/2B/3B'; codes=@(0x0b,0x1b,0x2b,0x3b) },
  @{ name='DAD rp'; pattern='09/19/29/39'; codes=@(0x09,0x19,0x29,0x39) }
)
$pairInstructions = foreach ($spec in $pairSpecs) {
  $mapped = @($model.opcodes | Where-Object { $_.code -in $spec.codes } | Sort-Object code | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method })
  $mappedCodes = @($model.opcodes | Where-Object { $_.code -in $spec.codes } | ForEach-Object { [int]$_.code })
  $manual = @($spec.codes | Where-Object { $_ -notin $mappedCodes } | ForEach-Object { '0x{0:X2}' -f $_ })
  @{ opcodePattern=$spec.pattern; name=$spec.name; sourceMapped=$mapped; manualRtlOnly=$manual; note=if($spec.name -eq 'DAD rp'){'updates HL and CY only; other flags preserved'}else{'all register-pair destinations, including SP, are decoded'} }
}
$semanticDependencies = @($model.memberDependencies | ForEach-Object {
  @{ kind = $_.kind; sourceMember = $_.sourceMember; file = $_.file; line = $_.line; syntax = $_.syntax;
     targetSymbol = $_.targetSymbol; targetFile = $_.targetFile; targetLine = $_.targetLine }
})
$stackInstructionSpecs = @(
  @{ name='CALL addr16'; pattern='CD'; codes=@(0xcd) },
  @{ name='CC addr16'; pattern='C4/CC/D4/DC/E4/EC/F4/FC'; codes=@(0xc4,0xcc,0xd4,0xdc,0xe4,0xec,0xf4,0xfc) },
  @{ name='RET'; pattern='C9'; codes=@(0xc9) },
  @{ name='RC'; pattern='C0/C8/D0/D8/E0/E8/F0/F8'; codes=@(0xc0,0xc8,0xd0,0xd8,0xe0,0xe8,0xf0,0xf8) },
  @{ name='PUSH rp/PSW'; pattern='C5/D5/E5/F5'; codes=@(0xc5,0xd5,0xe5,0xf5) },
  @{ name='POP rp/PSW'; pattern='C1/D1/E1/F1'; codes=@(0xc1,0xd1,0xe1,0xf1) },
  @{ name='RST n'; pattern='C7/CF/D7/DF/E7/EF/F7/FF'; codes=@(0xc7,0xcf,0xd7,0xdf,0xe7,0xef,0xf7,0xff) }
)
$stackInstructions = foreach ($spec in $stackInstructionSpecs) {
  $mapped=@($model.opcodes | Where-Object { $_.code -in $spec.codes } | Sort-Object code | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method })
  $mappedCodes=@($model.opcodes | Where-Object { $_.code -in $spec.codes } | ForEach-Object { [int]$_.code })
  $manual=@($spec.codes | Where-Object { $_ -notin $mappedCodes } | ForEach-Object { '0x{0:X2}' -f $_ })
  @{ opcodePattern=$spec.pattern; name=$spec.name; sourceMapped=$mapped; manualRtlOnly=$manual; note='stack data uses the explicit memory request/ready handshake; see architecture.stack' }
}
$instructionDescriptors = @(
  @{ opcodePattern='40-7F except 76'; name='MOV r,r / MOV r,M / MOV M,r'; description='All 63 register/memory transfer encodings are implemented in the integrated RTL. The (HL) operand uses the agreed memory ready handshake; no cycle count is inferred from emulator timing.'; sourceMapped=$movSourceOpcodes; manualRtlOnly=$movManualOpcodes },
  @{ opcodePattern='00/08/10/18/20/28/30/38'; name='NOP aliases'; description='These eight encodings retire without changing architectural state; included because they appear as NOP entries in the emulator dispatch dictionary.'; sourceMapped=@($model.opcodes | Where-Object { $_.code -in @(0x00,0x08,0x10,0x18,0x20,0x28,0x30,0x38) } | Sort-Object code | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); manualRtlOnly=@() },
  @{ opcodePattern='04/0C/14/1C/24/2C/34/3C and 05/0D/15/1D/25/2D/35/3D'; name='INR/DCR r/(HL)'; description='All 16 encodings implemented with 8-bit wrap, Z/S/even parity, architectural auxiliary-carry behavior and carry preservation. (HL) uses a read-modify-write handshake.'; sourceMapped=@($model.opcodes | Where-Object { ($_.code -band 0xc7) -in 4,5 } | Sort-Object code | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); manualRtlOnly=@('0x0C INR_C','0x2C INR_L','0x2D DCR_L','0x34 INR_M'); sourceCaveat='source helper flag behavior is not equivalent; see sourceEvidence.semanticWarnings' }
) + @($aluInstructions) + @($pairInstructions) + @($stackInstructions) + @(
  @{ opcodePattern='C2/CA/D2/DA/E2/EA/F2/FA'; name='Jcc addr16'; sourceMapped=@($model.opcodes | Where-Object { $_.code -in @(0xc2,0xca,0xd2,0xda,0xe2,0xea,0xf2,0xfa) } | Sort-Object code | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); manualRtlOnly=@() },
  @{ opcodePattern='C3/CB'; name='JMP addr16'; sourceMapped=@($model.opcodes | Where-Object { $_.code -in @(0xc3,0xcb) } | Sort-Object code | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); manualRtlOnly=@() },
  @{ opcodePattern='06/0E/16/1E/26/2E/36/3E'; name='MVI r/(HL), immediate'; description='All eight destinations are implemented; MVI (HL) commits the immediate through the memory request/ready write channel.'; sourceMapped=@($model.opcodes | Where-Object { ($_.code -band 0xc7) -eq 0x06 } | Sort-Object code | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); manualRtlOnly=@('0x16 MVI_D') },
  @{ opcodePattern='C6/CE/D6/DE/E6/EE/F6/FE'; name='ADI/ACI/SUI/SBI/ANI/XRI/ORI/CPI'; description='All eight immediate ALU opcodes fetch one byte, use the shared arithmetic/logical datapath, preserve A on CPI, and update selected architectural result flags.'; sourceMapped=@($model.opcodes | Where-Object { $_.code -in @(0xc6,0xce,0xd6,0xde,0xe6,0xee,0xf6,0xfe) } | Sort-Object code | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); manualRtlOnly=@('0xDE SBI (no source dispatch)') },
  @{ opcodePattern='02/0A/12/1A'; name='STAX/LDAX BC/DE'; description='Accumulator stores and loads use BC or DE as the byte address and hold the memory handshake until accepted.'; sourceMapped=@($model.opcodes | Where-Object { $_.code -in @(0x02,0x0a,0x12,0x1a) } | Sort-Object code | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); manualRtlOnly=@() },
  @{ opcodePattern='32/3A'; name='STA/LDA addr16'; description='Direct accumulator store/load fetch the 16-bit little-endian address and use the memory handshake for the data transfer.'; sourceMapped=@($model.opcodes | Where-Object { $_.code -in @(0x32,0x3a) } | Sort-Object code | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); manualRtlOnly=@() },
  @{ opcodePattern='22/2A'; name='SHLD/LHLD addr16'; description='Two consecutive acknowledged memory transfers move L then H at the little-endian immediate address and the following byte.'; sourceMapped=@($model.opcodes | Where-Object { $_.code -in @(0x22,0x2a) } | Sort-Object code | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); manualRtlOnly=@() },
  @{ opcodePattern='E3/E9/EB'; name='XTHL/PCHL/XCHG'; description='XTHL reads the stack word then writes the former HL word without changing SP; PCHL loads PC from HL; XCHG swaps HL and DE.'; sourceMapped=@($model.opcodes | Where-Object { $_.code -in @(0xe3,0xe9,0xeb) } | Sort-Object code | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); manualRtlOnly=@() },
  @{ opcode='27'; name='DAA'; description='Manual hardware extension from Intel 8080 Assembly Language Programming Manual Rev C: low-digit correction from A[3:0]/AC, then high-digit correction from adjusted A[7:4]/CY; recalculates SZP, AC on low-nibble carry, and sets CY on high-nibble carry while preserving incoming CY otherwise.'; sourceMapped=@(); manualRtlOnly=@('0x27'); sourceEvidence='No DAA opcode or implementation is present in emu8080.Core dispatch; Intel manual § DAA is the behavioral basis.'; specification='https://mark-ogden.uk/files/intel/publications/98-004C%208080%20Assembly%20Language%20Programming%20Manual-Jan76.pdf' },
  @{ opcode='F9'; name='SPHL'; description='Copies HL into SP; manually implemented because opcode 0xF9 is absent from the emulator dispatch.'; sourceMapped=@(); manualRtlOnly=@('0xF9'); sourceEvidence='No SPHL opcode or implementation is present in emu8080.Core dispatch.' },
  @{ opcode='C6'; name='ADI immediate'; source='Ops.ADI'; sourceMapped=@($model.opcodes | Where-Object code -eq 0xc6 | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); note='updates SZPC; source Ops.ADI preserves AC' },
  @{ opcode='76'; name='HLT'; source='Ops.HLT'; sourceMapped=@($model.opcodes | Where-Object code -eq 0x76 | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); note='Intel 8080 specification behavior: HLT leaves PC at the next sequential byte; source Ops.HLT comments no increment, but the emulator fetch path increments PC before dispatch. Bridge RTL follows spec and bench asserts next-byte PC.' },
  @{ opcode='F3'; name='DI'; source='Ops.DI'; sourceMapped=@($model.opcodes | Where-Object code -eq 0xf3 | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); note='interrupt semantics follow the user-confirmed hardware contract' },
  @{ opcode='FB'; name='EI'; source='Ops.EI'; sourceMapped=@($model.opcodes | Where-Object code -eq 0xfb | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); note='enable delayed through one following instruction' },
  @{ opcode='D3'; name='OUT port'; source='Ops.OUT'; sourceMapped=@($model.opcodes | Where-Object code -eq 0xd3 | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); note='source is a TODO that only advances PC; RTL drives A and port through selected ready/valid hardware contract' },
  @{ opcode='DB'; name='IN port'; source='Ops.IN_D8'; sourceMapped=@($model.opcodes | Where-Object code -eq 0xdb | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); note='source is a TODO that only advances PC; RTL samples input data into A through selected ready/valid hardware contract' },
  ,@{ opcodePattern='07/0F/17/1F'; name='RLC/RRC/RAL/RAR'; sourceMapped=@($model.opcodes | Where-Object { $_.code -in @(0x07,0x0f,0x17,0x1f) } | Sort-Object code | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); manualRtlOnly=@('0x17 RAL'); sourceEvidence='RAL 17h has no reachable entry in Cpu._ops and no Ops.RAL method in the current source; this encoding is included as a manual Intel 8080 instruction implementation.'; note='updates A and CY only; S/Z/P/AC preserved' }
  ,@{ opcode='2F'; name='CMA'; source='Ops.CMA'; sourceMapped=@($model.opcodes | Where-Object code -eq 0x2f | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); note='ones complement A; flags preserved' }
  ,@{ opcode='37'; name='STC'; source='Ops.STC'; sourceMapped=@($model.opcodes | Where-Object code -eq 0x37 | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); note='sets CY; other flags preserved' }
  ,@{ opcode='3F'; name='CMC'; source='Ops.CMC'; sourceMapped=@($model.opcodes | Where-Object code -eq 0x3f | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); note='complements CY; other flags preserved' }
)
$descriptorDispatchCodes = @(
  foreach ($descriptor in $instructionDescriptors) {
    foreach ($mapping in @($descriptor.sourceMapped)) {
      if ([string]$mapping -match '^0x(?<code>[0-9A-Fa-f]{2})\s') { [Convert]::ToInt32($Matches.code,16) }
    }
  }
) | Sort-Object -Unique
$sourceDispatchCodes = @($model.opcodes | ForEach-Object { [int]$_.code } | Sort-Object -Unique)
$unrepresentedDispatchCodes = @($sourceDispatchCodes | Where-Object { $_ -notin $descriptorDispatchCodes })
if ($unrepresentedDispatchCodes.Count -gt 0) {
  $missingText = ($unrepresentedDispatchCodes | ForEach-Object { '0x{0:X2}' -f $_ }) -join ', '
  throw "Refusing to export an incomplete source-opcode map; missing descriptors for: $missingText"
}
$immediateCodes = @(0xc6,0xce,0xd6,0xde,0xe6,0xee,0xf6,0xfe)
$immediateDescriptor = @($instructionDescriptors | Where-Object name -eq 'ADI/ACI/SUI/SBI/ANI/XRI/ORI/CPI')[0]
$immediateMapped = @($immediateDescriptor.sourceMapped | ForEach-Object { if ([string]$_ -match '^0x(?<code>[0-9A-Fa-f]{2})\s') { [Convert]::ToInt32($Matches.code,16) } })
$immediateManual = @($immediateDescriptor.manualRtlOnly | ForEach-Object { if ([string]$_ -match '^0x(?<code>[0-9A-Fa-f]{2})') { [Convert]::ToInt32($Matches.code,16) } })
if (@($immediateMapped | Where-Object { $_ -in $immediateManual }).Count -gt 0 -or
    @(($immediateMapped + $immediateManual) | Sort-Object -Unique).Count -ne $immediateCodes.Count -or
    @($immediateCodes | Where-Object { $_ -notin ($immediateMapped + $immediateManual) }).Count -gt 0) {
  throw 'Immediate ALU descriptor mapped/manual opcode partition is incomplete or overlapping.'
}
$template = [ordered]@{
  format = 'CircuitWeave.Template.v1'
  status = 'editable partial Intel 8080 hardware model; eleven bridge RTL benches pass; incomplete ISA and not full conformance'
  manualImplementationRequired = $true
  architecture = [ordered]@{
    clock = 'clk rising edge'; reset = 'synchronous active-high'
    addressBits = 16; dataBits = 8; ioPortBits = 8; stackPointerBits = 16
    memory = @{ protocol = 'synchronous single-beat request/ready'; signals = @('mem_req','mem_write','mem_addr[15:0]','mem_wdata[7:0]','mem_ready','mem_rdata[7:0]'); requestAndPayloadStableUntilReady = $true; completion = 'rising edge with mem_req && mem_ready' }
    stack = @{ widthBits=16; growth='descending addresses'; callPush='write return high byte at SP-1, then low byte at SP-2; update SP after both acknowledged'; returnPop='read low byte at SP, then high byte at SP+1; update SP after both acknowledged'; pushPop='PUSH pair writes high at SP-1 then low at SP-2; POP reads low at SP then high at SP+1; PSW maps S,Z,0,AC,0,P,1,C'; status='CALL, conditional calls, RET, conditional returns, PUSH/POP BC/DE/HL/PSW and RST are implemented and covered by directed bridge benches' }
    io = @{ protocol = 'separate request/ready'; signals = @('io_req','io_write','io_port[7:0]','io_wdata[7:0]','io_ready','io_rdata[7:0]'); completion = 'rising edge with io_req && io_ready'; status = 'RTL implements IN immediate-port read into A and OUT immediate-port write from A under an explicit hardware contract; source Ops.IN_D8/OUT are TODO and do not establish this data behavior' }
    interrupt = @{ protocol = 'single level-sensitive irq with injected opcode'; signals = @('irq','irq_opcode[7:0]','irq_ack'); semantics = 'instruction boundary; accept while enabled; one-cycle acknowledge; reset disables; DI immediate; EI after following instruction'; status = 'basic sequencing exists in subset RTL; emulator queue/dequeue behavior remains semantically distinct and ambiguous' }
  }
  blocks = @('registers_flags','instruction_control','execution','memory','io_interrupt')
  sourceEvidence = [ordered]@{
    sourceRoot = $model.sourceRoot; inventorySchema = $model.schema
    inventorySha256 = (Get-FileHash -LiteralPath $inventory -Algorithm SHA256).Hash.ToLowerInvariant()
    projectInventory = if ($project) { @{ path = $projectInventory; schema = $project.schema; sha256 = (Get-FileHash -LiteralPath $projectInventory -Algorithm SHA256).Hash.ToLowerInvariant(); targetFramework = $project.targetFramework; packageReferences = @($project.packageReferences) } } else { $null }
    files = @($model.files); disposition = $model.disposition
    semanticWarnings = @('Ops.INR_* and Ops.DCR_* map in dispatch syntax, but source helper flag behavior differs: Increment derives AC from the result low nibble, Decrement omits AC, and DCR_C calls CalcSZPC(byte), mutating CY. ADI/SUI/SBI source paths do not update AC; ACI does. Source AOR computes A & operand and ORI_D calls AOR, so the source immediate OR path is AND. Source XOR and ORA_M call CalcSZPC(byte), whose CalcCarryFlag(byte) sets CY to result bit 0; both leave AC unchanged. ORA_B explicitly clears CY/AC. Source ANA clears AC, unlike the selected 8080 ANA AC bit-3 OR rule in candidate RTL. Flags.PSW stores the source internal flag byte as-is; its declared AC mask is 0x08 and it does not impose the Intel PSW reserved-bit pattern, while the RTL implements architectural PSW bit positions and bit 1 set. Ops.IN_D8 and Ops.OUT are TODO stubs and their bus data behavior is a hardware design choice, not observed emulator behavior. The RTL follows documented 8080 behavior where specified; full emulator equivalence is not claimed.')
    dispatchEntries = @($model.opcodes).Count; dispatchOpcodes = $sourceDispatchOpcodes; dispatchSourceMap = @($sourceDispatch)
    semanticProjections = @($semanticProjections)
    dependencyOccurrences = $semanticDependencies.Count; semanticDependencies = $semanticDependencies
    constructsRequiringManualMapping = @('Dictionary<byte, delegate> dispatch and fallback policy','interrupt queue/event behavior','logger','byte-array memory and video buffer','imperative control flow and emulator cycle returns')
  }
  selectedTypes = @($selectedTypes)
  flagMapping = @{ inr = 'Z/S/P from result; AC set when input low nibble is F; CY preserved'; dcr = 'Z/S/P from result; AC set when input low nibble is 0; CY preserved'; addAdc = 'result flags plus carry and nibble carry'; subSbbCmp = 'result flags plus borrow in CY and auxiliary carry-out from equivalent two''s-complement nibble addition (inverse of widened nibble-subtract bit 4); CMP leaves A unchanged. Intel-spec decision; C# flag behavior is separately recorded and not assumed equivalent'; logical = 'ANA: CY=0 and selected AC=A[3]|operand[3]; XRA/ORA: CY=0, AC=0. Source differences are operation-specific: ANA clears AC; XOR derives CY from result[0] and preserves AC; ORA_M derives CY from result[0] and preserves AC; ORA_B clears CY/AC; source ORI computes AND via AOR.'; evidence = 'Intel 8080 Assembly Language Programming Manual Rev C; source projections preserve emulator behavior separately' }
  implementedSubset = [ordered]@{
    status = 'partial; manually authored; 11 directed Icarus 12.0 benches PASS; not exhaustive'
    rtl = @($rtlFiles)
    testbenches = @($testbenches)
    instructions = @($instructionDescriptors)
    unsupported = 'The emulator dispatch contains 166 distinct opcode entries; its remaining/unassigned encodings are not all architectural instructions. The documented instruction set is decoded, with DAA and SPHL added manually because they are absent from the source model. This RTL is not exhaustively verified across every opcode/state combination. I/O data behavior is manually chosen because source Ops methods are TODO. Timing, full bus side effects, and interrupt queue semantics are not equivalent or fully implemented.'
    sourceMappedDispatchOpcodes = @($sourceDispatchOpcodes)
    manualRtlOnlyOpcodes = @($instructionDescriptors | ForEach-Object { $_.manualRtlOnly } | Where-Object { $null -ne $_ })
  }
  sourceMap = @(
    @{ rtl='rtl/cw8080_subset_cpu_mov_alu_mvi_stack_pushpop_io_memops_daa_specana_hlt_v19_20261119.sv'; blocks=@('instruction_control','registers_flags','execution','memory','io_interrupt'); sources=@('Cpu.Step','Cpu.ProcessOp','Ops.MOV_*','Ops.INR_*','Ops.DCR_*','Utils.Increment','Utils.Decrement','Ops.ADD_*','Ops.ADC_*','Ops.SUB_*','Ops.SBB_*','Ops.ANA_*','Ops.XRA_*','Ops.ORA_*','Ops.CMP_*','Ops.MVI_*','Ops.STAX_B','Ops.STAX_D','Ops.LDAX_B','Ops.LDAX_D','Ops.STA','Ops.LDA','Ops.SHLD','Ops.LHLD','Ops.XTHL','Ops.XCHG','Ops.PCHL','Ops.ADI','Ops.ACI','Ops.SUI','Ops.SBI','Ops.ANI','Ops.XRI','Ops.ORI_D','Ops.CPI','Ops.LXI_*','Ops.INX_*','Ops.DCX_*','Ops.DAD_*','Ops.JMP','Ops.JNZ','Ops.JZ','Ops.JNC','Ops.JC','Ops.JPO','Ops.JPE','Ops.JP','Ops.JM','Ops.CALL','Ops.CNZ','Ops.CZ','Ops.CP','Ops.CM','Ops.RET','Ops.RNZ','Ops.RZ','Ops.RNC','Ops.RC','Ops.RPE','Ops.RM','Ops.RST_5','Ops.RST_7','Ops.PUSH_*','Ops.POP_*','Ops.RLC','Ops.RRC','Ops.RAR','Ops.CMA','Ops.STC','Ops.CMC','Ops.IN_D8','Ops.OUT','Ops.HLT','Ops.DI','Ops.EI','Registers.ProgramCounter','Registers.A/B/C/D/E/H/L','Registers.BC/DE/HL/StackPointer','Flags.CalcSZPC','Flags.CalcAuxCarryFlag','Memory indexer','Bus.Interrupt') },
    @{ rtl='Intel8080/Rtl/cw8080_add.sv'; blocks=@('execution','registers_flags'); sources=@('Ops.ADD_*','Ops.ADC_*','Ops.ADI','Flags.CalcSZPC','Flags.CalcAuxCarryFlag','Registers.A') }
  )
  validation = [ordered]@{
    roslyn = 'in-memory semantic compilation and source mapping; no imported-project build or execution; diagnostics retained in source inventory'
    sourceDispatchCount = @($model.opcodes).Count
    sourceDispatchCoverage = [ordered]@{ dispatchEntries=$sourceDispatchCodes.Count; entriesRepresentedByInstructionDescriptors=($sourceDispatchCodes.Count - $unrepresentedDispatchCodes.Count); unrepresentedOpcodes=@($unrepresentedDispatchCodes | ForEach-Object { '0x{0:X2}' -f $_ }); interpretation='source-dispatch to model-descriptor mapping only; it does not prove RTL syntax, behavior, or simulation' }
    systemVerilogSyntax = if ($rtlStatus -eq 'PASS') { 'compiled by the configured HDL runner' } else { 'not checked; configured HDL compiler is unavailable' }
    simulationOrFormal = if ($rtlStatus -eq 'PASS') { 'integrated RTL testbench executed; this is partial-subset simulation only' } else { 'not performed; HDL simulator unavailable or runner did not pass' }
    rtlRunner = @{ status = $rtlStatus; log = ([IO.Path]::GetRelativePath($allowedRoot,$rtlLog).Replace('\','/')); logSha256 = (Get-FileHash -LiteralPath $rtlLog -Algorithm SHA256).Hash.ToLowerInvariant() }
    mlir = 'only bounded result slices are lowered to MLIR; no full CPU or sequential RTL is generated'
    scalarMlirArtifacts = @($mlirEvidence)
    candidateEvidence = @($candidateEvidence)
  }
}
$json = ConvertTo-Json -InputObject $template -Depth 32
[IO.File]::WriteAllText($output, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "Created: $output"
