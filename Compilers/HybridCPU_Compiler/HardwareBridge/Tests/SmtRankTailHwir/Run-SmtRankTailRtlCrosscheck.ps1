param(
    [string]$ProducerRoot = (Join-Path $env:USERPROFILE 'Desktop\CircuitWeave-main'),
    [string]$Distro = 'Ubuntu'
)
$ErrorActionPreference = 'Stop'
$BridgeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$RunnerProject = Join-Path $PSScriptRoot 'SmtRankTailHwir.csproj'
$Intermediate = Join-Path $BridgeRoot '.artifacts\smt-rank-tail-composed-v9'
$ArtifactRoot = Join-Path $ProducerRoot 'artifacts\processor-20261114'
$StudioRoot = Join-Path $ArtifactRoot 'smt-rank-collected-composition-v2'
$Output = Join-Path $ArtifactRoot 'smt-rank-rtl-v9'
$FirtoolWindows = Join-Path $ArtifactRoot 'power-control-candidate\tool\firtool-1.160.0\bin\circt-opt'
$FirtoolSha = '187c0c0f0f1464a58997a3e99ca96560cc002b97276cc38c12c28addf0112c4e'
$IverilogSha = 'a6681e0b577dcfc4ea83d1591a5714710b0b3535ebb36491ee8177553bab9631'
$VvpSha = 'e0a087a4906d5a9cd1b0b85c9b4c2081704c2c9bcd94f224437fc56b15fc440d'

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function To-WslPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).Replace('\', '/')
    if ($full -notmatch '^([A-Za-z]):/(.*)$') { throw "Expected drive-qualified Windows path: $Path" }
    return "/mnt/$($Matches[1].ToLowerInvariant())/$($Matches[2])"
}
function Invoke-Wsl([string]$Command) {
    & wsl.exe -d $Distro --exec /bin/bash -c $Command
    if ($LASTEXITCODE -ne 0) { throw "WSL command failed with exit $LASTEXITCODE`: $Command" }
}
function Write-Wsl([string]$Command, [string]$Stdout, [string]$Stderr) {
    $cmd = "$Command > $(To-WslPath $Stdout) 2> $(To-WslPath $Stderr)"
    Invoke-Wsl $cmd
}

if (!(Test-Path -LiteralPath $ProducerRoot -PathType Container)) { throw "Producer root missing: $ProducerRoot" }
if ((Get-Sha256 $FirtoolWindows) -ne $FirtoolSha) { throw 'Pinned firtool executable hash mismatch.' }
New-Item -ItemType Directory -Force -Path $Intermediate | Out-Null
foreach ($staleName in @('smt-rank-tail.sv','smt-rank-tail-simulation-receipt.json','firtool-version.txt','firtool-version.stderr.txt','firtool-export.log','iverilog-version.txt','iverilog-version.stderr.txt','vvp-version.txt','vvp-version.stderr.txt','iverilog-compile.log','vvp-simulation.log','tool-hashes.txt','smt-rank-tail.vvp')) {
    $stalePath = Join-Path $Intermediate $staleName
    if (Test-Path -LiteralPath $stalePath -PathType Leaf) { Remove-Item -LiteralPath $stalePath -Force }
}

Push-Location $BridgeRoot
try {
    dotnet run --project $RunnerProject -- $Intermediate
    if ($LASTEXITCODE -ne 0) { throw "HWIR build/oracle runner failed with exit $LASTEXITCODE." }
} finally { Pop-Location }
$pins = Get-Content -LiteralPath (Join-Path $Intermediate 'smt-rank-tail-source-pins.json') -Raw | ConvertFrom-Json
if ($pins.mlirSha256 -ne (Get-Sha256 (Join-Path $Intermediate 'smt-rank-tail.mlir')) -or $pins.modelGitHead -ne '1bfe41a49cf11c80fa6c6aee87accb54b78b331f') { throw 'Generated design or model HEAD differs from captured source pins.' }
Push-Location $BridgeRoot
try {
    dotnet run --project $RunnerProject -- --publish $Intermediate $StudioRoot
    if ($LASTEXITCODE -ne 0) { throw "Initial immutable source snapshot publication and Studio API check failed with exit $LASTEXITCODE." }
} finally { Pop-Location }
$manifestPath = Join-Path $StudioRoot 'studio.project.json'
$sourceManifestSha = Get-Sha256 $manifestPath
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$mlirRecord = $manifest.artifacts | Where-Object { $_.id -eq 'smt-rank-tail-mlir' } | Select-Object -First 1
if ($null -eq $mlirRecord) { throw 'Published Studio snapshot has no generated SMT MLIR artifact.' }
$SnapshotId = ($mlirRecord.relativePath -split '[\\/]')[1]
$InputSnapshot = Join-Path (Join-Path $StudioRoot 'snapshots') $SnapshotId
$mlirArtifact = Join-Path $InputSnapshot 'smt-rank-tail.mlir'
if ((Get-Sha256 $mlirArtifact) -ne $pins.mlirSha256) { throw 'Published immutable snapshot MLIR differs from generated candidate.' }
New-Item -ItemType Directory -Force -Path $Output | Out-Null
Copy-Item -LiteralPath $mlirArtifact -Destination (Join-Path $Output 'smt-rank-tail.mlir') -Force
Copy-Item -LiteralPath (Join-Path $Intermediate 'smt-rank-tail.tb.sv') -Destination (Join-Path $Output 'smt-rank-tail.tb.sv') -Force

