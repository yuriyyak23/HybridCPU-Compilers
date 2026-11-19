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
$useWsl=$Simulator -eq 'wsl-iverilog'
if($useWsl){$tool=Get-Command 'wsl.exe' -ErrorAction SilentlyContinue}
else{$tool=Get-Command $Simulator -ErrorAction SilentlyContinue}
$lines=New-Object System.Collections.Generic.List[string]
$lines.Add("Simulator: $Simulator")
foreach($source in $sources){$hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant();$lines.Add("SOURCE $source SHA256 $hash")}
if($null -eq $tool){$lines.Add('STATUS NOT RUN - requested HDL simulator is not installed or not on PATH.');[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));Write-Output "NOT RUN; see $log";exit 2}
if($useWsl){
  $wslCheck=& $tool.Source -e sh -lc 'command -v iverilog >/dev/null && command -v vvp >/dev/null' 2>&1
  if($LASTEXITCODE -ne 0){$lines.Add('STATUS NOT RUN - WSL is present but iverilog/vvp are unavailable.');[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));Write-Output "NOT RUN; see $log";exit 2}
  $version=& $tool.Source -e iverilog -V 2>&1 | Select-Object -First 1
  $lines.Add("WSL simulator: $version")
}
function Convert-ToWslPath([string]$Path){
  $converted=& $tool.Source -e wslpath -u -a $Path
  if($LASTEXITCODE -ne 0){throw "wslpath failed for $Path"}
  return ([string]$converted).Trim()
}
function Invoke-BridgeCompile([string]$Top,[string]$Output,[string[]]$Inputs){
  if($useWsl){
    $wslArgList=@('-e','iverilog','-g2012','-s',$Top,'-o',(Convert-ToWslPath $Output))
    foreach($sourcePath in $Inputs){$wslArgList+=Convert-ToWslPath $sourcePath}
    $result=& $tool.Source @wslArgList 2>&1
  } else {$result=& $tool.Source -g2012 -s $Top -o $Output $Inputs 2>&1}
  $exitCode=$LASTEXITCODE; $global:LASTEXITCODE=$exitCode
  return $result
}
function Invoke-BridgeSimulation([string]$Executable){
  if($useWsl){$wslExe=Convert-ToWslPath $Executable; $result=& $tool.Source -e vvp $wslExe 2>&1}
  else{$result=& vvp $Executable 2>&1}
  $exitCode=$LASTEXITCODE; $global:LASTEXITCODE=$exitCode
  return $result
}
if($Simulator -in @('iverilog','wsl-iverilog')){
  $sim=Join-Path $out 'tb_cw8080_add.vvp'
  Invoke-BridgeCompile 'tb_cw8080_add' $sim @($sources[0],$sources[2]) | ForEach-Object {$lines.Add([string]$_)}
  if($LASTEXITCODE -ne 0){$lines.Add("ADD/ADC compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $addOutput=Invoke-BridgeSimulation $sim; $addExit=$LASTEXITCODE; $lines.AddRange([string[]]$addOutput); if($addExit -ne 0){$lines.Add("ADD/ADC test failed exit=$addExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $addExit}
  $cpu=Join-Path $out 'tb_cw8080_subset_cpu.vvp'
  Invoke-BridgeCompile 'tb_cw8080_subset_cpu' $cpu @($sources[1],$sources[3]) | ForEach-Object {$lines.Add([string]$_)}
  if($LASTEXITCODE -ne 0){$lines.Add("CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $cpuOutput=Invoke-BridgeSimulation $cpu; $cpuExit=$LASTEXITCODE; $lines.AddRange([string[]]$cpuOutput); if($cpuExit -ne 0){$lines.Add("CPU test failed exit=$cpuExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $cpuExit}
  $pair=Join-Path $out 'tb_cw8080_pair_cpu.vvp'
  Invoke-BridgeCompile 'tb_cw8080_pair_cpu' $pair @($sources[1],$sources[4]) | ForEach-Object {$lines.Add([string]$_)}
  if($LASTEXITCODE -ne 0){$lines.Add("pair CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $pairOutput=Invoke-BridgeSimulation $pair; $pairExit=$LASTEXITCODE; $lines.AddRange([string[]]$pairOutput); if($pairExit -ne 0){$lines.Add("pair CPU test failed exit=$pairExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $pairExit}
  $branch=Join-Path $out 'tb_cw8080_branch_cpu.vvp'
  Invoke-BridgeCompile 'tb_cw8080_branch_cpu' $branch @($sources[1],$sources[5]) | ForEach-Object {$lines.Add([string]$_)}
  if($LASTEXITCODE -ne 0){$lines.Add("branch CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $branchOutput=Invoke-BridgeSimulation $branch; $branchExit=$LASTEXITCODE; $lines.AddRange([string[]]$branchOutput); if($branchExit -ne 0){$lines.Add("branch CPU test failed exit=$branchExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $branchExit}
  $stack=Join-Path $out 'tb_cw8080_stack_cpu.vvp'
  Invoke-BridgeCompile 'tb_cw8080_stack_cpu' $stack @($sources[1],$sources[6]) | ForEach-Object {$lines.Add([string]$_)}
  if($LASTEXITCODE -ne 0){$lines.Add("stack CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $stackOutput=Invoke-BridgeSimulation $stack; $stackExit=$LASTEXITCODE; $lines.AddRange([string[]]$stackOutput); if($stackExit -ne 0){$lines.Add("stack CPU test failed exit=$stackExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $stackExit}
  $pushPop=Join-Path $out 'tb_cw8080_push_pop_cpu.vvp'
  Invoke-BridgeCompile 'tb_cw8080_push_pop_cpu' $pushPop @($sources[1],$sources[7]) | ForEach-Object {$lines.Add([string]$_)}
  if($LASTEXITCODE -ne 0){$lines.Add("PUSH/POP CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $pushPopOutput=Invoke-BridgeSimulation $pushPop; $pushPopExit=$LASTEXITCODE; $lines.AddRange([string[]]$pushPopOutput); if($pushPopExit -ne 0){$lines.Add("PUSH/POP CPU test failed exit=$pushPopExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $pushPopExit}
  $io=Join-Path $out 'tb_cw8080_io_cpu.vvp'
  Invoke-BridgeCompile 'tb_cw8080_io_cpu' $io @($sources[1],$sources[8]) | ForEach-Object {$lines.Add([string]$_)}
  if($LASTEXITCODE -ne 0){$lines.Add("I/O CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $ioOutput=Invoke-BridgeSimulation $io; $ioExit=$LASTEXITCODE; $lines.AddRange([string[]]$ioOutput); if($ioExit -ne 0){$lines.Add("I/O CPU test failed exit=$ioExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $ioExit}
  $rotate=Join-Path $out 'tb_cw8080_rotate_cpu.vvp'
  Invoke-BridgeCompile 'tb_cw8080_rotate_cpu' $rotate @($sources[1],$sources[9]) | ForEach-Object {$lines.Add([string]$_)}
  if($LASTEXITCODE -ne 0){$lines.Add("rotate CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $rotateOutput=Invoke-BridgeSimulation $rotate; $rotateExit=$LASTEXITCODE; $lines.AddRange([string[]]$rotateOutput); if($rotateExit -ne 0){$lines.Add("rotate CPU test failed exit=$rotateExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $rotateExit}
  $immAlu=Join-Path $out 'tb_cw8080_immediate_alu_cpu.vvp'
  Invoke-BridgeCompile 'tb_cw8080_immediate_alu_cpu' $immAlu @($sources[1],$sources[10]) | ForEach-Object {$lines.Add([string]$_)}
  if($LASTEXITCODE -ne 0){$lines.Add("immediate ALU CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $immAluOutput=Invoke-BridgeSimulation $immAlu; $immAluExit=$LASTEXITCODE; $lines.AddRange([string[]]$immAluOutput); if($immAluExit -ne 0){$lines.Add("immediate ALU CPU test failed exit=$immAluExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $immAluExit}
  $memoryAddress=Join-Path $out 'tb_cw8080_memory_address_cpu.vvp'
  Invoke-BridgeCompile 'tb_cw8080_memory_address_cpu' $memoryAddress @($sources[1],$sources[11]) | ForEach-Object {$lines.Add([string]$_)}
  if($LASTEXITCODE -ne 0){$lines.Add("addressed memory CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $memoryAddressOutput=Invoke-BridgeSimulation $memoryAddress; $memoryAddressExit=$LASTEXITCODE; $lines.AddRange([string[]]$memoryAddressOutput); if($memoryAddressExit -ne 0){$lines.Add("addressed memory CPU test failed exit=$memoryAddressExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $memoryAddressExit}
  $daa=Join-Path $out 'tb_cw8080_daa_cpu.vvp'
  Invoke-BridgeCompile 'tb_cw8080_daa_cpu' $daa @($sources[1],$sources[12]) | ForEach-Object {$lines.Add([string]$_)}
  if($LASTEXITCODE -ne 0){$lines.Add("DAA CPU compile failed exit=$LASTEXITCODE");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $LASTEXITCODE}
  $daaOutput=Invoke-BridgeSimulation $daa; $daaExit=$LASTEXITCODE; $lines.AddRange([string[]]$daaOutput); if($daaExit -ne 0){$lines.Add("DAA CPU test failed exit=$daaExit");[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));exit $daaExit}
  $lines.Add('STATUS PASS - all eleven testbenches compiled and executed.')
} else {$lines.Add('STATUS NOT RUN - supported simulators are iverilog/vvp and wsl-iverilog.');[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));Write-Output "NOT RUN; see $log";exit 2}
[IO.File]::WriteAllLines($log,$lines,[Text.UTF8Encoding]::new($false));Write-Output "PASS; see $log"
