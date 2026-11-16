param(
    [Parameter(Mandatory=$true)][string]$HwirRun,
    [Parameter(Mandatory=$true)][string]$RtlReceipt,
    [Parameter(Mandatory=$true)][string]$RuntimeComparison,
    [Parameter(Mandatory=$true)][string]$ActiveTrace,
    [Parameter(Mandatory=$true)][string]$GoldenTrace,
    [Parameter(Mandatory=$true)][string]$Vectors,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [string]$InventorySnapshot='C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\processor-20261114\trap-carrier-projection-v10\snapshots\c64ebdc7c67ce6960b648d7d809e1dab7f3dc0a5352864de3513018119e76344'
)
$ErrorActionPreference='Stop'
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $OutputRoot){throw "OutputRoot must be new: $OutputRoot"}
$hwir=Get-Content -LiteralPath (Join-Path $HwirRun 'page-fault-delivery.hwir.json') -Raw | ConvertFrom-Json
$hwirSha=(Get-FileHash -LiteralPath (Join-Path $HwirRun 'page-fault-delivery.hwir.json') -Algorithm SHA256).Hash.ToLowerInvariant()
$projectId='hybridcpu-pagefault-delivery'
$snapshotId=$hwirSha
$snapshot=Join-Path (Join-Path $OutputRoot 'snapshots') $snapshotId
New-Item -ItemType Directory -Force -Path $snapshot | Out-Null

$copyMap=[ordered]@{
    'hwir-preview.json'=(Join-Path $HwirRun 'page-fault-delivery.hwir.json')
    'page-fault-delivery.mlir'=(Join-Path $HwirRun 'page-fault-delivery.mlir')
    'page-fault-delivery.sv'=(Join-Path $RtlReceipt 'page-fault-delivery.sv')
    'rtl-receipt.json'=(Join-Path $RtlReceipt 'receipt.json')
    'rtl-mutant.sv'=(Join-Path $RtlReceipt 'page-fault-delivery-mutant.sv')
    'runtime-comparison.json'=$RuntimeComparison
    'active-trace.jsonl'=$ActiveTrace
    'golden-trace.jsonl'=$GoldenTrace
    'vectors.jsonl'=$Vectors
    'source-inventory.json'=(Join-Path $InventorySnapshot 'source-inventory.json')
    'captured-sources.json'=(Join-Path $InventorySnapshot 'captured-sources.json')
}
foreach($name in $copyMap.Keys){Copy-Item -LiteralPath $copyMap[$name] -Destination (Join-Path $snapshot $name)}

