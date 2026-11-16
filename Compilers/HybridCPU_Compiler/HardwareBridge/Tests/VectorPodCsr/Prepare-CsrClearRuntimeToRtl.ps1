param(
    [string]$ProducerRoot = 'C:\Users\Yuriy\Desktop\CircuitWeave-main',
    [string]$CandidateSource = 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\processor-20261114\csr-clear-retire-path-candidate-v3',
    [string]$RuntimeRoot = 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\processor-20261114\csr-clear-runtime-v2',
    [string]$OutputDirectory = 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\processor-20261114\csr-clear-retire-path-runtime-v4',
    [string]$CirctOpt = 'C:\Users\Yuriy\Desktop\CircuitWeave ExTool\final\packages\circt-firtool-1.163.0\firtool-1.163.0\bin\circt-opt.exe',
    [string]$IcarusBin = 'C:\Users\Yuriy\Desktop\CircuitWeave ExTool\final\packages\oss-cad-suite-2026-11-10\oss-cad-suite\bin'
)
$ErrorActionPreference = 'Stop'
$goldenCommit = '6946ccab9d888f7dae0a4d8e18480f436138acd9'
$activeCommit = '1bfe41a49cf11c80fa6c6aee87accb54b78b331f'
if (Test-Path -LiteralPath $OutputDirectory) { throw "OutputDirectory must be new: $OutputDirectory" }
if (-not (Test-Path -LiteralPath (Join-Path $CandidateSource 'vector-pod-csr.hwir.json')) -or
    -not (Test-Path -LiteralPath (Join-Path $RuntimeRoot 'comparison.json'))) { throw 'Pinned candidate or active/Golden runtime evidence is missing.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
Get-ChildItem -LiteralPath $CandidateSource -File | Copy-Item -Destination $OutputDirectory

function Get-Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
$comparisonPath = Join-Path $RuntimeRoot 'comparison.json'
$comparison = Get-Content -LiteralPath $comparisonPath -Raw | ConvertFrom-Json
$activeTracePath = Join-Path $RuntimeRoot 'active-trace.json'
$goldenTracePath = Join-Path $RuntimeRoot 'golden-trace.json'
$activeTrace = Get-Content -LiteralPath $activeTracePath -Raw | ConvertFrom-Json
$goldenTrace = Get-Content -LiteralPath $goldenTracePath -Raw | ConvertFrom-Json
if ($comparison.schema -ne 'circuitweave.csr-clear-runtime-differential/v1' -or
    $comparison.activeModelCommit -ne $activeCommit -or $comparison.activeModelGitHead -ne $activeCommit -or
    $comparison.goldenCommit -ne $goldenCommit -or $comparison.tracesEqual -ne $true -or
    $comparison.eventCount -ne 3 -or $activeTrace.Count -ne 3 -or $goldenTrace.Count -ne 3 -or
    $comparison.activeTraceSha256 -ne $comparison.goldenTraceSha256 -or
    $comparison.activeTraceSha256 -ne (Get-Sha $activeTracePath) -or
    $comparison.goldenTraceSha256 -ne (Get-Sha $goldenTracePath) -or
    $comparison.activeTraceTestLogSha256 -ne (Get-Sha (Join-Path $RuntimeRoot 'active-test.log')) -or
    $comparison.goldenTraceTestLogSha256 -ne (Get-Sha (Join-Path $RuntimeRoot 'golden-test.log'))) {
    throw 'CSR_CLEAR active/Golden runtime trace receipt did not validate.'
}

$expectedScenarios = @('effect-excluded-from-selected-prefix','prevalidation-rejected-batch','selected-batch-applied')
$vectorPath = Join-Path $OutputDirectory 'csr-clear-retire-path-vectors.txt'
$appendLines = [Collections.Generic.List[string]]::new()
$samples = [Collections.Generic.List[object]]::new()
$eventIndex = 0
foreach ($event in $activeTrace) {
    if ($event.scenario -ne $expectedScenarios[$eventIndex] -or $event.instructionValid -ne $true -or $event.opcode -ne 149) {
        throw "Unexpected runtime event/order at index $eventIndex."
    }
    $current = @($event.before | ForEach-Object { [uint32]$_ })
    $observed = @($event.after | ForEach-Object { [uint32]$_ })
    $clear = $event.retireBatchApplyEvent -eq 1
    $expected = if ($clear) { @(0,0,0,0,0) } else { $current }
    if ($clear -and ($event.batchAccepted -ne $true -or -not $event.effectCreated) -or
        -not $clear -and ($event.batchAccepted -ne $false -and $event.scenario -eq 'prevalidation-rejected-batch') -or
        ($observed -join ',') -ne ($expected -join ',')) {
        throw "Runtime event $($event.scenario) does not satisfy the HWIR event equation."
    }
    $line = ('{0:X4} {1} {2} {3} {4}' -f [int]$event.opcode, [int][bool]$event.instructionValid,
        [int]$event.retireBatchApplyEvent,
        (($current | ForEach-Object { $_.ToString('X8') }) -join ' '),
        (($observed | ForEach-Object { $_.ToString('X8') }) -join ' '))
    $appendLines.Add($line)
    $samples.Add([ordered]@{scenario=$event.scenario;instructionValid=$event.instructionValid;opcode=$event.opcode;retireBatchApplyEvent=$event.retireBatchApplyEvent;current=$current;runtimeNext=$observed;equationNext=$expected})
    $eventIndex++
}
if ($eventIndex -ne 3) { throw 'The runtime trace did not contain the three expected events.' }
[IO.File]::AppendAllText($vectorPath, ($appendLines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))

$rtlPath = Join-Path $OutputDirectory 'vector-pod-csr.sv'
$testbenchPath = Join-Path $OutputDirectory 'CsrClearRetirePathVectors.sv'
$simPath = Join-Path $OutputDirectory 'csr_clear_runtime_path_tb'
$logPath = Join-Path $OutputDirectory 'csr-clear-retire-path-simulation.log'
$iverilog = Join-Path $IcarusBin 'iverilog.exe'
$vvp = Join-Path $IcarusBin 'vvp.exe'
if (-not (Test-Path -LiteralPath $rtlPath) -or -not (Test-Path -LiteralPath $testbenchPath) -or
    -not (Test-Path -LiteralPath $CirctOpt) -or -not (Test-Path -LiteralPath $iverilog) -or -not (Test-Path -LiteralPath $vvp)) {
    throw 'Required pinned CIRCT/Icarus tools or candidate RTL/testbench are missing.'
}
Push-Location $OutputDirectory
$priorPath = $env:PATH
$env:PATH = $IcarusBin + [IO.Path]::PathSeparator + (Join-Path (Split-Path -Parent $IcarusBin) 'lib') + [IO.Path]::PathSeparator + $priorPath
try {
    & $iverilog -g2012 -s csr_clear_retire_path_tb -o $simPath $rtlPath $testbenchPath *> $logPath
    if ($LASTEXITCODE -ne 0) { throw "Icarus compile failed: $LASTEXITCODE" }
    & $vvp $simPath *> (Join-Path $OutputDirectory 'csr-clear-retire-path-simulation.vvp.log')
    if ($LASTEXITCODE -ne 0) { throw "Icarus simulation failed: $LASTEXITCODE" }
    Get-Content -LiteralPath (Join-Path $OutputDirectory 'csr-clear-retire-path-simulation.vvp.log') | Add-Content -LiteralPath $logPath -Encoding utf8
} finally { $env:PATH = $priorPath; Pop-Location }
if ((Get-Content -LiteralPath $logPath -Raw) -notmatch 'PASS: 262147 CSR_CLEAR decode-to-retire event vectors') {
    throw 'Icarus did not report the baseline plus three runtime-derived CSR_CLEAR vectors.'
}

$rtlReceipt = [ordered]@{
    schema='circuitweave.csr-clear-runtime-to-rtl/v1'
    status='PASS_ACTIVE_GOLDEN_RUNTIME_TRACE_TO_HWIREQUATION_TO_ICARUS'
    activeModelCommit=$activeCommit
    goldenCommit=$goldenCommit
    runtimeComparisonSha256=(Get-Sha $comparisonPath)
    activeTraceSha256=$comparison.activeTraceSha256
    goldenTraceSha256=$comparison.goldenTraceSha256
    runtimeEventCount=3
    runtimeTraceSha256=$comparison.activeTraceSha256
    simulatedRuntimeVectors=3
    baselineDecodeRetireVectors=262144
    totalDecodeRetireVectors=262147
    mismatches=0
    scenarios=$samples
    vectorFileSha256=(Get-Sha $vectorPath)
    rtlSha256=(Get-Sha $rtlPath)
    testbenchSha256=(Get-Sha $testbenchPath)
    simulationLogSha256=(Get-Sha $logPath)
    iverilogSha256=(Get-Sha $iverilog)
    vvpSha256=(Get-Sha $vvp)
    scope='Three real active/Golden software call-event transitions replayed as inputs to the source-bound combinational CSR_CLEAR HWIR and matched in Icarus; no physical clock, measured retire cycle, or general sequential equivalence.'
}
$receiptPath = Join-Path $OutputDirectory 'csr-clear-runtime-to-rtl-receipt.json'
$rtlReceipt | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $receiptPath -Encoding utf8

foreach ($receiptName in @('candidate-receipt.json','receipt.json')) {
    $path = Join-Path $OutputDirectory $receiptName
    $receipt = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    $receipt.csrClearRetirePathVectorCount = 262147
    $receipt.expectedCsrClearRetirePathVectorFileSha256 = Get-Sha $vectorPath
    $receipt.activeGoldenRuntimeDifferential = [ordered]@{status='PASS';events=3;traceSha256=$comparison.activeTraceSha256;comparisonSha256=(Get-Sha $comparisonPath);scope='decode/effect/selected-batch five-counter call event'}
    $receipt | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path -Encoding utf8
}
$simulationReceiptPath = Join-Path $OutputDirectory 'simulation-receipt.json'
$simulationReceipt = Get-Content -LiteralPath $simulationReceiptPath -Raw | ConvertFrom-Json -AsHashtable
$simulationReceipt.decodeRetireCallEventVectors = 262147
$simulationReceipt.sha256.'csr-clear-retire-path-vectors.txt' = Get-Sha $vectorPath
$simulationReceipt.sha256.'csr-clear-retire-path-simulation.log' = Get-Sha $logPath
$simulationReceipt.activeGoldenRuntimeDerivedVectors = 3
$simulationReceipt | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $simulationReceiptPath -Encoding utf8
$verificationPath = Join-Path $OutputDirectory 'verification-receipt-v3.json'
$verification = Get-Content (Join-Path $OutputDirectory 'verification-receipt-v2.json') -Raw | ConvertFrom-Json -AsHashtable
$verification.csrClearRuntimeToRtlReceiptSha256 = Get-Sha $receiptPath
$verification.independentIcarus.artifactSha256.'csr-clear-retire-path-vectors.txt' = Get-Sha $vectorPath
$verification.independentIcarus.artifactSha256.'csr-clear-retire-path-simulation.log' = Get-Sha $logPath
$verification.independentIcarus.results[2] = 'PASS: 262147 CSR_CLEAR decode-to-retire event vectors, including three active/Golden runtime-derived samples'
$verification.independentIcarus.vectors = 645767
$verification.runtimeDifferential = [ordered]@{status='PASS';events=3;traceSha256=$comparison.activeTraceSha256;goldenCommit=$goldenCommit}
$verification | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $verificationPath -Encoding utf8
Write-Output "Runtime-derived CSR_CLEAR vectors matched HWIR RTL: 3/3; full call-event vectors: 262147; evidence: $OutputDirectory"
