param(
  [string]$Simulator = 'iverilog',
  [string]$OutputDirectory = 'C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080\tests\rtl-validation'
)
$ErrorActionPreference='Stop'
$rtlRoot=$PSScriptRoot
$out=[IO.Path]::GetFullPath($OutputDirectory)
$allowedRoot=[IO.Path]::GetFullPath('C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080').TrimEnd('\')+'\'
if(-not $out.StartsWith($allowedRoot,[StringComparison]::OrdinalIgnoreCase)){throw "RTL test output must be inside $allowedRoot : $out"}
for($d=[IO.DirectoryInfo]::new($out);$null -ne $d;$d=$d.Parent){
  if(Test-Path -LiteralPath $d.FullName){if(($d.Attributes -band [IO.FileAttributes]::ReparsePoint)-ne 0){throw "Linked output path refused: $($d.FullName)"}}
}
if(-not (Test-Path -LiteralPath $out -PathType Container)){[void][IO.Directory]::CreateDirectory($out)}
for($d=[IO.DirectoryInfo]::new($out);$null -ne $d;$d=$d.Parent){if(($d.Attributes -band [IO.FileAttributes]::ReparsePoint)-ne 0){throw "Linked output path refused: $($d.FullName)"}}
$log=Join-Path $out 'rtl-test.log'
$generated=@($log,(Join-Path $out 'tb_cw8080_add.vvp'),(Join-Path $out 'tb_cw8080_subset_cpu.vvp'),(Join-Path $out 'tb_cw8080_pair_cpu.vvp'),(Join-Path $out 'tb_cw8080_branch_cpu.vvp'),(Join-Path $out 'tb_cw8080_stack_cpu.vvp'),(Join-Path $out 'tb_cw8080_push_pop_cpu.vvp'),(Join-Path $out 'tb_cw8080_io_cpu.vvp'),(Join-Path $out 'tb_cw8080_rotate_cpu.vvp'),(Join-Path $out 'tb_cw8080_immediate_alu_cpu.vvp'),(Join-Path $out 'tb_cw8080_memory_address_cpu.vvp'),(Join-Path $out 'tb_cw8080_daa_cpu.vvp'))
foreach($path in $generated){if(Test-Path -LiteralPath $path){throw "Refusing to overwrite existing test output: $path"}}
$sources=@('cw8080_add.sv','cw8080_subset_cpu.sv','tb_cw8080_add.sv','tb_cw8080_subset_cpu.sv','tb_cw8080_pair_cpu.sv','tb_cw8080_branch_cpu.sv','tb_cw8080_stack_cpu.sv','tb_cw8080_push_pop_cpu.sv','tb_cw8080_io_cpu.sv','tb_cw8080_rotate_cpu.sv','tb_cw8080_immediate_alu_cpu.sv','tb_cw8080_memory_address_cpu.sv','tb_cw8080_daa_cpu.sv')|ForEach-Object{Join-Path $rtlRoot $_}
foreach($source in $sources){if(-not(Test-Path -LiteralPath $source -PathType Leaf)){throw "RTL input missing: $source"}}
$tool=Get-Command $Simulator -ErrorAction SilentlyContinue
$lines=New-Object System.Collections.Generic.List[string]
$lines.Add("Simulator: $Simulator")
foreach($source in $sources){$hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant();$lines.Add("SOURCE $source SHA256 $hash")}
if($null -eq $tool){$lines.Add('STATUS NOT RUN - requested HDL simulator is not installed or not on PATH.');[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));Write-Output "NOT RUN; see $log";exit 2}
if($Simulator -eq 'iverilog'){
  $sim=Join-Path $out 'tb_cw8080_add.vvp'
  & $tool.Source -g2012 -s tb_cw8080_add -o $sim $sources[0] $sources[2]
  if($LASTEXITCODE -ne 0){$lines.Add("ADD/ADC compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $addOutput=& vvp $sim 2>&1; $addExit=$LASTEXITCODE; $lines.AddRange([string[]]$addOutput); if($addExit -ne 0){$lines.Add("ADD/ADC test failed exit=$addExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $addExit}
  $cpu=Join-Path $out 'tb_cw8080_subset_cpu.vvp'
  & $tool.Source -g2012 -s tb_cw8080_subset_cpu -o $cpu $sources[1] $sources[3]
  if($LASTEXITCODE -ne 0){$lines.Add("CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $cpuOutput=& vvp $cpu 2>&1; $cpuExit=$LASTEXITCODE; $lines.AddRange([string[]]$cpuOutput); if($cpuExit -ne 0){$lines.Add("CPU test failed exit=$cpuExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $cpuExit}
  $pair=Join-Path $out 'tb_cw8080_pair_cpu.vvp'
  & $tool.Source -g2012 -s tb_cw8080_pair_cpu -o $pair $sources[1] $sources[4]
  if($LASTEXITCODE -ne 0){$lines.Add("pair CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $pairOutput=& vvp $pair 2>&1; $pairExit=$LASTEXITCODE; $lines.AddRange([string[]]$pairOutput); if($pairExit -ne 0){$lines.Add("pair CPU test failed exit=$pairExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $pairExit}
  $branch=Join-Path $out 'tb_cw8080_branch_cpu.vvp'
  & $tool.Source -g2012 -s tb_cw8080_branch_cpu -o $branch $sources[1] $sources[5]
  if($LASTEXITCODE -ne 0){$lines.Add("branch CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $branchOutput=& vvp $branch 2>&1; $branchExit=$LASTEXITCODE; $lines.AddRange([string[]]$branchOutput); if($branchExit -ne 0){$lines.Add("branch CPU test failed exit=$branchExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $branchExit}
  $stack=Join-Path $out 'tb_cw8080_stack_cpu.vvp'
  & $tool.Source -g2012 -s tb_cw8080_stack_cpu -o $stack $sources[1] $sources[6]
  if($LASTEXITCODE -ne 0){$lines.Add("stack CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $stackOutput=& vvp $stack 2>&1; $stackExit=$LASTEXITCODE; $lines.AddRange([string[]]$stackOutput); if($stackExit -ne 0){$lines.Add("stack CPU test failed exit=$stackExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $stackExit}
  $pushPop=Join-Path $out 'tb_cw8080_push_pop_cpu.vvp'
  & $tool.Source -g2012 -s tb_cw8080_push_pop_cpu -o $pushPop $sources[1] $sources[7]
  if($LASTEXITCODE -ne 0){$lines.Add("PUSH/POP CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $pushPopOutput=& vvp $pushPop 2>&1; $pushPopExit=$LASTEXITCODE; $lines.AddRange([string[]]$pushPopOutput); if($pushPopExit -ne 0){$lines.Add("PUSH/POP CPU test failed exit=$pushPopExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $pushPopExit}
  $io=Join-Path $out 'tb_cw8080_io_cpu.vvp'
  & $tool.Source -g2012 -s tb_cw8080_io_cpu -o $io $sources[1] $sources[8]
  if($LASTEXITCODE -ne 0){$lines.Add("I/O CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $ioOutput=& vvp $io 2>&1; $ioExit=$LASTEXITCODE; $lines.AddRange([string[]]$ioOutput); if($ioExit -ne 0){$lines.Add("I/O CPU test failed exit=$ioExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $ioExit}
  $rotate=Join-Path $out 'tb_cw8080_rotate_cpu.vvp'
  & $tool.Source -g2012 -s tb_cw8080_rotate_cpu -o $rotate $sources[1] $sources[9]
  if($LASTEXITCODE -ne 0){$lines.Add("rotate CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $rotateOutput=& vvp $rotate 2>&1; $rotateExit=$LASTEXITCODE; $lines.AddRange([string[]]$rotateOutput); if($rotateExit -ne 0){$lines.Add("rotate CPU test failed exit=$rotateExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $rotateExit}
  $immAlu=Join-Path $out 'tb_cw8080_immediate_alu_cpu.vvp'
  & $tool.Source -g2012 -s tb_cw8080_immediate_alu_cpu -o $immAlu $sources[1] $sources[10]
  if($LASTEXITCODE -ne 0){$lines.Add("immediate ALU CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $immAluOutput=& vvp $immAlu 2>&1; $immAluExit=$LASTEXITCODE; $lines.AddRange([string[]]$immAluOutput); if($immAluExit -ne 0){$lines.Add("immediate ALU CPU test failed exit=$immAluExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $immAluExit}
  $memoryAddress=Join-Path $out 'tb_cw8080_memory_address_cpu.vvp'
  & $tool.Source -g2012 -s tb_cw8080_memory_address_cpu -o $memoryAddress $sources[1] $sources[11]
  if($LASTEXITCODE -ne 0){$lines.Add("addressed memory CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $memoryAddressOutput=& vvp $memoryAddress 2>&1; $memoryAddressExit=$LASTEXITCODE; $lines.AddRange([string[]]$memoryAddressOutput); if($memoryAddressExit -ne 0){$lines.Add("addressed memory CPU test failed exit=$memoryAddressExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $memoryAddressExit}
  $daa=Join-Path $out 'tb_cw8080_daa_cpu.vvp'
  & $tool.Source -g2012 -s tb_cw8080_daa_cpu -o $daa $sources[1] $sources[12]
  if($LASTEXITCODE -ne 0){$lines.Add("DAA CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $daaOutput=& vvp $daa 2>&1; $daaExit=$LASTEXITCODE; $lines.AddRange([string[]]$daaOutput); if($daaExit -ne 0){$lines.Add("DAA CPU test failed exit=$daaExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $daaExit}
  $lines.Add('STATUS PASS - all eleven testbenches compiled and executed.')
} else {$lines.Add('STATUS NOT RUN - this script currently supports iverilog/vvp only.');[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));Write-Output "NOT RUN; see $log";exit 2}
[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));Write-Output "PASS; see $log"
