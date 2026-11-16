param(
  [Parameter(Mandatory = $true)][string]$OracleJsonl,
  [string]$ModelRoot = 'C:\Users\Yuriy\Desktop\HybridCPU ISE\HybridCPU_ISE',
  [string]$CirctOpt = 'C:\Users\Yuriy\Desktop\CircuitWeave ExTool\final\packages\circt-firtool-1.163.0\firtool-1.163.0\bin\circt-opt.exe',
  [string]$OssBin = 'C:\Users\Yuriy\Desktop\CircuitWeave ExTool\final\packages\oss-cad-suite-2026-11-10\oss-cad-suite\bin',
  [string]$ArtifactDirectory = ''
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $OracleJsonl -PathType Leaf)) { throw "Oracle JSONL not found: $OracleJsonl" }
if (-not (Test-Path -LiteralPath $CirctOpt -PathType Leaf)) { throw "circt-opt not found: $CirctOpt" }
$iverilog = Join-Path $OssBin 'iverilog.exe'
$vvp = Join-Path $OssBin 'vvp.exe'
if (-not (Test-Path -LiteralPath $iverilog -PathType Leaf) -or -not (Test-Path -LiteralPath $vvp -PathType Leaf)) { throw 'Icarus tools not found in OSS CAD Suite bin.' }
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
  $ArtifactDirectory = Join-Path $env:TEMP ('cw-issue-packet-materialization-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [guid]::NewGuid().ToString('N'))
}
$dest = [IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $dest) { throw "Immutable artifact directory already exists: $dest" }
New-Item -ItemType Directory -Path $dest | Out-Null
$oracle = Join-Path $dest 'oracle.jsonl'
$mlir = Join-Path $dest 'materialization-eligibility.mlir'
$rtl = Join-Path $dest 'issue-packet-materialization.sv'
$tb = Join-Path $dest 'tb.sv'
$vectors = Join-Path $dest 'vectors.mem'
$sim = Join-Path $dest 'simulation.vvp'
$simLog = Join-Path $dest 'simulation.log'
$receipt = Join-Path $dest 'run-receipt.json'
$compare = Join-Path $PSScriptRoot 'Compare.py'
$runner = $PSCommandPath
$hwir = Join-Path $dest 'materialization-eligibility.hwir.json'
$bridgeRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$bridgeSource = Join-Path $bridgeRoot 'HWIR\BridgeHwirIssuePacketMaterializationEligibility.cs'
$bridgeProgram = Join-Path $PSScriptRoot 'Program.cs'
$bridgeProject = Join-Path $PSScriptRoot 'IssuePacketMaterializationEligibility.csproj'
Copy-Item -LiteralPath $OracleJsonl -Destination $oracle
dotnet run --project $bridgeProject --configuration Release -- $ModelRoot $dest
if ($LASTEXITCODE -ne 0) { throw 'Bridge source guard, HWIR validation, or CIRCT MLIR lowering failed.' }
if (-not (Test-Path -LiteralPath $mlir -PathType Leaf)) { throw 'Bridge project did not emit materialization-eligibility.mlir.' }

python $compare prepare --oracle $oracle --testbench $tb --vectors $vectors
if ($LASTEXITCODE -ne 0) { throw 'Oracle schema validation or exhaustive vector packing failed.' }
$svLines = & $CirctOpt $mlir --lower-comb --export-verilog -o NUL
if ($LASTEXITCODE -ne 0) { throw 'CIRCT lowering and SystemVerilog export failed.' }
[IO.File]::WriteAllText($rtl, (($svLines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
$env:PATH = $OssBin + ';' + (Join-Path (Split-Path $OssBin -Parent) 'lib') + ';' + $env:PATH
& $iverilog -g2012 -s tb -o $sim $rtl $tb
if ($LASTEXITCODE -ne 0) { throw 'Icarus compile failed.' }
$simLines = & $vvp $sim
$simExit = $LASTEXITCODE
[IO.File]::WriteAllText($simLog, (($simLines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
if ($simExit -ne 0) { throw "Icarus simulation failed with exit code $simExit. See $simLog" }
python $compare verify --oracle $oracle --rtl $rtl --testbench $tb --vectors $vectors --simulation-log $simLog --hwir $hwir --mlir $mlir --bridge-source $bridgeSource --bridge-program $bridgeProgram --bridge-project $bridgeProject --circt $CirctOpt --iverilog $iverilog --vvp $vvp --runner $runner --receipt $receipt
if ($LASTEXITCODE -ne 0) { throw 'Exhaustive comparison receipt validation failed.' }
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
Write-Host "PASS: exact source oracle vs CIRCT RTL, 131072 vectors, 0 mismatches"
Write-Host "Artifacts: $dest"
Write-Host "Receipt SHA-256: $(Hash $receipt)"