$exceptionsPath='HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/Faults/CPU_Core.PipelineExecution.Exceptions.cs'
$helpersPath='HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.Pipeline.Helpers.cs'
$stageEnumPath='HybridCPU_ISE/CloseToHSL/Core/Pipeline/ExecutionFlow/StageFlow/CPU_Core.Pipeline.Helpers.PipelineStage.cs'
$repo='https://github.com/yuriyyak23/HybridCPU-v2'
$commit='1bfe41a49cf11c80fa6c6aee87accb54b78b331f'
$deliveryIdentity='page-fault-delivery-after-winner/v1|7be87fb6ab1db964e1b4ef8fd17e8bbaf010662c31d173c99684dca6bb2b400f|fad6980d48ea651b237f2df350cbfcbee64ba31bba4503827de9cbfa7e6cb7e8|e1fd930195f41a1a79694f36868cb44325db7c2970c5893ea686ec649d71cb8e|e599b81db714cbb2a169b28f3eac4bcffa8e58141814624dc1df3891a9af45e7'
$stageEnumIdentity='pipeline-stage-enum/v1|f84c179da79ff204159a006aceb006e8eae865060d0c0e40db8a774d75b6bfa4|5a9e7287988f863e67cbeb29bed1376204746ab84590515387c02c7d0f3606c8'
$winnerIdentity='try-resolve-stage-aware-winner-metadata/v1|e1fd930195f41a1a79694f36868cb44325db7c2970c5893ea686ec649d71cb8e|e599b81db714cbb2a169b28f3eac4bcffa8e58141814624dc1df3891a9af45e7'
$markIdentity='mark-active-memory-page-fault/v1|7be87fb6ab1db964e1b4ef8fd17e8bbaf010662c31d173c99684dca6bb2b400f|fad6980d48ea651b237f2df350cbfcbee64ba31'
function Digest([string]$s){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($s))).ToLowerInvariant()}
function Add-Mapping($rows,[string]$graphNode,[string]$path,[int]$line,[string]$symbol,[string]$identity,[string]$role,[string]$hwirNode=$null){
    $mapping=[ordered]@{graphNodeId=$graphNode;source=[ordered]@{repository=$repo;commit=$commit;relativePath=$path;line=$line;column=13;symbol=$symbol};assembly='CircuitWeave.PageFaultDeliveryProjection';sourceIdentityDigest=(Digest $identity);role=$role}
    if($hwirNode){$mapping.hwirNodeId=$hwirNode}
    $rows.Add($mapping)
}
$mappings=[Collections.Generic.List[object]]::new()
Add-Mapping $mappings ('module:'+ $hwir.topModule) $exceptionsPath 255 'YAKSys_Hybrid_CPU.Processor.CPU_Core.DeliverStageAwareMemoryPageFault' $deliveryIdentity 'Primary' $hwir.topModule
$moduleRows=@($hwir.modules | Where-Object id -CEQ $hwir.topModule)
if($moduleRows.Count -ne 1){throw 'HWIR top module is missing or duplicated.'}
$module=$moduleRows[0]
foreach($port in $module.ports){
    if($port.name -ceq 'winner_stage'){
        Add-Mapping $mappings ('port:'+ $module.id+':'+$port.name) $stageEnumPath 14 'YAKSys_Hybrid_CPU.Processor.CPU_Core.PipelineStage' $stageEnumIdentity 'Dependency'
    }elseif($port.name -like 'winner_*'){
        Add-Mapping $mappings ('port:'+ $module.id+':'+$port.name) $helpersPath 906 'YAKSys_Hybrid_CPU.Processor.CPU_Core.TryResolveStageAwareExceptionWinnerMetadata' $winnerIdentity 'Dependency'
    }elseif($port.name -like 'incoming_*'){
        Add-Mapping $mappings ('port:'+ $module.id+':'+$port.name) $exceptionsPath 239 'YAKSys_Hybrid_CPU.Processor.CPU_Core.MarkActiveMemoryLanePageFault' $markIdentity 'Dependency'
    }else{
        Add-Mapping $mappings ('port:'+ $module.id+':'+$port.name) $exceptionsPath 255 'YAKSys_Hybrid_CPU.Processor.CPU_Core.DeliverStageAwareMemoryPageFault' $deliveryIdentity 'Primary'
    }
}
foreach($node in $module.nodes){
    Add-Mapping $mappings ('hwir:'+ $module.id+':'+$node.id) $exceptionsPath 255 'YAKSys_Hybrid_CPU.Processor.CPU_Core.DeliverStageAwareMemoryPageFault' $deliveryIdentity 'Primary' ($module.id+':'+$node.id)
}
$sourceMap=[ordered]@{schemaVersion='cwstudio.sourcemap/v1';snapshotId=$projectId;completeness='partial';mappings=@($mappings | Sort-Object graphNodeId)}
[IO.File]::WriteAllText((Join-Path $snapshot 'source-map.json'),($sourceMap|ConvertTo-Json -Depth 8)+"`n",[Text.UTF8Encoding]::new($false))

$notElaborated=[ordered]@{
    schema='circuitweave.processor-unelaborated-symbols/v1';snapshotId=$snapshotId;status='partial-source-inventory';
    modelCommit=$commit;goldenCommit='6946ccab9d888f7dae0a4d8e18480f436138acd9';
    elaborated=@('DeliverStageAwareMemoryPageFault winner-stage branch projection','MarkActiveMemoryLanePageFault payload origin (source dependency)','TryResolveStageAwareExceptionWinnerMetadata winner tuple as an external resolved input');
    notElaborated=@(
        [ordered]@{symbol='TryResolveStageAwareExceptionWinner / WB-MEM-EX lane arbitration';reason='Winner metadata is an explicit projection input. Lane masks, occupied checks, packet SlotIndex ordering and fallback lane ordering are not implemented in this module.';unlock='Implement and differentially check mask construction, explicit-packet slot-key ordering, and fallback lane ordering from active and pinned Golden stage snapshots.'},
        [ordered]@{symbol='ExecutePipelineCycle / flush handshake';reason='trap_flush is a combinational indication; no pipeline register, cycle, stall or replay state is modeled.';unlock='Capture cycle-level reset/stall/replay and stage-valid transitions with an independent sequential oracle.'},
        [ordered]@{symbol='TrapEntryEvent / CSR / mtvec';reason='No MEM PageFaultException to architectural TrapEntryEvent route is established by source/runtime evidence.';unlock='Prove an actual caller/retire path that consumes this exception into architectural CSR/mtvec state, then add the source and runtime evidence.'}
    )
}
[IO.File]::WriteAllText((Join-Path $snapshot 'source-inventory-disposition.json'),($notElaborated|ConvertTo-Json -Depth 8)+"`n",[Text.UTF8Encoding]::new($false))
$dot='digraph page_fault_delivery_after_winner {'+"`n  rankdir=LR;`n  module [shape=box,label=`"page_fault_delivery_after_winner`"]`n"
foreach($port in $module.ports){$dot+='  "port:'+ $module.id+':'+$port.name+'" [shape=oval,label="'+$port.name+'"]' +"`n"}
foreach($node in $module.nodes){$dot+='  "hwir:'+ $module.id+':'+$node.id+'" [shape=box,label="'+$node.id+'"]' +"`n"}
foreach($port in $module.ports){$dot+='  module -> "port:'+ $module.id+':'+$port.name+'";' +"`n"}
foreach($node in $module.nodes){$dot+='  module -> "hwir:'+ $module.id+':'+$node.id+'";' +"`n"}
$dot+='}' +"`n"
[IO.File]::WriteAllText((Join-Path $snapshot 'graph.dot'),$dot,[Text.UTF8Encoding]::new($false))

