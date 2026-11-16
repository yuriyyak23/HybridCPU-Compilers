param(
    [string]$ProducerRoot = 'C:\Users\Yuriy\Desktop\CircuitWeave-main',
    [string]$CirctBin = 'C:\Users\Yuriy\Desktop\CircuitWeave ExTool\final\packages\circt-firtool-1.163.0\firtool-1.163.0\bin',
    [string]$OssBin = 'C:\Users\Yuriy\Desktop\CircuitWeave ExTool\final\packages\oss-cad-suite-2026-11-10\oss-cad-suite\bin'
)
$ErrorActionPreference = 'Stop'
$testRoot = $PSScriptRoot
$project = Join-Path $testRoot 'VectorSegmentRead.csproj'
$out = Join-Path $ProducerRoot 'artifacts\processor-20261114\vector-segment-read-v1'
$python = Join-Path $testRoot 'RunVectorSegmentReadRtl.py'
$mlir = Join-Path $out 'vector-segment-read-lifecycle.mlir'
$hwir = Join-Path $out 'vector-segment-read-lifecycle.hwir.json'
$rtl = Join-Path $out 'vector-segment-read-lifecycle.sv'
$circtOpt = Join-Path $CirctBin 'circt-opt.exe'
$iverilog = Join-Path $OssBin 'iverilog.exe'
$vvp = Join-Path $OssBin 'vvp.exe'
$sim = Join-Path $out 'vector-segment-read-lifecycle.vvp'
$vectors = Join-Path $out 'vector-segment-read-rtl-vectors.jsonl'
$tb = Join-Path $out 'vector-segment-read-lifecycle.tb.sv'

dotnet run --project $project
if ($LASTEXITCODE -ne 0) { throw 'Source-bound vector HWIR/oracle generation failed.' }
& $circtOpt $mlir --lower-comb --export-verilog -o NUL | Set-Content -Encoding utf8 $rtl
if ($LASTEXITCODE -ne 0) { throw 'CIRCT lifecycle RTL emission failed.' }
python $python $rtl $vectors $tb $sim $vvp $mlir $hwir $circtOpt $iverilog
if ($LASTEXITCODE -ne 0) { throw 'Icarus lifecycle RTL/oracle comparison failed.' }
dotnet run --project $project
if ($LASTEXITCODE -ne 0) { throw 'Studio snapshot with RTL evidence failed.' }
Write-Host "RTL, 4096-vector receipt and Studio snapshot: $out"
