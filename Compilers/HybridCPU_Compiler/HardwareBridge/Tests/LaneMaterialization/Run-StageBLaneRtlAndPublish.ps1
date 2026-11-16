param([string]$ProducerRoot='C:\Users\Yuriy\Desktop\CircuitWeave-main',[string]$Distro='Ubuntu')
$ErrorActionPreference='Stop'
$bridge=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$intermediate=Join-Path $bridge '.artifacts\stage-b-lane-v1'
$artifactRoot=Join-Path $ProducerRoot 'artifacts\processor-20261114'
$output=Join-Path $artifactRoot 'stage-b-lane-rtl-v1'
$studio=Join-Path $artifactRoot 'stage-b-lane-v1'
$runner=Join-Path $PSScriptRoot 'LaneMaterialization.csproj'
$fir=Join-Path $artifactRoot 'power-control-candidate\tool\firtool-1.160.0\bin\circt-opt'
$pins=@{firtool='187c0c0f0f1464a58997a3e99ca96560cc002b97276cc38c12c28addf0112c4e';iverilog='a6681e0b577dcfc4ea83d1591a5714710b0b3535ebb36491ee8177553bab9631';vvp='e0a087a4906d5a9cd1b0b85c9b4c2081704c2c9bcd94f224437fc56b15fc440d'}
function Sha([string]$p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()}
function WslPath([string]$p){$f=[IO.Path]::GetFullPath($p).Replace('\','/');if($f -notmatch '^([A-Za-z]):/(.*)$'){throw "Bad drive path: $p"};"/mnt/$($Matches[1].ToLowerInvariant())/$($Matches[2])"}
function Wsl([string]$c){& wsl.exe -d $Distro --exec /bin/bash -c $c;if($LASTEXITCODE){throw "WSL failed ($LASTEXITCODE): $c"}}
if((Sha $fir)-ne $pins.firtool){throw 'Pinned firtool SHA mismatch.'}
New-Item -ItemType Directory -Force -Path $intermediate,$output,$studio|Out-Null
Push-Location $bridge
try { dotnet run --project $runner -- --emit $intermediate; if($LASTEXITCODE){throw 'Stage B source guard/HWIR evaluator/emitter failed.'} }
finally {Pop-Location}
$wp=WslPath $output; $wf=WslPath $fir
foreach($name in @('stage-b-lane.mlir','stage-b-lane.tb.sv')){Copy-Item -LiteralPath (Join-Path $intermediate $name) -Destination $output -Force}
Wsl "$wf --version > $wp/firtool-version.txt 2> $wp/firtool-version.stderr.txt"
Wsl "$wf --export-verilog $wp/stage-b-lane.mlir -o /dev/null > $wp/stage-b-lane.sv 2> $wp/firtool-export.log"
Wsl "/usr/bin/iverilog -V > $wp/iverilog-version.txt 2> $wp/iverilog-version.stderr.txt"
Wsl "/usr/bin/vvp -V > $wp/vvp-version.txt 2> $wp/vvp-version.stderr.txt"
Wsl "/usr/bin/iverilog -g2012 -s stage_b_lane_directed_tb -o $wp/stage-b-lane.vvp $wp/stage-b-lane.sv $wp/stage-b-lane.tb.sv > $wp/iverilog-compile.log 2>&1"
Wsl "cd $wp && /usr/bin/vvp ./stage-b-lane.vvp > vvp-simulation.log 2>&1"
Wsl "sha256sum $wf /usr/bin/iverilog /usr/bin/vvp > $wp/tool-hashes.txt"
$sim=Get-Content (Join-Path $output 'vvp-simulation.log') -Raw
if(!$sim.Contains('PASS all directed Stage B RTL scenarios')){throw 'Directed RTL assertions did not pass.'}
$toolText=Get-Content (Join-Path $output 'tool-hashes.txt') -Raw
foreach($pin in @($pins.firtool,$pins.iverilog,$pins.vvp)){if(!$toolText.Contains($pin)){throw "Tool hash absent: $pin"}}
$firVersion=(Get-Content (Join-Path $output 'firtool-version.txt') -Raw)+(Get-Content (Join-Path $output 'firtool-version.stderr.txt') -Raw)
$ivVersion=(Get-Content (Join-Path $output 'iverilog-version.txt') -Raw)+(Get-Content (Join-Path $output 'iverilog-version.stderr.txt') -Raw)
$vvpVersion=(Get-Content (Join-Path $output 'vvp-version.txt') -Raw)+(Get-Content (Join-Path $output 'vvp-version.stderr.txt') -Raw)
if(!$firVersion.Contains('CIRCT firtool-1.160.0') -or !$ivVersion.Contains('Icarus Verilog version 12.0 (stable)') -or !$vvpVersion.Contains('Icarus Verilog runtime version 12.0 (stable)')){throw 'Tool version text differs from the pinned versions.'}
foreach($name in @('stage-b-lane.sv','stage-b-lane.vcd')){if(!(Test-Path (Join-Path $output $name)) -or (Get-Item (Join-Path $output $name)).Length -eq 0){throw "RTL evidence absent: $name"}}
$receipt=[ordered]@{schema='circuitweave.stage-b-lane-rtl-receipt/v1';status='passed';independentVectors=74744;hardPinnedLane6ExhaustiveMasks=65536;replayChooserCases=5120;directedRtlScenarios=7;firtoolSha256=$pins.firtool;iverilogSha256=$pins.iverilog;vvpSha256=$pins.vvp;rtlSha256=(Sha (Join-Path $output 'stage-b-lane.sv'));vcdSha256=(Sha (Join-Path $output 'stage-b-lane.vcd'));simulationLogSha256=(Sha (Join-Path $output 'vvp-simulation.log'));scope='Combinational Stage B lane materialization; no Stage A, scheduler state mutation/commit, CPU cycle/reset/stall/replay trace, or Golden equivalence.'}
$receipt|ConvertTo-Json -Depth 5|Set-Content -Encoding utf8 (Join-Path $output 'stage-b-lane-simulation-receipt.json')
Copy-Item (Join-Path $output '*') $intermediate -Force
Push-Location $bridge
try { dotnet run --project $runner -- --publish $intermediate $studio; if($LASTEXITCODE){throw 'Producer Studio API checks failed.'} }
finally {Pop-Location}
Write-Output "Stage B lane RTL, directed simulation, and Studio API publication passed: $studio"
