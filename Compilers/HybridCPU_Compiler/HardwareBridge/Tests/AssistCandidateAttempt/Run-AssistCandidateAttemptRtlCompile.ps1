param([string]$Distro='Ubuntu')
$producer='C:\Users\Yuriy\Desktop\CircuitWeave-main'
$ErrorActionPreference='Stop'
$bridge=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=Join-Path $bridge '.artifacts\assist-candidate-attempt-v1'
$fir=Join-Path $producer 'artifacts\processor-20261114\power-control-candidate\tool\firtool-1.160.0\bin\circt-opt'
$pins=@{firtool='187c0c0f0f1464a58997a3e99ca96560cc002b97276cc38c12c28addf0112c4e';iverilog='a6681e0b577dcfc4ea83d1591a5714710b0b3535ebb36491ee8177553bab9631'}
function Sha([string]$p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()}
function WslPath([string]$p){$f=[IO.Path]::GetFullPath($p).Replace('\','/');if($f -notmatch '^([A-Za-z]):/(.*)$'){throw "Bad drive path: $p"};"/mnt/$($Matches[1].ToLowerInvariant())/$($Matches[2])"}
function Wsl([string]$c){& wsl.exe -d $Distro --exec /bin/bash -c $c;if($LASTEXITCODE){throw "WSL failed ($LASTEXITCODE): $c"}}
if((Sha $fir)-ne $pins.firtool){throw 'Pinned firtool hash mismatch.'}
New-Item -ItemType Directory -Force -Path $output|Out-Null
Push-Location $bridge
try { dotnet run --project Tests\AssistCandidateAttempt\AssistCandidateAttempt.csproj -- --emit $output; if($LASTEXITCODE){throw 'Assist candidate composition/MLIR emission failed.'} }
finally { Pop-Location }
$wo=WslPath $output; $wf=WslPath $fir
Wsl "`"$wf`" --export-verilog `"$wo/assist-candidate-attempt.mlir`" -o /dev/null > `"$wo/assist-candidate-attempt.sv`" 2> `"$wo/firtool-export.log`""
Wsl "/usr/bin/iverilog -g2012 -s assist_candidate_attempt -tnull `"$wo/assist-candidate-attempt.sv`" > `"$wo/iverilog-compile.log`" 2>&1"
Wsl "`"$wf`" --export-verilog `"$wo/assist-ranked-candidate-chain.mlir`" -o /dev/null > `"$wo/assist-ranked-candidate-chain.sv`" 2> `"$wo/ranked-firtool-export.log`""
Wsl "/usr/bin/iverilog -g2012 -s assist_ordered_candidate_chain -tnull `"$wo/assist-ranked-candidate-chain.sv`" > `"$wo/ranked-iverilog-compile.log`" 2>&1"
Wsl "/usr/bin/iverilog -g2012 -s assist_ranked_candidate_chain_tb -o `"$wo/assist-ranked-candidate-chain.tb.vvp`" `"$wo/assist-ranked-candidate-chain.sv`" `"$wo/assist-ranked-candidate-chain.tb.sv`" > `"$wo/ranked-tb-compile.log`" 2>&1"
Wsl "cd `"$wo`" && /usr/bin/vvp ./assist-ranked-candidate-chain.tb.vvp > ./ranked-tb-simulation.log 2>&1"
Wsl "/usr/bin/iverilog -V > `"$wo/iverilog-version.txt`" 2> `"$wo/iverilog-version.stderr.txt`""
Wsl "sha256sum `"$wf`" /usr/bin/iverilog > `"$wo/tool-hashes.txt`""
if(!(Test-Path (Join-Path $output 'assist-candidate-attempt.sv')) -or (Get-Item (Join-Path $output 'assist-candidate-attempt.sv')).Length -eq 0){throw 'RTL output is missing.'}
if(!(Test-Path (Join-Path $output 'assist-ranked-candidate-chain.sv')) -or (Get-Item (Join-Path $output 'assist-ranked-candidate-chain.sv')).Length -eq 0){throw 'Ranked RTL output is missing.'}
$simulationLog=Get-Content (Join-Path $output 'ranked-tb-simulation.log') -Raw
if($simulationLog -notmatch 'PASS all ranked RTL crosscheck vectors'){throw 'Ranked RTL crosscheck did not pass.'}
if(!(Test-Path (Join-Path $output 'assist-ranked-candidate-chain.vcd')) -or (Get-Item (Join-Path $output 'assist-ranked-candidate-chain.vcd')).Length -eq 0){throw 'Ranked RTL waveform was not produced.'}
$crosscheck=Get-Content (Join-Path $output 'assist-ranked-candidate-chain-independent-evaluator.json') -Raw|ConvertFrom-Json
if($crosscheck.vectorCount -lt 1 -or $crosscheck.rankCommitVectorCounts.rank0 -lt 1){throw 'Directed rank-0 commit witness is absent.'}
$toolText=Get-Content (Join-Path $output 'tool-hashes.txt') -Raw;foreach($pin in $pins.Values){if(!$toolText.Contains($pin)){throw "Tool hash pin absent: $pin"}}
$receipt=[ordered]@{schema='circuitweave.assist-ranked-candidate-chain-rtl-compile-receipt/v3';status='passed';trust='IndependentCombinationalDifferential';attemptHwirSha256=(Sha (Join-Path $output 'assist-candidate-attempt.hwir.json'));attemptMlirSha256=(Sha (Join-Path $output 'assist-candidate-attempt.mlir'));attemptRtlSha256=(Sha (Join-Path $output 'assist-candidate-attempt.sv'));rankedHwirSha256=(Sha (Join-Path $output 'assist-ranked-candidate-chain.hwir.json'));rankedMlirSha256=(Sha (Join-Path $output 'assist-ranked-candidate-chain.mlir'));rankedRtlSha256=(Sha (Join-Path $output 'assist-ranked-candidate-chain.sv'));testbenchSha256=(Sha (Join-Path $output 'assist-ranked-candidate-chain.tb.sv'));independentEvaluatorReceiptSha256=(Sha (Join-Path $output 'assist-ranked-candidate-chain-independent-evaluator.json'));simulationLogSha256=(Sha (Join-Path $output 'ranked-tb-simulation.log'));waveformSha256=(Sha (Join-Path $output 'assist-ranked-candidate-chain.vcd'));rankCommitVectorCounts=$crosscheck.rankCommitVectorCounts;vectorCount=$crosscheck.vectorCount;outputComparisons=$crosscheck.totalOutputComparisons;firtoolSha256=$pins.firtool;iverilogSha256=$pins.iverilog;simulation="Icarus executed $($crosscheck.vectorCount) deterministic input vectors; $($crosscheck.totalOutputComparisons) RTL outputs matched recursive HWIR evaluator; includes Stage A/B, backpressure, quota and rank-0 commit acceptance; VCD emitted.";scope='Combinational lowerer differential only. HWIR evaluator is not source-level independent behavior, Golden equivalence, or a sequential reset/stall/replay test.'}
$receipt|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'assist-candidate-attempt-rtl-compile-receipt.json') -Encoding utf8
$ranked=Get-Content (Join-Path $output 'assist-ranked-candidate-chain.hwir.json') -Raw|ConvertFrom-Json
$top=$ranked.modules|Where-Object id -eq 'assist_ordered_candidate_chain'
$attempt=$ranked.modules|Where-Object id -eq 'assist_candidate_attempt'
$fields=[System.Collections.Generic.List[object]]::new()
foreach($rank in 0..3){
    $instance=$top.instances|Where-Object id -eq "attempt_rank$rank"
    foreach($port in ($attempt.ports|Where-Object direction -eq 'Input')){
        $signal=$instance.portConnections.PSObject.Properties[$port.name].Value
        if($signal -notmatch "^rank${rank}_facts_"){continue}
        $bus=$signal;$lsb=0
        if($signal -match "^rank${rank}_fact_value_(.+)$"){
            $valueNode=$top.nodes|Where-Object id -eq $signal
            $sourceSignal=$valueNode.operands[0]
            if($sourceSignal -match "^rank${rank}_fact_shifted_(.+)$"){
                $shiftNode=$top.nodes|Where-Object id -eq "rank${rank}_fact_shift_$($Matches[1])"
                $lsb=[Convert]::ToInt32($shiftNode.bitPattern,2)
                $bus=($top.nodes|Where-Object id -eq $sourceSignal).operands[0]
            }else{$bus=$sourceSignal}
        }
        $fields.Add([ordered]@{rank=$rank;childInput=$port.name;width=$port.type.width;packedInput=$bus;lsb=$lsb})
    }
}
$layout=[ordered]@{schema='circuitweave.assist-ranked-fact-bus-layout/v1';topModule='assist_ordered_candidate_chain';packing='Unsigned two-state fact input ports packed in child HWIR input-port order, no field crosses a 64-bit bus; lshr64_mask6 then trunc for nonzero offsets';fields=$fields}
$layout|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'fact-bus-layout.json') -Encoding utf8
$published=Join-Path $producer 'artifacts\processor-20261114\assist-ranked-candidate-chain-v1'
New-Item -ItemType Directory -Force -Path $published|Out-Null
Copy-Item -LiteralPath (Join-Path $output 'assist-candidate-attempt.hwir.json'),(Join-Path $output 'assist-candidate-attempt.mlir'),(Join-Path $output 'assist-candidate-attempt.sv'),(Join-Path $output 'assist-ranked-candidate-chain.hwir.json'),(Join-Path $output 'assist-ranked-candidate-chain.mlir'),(Join-Path $output 'assist-ranked-candidate-chain.sv'),(Join-Path $output 'assist-ranked-candidate-chain.tb.sv'),(Join-Path $output 'assist-ranked-candidate-chain-independent-evaluator.json'),(Join-Path $output 'assist-ranked-candidate-chain.vcd'),(Join-Path $output 'ranked-tb-simulation.log'),(Join-Path $output 'assist-candidate-attempt-rtl-compile-receipt.json'),(Join-Path $output 'fact-bus-layout.json') -Destination $published -Force
Push-Location $bridge
try {
    $publishOutput=dotnet run --project Tests\AssistCandidateAttempt\AssistCandidateAttempt.csproj -- --publish $published $published
    $publishOutput
    if($LASTEXITCODE){throw 'Assist ranked Studio publication failed.'}
    if(($publishOutput -join "`n") -notmatch 'status=CandidateOnly'){throw 'Studio publication did not reach candidate-only status.'}
}
finally { Pop-Location }
Write-Output "Assist candidate attempt RTL and Studio publication PASS: $published"