$wslOut = To-WslPath $Output
$wslFirtool = To-WslPath $FirtoolWindows
Write-Wsl "$wslFirtool --version" (Join-Path $Output 'firtool-version.txt') (Join-Path $Output 'firtool-version.stderr.txt')
Write-Wsl "$wslFirtool --export-verilog $wslOut/smt-rank-tail.mlir -o /dev/null" (Join-Path $Output 'smt-rank-tail.sv') (Join-Path $Output 'firtool-export.log')
Write-Wsl '/usr/bin/iverilog -V' (Join-Path $Output 'iverilog-version.txt') (Join-Path $Output 'iverilog-version.stderr.txt')
Write-Wsl '/usr/bin/vvp -V' (Join-Path $Output 'vvp-version.txt') (Join-Path $Output 'vvp-version.stderr.txt')
Invoke-Wsl "/usr/bin/iverilog -g2012 -s smt_rank_tail_directed_tb -o $wslOut/smt-rank-tail.vvp $wslOut/smt-rank-tail.sv $wslOut/smt-rank-tail.tb.sv > $wslOut/iverilog-compile.log 2>&1"
Invoke-Wsl "/usr/bin/vvp $wslOut/smt-rank-tail.vvp > $wslOut/vvp-simulation.log 2>&1"
Invoke-Wsl "sha256sum $wslFirtool /usr/bin/iverilog /usr/bin/vvp > $wslOut/tool-hashes.txt"
$firtoolText = (Get-Content -LiteralPath (Join-Path $Output 'firtool-version.txt') -Raw) + (Get-Content -LiteralPath (Join-Path $Output 'firtool-version.stderr.txt') -Raw)
$iverilogText = (Get-Content -LiteralPath (Join-Path $Output 'iverilog-version.txt') -Raw) + (Get-Content -LiteralPath (Join-Path $Output 'iverilog-version.stderr.txt') -Raw)
$vvpText = (Get-Content -LiteralPath (Join-Path $Output 'vvp-version.txt') -Raw) + (Get-Content -LiteralPath (Join-Path $Output 'vvp-version.stderr.txt') -Raw)
$simulationText = Get-Content -LiteralPath (Join-Path $Output 'vvp-simulation.log') -Raw
if (!$firtoolText.Contains('CIRCT firtool-1.160.0') -or !$iverilogText.Contains('Icarus Verilog version 12.0 (stable)') -or
    !$vvpText.Contains('Icarus Verilog runtime version 12.0 (stable)') -or !$simulationText.Contains('PASS all directed RTL scenarios')) {
    throw 'RTL tool version, simulation output, or one or more directed assertions failed.'
}
$toolHashes = Get-Content -LiteralPath (Join-Path $Output 'tool-hashes.txt') -Raw
foreach ($pin in @($FirtoolSha, $IverilogSha, $VvpSha)) { if (!$toolHashes.Contains($pin)) { throw "Tool hash pin absent: $pin" } }