$files=Get-ChildItem -LiteralPath $snapshot -File | Sort-Object Name
$artifacts=[Collections.Generic.List[object]]::new()
foreach($file in $files){
    $id=$file.BaseName.ToLowerInvariant().Replace('_','-')
    $kind='candidateRunProvenance';$media='application/json'
    switch($file.Name){
        'hwir-preview.json'{$id='hwir';$kind='hardwareIr'}
        'graph.dot'{$id='dot';$kind='dotGraph';$media='text/vnd.graphviz'}
        'source-map.json'{$id='source-map';$kind='sourceMap'}
        'source-inventory.json'{$id='source-inventory';$kind='sourceInventory'}
        'captured-sources.json'{$id='captured-sources';$kind='capturedSources'}
        'page-fault-delivery.mlir'{$id='page-fault-delivery-mlir';$kind='mlir';$media='text/plain'}
        'page-fault-delivery.sv'{$id='page-fault-delivery-systemverilog';$kind='systemVerilog';$media='text/x-systemverilog'}
        'source-inventory-disposition.json'{$id='source-inventory-disposition'}
        'rtl-receipt.json'{$id='rtl-receipt'}
        'rtl-mutant.sv'{$id='rtl-mutant';$media='text/x-systemverilog'}
        'runtime-comparison.json'{$id='runtime-comparison'}
        'active-trace.jsonl'{$id='active-runtime-trace';$media='application/x-ndjson'}
        'golden-trace.jsonl'{$id='golden-runtime-trace';$media='application/x-ndjson'}
        'vectors.jsonl'{$id='vectors';$media='application/x-ndjson'}
    }
    $artifacts.Add([ordered]@{id=$id;kind=$kind;relativePath=('snapshots/'+$snapshotId+'/'+$file.Name);sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant();byteLength=$file.Length;mediaType=$media})
}
$project=[ordered]@{schemaVersion='cwstudio.project/v1';projectId=$projectId;title='HybridCPU PageFault MEM delivery branch projection (STRUCTURAL ONLY — NOT VERIFIED)';artifacts=@($artifacts);defaultGraphArtifactId='hwir'}
[IO.File]::WriteAllText((Join-Path $OutputRoot 'studio.project.json'),($project|ConvertTo-Json -Depth 8)+"`n",[Text.UTF8Encoding]::new($false))
$sliceManifest=[ordered]@{
    schema='circuitweave.processor-page-fault-delivery-slice/v1';producerHead='e4e2faeda14184ed9832f3bdab70bcdf9aeb0fad';modelCommit=$commit;goldenCommit='6946ccab9d888f7dae0a4d8e18480f436138acd9';
    snapshotId=$snapshotId;hwirSha256=$hwirSha;rtlReceiptSha256=(Get-FileHash (Join-Path $snapshot 'rtl-receipt.json') -Algorithm SHA256).Hash.ToLowerInvariant();
    runtimeComparisonSha256=(Get-FileHash (Join-Path $snapshot 'runtime-comparison.json') -Algorithm SHA256).Hash.ToLowerInvariant();
    trust='StructuralOnly';status='INCOMPLETE';scope='Combinational source projection after source-resolved winner metadata; excludes winner arbitration, sequential CPU state, reset/stall/replay, architectural trap/CSR/mtvec, and independent full-Processor equivalence.';
    runtimeEvidence='Three runtime-derived Active/Golden facts from the v195/v196 exact-Golden probe, including WB winner, MEM-over-EX, and dual-MEM winner/payload distinction.';
    rtlEvidence='CIRCT export checked by Icarus on six vectors against separately coded oracle; dual-MEM winner-payload substitution mutant detected.';
    studio='Stable project and immutable digest-named snapshot emitted; load/graph/search/source reveal/pagination check remains pending.'
}
[IO.File]::WriteAllText((Join-Path $OutputRoot 'manifest.json'),($sliceManifest|ConvertTo-Json -Depth 8)+"`n",[Text.UTF8Encoding]::new($false))
Write-Host "Published structural-only page-fault delivery snapshot: $OutputRoot"
