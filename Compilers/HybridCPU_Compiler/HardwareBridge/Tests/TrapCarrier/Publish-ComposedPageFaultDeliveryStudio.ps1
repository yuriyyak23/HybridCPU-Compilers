param(
    [Parameter(Mandatory=$true)][string]$HwirRun,
    [Parameter(Mandatory=$true)][string]$RtlReceipt,
    [Parameter(Mandatory=$true)][string]$RuntimeComparison,
    [Parameter(Mandatory=$true)][string]$ActiveTrace,
    [Parameter(Mandatory=$true)][string]$GoldenTrace,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [string]$TranslationFaultRuntimeComparison,
    [string]$ActiveTranslationFaultTrace,
    [string]$GoldenTranslationFaultTrace,
    [string]$ActiveScalarMemoryTranslationFaultTrace,
    [string]$GoldenScalarMemoryTranslationFaultTrace,
    [string]$ScalarMemoryTranslationFaultComparison,
    [string]$ActiveSecondStageTranslationFaultTrace,
    [string]$GoldenSecondStageTranslationFaultTrace,
    [string]$SecondStageTranslationFaultComparison,
    [string]$ActiveSecondStageScalarMemoryTranslationFaultTrace,
    [string]$GoldenSecondStageScalarMemoryTranslationFaultTrace,
    [string]$SecondStageScalarMemoryTranslationFaultComparison,
    [string]$ActiveSecondStageRebindRaceTrace,
    [string]$GoldenSecondStageRebindRaceTrace,
    [string]$SecondStageRebindRaceComparison,
    [string]$InventorySnapshot='C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\processor-20261114\trap-carrier-projection-v10\snapshots\c64ebdc7c67ce6960b648d7d809e1dab7f3dc0a5352864de3513018119e76344'
)
$ErrorActionPreference='Stop'
$translationFaultArgs=@($TranslationFaultRuntimeComparison,$ActiveTranslationFaultTrace,$GoldenTranslationFaultTrace)
if(($translationFaultArgs|Where-Object {$_}|Measure-Object).Count -notin @(0,3)){throw 'Supply all three translation-fault runtime comparison/trace inputs, or none.'}
$scalarMemoryArgs=@($ActiveScalarMemoryTranslationFaultTrace,$GoldenScalarMemoryTranslationFaultTrace)
if(($scalarMemoryArgs|Where-Object {$_}|Measure-Object).Count -notin @(0,2)){throw 'Supply both active and Golden scalar-memory translation-fault traces, or none.'}
if($ScalarMemoryTranslationFaultComparison -and -not $ActiveScalarMemoryTranslationFaultTrace){throw 'Scalar-memory comparison requires active and Golden scalar-memory traces.'}
$secondStageArgs=@($ActiveSecondStageTranslationFaultTrace,$GoldenSecondStageTranslationFaultTrace,$SecondStageTranslationFaultComparison)
if(($secondStageArgs|Where-Object {$_}|Measure-Object).Count -notin @(0,3)){throw 'Supply active, Golden and differential second-stage fault inputs, or none.'}
$secondStageScalarArgs=@($ActiveSecondStageScalarMemoryTranslationFaultTrace,$GoldenSecondStageScalarMemoryTranslationFaultTrace,$SecondStageScalarMemoryTranslationFaultComparison)
if(($secondStageScalarArgs|Where-Object {$_}|Measure-Object).Count -notin @(0,3)){throw 'Supply active, Golden and differential second-stage scalar-memory inputs, or none.'}
$secondStageRaceArgs=@($ActiveSecondStageRebindRaceTrace,$GoldenSecondStageRebindRaceTrace,$SecondStageRebindRaceComparison)
if(($secondStageRaceArgs|Where-Object {$_}|Measure-Object).Count -notin @(0,3)){throw 'Supply active, Golden and differential second-stage rebind-race inputs, or none.'}
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $OutputRoot){throw "OutputRoot must be new: $OutputRoot"}
$projectId='hybridcpu-pagefault-delivery-composed'
$repo='https://github.com/yuriyyak23/HybridCPU-v2';$commit='1bfe41a49cf11c80fa6c6aee87accb54b78b331f'
$manifest=Get-Content -LiteralPath (Join-Path $HwirRun 'manifest.json') -Raw|ConvertFrom-Json
$hwir=Get-Content -LiteralPath (Join-Path $HwirRun 'processor-pagefault-delivery.hwir.json') -Raw|ConvertFrom-Json
$snapshotId=$manifest.composedHwirSha256
$snapshot=Join-Path (Join-Path $OutputRoot 'snapshots') $snapshotId
New-Item -ItemType Directory -Force -Path $snapshot|Out-Null
$copy=[ordered]@{
    'processor-pagefault-delivery.hwir.json'=(Join-Path $HwirRun 'processor-pagefault-delivery.hwir.json')
    'processor-pagefault-delivery.mlir'=(Join-Path $HwirRun 'processor-pagefault-delivery.mlir')
    'stage-aware-fault-winner.hwir.json'=(Join-Path $HwirRun 'stage-aware-fault-winner.hwir.json')
    'stage-aware-fault-winner.mlir'=(Join-Path $HwirRun 'stage-aware-fault-winner.mlir')
    'fault-winner-payload-mux.hwir.json'=(Join-Path $HwirRun 'fault-winner-payload-mux.hwir.json')
    'fault-winner-payload-mux.mlir'=(Join-Path $HwirRun 'fault-winner-payload-mux.mlir')
    'composed-systemverilog'=(Join-Path $RtlReceipt 'processor-pagefault-delivery.sv')
    'composed-rtl-mutant.sv'=(Join-Path $RtlReceipt 'processor-pagefault-delivery-mutant.sv')
    'metadata-rtl-mutant.sv'=(Join-Path $RtlReceipt 'processor-pagefault-delivery-metadata-mutant.sv')
    'translation-fault-rtl-mutant.sv'=(Join-Path $RtlReceipt 'processor-pagefault-delivery-translation-fault-mutant.sv')
    'validity-rtl-mutant.sv'=(Join-Path $RtlReceipt 'processor-pagefault-delivery-validity-mutant.sv')
    'failing-address-rtl-mutant.sv'=(Join-Path $RtlReceipt 'processor-pagefault-delivery-failing-address-mutant.sv')
    'winner-systemverilog'=(Join-Path $RtlReceipt 'stage-aware-fault-winner.sv')
    'winner-rtl-mutant.sv'=(Join-Path $RtlReceipt 'stage-aware-fault-winner-mutant.sv')
    'rtl-receipt.json'=(Join-Path $RtlReceipt 'receipt.json')
    'composed-rtl-receipt.json'=(Join-Path $RtlReceipt 'composed-receipt.json')
    'vectors.jsonl'=(Join-Path $RtlReceipt 'vectors.jsonl')
    'runtime-comparison.json'=$RuntimeComparison
    'active-runtime-trace.jsonl'=$ActiveTrace
    'golden-runtime-trace.jsonl'=$GoldenTrace
    'source-inventory.json'=(Join-Path $InventorySnapshot 'source-inventory.json')
    'captured-sources.json'=(Join-Path $InventorySnapshot 'captured-sources.json')
}
if($TranslationFaultRuntimeComparison){
    $copy['translation-fault-runtime-comparison.json']=$TranslationFaultRuntimeComparison
    $copy['active-translation-fault.json']=$ActiveTranslationFaultTrace
    $copy['golden-translation-fault.json']=$GoldenTranslationFaultTrace
}
if($ActiveScalarMemoryTranslationFaultTrace){
    $copy['active-scalar-memory-translation-faults.json']=$ActiveScalarMemoryTranslationFaultTrace
    $copy['golden-scalar-memory-translation-faults.json']=$GoldenScalarMemoryTranslationFaultTrace
    if($ScalarMemoryTranslationFaultComparison){$copy['scalar-memory-translation-fault-comparison.json']=$ScalarMemoryTranslationFaultComparison}
}
if($ActiveSecondStageTranslationFaultTrace){
    $copy['active-second-stage-translation-fault.json']=$ActiveSecondStageTranslationFaultTrace
    $copy['golden-second-stage-translation-fault.json']=$GoldenSecondStageTranslationFaultTrace
    $copy['second-stage-translation-fault-comparison.json']=$SecondStageTranslationFaultComparison
}
if($ActiveSecondStageScalarMemoryTranslationFaultTrace){
    $copy['active-second-stage-scalar-memory-faults.json']=$ActiveSecondStageScalarMemoryTranslationFaultTrace
    $copy['golden-second-stage-scalar-memory-faults.json']=$GoldenSecondStageScalarMemoryTranslationFaultTrace
    $copy['second-stage-scalar-memory-fault-comparison.json']=$SecondStageScalarMemoryTranslationFaultComparison
}
if($ActiveSecondStageRebindRaceTrace){
    $copy['active-second-stage-rebind-race.json']=$ActiveSecondStageRebindRaceTrace
    $copy['golden-second-stage-rebind-race.json']=$GoldenSecondStageRebindRaceTrace
    $copy['second-stage-rebind-race-comparison.json']=$SecondStageRebindRaceComparison
}
function Assert-ComposedReceiptBindings([string]$snapshotPath) {
    $receiptPath = Join-Path $snapshotPath 'composed-rtl-receipt.json'
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($receipt.schema -cne 'circuitweave.processor-composed-pagefault-delivery-rtl-check/v2') {
        throw 'Unsupported composed RTL receipt schema.'
    }
    foreach ($binding in @(
        @{Field='candidateRtlSha256'; File='composed-systemverilog'},
        @{Field='vectorsSha256'; File='vectors.jsonl'}
    )) {
        $claim = $receipt.($binding.Field)
        if ($claim -isnot [string] -or $claim -cnotmatch '^[0-9a-f]{64}$') {
            throw "Invalid composed receipt digest: $($binding.Field)."
        }
        $actual = (Get-FileHash -LiteralPath (Join-Path $snapshotPath $binding.File) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($claim -cne $actual) { throw "Composed receipt digest mismatch: $($binding.Field)." }
    }
}
foreach($name in $copy.Keys){Copy-Item -LiteralPath $copy[$name] -Destination (Join-Path $snapshot $name)}
Assert-ComposedReceiptBindings $snapshot
$stageEnumPath='HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.Pipeline.Helpers.PipelineStage.cs'
$stageEnumIdentity='pipeline-stage-enum/v1|f84c179da79ff204159a006aceb006e8eae865060d0c0e40db8a774d75b6bfa4|5a9e7287988f863e67cbeb29bed1376204746ab84590515387c02c7d0f3606c8'
function Digest([string]$s){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($s))).ToLowerInvariant()}
function Add-OriginMap($rows,[string]$graphId,$source,[string]$role,[string]$hwirNode=$null){
    $mapping=[ordered]@{graphNodeId=$graphId;source=[ordered]@{repository=$source.repository;commit=$source.commit;relativePath=$source.path;line=$source.line;column=$source.column;symbol=($source.type+'.'+$source.member)};assembly=$source.assembly;sourceIdentityDigest=$source.identityDigest;role=$role}
    if($hwirNode){$mapping.hwirNodeId=$hwirNode};$rows.Add($mapping)
}
$modules=@{};foreach($module in $hwir.modules){$modules[$module.id]=$module}
$faultPayloadSources=@{}
foreach($drive in ($modules['fault_winner_payload_mux'].nodes|Where-Object {$_.kind -ceq 'drive' -and $_.outputPort -like 'winner_cpu_translation_fault_*'})){
    if(-not $drive.origin){throw ('Flattened translation-fault payload source origin is missing: '+$drive.outputPort)}
    $field=$drive.outputPort.Substring('winner_cpu_translation_fault_'.Length)
    $faultPayloadSources[$field]=$drive.origin
}
if($faultPayloadSources.Count -ne 26){throw ('Expected 23 payload fields and 3 derived translation-fault outputs; found '+$faultPayloadSources.Count)}
foreach($field in @('is_valid','failing_virtual_address_wrapped_sum','failing_virtual_address_overflow')){
    if(-not $faultPayloadSources.ContainsKey($field)){throw ('Derived translation-fault source origin is missing: '+$field)}
}
$maps=[Collections.Generic.List[object]]::new()
foreach($module in $hwir.modules){
    Add-OriginMap $maps ('module:'+ $module.id) $module.origin 'Primary' $module.id
    foreach($port in $module.ports){
        if($module.id -ceq 'stage_aware_fault_winner' -and $port.name -ceq 'winner_stage'){
            $enumSource=[ordered]@{repository=$repo;commit=$commit;path=$stageEnumPath;line=14;column=13;type='YAKSys_Hybrid_CPU.Processor.CPU_Core';member='PipelineStage';assembly=$module.origin.assembly;identityDigest=(Digest $stageEnumIdentity)}
            Add-OriginMap $maps ('port:'+ $module.id+':'+$port.name) $enumSource 'Dependency'
        }elseif($port.name -match '(^winner_cpu_translation_fault_|_lane[0-7]_cpu_translation_fault_)'){
            $field=($port.name -split '_cpu_translation_fault_',2)[1]
            $fieldOrigin=$faultPayloadSources[$field]
            if(-not $fieldOrigin){throw ('No source origin for flattened translation-fault port: '+$port.name)}
            Add-OriginMap $maps ('port:'+ $module.id+':'+$port.name) $fieldOrigin 'Primary'
        }else{Add-OriginMap $maps ('port:'+ $module.id+':'+$port.name) $module.origin $module.origin.role}
    }
    foreach($node in $module.nodes){$source=if($node.origin){$node.origin}else{$module.origin};Add-OriginMap $maps ('hwir:'+ $module.id+':'+$node.id) $source $source.role ($module.id+':'+$node.id)}
    foreach($instance in $module.instances){$target=$modules[$instance.moduleReference];$source=if($target){$target.origin}else{$module.origin};Add-OriginMap $maps ('instance:'+ $module.id+':'+$instance.id) $source 'Dependency'}
}
$sourceMap=[ordered]@{schemaVersion='cwstudio.sourcemap/v1';snapshotId=$projectId;completeness='partial';mappings=@($maps|Sort-Object graphNodeId)}
[IO.File]::WriteAllText((Join-Path $snapshot 'source-map.json'),($sourceMap|ConvertTo-Json -Depth 8)+"`n",[Text.UTF8Encoding]::new($false))
$translationRuntimeReason=if($TranslationFaultRuntimeComparison){$s='Production fetch nested fault records include active ExecutePipelineCycle and exact pinned Golden comparisons.';if($ActiveScalarMemoryTranslationFaultTrace){$s+=' Two first-stage records come from production LoadMicroOp/StoreMicroOp callers; they are caller-level observations, not pipeline-cycle timing.'};if($ActiveSecondStageTranslationFaultTrace){$s+=' A second-stage fetch violation is captured through ExecutePipelineCycle with present GPA and second-stage provenance.'};if($ActiveSecondStageScalarMemoryTranslationFaultTrace){$s+=' Two second-stage scalar caller records include GPA and translation provenance, without cycle timing claims.'};$s+=' Other RTL vectors are synthetic, the active-only FailureOffset check is not Golden-equivalent, and Golden lacks FailingVirtualAddress.';$s}else{'The 23 flattened payload fields remain synthetic in RTL vectors; no nested fault record has been observed from active/Golden Processor runtime.'}
$translationRuntimeUnlock=if($TranslationFaultRuntimeComparison){'Capture additional cycle-linked load/store and second-stage translation faults, then compare active/Golden architectural side effects while preserving revision-specific fields.'}else{'Capture complete fault records and derived outcomes from active and pinned-Golden runtime events, preserving the active-only FailureOffset semantic difference.'}
$disposition=[ordered]@{schema='circuitweave.processor-unelaborated-symbols/v1';snapshotId=$snapshotId;status='partial-source-inventory';modelCommit=$commit;goldenCommit='6946ccab9d888f7dae0a4d8e18480f436138acd9';elaborated=@('lane-state WB/MEM/EX occupied/fault masks and stage winner selection','signed 32-bit SlotIndex < 8 range and explicit packet lane ordering','winner FaultAddress/FaultIsWrite/thread identity/PC plus all flattened CpuInstructionTranslationFault positional/init fields for every WB/MEM/EX lane; MEM fault delivery consumes selected address/write','winner stage/lane/scalar payload connected into MEM page-fault delivery projection','active-source CpuInstructionTranslationFault.IsValid with pinned CPU_Core.SmtWays=4','FailingVirtualAddress modulo-2^64 value and checked-overflow flag','MEM delivery valid/rejected, selected active lane, thrown payload and flush indication');notElaborated=@([ordered]@{symbol='Stage materialization and clock/reset/enable';reason='All stage lanes and payload fields are inputs; pipeline register capture and reset/clock semantics are not modeled.';unlock='Add source-pinned producer registers and independently establish clock/reset/enable behavior.'},[ordered]@{symbol='Translation-fault runtime values and semantics';reason=$translationRuntimeReason;unlock=$translationRuntimeUnlock},[ordered]@{symbol='Stale-generation and replay behavior';reason='Generation/identity fields are carried but no stale-source retry/replay branch is modeled.';unlock='Trace source-pinned generation invalidation, replay, and resume outcomes in active and pinned Golden.'},[ordered]@{symbol='Precise init-property source navigation';reason='The read-only Studio CapturedNodeSource API resolves callable member identities but not synthesized init-property getter identities; those outputs map to the containing record constructor.';unlock='Extend Studio captured-source selection to resolve property symbol identities to exact property declaration lines.'},[ordered]@{symbol='PageFaultException to TrapEntryEvent/CSR/mtvec';reason='No architectural MEM exception caller route is established by current evidence.';unlock='Find and execute a real source-linked caller route or retain as unconnected.'},[ordered]@{symbol='Trap replay, stall and sequential CPU top';reason='Composed candidate is combinational and has no clocked pipeline state.';unlock='Build independent cycle/retire/memory/trap traces covering reset, stall, replay and resume.'},[ordered]@{symbol='Remaining Processor blocks';reason='This vertical slice covers only page-fault winner resolution and MEM delivery.';unlock='Continue by source-backed frontend/decode, ALU, LSU/cache/translation, DMA/MatrixTile, vector, system/interrupt, scheduler/memory integration.'})}
[IO.File]::WriteAllText((Join-Path $snapshot 'source-inventory-disposition.json'),($disposition|ConvertTo-Json -Depth 8)+"`n",[Text.UTF8Encoding]::new($false))
$dotLines=[Collections.Generic.List[string]]::new();$dotLines.Add('digraph processor_pagefault_delivery_top {');$dotLines.Add('  rankdir=LR;');foreach($module in $hwir.modules){$dotLines.Add('  "module:'+ $module.id+'" [shape=box,label="'+$module.id+'"]')};foreach($module in $hwir.modules){foreach($instance in $module.instances){$instanceId='instance:'+ $module.id+':'+$instance.id;$dotLines.Add('  "'+$instanceId+'" [shape=component,label="'+$instance.id+'"]');$dotLines.Add('  "module:'+ $module.id+'" -> "'+$instanceId+'";');$dotLines.Add('  "'+$instanceId+'" -> "module:'+ $instance.moduleReference+'";')}};$dotLines.Add('}');[IO.File]::WriteAllText((Join-Path $snapshot 'graph.dot'),($dotLines -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
$artifactRows=[Collections.Generic.List[object]]::new()
foreach($file in Get-ChildItem -LiteralPath $snapshot -File|Sort-Object Name){$id=$file.BaseName.ToLowerInvariant().Replace('_','-');$kind='candidateRunProvenance';$media='application/json';switch($file.Name){'processor-pagefault-delivery.hwir.json'{$id='hwir';$kind='hardwareIr'};'graph.dot'{$id='dot';$kind='dotGraph';$media='text/vnd.graphviz'};'source-map.json'{$id='source-map';$kind='sourceMap'};'source-inventory.json'{$id='source-inventory';$kind='sourceInventory'};'captured-sources.json'{$id='captured-sources';$kind='capturedSources'};'processor-pagefault-delivery.mlir'{$id='composed-mlir';$kind='mlir';$media='text/plain'};'composed-systemverilog'{$id='composed-systemverilog';$kind='systemVerilog';$media='text/x-systemverilog'};'winner-systemverilog'{$id='winner-systemverilog';$kind='systemVerilog';$media='text/x-systemverilog'};'composed-rtl-mutant.sv'{$id='composed-rtl-mutant';$media='text/x-systemverilog'};'metadata-rtl-mutant.sv'{$id='metadata-rtl-mutant';$media='text/x-systemverilog'};'translation-fault-rtl-mutant.sv'{$id='translation-fault-rtl-mutant';$media='text/x-systemverilog'};'validity-rtl-mutant.sv'{$id='validity-rtl-mutant';$media='text/x-systemverilog'};'failing-address-rtl-mutant.sv'{$id='failing-address-rtl-mutant';$media='text/x-systemverilog'};'winner-rtl-mutant.sv'{$id='winner-rtl-mutant';$media='text/x-systemverilog'};'vectors.jsonl'{$id='vectors';$media='application/x-ndjson'};'active-runtime-trace.jsonl'{$id='active-runtime-trace';$media='application/x-ndjson'};'golden-runtime-trace.jsonl'{$id='golden-runtime-trace';$media='application/x-ndjson'}};$artifactRows.Add([ordered]@{id=$id;kind=$kind;relativePath=('snapshots/'+$snapshotId+'/'+$file.Name);sha256=(Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant();byteLength=$file.Length;mediaType=$media})}
$project=[ordered]@{schemaVersion='cwstudio.project/v1';projectId=$projectId;title='HybridCPU page-fault delivery composed projection (STRUCTURAL ONLY — NOT VERIFIED)';artifacts=@($artifactRows);defaultGraphArtifactId='hwir'}
[IO.File]::WriteAllText((Join-Path $OutputRoot 'studio.project.json'),($project|ConvertTo-Json -Depth 10)+"`n",[Text.UTF8Encoding]::new($false))
$runtimeScope=if($TranslationFaultRuntimeComparison){$s='Production-fetch CpuInstructionTranslationFault records are sourced from active ExecutePipelineCycle and compared with exact pinned-Golden common fields';if($ActiveScalarMemoryTranslationFaultTrace){$s+='; two first-stage records capture production LoadMicroOp/StoreMicroOp callers without cycle timing claims'};if($ActiveSecondStageTranslationFaultTrace){$s+='; a second-stage fetch violation records present GPA and source generation via ExecutePipelineCycle'};if($ActiveSecondStageScalarMemoryTranslationFaultTrace){$s+='; two second-stage scalar records capture LoadMicroOp/StoreMicroOp faults with GPA and address-space provenance'};$s+='; Golden lacks FailingVirtualAddress, and active-only fields remain distinct.';$s}else{'Nested translation-fault payload values and derived values are synthetic; no active/Golden production fault record is attached.'}
$rebindRuntimeScope=if($ActiveSecondStageRebindRaceTrace){' A real blocked-page-walk rebind race is attached: active rejects in-flight rebind and observes stale on retry; Golden accepts rebind and observes stale in the in-flight cycle. Fault payloads coincide, transition timing differs.'}else{''}
$slice=[ordered]@{schema='circuitweave.processor-composed-pagefault-delivery-studio/v1';producerHead='e4e2faeda14184ed9832f3bdab70bcdf9aeb0fad';modelCommit=$commit;goldenCommit='6946ccab9d888f7dae0a4d8e18480f436138acd9';snapshotId=$snapshotId;composedHwirSha256=$manifest.composedHwirSha256;composedMlirSha256=$manifest.composedMlirSha256;rtlReceiptSha256=(Get-FileHash (Join-Path $snapshot 'composed-rtl-receipt.json') -Algorithm SHA256).Hash.ToLowerInvariant();trust='StructuralOnly';status='INCOMPLETE';scope=('Combinational composed stage-aware lane winner; complete scalar flattening of CpuInstructionTranslationFault fields; active-source IsValid with pinned SMT ways; modulo-2^64 FailingVirtualAddress and overflow flag; MEM PageFault delivery. '+$runtimeScope+$rebindRuntimeScope+' FailureOffset differs from Golden; stale/replay equivalence, cycle state, CSR/mtvec and Processor equivalence remain open.')}
[IO.File]::WriteAllText((Join-Path $OutputRoot 'manifest.json'),($slice|ConvertTo-Json -Depth 8)+"`n",[Text.UTF8Encoding]::new($false))
Write-Host "Published composed page-fault delivery snapshot: $OutputRoot"
