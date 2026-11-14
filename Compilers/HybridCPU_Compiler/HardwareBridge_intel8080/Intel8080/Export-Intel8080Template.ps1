param(
  [string]$InventoryPath = 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\bridge-semantic-deps-3\source-inventory.json',
  [string]$ProjectInventoryPath = 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\project-inventory.json',
  [string]$OutputFile = 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\hardware-model-8080-bridge-derived.cwtemplate.json'
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
$project = if ($projectInventory) { Get-Content -LiteralPath $projectInventory -Raw | ConvertFrom-Json } else { $null }
if ($project -and $project.schema -ne 'hardwarebridge.intel8080.msbuild-import-inventory/v1') { throw 'Unsupported project inventory schema.' }

$root = 'C:\Users\Yuriy\Desktop\HybridCPU Compilers\Compilers\HybridCPU_Compiler\HardwareBridge_intel8080\Intel8080\Rtl'
$rtlSpecs = @(
  @{ path = 'cw8080_subset_cpu.sv'; artifactPath = 'cw8080_subset_cpu_mov_incdec_alu.sv'; status = 'manual partial implementation; MOV, INR/DCR and complete register/memory ALU opcode groups, handshake stability and HLT/IRQ behavior included; not simulated or proven' },
  @{ path = 'cw8080_add.sv'; artifactPath = 'cw8080_add.sv'; status = 'manual arithmetic datapath variants; not integrated as a verified CPU' }
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
$tbArtifact = Join-Path $allowedRoot 'tests\tb_cw8080_subset_cpu_mov_incdec_alu.sv'
if (-not (Test-Path -LiteralPath $tbPath -PathType Leaf) -or -not (Test-Path -LiteralPath $tbArtifact -PathType Leaf)) { throw 'Updated integrated CPU testbench or its artifact copy is missing.' }
$tbHash = (Get-FileHash -LiteralPath $tbPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($tbHash -ne (Get-FileHash -LiteralPath $tbArtifact -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Bridge CPU testbench differs from its artifact copy.' }
$aluTbPath = Join-Path $root 'tb_cw8080_add.sv'
$aluTbArtifact = Join-Path $allowedRoot 'tests\tb_cw8080_add.sv'
if (-not (Test-Path -LiteralPath $aluTbPath -PathType Leaf) -or -not (Test-Path -LiteralPath $aluTbArtifact -PathType Leaf)) { throw 'ALU testbench or its artifact copy is missing.' }
$aluTbHash = (Get-FileHash -LiteralPath $aluTbPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($aluTbHash -ne (Get-FileHash -LiteralPath $aluTbArtifact -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Bridge ALU testbench differs from its artifact copy.' }
$testbenches = @(
  @{ path = 'tests/tb_cw8080_add.sv'; sha256 = $aluTbHash; status = 'ADD/ADC datapath testbench; authored but not simulated' },
  @{ path = 'tests/tb_cw8080_subset_cpu_mov_incdec_alu.sv'; sha256 = $tbHash; status = 'checks MOV, INR/DCR and ALU arithmetic/logical/compare register behavior, flags, CY, ready/payload stability, memory writes, idle HALT, EI delay and IRQ wake; authored but not simulated' }
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
$semanticDependencies = @($model.memberDependencies | ForEach-Object {
  @{ kind = $_.kind; sourceMember = $_.sourceMember; file = $_.file; line = $_.line; syntax = $_.syntax;
     targetSymbol = $_.targetSymbol; targetFile = $_.targetFile; targetLine = $_.targetLine }
})
$template = [ordered]@{
  format = 'CircuitWeave.Template.v1'
  status = 'editable partial Intel 8080 hardware model; manually authored RTL is not simulator-verified'
  manualImplementationRequired = $true
  architecture = [ordered]@{
    clock = 'clk rising edge'; reset = 'synchronous active-high'
    addressBits = 16; dataBits = 8; ioPortBits = 8
    memory = @{ protocol = 'synchronous single-beat request/ready'; signals = @('mem_req','mem_write','mem_addr[15:0]','mem_wdata[7:0]','mem_ready','mem_rdata[7:0]'); requestAndPayloadStableUntilReady = $true; completion = 'rising edge with mem_req && mem_ready' }
    io = @{ protocol = 'separate request/ready'; signals = @('io_req','io_write','io_port[7:0]','io_wdata[7:0]','io_ready','io_rdata[7:0]'); completion = 'rising edge with io_req && io_ready'; status = 'TODO: IN_D8 and OUT source semantics are incomplete; adapter RTL does not implement I/O' }
    interrupt = @{ protocol = 'single level-sensitive irq with injected opcode'; signals = @('irq','irq_opcode[7:0]','irq_ack'); semantics = 'instruction boundary; accept while enabled; one-cycle acknowledge; reset disables; DI immediate; EI after following instruction'; status = 'basic sequencing exists in subset RTL; emulator queue/dequeue behavior remains semantically distinct and ambiguous' }
  }
  blocks = @('registers_flags','instruction_control','execution','memory','io_interrupt')
  sourceEvidence = [ordered]@{
    sourceRoot = $model.sourceRoot; inventorySchema = $model.schema
    inventorySha256 = (Get-FileHash -LiteralPath $inventory -Algorithm SHA256).Hash.ToLowerInvariant()
    projectInventory = if ($project) { @{ path = $projectInventory; schema = $project.schema; sha256 = (Get-FileHash -LiteralPath $projectInventory -Algorithm SHA256).Hash.ToLowerInvariant(); targetFramework = $project.targetFramework; packageReferences = @($project.packageReferences) } } else { $null }
    files = @($model.files); disposition = $model.disposition
    semanticWarnings = @('Ops.INR_* and Ops.DCR_* map in dispatch syntax, but source helper flag behavior differs: Increment derives AC from the result low nibble, Decrement omits AC, and DCR_C calls CalcSZPC(byte), mutating CY. ORA_M also leaves CY/AC unchanged unlike the architectural logical-operation rule. The RTL follows the Intel manual for these flags; full emulator equivalence is not claimed.')
    dispatchEntries = @($model.opcodes).Count; dispatchOpcodes = $sourceDispatchOpcodes; dispatchSourceMap = @($sourceDispatch)
    dependencyOccurrences = $semanticDependencies.Count; semanticDependencies = $semanticDependencies
    constructsRequiringManualMapping = @('Dictionary<byte, delegate> dispatch and fallback policy','interrupt queue/event behavior','logger','byte-array memory and video buffer','imperative control flow and emulator cycle returns')
  }
  selectedTypes = @()
  flagMapping = @{ inr = 'Z/S/P from result; AC set when input low nibble is F; CY preserved'; dcr = 'Z/S/P from result; AC set when input low nibble is 0; CY preserved'; evidence = 'Intel 8080 manual plus hardware architecture decision; differs from source helper behavior noted in sourceEvidence.semanticWarnings' }
  implementedSubset = [ordered]@{
    status = 'partial; manually authored; integrated RTL not simulator-verified'
    rtl = @($rtlFiles)
    testbenches = @($testbenches)
    instructions = @(
      @{ opcodePattern='40-7F except 76'; name='MOV r,r / MOV r,M / MOV M,r'; description='All 63 register/memory transfer encodings are implemented in the integrated RTL. The (HL) operand uses the agreed memory ready handshake; no cycle count is inferred from emulator timing.'; sourceMapped=$movSourceOpcodes; manualRtlOnly=$movManualOpcodes },
      @{ opcodePattern='04/0C/14/1C/24/2C/34/3C and 05/0D/15/1D/25/2D/35/3D'; name='INR/DCR r/(HL)'; description='All 16 encodings implemented with 8-bit wrap, Z/S/even parity, architectural auxiliary-carry behavior and carry preservation. (HL) uses a read-modify-write handshake.'; sourceMapped=@($model.opcodes | Where-Object { ($_.code -band 0xc7) -in 4,5 } | Sort-Object code | ForEach-Object { '0x{0:X2} {1}' -f $_.code,$_.method }); manualRtlOnly=@('0x0C INR_C','0x2C INR_L','0x2D DCR_L','0x34 INR_M'); sourceCaveat='source helper flag behavior is not equivalent; see sourceEvidence.semanticWarnings' },
      @($aluInstructions),
      @{ opcode='3E'; name='MVI A, immediate'; source='Ops.MVI_A' },
      @{ opcode='C6'; name='ADI immediate'; source='Ops.ADI'; note='updates SZPC; source Ops.ADI preserves AC' },
      @{ opcode='76'; name='HLT'; source='Ops.HLT'; note='adapter RTL returns PC to the HLT opcode; source correspondence requires instruction-level verification' },
      @{ opcode='F3'; name='DI'; source='Ops.DI'; note='interrupt semantics follow the user-confirmed hardware contract' },
      @{ opcode='FB'; name='EI'; source='Ops.EI'; note='enable delayed through one following instruction' }
    )
    unsupported = 'Other opcodes set unsupported; complete register/memory writes, I/O, full flag semantics, instruction lengths and architectural effects remain unfinished.'
    sourceMappedDispatchOpcodes = @($sourceDispatchOpcodes)
    manualRtlOnlyOpcodes = @($aluInstructions | ForEach-Object { $_.manualRtlOnly })
  }
  sourceMap = @(
    @{ rtl='rtl/cw8080_subset_cpu_mov_incdec.sv'; blocks=@('instruction_control','registers_flags','execution','memory','io_interrupt'); sources=@('Cpu.Step','Cpu.ProcessOp','Ops.MOV_*','Ops.INR_*','Ops.DCR_*','Utils.Increment','Utils.Decrement','Ops.ADD_*','Ops.ADC_*','Ops.MVI_A','Ops.ADI','Ops.HLT','Ops.DI','Ops.EI','Registers.ProgramCounter','Registers.A/B/C/D/E/H/L','Flags.CalcSZPC','Flags.CalcAuxCarryFlag','Memory indexer','Bus.Interrupt') },
    @{ rtl='Intel8080/Rtl/cw8080_add.sv'; blocks=@('execution','registers_flags'); sources=@('Ops.ADD_*','Ops.ADC_*','Ops.ADI','Flags.CalcSZPC','Flags.CalcAuxCarryFlag','Registers.A') }
  )
  validation = [ordered]@{
    roslyn = 'in-memory semantic compilation and source mapping; no imported-project build or execution; diagnostics retained in source inventory'
    sourceDispatchCount = @($model.opcodes).Count
    sourceDispatchCoverage = 'source dictionary observations only; no claim of complete Intel 8080 opcode coverage'
    systemVerilogSyntax = 'not checked by a dedicated HDL parser in this export'
    simulationOrFormal = 'not performed; simulator availability is checked separately'
    mlir = 'not generated; this bridge has no qualified C#-to-MLIR lowering for this model'
  }
}
$json = ConvertTo-Json -InputObject $template -Depth 32
[IO.File]::WriteAllText($output, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "Created: $output"
