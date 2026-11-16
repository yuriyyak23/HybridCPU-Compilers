param(
    [Parameter(Mandatory=$true)][string]$Vectors,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [string]$Mlir,
    [string]$CirctBin = 'C:\Users\Yuriy\Desktop\CircuitWeave ExTool\final\packages\circt-firtool-1.163.0\firtool-1.163.0\bin',
    [string[]]$SourcePins = @(),
    [string]$OssBin = 'C:\Users\Yuriy\Desktop\CircuitWeave ExTool\final\packages\oss-cad-suite-2026-11-10\oss-cad-suite\bin'
)
$ErrorActionPreference = 'Stop'
$scriptRoot = $PSScriptRoot
$candidate = if ([string]::IsNullOrWhiteSpace($Mlir)) { Join-Path $scriptRoot 'trap_event_projection.mlir' } else { [System.IO.Path]::GetFullPath($Mlir) }
$verifier = Join-Path $scriptRoot 'Verify-TrapCarrierRtl.py'
if (-not (Test-Path -LiteralPath $candidate)) { throw "Candidate MLIR missing: $candidate" }
if (-not (Test-Path -LiteralPath $Vectors)) { throw "JSONL vectors missing: $Vectors" }
if (Test-Path -LiteralPath $OutputRoot) { throw "Output root must be new/empty: $OutputRoot" }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$rtl = Join-Path $OutputRoot 'trap_event_projection.sv'
$circtOpt = Join-Path $CirctBin 'circt-opt.exe'
$iverilog = Join-Path $OssBin 'iverilog.exe'
$vvp = Join-Path $OssBin 'vvp.exe'
& $circtOpt $candidate --lower-comb --export-verilog -o NUL | Set-Content -Path $rtl -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'CIRCT lowering/export failed.' }
 $pinArgs = @(); foreach ($pin in $SourcePins) { $pinArgs += @("--source-pin", $pin) }
python $verifier --mlir $candidate --rtl $rtl --vectors $Vectors --out $OutputRoot --circt-opt $circtOpt --iverilog $iverilog --vvp $vvp @pinArgs
if ($LASTEXITCODE -ne 0) { throw 'Trap carrier Icarus check failed.' }
Write-Host "Trap carrier combinational RTL check: $OutputRoot"





