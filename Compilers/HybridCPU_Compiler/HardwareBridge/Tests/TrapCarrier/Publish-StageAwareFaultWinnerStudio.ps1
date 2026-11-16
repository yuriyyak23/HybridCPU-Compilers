param(
    [Parameter(Mandatory=$true)][string]$HwirRun,
    [Parameter(Mandatory=$true)][string]$RtlReceipt,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [string]$InventorySnapshot='C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\processor-20261114\trap-carrier-projection-v10\snapshots\c64ebdc7c67ce6960b648d7d809e1dab7f3dc0a5352864de3513018119e76344'
)
$ErrorActionPreference='Stop'
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $OutputRoot){throw "OutputRoot must be new: $OutputRoot"}
$moduleId='stage_aware_fault_winner'; $projectId='hybridcpu-stage-aware-fault-winner'
$manifest=Get-Content -LiteralPath (Join-Path $HwirRun 'manifest.json') -Raw | ConvertFrom-Json
$snapshotId=$manifest.hwirSha256
$snapshot=Join-Path (Join-Path $OutputRoot 'snapshots') $snapshotId
New-Item -ItemType Directory -Force -Path $snapshot | Out-Null
$copy=[ordered]@{
    'hwir-preview.json'=(Join-Path $HwirRun 'stage-aware-fault-winner.hwir.json')
    'stage-aware-fault-winner.mlir'=(Join-Path $HwirRun 'stage-aware-fault-winner.mlir')
    'stage-aware-fault-winner.sv'=(Join-Path $RtlReceipt 'stage-aware-fault-winner.sv')
    'rtl-mutant.sv'=(Join-Path $RtlReceipt 'stage-aware-fault-winner-mutant.sv')
    'rtl-receipt.json'=(Join-Path $RtlReceipt 'receipt.json')
    'vectors.jsonl'=(Join-Path $RtlReceipt 'vectors.jsonl')
    'source-inventory.json'=(Join-Path $InventorySnapshot 'source-inventory.json')
    'captured-sources.json'=(Join-Path $InventorySnapshot 'captured-sources.json')
}
foreach($name in $copy.Keys){Copy-Item -LiteralPath $copy[$name] -Destination (Join-Path $snapshot $name)}
$helperPath='HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.Pipeline.Helpers.cs'
$stageEnumPath='HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.Pipeline.Helpers.PipelineStage.cs'
$repo='https://github.com/yuriyyak23/HybridCPU-v2'; $commit='1bfe41a49cf11c80fa6c6aee87accb54b78b331f'
$activeSourcePath='C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE\CloseToHSL\Core\Pipeline\ExecutionFlow\StageFlow\CPU_Core.Pipeline.Helpers.cs'
$sourceLines=[IO.File]::ReadAllLines($activeSourcePath)
function Find-Line([string]$needle){$idx=[Array]::FindIndex($sourceLines,[Predicate[string]]{param($line)$line.Contains($needle,[StringComparison]::Ordinal)});if($idx -lt 0){throw "Source anchor not found: $needle"};return $idx+1}
$winnerStarts=@(for($i=0;$i -lt $sourceLines.Length;$i++){if($sourceLines[$i].Contains('public static bool TryResolveStageAwareExceptionWinner(',[StringComparison]::Ordinal)){$i+1}})
if($winnerStarts.Count -ne 2){throw "Expected exactly two winner overload declarations; found $($winnerStarts.Count)."}
$winnerLine=$winnerStarts[1]
$wbMaskLine=Find-Line 'public static byte ResolveWriteBackFaultMask('
$memMaskLine=Find-Line 'public static byte ResolveMemoryFaultMask('
$exMaskLine=Find-Line 'public static byte ResolveExecuteFaultMask('
$wbKeyLine=Find-Line 'private static bool TryResolveWriteBackLaneOrderKey('
$memKeyLine=Find-Line 'private static bool TryResolveMemoryLaneOrderKey('
$exKeyLine=Find-Line 'private static bool TryResolveExecuteLaneOrderKey('
function Digest([string]$s){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($s))).ToLowerInvariant()}
$helperPin=$manifest.inputs|Where-Object path -CEQ $helperPath
$stageEnumPin=$manifest.inputs|Where-Object path -CEQ $stageEnumPath
if(!$helperPin -or !$stageEnumPin){throw 'Helper or PipelineStage source pin is missing from HWIR manifest.'}
$identity="stage-aware-winner/v1|$($helperPin.activeSha256)|$($helperPin.goldenMaterializedSha256)|$($stageEnumPin.activeSha256)|$($stageEnumPin.goldenMaterializedSha256)"
$module=Get-Content -LiteralPath (Join-Path $HwirRun 'stage-aware-fault-winner.hwir.json') -Raw|ConvertFrom-Json
function Add-Map($rows,[string]$graphNode,[string]$path,[int]$line,[string]$symbol,[string]$role,[string]$hwirNode=$null){$row=[ordered]@{graphNodeId=$graphNode;source=[ordered]@{repository=$repo;commit=$commit;relativePath=$path;line=$line;column=13;symbol=$symbol};assembly='CircuitWeave.StageAwareFaultWinner';sourceIdentityDigest=(Digest $identity);role=$role};if($hwirNode){$row.hwirNodeId=$hwirNode};$rows.Add($row)}
$maps=[Collections.Generic.List[object]]::new()
Add-Map $maps ('module:'+ $moduleId) $helperPath $winnerLine 'YAKSys_Hybrid_CPU.Processor.CPU_Core.TryResolveStageAwareExceptionWinner' 'Primary' $moduleId
foreach($port in $module.modules[0].ports){
    if($port.name -match '^(wb|mem|ex)_lane\d+_(occupied|has_fault)$'){$stagePrefix=$Matches[1];if($stagePrefix -eq 'wb'){$line=$wbMaskLine;$stageName='WriteBack'}elseif($stagePrefix -eq 'mem'){$line=$memMaskLine;$stageName='Memory'}else{$line=$exMaskLine;$stageName='Execute'};$symbol='YAKSys_Hybrid_CPU.Processor.CPU_Core.Resolve'+$stageName+'FaultMask';$path=$helperPath}
    elseif($port.name -match 'slot_index|uses_explicit'){$stagePrefix=$port.name.Substring(0,$port.name.IndexOf('_'));if($stagePrefix -eq 'wb'){$line=$wbKeyLine;$stageName='WriteBack'}elseif($stagePrefix -eq 'mem'){$line=$memKeyLine;$stageName='Memory'}else{$line=$exKeyLine;$stageName='Execute'};$symbol='YAKSys_Hybrid_CPU.Processor.CPU_Core.TryResolve'+$stageName+'LaneOrderKey';$path=$helperPath}
    elseif($port.name -ceq 'winner_stage'){$line=14;$symbol='YAKSys_Hybrid_CPU.Processor.CPU_Core.PipelineStage';$path=$stageEnumPath}
    else{$line=$winnerLine;$symbol='YAKSys_Hybrid_CPU.Processor.CPU_Core.TryResolveStageAwareExceptionWinner';$path=$helperPath}
    Add-Map $maps ('port:'+ $moduleId+':'+$port.name) $path $line $symbol 'Dependency'
}
foreach($node in $module.modules[0].nodes){$path=$helperPath;$line=$winnerLine;$symbol='YAKSys_Hybrid_CPU.Processor.CPU_Core.TryResolveStageAwareExceptionWinner';$role='Primary';Add-Map $maps ('hwir:'+ $moduleId+':'+$node.id) $path $line $symbol $role ($moduleId+':'+$node.id)}
$sourceMap=[ordered]@{schemaVersion='cwstudio.sourcemap/v1';snapshotId=$projectId;completeness='partial';mappings=@($maps|Sort-Object graphNodeId)}
[IO.File]::WriteAllText((Join-Path $snapshot 'source-map.json'),($sourceMap|ConvertTo-Json -Depth 8)+"`n",[Text.UTF8Encoding]::new($false))
$disposition=[ordered]@{schema='circuitweave.processor-unelaborated-symbols/v1';snapshotId=$snapshotId;status='partial-source-inventory';modelCommit=$commit;goldenCommit='6946ccab9d888f7dae0a4d8e18480f436138acd9';elaborated=@('WB/MEM/EX occupied AND HasFault lane eligibility','stage priority WB > MEM > EX','ascending physical lane scan with explicit slot-index comparison and physical-lane fallback');notElaborated=@([ordered]@{symbol='WriteBackStage/MemoryStage/ExecuteStage materialization';reason='Lane state is presented at module input; pipeline stage capture and valid/clock gating are not included.';unlock='Elaborate producer registers and their verified reset/enable/stall contract.'},[ordered]@{symbol='SlotIndex < 8 normalization';reason='slot_index_lt8 is a sampled lane fact derived from the active source guard; the int-to-boolean representation boundary is not elaborated.';unlock='Add typed source-level integer range predicate and verify its encoding against runtime lane snapshots.'},[ordered]@{symbol='StageAwareExceptionWinnerMetadata payload construction';reason='Module emits only valid/stage/lane and does not mux address, direction, PC, owner, VT or translation-fault payload.';unlock='Elaborate per-lane metadata selection from stage state and compare full winner tuples to runtime facts.'},[ordered]@{symbol='DeliverStageAwareMemoryPageFault composition';reason='Winner resolver is a separate combinational module and is not instantiated with page-fault delivery projection.';unlock='Compose winner stage/lane/payload outputs into MEM delivery logic and verify the hierarchy.'},[ordered]@{symbol='clock/reset/stall/replay and architectural trap';reason='No sequential pipeline or CSR/mtvec state is modeled.';unlock='Establish independent reset/stall/replay and retire/trap trace contract.'})}
[IO.File]::WriteAllText((Join-Path $snapshot 'source-inventory-disposition.json'),($disposition|ConvertTo-Json -Depth 8)+"`n",[Text.UTF8Encoding]::new($false))
$dot='digraph stage_aware_fault_winner {'+"`n  rankdir=LR;`n  module [shape=box,label=`"stage_aware_fault_winner`"]`n";foreach($p in $module.modules[0].ports){$dot+='  "port:'+ $moduleId+':'+$p.name+'" [shape=oval,label="'+$p.name+'"]' +"`n"};foreach($p in $module.modules[0].ports){$dot+='  module -> "port:'+ $moduleId+':'+$p.name+'";' +"`n"};$dot+='}' +"`n";[IO.File]::WriteAllText((Join-Path $snapshot 'graph.dot'),$dot,[Text.UTF8Encoding]::new($false))
$artifacts=[Collections.Generic.List[object]]::new()
foreach($file in Get-ChildItem -LiteralPath $snapshot -File|Sort-Object Name){$id=$file.BaseName.ToLowerInvariant().Replace('_','-');$kind='candidateRunProvenance';$media='application/json';switch($file.Name){'hwir-preview.json'{$id='hwir';$kind='hardwareIr'};'graph.dot'{$id='dot';$kind='dotGraph';$media='text/vnd.graphviz'};'source-map.json'{$id='source-map';$kind='sourceMap'};'source-inventory.json'{$id='source-inventory';$kind='sourceInventory'};'captured-sources.json'{$id='captured-sources';$kind='capturedSources'};'stage-aware-fault-winner.mlir'{$id='winner-mlir';$kind='mlir';$media='text/plain'};'stage-aware-fault-winner.sv'{$id='winner-systemverilog';$kind='systemVerilog';$media='text/x-systemverilog'};'rtl-mutant.sv'{$id='rtl-mutant';$media='text/x-systemverilog'};'vectors.jsonl'{$id='vectors';$media='application/x-ndjson'};'source-inventory-disposition.json'{$id='source-inventory-disposition'}};$artifacts.Add([ordered]@{id=$id;kind=$kind;relativePath=('snapshots/'+$snapshotId+'/'+$file.Name);sha256=(Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant();byteLength=$file.Length;mediaType=$media})}
$project=[ordered]@{schemaVersion='cwstudio.project/v1';projectId=$projectId;title='HybridCPU stage-aware fault winner projection (STRUCTURAL ONLY — NOT VERIFIED)';artifacts=@($artifacts);defaultGraphArtifactId='hwir'}
[IO.File]::WriteAllText((Join-Path $OutputRoot 'studio.project.json'),($project|ConvertTo-Json -Depth 8)+"`n",[Text.UTF8Encoding]::new($false))
$slice=[ordered]@{schema='circuitweave.processor-stage-aware-fault-winner-studio/v1';producerHead='e4e2faeda14184ed9832f3bdab70bcdf9aeb0fad';modelCommit=$commit;goldenCommit='6946ccab9d888f7dae0a4d8e18480f436138acd9';snapshotId=$snapshotId;hwirSha256=$manifest.hwirSha256;rtlReceiptSha256=(Get-FileHash (Join-Path $snapshot 'rtl-receipt.json') -Algorithm SHA256).Hash.ToLowerInvariant();trust='StructuralOnly';status='INCOMPLETE';scope='Combinational active/Golden-pinned lane-state winner resolver; stage lane facts are inputs; metadata mux, pipeline registers and delivery composition remain outside.'}
[IO.File]::WriteAllText((Join-Path $OutputRoot 'manifest.json'),($slice|ConvertTo-Json -Depth 8)+"`n",[Text.UTF8Encoding]::new($false))
Write-Host "Published stage-aware fault winner snapshot: $OutputRoot"