$receipt = [ordered]@{
    schema='circuitweave.smt-rank-tail-rtl-simulation/v1'; status='candidate-rtl-simulation-passed'; trust='StructuralOnly'
    source=[ordered]@{studioProject='artifacts/processor-20261114/smt-rank-collected-composition-v2/studio.project.json';manifestSha256=$sourceManifestSha;snapshotId=$SnapshotId;modelCommit=$pins.modelCommit;modelGitHead=$pins.modelGitHead;treeState=$pins.modelTreeState;hwirSha256=$pins.hwirSha256;mlirSha256=$pins.mlirSha256;mlirInputSha256=(Get-Sha256 (Join-Path $Output 'smt-rank-tail.mlir'))}
    tools=[ordered]@{circtVersion='CIRCT firtool-1.160.0';circtExecutableSha256=$FirtoolSha;iverilogVersion='Icarus Verilog version 12.0 (stable)';iverilogExecutableSha256=$IverilogSha;vvpVersion='Icarus Verilog runtime version 12.0 (stable)';vvpExecutableSha256=$VvpSha;environment="WSL2 $Distro"}
    commands=[ordered]@{rtl='circt-opt --export-verilog smt-rank-tail.mlir -o /dev/null';compile='iverilog -g2012 -s smt_rank_tail_directed_tb -o smt-rank-tail.vvp smt-rank-tail.sv smt-rank-tail.tb.sv';simulate='vvp smt-rank-tail.vvp';rtlExportExit=0;compileExit=0;simulationExit=0}
    artifacts=[ordered]@{rtlSha256=(Get-Sha256 (Join-Path $Output 'smt-rank-tail.sv'));rtlBytes=(Get-Item (Join-Path $Output 'smt-rank-tail.sv')).Length;testbenchSha256=(Get-Sha256 (Join-Path $Output 'smt-rank-tail.tb.sv'));compileLogSha256=(Get-Sha256 (Join-Path $Output 'iverilog-compile.log'));simulationLogSha256=(Get-Sha256 (Join-Path $Output 'vvp-simulation.log'));simulationOutput=$simulationText}
    scenarios=@('duplicate suppression after success; HardPinned lane 6 occupancy and class counter commit once','shared certificate conflict against initial bundle mask','HardPinned lane 6 conflict rejects without occupancy commit','same-register RAR remains admissible','RAW register conflict rejected','WAR register conflict rejected','WAW register conflict rejected','accepted candidate commits shared mask; next candidate observes conflict','accepted Load consumes shared/read budget before next candidate retry','Stage B lane rejection preserves memory budget state','ordered original bundle seeds saturated shared/read/write budget tuples','replay mask tuple ignored without complete key and template match','replay mask tuple selected when key and template match','sampled SuppressLsu rejects a memory candidate','sampled SuppressLsu bypasses a non-memory candidate','memory candidate passes when SuppressLsu is clear','legacy A3 LSU condition bit is ignored','per-VT same-bank outstanding memory rejects from scoreboard','another VT same-bank outstanding entry does not block this VT','different-bank outstanding entry does not block candidate','negative candidate bank id bypasses pending-bank check','legacy A3 bundle-store fact bit is ignored','same-bank original-bundle Store bank rejects with BankPendingReject','different-bank original-bundle Store does not block candidate','same-bank original-bundle Load does not set Store mask','same-bank original-bundle Store does not block candidate Load','out-of-range original-bundle Store bank does not set mask','A3 shared memory budget exhaustion derives HardwareBudgetReject','A3 Load uses read remaining budget','A3 Store uses write budget despite exhausted read budget','Atomic LoadStore subtype bypasses directional budget only','invalid bank bypasses per-bank limits while global credits remain active','packed two-bit per-bank consumed count is compared with sampled mask limit','A3 cross-VT speculation exhaustion returns SpeculationBudgetReject','scoreboard type scan counts OutstandingLoad and OutstandingStore','allocator-value scan counts free slots independently of entry type','outstanding plus free capacity saturates at eight','original bundle LoadStore facts seed projected count in slot order','projected memory capacity denies a full per-VT count','projected count increments only for accepted LoadStore candidate; legacy condition bit ignored','non-memory bypasses projected capacity','memory-marked non-LoadStore candidate does not consume scoreboard count','A3 selects first source-order rejection','Assist bypasses A3')
    limitations='Finite directed RTL candidate simulation only. Source-bound CanInjectWithReason shared-resource and per-VT RAW/WAR/WAW predicate, committed bundle mask accumulation, source-order A3 reason selection, per-VT bank-pending from scoreboard occupancy/type/bank, bundle-local outstanding Store bank mask from ordered sampled bundle slots, scoreboard-derived initial outstanding count, allocator-value-derived free capacity, original-bundle LoadStore count seeding, projected-capacity comparison, accepted-LoadStore-only count commit, current-state A3 bit4 budget predicate, and ordered accepted-candidate budget decrement/packed-bank increment are modeled over explicit inputs. The bit4 predicate derives shared/read/write remaining tests, per-bank consumed counters, 16-bank at-least-one/two masks, invalid-bank bypass and Load/Store/other-LoadStore subtype behavior. Initial remaining credits are derived from the sampled snapshot and original bundle facts; starting packed counters are zero then seeded in slot order. Snapshot values and bundle typed facts remain explicit sampled inputs not tied to runtime object identity. Candidate subtype/bank and original-bundle facts remain inputs not bound to runtime object identity. A3 bit5, rejection diagnostic counters, exact phase key/cache identity, post-success refresh and scheduler clock/reset/stall/replay lifecycle remain unmodeled. No complete method equivalence, Golden behavior comparison or formal proof; no waveform emitted.'
}
$receiptPath = Join-Path $Output 'smt-rank-tail-simulation-receipt.json'
[IO.File]::WriteAllText($receiptPath, (ConvertTo-Json -InputObject $receipt -Depth 20) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$publishFiles = @('smt-rank-tail.sv','smt-rank-tail.tb.sv','smt-rank-tail-simulation-receipt.json','firtool-version.txt','firtool-export.log','iverilog-version.txt','vvp-version.txt','iverilog-compile.log','vvp-simulation.log','tool-hashes.txt')
foreach ($name in $publishFiles) { Copy-Item -LiteralPath (Join-Path $Output $name) -Destination (Join-Path $Intermediate $name) -Force }
Push-Location $BridgeRoot
try {
    dotnet run --project $RunnerProject -- --publish $Intermediate $StudioRoot
    if ($LASTEXITCODE -ne 0) { throw "Studio publication and consumer API check failed with exit $LASTEXITCODE." }
} finally { Pop-Location }
Write-Output "Candidate RTL and directed Icarus scenarios passed; published to $StudioRoot"
