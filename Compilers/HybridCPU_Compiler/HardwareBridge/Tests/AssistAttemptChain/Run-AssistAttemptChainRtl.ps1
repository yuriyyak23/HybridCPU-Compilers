param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '.artifacts'),
    [string]$ProducerPublication = 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\processor-20261114\assist-attempt-chain-v3'
)
$ErrorActionPreference = 'Stop'
$BridgeRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$Output = [IO.Path]::GetFullPath($OutputDirectory)
if (!$Output.StartsWith($BridgeRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Output must stay under HardwareBridge: $Output"
}
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$Distro = 'Ubuntu'
$FirtoolWindows = 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\processor-20261114\power-control-candidate\tool\firtool-1.160.0\bin\circt-opt'
$FirtoolSha = '187c0c0f0f1464a58997a3e99ca96560cc002b97276cc38c12c28addf0112c4e'
$IverilogSha = 'a6681e0b577dcfc4ea83d1591a5714710b0b3535ebb36491ee8177553bab9631'
$VvpSha = 'e0a087a4906d5a9cd1b0b85c9b4c2081704c2c9bcd94f224437fc56b15fc440d'
function Get-Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function To-WslPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -notmatch '^([A-Za-z]):\\(.*)$') { throw "Expected drive rooted Windows path: $full" }
    return "/mnt/$($Matches[1].ToLowerInvariant())/$($Matches[2].Replace('\','/'))"
}
function Invoke-Wsl([string]$Command) {
    & wsl.exe -d $Distro --exec /bin/bash -c $Command
    if ($LASTEXITCODE -ne 0) { throw "WSL command failed ($LASTEXITCODE): $Command" }
}
if ((Get-Sha $FirtoolWindows) -ne $FirtoolSha) { throw 'Pinned firtool SHA mismatch.' }
$wslOutput = To-WslPath $Output
$wslFirtool = To-WslPath $FirtoolWindows
$qOutput = '"' + $wslOutput + '"'
$qFirtool = '"' + $wslFirtool + '"'
$project = Join-Path $PSScriptRoot 'AssistAttemptChain.csproj'
dotnet run --project $project --no-restore -p:NoWarn=1591 -- $Output
if ($LASTEXITCODE) { throw 'Assist attempt-chain semantic evaluator failed.' }
Invoke-Wsl "$qFirtool --version > $qOutput/firtool-version.txt 2> $qOutput/firtool-version.stderr.txt"
Invoke-Wsl "$qFirtool --export-verilog $qOutput/assist-attempt-chain.mlir -o /dev/null > $qOutput/assist-attempt-chain.sv 2> $qOutput/firtool-export.log"
Invoke-Wsl "/usr/bin/iverilog -V > $qOutput/iverilog-version.txt 2>&1"
Invoke-Wsl "/usr/bin/vvp -V > $qOutput/vvp-version.txt 2>&1"
Invoke-Wsl "/usr/bin/iverilog -g2012 -s assist_attempt_chain_tb -o $qOutput/assist-attempt-chain.vvp $qOutput/assist-attempt-chain.sv $qOutput/assist-attempt-chain.tb.sv > $qOutput/iverilog-compile.log 2>&1"
Invoke-Wsl "cd $qOutput && /usr/bin/vvp ./assist-attempt-chain.vvp > ./vvp-simulation.log 2>&1"
Invoke-Wsl "sha256sum $qFirtool /usr/bin/iverilog /usr/bin/vvp > $qOutput/tool-hashes.txt"
$simulation = Get-Content -LiteralPath (Join-Path $Output 'vvp-simulation.log') -Raw
if (!$simulation.Contains('PASS all Assist attempt-chain scenarios')) { throw 'Directed RTL testbench did not reach its PASS terminator.' }
$iverilogVersion = Get-Content -LiteralPath (Join-Path $Output 'iverilog-version.txt') -Raw
$vvpVersion = Get-Content -LiteralPath (Join-Path $Output 'vvp-version.txt') -Raw
if (!$iverilogVersion.Contains('Icarus Verilog version 12.0 (stable)') -or !$vvpVersion.Contains('Icarus Verilog runtime version 12.0 (stable)')) { throw 'Icarus version text differs from the pinned toolchain.' }
$hashes = Get-Content -LiteralPath (Join-Path $Output 'tool-hashes.txt') -Raw
foreach ($pin in @($FirtoolSha,$IverilogSha,$VvpSha)) { if (!$hashes.Contains($pin)) { throw "Tool hash absent: $pin" } }
$receipt = [ordered]@{
    schema='circuitweave.assist-attempt-chain-rtl-receipt/v1'; status='passed'; trust='StructuralOnly'
    sourceSha256='8515039c3d770382fd448439d2453c01bd42397f3b525b413d411161bf8035a8'
    semanticVectors=4108; directedScenarios=12; rtlSha256=(Get-Sha (Join-Path $Output 'assist-attempt-chain.sv'))
    testbenchSha256=(Get-Sha (Join-Path $Output 'assist-attempt-chain.tb.sv'))
    simulationLogSha256=(Get-Sha (Join-Path $Output 'vvp-simulation.log'))
    vcd='not-emitted-combinational-only'
    firtoolSha256=$FirtoolSha; iverilogSha256=$IverilogSha; vvpSha256=$VvpSha
    limitation='Ranks, runtime port facts, Stage A/B decisions and backpressure/quota outcomes are inputs. No reservation-state cascade, rank3 credit ranking definition, cycle lifecycle, full Processor or Golden equivalence.'
}
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Output 'assist-attempt-chain-rtl-receipt.json') -Encoding utf8
dotnet run --project $project --no-restore -p:NoWarn=1591 -- --publish $Output $ProducerPublication
if ($LASTEXITCODE) { throw 'Assist attempt-chain Studio API publication failed.' }
Write-Output "Assist attempt-chain RTL PASS; RTL=$(Get-Sha (Join-Path $Output 'assist-attempt-chain.sv')); log=$(Get-Sha (Join-Path $Output 'vvp-simulation.log'))"
