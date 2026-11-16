param([string]$ProducerRoot='C:\Users\Yuriy\Desktop\CircuitWeave-main',[string]$Distro='Ubuntu')
$ErrorActionPreference='Stop'
$bridge=Join-Path $PSScriptRoot '..\..'; $bridge=[IO.Path]::GetFullPath($bridge)
$intermediate=Join-Path $bridge '.artifacts\assist-reservation-v1'
$artifactRoot=Join-Path $ProducerRoot 'artifacts\processor-20261114'
$output=Join-Path $artifactRoot 'assist-reservation-rtl-v1'
$studio=Join-Path $artifactRoot 'assist-reservation-v1'
$runner=Join-Path $PSScriptRoot 'AssistQuota.csproj'
$fir=Join-Path $artifactRoot 'power-control-candidate\tool\firtool-1.160.0\bin\circt-opt'
$pins=@{firtool='187c0c0f0f1464a58997a3e99ca96560cc002b97276cc38c12c28addf0112c4e';iverilog='a6681e0b577dcfc4ea83d1591a5714710b0b3535ebb36491ee8177553bab9631';vvp='e0a087a4906d5a9cd1b0b85c9b4c2081704c2c9bcd94f224437fc56b15fc440d'}
function Sha([string]$p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()}
function WslPath([string]$p){$f=[IO.Path]::GetFullPath($p).Replace('\','/');if($f -notmatch '^([A-Za-z]):/(.*)$'){throw "Bad drive path: $p"};"/mnt/$($Matches[1].ToLowerInvariant())/$($Matches[2])"}
function Wsl([string]$c){& wsl.exe -d $Distro --exec /bin/bash -c $c;if($LASTEXITCODE){throw "WSL failed ($LASTEXITCODE): $c"}}
if((Sha $fir)-ne $pins.firtool){throw 'Pinned firtool hash mismatch.'}
New-Item -ItemType Directory -Force -Path $intermediate,$output,$studio|Out-Null
Push-Location $bridge
try { dotnet run --project $runner -- --quick --emit $intermediate; if($LASTEXITCODE){throw 'Assist evaluator/emitter failed.'} }
finally {Pop-Location}
$wp=WslPath $output; $wf=WslPath $fir
Copy-Item -LiteralPath (Join-Path $intermediate 'assist-reservation.mlir') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $intermediate 'assist-reservation.tb.sv') -Destination $output -Force
Wsl "$wf --version > $wp/firtool-version.txt 2> $wp/firtool-version.stderr.txt"
Wsl "$wf --export-verilog $wp/assist-reservation.mlir -o /dev/null > $wp/assist-reservation.sv 2> $wp/firtool-export.log"
Wsl "/usr/bin/iverilog -V > $wp/iverilog-version.txt 2> $wp/iverilog-version.stderr.txt"
Wsl "/usr/bin/vvp -V > $wp/vvp-version.txt 2> $wp/vvp-version.stderr.txt"
Wsl "/usr/bin/iverilog -g2012 -s assist_reservation_directed_tb -o $wp/assist-reservation.vvp $wp/assist-reservation.sv $wp/assist-reservation.tb.sv > $wp/iverilog-compile.log 2>&1"
Wsl "cd $wp && /usr/bin/vvp ./assist-reservation.vvp > vvp-simulation.log 2>&1"
Wsl "sha256sum $wf /usr/bin/iverilog /usr/bin/vvp > $wp/tool-hashes.txt"
$rtlPath=Join-Path $output 'assist-reservation.sv'
if(!(Test-Path -LiteralPath $rtlPath -PathType Leaf) -or (Get-Item -LiteralPath $rtlPath).Length -eq 0){throw 'RTL output missing.'}
$sim=Get-Content (Join-Path $output 'vvp-simulation.log') -Raw
if(!$sim.Contains('PASS all directed Assist reservation RTL scenarios')){throw 'Directed RTL simulation assertions did not pass.'}
if(!$sim.Contains('PASS Unrecognized ResourceConflict telemetry holds detail counters')){throw 'Unrecognized telemetry assertions did not pass.'}
$toolText=Get-Content (Join-Path $output 'tool-hashes.txt') -Raw
foreach($pin in @($pins.firtool,$pins.iverilog,$pins.vvp)){if(!$toolText.Contains($pin)){throw "Tool hash pin absent: $pin"}}
$firVersion=(Get-Content (Join-Path $output 'firtool-version.txt') -Raw)+(Get-Content (Join-Path $output 'firtool-version.stderr.txt') -Raw)
$ivVersion=(Get-Content (Join-Path $output 'iverilog-version.txt') -Raw)+(Get-Content (Join-Path $output 'iverilog-version.stderr.txt') -Raw)
$vvpVersion=(Get-Content (Join-Path $output 'vvp-version.txt') -Raw)+(Get-Content (Join-Path $output 'vvp-version.stderr.txt') -Raw)
if(!$firVersion.Contains('CIRCT firtool-1.160.0') -or !$ivVersion.Contains('Icarus Verilog version 12.0 (stable)') -or !$vvpVersion.Contains('Icarus Verilog runtime version 12.0 (stable)')){throw 'Tool version text differs from pins.'}
foreach($name in @('assist-reservation.vcd','assist-reservation.sv','assist-reservation.mlir','assist-reservation.tb.sv','firtool-export.log','iverilog-compile.log','vvp-simulation.log','tool-hashes.txt')){if(!(Test-Path (Join-Path $output $name))){throw "Missing RTL evidence $name"}}
$receipt=[ordered]@{schema='circuitweave.assist-reservation-rtl-receipt/v1';status='passed';semanticVectors=24192;rtlScenarios=11;firtoolSha256=$pins.firtool;iverilogSha256=$pins.iverilog;vvpSha256=$pins.vvp;rtlSha256=(Sha (Join-Path $output 'assist-reservation.sv'));vcdSha256=(Sha (Join-Path $output 'assist-reservation.vcd'));simulationLogSha256=(Sha (Join-Path $output 'vvp-simulation.log'));scope='Quick independent combinational evaluator plus eleven directed reservation and Stage A/B counter scenarios, including unrecognized ResourceConflict telemetry encodings. No Golden equivalence, CPU cycle behavior, or clock/reset proof.'}
$receipt|ConvertTo-Json -Depth 5|Set-Content -Encoding utf8 (Join-Path $output 'assist-reservation-simulation-receipt.json')
Copy-Item (Join-Path $output '*') $intermediate -Force
Push-Location $bridge
try { dotnet run --project $runner -- --publish $intermediate $studio; if($LASTEXITCODE){throw 'Producer Studio API publication checks failed.'} }
finally {Pop-Location}
Write-Output "Assist RTL simulation and Studio API publication passed: $studio"
