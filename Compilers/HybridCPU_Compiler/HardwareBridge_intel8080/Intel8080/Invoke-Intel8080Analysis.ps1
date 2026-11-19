param(
  [string]$SourceRoot = 'C:\Users\Yuriy\Desktop\8080-emulator-net-master\emu8080.Core',
  [string]$OutputDirectory = 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\bridge'
)
$ErrorActionPreference = 'Stop'
$toolRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'HardwareBridge.Intel8080.csproj'
$sourceRootFull = [IO.Path]::GetFullPath($SourceRoot)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$allowedArtifactsRoot = [IO.Path]::GetFullPath('C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080').TrimEnd('\') + '\'
if (-not $outputRoot.StartsWith($allowedArtifactsRoot,[StringComparison]::OrdinalIgnoreCase)) { throw "8080 analysis output must be inside $allowedArtifactsRoot : $outputRoot" }
if (-not (Test-Path -LiteralPath $sourceRootFull -PathType Container)) { throw "Source directory missing: $sourceRootFull" }
foreach ($path in @($outputRoot,$sourceRootFull)) {
  for ($d=[IO.DirectoryInfo]::new($path); $null -ne $d; $d=$d.Parent) {
    if (Test-Path -LiteralPath $d.FullName) { if (($d.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked path refused: $($d.FullName)" } }
  }
}
if (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) { [void][IO.Directory]::CreateDirectory($outputRoot) }
$selection = Join-Path $outputRoot 'selected-sources.json'
$inventory = Join-Path $outputRoot 'source-inventory.json'
$opcodeIr = Join-Path $outputRoot 'opcode-ir-candidate.json'
$methodIr = Join-Path $outputRoot 'ops-method-ir.json'
$addBehavior = Join-Path $outputRoot 'add-behavior-projection.json'
$adcBehavior = Join-Path $outputRoot 'adc-behavior-projection.json'
$sbbBehavior = Join-Path $outputRoot 'sbb-behavior-projection.json'
$subBehavior = Join-Path $outputRoot 'sub-behavior-projection.json'
$dadBehavior = Join-Path $outputRoot 'dad-behavior-projection.json'
$logicalBehavior = Join-Path $outputRoot 'logical-behavior-projection.json'
$report = Join-Path $outputRoot 'analysis-report.md'
foreach($path in @($selection,$inventory,$opcodeIr,$methodIr,$addBehavior,$adcBehavior,$sbbBehavior,$subBehavior,$dadBehavior,$logicalBehavior,$report)) { if(Test-Path -LiteralPath $path) { throw "Existing output refused: $path" } }
$names = @('Bus.cs','Cpu.cs','Flags.cs','Memory.cs','Ops.cs','Registers.cs','Utils.cs')
$chosen = @($names | ForEach-Object { $candidate=Join-Path $sourceRootFull $_; if(Test-Path -LiteralPath $candidate -PathType Leaf){[IO.Path]::GetFullPath($candidate)}else{throw "Required source missing: $candidate"} })
[IO.File]::WriteAllText($selection,($chosen | ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
& dotnet run --project $project -- $sourceRootFull $selection $inventory
if($LASTEXITCODE -ne 0){throw "8080 source analysis failed with exit code $LASTEXITCODE"}
$model=Get-Content -LiteralPath $inventory -Raw | ConvertFrom-Json
$lines=@('# Intel 8080 source analysis via HardwareBridge_intel8080','','Disposition: Roslyn in-memory semantic compilation for symbol binding; no project build, emit or source execution. Results remain unqualified for hardware semantics.','',"Source root: ``$sourceRootFull``","Files captured: $($model.files.Count)","Types found: $($model.types.Count)","Literal opcode entries parsed: $($model.opcodes.Count)",'','## Files and hashes')
foreach($f in $model.files){$lines+="- ``$($f.Path)`` · $($f.Sha256) · $($f.Length) bytes"}
$lines+=@('','## Types')
foreach($t in $model.types){$lines+="### $($t.Namespace).$($t.Name) ($($t.Kind))"; $lines+="Source: ``$($t.File):$($t.Line)``"; $lines+='Members: '+(($t.Members|ForEach-Object{"$($_.Kind) $($_.Name) [$($_.Line):$($_.Column)]"}) -join '; '); $lines+='Warnings: '+(($t.Warnings) -join '; '); $lines+=''}
$lines+=@('## Dispatch table','',"Entries: $($model.opcodes.Count)",'','| Opcode | Method | Source | Line |','|---|---|---|---:|')
foreach($op in $model.opcodes){$lines+='| 0x{0:X2} | {1} | `{2}` | {3} |' -f $op.Code,$op.Method,$op.File,$op.Line}
$ir=Get-Content -LiteralPath $opcodeIr -Raw | ConvertFrom-Json
$methods=Get-Content -LiteralPath $methodIr -Raw | ConvertFrom-Json
$add=Get-Content -LiteralPath $addBehavior -Raw | ConvertFrom-Json
$adc=Get-Content -LiteralPath $adcBehavior -Raw | ConvertFrom-Json
$sbb=Get-Content -LiteralPath $sbbBehavior -Raw | ConvertFrom-Json
$sub=Get-Content -LiteralPath $subBehavior -Raw | ConvertFrom-Json
$dad=Get-Content -LiteralPath $dadBehavior -Raw | ConvertFrom-Json
$logical=Get-Content -LiteralPath $logicalBehavior -Raw | ConvertFrom-Json
$dependencyGroups=@($model.memberDependencies|Group-Object kind|Sort-Object Name)
$lines+=@('','## Source-mapped member dependencies','',"Dependency occurrences: $($model.memberDependencies.Count)","Occurrences with in-project source target: $(@($model.memberDependencies|Where-Object{$_.targetFile}).Count)",'| Kind | Count |','|---|---:|')
foreach($group in $dependencyGroups){$lines+='| {0} | {1} |' -f $group.Name,$group.Count}
$lines+=@('','The `memberDependencies` array in `source-inventory.json` records direct Roslyn-bound method calls, object constructions and field/property/event references with source and target locations where available. It is an occurrence inventory, not complete dataflow, dynamic dispatch, or proof of hardware behavior.','','## Source-mapped opcode IR candidate','',"IR entries: $($ir.entries.Count)",'Each opcode is bound to an Ops method and source location when Roslyn resolves it; the hardware operation is deliberately marked TODO. See `opcode-ir-candidate.json` for editable entries.','','## Ops method structure projection','',"Methods inventoried: $($methods.methods.Count)","Methods with operation nodes: $(@($methods.methods|Where-Object{$_.operations.Count -gt 0}).Count)",'The `ops-method-ir.json` file retains source lines for assignments, calls, branches, arithmetic and indexed accesses. Unclassified constructs are preserved separately. This is still a syntax projection.','','## Hardware status','',($model.hardwareDecisions -join "`n- "),'',($model.unsupportedFeatures -join "`n- "),'','This bridge does not emit MLIR, elaborated HWIR modules or SystemVerilog. Opcode mapping is source evidence, not functional instruction verification.')
$lines+=@('','## Source-pinned ADD behavior projection','',"Schema: $($add.schema)","Dispatch entries: $($add.dispatch.Count)","Projected equations: $($add.equations.Count)",'`add-behavior-projection.json` is pinned to reviewed SHA-256 snapshots of Ops.cs, Flags.cs and Utils.cs, then checked with Roslyn text patterns and parity-table enumeration. It covers only the four source-dispatched ADD opcodes; it does not generate MLIR/SystemVerilog or describe the CPU FSM. Any source hash change fails export pending review.')
$lines+=@('','## Source-pinned ADC behavior projection','',"Schema: $($adc.schema)","Dispatch entries: $($adc.dispatch.Count)","Projected equations: $($adc.equations.Count)",'`adc-behavior-projection.json` exposes carry-in as an explicit input, pins the same reviewed source snapshots and records only source-dispatched ADC opcodes 88h, 89h, 8Bh-8Eh. Opcodes 8Ah and 8Fh are absent from this emulator dispatch. This declarative projection does not lower flags or CPU sequencing to RTL.')
$lines+=@('','## Source/spec-split SBB behavior projection','',"Schema: $($sbb.schema)","Dispatch entries: $($sbb.dispatch.Count)","Source equations: $($sbb.equations.Count)",'`sbb-behavior-projection.json` records source-derived SBB behavior and a separate Intel-spec auxiliary-carry selection. The C# helper omits incoming CY from its AC helper call; candidate RTL includes borrow-in per the documented two''s-complement SBB rule. No flags or CPU sequencing are lowered by the scalar CircuitWeave path.')
$lines+=@('','## Source/spec-split SUB behavior projection','',"Schema: $($sub.schema)","Dispatch entries: $($sub.dispatch.Count)","Source equations/effects: $($sub.equations.Count)",'`sub-behavior-projection.json` distinguishes source-dispatched `90h SUB B` from immediate `D6h SUI`. The former updates AC via the source helper; the latter leaves AC unchanged. The candidate RTL follows the architectural AC update for both forms. CircuitWeave lowers only the wrapped subtract result.')
$lines+=@('','## Source-pinned DAD behavior projection','',"Schema: $($dad.schema)","Dispatch entries: $($dad.dispatch.Count)","Source equations/effects: $($dad.equations.Count)",'`dad-behavior-projection.json` pins all four DAD wrappers, shared `Registers.DAD`, and its `CalcCarryFlag(int)` helper. The model records the source-selected HL result, carry, preserved flags and PC increment. The sibling CircuitWeave export lowers only the 17-bit widened sum; CPU state and sequencing remain outside that scalar module.')
$lines+=@('','## Source/spec-split logical behavior projection','',"Schema: $($logical.schema)","Dispatch entries: $($logical.dispatch.Count)","Source equations/effects: $($logical.equations.Count)",'`logical-behavior-projection.json` records the source logical subset and helper side effects. It preserves the `AOR` AND operation used by source `ORI_D`, the byte `CalcSZPC` carry=bit-0 behavior used by XOR/ORA_M, and per-handler AC updates. The architecture selection keeps ORI as OR and logical carry clear. This projection is not a complete logical RTL or CPU lowering.')
[IO.File]::WriteAllLines($report,$lines,[Text.UTF8Encoding]::new($false))
Write-Output "Created: $selection`nCreated: $inventory`nCreated: $opcodeIr`nCreated: $methodIr`nCreated: $addBehavior`nCreated: $adcBehavior`nCreated: $sbbBehavior`nCreated: $subBehavior`nCreated: $dadBehavior`nCreated: $logicalBehavior`nCreated: $report"
